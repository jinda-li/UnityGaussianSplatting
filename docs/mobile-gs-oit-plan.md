# 计划：用 Mobile-GS 的顺序无关渲染（OIT）解决 Quest 排序瓶颈

状态：**Phase 1 已结案；当前主线改为 MCMC 400k 重训（见下「决策」）**
参考论文：[Mobile-GS: Real-time Gaussian Splatting for Mobile Devices](https://arxiv.org/pdf/2603.11531)（ICLR 2026）
参考实现：[xiaobiaodu/Mobile-GS](https://github.com/xiaobiaodu/Mobile-GS) → `submodules/diff-gaussian-rasterization_ms_nosorting`（`precomputeGaussianData` / `renderTileOIT`）
背景：此前的 Quest 原生 splat 渲染调研结论——本地 v1.1.1 的 GPU radix sort 在 Quest 上不可用；正在做的 FidelityFX 排序移植（未提交的 `GpuSortFidelityFX.hlsl` / `SplatUtilitiesFfx.compute` 等文件）是"让排序能跑"的方案，本计划评估的是"直接不排序"的替代方案。

## 决策（2026-07-17）

Phase 1 在 Unity 上验证后：**帧率仍然太低，overdraw 严重**；无法复刻论文的 **Vulkan tile-based** 渲染器。Profiler 表明 **瓶颈不在排序**（PC 上 Sort ~1.5 ms；开 OIT 后 Draw 更贵）。

因此：

1. **暂缓** Mobile-GS Phase 2（Mini-Splatting → 60k OIT 微调 → SH 蒸馏/量化/剪枝）。
2. **先做** [MCMC 400k 重训](../projects/GaussianExample-URP/docs/plans/2026-07-17-mcmc-400k-retrain.md)：把 Botanical Garden 压成小规模 3DGS（200k / 400k 硬帽），在现有 Unity 路径上试 Quest 可用性。
3. 仅当后续改成真正的 tile 光栅、或点数压下来后排序重新成为主矛盾时，再议 OIT / Mobile-GS 完整工作流。

## 论文核心方法（用于对照实现）

用加权顺序无关合成替代 alpha blending，从而**完全跳过深度排序**：

```
C = (1-T) · Σ(cᵢ·αᵢ·wᵢ) / Σ(αᵢ·wᵢ) + T · c_bg
wᵢ = φᵢ² + φᵢ/dᵢ² + exp(s_max/dᵢ)
T  = ∏(1-αⱼ)   // 实现上用 Σ log(1-α) 再 exp
```

- `dᵢ`：深度；`s_max`：最大 splat 尺度；`φᵢ`：per-Gaussian 小 MLP（256→128→64）预测的视角相关标量，用于补偿丢失排序后重叠区域的透明度伪影。
- 配套压缩手段（1 阶 SH 蒸馏、神经向量量化、贡献度剪枝）把模型从 61.8MB 压到 4.6MB，PSNR 基本打平原 3DGS（27.12 vs 27.01）。
- 官方数据：Snapdragon 8 Gen 3、1600×1063，74–127 FPS。
- 局限：**逐场景优化**，不能跨场景泛化；需要在桌面 GPU（论文用 RTX 3090）预训练+微调 60k 迭代；过度量化在纹理丰富区域可能有色偏。

代码现状：官方仓库放出了训练代码和 CUDA 参考渲染器，但移动端 Vulkan 渲染器因公司政策未开源——渲染这块需要我们自己在 URP/Quest 上实现，不能直接照搬。

## Phase 1：低成本验证（不重训）— **已完成并结案**

目的：不碰训练流程，只看"跳过排序、用加权 OIT 直接合成现有 splat 资产"这条路本身在目标平台是否可行——帧率是否真的因排序消失而显著提升，画质伪影是否在可接受范围。

**做法**（对照官方 `diff-gaussian-rasterization_ms_nosorting`）：

1. ~~在渲染器里加一个可切换的 OIT 渲染分支~~ → `GaussianSplatRenderer.m_UseOIT`
2. ~~用简化版权重公式（φ=0）~~ → `w = exp(s_max / d)`，在 `CSCalcViewData` 预计算；MRT 累加 `Σ(c·α·w)` / `Σ(α·w)` / `Σ log(1-α)`，composite 做 `(1-T)·avg + T·bg`
3. 直接喂现有已训练好的 Garden/Forest splat 资产（未针对 OIT 微调过）。
4. ~~PC + Quest 3 Profiler 对比~~（见下）→ **结论：OIT 未赢；主线转 MCMC 减点。**

**怎么开 / 切换**：

- Inspector：`GaussianSplatRenderer` → **Use OIT**
- 运行时（Quest / Editor）：左手柄 **X**（或键盘 **X**）→ `OITModeToggle` 切换；日志 `[OITModeToggle] OIT ON/OFF`

**判定标准**：

- 帧率提升明显 且 伪影可接受 → 继续 Phase 2（值得投入重训成本）。
- 帧率提升不明显 → 排序不是当前的主要瓶颈，回到 FidelityFX 排序移植或找其他优化点。
- 伪影不可接受 且 帧率提升明显 → 有条件继续 Phase 2，但需要评估 MLP 补偿是否能救回画质。

### PC 实测结果（2026-07-17，Editor / D3D12）

截图：[`docs/oit-phase1/pc-profiler-sorted-no-oit.png`](oit-phase1/pc-profiler-sorted-no-oit.png)、[`docs/oit-phase1/pc-profiler-oit.png`](oit-phase1/pc-profiler-oit.png)

| | 不用 OIT（排序） | 用 OIT |
|--|--|--|
| GPU 整帧 | **~23.3 ms**（约 43 FPS） | **~31.9 ms**（约 31 FPS） |
| 相对变化 | — | **更慢约 +8–9 ms（≈ +37%）** |

不用 OIT 时 `GaussianSplatRenderGraph` 拆分：

| 项 | GPU | 占比（相对整帧） |
|--|--|--|
| **Draw** | **8.22 ms** | 35.3%（主成本） |
| **Sort** | **1.52 ms** | 6.5%（很小） |
| CalcView | 1.23 ms | 5.2% |
| Compose | 0.02 ms | 可忽略 |
| GS 合计 | ~11.0 ms | ~47% |

**PC 结论：**

- 在当前 Unity **硬件 quad 光栅**路径上，**Sort 不是瓶颈**，只占一小部分（~1.5 ms）。
- 开 OIT 后省掉 Sort，但 **Draw（光栅 + 双 MRT 累加混合）压力更大**，总 GPU 时间上升；CPU 侧表现为 `WaitForGPU`。

### Quest 3 实测结果（2026-07-17，ADB Profiler）

截图：[`docs/oit-phase1/quest3-profiler-oit.png`](oit-phase1/quest3-profiler-oit.png)（开 OIT）、[`docs/oit-phase1/quest3-profiler-sorted-no-oit.png`](oit-phase1/quest3-profiler-sorted-no-oit.png)（关 OIT）

| | 开 OIT（图1） | 关 OIT / 排序（图2） |
|--|--|--|
| 选中帧 CPU | **~158 ms**（约 6 FPS） | **~124 ms**（约 8 FPS） |
| 相对变化 | **更慢约 +34 ms（≈ +27%）** | 基线（仍远低于 72/90Hz） |
| 主线程大头 | `WaitForLastPresentation…` **~157 ms（93%）** | 同结构 **~116 ms（94%）** |
| 含义 | **严重 GPU-bound** | **同样 GPU-bound** |
| Batches / SetPass | ~44 / ~30 | ~44 / ~30（量级相同） |
| Triangles（Profiler） | ~12.9M | ~12.9M（几何规模同量级） |

说明：这两张是 **CPU 模块**抓的；GPU Usage 显示 `--ms`，没有像 PC 那样拆出 `GaussianSplat.Sort` / `Draw`。但整帧对比已经足够定方向：两边都在等 GPU，且 **开 OIT 更慢**。

**Quest 读法：**

- 若 Sort 是手机上的主瓶颈，去掉排序后整帧应明显下降；实际 **OIT 更差** → 在这条 Unity 路径上，**瓶颈仍是光栅/混合（Draw）**，不是 Sort。
- 与 PC 同构：省排序省得少，双 MRT 全量累加把 Draw 推得更贵。
- 两边都只有个位数 FPS，说明大场景在 Quest 上本身就被 Draw/overdraw 压垮；OIT 没有改写这条成本曲线。

### Quest 3 补充：~200k splats 场景（2026-07-17）

截图：[`docs/oit-phase1/quest3-200k-no-oit.png`](oit-phase1/quest3-200k-no-oit.png)（无 OIT）、[`docs/oit-phase1/quest3-200k-oit.png`](oit-phase1/quest3-200k-oit.png)（有 OIT）

| | 无 OIT（选中帧） | 有 OIT（选中帧） |
|--|--|--|
| CPU | **~47.5 ms**（约 21 FPS） | **~18.1 ms**（按 CPU 折合约 55 FPS） |
| `XRUpdate` | **~37 ms（78%）** | **~11 ms（61%）** |
| `FinishFrameRendering` | ~4.4 ms | ~3.7 ms |
| Triangles 量级 | ~2.7M | ~2.7M |

**读法注意：** Quest 上 `XRUpdate` 往往含 **等 GPU/呈现**；无 OIT 那帧更像尖刺等待。有 OIT 选中帧 CPU 更好看，但 GPU 曲线仍常压在 33 ms 以上，实际观感更接近 **~20–30 FPS 波动**，不能单凭一帧 CPU 断定 OIT 更快。

**与大场景对比：** 200k 比 ~13M tris 大场景（~8 FPS）明显好转（约 20 FPS 量级），仍远未到 72/90Hz；点数下来后主矛盾仍是 Draw，不是“OIT 救了排序”。

### Phase 1 结论（PC + Quest）

按计划判定标准：**在当前 Unity 硬件 quad 光栅路径上，用 OIT 换掉排序未能稳定提速 → 排序不是当前主瓶颈（至少不是用这套 OIT 实现能赢过的瓶颈）。**

| 平台 / 场景 | 不用 OIT | 用 OIT | 结论 |
|--|--|--|--|
| PC（Garden 量级） | ~23 ms | ~32 ms | OIT 更慢；Sort 仅 ~1.5 ms |
| Quest 3 大场景 | ~124 ms | ~158 ms | OIT 更慢；同为 GPU-bound |
| Quest 3 ~200k | ~47 ms 选中帧（≈21 FPS） | ~18 ms CPU 选中帧，但 GPU 仍重 | 单帧对比有噪声；整体仍难舒适 VR，未见稳定 OIT 胜出 |

**结案判定：**

- Unity 硬件 quad 路径帧率过低；半透明 splat **overdraw** 是主因（`ZWrite Off`，无法 early-Z）。
- 论文 **Vulkan tile-based** 渲染器未开源，**无法在现管线复刻**；仅移植 OIT 混合公式不够。
- **瓶颈不在排序** → 不做 Phase 2 Mobile-GS 全流程；先减点再谈渲染器。

### 下一步（已采纳）

- [x] PC Profiler 对比  
- [x] Quest 3 大场景对比  
- [x] Quest 3 ~200k 场景对比  
- [ ] **执行 [MCMC 400k 重训](../projects/GaussianExample-URP/docs/plans/2026-07-17-mcmc-400k-retrain.md)**（当前主线）  
- [ ]（可选，降优先级）FidelityFX 排序移植、tile 光栅调研  
- [ ]（搁置）Mobile-GS Phase 2  

## Phase 2：走完整论文工作流（**搁置**；仅当 tile 光栅落地或减点后排序重新成为瓶颈时再议）

目的：验证"逐场景 OIT 微调 + MLP 补偿 + 压缩"能否把 Phase 1 暴露的伪影问题解决到论文声称的水平（PSNR 基本无损、4.6MB 级别的模型体积）。

**为何现在不做：** Phase 1 已证明在现 Unity 路径上 OIT 不提速；完整训练救不了 overdraw / 无法复刻 Vulkan tile 光栅。优先把植物园训成小 3DGS（MCMC）更直接。

**训练数据**（若日后重启）：`C:\Users\standalone\Documents\3DGS\Botanical Garden - America - Colmap Data`
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
- 若要继续挖 OIT 性能：是否值得做更接近论文的 **tile 光栅**（而非仅改混合公式）？
