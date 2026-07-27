# WBall · v3.2 需求 —— 战斗平衡自定义、无头试跑与预设档

> 文档性质：功能需求（**前置：v3.1 已交付**，即对战区配置层/命令层/设置窗已在位）。日期：2026-07-27 ｜ 目标版本：**3.2.0**
> 状态：**待评审**（本文档批准前不动代码）

---

## 0. 一句话定义

**v3.2 = 把"玩法数值"从代码里放出来，并给它配一台秤**：v3.1 开放的是对战区的**规模与初始量**，本期开放**战斗平衡链路**（射速节奏、光晕对消、护罩与触杀、余烬爆发、经济→火力映射、战场物理弹性、收敛与胜负），落一个「战斗平衡」窗；同时新增 **`balance.sim` 无头试跑**——在不影响当前战局的独立实例里按多个种子快跑，直接吐出"平均收敛时长 / 胜者分布 / 平局率"，让调参有据可依；再补上 v3.1 Q6 挂账的**用户预设档**。

**不做**：不改指令语法、不改 Core 契约、不改左侧落球区玩法（倍率公式/钉阵/槽位语义）、不改武器库结构、不升 AppShell 包。**所有新字段默认值 = 现行硬编码值**，一个字段不动时行为与 v3.1 逐帧一致（§7 红线）。

---

## 1. 现状盘点：平衡参数全在代码里

v3.1 之后仍未开放的、**直接决定对局观感与时长**的常量：

| 关注点 | 现行值 | 位置 |
|---|---|---|
| 大球出膛间隔提速 | `interval/(1+ammo*0.25)`，下限 0.08s（direct 0.05s） | `Battle/BattleRuntime.cs:223-225` |
| 小球连射速率 | `min(90, 6+smallAmmo*0.15)`；定格时 `min(150, rate*2)` | `BattleRuntime.cs:397-399` |
| 小球散布 | 常态 8°、定格 1.5° | `BattleRuntime.cs:404` |
| 齐射环射发数 / 待发上限 | 24 发 / 8 次 | `BattleRuntime.cs:416`、`EconomyBridge.cs:82` |
| 直射定格时长 | `+clamp(value/16, 1, 12)`，上限 12s | `EconomyBridge.cs:77-79` |
| 弹药队列上限 | 512 发（满则丢弃） | `EconomyBridge.cs:87` |
| 光晕半径系数 | `1.6 ×(r1+r2)` | `BattleRuntime.cs:828` |
| 研磨速率 | `2.0 × 较大球数值 × dt`（1:1 守恒） | `BattleRuntime.cs:859-861` |
| 同色小球融入大球 | 恒开 | `BattleRuntime.cs:839-855` |
| 余烬爆发速度 | 150~400 随机；吸走左侧同色经济球 | `BattleRuntime.cs:542`、`529-537` |
| 经济→火力映射系数 | Size `8+scaled`、Burst `damage+scaled*0.02`、Pierce `*0.08`、Gravity `*0.15/*0.05`、Score `*0.01`，`scaled=EconomyScale*√total` | `EconomyBridge.cs:96-159` |
| 战场墙面弹性 / 弹-弹弹性 | 0.55 / 0.85（**左右世界共用一套常量**） | `Sim/PhysicsEngine.cs:116`、`369` |
| 结算展示时长 / 倒计时 | 2s / 1s | `Battle/BattleDirector.cs:159`、`69` |
| 护盾自然再生 | `ShieldGain=1` ⇒ +1/s | `Game/FactionBoard.cs:55`、`BattleRuntime.cs:1035` |

### 1.1 盘点中发现的疑似失衡点（须用户拍板，见 §9 Q3）

**护盾槽结算与护罩计价不在同一量级。** 护罩磨损/回充按 `shieldCostPerValue = 50000` 计（一发小球 = 5 万），
而左侧「护盾」槽结算只加 `value × EconomyScale × ShieldGain` = **+value（约 1~数百）**（`EconomyBridge.cs:106-110`）；
护盾自然再生也只有 +1/s。结论：**护盾槽目前近乎无效**，实战里护盾几乎只靠"自家小球飞回回充"（+5 万/发）在撑。

