using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Chorizite.OpenGLSDLBackend;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatReaderWriter.Types;
using DialogHostAvalonia;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using DatReaderWriter.DBObjs;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Shared.Lib;
using WorldBuilder.Shared.Models;
using WorldBuilder.ViewModels;
using WorldBuilder.Editors.Landscape;

namespace WorldBuilder.Editors.Dungeon {
    public partial class DungeonEditorViewModel : ViewModelBase {
        [ObservableProperty] private string _statusText = "No dungeon loaded";
        [ObservableProperty] private string _landblockInputText = "";
        [ObservableProperty] private string _currentPositionText = "";
        [ObservableProperty] private string _selectedCellInfo = "";
        [ObservableProperty] private int _cellCount;
        [ObservableProperty] private bool _hasDungeon;
        [ObservableProperty] private bool _hasSelectedCell;
        [ObservableProperty] private bool _isPlacementMode;
        [ObservableProperty] private string _placementStatusText = "";
        [ObservableProperty] private bool _isObjectPlacementMode;
        [ObservableProperty] private string _objectIdInput = "";

        private LoadedEnvCell? _selectedCell;
        private bool _needsCameraFocus;

        public WorldBuilderSettings Settings { get; }

        private Project? _project;
        private IDatReaderWriter? _dats;
        private DungeonScene? _scene;
        private DungeonDocument? _document;
        private ushort _loadedLandblockKey;

        public DungeonScene? Scene => _scene;
        public DungeonDocument? Document => _document;
        public RoomPaletteViewModel? RoomPalette { get; private set; }
        public DungeonCommandHistory CommandHistory { get; } = new();

        public DungeonEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _scene = new DungeonScene(_dats, Settings);

            RoomPalette = new RoomPaletteViewModel(_dats);
            RoomPalette.RoomSelected += OnRoomSelected;
            OnPropertyChanged(nameof(RoomPalette));

            _ = RoomPalette.LoadRoomsAsync();
        }

        /// <summary>
        /// Called by the viewport control on GL init to pass the renderer.
        /// </summary>
        internal void OnRendererReady(OpenGLRenderer renderer) {
            _scene?.InitGpu(renderer);
        }

        /// <summary>
        /// Render one frame. Called by the viewport on the GL thread.
        /// </summary>
        internal void RenderFrame(double deltaTime, Avalonia.PixelSize canvasSize, AvaloniaInputState inputState) {
            if (_scene == null) return;

            HandleInput(inputState, deltaTime);

            _scene.Camera.ScreenSize = new Vector2(canvasSize.Width, canvasSize.Height);
            _scene.Render((float)canvasSize.Width / canvasSize.Height);

            // Deferred camera focus: wait until cells are actually uploaded to GPU
            if (_needsCameraFocus && _scene.EnvCellManager != null && _scene.EnvCellManager.LoadedCellCount > 0) {
                if (_targetCellId >= 0x0100) {
                    _scene.FocusCameraOnCell(_loadedLandblockKey, _targetCellId);
                }
                else {
                    _scene.FocusCamera();
                }
                _needsCameraFocus = false;
                _targetCellId = 0;
            }

            var cam = _scene.Camera.Position;
            CurrentPositionText = $"({cam.X:F0}, {cam.Y:F0}, {cam.Z:F0})";
        }

