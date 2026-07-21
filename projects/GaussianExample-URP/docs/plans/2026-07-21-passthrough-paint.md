# Passthrough 涂色场景计划（2026-07-21）

状态：**待开工**（不阻塞 [MCMC 400k 重训练](2026-07-17-mcmc-400k-retrain.md)，两者互不冲突：本场景不加载任何 3DGS 资产）

目标：新场景 `PassthroughPaint.unity`。玩家戴 Quest 3 看到的**真实房间是灰白的**，用控制器扳机喷涂，被涂到的地方**还原成真实世界的彩色**。交互手感沿用现有 `StylizedSplats` 的 VR 笔刷（扳机、喷雾粒子、循环音效、震动）。

---

## 硬约束（先读这段，它决定了整个架构）

`com.unity.xr.meta-openxr` 的官方文档原话：

> Unity doesn't have access to pixels or other image data associated with Meta Passthrough images.

Passthrough 由系统 compositor 作为独立 composition layer 合成，Unity 的 eye buffer 里**根本没有它的像素**。核查了 `Runtime/CompositionLayers/`：该包只封装了 `XrCompositionLayerPassthroughFB` 的创建，**没有** `XrPassthroughStyleFB` / color map，所以：

- ❌ 不能写个后处理 shader 把 passthrough 变灰
- ❌ 不能用这个包做全局 grayscale styling（那是 Meta XR Core SDK 的 `OVRPassthroughLayer` 才有的能力）

### 采用的机制：白模罩世界（coat + erase）

在真实世界的表面上盖一层**不透明的灰白"白模"几何**（材质盖住 passthrough），涂色 = 把白模对应位置的 **alpha 擦成 0**，让底下**真正的彩色 passthrough** 透出来。

好处：
- "还原真实颜色"是天然成立的 —— 露出来的就是真相机画面，不需要 Passthrough Camera API、不需要相机权限、不需要投影贴图对齐
- XR 栈一行不改，继续用 Unity OpenXR + meta-openxr
- 笔刷交互层可直接从 `VRSplatBrush` 复制

代价：白模贴不准的地方会穿帮（见风险 2）。

---

## 架构

```
ARMeshManager (Quest 房间网格) ─┐
ARPlaneManager (墙/地/天花/桌面)─┴─→ 挂 PassthroughCoat 材质的几何 = 白模
                                          │ 采样
                            世界空间体素 mask (RenderTexture3D, R8)
                                          ↑ 写入
                    PassthroughPaintController.PaintSphere()  ← compute kernel
                                          ↑ 调用
                    PassthroughPaintBrush (复制自 VRSplatBrush)
```

### 1. mask 用世界空间体素，不用 UV

Quest 的 scene mesh **没有可用 UV**（运行时生成、拓扑随时更新），所以笔迹不能存成贴图。改用世界空间 3D 体素：

- `RenderTexture` + `dimension = Tex3D`, `format = R8`, `enableRandomWrite = true`, 三线性过滤
- 体积：以启动时玩家位置为中心的 `8 × 4 × 8 m` box
- 体素 4 cm → `200 × 100 × 200 = 4M` 体素 = **4 MB**（Quest 3 完全吃得下；先按 4 cm 起，太糊就降到 2 cm = 32 MB，仍可接受）
- 白模 shader：`worldPos → uvw → tex3D` 采一次，`alpha = 1 - mask`
- 体积原点挂在 XR Origin 的 TrackablesParent 下，避免 recenter 后笔迹与真实世界错位

### 2. 涂色 compute：只 dispatch 笔刷 AABB

`PassthroughPaint.compute` / `CSPaintSphere`：

- 输入：命中点、半径、强度
- **只 dispatch 笔刷球体 AABB 覆盖的体素子块**（半径 20 cm / 4 cm 体素 ≈ `10³ = 1000` 个体素），不是全 4M 体积 → 每帧开销接近零
- 累加逻辑照抄 `StylizedSplatPaint.compute`：smoothstep falloff × strength，`saturate` 封顶

### 3. 笔刷交互：复制 `VRSplatBrush`

`Assets/PassthroughPaint/Scripts/PassthroughPaintBrush.cs`，直接沿用原文件的手部发现（`HapticImpulsePlayer` → `NearFarInteractor` 的校准 ray origin）、inline `InputActionProperty` 扳机绑定、SprayFX 预制体/代码兜底、音量淡入淡出、震动。

**唯一的实质差别**：不再用 capsule 无视深度地涂。这次玩家站在房间**内部朝墙/家具涂**，是标准的从外向内 raycast，scene mesh 自带 `MeshCollider` → 老老实实 `Physics.Raycast` 拿命中点，在命中点做**球形笔刷**。比 capsule 精准得多。

> 注：项目里"玩家站在体积内部、不能靠 proxy collider raycast"的那条经验只适用于 3DGS 场景，这里不适用。

### 4. 白模材质

