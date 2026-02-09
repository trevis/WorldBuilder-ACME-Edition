using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Numerics;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class RotateObjectSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Rotate";
        public override string IconGlyph => "🔄";

        private bool _isDragging;
        private float _dragStartX;
        private Quaternion _originalOrientation;
        private readonly CommandHistory _commandHistory;

        public RotateObjectSubToolViewModel(TerrainEditingContext context, CommandHistory commandHistory) : base(context) {
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
        }

        public override void OnActivated() { }
        public override void OnDeactivated() {
            if (_isDragging) FinalizeDrag();
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed) return false;

            if (mouseState.ObjectHit.HasValue && mouseState.ObjectHit.Value.Hit) {
                var hit = mouseState.ObjectHit.Value;

                if (!Context.ObjectSelection.HasSelection ||
                    Context.ObjectSelection.SelectedObjectIndex != hit.ObjectIndex ||
                    Context.ObjectSelection.SelectedLandblockKey != hit.LandblockKey) {
                    Context.ObjectSelection.SelectFromHit(hit);
                    return true;
                }

                // Start rotation drag on selected non-scenery object
                if (!hit.IsScenery && Context.ObjectSelection.HasSelection) {
                    _isDragging = true;
                    _dragStartX = mouseState.Position.X;
                    _originalOrientation = Context.ObjectSelection.SelectedObject!.Value.Orientation;
                    return true;
                }
            }
            else {
                Context.ObjectSelection.Deselect();
            }

            return false;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
            if (_isDragging) {
                FinalizeDrag();
                return true;
            }
            return false;
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (!_isDragging || !Context.ObjectSelection.HasSelection) return false;

            var sel = Context.ObjectSelection;
            if (sel.IsScenery || sel.SelectedObjectIndex < 0) return false;

            // Compute rotation from horizontal mouse delta (degrees per pixel)
            float deltaX = mouseState.Position.X - _dragStartX;
            float angleDeg = deltaX * 0.5f; // 0.5 degrees per pixel
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angleDeg * MathF.PI / 180f);
            var newOrientation = Quaternion.Normalize(_originalOrientation * rotation);

            // Preview
            var docId = $"landblock_{sel.SelectedLandblockKey:X4}";
            var doc = Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
            if (doc != null && sel.SelectedObjectIndex < doc.StaticObjectCount) {
                var obj = doc.GetStaticObject(sel.SelectedObjectIndex);
                var updated = new StaticObject {
                    Id = obj.Id, IsSetup = obj.IsSetup, Origin = obj.Origin,
                    Orientation = newOrientation, Scale = obj.Scale
                };
                doc.UpdateStaticObject(sel.SelectedObjectIndex, updated);
                sel.RefreshFromDocument(doc);
                Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
            }

            return true;
        }

        private void FinalizeDrag() {
            _isDragging = false;

            var sel = Context.ObjectSelection;
            if (!sel.HasSelection || sel.IsScenery || sel.SelectedObjectIndex < 0) return;

            var docId = $"landblock_{sel.SelectedLandblockKey:X4}";
            var doc = Context.TerrainSystem.DocumentManager.GetOrCreateDocumentAsync<LandblockDocument>(docId).GetAwaiter().GetResult();
            if (doc == null) return;

            var currentObj = doc.GetStaticObject(sel.SelectedObjectIndex);
            if (currentObj.Orientation == _originalOrientation) return;

            var command = new RotateObjectCommand(
                Context, sel.SelectedLandblockKey, sel.SelectedObjectIndex,
                _originalOrientation, currentObj.Orientation);
            _commandHistory.ExecuteCommand(command);
        }
    }
}
