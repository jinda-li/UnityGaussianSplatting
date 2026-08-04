# 大型 Gaussian Splat 资产自动切分方案（绕过 GraphicsBuffer 128 MB 上限）

状态：**降级为兜底方案**（2026-07-30）。总路线图见 §0.6——本文档是其中的第 5 步，
且**若第 2 步的导入期剪枝把资产降到 128 MB 上限内即可整体归档**。
Stage C（整块视锥剔除）已被 per-splat GPU culling 取代（§0.5.3）。Stage A 仍建议立即做。
日期：2026-07-24，修订 2026-07-30
目标读者：任何实现者（人或 AI）。本文档包含全部背景、精确的文件/函数落点、算法、代码骨架、坑位说明和验收测试。按顺序实现即可运行。

## 0. 定位声明（2026-07-30 修订，先读这段）

**切分本身不是性能优化，不要指望它提帧率。**

| 维度 | 切分的影响 |
|---|---|
| overdraw / fill rate | **完全不变**。同样的 splat 覆盖同样的像素，alpha blend 开销一模一样（§0.3.1 实测显示这已不是主瓶颈） |
| 排序总计算量 | 不做剔除时不变（radix sort 是 O(n)，N 片总和相同）；**做剔除后按剔除率线性下降** ——§0.3.1 实测排序占 GPU 时间 ~50%，这是收益所在 |
| dispatch / draw call 固定开销 | ×N，但 §0.2 实测 CPU 侧有大量空闲余量，**实际可忽略** |
| 显存带宽 / cache miss | 不变（除非整片被剔除）——§0.2 显示这可能才是主因 |
| GPU 总显存 | 基本不变（略增，每片有独立 sort scratch） |
| 单 buffer 上限 | **这是唯一确定的收益**：让 5.8M 资产能创建成功，不再全黑 |

~~已知 Quest 3 卡顿根因是 GPU overdraw~~ —— **此结论已被 §0.3.1 的实测推翻**。
2026-07-30 真机 A/B 显示：**排序约占 GPU 时间的 50%（≈16.2 ms / 32.3 ms）**，
而降分辨率与缩小 splat 覆盖面积均无可测收益。overdraw 曾是根因（2026-07-17），
但该场景已应用过降 overdraw 的手段（`SHOrder=0`、`SplatScale=0.60`），排序已取而代之。

真正的性能收益来自 **§6 的整块视锥剔除**——它省掉的正是不可见片的排序，
直接命中最大的那一项。收益大小仍取决于视角与分片粒度（见 §3.2.1）。

**本文档的切分方案已从「主线」降为「兜底」**，完整优先级见 §0.6 的总路线图。
一句话结论：**若 §0.6 第 2 步的导入期剪枝把资产降到 128 MB 上限内，本方案可以整体归档。**

| 阶段 | 内容 | 前置条件 | 预期收益 |
|---|---|---|---|
| **Stage A** | §4 运行时防御检查（约 10 行） | 无，立即可做 | 消除全黑故障，一条清晰报错代替每帧异常 |
| **Stage B** | §3 导入期切分 + prefab | Stage A；**且剪枝后仍超 128 MB** | 让超限资产能跑；性能中性 |
| **Stage C** | ~~§6.1 整块视锥剔除~~ | — | **已被 §0.5.3 的 per-splat GPU culling 取代**（粒度更细且不需要切分） |

**Stage B 单独实施是零收益**（性能中性，见 §0.2 结论 2；只换来"能跑起来"）。

## 0.6 总路线图（2026-07-30 定稿，本文档在其中的位置）

瓶颈判断分两层，不要混为一谈：

- **500K 场景只有 20 fps → 瓶颈是排序，且大部分是自造的**：
  FFX 每 pass 仅 4 bits、32-bit key 需 **8 趟**、约 24 次 dispatch（`GpuSorting.cs:33,309`）。
  实测排序 ≈16.2 ms / 总 32.3 ms，而按带宽估算理论仅约 1 ms——**十几倍的浪费在 pass 数与 dispatch 上**。
  （注：这 16.2 ms 是单次排序成本；`m_VRSortOnceBothEyes` 在 Single Pass Instanced 下不生效，见 §0.3.4。）
- **7M 场景是预算严重超标**：超 128 MB 单 buffer 上限根本进不去，GPU 内存亦已 100% 满载（§0.4）。
  任何排序优化都与之无关，只能靠剪枝 / LOD。

| # | 改动 | 工作量 | 预期收益 | 风险 |
|---|---|---|---|---|
| **0** | `m_SortMethod` Auto → `DeviceRadixSort`（§0.3.4） | ✅ **已完成 2026-08-04** | pass 8→4；实测更快、穿帮更少 | 已验证低风险 |
| **1** | 排序 key 改 16-bit（DeviceRadixSort 4→2 pass） | ✅ **代码已完成 2026-08-04，待真机验证**（§0.5.0） | 剩余排序再减半 | 低；桶内同深度顺序随机，视觉无差别；已核查 pass-位置索引与 ascending-only 路径安全 |
| **2** | **导入期剪枝**（低 opacity / 超大投影面积 / 离群点不写入资产） | 1–2 天 | 砍 40–60% splat，**排序+光栅+显存+带宽同时降** | 中；需调阈值+目视验证，重新导入即可回滚 |
| **3** | per-splat GPU culling（compute 加视锥判定 + 流压缩，§0.5.3） | 2–3 天 | 视场外 splat 不排序不光栅 | 中；SuperSplat 二代核心，**不需要资产切分** |
| **4** | 自适应排序（相机位姿阈值触发重排，替代 `m_SortNthFrame` 死间隔） | 半天 | 静止时省掉大部分排序 | 低；VR 头部微动会削弱收益，旋转阈值应比平移放宽 |
| **5** | **本文档的资产切分（Stage A + B）** | 3–5 天 | 仅解决 >128 MB 上限 | 中；**第 2 步若把总量降到上限内，本步可跳过** |
| **6** | 去掉 view buffer（40 B/splat 的写+读） | 未评估 | 省大量带宽 | 高；与 paint mode / StylizedSplats 冲突，且 multipass 双眼需各算一次 |

**先做 0 和 1**：合计不到一天，预期 32.3 ms → ~20 ms（20 fps → ~33 fps）。

**第 2 步是分水岭**：剪枝后若 7M 降到 3M 以内，128 MB 上限问题自然消失，
本文档（第 5 步）可整体归档。这也是 SuperSplat 的产品思路——
其 Editor 的核心功能不是渲染而是**删 splat**（§0.5.3）。

### 0.6.1 与外部建议的分歧记录

评审意见曾主张「overdraw 是根因，排序优化对 overdraw 一点用都没有」，依据是 memory 里
2026-07-17 的结论。**该依据已被 §0.3.1 的真机 A/B 推翻**（`sortNth_8` −44%，
`viewport_0.5` 无收益）。

但需诚实标注置信度差异：「排序是大头」有强证据；
「overdraw 不是大头」证据较弱——因为 `viewport_0.5` **未独立验证是否真正生效**（§0.3.3 第 4 条）。
若第 0、1 步做完后帧时间下降不及预期，应回头重新验证 overdraw 的占比。

### 0.1 profile 需要回答的问题（进行中，2026-07-30）

单一问题：**GPU 帧时间里，排序 compute 占多少，光栅 + blend 占多少？**

