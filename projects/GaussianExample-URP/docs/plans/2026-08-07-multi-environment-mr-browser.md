# GardenMR → 多环境 MR 浏览器

> 状态：待实施 · 2026-08-07
> v3：**一个环境 = 一个 Unity 场景**，菜单直接切场景。（v1 单永久 renderer 换 asset、v2 prefab 整体切换，均已废弃 —— 理由见「为什么是场景」）
> 前置文档：[2026-07-30-tabletop-to-immersive.md](2026-07-30-tabletop-to-immersive.md)（当前单环境流程的设计）

## Context

GardenMR 现在是单环境 demo：MR 桌面预览 → 点击 SpawnPoint → 第一人称沉浸 → Return 回桌面。目标是把它扩展成一个 VR app：用户在 MR 中鸟瞰多个 3DGS 环境（植物园 / 工厂 / 教学楼疏散演练），左手 menu 键调出菜单，选一个直接切过去。

关键的产品前提是**每个环境的玩法、shader、甚至 player 都可能不同**：

- 植物园（`GardenAmericaSplat` 那套）基础显示是休眠粒子，靠甩彗星／喷枪唤醒上色，用 `SplatMaterialize.shader` + `SplatMaterializeController` + `SplatCometBrush` + `SplatPaintBloom`。
- 工厂之类的场景在 MR 里就是完整实拍呈现，用原生 `RenderGaussianSplats.shader`，进去之后是标记、拿锤子这类工具类交互，可能还要一个带工具腰带的 player。

所以不试图在一个场景里统一管理所有 demo。**每个环境独立开发成一个场景，场景里只有一个 `GaussianSplatRenderer`。**

本次交付：**共享外壳 + 场景切换 + 植物园作为第一个场景 + 新增环境的操作文档**。Forest / Victoria House / 工厂的内容由用户按文档制作（会提供一个最小 stub 场景用于验证切换）。

---

## 为什么是场景，不是 prefab 或 asset 切换

| 方案 | 致命复杂度 |
|---|---|
| **v1** 单永久 renderer，只换 `m_Asset` | `Update()` 不重建材质（`GaussianSplatRenderer.cs:504-512, 698-715`），换 shader 必须反射清 `internal` 的 `m_MatSplats`；渲染驱动要一层 `SplatRenderMode` 抽象管全局态；环境组件要镜像十几个 renderer 参数 |
| **v2** 环境 prefab 整体销毁重建 | 上面三条消掉了，但引入 `Resources.Load` 手动内存管理（`GaussianSplatAsset` 的 5 个 `TextAsset` 是及物引用链，植物园一套 ≈ 24 MB）、编辑器预览实例的保存剥离钩子、场景依赖静态扫描。三层脚手架只为绕开 Unity 本来就有的资源边界 |
| **v3 场景** | `LoadSceneAsync(Single)` 是 Unity 原生的资源边界：旧场景全部资产自动卸载，新场景按需加载。编辑器里想看哪个环境就打开哪个场景。player / shader / 玩法组件各场景自由。**上面两栏的脚手架全部不存在** |

代价只有两条，都可接受：切换从几百毫秒变成 1–3 秒的完整场景重载（MR 里淡到 passthrough，体感是「模型消失了，房间还在」）；场景间共享的东西靠 prefab 纪律维持，没有编译期保障（3–5 个场景没问题，上到十几个要重新考虑）。

---

## 已验证的技术约束（不要再推翻）

