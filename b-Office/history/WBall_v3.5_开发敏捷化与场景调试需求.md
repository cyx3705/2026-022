# WBall · v3.5 开发敏捷化与场景调试需求

> 文档性质：开发敏捷化与旧功能整合
> 目标版本：**3.5.0**
> 文档日期：2026-07-29
> 前置基线：v3.4.0 工程分层与质量收口
> 当前状态：**开发完成，验收通过**

---

## 0. 一句话定义

v3.5 在不改变战斗、存档和出片结果的前提下，把 WBall 的日常开发路径缩短为“在 Core/Application 内修改并快速验证”，同时以新的「场景调试」工作台整合三个长期默认隐藏的旧窗口，作为敏捷化结构的第一个真实纵向切片。

### 0.1 收口证据

| 门禁 | 2026-07-29 结果 |
|---|---|
| Fast | 12/12 通过，Release 热运行 29.8 ms；Core/Application 引用图无 Desktop、WPF、AppShell 或 Media Foundation |
| 结构 | `WBallCommands` 34 行；本期六个领域命令文件 75～337 行 |
| 构建与格式 | Debug 独立输出、Release 均为 0 警告/0 错误；`dotnet format --verify-no-changes` 与 `git diff --check` 通过 |
| 命令与 UI | 编辑命令成功/原子失败、游戏命令 smoke 通过；`scenedebug` 三页签、320 px 布局与自动切页 smoke 通过 |
| 确定性 | v3.1、v3.2 与 v3.3 seed 42/43 四个冻结哈希逐字一致 |
| 出片与性能 | 录制 smoke、1 万球自动降速/内存门禁、助力性能通过；60 秒 1080p30 完成 1800/1800 帧 |
| 真实启动 | Release 标题为 `WBall v3.5.0`，加载 AppShell 3.0.3，WBall 进程 TCP 监听数为 0 |

---

## 1. 当前基线

本方案依据 2026-07-29 的 v3.4.0 收口提交 `7f09c772` 制定。

| 指标 | v3.4 当前事实 | v3.5 目标 |
|---|---:|---:|
| C# 文件 / 代码行 | 73 / 18,229 行 | 功能增长不再集中到现有热点 |
| 超过 500 行的文件 | 8 个 | 本期涉及的热点继续下降；新增文件不超过 500 行 |
| 超过 1,000 行的文件 | 3 个 | 不再向三个热点追加新职责 |
| 最大热点 | `BattleRuntime` 1,599、`WBallCommands` 1,227、`DropZoneView` 1,100 | 本期拆分 `WBallCommands`；其余只设增长红线 |
| Desktop C# / AppShell 耦合 | 41 / 36 个文件 | UI 适配可耦合，编辑规则进入 Application |
| Battle 目录 / AppShell 耦合 | 10 / 10 个文件 | 本期不全面迁移 Battle，避免再次形成重型治理版本 |
| 默认验证耗时 | 当前机器约 80 秒 | Fast 验证不超过 10 秒 |
| 验证项目 | `net8.0-windows` + WPF，直接引用 Desktop | 新增纯 `net8.0` Fast 验证，只引用 Core/Application |
| 活动文档 | README、版本需求、交付证据多处同步 | 只维护本文件；原始日志由验证器生成 |

### 1.1 已有优势

- AppShell 已通过 NuGet 精确消费 3.0.3，不存在复制框架源码的问题。
- `WBall.Core` 与 `WBall.Application` 已能独立以 `net8.0` 构建，依赖方向正确。
- v3.1/v3.2 回退哈希、v3.3 确定性哈希、录制 smoke 和长时出片基线完整。
- 增量构建本身很快；当前主要成本来自功能集中、验证器依赖 Desktop 和每次修改使用完整验收。

### 1.2 本期试点问题

当前 `objdebug`、`ballpanel`、`referee` 三个窗口默认隐藏，发现性差且编辑纪律不一致：

1. 「对象调试」主要通过 `scene.*` / `solid.*` / `ball.*` 指令提交，但异步结果没有稳定显示。
2. 「小球」窗口直接修改球和 `PublicDefaults`，绕过 CommandBus；公式重算没有命令等价入口。
3. 「裁判区」的开局和积分重置使用命令，但阵营字段直接写 `SceneWorld`；既有需求中的 `faction.set` 尚未实现。
4. 三个窗口各自订阅同一 `SceneWorld`，用户需要在隐藏标签间寻找功能，无法形成连续的场景调试工作流。

