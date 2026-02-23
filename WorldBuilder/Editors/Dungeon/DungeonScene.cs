using Chorizite.OpenGLSDLBackend;
using DatReaderWriter.DBObjs;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
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
    /// </summary>
    public class DungeonScene : IDisposable {
        private readonly IDatReaderWriter _dats;
        private readonly WorldBuilderSettings _settings;
        private readonly TextureDiskCache _textureCache;

        public PerspectiveCamera Camera { get; }

        private OpenGLRenderer? _renderer;
        private StaticObjectManager? _objectManager;
        private EnvCellManager? _envCellManager;
        private bool _gpuInitialized;

        public EnvCellManager? EnvCellManager => _envCellManager;

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
        /// </summary>
        public void InitGpu(OpenGLRenderer renderer) {
            if (_gpuInitialized) return;
            _renderer = renderer;
            _objectManager = new StaticObjectManager(renderer, _dats, _textureCache);
            _envCellManager = new EnvCellManager(renderer, _dats, _objectManager._objectShader, _textureCache);
            _envCellManager.ShowDungeonCells = true;
            _envCellManager.AlwaysShowBuildingInteriors = false;
            _gpuInitialized = true;
        }

        /// <summary>
        /// Load a landblock's dungeon cells. Unloads any previously loaded landblock.
        /// Must call ProcessUploads on the GL thread afterwards.
        /// </summary>
        public bool LoadLandblock(ushort landblockKey) {
            if (_envCellManager == null) return false;

            if (_hasLoadedCells) {
                _envCellManager.UnloadLandblock(_loadedLandblockKey);
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

            var batch = _envCellManager.PrepareLandblockEnvCells(landblockKey, lbId, envCells, isDungeonOnly: true);
            if (batch != null) {
                _envCellManager.QueueForUpload(batch);
            }

            _loadedLandblockKey = landblockKey;
            _envCellManager.FocusedDungeonLB = landblockKey;
            _hasLoadedCells = true;
            return true;
        }

        /// <summary>
        /// Navigate the camera to the center of the loaded dungeon cells.
        /// </summary>
        public void FocusCamera() {
            if (_envCellManager == null || !_hasLoadedCells) return;

            var cells = _envCellManager.GetLoadedCellsForLandblock(_loadedLandblockKey);
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
        public void Render(float aspectRatio) {
            if (_renderer == null || _envCellManager == null) return;

            var gl = _renderer.GraphicsDevice.GL;

            _envCellManager.ProcessUploads(maxPerFrame: 8);

            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Lequal);
            gl.ClearColor(0.05f, 0.03f, 0.08f, 1.0f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            var view = Camera.GetViewMatrix();
            var projection = Camera.GetProjectionMatrix();
            var viewProjection = view * projection;

            var lightDir = Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f));
            float ambient = 0.4f;
            float specular = 16f;

            _envCellManager.Render(viewProjection, Camera, lightDir, ambient, specular);
        }

        public void Dispose() {
            _envCellManager?.Dispose();
            _objectManager?.Dispose();
        }
    }
}