这不是本期要顺手改掉的东西——改默认值就是改行为、要重跑哈希基线。本期做法：
把它升为字段 `shieldSlotGainPerValue`（默认 **1 = 保持现状**），并在窗口里对该字段标注"建议档 50000（与护罩计价对齐）"，
让用户用 `balance.sim` 对拍两档后自己拍板是否改默认。

---

## 2. 需求清单

### 2.1 平衡配置层（BP）

| 编号 | 需求 |
|---|---|
| BP-01 | 新增 `battle_balance.json` + `BalanceConfigStore`（与 `arena_layout.json` **分文件**：规模一套、玩法数值一套，便于"同规模换不同平衡档"）；结构、校验、缺字段取默认、加载失败回退内置模板并 Error 日志，一律比照 `BattleConfigStore` 现行语义 |
| BP-02 | §4 全部字段落地，**默认值 = §1 现行值**；`BattleRuntime` / `EconomyBridge` / `BattleDirector` / `PhysicsEngine` 相应硬编码点改读配置 |
| BP-03 | 光晕分桶步长（现 `const step = 160`，`BattleRuntime.cs:791`）**不做字段**，改为由 `haloReachFactor × 2 × 弹体最大半径` 推导，避免用户把光晕调大后漏检（现注释已写明这层耦合） |
| BP-04 | 物理弹性下沉到 `SceneWorld`（`WallRestitution` / `BallRestitution`，默认 0.55 / 0.85）：战场世界读平衡配置，**左侧经济世界恒取默认**，杜绝调平衡波及落球盘 |
| BP-05 | `ScenarioSnapshot` 增 `Balance` 段并进 `ScenarioStore.Apply/Capture/Clone`；老剧本无该段 = 取默认（老剧本行为不变） |
| BP-06 | 开关型字段（同色融入、余烬吸经济球、破盾直入、触杀、决胜期护盾封锁）默认全开 = 现行语义；关掉即回退对应补丁前的旧行为，用于对照实验 |

### 2.2 「战斗平衡」窗（BW）

| 编号 | 需求 |
|---|---|
| BW-01 | 新增工具窗 `id=balance`，标题「战斗平衡」，与「对战区」`arenaset` 同组标签、默认隐藏，`win.show name=balance` 唤出；关闭=隐藏 |
| BW-02 | 与 v3.1 设置窗同一纪律：**只经 `CommandBus` 下发 §5 命令**，不直写 store；控制台可完成同样的事 |
| BW-03 | 打开/应用后回读当前值填充控件；分组：① 火力节奏 ② 对消与融合 ③ 护罩与触杀 ④ 余烬爆发 ⑤ 经济→火力 ⑥ 战场物理 ⑦ 收敛与胜负 |
| BW-04 | 每字段标注**生效时机**（即时 / 需重置）与**作用模式**（`territory` / `direct` / 两者）——领地模式下经济→火力映射的多数系数只影响仪表与 direct 模式，不标清楚必然调错（§4.5 注） |
| BW-05 | 内嵌**试跑面板**：种子列表、时长、`开始试跑`（异步，不阻塞 UI）、结果表（每种子：收敛秒数 / 胜者 / 剩余格数分布）+ 汇总（平均/中位收敛时长、胜者分布、平局率、超时率）；进度可中断 |
| BW-06 | 一键对拍：`基线 vs 当前` 两组试跑并排显示（基线 = 出厂默认或用户选定预设档），差异列高亮 |
| BW-07 | 底部动作条：`应用` / `应用并重置战场` / `恢复出厂默认` / `存为预设` / `读取预设`；危险项按 danger 样式 |
| BW-08 | 「对战台」面板追加按钮「战斗平衡」→ `win.show name=balance` |

### 2.3 无头试跑（BS）

