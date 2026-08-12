# WBall · v3.4 工程卫生与质量修复需求

> 文档性质：工程质量修复与维护成本治理
> 目标版本：**3.4.0**
> 文档日期：2026-07-28
> 前置基线：v3.3.0 同阵营积分传递、v3.2.1 离线出片与时间模块
> 当前状态：**批次 A / B / C 已交付并验证；批次 D 未做（见 §10 交付记录）**

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
| OHS 规则覆盖率 | 44.6%，336 个文件未决 | 100% 纳管或明确忽略 |
| 验证器临时产物 | 45 个目录，约 3.172 GiB | 成功自动清理，失败按开关保留 |
| AppShell 引用 | `0.5.0` | 完成 `0.7.2` 契约迁移并锁定版本 |
| 配置字段维护点 | 字段、范围、克隆、命令、两个设置窗、测试多处重复 | 一处字段描述驱动验证、命令和 UI |
| 格式门禁 | 无根级 `.editorconfig` / CI；135 处格式诊断 | 格式、分析器和构建门禁进入 CI |

### 1.1 现有验证结论

- v3.3 确定性哈希、积分守恒、长时出片和页面烟测已通过，说明本次不是行为重写。
- AppShell 源码未复制进 WBall，当前是正常 NuGet 包引用；但 WBall 固定在旧的 0.5.0 UI 契约。
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
5. WBall 迁移到 AppShell 0.7.2 的统一工具窗口契约，保留现有工作流和布局可恢复性。
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
| V34-06 | P1 | AppShell 0.5.0 契约落后于正式 0.7.2 | 完成工具窗口迁移，删除 `MainContent/page.*` 旧依赖 |
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

### 4.5 AppShell 0.7.2 迁移（V34-06）

1. 先锁定 AppShell 0.7.2 四包版本和本地 feed 来源，再进行 WBall 重编译。
2. 将主舞台、编辑器、对战设置、配平设置、独立出片页统一注册为工具窗口；不再依赖 `ShellConfig.MainContent`、固定首页或 `page.*`。
3. 保留窗口 ID、布局保存/恢复、命令总线和已有用户数据迁移；旧布局中的无效文档必须安全丢弃。
4. 迁移期间同时保留 v3.3 的命令名称和 `record.*` 兼容别名。
5. 迁移完成后，仓库中不得出现 `IShellUiRegistrar` 之外的旧页面注册契约引用。

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
5. OHS `git.rule.scan` 的未决数量必须为 0；二进制只按项目规则跟踪或 LFS，生成物必须忽略。

---

## 5. 实施批次与提交边界

### 批次 A：先止血（建议独立提交）

实施 V34-01、V34-02、V34-03、V34-04、V34-08 的基础文件。此批不得改变战斗公式和画面结果。

验收：干净 checkout 构建；验证器成功清理；事件和帧内存有上限；v3.3 哈希不变。

### 批次 B：降低修改放大

实施 V34-05，并把现有 `balance.*`、两个设置窗和预设/剧本持久化迁移到描述符驱动。

验收：新增一个临时测试字段只需改描述符和行为映射；现有所有字段的默认值、范围、JSON 和命令结果不变。

### 批次 C：升级 Shell 契约

实施 V34-06，先完成 0.7.2 工具窗口迁移，再拆 `Core/Application/Desktop`。

验收：布局恢复、窗口显示/隐藏/浮动/停靠、MCP 命令、出片页和 v3.3 回归全部通过。

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
| AppShell | 0.7.2 工具窗口契约通过，旧 `MainContent/page.*` 依赖为 0 |
| 分层 | `WBall.Core` 不引用 `System.Windows`、AppShell 或 Media Foundation |
| OHS 规则 | `git.rule.scan` 未决文件为 0，生成目录有明确 ignore 规则 |

---

## 7. 风险与回滚

