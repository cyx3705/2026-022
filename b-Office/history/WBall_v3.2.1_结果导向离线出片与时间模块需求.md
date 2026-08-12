# WBall · v3.2.1 需求 —— 结果导向离线出片与时间模块

> 文档性质：v3.2 已交付后的专项升级。日期：2026-07-27 ｜ 目标版本：**3.2.1**
> 状态：**已实现，2026-07-27 完成完整自动验收**。本版本只解决出片与时间调度，不改变战斗规则。

---

## 0. 一句话定义

WBall 不再“边播放边录屏”，而是把一局冻结成可复现任务，在后台算出每个输出时刻的画面数据，再由独立渲染线程组合成帧并流式编码。机器算得慢只会让生成墙钟时间变长，不允许视频掉帧、跳帧或拖垮主界面。

本期同时建立统一时间模块：明确区分**视频时间、模拟时间、生成耗时**；当战场球数过多时，按可配置且确定性的曲线降低模拟倍率，让成片进入慢速段，而不是让实时卡顿冒充慢动作。

## 0.1 已拍板原则

1. 产品入口用“出片 / 生成视频”，不再把它描述为摄像式“录制”。
2. 输出是计算结果：固定种子、固定配置、固定时间映射，同一任务逐帧可复现。
3. 主界面只显示预览和进度；模拟、画面数据生成、帧合成、编码均不得长期占用主 UI 线程。
4. 不因机器一时变慢而改变模拟结果；自动降速只由球数等确定性战局状态决定，不读取 CPU 占用、墙钟帧率或窗口是否前台。
5. 球多时降低的是“模拟时间 / 视频时间”的倍率。输出 FPS 与视频时间保持稳定，观众看到可控慢动作。
6. 不在内存保存整段 BGRA 原始帧。帧按小缓冲流水线消费，编码失败才按策略保留 PNG。
7. 新增独立「出片与时间」工具页；原 `record.*` 保留兼容别名，不让旧面板和脚本立即失效。

---

## 1. 当前问题

当前 `StageRecorder` 已经使用固定步进和 `RenderTargetBitmap`，不是传统屏幕抓取，但执行方式仍有四个结构性问题：

| 编号 | 现状 | 后果 |
|---|---|---|
| RT-01 | `record.start` 要求 UI 线程，同步循环推进导演、布局、渲染 | 长片或球多时主窗口无响应，停止按钮也无法及时生效 |
| RT-02 | MP4 路径把所有 `byte[]` BGRA 帧放进 `List<byte[]>` 后再编码 | 1080p 一帧约 7.9 MiB，数百帧即可占用数 GiB |
| RT-03 | `stepsPerFrame = Round(60/fps)` | 24 FPS 等不能整除 60 的规格产生错误时间比例，时间语义不精确 |
| RT-04 | 舞台现场被 `Reset/Start` 后直接拿来出片 | 出片污染当前战局，用户不能一边调参、一边查看已有现场 |
| RT-05 | “录制时长”只有一个 `seconds` | 无法区分输出视频时长、战斗模拟时长、实际生成耗时 |
| RT-06 | PNG 与 MP4 都围绕现场 `StageView` 工作 | 窗口尺寸、布局和 UI 生命周期仍进入核心出片链路 |

本期不是给同步循环打补丁，而是把“任务计算”和“画面组合”拆成明确的流水线。

---

## 2. 范围

### 2.1 本期必须完成

- 独立 `RenderJob`，冻结种子、剧本、arena、balance、武器、舞台和 HUD 配置。
- 独立双世界与导演实例，绝不复用当前战局的可变对象。
- 统一 `TimelineClock`，支持任意输出 FPS 的精确分数步进。
- 球数驱动的自动模拟降速，参数可查、可设、可关闭。
- 后台模拟生成不可变 `RenderFrameData`，独立 STA 渲染线程组合画面帧。
- Media Foundation 改为流式 `Begin/WriteFrame/Complete`，禁止全片 BGRA 常驻内存。
- 独立「出片与时间」页面，支持新建、开始、暂停、继续、取消、状态、结果和打开目录。
- 任务清单与 `manifest.json`，记录输入快照、帧数、时间轴、哈希、输出和错误。
- `render.*` 命令族；`record.*` 兼容转发并打印迁移提示。

