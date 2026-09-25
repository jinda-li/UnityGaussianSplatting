# UkemiXR Splat Walk（网页端）

在浏览器里打开任意 `.spz` / `.ply` 高斯场景，用 Unity 那套 locomotion 在里面走，戴上头显直接进 VR。

```
打开 .spz/.ply（示例 / 文件 / 拖放 / ?url=）
  → Spark 渲染（three.js，World Labs 出品，Marble 用的就是它）
  → 从 splat 现场体素化出碰撞体（50 万点约 0.5–1 s）
  → 找出生点（主地面层上离原点最近的空地）
  → locomotion：第一人称站立 / 推摇杆第三人称行走、镜头瞬移追赶
  → 「进入 VR」：WebXR immersive-vr，local-floor
```

## 本地运行

```bash
cd projects/GaussianExample-URP/Web
npm ci
npm run dev          # http://127.0.0.1:5173
```

URL 参数：

| 参数 | 作用 |
|------|------|
| `?scene=living-room` / `bamboo-courtyard` | 打开示例 |
| `?url=https://…/x.spz` | 打开任意远程文件（需对方允许跨域） |
| `?flip=1` | 上下翻转（原版 3DGS 训练出来的 PLY 通常是 Y 朝下） |
| `?debug` | 显示碰撞体素 |
| `?xremu` | 注入模拟 Quest 3（Meta IWER），没有头显也能走一遍 VR 流程 |

## 部署到 Vercel（公开访问）

仓库根目录和本目录各有一份 `vercel.json`，两种导入方式都能直接用：

1. Vercel → **Add New… → Project** → 选 `jinda-li/UnityGaussianSplatting`。
2. **Root Directory** 选 `projects/GaussianExample-URP/Web`（推荐；Framework 会识别为 Vite）。
   不改也行，根目录的 `vercel.json` 会 `cd` 进来构建。
3. Deploy。之后每次 push：`vr-preview` 分支得到 Preview 链接，生产分支得到正式域名。
   想让 `vr-preview` 就是正式站，在 **Settings → Git → Production Branch** 改成它。

WebXR 只在 HTTPS 下可用，Vercel 的域名天然满足。部署产物约 15 MB（两个 `.spz` 示例 + 3.3 MB JS），
`.ply` 示例体积大（34 MB/个）且内容与 `.spz` 相同，只在本地 dev 里列出。

## 四个问题的答案

### 1. 碰撞怎么简单生成？

`src/collision/VoxelWorld.js`，加载时在浏览器里现算，不需要任何离线步骤：

- 每个 splat 按不透明度累加到一张稠密体素网格（默认 10 cm，场景太大时自动放大到 ≤ 2400 万格）。
  大 splat 沿它最长的两个轴采样多点，保证一片大地板 splat 能盖住它画出来的那块地。
- 累计不透明度 ≥ 阈值（默认 1.0，设置里可调）才算实心 —— 零星飞点自然被滤掉。
- 包围盒按 0.4% / 99.6% 分位数取，远处飞点不会把网格撑爆。
- 玩家是一根圆柱（半径 0.22 m，高 1.7 m），和 CharacterController 一样：
  - **脚下**：在「上一步台阶 0.35 m / 下一步 0.6 m」范围内找地面，5 个采样点至少 2 个踩到才算有地。
    **没有地 = 洞或者场景边缘 → 拒绝移动**，这就是「不能走出边界」。
  - **身体**：膝盖以上到头顶之间有实心体素 = 撞墙。椅子座面高于台阶高度，所以会停下。
  - 爬升按最近 0.4 m 的轨迹限制在台阶高度以内：楼梯能上，沙发垫这种「小台阶连成的斜坡」上不去。
  - 子步长 < 半个体素，撞墙时按单轴滑动，斜着推墙会贴着墙走。
- 出生点：扫描原点 8 m 内每一列能站的最低面，取出现最多的高度当「主地面」，
  再选离原点最近、四周 0.5 m 都能站的点。竹林庭院原点下面是水池，靠这个避开。

Unity 那边的做法（`splat-transform` 体素化 → `.collision.glb` → MeshCollider）精度更高但要离线跑；
网页端面对的是用户随手拖进来的任意文件，所以换成了现场体素化，思路相同（都是体素 + 连通地面）。

### 2. 网页端怎么渲染？

