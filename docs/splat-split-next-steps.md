# Splat 资产切分：交接与下一步

分支：`feature/splat-asset-split`
起点 commit：`9b60c9e` — Implement import-time splitting of large Gaussian splat assets
日期：2026-07-28
适用：在另一台机器上接手验证。本文自包含，不需要读之前的对话。

配套文档：[splat-asset-split-plan.md](splat-asset-split-plan.md)（完整设计、算法、代码落点）。
本文只讲**接下来要做什么**。

---

## 0. 拉取

```bash
git fetch origin && git checkout feature/splat-asset-split
```

Unity 版本：**6000.3.6f1**（见 `projects/GaussianExample-URP/ProjectSettings/ProjectVersion.txt`）。
用别的版本打开会触发资产升级，先别混用。

---

## 1. 已经做完的（不用重做）

| 内容 | 文件 |
|---|---|
| 导入期把大 PLY 切成 N 个标准 `GaussianSplatAsset` + 自动生成组装好的 prefab | `package/Editor/GaussianSplatAssetCreator.cs` |
| 运行时"资产过大"防御检查，替代每帧异常刷屏 | `package/Runtime/GaussianSplatRenderer.cs`，`CreateResourcesForAsset()` 开头 |

**验证到的程度**：用 Unity 自带 Roslyn 编译过 `package/` 的 Runtime + Editor 全部源码，exit 0。
**没有在 Unity 里实际导入过任何文件，没有上过 Quest。** 下面全是待办。

不改动：shader、compute、排序算法、URP/HDRP feature、`GaussianSplatAsset` 序列化格式
（`kCurrentVersion` 不变，旧资产完全兼容）。

### 与计划文档的一处偏离

计划 §3.3 骨架里用 `inputSplats.GetSubArray(start, len)` 取切片。实现改成了
**`NativeArray.Copy` 复制出独立缓冲**，用完即 `Dispose`。

原因：`CalcChunkDataJob` 会就地改写 splat 数据，而 `GetSubArray` 视图在 job 安全系统下
是否保持可写随 Unity 版本而异（计划 §3.3 坑位 1 自己预判了这点），当时无法实测所以不赌。
代价是同一时刻多占一片的内存（1.6M splats 约 380 MB）。
**单片（不切分）路径不走这条分支，行为与改动前逐字节一致。**

---

## 2. 立刻要跑的：编辑器内冒烟测试

打开项目，等编译完，`Tools > Gaussian Splats > Create GaussianSplatAsset`。
窗口底部应该多出一组 **"Large asset splitting"**：开关 + Device Buffer Limit 下拉
（Quest / Mobile 128 MB、Desktop 2 GB、Custom）+ 一行实时显示"会切成几片、每片多少 splats"。

### T1 — 确认没搞坏原有行为

小 PLY（<100k splats），Split=on，Quest 预设。

通过标准：显示 "1 part"；产出与改动前完全一致 —— 单个 `.asset`、**无** `_p00` 后缀、**无** prefab。

### T2 — 逼它切分

同一个小 PLY，Device Buffer Limit 选 **Custom**，填 `8388608`（8 MB）。

通过标准：
- 显示切成 N 片（N > 1）
- 产出 `<name>_p00.asset` … `<name>_p0N.asset` + `<name>_Split.prefab`
- 每片的 Splat Count **是 256 的倍数**（最后一片可以不是）
- 各片 splat 数之和 == 源文件总数

### T3 — 拖进场景

T2 的 prefab 拖进 `TestEmpty.unity`，按 Play。

通过标准：点云完整显示、与切分前视觉一致；Console 无任何报错。
特别确认 prefab 里每个子 renderer 的 shader / compute 字段都自动填好了（不是 None）。

> **T1–T3 全过，说明导入链路和 prefab 自动接线是对的。**
> 任何一条不过，把 Console 原文贴回来再继续。

---

## 3. 真实资产

### T4 — Garden 5.76M

导入 `Botanical Garden - America v3_clean-edit.ply`，Quality=**Medium**，Quest 预设。

预期：切 **4** 片，每片 ≤ 1,677,568 splats。

