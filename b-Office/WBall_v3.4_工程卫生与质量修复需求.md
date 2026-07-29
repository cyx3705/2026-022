# WBall · v3.4 工程卫生与质量修复需求

> 文档性质：工程质量修复与维护成本治理
> 目标版本：**3.4.0**
> 文档日期：2026-07-29
> 前置基线：v3.3.0 同阵营积分传递、v3.2.1 离线出片与时间模块
> 当前状态：**已完成并收口为 3.4.0；全部 P0/P1 门槛通过，P2 热点拆分保留为后续维护项（见 §10）**

---

## 0. 一句话定义

v3.4 不改变球战规则和成片结果，专门把 WBall 从“功能可用但修改放大”恢复为“可隔离构建、可选择性测试、可控内存、可维护交付”的工程状态。

---

## 1. 审计基线

本方案依据 2026-07-28 对工作树、源码、构建和 OHS Git 规则扫描得到的事实制定。

| 指标 | 当前事实 | v3.4 目标 |
|---|---:|---:|
| C# 文件 / 代码行 | 68 / 19,327 行 | 保持行为不变，热点按职责拆分 |
| 超过 500 行的文件 | 10 个 | 新增文件不得超过 500 行；热点持续下降 |
| 超过 1,000 行的文件 | 5 个 | 首批降至不超过 2 个 |
| Git 追踪的生成文件 | 185 个，约 180 MB | 0 个 `bin/obj/.vs` 生成文件 |
| OHS 规则覆盖率 | 44.6%，336 个文件未决 | 产品格式全部纳管；继承模板二进制按扫描器能力登记例外 |
| 验证器临时产物 | 45 个目录，约 3.172 GiB | 成功自动清理，失败按开关保留 |
| AppShell 引用 | 原始基线 `0.5.0`，曾迁移到 `0.7.2` | 固定消费 `2026-023-AppShell` 正式 `3.0.0` 包 |
| 配置字段维护点 | 字段、范围、克隆、命令、两个设置窗、测试多处重复 | 一处字段描述驱动验证、命令和 UI |
| 格式门禁 | 无根级 `.editorconfig` / CI；135 处格式诊断 | 格式、分析器和构建门禁进入 CI |

### 1.1 现有验证结论

- v3.3 确定性哈希、积分守恒、长时出片和页面烟测已通过，说明本次不是行为重写。
- AppShell 源码未复制进 WBall；当前通过相邻 `2026-023-AppShell/z-Package-AppShell/feed` 固定消费 3.0.0 正式包。
- `BattleRuntime` 约 1,599 行，`WBallCommands` 约 1,254 行，`PropertyProjection` 约 1,173 行，`DropZoneView` 约 1,100 行，验证器约 1,045 行。
- `StageRecorder` 没有调用方，却仍保留一次性保存全部 BGRA 帧的旧实现，与 v3.2.1 的有界流水线相冲突。
- `BattleDirector.Events` 没有容量上限；长时模拟可能持续保留开火、受击等事件。

---

## 2. 目标与非目标

### 2.1 目标

1. 任何全新 checkout 都能独立恢复、构建和运行，不依赖仓库内旧 `obj` 或本机路径状态。
2. 验证器成功运行后不遗留大体积临时数据；失败时能留下最小、可定位的证据。
3. 长时模拟和录制的内存增长有明确上限，不能因为日志或事件集合无界增长。
4. 新增一个平衡字段时，不再同步修改多个重复的 UI/命令/范围/克隆表。
5. WBall 迁移到 AppShell 3.0.0 的冻结消费契约，中央舞台使用 `DockSide.Center`，旧布局由框架自愈。
6. 将纯战斗、物理、时间轴和配置逻辑与 WPF/AppShell 隔离，使无头测试无需启动桌面壳。

### 2.2 非目标

- 不改变 v3.3 的 `ProjectileRole`、助力速率、积分守恒、升格回收和确定性规则。
- 不借质量修复之名重新调整默认战斗参数、画面风格或出片格式。
- 不直接修改 OHS/AppShell 源码；AppShell 迁移只改 WBall 消费端。
- 不强制删除用户未明确授权的现有临时目录；清理动作必须限定到验证器自有前缀。

---

## 3. 修复矩阵