| 编号 | 需求 |
|---|---|
| BS-01 | `balance.sim` 命令：在**独立实例**（新的 `SceneWorld`×2 + `BattleRuntime` + `BattleDirector` + 配置内存副本）里跑，**不触碰当前战局、不写 `%AppData%` 配置** |
| BS-02 | 参数：`seeds=42,43,44`（或 `seeds=42..49`）、`seconds=180`（每局上限）、`config=current|default|<预设名>`；默认 `seeds=42,43,44 seconds=180 config=current` |
| BS-03 | 输出：逐种子（收敛秒数 / 胜者 / 是否超时 / 各方剩余格数）+ 汇总（平均、中位、最短、最长、胜者分布、平局率、超时率）；`format=table|csv`，csv 便于贴表 |
| BS-04 | 复用现有确定性时钟（`AdvanceSteps` + `FixedStepSeconds`），**不引入新随机源**；同 `seeds+config` 两次试跑结果必须逐字相同 |
| BS-05 | 性能：单局 180s 逻辑（10800 帧）应显著快于实时；超过 `timeoutMs=60000` 或用户中断即停并报告已完成部分 |
| BS-06 | 试跑期间可见进度（命令回显分段 / 窗口进度条）；试跑不改变 `stage.mode`、不影响录制 |

### 2.4 预设档（PS，v3.1 Q6 挂账项）

| 编号 | 需求 |
|---|---|
| PS-01 | 新增 `presets/*.json`（数据根下），一档 = **arena 规模段 + balance 平衡段**（轻量档：不含场景、炮台名色、武器库——那些归剧本） |
| PS-02 | 命令 `preset.list` / `preset.save name=` / `preset.load name= [reset=true]` / `preset.delete name=`（delete 须 `confirm=true`） |
| PS-03 | 内置三档随首启种入、可被覆盖：`standard`（出厂默认）、`rush`（快节奏：射速↑、研磨↑、决胜时刻 120s）、`marathon`（慢热：初始护盾↑、射速↓、决胜时刻 480s）。**内置档的具体数值以 `balance.sim` 试跑结果标定后写入文档附录**，不凭感觉拍 |
| PS-04 | 与剧本的分工在窗口与文档里写明：**预设=数值档（可跨场景复用）**，**剧本=整局快照（含场景与炮台）**；`preset.load` 只覆盖 arena/balance 两段 |

---

## 3. 术语与作用域

| 概念 | 文件 | 覆盖内容 | 命令域 |
|---|---|---|---|
| 规模（v3.1） | `arena_layout.json` | 宽高、炮塔、格边长、弹体尺寸/动量映射、预载 | `arena.*` |
| 平衡（本期） | `battle_balance.json` | 射速节奏、对消、护罩触杀、余烬、经济映射、物理、收敛 | `balance.*` |
| 预设档（本期） | `presets/<name>.json` | 上面两段的组合 | `preset.*` |
| 剧本（既有） | `workspace/scenarios/*.json` | 炮台+arena+武器库+经济场景 | `scenario.*` |

---

## 4. 参数总表（新增字段 / 默认值 / 区间）

JSON camelCase，均置于 `battle_balance.json`。**默认列即现行硬编码值。**

### 4.1 火力节奏（territory 为主）