| 事实 | 出处 | 后果 |
|---|---|---|
| `Shader.SetGlobal*` **不随场景卸载重置** | Unity 语义 | 从植物园切到工厂，`_Dormant*` / `_Style*` / `_Pop*` 一整套全局值原样保留。**场景切换不帮你解决这个** —— 必须在新场景开局主动复位（见 §3） |
| `LoadScene(Single)` 会连 XR Origin 一起销毁重建 | Unity 语义 | VR 里会追踪重置、画面跳变。需要一个 `DontDestroyOnLoad` 的过渡对象负责淡出/淡入（见 §2） |
| `GaussianSplatAsset` 持 5 个 `TextAsset`；植物园一套 `.bytes` = shs 16 MB + oth 3.9 MB + col 2.0 MB + pos 2.0 MB + chk 0.12 MB ≈ **24 MB** | `package/Runtime/GaussianSplatAsset.cs:210-215`、`Assets/GaussianAssets/` 实测 | 序列化引用链是及物的。共享外壳 prefab 在**每个场景里都有一份**，所以它（或它引用的 catalog）**绝不能持有任何 `GaussianSplatAsset` 引用**，否则每个场景启动都把所有环境拉进内存。菜单只存场景名字符串 + 缩略图 |
| 渲染驱动全靠 `Shader.SetGlobal*`，无 per-material 隔离 | `Assets/StylizedSplats/Scripts/StylizedSplatsController.cs:136-162`、`Assets/GardenAmericaSplat/Scripts/SplatMaterializeController.cs:304-333` | 一个场景内同时只能有一个渲染驱动活跃。场景独立后天然满足 |
| splat 走独立 RT + `Blitter.BlitCameraTexture`，**不写深度** | `GaussianSplatURPFeature` / `RenderGaussianSplats.shader`（`ZWrite Off`） | 世界空间 Canvas 永远不会被 splat 遮挡，但**会被地面 `Plane` 的 MeshRenderer 裁掉** |
| `Activate` 和 `UI Press` 绑定同一个左扳机 | `XRI Default Input Actions.inputactions` | 点菜单按钮会同时触发身后 SpawnPoint 的 Dive，**必须防护**（见 §7） |
| `<XRController>{LeftHand}/menuButton` 空闲 | 全项目 grep 无绑定 | 用于**调出菜单**。Summon 因此改为菜单里的一个按钮（见 §9） |
| 项目无 ProBuilder、无中文字体 | `Packages/manifest.json`、`Assets/TextMesh Pro/` | 手柄用 primitive 拼；菜单**纯图标无文字** |
| 场景必须进 Build Settings 才能 `LoadScene` | Unity 语义 | 「新增环境」文档里列为必做步骤 |

---

## 架构

**一个环境 = 一个场景。场景之间靠一个共享外壳 prefab 保持一致，靠一个跨场景过渡对象保持连续。**

```
Assets/Scenes/MR/
  MR_BotanicalGarden.unity
  MR_ForestStub.unity          ← Phase 2 验证用
  MR_Factory.unity             ← 后续

每个环境场景的内容（就这些，扁平）：
  [GardenMR_Shell]  prefab 实例        ← 共享，实例上覆盖环境专属值
  [Player]          prefab 实例        ← 各场景可换（工厂用带工具腰带的变体）
  GaussianSplats
    [GaussianSplatRenderer]  m_Asset / m_ShaderSplats / m_SplatScale …
    [该环境专属的渲染驱动]    植物园 = SplatMaterializeController；工厂 = 无
    [该环境专属的交互组件]    植物园 = SplatCometBrush + SplatPaintBloom；工厂 = MarkerTool …
    Plane / GSCutout / SpawnPoints / CollisionProxy
  AR Session / Directional Light
```

`GardenMR_Shell.prefab` 里装每个场景都一样的那套：

```
GardenMR_Shell
  GardenMRRig
    PlaceModeHandles                  ← EnterImmersive 时整体 SetActive(false)
      MoveHandle    [重做：胶囊抓握条 + BoxCollider]
      ScaleHandle   [重做：L 形直角括号 + BoxCollider]
  Systems
    TabletopDiveController            ← 环境专属值（m_FloorLocal / 缩放范围 / ambience）在实例上覆盖
    SplatGlobalsReset                 ← Awake 复位全部 splat 全局
    EnvironmentMenu   [World Space Canvas，默认隐藏，menu 键调出]
    EventSystem + XRUIInputModule
```

**必须是 prefab**，否则改一次手柄要改 N 个场景。环境专属的差异走 prefab 实例覆盖；player 的差异走独立 prefab（或 prefab variant）。

跨场景存活的只有一个对象：

```
[DontDestroyOnLoad]
  SceneTransition                     ← 淡出 → LoadSceneAsync → 淡入
```

菜单本身随场景销毁，不需要跨场景。

---

## 文件清单