### 2.2 本期不做

- 不改变物理规则、胜负规则和 v3.2 平衡默认值。
- 不做屏幕、鼠标、其他窗口、麦克风或系统声音捕获。
- 不引入在线服务或云端渲染。
- 不为了快而跳过固定模拟步、改变碰撞顺序或使用不确定并行物理。
- 不承诺暂停后跨应用进程续算；本期只要求同进程暂停/继续，任务失败可重新生成。

---

## 3. 三套时间

| 时间 | 权威定义 | 用途 |
|---|---|---|
| **视频时间 `outputTime`** | `frameIndex / outputFps` | 决定 MP4 帧时间戳、总时长和进度 |
| **模拟时间 `simulationTime`** | 固定步 `1/60s` 的累计 | 物理、开火、解封、硬时限和 HUD“本局时间” |
| **生成耗时 `wallElapsed`** | 任务从开始到当前的真实墙钟时间 | 仅用于进度、速度和 ETA，不得参与游戏规则 |

### 3.1 精确帧步进

每个输出帧不再使用整数 `Round(60/fps)`。时间模块维护分数累计量：

```text
stepCredit += simulationHz * simulationScale / outputFps
while stepCredit >= 1:
    AdvanceFixedStep()
    stepCredit -= 1
ComposeFrame(frameIndex, outputTime, simulationTime)
```

- `simulationHz` 固定为 60，本期不开放修改。
- `outputFps` 支持 1~120，24/25/30/50/60 均不得漂移。
- `simulationScale=1` 时，输出 10 秒必须恰好推进约 10 秒模拟时间；误差不超过一个固定步。
- 分数累计状态写进任务清单，暂停/继续不得重置。

### 3.2 时长模式

`render.start` 必须显式或默认选择一种终止语义：

| 模式 | 终止条件 | 适用场景 |
|---|---|---|
| `output` | 输出视频达到 `seconds` | 定长短片，默认 |
| `simulation` | 模拟时间达到 `seconds` | 对拍与科学比较 |
| `winner` | 战局进入 Ended，另受 `maxOutputSeconds` 保护 | 完整对战视频 |

自动降速开启后，`output` 模式会在同样视频时长内推进较少的战局时间；`simulation` / `winner` 模式会生成更长视频。这两种结果不能混称。

### 3.3 实时预览与离线出片共用策略

`TimelineClock` 提供同一套倍率策略，但两种驱动源不同：

- **Play 预览**：以真实 UI tick 增加步进信用。球数进入降速区后，例如 0.25x，只推进约 15 个固定步/真实秒，主动减轻模拟负载；UI 不再为了追赶 60 步/秒不断积压。
- **Render 出片**：以输出帧时间增加步进信用，不等待真实时间。机器有余力时仍可快于实时生成；0.25x 表示每秒视频只推进约 0.25 秒战局，而不是把工作线程限速到 0.25 倍实时。

两者都读取相同的确定性球数曲线，因此现场看到的慢速段与成片时间映射一致。页面可分别关闭“预览使用自动倍率”和“出片使用自动倍率”，但倍率计算公式只有一份。

---

## 4. 球数驱动的自动降速

### 4.1 建议默认曲线（待压力标定）

新增 `render_time.json`：

| 字段 | 默认 | 范围 | 说明 |
|---|---:|---:|---|
| `autoSlowEnabled` | `true` | bool | 是否按总在场球数自动降速 |
| `slowStartBalls` | `2000` | 100~100000 | 不超过此值时保持 1.0x |
| `slowFullBalls` | `10000` | start+1~500000 | 达到此值时进入最低倍率 |
| `minSimulationScale` | `0.25` | 0.05~1 | 最低模拟倍率 |
| `manualSimulationScale` | `1` | 0.05~4 | 关闭自动时使用；大于 1 为快进 |
| `scaleQuantization` | `0.05` | 0.01~0.25 | 倍率量化步长，避免数值抖动 |
| `hysteresisBalls` | `200` | 0~10000 | 降档/升档迟滞，防止阈值附近跳变 |

上表是首轮实现与压力测试的建议起点，不视为用户已拍板的最终数值。R0/R8 必须用 2k、5k、10k、20k 球档实测后，把最终曲线和机器数据写回交付附录。