| 字段 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|
| `shellIntervalAmmoFactor` | 大球间隔提速系数：`interval/(1+ammo×f)` | 0.25 | 0~5 | 即时 |
| `shellIntervalFloorSec` | 大球间隔下限 | 0.08 | 0.02~5 | 即时 |
| `smallRateBase` | 小球基础射速（发/秒） | 6 | 0~200 | 即时 |
| `smallRatePerAmmo` | 每点小球弹药加成射速 | 0.15 | 0~5 | 即时 |
| `smallRateMax` | 小球射速上限 | 90 | 1~600 | 即时 |###上限要更高，这是由于我发现弹药库里面小球很难发射完，如果实在发射不完我们这样，当弹药库数量达到一定值时设置梯度，小球转为小型大球，比如弹药有40k了改为发射积分为2的大球
| `smallRateFrozenFactor` | 定格（直射）射速倍数 | 2.0 | 1~10 | 即时 |
| `smallRateFrozenMax` | 定格射速上限 | 150 | 1~900 | 即时 |
| `smallSpreadDeg` | 小球常态散布 | 8 | 0~90 | 即时 |
| `smallSpreadFrozenDeg` | 定格散布 | 1.5 | 0~90 | 即时 |
| `volleyRingCount` | 齐射环射发数 | 24 | 4~120 | 即时 |
| `volleyPendingMax` | 齐射待发上限 | 8 | 1~64 | 即时 |
| `freezeSecondsPerValue` | 直射定格：每点数值秒数（现 1/16） | 0.0625 | 0~2 | 即时 |
| `freezeMaxSeconds` | 定格时长上限 | 12 | 0~60 | 即时 |
| `ammoQueueLimit` | 大球弹药队列上限（满则丢弃） | 512 | 8~4096 | 即时 |###别设置队列上限

### 4.2 对消与融合

| 字段 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|
| `haloReachFactor` | 光晕接触判定 = 系数 ×(r1+r2) | 1.6 | 1.0~4.0 | 即时 |
| `grindRatePerSecond` | 研磨速率（× 较大球数值 × dt，1:1 守恒不变） | 2.0 | 0.1~50 | 即时 |
| `mergeSameOwnerSmall` | 同色小球融入自家大球 | true | — | 即时 |

### 4.3 护罩与触杀

| 字段 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|
| `shieldBreakthrough` | 破盾直入（v2.12.5 BS-01）；关 = 回退"未破必反弹" | true | — | 即时 |
| `contactKillEnabled` | 破罩后触碰本体即摧毁（v2.12.4 TK-01） | true | — | 即时 |
| `selfShieldRefundEnabled` | 自家小球飞回回充护盾（单位 = v3.1 `shieldCostPerValue`） | true | — | 即时 |
| `suddenDeathShieldBlock` | 决胜期护盾槽转喂小球弹药 | true | — | 即时 |
| `shieldSlotGainPerValue` | 「护盾」槽每点数值加多少护盾（**见 §1.1**；建议档 50000） | 1 | 0~1e6 | 即时 |
| `shieldRegenPerSecond` | 护盾自然再生（现 `ShieldGain=1`） | 1 | 0~1e6 | 即时 |###护盾不允许自然再生因为我们护盾无论大小都能反弹大球，如果自然再生大球非常难杀护盾

### 4.4 余烬爆发

| 字段 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|
| `emberSpeedMin` / `emberSpeedMax` | 余烬弹速区间 | 150 / 400 | 10~3000 | 即时 |
| `emberFromAmmo` | 弹药库（大球队列+小球池）转余烬 | true | — | 即时 |
| `emberDrainEconomy` | 吸走左侧同色经济球转余烬 | true | — | 即时 |

### 4.5 经济→火力映射（**direct 模式为主**）

`scaled = EconomyScale × total^intensityExponent`（现 `√` ⇒ 0.5）。

| 字段 | 含义 | 默认 | 区间 |
|---|---|---|---|
| `intensityExponent` | 强度曲线指数 | 0.5 | 0.1~1 |
| `sizeGainBase` | Size 槽尺寸基数（`base+scaled`） | 8 | 2~60 |
| `burstDamageGain` / `burstSpreadGain` | Burst 伤害/散布增益 | 0.02 / 0.05 | 0~1 |
| `pierceDamageGain` | Pierce 伤害增益 | 0.08 | 0~1 |
| `gravitySizeGain` / `gravityDamageGain` | Gravity 尺寸/伤害增益 | 0.15 / 0.05 | 0~2 |
| `scoreDamageGain` | Score(RUN) 伤害增益 | 0.01 | 0~1 |

> **必须写进窗口的提醒**：领地模式（默认玩法）的实际火力只吃「大球队列数值 / 小球池 / 护盾」三条，
> 本组多数系数只影响 **direct 模式**与仪表显示。调它们时窗口应显示"当前模式：territory —— 本组仅部分生效"。