### 新增
| 路径 | 内容 |
|---|---|
| `Assets/GardenMR/Prefabs/GardenMR_Shell.prefab` | 共享外壳，见上 |
| `Assets/GardenMR/Scripts/EnvironmentCatalog.cs` | **ScriptableObject**：`List<Entry>{ m_SceneName, m_Thumbnail, m_DisplayId }`。一份资产，被 Shell prefab 引用。**严禁出现 `GaussianSplatAsset` 字段** |
| `Assets/GardenMR/EnvironmentCatalog.asset` | catalog 实例 |
| `Assets/GardenMR/Scripts/SceneTransition.cs` | `DontDestroyOnLoad` 单例。`LoadEnvironment(string sceneName)`：锁输入 → 淡出到 passthrough → `LoadSceneAsync` → 淡入。自身在首个场景 `Awake` 时自建 |
| `Assets/GardenMR/Scripts/SplatGlobalsReset.cs` | 挂 Shell，`Awake` 把两套渲染驱动写过的全部 global 写回默认值。清单以各 controller 的 `Props` 静态类为准 |
| `Assets/GardenMR/Scripts/EnvironmentMenu.cs` | menu 键开关面板；从 catalog 渲染缩略图列表；点一张 → `SceneTransition.LoadEnvironment`；当前场景那张标为已加载且 `interactable = false` |
| `Assets/GardenMR/Scripts/UIButtonMotion.cs` | `IPointerEnter/Exit/Down/Up` → `SmoothDamp` localScale 1.0 / 1.04 / 0.97 |
| `Assets/GardenMR/Scripts/UIHoverHaptics.cs` | 挂 NearFarInteractor，订阅 `uiHoverEntered` → `HapticImpulsePlayer.SendHapticImpulse` |
| `Assets/GardenMR/Scripts/HandleVisualState.cs` | 手柄 idle/hover/select 的颜色+自发光+缩放，走 `MaterialPropertyBlock` |
| `Assets/GardenMR/Shaders/UI-Passthrough.shader` | `UI-Default` 的拷贝，改 1 行混合 + 加圆角 SDF（见 §5） |
| `Assets/GardenMR/Materials/M_HandleChrome.mat` `M_UIPassthrough.mat` | URP/Lit 手柄材质、UI 材质 |
| `Assets/Scenes/MR/MR_BotanicalGarden.unity` | 由现有 `GardenMR.unity` 改造而来 |
| `Assets/Scenes/MR/MR_ForestStub.unity` | 最小 stub：asset + 占位地面 + 一个 spawn point + **原生 shader、无渲染驱动**（故意与植物园不同，用来验证全局态隔离） |
| `docs/plans/gardenmr-add-environment.md` | 「如何新增一个环境」逐步文档 |

### 修改
| 路径 | 改动 |
|---|---|
| `Assets/GardenMR/Scripts/TabletopDiveController.cs` | `SetVignette/SetRigGrabEnabled/SetSplatGroundColliders/SetActive` 提升为 public（`SceneTransition` 与菜单要用）；新增 `m_TransitionLock`/`IsBusy`/`Begin\|EndExclusiveTransition`；`TryComputePlacementPose`/`SummonRig`（见 §9）；**删除 `EnsureVrReturnBindings()` 及其调用**；`ValidateSpawnPoints()` 启动自检（见 §4） |
| `Assets/GardenMR/Scripts/SplatHandleRig.cs` | `m_FloorLocal` + `SetFloorLocal()`，`PivotWorld`/`ApplyScale`/`InitializeFromScene` 改为绕任意地面点（见 §4） |
| `Assets/GardenMR/Scripts/SplatSpawnPoint.cs` | `ApplyColor` 改 `MaterialPropertyBlock`（现在每次都泄漏一个 Material）；`OnActivated` 加 UI 命中防护 + `IsBusy` 防护 |
| `Assets/GardenMR.unity` | 拆分为 `Assets/Scenes/MR/MR_BotanicalGarden.unity`，共享部分替换为 Shell prefab 实例 |
| `ProjectSettings/EditorBuildSettings.asset` | 登记所有 `Assets/Scenes/MR/*.unity` |

---

## 关键实现细节

### 1. 场景切换流程

```csharp
// SceneTransition（DontDestroyOnLoad 单例）
public void LoadEnvironment(string sceneName)
{
    if (m_Busy) return;
    StartCoroutine(Routine(sceneName));
}

IEnumerator Routine(string sceneName)
{
    m_Busy = true;
    controller.BeginExclusiveTransition();      // 锁 Dive / 抓取 / 菜单
    yield return FadeOut();                     // 渐晕收到全闭，露出 passthrough
    var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
    while (!op.isDone) yield return null;
    yield return null; yield return null;       // 空跑 2 帧，等新场景 Awake/Start 与首帧渲染
    yield return FadeIn();
    m_Busy = false;
}
```

