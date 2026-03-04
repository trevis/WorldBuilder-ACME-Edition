using Avalonia.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using WorldBuilder.Lib;

namespace WorldBuilder.Editors.Dungeon.Tools {

    public class SelectTool : DungeonToolBase {
        public override string Name => "Select";
        public override string IconGlyph => "\u2B9E"; // arrow

        private readonly ObservableCollection<DungeonSubToolBase> _subTools;
        public override ObservableCollection<DungeonSubToolBase> AllSubTools => _subTools;

        private readonly DungeonEditingContext _ctx;

        public SelectTool(DungeonEditingContext ctx) {
            _ctx = ctx;
            _subTools = new ObservableCollection<DungeonSubToolBase> {
                new SelectSubTool(ctx),
                new MoveSubTool(ctx),
            };
        }

        public override void OnActivated() {
            StatusText = "";
            if (SelectedSubTool == null && _subTools.Count > 0)
                ActivateSubTool(_subTools[0]);
        }

        public override void OnDeactivated() {
            SelectedSubTool?.OnDeactivated();
        }

        public override bool HandleMouseDown(MouseState mouseState, DungeonEditingContext ctx) =>
            SelectedSubTool?.HandleMouseDown(mouseState) ?? false;

        public override bool HandleMouseUp(MouseState mouseState, DungeonEditingContext ctx) =>
            SelectedSubTool?.HandleMouseUp(mouseState) ?? false;

        public override bool HandleMouseMove(MouseState mouseState, DungeonEditingContext ctx) =>
            SelectedSubTool?.HandleMouseMove(mouseState) ?? false;

        public override bool HandleKeyDown(KeyEventArgs e, DungeonEditingContext ctx) =>
            SelectedSubTool?.HandleKeyDown(e) ?? false;
    }

    public class SelectSubTool : DungeonSubToolBase {
        public override string Name => "Select";
        public override string IconGlyph => "\u25C6"; // diamond

        private bool _boxSelectActive;
        private Vector2 _boxSelectStart;
        private Vector2 _boxSelectEnd;
        private bool _didHitOnDown;

        public SelectSubTool(DungeonEditingContext ctx) : base(ctx) { }

        public override void OnActivated() { }
        public override void OnDeactivated() { _boxSelectActive = false; }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed || mouseState.RightPressed) return false;
            if (Ctx.Scene?.EnvCellManager == null || Ctx.Document == null) return false;

            var ray = Ctx.ComputeRay(mouseState);
            if (ray == null) return false;
            var (origin, dir) = ray.Value;

            // Object raycast
            var objHit = DungeonObjectRaycast.Raycast(origin, dir, Ctx.Document, Ctx.Scene);
            if (objHit.Hit) {
                var cell = Ctx.Document.GetCell(objHit.CellNumber);
                if (cell != null && objHit.ObjectIndex < cell.StaticObjects.Count) {
                    Ctx.SelectedObjCellNum = objHit.CellNumber;
                    Ctx.SelectedObjIndex = objHit.ObjectIndex;
                    Ctx.SelectedCell = null;
                    Ctx.SelectedCells.Clear();
                    Ctx.NotifySelectionChanged();
                    _didHitOnDown = true;
                    _boxSelectActive = false;
                    return true;
                }
            }

            // Cell raycast
            var hit = Ctx.Raycast(origin, dir);
            if (hit.Hit) {
                bool ctrlAdd = mouseState.CtrlPressed;
                if (ctrlAdd) {
                    var idx = Ctx.SelectedCells.FindIndex(c => c.CellId == hit.Cell.CellId);
                    if (idx >= 0) Ctx.SelectedCells.RemoveAt(idx);
                    else Ctx.SelectedCells.Add(hit.Cell);
                    Ctx.SelectedCell = Ctx.SelectedCells.Count > 0 ? Ctx.SelectedCells[0] : null;
                }
                else {
                    Ctx.SelectedCells.Clear();
                    Ctx.SelectedCells.Add(hit.Cell);
                    Ctx.SelectedCell = hit.Cell;
                }
                Ctx.SelectedObjIndex = -1;
                Ctx.NotifySelectionChanged();
                _didHitOnDown = true;
                _boxSelectActive = false;
                return true;
            }

            // Miss — start box select
            _boxSelectActive = true;
            _boxSelectStart = mouseState.Position;
            _boxSelectEnd = mouseState.Position;
            _didHitOnDown = false;
            return false;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            if (!_boxSelectActive) return false;

            var endPos = mouseState.Position;
            _boxSelectActive = false;

