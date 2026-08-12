# WBall · v3.1 需求 —— 右侧对战区自定义与「对战区设置」窗

> 文档性质：功能需求（基线 v3.0.0，功能基线 v2.12.5）。日期：2026-07-27 ｜ 目标版本：**3.1.0**
> 状态：**已交付**（M1~M5 完成，§7 验收结果见 §10 附录；§9 待拍板问题已由用户答复并落地）

---

## 0. 一句话定义

**v3.1 = 把右侧对战区（战场）从"改 JSON / 背命令"变成"开窗就能调"**：把散落在代码里的对战区硬编码常量（炮塔半径联动、护罩环倍率、大球尺寸/动量公式、小球速度、开局预载弹药、护盾计价）全部升为 `arena_layout.json` 配置字段，补齐对应命令，并新增一个可停靠的**「对战区设置」窗**作为这些命令的图形外壳，带派生值实时预览（格数 / 每方初始血量 / 初始大球动量）。

**不做**：不改物理引擎语义、不改经济链路、不改左侧落球区、不改武器库结构。**默认值一律等于 v3.0 现行硬编码值**，一个字段都不动时行为与 v3.0 逐帧一致（见 §6 确定性纪律）。

---

## 1. 现状盘点（为什么要做）

用户可调的对战区参数目前**只有** `arena_layout.json` 的 10 个字段（`arena.size/gravity/layout/mode/targeting` 五条命令覆盖其中 5 个），其余全是代码常量：

| 关注点 | 现状 | 位置 |
|---|---|---|
| 战场逻辑尺寸 | 可配（`arena.size`） | `Battle/BattleConfigStore.cs:27` |
| 领地格边长 | 可配（**无命令**，只能手改 JSON） | `BattleConfigStore.cs:40` ｜ 生效 `BattleRuntime.cs:144` |
| 炮塔半径 | 可配（**无命令**） | `BattleConfigStore.cs:32` ｜ 生效 `BattleRuntime.cs:689` |
| 炮塔落位（离角比例 0.12/0.14） | **硬编码** | `BattleRuntime.cs:680-681` |
| 护罩环半径倍率 1.55 | **硬编码两处**（判定 + 渲染必须同值） | `BattleRuntime.cs:900`、`Stage/ArenaView.cs:234` |
| 护盾计价 50000/值 | **硬编码** | `BattleRuntime.cs:893` |
| 最大生命 / 最大护盾 / 初始护盾 | 可配（`turret.set`，但**逐座手改**，四座要敲四遍） | `BattleConfigStore.cs:15-17` |
| 大球尺寸公式 `cell*0.5*value^0.25`（0.5~5 格） | **硬编码** | `BattleRuntime.cs:345` |
| 大球动量：抖动 ±25%、`speed/value^0.12`、夹 60~700 | **硬编码** | `BattleRuntime.cs:347-351` |
| 大球质量 = 结算数值 | **硬编码** | `BattleRuntime.cs:364` |
| 开局预载 12 发 value=1「直射」 | **硬编码** | `BattleRuntime.cs:129-130` |
| 小球速度 380 / 尺寸 0.5 格 | **硬编码** | `BattleRuntime.cs:428-429` |
| 最大弹数 / 决胜时刻 | 可配（**无命令**） | `BattleConfigStore.cs:33,43` |

**两个必须在文档里说清的耦合**（否则用户调参会调出反直觉结果）：

1. **网格边长 = 初始血量**。领地模式下 `MaxHp` 被覆写成"开局占有格数"（`BattleRuntime.cs:179-181`），即
   `每方初始血量 ≈ ⌈W/cell⌉ × ⌈H/cell⌉ ÷ 炮台数`。960×900、cell=10 → 96×90=8640 格 → 四方各 2160。
   `TurretDefinition.MaxHp` 字段**只在 direct 模式生效**；改格边长会同时改血量与对局时长。
2. **护盾的"血量"单位是 5 万**。初始护盾 500000 = 可挡 10 发小球（`costPerValue=50000`）；
   用户所说的"初始护盾血量"应当同时以**绝对值**和**可挡小球数**两种口径显示。

---

## 2. 需求清单

