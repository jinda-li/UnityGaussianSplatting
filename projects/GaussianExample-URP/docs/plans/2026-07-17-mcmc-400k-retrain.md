# 3DGS-MCMC 400k 重训练计划（2026-07-17）

目标：把 Garden 场景的高斯点数压到 **~400k**，在 Quest 3 上跑到可用帧率，视觉质量尽量接近原版。

## 背景（当日调试结论）

- 原版 Botanical Garden 资产 848 万点；随机抽稀 5%（106 万点）在 Quest 3 上仍只有 ~6-10 FPS。
- Profiler 实锤瓶颈：`TimeUpdate.WaitForLastPresentationAndUpdateTime` 占 94%，CPU 全程空等 GPU
  → 纯 GPU 瓶颈，根源是 splat 半透明混合的 overdraw（`ZWrite Off` + `Blend OneMinusDstAlpha One`
  无法 early-Z 剔除）。切 `DebugPoints`（不透明 + ZWrite On）立即流畅，验证了这一点。
- 结论：靠"更聪明地减点"而非随机抽稀。随机抽稀丢的是均匀采样，重要性驱动的方法同点数画质高得多。

## 数据

- 原始 COLMAP 数据集（含照片 + 相机标定，RealityScan 导出）：
  `C:\Users\standalone\Documents\3DGS\Botanical Garden - America - Colmap Data`
- 来源：Kaggle `simonbethke/botanical-garden-america`（CC BY-SA 4.0，须署名 Simon Bethke）
- 对应 SuperSplat 场景：https://superspl.at/scene/e2b36b69
- 注意：换成了 **America House**（不再是 Victoria House）。Unity 场景里的资产要整体替换。

## 方法选型（已比较过的方案）

| 方案 | 结论 |
|---|---|
| **3DGS-MCMC via gsplat（选定）** | 训练时 `cap_max` 硬性锁定点数预算，重定位代替克隆/分裂，同预算画质最优；gsplat 维护活跃、COLMAP 直入、PLY 直出 |
| Mini-Splatting | 自然输出 ~50 万档，预算控制是间接的；研究代码无持续维护 |
| LightGaussian | 为 ~66% 剪枝率设计，且需先训完整模型再剪，多两道工序；量化针对存储体积，对 overdraw 无益 |
| Taming 3DGS | 也支持点数预算，备选；工程成熟度不如 gsplat |

## 实施步骤

### 1. 环境（已完成一半）

- [x] CUDA Toolkit 12.8（已装，winget）
- [x] VS 2022（已有）
- [ ] 克隆 gsplat：`git clone https://github.com/nerfstudio-project/gsplat --recursive`
- [ ] venv + PyTorch cu128：`pip install torch torchvision --index-url https://download.pytorch.org/whl/cu128`
- [ ] `pip install gsplat` 及 examples 依赖（首次运行会 JIT 编译 CUDA 扩展，需 nvcc + cl 在 PATH）
- 注：之前克隆的 `C:\Users\standalone\Documents\3DGS\LightGaussian` 已弃用，可删

### 2. 训练（RTX 3090，每档预计 30-60 分钟）

```bash
cd gsplat/examples
python simple_trainer.py mcmc \
  --strategy.cap-max 400000 \
  --data-dir "C:/Users/standalone/Documents/3DGS/Botanical Garden - America - Colmap Data" \
  --result-dir ./results/america_400k
```

- 跑两档：**400k**（主目标）+ **200k**（保险对比档）
- 数据集是 RealityScan 导出的 COLMAP：先确认目录结构（`images/` + `sparse/0/`），
  不符时用 `--data-factor` / 整理目录适配
- 导出 PLY（gsplat 自带 export，或训练结束的 checkpoint 转 PLY）

### 3. Unity 接入

1. 用包内 `GaussianSplatAssetCreator`（Tools → Gaussian Splats → Create GaussianSplatAsset）导入 PLY
   - 压缩档位保持与现资产一致（pos=Norm11, SH=Cluster64k 级别）
2. Garden 场景 `GaussianSplats` 物体换资产引用；`m_RenderMode` 记得改回 `Splats`（0）——
   当前场景里还留着调试用的 `DebugPoints`（1）
3. 场景变换（当前 scale=(-1,1,1)、rotation X180°）对新资产重新校准

### 4. 实测协议

1. `manage_build` 打 development APK → adb 安装
2. 无线 Profiler 连 Quest 3，看 `WaitForLastPresentationAndUpdateTime` 占比是否显著下降
3. 若 400k 仍不达标 → 换 200k 档；仍不行再叠加：
   - `m_SplatScale` 调小（overdraw 面积按平方降）
   - shader 里 quad 半径 `quadPos *= 2` → `*= 1.5`（牺牲高斯尾部换 overdraw）
   - `m_SortNthFrame` 调大、FFR（注视点渲染）
4. 达标标准：72Hz 稳定（帧时间 ≤13.9ms），画质主观可接受

## 风险

- gsplat Windows 编译失败 → 备选：WSL2 + CUDA，或 Taming 3DGS
- RealityScan 的 COLMAP 导出格式与 gsplat loader 不完全兼容 → 写转换脚本
- 400k 对整栋温室场景仍偏少，细叶会糊 → 已有 200k/400k 两档对比，必要时加 600k 档
- overdraw 与点数非线性相关：点少了但每点更大，收益可能打折 → 配合 `m_SplatScale` 实测

## 许可

CC BY-SA 4.0：发布物需署名 Simon Bethke 并同协议共享。license.txt 随资产保留。