淡出**淡到 passthrough 而不是黑屏** —— MR 里用户看着真实房间等待 1–3 秒完全可接受，黑屏则不适。渐晕复用 `TabletopDiveController` 已有的 `ApertureOpen`/`ApertureClosedMin`，所以这两个要提升为 public。

`SceneTransition` 自身的渐晕材质/Canvas 也在 `DontDestroyOnLoad` 对象上，否则淡出做到一半就被场景卸载连带销毁。

### 2. 共享外壳的纪律

`GardenMR_Shell.prefab` 是唯一的一致性保障，没有编译期约束。三条规矩写进新增环境文档：

- 场景里**只能有一个** Shell 实例，且**只能靠实例覆盖**改环境专属值（`m_FloorLocal`、`m_MinScale`/`m_MaxScale`、ambience clip）。改行为要回 prefab 改。
- 场景里**只能有一个** `GaussianSplatRenderer`。
- Player 是独立 prefab，允许各场景不同；但 XR Origin / 输入 action 资产必须是同一份，否则手柄绑定会在某个场景静默失效。

### 3. 全局 shader 状态复位（必做，最容易漏）

`Shader.SetGlobal*` 是进程级的，`LoadScene(Single)` **不重置**。从植物园切到没有渲染驱动的工厂，工厂会继承休眠点效果和 stylize 参数。

`SplatGlobalsReset` 挂在 Shell 上，`Awake` 里把 `SplatMaterializeController.Props` 与 `StylizedSplatsController.Props` 列出的键**全部**写回默认。每个场景开局都干净；有渲染驱动的场景随后自己 push 一套覆盖掉。

顺序是安全的：`LoadSceneAsync(Single)` 先卸旧场景（跑完 `OnDisable`/`OnDestroy`）再建新场景，所以 `Awake` 里复位不会被旧场景的收尾覆盖。

一个组件管所有场景，比让每个 controller 各自写 `ResetGlobals()` 更可靠 —— 不依赖每个 controller 是否写全。

### 4. `SplatHandleRig` 地面枢轴泛化 + spawn point 自检

**地面枢轴**：现在 `PivotWorld => m_SplatRoot.position` 且 `ApplyScale` 硬钉 `localPosition = Vector3.zero` —— 只对「地面恰好在 asset 局部原点」的植物园成立。改为：

```csharp
public Vector3 PivotWorld => m_SplatRoot ? m_SplatRoot.TransformPoint(m_FloorLocal) : Vector3.zero;
// ApplyScale 内：
m_SplatRoot.localPosition = -Vector3.Scale(scaleVec, m_FloorLocal);   // 把地面点钉在 rig 原点
```

`m_FloorLocal = Vector3.zero` 时与现状逐位一致，可在 Phase 1 无风险落地。`TabletopDiveController` 的 dive/return 数学本来就是 anchor 相对的，不受影响。每个新场景量一次这个值。

**桌面尺寸约定**（authoring 约定，不做成代码）：所有环境把桌面上的可见横向跨度调到 **0.75 m**，手柄固定在 `±0.4` 才对每个环境都在合理位置。植物园现在 `m_SplatScale = 0.0375` × 地面 20 单位 = 0.75 m，即现状。

**spawn point 自检**：`TabletopDiveController.ValidateSpawnPoints()` 在 `Start` 跑一次 —— 每个 spawn point 向下 `Physics.Raycast` 1 m，未命中则 `LogWarning` 点名；`m_SpawnPointsGroup` 一个 `SplatSpawnPoint` 都没有则 `LogError`；`DiveFromShortcut()` 找不到目标时也记一条（现在是静默 return）。硬性约束写进新增环境文档：spawn point 的 y 必须落在 `CollisionProxy` 网格上方 ≤5 cm，否则 Dive 后玩家悬空自由落体或卡在几何内。

### 5. Passthrough alpha —— 最高风险项

Meta passthrough 是 underlay，Unity 提交**预乘 alpha** 的投影层：`final = eye.rgb + passthrough.rgb*(1-eye.a)`。管线 `Medium_PipelineAsset` 无 HDR，颜色目标是 RGBA8，alpha 通道真实存在且 `EnforcePassthroughCamera` 保证 post-processing 关闭。