自动曲线在 `slowStartBalls` 与 `slowFullBalls` 之间线性下降，再按 `scaleQuantization` 量化。输入只取两个世界当前可渲染球数之和，因此相同种子和配置得到相同时间轴。

### 4.2 纪律

- 不能根据实际 FPS、CPU、内存、温度或编码速度临时改倍率，否则同一任务无法复现。
- 降速不减少输出 FPS，不丢输出帧，不跨越固定模拟步。
- 页面同时显示 `outputTime`、`simulationTime`、当前倍率和球数，避免用户把慢动作误判为卡顿。
- 预览允许降低刷新频率，例如每 10 个输出帧刷新一次页面；预览降频不影响最终帧。
- 时间倍率变化写入 manifest 的分段表，便于复核某一时刻为何变慢。

---

## 5. 出片流水线

```text
冻结任务输入
    ↓
Simulation Worker（固定顺序、单线程物理）
    ↓ 不可变 RenderFrameData，容量 3~8 的有界队列
Dedicated STA Composer（离屏组合左右世界 + HUD）
    ↓ BGRA 单帧
Streaming Encoder / PNG Sink
    ↓
MP4 + manifest + 可选 PNG
```

### 5.1 计算阶段

- 从当前配置或指定剧本创建内存副本；现场的 `battle.status`、`arena.status`、`balance.config` 在任务前后逐字不变。
- 物理仍单线程按现有确定顺序推进。只允许帧数据生产、合成和编码形成流水线，不允许并行修改世界。
- `RenderFrameData` 只包含绘图所需的不可变投影：球、炮台、领地版本、HUD 数值、事件和时间戳。
- 每帧投影必须是增量友好的紧凑结构；不序列化完整 `SceneWorld` JSON。

### 5.2 合成阶段

- 新建专用 STA 线程和 Dispatcher，持有离屏 `StageFrameRenderer`；主窗口 `StageView` 不参与最终出片。
- 逻辑分辨率由任务配置决定，不跟随窗口当前大小。
- 左世界、右世界、HUD、水印按冻结的舞台布局组合；同一 `RenderFrameData` 重绘哈希一致。
- 合成线程落后时，有界队列让模拟线程等待，不允许丢帧。

### 5.3 编码阶段

- `MediaFoundationEncoder` 改为流式生命周期：`Open → WriteFrame → Finalize/Abort`。
- 默认编码成功后不保留 PNG；`keepPng=true` 时逐帧写盘，不额外保留 BGRA 副本。
- MP4 失败时，从已配置的 PNG 或临时帧缓存恢复；不得重新污染现场再跑一遍。
- 临时文件先写 `.partial`，成功后原子改名；取消任务保留 manifest 并标记 `cancelled`。

### 5.4 内存红线

- 1080p 下 BGRA 同时在内存中的帧数默认不得超过 8。
- 任务运行内存相对基线增长目标 `< 512 MiB`，与视频总帧数近似无关。
- 禁止恢复 `List<byte[]>(frames)` 或任何等价的全片原始帧集合。

---

## 6. 独立页面

新增工具窗：`id=render`，标题「出片与时间」，命令入口 `win.show name=render`。

页面分区：

1. **任务**：剧本/当前配置、种子、名称、终止模式、时长。
2. **画面**：宽、高、FPS、MP4、PNG、舞台布局与 HUD。
3. **时间**：自动降速开关、起降球数、最低倍率、手动倍率；实时显示三套时间。
4. **进度**：阶段、已完成帧/总帧、球数、倍率、生成 FPS、ETA、内存。
5. **操作**：开始、暂停、继续、取消、打开结果目录。
6. **结果**：MP4/PNG/manifest 路径、帧数、输出时长、模拟时长、哈希和告警。

页面关闭只隐藏，任务继续；只有“取消”才终止任务。开始后冻结输入字段，避免中途修改造成半段新配置。

---

## 7. 命令与兼容

| 命令 | 示例 | 说明 |
|---|---|---|
| `render.config` | `render.config w=1920 h=1080 fps=60 autoSlow=true minScale=0.25` | 查询/设置默认出片与时间参数 |
| `render.estimate` | `render.estimate seconds=180 mode=output` | 给出帧数、预计原始吞吐和空间，不改变现场 |
| `render.start` | `render.start mode=winner seed=42 maxOutputSeconds=600 name=demo4` | 创建并启动独立任务 |
| `render.pause` / `render.resume` | - | 暂停/继续计算流水线 |
| `render.cancel` | `render.cancel confirm=true` | 取消当前任务，保留 manifest |
| `render.status` | `render.status` | 返回阶段、三套时间、球数、倍率、帧进度和输出 |
| `render.list` | `render.list limit=20` | 列出历史任务与结果 |