`PassthroughCoat.shader`：
- **Opaque queue + `ZWrite On`**（不是 Transparent）。未涂区域 alpha=1 完全盖住 passthrough，走不透明路径拿 early-Z，这在 Quest 上是性能关键
- 输出可变 alpha：`alpha = 1 - mask`，配 `Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha`（分离 alpha 通道混合）
- 灰白外观：基础 `BaseLift` 浅灰 + 法线方向的简单明暗，让房间读起来像"白模/石膏稿"而不是一片死白。可复用 `StylizedSplats.shader` 里 `_BaseSaturation` / `_BaseLift` 那段的思路
- 擦除边缘用 `_StylizedBrushTex`（现成的 `BrushStroke_*.png`）做噪声抖动，避免露出完美圆形的塑料边

### 5. 遮挡

`AROcclusion` feature 已启用 → 加 `AROcclusionManager`。手和走进来的人会正确遮挡白模，也就是说**手是彩色的**。这不是 bug，是白送的好效果（"你的手带着颜色"）。

---

## 实施阶段

### Phase 0 — 骨架 + alpha 合成验证（最关键，先做）

- [ ] 新建 `Assets/PassthroughPaint.unity`：AR Session + XR Origin (AR) + `ARCameraManager`（启用即 passthrough）
- [ ] Camera 清除标志 = Solid Color，**Background alpha = 0**
- [ ] 确认 OpenXR 的 **Meta Quest: Camera (Passthrough)** feature 已启用（其余 Meshing/Planes/Occlusion/CompositionLayers 已核实开启）
- [ ] 手摆一个 quad 贴 `PassthroughCoat` 材质，用 Inspector 滑条驱动一个假 mask
- [ ] **上机验证：alpha 从 1 → 0 时，quad 那块能否真的透出彩色 passthrough**

> ⚠️ 这一步是整个方案的生死线，且 **PC 上看不出来，必须真机验证**。如果 opaque queue 的 alpha 到不了 compositor，回退到 Transparent queue + `ZWrite Off`，性能打折但机制不变。同时确认 URP 的 post-processing / HDR 没有吃掉 alpha 通道。

### Phase 1 — 白模画布

- [ ] `ARMeshManager` 拉房间网格，mesh prefab 挂 `PassthroughCoat` 材质 + `MeshCollider`
- [ ] `ARPlaneManager` 兜底（`m_PlaneProviderType` 已配好），没做过 Space Setup 时至少墙地能涂
- [ ] 运行时申请 `com.oculus.permission.USE_SCENE`；Space Setup 未完成时给一句世界空间提示文字引导用户去扫描
- [ ] 效果检查：戴上去房间应该整个变成灰白白模

### Phase 2 — 涂色

- [ ] `PassthroughPaintController`：体素 RT 生命周期、compute dispatch、shader globals（结构对标 `StylizedSplatsController`，但不需要反射，因为不碰 3DGS 私有 buffer）
- [ ] `PassthroughPaint.compute` / `CSPaintSphere`
- [ ] `PassthroughPaintBrush`：从 `VRSplatBrush` 复制改造
- [ ] `ClearPaint()` 复位（绑到某个按键或菜单）

### Phase 3 — 打磨

- [ ] SprayFX 预制体复用（粒子颜色调成"揭色"的白/冷调，和现有 `BuildDefaultSprayFx` 一致）
- [ ] 笔刷边缘噪声、涂色瞬间的高光脉冲
- [ ] 进度统计：体素 mask 的 reduction → "已还原 37% 的世界"，可做完成度反馈

### Phase 4 — 性能

- [ ] Quest 3 实机帧率：目标 72 fps 稳定。白模是不透明单 pass + 一次 tex3D，理论上非常轻
- [ ] 检查 scene mesh 三角面数，必要时开 `ARMeshManager` 的 density 限制

---

## 风险

| # | 风险 | 缓解 |
|---|---|---|
| 1 | **alpha 到不了 compositor**（opaque queue 写的 alpha 被 URP 丢弃） | Phase 0 先验证；回退 Transparent + `ZWrite Off`；关 post-processing |
| 2 | **Scene mesh 精度粗糙**，细小物体、桌上杂物盖不准 → 白模边缘穿帮 | 白模美术风格本身就是"粗白模"，误差可被风格吸收；边缘加噪声抖动；接受"大结构准、小物件糊" |
| 3 | 用户没做过 Space Setup → 没有网格 | plane 兜底 + 引导提示；最差情况仍能涂墙地 |
| 4 | recenter / guardian 重建导致体素与真实世界错位 | 体积挂 TrackablesParent；提供一键重置 |
| 5 | 体素 4 cm 笔迹太糊 | 降到 2 cm（32 MB，仍可接受）；三线性过滤已能软化 |

---

## 新增文件

```
Assets/PassthroughPaint.unity
Assets/PassthroughPaint/
  Scripts/
    PassthroughPaintController.cs   ← 体素 RT + compute dispatch + shader globals
    PassthroughPaintBrush.cs        ← 复制自 StylizedSplats/Scripts/VRSplatBrush.cs
    PassthroughSetupGuide.cs        ← 权限申请 + Space Setup 引导
  Shaders/
    PassthroughCoat.shader          ← 白模，alpha = 1 - mask
    PassthroughPaint.compute        ← CSPaintSphere
```

不改动 `Assets/StylizedSplats/` 与 `package/` 下任何现有文件。