Unity 内置 `UI/Default` 用 `Blend SrcAlpha OneMinusSrcAlpha` 作用于**全部四个通道**，于是 `dst.a = a²` 偏低 → 面板不会变成灰块，而是**发虚过亮、真实房间透过来 28% 而不是 15%**，且嵌套 Image 逐层放大误差。修法是拷一份 `UI-Default.shader` 只改一行：

```hlsl
Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
```

其余（`Stencil` 块、`ColorMask [_ColorMask]`、`ZTest [unity_GUIZTestMode]`、`UNITY_UI_CLIP_RECT`/`UNITY_UI_ALPHACLIP` 关键字）**必须逐字保留**，否则 Mask / RectMask2D 失效。TMP 的 `TMP_SDF.shader` 本来就是 `Blend One OneMinusSrcAlpha`（已正确），无需处理。

**Phase 2.5 先做设备验证再动菜单**：裸 Canvas 上放两个 100×100 白色 Image（α 1.0 / α 0.5）对着中灰墙看 —— 修正后 0.5 那块应读作墙与白的精确中点。若读作偏暗，说明运行时提交的是非预乘，改用 `Blend One OneMinusSrcAlpha` 并在片元里预乘顶点色。

### 6. 菜单（纯图标，menu 键调出）

原 v1/v2 的「浏览候选 → 确定按钮 → 圆点指示器」两段式**全部砍掉**。场景切换本来就是一次明确的重操作，缩略图卡片够大就不会误触，误触了切回来即可。

**交互**：左手 `menuButton` 开/关面板。打开时面板出现在头部正前方固定位姿（`head.position + head.forward*1.0`，去掉 pitch/roll 只保留 yaw，然后**锁定不再跟头**）。面板上是一行缩略图卡片，点一张 → `SceneTransition.LoadEnvironment(entry.m_SceneName)`。当前场景那张画一圈 `#FFFFFF` α0.5 描边环并 `interactable = false`。再加一个「召唤模型」按钮（见 §9）和关闭按钮。

`EventSystem` + `XRUIInputModule` 挂 Shell 的 `Systems` 下。XRI 3.3.2 只需 `Enable XR Input = true`，其余 mouse/touch/gamepad/joystick/builtin-fallback 全关，action 引用全空。`activeInputMode` 在 3.3.2 已 `[Obsolete][HideInInspector]`，不要碰；3.x 没有 `uiCamera` 字段。NearFarInteractor 的 `m_EnableUIInteraction: 1` 会自行注册。

| 项 | 值 |
|---|---|
| `sizeDelta` / `localScale` | `1200×420` / `0.0005` → **0.60 m × 0.21 m** |
| 打开位姿 | `head.position + yawOnly(head.forward) * 1.0`，面向头部 |
| RenderMode / worldCamera | WorldSpace / 显式指向 XR Main Camera |
| CanvasScaler | `dynamicPixelsPerUnit = 3`，`referencePixelsPerUnit = 100` |
| `TrackedDeviceGraphicRaycaster` | `ignoreReversedGraphics = true`（prefab 默认 0，要改）、2D/3D 遮挡检查关 |

**面板不能落在地面 `Plane` 的足迹内**（splat 不写深度，但 Plane 的 MeshRenderer 会把 Canvas 裁掉）。打开时若算出的位姿落在 rig 的 ±0.56 m 足迹内，把距离推到 1.0 m 之外或抬高到 0.40 m。

**视觉（Horizon OS 取值）**：面板 `#16181B` α0.80 / 圆角 24 px / 描边 `#FFFFFF` α0.12 1.5 px；accent `#0064E0`，hover `#1D8AFF`；卡片 idle 填充 `#FFFFFF` α0.08、hover α0.16 + scale 1.04、pressed α0.28 + scale 0.97、disabled α0.04。**不要做投影** —— 阴影恰好在你想要 passthrough 的地方写 alpha，在真实房间上读作灰泥。圆角直接加进 `UI-Passthrough.shader` 的 SDF（`_Radius`/`_StrokeWidth` 两个属性，约 8 行片元代码），比九宫格 sprite 更清晰且零资源。缩略图与面板底板 `raycastTarget = false`，只有卡片和按钮是 UI 命中目标。

### 7. 菜单误触 Dive 的防护（必做）