        private void HandleInput(AvaloniaInputState inputState, double deltaTime) {
            if (_scene == null) return;
            var camera = _scene.Camera;

            camera.ProcessMouseMovement(inputState.MouseState);

            bool shiftHeld = inputState.IsKeyDown(Key.LeftShift) || inputState.IsKeyDown(Key.RightShift);
            bool ctrlHeld = inputState.IsKeyDown(Key.LeftCtrl) || inputState.IsKeyDown(Key.RightCtrl);

            if ((shiftHeld || ctrlHeld)) {
                float rotateSpeed = 60f * (float)deltaTime;
                if (inputState.IsKeyDown(Key.Left)) camera.ProcessKeyboardRotation(rotateSpeed, 0);
                if (inputState.IsKeyDown(Key.Right)) camera.ProcessKeyboardRotation(-rotateSpeed, 0);
                if (inputState.IsKeyDown(Key.Up)) camera.ProcessKeyboardRotation(0, rotateSpeed);
                if (inputState.IsKeyDown(Key.Down)) camera.ProcessKeyboardRotation(0, -rotateSpeed);
            }

            if (inputState.IsKeyDown(Key.W) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Key.Up)))
                camera.ProcessKeyboard(CameraMovement.Forward, deltaTime);
            if (inputState.IsKeyDown(Key.S) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Key.Down)))
                camera.ProcessKeyboard(CameraMovement.Backward, deltaTime);
            if (inputState.IsKeyDown(Key.A) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Key.Left)))
                camera.ProcessKeyboard(CameraMovement.Left, deltaTime);
            if (inputState.IsKeyDown(Key.D) || ((!shiftHeld && !ctrlHeld) && inputState.IsKeyDown(Key.Right)))
                camera.ProcessKeyboard(CameraMovement.Right, deltaTime);

            if (inputState.IsKeyDown(Key.Space))
                camera.ProcessKeyboard(CameraMovement.Up, deltaTime);
            if (shiftHeld)
                camera.ProcessKeyboard(CameraMovement.Down, deltaTime);
        }

        internal void HandleKeyDown(KeyEventArgs e) {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (ctrl && e.Key == Key.G) {
                _ = OpenLandblockCommand.ExecuteAsync(null);
                return;
            }
            if (ctrl && e.Key == Key.Z) {
                if (shift) UndoRedoRedo();
                else UndoRedoUndo();
                return;
            }
            if (ctrl && e.Key == Key.Y) {
                UndoRedoRedo();
                return;
            }
            if (e.Key == Key.Delete) {
                DeleteSelectedCellCommand.Execute(null);
                return;
            }
            if (e.Key == Key.Escape) {
                if (IsPlacementMode) CancelPlacement();
                else DeselectCell();
            }
        }

        private void UndoRedoUndo() {
            if (_document == null || !CommandHistory.CanUndo) return;
            CommandHistory.Undo(_document);
            DeselectCell();
            RefreshRendering();
            CellCount = _document.Cells.Count;
            StatusText = $"LB {_document.LandblockKey:X4}: {CellCount} cells (Undo: {CommandHistory.LastCommandDescription ?? "none"})";
        }

        private void UndoRedoRedo() {
            if (_document == null || !CommandHistory.CanRedo) return;
            CommandHistory.Redo(_document);
            DeselectCell();
            RefreshRendering();
            CellCount = _document.Cells.Count;
            StatusText = $"LB {_document.LandblockKey:X4}: {CellCount} cells";
        }

        internal void HandlePointerWheel(PointerWheelEventArgs e) {
            _scene?.Camera.ProcessMouseScroll((float)e.Delta.Y);
        }

        internal void HandlePointerPressed(AvaloniaInputState inputState) {
            if (_scene?.EnvCellManager == null) return;

            var mouse = inputState.MouseState;
            if (!mouse.LeftPressed || mouse.RightPressed) return;

            // If in placement mode and dungeon is empty, place first cell at camera target
            if (IsPlacementMode && _pendingRoom != null && _document != null && _document.Cells.Count == 0) {
                PlaceFirstCell();
                return;
            }

            var camera = _scene.Camera;
            float width = camera.ScreenSize.X;
            float height = camera.ScreenSize.Y;
            if (width <= 0 || height <= 0) return;

            float ndcX = 2.0f * mouse.Position.X / width - 1.0f;
            float ndcY = 2.0f * mouse.Position.Y / height - 1.0f;

            Matrix4x4 projection = camera.GetProjectionMatrix();
            Matrix4x4 view = camera.GetViewMatrix();
            if (!Matrix4x4.Invert(view * projection, out Matrix4x4 vpInverse)) return;

            Vector4 nearW = Vector4.Transform(new Vector4(ndcX, ndcY, -1f, 1f), vpInverse);
            Vector4 farW = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), vpInverse);
            nearW /= nearW.W;
            farW /= farW.W;

            var rayOrigin = new Vector3(nearW.X, nearW.Y, nearW.Z);
            var rayDir = Vector3.Normalize(new Vector3(farW.X, farW.Y, farW.Z) - rayOrigin);

            // Object placement mode
            if (IsObjectPlacementMode && _pendingObjectId != null && _document != null) {
                TryPlaceObject(rayOrigin, rayDir);
                return;
            }

            // Room placement mode - snap to portal
            if (IsPlacementMode && _pendingRoom != null && _document != null) {
                TrySnapToPortal(rayOrigin, rayDir);
                return;
            }

            // Normal mode: select cell
            if (!HasDungeon) return;
            var hit = _scene.EnvCellManager.Raycast(rayOrigin, rayDir);
            if (hit.Hit) {
                SelectCell(hit.Cell);
            }
            else {
                DeselectCell();
            }
        }

        private void SelectCell(LoadedEnvCell cell) {
            _selectedCell = cell;
            HasSelectedCell = true;
            if (_scene != null) _scene.SelectedCell = cell;

            int portalCount = cell.Portals?.Count ?? 0;
            int connectedPortals = 0;
            if (cell.Portals != null) {
                foreach (var p in cell.Portals) {
                    if (p.OtherCellId != 0 && p.OtherCellId != 0xFFFF) connectedPortals++;
                }
            }

            int staticCount = 0;
            var dc = _document?.GetCell((ushort)(cell.CellId & 0xFFFF));
            if (dc != null) staticCount = dc.StaticObjects.Count;

            SelectedCellInfo = $"Cell {cell.CellId:X8}  |  Env: {cell.EnvironmentId:X8}\n" +
                $"Portals: {connectedPortals}/{portalCount} connected  |  Surfaces: {cell.SurfaceCount}\n" +
                $"Statics: {staticCount}  |  Pos: ({cell.WorldPosition.X:F1}, {cell.WorldPosition.Y:F1}, {cell.WorldPosition.Z:F1})";

            if (dc != null) {
                SelectedCellSurfaces = string.Join(", ", dc.Surfaces.Select(s => $"{s:X4}"));
            }
        }

        private void DeselectCell() {
            _selectedCell = null;
            HasSelectedCell = false;
            SelectedCellInfo = "";
            if (_scene != null) _scene.SelectedCell = null;
        }

        #region Cell Editing

        [RelayCommand]
        private void DeleteSelectedCell() {
            if (_selectedCell == null || _document == null) return;

            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            CommandHistory.Execute(new RemoveCellCommand(cellNum), _document);
            DeselectCell();
            RefreshRendering();
            CellCount = _document.Cells.Count;
            StatusText = $"LB {_document.LandblockKey:X4}: {CellCount} cells";
        }

        [RelayCommand] private void NudgeCellXPos() => NudgeSelectedCell(new Vector3(1, 0, 0));
        [RelayCommand] private void NudgeCellXNeg() => NudgeSelectedCell(new Vector3(-1, 0, 0));
        [RelayCommand] private void NudgeCellYPos() => NudgeSelectedCell(new Vector3(0, 1, 0));
        [RelayCommand] private void NudgeCellYNeg() => NudgeSelectedCell(new Vector3(0, -1, 0));
        [RelayCommand] private void NudgeCellZPos() => NudgeSelectedCell(new Vector3(0, 0, 1));
        [RelayCommand] private void NudgeCellZNeg() => NudgeSelectedCell(new Vector3(0, 0, -1));

        [RelayCommand] private void RotateCellCW() => RotateSelectedCell(-90);
        [RelayCommand] private void RotateCellCW45() => RotateSelectedCell(-45);
        [RelayCommand] private void RotateCellCCW45() => RotateSelectedCell(45);
        [RelayCommand] private void RotateCellCCW() => RotateSelectedCell(90);

        private void RotateSelectedCell(float degrees) {
            if (_selectedCell == null || _document == null) return;
            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            CommandHistory.Execute(new RotateCellCommand(cellNum, degrees), _document);
            RefreshRendering();
            _needsCameraFocus = false;
        }

        private void NudgeSelectedCell(Vector3 offset) {
            if (_selectedCell == null || _document == null) return;
            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            CommandHistory.Execute(new NudgeCellCommand(cellNum, offset), _document);
            RefreshRendering();
            _needsCameraFocus = false;
        }

        [ObservableProperty] private string _selectedCellSurfaces = "";

        [RelayCommand]
        private void ApplySurfaces() {
            if (_selectedCell == null || _document == null) return;
            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            var dc = _document.GetCell(cellNum);
            if (dc == null) return;

            var newSurfaces = new List<ushort>();
            foreach (var part in SelectedCellSurfaces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                var hex = part.TrimStart('0', 'x', 'X');
                if (string.IsNullOrEmpty(hex)) hex = "0";
                if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var surfId))
                    newSurfaces.Add(surfId);
            }

            if (newSurfaces.Count > 0) {
                dc.Surfaces.Clear();
                dc.Surfaces.AddRange(newSurfaces);
                RefreshRendering();
                StatusText = $"Updated {newSurfaces.Count} surfaces on cell {cellNum:X4}";
            }
        }

        [RelayCommand]
        private void DisconnectPortal() {
            if (_selectedCell == null || _document == null) return;

            var cellNum = (ushort)(_selectedCell.CellId & 0xFFFF);
            var dc = _document.GetCell(cellNum);
            if (dc == null || dc.CellPortals.Count == 0) return;

            var lastPortal = dc.CellPortals[^1];
            var otherCellNum = lastPortal.OtherCellId;

            dc.CellPortals.RemoveAt(dc.CellPortals.Count - 1);

            var otherCell = _document.GetCell(otherCellNum);
            if (otherCell != null) {
                otherCell.CellPortals.RemoveAll(cp => cp.OtherCellId == cellNum);
            }

            RefreshRendering();
            SelectCell(_selectedCell);
        }

        #endregion

        #region Object Placement

        [RelayCommand]
        private void StartObjectPlacement() {
            if (_selectedCell == null || string.IsNullOrWhiteSpace(ObjectIdInput)) return;

            var hex = ObjectIdInput.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var objId)) {
                StatusText = "Invalid object ID";
                return;
            }

            _pendingObjectId = objId;
            IsObjectPlacementMode = true;
            PlacementStatusText = $"Click in viewport to place object 0x{objId:X8}";
        }

        [RelayCommand]
        private void CancelObjectPlacement() {
            _pendingObjectId = null;
            IsObjectPlacementMode = false;
            if (!IsPlacementMode) PlacementStatusText = "";
        }

        private uint? _pendingObjectId;

        private void TryPlaceObject(Vector3 rayOrigin, Vector3 rayDir) {
            if (_pendingObjectId == null || _document == null || _scene == null) return;

            var hit = _scene.EnvCellManager?.Raycast(rayOrigin, rayDir);
            if (hit == null || !hit.Value.Hit) return;

            var targetCell = hit.Value.Cell;
            var cellNum = (ushort)(targetCell.CellId & 0xFFFF);
            var dc = _document.GetCell(cellNum);
            if (dc == null) return;

            // Convert world-space hit position to landblock-local coordinates
            uint lbId = _document.LandblockKey;
            var blockX = (lbId >> 8) & 0xFF;
            var blockY = lbId & 0xFF;
            var lbOffset = new Vector3(blockX * 192f, blockY * 192f, 0f);

            var localOrigin = hit.Value.HitPosition - lbOffset;
            localOrigin.Z += 50f; // Reverse the dungeon depth offset

            dc.StaticObjects.Add(new DatReaderWriter.Types.Stab {
                Id = _pendingObjectId.Value,
                Frame = new DatReaderWriter.Types.Frame {
                    Origin = localOrigin,
                    Orientation = Quaternion.Identity
                }
            });

            RefreshRendering();
            StatusText = $"Placed 0x{_pendingObjectId.Value:X8} in cell {cellNum:X4}";
            CancelObjectPlacement();
        }

        #endregion

        #region Placement

        private RoomEntry? _pendingRoom;

        private void OnRoomSelected(object? sender, RoomEntry room) {
            if (_document == null) {
                // Need a landblock first -- create a new empty dungeon
                EnsureDocument();
            }

            _pendingRoom = room;
            IsPlacementMode = true;

            if (_document!.Cells.Count == 0) {
                PlacementStatusText = $"Click viewport to place '{room.DisplayName}' as first cell";
            }
            else {
                PlacementStatusText = $"Click an open portal to attach '{room.DisplayName}'";
            }
        }

        [RelayCommand]
        private void CancelPlacement() {
            _pendingRoom = null;
            IsPlacementMode = false;
            PlacementStatusText = "";
        }

        [RelayCommand]
        private async Task NewDungeon() {
            if (_dats == null) return;

            var cellId = await ShowNewDungeonDialog();
            if (cellId == null) return;

            var lbKey = (ushort)(cellId.Value >> 16);
            if (lbKey == 0) lbKey = (ushort)(cellId.Value & 0xFFFF);

            _loadedLandblockKey = lbKey;
            _document = new DungeonDocument(lbKey);
            HasDungeon = true;
            CellCount = 0;
            StatusText = $"LB {lbKey:X4}: New dungeon (empty)";

            _scene?.Camera.SetPosition(Vector3.Zero + new Vector3(0, -20f, 10f));
            _scene?.Camera.LookAt(Vector3.Zero);
        }

        private void EnsureDocument() {
            if (_document != null) return;
            _loadedLandblockKey = 0xFFFF;
            _document = new DungeonDocument(_loadedLandblockKey);
            HasDungeon = true;
        }

        private void PlaceFirstCell() {
            if (_pendingRoom == null || _document == null || _scene == null || _dats == null) return;

            var surfaces = GetSurfacesForRoom(_pendingRoom);
            var cmd = new AddCellCommand(
                _pendingRoom.EnvironmentId, _pendingRoom.CellStructureIndex,
                Vector3.Zero, Quaternion.Identity, surfaces);
            CommandHistory.Execute(cmd, _document);

            RefreshRendering();
            _needsCameraFocus = true;

            StatusText = $"LB {_document.LandblockKey:X4}: {_document.Cells.Count} cells";
            CellCount = _document.Cells.Count;

            CancelPlacement();
        }

        private void TrySnapToPortal(Vector3 rayOrigin, Vector3 rayDir) {
            if (_pendingRoom == null || _document == null || _scene == null || _dats == null) return;

            // Raycast to find which cell was clicked
            var hit = _scene.EnvCellManager?.Raycast(rayOrigin, rayDir);
            if (hit == null || !hit.Value.Hit) return;

            var targetCell = hit.Value.Cell;
            var targetCellNum = (ushort)(targetCell.CellId & 0xFFFF);
            var targetDocCell = _document.GetCell(targetCellNum);
            if (targetDocCell == null) return;

            // Load the target cell's CellStruct to get portal polygons
            uint targetEnvFileId = targetCell.EnvironmentId;
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(targetEnvFileId, out var targetEnv)) return;

            var targetDocCellStruct = (ushort)targetDocCell.CellStructure;
            if (!targetEnv.Cells.TryGetValue(targetDocCellStruct, out var targetCellStruct)) return;

            // Find an open portal on the target cell (not yet connected)
            var connectedPolygons = new HashSet<ushort>();
            foreach (var cp in targetDocCell.CellPortals) {
                connectedPolygons.Add((ushort)cp.PolygonId);
            }

            var targetPortalIds = PortalSnapper.GetPortalPolygonIds(targetCellStruct);
            ushort? openPortalId = null;
            foreach (var pid in targetPortalIds) {
                if (!connectedPolygons.Contains(pid)) {
                    openPortalId = pid;
                    break;
                }
            }

            if (openPortalId == null) {
                PlacementStatusText = "No open portals on this cell";
                return;
            }

            // Get target portal geometry in local space, then transform to world
            var targetPortalLocal = PortalSnapper.GetPortalGeometry(targetCellStruct, openPortalId.Value);
            if (targetPortalLocal == null) return;

            var (targetCentroidWorld, targetNormalWorld) = PortalSnapper.TransformPortalToWorld(
                targetPortalLocal.Value, targetDocCell.Origin, targetDocCell.Orientation);

            // Load the source room's CellStruct
            uint sourceEnvFileId = (uint)(_pendingRoom.EnvironmentId | 0x0D000000);
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(sourceEnvFileId, out var sourceEnv)) return;
            if (!sourceEnv.Cells.TryGetValue(_pendingRoom.CellStructureIndex, out var sourceCellStruct)) return;

            // Pick the first portal on the source room to connect with
            var sourcePortalIds = PortalSnapper.GetPortalPolygonIds(sourceCellStruct);
            if (sourcePortalIds.Count == 0) {
                PlacementStatusText = "Selected room has no portals";
                return;
            }
            var sourcePortalId = sourcePortalIds[0];

            var sourcePortalLocal = PortalSnapper.GetPortalGeometry(sourceCellStruct, sourcePortalId);
            if (sourcePortalLocal == null) return;

            // Compute snap transform
            var (newOrigin, newOrientation) = PortalSnapper.ComputeSnapTransform(
                targetCentroidWorld, targetNormalWorld, sourcePortalLocal.Value);

            // Add the new cell with portal connection via command history
            var surfaces = GetSurfacesForRoom(_pendingRoom);
            var cmd = new AddCellCommand(
                _pendingRoom.EnvironmentId, _pendingRoom.CellStructureIndex,
                newOrigin, newOrientation, surfaces,
                connectToCellNum: targetCellNum, connectToPolyId: openPortalId.Value, sourcePolyId: sourcePortalId);
            CommandHistory.Execute(cmd, _document);

            RefreshRendering();

            StatusText = $"LB {_document.LandblockKey:X4}: {_document.Cells.Count} cells";
            CellCount = _document.Cells.Count;

            CancelPlacement();
        }

        private List<ushort> GetSurfacesForRoom(RoomEntry room) {
            // Try to find surfaces from an existing EnvCell using this environment
            if (room.DefaultSurfaces.Count > 0)
                return new List<ushort>(room.DefaultSurfaces);

            return new List<ushort>();
        }

        private void RefreshRendering() {
            if (_scene == null || _document == null) return;
            _scene.RefreshFromDocument(_document);
        }

        #endregion

        private async Task<uint?> ShowNewDungeonDialog() {
            uint? result = null;
            var textBox = new TextBox {
                Text = "",
                Width = 300,
                Watermark = "Landblock hex ID for new dungeon (e.g. FFFF)"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "New Dungeon",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Enter a landblock ID for the new dungeon.",
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    textBox,
                    errorText,
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("DungeonDialogHost"))
                            },
                            new Button {
                                Content = "Create",
                                Command = new RelayCommand(() => {
                                    var parsed = ParseLandblockInput(textBox.Text);
                                    if (parsed != null) {
                                        result = parsed;
                                        DialogHost.Close("DungeonDialogHost");
                                    }
                                    else {
                                        errorText.Text = "Invalid hex ID.";
                                        errorText.IsVisible = true;
                                    }
                                })
                            }
                        }
                    }
                }
            }, "DungeonDialogHost");

            return result;
        }

        [RelayCommand]
        public async Task OpenLandblock() {
            if (_dats == null) return;

            var cellId = await ShowOpenDungeonDialog();
            if (cellId == null) return;

            var fullId = cellId.Value;
            var lbId = (ushort)(fullId >> 16);
            var cellPart = (ushort)(fullId & 0xFFFF);

            // If only landblock was given (4-char hex), cellPart might be the landblock
            if (lbId == 0 && cellPart != 0) {
                lbId = cellPart;
                cellPart = 0;
            }

            LoadDungeon(lbId, cellPart >= 0x0100 ? cellPart : (ushort)0);
        }

        private ushort _targetCellId;

        public void LoadDungeon(ushort landblockKey, ushort targetCellId = 0) {
            if (_scene == null || _dats == null) return;

            uint lbiId = ((uint)landblockKey << 16) | 0xFFFE;
            if (!_dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) {
                StatusText = $"LB {landblockKey:X4}: No dungeon cells found";
                HasDungeon = false;
                CellCount = 0;
                return;
            }

            // Load into document for editing
            _document = new DungeonDocument(landblockKey);
            _document.LoadFromDat(_dats);

            if (_scene.LoadLandblock(landblockKey)) {
                _loadedLandblockKey = landblockKey;
                _targetCellId = targetCellId;
                CellCount = _document.Cells.Count;
                StatusText = $"LB {landblockKey:X4}: {CellCount} cells";
                HasDungeon = true;

                _needsCameraFocus = true;
            }
            else {
                StatusText = $"LB {landblockKey:X4}: Failed to load";
                HasDungeon = false;
                CellCount = 0;
            }
        }

        private async Task<uint?> ShowOpenDungeonDialog() {
            uint? result = null;
            var textBox = new TextBox {
                Text = LandblockInputText,
                Width = 400,
                Watermark = "Search dungeon by name or enter hex ID (e.g. 01D9)"
            };
            var errorText = new TextBlock {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 12,
                IsVisible = false
            };

            var locationList = new ListBox {
                MaxHeight = 300,
                Width = 400,
                IsVisible = false,
                FontSize = 12,
            };

            void UpdateLocationResults(string? query) {
                if (string.IsNullOrWhiteSpace(query) || query.Length < 2) {
                    locationList.IsVisible = false;
                    locationList.ItemsSource = null;
                    return;
                }

                var results = LocationDatabase.Search(query, typeFilter: "Dungeon").Take(50).ToList();
                if (results.Count > 0) {
                    locationList.ItemsSource = results.Select(r => $"{r.Name}  [{r.LandblockHex}]").ToList();
                    locationList.IsVisible = true;
                }
                else {
                    locationList.IsVisible = false;
                    locationList.ItemsSource = null;
                }
            }

            textBox.TextChanged += (s, e) => {
                errorText.IsVisible = false;
                UpdateLocationResults(textBox.Text);
            };

            locationList.SelectionChanged += (s, e) => {
                if (locationList.SelectedIndex < 0) return;
                var query = textBox.Text;
                if (string.IsNullOrWhiteSpace(query)) return;
                var results = LocationDatabase.Search(query, typeFilter: "Dungeon").Take(50).ToList();
                if (locationList.SelectedIndex < results.Count) {
                    var selected = results[locationList.SelectedIndex];
                    LandblockInputText = selected.Name;
                    result = selected.CellId;
                    DialogHost.Close("DungeonDialogHost");
                }
            };

            await DialogHost.Show(new StackPanel {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children = {
                    new TextBlock {
                        Text = "Open Dungeon",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock {
                        Text = "Search by dungeon name or enter a\nlandblock ID in hex (e.g. 01D9).",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 400,
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    textBox,
                    locationList,
                    errorText,
                    new StackPanel {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = {
                            new Button {
                                Content = "Cancel",
                                Command = new RelayCommand(() => DialogHost.Close("DungeonDialogHost"))
                            },
                            new Button {
                                Content = "Open",
                                Command = new RelayCommand(() => {
                                    if (result != null) {
                                        DialogHost.Close("DungeonDialogHost");
                                        return;
                                    }
                                    var parsed = ParseLandblockInput(textBox.Text);
                                    if (parsed != null) {
                                        LandblockInputText = textBox.Text ?? "";
                                        result = parsed;
                                        DialogHost.Close("DungeonDialogHost");
                                    }
                                    else {
                                        errorText.Text = "Invalid input. Try a dungeon name or hex ID.";
                                        errorText.IsVisible = true;
                                    }
                                })
                            }
                        }
                    }
                }
            }, "DungeonDialogHost");

            return result;
        }

        internal static uint? ParseLandblockInput(string? input) {
            if (string.IsNullOrWhiteSpace(input)) return null;
            input = input.Trim();

            var hex = input;
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];

            if (hex.Length > 4 && hex.Length <= 8) {
                if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fullId))
                    return fullId;
                return null;
            }

            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lbId))
                return (uint)(lbId << 16);

            return null;
        }

        public void Cleanup() {
            _scene?.Dispose();
            _scene = null;
        }
    }
}
