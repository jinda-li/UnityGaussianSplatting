# superspl.at 场景 → Unity 可行走场景（标准流程）

姊妹文档：[Photos → Unity 3DGS](photos-to-unity-3dgs.md)（自己拍照训练的那条线）。这一篇讲的是
**别人已经训好、发布在 superspl.at 上的场景**怎么进项目，并且让 VR locomotion 能在里面走。

首个实例：Trogir 老城（`Assets/Trogir/`，作者 Paolo Tosolini，CC BY 4.0）。署名与重建命令见
`Assets/Trogir/CREDITS.md`。

## 端到端一览

```
superspl.at 场景页
  → 下载 .ssog（登录后可下），或直接读公开 CDN
  → splat-transform 合并某一级 LOD → PLY
  → splat-transform 体素化 + flood fill → .collision.glb
  → Unity：导入 GaussianSplatAsset；建场景；挂 CollisionProxy
  → Play 模式两个 probe 验证：碰撞体、locomotion
```

`Assets/Trogir/Editor/TrogirSceneBuilder.cs` 把后三步做成了菜单项 / `-executeMethod` 入口，
换场景时复制改常量即可。

---

## 1. 取素材：.ssog 和 CDN 是同一份数据

场景页的 **Download → Source file** 给的是 `.ssog`，本质是一个 zip，里面是 SOG 分块。同样的分块
也挂在公开 CDN 上，**不需要登录**：

```
https://d28zzqy0iyovbz.cloudfront.net/<sceneId>/v1/lod-meta.json
https://d28zzqy0iyovbz.cloudfront.net/<sceneId>/v1/<lod>_<n>/meta.json
```

实测两者内容一致。选哪条看场合：要长期留底、要署名文件就走官方下载；只是想快速试一下就读 CDN。

`lod-meta.json` 里的 `counts` 数组是每级 LOD 的高斯点数。**每一级都是整个场景的完整表示**，不是
上一级的增量 —— 所以选 LOD 等于选点数预算。Trogir 的例子：

| LOD | splats | 用途 |
|-----|--------|------|
| 6 | 359K | Quest |
| 5 | 719K | Quest |
| 4 | 1.44M | Quest 上限附近 |
| 3 | 2.88M | 桌面 |
| 2 | 5.76M | 桌面，`kMaxSplats` 内最细的一级 |
| 1 | 11.5M | **超** `GaussianSplatAsset.kMaxSplats`（8.6M） |
| 0 | 23M | **超** |

注意 SuperSplat 生成 LOD 时会剥掉球谐，各级都是 `0 SH bands`，即视角无关着色。导入时 SH 格式
选 `Cluster4k` 就行，位宽省下来给位置。

---

## 2. 合并 LOD → PLY

`splat-transform` 直接读 `meta.json`，也接受 `http(s)://` URL。**不支持** `lod-meta.json` 作为
输入（会报 `Unsupported SOG meta version: 1`），所以要把某一级的分块逐个列出来：

```bash
splat-transform 2_0/meta.json 2_1/meta.json 2_2/meta.json 2_3/meta.json 2_4/meta.json 2_5/meta.json 2_6/meta.json 2_7/meta.json 2_8/meta.json 2_9/meta.json 2_10/meta.json -N Trogir_lod2.ply
```

分块数量看 `lod-meta.json` 的 `filenames`。`-N` 滤 NaN。

PLY 体积很大（5.76M 点 ≈ 308 MB），放项目外；`Assets/GaussianAssets/` 在 gitignore 里，导入产物
也不进库。

---

## 3. 坐标系：file 帧 vs engine 帧

**这是最容易浪费时间的一步。** 有两个坐标系，长得很像但不是一个：

| 帧 | 是什么 |
|----|--------|
| **file** | PLY 里的原始坐标，也就是 `GaussianSplatAsset` 的本地空间 |
| **engine** | splat-transform 的 `--camera` / `--filter-box` / `--seed-pos` 用的世界系，也是 superspl.at 网页 viewer 显示的数字。等于 file 帧绕 Z 转 180°（x → −x，y → −y） |

`Editor/R2BGaussianSplatCoords.cs` 的注释早就写着这件事，值得先读一遍再动手。