`Activate` 与 `UI Press` 是同一个左扳机，且 `NearFarInteractor.Process3dHit` 在射线命中 UI 时**不会**抑制 3D 目标注册，叠加 `m_AllowHoveredActivate: 1` → 点菜单卡片会同时向身后的 `SplatSpawnPoint` 派发 `activated`。菜单现在是浮动面板、位姿不固定，几何上挡不住，所以这段防护从「保险」升级为**唯一依靠**：

```csharp
if (args.interactorObject is NearFarInteractor nf && nf.TryGetCurrentUIRaycastResult(out _))
    return;
if (m_Controller && m_Controller.IsBusy) return;
```

菜单打开期间额外调 `BeginExclusiveTransition()` 的轻量版（只锁 Dive，不锁抓取），双保险。

### 8. 手柄重做（无 ProBuilder，只用 primitive）

Horizon OS 的实际语汇：窗口下缘一条浅灰**胶囊抓握条**（窗体本身不可抓）+ 角上一个**直角括号**（描边非实心）。hover 提亮至纯白 + 细蓝边光 + 放大 1.05×，press 蓝色饱和 + 缩到 0.96×，缓动 80–100 ms。

- **MoveHandle** → `localPosition (0,0,-0.46)`（近边缘，手自然伸到的位置）；子物体 `Bar` = Capsule primitive，`localRotation (0,0,90)`、`localScale (0.022,0.18,0.022)` → 0.18 m 长 22 mm 粗；`BoxCollider size (0.22,0.055,0.055)`（比视觉更胖，远射线好瞄）。
- **ScaleHandle** → `localPosition (0.44,0,-0.44)`；两个 Cube 拼 L：`ArmX pos(0.035,0,0) scale(0.09,0.016,0.016)`、`ArmZ pos(0,0,0.035) scale(0.016,0.016,0.09)`；`BoxCollider center(0.03,0,0.03) size(0.12,0.06,0.12)`。
- 必须重连 `GardenMRRig.XRGrabInteractable.m_Colliders`（现在指向旧 SphereCollider）与 `attachTransform`；删掉旧 SphereCollider，否则 `SplatScaleHandle.EnsureCollider()` 会留下两个碰撞体。
- 材质 `M_HandleChrome`：URP/Lit **Opaque**（写 alpha=1，干净地在 passthrough 上开洞）、`_BaseColor #C8CDD4`、metallic 0、smoothness 0.62、**材质上启用 Emission 关键字但 `_EmissionColor` 设黑** —— `MaterialPropertyBlock` 无法设置 shader 关键字，只能设值，这是 MPB 驱动 URP/Lit 发光的必要前提。

`HandleVisualState.m_Source` 对 MoveHandle 而言是 **GardenMRRig 上的 `XRGrabInteractable`**（MoveHandle 自己没有 interactable）。这不是近似 —— 该 interactable 的 collider 列表**只**包含 MoveHandle 的碰撞体，所以「rig 被 hover」逻辑上等价于「MoveHandle 被 hover」。在类注释里把这条写成 invariant。用 `m_HoverCount` 计数而非 bool（多 interactor 会成对触发）。`m_ScaleTarget` 指向视觉子物体而非手柄根，否则碰撞体会跟着呼吸。

由于手柄在 Shell prefab 里，这一节改一次，所有场景生效。

### 9. Summon（改为菜单按钮）

`menuButton` 现在归菜单，Summon 改成**菜单里的一个按钮** + `<Keyboard>/c`（Editor 测试用）。`EnsureVrReturnBindings()` 仍然要**删掉而不是修好**：它现在因为场景里已序列化了 `secondaryButton` 而提前返回（属于死代码），若「修好」成检查 `menuButton` 会与菜单正面冲突。

把 `PlaceRigInFrontOfPlayer()` 拆成纯计算 `TryComputePlacementPose(out pos, out rot)` + 瞬移设值 + `SummonRig()` 协程。

关键设计：**目标位姿只在开始时算一次**（追头会橡皮筋且永不收敛）；用 `Vector3.Slerp` 插值「相对头部的偏移」而非世界坐标 `Lerp` —— 从背后召唤时会绕着头划弧而不是穿脸而过；ease-out cubic（快出软落）；时长 `dist × 0.45 s/m` 钳在 `[0.28, 0.70]`；开头调 `SetRigGrabEnabled(false)` 强制解除抓取（否则 `XRGeneralGrabTransformer` 每帧跟协程打架，最后一帧弹回手柄）；全程 `m_TransitionLock = true` 阻止 Dive 与场景切换。