### 4.6 战场物理（仅右世界，BP-04）

| 字段 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|
| `wallRestitution` | 墙面反弹系数 | 0.55 | 0~1 | 即时 |
| `ballRestitution` | 弹-弹碰撞弹性 | 0.85 | 0~1 | 即时 |

### 4.7 收敛与胜负

| 字段 | 含义 | 默认 | 区间 | 生效 |
|---|---|---|---|---|
| `countdownSeconds` | 开局倒计时 | 1 | 0~30 | 下局 |
| `settleSeconds` | 分胜负后结算展示时长 | 2 | 0~30 | 即时 |
| `hardTimeLimitSeconds` | **新增**：硬性时限，到点按剩余格数判胜（0=关闭，沿用决胜时刻收敛） | 0 | 0~7200 | 即时 |

> `hardTimeLimitSeconds` 是本期唯一的**新玩法开关**（默认关，零漂移）。目的是给出片定长：录 3 分钟片子就设 180。见 §9 Q4。

---

## 5. 命令清单

| 命令 | 例 | 说明 |
|---|---|---|
| `balance.rate` | `balance.rate smallBase=6 smallPerAmmo=0.15 smallMax=90 shellFactor=0.25` | §4.1，支持任意子集 |
| `balance.duel` | `balance.duel halo=1.6 grind=2.0 merge=true` | §4.2 |
| `balance.shield` | `balance.shield breakthrough=true refund=true slotGain=1 regen=1` | §4.3 |
| `balance.ember` | `balance.ember speedMin=150 speedMax=400 economy=true` | §4.4 |
| `balance.economy` | `balance.economy exponent=0.5 sizeBase=8 pierce=0.08` | §4.5 |
| `balance.physics` | `balance.physics wall=0.55 ball=0.85` | §4.6（仅右世界） |
| `balance.round` | `balance.round countdown=1 settle=2 limit=0` | §4.7 |
| `balance.config` | `balance.config` | 全量打印 + 当前模式下"哪些字段实际生效"标注 |
| `balance.default` | `balance.default reset=true` | 恢复出厂（危险，显式 `reset` 才重置战场） |
| `balance.sim` | `balance.sim seeds=42..49 seconds=180 config=current format=table` | 无头试跑（§2.3） |
| `balance.diff` | `balance.diff a=default b=current` | 两档字段差异表（配 BW-06 对拍） |
| `preset.list / save / load / delete` | `preset.save name=rush` / `preset.load name=rush reset=true` | 预设档（§2.4） |
| `win.show name=balance` | — | 唤出平衡窗（框架既有命令） |

命令纪律沿用 v3.1：无参=查询、带参=设置、越界 clamp 并回显、写入即 `Save()`、除显式 `reset=true` 外不自动重置战场。

---

## 6. 窗口布局（草图）