推论：给 `GaussianSplatRenderer` 挂 **`rotation (0, 0, 180)`、scale 保持 1**，Unity 世界坐标就和
网页 viewer 上看到的数字一一对应。好处是 viewer 的 `settings.json` 里那个初始相机可以直接拿来当
出生点：

```
https://s3-eu-west-1.amazonaws.com/splats.playcanvas.com/<sceneId>/v1/settings.json
```

不要靠猜来定朝向。`splat-transform` 自带 GPU 光栅器，几秒就能出图，比反复进 Unity 快得多：

```bash
splat-transform -w scene.ply --camera 14.77,1.52,42.51 --look-at 16.26,1.33,43.84 --up 0,1,0 --fov 75 --resolution 960x540 check.webp
```

渲出来和网页上一致，坐标映射就是对的。同一个工具也能出俯视图，用来在地图上量出可行走路线的
waypoint：

```bash
splat-transform -w scene.ply --filter-box -20,-1,10,50,3,80 --camera 14.77,60,42.51 --look-at 14.77,0,42.51 --up 0,0,1 --fov 60 --resolution 900x900 top.webp
```

俯视图里 **+x 朝画面左、+z 朝画面上**（用两次 `--filter-box` 各切一半就能自己验一遍）。

---

## 4. 生成碰撞

用仓库已有的 `R2B/Gaussian Splats/Generate Splat Collision`，或直接调 CLI（两者跑的是同一条命令）：

```bash
splat-transform -w scene.ply \
  --filter-box -70,-5,-12,80,12,156 \
  --filter-cluster 1.0,0.999,0.1 \
  --seed-pos 14.7745,1.5232,42.5117 \
  scene.voxel.json --voxel-params 0.1,0.1 --voxel-floor-fill 1.6 -K smooth
```

要点：

- `--filter-box` / `--seed-pos` 都在 **engine 帧**。
- `--seed-pos` 放在确定站得住人的地方（viewer 的初始相机就很合适）。`--filter-cluster` 从这个种子
  做连通域，天空、飞点会被扔掉；不加它 flood fill 会漏出去。
- 体素尺寸 0.1 m 对整城尺度是可行的：Trogir（134 × 20 × 162 m）约 53 秒出 5.13M 三角面 / 88 MB glb。
  太细会炸显存，先拿小 box 试跑估算。
- 官方 viewer 自己的 walk mode 数据在 `scene.voxel.json`（同 S3 路径），可以拿来核对生成结果的
  `sceneBounds` 对不对得上。

### MeshCollider 超过 2M 面必须关 Fast Midphase

PhysX 的 fast midphase 只索引 2,097,152 个三角面，超出部分的碰撞**可能被静默漏掉**。整城网格
轻松就过线，所以：

```csharp
// 必须在赋 sharedMesh 之前设，否则触发 cook 的那一次用的还是旧选项
mc.cookingOptions &= ~MeshColliderCookingOptions.UseFastMidphase;
mc.sharedMesh = mesh;
```

Unity 会在 Console 里警告这件事，但只是一条带堆栈的普通 warning，很容易被淹掉。

---

## 5. 无头 Unity 的坑

这些和 splat 没关系，是把上面这套自动化时一定会撞到的。

| 坑 | 表现 | 处理 |
|----|------|------|
| `unity run` 自带 `-quit` | `-executeMethod` 里进 Play 模式没机会跑，直接退出 | 需要 Play 模式时直接调 `Unity.exe -batchmode -projectPath ... -executeMethod ...`，自己在方法里 `EditorApplication.Exit` |
| 进 Play 模式会 domain reload | 之前注册的 `EditorApplication.update` 回调没了 | 用 `SessionState` 存标志，`[InitializeOnLoadMethod]` 里重新挂 |
| `WaitForEndOfFrame` 在 `-batchmode` 下永不恢复 | 协程卡死 | 自己 `camera.Render()` 到 RenderTexture |
| 帧耗时极不均匀 | 一帧画 5.7M splat 要一秒，`Time.deltaTime` 灌进重力就是一次百米俯冲 | `Time.captureDeltaTime = 1f/60f`；走路时把 `GaussianSplatRenderer.enabled` 关掉，只在截图那一帧打开 |
| `_OrderBuffer` / `_SplatViewData` 的 Vulkan 警告 | 刷屏 | 是编辑器选择 pass，splat 照常渲染，可忽略 |

### batchmode 里注入输入有两道门

