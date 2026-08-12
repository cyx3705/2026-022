# WBall

WBall 是一个基于 .NET 8、WPF 与 OneHistory AppShell 的可配置落球对战模拟器。

左侧落球区负责产生阵营经济，结算结果会转换为弹药、护盾与火力；右侧对战区以固定时间步进模拟炮台、弹体、领地和胜负。程序同时提供命令控制台、停靠式设置窗口、场景编辑、剧本保存和结果导向离线出片能力。

![WBall Logo](./Logo.png)

## 当前状态

- 当前应用版本：`3.6.0`
- 当前代码基线：v3.2.1「离线出片与时间模块」和 v3.3「同阵营积分传递与升格弹回收」已交付
- v3.4「工程卫生与质量修复」已完成：核心逻辑与 WPF/AppShell 分层，构建、格式、确定性、出片和页面门禁均已通过
- v3.5「开发敏捷化与场景调试」已完成：新增 Core/Application 快速验证，编辑写入统一走 CommandBus，并以默认右侧的「场景调试」窗口整合对象、小球与公式、裁判功能
- v3.5.2 以光环/帧间扫掠接触修复友军吸收：大球接触同阵营小球时，小球当帧回收且全部数值立即等量增加到大球；小球之间永不吸收；大球互吸仍按低速预算执行。球碰撞默认关闭，手动开启后所有友军吸收停止。护盾增长不再受旧 `MaxShield` 封顶，炮台外圈显示本方护盾占四方当前护盾总值的比例
- v3.6 将出片收敛为 winner-only：冻结场景计算到唯一胜者，追加固定 3 秒胜利动画，并由随应用发布的 FFmpeg 8.0.1 通过 BGRA 标准输入直接生成 H.264 MP4；不再公开时长、模式或 PNG 降级入口
- 本次碰撞/吸收互斥规则属于确定性玩法升级，v3.1/v3.2 回退配置与当前默认哈希已登记新基线
- 平台：Windows、`.NET 8`、WPF
- 框架：`OneHistory.AppShell.Shell 3.0.3`（最终冻结契约，权威项目 `2026-023-AppShell`）
- MCP：WBall 默认显式关闭；模块托管保持开启

v3.2 在 v3.1 的 `arena_layout.json` 规模配置之上新增独立的 `battle_balance.json`，把射速、升格、对消、护罩、余烬、经济映射、物理弹性和回合收敛参数开放为 `balance.*` 命令与「战斗平衡」设置窗。`balance.sim` 可在隔离实例中按多个种子试跑，不写配置、不改变当前战局；预设档只携带 arena + balance，内置 `standard`、`rush`、`marathon`。当前有效规则见 [`b-Office/current/技术合同.md`](./b-Office/current/技术合同.md)，旧版本需求只用于按需追溯。

v3.2.1 建立了冻结输入后的生产者/消费者出片任务；v3.6 在此基础上移除定长 PNG/Media Foundation 路径，改由独立双世界持续计算到唯一胜者，经有界队列把不可变帧投影交给专用 STA 线程组合，再把 BGRA 帧直接流入固定 FFmpeg。`TimelineClock` 区分视频、模拟和墙钟时间，并只按确定性球数曲线降速。v3.3 新增 `ProjectileRole`，标 2/4/8/64 的升格弹仍是小球；当前规则下同阵营大球即时等值吸收小球，较大大球以低速预算接收较小大球积分，并以可关闭的弱连线和聚合 `+N` 提供反馈。

## 主要能力

