using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using DatReaderWriter.DBObjs;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    public static class DungeonKnowledgeBuilder {
        private static readonly string KnowledgePath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "ACME WorldBuilder", "dungeon_knowledge.json");

        public static DungeonKnowledgeBase? LoadCached() {
            try {
                if (!File.Exists(KnowledgePath)) return null;
                var json = File.ReadAllText(KnowledgePath);
                var kb = JsonSerializer.Deserialize<DungeonKnowledgeBase>(json, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                if (kb != null && kb.Version < DungeonKnowledgeBase.CurrentVersion) {
                    Console.WriteLine($"[DungeonKnowledge] Stale knowledge base (v{kb.Version}, need v{DungeonKnowledgeBase.CurrentVersion}) — deleting for rebuild");
                    try { File.Delete(KnowledgePath); } catch { }
                    return null;
                }
                return kb;
            }
            catch { return null; }
        }

        /// <summary>True if the cached KB exists and is up to date.</summary>
        public static bool IsCachedValid() {
            try {
                if (!File.Exists(KnowledgePath)) return false;
                var json = File.ReadAllText(KnowledgePath);
                var kb = JsonSerializer.Deserialize<DungeonKnowledgeBase>(json, new JsonSerializerOptions {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                return kb != null && kb.Version >= DungeonKnowledgeBase.CurrentVersion && kb.Prefabs.Count >= 100;
            }
            catch { return false; }
        }

        public static void Save(DungeonKnowledgeBase kb) {
            try {
                var dir = Path.GetDirectoryName(KnowledgePath);
                if (dir != null) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(kb, new JsonSerializerOptions {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(KnowledgePath, json);
            }
            catch (Exception ex) {
                Console.WriteLine($"[DungeonKnowledge] Save error: {ex.Message}");
            }
        }

        public static DungeonKnowledgeBase Build(IDatReaderWriter dats) {
            Console.WriteLine("[DungeonKnowledge] Building knowledge base from DAT...");

            var edgeCounts = new Dictionary<string, (AdjacencyEdge edge, int count)>();
            var allPrefabs = new List<DungeonPrefab>();
            var prefabSignatures = new Dictionary<string, int>();
            var roomUsage = new Dictionary<(ushort envId, ushort cs), (int count, HashSet<string> dungeonNames, List<ushort> sampleSurfaces)>();

            // Collect static objects per room type across all dungeons
            var roomStaticsRaw = new Dictionary<(ushort envId, ushort cs), List<List<(uint id, Vector3 pos, Quaternion rot)>>>();

            // Collect full dungeon graph structures as templates
            var templates = new List<DungeonTemplate>();

            var lbiIds = GetDungeonLandblockIds(dats);
            Console.WriteLine($"[DungeonKnowledge] Scanning {lbiIds.Length} dungeon landblocks...");

            var dungeonNameLookup = new Dictionary<ushort, string>();
            foreach (var entry in Lib.LocationDatabase.Dungeons) {
                if (!dungeonNameLookup.ContainsKey(entry.LandblockId) && !string.IsNullOrEmpty(entry.Name?.Trim()))
                    dungeonNameLookup[entry.LandblockId] = entry.Name.Trim();
            }

            int dungeonsScanned = 0;
            int totalLbiIds = lbiIds.Length;
            for (int li = 0; li < totalLbiIds; li++) {
                var lbiId = lbiIds[li];
                if (!dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) continue;
                if (lbi.Buildings != null && lbi.Buildings.Count > 0) continue;

                uint lbId = lbiId >> 16;

                var cells = new Dictionary<ushort, EnvCell>();
                for (uint i = 0; i < lbi.NumCells; i++) {
                    ushort cellNum = (ushort)(0x0100 + i);
                    uint cellId = (lbId << 16) | cellNum;
                    if (dats.TryGet<EnvCell>(cellId, out var ec))
                        cells[cellNum] = ec;
                }

                if (cells.Count < 2) continue;
                dungeonsScanned++;

                var lbKey = (ushort)(lbId & 0xFFFF);
                dungeonNameLookup.TryGetValue(lbKey, out var dungeonName);
                foreach (var (_, ec) in cells) {
                    var rk = ((ushort)ec.EnvironmentId, (ushort)ec.CellStructure);
                    if (!roomUsage.TryGetValue(rk, out var ru)) {
                        ru = (0, new HashSet<string>(), new List<ushort>());
                    }
                    ru.count++;
                    if (!string.IsNullOrEmpty(dungeonName)) ru.dungeonNames.Add(dungeonName);
                    var ecSurfaces = ec.Surfaces?.ToList() ?? new List<ushort>();
                    if (ecSurfaces.Count > ru.sampleSurfaces.Count && ecSurfaces.Count > 0)
                        ru.sampleSurfaces = ecSurfaces;
                    roomUsage[rk] = (ru.count, ru.dungeonNames, ru.sampleSurfaces);

                    // Collect static objects for this room type instance.
                    // Convert from landblock-absolute to cell-relative coordinates
                    // so they can be placed correctly in generated cells at different positions.
                    if (ec.StaticObjects != null && ec.StaticObjects.Count > 0) {
                        if (!roomStaticsRaw.TryGetValue(rk, out var instanceList)) {
                            instanceList = new List<List<(uint, Vector3, Quaternion)>>();
                            roomStaticsRaw[rk] = instanceList;
                        }
                        if (instanceList.Count < 20) {
                            var cellOrigin = ec.Position.Origin;
                            var cellRot = ec.Position.Orientation;
                            var invCellRot = Quaternion.Conjugate(cellRot);

                            var objs = new List<(uint id, Vector3 pos, Quaternion rot)>();
                            foreach (var stab in ec.StaticObjects) {
                                var relPos = Vector3.Transform(stab.Frame.Origin - cellOrigin, invCellRot);
                                var relRot = Quaternion.Normalize(invCellRot * stab.Frame.Orientation);
                                objs.Add((stab.Id, relPos, relRot));
                            }
                            instanceList.Add(objs);
                        }
                    }
                }

                ExtractAdjacencyEdges(cells, edgeCounts);

                if (cells.Count <= 80) {
                    ExtractPrefabs(cells, dats, lbKey, dungeonName ?? "", allPrefabs, prefabSignatures);
                }

                // Extract dungeon topology as a template (limit to named dungeons <=60 cells)
                if (cells.Count >= 3 && cells.Count <= 60 && templates.Count < 1000) {
                    var tmpl = ExtractTemplate(cells, lbKey, dungeonName);
                    if (tmpl != null) templates.Add(tmpl);
                }

                if (dungeonsScanned % 500 == 0)
                    Console.WriteLine($"[DungeonKnowledge] Progress: {dungeonsScanned} dungeons, {edgeCounts.Count} edges, {allPrefabs.Count} prefabs");
            }

            var edges = edgeCounts.Values
                .Select(v => { v.edge.Count = v.count; return v.edge; })
                .OrderByDescending(e => e.Count)
                .ToList();

            var uniquePrefabs = allPrefabs
                .GroupBy(p => p.Signature)
                .Select(g => {
                    var best = g.OrderByDescending(x => x.Cells.Count).First();
                    best.UsageCount = g.Count();
                    return best;
                })
                .OrderByDescending(p => p.UsageCount)
                .Take(2000)
                .ToList();

            var catalog = BuildCatalog(roomUsage, dats);
            var roomStatics = BuildRoomStatics(roomStaticsRaw);
            var gridConstants = ComputeGridConstants(edges);

            PrefabNamer.NameAll(uniquePrefabs, catalog);

            Console.WriteLine($"[DungeonKnowledge] Done: {dungeonsScanned} dungeons, {edges.Count} edges, " +
                $"{uniquePrefabs.Count} prefabs, {catalog.Count} catalog rooms, " +
                $"{roomStatics.Count} room statics, {templates.Count} templates, " +
                $"grid H={gridConstants.h:F1} V={gridConstants.v:F1}");

            var kb = new DungeonKnowledgeBase {
                Version = DungeonKnowledgeBase.CurrentVersion,
                AnalyzedAt = DateTime.UtcNow,
                DungeonsScanned = dungeonsScanned,
                TotalEdges = edges.Count,
                TotalPrefabs = uniquePrefabs.Count,
                TotalCatalogRooms = catalog.Count,
                GridStepH = gridConstants.h,
                GridStepV = gridConstants.v,
                Edges = edges,
                Prefabs = uniquePrefabs,
                Catalog = catalog,
                RoomStatics = roomStatics,
                Templates = templates
            };

            Save(kb);
            return kb;
        }

        private static uint[] GetDungeonLandblockIds(IDatReaderWriter dats) {
            var lbiIds = dats.Dats.GetAllIdsOfType<LandBlockInfo>().ToArray();
            if (lbiIds.Length == 0)
                lbiIds = dats.Dats.Cell.GetAllIdsOfType<LandBlockInfo>().ToArray();
            if (lbiIds.Length == 0) {
                var brute = new List<uint>();
                for (uint x = 0; x < 256; x++) {
                    for (uint y = 0; y < 256; y++) {
                        var infoId = ((x << 8) | y) << 16 | 0xFFFE;
                        if (dats.TryGet<LandBlockInfo>(infoId, out var lbi) && lbi.NumCells > 0)
                            brute.Add(infoId);
                    }
                }
                lbiIds = brute.ToArray();
            }
            return lbiIds;
        }

        private static List<CatalogRoom> BuildCatalog(
            Dictionary<(ushort envId, ushort cs), (int count, HashSet<string> dungeonNames, List<ushort> sampleSurfaces)> roomUsage,
            IDatReaderWriter dats) {

            var catalog = new List<CatalogRoom>();
            foreach (var (key, data) in roomUsage) {
                uint envFileId = (uint)(key.envId | 0x0D000000);
                if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(key.cs, out var cellStruct)) continue;

                var portalIds = PortalSnapper.GetPortalPolygonIds(cellStruct);
                int portalCount = portalIds.Count;

                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                if (cellStruct.VertexArray?.Vertices != null) {
                    foreach (var v in cellStruct.VertexArray.Vertices.Values) {
                        if (v.Origin.X < minX) minX = v.Origin.X; if (v.Origin.X > maxX) maxX = v.Origin.X;
                        if (v.Origin.Y < minY) minY = v.Origin.Y; if (v.Origin.Y > maxY) maxY = v.Origin.Y;
                        if (v.Origin.Z < minZ) minZ = v.Origin.Z; if (v.Origin.Z > maxZ) maxZ = v.Origin.Z;
                    }
                }
                float width = maxX - minX, depth = maxY - minY, height = maxZ - minZ;
                if (!float.IsFinite(width)) width = 0;
                if (!float.IsFinite(depth)) depth = 0;
                if (!float.IsFinite(height)) height = 0;

                string category = portalCount switch {
                    0 => "Sealed Room", 1 => "Dead End", 2 => "Corridor",
                    3 => "T-Junction", 4 => "Crossroads", _ => $"{portalCount}-Way Hub"
                };
                float maxDim = MathF.Max(width, depth);
                string size = maxDim < 10 ? "Small" : maxDim < 25 ? "Medium" : "Large";
                string style = PrefabNamer.InferStyle(string.Join(" ", data.dungeonNames));

                // Compute portal polygon dimensions
                var portalDims = new List<PortalDimension>();
                foreach (var pid in portalIds) {
                    var geom = PortalSnapper.GetPortalGeometry(cellStruct, pid);
                    if (geom == null) continue;
                    var verts = geom.Value.Vertices;
                    if (verts.Count < 3) continue;
                    float pMinX = float.MaxValue, pMaxX = float.MinValue;
                    float pMinY = float.MaxValue, pMaxY = float.MinValue;
                    float pMinZ = float.MaxValue, pMaxZ = float.MinValue;
                    foreach (var pv in verts) {
                        if (pv.X < pMinX) pMinX = pv.X; if (pv.X > pMaxX) pMaxX = pv.X;
                        if (pv.Y < pMinY) pMinY = pv.Y; if (pv.Y > pMaxY) pMaxY = pv.Y;
                        if (pv.Z < pMinZ) pMinZ = pv.Z; if (pv.Z > pMaxZ) pMaxZ = pv.Z;
                    }
                    float pw = MathF.Max(pMaxX - pMinX, pMaxY - pMinY);
                    float ph = pMaxZ - pMinZ;
                    portalDims.Add(new PortalDimension { PolyId = pid, Width = pw, Height = ph });
                }

                catalog.Add(new CatalogRoom {
                    EnvId = key.envId, CellStruct = key.cs,
                    Category = category, Size = size, Style = style,
                    DisplayName = $"{size} {style} {category}",
                    PortalCount = portalCount, UsageCount = data.count,
                    BoundsWidth = width, BoundsDepth = depth, BoundsHeight = height,
                    SourceDungeons = data.dungeonNames.Take(5).OrderBy(n => n).ToList(),
                    SampleSurfaces = data.sampleSurfaces.Take(10).ToList(),
                    PortalDimensions = portalDims
                });
            }
            return catalog.OrderByDescending(r => r.UsageCount).ToList();
        }

        private static void ExtractAdjacencyEdges(
            Dictionary<ushort, EnvCell> cells,
            Dictionary<string, (AdjacencyEdge edge, int count)> edgeCounts) {

            foreach (var (cellNum, cell) in cells) {
                if (cell.CellPortals == null) continue;
                foreach (var portal in cell.CellPortals) {
                    if (!cells.TryGetValue(portal.OtherCellId, out var otherCell)) continue;
                    var envA = (ushort)cell.EnvironmentId;
                    var csA = (ushort)cell.CellStructure;
                    var envB = (ushort)otherCell.EnvironmentId;
                    var csB = (ushort)otherCell.CellStructure;

                    string key = envA < envB || (envA == envB && csA <= csB)
                        ? $"{envA:X4}_{csA}_{portal.PolygonId:X4}->{envB:X4}_{csB}_{portal.OtherPortalId:X4}"
                        : $"{envB:X4}_{csB}_{portal.OtherPortalId:X4}->{envA:X4}_{csA}_{portal.PolygonId:X4}";

                    if (!edgeCounts.TryGetValue(key, out var existing)) {
                        var invCellRot = Quaternion.Conjugate(cell.Position.Orientation);
                        var relOffset = Vector3.Transform(otherCell.Position.Origin - cell.Position.Origin, invCellRot);
                        var relRot = Quaternion.Normalize(invCellRot * otherCell.Position.Orientation);
                        existing = (new AdjacencyEdge {
                            EnvIdA = envA, CellStructA = csA, PolyIdA = (ushort)portal.PolygonId,
                            EnvIdB = envB, CellStructB = csB, PolyIdB = (ushort)portal.OtherPortalId,
                            RelOffsetX = relOffset.X, RelOffsetY = relOffset.Y, RelOffsetZ = relOffset.Z,
                            RelRotX = relRot.X, RelRotY = relRot.Y, RelRotZ = relRot.Z, RelRotW = relRot.W,
                        }, 0);
                    }
                    edgeCounts[key] = (existing.edge, existing.count + 1);
                }
            }
        }

        private static void ExtractPrefabs(
            Dictionary<ushort, EnvCell> cells,
            IDatReaderWriter dats,
            ushort sourceLandblock,
            string dungeonName,
            List<DungeonPrefab> allPrefabs,
            Dictionary<string, int> prefabSignatures) {

            if (cells.Count < 2 || cells.Count > 80) return;
            if (allPrefabs.Count > 200000) return;

            var adj = new Dictionary<ushort, List<(ushort neighbor, ushort myPoly, ushort theirPoly)>>();
            foreach (var (cellNum, cell) in cells) {
                adj[cellNum] = new List<(ushort, ushort, ushort)>();
                if (cell.CellPortals == null) continue;
                foreach (var portal in cell.CellPortals) {
                    if (cells.ContainsKey(portal.OtherCellId))
                        adj[cellNum].Add((portal.OtherCellId, (ushort)portal.PolygonId, (ushort)portal.OtherPortalId));
                }
            }

            int perDungeonCount = 0;
            int perDungeonCap = 50;

            foreach (var (startCell, _) in cells) {
                if (!adj.TryGetValue(startCell, out var neighbors)) continue;
                if (perDungeonCount >= perDungeonCap) break;

                foreach (var (n1, _, _) in neighbors) {
                    if (perDungeonCount >= perDungeonCap) break;
                    TryAddPrefab(cells, dats, adj, new[] { startCell, n1 }, sourceLandblock, dungeonName,
                        allPrefabs, prefabSignatures, 20, ref perDungeonCount);

                    if (!adj.TryGetValue(n1, out var n1Neighbors)) continue;
                    foreach (var (n2, _, _) in n1Neighbors) {
                        if (n2 == startCell) continue;
                        if (perDungeonCount >= perDungeonCap) break;
                        TryAddPrefab(cells, dats, adj, new[] { startCell, n1, n2 }, sourceLandblock, dungeonName,
                            allPrefabs, prefabSignatures, 10, ref perDungeonCount);
                    }
                }
            }

            foreach (var (startCell, _) in cells) {
                if (!adj.TryGetValue(startCell, out var neighbors)) continue;
                if (perDungeonCount >= perDungeonCap) break;

                foreach (var (n1, _, _) in neighbors) {
                    if (perDungeonCount >= perDungeonCap) break;
                    var chain = new List<ushort> { startCell, n1 };
                    var visited = new HashSet<ushort> { startCell, n1 };

                    for (int step = 0; step < 4 && perDungeonCount < perDungeonCap; step++) {
                        var last = chain[^1];
                        if (!adj.TryGetValue(last, out var lastNeighbors)) break;

                        var next = lastNeighbors.FirstOrDefault(n => !visited.Contains(n.neighbor));
                        if (next.neighbor == 0 && !cells.ContainsKey(0)) break;
                        if (visited.Contains(next.neighbor)) break;

                        chain.Add(next.neighbor);
                        visited.Add(next.neighbor);

                        if (chain.Count >= 4) {
                            TryAddPrefab(cells, dats, adj, chain.ToArray(), sourceLandblock, dungeonName,
                                allPrefabs, prefabSignatures, 5, ref perDungeonCount);
                        }
                    }
                }
            }

            if (cells.Count >= 5 && cells.Count <= 15) {
                var allCellNums = cells.Keys.ToArray();
                TryAddPrefab(cells, dats, adj, allCellNums, sourceLandblock, dungeonName,
                    allPrefabs, prefabSignatures, 1, ref perDungeonCount);
            }
        }

        private static void TryAddPrefab(
            Dictionary<ushort, EnvCell> cells, IDatReaderWriter dats,
            Dictionary<ushort, List<(ushort neighbor, ushort myPoly, ushort theirPoly)>> adj,
            ushort[] cellNums, ushort sourceLandblock, string dungeonName,
            List<DungeonPrefab> allPrefabs, Dictionary<string, int> prefabSignatures,
            int maxSigCount, ref int perDungeonCount) {

            if (allPrefabs.Count > 200000) return;
            var prefab = BuildPrefab(cells, dats, adj, cellNums, sourceLandblock);
            if (prefab == null) return;
            prefab.SourceDungeonName = dungeonName;

            prefabSignatures.TryGetValue(prefab.Signature, out var sigCount);
            if (sigCount < maxSigCount) {
                allPrefabs.Add(prefab);
                prefabSignatures[prefab.Signature] = sigCount + 1;
                perDungeonCount++;
            }
        }

        private static DungeonPrefab? BuildPrefab(
            Dictionary<ushort, EnvCell> allCells,
            IDatReaderWriter dats,
            Dictionary<ushort, List<(ushort neighbor, ushort myPoly, ushort theirPoly)>> adj,
            ushort[] cellNums,
            ushort sourceLandblock) {

            var cellSet = new HashSet<ushort>(cellNums);
            var firstCell = allCells[cellNums[0]];
            var originPos = firstCell.Position.Origin;
            var originRot = firstCell.Position.Orientation;
            var invRot = Quaternion.Conjugate(originRot);

            var prefab = new DungeonPrefab { SourceLandblock = sourceLandblock };
            var cellIndexMap = new Dictionary<ushort, int>();

            for (int i = 0; i < cellNums.Length; i++) {
                var cn = cellNums[i];
                var ec = allCells[cn];
                cellIndexMap[cn] = i;

                var relPos = Vector3.Transform(ec.Position.Origin - originPos, invRot);
                var relRot = Quaternion.Normalize(invRot * ec.Position.Orientation);
                int portalCount = ec.CellPortals?.Count ?? 0;

                prefab.Cells.Add(new PrefabCell {
                    LocalIndex = i,
                    EnvId = (ushort)ec.EnvironmentId,
                    CellStruct = (ushort)ec.CellStructure,
                    PortalCount = portalCount,
                    OffsetX = relPos.X, OffsetY = relPos.Y, OffsetZ = relPos.Z,
                    RotX = relRot.X, RotY = relRot.Y, RotZ = relRot.Z, RotW = relRot.W,
                    Surfaces = ec.Surfaces != null ? ec.Surfaces.ToList() : new List<ushort>()
                });
            }

            foreach (var cn in cellNums) {
                if (!adj.TryGetValue(cn, out var neighbors)) continue;
                int myIdx = cellIndexMap[cn];
                var ec = allCells[cn];

                foreach (var (neighbor, myPoly, theirPoly) in neighbors) {
                    if (cellSet.Contains(neighbor)) {
                        int otherIdx = cellIndexMap[neighbor];
                        if (myIdx < otherIdx) {
                            prefab.InternalPortals.Add(new PrefabPortal {
                                CellIndexA = myIdx, PolyIdA = myPoly,
                                CellIndexB = otherIdx, PolyIdB = theirPoly
                            });
                        }
                    }
                    else {
                        float nx = 0, ny = 0, nz = 0;
                        uint envFileId = (uint)(ec.EnvironmentId | 0x0D000000);
                        if (dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env) &&
                            env.Cells.TryGetValue((ushort)ec.CellStructure, out var cs)) {
                            var geom = PortalSnapper.GetPortalGeometry(cs, myPoly);
                            if (geom != null) {
                                var cellRot = Quaternion.Normalize(new Quaternion(
                                    prefab.Cells[myIdx].RotX, prefab.Cells[myIdx].RotY,
                                    prefab.Cells[myIdx].RotZ, prefab.Cells[myIdx].RotW));
                                var worldNormal = Vector3.Transform(geom.Value.Normal, cellRot);
                                nx = worldNormal.X; ny = worldNormal.Y; nz = worldNormal.Z;
                            }
                        }

                        prefab.OpenFaces.Add(new PrefabOpenFace {
                            CellIndex = myIdx, PolyId = myPoly,
                            EnvId = (ushort)ec.EnvironmentId,
                            CellStruct = (ushort)ec.CellStructure,
                            NormalX = nx, NormalY = ny, NormalZ = nz
                        });
                    }
                }
            }

            var sig = string.Join("|",
                prefab.Cells.OrderBy(c => c.EnvId).ThenBy(c => c.CellStruct)
                    .Select(c => $"{c.EnvId:X4}_{c.CellStruct}")) +
                $"_P{prefab.InternalPortals.Count}_O{prefab.OpenFaces.Count}";
            prefab.Signature = sig;

            if (prefab.Cells.Count < 2) return null;
            return prefab;
        }

        /// <summary>
        /// Consolidate per-room-type static object observations into a representative set.
        /// For each room type, pick the object placements that appear across the most instances.
        /// </summary>
        private static List<RoomStaticSet> BuildRoomStatics(
            Dictionary<(ushort envId, ushort cs), List<List<(uint id, Vector3 pos, Quaternion rot)>>> raw) {

            var result = new List<RoomStaticSet>();
            foreach (var (key, instances) in raw) {
                if (instances.Count == 0) continue;

                // Pick the instance with the most objects as representative
                var best = instances.OrderByDescending(i => i.Count).First();
                if (best.Count == 0) continue;

                var set = new RoomStaticSet { EnvId = key.envId, CellStruct = key.cs };
                foreach (var (id, pos, rot) in best) {
                    // Count how many other instances also have this object ID
                    int freq = instances.Count(inst => inst.Any(o => o.id == id));
                    set.Placements.Add(new StaticPlacement {
                        ObjectId = id,
                        X = pos.X, Y = pos.Y, Z = pos.Z,
                        RotX = rot.X, RotY = rot.Y, RotZ = rot.Z, RotW = rot.W,
                        Frequency = freq
                    });
                }
                // Only keep objects that appear in at least 30% of instances
                int threshold = Math.Max(1, instances.Count * 3 / 10);
                set.Placements.RemoveAll(p => p.Frequency < threshold);
                if (set.Placements.Count > 0)
                    result.Add(set);
            }
            Console.WriteLine($"[DungeonKnowledge] Built static sets for {result.Count} room types ({result.Sum(s => s.Placements.Count)} total objects)");
            return result;
        }

        /// <summary>
        /// Extract a complete dungeon blueprint -- positions, orientations, portal
        /// connections, surfaces, and graph structure. This is enough data to
        /// reconstruct the dungeon exactly or to re-skin it with different room types.
        /// </summary>
        private static DungeonTemplate? ExtractTemplate(
            Dictionary<ushort, EnvCell> cells, ushort sourceLandblock, string dungeonName) {

            var cellNums = cells.Keys.OrderBy(k => k).ToList();
            var indexMap = new Dictionary<ushort, int>();
            for (int i = 0; i < cellNums.Count; i++)
                indexMap[cellNums[i]] = i;

            var adj = new Dictionary<int, List<int>>();
            for (int i = 0; i < cellNums.Count; i++) adj[i] = new List<int>();

            var connections = new List<TemplateConnection>();
            var drawnEdges = new HashSet<(int, int)>();

            foreach (var (cn, ec) in cells) {
                if (ec.CellPortals == null) continue;
                int myIdx = indexMap[cn];
                foreach (var portal in ec.CellPortals) {
                    if (!indexMap.TryGetValue(portal.OtherCellId, out int otherIdx)) continue;
                    if (!adj[myIdx].Contains(otherIdx)) adj[myIdx].Add(otherIdx);

                    var edgeKey = (Math.Min(myIdx, otherIdx), Math.Max(myIdx, otherIdx));
                    if (drawnEdges.Add(edgeKey)) {
                        connections.Add(new TemplateConnection {
                            NodeA = myIdx,
                            PolyIdA = (ushort)portal.PolygonId,
                            NodeB = otherIdx,
                            PolyIdB = (ushort)portal.OtherPortalId
                        });
                    }
                }
            }

            // BFS to compute depth and detect graph type
            var visited = new HashSet<int>();
            var queue = new Queue<(int node, int depth)>();
            queue.Enqueue((0, 0));
            visited.Add(0);
            int maxDepth = 0;
            int branchCount = 0;

            while (queue.Count > 0) {
                var (node, depth) = queue.Dequeue();
                if (depth > maxDepth) maxDepth = depth;
                foreach (var n in adj[node]) {
                    if (visited.Add(n)) queue.Enqueue((n, depth + 1));
                }
            }

            foreach (var (_, neighbors) in adj) {
                if (neighbors.Count >= 3) branchCount++;
            }

            bool hasCycles = cells.Values.Sum(c => c.CellPortals?.Count ?? 0) / 2 > cellNums.Count - 1;
            string graphType = hasCycles ? "Complex" : branchCount == 0 ? "Linear" : "Tree";

            // Store positions relative to the first cell so templates are origin-independent
            var firstCell = cells[cellNums[0]];
            var originPos = firstCell.Position.Origin;
            var originRot = firstCell.Position.Orientation;
            var invRot = Quaternion.Conjugate(originRot);

            var nodes = new List<TemplateNode>();
            for (int i = 0; i < cellNums.Count; i++) {
                var ec = cells[cellNums[i]];
                int degree = adj[i].Count;
                string role = degree == 0 ? "Isolated" : degree == 1 ? "DeadEnd" : degree == 2 ? "Corridor" : "Junction";
                if (i == 0) role = "Entry";

                var relPos = Vector3.Transform(ec.Position.Origin - originPos, invRot);
                var relRot = Quaternion.Normalize(invRot * ec.Position.Orientation);

                nodes.Add(new TemplateNode {
                    Index = i,
                    EnvId = (ushort)ec.EnvironmentId,
                    CellStruct = (ushort)ec.CellStructure,
                    PortalCount = ec.CellPortals?.Count ?? 0,
                    Role = role,
                    ConnectedTo = adj[i],
                    OffsetX = relPos.X, OffsetY = relPos.Y, OffsetZ = relPos.Z,
                    RotX = relRot.X, RotY = relRot.Y, RotZ = relRot.Z, RotW = relRot.W,
                    Surfaces = ec.Surfaces?.ToList() ?? new List<ushort>()
                });
            }

            string style = PrefabNamer.InferStyle(dungeonName);

            return new DungeonTemplate {
                SourceLandblock = sourceLandblock,
                DungeonName = dungeonName,
                Style = style,
                CellCount = cellNums.Count,
                GraphType = graphType,
                MaxDepth = maxDepth,
                BranchCount = branchCount,
                Nodes = nodes,
                Connections = connections
            };
        }

        /// <summary>
        /// Compute the most common horizontal and vertical grid step from edge offsets.
        /// </summary>
        private static (float h, float v) ComputeGridConstants(List<AdjacencyEdge> edges) {
            var hSteps = new Dictionary<float, int>();
            var vSteps = new Dictionary<float, int>();

            foreach (var e in edges) {
                float absX = MathF.Abs(e.RelOffsetX);
                float absY = MathF.Abs(e.RelOffsetY);
                float absZ = MathF.Abs(e.RelOffsetZ);

                float hStep = MathF.Max(absX, absY);
                if (hStep > 1f) {
                    float rounded = MathF.Round(hStep);
                    hSteps.TryGetValue(rounded, out int hc);
                    hSteps[rounded] = hc + e.Count;
                }
                if (absZ > 1f) {
                    float rounded = MathF.Round(absZ);
                    vSteps.TryGetValue(rounded, out int vc);
                    vSteps[rounded] = vc + e.Count;
                }
            }

            float gridH = hSteps.Count > 0 ? hSteps.OrderByDescending(kv => kv.Value).First().Key : 10f;
            float gridV = vSteps.Count > 0 ? vSteps.OrderByDescending(kv => kv.Value).First().Key : 6f;

            return (gridH, gridV);
        }
    }
}
