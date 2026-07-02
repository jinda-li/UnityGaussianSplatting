// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace R2B.Editor.GaussianCollision
{
    public static class HeightfieldMeshBuilder
    {
        const int kLargeGridThreshold = 600;

        public static HeightfieldData FillHoles(HeightfieldData source, int maxIterations = 8)
        {
            var data = Clone(source);
            GetValidCellBounds(data, out int minX, out int maxX, out int minZ, out int maxZ);

            var pending = new bool[data.width * data.height];
            for (int iter = 0; iter < maxIterations; ++iter)
            {
                int filled = 0;
                for (int z = minZ; z <= maxZ; ++z)
                {
                    for (int x = minX; x <= maxX; ++x)
                    {
                        if (data.IsValidCell(x, z))
                            continue;

                        float sum = 0f;
                        int count = 0;
                        for (int dz = -1; dz <= 1; ++dz)
                        {
                            for (int dx = -1; dx <= 1; ++dx)
                            {
                                if (dx == 0 && dz == 0)
                                    continue;
                                int nx = x + dx;
                                int nz = z + dz;
                                if (!data.IsValidCell(nx, nz))
                                    continue;
                                sum += data.GetHeight(nx, nz);
                                count++;
                            }
                        }

                        if (count > 0)
                        {
                            int idx = data.CellIndex(x, z);
                            pending[idx] = true;
                            data.heights[idx] = sum / count;
                            filled++;
                        }
                    }
                }

                for (int i = 0; i < pending.Length; ++i)
                {
                    if (!pending[i])
                        continue;
                    data.valid[i] = true;
                    pending[i] = false;
                }

                if (filled == 0)
                    break;
            }

            return data;
        }

        public static HeightfieldData Smooth(HeightfieldData source, int iterations)
        {
            var data = Clone(source);
            if (iterations <= 0)
                return data;

            GetValidCellBounds(data, out int minX, out int maxX, out int minZ, out int maxZ);
            var scratch = new float[data.heights.Length];

            for (int iter = 0; iter < iterations; ++iter)
            {
                for (int z = minZ; z <= maxZ; ++z)
                {
                    for (int x = minX; x <= maxX; ++x)
                    {
                        int idx = data.CellIndex(x, z);
                        if (!data.valid[idx])
                            continue;

                        float sum = 0f;
                        int count = 0;
                        for (int dz = -1; dz <= 1; ++dz)
                        {
                            for (int dx = -1; dx <= 1; ++dx)
                            {
                                int nx = x + dx;
                                int nz = z + dz;
                                if (!data.IsValidCell(nx, nz))
                                    continue;
                                sum += data.GetHeight(nx, nz);
                                count++;
                            }
                        }

                        scratch[idx] = count > 0 ? sum / count : data.heights[idx];
                    }
                }

                for (int z = minZ; z <= maxZ; ++z)
                {
                    for (int x = minX; x <= maxX; ++x)
                    {
                        int idx = data.CellIndex(x, z);
                        if (data.valid[idx])
                            data.heights[idx] = scratch[idx];
                    }
                }
            }

            return data;
        }

        public static Mesh BuildMesh(HeightfieldData data)
        {
            var vertices = new List<Vector3>(data.width * data.height);
            var uvs = new List<Vector2>(data.width * data.height);
            var triangles = new List<int>((data.width - 1) * (data.height - 1) * 6);
            var vertexGrid = new int[data.width * data.height];

            for (int i = 0; i < vertexGrid.Length; ++i)
                vertexGrid[i] = -1;

            for (int z = 0; z < data.height; ++z)
            {
                for (int x = 0; x < data.width; ++x)
                {
                    if (!data.IsValidCell(x, z))
                        continue;

                    int idx = data.CellIndex(x, z);
                    vertexGrid[idx] = vertices.Count;
                    vertices.Add(data.GetVertexPosition(x, z));
                    uvs.Add(new Vector2(
                        data.width > 1 ? x / (float)(data.width - 1) : 0f,
                        data.height > 1 ? z / (float)(data.height - 1) : 0f));
                }
            }

            for (int z = 0; z < data.height - 1; ++z)
            {
                for (int x = 0; x < data.width - 1; ++x)
                {
                    int i00 = vertexGrid[data.CellIndex(x, z)];
                    int i10 = vertexGrid[data.CellIndex(x + 1, z)];
                    int i01 = vertexGrid[data.CellIndex(x, z + 1)];
                    int i11 = vertexGrid[data.CellIndex(x + 1, z + 1)];

                    if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0)
                        continue;

                    triangles.Add(i00);
                    triangles.Add(i11);
                    triangles.Add(i10);

                    triangles.Add(i00);
                    triangles.Add(i01);
                    triangles.Add(i11);
                }
            }

            if (vertices.Count == 0 || triangles.Count == 0)
                throw new System.InvalidOperationException("Heightfield produced an empty mesh. Try lowering opacity threshold or cell size.");

            var mesh = new Mesh
            {
                name = "GroundCollisionMesh",
                indexFormat = vertices.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Mesh BuildPreviewMesh(HeightfieldData data, int maxVertices = 25000)
        {
            int step = 1;
            int validCount = 0;
            for (int i = 0; i < data.valid.Length; ++i)
            {
                if (data.valid[i])
                    validCount++;
            }

            while (validCount / (step * step) > maxVertices && step < 64)
                step++;

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var vertexGrid = new int[data.width * data.height];
            for (int i = 0; i < vertexGrid.Length; ++i)
                vertexGrid[i] = -1;

            for (int z = 0; z < data.height; z += step)
            {
                for (int x = 0; x < data.width; x += step)
                {
                    if (!data.IsValidCell(x, z))
                        continue;
                    int idx = data.CellIndex(x, z);
                    vertexGrid[idx] = vertices.Count;
                    vertices.Add(data.GetVertexPosition(x, z));
                }
            }

            for (int z = 0; z < data.height - step; z += step)
            {
                for (int x = 0; x < data.width - step; x += step)
                {
                    int i00 = vertexGrid[data.CellIndex(x, z)];
                    int i10 = vertexGrid[data.CellIndex(x + step, z)];
                    int i01 = vertexGrid[data.CellIndex(x, z + step)];
                    int i11 = vertexGrid[data.CellIndex(x + step, z + step)];
                    if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0)
                        continue;

                    triangles.Add(i00);
                    triangles.Add(i11);
                    triangles.Add(i10);
                    triangles.Add(i00);
                    triangles.Add(i01);
                    triangles.Add(i11);
                }
            }

            if (vertices.Count == 0)
                return null;

            var mesh = new Mesh { name = "GroundCollisionPreview" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        public static HeightfieldData Process(HeightfieldData source, int holeFillIterations, int smoothingIterations)
        {
            int maxDim = Mathf.Max(source.width, source.height);
            if (maxDim > kLargeGridThreshold)
            {
                holeFillIterations = Mathf.Min(holeFillIterations, 4);
                smoothingIterations = Mathf.Min(smoothingIterations, 2);
            }

            var filled = FillHoles(source, holeFillIterations);
            return Smooth(filled, smoothingIterations);
        }

        static void GetValidCellBounds(HeightfieldData data, out int minX, out int maxX, out int minZ, out int maxZ)
        {
            minX = data.width;
            maxX = -1;
            minZ = data.height;
            maxZ = -1;

            for (int z = 0; z < data.height; ++z)
            {
                for (int x = 0; x < data.width; ++x)
                {
                    if (!data.IsValidCell(x, z))
                        continue;
                    minX = Mathf.Min(minX, x);
                    maxX = Mathf.Max(maxX, x);
                    minZ = Mathf.Min(minZ, z);
                    maxZ = Mathf.Max(maxZ, z);
                }
            }

            if (maxX < minX || maxZ < minZ)
            {
                minX = maxX = minZ = maxZ = 0;
            }
        }

        static HeightfieldData Clone(HeightfieldData source)
        {
            return new HeightfieldData
            {
                width = source.width,
                height = source.height,
                origin = source.origin,
                cellSize = source.cellSize,
                heights = (float[])source.heights.Clone(),
                valid = (bool[])source.valid.Clone()
            };
        }
    }
}