> 提示：Medium 用的 SH 格式是 Norm6，不聚类，所以快。
> 如果改选 Cluster 系列（Low / VeryLow），**每片都要独立跑一次 K-means 聚类**，
> 6M splats 单次要 3–10 分钟，乘以片数。别以为卡死了。

### T6 — 块间排序方向（需要目视）

在编辑器里走到两片交界处观察。

通过标准：交界区域没有"前面的片被后面的片盖住"的明显穿帮。

**若有穿帮**：把 `package/Runtime/GaussianSplatRenderer.cs` 第 101 行
`return posA.z.CompareTo(posB.z);` 改成 `posB.z.CompareTo(posA.z)` 再验一次，
以视觉正确的方向为准，并在那行加注释说明"必须与 splat 内部 GPU 排序方向一致"。

---

## 4. 上 Quest

### 先做一个决策

`SplatMaterializeController` 和 `StylizedSplatsController`（在
`projects/GaussianExample-URP/Assets/Scripts/`）**各只引用一个 `GaussianSplatRenderer`**。
换成 4 片之后，reveal / 风格化效果只会作用在其中一片上。

两个选项：

- **A（快，功能降级）**：先只把脚本挂到主片，其余片不参与效果。标 TODO，先跑通 Quest。
- **B（干净）**：把这两个脚本改成接受 `GaussianSplatRenderer[]`，遍历下发参数。

建议先 A 跑通链路，确认切分本身没问题，再回头做 B。

### T5 — 场景迁移 + Build & Run

按 [splat-asset-split-plan.md](splat-asset-split-plan.md) §7 的步骤操作。要点：

1. 记录旧 `GaussianSplats` 物体的 Transform（**注意它有 180° X 翻转**）
2. 拖入 prefab，Transform 设到 prefab 根上
3. 渲染参数（`m_RenderMode` / `m_SplatScale` / `m_OpacityScale` / `m_SHOrder` / `m_SortNthFrame`）
   逐项设到**每个**子 renderer（多选子物体可一次改完）
4. Cutout（`GSCutout`）引用要加到**每个**子 renderer 的 `m_Cutouts` 列表
   （cutout 是世界空间椭球，对每片独立生效，语义不变）
5. 禁用/删除旧物体，保存场景

通过标准：`adb logcat` 无 `ArgumentException`、无 `NullReferenceException`、
无 `Render Graph Execution error`；头显中能看到天空盒、Cube 和 splat 场景。

### T7 — 防御检查生效（单独跑，很快）

故意把旧的 5.76M **单体** asset 挂回场景，Build & Run。

通过标准：Console/logcat 只出现**一条**清晰错误日志
（`... needs a 186MB GPU buffer but this device supports at most 128MB ...`），
天空盒和普通 Mesh **正常渲染** —— 不再整帧黑屏。

这条是运行时那 15 行改动的直接验证，与切分是否正确无关，可以最先跑。

### T8 — 内存实测（**目标已修正，见下**）

```bash
adb shell dumpsys meminfo <pid>
```
或看 logcat 的 `ClientSharedTelemetryStats`。

---

## 5. 内存事实修正（重要）

计划文档 §8 T8 里写的 "5.84M 单体时 406/336 MB = 121%，超预算" —— **这个"超预算"的判断不成立**，
后续不要拿它当决策依据。

Quest 3 **没有独立显存**，8GB LPDDR5 是 CPU/GPU 共享统一内存。三个完全不同的数字：

| | Quest 2 / Pro | Quest 3 / 3S |
|---|---|---|
| 物理 RAM | 6 GB / 12 GB | 8 GB |
| **应用可用预算（PSS kill limit）** | **4.4 GiB** | **5.75 GiB** |
| **单个 GraphicsBuffer 上限** | 128 MB | **128 MB** |