### 2.1 配置模型扩展（AC）

| 编号 | 需求 |
|---|---|
| AC-01 | `ArenaLayoutConfig` 新增 §3 全部字段；**每个字段默认值 = 现行硬编码值**；JSON 缺字段即取默认（老 `arena_layout.json` 直接可用，不需迁移） |
| AC-02 | 上述硬编码点全部改为读配置：炮塔落位比例、护罩环倍率（判定与渲染**共用同一字段**，杜绝两处漂移）、护盾计价、大球尺寸/动量公式、大球质量系数、开局预载、小球速度/尺寸 |
| AC-03 | `BattleConfigStore.Validate` 扩展范围校验（§3 表内 min/max）；越界 = 加载失败走既有"回退内置模板 + Error 日志"语义，不静默 clamp |
| AC-04 | `ScenarioSnapshot` 随之携带新字段：`ScenarioStore.CloneArena` 补齐全部新字段，**并顺带修既有漏项 `SuddenDeathAtSeconds`**（v2.12.4 引入但未进 CloneArena，存/读剧本会丢该值 → 现存缺陷） |
| AC-05 | 新增"等比缩放"派生操作：以系数 k 同乘 `Width/Height/TurretRadius/CellSize` 及速度夹值上下限 —— **格数不变 ⇒ 血量与对局结构不变**，纯观感放大（区别于单改格边长） |

### 2.2 「对战区设置」窗（AW）

| 编号 | 需求 |
|---|---|
| AW-01 | 新增工具窗 `id=arenaset`，标题「对战区」，默认停靠右侧、与「对战台」同组标签、`DefaultVisible=false`，`win.show name=arenaset` 唤出；关闭=隐藏（框架不变量） |
| AW-02 | **窗口是命令的图形外壳**：所有编辑一律经 `CommandBus` 下发 §4 命令，不直写 `BattleConfigStore`；关掉本窗后控制台手敲命令可完成同样的事（框架不变量"控制台完备性"） |
| AW-03 | 打开/应用/战场重置后自动回读当前配置填充控件（不做静态默认值），字段旁标注**生效时机**：`即时` / `需重置` |
| AW-04 | 分组呈现（§5 布局）：① 规模 ② 网格与领地 ③ 护盾与血量 ④ 大球动量 ⑤ 小球 ⑥ 全局 |
| AW-05 | **派生值实时预览区**（只读，随控件改动即时重算，不必真的应用）：格数 `cols×rows`、总格数、每方初始格数（=领地模式初始血量）、初始护盾可挡小球数、初始大球（size/speed/weight/动量 = weight×speed）、战场长宽比与 `stage.logical` 出片比是否一致 |
| AW-06 | 底部动作条：`应用（即时项）` / `应用并重置战场` / `恢复出厂默认` / `存为剧本` / `读取剧本`；`恢复出厂默认` 与 `重置` 按危险样式 |
| AW-07 | 「对战台」面板加一个按钮「对战区设置」→ `win.show name=arenaset`（面板 JSON 追加，随启动重写） |
| AW-08 | 规模档预设下拉（`小 720×675 / 中 960×900（出厂）/ 大 1440×1350`，等比缩放实现），选中即填控件、不自动应用 |

### 2.3 命令（AK）

| 编号 | 需求 |
|---|---|
| AK-01 | 新增 §4 命令；沿用现有 `arena.*` 域与"查询即回显、带参即设置"惯例，全部 `RequiresUiThread=true`，写入后 `config.Save()` |
| AK-02 | 参数越界由命令 clamp 到合法区间并在回显里说明（与 `arena.gravity` 现行语义一致） |
| AK-03 | 需重置才生效的命令统一**不**自动重置（除 `arena.scale` 与 `arena.default` 显式带 `reset=true` 时），由用户/窗口决定何时 `battle.reset`，避免误清战局 |
| AK-04 | `arena.config` 一条命令打印全量配置 + §2.2 AW-05 全部派生值（无窗口也能自查） |

---

## 3. 参数总表（新增字段 / 默认值 / 区间）

命名沿用 camelCase JSON。**默认值列即 v3.0 现行硬编码值**，改动它就是改动行为。