想在无头环境里驱动 XR Device Simulator，要开两个开关，**第二个很阴**：

1. 进程永远没有焦点，`InputSystem.settings.backgroundBehavior` 默认
   `ResetAndDisableNonBackgroundDevices`，会把键盘设备直接禁用。
2. 就算重新 `InputSystem.EnableDevice`，`editorInputBehaviorInPlayMode` 默认
   `PointersAndKeyboardsRespectGameViewFocus`，Play 模式下按键只发给有焦点的 Game View。
   batchmode 没有 Game View，按键在到达 action 之前就被丢掉 —— 而设备本身仍然报 `enabled=True`。

两项都要临时放开，**并在结束时还原**（它们存在 project asset 里，不是场景里）。写设备状态用
`InputState.Change` 而不是 `QueueStateEvent`，前者绕过那条受焦点门控的事件队列。

---

## 6. 验证：两个 probe，分工不同

`Assets/Trogir/Scripts/` 下有两个，**两个都要跑**，因为它们证明的是不同的事：

### `TrogirWalkProbe` —— 只验碰撞体

把 player 身上所有脚本关掉，自己驱动 CharacterController 沿 waypoint 走。

这一步的关键是**必须关掉那些脚本**：VRIK / rig controller / root motion 每帧都会把 transform 写回去，
外部 `Move()` 看起来就像撞上了一堵解不开的墙 —— 第一次跑就是这么翻车的，角色位置一格没动，而报告
还显示 PASS，因为当时的判据只查"有没有掉出世界"。

报告要能区分**撞墙**（走了一段后停住，正常）和**卡死**（完全没动，有问题），否则等于没测。

### `TrogirLocomotionProbe` —— 验完整链路

一个脚本都不关，按 **Left Shift + WASD**（XR Device Simulator 的默认绑定：`Manipulate Left` +
`Axis 2D`），走的是真实路径：

```
键盘 → XRDeviceSimulator → XRSimulatedController.primary2DAxis
     → Move action（prefab 绑在 <XRController>{LeftHand}/{Primary2DAxis}）
     → VRPlayerControllerInput.MoveAxis → PlayerController.TickLocomotion
     → CharacterController.Move → CollisionProxy
```

这条链路项目里没有任何文档写过，是从
[PlayerController.cs:161](../../Assets/Scripts/PlayerController.cs) 往上，经
[VRPlayerControllerInput.cs:9](../../Assets/VRPlayerLocomotion/Scripts/VRPlayerControllerInput.cs)、
`VR Player Locomotion.prefab` 的序列化绑定，一直追到 XRI 包源码和
`XR Device Simulator Controls.inputactions` 拼出来的。

正因为是拼出来的，probe 里带一个 `Diagnose()`，**逐环节打印实测值**：

```
diag keys: leftShift=True w=True
diag simulator: manipulatingLeftController=True
diag left stick: (0.00, 1.00)
diag input: canMove=True MoveAxis=(0.00, 1.00)
diag moveAction: 'Move' enabled=True controls=1 value=(0.00, 1.00)
```

前两轮这里是 `leftShift=False`，一眼就能看出断在最上游（输入根本没进设备），而不用去猜 locomotion
是不是坏了。**任何一条跨了四五个系统的链路，都值得先写这样一段分环节自证的日志。**

### 出生朝向

locomotion 是相对视线方向移动的（`GetViewRelativeMove` 用 hmd 的 yaw）。没接头显时头的朝向就是
rig 的初始朝向，所以出生 rotation 要对着巷子，否则第一步就是撞墙。

### 两个 probe 在保存的场景里都必须是关的

否则别人按 Play 就被脚本化巡演接管（尤其 `TrogirWalkProbe` 会关掉 player 全部组件）。
`RunProbe` 只在内存里把它打开，不写回磁盘。

---

## 7. 复跑

```bash
TROGIR_SCRATCH=<放 ply 和 glb 的目录> unity run . -- -executeMethod Trogir.EditorTools.TrogirSceneBuilder.BuildEverything
```

```bash
"C:\Program Files\Unity\Hub\Editor\6000.3.6f1\Editor\Unity.exe" -batchmode -projectPath <project> -executeMethod Trogir.EditorTools.TrogirSceneBuilder.RunLocomotionProbe -logFile -
```

截图和报告写到 `Assets/Screenshots/`（gitignore 中）。