[Spark](https://sparkjs.dev)（`@sparkjsdev/spark`，MIT）：three.js 的高斯渲染器，World Labs 做的，
Marble 自己也在用。支持 `.spz` `.ply`（含压缩 PLY）`.splat` `.ksplat` `.sog`，
能和普通网格混合排序（所以机器人 avatar 可以正常站在 splat 场景里），也处理 WebXR 双目。

### 3. 网页端 XR

直接用 WebXR：`immersive-vr` + `local-floor`，three.js 的 `renderer.xr` 管会话，
相机挂在 `rig`（= Unity 的 XR Origin）下面，头显位姿由设备写，locomotion 只移动 `rig`。
帧缓冲缩放默认 0.75（设置存在 `localStorage` 的 `xrScale`）。
**还没在真机上测过帧率** —— 自动化测试用的是模拟头显，见下文「测试」。

在 Quest / Pico 浏览器打开网址 → 点「进入 VR」即可。电脑没头显时按钮显示「需要 VR 头显」。

### 4. Unity 的 locomotion 怎么迁移过来

逐文件移植，参数默认值与 Unity 序列化值一致：

| Unity | Web | 说明 |
|-------|-----|------|
| `VRPlayerControllerInput.cs` | `src/locomotion/PlayerInput.js` | 同样的缓冲边沿 + 迟滞阈值（移动 0.20/0.15，转向 0.75/0.50） |
| `VRCameraRigController.cs` | `src/locomotion/CameraRig.js` | 瞬移追赶、环绕半径 2.5 m、追赶间隔 0.25 s、35° 转向、起步推镜、环绕防穿墙 |
| `PlayerController.cs` | `src/locomotion/PlayerController.js` | Idle / Locomotion 两态、视线相对移动 2.5 m/s、room-scale 头动带身体 |
| CharacterController + MeshCollider | `src/collision/VoxelWorld.js` | `moveAndSlide` ≈ `CharacterController.Move` |
| VRIK + Mixamo 角色 | `src/avatar/Avatar.js` | CC0 机器人（Quaternius RobotExpressive），Idle/Walk/Run 按实际速度混合 |
| （Unity Input System） | `src/locomotion/InputSources.js` | WebXR 手柄、键盘、鼠标、手柄、手机触摸 |

防晕的核心没有变：**走路时镜头从不连续移动、从不自己转**。人物往前走，镜头原地不动，
每 0.25 s 切到人物身后 2.5 m；人物朝镜头走过来（< 1 m）时立刻往后跳；松开摇杆，镜头瞬移回人物头里。
没有连续的视觉流动就没有 vection，也就不晕。

和 Unity 的差异（都是有意的）：

- **回到第一人称时保持当前朝向**。Unity 会把视角转到 avatar 头的朝向（VRIK 的头本来就看着你看的方向，
  所以几乎不转）；这里机器人是面朝行走方向的，照搬会多一次没人要的转向。`CameraRig.keepYawOnReturn` 可关。
- **起步推镜也做了防穿墙**（Unity 只对环绕做了射线检测），贴墙起步不会把镜头推进墙里。
- **后跳距离取 min(1 m, 剩余距离)**，避免离目标很近时来回过冲。
- **桌面默认平滑跟随**：平面屏幕上每秒 4 次的硬切看起来像掉帧；VR 里自动切回瞬移追赶。
  设置里可以强制任一种。
- **回到第一人称时 0.25 s 淡入淡出**（可关）。
- Dodge roll（B 键翻滚）依赖 Mecanim 动画根运动，没有移植；按键缓冲已在 `PlayerInput` 里留好。

## 测试

```bash
npm run dev &                 # 或 npm run preview（测构建产物，BASE=http://127.0.0.1:4173）
npm test
```

| 脚本 | 测什么 |
|------|--------|
| `tests/collision.test.mjs` | Node 里直接读 PLY，建体素、找出生点、16 个方向各走 30 s，不许出界、不许掉下去 |
| `tests/reachMap.mjs` | 画俯视图：从出生点能走到哪（绿色）。调碰撞参数时看这个 |
| `tests/e2e.mjs` | 无头 Chromium 驱动真页面：第一人称 ↔ 第三人称、镜头只做离散跳切且不自转、撞家具停下、35° 转向、后退不穿脸 |
| `tests/xr.mjs` | 模拟 Quest 3（IWER）：点「进入 VR」，用真的 WebXR 手柄摇杆走、转、松手回第一人称、退出 VR |
| `tests/upload.mjs` | 通过文件选择框打开 `.ply` / `.spz` |
| `tests/screens.mjs` | 渲染截图（SwiftShader 很慢，约 2 分钟） |

## 已知限制 / 下一步

- 碰撞只认「地面 + 身体圆柱」：不能钻桌子底下，也没有天花板/低矮门框检查（身高 1.7 m 以上不查）。
- 尺度假设为米。Marble 导出的是米制；COLMAP 训练的场景尺度随意，需要加一个缩放参数。
- 上下方向：默认 +Y 朝上；站不住时自动尝试翻转，仍不对就手动「上下翻转」。
- 可以加载 Unity 流程产出的 `.collision.glb` 替代现场体素化（坐标系差一个绕 Z 180°，见
  `docs/workflows/superspl-at-to-unity.md` 第 3 节）。
- 手柄模型 / 射线还没画；VR 里暂时没有菜单（换场景要摘头显）。

## 素材

- 示例场景：`SplatSamples/`（Marble 生成）。
- Avatar：RobotExpressive，Tomás Laulhé（Quaternius），CC0 1.0，Don McCurdy 修改版，取自 three.js 示例。