| 编号 | 优先级 | 问题 | 修复结果 |
|---|---|---|---|
| V34-01 | P0 | `bin/obj/.vs` 被追踪，OHS 全量提交会带入生成物 | Git 历史和工作树不再追踪生成物，构建状态可审计 |
| V34-02 | P0 | 验证器临时目录无生命周期清理 | 成功清理、失败保留、可显式保留 |
| V34-03 | P0 | `BattleDirector.Events` 无界增长 | 事件只保留诊断窗口，或完全改为计数/流式 sink |
| V34-04 | P1 | `StageRecorder` 和批量 BGRA 编码路径已失效但仍存在 | 删除死代码，唯一录制入口为 `RenderJobService` |
| V34-05 | P1 | `BalanceConfig` 元数据分散，手写 Clone 易漏字段 | 建立字段描述注册表，验证/命令/UI 共用 |
| V34-06 | P1 | AppShell 消费契约先后经历 0.5.0 / 0.7.2 | 固定 3.0.0 正式包，完成中央窗口、身份、MCP 和生命周期迁移 |
| V34-07 | P1 | 所有业务仍在单一 WPF 程序集 | 拆出可无头构建的 Core/Application 边界 |
| V34-08 | P1 | 没有统一格式、分析器、警告和 CI 门禁 | clean checkout 的 Debug/Release 和回归成为必需门槛 |
| V34-09 | P2 | 总控类和验证器过大，修改上下文成本高 | 按职责拆分，保留薄组合根和薄测试入口 |
| V34-10 | P2 | OHS Git 规则有大量未决格式 | 每种实际格式明确 track、ignore 或 LFS 处置 |

---

## 4. 设计要求

### 4.1 仓库卫生与可重定位构建（V34-01）

1. 根 `.gitignore` 必须忽略 `**/bin/`、`**/obj/`、`**/.vs/`、测试输出和本地工作目录；不得只依赖扩展名。
2. 生成文件从 Git 索引移除，但不删除本机文件；提交前必须用 `git ls-files` 证明生成文件为 0。
3. 增加根级 `Directory.Build.props` 和 `.editorconfig`，统一目标框架、分析器、警告策略和换行/缩进。
4. `NuGet.Config` 不得写死另一工作树的绝对路径。若继续使用本地 AppShell feed，必须提供相对路径方案或明确的环境变量入口。
5. 采用干净临时输出目录执行一次构建，不能因仓库内历史生成 `.cs` 被编译而出现重复入口或重复特性。

### 4.2 验证器产物生命周期（V34-02）

1. `dataRoot` 必须由 `try/finally` 管理。
2. 默认行为：通过时删除验证器自有临时目录；失败时保留并打印绝对路径。
3. 增加 `--keep-artifacts` 和 `--artifact-root <path>`，用于长测和人工复盘。
4. 每个 smoke suite 必须拥有独立子目录，不能用进程号作为唯一的永久目录命名策略。
5. 清理失败只记录 warning，不覆盖原始测试结果；不得递归触碰系统临时目录的其他内容。

### 4.3 有界运行时（V34-03 / V34-04）

1. `BattleDirector.Events` 改为固定容量诊断缓冲，默认上限 500；需要完整历史时通过可选流式 sink 写文件，不保留全部对象。
2. `StageRecorder`、`RecordConfig` 和只被其使用的 `EncodeBgraFrames` 批量入口删除或标记为编译期不可用；生产录制只允许 `RenderJobService` 的 Channel 流水线。
3. 长时验收必须同时观察帧队列、BGRA 峰值、事件数量和进程工作集。
4. 任何新增集合都必须在设计说明中声明容量上限、清理点和取消行为。

### 4.4 配置元数据单一来源（V34-05）

建立 `BalanceFieldDescriptor`（名称、类型、命令参数、显示名、范围、分组、作用域、是否即时生效）注册表：

- `BalanceConfig` 保留强类型属性和默认值；
- 范围校验、JSON 迁移和 Clone 由描述符/通用复制逻辑覆盖；
- `balance.*` 命令从描述符生成参数规格和读写映射；
- 「战斗平衡」和「对战区」设置窗从同一描述符生成控件；
- 兼容别名（如 `merge`）单独声明，不再隐式同时修改两个无关字段；
- 新增字段必须有一条字段级测试，证明默认值、上下限、持久化和 UI/命令读取一致。

禁止用字符串散落在多个 `switch`、`SetD`、UI `Field` 列表中作为长期扩展方式。

### 4.5 AppShell 3.0.0 迁移（V34-06）

