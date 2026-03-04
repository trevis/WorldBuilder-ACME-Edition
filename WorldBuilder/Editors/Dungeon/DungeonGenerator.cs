using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    public record GeneratorParams {
        public int RoomCount { get; init; } = 10;
        public string Style { get; init; } = "All";
        public int Seed { get; init; } = 0;
    }

    /// <summary>
    /// Generates dungeons by chaining prefabs (multi-cell chunks from real dungeons).
    /// Each prefab has correct internal geometry from actual game data.
    /// Only the connection point between prefabs uses PortalSnapper — a single
    /// portal face alignment, which is reliable.
    /// </summary>
    public static class DungeonGenerator {

        public static DungeonDocument? Generate(
            GeneratorParams p,
            List<RoomEntry> availableRooms,
            IDatReaderWriter dats,
            ushort landblockKey) {

            var rng = p.Seed != 0 ? new Random(p.Seed) : new Random();
            int targetCells = p.RoomCount;

            var kb = DungeonKnowledgeBuilder.LoadCached();
            if (kb == null || kb.Prefabs.Count == 0) {
                Console.WriteLine("[DungeonGen] No knowledge base — run Analyze Rooms first");
                return null;
            }

            // Build compatibility index for edge-guided generation
            var portalIndex = PortalCompatibilityIndex.Build(kb);

            // Filter prefabs by style
            var candidates = kb.Prefabs
                .Where(pf => p.Style == "All" || pf.Style.Equals(p.Style, StringComparison.OrdinalIgnoreCase))
                .Where(pf => pf.OpenFaces.Count >= 1)
                .OrderByDescending(pf => pf.UsageCount)
                .Take(500)
                .ToList();

            if (candidates.Count == 0) {
                candidates = kb.Prefabs
                    .Where(pf => pf.OpenFaces.Count >= 1)
                    .OrderByDescending(pf => pf.UsageCount)
                    .Take(500)
                    .ToList();
            }

            if (candidates.Count == 0) {
                Console.WriteLine("[DungeonGen] No suitable prefabs");
                return null;
            }

            // Build lookup for fast prefab search by room type
            var prefabsByRoomType = new Dictionary<(ushort envId, ushort cs), List<DungeonPrefab>>();
            foreach (var pf in candidates) {
                foreach (var of in pf.OpenFaces) {
                    var key = (of.EnvId, of.CellStruct);
                    if (!prefabsByRoomType.TryGetValue(key, out var list)) {
                        list = new List<DungeonPrefab>();
                        prefabsByRoomType[key] = list;
                    }
                    list.Add(pf);
                }
            }

            var connectors = candidates.Where(pf => pf.OpenFaces.Count >= 2).ToList();
            var caps = candidates.Where(pf => pf.OpenFaces.Count == 1).ToList();

            var starter = connectors.Count > 0
                ? connectors[rng.Next(connectors.Count)]
                : candidates[rng.Next(candidates.Count)];

            Console.WriteLine($"[DungeonGen] Starter: {starter.DisplayName} ({starter.Cells.Count} cells, {starter.OpenFaces.Count} exits), index has {portalIndex.PortalFaceCount} portal faces");

            var doc = new DungeonDocument(new Microsoft.Extensions.Logging.Abstractions.NullLogger<DungeonDocument>());
            doc.SetLandblockKey(landblockKey);

            var placedCellNums = PlacePrefabAtOrigin(doc, dats, starter);
            int totalCells = placedCellNums.Count;

            var frontier = new List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)>();
            CollectOpenFaces(doc, dats, frontier);

            int maxAttempts = targetCells * 10;
            int attempts = 0;
            int prefabsPlaced = 1;
            int indexedPlacements = 0;

            while (totalCells < targetCells && frontier.Count > 0 && attempts < maxAttempts) {
                attempts++;

                int fi = rng.Next(frontier.Count);
                var (existingCellNum, existingPolyId, existingEnvId, existingCS) = frontier[fi];

                // Edge-guided selection: find prefabs with rooms that are proven
                // to connect at this portal face
                DungeonPrefab? chosen = null;
                var compatible = portalIndex.GetCompatible(existingEnvId, existingCS, existingPolyId);
                if (compatible.Count > 0) {
                    var compatRoomTypes = compatible.Select(c => (c.EnvId, c.CellStruct)).ToHashSet();
                    var matchingPrefabs = new List<(DungeonPrefab prefab, int weight)>();
                    foreach (var roomType in compatRoomTypes) {
                        if (prefabsByRoomType.TryGetValue(roomType, out var pfs)) {
                            bool wantCap = totalCells >= targetCells - 3;
                            foreach (var pf in pfs) {
                                if (wantCap && pf.OpenFaces.Count > 1) continue;
                                int weight = compatible.Where(c => c.EnvId == roomType.EnvId && c.CellStruct == roomType.CellStruct)
                                    .Sum(c => c.Count);
                                matchingPrefabs.Add((pf, weight));
                            }
                        }
                    }
                    if (matchingPrefabs.Count > 0) {
                        int totalWeight = matchingPrefabs.Sum(m => m.weight);
                        int roll = rng.Next(totalWeight);
                        int acc = 0;
                        foreach (var (pf, w) in matchingPrefabs) {
                            acc += w;
                            if (roll < acc) { chosen = pf; break; }
                        }
                    }
                }

                // Fall back to random if no indexed match
                if (chosen == null) {
                    var pool = (totalCells >= targetCells - 3 && caps.Count > 0) ? caps :
                               (connectors.Count > 0 ? connectors : candidates);
                    chosen = pool[rng.Next(pool.Count)];
                }
                else {
                    indexedPlacements++;
                }

                var newCells = TryAttachPrefab(doc, dats, existingCellNum, existingPolyId, chosen);
                if (newCells == null || newCells.Count == 0) {
                    frontier.RemoveAt(fi);
                    continue;
                }

                totalCells += newCells.Count;
                prefabsPlaced++;
                frontier.RemoveAt(fi);

                frontier.Clear();
                CollectOpenFaces(doc, dats, frontier);
            }

            Console.WriteLine($"[DungeonGen] Placed {prefabsPlaced} prefabs, {totalCells} total cells ({frontier.Count} open exits, {attempts} attempts, {indexedPlacements} edge-guided)");
            doc.ComputeVisibleCells();
            return doc;
        }

        private static List<ushort> PlacePrefabAtOrigin(DungeonDocument doc, IDatReaderWriter dats, DungeonPrefab prefab) {
            var cellMap = new Dictionary<int, ushort>();

            // Place first cell at origin
            var first = prefab.Cells[0];
            var firstCellNum = doc.AddCell(first.EnvId, first.CellStruct,
                Vector3.Zero, Quaternion.Identity, first.Surfaces.ToList());
            cellMap[0] = firstCellNum;

            // Place remaining cells using stored relative transforms + portal snapping
            PlaceRemainingCells(doc, dats, prefab, cellMap);

            return cellMap.Values.ToList();
        }

        private static List<ushort>? TryAttachPrefab(
            DungeonDocument doc, IDatReaderWriter dats,
            ushort existingCellNum, ushort existingPolyId,
            DungeonPrefab prefab) {

            var existingCell = doc.GetCell(existingCellNum);
            if (existingCell == null) return null;

            uint existingEnvFileId = (uint)(existingCell.EnvironmentId | 0x0D000000);
            if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(existingEnvFileId, out var existingEnv)) return null;
            if (!existingEnv.Cells.TryGetValue(existingCell.CellStructure, out var existingCS)) return null;

            var targetGeom = PortalSnapper.GetPortalGeometry(existingCS, existingPolyId);
            if (targetGeom == null) return null;

            var (targetCentroid, targetNormal) = PortalSnapper.TransformPortalToWorld(
                targetGeom.Value, existingCell.Origin, existingCell.Orientation);

            // Try each open face on the prefab to find one that can connect
            foreach (var openFace in prefab.OpenFaces) {
                var prefabCell = prefab.Cells[openFace.CellIndex];
                uint prefabEnvFileId = (uint)(prefabCell.EnvId | 0x0D000000);
                if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(prefabEnvFileId, out var prefabEnv)) continue;
                if (!prefabEnv.Cells.TryGetValue(prefabCell.CellStruct, out var prefabCS)) continue;

                var sourceGeom = PortalSnapper.GetPortalGeometry(prefabCS, openFace.PolyId);
                if (sourceGeom == null) continue;

                // Check normal compatibility — portals should face each other
                var sourceNormalLocal = sourceGeom.Value.Normal;
                float dot = Vector3.Dot(Vector3.Normalize(sourceNormalLocal),
                    Vector3.Normalize(new Vector3(openFace.NormalX, openFace.NormalY, openFace.NormalZ)));

                var (snapOrigin, snapRot) = PortalSnapper.ComputeSnapTransform(
                    targetCentroid, targetNormal, sourceGeom.Value);

                // The snap transform positions the connecting cell. If the connecting cell
                // is not cell 0, we need to offset all cells relative to it.
                var cellMap = new Dictionary<int, ushort>();

                // Place the connecting cell first
                var connectingPrefabCell = prefab.Cells[openFace.CellIndex];
                var connectCellNum = doc.AddCell(connectingPrefabCell.EnvId, connectingPrefabCell.CellStruct,
                    snapOrigin, snapRot, connectingPrefabCell.Surfaces.ToList());
                cellMap[openFace.CellIndex] = connectCellNum;

                // Connect the existing cell's portal to the new prefab's connecting cell
                doc.ConnectPortals(existingCellNum, existingPolyId, connectCellNum, openFace.PolyId);

                // Place remaining prefab cells relative to the connecting cell
                PlaceRemainingCells(doc, dats, prefab, cellMap);

                return cellMap.Values.ToList();
            }

            return null;
        }

        private static void PlaceRemainingCells(
            DungeonDocument doc, IDatReaderWriter dats,
            DungeonPrefab prefab, Dictionary<int, ushort> cellMap) {

            int baseIdx = cellMap.Keys.First();
            var baseCellNum = cellMap[baseIdx];
            var baseDoc = doc.GetCell(baseCellNum);
            if (baseDoc == null) return;

            var basePC = prefab.Cells[baseIdx];
            var baseOffset = new Vector3(basePC.OffsetX, basePC.OffsetY, basePC.OffsetZ);
            var baseRelRot = Quaternion.Normalize(new Quaternion(basePC.RotX, basePC.RotY, basePC.RotZ, basePC.RotW));

            Quaternion invBaseRelRot = baseRelRot.LengthSquared() > 0.01f ? Quaternion.Inverse(baseRelRot) : Quaternion.Identity;
            var worldBaseRot = Quaternion.Normalize(baseDoc.Orientation * invBaseRelRot);
            var worldBaseOrigin = baseDoc.Origin - Vector3.Transform(baseOffset, worldBaseRot);

            for (int i = 0; i < prefab.Cells.Count; i++) {
                if (cellMap.ContainsKey(i)) continue;

                var pc = prefab.Cells[i];
                var offset = new Vector3(pc.OffsetX, pc.OffsetY, pc.OffsetZ);
                var relRot = Quaternion.Normalize(new Quaternion(pc.RotX, pc.RotY, pc.RotZ, pc.RotW));

                var worldOrigin = worldBaseOrigin + Vector3.Transform(offset, worldBaseRot);
                var worldRot = Quaternion.Normalize(worldBaseRot * relRot);

                var newCellNum = doc.AddCell(pc.EnvId, pc.CellStruct,
                    worldOrigin, worldRot, pc.Surfaces.ToList());
                cellMap[i] = newCellNum;
            }

            foreach (var ip in prefab.InternalPortals) {
                if (cellMap.TryGetValue(ip.CellIndexA, out var cellA) && cellMap.TryGetValue(ip.CellIndexB, out var cellB)) {
                    doc.ConnectPortals(cellA, ip.PolyIdA, cellB, ip.PolyIdB);
                }
            }
        }

        private static void CollectOpenFaces(
            DungeonDocument doc, IDatReaderWriter dats,
            List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)> frontier) {

            foreach (var dc in doc.Cells) {
                uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) continue;

                var allPortals = PortalSnapper.GetPortalPolygonIds(cs);
                var connected = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));

                foreach (var pid in allPortals) {
                    if (!connected.Contains(pid))
                        frontier.Add((dc.CellNumber, pid, dc.EnvironmentId, dc.CellStructure));
                }
            }
        }
    }
}