### 3.1 规模（右侧对战区规模）

| JSON 字段 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|
| `width` / `height` | 战场逻辑尺寸 | 960 / 900 | 200~4000 | 需重置 |
| `turretRadius` | 炮塔半径（同时定出膛距离、触杀判定、仪表环尺寸） | 26 | 6~200 | 需重置 |
| `turretMarginXRatio` | 炮塔离左右边距 ÷ 宽 | 0.12 | 0.02~0.45 | 需重置 |
| `turretMarginYRatio` | 炮塔离上下边距 ÷ 高 | 0.14 | 0.02~0.45 | 需重置 |
| `shieldRingScale` | 护罩环半径 ÷ 炮塔半径（判定与渲染同源） | 1.55 | 1.0~4.0 | 即时 |

### 3.2 网格与领地

| JSON 字段 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|
| `cellSize` | 领地格边长（**决定格数 ⇒ 决定领地模式初始血量**） | 10 | 5~100 | 需重置 |
| `mode` | `territory` / `direct` | territory | — | 需重置 |
| `suddenDeathAtSeconds` | 决胜时刻（此后护盾只降不升） | 240 | 0~3600 | 即时 |

### 3.3 护盾与血量

| 字段 | 位置 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|---|
| `initialShield` | turrets[] | 初始护盾 | 500000 | 0~`maxShield` | 需重置 |
| `maxShield` | turrets[] | 护盾上限 | 5000000 | 0~1e12 | 需重置 |
| `maxHp` | turrets[] | 生命上限（**仅 direct 模式生效**） | 20000000 | 1~1e12 | 需重置 |
| `shieldCostPerValue` | arena | 护盾计价：一发小球磨掉多少护盾（也是自家小球回充量） | 50000 | 1~1e9 | 即时 |

窗口以「统一设置四座 / 逐座展开」两种方式呈现（对应命令 `turret.setall` 与既有 `turret.set`）。

### 3.4 大球动量（领地模式弹体映射）

现行公式（`FireShell`，`value` = 结算数值 = 可占格预算）：

```
size  = clamp(cell * sizeCellFactor * value^sizeValueExponent, cell*sizeMinCells, cell*sizeMaxCells)
jitter= 1 + (rng-0.5) * 2 * speedJitter                 # 默认 0.75~1.25
speed = clamp(clamp(weapon.Speed,80,1200) * jitter / value^speedValueExponent, speedMin, speedMax)
weight= max(1, value * weightScale)                      # 动量 = weight × speed
```

| JSON 字段 | 含义 | 默认 | 区间 |
|---|---|---|---|
| `shellSizeCellFactor` | 尺寸基数（× 格边长） | 0.5 | 0.1~5 |
| `shellSizeValueExponent` | 尺寸随数值增长指数 | 0.25 | 0~1 |
| `shellSizeMinCells` / `shellSizeMaxCells` | 尺寸下/上限（格） | 0.5 / 5 | 0.1~20 |
| `shellSpeedJitter` | 出膛速度随机幅度（±比例） | 0.25 | 0~0.9 |
| `shellSpeedValueExponent` | 重弹减速指数 | 0.12 | 0~1 |
| `shellSpeedMin` / `shellSpeedMax` | 速度夹值（px/s） | 60 / 700 | 10~3000 |
| `shellWeightScale` | 质量系数（× 数值） | 1.0 | 0.1~100 |
| `initialShellCount` | **开局预载大球发数** | 12 | 0~512 |
| `initialShellValue` | 开局预载每发数值 | 1 | 1~100000 |
| `initialShellWeapon` | 开局预载武器名（决定基速） | 直射 | 武器库内任一 |

> **权威分工**：每种武器的**基础速度**仍归武器库（`weapon.set 大球 speed=...`），本组只管"数值 → 弹体尺寸/速度/质量"的映射与夹值。窗口在预览区注明当前 `initialShellWeapon` 的基速来源，避免两处对打。
> 出厂基线（cell=10、直射基速 360、value=1）：`size=5px`、`speed=270~450`、`weight=1`、**动量 270~450**。

### 3.5 小球