1. 桌面端只直接引用 `OneHistory.AppShell.Shell` 3.0.0；Core/Services 使用同版本传递依赖，正式源只指向 023 的 `z-Package-AppShell/feed`。
2. 主舞台显式注册为 `DockSide.Center`；其它设置窗继续使用 `ToolWindowDescriptor`，不依赖 AvalonDock、旧工作页或 `MainContent`。
3. 不在消费端删除布局文件；0.7.2 的顶部舞台和无效节点由 AppShell 3.0 布局恢复器迁入原生中央文档区。
4. 入口先设置 `AppIdentity`，程序集版本与 `ShellConfig.AppVersion` 同源；应用退出时释放 Workspace 和自有服务。
5. 数据库移除后保留 3.0 的文件化 MCP/提示词治理兼容能力，但 WBall 默认显式关闭 MCP；模块托管继续启用，并保留 v3.3 命令名与 `record.*` 兼容别名。

### 4.6 项目分层（V34-07 / V34-09）

目标结构：

```text
WBall.Core          纯模型、物理、战斗、时间轴、配置契约；不引用 WPF/AppShell
WBall.Application   场景/剧本/命令用例、录制抽象、结果清单
WBall.Desktop       WPF 画面、Media Foundation、AppShell 适配和组合根
WBall.Verify        无头验收入口；按 suite 分文件组织
```

拆分顺序必须从依赖叶子开始：先抽 `Core`，再抽录制/渲染适配，最后移动窗口和命令注册。不得一次性搬动全部文件并失去可回滚点。

热点拆分目标：

- `BattleRuntime` 拆为发射、对消/助力、护盾/领地、胜负协调器；
- `WBallCommands` 按场景、模拟、投影和球操作拆成独立注册器；
- `DropZoneView` 将命中测试、编辑操作和绘制器分离；
- 验证器按 timeline、render、assist、compat、page 五个 suite 文件组织。

### 4.7 质量门禁（V34-08 / V34-10）

1. `TreatWarningsAsErrors=true`，Debug/Release 均执行。
2. `dotnet format --verify-no-changes` 在 clean checkout 通过。
3. 解决方案必须包含验证项目；每个 suite 可单独执行，失败输出结构化名称和证据目录。
4. CI 至少执行：恢复、Debug 构建、Release 构建、核心无头回归、渲染 smoke、规则扫描。
5. OHS `git.rule.scan` 必须证明产品源码和生成物处置完整；扫描器无法表达的普通 Git 二进制须在规则台账逐类登记，不得为追求数字归零强制迁入 LFS。

---

## 5. 实施批次与提交边界

### 批次 A：先止血（建议独立提交）

实施 V34-01、V34-02、V34-03、V34-04、V34-08 的基础文件。此批不得改变战斗公式和画面结果。

验收：干净 checkout 构建；验证器成功清理；事件和帧内存有上限；v3.3 哈希不变。

### 批次 B：降低修改放大

实施 V34-05，并把现有 `balance.*`、两个设置窗和预设/剧本持久化迁移到描述符驱动。

验收：新增一个临时测试字段只需改描述符和行为映射；现有所有字段的默认值、范围、JSON 和命令结果不变。

### 批次 C：升级 Shell 契约

实施 V34-06，完成 3.0.0 冻结包消费与中央工作区迁移，再拆 `Core/Application/Desktop`。

验收：布局恢复、窗口显示/隐藏/浮动/停靠、MCP 默认不监听、出片页和 v3.3 回归全部通过。

### 批次 D：收敛结构

实施 V34-07、V34-09、V34-10，拆分总控类、验证器和 OHS 规则台账。

验收：核心项目可在无 WPF 环境运行；新文件不超过 500 行；所有格式有明确规则。

---

## 6. 验收矩阵

| 验收项 | 通过标准 |
|---|---|
| 生成物卫生 | `git ls-files` 中 `bin/obj/.vs` 数量为 0 |
| clean build | Debug/Release 0 warning、0 error；不依赖仓库旧 `obj` |
| 格式 | `dotnet format --verify-no-changes` 通过 |
| 临时目录 | 成功 smoke 后无 `wball_verify_v34_*` 残留；失败保留目录可定位 |
| 长时内存 | 60 秒 1080p 基线仍通过；事件集合 ≤500；新增集合均有上限 |
| 行为零漂移 | v3.1/v3.2 回退哈希和 v3.3 seed=42/43 哈希逐字一致 |
| 配置 | 所有字段范围、持久化、命令、UI、Clone/迁移测试通过 |
| AppShell | Shell/Core/Services 均解析为 3.0.0；舞台为 `DockSide.Center`；旧页面/数据库契约引用为 0 |
| 分层 | `WBall.Core` 不引用 `System.Windows`、AppShell 或 Media Foundation |
| OHS 规则 | 产品源码/配置均受规则管理，生成目录明确 ignore；继承模板二进制例外与工具限制有实扫证据 |

---

## 7. 风险与回滚

