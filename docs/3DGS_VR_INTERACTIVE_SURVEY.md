#  3DGS / 4DGS 互动体验研究综述（精简版）

> 面向 Quest 3 + Unity 落地与互动项目。基于你提供的种子文献及引用链整理。  
> 更新：2025-06

---

## 一句话结论


| 阶段           | 能做什么                                  | 现成参考                      |
| ------------ | ------------------------------------- | ------------------------- |
| **现在（3DGS）** | 走进真实扫描场景、自由漫游、POI/多人                  | 虚拟博物馆论文、本仓库               |
| **近期（互动）**   | 碰撞/选取靠 **mesh 代理**，不要直接打 splat        | GS-Verse、GaMeS（上版综述）      |
| **中期（4DGS）** | 动态场景播放；Quest 需 **压缩 + 按帧活跃 splat 筛选** | 4D-GS → ST-4DGS → 4DGS-1K |


**射击/工厂类互动**：文献几乎没有完整游戏案例；最接近的是 **博物馆漫游 + 多人 + 碰撞体**，物理破坏需另接 Unity 引擎层。

---

## 依赖关系（简图）

```
3DGS (Kerbl 2023)
  ├─► Unity 落地 ──► aras-p/UnityGaussianSplatting ──► zachdrouin/GaussianSplatViewer (Quest 3)
  ├─► 互动漫游案例 ──► ISPRS 虚拟博物馆 (Unity 6 + 碰撞 + Photon 多人)
  └─► 4D 动态场景
        ├─► 4D-GS (CVPR 2024) — 变形场 + HexPlane，~30 FPS 真场景
        ├─► ST-4DGS (SIGGRAPH 2024) — 时空一致、更紧凑
        └─► 4DGS-1K (NeurIPS 2025) — 剪枝 + 活跃 mask，1000+ FPS (PC)
```

---

## 种子文献卡片

### 1. 虚拟博物馆（互动落地标杆）

