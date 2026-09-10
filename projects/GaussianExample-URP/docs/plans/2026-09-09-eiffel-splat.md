# 手上的埃菲尔铁塔 —— 合成数据训练 3DGS + 抛掷进入场景

2026-09-09 起草，2026-09-10 大改（换外部铁塔模型、HDRI 光照、远景城市）。

目标视频：第一人称，手里握着一座微缩铁塔 → 凑近看细节 → 甩出去 → 落地瞬间胀成 1:1 →
站在战神广场上仰望。

**当前状态**：Blender 场景已达到接近照片级，数据集渲染和 3DGS 训练**尚未启动**。
交互侧 `ThrowToDive.cs` 已写完但未在 Unity 里接线。

---

## 0. 版权立场（硬约束）

| 项 | 状态 |
|---|---|
| 铁塔**结构本身** | 公共领域。Gustave Eiffel 卒于 1923 年，设计版权 1993 年到期 |
| 夜间金色灯饰 / 闪灯 | **受版权保护**，1985 年 Pierre Bideau 设计，作者 2021 年卒。商用需 SETE 授权 |
| 场景素材 | Poly Haven，全部 CC0，公开 API 免账号下载 |
| 远景城市 / 塞纳河 | 本仓库程序化生成 |
| **铁塔网格** | ⚠️ 见下 |

**只做白天，绝不出现夜间灯饰。** 这是发布前提，不是美术偏好。

### 铁塔模型的授权风险

用的是 Sketchfab 的 `eiffel_tower_model_3d_with_best_quality`（shatlykxfree, CC-BY, 54 万面），
文件在 `C:\Users\standalone\Documents\3DGS\Eiffel\sketchfab\`。

检查结论：几何是真的（85 个 mesh、69.6 万顶点、零面积面为 0、**无贴图**所以没有 alpha 贴片风险）。
但原文件里有 `Archexteriors4_mat_19_Slot_1/2` 材质命名，那是 **Evermotion Archexteriors vol.4
商业素材库**的命名规范，上传者未必有权标 CC-BY。

**缓解**：`tower_asset.import_tower` 按半径 78 m 裁剪，裁掉的 29,442 面**正好等于**那两个材质的
面数——带商业库指纹的几何已经完全不在场景里，剩下 514,993 面全是 `material_0`。这不证明其余
部分来历清白。

**商业发布前建议花 20–50 美元买一个授权明确的模型**（CGTrader / TurboSquid）。管线是模型无关的：
把新文件丢进同一目录、改 `scene.DEFAULT_TOWER` 即可，其余全部不用动。

程序化的铁塔生成器 `eiffel_tower.py` 保留着，`scene.py` 找不到外部模型时自动回退。它轮廓正确
但没有一二层楼阁、电梯轨道、楼梯，桁架图案是通用空间桁架而非埃菲尔自己的图案。

---

## 1. 为什么走合成数据

网上**没有**授权明确、可下载的「铁塔 + 环境」3DGS 资源（搜过 Sketchfab / HuggingFace / Polycam /
Scaniverse / SuperSplat / radiancefields 目录）。实地扫描需要人在巴黎，巴黎全域禁飞无人机。

合成数据在这个题材上有硬优势：

1. **相机位姿精确已知**，完全跳过 COLMAP，零漂移
2. **初始点云直接射线投射得到**。细桁架在真实拍摄里几乎拿不到 SfM 特征点，3DGS 从稀疏初值
   重建薄结构会糊成一团；合成数据下每根杆件都能给到密集带色初值
3. 场景已是米制、塔基在原点，`--no-normalize-world-space` 让这个尺度原样进 Unity，**不需要二次对齐**

---

## 2. 文件与职责

全部在 `tools/eiffel/`，headless 驱动 Blender 5.1，**不需要 blender MCP 插件**（该插件在 `-b`
模式下不工作）。

| 文件 | 职责 |
|---|---|
| `scene.py` | 总装。地面 / 草坪 / 树 / 草 / 街具 / 雾 / 天空穹顶 / 调用其余模块。也是 CLI 入口 |
| `tower_asset.py` | 导入外部铁塔网格，归一化到米制（塔基 z=0、轴在原点、尖端 324 m），套材质 |
| `eiffel_tower.py` | 程序化铁塔生成器（回退方案） |
| `iron.py` | 铁塔漆材质 + 塔墩石材，盒状投影 |
| `cityscape.py` | 远景：Haussmann 街区、夏乐宫、军事学院、蒙帕纳斯塔、塞纳河、远景地面 |
| `sky.py` | HDRI 世界光照 + tone mapping |
| `assets.py` | Poly Haven 公开 API 下载与缓存（模型 / 贴图 / HDRI） |
| `render_rig.py` | 相机阵列 → COLMAP 数据集 + 射线投射初始点云 |
| `shot.py` / `preview.py` | 单张检视渲染 |
| `blenderutil.py` | `aim()` 相机朝向 |

---

## 3. 命令

```bash
B="/c/Program Files/Blender Foundation/Blender 5.1/blender.exe"
S="/c/Users/standalone/Documents/3DGS/Eiffel/scene_hdri.blend"
D="/c/Users/standalone/Documents/3DGS/Eiffel/dataset"
cd projects/GaussianExample-URP/tools/eiffel
```

构建场景（约 16 秒，素材已缓存）：
```bash
"$B" --background --python scene.py -- --detail 1.0 --texres 2k --out "$S"
```

单张检视：
```bash
"$B" --background "$S" --python shot.py -- --pos "76,-76,1.7" --look "0,0,95" \
     --lens 20 --res 900 --samples 128 --out check.png