| JSON 字段 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|
| `smallBallSpeed` | 小球出膛速度 | 380 | 20~3000 | 即时 |
| `smallBallSizeCellFactor` | 小球半径 ÷ 格边长 | 0.5 | 0.1~3 | 即时 |

### 3.6 全局（已有字段，补命令 + 进窗口）

`gravityG`(0) · `ballCollision`(true) · `targeting`(spin) · `maxProjectiles`(2000) · `projectileLifetimeSec`(12，仅 direct)。

### 3.7 明确不在本期范围

小球射速曲线（`6 + ammo*0.15`，夹 90/150）、大球出膛间隔提速（`interval/(1+ammo*0.25)`）、光晕研磨速率、余烬爆发参数、触杀判定 —— 这些属于**平衡链路**而非"对战区规模/初始量"，改它们等于改玩法。若要开放，另起 v3.2「战斗平衡自定义」。

---

## 4. 命令清单

| 命令 | 例 | 说明 |
|---|---|---|
| `arena.cell` | `arena.cell size=8` | 格边长；回显新格数与每方初始格数（血量）变化预估 |
| `arena.turret` | `arena.turret radius=26 mx=0.12 my=0.14 ring=1.55` | 炮塔半径/落位/护罩环 |
| `arena.scale` | `arena.scale k=1.5 reset=true` | 等比缩放宽高/半径/格边长/速度夹值；格数与血量不变 |
| `arena.shell` | `arena.shell speedJitter=0.3 speedMin=80 weightScale=1` | §3.4 尺寸/动量映射，支持任意子集 |
| `arena.preload` | `arena.preload count=12 value=1 weapon=直射` | 开局预载大球队列 |
| `arena.small` | `arena.small speed=380 size=0.5` | 小球速度/尺寸 |
| `arena.shield` | `arena.shield cost=50000` | 护盾计价 |
| `arena.suddendeath` | `arena.suddendeath at=240` | 决胜时刻 |
| `arena.limit` | `arena.limit max=2000 life=12` | 最大弹数 / 弹丸寿命（direct） |
| `arena.collision` | `arena.collision on=true` | 弹-弹碰撞 |
| `turret.setall` | `turret.setall hp=2e7 shield=5e6 initshield=500000 rpm=6` | 批量刷全部炮台（参数同 `turret.set`，缺省项不动） |
| `arena.config` | `arena.config` | 全量配置 + 派生值一屏打印 |
| `arena.default` | `arena.default reset=true` | 恢复出厂默认（危险，须显式带 `reset` 才重置战场） |
| `win.show name=arenaset` | — | 唤出设置窗（框架既有命令，无需新增） |

---

## 5. 窗口布局（草图）

```
┌ 对战区 ─────────────────────────────┐
│ 预设 [中 960×900 ▾]   等比缩放 k [1.00]───□ │
│ ── ① 规模 ─────────────────── 需重置 │
│ 宽[960] 高[900]  炮塔半径[26]              │
│ 离角 X[0.12] Y[0.14]  护罩环[1.55] 即时    │
│ ── ② 网格与领地 ──────────────────── │
│ 格边长[10] 需重置   模式[territory ▾]      │
│ 决胜时刻[240]s 即时                        │
│ ── ③ 护盾与血量 ──────────────────── │
│ ○统一四座 ○逐座   初始护盾[500000]        │
│ 护盾上限[5000000] 生命上限[2e7](direct)    │
│ 护盾计价[50000]/发 即时                    │
│ ── ④ 大球动量 ────────────────────── │
│ 预载 发数[12] 数值[1] 武器[直射 ▾]         │
│ 尺寸 基数[0.5] 指数[0.25] 夹[0.5~5]格      │
│ 速度 抖动[±25%] 减速指数[0.12] 夹[60~700]  │
│ 质量系数[1.0]                              │
│ ── ⑤ 小球 ───────────── 速度[380] 尺寸[0.5]│
│ ── ⑥ 全局 ── 重力[0]g 弹弹碰撞[✓]          │
│ 最大弹数[2000] 瞄准[spin ▾]                │
│ ── 预览（只读）────────────────────── │
│ 网格 96×90 = 8640 格 → 每方 2160（=血量）  │
│ 初始护盾 500000 = 可挡 10 发小球           │
│ 初始大球 size 5px  speed 270~450  w 1      │
│          动量 270~450（直射基速 360）      │
│ 战场 1.067:1 ｜ 出片 1280×720 = 1.778:1    │
├──────────────────────────────────────┤
│ [应用] [应用并重置战场] [恢复默认] [存/读剧本] │
└──────────────────────────────────────┘
```