---

## 分阶段实施

场景里已有 `XR Device Simulator` prefab（当前 disabled），Phase 1/2/4 启用它在 Editor 内测。

**Phase 0 · 基线**
模拟器进 Play，确认放置/抓取/缩放/Dive/Return 全部正常，截图存档，作为后续「无行为变化」的对照。

**Phase 1 · 安全重构，零行为变化**
`SplatHandleRig` 地面枢轴泛化（`m_FloorLocal=0` 逐位一致）；`SplatSpawnPoint` 改 MPB + 加 UI/IsBusy 防护；`TabletopDiveController` 方法提升 public、`m_TransitionLock`/`IsBusy`、暴露 `ApertureOpen/ApertureClosedMin`、加 `ValidateSpawnPoints`、删 `EnsureVrReturnBindings`。全部在现有 `GardenMR.unity` 里做。
**验证**：与 Phase 0 截图逐项对照。

**Phase 2 · 场景化 + 切换（无 UI）— 最高价值里程碑，桌面可完整验证**

1. 把 `GardenMR.unity` 里的共享部分（GardenMRRig / PlaceModeHandles / handles / TabletopDiveController / AR Session）提取为 `GardenMR_Shell.prefab`，另存场景为 `Assets/Scenes/MR/MR_BotanicalGarden.unity`。
2. `SplatGlobalsReset` + `SceneTransition`（含淡出到 passthrough 的渐晕，`DontDestroyOnLoad`）。
3. `EnvironmentCatalog` ScriptableObject，先填两条。
4. 建 `MR_ForestStub.unity`：Shell 实例 + player + asset + 占位地面 + 一个 spawn point，**用原生 shader、无渲染驱动**。
5. 两个场景登记进 Build Settings。
6. 临时调试键：`1`/`2` 直接 `SceneTransition.LoadEnvironment(...)`。

**验证**：
- 两场景来回切 10 次：无 NullReference；淡出淡入连贯、无黑屏跳变；切换后 Dive→Return 正常；Dive 途中切换被拒绝。
- **全局态隔离**：切到 Forest stub 后不得残留植物园的休眠点效果 / stylize 参数；切回来后彗星涂色仍正常。这是本阶段第一优先验证项。
- **内存**：Memory Profiler 抓三张快照（植物园 → stub → 植物园），确认旧场景的 24 MB 每次都被卸载、不累积。
- Profiler 量一次切换的墙钟时长，判断 1–3 秒的预期是否成立。
- Shell prefab 改一处（比如手柄颜色），确认两个场景同时生效。

**Phase 2.5 · 设备上验证 passthrough alpha（20 分钟，先于菜单）**
写 `UI-Passthrough.shader`，裸 Canvas 放 α1.0/α0.5 两个白块，构建到 Quest 3 对着中灰墙做中点测试。**这是唯一真正未知的风险，不通过不进 Phase 3。**

**Phase 3 · 菜单**
圆角 SDF 并入 shader + `M_UIPassthrough`；`EventSystem` + `XRUIInputModule`；按 §6 搭建 Canvas（缩略图卡片行 + 召唤按钮 + 关闭按钮，全 Image）；`EnvironmentMenu` + `UIButtonMotion` + `UIHoverHaptics`；绑 `menuButton` 开关；移除临时键盘绑定。
**设备验证**：menu 键开关面板、面板出现在正前方且不跟头、射线能命中卡片、hover/press 视觉与震动、当前场景那张为 disabled、点卡片后按钮立即变灰不可重入、以及 —— **明确测试**对着卡片扣扳机不会触发 Dive。

**Phase 4 · Summon**
`TryComputePlacementPose`/`SummonRig`/`SummonRoutine`，接上菜单里的召唤按钮 + `<Keyboard>/c`。
**验证**（先模拟器）：抓取中召唤、连按两次、背对模型召唤（确认是绕弧不是穿脸）、切换场景途中召唤必须被拒绝。

