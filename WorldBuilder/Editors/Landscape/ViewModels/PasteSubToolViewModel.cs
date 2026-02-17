using System;
using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Services;
using WorldBuilder.Shared.Models;
using WorldBuilder.Utilities;

namespace WorldBuilder.Editors.Landscape.ViewModels {
    public partial class PasteSubToolViewModel : SubToolViewModelBase {
        public override string Name => "Paste";
        public override string IconGlyph => "📌";

        private readonly StampLibraryManager _stampLibrary;
        private readonly CommandHistory _commandHistory;

        [ObservableProperty]
        private ObservableCollection<TerrainStamp> _availableStamps;

        [ObservableProperty]
        private TerrainStamp? _selectedStamp;

        [ObservableProperty]
        private int _rotationDegrees = 0; // 0, 90, 180, 270

        [ObservableProperty]
        private bool _includeObjects = true;

        [ObservableProperty]
        private bool _blendEdges = false;

        private Vector2 _previewPosition;
        private TerrainStamp? _rotatedStamp;

        public PasteSubToolViewModel(
            TerrainEditingContext context,
            StampLibraryManager stampLibrary,
            CommandHistory commandHistory) : base(context) {
            _stampLibrary = stampLibrary;
            _commandHistory = commandHistory;
            _availableStamps = _stampLibrary.Stamps;
        }

        partial void OnSelectedStampChanged(TerrainStamp? value) {
            if (value != null) {
                UpdateRotatedStamp();
            }
        }

        partial void OnRotationDegreesChanged(int value) {
            UpdateRotatedStamp();
        }

        private void UpdateRotatedStamp() {
            if (SelectedStamp == null) {
                _rotatedStamp = null;
                return;
            }

            _rotatedStamp = RotationDegrees switch {
                90 => StampTransforms.Rotate90Clockwise(SelectedStamp),
                180 => StampTransforms.Rotate180(SelectedStamp),
                270 => StampTransforms.Rotate270Clockwise(SelectedStamp),
                _ => SelectedStamp
            };
        }

        public override void OnActivated() {
            Context.BrushActive = false;
        }

        public override void OnDeactivated() {
            // Clear preview
        }

        public override bool HandleMouseMove(MouseState mouseState) {
            if (!mouseState.TerrainHit.HasValue || _rotatedStamp == null)
                return false;

            var hit = mouseState.TerrainHit.Value;

            // Snap to cell grid (24 units)
            _previewPosition = new Vector2(
                MathF.Floor(hit.HitPosition.X / 24f) * 24f,
                MathF.Floor(hit.HitPosition.Y / 24f) * 24f);

            // TODO Sprint 4: Show ghostly preview overlay

            return true;
        }

        public override bool HandleMouseDown(MouseState mouseState) {
            if (!mouseState.LeftPressed || _rotatedStamp == null)
                return false;

            // Paste the stamp
            var command = new PasteStampCommand(
                Context, _rotatedStamp, _previewPosition,
                IncludeObjects, BlendEdges);
            _commandHistory.ExecuteCommand(command);

            Console.WriteLine($"[Paste] Stamped {_rotatedStamp.WidthInVertices}x{_rotatedStamp.HeightInVertices} at ({_previewPosition.X}, {_previewPosition.Y})");

            return true;
        }

        public override bool HandleMouseUp(MouseState mouseState) {
             return false;
        }

        [RelayCommand]
        private void RotateClockwise() {
            RotationDegrees = (RotationDegrees + 90) % 360;
        }

        [RelayCommand]
        private void RotateCounterClockwise() {
            RotationDegrees = (RotationDegrees + 270) % 360;
        }
    }
}