- 左右双世界：落球经济世界与炮台对战世界相互联动
- 领地战与 direct 对照模式
- 大球、小球、齐射、直射、护盾等武器与弹药机制
- 固定 60 Hz 时间步进、任意输出 FPS 精确步进、种子复现和确定性哈希
- 球数驱动的确定性自动慢动作；预览和出片共用倍率公式
- 可配置对战区、战斗平衡、同阵营吸收与低速大球助力、炮台、武器库、HUD 和出片参数
- 隔离式多种子无头试跑，支持表格/CSV、墙钟超时与取消后部分结果
- `standard`、`rush`、`marathon` 内置数值预设和用户预设往返
- 场景、线框、异形实体及属性表编辑
- 剧本保存、读取与演示场景
- 命令总线：界面操作均有对应命令，控制台可独立驱动程序
- 独立世界 winner-only 离线出片、固定 3 秒胜利动画与 H.264 MP4-only 交付
- 出片 manifest 记录 FFmpeg 身份、可战价值、淘汰时刻、胜者、动画帧范围、最终哈希和 BGRA/队列峰值

## 项目结构

```text
b-Code-WBall/
  Core/                   无 WPF/AppShell 的模型、物理、时间轴与配置契约
  Application/            场景和属性文件用例
  App/                    WPF/AppShell 桌面组合根与出片适配
  WBall.sln               Visual Studio 解决方案
b-Code-Verify/
  WBallFastVerify/        纯 Core/Application 的快速验证器
  WBallVerify/            确定性与整局模拟验证器
b-Office/                 现行合同、稳定规则与冻结历史
b-Picture/                图片素材
b-Video/                  视频素材
b-Publish/                单槽开发测试候选与发布事务区
z-Package/                最新正式可消费快照
```

桌面层的重要模块：

```text
App/Battle/               战斗运行时、导演、经济桥、配置与剧本
App/Commands/             WBall 自定义命令
App/Presentation/         工作区与设置窗口
App/Stage/                舞台、对战区和 HUD
App/Recording/            独立出片任务、胜利帧组合与 FFmpeg 流式编码
```

`WBall.Core` 单独以 `net8.0` 构建；验证器会检查其程序集引用，禁止重新引入 WPF、AppShell 或 Media Foundation。

## 构建

本机环境需要关闭 Roslyn 的 Write XOR Execute 路径：

```powershell
$env:DOTNET_EnableWriteXorExecute='0'
dotnet build .\b-Code-WBall\WBall.sln -c Debug
```

构建产物位于：

```text
b-Code-WBall/App/bin/Debug/net8.0-windows/WBall.exe
```

## 开发测试候选

`b-Publish/candidate/` 保存最近一次通过指定验证套件的本地 Debug 测试候选。它使用与 OHS 相同的单槽和事务回滚规则，但不属于正式发布包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-WBall\eng\Test-Deploy-WBall.ps1 -Suite Fast
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-WBall\eng\Test-Deploy-WBall.ps1 -Suite Fast,Full
```

友军小球回收门禁：

```powershell
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Release -- --friendly-absorb-smoke
```

当前配置开战与护盾槽门禁：

```powershell
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Release -- --gameplay-fixes
```

候选内的 `development-verification.json` 记录源 Commit、脏工作树状态、验证套件、版本和 AppShell 版本。完整规则见 [`b-Publish/README.md`](./b-Publish/README.md)。

正式发布必须从已提交且洁净的源码生成，并通过 Debug/Release、Fast、Full、manifest/checksum 二次复验：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-WBall\eng\Publish-WBall.ps1 -Publish
```

正式可消费快照只位于 `z-Package/`；脚本不会自动提交、推送或修改 `%AppData%/WBall/`。

## 运行

直接启动：

```powershell
.\b-Code-WBall\App\bin\Debug\net8.0-windows\WBall.exe
```

启动时执行命令：

```powershell
.\b-Code-WBall\App\bin\Debug\net8.0-windows\WBall.exe `
  --exec "demo.play seed=42" `
  --exec "arena.status"
```

常用入口：

```text
help
demo.play seed=42
battle.status
arena.config
win.show name=arenaset
balance.config
balance.sim seeds=42..49 seconds=180 config=current format=table
preset.list
win.show name=balance
balance.assist
win.show name=scenedebug
win.show name=render
render.config
render.start seed=42 name=demo
render.status
```

## 数据目录

运行数据默认保存在 `%AppData%/WBall/`，主要包括：