| profile 结果 | 决策 |
|---|---|
| 光栅/blend ≥ 70% | 切分+剔除天花板很低 → Stage B/C **降优先级或放弃**，转去削 overdraw（MCMC 重训 / 剪枝 / `docs/mobile-gs-oit-plan.md` Phase 1） |
| 排序占大头 | Stage B/C 有实打实收益 → 按 §3.2.1 用剔除率反推分片粒度 |

粗判断的最省事做法：把 `m_SortNthFrame` 从 1 调到 8。帧率几乎不动 → 瓶颈不在排序。

### 0.2 profile 第一轮结果（2026-07-30，Editor Play Mode / PC）

环境：Unity Editor Play Mode，PC，**非 Quest**。Unity GPU Profiler 模块无数据（`GPU:--ms`），
故以 `Gfx.WaitForPresentOnGfxThread` 作为 GPU 时间的代理指标。

| 指标 | 500K 场景 | 7M 场景 | 倍数 |
|---|---|---|---|
| splat 数 | 500K | 7M | ×14 |
| CPU 帧时间 | 28.17 ms | 52.05 ms | ×1.85 |
| `EditorLoop` | 19.92 ms (70.7%) | —（被挤出） | — |
| `PlayerLoop` | 7.00 ms (24.8%) | 42.72 ms (82.0%) | ×6.1 |
| `FrameEvents.XRBeginFrame` | 3.32 ms | 38.57 ms (74.1%) | ×11.6 |
| **`Gfx.WaitForPresentOnGfxThread`** | 1.86 ms | **37.17 ms (71.4%)** | **×20** |
| `RenderPipelineManager.DoRenderLoop_Internal` | 1.66 ms | **1.74 ms** | **×1.05** |
| Batches / SetPass | 25 / 19 | 35 / 29 | ~持平 |

**已确定的三条结论：**

1. **GPU-bound，无争议。** 7M 场景主线程 42.72 ms 中 37.17 ms 是 `Semaphore.WaitForSignal`
   （self time 亦为 37.17 ms），CPU 纯等待占 87%。Scripts / Physics / Animation 全部 ~0 ms。
2. **CPU 提交开销不随 splat 数增长**：14 倍 splat，`DoRenderLoop_Internal` 只涨 5%，
   Batches/SetPass 基本不变。
   → **修正 §0 定位表中"dispatch/draw call 固定开销 ×N 变差"的权重**：CPU 侧有 37 ms 空闲余量，
   切分成 4 片甚至 40 片的 CPU 代价都是噪音。**切分的净效应是中性，不是负。**
   GPU 侧的 ×N dispatch barrier 仍存在，但相对 37 ms 量级同样是噪音。
3. **×20 增长是超线性的**（splat 仅 ×14）。排序 O(n)、overdraw 近似线性，都解释不了超线性，
   指向**显存带宽 / cache miss**——7M 的 view buffer 达 280 MB，远超 GPU cache，
   排序后的随机访问退化。
   → 这一条对切分**不利**：带宽压力不因切分减少（同样的数据总量仍要读一遍），
   除非整片被视锥剔除掉。

**仍未回答的关键问题**：GPU 时间里排序 compute 与光栅 blend 各占多少。
`WaitForPresent` 只说明 GPU 慢，不说明慢在哪。

### 0.3 待做：A/B 实验（可在 Editor 内完成，不必先上 Quest）

用 `Gfx.WaitForPresentOnGfxThread` 作为测量指标，7M 场景、机位固定，每次只改一个变量：

| 实验 | 改动 | 该项时间大降则说明 | 对应决策 |
|---|---|---|---|
| A | `m_SortNthFrame` 1 → 8 | 排序是瓶颈 | Stage B/C 值得做，按 §3.2.1 定粒度 |
| B | `m_SplatScale` 1 → 0.5 | overdraw 是瓶颈 | **切分放弃**，转 MCMC 重训 / 剪枝 / `docs/mobile-gs-oit-plan.md` Phase 1 |
| C | `m_SHOrder` 降到 0 | SH 计算/带宽是瓶颈 | 换更低 SH 格式收益最大，优先于切分 |

### 0.3.1 A/B 实测结果（2026-07-30，Quest 3 真机，**500K 场景**）

> **场景规模务必看清：这组数据来自 500K splats 的场景，不是 7M。**
> 7M 资产在 Quest 上因超过 128 MB 单 buffer 上限而**根本无法显示**（正是 §1.1 描述的故障），
> 所以真机上只能测 500K。§0.2 的 Editor 数据才是 7M。
>
> **这个事实本身就是最重要的结论之一**：500K 已经只有 20 fps，
> 而上游记录的 Quest 3 参考值是 **400k splats @ 72 fps**（见 memory `quest-native-splat-research`）。
> 慢了一个数量级，说明**存在配置或实现问题，不是资产规模问题**。见 §0.3.4。

场景：`Assets/GardenMR.unity`（build 包名 `com.DefaultCompany.GardenMRSort0`）。
工具：`Assets/_Profiling/SplatPerfExperiment.cs` 自动轮换配置 + logcat `VrApi` 的 `App=` 值。
每组 20 s，丢弃前 5 s；**只统计应用自身 pid**（`com.oculus.vrshell` 也发 VrApi 日志，必须排除，
否则数据被系统界面的低负载帧污染）。

| config | n | median | vs baseline |
|---|---|---|---|
| baseline | 10 | 32.30 ms | — |
| viewport_0.5 | 15 | 35.52 ms | +10.0% |
| **sortNth_8** | 15 | **18.13 ms** | **−43.9%** |
| shOrder_0 | 15 | 40.06 ms | +24.0% |
| splatScale_0.5 | 15 | 37.78 ms | +17.0% |
| baseline_end | 15 | 30.01 ms | −7.1% |

**噪声底**：场景原本就是 `SHOrder=0`，故 `shOrder_0` 实为第三次 baseline。
三次等效 baseline = 32.30 / 40.06 / 30.01 → **噪声带 ±30%**。
因此 `viewport_0.5` 与 `splatScale_0.5` 的变化**落在噪声内，无结论**。
`sortNth_8` 的 −43.9% 远超噪声（其 range 14.77–22.80 完全低于所有 baseline 中位数），**信号可信**。

**核心结论——排序约占 GPU 时间一半：**

```
32.30 = 其他开销 + X        (每帧排序)
18.13 = 其他开销 + X/8      (每 8 帧排序)
→ 7X/8 = 14.17  →  X ≈ 16.2 ms
排序 ≈ 16.2 ms ，其余（光栅 + blend + 全部）≈ 16.1 ms
```

**这推翻了"overdraw 是根因"的旧结论**（§0.2 引用的 2026-07-17 记录）：
`viewport_0.5` 把像素砍到 1/4、`splatScale_0.5` 缩小覆盖面积，两者都没让它变快。

推测原因：该场景已应用过降 overdraw 的手段（`SHOrder=0`、`SplatScale=0.60` 均非默认值），
overdraw 被压下去之后，**排序浮现为新的首要瓶颈**。与 07-17 结论不矛盾，是同一条路的下一阶段。

**→ 决策：Stage B/C 放行。** 视锥剔除省掉的正是不可见片的排序，收益直接落在最大的那一项上。

### 0.3.2 但先做更便宜的：错帧排序

`sortNth_8` 白给 44%，零工程量。代价是排序滞后——20 fps 下每 8 帧排一次 = 0.4 s，
VR 转头时大概率能看出穿帮。

**行动顺序**：先试 `m_SortNthFrame` = 2 / 3 / 4，找画质可接受的最大值。
这与 Stage B/C **可叠加**（切分+剔除无画质代价，错帧排序有），不是二选一。