---

## 2. 目标与非目标

### 2.1 目标

1. 建立 Fast、Full、Release Acceptance 三级验证，普通规则修改先获得 10 秒内反馈。
2. 纯编辑规则和校验可在不加载 WPF、AppShell、Media Foundation 的情况下测试。
3. 将对象、小球与公式、裁判功能合入一个默认可见的「场景调试」窗口。
4. 所有 UI 写操作经过 CommandBus，UI、控制台和 Application 用例共享同一校验及结果。
5. `WBallCommands` 按功能域拆分，保留薄注册入口，降低单次阅读与修改范围。
6. 单一玩法或编辑规则原则上只涉及一个功能目录、最多约 5 个生产文件。
7. 已发布需求保持冻结；V3.5 的状态、决策和验收只维护在本文件。

### 2.2 非目标

- 不改变 v3.4 的战斗默认值、积分规则、随机序列或确定性哈希。
- 不改变场景文件格式、用户数据目录、录制格式或离线出片语义。
- 不全面重写或迁移 `BattleRuntime`、`BattleDirector`、`DropZoneView`。
- 不引入 DI 容器、消息总线替代品、微服务式分层或新的 UI 框架。
- 不修改 `2026-023-AppShell` 源码，也不恢复 WBall 自身 MCP 默认启动。
- 不为了兼容旧工具窗 ID 复制三套 View 或维护双入口。

---

## 3. 需求矩阵

| 编号 | 优先级 | 需求 | 完成结果 |
|---|---|---|---|
| V35-01 | P0 | 日常反馈依赖完整 WPF 验证器 | 新增纯 `net8.0` Fast 验证，当前机器热启动不超过 10 秒 |
| V35-02 | P0 | 三个旧调试窗口默认隐藏且入口分散 | 合并为默认右侧显示的 `scenedebug` 工具窗 |
| V35-03 | P0 | 小球、公式和阵营编辑存在 UI 直写 | 写操作统一经过 CommandBus 和 Application 用例 |
| V35-04 | P0 | 阵营编辑缺少命令等价入口 | 新增事务化 `faction.set` |
| V35-05 | P1 | 公式保存与全量重算没有统一入口 | `formula.set` 增加 `recalc=true|false` |
| V35-06 | P1 | `WBallCommands` 同时承载五个功能域 | 拆为 Scene/Ball/Formula/Game/Simulation 指令模块 |
| V35-07 | P1 | 每次修改都倾向运行完整验收 | CI 与本地命令明确分成三级门禁 |
| V35-08 | P1 | 验证结果在多份文档重复抄写 | 验证器输出机器可读摘要，文档只记录结论和证据路径 |
| V35-09 | P2 | 其它热点仍大 | 本期只禁止增长，后续按实际功能纵向拆分 |

---

## 4. 「场景调试」统一窗口

### 4.1 AppShell 注册契约

新增并只注册一个合并窗口：

| 属性 | 定稿值 |
|---|---|
| ToolWindow ID | `scenedebug` |
| 标题 | `场景调试` |
| 默认位置 | `DockSide.Right` |
| 默认比例 | `0.28` |
| 默认可见 | `true` |
| 内容 | 一个 `TabControl`，包含三个固定页签 |

旧 `objdebug`、`ballpanel`、`referee` 描述符直接删除，不注册别名，也不保留重复窗口。执行 `win.show name=scenedebug` 是唯一窗口入口。

AppShell 3.0 恢复旧布局时允许忽略失效节点并加入新默认窗口；WBall 不删除 `current.layout.xml`，不重置其它窗口位置，不触碰用户场景和配置文件。

### 4.2 页签结构

