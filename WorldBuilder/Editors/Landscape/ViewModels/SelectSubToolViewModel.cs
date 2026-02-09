using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class SelectSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Select";
        public override string IconGlyph => "🔍";

        [ObservableProperty]
        private string _selectedObjectInfo = "No selection";

        [ObservableProperty]
        private string _selectedObjectId = "";

        [ObservableProperty]
        private bool _hasEditableSelection;

        // Editable position fields
        [ObservableProperty] private float _positionX;
        [ObservableProperty] private float _positionY;
        [ObservableProperty] private float _positionZ;

        // Editable rotation (degrees around Z axis)
        [ObservableProperty] private float _rotationDeg;


        private bool _suppressPropertyUpdates;
        private readonly CommandHistory _commandHistory;

        public SelectSubToolViewModel(TerrainEditingContext context, CommandHistory commandHistory) : base(context) {
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
            context.ObjectSelection.SelectionChanged += OnSelectionChanged;
        }

        private void OnSelectionChanged(object? sender, EventArgs e) {
            UpdateSelectionInfo();
        }

        private void UpdateSelectionInfo() {
            _suppressPropertyUpdates = true;
            var sel = Context.ObjectSelection;

            if (sel.IsMultiSelection) {
                // Multi-selection: show count, hide individual editing
                var nonSceneryCount = sel.SelectedEntries.Count(e => !e.IsScenery);
                SelectedObjectId = "";
                SelectedObjectInfo = $"{sel.SelectionCount} objects selected ({nonSceneryCount} editable)";
                HasEditableSelection = false; // Disable per-object editing for multi-select
                PositionX = 0; PositionY = 0; PositionZ = 0;
                RotationDeg = 0;
            }
            else if (sel.HasSelection && sel.SelectedObject.HasValue) {
                // Single selection: show full details
                var obj = sel.SelectedObject.Value;
                SelectedObjectId = $"0x{obj.Id:X8} ({(obj.IsSetup ? "Setup" : "GfxObj")})";
                SelectedObjectInfo = sel.IsScenery ? "Scenery (read-only)" : $"Landblock {sel.SelectedLandblockKey:X4} [{sel.SelectedObjectIndex}]";
                HasEditableSelection = !sel.IsScenery && sel.SelectedObjectIndex >= 0;

                PositionX = obj.Origin.X;
                PositionY = obj.Origin.Y;
                PositionZ = obj.Origin.Z;

                // Extract Z rotation from quaternion (yaw in degrees)
                var q = obj.Orientation;
                float siny_cosp = 2.0f * (q.W * q.Z + q.X * q.Y);
                float cosy_cosp = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
                RotationDeg = MathF.Atan2(siny_cosp, cosy_cosp) * 180f / MathF.PI;
            }
            else {
                SelectedObjectId = "";
                SelectedObjectInfo = "No selection";
                HasEditableSelection = false;
                PositionX = 0; PositionY = 0; PositionZ = 0;
                RotationDeg = 0;
            }
            _suppressPropertyUpdates = false;
        }

        [RelayCommand]
        private void ApplyPosition() {
            if (_suppressPropertyUpdates || !HasEditableSelection) return;
            var sel = Context.ObjectSelection;
            if (!sel.HasSelection || sel.IsScenery || sel.SelectedObjectIndex < 0) return;

            var doc = GetDocument();
            if (doc == null) return;

            var obj = doc.GetStaticObject(sel.SelectedObjectIndex);
            var oldPos = obj.Origin;
            var newPos = new Vector3(PositionX, PositionY, PositionZ);
            if (Vector3.Distance(oldPos, newPos) < 0.001f) return;

            var cmd = new MoveObjectCommand(Context, sel.SelectedLandblockKey, sel.SelectedObjectIndex, oldPos, newPos);
            _commandHistory.ExecuteCommand(cmd);
            sel.RefreshFromDocument(doc);
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
        }

        [RelayCommand]
        private void ApplyRotation() {
            if (_suppressPropertyUpdates || !HasEditableSelection) return;
            var sel = Context.ObjectSelection;
            if (!sel.HasSelection || sel.IsScenery || sel.SelectedObjectIndex < 0) return;

            var doc = GetDocument();
            if (doc == null) return;

            var obj = doc.GetStaticObject(sel.SelectedObjectIndex);
            var oldRot = obj.Orientation;
            var newRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, RotationDeg * MathF.PI / 180f);
            if (Quaternion.Dot(oldRot, newRot) > 0.9999f) return;

            var cmd = new RotateObjectCommand(Context, sel.SelectedLandblockKey, sel.SelectedObjectIndex, oldRot, newRot);
            _commandHistory.ExecuteCommand(cmd);
            sel.RefreshFromDocument(doc);
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
        }

        private LandblockDocument? GetDocument() {
            var sel = Context.ObjectSelection;
            var docId = $"landblock_{sel.SelectedLandblockKey:X4}";
            return Context.TerrainSystem.DocumentManager
                .GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
        }

        public override void OnActivated() {
            UpdateSelectionInfo();
        }

        public override void OnDeactivated() {
            // Clear placement mode when switching sub-tools
            Context.ObjectSelection.IsPlacementMode = false;
            Context.ObjectSelection.PlacementPreview = null;
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed) return false;

            var sel = Context.ObjectSelection;

            // Placement mode: click terrain to place the object
            if (sel.IsPlacementMode && sel.PlacementPreview.HasValue) {
                if (mouseState.IsOverTerrain && mouseState.TerrainHit.HasValue) {
                    var terrainPos = mouseState.TerrainHit.Value.HitPosition;
                    var preview = sel.PlacementPreview.Value;

                    int lbX = (int)Math.Floor(terrainPos.X / 192f);
                    int lbY = (int)Math.Floor(terrainPos.Y / 192f);
                    ushort lbKey = (ushort)((lbX << 8) | lbY);

                    var newObj = new StaticObject {
                        Id = preview.Id,
                        IsSetup = preview.IsSetup,
                        Origin = terrainPos,
                        Orientation = preview.Orientation,
                        Scale = preview.Scale
                    };

                    var cmd = new AddObjectCommand(Context, lbKey, newObj);
                    _commandHistory.ExecuteCommand(cmd);
                    Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
                    Context.ObjectSelection.Select(newObj, lbKey, cmd.AddedIndex, false);

                    Console.WriteLine($"[Selector] Placed object 0x{newObj.Id:X8} at ({terrainPos.X:F1}, {terrainPos.Y:F1}, {terrainPos.Z:F1})");
                    return true;
                }
                return false;
            }

            // Normal selection (Ctrl+Click = toggle multi-select)
            if (mouseState.ObjectHit.HasValue && mouseState.ObjectHit.Value.Hit) {
                if (mouseState.CtrlPressed) {
                    Context.ObjectSelection.ToggleSelectFromHit(mouseState.ObjectHit.Value);
                }
                else {
                    Context.ObjectSelection.SelectFromHit(mouseState.ObjectHit.Value);
                }
                return true;
            }
            else {
                Context.ObjectSelection.Deselect();
                return false;
            }
        }

        public override bool HandleMouseUp(MouseState mouseState) => false;

        public override bool HandleMouseMove(MouseState mouseState) {
            var sel = Context.ObjectSelection;
            if (sel.IsPlacementMode && sel.PlacementPreview.HasValue && mouseState.IsOverTerrain && mouseState.TerrainHit.HasValue) {
                var terrainPos = mouseState.TerrainHit.Value.HitPosition;
                var preview = sel.PlacementPreview.Value;
                sel.PlacementPreview = new StaticObject {
                    Id = preview.Id,
                    IsSetup = preview.IsSetup,
                    Origin = terrainPos,
                    Orientation = preview.Orientation,
                    Scale = preview.Scale
                };
            }
            return false;
        }
    }
}