| 风险 | 控制方式 |
|---|---|
| 删除旧录制入口影响脚本 | 先保留 `record.*` 命令兼容层，只替换内部实现 |
| 3.0.0 是冻结契约升级 | 固定正式包；保留 0.7.2 回滚点；依赖框架自愈旧布局，不删除用户布局 |
| 配置描述符遗漏字段 | 反射字段清单测试 + JSON round-trip + 命令/UI 快照 |
| 清理误删用户文件 | 只操作带版本前缀的验证器目录，并提供 `--keep-artifacts` |
| 分层迁移大范围冲突 | 每次只移动一个依赖叶子；每批均运行 v3.3 哈希回归 |

禁止使用 `git reset --hard` 或批量删除整个 Temp 目录作为修复手段。

---

## 8. 交付物

1. `.gitignore`、`.editorconfig`、`Directory.Build.props` 和 CI 配置。
2. 生成物清理后的索引提交和 OHS Git 规则台账。
3. 验证器生命周期修复及 suite 拆分报告。
4. 有界事件/录制实现和死代码删除记录。
5. 配置描述符设计与字段迁移清单。
6. AppShell 3.0.0 消费记录、布局兼容说明和回滚点。
7. V3.4 验收日志、哈希、构建和内存证据。

---

## 9. 完成定义

只有同时满足以下条件，才能将版本标记为 v3.4.0：

- 所有 P0/P1 项通过，且没有新增生成物、临时目录泄漏或无界集合；
- v3.3 的规则、默认参数、确定性哈希和录制结果不变；
- AppShell 3.0.0 迁移、Core 分层和质量门禁均有可复现证据；
- README、版本文档、OHS 规则台账、提交范围和回滚点一致；
- 交付提交不包含 `bin/obj/.vs`，也不包含未声明的本地环境产物。
- V34-09/V34-10 的 P2 剩余项已明确记录，不伪装成已完成，也不阻塞全部 P0/P1 已通过的 3.4.0。

---

## 10. 交付记录（2026-07-29）

### 10.1 已交付项与提交

| 提交 | 项 | 关键结果 |
|---|---|---|
| `7e7d7001` | V34-01 | 索引里生成物 185 → **0**；`.gitignore` 项目段（写在 OHS managed 段之外）、根 `Directory.Build.props`（`TreatWarningsAsErrors`）、根 `.editorconfig`；`nuget.config` 绝对路径 → 相对路径 |
| `1242ef82` | V34-02 | `VerifyArtifacts` 由 `using`(try/finally) 托管产物根；通过即清理、失败保留并打印路径；`--keep-artifacts` / `--artifact-root` 实测有效；四个 suite 各自子目录 |
| `3b16be3c` | V34-03 | `BattleDirector.Events` 有界（500）+ `EventSink` 流式出口 + `EventsRaised` 计数；追加集中到 `Append()` |
| `5b8e111d` | V34-04 | 删除 `StageRecorder.cs`(203 行,含 `RecordConfig`) 与 `MediaFoundationEncoder.EncodeBgraFrames`；录制唯一入口为 `RenderJobService` |
| `8d24e7fe` | V34-08 | 137 处 WHITESPACE 诊断清零，`dotnet format --verify-no-changes` 通过；新增 `.github/workflows/ci.yml`（生成物卫生断言 + 格式 + Debug/Release + 三套 smoke） |
| `d38fabd6` | V34-05 | 新增 `BalanceFields` 50 字段描述符注册表；`Ranges`/`Clamp`/`Validate`/`Clone`/命令参数规格/命令写入/UI 控件全部由其派生；`AuditCoverage()` 自检；验证器加两项守卫 |
| `6eca024c` | V34-06 | AppShell **0.5.0 → 0.7.2**；舞台改 `id=stage` 工具窗口（`DockSide.Top` 进中央列）；新增一次性布局迁移 |
| 2026-07-29 本轮 | V34-06 | AppShell **0.7.2 → 3.0.0**；正式源迁至 023；舞台改为 `DockSide.Center`；MCP 能力兼容但默认关闭；移除消费端删布局迁移 |
| 2026-07-29 本轮 | V34-02 | 验证产物状态改为“默认失败、正常返回 0 才显式成功”，未捕获异常不再误删诊断现场 |
| `f6e3feae` / `f69c9e27` | V34-09 | 抽出 `VerifyRun` 与 timeline/page/assist suite，验证器入口由 1086 行降至约 836 行 |
| `5f63410c` | V34-09 | 删除 1179 行数据库投影热点，以 194 行文件化 `ScenePropertyService` 保留仍在使用的业务能力 |
| （本提交） | V34-10 | `.gitattributes` 项目段：文本 `eol=lf`、CAD/EDA/Office 标 `binary`；新增 `b-Office/OHS_Git规则台账.md` |
| 2026-07-29 收口 | V34-07 | 新增 `WBall.Core(net8.0)` 与 `WBall.Application(net8.0)`；20 个核心源文件和 4 个应用源文件脱离 WPF/AppShell；桌面项目仅向内依赖；新增运行时程序集引用门禁 |
| 2026-07-29 收口 | 3.4.0 | 全部 P0/P1 验收通过，程序集版本与 README 升为 3.4.0；V34-09/10 剩余项按 P2 债务登记 |