### 0.3.3 本轮实验的缺陷（复测时必修）

1. **噪声 ±30% 过大**：每组仅 15 样本，且头显里的微小移动会剧烈改变可见 splat 数与覆盖面积。
   复测应延长到每组 40 s，并固定头部（可考虑锁定相机或用 stationary 模式）。
2. **`shOrder_0` 无效**：原值即为 0。要测 SH 影响，需先把场景改成 `SHOrder=3` 再做对照。
3. **`splatScale_0.5` 变化过小**：从 0.60 到 0.50，覆盖面积仅降至 69%，不足以产生可分辨的信号。
   复测用 0.2 或更低。
4. **`viewport_0.5` 是否真正生效未经独立验证**：日志确认 `XRSettings.renderViewportScale=0.500`
   已设置，但未验证 OpenXR 后端是否响应。复测应同时记录 `XRSettings.eyeTextureWidth/Height`
   或改用 0.25 这样的极端值观察是否出现可见的画面模糊。
5. **不要再加 `m_SplatScale` 实验**：该参数关系画面效果，不可改动（2026-07-30 用户明确要求）。
   脚本中已移除，替换为 `sortNth_2` / `sortNth_4` 两档。

### 0.3.4 排序开销的真实来源：FFX 的 8-pass，而非双眼重排

> **已作废的判断（保留以免重犯）**：曾认为 `Assets/GardenMR.unity:1806` 的
> `m_VRSortOnceBothEyes: 0` 导致双眼各排一次、排序翻倍，打开可省约 8 ms。**这是错的。**

`Assets/XR/Settings/OpenXRPackageSettings.asset` 第 1507 行 `m_Name: Android`，
其下第 1543 行 `m_renderMode: 0`。Unity OpenXR 枚举为
`SinglePassInstanced = 0` / `MultiPass = 1`，即 **Android 端跑的是 Single Pass Instanced**。

该模式下渲染循环每帧只跑一次，`GaussianSplatRenderer.cs:129` 的 `secondEyePass` 恒为 false，
**`m_VRSortOnceBothEyes` 打不打勾都不生效**（按其注释，该开关是为 multi-pass 设计的）。

**所以 §0.3.1 反推的 16.2 ms 是「单次排序」的真实成本，不是双眼合计。**

500K splats 单次排序 16.2 ms 的异常程度：8 pass × 500K × 16 B 读写 ≈ 64 MB，
按 Quest 3 带宽估算理论仅约 1 ms，**实际是理论值的十几倍**。
多出的开销大概率在 dispatch 与 barrier 上：FFX 每 pass 需 count/scan/scatter 三次 dispatch，
8 pass ≈ **24 次 dispatch + barrier**，移动 GPU 上这项固定开销非常贵。

真正的元凶在 `GpuSorting.cs`：

| 方案 | bits/pass | pass 数 | dispatch 数 |
|---|---|---|---|
| FFX（当前，`m_SortMethod=Auto` → Android 选它） | 4（`:33`） | **8**（`:309`） | ~24 |
| DeviceRadixSort | 8（`:26`） | **4**（`:251`） | ~12 |

**已实测（2026-08-04，Quest 3 真机，GardenMR 500K，两个 build 对比）**：

> 修正：曾以为「DeviceRadixSort 在 Quest 上是否可用」从未实测。**这是错的**——
> `commit f8cdb2f`（2026-07-17，"Add FidelityFX GPU sort path for Quest/Android compatibility"）
> 提交信息明确写着「DeviceRadixSort relies heavily on wave intrinsics that miscompile on
> some mobile Vulkan drivers (Quest/Adreno), causing splat sort corruption」——
> FFX 正是为了修复当时观察到的排序错乱才加的，Auto 默认在 Android 选 FFX 就是因为这段历史。

2026-08-04 用户直接打两个 build 对比（FFX vs DeviceRadixSort，同一 GardenMR 场景）：

- **性能**：FFX 更差（DeviceRadixSort 更快）
- **画质**：穿帮（07-17 记录的排序错乱）在 DeviceRadixSort 下**略有减少**，不是加重

即结果与 07-17 的记录相反。可能是这段时间内 Quest OS / Adreno 驱动更新修复了该 wave
intrinsics 问题（当前设备 OS 为 releases-oculus-14.0-v204，可能比 07-17 测试时更新）。
**结论：`m_SortMethod` 改为 `DeviceRadixSort`，8→4 pass 生效。**
之前 §0.3.1 用 setprop 实验测得的 30.41ms（vs FFX 基线 32.30ms，仅 −6%）**被 GPU 频率
档位差异污染**（测试时 GPU 从档位 3 升到档位 5，尚未热稳态），不能作为改善幅度的准确读数，
但不影响「改用 DeviceRadixSort」这个方向性结论——性能和画质两项实测都支持切换。

排查同时确认的**正常项**：

| 字段 | 值 | 判定 |
|---|---|---|
| `m_CSSplatUtilitiesFfx` | 已赋值（guid `03bca3c7…`） | 正常，FFX 排序可用 |
| `m_SortNthFrame` | 1 | 正常 |
| `m_SHOrder` | 0 | 已优化过 |
| `m_SplatScale` | 0.598 | 已优化过，**不可改动** |

### 0.3.5 附带发现：Single Pass Instanced 与既有结论冲突（画质隐患）

memory `quest-native-splat-research` 写明「**Quest 需 OpenXR Multi-pass 立体模式
（不是 Single Pass Instanced）**」，理由是 compute 用 `UNITY_MATRIX_VP`，multipass 下每眼矩阵自动正确。

当前 Android 端却是 Single Pass Instanced —— compute shader 只能拿到一只眼的矩阵，
`CalcViewData` 与排序距离均按单眼计算。**这是画质隐患（双眼 splat 形变/顺序略有偏差），
不是性能问题。** 需确认是有意改动还是无意回退。

### 0.4 Quest 侧的独立约束：总显存（切分解决不了）

7M splats 的 GPU buffer 总量（Norm6 SH 口径，按 §1.2 表格）：

```
pos 28MB + other 56MB + SH 224MB + view 280MB + sort 56MB + FFX scratch ~70MB ≈ 714MB
```

历史记录中 Quest 3 在 406/336 MB 时已是 121% 超预算。**切分让 buffer 能创建成功，
但不减少总显存**（§8 T8 已写明）。7M 级别资产要上 Quest，需要的是降 LOD 或剪枝，
不是切分。这条独立于上面的 A/B 结论成立。

## 0.5 SuperSplat / PlayCanvas 调研（2026-07-30）

### 0.5.1 他们的两代架构

| 代 | 后端 | 排序方案 |
|---|---|---|
| 一代 | WebGL 2 | **Web Worker 里的 CPU counting sort**（65,536 桶），异步不阻塞主线程 |
| 二代 | WebGPU | **compute shader**：剔除不可见 splat → 投影 → GPU radix sort |

官方给出的二代 vs 一代实测加速比：

- 桌面（Apple M4 Max）：1×（1M splats）→ **5.7×**（35M splats）
- **移动（iPhone 13 Pro Max）：2–2.1×（1M–4M splats）**

### 0.5.2 结论：「把排序放到 CPU」是他们走过并放弃的方向

一代的 CPU worker 排序是 **WebGL 2 没有 compute shader 时的妥协**，不是性能优选。
一旦 WebGPU 提供 compute，PlayCanvas 就把排序迁回 GPU，并在**移动端拿到 2× 提升**。

