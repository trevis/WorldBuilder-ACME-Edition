using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Dungeon {
    public interface IDungeonCommand {
        void Execute(DungeonDocument document);
        void Undo(DungeonDocument document);
        string Description { get; }
    }

    public class DungeonCommandHistory {
        private readonly Stack<IDungeonCommand> _undoStack = new();
        private readonly Stack<IDungeonCommand> _redoStack = new();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;
        public string? LastCommandDescription => _undoStack.Count > 0 ? _undoStack.Peek().Description : null;

        public event EventHandler? Changed;

        public void Execute(IDungeonCommand command, DungeonDocument document) {
            command.Execute(document);
            _undoStack.Push(command);
            _redoStack.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Undo(DungeonDocument document) {
            if (_undoStack.Count == 0) return;
            var command = _undoStack.Pop();
            command.Undo(document);
            _redoStack.Push(command);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Redo(DungeonDocument document) {
            if (_redoStack.Count == 0) return;
            var command = _redoStack.Pop();
            command.Execute(document);
            _undoStack.Push(command);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear() {
            _undoStack.Clear();
            _redoStack.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public class AddCellCommand : IDungeonCommand {
        private readonly ushort _environmentId;
        private readonly ushort _cellStructure;
        private readonly Vector3 _origin;
        private readonly Quaternion _orientation;
        private readonly List<ushort> _surfaces;
        private readonly ushort? _connectToCellNum;
        private readonly ushort _connectToPolyId;
        private readonly ushort _sourcePolyId;
        private ushort _createdCellNum;

        public string Description => "Place Cell";

        public AddCellCommand(ushort envId, ushort cellStruct, Vector3 origin, Quaternion orientation,
            List<ushort> surfaces, ushort? connectToCellNum = null, ushort connectToPolyId = 0, ushort sourcePolyId = 0) {
            _environmentId = envId;
            _cellStructure = cellStruct;
            _origin = origin;
            _orientation = orientation;
            _surfaces = new List<ushort>(surfaces);
            _connectToCellNum = connectToCellNum;
            _connectToPolyId = connectToPolyId;
            _sourcePolyId = sourcePolyId;
        }

        public void Execute(DungeonDocument document) {
            _createdCellNum = document.AddCell(_environmentId, _cellStructure, _origin, _orientation, _surfaces);
            if (_connectToCellNum.HasValue) {
                document.ConnectPortals(_connectToCellNum.Value, _connectToPolyId, _createdCellNum, _sourcePolyId);
            }
        }

        public void Undo(DungeonDocument document) {
            document.RemoveCell(_createdCellNum);
        }
    }

    public class RemoveCellCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private DungeonCellData? _savedCell;
        private List<(ushort otherCellNum, DungeonCellPortalData portal)> _savedOtherPortals = new();

        public string Description => "Delete Cell";

        public RemoveCellCommand(ushort cellNum) {
            _cellNum = cellNum;
        }

        public void Execute(DungeonDocument document) {
            var src = document.GetCell(_cellNum);
            if (src == null) return;

            _savedCell = CloneCell(src);

            _savedOtherPortals.Clear();
            foreach (var other in document.Cells) {
                if (other.CellNumber == _cellNum) continue;
                foreach (var cp in other.CellPortals) {
                    if (cp.OtherCellId == _cellNum) {
                        _savedOtherPortals.Add((other.CellNumber, new DungeonCellPortalData {
                            OtherCellId = cp.OtherCellId,
                            PolygonId = cp.PolygonId,
                            OtherPortalId = cp.OtherPortalId,
                            Flags = cp.Flags
                        }));
                    }
                }
            }

            document.RemoveCell(_cellNum);
        }

        public void Undo(DungeonDocument document) {
            if (_savedCell == null) return;

            var restored = CloneCell(_savedCell);
            document.Cells.Add(restored);

            foreach (var (otherCellNum, portal) in _savedOtherPortals) {
                var otherCell = document.GetCell(otherCellNum);
                otherCell?.CellPortals.Add(portal);
            }
        }

        private static DungeonCellData CloneCell(DungeonCellData src) => new DungeonCellData {
            CellNumber = src.CellNumber,
            EnvironmentId = src.EnvironmentId,
            CellStructure = src.CellStructure,
            Origin = src.Origin,
            Orientation = src.Orientation,
            Flags = src.Flags,
            Surfaces = new List<ushort>(src.Surfaces),
            CellPortals = new List<DungeonCellPortalData>(src.CellPortals),
            VisibleCells = new List<ushort>(src.VisibleCells),
        };
    }

    public class NudgeCellCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly Vector3 _offset;

        public string Description => "Move Cell";

        public NudgeCellCommand(ushort cellNum, Vector3 offset) {
            _cellNum = cellNum;
            _offset = offset;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Origin += _offset;
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Origin -= _offset;
        }
    }

    public class RotateCellCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly Quaternion _rotation;
        private readonly Quaternion _inverseRotation;

        public string Description => "Rotate Cell";

        public RotateCellCommand(ushort cellNum, float degrees, Vector3? axis = null) {
            _cellNum = cellNum;
            var ax = axis ?? Vector3.UnitZ;
            _rotation = Quaternion.CreateFromAxisAngle(ax, degrees * MathF.PI / 180f);
            _inverseRotation = Quaternion.CreateFromAxisAngle(ax, -degrees * MathF.PI / 180f);
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Orientation = Quaternion.Normalize(_rotation * cell.Orientation);
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null) cell.Orientation = Quaternion.Normalize(_inverseRotation * cell.Orientation);
        }
    }

    public class MoveStaticObjectCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly int _objectIndex;
        private readonly Vector3 _delta;

        public string Description => "Move Object";

        public MoveStaticObjectCommand(ushort cellNum, int objectIndex, Vector3 delta) {
            _cellNum = cellNum;
            _objectIndex = objectIndex;
            _delta = delta;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Origin += _delta;
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Origin -= _delta;
        }
    }

    public class RotateStaticObjectCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly int _objectIndex;
        private readonly Quaternion _rotation;
        private readonly Quaternion _inverseRotation;

        public string Description => "Rotate Object";

        public RotateStaticObjectCommand(ushort cellNum, int objectIndex, float degrees) {
            _cellNum = cellNum;
            _objectIndex = objectIndex;
            _rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);
            _inverseRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -degrees * MathF.PI / 180f);
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Orientation = Quaternion.Normalize(_rotation * cell.StaticObjects[_objectIndex].Orientation);
        }

        public void Undo(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell != null && _objectIndex < cell.StaticObjects.Count)
                cell.StaticObjects[_objectIndex].Orientation = Quaternion.Normalize(_inverseRotation * cell.StaticObjects[_objectIndex].Orientation);
        }
    }

    public class PasteCellsCommand : IDungeonCommand {
        private readonly List<DungeonCellData> _cellTemplates;
        private readonly Vector3 _offset;
        private readonly List<ushort> _createdCellNums = new();

        public string Description => $"Paste {_cellTemplates.Count} Cell(s)";

        public PasteCellsCommand(List<DungeonCellData> cellTemplates, Vector3 offset) {
            _cellTemplates = cellTemplates;
            _offset = offset;
        }

        public void Execute(DungeonDocument document) {
            _createdCellNums.Clear();

            var cellMap = new Dictionary<ushort, ushort>();
            foreach (var template in _cellTemplates) {
                var newNum = document.AllocateCellNumber();
                cellMap[template.CellNumber] = newNum;
                _createdCellNums.Add(newNum);
            }

            for (int i = 0; i < _cellTemplates.Count; i++) {
                var src = _cellTemplates[i];
                var newCell = new DungeonCellData {
                    CellNumber = _createdCellNums[i],
                    EnvironmentId = src.EnvironmentId,
                    CellStructure = src.CellStructure,
                    Origin = src.Origin + _offset,
                    Orientation = src.Orientation,
                    Flags = src.Flags,
                    RestrictionObj = src.RestrictionObj,
                };
                newCell.Surfaces.AddRange(src.Surfaces);

                foreach (var cp in src.CellPortals) {
                    if (cellMap.TryGetValue(cp.OtherCellId, out var mappedOther)) {
                        newCell.CellPortals.Add(new DungeonCellPortalData {
                            OtherCellId = mappedOther,
                            PolygonId = cp.PolygonId,
                            OtherPortalId = cp.OtherPortalId,
                            Flags = cp.Flags
                        });
                    }
                }

                foreach (var vc in src.VisibleCells) {
                    if (cellMap.TryGetValue(vc, out var mappedVc))
                        newCell.VisibleCells.Add(mappedVc);
                }

                foreach (var stab in src.StaticObjects) {
                    newCell.StaticObjects.Add(new DungeonStabData {
                        Id = stab.Id,
                        Origin = stab.Origin,
                        Orientation = stab.Orientation
                    });
                }

                document.Cells.Add(newCell);
            }
            document.MarkDirty();
        }

        public void Undo(DungeonDocument document) {
            foreach (var cellNum in _createdCellNums) {
                document.RemoveCell(cellNum);
            }
        }

        public IReadOnlyList<ushort> CreatedCellNums => _createdCellNums;
    }

    public class DeleteStaticObjectCommand : IDungeonCommand {
        private readonly ushort _cellNum;
        private readonly int _objectIndex;
        private DungeonStabData? _savedObject;

        public string Description => "Delete Object";

        public DeleteStaticObjectCommand(ushort cellNum, int objectIndex) {
            _cellNum = cellNum;
            _objectIndex = objectIndex;
        }

        public void Execute(DungeonDocument document) {
            var cell = document.GetCell(_cellNum);
            if (cell == null || _objectIndex >= cell.StaticObjects.Count) return;
            _savedObject = new DungeonStabData {
                Id = cell.StaticObjects[_objectIndex].Id,
                Origin = cell.StaticObjects[_objectIndex].Origin,
                Orientation = cell.StaticObjects[_objectIndex].Orientation
            };
            cell.StaticObjects.RemoveAt(_objectIndex);
        }

        public void Undo(DungeonDocument document) {
            if (_savedObject == null) return;
            var cell = document.GetCell(_cellNum);
            if (cell == null) return;
            cell.StaticObjects.Insert(Math.Min(_objectIndex, cell.StaticObjects.Count), _savedObject);
        }
    }
}
