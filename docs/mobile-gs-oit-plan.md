# 计划：用 Mobile-GS 的顺序无关渲染（OIT）解决 Quest 排序瓶颈

状态：**规划中，未实现**
参考论文：[Mobile-GS: Real-time Gaussian Splatting for Mobile Devices](https://arxiv.org/pdf/2603.11531)（ICLR 2026）
背景：此前的 Quest 原生 splat 渲染调研结论——本地 v1.1.1 的 GPU radix sort 在 Quest 上不可用；正在做的 FidelityFX 排序移植（未提交的 `GpuSortFidelityFX.hlsl` / `SplatUtilitiesFfx.compute` 等文件）是"让排序能跑"的方案，本计划评估的是"直接不排序"的替代方案。

## 论文核心方法（用于对照实现）

用加权顺序无关合成替代 alpha blending，从而**完全跳过深度排序**：

```
C = (1-T) · Σ(cᵢ·αᵢ·wᵢ) / Σ(αᵢ·wᵢ) + T · c_bg
wᵢ = φᵢ² + φᵢ/dᵢ² + exp(s_max/dᵢ)
T  = ∏(1-αⱼ)
```

- `dᵢ`：深度；`s_max`：最大 splat 尺度；`φᵢ`：per-Gaussian 小 MLP（256→128→64）预测的视角相关标量，用于补偿丢失排序后重叠区域的透明度伪影。
- 配套压缩手段（1 阶 SH 蒸馏、神经向量量化、贡献度剪枝）把模型从 61.8MB 压到 4.6MB，PSNR 基本打平原 3DGS（27.12 vs 27.01）。
- 官方数据：Snapdragon 8 Gen 3、1600×1063，74–127 FPS。
- 局限：**逐场景优化**，不能跨场景泛化；需要在桌面 GPU（论文用 RTX 3090）预训练+微调 60k 迭代；过度量化在纹理丰富区域可能有色偏。

代码现状：官方仓库（[xiaobiaodu/Mobile-GS](https://github.com/xiaobiaodu/mobile-gs)）放出了训练代码和 CUDA 参考渲染器，但移动端 Vulkan 渲染器因公司政策未开源——渲染这块需要我们自己在 URP/Quest 上实现，不能直接照搬。

## Phase 1：低成本验证（不重训，1~2 天）

目的：不碰训练流程，只看"跳过排序、用加权 OIT 直接合成现有 splat 资产"这条路本身在 Quest 上是否可行——帧率是否真的因排序消失而显著提升，画质伪影是否在可接受范围。

**做法**：

1. 在渲染器里加一个可切换的 OIT 渲染分支（不影响现有排序渲染路径），跳过 [GpuSorting.cs](../package/Runtime/GpuSorting.cs) 的 dispatch。
2. 用简化版权重公式（先不训练 MLP，`φᵢ` 可先固定为 0 或用已有的 opacity 近似）在 shader 里做加权累加 + 归一化合成，替代现有的 sorted alpha blending。
3. 直接喂现有已训练好的 Garden/Forest splat 资产（未针对 OIT 微调过），预期会看到论文里提到的"重叠区域透明度伪影"——这是本阶段要观察和评估的重点，不是 bug。
4. Quest 3 真机测帧率（对比现有排序管线）和主观画质（伪影是否可忍受，尤其是植被/树叶这类高重叠密度区域）。

**判定标准**：

- 帧率提升明显 且 伪影可接受 → 继续 Phase 2（值得投入重训成本）。
- 帧率提升不明显 → 排序不是当前的主要瓶颈，回到 FidelityFX 排序移植或找其他优化点。
- 伪影不可接受 且 帧率提升明显 → 有条件继续 Phase 2，但需要评估 MLP 补偿是否能救回画质。

**若 Phase 1 成立**：正在做的 FidelityFX 排序移植（`GpuSortFidelityFX.hlsl`、`SplatUtilitiesFfx.compute`、`SplatUtilitiesBody.hlsl` 等未提交文件）大概率不再需要——排序管线整体可以被 OIT 分支替代，而不是优化排序本身。

## Phase 2：走完整论文工作流（视 Phase 1 结果决定是否启动）

目的：验证"逐场景 OIT 微调 + MLP 补偿 + 压缩"能否把 Phase 1 暴露的伪影问题解决到论文声称的水平（PSNR 基本无损、4.6MB 级别的模型体积）。

**训练数据**：`C:\Users\standalone\Documents\3DGS\Botanical Garden - America - Colmap Data`
（Botanical Garden 场景，550 张照片 + COLMAP 稀疏重建结果 `Cameras.txt` / `Images.txt` / `Points3D.txt`）

**流程**（对照论文 Section 3.4 / 官方仓库 README）：

1. Mini-Splatting 预训练（作为 teacher model）。
2. OIT-aware 微调，60k 迭代，损失函数 `L_rgb + λ_distill·L_distill + λ_depth·L_depth`，35k 迭代起启动神经向量量化。
3. 蒸馏到 1 阶 SH + 剪枝，导出压缩后的 PLY。
4. 把训练产物接回 Unity 渲染器（Phase 1 的 OIT 渲染分支），实机对比压缩前后的帧率/体积/画质。

**前提**：需要一台桌面级 GPU（论文用 RTX 3090）跑训练；训练代码是官方开源的 CUDA 版本，需要先跑通环境（Python/PyTorch + CUDA 扩展编译）。

**开放问题**（留到 Phase 2 启动时再定）：

- Garden/Forest 现有场景是否也要走一遍重训，还是先只用 Botanical Garden 这份新数据验证方法本身？
- 训练产物如何接入现有 GaussianSplatAsset 导入管线（是否需要写新的 importer，还是复用现有 PLY 导入路径）？
- 官方 MLP（φᵢ 预测）在移动端如何跑：per-Gaussian 低频更新还是预烘焙近似？

## 下一步

先做 Phase 1，验证结果视具体数据决定是否启动 Phase 2。本文档目前只记录计划，两个阶段均未开始实现。
