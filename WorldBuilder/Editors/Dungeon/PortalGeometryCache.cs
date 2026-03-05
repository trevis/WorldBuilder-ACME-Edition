using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Shared.Lib;

namespace WorldBuilder.Editors.Dungeon {

    public class PortalGeometryInfo {
        public float Area { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public int VertexCount { get; set; }
        public Vector3 Centroid { get; set; }
        public Vector3 Normal { get; set; }
    }

    /// <summary>
    /// Lazily caches portal polygon geometry (area, vertex count) from CellStruct data.
    /// Used for compatibility checks: two portals match if areas are within tolerance.
    /// </summary>
    public class PortalGeometryCache {
        private readonly Dictionary<(ushort envId, ushort cs, ushort polyId), PortalGeometryInfo?> _cache = new();
        private readonly IDatReaderWriter _dats;
        private const float AreaTolerance = 0.15f;

        public PortalGeometryCache(IDatReaderWriter dats) {
            _dats = dats;
        }

        public PortalGeometryInfo? Get(ushort envId, ushort cellStruct, ushort polyId) {
            var key = (envId, cellStruct, polyId);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var info = Compute(envId, cellStruct, polyId);
            _cache[key] = info;
            return info;
        }

        private PortalGeometryInfo? Compute(ushort envId, ushort cellStruct, ushort polyId) {
            uint envFileId = (uint)(envId | 0x0D000000);
            if (!_dats.TryGet<DatReaderWriter.DBObjs.Environment>(envFileId, out var env)) return null;
            if (!env.Cells.TryGetValue(cellStruct, out var cs)) return null;

            var geom = PortalSnapper.GetPortalGeometry(cs, polyId);
            if (geom == null) return null;

            var verts = geom.Value.Vertices;
            float area = ComputePolygonArea(verts);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in verts) {
                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
            }
            float w = MathF.Max(maxX - minX, maxY - minY);
            float h = maxZ - minZ;

            return new PortalGeometryInfo {
                Area = area,
                Width = w,
                Height = h,
                VertexCount = verts.Count,
                Centroid = geom.Value.Centroid,
                Normal = geom.Value.Normal
            };
        }

        private static float ComputePolygonArea(List<Vector3> vertices) {
            if (vertices.Count < 3) return 0f;
            var cross = Vector3.Zero;
            for (int i = 1; i < vertices.Count - 1; i++) {
                cross += Vector3.Cross(vertices[i] - vertices[0], vertices[i + 1] - vertices[0]);
            }
            return cross.Length() * 0.5f;
        }

        /// <summary>
        /// Check if two portals are geometrically compatible.
        /// Compares area, width, and height within tolerance.
        /// </summary>
        public bool AreCompatible(ushort envA, ushort csA, ushort polyA,
                                   ushort envB, ushort csB, ushort polyB) {
            var a = Get(envA, csA, polyA);
            var b = Get(envB, csB, polyB);
            if (a == null || b == null) return true;
            if (a.Area < 0.01f || b.Area < 0.01f) return true;

            float areaRatio = MathF.Min(a.Area, b.Area) / MathF.Max(a.Area, b.Area);
            if (areaRatio < (1f - AreaTolerance)) return false;

            if (a.Width > 0.5f && b.Width > 0.5f) {
                float widthRatio = MathF.Min(a.Width, b.Width) / MathF.Max(a.Width, b.Width);
                if (widthRatio < 0.7f) return false;
            }
            if (a.Height > 0.5f && b.Height > 0.5f) {
                float heightRatio = MathF.Min(a.Height, b.Height) / MathF.Max(a.Height, b.Height);
                if (heightRatio < 0.7f) return false;
            }

            return true;
        }
    }
}