```
┌ 战斗平衡 ───────────────────────────────┐
│ 预设 [standard ▾]  当前模式 territory ⓘ         │
│ ── ① 火力节奏 ───────────────── 即时 │
│ 小球 基础[6]/秒 每弹药[0.15] 上限[90]           │
│ 定格 倍数[2.0] 上限[150] 散布[1.5°] 常态[8°]    │
│ 大球 提速系数[0.25] 间隔下限[0.08]s             │
│ 齐射 环射[24] 待发[8] 定格 每值[0.0625]s 上限[12]│
│ 弹药队列上限[512]                               │
│ ── ② 对消与融合 ── 光晕[1.6×] 研磨[2.0]/s      │
│ 同色小球融入[✓]                                 │
│ ── ③ 护罩与触杀 ────────────────── │
│ 破盾直入[✓] 触杀[✓] 小球回充[✓] 决胜封锁[✓]     │
│ 护盾槽增益[1]/点 ⚠ 建议 50000（见文档 §1.1）    │
│ 护盾再生[1]/秒                                  │
│ ── ④ 余烬爆发 ── 速度[150~400] 吸经济球[✓]      │
│ ── ⑤ 经济→火力 ⓘ territory 下仅部分生效        │
│ 指数[0.5] 尺寸基数[8] Burst[0.02/0.05] …        │
│ ── ⑥ 战场物理 ── 墙[0.55] 弹弹[0.85]（仅右世界）│
│ ── ⑦ 收敛与胜负 ── 倒计时[1] 结算[2]            │
│ 硬性时限[0]s（0=关）                            │
│ ── 无头试跑 ────────────────────── │
│ 种子[42..49] 时长[180]s  档[当前 ▾] [开始][中断] │
│ ┌ 结果 ────────────────────────┐ │
│ │ 种子 收敛(s) 胜者   剩余格数              │ │
│ │ 42    96.4   blue   2140/0/0/612          │ │
│ │ 43   142.8   green  ...                   │ │
│ │ 平均 118.2 ｜ 中位 109.5 ｜ 平局 0% 超时 0%│ │
│ │ 胜者分布 blue3 red2 green2 yellow1        │ │
│ └──────────────────────────────┘ │
│ [与基线对拍] → 并排差异列                       │
├──────────────────────────────────────────┤
│ [应用] [应用并重置] [恢复默认] [存为预设] [读取预设]│
└──────────────────────────────────────────┘
```

实现取向同 v3.1：代码构 WPF（`UserControl` + `ICommandBusAware`），注册进 `WBallWorkspaceViews.CreateToolWindows()`；试跑走后台线程 + `Dispatcher` 回帖，禁止在 UI 线程跑 10800 帧。

---

## 7. 兼容与确定性纪律（红线）

1. **零漂移**：新字段默认值 = 现行常量。不改任何设置时 `WBallVerify` 同种子哈希必须与 **v3.1 留档值一致**（M1 先留档）。
2. **§1.1 的失衡点默认不修**（`shieldSlotGainPerValue=1` 保持现状）；要改默认必须先 `balance.sim` 对拍、用户拍板、并重跑哈希基线（§9 Q3）。
3. **试跑绝不污染现场**：独立实例 + 配置内存副本；不写配置文件、不动 `stage.mode`、不动录制、不复用全局随机源。
4. **左世界隔离**：物理弹性只对战场世界生效，落球盘恒取 0.55/0.85（BP-04）。
5. **分桶步长自适应**（BP-03）：`haloReachFactor` 调大后不得漏检对消——用一组"两球正好在新光晕边缘"的定向测试守住。
6. **老数据即插即用**：无 `battle_balance.json` 即取默认；老剧本无 `balance` 段取默认；`%AppData%` 其余数据零迁移。
7. **冻结区**：不改指令语法、Core 公共契约、DockingHost 三大机制；AppShell 仍 0.5.0 包引用。
8. 构建沿用 `DOTNET_EnableWriteXorExecute=0`，零警告零错误；`AppVersion` 升 **3.2.0**。

---

## 8. 验收