我们在 Unity/Vulkan 上本来就有 compute shader，**等于已经站在他们的二代架构上**。
把排序挪到 CPU 属于逆向迁移。

### 0.5.0 16-bit 排序 key 已实现（2026-08-04，待真机验证）

代码改动（三个文件）：

- `package/Runtime/GpuSorting.cs`：`DeviceRadixSort` 的 pass 循环改按 `m_KeyBits` 跑
  （16 bit → 2 pass，32 bit → 4 pass 不变）。`SupportResources.Load` 新增 `keyBits` 参数，
  按 `keyBits/8` 算 `reducedScratchBufferSize`。**FFX 路径不受影响**，仍固定 32-bit/8-pass
  （keyBits 传给它也无害但无收益，故未启用——见文件内注释）。
- `package/Shaders/SplatUtilitiesBody.hlsl`：新增 `DepthToSortKey16`，把 view-space Z
  线性量化到 16 bit（65536 桶）。`_SortKeyBits==16` 时启用，否则走原 `FloatToSortableUint`。
- `package/Runtime/GaussianSplatRenderer.cs`：新增 `m_SortKeyReducedPrecision`
  （默认关，opt-in，仅 `DeviceRadixSort` 生效）。`SortPoints` 每帧用 asset 的
  object-space AABB 8 角点变换到 view space 求 zMin/zMax，作归一化范围。

**安全性核查**（防止重蹈 §0.3.4 的排序错乱覆辙）：读过 `SortCommon.hlsl` 确认
`GlobalHistOffset() = (radixShift/8) * RADIX`——按 pass **位置**索引 histogram buffer，
不是按 shift 绝对值，所以缩短 pass 数是安全的；且确认「descending 分支硬编码
`shift==24` 为最后一 pass」这个陷阱踩不到，因为代码里 `SHOULD_ASCEND` 恒为开
（`GpuSorting.cs` 构造函数无条件 `EnableKeyword`），只走 ascending 分支，无此特判。

**待办**：Unity 编译确认无误 → `GardenMR.unity` 勾选 `Sort Key Reduced Precision`
（前提 `m_SortMethod` 已是 `DeviceRadixSort`，见 §0.3.4）→ Build & Run Quest →
目视排序穿帮 + logcat `App=` 对比。预期 4 pass → 2 pass，排序开销再打对折。

### 0.5.2.1 「把排序放到 CPU」的完整评估（2026-07-30）

**结论：暂不做。先修 GPU 排序的实现浪费（路线图第 0、1 步），修完再重新评估。**

支持的论据：

- Quest 3 实测 `GPU%=0.99 / CPU%=0.16`（§0.3.1 日志），**CPU 有 84% 空闲**，纯负载均衡角度存在套利空间
- Quest 3 是**统一内存架构**，CPU→GPU 回传无 PCIe 开销，比桌面端便宜
- PlayCanvas 一代正是 CPU worker + 65536 桶 counting sort，证明百万级可行
- 500K 的规模对 CPU counting sort 完全够得着：Burst + Jobs 下估算单核 2–5 ms，多核并行可到 1–2 ms

反对的论据（更重）：

- **我们 GPU 排序慢是实现问题，不是 GPU 本质问题**：16.2 ms 对比理论约 1 ms，浪费全在
  8 个 pass 与 ~24 次 dispatch 上（§0.3.4）。第 0 步（DeviceRadixSort，pass 8→4）
  与第 1 步（16-bit key，再砍半）预期把它压到 **约 4 ms**
- 一旦 GPU 排序到了 4 ms，CPU 方案的 2–5 ms 再加上**回传带宽、1 帧延迟、
  CPU 端位置数据副本**（500K → 6 MB，7M → 84 MB）与全部工程量，优势荡然无存
- PlayCanvas 在有了 compute 之后就从 CPU 迁回 GPU，且**移动端拿到 2× 提升**（§0.5.1）
- 若后续实施 per-splat GPU culling（路线图第 3 步），排序规模本身会下降，CPU 方案的必要性进一步降低

**唯一会让它重新变成首选的情形**：第 0 + 1 步做完后排序仍显著高于 ~8 ms。
那将说明 Quest 上的 compute 排序存在根本性障碍（而非实现浪费），此时 CPU worker 才是真正的备选。

真要实施时的方案要点：Burst + Jobs 的 counting sort（桶数 65536）、
跨帧摊销、相机位姿阈值触发重排（与路线图第 4 步合并）、结果用 double buffer 避免与渲染争用。

### 0.5.3 SuperSplat 真正的性能来源（以及对本方案的冲击）

1. **per-splat GPU compute culling** —— 剔除视锥外的 splat 后才排序与光栅
2. **LOD + streaming（Streamed SOG）** —— 按相机距离动态降级
3. **全局 `splatBudget`** —— 官方建议**移动端 1M、桌面 3M+**

**对本切分方案最重要的一条影响：per-splat culling 根本不需要资产切分。**

可以直接在现有 `SplatUtilities.compute` 里加一个 culling pass（视锥判定 + 流压缩），
粒度比 §6.1 的「整片剔除」细几个数量级，而且**不改导入器、不改资产格式、不改场景**。

→ **Stage C 的「必须先做 Stage B 切分」这一前提不再成立。**
→ 切分的用途收窄为**「绕过 128 MB 单 buffer 上限」这一项**（对 7M 场景仍然必需，见 §1.1）。

另需注意：PlayCanvas 官方文档仍称 **fill rate 是主要瓶颈**（「每个最终像素要处理几十甚至几百个
fragment」），与我们 §0.3.1 实测「排序占一半」不同。原因是我们的场景已经压过 overdraw
（`SHOrder=0`、`SplatScale=0.598`），两者不矛盾。