实现取向：仿 `Game/RefereeView.cs` 的代码构 WPF 工具窗（`UserControl` + `ICommandBusAware`），注册进 `WBallWorkspaceViews.CreateToolWindows()`。

> 备选方案（若要"零 WPF 代码"）：用 AppShell 面板 JSON（`panels/arenaset.json`，控件类型已支持 number/slider/combo/check/button）。代价：面板默认值是**静态文本**，做不到 AW-03 回读与 AW-05 实时预览。**建议按主方案做自绘窗**，见 §9 Q1。

---

## 6. 兼容与确定性纪律

1. **零漂移**：所有新字段默认值 = 现行常量。不改任何设置时，`WBallVerify` 同种子确定性哈希必须与 v3.0 **完全一致**（回归红线，先跑基线哈希留档再动手）。
2. **老数据即插即用**：`%AppData%\WBall\arena_layout.json`、`turrets.json`、`weapons.json`、面板/布局/场景/剧本一律不需迁移；缺字段取默认。
3. **剧本闭环**：`scenario.save` → 改配置 → `scenario.load` 必须逐字段还原（含 AC-04 修的 `suddenDeathAtSeconds`）。
4. **护罩单一真相**：`shieldRingScale` 供判定与渲染共读，禁止任一处保留字面 1.55。
5. **冻结区**：不改指令语法、不改 Core 公共契约、不碰 DockingHost 三大机制；`AppShell` 仍是 0.5.0 包引用（本期不升包）。
6. 构建沿用 `DOTNET_EnableWriteXorExecute=0`，零警告零错误；`AppVersion` 升 **3.1.0**。

---

## 7. 验收

1. `arena.config` 一屏打印全部字段与派生值；每条新命令都能查（无参）与设（带参），越界 clamp 并回显。
2. 「对战区」窗：`win.show name=arenaset` 唤出，回读当前值；改格边长/宽高时预览的格数、每方血量、长宽比即时随动；`应用并重置战场` 后战局按新参数开跑。
3. `arena.scale k=1.5 reset=true` 后：观感整体放大 1.5 倍，**格数与每方初始血量不变**，整局收敛时长与 k=1 同量级。
4. `arena.cell size=20` 后每方初始血量约降至 1/4，`turret.list` 与 HUD 同步；`arena.cell size=5` 反向成立。
5. `turret.setall initshield=0` 后四座开局无盾，首发小球即刻见效（护罩不再吞弹）。
6. `arena.preload count=0` 后开局无大球预载；`arena.shell speedMin=300 speedMax=300` 后大球速度恒定，抖动=0 时逐帧可复现。
7. `scenario.save` / `scenario.load` 往返后 `arena.config` 输出逐字段一致。
8. **未改任何设置时**：无头验证全绿且同种子哈希与 v3.0 留档值一致；改设置后哈希变化但同种子内部仍自洽（A/B 两跑相等）。
9. 实机 `WBall.exe --exec "demo.play seed=42"` 冷启动行为与 v2.12.5/v3.0 一致（默认值路径）。
10. 构建零警告；版本号 3.1.0；本文档随代码提交入 `b-Office`。

---

## 8. 里程碑

```
M0 本文档评审拍板（含 §9 问题）
 → M1 留档 v3.0 基线哈希（WBallVerify 跑一遍存输出）
 → M2 AC-01~05:配置模型扩展 + 硬编码点改读配置 + CloneArena 补齐;跑哈希对齐(必须与 M1 相同)
 → M3 AK:命令层落地 + arena.config 派生值计算(与窗口共用同一计算函数)
 → M4 AW:「对战区」窗 + 对战台按钮
 → M5 §7 验收全绿 + 提交(配置层/命令层/窗口层三段提交,不混合)
```

