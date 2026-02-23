using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Tracks all cells in a dungeon being edited. Manages cell ID allocation,
    /// portal connections, and converts to EnvCell objects for rendering.
    /// </summary>
    public class DungeonDocument {
        public ushort LandblockKey { get; set; }
        public List<DungeonCell> Cells { get; } = new();

        private ushort _nextCellNumber = 0x0100;

        public DungeonDocument(ushort landblockKey) {
            LandblockKey = landblockKey;
        }

        /// <summary>
        /// Initialize from existing dungeon cells loaded from cell.dat.
        /// </summary>
        public void LoadFromDat(IDatReaderWriter dats) {
            Cells.Clear();
            uint lbId = LandblockKey;
            uint lbiId = (lbId << 16) | 0xFFFE;

            if (!dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0)
                return;

            for (uint i = 0; i < lbi.NumCells; i++) {
                ushort cellNum = (ushort)(0x0100 + i);
                uint cellId = (lbId << 16) | cellNum;
                if (dats.TryGet<EnvCell>(cellId, out var envCell)) {
                    var dc = new DungeonCell {
                        CellNumber = cellNum,
                        EnvironmentId = envCell.EnvironmentId,
                        CellStructure = envCell.CellStructure,
                        Origin = envCell.Position.Origin,
                        Orientation = envCell.Position.Orientation,
                        Flags = envCell.Flags,
                    };
                    dc.Surfaces.AddRange(envCell.Surfaces);

                    if (envCell.CellPortals != null) {
                        dc.CellPortals.AddRange(envCell.CellPortals);
                    }
                    if (envCell.VisibleCells != null) {
                        dc.VisibleCells.AddRange(envCell.VisibleCells);
                    }

                    Cells.Add(dc);
                }
            }

            if (Cells.Count > 0) {
                _nextCellNumber = (ushort)(Cells.Max(c => c.CellNumber) + 1);
            }
        }

        public ushort AllocateCellNumber() {
            return _nextCellNumber++;
        }

        /// <summary>
        /// Add a new cell to the document. Returns the assigned cell number.
        /// </summary>
        public ushort AddCell(ushort environmentId, ushort cellStructure, Vector3 origin, Quaternion orientation, List<ushort> surfaces) {
            var cellNum = AllocateCellNumber();
            var cell = new DungeonCell {
                CellNumber = cellNum,
                EnvironmentId = environmentId,
                CellStructure = cellStructure,
                Origin = origin,
                Orientation = orientation,
            };
            cell.Surfaces.AddRange(surfaces);
            Cells.Add(cell);
            return cellNum;
        }

        /// <summary>
        /// Connect two cells at their portal polygons (bidirectional).
        /// </summary>
        public void ConnectPortals(ushort cellNumA, ushort polyIdA, ushort cellNumB, ushort polyIdB) {
            var cellA = Cells.FirstOrDefault(c => c.CellNumber == cellNumA);
            var cellB = Cells.FirstOrDefault(c => c.CellNumber == cellNumB);
            if (cellA == null || cellB == null) return;

            cellA.CellPortals.Add(new CellPortal {
                OtherCellId = cellNumB,
                PolygonId = polyIdA,
                OtherPortalId = 0,
                Flags = 0
            });

            cellB.CellPortals.Add(new CellPortal {
                OtherCellId = cellNumA,
                PolygonId = polyIdB,
                OtherPortalId = 0,
                Flags = 0
            });
        }

        public void RemoveCell(ushort cellNumber) {
            var cell = Cells.FirstOrDefault(c => c.CellNumber == cellNumber);
            if (cell == null) return;

            // Remove portal references from other cells pointing to this one
            foreach (var other in Cells) {
                other.CellPortals.RemoveAll(cp => cp.OtherCellId == cellNumber);
            }

            Cells.Remove(cell);
        }

        /// <summary>
        /// Convert all document cells to EnvCell objects for rendering via EnvCellManager.
        /// </summary>
        public List<EnvCell> ToEnvCells() {
            uint lbId = LandblockKey;
            var result = new List<EnvCell>();

            foreach (var dc in Cells) {
                uint fullCellId = (lbId << 16) | dc.CellNumber;
                var envCell = new EnvCell {
                    Id = fullCellId,
                    EnvironmentId = dc.EnvironmentId,
                    CellStructure = dc.CellStructure,
                    Flags = dc.Flags,
                    Position = new Frame {
                        Origin = dc.Origin,
                        Orientation = dc.Orientation
                    }
                };

                envCell.Surfaces.AddRange(dc.Surfaces);
                envCell.CellPortals.AddRange(dc.CellPortals);
                envCell.VisibleCells.AddRange(dc.VisibleCells);

                result.Add(envCell);
            }

            return result;
        }

        public DungeonCell? GetCell(ushort cellNumber) =>
            Cells.FirstOrDefault(c => c.CellNumber == cellNumber);
    }

    public class DungeonCell {
        public ushort CellNumber;
        public ushort EnvironmentId;
        public ushort CellStructure;
        public Vector3 Origin;
        public Quaternion Orientation = Quaternion.Identity;
        public EnvCellFlags Flags;
        public List<ushort> Surfaces = new();
        public List<CellPortal> CellPortals = new();
        public List<ushort> VisibleCells = new();
    }
}
