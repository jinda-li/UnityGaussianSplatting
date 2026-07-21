# Photos → Unity 3DGS 工作流（标准流程）

关联计划：[2026-07-17 MCMC 400k 重训练](../plans/2026-07-17-mcmc-400k-retrain.md)

## 端到端一览

```
照片
  → RealityScan：Align → 定尺度 → Set Ground Plane
  → 导出 COLMAP（Registration，Grid plane；含 cameras/images/points3D + 去畸变图）
  →（必做）在 RealityScan 外裁稀疏点：删主体外 / 飞点
       · **推荐**：导出 `.rsbox`，脚本按 Reconstruction Region 裁 `points3D.txt`
       · 备选：CloudCompare / 浏览器编辑器 / 手写 AABB
  → 整理 gsplat 目录：images/ + images_4/ + sparse/0/
  → 初始点抽到 ≤ N（如 40 万）
  → gsplat MCMC（cap_max=N，**必须** --no-normalize-world-space）→ PLY
  →（建议）滤极端 floater
  → Unity：Create GaussianSplatAsset → 挂 GaussianSplatRenderer
  → Transform：Scale X=-1，微调位置/旋转/等比尺度
  → Play / VR 验证
```

---

## 0. 分工（别搞混）

| 步骤 | 用什么 | 做什么 |
|------|--------|--------|
| 对齐 / 尺度 / 地平线 | **RealityScan** | Align、Define Distance、Set Ground Plane |
| 删房子外点、删飞点 | **脚本按 `.rsbox` 裁**（或 CloudCompare） | RealityScan **不能**删稀疏 tie points |
| Reconstruction Region | RealityScan | **只影响建 mesh**，**不会**自动裁 COLMAP；但导出的 `.rsbox` 可给脚本当裁剪盒 |
| 训练 | **gsplat** | 必须 `--no-normalize-world-space` |
| 进引擎 | **Unity** | 导入 PLY；Scale X=-1 |

---

## 1. 照片 → RealityScan

1. 导入照片 → **Align**（得到 Component，稀疏点云）。
2. **定尺度**：Define Distance / Distance Constraints → 填真实米数 → **ALIGNMENT → Update**。  
   （每个约束点至少打在 **2 张图**上，否则 Update 会失败。）
3. **扶水平**：**SCENE 3D → TOOLS → Set Ground Plane**，地面贴网格。
4. （建议）设 **Reconstruction Region**（房子/主体范围）：仍**不会**自动裁 COLMAP，但请 **导出 `.rsbox`**，后面用脚本裁 `points3D.txt`。  
   导出：**SCENE 3D / TOOLS → Export → Reconstruction Region**（或 MESH & COLOR 里对应 Export）。

