// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEngine;

namespace R2B.Editor.GaussianCollision
{
    /// <summary>
    /// splat-transform uses raw PLY file coordinates (= GaussianSplatAsset local space).
    /// Unity displays them via GaussianSplatRenderer.transform, same as the splat shader.
    /// </summary>
    public static class GaussianSplatCoords
    {
        public static Vector3 WorldToFile(Transform renderer, Vector3 world)
        {
            return renderer.InverseTransformPoint(world);
        }

        public static Vector3 FileToWorld(Transform renderer, Vector3 file)
        {
            return renderer.TransformPoint(file);
        }

        public static Matrix4x4 FileToWorldMatrix(Transform renderer)
        {
            return renderer.localToWorldMatrix;
        }

        public static Bounds GetAssetFileBounds(GaussianSplatRenderer renderer)
        {
            if (renderer?.m_Asset == null)
                return new Bounds(Vector3.zero, Vector3.one);

            var bounds = new Bounds();
            bounds.SetMinMax(renderer.m_Asset.boundsMin, renderer.m_Asset.boundsMax);
            return bounds;
        }

        /// <summary>
        /// Same oriented-bounds transform used by GaussianSplatRendererEditor.
        /// </summary>
        public static Bounds TransformBounds(Transform transform, Bounds localBounds)
        {
            Vector3 center = transform.TransformPoint(localBounds.center);

            Vector3 ext = localBounds.extents;
            Vector3 axisX = transform.TransformVector(ext.x, 0f, 0f);
            Vector3 axisY = transform.TransformVector(0f, ext.y, 0f);
            Vector3 axisZ = transform.TransformVector(0f, 0f, ext.z);

            ext.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            ext.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            ext.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

            return new Bounds(center, ext * 2f);
        }

        public static Bounds GetAssetWorldBounds(GaussianSplatRenderer renderer)
        {
            if (renderer == null)
                return new Bounds(Vector3.zero, Vector3.one);
            return TransformBounds(renderer.transform, GetAssetFileBounds(renderer));
        }

        public static Bounds GetMeshWorldBounds(Mesh mesh, Transform renderer)
        {
            if (mesh == null || renderer == null)
                return default;
            return TransformBounds(renderer.transform, mesh.bounds);
        }

        public static void WorldBoxToFileAabb(
            Transform renderer,
            Vector3 worldCenter,
            Vector3 worldHalfExtents,
            out Vector3 fileMin,
            out Vector3 fileMax)
        {
            fileMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            fileMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

            Vector3 h = worldHalfExtents;
            for (int xi = -1; xi <= 1; xi += 2)
            for (int yi = -1; yi <= 1; yi += 2)
            for (int zi = -1; zi <= 1; zi += 2)
            {
                Vector3 worldCorner = worldCenter + new Vector3(xi * h.x, yi * h.y, zi * h.z);
                Vector3 fileCorner = renderer.InverseTransformPoint(worldCorner);
                fileMin = Vector3.Min(fileMin, fileCorner);
                fileMax = Vector3.Max(fileMax, fileCorner);
            }
        }

        public static bool HasNonIdentityRotation(Transform renderer)
        {
            if (renderer == null)
                return false;
            return Quaternion.Angle(renderer.rotation, Quaternion.identity) > 0.5f;
        }
    }
}