            if ((endPos - _boxSelectStart).Length() > 5) {
                float minX = Math.Min(_boxSelectStart.X, endPos.X);
                float maxX = Math.Max(_boxSelectStart.X, endPos.X);
                float minY = Math.Min(_boxSelectStart.Y, endPos.Y);
                float maxY = Math.Max(_boxSelectStart.Y, endPos.Y);

                Ctx.SelectedCells.Clear();
                Ctx.SelectedCell = null;
                var cells = Ctx.Scene?.EnvCellManager?.GetLoadedCellsForLandblock(Ctx.Document.LandblockKey);
                if (cells != null) {
                    foreach (var cell in cells) {
                        var screenPos = ProjectToScreen(cell.WorldPosition, Ctx);
                        if (screenPos.X >= minX && screenPos.X <= maxX && screenPos.Y >= minY && screenPos.Y <= maxY) {
                            Ctx.SelectedCells.Add(cell);
                        }
                    }
                    if (Ctx.SelectedCells.Count > 0) Ctx.SelectedCell = Ctx.SelectedCells[0];
                }
                Ctx.NotifySelectionChanged();
                return true;
            }

            // Tiny drag in empty space — deselect
            Ctx.SelectedCells.Clear();
            Ctx.SelectedCell = null;
            Ctx.SelectedObjIndex = -1;
            Ctx.NotifySelectionChanged();
            return false;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (_boxSelectActive) {
                _boxSelectEnd = mouseState.Position;
                return true;
            }

            if (Ctx.Scene?.EnvCellManager == null) return false;
            var ray = Ctx.ComputeRay(mouseState);
            if (ray == null) return false;
            var (origin, dir) = ray.Value;