**Realistic and Interactive Virtual Museum Representation Using 3D Gaussian Splatting**  
Kwon & Yu · ISPRS Annals 2025 · [PDF](https://isprs-annals.copernicus.org/articles/X-M-2-2025/185/2025/isprs-annals-X-M-2-2025-185-2025.pdf)


| 项        | 内容                                                                                                  |
| -------- | --------------------------------------------------------------------------------------------------- |
| **做了什么** | 同一展厅内容，对比 **360° 全景** vs **3DGS**；用户研究 30 人                                                         |
| **工作流**  | 相机拍 1449 张 → RealityCapture 对齐 → Postshot 训练 300k 步 → 导出 PLY → **Unity 6 + UnityGaussianSplatting** |
| **互动**   | 自由行走（键盘/鼠标）、展品 POI、**Collider 碰撞**、**Photon Fusion 多人**                                             |
| **结论**   | 3DGS 在存在感(IPQ)、满意度、复访意愿上 **全面显著优于** 360°；弱点是文字清晰度                                                   |
| **对你们**  | **博物馆/工厂参观、展厅射击背景** 的直接模板；互动层 = Unity 常规组件，非 splat 本身                                               |


---

### 2. GaussianSplatViewer（你们的工程基座）

**[zachdrouin/GaussianSplatViewer](https://github.com/zachdrouin/GaussianSplatViewer)** · Unity 6 + URP + OpenXR


| 项        | 内容                                                            |
| -------- | ------------------------------------------------------------- |
| **做了什么** | Quest 3 原生 **静态 PLY** 查看器：文件浏览、6DoF locomotion、GPU compute 渲染 |
| **工作流**  | 设备存储选 PLY → Burst 异步加载 → URP Pass 渲染                          |
| **互动**   | 漫游 + UI；**无**物理/射击/场景编辑                                       |
| **对你们**  | 当前仓库即此路线；射击/工厂 = 在此之上加 **XR Interaction + 碰撞代理 + 关卡逻辑**       |


---

### 3. 4D-GS（4D 基础）

**4D Gaussian Splatting for Real-Time Dynamic Scene Rendering**  
Wu et al. · CVPR 2024 · [项目页](https://guanjunwu.github.io/4dgs/) · [代码](https://github.com/hustvl/4DGaussians)


| 项        | 内容                                                         |
| -------- | ---------------------------------------------------------- |
| **做了什么** | 一组 **canonical 3D Gaussian** + HexPlane/MLP **变形场**，表达动态场景 |
| **工作流**  | 多视角视频 → COLMAP → 训练 ~30min → 实时查看器（PC）                     |
| **性能**   | 合成 ~82 FPS；真实场景 ~30 FPS @ 3090（**未上 Quest**）               |
| **互动**   | 仅 **观看**；支持合并多个 4D 场景                                      |
| **对你们**  | 4D 内容 **生产管线** 起点；Quest 需离线烘焙或大幅简化                         |


---

### 4. ST-4DGS（更高质量 4D）

**ST-4DGS: Spatial-Temporally Consistent 4D Gaussian Splatting**  
Li et al. · SIGGRAPH 2024 · [代码](https://github.com/wanglids/ST-4DGS)


| 项        | 内容                                            |
| -------- | --------------------------------------------- |
| **做了什么** | 在 4D-GS 思路上加强 **时空一致性**，Gaussian 更贴运动物体表面、更紧凑 |
| **工作流**  | 动态数据集 → COLMAP → **RAFT 光流** → 训练             |
| **依赖**   | 基于 3DGS、4DGaussians、D3DGS                     |
| **互动**   | 重建 + 渲染；**无 VR/游戏**                           |
| **对你们**  | 工厂 **运动设备/流水线** 扫描重建时优先考虑；仍属 **离线训练 → 运行时播放** |


---

### 5. 4DGS-1K（4D 实时化方向）

**1000+ FPS 4D Gaussian Splatting for Dynamic Scene Rendering**  
Yuan et al. · NeurIPS 2025 · [arXiv:2503.16422](https://arxiv.org/html/2503.16422) · [项目页](https://4dgs-1k.github.io/)


| 项        | 内容                                                                  |
| -------- | ------------------------------------------------------------------- |
| **做了什么** | 针对 4DGS **冗余**：剪短寿命 Gaussian + **每帧活跃 mask**，存储 **÷41**，光栅 **×9**   |
| **工作流**  | 在已有 4DGS 模型上 **后处理压缩**                                              |
| **性能**   | **1000+ FPS @ RTX 3090**（PC）；质量与原版相当                                |
| **互动**   | 无；纯渲染加速                                                             |
| **对你们**  | 4D 上 Quest 的 **关键思路**：只渲染当前帧活跃 splat；与你们 `SplatCuller` 理念一致，需扩展到时间维 |


---

## 按目标选路线

### A. 博物馆 / 展厅漫游 / 工厂参观（**最先落地**）

```
实景拍摄 → RealityCapture/Postshot → PLY/SPZ
    → Unity 6 (GaussianSplatViewer)
    → Collider + XRI + POI/UI
    → 可选 Photon 多人
```

**参考**：ISPRS 虚拟博物馆（已验证用户更愿意自由走动而非定点全景）。

### B. 射击 / 可破坏互动（**文献空白，工程拼接**）

```
静态 3DGS 场景（背景）
    + Unity Mesh Collider / 简化碰撞体（命中判定）
    + 可选：Gaussian Grouping 离线分物体 → 击毁时隐藏/替换 PLY 块
```

尚无公开「3DGS FPS 游戏」论文；勿用 splat 透明度做射击判定。

### C. 动态场景 / 4D（**下一阶段**）

```
多视角视频 → 4D-GS 或 ST-4DGS 训练
    → 4DGS-1K 压缩
    → 导出逐帧或 residual 流
    → Quest：仅播放预烘焙片段（局部动态），全场景 4D 仍超算力
```

**远期**：NVIDIA Play4D + QUEEN 式流式（服务器编码 → 头显解码），适合表演型内容而非本地单机游戏。

---

## 相关但非种子的高价值参考（互动向）


| 工作                             | 为何相关                                 |
| ------------------------------ | ------------------------------------ |
| **GS-Verse** (2025)            | Mesh + GS + **Unity 物理交互** VR，用户研究验证 |
| **GaussianShopVR** (UIST 2025) | VR 内分割/编辑 3DGS 场景                    |
| **VR-GS** (SIGGRAPH 2024)      | GS + XPBD 物理 VR（学术 demo，非 Unity 产品）  |
| **Meta Spatial SDK Splats**    | Quest 官方 .spz，单场景 ≤150k，快速验证         |


---

## 推荐阅读顺序

1. ISPRS 虚拟博物馆 — **互动怎么搭**
2. zachdrouin/GaussianSplatViewer — **Quest 运行时**
3. 4D-GS — **动态表示是什么**
4. ST-4DGS — **更好的 4D 重建**
5. 4DGS-1K — **4D 如何变快**

---

## 与本仓库映射


| 论文能力              | 本仓库现状                                         |
| ----------------- | --------------------------------------------- |
| PLY 加载 + Quest 渲染 | ✅ `PLYLoader`, `GaussianSplatPass`            |
| 自由漫游              | ✅ `VRLocomotion`, `VRRigController`           |
| 文件浏览              | ✅ `VRFileBrowser`                             |
| 大场景               | 🔶 `ChunkStreamingManager`, `SplatLODManager` |
| 碰撞 / 射击 / 多人      | ❌ 待做（参考博物馆论文）                                 |
| 4D 播放             | ❌ 待做（参考 4D-GS + 4DGS-1K 思路）                   |


---

*如需扩展：提供更多种子论文 BibTeX，可在此文档上追加「引用递归」章节。*