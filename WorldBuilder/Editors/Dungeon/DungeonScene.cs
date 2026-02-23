using Chorizite.Core.Lib;
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
using WorldBuilder.Shared.Documents;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Simplified scene for the Dungeon Editor. Manages camera, EnvCell rendering,
    /// static object rendering, and GL state for a single dungeon landblock.
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

        private List<StaticObject> _dungeonStatics = new();
        private readonly Dictionary<(uint, bool), List<Matrix4x4>> _objectGroupBuffer = new();

        /// <summary>
        /// Set by the editor to highlight a selected cell with a wireframe box.
        /// </summary>
        public LoadedEnvCell? SelectedCell { get; set; }

        private uint _lineVAO;
        private uint _lineVBO;

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
                IntegrateStatics(batch);
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
                IntegrateStatics(batch);
            }

            ecm.FocusedDungeonLB = _loadedLandblockKey;
            _hasLoadedCells = true;
        }

        private void IntegrateStatics(PreparedEnvCellBatch batch) {
            _dungeonStatics.Clear();
            _dungeonStatics.AddRange(batch.DungeonStaticObjects);

            if (_sceneContext == null) return;
            foreach (var obj in _dungeonStatics) {
                if (_sceneContext.ObjectManager.TryGetCachedRenderData(obj.Id) == null &&
                    !_sceneContext.ObjectManager.IsKnownFailure(obj.Id)) {
                    _sceneContext.ModelWarmupQueue.Enqueue((obj.Id, obj.IsSetup));
                }
            }
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
                Console.WriteLine($"[DungeonScene] Cells: {ecm.LoadedCellCount}, " +
                    $"Statics: {_dungeonStatics.Count}, " +
                    $"ModelsQueued: {_sceneContext?.ModelWarmupQueue.Count ?? 0}, " +
                    $"ModelsPreparing: {_sceneContext?.ModelsPreparing.Count ?? 0}, " +
                    $"ModelsUploading: {_sceneContext?.ModelUploadQueue.Count ?? 0}");
            }

            var lightDir = Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f));
            float ambient = 0.4f;
            float specular = 16f;

            ecm.Render(viewProjection, Camera, lightDir, ambient, specular);

            // Process model warmup/uploads for static objects
            ProcessModelUploads();

            // Render static objects (torches, furniture, etc.)
            if (_dungeonStatics.Count > 0) {
                RenderStaticObjects(gl, viewProjection);
            }

            // Render portal indicators on selected cell
            if (SelectedCell != null) {
                RenderPortalIndicators(gl, viewProjection);
            }

            // Render selection highlight
            if (SelectedCell != null) {
                RenderSelectionBox(gl, viewProjection);
            }
        }

        private void ProcessModelUploads() {
            if (_sceneContext == null) return;
            var objectManager = _sceneContext.ObjectManager;

            int uploaded = 0;
            while (uploaded < 16 && _sceneContext.ModelUploadQueue.TryDequeue(out var preparedModel)) {
                objectManager.FinalizeGpuUpload(preparedModel);
                uploaded++;
            }

            int warmupCount = 0;
            while (warmupCount < 16 && _sceneContext.ModelWarmupQueue.Count > 0) {
                var (id, isSetup) = _sceneContext.ModelWarmupQueue.Dequeue();
                if (objectManager.TryGetCachedRenderData(id) != null) continue;
                if (_sceneContext.ModelsPreparing.Contains(id)) continue;
                _sceneContext.ModelsPreparing.Add(id);

                var localId = id;
                var localIsSetup = isSetup;
                var localCtx = _sceneContext;
                System.Threading.Tasks.Task.Run(() => {
                    var prepared = objectManager.PrepareModelData(localId, localIsSetup);
                    if (prepared != null) {
                        localCtx.ModelUploadQueue.Enqueue(prepared);
                    }
                    localCtx.ModelsPreparing.Remove(localId);
                });
                warmupCount++;
            }
        }

        private unsafe void RenderStaticObjects(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || _dungeonStatics.Count == 0) return;
            var objectManager = _sceneContext.ObjectManager;

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);

            objectManager._objectShader.Bind();
            objectManager._objectShader.SetUniform("uViewProjection", viewProjection);
            objectManager._objectShader.SetUniform("uCameraPosition", Camera.Position);
            objectManager._objectShader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            objectManager._objectShader.SetUniform("uAmbientIntensity", 0.4f);
            objectManager._objectShader.SetUniform("uSpecularPower", 16f);

            foreach (var list in _objectGroupBuffer.Values) list.Clear();

            foreach (var obj in _dungeonStatics) {
                var key = (obj.Id, obj.IsSetup);
                if (!_objectGroupBuffer.TryGetValue(key, out var list)) {
                    list = new List<Matrix4x4>();
                    _objectGroupBuffer[key] = list;
                }
                list.Add(
                    Matrix4x4.CreateScale(obj.Scale)
                    * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                    * Matrix4x4.CreateTranslation(obj.Origin));
            }

            foreach (var group in _objectGroupBuffer) {
                if (group.Value.Count == 0) continue;
                var (id, isSetup) = group.Key;

                var renderData = objectManager.TryGetCachedRenderData(id);
                if (renderData == null) continue;

                if (isSetup) {
                    foreach (var (partId, partTransform) in renderData.SetupParts) {
                        var partRenderData = objectManager.TryGetCachedRenderData(partId);
                        if (partRenderData == null) continue;

                        var partTransforms = new List<Matrix4x4>();
                        foreach (var instanceMatrix in group.Value) {
                            partTransforms.Add(partTransform * instanceMatrix);
                        }

                        RenderBatchedObject(gl, partRenderData, partTransforms);
                    }
                }
                else {
                    RenderBatchedObject(gl, renderData, group.Value);
                }
            }

            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(EnableCap.Blend);
        }

        private unsafe void RenderPortalIndicators(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || SelectedCell == null) return;

            uint envFileId = SelectedCell.EnvironmentId;
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) return;

            var gpuKey = SelectedCell.GpuKey;
            ushort cellStructIdx = (ushort)gpuKey.CellStructure;
            if (!env.Cells.TryGetValue(cellStructIdx, out var cellStruct)) return;

            var portalIds = PortalSnapper.GetPortalPolygonIds(cellStruct);
            if (portalIds.Count == 0) return;

            var connectedPolys = new HashSet<ushort>();
            foreach (var p in SelectedCell.Portals) {
                connectedPolys.Add(p.PolygonId);
            }

            var transform = SelectedCell.WorldTransform;
            var lineVerts = new List<float>();

            foreach (var polyId in portalIds) {
                if (!cellStruct.Polygons.TryGetValue(polyId, out var poly)) continue;
                if (poly.VertexIds.Count < 3) continue;

                var worldVerts = new List<Vector3>();
                foreach (var vid in poly.VertexIds) {
                    if (cellStruct.VertexArray.Vertices.TryGetValue((ushort)vid, out var vtx)) {
                        worldVerts.Add(Vector3.Transform(vtx.Origin, transform));
                    }
                }

                if (worldVerts.Count < 3) continue;

                bool isConnected = connectedPolys.Contains(polyId);
                float portalLineWidth = 0.12f;
                var camPos = Camera.Position;

                // Billboard quads for each edge of the portal polygon
                for (int i = 0; i < worldVerts.Count; i++) {
                    int next = (i + 1) % worldVerts.Count;
                    var a = worldVerts[i];
                    var b = worldVerts[next];
                    var edgeDir = Vector3.Normalize(b - a);
                    var mid = (a + b) * 0.5f;
                    var toCamera = Vector3.Normalize(camPos - mid);
                    var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * portalLineWidth;
                    var n = toCamera;

                    void PV(Vector3 p) { lineVerts.Add(p.X); lineVerts.Add(p.Y); lineVerts.Add(p.Z); lineVerts.Add(n.X); lineVerts.Add(n.Y); lineVerts.Add(n.Z); }
                    PV(a - sideDir); PV(b - sideDir); PV(b + sideDir);
                    PV(a - sideDir); PV(b + sideDir); PV(a + sideDir);
                }
            }

            if (lineVerts.Count == 0) return;
            int vertCount = lineVerts.Count / 6;
            var portalData = lineVerts.ToArray();

            uint portalVAO, portalVBO;
            gl.GenVertexArrays(1, out portalVAO);
            gl.GenBuffers(1, out portalVBO);

            gl.BindVertexArray(portalVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, portalVBO);
            fixed (float* ptr = portalData) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(portalData.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);

            gl.DisableVertexAttribArray(2);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", Camera.Position);
            shader.SetUniform("uSphereColor", new Vector3(0.2f, 0.9f, 0.3f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);

            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);

            gl.DeleteBuffer(portalVBO);
            gl.DeleteVertexArray(portalVAO);
        }

        private unsafe void RenderSelectionBox(GL gl, Matrix4x4 viewProjection) {
            if (_sceneContext == null || SelectedCell == null) return;

            var cell = SelectedCell;
            var min = cell.LocalBoundsMin;
            var max = cell.LocalBoundsMax;
            if (min.X >= max.X) return;

            var transform = cell.WorldTransform;

            Vector3[] c = new Vector3[8];
            c[0] = Vector3.Transform(new Vector3(min.X, min.Y, min.Z), transform);
            c[1] = Vector3.Transform(new Vector3(max.X, min.Y, min.Z), transform);
            c[2] = Vector3.Transform(new Vector3(max.X, max.Y, min.Z), transform);
            c[3] = Vector3.Transform(new Vector3(min.X, max.Y, min.Z), transform);
            c[4] = Vector3.Transform(new Vector3(min.X, min.Y, max.Z), transform);
            c[5] = Vector3.Transform(new Vector3(max.X, min.Y, max.Z), transform);
            c[6] = Vector3.Transform(new Vector3(max.X, max.Y, max.Z), transform);
            c[7] = Vector3.Transform(new Vector3(min.X, max.Y, max.Z), transform);

            // Build billboard quads for each edge (2 tris per edge, camera-facing thickness)
            int[][] edges = { new[]{0,1}, new[]{1,2}, new[]{2,3}, new[]{3,0},
                              new[]{4,5}, new[]{5,6}, new[]{6,7}, new[]{7,4},
                              new[]{0,4}, new[]{1,5}, new[]{2,6}, new[]{3,7} };

            const float lineWidth = 0.15f;
            var camPos = Camera.Position;
            var verts = new List<float>();

            foreach (var e in edges) {
                var a = c[e[0]];
                var b = c[e[1]];
                var edgeDir = Vector3.Normalize(b - a);
                var midpoint = (a + b) * 0.5f;
                var toCamera = Vector3.Normalize(camPos - midpoint);
                var sideDir = Vector3.Normalize(Vector3.Cross(edgeDir, toCamera)) * lineWidth;

                var normal = toCamera;
                var a0 = a - sideDir; var a1 = a + sideDir;
                var b0 = b - sideDir; var b1 = b + sideDir;

                void V(Vector3 p) { verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z); verts.Add(normal.X); verts.Add(normal.Y); verts.Add(normal.Z); }
                V(a0); V(b0); V(b1);
                V(a0); V(b1); V(a1);
            }

            int vertCount = verts.Count / 6;
            var data = verts.ToArray();

            if (_lineVAO == 0) {
                gl.GenVertexArrays(1, out _lineVAO);
                gl.GenBuffers(1, out _lineVBO);
            }

            gl.BindVertexArray(_lineVAO);
            gl.BindBuffer(GLEnum.ArrayBuffer, _lineVBO);
            fixed (float* ptr = data) {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(data.Length * sizeof(float)), ptr, GLEnum.DynamicDraw);
            }
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GLEnum.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            for (uint i = 2; i < 8; i++) gl.DisableVertexAttribArray(i);
            gl.DisableVertexAttribArray(2);
            gl.VertexAttrib4(2, 0f, 0f, 0f, 1f);

            var shader = _sceneContext.SphereShader;
            shader.Bind();
            shader.SetUniform("uViewProjection", viewProjection);
            shader.SetUniform("uCameraPosition", camPos);
            shader.SetUniform("uSphereColor", new Vector3(0.7f, 0.3f, 1.0f));
            shader.SetUniform("uLightDirection", Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f)));
            shader.SetUniform("uAmbientIntensity", 1.0f);
            shader.SetUniform("uSpecularPower", 0f);
            shader.SetUniform("uGlowColor", new Vector3(0f, 0f, 0f));
            shader.SetUniform("uGlowIntensity", 0f);
            shader.SetUniform("uGlowPower", 1.0f);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);

            gl.DrawArrays(GLEnum.Triangles, 0, (uint)vertCount);

            gl.Enable(EnableCap.DepthTest);
            gl.Enable(EnableCap.CullFace);

            gl.BindVertexArray(0);
            gl.UseProgram(0);
        }

        private unsafe void RenderBatchedObject(GL gl, StaticObjectRenderData renderData, List<Matrix4x4> instanceTransforms) {
            if (_sceneContext == null || instanceTransforms.Count == 0 || renderData.Batches.Count == 0) return;

            int requiredFloats = instanceTransforms.Count * 16;

            if (_sceneContext.InstanceUploadBuffer.Length < requiredFloats) {
                int newSize = Math.Max(requiredFloats, 256);
                newSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newSize);
                _sceneContext.InstanceUploadBuffer = new float[newSize];
            }

            for (int i = 0; i < instanceTransforms.Count; i++) {
                var t = instanceTransforms[i];
                int o = i * 16;
                _sceneContext.InstanceUploadBuffer[o+ 0]=t.M11; _sceneContext.InstanceUploadBuffer[o+ 1]=t.M12;
                _sceneContext.InstanceUploadBuffer[o+ 2]=t.M13; _sceneContext.InstanceUploadBuffer[o+ 3]=t.M14;
                _sceneContext.InstanceUploadBuffer[o+ 4]=t.M21; _sceneContext.InstanceUploadBuffer[o+ 5]=t.M22;
                _sceneContext.InstanceUploadBuffer[o+ 6]=t.M23; _sceneContext.InstanceUploadBuffer[o+ 7]=t.M24;
                _sceneContext.InstanceUploadBuffer[o+ 8]=t.M31; _sceneContext.InstanceUploadBuffer[o+ 9]=t.M32;
                _sceneContext.InstanceUploadBuffer[o+10]=t.M33; _sceneContext.InstanceUploadBuffer[o+11]=t.M34;
                _sceneContext.InstanceUploadBuffer[o+12]=t.M41; _sceneContext.InstanceUploadBuffer[o+13]=t.M42;
                _sceneContext.InstanceUploadBuffer[o+14]=t.M43; _sceneContext.InstanceUploadBuffer[o+15]=t.M44;
            }

            if (_sceneContext.InstanceVBO == 0) {
                gl.GenBuffers(1, out uint vbo);
                _sceneContext.InstanceVBO = vbo;
            }

            gl.BindBuffer(GLEnum.ArrayBuffer, _sceneContext.InstanceVBO);

            if (requiredFloats > _sceneContext.InstanceBufferCapacity) {
                int newCapacity = Math.Max(requiredFloats, 256);
                newCapacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)newCapacity);
                _sceneContext.InstanceBufferCapacity = newCapacity;
                fixed (float* ptr = _sceneContext.InstanceUploadBuffer) {
                    gl.BufferData(GLEnum.ArrayBuffer, (nuint)(newCapacity * sizeof(float)), ptr, GLEnum.DynamicDraw);
                }
            }
            else {
                fixed (float* ptr = _sceneContext.InstanceUploadBuffer) {
                    gl.BufferSubData(GLEnum.ArrayBuffer, 0, (nuint)(requiredFloats * sizeof(float)), ptr);
                }
            }

            gl.BindVertexArray(renderData.VAO);

            gl.BindBuffer(GLEnum.ArrayBuffer, _sceneContext.InstanceVBO);
            for (int i = 0; i < 4; i++) {
                gl.EnableVertexAttribArray((uint)(3 + i));
                gl.VertexAttribPointer((uint)(3 + i), 4, GLEnum.Float, false, (uint)(16 * sizeof(float)), (void*)(i * 4 * sizeof(float)));
                gl.VertexAttribDivisor((uint)(3 + i), 1);
            }

            bool cullFaceEnabled = true;
            foreach (var batch in renderData.Batches) {
                if (batch.TextureArray == null) continue;

                if (batch.IsDoubleSided && cullFaceEnabled) {
                    gl.Disable(EnableCap.CullFace);
                    cullFaceEnabled = false;
                }
                else if (!batch.IsDoubleSided && !cullFaceEnabled) {
                    gl.Enable(EnableCap.CullFace);
                    cullFaceEnabled = true;
                }

                batch.TextureArray.Bind(0);
                _sceneContext.ObjectManager._objectShader.SetUniform("uTextureArray", 0);
                _sceneContext.ObjectManager._objectShader.SetUniform("uTextureIndex", (float)batch.TextureIndex);
                gl.DisableVertexAttribArray(7);
                gl.VertexAttrib1((uint)7, (float)batch.TextureIndex);

                gl.BindBuffer(GLEnum.ElementArrayBuffer, batch.IBO);
                gl.DrawElementsInstanced(GLEnum.Triangles, (uint)batch.IndexCount, GLEnum.UnsignedShort, null, (uint)instanceTransforms.Count);
            }

            if (!cullFaceEnabled) gl.Enable(EnableCap.CullFace);
        }

        public void Dispose() {
            _sceneContext?.Dispose();
        }
    }
}