| 页签 | 复用能力 | 必须修复的行为 |
|---|---|---|
| 对象 | 当前对象/异形的几何、名称、颜色和选中状态 | 提交结果可见；非法输入不得静默跳过或部分应用 |
| 小球与公式 | 当前球的颜色、倍率、尺寸、重量；全局公式、预览和重算 | 全部经 `ball.set` / `formula.set`，不得直接写 `SceneWorld` 或配置文件 |
| 裁判 | 阵营列表、颜色、初始球数、初始倍率、积分、开局和重置 | 阵营编辑经 `faction.set`，开局和重置继续使用现有命令 |

窗口最小可用宽度按 320 px 设计。三个页签内部均可垂直滚动；固定按钮区、动态状态和最长中文标签在 1280×720 与 1920×1080 下不得重叠、裁切或撑开停靠比例。

### 4.3 自动页签切换

统一窗口监听 `SceneWorld` 选中对象的**类型变化**，映射如下：

| 新选择 | 自动页签 |
|---|---|
| `SelectedBallId` 指向有效球 | 小球与公式 |
| `SelectedId` 指向有效场景对象 | 对象 |
| `SelectedSolidId` 指向有效异形 | 对象 |
| 无选择、选择被删除或只有普通属性变化 | 保持当前页签 |
| 阵营变化 | 不自动切换；裁判页只由用户选择 |

同一个选中对象发生位置、颜色、倍率或积分刷新时只刷新字段，不重新设置页签。用户手动进入裁判页后，普通世界刷新不得把页面切走；只有随后发生新的有效画布选择类型变化才允许自动定位。

### 4.4 交互与反馈纪律

1. View 只收集输入并调用 CommandBus，不直接改 `SceneWorld`、`Faction`、`Ball` 或 `PublicDefaultsStore`。
2. 点击“应用”“保存公式”“重算全部球”必须等待指令结果，并在当前页显示成功或失败状态。
3. 参数解析、范围校验和事务性由 Application 用例完成；View 不复制范围表。
4. 失败时不得修改任何字段、保存文件或触发全量重算。
5. 成功后由 `SceneWorld.Changed` 刷新三个页签；不得用 View 内缓存作为权威。
6. 色板仍允许即时提交，但必须调用等价命令；无效 HEX 只更新为错误状态，不写入世界。

---

## 5. Application 用例与命令契约

### 5.1 最小用例边界

在 `WBall.Application` 增加三个功能内聚的用例服务：

```text
BallEditorService       校验并应用 BallEditRequest
FormulaEditorService    校验、持久化并应用 FormulaEditRequest
FactionEditorService    校验并应用 FactionEditRequest
EditResult              Success + Message，不引用 AppShell CommandResult
```

Desktop 组合根只创建一次服务实例；命令处理器调用服务，View 只调用命令。Fast 验证直接调用服务，因此不需要加载 WPF 或 AppShell。

每个请求必须先完整解析、复制候选值并校验，再一次性提交。持久化失败时内存状态保持原值；成功后只发送一次与业务语义相符的 `SceneWorld.Changed`。

### 5.2 `ball.set`

保留现有名称和参数：

```text
ball.set id=<id> [color=#RRGGBB] [multiplier=1..1000000000000]
         [size=2..72] [weight=0.1..500]
```

- 至少提供一个待修改字段；空更新失败。
- 所有参数先校验；任一非法则整条失败。
- 设置 multiplier 且没有显式 size/weight 时，按公式重算两者；显式值最后覆盖公式结果。
- 成功回显继续包含球 ID 和最终倍率，不改变既有命令脚本的成功语义。

### 5.3 `formula.set`

在现有参数后增加：

```text
formula.set [sizebase=] [sizescale=] [weightbase=] [weightscale=]
            [initial=1..1000000000000] [recalc=true|false]
```

- `recalc` 默认 `false`，保持旧脚本行为。
- 所有数值必须是有限数；initial 必须位于合法范围。
- 保存配置成功后才替换世界中的默认公式。
- `recalc=true` 时对当前全部球应用新公式，并在回显中报告重算球数。
- `recalc=false` 只影响新球和后续倍率变化，不改变现存球尺寸/重量。

### 5.4 `faction.set`

新增命令：

```text
faction.set id=<id> [name=<text>] [color=#RRGGBB]
            [balls=0..2147483647] [multiplier=1..1000000000000]
            [score=0..9223372036854775807]
```