---

## 9. 待拍板问题

| # | 问题 | 建议默认 |
|---|---|---|
| Q1 | 设置窗做**自绘 WPF 窗**还是**面板 JSON**？ | ✅ 自绘窗（面板做不到回读与实时预览；仿 RefereeView，成本可控） |答复:窗体
| Q2 | 「初始护盾血量」的显示口径 | ✅ 绝对值为主，括号附"可挡 N 发小球"；`shieldCostPerValue` 一并开放 |对（注意大球是积分相消）
| Q3 | 护盾/血量默认按"统一四座"还是"逐座" | ✅ 默认统一（`turret.setall`），需要差异化时切逐座 |统一（先公平设置/不公平版本待更新）
| Q4 | 改"炮塔大小"时是否连带缩放战场（用户原话"其实是右侧对战区规模"） | ✅ 拆成两件：`arena.turret radius=` 只改炮塔；`arena.scale k=` 才整体缩放（含格边长，保血量不变） |拆2件（不过战场缩小炮塔应该同步缩小，这里可以改炮塔主要是怕规模扩大以后，炮塔太小）战场缩小，数字和小球一定同步缩小但是数字有最小值防止无限小看不到小球没关系，数字超出小球外以后超出部分做暗淡防止变成数字代替小球的进行对战
| Q5 | 大球动量是否也开放"每武器基速" | ✅ 不重复开放，窗口内只显示来源并给一句提示，改基速仍走 `weapon.set` |不重复开放（改基速，也请加入这次的控制面板中）
| Q6 | 是否需要"当前设置存为规模预设"（用户自定义档） | ⭕ 本期不做，用剧本（`scenario.save`）承担，v3.2 再议 |走V3.2
| Q7 | §3.7 平衡参数是否要提前塞进本期 | ⭕ 不塞，另起 v3.2「战斗平衡自定义」 |走V3.2

---

---

## 10. 附录：交付记录（2026-07-27）

### 10.1 哈希基线（零漂移红线）

| 场景 | seed=42 @60s 哈希 | 结论 |
|---|---|---|
| M1 动工前（v3.0 代码） | `6381A3898C0FAD65B57D43C140917A010713AA3015F601BACE14C7E5B88333F3` | 留档基线 |
| M2 配置层落地后 | 同上 | ✅ 逐字一致 |
| M5 全部代码交付后 | 同上 | ✅ 逐字一致 |
| 改设置后（initshield=0 / preload=0 / shell 定速 / small 500） | `0A36EF12D0784B45BA63FC7BAB362BBA439704F756EB0AB80463CBAEC02B8692` | 哈希变化，同种子 A/B 仍相等 ✅ |
| 恢复默认设置后 | 回到 `6381A389…` | ✅ 可逆 |

`WBallVerify` 新增两行哈希打印（`hash seed=42/43 @60s`）作为长期回归锚点；seed=43 基线
`4A8C80247437C3470C5B6105667CC24ACA74100437CFDFF82F38BD0E9F3EF931`。
全局无头验证 `VERIFY PASS`（整局 seed=7 于 310.1s 收敛出 orange；巨球斩满盾定向测试通过）。

### 10.2 §7 验收结果

| # | 项 | 结果 |
|---|---|---|
| 1 | `arena.config` 全量+派生值、各命令可查可设、越界 clamp | ✅ |
| 2 | 设置窗唤出并回读当前值、预览随控件重算 | ✅（窗体渲染实测见 §10.4） |
| 3 | `arena.scale k=1.5 reset=true` 格数不变 | ✅ 2160 → 2160，宽高/炮塔/格边长/弹速同乘 |
| 4 | `arena.cell size=20` 血量约降至 1/4 | ✅ 2160 → 528/552（象限取整差） |
| 5 | `turret.setall initshield=0` 四座开局无盾 | ✅ 写入 turrets.json 并即时刷运行时 |
| 6 | `arena.preload count=0` / `arena.shell speedMin=speedMax` | ✅ |
| 7 | 剧本往返逐字段还原 | ✅ 含此前遗漏的 `suddenDeathAtSeconds`（AC-04 修复） |
| 8 | 零漂移 + 改设置后自洽 | ✅ 见 §10.1 |
| 9 | 实机 `demo.play seed=42` | ✅ 正常开局，无异常日志 |
| 10 | 构建零警告、版本 3.1.0、文档入 b-Office | ✅ |

