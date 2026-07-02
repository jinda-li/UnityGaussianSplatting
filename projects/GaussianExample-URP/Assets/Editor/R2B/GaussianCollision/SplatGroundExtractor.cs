// SPDX-License-Identifier: MIT

using System;
using GaussianSplatting.Editor.Utils;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace R2B.Editor.GaussianCollision
{
    public struct GroundExtractionSettings
    {
        public float cellSize;
        public float opacityThreshold;
        public float heightPercentile;
        public float4x4 transform;
        public bool applyTransform;
        public int maxGridDimension;
        public int splatStride;
        public bool useTightBounds;
        public float tightBoundsPercentile;
        public float3 boundsMinOverride;
        public float3 boundsMaxOverride;
        public bool useBoundsOverride;

        public const int kDefaultMaxGridDimension = 512;
        public const int kGenerateMaxGridDimension = 2048;
        public const int kHistogramBuckets = 64;
        public const int kTightBoundsSampleCap = 200000;
    }

    public static class SplatGroundExtractor
    {
        struct BoundsResult
        {
            public float3 min;
            public float3 max;
            public int filteredCount;
        }

        public static HeightfieldData ExtractFromFile(string filePath, GroundExtractionSettings settings)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                throw new ArgumentException($"PLY/SPZ file not found: {filePath}");

            GaussianFileReader.ReadFile(filePath, out NativeArray<InputSplatData> splats);
            try
            {
                return Extract(splats, settings);
            }
            finally
            {
                if (splats.IsCreated)
                    splats.Dispose();
            }
        }

        public static HeightfieldData Extract(NativeArray<InputSplatData> splats, GroundExtractionSettings settings)
        {
            if (!splats.IsCreated || splats.Length == 0)
                throw new InvalidOperationException("No splat data to process.");

            int maxGrid = settings.maxGridDimension <= 0
                ? GroundExtractionSettings.kDefaultMaxGridDimension
                : settings.maxGridDimension;
            float percentile = math.clamp(settings.heightPercentile, 0f, 1f);
            float cellSize = math.max(settings.cellSize, 0.01f);
            int stride = math.max(1, settings.splatStride);

            var bounds = ComputeBounds(splats, settings, stride);
            if (bounds.filteredCount == 0)
                throw new InvalidOperationException("No splats passed the opacity threshold.");

            if (settings.useBoundsOverride)
            {
                bounds.min = settings.boundsMinOverride;
                bounds.max = settings.boundsMaxOverride;
            }
            else if (settings.useTightBounds)
            {
                ComputeTightXZBounds(splats, settings, stride, ref bounds);
            }

            float extentX = bounds.max.x - bounds.min.x;
            float extentZ = bounds.max.z - bounds.min.z;
            if (extentX < cellSize || extentZ < cellSize)
                throw new InvalidOperationException("Region bounds are too small for the chosen cell size.");

            int gridW = math.max(1, (int)math.ceil(extentX / cellSize));
            int gridH = math.max(1, (int)math.ceil(extentZ / cellSize));

            if (gridW > maxGrid || gridH > maxGrid)
            {
                float scale = math.max(gridW / (float)maxGrid, gridH / (float)maxGrid);
                cellSize *= scale;
                gridW = math.max(1, (int)math.ceil(extentX / cellSize));
                gridH = math.max(1, (int)math.ceil(extentZ / cellSize));
                Debug.LogWarning(
                    $"Ground collision grid capped to {maxGrid}x{maxGrid}. Cell size increased to {cellSize:F3}m.");
            }

            var origin = new Vector3(bounds.min.x, 0f, bounds.min.z);
            int gridCells = gridW * gridH;
            int buckets = GroundExtractionSettings.kHistogramBuckets;

            var histogram = new NativeArray<int>(gridCells * buckets, Allocator.TempJob);
            var cellCounts = new NativeArray<int>(gridCells, Allocator.TempJob);

            try
            {
                for (int i = 0; i < histogram.Length; ++i)
                    histogram[i] = 0;
                for (int i = 0; i < cellCounts.Length; ++i)
                    cellCounts[i] = 0;

                float yMin = bounds.min.y;
                float yMax = bounds.max.y;
                float yRange = math.max(yMax - yMin, 1e-4f);

                unsafe
                {
                    var histJob = new AccumulateHistogramJob
                    {
                        splats = splats,
                        opacityThreshold = settings.opacityThreshold,
                        transform = settings.transform,
                        applyTransform = settings.applyTransform,
                        boundsMin = bounds.min,
                        boundsMax = bounds.max,
                        gridWidth = gridW,
                        gridHeight = gridH,
                        cellSize = cellSize,
                        stride = stride,
                        yMin = yMin,
                        yInvRange = buckets / yRange,
                        bucketCount = buckets,
                        histogram = (int*)histogram.GetUnsafePtr(),
                        cellCounts = (int*)cellCounts.GetUnsafePtr()
                    };
                    int jobLength = (splats.Length + stride - 1) / stride;
                    histJob.Schedule(jobLength, 2048).Complete();
                }

                var heights = new float[gridCells];
                var valid = new bool[gridCells];
                ComputeHeightsFromHistogram(histogram, cellCounts, gridCells, buckets, percentile, yMin, yMax, heights, valid);

                int validCells = 0;
                for (int i = 0; i < valid.Length; ++i)
                {
                    if (valid[i])
                        validCells++;
                }

                if (validCells == 0)
                    throw new InvalidOperationException("No splats mapped to the ground grid. Try lowering opacity threshold or enlarging region.");

                return new HeightfieldData
                {
                    width = gridW,
                    height = gridH,
                    origin = origin,
                    cellSize = cellSize,
                    heights = heights,
                    valid = valid
                };
            }
            finally
            {
                if (histogram.IsCreated)
                    histogram.Dispose();
                if (cellCounts.IsCreated)
                    cellCounts.Dispose();
            }
        }

        static void ComputeHeightsFromHistogram(NativeArray<int> histogram, NativeArray<int> cellCounts, int gridCells,
            int buckets, float percentile, float yMin, float yMax, float[] heights, bool[] valid)
        {
            float yRange = yMax - yMin;
            for (int cell = 0; cell < gridCells; ++cell)
            {
                int count = cellCounts[cell];
                if (count <= 0)
                    continue;

                int target = math.max(1, (int)math.ceil(percentile * count));
                int seen = 0;
                int histOffset = cell * buckets;
                for (int b = 0; b < buckets; ++b)
                {
                    seen += histogram[histOffset + b];
                    if (seen >= target)
                    {
                        float t = buckets > 1 ? b / (float)(buckets - 1) : 0f;
                        heights[cell] = yMin + t * yRange;
                        valid[cell] = true;
                        break;
                    }
                }
            }
        }

        static void ComputeTightXZBounds(NativeArray<InputSplatData> splats, GroundExtractionSettings settings, int stride,
            ref BoundsResult bounds)
        {
            int sampleCap = GroundExtractionSettings.kTightBoundsSampleCap;
            var xs = new NativeList<float>(sampleCap, Allocator.Temp);
            var zs = new NativeList<float>(sampleCap, Allocator.Temp);
            try
            {
                for (int i = 0; i < splats.Length; i += stride)
                {
                    var splat = splats[i];
                    if (splat.opacity < settings.opacityThreshold)
                        continue;

                    float3 pos = splat.pos;
                    if (settings.applyTransform)
                        pos = math.mul(settings.transform, new float4(pos, 1f)).xyz;

                    xs.Add(pos.x);
                    zs.Add(pos.z);
                    if (xs.Length >= sampleCap)
                        break;
                }

                if (xs.Length < 16)
                    return;

                var xArray = xs.AsArray();
                var zArray = zs.AsArray();
                xArray.Sort();
                zArray.Sort();

                float p = math.clamp(settings.tightBoundsPercentile, 0f, 0.49f);
                int low = (int)math.floor(p * (xs.Length - 1));
                int high = (int)math.floor((1f - p) * (xs.Length - 1));

                bounds.min.x = xArray[low];
                bounds.max.x = xArray[high];
                bounds.min.z = zArray[low];
                bounds.max.z = zArray[high];
            }
            finally
            {
                if (xs.IsCreated)
                    xs.Dispose();
                if (zs.IsCreated)
                    zs.Dispose();
            }
        }

        static BoundsResult ComputeBounds(NativeArray<InputSplatData> splats, GroundExtractionSettings settings, int stride)
        {
            var mins = new NativeArray<float3>((splats.Length + stride - 1) / stride, Allocator.TempJob);
            var maxs = new NativeArray<float3>(mins.Length, Allocator.TempJob);
            var counts = new NativeArray<int>(mins.Length, Allocator.TempJob);

            try
            {
                var boundsJob = new FilterBoundsJob
                {
                    splats = splats,
                    opacityThreshold = settings.opacityThreshold,
                    transform = settings.transform,
                    applyTransform = settings.applyTransform,
                    stride = stride,
                    mins = mins,
                    maxs = maxs,
                    counts = counts
                };
                boundsJob.Schedule(mins.Length, 1024).Complete();

                var result = new BoundsResult
                {
                    min = new float3(float.PositiveInfinity),
                    max = new float3(float.NegativeInfinity),
                    filteredCount = 0
                };

                for (int i = 0; i < mins.Length; ++i)
                {
                    if (counts[i] == 0)
                        continue;

                    result.filteredCount++;
                    result.min = math.min(result.min, mins[i]);
                    result.max = math.max(result.max, maxs[i]);
                }

                return result;
            }
            finally
            {
                mins.Dispose();
                maxs.Dispose();
                counts.Dispose();
            }
        }

        [BurstCompile]
        struct FilterBoundsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> splats;
            public float opacityThreshold;
            public float4x4 transform;
            public bool applyTransform;
            public int stride;
            [WriteOnly] public NativeArray<float3> mins;
            [WriteOnly] public NativeArray<float3> maxs;
            [WriteOnly] public NativeArray<int> counts;

            public void Execute(int index)
            {
                int src = index * stride;
                if (src >= splats.Length)
                {
                    counts[index] = 0;
                    mins[index] = new float3(float.PositiveInfinity);
                    maxs[index] = new float3(float.NegativeInfinity);
                    return;
                }

                var splat = splats[src];
                if (splat.opacity < opacityThreshold)
                {
                    counts[index] = 0;
                    mins[index] = new float3(float.PositiveInfinity);
                    maxs[index] = new float3(float.NegativeInfinity);
                    return;
                }

                float3 pos = splat.pos;
                if (applyTransform)
                    pos = math.mul(transform, new float4(pos, 1f)).xyz;

                counts[index] = 1;
                mins[index] = pos;
                maxs[index] = pos;
            }
        }

        struct AccumulateHistogramJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> splats;
            public float opacityThreshold;
            public float4x4 transform;
            public bool applyTransform;
            public float3 boundsMin;
            public float3 boundsMax;
            public int gridWidth;
            public int gridHeight;
            public float cellSize;
            public int stride;
            public float yMin;
            public float yInvRange;
            public int bucketCount;
            [NativeDisableUnsafePtrRestriction] public unsafe int* histogram;
            [NativeDisableUnsafePtrRestriction] public unsafe int* cellCounts;

            public unsafe void Execute(int index)
            {
                int src = index * stride;
                if (src >= splats.Length)
                    return;

                var splat = splats[src];
                if (splat.opacity < opacityThreshold)
                    return;

                float3 pos = splat.pos;
                if (applyTransform)
                    pos = math.mul(transform, new float4(pos, 1f)).xyz;

                if (pos.x < boundsMin.x || pos.x > boundsMax.x || pos.z < boundsMin.z || pos.z > boundsMax.z)
                    return;

                int cellX = (int)math.floor((pos.x - boundsMin.x) / cellSize);
                int cellZ = (int)math.floor((pos.z - boundsMin.z) / cellSize);
                if (cellX < 0 || cellZ < 0 || cellX >= gridWidth || cellZ >= gridHeight)
                    return;

                int cell = cellZ * gridWidth + cellX;
                int bucket = (int)math.floor((pos.y - yMin) * yInvRange);
                bucket = math.clamp(bucket, 0, bucketCount - 1);

                System.Threading.Interlocked.Increment(ref cellCounts[cell]);
                System.Threading.Interlocked.Increment(ref histogram[cell * bucketCount + bucket]);
            }
        }
    }
}