来源：[Meta — Memory / RAM](https://developers.meta.com/horizon/documentation/unity/po-memory-ram/)。
因为是共享内存架构，GLES/Vulkan 创建的纹理和 buffer 与 CPU 分配**算在同一个 PSS 里**，
没有单独的"显存预算"。第三行的 128 MB 是**单次分配**上限，与总量无关 —— 这正是本次切分解决的东西。

Garden 5.76M / Medium 的实际占用估算：

| | 字节/splat | 5.76M 合计 |
|---|---|---|
| 资产本体（pos 4 + other 8 + color 4 + SH 32） | 48 | ~276 MB |
| 运行时（view 40 + sort distances 4 + keys 4） | 48 | ~276 MB |
| FFX 排序 scratch | ~10 | ~58 MB |
| **合计** | ~106 | **~610 MB** |

**610 MB / 5.75 GiB ≈ 10%。内存不是瓶颈。**

那个 `336 MB` 计数器不是 PSS 预算（差一个数量级），具体口径未知。
**跑 T8 时请把 logcat 原始行抄回来**，确认它到底在计什么，再更新本节。

结论：Quest 独立版跑不动 5.76M splats 的真正原因是
（a）单 buffer 128 MB 上限 —— 本次切分已解决；
（b）每帧对 576 万 splat 做 GPU radix sort + 超大量半透明 overdraw 的算力问题。
**剩下的是性能调优，不是"内存不够只能抽稀"。**

---

## 6. 之后（二期，不阻塞）

按性价比排序：

1. **整块视锥剔除**（~20 行，切分带来的净收益）
   `GaussianSplatRenderSystem.GatherSplatsForCamera` 里对每个 renderer 用
   `GeometryUtility.CalculateFrustumPlanes(cam)` + `TestPlanesAABB` 判可见性
   （AABB 取 `asset.boundsMin/Max` 经 `transform.localToWorldMatrix` 变换），不可见则跳过。
   **背向的片连排序都省了。**
2. **错帧排序** — 给每片设不同的 `m_SortNthFrame` 相位，把排序开销摊到多帧
3. **OIT 方向** — 见 [mobile-gs-oit-plan.md](mobile-gs-oit-plan.md)，Phase 1 已结案，Phase 2 搁置中
4. **SOG 瓦片导入器** — 复用 PlayCanvas 的瓦片划分 + 6 级 LOD，需要 WebP 解码 + codebook 反量化，工作量大

另一条独立的路线：**PCVR 串流**（Quest Link / Air Link / Virtual Desktop）。
在 PC/Standalone 的 XR Plug-in Management 里启用 OpenXR、Play Mode Runtime 选 Oculus，
编辑器直接按 Play 就进头显，用 RTX 渲染几百万到上千万 splats。
如果 Quest 独立版调优成本太高，这是绕开整个问题的选项。

---

## 7. 回归确认

改完任何东西后，这两个场景的行为必须不变：

- `TestEmpty`（无 splat）
- Forest 里现有的小资产 `Victoria House 5%`（1.06M，不需要切分）

---

## 附：离线编译检查

另一台机器上如果想在不开 Unity 的情况下确认 `package/` 能编译：

- 编译器：`<Unity>/Editor/Data/MonoBleedingEdge/lib/mono/msbuild/Current/bin/Roslyn/csc.exe`，
  用 `MonoBleedingEdge/bin/mono.exe` 跑
- 命令行超长，用 response file，但 `-noconfig` 必须写在命令行上（写在 rsp 里会被忽略）
- 标志：`-nostdlib -target:library -langversion:preview -unsafe -define:UNITY_EDITOR`
- 引用：`Data/NetStandard/ref/2.1.0/netstandard.dll`
  + `Data/NetStandard/compat/2.1.0/shims/netfx/mscorlib.dll`（netfx shim，不是 netstandard shims）
  + `Data/Managed/UnityEngine/*.dll` 全部
  + `Library/ScriptAssemblies/*.dll`，但**排除 `GaussianSplatting.dll` 和 `GaussianSplattingEditor.dll`**
    （否则包内每个类型都和自己的预编译副本冲突）
- **不要**引 `Data/Managed/UnityEditor.dll` —— 它和模块化的 `UnityEditor.*Module.dll` 重复定义，
  会刷一屏 CS0433 把真错误淹掉
- exit 0 = 编译干净