### 10.3 §9 答复的落地方式

| 问题 | 答复 | 落地 |
|---|---|---|
| Q1 窗体 | 自绘 WPF 窗 | `Presentation/ArenaSettingsView.cs`（`UserControl` + `ICommandBusAware`），一切写入经 CommandBus |
| Q2 护盾口径（注意大球是积分相消） | 绝对值 +「可挡 N 发小球」 | 预览同时给出"可挡 N 发小球 / 等量抵消 N 点大球积分、计价 X/点"，`shieldCostPerValue` 开放 |
| Q3 统一（先公平局） | 统一四座 | `turret.setall` 为窗口主路径；差异化仍走 `turret.set`，窗内明确提示 |
| Q4 拆两件 + 战场缩小时数字/小球同步缩小、数字有下限、超出部分暗淡 | 全部落地 | `arena.turret radius=` 只改炮塔；`arena.scale k=` 整体缩放（含格边长与新增 `projectileSpeedScale`，保证放大后弹速观感一致）；弹体数字字号 = `半径×factor` 夹于 `[min,max]`，球内实色、超出球体部分按 `shellLabelOutsideOpacity` 暗淡（原 `radius>=8` 显示门槛取消，小弹也标数字） |
| Q5 基速不重复开放但要能在面板里改 | 落地 | 窗内「武器基速」行：选武器 → 填速度 → 下发 `weapon.set name=… key=speed`，权威仍是武器库 |
| Q6/Q7 | 走 v3.2 | 预设档与平衡参数不在本期 |

### 10.4 实现偏差（与正文的三处不同，均为踩坑后的选择）

1. **设置窗停靠位置**：正文 AW-01 写"与「对战台」同组标签"，实际改为**与调试三窗（`objdebug`）同组**
   —— 面板 id（`battle`/`editor`）由面板系统后置创建，工具窗注册期不能作为 tab 目标；且新开右侧窗格在
   "恢复旧布局"路径下拿不到稳定比例会塌缩（《二次开发演进手册》§3.3 已警示）。入口不变：
   「对战台 → 对战区设置」按钮或 `win.show name=arenaset`，也可 `win.float name=arenaset` 浮成独立窗。
2. **新增字段 `projectileSpeedScale`**（默认 1）：正文 AC-05 原写"等比缩放同乘速度夹值上下限"，但夹值不改基速
   等于放大后弹速相对变慢；改为引入弹体速度总缩放，`arena.scale` 同乘它 —— 武器库基速零改动，缩放才真正等比。
3. **新增命令 `arena.label`**：正文命令表未列，用于 Q4 的数字字号/暗淡（`factor/min/max/outside`）。

### 10.5 代码落点

| 层 | 文件 |
|---|---|
| 配置 | `Battle/BattleConfigStore.cs`（26 个新字段 + `Ranges` 区间表 + 校验 + 恢复默认）、`Battle/ArenaMetrics.cs`（`ArenaFormulas` 公式单一真相 + `ArenaMetrics` 派生值） |
| 运行时 | `Battle/BattleRuntime.cs`（预载/落位/弹体尺寸速度质量/小球/护罩计价改读配置 + `SyncTurretNumbersFromConfig`）、`Battle/ScenarioStore.cs`（CloneArena 补齐） |
| 命令 | `Commands/ArenaConfigCommands.cs`（13 条 `arena.*` + `turret.setall`）、`App.xaml.cs`（注册/版本 3.1.0/对战台加两个按钮） |
| 界面 | `Presentation/ArenaSettingsView.cs`、`Presentation/WBallWorkspaceViews.cs`（注册 `arenaset`）、`Stage/ArenaView.cs`（护罩环共读字段 + 弹体数字缩放与暗淡） |
| 验证 | `b-Code-Verify/WBallVerify/Program.cs`（哈希打印留档） |

—— 文档结束 ——
