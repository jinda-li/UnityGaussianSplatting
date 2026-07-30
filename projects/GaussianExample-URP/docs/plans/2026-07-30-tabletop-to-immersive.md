# MR 鸟瞰摆放 → 穿越进入 1:1 沉浸（GardenMR）

2026-07-30 起草，同日按摆放/缩放交互、cutout 硬切、花园示范三轮需求修订。

目标：MR 中把一个缩比的真实场景（splat）**自由摆到真实桌面上**，缩放到合适的鸟瞰大小，然后用控制器指向场景内部的出生点、扣扳机 → 世界快速膨胀，玩家站进那个位置，用现有 locomotion 走动。

**工作场景**：`Assets/GardenMR.unity`
**示范资产**：先用已验证的花园（`Assets/GaussianAssets/Botanical Garden - America v3_clean-edit_clean.asset` 一类）做通路示范。场景里现有的 `GaussianSplatsIVictoriaHouse` 留作第二步——室内一体扫描才是这个玩法的目标形态，但通路先用跑得通的资产验证。

---

## 0. 架构约束（硬性）

**本方案零 package 改动、零现有脚本改动。** 只新增脚本 + 场景配置。

明确不碰：
- `package/`（渲染管线、shader、compute、GaussianSplatRenderer）
- `Assets/StylizedSplats/Scripts/StylizedSplatsController.cs`、`SplatWorldReveal.cs`、`VRSplatBrush.cs`
- `Assets/GardenAmericaSplat/Scripts/`（materialize 玩法）

这不是事后加的约束，是设计本来就成立的：第 2.1 / 2.6 节的结论全部是"利用 package 的现有行为"，而非"修改它"。新脚本只做三件事——改 transform、改 `cutout.enabled`、开关别的组件。

**为什么与 StylizedSplatsController 天然无冲突**：它走的是材质/compute 的 shader property（`_StylizedEnable` 等，`StylizedSplatsController.cs:216+`），还通过反射读 `GaussianSplatRenderer` 的私有字段（`:64`）。本方案完全不触碰材质、compute、shader property 或那些私有字段，两者作用面不相交。同一个 splat 上两套东西可以共存。

---

## 1. Demo 流程

| 阶段 | 玩家看到 / 能做 | 系统状态 |
|---|---|---|
| **Place** | passthrough 真实房间 + 悬浮的花园"盆景"。抓旁边的 **Move 把手** → 平移 + 绕竖直轴旋转（pitch/roll 锁死），放到真实桌面上。抓 **Scale 把手** → 在鸟瞰区间内调大小。 | rig 位姿由玩家决定，`SplatRoot.localScale ∈ [s_min, s_max]`，**`GSCutout` 开**（裁出整齐边界），**skybox 关**、**locomotion 禁用** |
| **Aim** | 射线指向场景内部的出生点小球，小球高亮 | 把手与出生点互不干扰（见 2.3） |
| **Dive** | 世界以出生点为不动点快速膨胀（0.8–1.2 s），屏幕边缘收窄 | scale 对数插值；**孔径最窄的那一帧**：关 `GSCutout`、关 passthrough、开 skybox（见 2.6） |
| **Immersive** | 站在 1:1 花园里，视野开阔 | scale = 1，`GSCutout` 关，skybox 开，**locomotion 启用** |
| **Return** | 反向动画回到桌面上原来的位姿 | 同一状态机反向，全部状态复位 |

---

## 2. 关键技术决策

### 2.1 缩放 splat 是数学正确的（已核实）

`SplatUtilitiesBody.hlsl:200` 把 object-space `cov3d` 与 `_MatrixMV` 一起送进 `CalcCovariance2D`，
而 `GaussianSplatting.hlsl:77-84` 用 `W = (float3x3)viewMatrix` 做 `T = J*W`、`cov = T·V·Tᵀ`——
model scale 包含在 `_MatrixMV` 里，因此**直接改 transform 的 localScale，每个 splat 椭球会跟着正确缩小**，不会"中心缩了椭球没缩"。**无需改任何渲染代码。**

`GaussianSplatting.hlsl:87-88` 有 `cov._m00 += 0.3` 的 1px 低通滤波，缩到盆景尺度时 splat 不会亚像素闪烁消失，代价是轻微发虚——盆景视角下反而像柔和实体模型。

结论：**单实例 + scale 动画**，不做"低模剪影 + 高模内部"双实例（显存翻倍，Quest 直接排除）。

### 2.2 场景层级（整个方案的骨架）