- `arena_layout.json`：对战区规模及弹体映射
- `battle_balance.json`：战斗平衡与回合参数
- `render_time.json`：出片规格、自动降速曲线与手动倍率
- `turrets.json`：炮台定义
- `weapons.json`：武器库
- `presets/`：内置及用户数值预设
- `workspace/scenes/`：场景文件
- `workspace/scenarios/`：对战剧本
- `workspace/records/`：历史出片结果；v3.6 新任务只生成 manifest 与 MP4
- `panels/`：控制面板配置
- `layout/`、`logs/`、`settings.json`：AppShell 布局、日志和设置

## 验证

日常开发先运行不加载 WPF/AppShell 的 Fast 验证：

```powershell
dotnet run --project .\b-Code-Verify\WBallFastVerify\WBallFastVerify.csproj -c Release
```

合并与版本收口再运行完整桌面回归：

```powershell
$env:DOTNET_EnableWriteXorExecute='0'
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Release
```

验证器覆盖 v3.1/v3.2 回退、v3.5.2 当前玩法哈希、ProjectileRole、同阵营积分守恒、升格梯度、超 512 队列、当前配置开战、护盾槽积分换算、剧本/预设、无头试跑，以及 `balance.*` / `preset.*` 命令烟测。`--render-smoke` 验证完整可战价值淘汰、winner-only 接口、3 秒胜利动画、四档分辨率 H.264 MP4、暂停/取消、编码故障事务、确定性和现场隔离。

验证 winner-only、独立出片、暂停/取消、MP4-only 故障事务和现场隔离：

```powershell
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Release -- --render-smoke
```

专项性能与 300px 窄页布局：

```powershell
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Debug -- --assist-performance
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Debug -- --render-page-smoke
```

留档哈希：

```text
v3.1 rollback config, seed=42 @60s: 7231013A2B055BF00CA51012343A071055178F697C947792A1A7BFA96254DD65
v3.2 rollback config, seed=42 @60s: AAD5428D2F251BE5F31451B4B94971D2ADBBC8B1F6979340CCA2D2CB058C1D74
v3.3 default,  seed=42 @60s: 5A458728F1A2A4296B126E1EC2F50221EC3D212393125EDFAD62EFED12F8525B
v3.3 default,  seed=43 @60s: E3CC0CFA0E3B630DBB11372AC3F31F03DB031E144858E75D552FC1CC1C3656CA
v3.5.2 default, seed=42 @60s: D87DCBA51531D804F86D913506324A485FC8C0B4929909A3FB878F033329D3CA
v3.5.2 default, seed=43 @60s: 43FC8561CF7AABA35D33EC93DB0DCC3165A3C39F1B1697EA370B0AEFB3734B78
```

## 开发约束

WBall 应用层可以自由扩展，但 AppShell 的指令语法、公共契约和停靠机制属于冻结区。项目文档采用“现行合同 / 稳定规则 / 冻结历史”三层结构，开始修改前按任务读取：

- [WBall 文档中心](./b-Office/文档中心.md)
- [项目概览](./b-Office/current/项目概览.md)
- [技术合同](./b-Office/current/技术合同.md)
- [验证合同](./b-Office/current/验证合同.md)
- [文档包复用说明](./b-Office/package/复用说明.md)
- [目录规范](./b-Office/package/目录规范.md)

- [AppShell 3.0 复用合同](../2026-023-AppShell/z-Package-AppShell/AppShell.reuse.md)
- [AppShell 3.0 API 与指令手册](../2026-023-AppShell/z-Package-AppShell/docs/AppShell_API与指令手册.md)
- [AppShell 3.0 消费变更摘要](../2026-023-AppShell/z-Package-AppShell/docs/AppShell_3.0_消费变更摘要.md)

`b-Office/history/` 保存旧版本需求与阶段记录，默认不进入开发上下文；需要追溯时只读取与问题直接相关的文件。

## 许可证

当前仓库未声明开源许可证。