- id 必填且必须命中现有阵营；本期不通过该命令新建阵营。
- 至少提供一个更新字段；所有字段校验通过后一次性提交。
- `name` 不得为空白；颜色规范化为 `#RRGGBB`；数值越界直接失败，不静默 clamp。
- 成功后不自动开局、不重置战场、不改变弹药队列和炮台生命。
- `game.start`、`game.resetscore`、`faction.list` 名称和既有语义保持不变。

### 5.5 指令文件拆分

`WBallCommands` 保留为不超过 200 行的注册门面，按顺序调用：

```text
SceneCommands.Register
SceneFileCommands.Register
BallCommands.Register
FormulaCommands.Register
GameCommands.Register
SimulationCommands.Register
WireCommands.Register
SolidCommands.Register
```

拆分只移动描述符和处理器，不批量改名、改参数、改回显。每个新指令文件不超过 500 行；跨域共享解析器仅在确有两处以上使用时提取。

---

## 6. 三级验证与开发流程

### 6.1 Fast

新增 `b-Code-Verify/WBallFastVerify/WBallFastVerify.csproj`：

- 目标框架 `net8.0`。
- 只引用 `WBall.Core` 和 `WBall.Application`。
- 不设置 `UseWPF`，程序集引用门禁禁止 WPF、AppShell 和 Media Foundation。
- 覆盖本期三个编辑服务、时间轴和适合纯逻辑运行的短场景。
- 当前机器 Release 热构建后的执行时间不超过 10 秒；冷 NuGet restore 不计入该指标。

本地普通编辑循环只要求：目标项目构建、Fast 验证和相关格式检查。

### 6.2 Full

用于功能分支合并和推送主工作分支：

1. 解决方案 Release 构建，0 警告、0 错误。
2. v3.1/v3.2 回退哈希与 v3.3 seed 42/43 确定性哈希。
3. 命令烟测、场景调试页面 smoke、录制 smoke。
4. `dotnet format --verify-no-changes` 和生成物索引检查。

### 6.3 Release Acceptance

只在版本收口或人工触发时执行：

1. Debug/Release 双构建。
2. 出片页、长时 60 秒 1080p30、助力性能和内存红线。
3. 真实启动、窗口布局恢复、AppShell DLL 版本和 MCP 监听检查。
4. OHS `git.rule.scan refresh=true deep=true`、提交和推送。

CI 必须将三档以独立步骤或独立作业表达，不能让本地 Fast 命令隐式触发 Full/Release Acceptance。

### 6.4 验证证据

Fast 与 Full 在控制台输出 PASS/FAIL，并生成一个机器可读摘要，至少包含：版本、套件、耗时、通过数、失败数、哈希和产物目录。成功时仍按 v3.4 纪律清理临时大文件；失败时保留证据。

README 只记录稳定命令和当前版本链接，不复制完整验收日志。v3.1～v3.4 已发布需求不再补写 V3.5 实施过程。

---

## 7. 实施批次

| 批次 | 内容 | 出口 |
|---|---|---|
| A | 本文档定稿；建立 Fast 项目和计时基线 | Fast 可独立构建运行，不引用 Desktop |
| B | 新增三个 Application 编辑服务；命令处理器改为调用服务 | 服务事务性测试通过，现有命令回归不变 |
| C | 增加 `faction.set` 与 `formula.set recalc=`；修复三页 UI 直写 | UI 与控制台结果一致，失败无部分写入 |
| D | 新建 `SceneDebugView`，合并三个页签并替换 ToolWindow 描述符 | `scenedebug` 默认右侧可见，自动切页规则通过 |
| E | 按六个功能域拆分 `WBallCommands` | 门面不超过 200 行，命令清单与回显回归通过 |
| F | CI 分档、页面与布局迁移 smoke、Full 验收 | 日常 Fast 与完整验收互不隐式触发 |
| G | Release Acceptance、版本升 3.5.0、README 单点更新 | 全部门槛通过后才标记完成 |

每个批次单独构建并运行 Fast；涉及命令或 UI 的批次增加对应 smoke。不得等到 G 批次才首次运行确定性哈希。

---

## 8. 验收标准

### 8.1 敏捷化