            var hit = Ctx.Raycast(origin, dir);
            if (hit.Hit) {
                var roomName = Ctx.RoomPalette?.GetRoomDisplayName(hit.Cell.EnvironmentId, (ushort)hit.Cell.GpuKey.CellStructure);
                var label = !string.IsNullOrEmpty(roomName) ? roomName : $"Env 0x{hit.Cell.EnvironmentId:X8}";
                Ctx.SetStatus($"0x{hit.Cell.CellId:X8}  |  {label}");
            }
            return false;
        }

        public override bool HandleKeyDown(KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                _boxSelectActive = false;
                Ctx.SelectedCells.Clear();
                Ctx.SelectedCell = null;
                Ctx.SelectedObjIndex = -1;
                Ctx.NotifySelectionChanged();
                return true;
            }
            if (e.Key == Key.Delete) {
                return false;
            }
            return false;
        }

        private static Vector2 ProjectToScreen(Vector3 worldPos, DungeonEditingContext ctx) {
            var camera = ctx.Scene?.Camera;
            if (camera == null) return new Vector2(-1, -1);

            var view = camera.GetViewMatrix();
            var proj = camera.GetProjectionMatrix();
            var vp = view * proj;
            var clip = Vector4.Transform(new Vector4(worldPos, 1f), vp);
            if (clip.W <= 0) return new Vector2(-1, -1);

            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float screenX = (ndcX + 1f) * 0.5f * camera.ScreenSize.X;
            float screenY = (ndcY + 1f) * 0.5f * camera.ScreenSize.Y;
            return new Vector2(screenX, screenY);
        }
    }

    public class MoveSubTool : DungeonSubToolBase {
        public override string Name => "Move";
        public override string IconGlyph => "\u2725"; // four-point star

        private bool _isDragging;
        private bool _isDraggingObject;
        private Vector3 _dragStartHit;
        private Vector3 _dragStartOrigin;

        public MoveSubTool(DungeonEditingContext ctx) : base(ctx) { }

        public override void OnActivated() { }
        public override void OnDeactivated() {
            _isDragging = false;
            _isDraggingObject = false;
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed || mouseState.RightPressed) return false;
            if (Ctx.Scene?.EnvCellManager == null || Ctx.Document == null) return false;

            var ray = Ctx.ComputeRay(mouseState);
            if (ray == null) return false;
            var (origin, dir) = ray.Value;

            // Try object hit for object dragging
            if (Ctx.HasSelectedObject) {
                var objHit = DungeonObjectRaycast.Raycast(origin, dir, Ctx.Document, Ctx.Scene);
                if (objHit.Hit && objHit.CellNumber == Ctx.SelectedObjCellNum && objHit.ObjectIndex == Ctx.SelectedObjIndex) {
                    var cell = Ctx.Document.GetCell(objHit.CellNumber);
                    if (cell != null && objHit.ObjectIndex < cell.StaticObjects.Count) {
                        _isDraggingObject = true;
                        _dragStartHit = objHit.HitPosition;
                        _dragStartOrigin = cell.StaticObjects[objHit.ObjectIndex].Origin;
                        return true;
                    }
                }
            }

            // Try cell hit for cell dragging
            var hit = Ctx.Raycast(origin, dir);
            if (hit.Hit && Ctx.SelectedCell != null) {
                bool isSelected = Ctx.SelectedCells.Any(c => c.CellId == hit.Cell.CellId);
                if (isSelected) {
                    var cellNum = (ushort)(Ctx.SelectedCell.CellId & 0xFFFF);
                    var dc = Ctx.Document.GetCell(cellNum);
                    if (dc != null) {
                        _isDragging = true;
                        _dragStartHit = hit.HitPosition;
                        _dragStartOrigin = dc.Origin;
                        return true;
                    }
                }
                // If not already selected, select first then start drag on next click
                Ctx.SelectedCells.Clear();
                Ctx.SelectedCells.Add(hit.Cell);
                Ctx.SelectedCell = hit.Cell;
                Ctx.NotifySelectionChanged();
            }
            return false;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            if (_isDraggingObject && Ctx.Document != null) {
                var cell = Ctx.Document.GetCell(Ctx.SelectedObjCellNum);
                if (cell != null && Ctx.SelectedObjIndex < cell.StaticObjects.Count) {
                    var delta = cell.StaticObjects[Ctx.SelectedObjIndex].Origin - _dragStartOrigin;
                    if (delta.LengthSquared() > 0.001f) {
                        cell.StaticObjects[Ctx.SelectedObjIndex].Origin = _dragStartOrigin;
                        Ctx.CommandHistory.Execute(
                            new MoveStaticObjectCommand(Ctx.SelectedObjCellNum, Ctx.SelectedObjIndex, delta),
                            Ctx.Document);
                        Ctx.RefreshRendering();
                        Ctx.NotifySelectionChanged();
                    }
                }
                _isDraggingObject = false;
                return true;
            }

            if (_isDragging && Ctx.SelectedCell != null && Ctx.Document != null) {
                var cellNum = (ushort)(Ctx.SelectedCell.CellId & 0xFFFF);
                var dc = Ctx.Document.GetCell(cellNum);
                if (dc != null) {
                    var delta = dc.Origin - _dragStartOrigin;
                    if (delta.LengthSquared() > 0.001f) {
                        dc.Origin = _dragStartOrigin;
                        Ctx.CommandHistory.Execute(new NudgeCellCommand(cellNum, delta), Ctx.Document);
                        Ctx.RefreshRendering();
                    }
                }
                _isDragging = false;
                return true;
            }

            _isDragging = false;
            _isDraggingObject = false;
            return false;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (!_isDragging && !_isDraggingObject) return false;
            if (Ctx.Scene?.EnvCellManager == null || Ctx.Document == null) return false;

            var ray = Ctx.ComputeRay(mouseState);
            if (ray == null) return false;
            var (origin, dir) = ray.Value;
            var hit = Ctx.Raycast(origin, dir);
            if (!hit.Hit) return false;

            if (_isDraggingObject) {
                var cell = Ctx.Document.GetCell(Ctx.SelectedObjCellNum);
                if (cell != null && Ctx.SelectedObjIndex < cell.StaticObjects.Count) {
                    var delta = hit.HitPosition - _dragStartHit;
                    cell.StaticObjects[Ctx.SelectedObjIndex].Origin = _dragStartOrigin + delta;
                    Ctx.RefreshRendering();
                }
                return true;
            }

            if (_isDragging && Ctx.SelectedCell != null) {
                var delta = hit.HitPosition - _dragStartHit;
                var newOrigin = _dragStartOrigin + delta;
                if (Ctx.GridSnapEnabled && Ctx.GridSnapSize > 0.1f) {
                    newOrigin = new Vector3(
                        MathF.Round(newOrigin.X / Ctx.GridSnapSize) * Ctx.GridSnapSize,
                        MathF.Round(newOrigin.Y / Ctx.GridSnapSize) * Ctx.GridSnapSize,
                        MathF.Round(newOrigin.Z / Ctx.GridSnapSize) * Ctx.GridSnapSize);
                }
                var cellNum = (ushort)(Ctx.SelectedCell.CellId & 0xFFFF);
                var dc = Ctx.Document.GetCell(cellNum);
                if (dc != null) {
                    dc.Origin = newOrigin;
                    Ctx.RefreshRendering();
                }
                return true;
            }
            return false;
        }
    }
}
