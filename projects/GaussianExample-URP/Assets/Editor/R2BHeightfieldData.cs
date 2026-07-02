// SPDX-License-Identifier: MIT

using UnityEngine;

namespace R2B.Editor.GaussianCollision
{
    public class HeightfieldData
    {
        public int width;
        public int height;
        public Vector3 origin;
        public float cellSize;
        public float[] heights;
        public bool[] valid;

        public int CellIndex(int x, int z) => z * width + x;

        public bool IsValidCell(int x, int z)
        {
            if (x < 0 || z < 0 || x >= width || z >= height)
                return false;
            return valid[CellIndex(x, z)];
        }

        public float GetHeight(int x, int z)
        {
            return heights[CellIndex(x, z)];
        }

        public Vector3 GetWorldCorner(int x, int z)
        {
            return origin + new Vector3(x * cellSize, 0f, z * cellSize);
        }

        public Vector3 GetVertexPosition(int x, int z)
        {
            var p = GetWorldCorner(x, z);
            if (IsValidCell(x, z))
                p.y = GetHeight(x, z);
            return p;
        }
    }
}