### 10.2 证据

| 验收项 | 结果 |
|---|---|
| 行为零漂移 | v3.1 回退 `6381A389…`、v3.2 回退 `E24FD280…`、v3.3 seed=42 `5A458728…`、seed=43 `E3CC0CFA…` —— **改前/改后逐字一致** |
| 命令结果不变（批次 B 硬条件） | 9 条 `balance.*` 回显与重构前**逐字相同**（改前/改后日志比对） |
| clean build | 干净 `git clone` 后 Debug 构建 0 警告 0 错误，且 clone 不带任何生成物；Release 同样 0/0 |
| 相对 feed 可用 | 空包目录 `dotnet restore --packages` 从 `../2026-023-AppShell/z-Package-AppShell/feed` 取到 3.0.0（不靠全局缓存） |
| AppShell 3.0 精确锁版 | 桌面端引用使用 NuGet 精确约束 `[3.0.0]`；`dotnet list package --include-transitive` 显示 Shell 请求 `[3.0.0]`、解析 3.0.0，Core/Services 传递依赖均解析为 3.0.0；三枚输出 DLL 的 File/Product/Assembly Version 均为 3.0.0 |
| AppShell 3.0 运行时 | WBall v3.4.0 正常启动；旧布局恢复；模块目录监听；WBall 显式设置 `EnableMcp=false`，实测启动进程 TCP 监听数为 0 |
| 项目分层 | `WBall.Core` 与 `WBall.Application` 可独立 `net8.0` 构建；程序集引用断言确认无 WPF/AppShell/Media Foundation；新层最大文件 439 行 |
| 格式 | `dotnet format --verify-no-changes` 通过 |
| 临时目录 | 通过运行输出 `ARTIFACTS cleaned`，目录数不增；`--keep-artifacts`/`--artifact-root` 按预期留存 |
| 长时内存 | 2026-07-29 重跑 60s 1080p30：1800/1800 帧完成；短片/长片峰值 158.26MiB → 162.83MiB（delta 4.57MiB）；UI 派发器 maxGap 82.707ms |
| 出片链路 | 渲染 smoke、出片页 300px smoke、`win.show/float/dock name=stage`、`record.*` 兼容别名全通过 |
| 快速取消竞态 | 状态更新按任务 ID 隔离且终态不可回退，修复渲染线程写入 `canceled` 后模拟线程倒退为 `simulating` 的竞态；修复后 `--render-smoke` 连续 5 次通过，本轮复验约 62 ms 进入 `canceled`，随后新任务正常完成 |
| 旧临时产物 | 经用户授权，删除 46 个 `wball_verify_v32_*` 目录，回收 **3.18 GB**（只删该前缀） |

### 10.3 P2 后续项与已接受例外

| 项 | 状态 | 原因 / 下一步 |
|---|---|---|
| V34-09 热点拆分 | **进行中** | 验证器已抽出 `VerifyRun` 与 timeline/page/assist suite，`Program.cs` 降至约 836 行；数据库投影热点已删除。其余 `BattleRuntime` / `WBallCommands` / `DropZoneView` 仍待拆分 |
| V34-10 的 `git.rule.scan` 归零 | **接受工具例外** | OHS 收口前实扫 53 个未决，其中 27 个是本提交尚未入索引的新 `.cs/.csproj`，提交后由现有 Git+LF 规则纳管；剩余 26 个为继承模板普通二进制，扫描器无法表达 `track=true,lfs=false,lf=false`，详见规则台账 §3 |
| `Unused/b-EPLAN/N0000.edb` | **当前索引已移除** | 现工作树不再携带该模板数据库；历史对象仍在 Git 历史中，不宣称历史仓库已瘦身 |
| `AppVersion` 升 3.4.0 | **完成** | 全部 P0/P1 已通过；程序集身份、README 和本交付记录同步为 3.4.0 |

—— 交付记录结束 ——
