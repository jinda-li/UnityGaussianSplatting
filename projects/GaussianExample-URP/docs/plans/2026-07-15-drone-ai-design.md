# DroneAI 设计文档（2026-07-15）

VR 3DGS 森林场景中的无人机 AI：搜索 → 识别 → 俯冲攻击流程，配合暂停式教学 UI。
占位方块代替无人机模型，后续替换。

## 组件

全部位于 `Assets/Scripts/DroneAI/`。

### DroneAI.cs（Drone 根节点 = 重心）

状态机：`Search → Identify → Attack`，或 `Search → Leave → Destroy`。

- **Search**：朝 `flybyTarget` 以 `searchSpeed` 直线飞行，yaw 平滑转向飞行方向。
  到达 target（`arriveThreshold`）后原地悬停继续搜索 `searchHoverDuration` 秒，
  仍未发现玩家 → Leave。
- **发现判定**：每 `detectInterval` 秒一次；距玩家头部 ≤ `detectRadius` 且
  Linecast（`obstacleMask`）无遮挡，**持续保持视线 ≥ `detectConfirmTime`（默认 2s）**
  才确认发现（视线中断计时清零）→ Identify。Leave 状态不再检测。
- **Identify**：以 `approachSpeed` 飞向玩家，保持在距玩家头部 `hoverDistance`
  （默认 5m）悬停，yaw 持续跟踪玩家。首次到达悬停距离后开始计时，
  `identifyDuration` 秒后 → Attack。
- **Attack**：进入瞬间锁定玩家头部位置，沿该 3D 直线以 `diveSpeed` 俯冲，
  中途不修正（侧向翻滚可躲避）。此时启用 trigger 碰撞体；撞到任何 collider
  → `Explode()`；撞到玩家（`GetComponentInParent<PlayerController>()`）额外先触发
  `OnPlayerHit`。冲过锁定点 `diveOvershoot` 米仍未命中 → 空中自爆。
  每帧附加 SphereCast 防止高速穿透。
- **Leave**：保持当前朝向直线飞行，`leaveLifetime`（默认 10s）后 Destroy。
- **Explode()**：空壳实现——触发 `OnExploded`、log、Destroy；留 VFX/音效接口。
- **事件**：静态 `StateChanged(DroneAI, from, to)`（供 DroneLessonUI 捕捉动态生成的
  实例）+ Inspector 可见的 `onPlayerHit` / `onExploded` UnityEvent。
  进入 Search（出生）也触发一次 StateChanged(None→Search)。

朝向策略：根节点只做 yaw（机身保持水平），碰撞体姿态稳定。

### DroneMotionSim.cs（Graphic 子物体）

纯视觉：由根节点每帧位移推算速度，换算局部坐标系 →
前向速度 → pitch 前倾，侧向速度 → roll 侧倾，yaw 角速度 → 附加 roll 压弯。
倾角上限 `maxTiltAngle`、平滑 `tiltSmoothing`，Inspector 可调。无上下浮动。
`Time.deltaTime == 0`（暂停）时跳过。

### DroneSpawner.cs

场景常驻。`Start` 后 `spawnDelay`（默认 20s，scaled time）在自身位置实例化
`dronePrefab`，注入 `flybyTarget`（列表中随机取一）与玩家引用。
公开 `SpawnNow()` 供手动/重复触发。

### DroneLessonUI.cs

场景全局单例。订阅 `DroneAI.StateChanged`。按状态在 Inspector 配置条目
（标题/正文/启用/只弹一次，默认每状态全局只弹一次）。
触发：`Time.timeScale = 0`，面板置于头显前方 `panelDistance`（默认 1.2m），
yaw-only 朝向玩家；OK 按钮 → `timeScale = 1`、收起、标记已弹。
面板为场景内引用的 world-space Canvas（默认隐藏），XRI 交互走 unscaled time，
暂停时可正常点击。

## 决策记录

- 撞掩体/地面：同样爆炸销毁；仅撞玩家额外触发 OnPlayerHit（对应 Death /
  Survive but injured 分支）。
- 发现判定：仅距离 + 无遮挡（无视野锥角），但需 2s 持续视线确认。
- 暂停：`Time.timeScale = 0` 全局冻结。
- OK 按钮：方案 1 —— UGUI + `TrackedDeviceGraphicRaycaster` + `XRUIInputModule`。
  Forest 玩家 rig 为极简版（手部仅 TrackedPoseDriver），需补：EventSystem +
  XRUIInputModule、InputActionManager（XRI Default Input Actions）、
  Left/Right_NearFarInteractor prefab（XRI Starter Assets 现成资源）挂到手部锚点。
- Identify 悬停只用 yaw 对准玩家，不加俯角。
- prefab 与场景接线通过 UnityMCP 直接完成，不写 Editor 菜单脚本。

## 场景接线（Forest.unity）

1. `DroneSpawner` 空物体（位置/朝向手动摆放）
2. 若干 flyby target 空物体 → spawner 列表
3. `DroneLessonUI` 单例物体 + 面板 prefab 实例（隐藏），填文案
4. XR UI 输入链路（见上）

## 验证

- Unity Roslyn/控制台编译检查所有脚本
- 每个状态切换打 Debug log，戴头显实测流程
