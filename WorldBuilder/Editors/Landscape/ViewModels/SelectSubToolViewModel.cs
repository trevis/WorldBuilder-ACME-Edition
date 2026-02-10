using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape.Commands;
using WorldBuilder.Lib;
using WorldBuilder.Lib.History;
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

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

        // Editable rotation (Euler angles in degrees)
        [ObservableProperty] private float _rotationX;
        [ObservableProperty] private float _rotationY;
        [ObservableProperty] private float _rotationZ;

        // Landcell display
        [ObservableProperty] private string _landcellText = "";


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
                HasEditableSelection = false;
                PositionX = 0; PositionY = 0; PositionZ = 0;
                RotationX = 0; RotationY = 0; RotationZ = 0;
                LandcellText = "";
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

                // Extract Euler angles (X, Y, Z) from quaternion
                QuaternionToEuler(obj.Orientation, out float ex, out float ey, out float ez);
                RotationX = ex;
                RotationY = ey;
                RotationZ = ez;

                // Compute landcell from world position
                int lbX = (int)MathF.Floor(obj.Origin.X / 192f);
                int lbY = (int)MathF.Floor(obj.Origin.Y / 192f);
                int cellX = (int)MathF.Floor((obj.Origin.X - lbX * 192f) / 24f);
                int cellY = (int)MathF.Floor((obj.Origin.Y - lbY * 192f) / 24f);
                cellX = Math.Clamp(cellX, 0, 7);
                cellY = Math.Clamp(cellY, 0, 7);
                uint landcell = (uint)((lbX << 24) | (lbY << 16) | (cellX << 3 | cellY));
                LandcellText = $"0x{landcell:X8}";
            }
            else {
                SelectedObjectId = "";
                SelectedObjectInfo = "No selection";
                HasEditableSelection = false;
                PositionX = 0; PositionY = 0; PositionZ = 0;
                RotationX = 0; RotationY = 0; RotationZ = 0;
                LandcellText = "";
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
            var newRot = EulerToQuaternion(RotationX, RotationY, RotationZ);
            if (Quaternion.Dot(oldRot, newRot) > 0.9999f) return;

            var cmd = new RotateObjectCommand(Context, sel.SelectedLandblockKey, sel.SelectedObjectIndex, oldRot, newRot);
            _commandHistory.ExecuteCommand(cmd);
            sel.RefreshFromDocument(doc);
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
        }

        [RelayCommand]
        private void SnapToTerrain() {
            if (_suppressPropertyUpdates || !HasEditableSelection) return;
            var sel = Context.ObjectSelection;
            if (!sel.HasSelection || sel.IsScenery || sel.SelectedObjectIndex < 0) return;

            var doc = GetDocument();
            if (doc == null) return;

            var obj = doc.GetStaticObject(sel.SelectedObjectIndex);
            float terrainZ = Context.GetHeightAtPosition(obj.Origin.X, obj.Origin.Y);
            if (MathF.Abs(obj.Origin.Z - terrainZ) < 0.001f) return;

            var oldPos = obj.Origin;
            var newPos = new Vector3(obj.Origin.X, obj.Origin.Y, terrainZ);

            var cmd = new MoveObjectCommand(Context, sel.SelectedLandblockKey, sel.SelectedObjectIndex, oldPos, newPos);
            _commandHistory.ExecuteCommand(cmd);
            sel.RefreshFromDocument(doc);
            Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
        }

        /// <summary>
        /// Converts a quaternion to Euler angles (degrees) in XYZ order.
        /// </summary>
        private static void QuaternionToEuler(Quaternion q, out float xDeg, out float yDeg, out float zDeg) {
            // Roll (X)
            float sinr_cosp = 2.0f * (q.W * q.X + q.Y * q.Z);
            float cosr_cosp = 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y);
            float roll = MathF.Atan2(sinr_cosp, cosr_cosp);

            // Pitch (Y)
            float sinp = 2.0f * (q.W * q.Y - q.Z * q.X);
            float pitch = MathF.Abs(sinp) >= 1.0f
                ? MathF.CopySign(MathF.PI / 2f, sinp)
                : MathF.Asin(sinp);

            // Yaw (Z)
            float siny_cosp = 2.0f * (q.W * q.Z + q.X * q.Y);
            float cosy_cosp = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
            float yaw = MathF.Atan2(siny_cosp, cosy_cosp);

            xDeg = roll * 180f / MathF.PI;
            yDeg = pitch * 180f / MathF.PI;
            zDeg = yaw * 180f / MathF.PI;
        }

        /// <summary>
        /// Converts Euler angles (degrees) in XYZ order to a quaternion.
        /// </summary>
        private static Quaternion EulerToQuaternion(float xDeg, float yDeg, float zDeg) {
            float xRad = xDeg * MathF.PI / 180f;
            float yRad = yDeg * MathF.PI / 180f;
            float zRad = zDeg * MathF.PI / 180f;

            var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, xRad);
            var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yRad);
            var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, zRad);

            return Quaternion.Normalize(qz * qy * qx);
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

                    // Flatten terrain first, then snap building Z to the exact flattened height
                    float? snappedZ = FlattenTerrainUnderBuilding(newObj);
                    if (snappedZ.HasValue && Math.Abs(snappedZ.Value - newObj.Origin.Z) > 0.01f) {
                        newObj.Origin = new Vector3(newObj.Origin.X, newObj.Origin.Y, snappedZ.Value);
                    }

                    var cmd = new AddObjectCommand(Context, lbKey, newObj);
                    _commandHistory.ExecuteCommand(cmd);
                    Context.TerrainSystem.Scene.InvalidateStaticObjectsCache();
                    Context.ObjectSelection.Select(newObj, lbKey, cmd.AddedIndex, false);

                    Console.WriteLine($"[Selector] Placed object 0x{newObj.Id:X8} at ({newObj.Origin.X:F1}, {newObj.Origin.Y:F1}, {newObj.Origin.Z:F1})");

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

        /// <summary>
        /// Flattens terrain vertices under a building's footprint to the building's Z height.
        /// Only applies to known building models. Uses the model's bounding box to determine
        /// the rectangular footprint.
        /// </summary>
        /// <summary>
        /// Returns the snapped Z height (from the height table) or null if flattening didn't apply.
        /// </summary>
        private float? FlattenTerrainUnderBuilding(StaticObject obj) {
            try {
                // Start with model bounds from the object manager
                var bounds = Context.TerrainSystem.Scene._objectManager.GetBounds(obj.Id, obj.IsSetup);
                if (!bounds.HasValue) return null;

                var (localMin, localMax) = bounds.Value;

                // Compute world-space AABB from exterior model
                float worldMinX = obj.Origin.X + localMin.X;
                float worldMaxX = obj.Origin.X + localMax.X;
                float worldMinY = obj.Origin.Y + localMin.Y;
                float worldMaxY = obj.Origin.Y + localMax.Y;

                // Expand footprint using blueprint EnvCell positions (covers full interior extent)
                var dats = Context.TerrainSystem.Dats;
                if (dats != null) {
                    var blueprint = BuildingBlueprintCache.GetBlueprint(obj.Id, dats);
                    if (blueprint != null && blueprint.Cells.Count > 0) {
                        foreach (var cell in blueprint.Cells) {
                            // Transform cell's local offset by building orientation to get world offset
                            var worldOffset = Vector3.Transform(cell.RelativeOrigin, obj.Orientation);
                            float cellX = obj.Origin.X + worldOffset.X;
                            float cellY = obj.Origin.Y + worldOffset.Y;
                            // Expand bounds to include this cell (with margin for cell geometry)
                            // AC cells can be up to ~24m across, use full cell width as margin
                            const float cellMargin = 24f;
                            worldMinX = Math.Min(worldMinX, cellX - cellMargin);
                            worldMaxX = Math.Max(worldMaxX, cellX + cellMargin);
                            worldMinY = Math.Min(worldMinY, cellY - cellMargin);
                            worldMaxY = Math.Max(worldMaxY, cellY + cellMargin);
                        }
                    }
                }

                // Ensure minimum footprint of at least 24m around building center
                const float minRadius = 24f;
                worldMinX = Math.Min(worldMinX, obj.Origin.X - minRadius);
                worldMaxX = Math.Max(worldMaxX, obj.Origin.X + minRadius);
                worldMinY = Math.Min(worldMinY, obj.Origin.Y - minRadius);
                worldMaxY = Math.Max(worldMaxY, obj.Origin.Y + minRadius);

                // Find the target height byte by reverse-lookup in the height table
                var heightTable = Context.TerrainSystem.Region.LandDefs.LandHeightTable;
                byte targetHeight = FindClosestHeightByte(heightTable, obj.Origin.Z);

                // Get all terrain vertices in the rectangular footprint
                var vertices = PaintCommand.GetVerticesInRect(worldMinX, worldMinY, worldMaxX, worldMaxY, Context);
                if (vertices.Count == 0) return null;

                // Build change set (same pattern as HeightSetSubToolViewModel)
                var changes = new Dictionary<ushort, List<(int VertexIndex, byte OriginalValue, byte NewValue)>>();
                var landblockDataCache = new Dictionary<ushort, TerrainEntry[]>();

                foreach (var (lbId, vIndex, _) in vertices) {
                    if (!landblockDataCache.TryGetValue(lbId, out var data)) {
                        data = Context.TerrainSystem.GetLandblockTerrain(lbId);
                        if (data == null) continue;
                        landblockDataCache[lbId] = data;
                    }

                    if (!changes.TryGetValue(lbId, out var list)) {
                        list = new List<(int, byte, byte)>();
                        changes[lbId] = list;
                    }

                    if (list.Any(c => c.VertexIndex == vIndex)) continue;

                    byte original = data[vIndex].Height;
                    if (original == targetHeight) continue;
                    list.Add((vIndex, original, targetHeight));
                }

                if (changes.Count == 0) return heightTable[targetHeight];

                // Apply terrain changes directly (without HeightChangeCommand's static object Z adjustment,
                // which would cause nearby existing buildings to float/sink)
                var batchChanges = new Dictionary<ushort, Dictionary<byte, uint>>();
                foreach (var (lbId, changeList) in changes) {
                    if (!landblockDataCache.TryGetValue(lbId, out var terrainData)) continue;
                    if (!batchChanges.TryGetValue(lbId, out var lbChanges)) {
                        lbChanges = new Dictionary<byte, uint>();
                        batchChanges[lbId] = lbChanges;
                    }
                    foreach (var (vIndex, _, newVal) in changeList) {
                        var newEntry = terrainData[vIndex] with { Height = newVal };
                        lbChanges[(byte)vIndex] = newEntry.ToUInt();
                    }
                }
                var modifiedLandblocks = Context.TerrainSystem.UpdateLandblocksBatch(TerrainField.Height, batchChanges);
                Context.MarkLandblocksModified(modifiedLandblocks);

                Console.WriteLine($"[Selector] Flattened {vertices.Count} terrain vertices under building 0x{obj.Id:X8} (height={heightTable[targetHeight]:F1})");
                return heightTable[targetHeight];
            }
            catch (Exception ex) {
                Console.WriteLine($"[Selector] Error flattening terrain: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the height byte (0-255) whose height table value is closest to the target Z.
        /// </summary>
        private static byte FindClosestHeightByte(float[] heightTable, float targetZ) {
            byte best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < heightTable.Length && i < 256; i++) {
                float dist = Math.Abs(heightTable[i] - targetZ);
                if (dist < bestDist) {
                    bestDist = dist;
                    best = (byte)i;
                }
            }
            return best;
        }
    }
}
