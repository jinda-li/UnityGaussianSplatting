# 大型 Gaussian Splat 资产自动切分方案（绕过 GraphicsBuffer 128 MB 上限）

状态：待实现
日期：2026-07-24
目标读者：任何实现者（人或 AI）。本文档包含全部背景、精确的文件/函数落点、算法、代码骨架、坑位说明和验收测试。按顺序实现即可运行。

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

**结论：绑定项是 `m_GpuView` 的 40 字节/splat。** 理论单 renderer 上限 3.35M splats；
留出 FFX scratch 与其他 buffer 的余量后，取安全系数 0.5：

```
maxSplatsPerPart = floor(134217728 × 0.5 / 40) ≈ 1,677,721  →  取整为 1,600,000
```

5.84M 的 Forest 切成 4 片，5.76M 的 Garden 切成 4 片。

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
    // 对齐到 kChunkSize(256)，让每片的压缩 chunk 都是完整的
    result = result / GaussianSplatAsset.kChunkSize * GaussianSplatAsset.kChunkSize;
    return (int)math.min(result, int.MaxValue);
}
```

数值感受：`Medium` 质量（Norm11/Norm11/Norm8x4/Norm6）+ 128 MB + 0.5 → **1,677,568**。

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

## 4. Runtime 改动：过大资产的防御检查

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

## 6. 可选优化（二期，不阻塞主线）

1. **整块视锥剔除**：`GatherSplatsForCamera` 里对每个 renderer 用
   `GeometryUtility.CalculateFrustumPlanes(cam)` + `TestPlanesAABB`（AABB 取
   `asset.boundsMin/Max` 经 `transform.localToWorldMatrix` 变换）判定，不可见则跳过。
   背向的片连排序都省了，是切分带来的净收益。
2. **错帧排序**：给每片设不同的 `m_SortNthFrame` 相位，把排序开销摊到多帧。
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
| T8 | `adb shell dumpsys meminfo <pid>` 或 logcat 的 `ClientSharedTelemetryStats` | GPU 内存不超预算（之前 5.84M 单体时为 406/336 MB = 121%，切分本身不减总量，若仍超预算需换更小 LOD——切分解决的是"单 buffer 上限"，不是总显存） |

回归确认：`TestEmpty`（无 splat）与 Forest 现有小资产（`Victoria House 5%`，1.06M，无需切分）行为不变。

---

## 9. 明确不做的事

- 不在单个 renderer 内部做多 bank buffer 拼接（需要重写排序器与所有 compute 的寻址，收益不成比例）。
- 不做运行时动态切分或流式加载。
- 不改 `GaussianSplatAsset` 序列化格式（`kCurrentVersion` 不变，旧资产完全兼容）。
- 不处理跨片的全局 splat 精确排序——块间近似顺序即可（见 §5）。
