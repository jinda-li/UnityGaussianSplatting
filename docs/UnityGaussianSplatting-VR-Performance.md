# UnityGaussianSplatting VR 性能优化指南

## 成本模型

官方在 PC（RTX 3080 Ti）上对 **610 万 splat** 的耗时分解：

| 阶段 | 耗时 |
|------|------|
| Draw（片元填充） | ~4.5 ms |
| Sort（GPU Radix Sort） | ~1.1 ms |
| CalcViewData（投影 + SH 着色） | ~0.8 ms |

额外开销：

- 每个 splat 约 **48 字节** GPU 临时数据（排序 + 视图缓存）
- VR 双眼通常各跑一遍完整管线，成本约 **×2**
- 离屏 RT（`R16G16B16A16`）+ Composite Blit 在移动端也有带宽成本

---

## 优化优先级

| 优先级 | 手段 | 收益 |
|--------|------|------|
| ★★★★★ | 减少 splat 总数（导入压缩 + 源数据 prune） | 最高 |
| ★★★★☆ | 降低 SH Order（3 → 0/1） | 高 |
| ★★★★☆ | 提高 Sort Nth Frame（1 → 3+） | 高 |
| ★★★★☆ | 降低眼缓冲分辨率 / 开 FFR | 高 |
| ★★★☆☆ | 降低 Splat Scale（减少 overdraw） | 中 |
| ★★★☆☆ | 单 renderer、关 Debug/编辑 | 中 |
| ★★☆☆☆ | 改 RT 格式 / 去掉 Composite（需 fork） | 需改代码 |

---

## 一、导入阶段（最有效）

插件**没有运行时 LOD**，splat 数量在导入时基本定死。

### 资产质量预设

路径：`Tools → Gaussian Splats → Create GaussianSplatAsset`

| 预设 | 压缩比 | 适用平台 |
|------|--------|----------|
| **Very Low** | ~18× | Quest 首选 |
| **Low** | ~14× | Quest 备选 |
| **Medium** | ~5× | PC VR |
| High / Very High | ~1–3× | 高端 PC VR |

**Very Low 配置示例：**

- Position: `Norm11`
- Scale: `Norm6`
- Color: `BC7`
- SH: `Cluster4k`

### 控制 splat 数量

- 使用更小的训练场景，或在外部工具 prune 低 opacity / 远处 splat
- Quest 3 务实目标：**10 万～50 万** splat（百万级以上需激进压缩）
- 场景内尽量 **一个 `GaussianSplatRenderer` 对应一个资产**

---

## 二、运行时参数（`GaussianSplatRenderer` 组件）

### `Sort Nth Frame`（`m_SortNthFrame`）

- 默认 `1`：每帧排序
- VR 建议：**2～4**（静止观看可更高）
- 快速转头时可能有轻微透明瑕疵，但可显著节省 Sort 开销

### `SH Order`（`m_SHOrder`）

- 默认 `3`：完整视角相关颜色（最贵）
- VR / Quest 建议：**0 或 1**
  - `0`：仅 DC，颜色固定，最省
  - `1`：保留基础明暗，性价比高

### `Splat Scale`（`m_SplatScale`）

- 缩小屏幕 splat 尺寸 → 减少 overdraw（通常是最贵部分）
- 建议范围：`0.7～0.9`，按观感微调

### `Opacity Scale`（`m_OpacityScale`）

- 运行时调整效果有限；导入时 prune 更有效

### 其他

- `Render Mode` 保持 **Splats**，不用 Debug 模式
- VR 播放 build 移除 `GaussianCutout` 和编辑相关功能

---

## 三、VR / XR 平台设置

| 设置 | 建议 |
|------|------|
| 图形 API | Quest：**Vulkan**（OpenGL ES 不支持） |
| PC VR | **DX12 或 Vulkan**（不用 DX11） |
| 眼缓冲分辨率 | 降低或使用 Dynamic Resolution |
| 固定注视点渲染 | 开启 FFR |
| 目标帧率 | Quest 3：**72 FPS**（GPU 预算 <10 ms） |
| MSAA | **不要开**（插件不支持，且易出问题） |
| URP | Unity 6，关闭 Render Graph Compatibility Mode，添加 `GaussianSplatURPFeature` |

### 双眼成本

URP Feature 对**每个 XR 相机**调用 `SortAndRenderSplats`，Sort + CalcViewData + Draw 在双眼各执行一遍。官方未提供「单眼排序、双眼复用」选项。

---

## 四、渲染管线成本

```
Sort → CalcViewData → 离屏 RT (R16G16B16A16) → Composite Blit → 主场景
```

移动端可考虑 fork 后：

- 降低 RT 精度（如 `R8G8B8A8`）
- 或直接画进 framebuffer，省掉 Blit（混合行为会变化）

---

## 五、场景与内容

- 将场景缩放到合适观看距离，减少同屏 splat 覆盖
- 避免大量重叠 splat（透明 overdraw 爆炸）
- 多 GS 对象时正确设置 `Render Order`
- splat 不受 URP 光照影响，不要为 splat 加多余灯光

---

## 六、内存参考

除资产本体外，运行时约需 **~48 字节 / splat** 额外 GPU 内存。

示例：610 万 splat ≈ 额外 ~280 MB。Quest 上必须严格控制 splat 数量。

压缩资产（Norm6/11、BC7、SH Cluster）同时降低显存、解压带宽和缓存压力。

---

## 七、需改代码的进阶方向

官方未内置，常见二次开发：

1. 上限可见 splat 数（距离 / 视锥剔除后 cap）
2. 降频 `CalcViewData`（类似 `SortNthFrame`）
3. 单眼排序、双眼复用（近似）
4. SH Order 0 + 仅 DC 颜色
5. 跳过 Composite，直接混合
6. 导入时 prune 低贡献 splat

---

## 八、Quest 3 推荐起步配置

```yaml
资产导入: Very Low 或 Low
SH Order: 0 或 1
Sort Nth Frame: 3～4
Splat Scale: 0.7～0.9
Eye Resolution: 默认或略降
FFR: 开启
图形 API: Vulkan
Renderer 数量: 单场景单 GaussianSplatRenderer
目标 splat 数: < 50 万（理想 < 30 万）
```

---

## 九、一句话总结

UnityGaussianSplatting 的 VR 性能优化没有单独的「VR 魔法开关」，核心是：

> **少 splat、少 SH 计算、少排序频率、少像素填充、控制双眼重复开销。**

官方工具链中，**导入压缩 + Inspector 四个参数**（SH Order、Sort Nth Frame、Splat Scale、质量预设）即可覆盖大多数场景。