参考：
- [PlayCanvas Blog: New in SuperSplat — WebGPU and Streaming](https://blog.playcanvas.com/new-in-supersplat-webgpu-and-streaming-bring-huge-performance-wins/)
- [PlayCanvas Docs: Gaussian Splatting Performance](https://developer.playcanvas.com/user-manual/gaussian-splatting/building/performance/)
- [playcanvas/engine Issue #7802: Implement LOD / global sorting for gsplat](https://github.com/playcanvas/engine/issues/7802)

---

## 1. 背景与根因（为什么要做）

### 1.1 实测故障

Quest 3（Adreno GPU，Vulkan）上 `SystemInfo.maxGraphicsBufferSize` = **134,217,728 字节（128 MB）**。
任何单块 `GraphicsBuffer` 超过它都会在创建时抛异常。

实测 logcat（Forest 场景，`gaussian-forest-lod0.asset`，5,840,396 splats）：

```
ArgumentException: The total size of the graphics buffer (186892672 bytes) exceeds
the maximum buffer size. Maximum supported buffer size: 134217728 bytes.
  at GaussianSplatting.Runtime.GaussianSplatRenderer.CreateResourcesForAsset ()
```

186,892,672 = 5,840,396 × 32 字节，即 **SH 数据 buffer**（`SHFormat.Norm6`，
`SHTableItemNorm6` 每 splat 32 字节）。此外 `m_GpuView`（40 字节/splat ≈ 234 MB）也会超限。

创建失败后该 renderer 的 GPU 资源为 null，但组件仍注册在渲染系统里，后续每帧在
`SortPoints` 抛 `NullReferenceException`，进而 `Render Graph Execution error`，
整个相机输出全黑（连天空盒和普通 Mesh 都消失）。

### 1.2 每 splat 的 GPU buffer 占用（决定切分粒度）

运行时在 `package/Runtime/GaussianSplatRenderer.cs` 的 `CreateResourcesForAsset()` 与
`InitSortBuffers()` 里创建这些 buffer：

| Buffer | 每 splat 字节数 | 5.84M splats 实际大小 | 128 MB 上限对应最大 splat 数 |
|---|---|---|---|
| `m_GpuPosData` | `GetVectorSize(posFormat)`，Norm11=4 | 23 MB | 33.5M |
| `m_GpuOtherData` | 4(rot) + `GetVectorSize(scaleFormat)`，Norm11 时共 8 | 47 MB | 16.7M |
| `m_GpuSHData` | 取决于 SHFormat；Norm6=32，Norm11=48，Float16=96 | **187 MB（超限）** | 4.19M（Norm6） |
| `m_GpuView` | `kGpuViewDataSize` = **40**（常量，与格式无关） | **234 MB（超限）** | 3.35M |
| `m_GpuSortDistances` + `m_GpuSortKeys` | 4 + 4 | 23+23 MB | 33.5M |
| FFX 排序 scratch（`GpuSorting.SupportResources.Load`） | ~8–12 | ~60 MB | — |

颜色数据是 `Texture2D`（2048 宽，`kTextureWidth`），不占 GraphicsBuffer 限额，无需考虑。

**绑定项取决于 SH 格式，不是固定的：**

- `Norm6`（32B）/ `Norm11`（60B）等低位宽格式 → 绑定项是 `m_GpuView` 的 **40 B/splat**
- `Float16`（96B）/ `Float32`（192B）→ 绑定项是 **SH buffer**

所以 §3.2 的计算必须对所有 buffer 取 `max()`，不能写死 40。以当前使用的 Medium 质量
（SH=Norm6）为例，绑定项是 view 的 40 B，理论单 renderer 上限 3.35M splats；
留出 FFX scratch 与其他 buffer 的余量后，取安全系数 0.5：

```
maxSplatsPerPart = floor(134217728 × 0.5 / 40) ≈ 1,677,721  →  取整为 1,600,000
```

按这个上限，5.84M 的 Forest 切成 4 片，5.76M 的 Garden 切成 4 片。

> **注意**：这个片数是被 **buffer 上限**倒推出来的，不是被**剔除效率**倒推出来的。
> 4 片在 VR 里几乎剔不掉任何东西（100° 视场下站在场景中心，片片都碰得到视锥）。
> 若 profile 显示要走 Stage C，分片粒度得重新按 §3.2.1 定，通常比这细一个数量级。
> 安全系数 0.5 是拍脑袋的保守值，同样待 §3.2.1 复核。

### 1.2.1 与上游 `kMaxSplats` 的关系（2026-07-30 补）

上游在 `package/Runtime/GaussianSplatAsset.cs:16` 有一个写死的全局上限：

```csharp
public const int kMaxSplats = 8_600_000; // mostly due to 2GB GPU buffer size limit when exporting a splat (2GB / 248B is just over 8.6M)
```

拆解这个 248 B/splat：`SHTableItemFloat32`(192) + other(16) + `kGpuViewDataSize`(40) = 248，
即上游用的是**各 buffer 字节数之和**，且假定最坏的 SH 格式。

**两个必须知道的点：**

1. **口径与本方案不同，且上游口径不精确。** GraphicsBuffer 上限约束的是**单个 buffer**，
   不是所有 buffer 之和，正确口径是 `max()`（§3.2 用的就是 max）。上游的 `sum` 口径
   偏保守，相当于顺带把总显存预算也算进去了。两者不冲突——本方案在 §3.2 里对
   `kMaxSplats` 再 clamp 一次即可，取二者更小值。
2. **`kMaxSplats` 同时在防 `int` 溢出**：8.6M × 248 B = 2.13 G，紧贴 `int.MaxValue`(2.147 G)。
   **实现切分时，任何 `splatCount × 每-splat字节数` 的表达式都必须先转 `long` 再乘**，
   §3.2 与 §4 的代码骨架已遵守此约定，改动时不要退回 int。

**现有校验点**（grep `kMaxSplats` 的全部结果）：

| 位置 | 场景 |
|---|---|
| `Editor/GaussianSplatRendererEditor.cs:184, 225` | 合并多个 asset 时 |
| `Runtime/GaussianSplatRenderer.cs:1019` | `EditSetSplatCount` 编辑路径 |

**导入路径 `GaussianSplatAssetCreator` 不检查 `kMaxSplats`。** 这意味着切分方案有一个
之前没写出来的额外收益：**单片受 8.6M 约束，整个场景的总 splat 数不再受约束**。
上面三个校验点都以单 asset 为单位，语义在切分后依然正确，**无需修改**。

### 1.3 方案概述

**在导入期切分，运行时零新概念。**
导入器把一个 PLY 切成 N 个标准 `GaussianSplatAsset`（互相独立、每片都在上限内），
并生成一个 prefab：父物体下挂 N 个子物体，每个子物体一个 `GaussianSplatRenderer`。
运行时代码只加一个"资产过大"的防御检查。不改任何 shader、不改排序算法、不改渲染管线。

已有基础（不需要新写）：

- 导入器已做 **Morton（z-order）空间重排**（`ReorderMorton`，
  `GaussianSplatAssetCreator.cs` 第 281–282 行调用）。重排后**按连续区间切片天然就是空间紧凑的团块**——这是本方案成立的关键前提。
- 渲染系统已对多个 renderer **按相机距离排序后逐个绘制**
  （`GaussianSplatRenderSystem.GatherSplatsForCamera`，
  `GaussianSplatRenderer.cs` 第 89–102 行），块间顺序不需要新代码。
- 每个 renderer 独立排序：radix sort 是 O(n)，N 片总计算量与单片基本相同。

---

## 2. 涉及文件一览

| 文件 | 改动 |
|---|---|
| `package/Editor/GaussianSplatAssetCreator.cs` | 主要改动：切分逻辑 + UI + prefab 生成 |
| `package/Runtime/GaussianSplatRenderer.cs` | 小改动：`CreateResourcesForAsset` 开头加大小检查 |
| `projects/GaussianExample-URP/Assets/Forest.unity` 等场景 | 手工操作：用新 prefab 替换旧单体 renderer（见 §7） |

不改动：`GaussianSplatAsset.cs`、所有 shader、所有 compute、URP/HDRP feature、排序器。

---

## 3. Editor 改动：`GaussianSplatAssetCreator.cs`

### 3.1 新增序列化字段与 UI

在现有字段区（`m_FormatSH` 之后）加：

```csharp
[SerializeField] bool m_SplitLargeAssets = true;
// 目标平台单 buffer 上限（字节）。Quest/Adreno = 128MB。0 = 不切分。
[SerializeField] long m_TargetMaxBufferSize = 128L * 1024 * 1024;
[SerializeField] float m_SplitSafetyFactor = 0.5f;
```

`OnGUI()` 里 Quality 下拉之后加一组 UI：

- Toggle "Split Large Assets"
- 下拉预设：`Quest (128 MB)` / `Desktop 2GB` / `Custom`，Custom 时露出 long 输入框
- 只读 Label：显示当前格式下算出的 `maxSplatsPerPart` 和预计切片数
  （`ceil(m_PrevVertexCount / maxSplatsPerPart)`）

### 3.2 每片最大 splat 数的计算

新增静态方法（放在 `CreateAsset()` 附近）：

```csharp
// 返回单片允许的最大 splat 数；返回 int.MaxValue 表示无需切分。
static int CalcMaxSplatsPerPart(long maxBufferBytes, float safety,
    GaussianSplatAsset.VectorFormat posF, GaussianSplatAsset.VectorFormat sclF,
    GaussianSplatAsset.SHFormat shF)
{
    if (maxBufferBytes <= 0)
        return int.MaxValue;

    // 与 CreateResourcesForAsset/InitSortBuffers 实际分配一一对应
    long perSplat = 0;
    perSplat = math.max(perSplat, GaussianSplatAsset.GetVectorSize(posF));          // m_GpuPosData
    perSplat = math.max(perSplat, 4 + GaussianSplatAsset.GetVectorSize(sclF) + 2);  // m_GpuOtherData（+2 是 SH cluster 索引，保守带上）
    perSplat = math.max(perSplat, 40);                                              // m_GpuView, kGpuViewDataSize
    perSplat = math.max(perSplat, 4);                                               // sort distances / keys

    // SH：非 cluster 格式按每 splat 大小算；cluster 格式是固定表，不随 splat 数增长
    long shPerSplat = shF switch
    {
        GaussianSplatAsset.SHFormat.Float32 => UnsafeUtility.SizeOf<GaussianSplatAsset.SHTableItemFloat32>(),
        GaussianSplatAsset.SHFormat.Float16 => UnsafeUtility.SizeOf<GaussianSplatAsset.SHTableItemFloat16>(),
        GaussianSplatAsset.SHFormat.Norm11  => UnsafeUtility.SizeOf<GaussianSplatAsset.SHTableItemNorm11>(),
        GaussianSplatAsset.SHFormat.Norm6   => UnsafeUtility.SizeOf<GaussianSplatAsset.SHTableItemNorm6>(),
        _ => 0
    };
    perSplat = math.max(perSplat, shPerSplat);

    long result = (long)(maxBufferBytes * safety) / perSplat;

    // 再与上游全局上限取小。kMaxSplats 口径是"各 buffer 之和"且防 int 溢出，见 §1.2.1。
    result = math.min(result, GaussianSplatAsset.kMaxSplats);

    // 对齐到 kChunkSize(256)，让每片的压缩 chunk 都是完整的
    result = result / GaussianSplatAsset.kChunkSize * GaussianSplatAsset.kChunkSize;
    return (int)math.min(result, int.MaxValue);
}
```

注意 `perSplat` 与 `result` 全程用 `long`：`splatCount × 每-splat字节` 在 8.6M 量级会溢出
`int`（见 §1.2.1 第 2 点）。

数值感受：`Medium` 质量（Norm11/Norm11/Norm8x4/Norm6）+ 128 MB + 0.5 → **1,677,568**。

### 3.2.1 分片粒度怎么定（Stage C 才需要，等 profile）

上面算出的是**允许的最大片大小**（correctness 上限）。若走 Stage C，实际片大小应该取
**更小**的值，由剔除效率决定。两个目标的取舍：

- 片越大 → dispatch/draw call 越少，但整片落在视锥外的概率越低，剔除收益趋近于 0
- 片越小 → 剔除率高，但固定开销 ×N 上升，且每片的 sort scratch 有下限开销

**定参步骤（profile 出结果、且结论是"排序占大头"时才执行）：**

1. 在 Garden 场景取 3–5 个代表性视角（场景中心、边缘朝外、边缘朝内）。
2. 对候选片大小（建议试 1.6M / 400k / 100k 三档），用 `asset.boundsMin/Max` 变换到世界空间后
   做 `TestPlanesAABB`，**离线统计每个视角的可剔除片数占比**——这一步不需要真实现切分，
   写个 Editor 脚本对 Morton 序做虚拟分段即可。
3. 若某档的平均剔除率 < 25%，该档不值得做（固定开销吃掉收益）。
4. 选剔除率与片数的拐点。**新增字段 `m_MaxSplatsPerPartOverride`（0 = 用 §3.2 的自动值）**，
   把选定值填进去，不要去动安全系数——两者语义不同，混用会让上限校验失效。

在 profile 结果出来前，**不要凭直觉选片大小**。当前文档里的 4 片是 buffer 上限的副产物，
不是任何性能分析的结论。

### 3.3 `CreateAsset()` 改为多片循环

现在的流程（第 247–340 行）是：读文件 → 算 bounds → `ReorderMorton` → 可选 `ClusterSHs`
→ 建 1 个 asset + 5 个 `.bytes` → `AssetDatabase.Refresh` → `SetAssetFiles` → 存盘。

改造要点：**Morton 重排必须在切分前对整体做**（保证切片空间紧凑），其余全部下沉到每片循环里。

```csharp
unsafe void CreateAsset()
{
    // ...现有输入校验、LoadJsonCamerasFile、LoadInputSplatFile、CalcBoundsJob、ReorderMorton 不动...

    int maxPerPart = m_SplitLargeAssets
        ? CalcMaxSplatsPerPart(m_TargetMaxBufferSize, m_SplitSafetyFactor, m_FormatPos, m_FormatScale, m_FormatSH)
        : int.MaxValue;

    int total = inputSplats.Length;
    int numParts = math.max(1, (total + maxPerPart - 1) / maxPerPart);
    // 均分并对齐 256，避免最后一片过小
    int partSize = ((total + numParts - 1) / numParts + GaussianSplatAsset.kChunkSize - 1)
                   / GaussianSplatAsset.kChunkSize * GaussianSplatAsset.kChunkSize;

    string baseName = Path.GetFileNameWithoutExtension(FilePickerControl.PathToDisplayString(m_InputFile));
    var partAssets = new List<GaussianSplatAsset>();
    var partPaths  = new List<(string chunk, string pos, string other, string col, string sh, string asset)>();

    for (int part = 0; part < numParts; ++part)
    {
        int start = part * partSize;
        int len = math.min(partSize, total - start);
        if (len <= 0) break;

        NativeArray<InputSplatData> slice = inputSplats.GetSubArray(start, len);

        // 每片独立：bounds、SH 聚类、chunk、编码
        float3 pMin, pMax;
        var bj = new CalcBoundsJob { m_BoundsMin = &pMin, m_BoundsMax = &pMax, m_SplatData = slice };
        bj.Schedule().Complete();

        NativeArray<int> shIdx = default;
        NativeArray<GaussianSplatAsset.SHTableItemFloat16> shTab = default;
        if (m_FormatSH >= GaussianSplatAsset.SHFormat.Cluster64k)
            ClusterSHs(slice, m_FormatSH, out shTab, out shIdx);

        string suffix = numParts > 1 ? $"_p{part:00}" : "";
        string partName = baseName + suffix;
        var paths = (chunk: $"{m_OutputFolder}/{partName}_chk.bytes",
                     pos:   $"{m_OutputFolder}/{partName}_pos.bytes",
                     other: $"{m_OutputFolder}/{partName}_oth.bytes",
                     col:   $"{m_OutputFolder}/{partName}_col.bytes",
                     sh:    $"{m_OutputFolder}/{partName}_shs.bytes",
                     asset: $"{m_OutputFolder}/{partName}.asset");

        var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        // 相机信息只挂第 0 片，避免重复
        asset.Initialize(len, m_FormatPos, m_FormatScale, m_FormatColor, m_FormatSH,
                         pMin, pMax, part == 0 ? cameras : null);
        asset.name = partName;

        var hash = new Hash128((uint)asset.splatCount, (uint)asset.formatVersion, 0, 0);
        if (isUsingChunks) CreateChunkData(slice, paths.chunk, ref hash);
        CreatePositionsData(slice, paths.pos, ref hash);
        CreateOtherData(slice, paths.other, ref hash, shIdx);
        CreateColorData(slice, paths.col, ref hash);
        CreateSHData(slice, paths.sh, ref hash, shTab);
        asset.SetDataHash(hash);

        if (shIdx.IsCreated) shIdx.Dispose();
        if (shTab.IsCreated) shTab.Dispose();

        partAssets.Add(asset);
        partPaths.Add(paths);
    }

    // 全部 .bytes 写完后统一 Refresh 一次（比每片 Refresh 快得多）
    AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

    var savedAssets = new List<GaussianSplatAsset>();
    for (int i = 0; i < partAssets.Count; ++i)
    {
        var p = partPaths[i];
        partAssets[i].SetAssetFiles(
            isUsingChunks ? AssetDatabase.LoadAssetAtPath<TextAsset>(p.chunk) : null,
            AssetDatabase.LoadAssetAtPath<TextAsset>(p.pos),
            AssetDatabase.LoadAssetAtPath<TextAsset>(p.other),
            AssetDatabase.LoadAssetAtPath<TextAsset>(p.col),
            AssetDatabase.LoadAssetAtPath<TextAsset>(p.sh));
        savedAssets.Add(CreateOrReplaceAsset(partAssets[i], p.asset));
    }

    if (savedAssets.Count > 1)
        CreateSplitPrefab(baseName, savedAssets);   // §3.4

    AssetDatabase.SaveAssets();
    EditorUtility.ClearProgressBar();
    Selection.activeObject = savedAssets[0];
}
```

**实现细节与坑：**

1. **`GetSubArray` 与 Burst job**：所有 `Create*Data` job 已接受 `NativeArray<InputSplatData>`，
   传子数组即可。`CalcChunkDataJob` 会**就地修改** slice 里的数据（归一化到 chunk 范围）——
   切片互不重叠所以安全，但意味着**每片必须完整跑完自己的 Create 链后再进下一片**（上面的循环结构已保证）。
   若 Unity 版本的 safety system 拒绝对 `GetSubArray` 的写入，退路是
   `new NativeArray<InputSplatData>(len, Allocator.Persistent)` + `NativeArray.Copy` 复制切片。
2. **进度条**：循环里用
   `EditorUtility.DisplayProgressBar(kProgressTitle, $"Part {part+1}/{numParts}: ...", (part + stageT) / numParts)`。
3. **SH 聚类按片做**：cluster 格式（Cluster64k 等）每片独立聚类。质量与整体聚类略有差异，可接受；
   注意 `ClusterSHs` 本身很慢（每片数分钟），进度条要体现。
4. **相机 JSON**：只挂在第 0 片（见上面代码），否则 `ActivateCamera` 会重复。
5. **命名**：单片时不加后缀，保持对旧行为的完全兼容（`numParts == 1` 路径等价于现状）。

### 3.4 生成组装好的 prefab：`CreateSplitPrefab`

```csharp
void CreateSplitPrefab(string baseName, List<GaussianSplatAsset> parts)
{
    var root = new GameObject(baseName);
    try
    {
        foreach (var part in parts)
        {
            var go = new GameObject(part.name);
            go.transform.SetParent(root.transform, false);
            var gs = go.AddComponent<GaussianSplatRenderer>();
            gs.m_Asset = part;
            AssignDefaultResources(gs);   // 见下
        }
        string prefabPath = $"{m_OutputFolder}/{baseName}_Split.prefab";
        PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
    }
    finally { DestroyImmediate(root); }
}
```

`GaussianSplatRenderer` 的 shader/compute 引用（`m_ShaderSplats`、`m_ShaderComposite`、
`m_ShaderDebugPoints`、`m_ShaderDebugBoxes`、`m_CSSplatUtilities`，以及 FFX 变体字段如
`m_CSSplatUtilitiesFfx`——**实现时打开 `GaussianSplatRenderer.cs` 核对全部
`[SerializeField]` 的 Shader/ComputeShader 字段清单**）默认是 null，必须显式赋值：

```csharp
static void AssignDefaultResources(GaussianSplatRenderer gs)
{
    // 路径前缀 = "Packages/<package.json 里的 name 字段>/"
    // 实现时读 package/package.json 确认包名后写死到常量。
    const string pkg = "Packages/org.nesnausk.gaussian-splatting";
    gs.m_ShaderSplats      = AssetDatabase.LoadAssetAtPath<Shader>($"{pkg}/Shaders/RenderGaussianSplats.shader");
    gs.m_ShaderComposite   = AssetDatabase.LoadAssetAtPath<Shader>($"{pkg}/Shaders/GaussianComposite.shader");
    gs.m_ShaderDebugPoints = AssetDatabase.LoadAssetAtPath<Shader>($"{pkg}/Shaders/GaussianDebugRenderPoints.shader");
    gs.m_ShaderDebugBoxes  = AssetDatabase.LoadAssetAtPath<Shader>($"{pkg}/Shaders/GaussianDebugRenderBoxes.shader");
    gs.m_CSSplatUtilities  = AssetDatabase.LoadAssetAtPath<ComputeShader>($"{pkg}/Shaders/SplatUtilities.compute");
    // FFX compute 字段若存在也要赋值（Shaders/SplatUtilitiesFfx.compute）
}
```

若字段是 `internal`/`private`，用 `SerializedObject`：
`new SerializedObject(gs).FindProperty("m_ShaderSplats").objectReferenceValue = ...` 后 `ApplyModifiedPropertiesWithoutUndo()`。

**校验点**：这些字段名可在任一示例场景 YAML 里确认（如
`projects/GaussianExample-URP/Assets/Forest.unity` 第 4316–4334 行的
`GaussianSplatRenderer` 组件序列化块）。

---

## 4. Runtime 改动：过大资产的防御检查 —— **Stage A，无前置依赖，优先做**

这一节独立于切分逻辑，不依赖 profile 结果，约 10 行，先做掉。

`package/Runtime/GaussianSplatRenderer.cs` 的 `CreateResourcesForAsset()`（约第 427 行）开头插入：

```csharp
void CreateResourcesForAsset()
{
    if (!HasValidAsset)
        return;

    long maxBuf = SystemInfo.maxGraphicsBufferSize;
    long viewSize = (long)asset.splatCount * kGpuViewDataSize;
    long biggest = math.max(math.max((long)asset.posData.dataSize, (long)asset.otherData.dataSize),
                   math.max((long)asset.shData.dataSize, viewSize));
    if (biggest > maxBuf)
    {
        Debug.LogError($"{nameof(GaussianSplatRenderer)} '{name}': asset '{asset.name}' needs a " +
            $"{biggest / (1024*1024)}MB GPU buffer but this device supports at most {maxBuf / (1024*1024)}MB. " +
            $"Re-import the source file with 'Split Large Assets' enabled (Tools > Gaussian Splats > Create GaussianSplatAsset).", this);
        return;
    }
    // ...原有代码...
}
```

提前 `return` 后 `m_GpuPosData` 保持 null → `HasValidRenderSetup` 为 false →
`GatherSplatsForCamera`（第 82 行）自动跳过该 renderer。效果：**一条清晰错误替代每帧异常刷屏，其余场景内容正常渲染**。这同时修复了 Garden 场景"整帧全黑"的连带故障模式。

---

## 5. 块间绘制顺序（需要验证，不需要新代码）

`GatherSplatsForCamera` 已按 `m_RenderOrder`、再按相机本地 z 升序排列所有 renderer
（`GaussianSplatRenderer.cs` 第 89–102 行），然后 `SortAndRenderSplats` 按此顺序逐个
sort + draw。块内顺序由各自的 GPU radix sort 保证。

**实现后必须做一次方向验证**（splat 混合对顺序敏感，块间顺序必须与块内 GPU 排序方向一致）：

1. 造两片在深度上明显前后重叠的测试资产（或直接用切分后的 Garden，站在两片交界处）。
2. 观察交界区域：若出现"前面的片被后面的片盖住"的明显穿帮，把第 101 行的比较反向
   （`posB.z.CompareTo(posA.z)`）再验一次。
3. 以视觉正确的方向为准，加一行注释说明"必须与 splat 内部排序方向一致"。

预期：Morton 切片让边界面积最小，块间误差仅在交界薄层可见，实践中可忽略
（PlayCanvas SOG 查看器同样按空间瓦片独立排序）。

---

## 6. Stage C：整块视锥剔除（唯一的正收益来源，需 profile 结果放行）

**这不再是"可选二期"。** 见 §0：Stage B 单独实施是负收益，性能收益全部来自这里。
若 profile 结论是"光栅/blend 占大头"，则 Stage B 与 C **一起放弃**，不要只做 B。

1. **整块视锥剔除**：`GatherSplatsForCamera` 里对每个 renderer 用
   `GeometryUtility.CalculateFrustumPlanes(cam)` + `TestPlanesAABB`（AABB 取
   `asset.boundsMin/Max` 经 `transform.localToWorldMatrix` 变换）判定，不可见则跳过。
   背向的片连排序都省了。

   **VR 注意**：双眼视锥不同。用 center-eye 的合并视锥（或直接用 `Camera.main` 的
   `cullingMatrix`）做保守剔除，不要按单眼剔——会在一只眼睛里出现缺片。
   与已实现的 `m_VRSortOnceBothEyes`（见 vr-preview 分支）保持同一套 center-eye 口径。

   **剔除率必须实测**：分片粒度由 §3.2.1 定，粒度不对时剔除率可能接近 0。
2. **错帧排序**：给每片设不同的 `m_SortNthFrame` 相位，把排序开销摊到多帧。
   这一条**不依赖切分也部分可行**，且若 profile 显示排序是瓶颈，它比整套切分便宜得多——
   **先试这条，再决定要不要做 Stage B**。
3. **SOG 瓦片导入器**：`C:\Users\standalone\Downloads\Botanical Garden - America v3 (VR Ready)`
   是 PlayCanvas SOG 格式（73 个 WebP 瓦片 + 6 级 LOD + codebook）。当前导入器只支持
   PLY/SPZ（`GaussianFileReader.cs`）。若做 SOG 读取器可直接复用它的瓦片划分和 LOD，
   但需要 WebP 解码 + codebook 反量化，工作量大，二期再议。
   一期直接用该文件夹里自带的 `Botanical Garden - America v3_clean-edit.ply` 走切分导入。

---

## 7. 场景迁移步骤（手工，实现完成后执行）

以 Forest 为例：

1. `Tools > Gaussian Splats > Create GaussianSplatAsset`，输入
   `gaussian-forest-lod0` 的源 PLY，Quality=Medium，Split=on，Quest 预设 → 生成
   `gaussian-forest-lod0_p00..p03.asset` + `gaussian-forest-lod0_Split.prefab`。
2. 打开 `Forest.unity`，记录旧 `GaussianSplats` 物体的 Transform（注意它有 180° X 翻转）、
   `m_RenderMode`、`m_SplatScale`、`m_OpacityScale`、`m_SHOrder`、`m_SortNthFrame`、Cutout 引用。
3. 拖入 prefab，把记录的 Transform 设到 prefab 根上；渲染参数逐项设到每个子 renderer
   （多选子物体一次改完）。Cutout（`GSCutout`）引用需要加到**每个**子 renderer 的
   `m_Cutouts` 列表（cutout 是世界空间椭球，对每片独立生效，语义不变）。
4. 禁用/删除旧物体，保存场景。
5. GardenAmericaSplat / GardenAmericaRepaint 同理；注意这两个场景的
   `SplatMaterializeController` / `StylizedSplatsController` 脚本各引用一个 renderer，
   迁移时要么让脚本改为接受 renderer 数组，要么一期先只挂主片（功能降级，标注 TODO）。

---

## 8. 验收测试

按顺序执行，全部通过才算完成：

| # | 步骤 | 通过标准 |
|---|---|---|
| T1 | 编辑器导入小 PLY（<100k splats），Split=on | 行为与改动前完全一致：单 asset、无后缀、无 prefab |
| T2 | 把 `m_TargetMaxBufferSize` 临时设为 8 MB，导入同一小 PLY | 生成多片 + prefab；每片 `m_SplatCount` ≤ 计算值且为 256 的倍数；片数 × 片大小 ≥ 总数 |
| T3 | prefab 拖进 `TestEmpty.unity`，编辑器播放 | 点云完整显示（与切分前视觉一致），Console 无错误 |
| T4 | 导入 Garden 的 `_clean-edit.ply`（5.76M），Quest 预设 | 生成 4 片，每片 < 1.68M splats |
| T5 | 用 T4 的 prefab 替换 `GardenAmericaSplat.unity` 的单体 renderer，Build & Run 到 Quest | `adb logcat` 无 `ArgumentException`、无 `NullReferenceException`、无 `Render Graph Execution error`；头显中能看到天空盒、Cube 和 splat 场景 |
| T6 | 站到两片交界处观察 | 无明显排序穿帮（若有，按 §5 翻转块间顺序后重验） |
| T7 | 故意把旧 5.76M 单体 asset 挂回场景，Build & Run | 只出现一条 §4 的明确错误日志；天空盒和 Mesh 正常渲染（不再整帧黑屏） |
| T8 | `adb shell dumpsys meminfo <pid>` 或 logcat 的 `ClientSharedTelemetryStats` | GPU 内存不超预算（之前 5.84M 单体时为 406/336 MB = 121%，切分本身不减总量，若仍超预算需换更小 LOD——切分解决的是"单 buffer 上限"，不是总显存。另见 §0.4） |
| T9 | **Stage C 专属**：切分前后对比 `Gfx.WaitForPresentOnGfxThread`（同机位、同场景） | 该指标**必须下降**。若持平或上升，说明剔除率不足（粒度错，回 §3.2.1 重定）或瓶颈本就不在此——此时应回滚 Stage C 并重新审视 §0.3 的 A/B 结论 |
| T10 | **Stage C 专属**：VR 中缓慢转头 360°，观察分片边缘 | 无整片突然消失/出现的爆闪；双眼均无缺片（验证 §6.1 的 center-eye 保守剔除口径） |

对应关系：T1–T3、T7 覆盖 Stage A/B 的正确性；T4–T6 覆盖 Stage B 的设备可用性；
T9–T10 是 Stage C 的收益与正确性门槛，**T9 不通过则 Stage C 无意义，应整体回滚**。

回归确认：`TestEmpty`（无 splat）与 Forest 现有小资产（`Victoria House 5%`，1.06M，无需切分）行为不变。

---

## 9. 明确不做的事

- 不在单个 renderer 内部做多 bank buffer 拼接（需要重写排序器与所有 compute 的寻址，收益不成比例）。
- 不做运行时动态切分或流式加载。
- 不改 `GaussianSplatAsset` 序列化格式（`kCurrentVersion` 不变，旧资产完全兼容）。
- 不处理跨片的全局 splat 精确排序——块间近似顺序即可（见 §5）。