| 风险 | 控制方式 |
|---|---|
| 删除旧录制入口影响脚本 | 先保留 `record.*` 命令兼容层，只替换内部实现 |
| 0.7.2 是破坏性 AppShell 升级 | 单独批次、单独提交；保留 0.5.0 回滚提交和布局备份 |
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
6. AppShell 0.7.2 迁移记录、布局兼容说明和回滚点。
7. V3.4 验收日志、哈希、构建和内存证据。

---

## 9. 完成定义

只有同时满足以下条件，才能将版本标记为 v3.4.0：

- 所有 P0/P1 项通过，且没有新增生成物、临时目录泄漏或无界集合；
- v3.3 的规则、默认参数、确定性哈希和录制结果不变；
- AppShell 0.7.2 迁移、Core 分层和质量门禁均有可复现证据；
- README、版本文档、OHS 规则台账、提交范围和回滚点一致；
- 交付提交不包含 `bin/obj/.vs`，也不包含未声明的本地环境产物。

---

## 10. 交付记录（2026-07-28）

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
| （本提交） | V34-10 | `.gitattributes` 项目段：文本 `eol=lf`、CAD/EDA/Office 标 `binary`；新增 `b-Office/OHS_Git规则台账.md` |

### 10.2 证据

| 验收项 | 结果 |
|---|---|
| 行为零漂移 | v3.1 回退 `6381A389…`、v3.2 回退 `E24FD280…`、v3.3 seed=42 `5A458728…`、seed=43 `E3CC0CFA…` —— **改前/改后逐字一致** |
| 命令结果不变（批次 B 硬条件） | 9 条 `balance.*` 回显与重构前**逐字相同**（改前/改后日志比对） |
| clean build | 干净 `git clone` 后 Debug 构建 0 警告 0 错误，且 clone 不带任何生成物；Release 同样 0/0 |
| 相对 feed 可用 | 空包目录 `dotnet restore --packages` 从相对路径 feed 取到 AppShell（不靠全局缓存） |
| 格式 | `dotnet format --verify-no-changes` 通过 |
| 临时目录 | 通过运行输出 `ARTIFACTS cleaned`，目录数不增；`--keep-artifacts`/`--artifact-root` 按预期留存 |
| 长时内存 | 60s 1080p30：1800/1800 帧完成；峰值 158.56MiB → 168.11MiB（delta 9.54MiB，非时长线性）；UI 派发器 maxGap 79.9ms |
| 出片链路 | 渲染 smoke、出片页 300px smoke、`win.show/float/dock name=stage`、`record.*` 兼容别名全通过 |
| 旧临时产物 | 经用户授权，删除 46 个 `wball_verify_v32_*` 目录，回收 **3.18 GB**（只删该前缀） |

### 10.3 未做项与原因

| 项 | 状态 | 原因 / 下一步 |
|---|---|---|
| V34-07 分层（Core/Application/Desktop） | **未做** | 60 个源文件的跨工程搬迁，须按 §4.6 "从依赖叶子开始、每批一个可回滚点、每批跑哈希回归"推进；与 V34-09 同批做才有意义。建议下一轮单独开工，第一步只抽 `WBall.Core`（Model/Sim/Battle 纯逻辑，不含 WPF） |
| V34-09 热点拆分 | **未做** | `BattleRuntime`(1611) / `WBallCommands`(1256) / `PropertyProjection`(1179) / `DropZoneView`(1107) / 验证器(1057+)。拆分本身不改行为，但每拆一处都要重跑哈希与 smoke，属独立批次 |
| V34-10 的 `git.rule.scan` 归零 | **仓库侧完成，扫描待 OHS 侧执行** | 扫描器属 2026-020 工具链，不在 022 工作树内；台账 §3 已写明口径 |
| `Unused/b-EPLAN/N0000.edb`（248 文件 / 68 MB） | **待拍板** | 索引里的历史锁/备份文件，OHS 忽略规则对已跟踪文件无效；三种处置见台账 §2，未经授权不动 |答复：我已经删除
| `AppVersion` 升 3.4.0 | **未升**（仍 3.3.0） | §9 要求"全部 P0/P1 通过"才标 v3.4.0；V34-07 未完成前不改版本号，避免版本号说谎 |

—— 交付记录结束 ——

