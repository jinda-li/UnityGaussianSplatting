using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // GPU (uint key, uint payload) sorting, with two selectable implementations:
    //
    // - DeviceRadixSort: 8 bit-LSD radix sort, using reduce-then-scan.
    //   Copyright Thomas Smith 2024, MIT license, https://github.com/b0nes164/GPUSorting
    //   Fast, but relies heavily on wave intrinsics (SM 6.0 / DXC) which miscompile on
    //   some mobile Vulkan drivers (e.g. Quest / Adreno).
    // - FidelityFX: 4 bit-LSD radix sort derived from AMD FidelityFX-ParallelSort v1.1.1.
    //   Copyright © 2023 Advanced Micro Devices, Inc., MIT license.
    //   Only needs basic wave ops; slower on desktop but reliable on mobile GPUs.
    public class GpuSorting
    {
        public enum SortType
        {
            DeviceRadixSort,
            FidelityFX,
        }

        // DeviceRadixSort constants
        const uint DEVICE_RADIX_SORT_PARTITION_SIZE = 3840;
        const uint DEVICE_RADIX_SORT_BITS = 8;
        const uint DEVICE_RADIX_SORT_RADIX = 256;
        const uint DEVICE_RADIX_SORT_PASSES = 4;

        // FidelityFX constants, these need to match the compute shader
        const uint FFX_ELEMENTS_PER_THREAD = 4;
        const uint FFX_THREADGROUP_SIZE = 128;
        const int FFX_SORT_BITS_PER_PASS = 4;
        const uint FFX_SORT_BIN_COUNT = 1u << FFX_SORT_BITS_PER_PASS;
        // The maximum number of thread groups to run in parallel. Modifying this value can help or hurt GPU occupancy,
        // but is very hardware class specific
        const uint FFX_MAX_THREADGROUPS_TO_RUN = 800;

        //Keywords to enable for the DeviceRadixSort shader
        LocalKeyword m_keyUintKeyword;
        LocalKeyword m_payloadUintKeyword;
        LocalKeyword m_ascendKeyword;
        LocalKeyword m_sortPairKeyword;
        LocalKeyword m_vulkanKeyword;

        public struct Args
        {
            public uint             count;
            public GraphicsBuffer   inputKeys;
            public GraphicsBuffer   inputValues;
            public SupportResources resources;
            internal int workGroupCount;
        }

        public struct SupportResources
        {
            public GraphicsBuffer altBuffer;
            public GraphicsBuffer altPayloadBuffer;
            public GraphicsBuffer passHistBuffer;
            public GraphicsBuffer globalHistBuffer;

            public static SupportResources Load(uint count) => Load(count, SortType.DeviceRadixSort);

            public static SupportResources Load(uint count, SortType type)
            {
                uint scratchBufferSize, reducedScratchBufferSize;
                if (type == SortType.DeviceRadixSort)
                {
                    //This is threadBlocks * DEVICE_RADIX_SORT_RADIX
                    scratchBufferSize = DivRoundUp(count, DEVICE_RADIX_SORT_PARTITION_SIZE) * DEVICE_RADIX_SORT_RADIX;
                    reducedScratchBufferSize = DEVICE_RADIX_SORT_RADIX * DEVICE_RADIX_SORT_PASSES;
                }
                else
                {
                    uint blockSize = FFX_ELEMENTS_PER_THREAD * FFX_THREADGROUP_SIZE;
                    uint numBlocks = DivRoundUp(count, blockSize);
                    uint numReducedBlocks = DivRoundUp(numBlocks, blockSize);
                    scratchBufferSize = FFX_SORT_BIN_COUNT * numBlocks;
                    reducedScratchBufferSize = FFX_SORT_BIN_COUNT * numReducedBlocks;
                }

                var target = GraphicsBuffer.Target.Structured;
                var resources = new SupportResources
                {
                    altBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "GpuSortAlt" },
                    altPayloadBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "GpuSortAltPayload" },
                    passHistBuffer = new GraphicsBuffer(target, (int)scratchBufferSize, 4) { name = "GpuSortPassHistogram" },
                    globalHistBuffer = new GraphicsBuffer(target, (int)reducedScratchBufferSize, 4) { name = "GpuSortGlobalHistogram" },
                };
                return resources;
            }

            public void Dispose()
            {
                altBuffer?.Dispose();
                altPayloadBuffer?.Dispose();
                passHistBuffer?.Dispose();
                globalHistBuffer?.Dispose();

                altBuffer = null;
                altPayloadBuffer = null;
                passHistBuffer = null;
                globalHistBuffer = null;
            }
        }

        readonly ComputeShader m_CS;
        readonly SortType m_SortType;

        // DeviceRadixSort kernels
        readonly int m_kernelInitDeviceRadixSort = -1;
        readonly int m_kernelUpsweep = -1;
        readonly int m_kernelScan = -1;
        readonly int m_kernelDownsweep = -1;

        // FidelityFX kernels
        readonly int m_kernelFfxCount = -1;
        readonly int m_kernelFfxReduce = -1;
        readonly int m_kernelFfxScan = -1;
        readonly int m_kernelFfxScanAdd = -1;
        readonly int m_kernelFfxScatter = -1;

        readonly bool m_Valid;

        public bool Valid => m_Valid;
        public SortType sortType => m_SortType;

        public GpuSorting(ComputeShader cs) : this(cs, SortType.DeviceRadixSort)
        {
        }

        public GpuSorting(ComputeShader cs, SortType type)
        {
            m_CS = cs;
            m_SortType = type;
            if (type == SortType.DeviceRadixSort)
            {
                if (cs)
                {
                    if (cs.HasKernel("InitDeviceRadixSort")) m_kernelInitDeviceRadixSort = cs.FindKernel("InitDeviceRadixSort");
                    if (cs.HasKernel("Upsweep")) m_kernelUpsweep = cs.FindKernel("Upsweep");
                    if (cs.HasKernel("Scan")) m_kernelScan = cs.FindKernel("Scan");
                    if (cs.HasKernel("Downsweep")) m_kernelDownsweep = cs.FindKernel("Downsweep");
                }

                m_Valid = m_kernelInitDeviceRadixSort >= 0 &&
                          m_kernelUpsweep >= 0 &&
                          m_kernelScan >= 0 &&
                          m_kernelDownsweep >= 0;
                if (m_Valid)
                {
                    if (!cs.IsSupported(m_kernelInitDeviceRadixSort) ||
                        !cs.IsSupported(m_kernelUpsweep) ||
                        !cs.IsSupported(m_kernelScan) ||
                        !cs.IsSupported(m_kernelDownsweep))
                    {
                        m_Valid = false;
                    }
                }

                if (m_Valid)
                {
                    m_keyUintKeyword = new LocalKeyword(cs, "KEY_UINT");
                    m_payloadUintKeyword = new LocalKeyword(cs, "PAYLOAD_UINT");
                    m_ascendKeyword = new LocalKeyword(cs, "SHOULD_ASCEND");
                    m_sortPairKeyword = new LocalKeyword(cs, "SORT_PAIRS");
                    m_vulkanKeyword = new LocalKeyword(cs, "VULKAN");

                    cs.EnableKeyword(m_keyUintKeyword);
                    cs.EnableKeyword(m_payloadUintKeyword);
                    cs.EnableKeyword(m_ascendKeyword);
                    cs.EnableKeyword(m_sortPairKeyword);
                    if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan)
                        cs.EnableKeyword(m_vulkanKeyword);
                    else
                        cs.DisableKeyword(m_vulkanKeyword);
                }
            }
            else
            {
                if (cs)
                {
                    if (cs.HasKernel("FfxParallelSortCount")) m_kernelFfxCount = cs.FindKernel("FfxParallelSortCount");
                    if (cs.HasKernel("FfxParallelSortReduce")) m_kernelFfxReduce = cs.FindKernel("FfxParallelSortReduce");
                    if (cs.HasKernel("FfxParallelSortScan")) m_kernelFfxScan = cs.FindKernel("FfxParallelSortScan");
                    if (cs.HasKernel("FfxParallelSortScanAdd")) m_kernelFfxScanAdd = cs.FindKernel("FfxParallelSortScanAdd");
                    if (cs.HasKernel("FfxParallelSortScatter")) m_kernelFfxScatter = cs.FindKernel("FfxParallelSortScatter");
                }

                m_Valid = m_kernelFfxCount >= 0 &&
                          m_kernelFfxReduce >= 0 &&
                          m_kernelFfxScan >= 0 &&
                          m_kernelFfxScanAdd >= 0 &&
                          m_kernelFfxScatter >= 0;
                if (m_Valid)
                {
                    if (!cs.IsSupported(m_kernelFfxCount) ||
                        !cs.IsSupported(m_kernelFfxReduce) ||
                        !cs.IsSupported(m_kernelFfxScan) ||
                        !cs.IsSupported(m_kernelFfxScanAdd) ||
                        !cs.IsSupported(m_kernelFfxScatter))
                    {
                        m_Valid = false;
                    }
                }
            }
        }

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        public void Dispatch(CommandBuffer cmd, Args args)
        {
            Assert.IsTrue(Valid);
            if (m_SortType == SortType.DeviceRadixSort)
                DispatchDeviceRadixSort(cmd, args);
            else
                DispatchFfx(cmd, args);
        }

        void DispatchDeviceRadixSort(CommandBuffer cmd, Args args)
        {
            GraphicsBuffer srcKeyBuffer = args.inputKeys;
            GraphicsBuffer srcPayloadBuffer = args.inputValues;
            GraphicsBuffer dstKeyBuffer = args.resources.altBuffer;
            GraphicsBuffer dstPayloadBuffer = args.resources.altPayloadBuffer;

            uint numKeys = args.count;
            uint threadBlocks = DivRoundUp(args.count, DEVICE_RADIX_SORT_PARTITION_SIZE);

            // Setup overall constants
            cmd.SetComputeIntParam(m_CS, "e_numKeys", (int)numKeys);
            cmd.SetComputeIntParam(m_CS, "e_threadBlocks", (int)threadBlocks);

            //Set statically located buffers
            //Upsweep
            cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_passHist", args.resources.passHistBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_globalHist", args.resources.globalHistBuffer);

            //Scan
            cmd.SetComputeBufferParam(m_CS, m_kernelScan, "b_passHist", args.resources.passHistBuffer);

            //Downsweep
            cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_passHist", args.resources.passHistBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_globalHist", args.resources.globalHistBuffer);

            //Clear the global histogram
            cmd.SetComputeBufferParam(m_CS, m_kernelInitDeviceRadixSort, "b_globalHist", args.resources.globalHistBuffer);
            cmd.DispatchCompute(m_CS, m_kernelInitDeviceRadixSort, 1, 1, 1);

            // Execute the sort algorithm in 8-bit increments
            for (uint radixShift = 0; radixShift < 32; radixShift += DEVICE_RADIX_SORT_BITS)
            {
                cmd.SetComputeIntParam(m_CS, "e_radixShift", (int)radixShift);

                //Upsweep
                cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_sort", srcKeyBuffer);
                cmd.DispatchCompute(m_CS, m_kernelUpsweep, (int)threadBlocks, 1, 1);

                // Scan
                cmd.DispatchCompute(m_CS, m_kernelScan, (int)DEVICE_RADIX_SORT_RADIX, 1, 1);

                // Downsweep
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sort", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sortPayload", srcPayloadBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_alt", dstKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_altPayload", dstPayloadBuffer);
                cmd.DispatchCompute(m_CS, m_kernelDownsweep, (int)threadBlocks, 1, 1);

                // Swap
                (srcKeyBuffer, dstKeyBuffer) = (dstKeyBuffer, srcKeyBuffer);
                (srcPayloadBuffer, dstPayloadBuffer) = (dstPayloadBuffer, srcPayloadBuffer);
            }
        }

        void DispatchFfx(CommandBuffer cmd, Args args)
        {
            GraphicsBuffer srcKeyBuffer = args.inputKeys;
            GraphicsBuffer srcPayloadBuffer = args.inputValues;
            GraphicsBuffer dstKeyBuffer = args.resources.altBuffer;
            GraphicsBuffer dstPayloadBuffer = args.resources.altPayloadBuffer;

            uint blockSize = FFX_ELEMENTS_PER_THREAD * FFX_THREADGROUP_SIZE;
            uint numBlocks = DivRoundUp(args.count, blockSize);

            // Figure out data distribution
            uint numThreadGroupsToRun = FFX_MAX_THREADGROUPS_TO_RUN;
            uint blocksPerThreadGroup = numBlocks / numThreadGroupsToRun;
            uint numThreadGroupsWithAdditionalBlocks = numBlocks % numThreadGroupsToRun;

            if (numBlocks < numThreadGroupsToRun)
            {
                blocksPerThreadGroup = 1;
                numThreadGroupsToRun = numBlocks;
                numThreadGroupsWithAdditionalBlocks = 0;
            }

            // Calculate the number of thread groups to run for reduction (each thread group can process blockSize number of entries)
            uint numReducedThreadGroupsToRun = FFX_SORT_BIN_COUNT * ((blockSize > numThreadGroupsToRun) ? 1 : (numThreadGroupsToRun + blockSize - 1) / blockSize);

            // Setup overall constants
            cmd.SetComputeIntParam(m_CS, "numKeys", (int)args.count);
            cmd.SetComputeIntParam(m_CS, "numBlocksPerThreadGroup", (int)blocksPerThreadGroup);
            cmd.SetComputeIntParam(m_CS, "numThreadGroups", (int)numThreadGroupsToRun);
            cmd.SetComputeIntParam(m_CS, "numThreadGroupsWithAdditionalBlocks", (int)numThreadGroupsWithAdditionalBlocks);
            cmd.SetComputeIntParam(m_CS, "numReduceThreadgroupPerBin", (int)(numReducedThreadGroupsToRun / FFX_SORT_BIN_COUNT));
            cmd.SetComputeIntParam(m_CS, "numScanValues", (int)numReducedThreadGroupsToRun);

            // Execute the sort algorithm in 4-bit increments
            for (int shift = 0; shift < 32; shift += FFX_SORT_BITS_PER_PASS)
            {
                cmd.SetComputeIntParam(m_CS, "shift", shift);

                // Sum
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxCount, "rw_source_keys", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxCount, "rw_sum_table", args.resources.passHistBuffer);
                cmd.DispatchCompute(m_CS, m_kernelFfxCount, (int)numThreadGroupsToRun, 1, 1);

                // Reduce
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxReduce, "rw_sum_table", args.resources.passHistBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxReduce, "rw_reduce_table", args.resources.globalHistBuffer);
                cmd.DispatchCompute(m_CS, m_kernelFfxReduce, (int)numReducedThreadGroupsToRun, 1, 1);

                // Scan
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScan, "rw_scan_source", args.resources.globalHistBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScan, "rw_scan_dest", args.resources.globalHistBuffer);
                cmd.DispatchCompute(m_CS, m_kernelFfxScan, 1, 1, 1);

                // Scan add
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScanAdd, "rw_scan_source", args.resources.passHistBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScanAdd, "rw_scan_dest", args.resources.passHistBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScanAdd, "rw_scan_scratch", args.resources.globalHistBuffer);
                cmd.DispatchCompute(m_CS, m_kernelFfxScanAdd, (int)numReducedThreadGroupsToRun, 1, 1);

                // Scatter
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScatter, "rw_source_keys", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScatter, "rw_dest_keys", dstKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScatter, "rw_sum_table", args.resources.passHistBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScatter, "rw_source_payloads", srcPayloadBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelFfxScatter, "rw_dest_payloads", dstPayloadBuffer);
                cmd.DispatchCompute(m_CS, m_kernelFfxScatter, (int)numThreadGroupsToRun, 1, 1);

                // Swap
                (srcKeyBuffer, dstKeyBuffer) = (dstKeyBuffer, srcKeyBuffer);
                (srcPayloadBuffer, dstPayloadBuffer) = (dstPayloadBuffer, srcPayloadBuffer);
            }
        }
    }
}
