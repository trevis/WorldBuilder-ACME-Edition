using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Chorizite.OpenGLSDLBackend;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DialogHostAvalonia;
using System;
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

        private LoadedEnvCell? _selectedCell;

        public WorldBuilderSettings Settings { get; }

        private Project? _project;
        private IDatReaderWriter? _dats;
        private DungeonScene? _scene;
        private ushort _loadedLandblockKey;

        public DungeonScene? Scene => _scene;

        public DungeonEditorViewModel(WorldBuilderSettings settings) {
            Settings = settings;
        }

        internal void Init(Project project) {
            _project = project;
            _dats = project.DocumentManager.Dats;
            _scene = new DungeonScene(_dats, Settings);
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
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.G) {
                _ = OpenLandblockCommand.ExecuteAsync(null);
            }
            if (e.Key == Key.Escape) {
                DeselectCell();
            }
        }

        internal void HandlePointerWheel(PointerWheelEventArgs e) {
            _scene?.Camera.ProcessMouseScroll((float)e.Delta.Y);
        }

        internal void HandlePointerPressed(AvaloniaInputState inputState) {
            if (_scene?.EnvCellManager == null || !HasDungeon) return;

            var mouse = inputState.MouseState;
            if (!mouse.LeftPressed || mouse.RightPressed) return;

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

            ushort cellNum = (ushort)(cell.CellId & 0xFFFF);
            ushort lbKey = (ushort)(cell.CellId >> 16);
            int portalCount = cell.Portals?.Count ?? 0;
            int openPortals = 0;

            if (cell.Portals != null) {
                foreach (var p in cell.Portals) {
                    if (p.OtherCellId == 0 || p.OtherCellId == 0xFFFF) openPortals++;
                }
            }

            SelectedCellInfo = $"Cell {cell.CellId:X8}  |  Env: {cell.EnvironmentId:X8}  |  Portals: {portalCount} ({openPortals} open)  |  Surfaces: {cell.SurfaceCount}";
        }

        private void DeselectCell() {
            _selectedCell = null;
            HasSelectedCell = false;
            SelectedCellInfo = "";
        }

        [RelayCommand]
        public async Task OpenLandblock() {
            if (_dats == null) return;

            var cellId = await ShowOpenDungeonDialog();
            if (cellId == null) return;

            var lbId = (ushort)(cellId.Value >> 16);
            if (lbId == 0) lbId = (ushort)(cellId.Value & 0xFFFF);

            LoadDungeon(lbId);
        }

        public void LoadDungeon(ushort landblockKey) {
            if (_scene == null || _dats == null) return;

            uint lbiId = ((uint)landblockKey << 16) | 0xFFFE;
            if (!_dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) {
                StatusText = $"LB {landblockKey:X4}: No dungeon cells found";
                HasDungeon = false;
                CellCount = 0;
                return;
            }

            if (_scene.LoadLandblock(landblockKey)) {
                _loadedLandblockKey = landblockKey;
                var cells = _scene.EnvCellManager?.GetLoadedCellsForLandblock(landblockKey);
                CellCount = cells?.Count ?? (int)lbi.NumCells;
                StatusText = $"LB {landblockKey:X4}: {CellCount} cells";
                HasDungeon = true;

                _scene.FocusCamera();
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