```
GardenMRRig                    ← XRGrabInteractable + RotationAxisLockGrabTransformer(Y only)
├── SplatRoot                  ← GaussianSplatRenderer, localScale = s_table
│   ├── GSCutout               ← GaussianCutout（盆景边界）
│   ├── CollisionProxy         ← 地面/碰撞
│   └── SpawnPoints/
│       └── SpawnPoint ×N      ← 出生点小球
├── MoveHandle                 ← rig 上唯一的抓取 collider + visual
└── ScaleHandle                ← 独立 interactable，只驱动 SplatRoot.localScale
```

分层理由：**rig 管世界位姿（玩家摆放），SplatRoot 管尺度**。两件事解耦，Dive 的锚点缩放只需动 SplatRoot。

### 2.3 摆放 / 缩放把手：XRI 原生就能做到"不干扰指内部"

需求里最关键的一点，好消息是**不需要新机制**：

- `XRGrabInteractable` 挂在 **GardenMRRig**，但它的 `m_Colliders` 列表**只填 MoveHandle 的 collider**。
  → 射线只有指到把手才能抓；抓住时移动的是整个 rig（含资产）。指向场景内部的出生点完全不受影响。
- 旋转锁轴用项目里现成的 [`RotationAxisLockGrabTransformer.cs`](Assets/VRPlayerLocomotion/ThirdParty/XRI%20Starter%20Assets/Scripts/RotationAxisLockGrabTransformer.cs)，设 `m_PermittedRotationAxis = Y` → 只能绕竖直轴转，pitch/roll 保持初始值。**零新代码。**
- **ScaleHandle** 是独立小把手（自己的 collider + interactable）。抓住后把"控制器到 rig 原点的距离变化"映射成 `SplatRoot.localScale`，`clamp` 在 `[s_min, s_max]`，越界给顶到边界的视觉/触觉反馈。

把手摆位：贴在盆景底座边缘（Move 一侧、Scale 另一侧），世界尺寸恒定（同 2.5 的补偿逻辑），这样缩小资产时把手不会跟着缩到抓不住。

**缩放中心**：改 `SplatRoot.localScale` 时 localPosition 不变，缩放中心就是 SplatRoot 原点。因此要把**原点校正到场景底面中心** → 缩放时盆景"从桌面长大"，底面始终贴桌。（用 `Editor/R2BGaussianSplatCoords.cs`，只动资产/transform，不改代码。）

**鸟瞰区间**：`s_min / s_max` 保证任何允许尺度下玩家都在场景外部俯视。按花园约 20×20 m 估，建议 `s_min = 0.02`（0.4 m，小茶几）、`s_max = 0.06`（1.2 m，大桌子）。实测再调。

### 2.4 穿越 = 缩放世界，玩家不动

玩家 rig 全程不动（不晕，也不要求 room-scale 空间）。Dive 开始时：

1. `splatRoot.SetParent(null, true)` —— **脱离 GardenMRRig 但保持世界位姿**，避免父级矩阵参与后面的世界空间计算。记下原 parent 与原 local 位姿供 Return 用。
2. 每帧按"出生点固定"重算：

```
p_local = splatRoot.InverseTransformPoint(spawnPointWorldPos)   // Dive 开始算一次，之后不变
F       = 玩家脚下目标点 (rig XZ, 地面 Y)
s_start = splatRoot.localScale (玩家当时调的鸟瞰尺度)

s(t)    = exp(lerp(log(s_start), log(1.0), ease(t)))            // 对数插值，视觉速率恒定
splatRoot.localScale = s(t)
splatRoot.position   = F - splatRoot.rotation * (p_local * s(t))
```

出生点始终钉在玩家脚下，世界从桌面长大到 1:1。缩放中心在视野内 → 最不晕。

Return 用同一公式，端点互换，`F` 换成记录的桌面位姿，结束后 `SetParent` 回 rig。

### 2.5 出生点小球必须做尺寸补偿（真实的坑）

出生点是 SplatRoot 的子对象（位置要跟着缩放走，这是对的），但它的**视觉和 collider 世界尺寸不能跟着缩**——`s_table = 0.02` 时一个 5 cm 的球会变成 1 mm，根本指不中。

做法：每帧（或 scale 变化时）
```
visual.localScale = targetWorldDiameter / splatRoot.lossyScale   // 恒定 ~3 cm 世界直径
```
把手（2.3）用同一套逻辑。

顺带提醒（已有教训 `feedback_vr_ray_interactions`）：**不要对 splat 本身做 raycast**，splat 没有 mesh。出生点是预置实体 collider，这条不冲突。