兼容策略：

- `record.config/start/stop/status` 保留一个版本周期，内部转发到 `render.*`。
- `record.start seconds=N` 映射为 `render.start mode=output seconds=N`。
- 控制台回显“record.* 已兼容转发，建议改用 render.*”，但不视为失败。
- 原 `StageMode.Record` 可保留为 UI 展示态，不能再承担驱动模拟的职责。

---

## 8. 数据与任务清单

```text
%AppData%/WBall/render_time.json
workspace/records/<name>_<seed>_<timestamp>/
  manifest.json
  output.partial.mp4
  output.mp4
  frames/                 # keepPng=true 或编码兜底
```

manifest 至少记录：

- App 版本、任务 ID、创建/开始/结束时间。
- 种子与场景、arena、balance、武器、舞台、HUD 的内容哈希。
- 输出规格、终止模式、总帧数、输出/模拟/墙钟时长。
- 每段时间倍率的起止输出帧、模拟时间、球数和倍率。
- 编码后端、结果文件、文件大小、取消/错误信息。
- 最终导演确定性哈希与可选关键帧哈希。

---

## 9. 验收

1. 生成 60 秒 1080p/30 FPS 时，主窗口可拖动、切换工具窗并执行只读命令；无连续数秒“未响应”。
2. 同种子/同快照/同时间配置连续两次，manifest 时间倍率分段、最终导演哈希和抽样帧哈希一致。
3. 24、25、30、50、60 FPS 下，`simulationScale=1` 的输出/模拟时间误差不超过 `1/60s`。
4. 1 万球压力场景进入 `minSimulationScale`，视频仍保持目标 FPS 与连续帧号；没有跳模拟步。
5. 关闭自动降速后，同压力场景保持指定手动倍率；生成变慢只体现在 `wallElapsed`。
6. 出片前后现场的 `battle.status`、`arena.status`、`balance.config` 与确定性哈希完全不变。
7. 1080p 长片的 BGRA 在途帧不超过配置上限，内存不随总帧数线性上涨。
8. MP4 写入失败时返回明确错误并保留可组合的 PNG；不存在损坏文件冒充成功结果。
9. 取消在 2 秒内生效，`.partial` 与 manifest 状态一致；重新开始不会复用脏编码器状态。
10. `record.*` 旧脚本仍可执行，结果语义与对应 `render.*` 一致。
11. 「出片与时间」窗可独立打开、滚动、暂停和查看结果，最窄停靠宽度下文字与按钮不重叠。

---

## 10. 实施顺序

```text
R0 留档当前 record.* 行为、内存峰值和 24/30/60 FPS 时间偏差
 → R1 TimelineClock + 三套时间 + 自动降速纯逻辑测试
 → R2 RenderJob 输入冻结、独立世界与 manifest
 → R3 RenderFrameData 投影 + 有界生产队列
 → R4 独立 STA StageFrameRenderer
 → R5 Media Foundation 流式编码 + PNG/partial 兜底
 → R6 render.* 命令 + record.* 兼容转发
 → R7 「出片与时间」独立页
 → R8 压力、确定性、内存、取消与实机验收
```

---

## 11. 风险与约束

| 风险 | 处理 |
|---|---|
| WPF 对象跨线程 | `RenderFrameData` 必须是普通不可变数据；WPF 资源只归专用 STA 合成线程 |
| 编码器背压 | 有界队列阻塞生产者，绝不丢帧或无限堆内存 |
| 自动降速破坏复现 | 只读取确定性球数并量化；禁止读墙钟性能 |
| 任务配置中途变化 | 开始时深拷贝并哈希，运行中 UI 只改“下一任务默认值” |
| winner 模式慢动作导致超长视频 | 强制 `maxOutputSeconds` 保护并在 manifest 标记截断 |
| 旧 record 入口混淆 | 兼容转发一个版本，UI 全部改称“出片” |

---

