using System;
using System.Collections.Generic;
using System.Numerics;
using DatReaderWriter.Types;

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
        private DungeonCell? _savedCell;
        private List<(ushort otherCellNum, CellPortal portal)> _savedOtherPortals = new();

        public string Description => "Delete Cell";

        public RemoveCellCommand(ushort cellNum) {
            _cellNum = cellNum;
        }

        public void Execute(DungeonDocument document) {
            _savedCell = document.GetCell(_cellNum);
            if (_savedCell == null) return;

            _savedCell = CloneCell(_savedCell);

            _savedOtherPortals.Clear();
            foreach (var other in document.Cells) {
                if (other.CellNumber == _cellNum) continue;
                foreach (var cp in other.CellPortals) {
                    if (cp.OtherCellId == _cellNum) {
                        _savedOtherPortals.Add((other.CellNumber, cp));
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

        private static DungeonCell CloneCell(DungeonCell src) => new DungeonCell {
            CellNumber = src.CellNumber,
            EnvironmentId = src.EnvironmentId,
            CellStructure = src.CellStructure,
            Origin = src.Origin,
            Orientation = src.Orientation,
            Flags = src.Flags,
            Surfaces = new List<ushort>(src.Surfaces),
            CellPortals = new List<CellPortal>(src.CellPortals),
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

        public RotateCellCommand(ushort cellNum, float degrees) {
            _cellNum = cellNum;
            _rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);
            _inverseRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -degrees * MathF.PI / 180f);
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
}