**Phase 5 · 手柄重做**
`M_HandleChrome`、MoveHandle 胶囊条、ScaleHandle L 括号、`XRGrabInteractable.m_Colliders` 与 `SplatScaleHandle` 重连、两处挂 `HandleVisualState`。全部在 Shell prefab 里做。
**验证**：抓取/缩放行为不变，hover/press 状态正确，碰撞体不随 hover 缩放而呼吸；两个场景都对。

**Phase 6 · 收尾与交接**
写 `docs/plans/gardenmr-add-environment.md`：复制模板场景 → 换 asset → 量 `m_FloorLocal` 与桌面 0.75 m 跨度 → 摆 GSCutout / CollisionProxy / SpawnPoints → 选 shader 与渲染驱动 → 选 player prefab → 出缩略图 → 加进 `EnvironmentCatalog` → **登记 Build Settings**。附三条 Shell 纪律（§2）与 spawn point 硬约束（§4）。Forest / Victoria House 的正式内容按此文档制作。

**Phase 7 · 工厂玩法（后续）**
新建 `MR_Factory.unity`，原生 shader、带工具腰带的 player 变体、标记工具 / 锤子。全部是场景内自由发挥，不需要改任何共享代码 —— 这一阶段用来检验 v3 的核心假设是否成立。

---

## 验证方式

- **Editor**：启用 `XR Device Simulator`，键盘 `D`(Dive) / `R`(Return) / `1`·`2`(切场景，临时) / `C`(Summon)。每个 Phase 结束跑一次完整回路：放置 → 缩放 → Dive → 走动 → Return → 切场景 → 再来一遍。
- **编译**：改脚本后用 Unity MCP 的 `read_console` 确认无编译错误再继续；用 `editor_state` 的 `isCompiling` 判断域重载结束。
- **内存**：Memory Profiler 三点快照法（见 Phase 2），确认场景卸载真的还了内存、反复切换不累积。
- **性能**：Phase 2 量单次切换墙钟时长；Phase 3 之后在 Quest 3 上用 OVR Metrics 看稳态帧时间没有回退（当前基线 32.3 ms，见 `docs/splat-asset-split-plan.md` §0.3.1）。
- **设备**：Phase 2.5 与 Phase 3 必须真机验证 —— passthrough alpha 与 UI 射线交互在 Editor 里都测不出真实表现。

---

## 残留风险

| 风险 | 缓解 |
|---|---|
| `SplatGlobalsReset` 漏了某个 global，切场景后视觉残留 | 头号风险。以各 controller 的 `Props` 静态类为清单逐项对照；Phase 2 的植物园 ↔ Forest stub 来回切是专门为此设计的 |
| 有人往 `EnvironmentCatalog` 或 Shell prefab 上加了 `GaussianSplatAsset` 引用 | Shell 在每个场景都有一份，一旦引用就是每场景全量加载。catalog 只存 `string` + 缩略图；code review 时盯住这一条 |
| Shell prefab 的一致性没有编译期保障，某个场景偷偷改了实例结构 | 三条纪律写进新增环境文档；场景数控制在 3–5 个 |
| 切换时长超出 1–3 秒预期，MR 里等待过久 | 淡到 passthrough 而非黑屏，等待体感可接受；真机实测若仍过长，退路是把 `.bytes` 拆分/降 SH 阶（见 `docs/splat-asset-split-plan.md`） |
| `LoadScene` 后 XR 追踪/passthrough 需要重新初始化，出现闪烁 | `SceneTransition` 在 `LoadSceneAsync` 完成后空跑 2 帧再淡入；真机实测若不够就加到 4 帧 |
| 投影层实为非预乘 → 面板偏暗而非偏亮 | Phase 2.5 中点测试一次构建即可区分，备用混合式已给出 |
| 菜单是浮动面板，几何上挡不住误触 Dive | §7 的 UI 命中防护成为唯一依靠，Phase 3 必须明确测试；菜单打开时额外锁 Dive |
| 面板落在地面 `Plane` 足迹内被深度裁掉 | 打开时检测 rig 的 ±0.56 m 足迹，落在里面就推远或抬高 |
| 有人给 `GardenMRRig.XRGrabInteractable` 加第二个 collider | `HandleVisualState` 的 hover 语义会静默失效 —— 类注释写成 invariant |
| 新场景的 spawn point 没落在碰撞网格上 → Dive 后悬空/卡住 | `ValidateSpawnPoints` 向下射线校验并 LogWarning 点名；新增环境文档里列为硬性约束 |
