using Chorizite.OpenGLSDLBackend;
using DatReaderWriter.DBObjs;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WorldBuilder.Editors.Landscape;
using WorldBuilder.Lib;
using WorldBuilder.Lib.Settings;
using WorldBuilder.Rendering;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Simplified scene for the Dungeon Editor. Manages camera, EnvCell rendering,
    /// and GL state for a single dungeon landblock. No terrain, no scenery.
    /// Uses SceneContext (same as the landscape editor) for GPU resource management.
    /// </summary>
    public class DungeonScene : IDisposable {
        private readonly IDatReaderWriter _dats;
        private readonly WorldBuilderSettings _settings;
        private readonly TextureDiskCache _textureCache;

        public PerspectiveCamera Camera { get; }

        private OpenGLRenderer? _renderer;
        private SceneContext? _sceneContext;
        private bool _gpuInitialized;

        public EnvCellManager? EnvCellManager => _sceneContext?.EnvCellManager;

        private ushort _loadedLandblockKey;
        private bool _hasLoadedCells;

        public DungeonScene(IDatReaderWriter dats, WorldBuilderSettings settings) {
            _dats = dats;
            _settings = settings;

            var textureCacheDir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "ACME WorldBuilder", "TextureCache");
            _textureCache = new TextureDiskCache(textureCacheDir);

            Camera = new PerspectiveCamera(Vector3.Zero, settings);
        }

        /// <summary>
        /// Initialize GPU resources on the GL thread when the renderer becomes available.
        /// Uses the same SceneContext pattern as the landscape editor.
        /// </summary>
        public void InitGpu(OpenGLRenderer renderer) {
            if (_gpuInitialized && _renderer == renderer) return;

            _sceneContext?.Dispose();
            _renderer = renderer;
            _sceneContext = new SceneContext(renderer, _dats, _textureCache);
            _sceneContext.EnvCellManager.ShowDungeonCells = true;
            _sceneContext.EnvCellManager.AlwaysShowBuildingInteriors = false;
            _gpuInitialized = true;
        }

        /// <summary>
        /// Load a landblock's dungeon cells. Unloads any previously loaded landblock.
        /// Must call ProcessUploads on the GL thread afterwards.
        /// </summary>
        public bool LoadLandblock(ushort landblockKey) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null) return false;

            if (_hasLoadedCells) {
                ecm.UnloadLandblock(_loadedLandblockKey);
                _hasLoadedCells = false;
            }

            uint lbId = landblockKey;
            var envCells = new List<EnvCell>();

            uint lbiId = (lbId << 16) | 0xFFFE;
            if (!_dats.TryGet<LandBlockInfo>(lbiId, out var lbi) || lbi.NumCells == 0) {
                return false;
            }

            for (uint i = 0; i < lbi.NumCells; i++) {
                uint cellId = (lbId << 16) | (0x0100 + i);
                if (_dats.TryGet<EnvCell>(cellId, out var cell)) {
                    envCells.Add(cell);
                }
            }

            if (envCells.Count == 0) return false;

            var batch = ecm.PrepareLandblockEnvCells(landblockKey, lbId, envCells, isDungeonOnly: true);
            if (batch != null) {
                ecm.QueueForUpload(batch);
            }

            _loadedLandblockKey = landblockKey;
            ecm.FocusedDungeonLB = landblockKey;
            _hasLoadedCells = true;
            return true;
        }

        /// <summary>
        /// Reload rendering from a DungeonDocument's cell list.
        /// Unloads existing cells and re-prepares from the document.
        /// </summary>
        public void RefreshFromDocument(DungeonDocument document) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null) return;

            if (_hasLoadedCells) {
                ecm.UnloadLandblock(_loadedLandblockKey);
                _hasLoadedCells = false;
            }

            var envCells = document.ToEnvCells();
            if (envCells.Count == 0) return;

            _loadedLandblockKey = document.LandblockKey;
            uint lbId = document.LandblockKey;

            var batch = ecm.PrepareLandblockEnvCells(_loadedLandblockKey, lbId, envCells, isDungeonOnly: true);
            if (batch != null) {
                ecm.QueueForUpload(batch);
            }

            ecm.FocusedDungeonLB = _loadedLandblockKey;
            _hasLoadedCells = true;
        }

        /// <summary>
        /// Navigate the camera to a specific cell within the loaded dungeon.
        /// </summary>
        public void FocusCameraOnCell(ushort landblockKey, ushort cellId) {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null || !_hasLoadedCells) return;

            var cells = ecm.GetLoadedCellsForLandblock(landblockKey);
            if (cells == null || cells.Count == 0) {
                FocusCamera();
                return;
            }

            uint fullCellId = ((uint)landblockKey << 16) | cellId;
            var target = cells.FirstOrDefault(c => c.CellId == fullCellId);
            if (target == null) {
                FocusCamera();
                return;
            }

            Camera.SetPosition(target.WorldPosition + new Vector3(0, -15f, 8f));
            Camera.LookAt(target.WorldPosition);
        }

        /// <summary>
        /// Navigate the camera to the center of the loaded dungeon cells.
        /// </summary>
        public void FocusCamera() {
            var ecm = _sceneContext?.EnvCellManager;
            if (ecm == null || !_hasLoadedCells) return;

            var cells = ecm.GetLoadedCellsForLandblock(_loadedLandblockKey);
            if (cells == null || cells.Count == 0) return;

            var center = Vector3.Zero;
            foreach (var cell in cells) {
                center += cell.WorldPosition;
            }
            center /= cells.Count;

            Camera.SetPosition(center + new Vector3(0, -20f, 10f));
            Camera.LookAt(center);
        }

        /// <summary>
        /// Process pending GPU uploads and render the dungeon cells.
        /// Must be called on the GL thread.
        /// </summary>
        private int _diagFrame;

        public void Render(float aspectRatio) {
            var ecm = _sceneContext?.EnvCellManager;
            if (_renderer == null || ecm == null) return;

            var gl = _renderer.GraphicsDevice.GL;

            ecm.ProcessUploads(maxPerFrame: 8);

            // Explicitly set viewport to match camera screen size (FBO dimensions)
            int vpW = (int)Camera.ScreenSize.X;
            int vpH = (int)Camera.ScreenSize.Y;
            if (vpW > 0 && vpH > 0) {
                gl.Viewport(0, 0, (uint)vpW, (uint)vpH);
            }

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Less);
            gl.DepthMask(true);
            gl.ClearColor(0.05f, 0.03f, 0.08f, 1.0f);
            gl.ClearDepth(1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            var view = Camera.GetViewMatrix();
            var projection = Camera.GetProjectionMatrix();
            var viewProjection = view * projection;

            if (++_diagFrame % 300 == 1 && _hasLoadedCells) {
                var cells = ecm.GetLoadedCellsForLandblock(_loadedLandblockKey);
                Console.WriteLine($"[DungeonScene] Cells: {ecm.LoadedCellCount}, " +
                    $"Focused: {ecm.FocusedDungeonLB}, ShowDungeon: {ecm.ShowDungeonCells}, " +
                    $"Cam: ({Camera.Position.X:F0},{Camera.Position.Y:F0},{Camera.Position.Z:F0}), " +
                    $"Screen: ({vpW},{vpH}), " +
                    $"FirstCell: {(cells != null && cells.Count > 0 ? $"({cells[0].WorldPosition.X:F0},{cells[0].WorldPosition.Y:F0},{cells[0].WorldPosition.Z:F0})" : "none")}");
            }

            var lightDir = Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f));
            float ambient = 0.4f;
            float specular = 16f;

            ecm.Render(viewProjection, Camera, lightDir, ambient, specular);

            if (_diagFrame % 300 == 1 && _hasLoadedCells) {
                var err = gl.GetError();
                if (err != GLEnum.NoError)
                    Console.WriteLine($"[DungeonScene] GL error: {err}");

                var vis = ecm.LastVisibilityResult;
                Console.WriteLine($"[DungeonScene] Visibility: {(vis == null ? "null (frustum)" : $"cell 0x{vis.CameraCell?.CellId:X8}, {vis.VisibleCellIds.Count} visible")}");
            }
        }

        public void Dispose() {
            _sceneContext?.Dispose();
        }
    }
}