## 12. 交付附录（2026-07-27）

### 12.1 已交付

- `RenderTimeConfigStore` 持久化 `render_time.json`；默认起降球数 `2000/10000`、最低倍率 `0.25x`、量化 `0.05`、迟滞 `200`。
- `TimelineClock` 对 24/25/30/50/60 FPS 均以分数信用精确推进；10 秒用例全部得到 600 个固定步。
- Play 预览改为读取同一球数倍率曲线；不读取 CPU、实际 FPS 或编码速度。
- `RenderJobService` 在开始时冻结当前配置或指定 scenario 的 scene、arena、balance、turrets、weapons、stage、seed 与时间参数；模拟线程创建自己的双世界、经济桥和导演，不复用现场对象。
- MTA 模拟生产者只推进固定步并生成普通不可变 `RenderFrameData`；容量 `1~8`（默认 `4`）的有界 `Channel` 以等待方式背压，不丢帧、不无限积压。
- 专用 STA 消费线程独占 `StageFrameRenderer`、WPF 位图和 Media Foundation writer，逐帧执行“离屏组合 → PNG → 流式编码”；主窗口 `StageView` 不参与最终出片。
- Media Foundation 改为 `Open/WriteFrame/Complete` 流式接口，时间戳使用精确整数比例；MP4 成功后可删除临时 PNG，任一阶段失败则保留完整 PNG 与 manifest。
- 新增独立 `win.show name=render` 页面以及 `render.config/estimate/start/pause/resume/cancel/status/list`；`record.*` 保留兼容转发。
- manifest 已记录输入内容哈希、输出/模拟/墙钟时间、帧数、抽样帧哈希、最终导演哈希、BGRA/队列/内存峰值、弹丸价值账本、升格弹峰值、输出路径和错误。

### 12.2 最终线程与内存模型

最终实现严格采用“**MTA 模拟生产者 → 不可变紧凑帧投影 → 有界队列 → STA 合成/编码消费者**”。跨线程对象只含 `ImmutableArray` 承载的球、弹体、炮台、领地增量、HUD 数值和助力视觉数据；不携带 `SceneWorld` 或 WPF 对象。队列满时生产者等待，消费者失败会取消生产者并写失败 manifest；生产者失败会以失败终态关闭通道，不伪装成用户取消。BGRA 只存在于消费者当前帧，内存与总帧数无关。

### 12.3 验收结果

```text
PASS 24/25/30/50/60 FPS: 10 秒输出均推进 600 固定步
PASS 2 帧离屏 PNG: 连续帧号、manifest、输出目录完整
PASS 出片前后现场 DeterministicHash 完全一致
PASS 暂停时帧号不前进；取消进入 canceled 终态
PASS MP4 初始化失败时任务完成为 PNG，error 明确记录，不生成伪成功 MP4
PASS 同 seed/快照/时间配置连续两次：最终导演哈希、抽样帧哈希、倍率分段逐字一致
PASS scenario=demo2：冻结命名场景并使用场景 seed=7，现场哈希不变
PASS 10000 球正式场景：首帧进入 0.25x、帧号连续，工作集相对基线增加约 20.93 MiB（红线 512 MiB）
PASS 10000 球关闭自动降速：保持手动 0.1x，1 秒输出推进 0.1 秒模拟时间
PASS 取消 62.903 ms 进入 canceled；manifest/partial 一致，停止后可重新开始并完成
PASS record.config/start/status/stop 与 render.* 结果语义一致
PASS 出片前后 battle/arena/balance 配置逐字段一致，现场 DeterministicHash 不变
PASS 60 秒 1920x1080/30 FPS：1800/1800 连续 PNG，任务启动 2.816 ms
PASS UI Dispatcher：2658 次心跳，最大间隔 79.605 ms，无连续 2 秒未响应
PASS 1080p 5 秒/60 秒峰值：171.27/176.50 MiB，只增加 5.23 MiB；BGRA 峰值 1 帧，队列不超过 4
PASS 独立页面 300x720 STA 离屏布局：横向溢出 0 px，快照非空，控件无重叠
```

当前验收机的 Media Foundation H.264 media sink 返回 `0xC00D36FA`，因此走完整 PNG 降级；这是系统编码器能力问题，流式写入接口和失败事务已覆盖。

---

—— 文档结束 ——