官方：[Scaling](https://rshelp.capturingreality.com/en-US/tutorials/scaling.htm)、[Ground Plane](https://rshelp.capturingreality.com/en-US/tools/alignground.htm)、[Reconstruction Region](https://rshelp.capturingreality.com/en-US/tools/reconbox.htm)

---

## 2. 导出 COLMAP

**ALIGNMENT → Registration**（或 SCENE 3D → Registration）：

| 项 | 建议 |
|----|------|
| Directory structure | **COLMAP standard** |
| File type | **ASCII (.txt)** |
| Undistort images | Yes |
| Export images | Yes（png / `00000...`） |
| Coordinate system | **Grid plane** |
| Scene transform | 全 0 |

导出后必须有：

```
images/                 # 去畸变照片
sparse/0/
  cameras.txt
  images.txt            # 缺这个不能训练
  points3D.txt
```

文件名用小写；路径尽量英文、无空格。

---

## 3. 裁稀疏点（RealityScan 之外）

RealityScan 的 Point Lasso **只能选中，不能删**；Filter Selection / Cut by Box / Reconstruction Region 只对 **mesh** 有效，**不会**改 COLMAP 的 `points3D`。

**不要改** `cameras.txt` / `images.txt`，只替换 `points3D.txt`。

### 推荐 A — 用 `.rsbox` 脚本裁（可复现，优先）

RealityScan 导出的 Reconstruction Region 是 XML（`.rsbox` / 旧名 `.rcbox`），关键字段：

| 字段 | 含义 |
|------|------|
| `CentreEuclid` / `<centre>` | 盒子中心（与 COLMAP **Grid plane** 同一坐标系） |
| `widthHeightDepth` | 盒子全尺寸（宽、高、深） |
| `yawPitchRoll` | 盒子朝向（度）；多为 `0 0 0` → 轴对齐盒 |

判定：把点变到盒子局部坐标 `p_local = Rᵀ (p − centre)`（`R` 由 yaw/pitch/roll 构造），当 `|p_local| ≤ size/2` 时保留。

**步骤**

1. 在 RealityScan 调好 Reconstruction Region → 导出 `*.rsbox`，放到数据集旁（与 `sparse/` 同级即可）。  
2. 确认 COLMAP 导出用的是 **Grid plane**（与 `CentreEuclid` 一致）。  
3. 跑裁剪脚本：读 `.rsbox` + `sparse/0/points3D.txt` → 只保留盒内点 → **先备份再覆盖** `points3D.txt`（可重编号 POINT3D_ID）。  
4. 若点数仍 > N（如 40 万），再随机抽稀到 ≤ N。

示例（America House，一次实测）：

```
.rsbox: BotanicalGardenAmericaRealityScan.rsbox
  centre ≈ (0.81, 0.68, 3.98)
  size   ≈ 15.59 × 10.24 × 8.76
  yawPitchRoll = 0 0 0
裁前 715401 → 裁后 444706（删 270695，含极远飞点）
备份: sparse/0/points3D_before_rsbox_crop_<timestamp>.txt
```

`.rsbox` 里的 `<Residual R t s>` 是组件对齐残差，**裁剪时不要用**；点已在 Euclid/Grid 坐标时，只用 `centre` + `widthHeightDepth` + `yawPitchRoll`。

### 备选 B — CloudCompare / 浏览器（手动）

- **CloudCompare**：`points3D` → PLY → Segment / Clip → 再转回 `points3D.txt`。  
- **浏览器**：[Panoton Point Cloud Editor](https://www.panoton.de/tools/pointcloud-editor/)（直接吃 COLMAP `points3D.txt`）。大文件（几十万点）可能较慢。  
- **[SuperSplat](https://superspl.at/editor)**：只编辑**训练后的** Gaussian Splat PLY，**不能**裁 COLMAP 稀疏点。

### 备选 C — 手写 AABB / 高度

没有 `.rsbox` 时，按经验包围盒或高度阈值过滤 `points3D.txt`。

---

## 4. 整理成 gsplat 数据

```
Dataset/
  images/
  images_4/             # --data-factor 4 时需要（把 images 缩到 1/4）
  sparse/0/
    cameras.txt
    images.txt
    points3D.txt        # 已裁切，且点数 ≤ N
```

### 点数预算（例如 N=400000）

- `cap_max` **只阻止再长，不会砍已有点**。
- 必须先把初始 `points3D` 抽到 **≤ N**，再设 `--strategy.cap-max N`。

---

## 5. 训练（gsplat）→ PLY

| 项 | 选择 |
|----|------|
| 包 | gsplat（`simple_trainer.py mcmc`，examples ≈ v1.5.3） |
| 点数 | `--strategy.cap-max <N>` |
| 世界坐标 | **`--no-normalize-world-space`（必开）** |

### 必开：`--no-normalize-world-space`

默认 `normalize_world_space=True` 会 PCA/归一化，**抹掉** RealityScan 的地平线与尺度。  
进 Unity 会表现为：Transform 旋转全 0，地面仍歪。

```bash
cd gsplat/examples
python simple_trainer.py mcmc \
  --strategy.cap-max 400000 \
  --data-dir "<Dataset>" \
  --result-dir ./results/<run_name> \
  --data-factor 4 \
  --no-normalize-world-space \
  --disable-viewer --save-ply --ply-steps 7000 30000
```

Launcher 示例：`gsplat/examples/train_america_rscleaned_400k.ps1`（已带该参数）。

---

## 6. PLY → Unity

1. **Tools → Gaussian Splats → Create GaussianSplatAsset** 导入最终 `point_cloud_*.ply`  
2. 挂到 `GaussianSplatRenderer`，`RenderMode = Splats`  
3. Transform：
   - **Scale X = -1**（COLMAP ↔ Unity 手性）
   - 关 normalize 后地面应接近水平；仍歪再微调 Rotation  
   - 尺度不对则三轴等比放大（X 保持负号）

取景靠近训练相机距离；拉太远会像碎点。

---

## 7. 本项目备忘（America House）

- 照片：550 张（Kaggle CC BY-SA，署名 Simon Bethke）
- 数据集目录：`Botanical Garden-America-RealityScanCleaned/`（`images/` + `sparse/0/` + `BotanicalGardenAmericaRealityScan.rsbox`）
- 稀疏点裁剪：按 `.rsbox` 裁 `points3D.txt`（715401 → 444706）；训练前再抽到 ≤400k
- 目标：400k（抽稀 + `cap_max=400000`）
- 数据集示例：`BotanicalGarden-America-RSCleaned-400k`
- 正确朝向跑次 PLY：  
  `gsplat/examples/results/america_rscleaned_400k_nonorm/ply/point_cloud_29999.ply`
