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
        /// <summary>Target number of cells (rooms) in the generated dungeon.</summary>
        public int RoomCount { get; init; } = 10;
        public string Style { get; init; } = "All";
        public int Seed { get; init; } = 0;
        public bool RequireRoof { get; init; } = true;
        public bool AllowVertical { get; init; } = false;
        public bool LockStyle { get; init; } = true;
        /// <summary>When true, restrict generation to only prefabs whose Signature is in FavoritePrefabSignatures.</summary>
        public bool UseFavoritesOnly { get; init; } = false;
        /// <summary>Prefab signatures to use when UseFavoritesOnly is true.</summary>
        public HashSet<string>? FavoritePrefabSignatures { get; init; }
        /// <summary>Custom prefabs (not in KB) to include when resolving favorites.</summary>
        public List<DungeonPrefab>? CustomPrefabs { get; init; }
    }

    /// <summary>
    /// Generates dungeons by chaining prefabs using proven portal transforms from real game data.
    /// Connections use the exact relative offsets/rotations observed in actual AC dungeons,
    /// falling back to geometric snap only when no proven data exists.
    /// </summary>
    public static class DungeonGenerator {

        private const float OverlapMinDistDefault = 6.0f;

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

            var portalIndex = PortalCompatibilityIndex.Build(kb);
            var geoCache = new PortalGeometryCache(dats);

            // Vertical direction labels to exclude when AllowVertical is false
            var verticalDirs = new HashSet<string> { "Up", "Down" };

            bool PassesFilters(DungeonPrefab pf) {
                if (pf.OpenFaces.Count < 1) return false;
                if (p.RequireRoof && pf.HasNoRoof) return false;
                if (!p.AllowVertical && pf.OpenFaceDirections.Any(d => verticalDirs.Contains(d))) return false;
                return true;
            }

            bool useFavorites = p.UseFavoritesOnly && p.FavoritePrefabSignatures is { Count: > 0 };
            var favSigs = p.FavoritePrefabSignatures;
            List<DungeonPrefab>? favPrefabs = null;

            IEnumerable<DungeonPrefab> sourcePool = kb.Prefabs;
            if (useFavorites) {
                // Search KB + custom prefabs when resolving favorites
                IEnumerable<DungeonPrefab> allKnown = kb.Prefabs;
                if (p.CustomPrefabs is { Count: > 0 })
                    allKnown = allKnown.Concat(p.CustomPrefabs);
                favPrefabs = allKnown.Where(pf => favSigs!.Contains(pf.Signature)).ToList();

                // Decompose favorites into room types — this turns whole-dungeon
                // favorites into the individual corridor/chamber geometries they contain.
                var favRoomTypes = new HashSet<(ushort, ushort)>();
                var favSourceLandblocks = new HashSet<int>();
                foreach (var fav in favPrefabs) {
                    foreach (var cell in fav.Cells)
                        favRoomTypes.Add((cell.EnvId, cell.CellStruct));
                    if (fav.SourceLandblock != 0)
                        favSourceLandblocks.Add(fav.SourceLandblock);
                }

                // Build candidate pool in tiers — prefer pieces where ALL cells use
                // favorite room types (tight match), then fall back to looser matching.
                var strictPool = kb.Prefabs.Where(pf =>
                    pf.Category != "Full Dungeon" &&
                    pf.Cells.All(c => favRoomTypes.Contains((c.EnvId, c.CellStruct)))).ToList();

                // Also include pieces from the exact same source dungeons
                var sourceMatches = kb.Prefabs.Where(pf =>
                    pf.Category != "Full Dungeon" &&
                    pf.SourceLandblock != 0 && favSourceLandblocks.Contains(pf.SourceLandblock) &&
                    !strictPool.Contains(pf)).ToList();

                var tightPool = strictPool.Concat(sourceMatches).ToList();

                if (tightPool.Count >= 15) {
                    sourcePool = tightPool;
                    Console.WriteLine($"[DungeonGen] Favorites: {favPrefabs.Count} favorited → " +
                        $"{favRoomTypes.Count} room types, {favSourceLandblocks.Count} source dungeons → " +
                        $"{tightPool.Count} tight-match pieces ({strictPool.Count} all-cells + {sourceMatches.Count} source)");
                }
                else {
                    // Not enough tight matches — fall back to any-cell matching
                    sourcePool = kb.Prefabs.Where(pf =>
                        pf.Category != "Full Dungeon" &&
                        (pf.Cells.Any(c => favRoomTypes.Contains((c.EnvId, c.CellStruct))) ||
                         (pf.SourceLandblock != 0 && favSourceLandblocks.Contains(pf.SourceLandblock))));
                    var looseCount = sourcePool.Count();
                    Console.WriteLine($"[DungeonGen] Favorites: {favPrefabs.Count} favorited → " +
                        $"{favRoomTypes.Count} room types → tight={tightPool.Count} (too few), loose={looseCount} candidates");
                }
            }

            var candidates = sourcePool
                .Where(pf => p.Style == "All" || pf.Style.Equals(p.Style, StringComparison.OrdinalIgnoreCase))
                .Where(PassesFilters)
                .OrderByDescending(pf => pf.UsageCount)
                .Take(500)
                .ToList();

            // If RequireRoof was too strict, relax to allow partial roof
            if (candidates.Count < 20 && p.RequireRoof) {
                candidates = sourcePool
                    .Where(pf => p.Style == "All" || pf.Style.Equals(p.Style, StringComparison.OrdinalIgnoreCase))
                    .Where(pf => pf.OpenFaces.Count >= 1 && !pf.HasNoRoof)
                    .Where(pf => p.AllowVertical || !pf.OpenFaceDirections.Any(d => verticalDirs.Contains(d)))
                    .OrderByDescending(pf => pf.UsageCount)
                    .Take(500)
                    .ToList();
            }

            if (candidates.Count == 0 && useFavorites) {
                Console.WriteLine($"[DungeonGen] Favorites pool produced 0 candidates after filters, relaxing filters");
                candidates = sourcePool
                    .Where(pf => pf.OpenFaces.Count >= 1)
                    .OrderByDescending(pf => pf.UsageCount)
                    .Take(500)
                    .ToList();
            }

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

            // When style is "All" and LockStyle is on, pick the starter first then
            // lock candidates to its style so the dungeon looks consistent.
            string? lockedStyle = null;

            var connectors = candidates.Where(pf => pf.OpenFaces.Count >= 2).ToList();
            var caps = candidates.Where(pf => pf.OpenFaces.Count == 1).ToList();

            // Pick the starter by scoring each connector on how many compatible
            // connections its open faces have in the portal index. Starters with
            // well-connected portals produce much better dungeons.
            DungeonPrefab starter;
            if (connectors.Count > 0) {
                var scored = connectors.Select(pf => {
                    int score = 0;
                    foreach (var of in pf.OpenFaces) {
                        var compat = portalIndex.GetCompatible(of.EnvId, of.CellStruct, of.PolyId);
                        score += compat.Count;
                    }
                    return (pf, score);
                }).OrderByDescending(x => x.score).ToList();
                // Pick randomly from the top 20% best-connected starters for variety
                int topN = Math.Max(1, scored.Count / 5);
                starter = scored[rng.Next(topN)].pf;
            }
            else {
                starter = candidates[rng.Next(candidates.Count)];
            }

            if (p.LockStyle && p.Style == "All" && !string.IsNullOrEmpty(starter.Style)) {
                lockedStyle = starter.Style;
                candidates = candidates.Where(pf =>
                    pf.Style.Equals(lockedStyle, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(pf.Style)).ToList();
                connectors = candidates.Where(pf => pf.OpenFaces.Count >= 2).ToList();
                caps = candidates.Where(pf => pf.OpenFaces.Count == 1).ToList();

                // If locking cut candidates too aggressively, pull from full pool
                if (candidates.Count < 10) {
                    lockedStyle = null;
                    candidates = kb.Prefabs
                        .Where(PassesFilters)
                        .OrderByDescending(pf => pf.UsageCount)
                        .Take(500)
                        .ToList();
                    connectors = candidates.Where(pf => pf.OpenFaces.Count >= 2).ToList();
                    caps = candidates.Where(pf => pf.OpenFaces.Count == 1).ToList();
                }
            }

            var prefabsByRoomType = new Dictionary<(ushort envId, ushort cs), List<DungeonPrefab>>();
            foreach (var pf in candidates) {
                foreach (var of in pf.OpenFaces) {
                    var key = (of.EnvId, of.CellStruct);
                    if (!prefabsByRoomType.TryGetValue(key, out var list)) {
                        list = new List<DungeonPrefab>();
                        prefabsByRoomType[key] = list;
                    }
                    if (!list.Contains(pf)) list.Add(pf);
                }
            }

            // Build lookups from the catalog for overlap detection and bridge preference.
            var roomBounds = new Dictionary<(ushort envId, ushort cs), float>();
            var roomPortalCounts = new Dictionary<(ushort envId, ushort cs), int>();
            foreach (var cr in kb.Catalog) {
                var key = (cr.EnvId, cr.CellStruct);
                if (!roomBounds.ContainsKey(key))
                    roomBounds[key] = MathF.Max(cr.BoundsWidth, cr.BoundsDepth) * 0.5f;
                if (!roomPortalCounts.ContainsKey(key))
                    roomPortalCounts[key] = cr.PortalCount;
            }

            bool useBridges = useFavorites;
            if (useBridges)
                Console.WriteLine($"[DungeonGen] Edge-direct bridging enabled, {roomBounds.Count} catalog rooms with bounds data");

            var favLabel = useFavorites ? $", favorites-only ({favSigs!.Count} sigs)" : "";
            Console.WriteLine($"[DungeonGen] Starter: {starter.DisplayName} ({starter.Cells.Count} cells, {starter.OpenFaces.Count} exits), style={lockedStyle ?? "mixed"}, candidates={candidates.Count} ({prefabsByRoomType.Count} room types){favLabel}, index has {portalIndex.PortalFaceCount} portal faces");

            var doc = new DungeonDocument(new Microsoft.Extensions.Logging.Abstractions.NullLogger<DungeonDocument>());
            doc.SetLandblockKey(landblockKey);

            var placedCellNums = PlacePrefabAtOrigin(doc, dats, starter);
            int totalCells = placedCellNums.Count;

            var placedPositions = new List<Vector3>();
            foreach (var cn in placedCellNums) {
                var c = doc.GetCell(cn);
                if (c != null) placedPositions.Add(c.Origin);
            }

            var frontier = new List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)>();
            CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);

            // --- PHASE 1: Growth ---
            // Target is cell count. Bridge cells don't count toward target.
            int maxAttempts = targetCells * 20;
            int attempts = 0;
            int contentCells = totalCells;
            int indexedPlacements = 0;
            int bridgePlacements = 0;
            int snapPlacements = 0;
            int retiredPortals = 0;
            int edgeMissCount = 0;

            var frontierFailures = new Dictionary<(ushort cellNum, ushort polyId), int>();
            const int maxPortalRetries = 5;

            while (contentCells < targetCells && frontier.Count > 0 && attempts < maxAttempts) {
                attempts++;

                // Bias frontier selection toward portals that extend the dungeon
                // outward rather than filling in gaps. Prefer portals farthest from
                // the centroid -- this produces elongated layouts like real dungeons.
                int fi;
                if (placedPositions.Count >= 3 && frontier.Count >= 3) {
                    var centroid = Vector3.Zero;
                    foreach (var pp in placedPositions) centroid += pp;
                    centroid /= placedPositions.Count;

                    var scored = new List<(int idx, float dist)>();
                    for (int f = 0; f < frontier.Count; f++) {
                        var fc = doc.GetCell(frontier[f].cellNum);
                        if (fc == null) continue;
                        float dist = (fc.Origin - centroid).LengthSquared();
                        scored.Add((f, dist));
                    }
                    if (scored.Count > 0) {
                        scored.Sort((a, b) => b.dist.CompareTo(a.dist));
                        int topN = Math.Max(1, scored.Count / 3);
                        fi = scored[rng.Next(topN)].idx;
                    } else {
                        fi = rng.Next(frontier.Count);
                    }
                }
                else {
                    fi = rng.Next(frontier.Count);
                }

                var (existingCellNum, existingPolyId, existingEnvId, existingCS) = frontier[fi];
                var frontierKey = (existingCellNum, existingPolyId);

                // Structural control: as the dungeon grows, progressively prefer
                // smaller rooms. Early growth allows junctions (branching), middle
                // growth prefers corridors, late growth caps with dead-ends.
                float progress = (float)contentCells / targetCells;
                int preferredMaxExits = progress < 0.3f ? 99    // early: any size, allow branching
                                      : progress < 0.7f ? 3     // middle: corridors and small junctions
                                      : 2;                      // late: corridors and dead-ends only

                bool exitBudgetTight = frontier.Count > totalCells * 1.5f;
                if (exitBudgetTight) preferredMaxExits = Math.Min(preferredMaxExits, 2);

                DungeonPrefab? chosen = null;
                CompatibleRoom? matchedRoom = null;

                var compatible = portalIndex.GetCompatible(existingEnvId, existingCS, existingPolyId);
                if (compatible.Count > 0) {
                    // First: try matching from the favorites/style candidate pool,
                    // preferring rooms that match the template's role.
                    var matchingPrefabs = new List<(DungeonPrefab prefab, CompatibleRoom room, int weight)>();
                    foreach (var cr in compatible) {
                        if (!geoCache.AreCompatible(existingEnvId, existingCS, existingPolyId,
                                cr.EnvId, cr.CellStruct, cr.PolyId))
                            continue;

                        var roomKey = (cr.EnvId, cr.CellStruct);
                        if (prefabsByRoomType.TryGetValue(roomKey, out var pfs)) {
                            foreach (var pf in pfs) {
                                if (pf.OpenFaces.Count > preferredMaxExits) continue;
                                bool hasFace = pf.OpenFaces.Any(of =>
                                    of.EnvId == cr.EnvId && of.CellStruct == cr.CellStruct);
                                if (!hasFace) continue;
                                matchingPrefabs.Add((pf, cr, cr.Count));
                            }
                        }
                    }

                    if (matchingPrefabs.Count > 0) {
                        int totalWeight = matchingPrefabs.Sum(m => m.weight);
                        int roll = rng.Next(totalWeight);
                        int acc = 0;
                        foreach (var (pf, cr, w) in matchingPrefabs) {
                            acc += w;
                            if (roll < acc) { chosen = pf; matchedRoom = cr; break; }
                        }
                    }

                    // Second: edge-direct bridge — prefer corridors (2 portals) over junctions.
                    if (chosen == null && useBridges) {
                        var existingCell = doc.GetCell(existingCellNum);
                        if (existingCell != null) {
                            var bridgeCandidates2 = new List<CompatibleRoom>();
                            foreach (var cr in compatible) {
                                if (!geoCache.AreCompatible(existingEnvId, existingCS, existingPolyId,
                                        cr.EnvId, cr.CellStruct, cr.PolyId))
                                    continue;
                                var bKey = (cr.EnvId, cr.CellStruct);
                                // Filter by roof: skip rooms without ceiling when user wants roofed
                                if (p.RequireRoof) {
                                    var catRoom = kb.Catalog.FirstOrDefault(c => c.EnvId == cr.EnvId && c.CellStruct == cr.CellStruct);
                                    if (catRoom != null && !catRoom.HasCeiling) continue;
                                }
                                // When exit budget is tight, only allow corridor bridges
                                if (exitBudgetTight) {
                                    if (roomPortalCounts.TryGetValue(bKey, out int pc) && pc > 2) continue;
                                }
                                bridgeCandidates2.Add(cr);
                            }
                            // Sort by portal count: prefer corridors (2) over junctions (3+)
                            bridgeCandidates2.Sort((a, b) => {
                                roomPortalCounts.TryGetValue((a.EnvId, a.CellStruct), out int pa);
                                roomPortalCounts.TryGetValue((b.EnvId, b.CellStruct), out int pb);
                                return pa.CompareTo(pb);
                            });
                            bool bridgePlaced = false;
                            foreach (var br in bridgeCandidates2.Take(5)) {
                                var bridgeResult = TryPlaceEdgeDirectBridge(doc, dats, geoCache, kb,
                                    existingCell, existingCellNum, existingPolyId,
                                    br, placedPositions, roomBounds);
                                if (bridgeResult != null) {
                                    totalCells += 1;
                                    bridgePlacements++;
                                    indexedPlacements++;
                                    placedPositions.Add(doc.GetCell(bridgeResult.Value)!.Origin);
                                    frontier.Clear();
                                    frontierFailures.Clear();
                                    CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
                                    bridgePlaced = true;
                                    break;
                                }
                            }
                            if (bridgePlaced) continue;
                        }
                    }
                }
                else {
                    edgeMissCount++;
                }

                if (chosen == null) {
                    frontierFailures.TryGetValue(frontierKey, out int fails);
                    frontierFailures[frontierKey] = fails + 1;

                    // In favorites mode, skip geo-snap entirely -- approximate
                    // positioning produces visible portal misalignment. Only proven
                    // transforms (edge-guided + bridges) produce correct results.
                    if (!useFavorites) {
                        int maxRetries = 5;
                        if (fails < maxRetries) {
                            var pool = exitBudgetTight
                                ? candidates.Where(pf => pf.OpenFaces.Count <= 2).ToList()
                                : (connectors.Count > 0 ? connectors : candidates);
                            if (pool.Count == 0) pool = candidates;
                            int retries = Math.Min(5, pool.Count);
                            List<ushort>? fallbackResult = null;
                            for (int r = 0; r < retries; r++) {
                                var candidate = pool[rng.Next(pool.Count)];
                                fallbackResult = TryAttachPrefab(doc, dats, portalIndex, geoCache,
                                    existingCellNum, existingPolyId, candidate, null, placedPositions);
                                if (fallbackResult != null && fallbackResult.Count > 0) {
                                    chosen = candidate;
                                    break;
                                }
                            }
                            if (fallbackResult != null && fallbackResult.Count > 0) {
                                totalCells += fallbackResult.Count;
                                contentCells += fallbackResult.Count;
                                snapPlacements++;
                                foreach (var cn in fallbackResult) {
                                    var c = doc.GetCell(cn);
                                    if (c != null) placedPositions.Add(c.Origin);
                                }
                                frontier.Clear();
                                frontierFailures.Clear();
                                CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
                                continue;
                            }
                        }
                    }

                    if (fails + 1 >= maxPortalRetries) {
                        frontier.RemoveAt(fi);
                        retiredPortals++;
                    }
                    continue;
                }

                var result = TryAttachPrefab(doc, dats, portalIndex, geoCache,
                    existingCellNum, existingPolyId, chosen, matchedRoom, placedPositions);

                if (result == null || result.Count == 0) {
                    frontierFailures.TryGetValue(frontierKey, out int fails);
                    frontierFailures[frontierKey] = fails + 1;
                    if (fails + 1 >= maxPortalRetries) {
                        frontier.RemoveAt(fi);
                        retiredPortals++;
                    }
                    continue;
                }

                totalCells += result.Count;
                contentCells += result.Count;
                indexedPlacements++;

                foreach (var cn in result) {
                    var c = doc.GetCell(cn);
                    if (c != null) placedPositions.Add(c.Origin);
                }

                frontier.Clear();
                frontierFailures.Clear();
                CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
            }

            Console.WriteLine($"[DungeonGen] Growth: {totalCells} cells ({contentCells} content + {bridgePlacements} bridge), " +
                $"{frontier.Count} open exits, {attempts} attempts, {indexedPlacements} edge-guided" +
                (snapPlacements > 0 ? $", {snapPlacements} geo-snap" : "") +
                $", {retiredPortals} retired");

            // --- PHASE 2: Capping ---
            // Place dead-end rooms on remaining open portals to close branches.
            int maxCaps = Math.Min(frontier.Count, (int)(totalCells * 0.3f));
            int capsPlaced = 0;
            if (maxCaps > 0 && frontier.Count > 0) {
                var capFrontier = new List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)>(frontier);
                for (int i = capFrontier.Count - 1; i > 0; i--) {
                    int j = rng.Next(i + 1);
                    (capFrontier[i], capFrontier[j]) = (capFrontier[j], capFrontier[i]);
                }

                foreach (var (capCellNum, capPolyId, capEnvId, capCS) in capFrontier) {
                    if (capsPlaced >= maxCaps) break;

                    var capCompatible = portalIndex.GetCompatible(capEnvId, capCS, capPolyId);
                    if (capCompatible.Count == 0) continue;

                    var existingCell = doc.GetCell(capCellNum);
                    if (existingCell == null) continue;

                    // Prefer dead-end rooms (1 portal) for capping. Filter by roof.
                    var capCandidates = new List<CompatibleRoom>();
                    foreach (var cr in capCompatible) {
                        if (!geoCache.AreCompatible(capEnvId, capCS, capPolyId,
                                cr.EnvId, cr.CellStruct, cr.PolyId))
                            continue;
                        if (p.RequireRoof) {
                            var catRoom = kb.Catalog.FirstOrDefault(c => c.EnvId == cr.EnvId && c.CellStruct == cr.CellStruct);
                            if (catRoom != null && !catRoom.HasCeiling) continue;
                        }
                        var cKey = (cr.EnvId, cr.CellStruct);
                        if (roomPortalCounts.TryGetValue(cKey, out int pc) && pc == 1)
                            capCandidates.Add(cr);
                    }
                    if (capCandidates.Count == 0) {
                        foreach (var cr in capCompatible) {
                            if (!geoCache.AreCompatible(capEnvId, capCS, capPolyId,
                                    cr.EnvId, cr.CellStruct, cr.PolyId))
                                continue;
                            if (p.RequireRoof) {
                                var catRoom = kb.Catalog.FirstOrDefault(c => c.EnvId == cr.EnvId && c.CellStruct == cr.CellStruct);
                                if (catRoom != null && !catRoom.HasCeiling) continue;
                            }
                            var cKey = (cr.EnvId, cr.CellStruct);
                            if (roomPortalCounts.TryGetValue(cKey, out int pc) && pc <= 2)
                                capCandidates.Add(cr);
                        }
                    }
                    if (capCandidates.Count == 0) continue;

                    var capRoom = capCandidates[rng.Next(capCandidates.Count)];
                    var capResult = TryPlaceEdgeDirectBridge(doc, dats, geoCache, kb,
                        existingCell, capCellNum, capPolyId,
                        capRoom, placedPositions, roomBounds);
                    if (capResult != null) {
                        totalCells++;
                        capsPlaced++;
                        placedPositions.Add(doc.GetCell(capResult.Value)!.Origin);
                    }
                }

                if (capsPlaced > 0) {
                    frontier.Clear();
                    CollectOpenFaces(doc, dats, frontier, p.AllowVertical ? null : geoCache);
                    Console.WriteLine($"[DungeonGen] Capping: placed {capsPlaced} dead-ends, {frontier.Count} exits remaining");
                }
            }

            Console.WriteLine($"[DungeonGen] Final: {totalCells} cells, {frontier.Count} open exits");

            // Post-generation: fix one-way portals that may have been missed during
            // placement. The game client requires symmetric portal entries.
            int portalFixes = doc.AutoFixPortals();
            if (portalFixes > 0)
                Console.WriteLine($"[DungeonGen] Auto-fixed {portalFixes} one-way portal(s)");

            // Post-generation: apply surfaces.
            // In favorites mode, use the surfaces from the favorited prefabs' cells directly
            // rather than the KB catalog — the favorites have the exact textures the user wants.
            string? retextureStyle = lockedStyle ?? (p.Style != "All" ? p.Style : null);
            if (useFavorites && favPrefabs is { Count: > 0 }) {
                int applied = ApplyFavoriteSurfaces(doc, favPrefabs);
                Console.WriteLine($"[DungeonGen] Applied surfaces from favorites to {applied}/{doc.Cells.Count} cells");
            }
            else if (retextureStyle != null) {
                int retextured = RetextureCells(doc, dats, kb, retextureStyle);
                Console.WriteLine($"[DungeonGen] Retextured {retextured}/{doc.Cells.Count} cells for style '{retextureStyle}'");
            }

            // Post-generation: furnish rooms with static objects from the KB.
            if (kb.RoomStatics.Count > 0) {
                int furnished = FurnishRooms(doc, kb);
                if (furnished > 0)
                    Console.WriteLine($"[DungeonGen] Furnished {furnished}/{doc.Cells.Count} rooms with static objects");
            }

            doc.ComputeVisibleCells();

            // Log validation summary so issues are visible during development
            var warnings = doc.Validate();
            if (warnings.Count > 0) {
                Console.WriteLine($"[DungeonGen] Validation warnings ({warnings.Count}):");
                foreach (var w in warnings.Take(10))
                    Console.WriteLine($"  - {w}");
                if (warnings.Count > 10)
                    Console.WriteLine($"  ... and {warnings.Count - 10} more");
            }

            return doc;
        }

        private static List<ushort> PlacePrefabAtOrigin(DungeonDocument doc, IDatReaderWriter dats, DungeonPrefab prefab) {
            var cellMap = new Dictionary<int, ushort>();

            var first = prefab.Cells[0];
            var firstCellNum = doc.AddCell(first.EnvId, first.CellStruct,
                Vector3.Zero, Quaternion.Identity, first.Surfaces.ToList());
            cellMap[0] = firstCellNum;

            PlaceRemainingCells(doc, dats, prefab, cellMap);

            return cellMap.Values.ToList();
        }

        /// <summary>
        /// Try to attach a prefab to an existing cell's open portal.
        /// Uses proven transforms from real game data when available.
        /// </summary>
        private static List<ushort>? TryAttachPrefab(
            DungeonDocument doc, IDatReaderWriter dats,
            PortalCompatibilityIndex portalIndex, PortalGeometryCache geoCache,
            ushort existingCellNum, ushort existingPolyId,
            DungeonPrefab prefab, CompatibleRoom? matchedRoom,
            List<Vector3> placedPositions) {

            var existingCell = doc.GetCell(existingCellNum);
            if (existingCell == null) return null;

            // Strategy 1: Use proven transform from the compatibility index
            if (matchedRoom != null) {
                var result = TryAttachWithProvenTransform(doc, dats, geoCache,
                    existingCell, existingCellNum, existingPolyId,
                    prefab, matchedRoom, placedPositions);
                if (result != null) return result;
            }

            // Strategy 2: Search the compatibility index for ANY matching open face
            foreach (var openFace in prefab.OpenFaces) {
                var match = portalIndex.FindMatch(
                    existingCell.EnvironmentId, existingCell.CellStructure, existingPolyId,
                    openFace.EnvId, openFace.CellStruct);

                if (match != null) {
                    if (!geoCache.AreCompatible(
                        existingCell.EnvironmentId, existingCell.CellStructure, existingPolyId,
                        openFace.EnvId, openFace.CellStruct, match.PolyId))
                        continue;

                    var newOrigin = existingCell.Origin + Vector3.Transform(match.RelOffset, existingCell.Orientation);
                    var newRot = Quaternion.Normalize(existingCell.Orientation * match.RelRot);

                    if (WouldOverlap(newOrigin, placedPositions, prefab, openFace.CellIndex, newRot,
                            excludeOrigin: existingCell.Origin))
                        continue;

                    var cellMap = new Dictionary<int, ushort>();
                    var connectCellNum = doc.AddCell(openFace.EnvId, openFace.CellStruct,
                        newOrigin, newRot,
                        prefab.Cells[openFace.CellIndex].Surfaces.ToList());
                    cellMap[openFace.CellIndex] = connectCellNum;
                    doc.ConnectPortals(existingCellNum, existingPolyId, connectCellNum, match.PolyId);
                    PlaceRemainingCells(doc, dats, prefab, cellMap);
                    return cellMap.Values.ToList();
                }
            }

            // Strategy 3: Geometric snap fallback (only for portals with matching geometry)
            return TryAttachWithGeometricSnap(doc, dats, geoCache,
                existingCell, existingCellNum, existingPolyId,
                prefab, placedPositions);
        }

        /// <summary>
        /// Attach using a proven cell-to-cell transform from the PortalCompatibilityIndex.
        /// This uses the exact relative offset and rotation observed in real game dungeons.
        /// </summary>
        private static List<ushort>? TryAttachWithProvenTransform(
            DungeonDocument doc, IDatReaderWriter dats, PortalGeometryCache geoCache,
            DungeonCellData existingCell, ushort existingCellNum, ushort existingPolyId,
            DungeonPrefab prefab, CompatibleRoom matchedRoom,
            List<Vector3> placedPositions) {

            // Find the open face on the prefab that matches the proven room type
            PrefabOpenFace? connectingFace = null;
            foreach (var of in prefab.OpenFaces) {
                if (of.EnvId == matchedRoom.EnvId && of.CellStruct == matchedRoom.CellStruct) {
                    connectingFace = of;
                    break;
                }
            }
            if (connectingFace == null) return null;

            var rawOrigin = existingCell.Origin + Vector3.Transform(matchedRoom.RelOffset, existingCell.Orientation);
            var newOrigin = SnapToGrid(rawOrigin, 10f, 6f);
            var newRot = Quaternion.Normalize(existingCell.Orientation * matchedRoom.RelRot);

            if (WouldOverlap(newOrigin, placedPositions, prefab, connectingFace.CellIndex, newRot,
                    excludeOrigin: existingCell.Origin))
                return null;

            var cellMap = new Dictionary<int, ushort>();
            var prefabCell = prefab.Cells[connectingFace.CellIndex];
            var connectCellNum = doc.AddCell(prefabCell.EnvId, prefabCell.CellStruct,
                newOrigin, newRot, prefabCell.Surfaces.ToList());
            cellMap[connectingFace.CellIndex] = connectCellNum;

            doc.ConnectPortals(existingCellNum, existingPolyId, connectCellNum, matchedRoom.PolyId);

            PlaceRemainingCells(doc, dats, prefab, cellMap);
            return cellMap.Values.ToList();
        }

        /// <summary>
        /// Fallback: use geometric portal snap when no proven transform exists.
        /// Only used for connections not found in the knowledge base.
        /// </summary>
        private static List<ushort>? TryAttachWithGeometricSnap(
            DungeonDocument doc, IDatReaderWriter dats, PortalGeometryCache geoCache,
            DungeonCellData existingCell, ushort existingCellNum, ushort existingPolyId,
            DungeonPrefab prefab, List<Vector3> placedPositions) {

            uint existingEnvFileId = (uint)(existingCell.EnvironmentId | 0x0D000000);
            if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(existingEnvFileId, out var existingEnv)) return null;
            if (!existingEnv.Cells.TryGetValue(existingCell.CellStructure, out var existingCS)) return null;

            var targetGeom = PortalSnapper.GetPortalGeometry(existingCS, existingPolyId);
            if (targetGeom == null) return null;

            var (targetCentroid, targetNormal) = PortalSnapper.TransformPortalToWorld(
                targetGeom.Value, existingCell.Origin, existingCell.Orientation);

            foreach (var openFace in prefab.OpenFaces) {
                var prefabCell = prefab.Cells[openFace.CellIndex];
                uint prefabEnvFileId = (uint)(prefabCell.EnvId | 0x0D000000);
                if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(prefabEnvFileId, out var prefabEnv)) continue;
                if (!prefabEnv.Cells.TryGetValue(prefabCell.CellStruct, out var prefabCS)) continue;

                if (!geoCache.AreCompatible(
                    existingCell.EnvironmentId, existingCell.CellStructure, existingPolyId,
                    prefabCell.EnvId, prefabCell.CellStruct, openFace.PolyId))
                    continue;

                var sourceGeom = PortalSnapper.GetPortalGeometry(prefabCS, openFace.PolyId);
                if (sourceGeom == null) continue;

                var (snapOrigin, snapRot) = PortalSnapper.ComputeSnapTransform(
                    targetCentroid, targetNormal, sourceGeom.Value);

                if (WouldOverlap(snapOrigin, placedPositions, prefab, openFace.CellIndex, snapRot,
                        excludeOrigin: existingCell.Origin))
                    continue;

                var cellMap = new Dictionary<int, ushort>();
                var connectCellNum = doc.AddCell(prefabCell.EnvId, prefabCell.CellStruct,
                    snapOrigin, snapRot, prefabCell.Surfaces.ToList());
                cellMap[openFace.CellIndex] = connectCellNum;

                doc.ConnectPortals(existingCellNum, existingPolyId, connectCellNum, openFace.PolyId);
                PlaceRemainingCells(doc, dats, prefab, cellMap);

                return cellMap.Values.ToList();
            }

            return null;
        }

        /// <summary>
        /// Place a single room using a proven edge transform directly, without needing
        /// a prefab. Gets exact surface data from the DAT so the cell renders correctly.
        /// </summary>
        private static ushort? TryPlaceEdgeDirectBridge(
            DungeonDocument doc, IDatReaderWriter dats, PortalGeometryCache geoCache,
            DungeonKnowledgeBase kb,
            DungeonCellData existingCell, ushort existingCellNum, ushort existingPolyId,
            CompatibleRoom bridgeRoom, List<Vector3> placedPositions,
            Dictionary<(ushort, ushort), float>? roomBounds) {

            var rawOrigin = existingCell.Origin + Vector3.Transform(bridgeRoom.RelOffset, existingCell.Orientation);
            var newOrigin = SnapToGrid(rawOrigin, 10f, 6f);
            var newRot = Quaternion.Normalize(existingCell.Orientation * bridgeRoom.RelRot);

            if (WouldOverlapSingle(newOrigin, placedPositions, bridgeRoom.EnvId, bridgeRoom.CellStruct, roomBounds,
                    excludeOrigin: existingCell.Origin))
                return null;

            // Get exact surfaces from the DAT by finding a real EnvCell that uses
            // this room type. The catalog's SampleSurfaces may have the wrong count.
            var surfaces = FindSurfacesFromDat(dats, bridgeRoom.EnvId, bridgeRoom.CellStruct);
            if (surfaces == null || surfaces.Count == 0) {
                var catalogRoom = kb.Catalog.FirstOrDefault(cr =>
                    cr.EnvId == bridgeRoom.EnvId && cr.CellStruct == bridgeRoom.CellStruct);
                if (catalogRoom?.SampleSurfaces != null && catalogRoom.SampleSurfaces.Count > 0)
                    surfaces = new List<ushort>(catalogRoom.SampleSurfaces);
                else
                    surfaces = new List<ushort> { 0x032A };
            }

            var cellNum = doc.AddCell(bridgeRoom.EnvId, bridgeRoom.CellStruct,
                newOrigin, newRot, surfaces);
            doc.ConnectPortals(existingCellNum, existingPolyId, cellNum, bridgeRoom.PolyId);
            return cellNum;
        }

        /// <summary>
        /// Scan LandBlockInfo entries to find an EnvCell that uses the given room type
        /// and return its surface list. This gives us the exact surface count that the
        /// geometry requires, avoiding rendering errors from mismatched surface slots.
        /// </summary>
        private static List<ushort>? FindSurfacesFromDat(IDatReaderWriter dats, ushort envId, ushort cellStruct) {
            try {
                var lbiIds = dats.Dats.GetAllIdsOfType<DatReaderWriter.DBObjs.LandBlockInfo>().Take(2000).ToArray();
                if (lbiIds.Length == 0) lbiIds = dats.Dats.Cell.GetAllIdsOfType<DatReaderWriter.DBObjs.LandBlockInfo>().Take(2000).ToArray();
                foreach (var lbiId in lbiIds) {
                    if (!dats.TryGet<DatReaderWriter.DBObjs.LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) continue;
                    uint lbId = lbiId >> 16;
                    for (uint i = 0; i < lbi.NumCells && i < 100; i++) {
                        uint cellId = (lbId << 16) | (0x0100 + i);
                        if (!dats.TryGet<DatReaderWriter.DBObjs.EnvCell>(cellId, out var ec)) continue;
                        if (ec.EnvironmentId == envId && ec.CellStructure == cellStruct && ec.Surfaces.Count > 0)
                            return new List<ushort>(ec.Surfaces);
                    }
                }
            } catch { }
            return null;
        }

        /// <summary>
        /// Check if placing a prefab at the given position would overlap existing cells.
        /// Uses catalog room dimensions. Skips the connecting cell's origin since rooms
        /// joined by portals are SUPPOSED to be adjacent.
        /// </summary>
        private static bool WouldOverlap(Vector3 connectOrigin, List<Vector3> existing,
            DungeonPrefab prefab, int connectCellIdx, Quaternion connectRot,
            Dictionary<(ushort, ushort), float>? roomBounds = null,
            Vector3? excludeOrigin = null) {

            if (existing.Count == 0) return false;

            var connectPC = prefab.Cells[connectCellIdx];
            var connectOffset = new Vector3(connectPC.OffsetX, connectPC.OffsetY, connectPC.OffsetZ);
            var connectRelRot = Quaternion.Normalize(new Quaternion(connectPC.RotX, connectPC.RotY, connectPC.RotZ, connectPC.RotW));

            Quaternion invRelRot = connectRelRot.LengthSquared() > 0.01f ? Quaternion.Inverse(connectRelRot) : Quaternion.Identity;
            var worldBaseRot = Quaternion.Normalize(connectRot * invRelRot);
            var worldBaseOrigin = connectOrigin - Vector3.Transform(connectOffset, worldBaseRot);

            for (int i = 0; i < prefab.Cells.Count; i++) {
                var pc = prefab.Cells[i];
                Vector3 cellWorldPos;
                if (i == connectCellIdx) {
                    cellWorldPos = connectOrigin;
                }
                else {
                    var offset = new Vector3(pc.OffsetX, pc.OffsetY, pc.OffsetZ);
                    cellWorldPos = worldBaseOrigin + Vector3.Transform(offset, worldBaseRot);
                }

                float halfWidth = OverlapMinDistDefault;
                if (roomBounds != null && roomBounds.TryGetValue((pc.EnvId, pc.CellStruct), out var r))
                    halfWidth = MathF.Max(r, 3.0f);
                float minDist = halfWidth * 1.5f;

                foreach (var ep in existing) {
                    if (excludeOrigin.HasValue && (ep - excludeOrigin.Value).LengthSquared() < 1f)
                        continue;
                    float dist = (cellWorldPos - ep).Length();
                    if (dist < minDist) return true;
                }
            }

            return false;
        }

        private static bool WouldOverlapSingle(Vector3 origin, List<Vector3> existing,
            ushort envId, ushort cellStruct, Dictionary<(ushort, ushort), float>? roomBounds,
            Vector3? excludeOrigin = null) {

            float halfWidth = OverlapMinDistDefault;
            if (roomBounds != null && roomBounds.TryGetValue((envId, cellStruct), out var r))
                halfWidth = MathF.Max(r, 3.0f);
            float radius = halfWidth * 1.5f;

            foreach (var ep in existing) {
                if (excludeOrigin.HasValue && (ep - excludeOrigin.Value).LengthSquared() < 1f)
                    continue;
                if ((origin - ep).Length() < radius) return true;
            }
            return false;
        }

        /// <summary>
        /// Snap a position to the dungeon grid. Real AC dungeons use a 10-unit
        /// horizontal and 6-unit vertical grid. Snapping eliminates floating-point
        /// drift from chained transforms.
        /// </summary>
        private static Vector3 SnapToGrid(Vector3 pos, float gridH, float gridV) {
            if (gridH <= 0 || gridV <= 0) return pos;
            return new Vector3(
                MathF.Round(pos.X / gridH) * gridH,
                MathF.Round(pos.Y / gridH) * gridH,
                MathF.Round(pos.Z / gridV) * gridV);
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

        /// <param name="geoCache">When non-null, skip portals with vertical normals (Up/Down).</param>
        private static void CollectOpenFaces(
            DungeonDocument doc, IDatReaderWriter dats,
            List<(ushort cellNum, ushort polyId, ushort envId, ushort cellStruct)> frontier,
            PortalGeometryCache? skipVerticalGeoCache = null) {

            foreach (var dc in doc.Cells) {
                uint envFileId = (uint)(dc.EnvironmentId | 0x0D000000);
                if (!dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) continue;
                if (!env.Cells.TryGetValue(dc.CellStructure, out var cs)) continue;

                var allPortals = PortalSnapper.GetPortalPolygonIds(cs);
                var connected = new HashSet<ushort>(dc.CellPortals.Select(cp => cp.PolygonId));

                foreach (var pid in allPortals) {
                    if (connected.Contains(pid)) continue;

                    if (skipVerticalGeoCache != null) {
                        var geom = PortalSnapper.GetPortalGeometry(cs, pid);
                        if (geom != null) {
                            var worldNormal = Vector3.Transform(geom.Value.Normal, dc.Orientation);
                            if (MathF.Abs(worldNormal.Z) > 0.7f) continue;
                        }
                    }

                    frontier.Add((dc.CellNumber, pid, dc.EnvironmentId, dc.CellStructure));
                }
            }
        }

        /// <summary>
        /// Apply surfaces from favorited prefabs to generated cells. Builds a lookup of
        /// (envId, cellStruct) → surfaces from the favorites, then overwrites each generated
        /// cell's surfaces with the favorite version. This preserves the exact textures from
        /// the dungeons the user favorited.
        /// </summary>
        private static int ApplyFavoriteSurfaces(DungeonDocument doc, List<DungeonPrefab> favPrefabs) {
            var surfaceLookup = new Dictionary<(ushort, ushort), List<ushort>>();
            foreach (var fav in favPrefabs) {
                foreach (var cell in fav.Cells) {
                    if (cell.Surfaces.Count == 0) continue;
                    var key = (cell.EnvId, cell.CellStruct);
                    if (!surfaceLookup.ContainsKey(key))
                        surfaceLookup[key] = new List<ushort>(cell.Surfaces);
                }
            }

            int applied = 0;
            foreach (var dc in doc.Cells) {
                var key = (dc.EnvironmentId, dc.CellStructure);
                if (surfaceLookup.TryGetValue(key, out var favSurfaces) && favSurfaces.Count == dc.Surfaces.Count) {
                    dc.Surfaces.Clear();
                    dc.Surfaces.AddRange(favSurfaces);
                    applied++;
                }
            }
            return applied;
        }

        /// <summary>
        /// Retexture all cells in the document to use surfaces matching the target style.
        /// For each cell, looks up the catalog for a room of the same geometry + style.
        /// If found and surface slot count matches, replaces the cell's surfaces.
        /// Falls back to a style-wide "dominant palette" for cells without a direct match.
        /// </summary>
        private static int RetextureCells(DungeonDocument doc, IDatReaderWriter dats,
            DungeonKnowledgeBase kb, string style) {

            // Build lookup: (envId, cellStruct) → surfaces for the target style
            var styleSurfaces = new Dictionary<(ushort, ushort), List<ushort>>();
            foreach (var cr in kb.Catalog) {
                if (!cr.Style.Equals(style, StringComparison.OrdinalIgnoreCase)) continue;
                if (cr.SampleSurfaces.Count == 0) continue;
                var key = (cr.EnvId, cr.CellStruct);
                if (!styleSurfaces.ContainsKey(key))
                    styleSurfaces[key] = cr.SampleSurfaces;
            }

            // Build a fallback palette: for each required slot count, collect
            // the most common surface ID at each slot position across the style.
            var surfacesBySlotCount = new Dictionary<int, List<List<ushort>>>();
            foreach (var surfaces in styleSurfaces.Values) {
                int count = surfaces.Count;
                if (!surfacesBySlotCount.TryGetValue(count, out var lists)) {
                    lists = new List<List<ushort>>();
                    surfacesBySlotCount[count] = lists;
                }
                lists.Add(surfaces);
            }

            var fallbackPalette = new Dictionary<int, List<ushort>>();
            foreach (var (slotCount, surfaceLists) in surfacesBySlotCount) {
                var palette = new List<ushort>();
                for (int i = 0; i < slotCount; i++) {
                    var freqs = new Dictionary<ushort, int>();
                    foreach (var sl in surfaceLists) {
                        if (i < sl.Count) {
                            freqs.TryGetValue(sl[i], out int f);
                            freqs[sl[i]] = f + 1;
                        }
                    }
                    palette.Add(freqs.Count > 0 ? freqs.OrderByDescending(kv => kv.Value).First().Key : (ushort)0x032A);
                }
                fallbackPalette[slotCount] = palette;
            }

            int retextured = 0;
            foreach (var dc in doc.Cells) {
                if (dc.Surfaces.Count == 0) continue;
                int needed = dc.Surfaces.Count;

                // Direct match: same room type exists in the style
                var roomKey = (dc.EnvironmentId, dc.CellStructure);
                if (styleSurfaces.TryGetValue(roomKey, out var directMatch) && directMatch.Count == needed) {
                    dc.Surfaces.Clear();
                    dc.Surfaces.AddRange(directMatch);
                    retextured++;
                    continue;
                }

                // Fallback: use the dominant palette for this slot count
                if (fallbackPalette.TryGetValue(needed, out var palette)) {
                    dc.Surfaces.Clear();
                    dc.Surfaces.AddRange(palette);
                    retextured++;
                    continue;
                }

                // Last resort: find the closest slot count palette and stretch/shrink
                if (fallbackPalette.Count > 0) {
                    var closest = fallbackPalette.OrderBy(kv => Math.Abs(kv.Key - needed)).First().Value;
                    dc.Surfaces.Clear();
                    for (int i = 0; i < needed; i++)
                        dc.Surfaces.Add(closest[i % closest.Count]);
                    retextured++;
                }
            }

            return retextured;
        }

        /// <summary>
        /// Add static objects (torches, furniture, decorations) to generated rooms
        /// using the most common object placements observed for each room type.
        /// Objects are placed in cell-local coordinates and transformed to world space.
        /// </summary>
        private static int FurnishRooms(DungeonDocument doc, DungeonKnowledgeBase kb) {
            var staticsLookup = new Dictionary<(ushort, ushort), RoomStaticSet>();
            foreach (var rs in kb.RoomStatics)
                staticsLookup.TryAdd((rs.EnvId, rs.CellStruct), rs);

            int furnished = 0;
            foreach (var dc in doc.Cells) {
                if (dc.StaticObjects.Count > 0) continue;
                var key = (dc.EnvironmentId, dc.CellStructure);
                if (!staticsLookup.TryGetValue(key, out var statics)) continue;

                foreach (var sp in statics.Placements) {
                    var localPos = new Vector3(sp.X, sp.Y, sp.Z);
                    var localRot = new Quaternion(sp.RotX, sp.RotY, sp.RotZ, sp.RotW);

                    var worldPos = dc.Origin + Vector3.Transform(localPos, dc.Orientation);
                    var worldRot = Quaternion.Normalize(dc.Orientation * localRot);

                    dc.StaticObjects.Add(new WorldBuilder.Shared.Documents.DungeonStabData {
                        Id = sp.ObjectId,
                        Origin = worldPos,
                        Orientation = worldRot
                    });
                }
                if (statics.Placements.Count > 0) furnished++;
            }

            doc.MarkDirty();
            return furnished;
        }
    }
}