1. `balance.config` 一屏打印全部字段，并按当前模式标注"实际生效/仅 direct 生效"；每条 `balance.*` 命令可查可设、越界 clamp 并回显。
2. 「战斗平衡」窗 `win.show name=balance` 唤出、回读当前值；改字段→`应用`→`turret.list`/HUD/实机观感同步。
3. `balance.rate smallBase=30` 后小球明显变密；`balance.rate smallMax=6` 后压回稀疏；两者均在同种子下逐帧可复现。
4. `balance.duel halo=3.0` 后对消范围明显变大且**无漏检**（定向测试：两球置于 `3.0×(r1+r2)` 内侧 1px 必对消、外侧 1px 不对消）。
5. `balance.shield breakthrough=false` 后回退"护盾未破必反弹"旧行为；`contactKillEnabled=false` 后破罩也不再触杀（用于对照，不作为默认）。
6. `balance.shield slotGain=50000` 后护盾槽结算明显顶盾（对照 §1.1），`balance.sim` 显示平均收敛时长变化并可量化。
7. `balance.physics wall=0.1` 只影响右侧弹体反弹，**左侧落球盘轨迹逐帧不变**（同种子哈希的左世界段不变）。
8. `balance.round limit=180` 后每局必在 180s 内按剩余格数判胜；`limit=0` 时行为与 v3.1 一致。
9. `balance.sim seeds=42..49 seconds=180`：8 局跑完给出逐种子与汇总；同参两次结果逐字相同；跑完后当前战局状态与试跑前**完全一致**（`battle.status`/`arena.status`/`balance.config` 三者不变）。
10. `preset.save name=rush` → `balance.default` → `preset.load name=rush` 后 `balance.config` 与 `arena.config` 逐字段还原；内置三档可 `preset.list` 看到，`rush`/`marathon` 的数值有 §8.11 试跑数据支撑。
11. 附录记入三档预设的试跑标定数据（各 8 种子：平均/中位收敛、胜者分布、平局率）。
12. **未改任何设置时**：无头验证全绿且同种子哈希与 v3.1 留档一致；改设置后哈希变化但同种子 A/B 两跑相等。
13. 实机 `WBall.exe --exec "demo.play seed=42"` 默认值路径行为与 v3.1 一致；构建零警告；版本 3.2.0；本文档随代码入 `b-Office`。

---

## 9. 里程碑

```
M0 本文档评审拍板（含 §9 问题）
 → M1 留档 v3.1 基线哈希
 → M2 BP-01~06:balance 配置层 + 硬编码点改读配置 + 剧本携带;跑哈希对齐(必须与 M1 相同)
 → M3 BK:balance.* 命令层 + balance.config 生效性标注 + balance.diff
 → M4 BS:balance.sim 无头试跑(独立实例、确定性、可中断)
 → M5 PS:预设档命令 + 用 M4 试跑标定 rush/marathon 数值,写入附录
 → M6 BW:「战斗平衡」窗(含试跑面板与对拍) + 对战台按钮
 → M7 §8 验收全绿 + 分段提交(配置/命令/试跑/预设/窗口 五段,不混合)
```

---

## 10. 待拍板问题

| # | 问题 | 建议默认 |
|---|---|---|
| Q1 | 平衡参数放**新文件** `battle_balance.json` 还是塞进 `arena_layout.json`？ | ✅ 新文件（规模与玩法两条轴分开，才能"同规模换平衡档"） |
| Q2 | `balance.sim` 本期做还是推后？ | ✅ 本期做，且是本期价值核心——没有秤，§4 的 40 个旋钮等于让人蒙着调 |
| Q3 | §1.1 护盾槽失衡：默认值改不改？ | ⭕ 本期**不改默认**，只开放字段 + 窗口标注建议档；试跑对拍后由你拍板（改则单独一次提交 + 重跑哈希基线） |
| Q4 | 新增 `hardTimeLimitSeconds`（硬性时限判胜）是否要？ | ✅ 要，默认 0（关闭）；出片定长很需要，且零漂移 |
| Q5 | 战场物理弹性是否开放？ | ✅ 开放但**只对右世界**（BP-04）；左侧落球盘手感是已验收资产，不许被平衡调参波及 |
| Q6 | 经济→火力映射（§4.5）在 territory 默认玩法下作用有限，是否本期开放？ | ✅ 开放但窗口明确标注生效范围；direct 模式仍是对照实验的重要通道 |
| Q7 | 预设档内置几档、叫什么？ | ✅ 三档 `standard/rush/marathon`，数值由 M5 试跑标定后写入附录，不凭手感 |
| Q8 | 是否顺手开放小球射速上限之外的"弹药通胀"约束（如全局弹药衰减）？ | ⭕ 不做，属新玩法；若试跑显示后期弹药爆炸再单独立项 v3.3 |

---

**批准本文档后回复"开冲 v3.2"即执行 M1~M7；对任一项有异议请直接改本文件或口头拍板。**

—— 文档结束 ——