### 2.6 GSCutout：MR 阶段开，进入场景后关，**硬切**

**状态机硬性规则**：
- **Place / Aim（MR 鸟瞰）**：`GSCutout` **enabled = true**。把花园裁成整齐的方块盆景边界，同时切掉扫描外围的噪点飞散 splat——这是 MR 桌面观感成立的前提。
- **Immersive（1:1）**：`GSCutout` **enabled = false**。恢复完整场景，否则站在里面会看到四周被生硬切断。

（`m_Invert` 控制保留 box 内还是 box 外，方向以 Inspector 实测为准。室内资产时这个 cutout 改为切天花板做剖面，语义换、机制不变。）

运行时开关是安全的：`GaussianSplatRenderer.cs:563` 每帧渲染前都调 `UpdateCutoutsBuffer()`，`GaussianCutout.cs:29` 判的是 `isActiveAndEnabled`（disabled 时写 `typeAndFlags = ~0u` 失效标记，数组长度不变、buffer 不重建）。**直接改 `enabled` 即可，无需任何渲染代码改动。**

**cutout 无法淡出**——裁剪是二值的（`SplatUtilitiesBody.hlsl:181-184` 直接把 `centerClipPos.w = 0`），没有 alpha 过渡。

**定稿做法：趁 vignette 孔径最小时硬切。** Dive 中段周边视野已被 2.7 的孔径收缩遮掉、视野最窄，此时切换玩家察觉不到。零成本，不需要任何渐变机制。

具体：`TabletopDiveController` 在 Dive 进度到达 vignette 孔径最小的那一帧，一次性完成三件事——关 `GSCutout`、关 passthrough、开 skybox。全部是硬切，全部藏在同一个视觉遮蔽窗口里。Return 时在对称位置反向切回。

### 2.7 防晕：孔径收缩，不做真模糊

快速缩放（0.8–1.2 s，可调字段）+ 边缘收窄。

- **做法**：项目已有 [`TunnelingVignette`](Assets/VRPlayerLocomotion/ThirdParty/XRI%20Starter%20Assets/TunnelingVignette/TunnelingVignette.prefab) —— 它是**贴在相机前的半球 mesh + shader**，不是全屏 post pass。Dive 期间驱动它的孔径收缩 + feathering。防晕效果与真模糊等价（周边视野运动才是晕动主因，遮掉就行），成本几乎为零，**在 VR multi-pass 立体下天然安全**，且不需要碰渲染管线。
- 这个孔径最小的窗口同时也是 2.6 三件硬切的掩护窗口——一个机制解决两个问题。
- **不做真径向模糊**：需要读屏（URP Renderer Feature + `_CameraOpaqueTexture`），本项目 Quest 走 Multi-pass 立体（见 `project_quest_native_splat_research`），自定义全屏 pass 要额外验证，Quest 带宽也是瓶颈，且违反第 0 节"不碰渲染架构"。若一期实测觉得不够，再在 vignette shader 上叠一层径向滚动条纹做"速度线"式假模糊（仍是半球 mesh 上的操作，很便宜，且是新增材质而非改现有管线）。

### 2.8 Passthrough / skybox / locomotion 的状态切换

已装 `com.unity.xr.meta-openxr` 2.5.0，走 AR Foundation 的 `ARCameraManager` + `ARCameraBackground`。

- **Place/Aim**：passthrough 开（camera background = solid color，alpha 0）；**skybox 必须关**；**locomotion 禁用**（MR 鸟瞰时误碰摇杆会把自己推离桌子）
- **Dive 孔径最窄那一帧**：关 `ARCameraBackground` → 全沉浸；开 skybox
- **Immersive**：启用 locomotion
- **Return**：反向

**室外示范特有的新问题（房子资产时不明显）**：花园是室外场景，1:1 沉浸时需要天空；但 MR 鸟瞰时天空盒会**挡住 passthrough**，必须关。所以 skybox 开关要进状态机。项目已有 `Customizable Skybox` 和 skybox fade（commit 3cce0d2），**只调它暴露的开关/参数，不改它的代码**。

已知限制（`project_passthrough_paint_demo`）：Unity 侧拿不到 passthrough 像素，只能整层开关。本 demo 不需要。

---

## 3. 资产管线

**示范阶段（花园）**：资产已在 `Assets/GaussianAssets/`，已验证可跑。只需两件事——把 SplatRoot 原点校正到底面中心（2.3 依赖），确认 `CollisionProxy` 覆盖出生点区域。**没有导入工作。**