```

渲染训练数据集（730 视角，约 50 分钟）：
```bash
"$B" --background "$S" --python render_rig.py -- --out "$D" \
     --res 1400 --samples 96 --points 600000 --point_views 90
```

手持微缩塔的独立数据集（只有塔和天空，Unity 里用 `GaussianCutout` 裁掉远处）：
```bash
"$B" --background "$S" --python render_rig.py -- --out "${D}_object" --mode object \
     --res 1400 --samples 96
```

训练：
```powershell
$env:EIFFEL_DATA="C:\Users\standalone\Documents\3DGS\Eiffel\dataset"
$env:EIFFEL_CAP="4000000"
C:\Users\standalone\Documents\3DGS\gsplat\examples\train_eiffel.ps1
```

---

## 4. 踩过的坑 —— 改代码前先读这一节

每一条都花了多轮排查，改回去就会重现。

### 4.1 光照配比：不要关掉天空自带的太阳盘

Blender 的 Nishita 天空在 strength 1 时是按「天空作为唯一光源」标定的。曾经关掉 `sun_disc`
另配一盏 3.0 的 Sun 灯，实测在空地上：只有太阳时沙地是 (122,90,67) 暖棕，**只有天空时是
(217,216,220) 近白**——三分之二的光是平铺的蓝色天光，把所有材质冲成中性灰，而且会烘进 splat。

现在用 HDRI（`kloofendal_48d_partly_cloudy_puresky`，CC0），太阳和天光天然自洽。曝光 +2.2。
若改回解析天空，曝光要 −3.8。

### 4.2 view transform 不要用 AgX

AgX 去饱和极强，会把铁锈棕压成中性灰并烘进 splat。用 Filmic。

### 4.3 材质 specular 要压到默认值以下

Principled 默认 `Specular IOR Level = 0.5`。地面在掠射角下的 Fresnel 白会把沙子的棕色冲成灰色——
这个查了最久。地面用 0.08，铁塔用 0.22。

铁塔 `Metallic` 必须是 0：喷漆铁不是裸金属，任何 metallic 权重都会让整个格构镜面反射天空。

### 4.4 大气雾必须是有界体积

把 Volume Scatter 挂在 World 上，环境光的光程变成无限长、全部消光，**整个场景渲染成纯黑剪影**。
必须用包住场景的球（`build_haze`，半径 700 m）。它**比不开雾还快**（18.9s vs 23.0s），散射让光线
提前终止。

密度别调高：8e-5 时铁塔会在雾里投出硬边阴影柱横跨天空，看着像渲染穿帮。3e-5 正好。

### 4.5 Poly Haven 植被资产不是「一个资产一个 mesh」

每个植被 .blend 里有：一个叫 `Cube` 的 geometry-nodes 散布域代理、一个节点载体、每个部件三级
LOD、以及树的**零件**（树干、五种叶片卡）与**装配好的整树**并列。

`jacaranda_tree` 返回 7 个 mesh，只有 `jacaranda_tree_LOD0`（223 万面）是整棵树。随机挑一个
去实例化，**七分之六会是光树干或几片叶子**——曾经的「枯枝林」就是这么来的。那些绿色圆顶是
`Cube` 散布域被当成草簇撒了一地。

`assets.append_model` 用 `mode="assembled"` 取整树、`mode="variants"` 取草簇变体。

**另一个连带坑**：同一资产 append 两次会被 Blender 重命名成 `_LOD0.001`，精确匹配失效又退回全部
零件。权重要靠加载一次后在列表里重复，不是 append 两次。

### 4.6 glTF 导入的两个陷阱

- **根部有个空物体带 Y-up→Z-up 旋转**。清 parent 时不保留世界变换，整座塔会横躺、零件散开。
- **自带自定义分裂法线（`custom_normal`）**。带着它铁塔从任何角度看都是纯黑，而旁边地面吃满
  阳光；换纯白漫反射也是黑。必须 `customdata_custom_splitnormals_clear()` + `normals_make_consistent()`。
  这个模型 65.8 万条边里有 65.7 万是边界边（单面零厚度片），作者的法线数据大概就是这么坏的。

### 4.7 Blender 的 Brick 节点做不出竖直立面

Brick 只用输入向量的 X/Y 出图案。立面是竖直的，朝 ±Y 的墙上 Y 是常量，网格退化成条纹。
必须按墙面法线在 (x,z) 和 (y,z) 两套坐标间切换。

而且 Brick 的砂浆各向同性，窗洞只能是方的，读起来是现代办公楼。Haussmann 的法式窗约
1.2 × 2.15 m，得用横竖两个一维遮罩相乘（`cityscape.haussmann_facade`）。

### 4.8 pycolmap 读 `images.txt` 遇空行即停

`iter(readline, '')` 的哨兵是空串。COLMAP 规范里图像没有 2D 观测点时第二行就是空行——照写会
得到一个**零图像的数据集，而且不报错**。`render_rig.py` 写一个惰性观测 `0.0 0.0 1` 占位。

### 4.9 逐帧 `bpy.ops.render.render()` 会泄漏

循环渲染 80 帧后内存涨到 9.7 GB、显存吃满 24 GB 然后卡死。改成给相机打关键帧、一次性渲染动画
序列，配合 `use_persistent_data`（场景不变只动相机，BVH 不该每帧重建）。

### 4.10 Blender 5.1 的 API 变化

- 合成器改成 `scene.compositing_node_group`（node group 数据块），`scene.node_tree` 已删
- `CompositorNodeOutputFile` 改成 `directory`/`file_name`/`file_output_items`，格式只剩
  `OPEN_EXR_MULTILAYER`。**所以初始点云不走 Position pass，改成直接 `scene.ray_cast`**
- 天空节点类型是 `MULTIPLE_SCATTERING`，不是 `NISHITA`
- 渲染引擎枚举是 `BLENDER_EEVEE`（不是 `BLENDER_EEVEE_NEXT`）
- 后台模式下 `view_transform` / `look` 的 enum introspection 返回 `NONE`，赋值本身有效，要 try/except
- `MapRange` 节点有两套同名插槽（float 和 vector），必须按索引取：`inputs[1]`、`inputs[2]`

### 4.11 悬空

改动几何后跑一次高度检查（把每个物体组的最低顶点 z 打出来）。历史上出现过：

- 铁塔悬空 1.6 m（`PLINTH_HEIGHT` 残留——程序化版本要自己造台座才需要这个偏移，外部模型自带
  z=0 的石砌塔墩）
- 远景楼群下面**没有地**（草坪盘半径 700 m，但街区排到 y=−880、军事学院 −950、蒙帕纳斯 −2300）。
  `cityscape.build_hinterland` 铺到 4200 m
- 夏乐宫整排悬空 26 m（构件放在 z=26，但它脚下高地的顶面在 z=0）

现在所有散布物都轻微埋进地面（树 −0.27、草 −0.06、街具 −0.03、街区 −0.6）。

### 4.12 草的密度只能靠粒子系统

草坪要 6 丛/㎡ 铺 4 万平米就是 26 万个实例。逐对象实例化会让 .blend 爆掉、初始点云的射线投射
爬行。用 hair 粒子系统 + 集合实例化（`build_grass`），同样密度构建时间反而更短。

---

## 5. 相机阵列

`render_rig.camera_poses()`，共约 730 个位姿：

- 地面环（r = 92 / 66 / 44 / 26 / 12 m，h = 1.65 m）——观众实际会站的位置，采样最密
- 抬高环（h = 12 / 30 m）
- 贴近塔身的壳（h = 130 / 175 / 220 / 262 / 292 / 316 m，r = 16–34 m）——塔尖最细、离地面相机最远，
  没有这几圈重建出来是糊的
- 无人机式球壳（h = 70 / 140 / 210 / 300 m，r = 120–175 m）
- 向外向下的一圈——让地面和树线不只被斜着看到

散布的树全部避开 `CAMERA_RADIUS + 12 m`，否则会有树正好长在某个训练相机镜头前，splat 会被挖出洞。

`--mode object` 换成环绕单塔的半球（`object_poses()`），用于手持微缩塔资产。

---

## 6. Unity 侧的具体数字

铁塔 324 m 高，把现有 GardenMR 的所有尺度常数推到区间外，配场景时要重设：

| 参数 | 花园的值 | 铁塔要设成 |
|---|---|---|
| `TabletopDiveController.m_DefaultTableScale` | 0.0375 | **0.0009**（0.0375 × 324 m = 12 m，那不是手上能拿的东西；0.0009 给出 0.29 m） |
| `SplatScaleHandle.m_MinScale` / `m_MaxScale` | 鸟瞰区间 | 约 0.0005 – 0.0025 |
| `m_DiveDuration` | 1.0 | **2.2**（由 `ThrowToDive.m_DiveDuration` 覆盖） |
| 落地 spawn point 的 splat 局部坐标 | — | 约 `(66, -66, 1.6)`，即训练相机环所在的位置 |

数据集是米制且塔基在原点（`--no-normalize-world-space`），splat 导进来 `localScale = 1` 就是 1:1。

导入：`Tools > Gaussian Splats > Create GaussianSplatAsset`。

**注意 Unity MCP 服务器当前连不上**（ConnectionRefused），Unity 侧的接线要么手工做、要么先修 MCP。

---

## 7. 抛掷触发

[`ThrowToDive.cs`](../../Assets/GardenMR/Scripts/ThrowToDive.cs) **只换触发方式**，不重写 dive。
挂在 `GardenMRRig`（`XRGrabInteractable` 所在的对象）上：

1. `selectExited` 时按最近若干帧位移的割线算释放速度（单帧差分噪声太大，扔不准）
2. 抛物线飞行，落地后摆正、停一拍
3. 调用现有的 `TabletopDiveController.Dive(m_LandingSpawn)`

**玩家最终站在哪里由 spawn point 决定**：`Dive()` 会把 spawn 的 splat 局部坐标钉到玩家脚下。

飞行期间 `CurrentState` 仍是 `Place`，所以要 `BeginExclusiveTransition()` 上锁；`Dive()` 前解锁。
rig 上的 Rigidbody 在飞行期间强制 kinematic（`m_ThrowOnDetach` 是 0，XRI 不会自己施加速度）。

**手上那把塔单独训一个资产**。324 m 场景 splat 缩到 0.3 m 拿到眼前是糊的——它没有厘米级细节。
抛出瞬间交叉溶解切到大场景，叙事上也说得通。

---

## 8. 后处理的位置（重要）

镜头类效果（bloom、景深、暗角、色差、颗粒）**不要烘进训练图**：

- **暗角**让画面边缘变暗，同一块地面在不同相机里落在画面不同位置，训练器只能把矛盾解释成
  「这块地的颜色跟视角有关」，塞进球谐，结果是转头时地面忽明忽暗
- **景深**把远处糊掉，splat 会把模糊重建成几何，塔尖直接烂掉
- **颗粒**是每帧独立噪声，等于告诉训练器同一点每次看颜色都不同

正确位置是播放端：Unity URP 的 Post-processing Volume，或最后剪辑。

能安全烘进训练图的只有逐像素、与视角无关的整体调色——色调曲线、对比度、饱和度。
现在是 Filmic + 曝光 +2.2。

---

## 9. 训练结果记录

| 版本 | 数据 | PSNR | SSIM | 备注 |
|---|---|---|---|---|
| `eiffel_mcmc_4m` | 590 视角，程序化塔，无天空穹顶 | 22.1 | 0.86 | 塔身好，塔尖糊。**已作废**（塔和光照都换了） |

7k 步时只有 15.7，30k 才到 22.1——这个场景收敛慢，别用早期指标下结论。

---

## 10. 待办

- [x] 铁塔几何（外部模型 + 归一化 + 材质）
- [x] 环境（地面 / 草坪 / 树 / 草 / 街具）
- [x] HDRI 光照 + 大气雾
- [x] 远景城市（Haussmann 街区 / 夏乐宫 / 军事学院 / 塞纳河）
- [x] 悬空修正
- [x] 相机阵列 + COLMAP 导出 + 射线投射初始点云（位姿已用小规模训练验证对齐）
- [x] `ThrowToDive.cs`
- [ ] **全分辨率数据集渲染 + 4M MCMC 训练**（下一步）
- [ ] 手持微缩塔的独立资产
- [ ] `MR_Eiffel` 场景 + `EnvironmentCatalog` 条目（沿用 `3DGS_MR_Shell.prefab`）
- [ ] 场景细化（按剩余真实度差距排序）：
  1. 草坪只有一种偏干的草，缺三叶草/杂草混生和踩踏磨损
  2. 铁塔近距离没有铆钉几何（靠法线贴图，两三米内看得出是平的）
  3. 场地空——没有围栏、垃圾桶、指示牌
  4. 塞纳河和耶拿桥是占位方块

---

## 11. 机器状态（repo 之外）

- 工作目录 `C:\Users\standalone\Documents\3DGS\Eiffel\`：`scene_hdri.blend`（当前场景）、
  `assets/`（Poly Haven 缓存）、`sketchfab/`（外部铁塔模型）、`renders/`（look-dev 截图）
- gsplat 在 `C:\Users\standalone\Documents\3DGS\gsplat`，`.venv` 已装好，
  启动器 `examples/train_eiffel.ps1`（自带 vcvars 引导）
- Blender 5.1.2 在 `C:\Program Files\Blender Foundation\Blender 5.1\`
- RTX 3090 / CUDA 12.8
