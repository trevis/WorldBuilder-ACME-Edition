using CommunityToolkit.Mvvm.DependencyInjection;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using MemoryPack;
using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WorldBuilder.Shared.Lib;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace WorldBuilder.Shared.Documents {
    [MemoryPack.MemoryPackable]
    public partial struct StaticObject {
        public uint Id; // GfxObj or Setup ID
        public bool IsSetup; // True for Setup, false for GfxObj
        public Vector3 Origin; // World-space position
        public Quaternion Orientation; // World-space rotation
        public Vector3 Scale;
    }

    [MemoryPack.MemoryPackable]
    public partial class LandblockData {
        public List<StaticObject> StaticObjects = new();
    }

    public partial class LandblockDocument : BaseDocument {
        public override string Type => nameof(LandblockDocument);

        [MemoryPackInclude]
        private LandblockData _data = new();

        public LandblockDocument(ILogger logger) : base(logger) {
        }

        protected override async Task<bool> InitInternal(IDatReaderWriter datreader, DocumentManager documentManager) {
            var lbIdHex = Id.Replace("landblock_", "");
            var lbId = uint.Parse(lbIdHex, System.Globalization.NumberStyles.HexNumber);
            var infoId = lbId << 16 | 0xFFFE;

            if (datreader.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                foreach (var obj in lbi.Objects) {
                    _data.StaticObjects.Add(new StaticObject {
                        Id = obj.Id,
                        IsSetup = (obj.Id & 0x02000000) != 0,
                        Origin = Offset(obj.Frame.Origin, lbId),
                        Orientation = obj.Frame.Orientation,
                        Scale = Vector3.One
                    });
                }

                foreach (var building in lbi.Buildings) {
                    _data.StaticObjects.Add(new StaticObject {
                        Id = building.ModelId,
                        IsSetup = (building.ModelId & 0x02000000) != 0,
                        Origin = Offset(building.Frame.Origin, lbId),
                        Orientation = building.Frame.Orientation,
                        Scale = Vector3.One
                    });
                }
            }

            ClearDirty();
            return true;
        }

        private Vector3 Offset(Vector3 origin, uint lbId) {
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            return new Vector3(blockX * 192f + origin.X, blockY * 192f + origin.Y, origin.Z);
        }

        protected override byte[] SaveToProjectionInternal() {
            return MemoryPackSerializer.Serialize(_data);
        }

        protected override bool LoadFromProjectionInternal(byte[] projection) {
            _data = MemoryPackSerializer.Deserialize<LandblockData>(projection) ?? new();
            return true;
        }

        protected override Task<bool> SaveToDatsInternal(IDatReaderWriter datwriter, int iteration = 0) {
            var lbIdHex = Id.Replace("landblock_", "");
            var lbId = uint.Parse(lbIdHex, System.Globalization.NumberStyles.HexNumber);
            var infoId = lbId << 16 | 0xFFFE;

            _logger.LogInformation("[LBDoc] Saving landblock 0x{LbId:X4} — {ObjCount} static objects", lbId, _data.StaticObjects.Count);

            // Read original LandBlockInfo to get building data (portals, cells, etc.)
            if (!datwriter.TryGet<LandBlockInfo>(infoId, out var lbi)) {
                lbi = new LandBlockInfo();
                lbi.Id = infoId;
                _logger.LogInformation("[LBDoc]   No original LandBlockInfo found, creating new");
            }
            else {
                _logger.LogInformation("[LBDoc]   Original LandBlockInfo: {ObjCount} objects, {BldCount} buildings",
                    lbi.Objects.Count, lbi.Buildings.Count);
            }

            // Track which StaticObjects have been matched to an original building
            var consumed = new HashSet<int>();

            // For each original building, try to find a matching StaticObject.
            // If found: update the building's position (preserving portal/cell data).
            // If not found: the building was deleted by the user.
            var survivingBuildings = new List<BuildingInfo>();
            foreach (var building in lbi.Buildings) {
                var buildingWorldPos = Offset(building.Frame.Origin, lbId);

                // Find the best matching StaticObject (same ID, closest to original position)
                int bestIdx = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < _data.StaticObjects.Count; i++) {
                    if (consumed.Contains(i)) continue;
                    var obj = _data.StaticObjects[i];
                    if (obj.Id != building.ModelId) continue;

                    float dist = Vector3.Distance(obj.Origin, buildingWorldPos);
                    if (dist < bestDist) {
                        bestDist = dist;
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0) {
                    // Building still exists — update its Frame from the StaticObject (handles moves)
                    consumed.Add(bestIdx);
                    var matched = _data.StaticObjects[bestIdx];
                    var newLocal = ReverseOffset(matched.Origin, lbId);
                    var oldLocal = building.Frame.Origin;

                    if (Vector3.Distance(newLocal, oldLocal) > 0.1f) {
                        _logger.LogInformation("[LBDoc]   Building 0x{Id:X8} MOVED: ({OldX:F1},{OldY:F1},{OldZ:F1}) -> ({NewX:F1},{NewY:F1},{NewZ:F1})",
                            building.ModelId, oldLocal.X, oldLocal.Y, oldLocal.Z, newLocal.X, newLocal.Y, newLocal.Z);
                    }

                    building.Frame = new Frame {
                        Origin = newLocal,
                        Orientation = matched.Orientation
                    };
                    survivingBuildings.Add(building);
                }
                else {
                    _logger.LogInformation("[LBDoc]   Building 0x{Id:X8} DELETED (no matching StaticObject)", building.ModelId);
                }
            }

            lbi.Buildings.Clear();
            lbi.Buildings.AddRange(survivingBuildings);

            // All non-consumed StaticObjects become regular Stab entries
            lbi.Objects.Clear();
            for (int i = 0; i < _data.StaticObjects.Count; i++) {
                if (consumed.Contains(i)) continue;

                var obj = _data.StaticObjects[i];
                lbi.Objects.Add(new Stab {
                    Id = obj.Id,
                    Frame = new Frame {
                        Origin = ReverseOffset(obj.Origin, lbId),
                        Orientation = obj.Orientation
                    }
                });
            }

            _logger.LogInformation("[LBDoc]   Result: {ObjCount} objects, {BldCount} buildings",
                lbi.Objects.Count, lbi.Buildings.Count);

            if (!datwriter.TrySave(lbi, iteration)) {
                _logger.LogError("[LBDoc]   FAILED to save LandBlockInfo 0x{InfoId:X8}", infoId);
                return Task.FromResult(false);
            }

            _logger.LogInformation("[LBDoc]   Saved LandBlockInfo 0x{InfoId:X8} successfully", infoId);
            return Task.FromResult(true);
        }

        private Vector3 ReverseOffset(Vector3 worldPos, uint lbId) {
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            return new Vector3(worldPos.X - blockX * 192f, worldPos.Y - blockY * 192f, worldPos.Z);
        }

        public bool Apply(BaseDocumentEvent evt) {
            if (evt is StaticObjectUpdateEvent objEvt) {
                if (objEvt.IsAdd) {
                    _data.StaticObjects.Add(objEvt.Object);
                }
                else {
                    // Remove by matching Id and Origin
                    var idx = _data.StaticObjects.FindIndex(o =>
                        o.Id == objEvt.Object.Id &&
                        Vector3.Distance(o.Origin, objEvt.Object.Origin) < 0.01f);
                    if (idx >= 0) {
                        _data.StaticObjects.RemoveAt(idx);
                    }
                }
                MarkDirty();
                return true;
            }
            return true;
        }

        public IEnumerable<(Vector3 Position, Quaternion Rotation)> GetStaticSpawns() {
            foreach (var obj in _data.StaticObjects) {
                yield return (obj.Origin, obj.Orientation);
            }
        }

        public IEnumerable<StaticObject> GetStaticObjects() => _data.StaticObjects;

        /// <summary>
        /// Gets the number of static objects in this landblock
        /// </summary>
        public int StaticObjectCount => _data.StaticObjects.Count;

        /// <summary>
        /// Gets a static object by index
        /// </summary>
        public StaticObject GetStaticObject(int index) => _data.StaticObjects[index];

        /// <summary>
        /// Updates the Z (height) coordinate of a static object at the given index
        /// </summary>
        public void SetStaticObjectHeight(int index, float newZ) {
            if (index < 0 || index >= _data.StaticObjects.Count) return;
            var obj = _data.StaticObjects[index];
            obj.Origin = new Vector3(obj.Origin.X, obj.Origin.Y, newZ);
            _data.StaticObjects[index] = obj;
            MarkDirty();
        }

        /// <summary>
        /// Replaces a static object at the given index with updated data
        /// </summary>
        public void UpdateStaticObject(int index, StaticObject updatedObj) {
            if (index < 0 || index >= _data.StaticObjects.Count) return;
            _data.StaticObjects[index] = updatedObj;
            MarkDirty();
        }

        /// <summary>
        /// Adds a new static object to the landblock
        /// </summary>
        public int AddStaticObject(StaticObject obj) {
            _data.StaticObjects.Add(obj);
            MarkDirty();
            return _data.StaticObjects.Count - 1;
        }

        /// <summary>
        /// Removes the static object at the given index
        /// </summary>
        public bool RemoveStaticObject(int index) {
            if (index < 0 || index >= _data.StaticObjects.Count) return false;
            _data.StaticObjects.RemoveAt(index);
            MarkDirty();
            return true;
        }
    }
    public class StaticObjectUpdateEvent : TerrainUpdateEvent {
        public StaticObject Object { get; }
        public bool IsAdd { get; } // True for add, false for remove

        public StaticObjectUpdateEvent(StaticObject obj, bool isAdd) {
            Object = obj;
            IsAdd = isAdd;
        }
    }
}