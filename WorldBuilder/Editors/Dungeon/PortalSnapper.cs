using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace WorldBuilder.Editors.Dungeon {
    /// <summary>
    /// Computes the world transform for a new cell so that one of its portal
    /// polygons aligns flush with a portal polygon on an existing cell.
    /// </summary>
    public static class PortalSnapper {

        public struct PortalGeometry {
            public Vector3 Centroid;
            public Vector3 Normal;
            public List<Vector3> Vertices;
        }

        /// <summary>
        /// Extract portal polygon geometry (centroid + normal) from a CellStruct in local space.
        /// </summary>
        public static PortalGeometry? GetPortalGeometry(CellStruct cellStruct, ushort polygonId) {
            if (!cellStruct.Polygons.TryGetValue(polygonId, out var poly))
                return null;
            if (poly.VertexIds.Count < 3) return null;

            var verts = new List<Vector3>();
            foreach (var vid in poly.VertexIds) {
                if (cellStruct.VertexArray.Vertices.TryGetValue((ushort)vid, out var vtx))
                    verts.Add(vtx.Origin);
            }
            if (verts.Count < 3) return null;

            var centroid = Vector3.Zero;
            foreach (var v in verts) centroid += v;
            centroid /= verts.Count;

            var edge1 = verts[1] - verts[0];
            var edge2 = verts[2] - verts[0];
            var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));

            return new PortalGeometry {
                Centroid = centroid,
                Normal = normal,
                Vertices = verts
            };
        }

        /// <summary>
        /// Find all portal polygon IDs in a CellStruct (polygons with StipplingType.NoPos).
        /// </summary>
        public static List<ushort> GetPortalPolygonIds(CellStruct cellStruct) {
            var result = new List<ushort>();
            foreach (var kvp in cellStruct.Polygons) {
                if (kvp.Value.Stippling.HasFlag(StipplingType.NoPos))
                    result.Add(kvp.Key);
            }
            return result;
        }

        /// <summary>
        /// Compute the world transform (origin + orientation) for a new cell so that
        /// its sourcePortal aligns with the targetPortal on an existing cell.
        ///
        /// The portals will face each other (normals opposite), centers aligned.
        /// </summary>
        /// <param name="targetCentroidWorld">Target portal centroid in world space.</param>
        /// <param name="targetNormalWorld">Target portal normal in world space (pointing outward from existing cell).</param>
        /// <param name="sourceGeometryLocal">Source portal geometry in local space of the new cell.</param>
        /// <returns>Origin and orientation for the new cell in world space.</returns>
        public static (Vector3 origin, Quaternion orientation) ComputeSnapTransform(
            Vector3 targetCentroidWorld,
            Vector3 targetNormalWorld,
            PortalGeometry sourceGeometryLocal) {

            // The source portal normal needs to face OPPOSITE to the target normal
            // (portals face each other across the opening)
            var desiredSourceNormalWorld = -targetNormalWorld;

            // Compute rotation from source normal to desired direction
            var rotation = RotationBetween(sourceGeometryLocal.Normal, desiredSourceNormalWorld);

            // The rotated source centroid needs to end up at the target centroid
            var rotatedSourceCentroid = Vector3.Transform(sourceGeometryLocal.Centroid, rotation);
            var origin = targetCentroidWorld - rotatedSourceCentroid;

            return (origin, rotation);
        }

        /// <summary>
        /// Transform a portal's local-space geometry to world space given a cell's transform.
        /// </summary>
        public static (Vector3 centroid, Vector3 normal) TransformPortalToWorld(
            PortalGeometry localGeometry, Vector3 cellOrigin, Quaternion cellOrientation) {

            var worldCentroid = Vector3.Transform(localGeometry.Centroid, cellOrientation) + cellOrigin;
            var worldNormal = Vector3.Normalize(Vector3.Transform(localGeometry.Normal, cellOrientation));
            return (worldCentroid, worldNormal);
        }

        /// <summary>
        /// Compute the shortest rotation quaternion that rotates vector 'from' to vector 'to'.
        /// </summary>
        private static Quaternion RotationBetween(Vector3 from, Vector3 to) {
            from = Vector3.Normalize(from);
            to = Vector3.Normalize(to);

            float dot = Vector3.Dot(from, to);

            if (dot > 0.999999f) {
                return Quaternion.Identity;
            }

            if (dot < -0.999999f) {
                // 180-degree rotation: pick an arbitrary perpendicular axis
                var perp = Math.Abs(from.X) < 0.9f
                    ? Vector3.Cross(Vector3.UnitX, from)
                    : Vector3.Cross(Vector3.UnitY, from);
                perp = Vector3.Normalize(perp);
                return Quaternion.CreateFromAxisAngle(perp, MathF.PI);
            }

            var axis = Vector3.Cross(from, to);
            float w = 1.0f + dot;
            return Quaternion.Normalize(new Quaternion(axis.X, axis.Y, axis.Z, w));
        }
    }
}