1. `WBallFastVerify` 在纯 `net8.0` 下构建，引用图中没有 Desktop、WPF、AppShell 或 Media Foundation。
2. 当前机器 Release 热启动执行不超过 10 秒，并输出机器可读摘要。
3. 小球、公式和阵营规则可直接通过 Application 服务测试，不启动窗口。
4. `WBallCommands` 不超过 200 行；新增指令模块均不超过 500 行。
5. `BattleRuntime`、`DropZoneView` 本期不增加新职责或因窗口整合继续膨胀。

### 8.2 场景调试窗口

1. 首次默认布局右侧显示「场景调试」，窗口 ID 为 `scenedebug`，中央对战舞台保持可用。
2. 窗口包含且只包含“对象”“小球与公式”“裁判”三个一级页签。
3. 选择球、对象或异形时按 §4.3 自动定位；普通刷新和无选择不抢走当前页签。
4. 三个旧窗口 ID 不再出现在工具窗清单；调用旧 `win.show` 明确失败，不产生重复窗口。
5. 带 V3.4 旧布局启动时不崩溃、不删除布局或用户数据，新窗口可恢复到默认右侧位置。
6. 320 px 窗口宽度及 1280×720、1920×1080 主窗口下，无控件重叠、不可达按钮或横向撑破。

### 8.3 编辑与命令

1. `ball.set`、`formula.set recalc=`、`faction.set` 的成功与失败路径均有事务性测试。
2. UI 点击与控制台执行同一命令得到相同状态和等价回显。
3. 非法数字、未知 ID、空名称和无效颜色均失败，世界、配置文件和 UI 权威值保持原样。
4. `formula.set recalc=true` 的重算数量准确；false 不改变现存球的 size/weight。
5. `game.start`、`game.resetscore`、`faction.list` 与 V3.4 行为一致。

### 8.4 全量回归

1. v3.1：`6381A3898C0FAD65B57D43C140917A010713AA3015F601BACE14C7E5B88333F3`。
2. v3.2：`E24FD280C34B54F79DAFCAE466DE299B4B76F56B69D83EF63757B96F81BF9184`。
3. v3.3 seed 42：`5A458728F1A2A4296B126E1EC2F50221EC3D212393125EDFAD62EFED12F8525B`。
4. v3.3 seed 43：`E3CC0CFA0E3B630DBB11372AC3F31F03DB031E144858E75D552FC1CC1C3656CA`。
5. 默认回归、场景调试页面 smoke、录制 smoke、长时出片和格式检查全部通过。

---

## 9. 风险与约束

| 风险 | 控制方式 |
|---|---|
| 敏捷化再次演变为全仓重构 | 只迁移本期编辑纵向切片；Battle 热点不在本期全面处理 |
| 三个 View 合并后仍各自维护状态 | `SceneWorld` 为唯一权威；统一窗口只协调页签，不复制业务数据 |
| 自动切页打断输入 | 只在有效选择类型变化时切换，属性刷新和无选择保持当前页 |
| 旧布局引用已删除 ID | 依赖 AppShell 3.0 的失效节点自愈；增加真实旧布局 smoke，不手工删布局 |
| 命令拆分导致脚本回退 | 拆分前后对 `command.list`、参数、成功/失败回显做快照比对 |
| Fast 为追求速度漏掉关键行为 | Fast 负责局部规则；Full 和 Release Acceptance 继续保留确定性与出片红线 |
| UI 与命令出现两套校验 | 校验只在 Application 服务，View 不复制范围和默认值 |

---

## 10. 完成定义

只有同时满足以下条件，才能将文档状态改为“已完成”并把程序集版本升为 3.5.0：

- V35-01～V35-08 全部通过；V35-09 后续项有明确记录且没有新增热点债务。
- 「场景调试」默认可见，旧窗口退役，所有编辑操作由命令与 Application 用例统一承载。
- Fast 不超过 10 秒，Full 和 Release Acceptance 可分别执行且全部通过。
- 四个确定性哈希逐字一致，场景格式、用户数据和录制输出没有迁移。
- README 只增加 V3.5 状态和本文件入口，不复制本文件的基线、矩阵或验收日志。
- OHS 提交不包含 `bin/obj/.vs`、验证临时目录或其它本机生成物。

—— V3.5 需求文档结束 ——