**第二步（室内资产）** 才需要走完整管线。2M+ splats / 489 MB 在 Quest 上没有可能（上限约 400k，瓶颈是 alpha blend overdraw，见 `docs/plans/2026-07-17-mcmc-400k-retrain.md`）：

1. SuperSplat 在线编辑器裁剪：删室外噪点、天空浮点、扫描边缘飞散 splat；只留要用的 2–3 个房间。
2. 导出选 **.ply 或 .spz** —— `GaussianSplatAssetCreator.cs:85` 的导入过滤器就是 `"ply,spz"`。**别导 `.sog`**（要先过项目自带 `R2BSogToPlyWindow`，多一道手工步骤）。
3. `Tools → Gaussian Splats → Create GaussianSplatAsset`，quality 先 Medium，目标 ≤400k splats。
4. `Editor/R2BGaussianSplatCoords.cs` 校正朝向 + 原点对齐底面中心；`R2BGaussianGroundCollisionWindow.cs` 生成 `CollisionProxy`。
5. 候选资产：SuperSplat `Home Scan`（luxury_scans，NJ ranch home 室内合并扫描，CC BY 4.0）https://superspl.at/scene/3f89bbd3 —— 用它要署名。

---

## 4. 新增脚本

放 `Assets/GardenMR/Scripts/`（与 `GardenAmericaSplat/`、`StylizedSplats/` 同级隔离，沿用项目惯例）。**全部是新增文件，不修改任何现有脚本。**

- **`TabletopDiveController.cs`** — 状态机（Place / Diving / Immersive / Returning）+ 2.4 的锚点缩放动画 + detach/attach + 孔径最窄那一帧的三件硬切（cutout / passthrough / skybox）+ vignette 驱动 + locomotion 启停。序列化：`m_Rig`、`m_SplatRoot`、`m_Cutout`、`m_MinScale`、`m_MaxScale`、`m_DiveDuration`、`m_Vignette`、`m_LocomotionRoot`、`m_CameraBackground`、`m_SkyboxController`。
- **`SplatScaleHandle.cs`** — ScaleHandle 抓取 → `SplatRoot.localScale` 映射 + clamp + 边界反馈。
- **`SplatSpawnPoint.cs`** — 出生点：hover 高亮 + `OnSelect` 调 `controller.Dive(this)`。交互写法参考现有 `InfoOrb/InfoOrbController.cs`（参考，不修改）。
- **`ConstantWorldScale.cs`** — 2.5 的尺寸补偿，挂在出生点 visual 和两个把手上。

Move 把手不需要脚本（XRGrabInteractable + 现成的 RotationAxisLockGrabTransformer）。

---

## 5. 里程碑

1. **M1 桌面验证（无 VR，花园资产）**：搭 2.2 的层级，Editor 里键盘触发 Dive。验证缩放渲染正确、cutout 硬切时机对、动画曲线舒服。**方案成不成立在这一步就能判定。**
2. **M2 摆放交互（PC VR / Quest Link）**：Move/Scale 把手 + 锁 yaw + 尺寸补偿 + 出生点扣扳机 + vignette + passthrough/skybox 切换。验证两件事：把手是否真的不干扰指内部、快速 Dive 配孔径收缩晕不晕。
3. **M3 换室内资产**：走第 3 节完整管线，cutout 语义从"盆景边界"换成"天花板剖面"，机制不变。
4. **M4 Quest 单机**：需 Multi-pass 立体 + 手动指定 `SplatUtilitiesFfx`（见 `project_quest_native_splat_research`）。帧率不达标则走 MCMC 重训或 `docs/mobile-gs-oit-plan.md` 的 OIT 方案。

**先别碰 Quest 单机** —— overdraw 是独立问题，不要和交互设计的验证纠缠在一起。

## 6. 已知风险

- **Dive 中段最贵**：世界正铺满视野但还没被遮挡裁掉，splat 屏幕覆盖面积峰值在这里，不在两个端点。花园是室外开阔场景，这一点比室内更明显。
- **盆景尺度下的可读性**：1px 低通会让细节发虚，`s_min = 0.02` 下能否看清"这是哪个角落"要实测。读不出就抬高 `s_min`。
- **沉浸阶段帧率**：花园室外 overdraw 重（已知 Quest 瓶颈），是全 demo 的性能下限。
- **室内资产的鸟瞰观感（M3 才验证）**：合并扫描通常没有完整外墙/屋顶几何，从上方看可能是断面 + 噪点。

## 7. 环境备注

Unity MCP bridge 只在 Unity 编辑器窗口活跃时连得上（`project_unity_mcp_bridge`）。M1 的场景搭建需要 Unity 在前台。
