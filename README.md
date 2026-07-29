# WBall

WBall 是一个基于 .NET 8、WPF 与 OneHistory AppShell 的可配置落球对战模拟器。

左侧落球区负责产生阵营经济，结算结果会转换为弹药、护盾与火力；右侧对战区以固定时间步进模拟炮台、弹体、领地和胜负。程序同时提供命令控制台、停靠式设置窗口、场景编辑、剧本保存和结果导向离线出片能力。

![WBall Logo](./Logo.png)

## 当前状态

- 当前应用版本：`3.4.0`
- 当前代码基线：v3.2.1「离线出片与时间模块」和 v3.3「同阵营积分传递与升格弹回收」已交付
- v3.4「工程卫生与质量修复」已完成：核心逻辑与 WPF/AppShell 分层，构建、格式、确定性、出片和页面门禁均已通过
- v3.1 回退兼容：关闭三项 v3.2 默认玩法变更后，留档哈希逐字一致
- 平台：Windows、`.NET 8`、WPF
- 框架：`OneHistory.AppShell.Shell 3.0.0`（冻结契约，权威项目 `2026-023-AppShell`）
- MCP：WBall 默认显式关闭；模块托管保持开启

v3.2 在 v3.1 的 `arena_layout.json` 规模配置之上新增独立的 `battle_balance.json`，把射速、升格、对消、护罩、余烬、经济映射、物理弹性和回合收敛参数开放为 `balance.*` 命令与「战斗平衡」设置窗。`balance.sim` 可在隔离实例中按多个种子试跑，不写配置、不改变当前战局；预设档只携带 arena + balance，内置 `standard`、`rush`、`marathon`。详细规格与验收数据见 [`b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md`](./b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md)。

v3.2.1 将出片改为冻结输入后的生产者/消费者任务：MTA 模拟线程使用独立双世界与导演生成不可变帧投影，经有界队列交给专用 STA 线程离屏组合并逐帧写 PNG/Media Foundation，不再重置现场或把整片 BGRA 留在内存。`TimelineClock` 区分输出、模拟和墙钟时间，并只按确定性球数曲线降速。v3.3 新增 `ProjectileRole`，标 2/4/8/64 的升格弹仍是小球；同阵营大球以默认 `0.25`/`0.10` 点每秒的共享预算吸收小球或接收较小大球积分，并以可关闭的弱连线和聚合 `+N` 提供反馈。

## 主要能力

- 左右双世界：落球经济世界与炮台对战世界相互联动
- 领地战与 direct 对照模式
- 大球、小球、齐射、直射、护盾等武器与弹药机制
- 固定 60 Hz 时间步进、任意输出 FPS 精确步进、种子复现和确定性哈希
- 球数驱动的确定性自动慢动作；预览和出片共用倍率公式
- 可配置对战区、战斗平衡、同阵营低速助力、炮台、武器库、HUD 和出片参数
- 隔离式多种子无头试跑，支持表格/CSV、墙钟超时与取消后部分结果
- `standard`、`rush`、`marathon` 内置数值预设和用户预设往返
- 场景、线框、异形实体及属性表编辑
- 剧本保存、读取与演示场景
- 命令总线：界面操作均有对应命令，控制台可独立驱动程序
- 独立世界离线出片、流式 MP4 与完整 PNG 降级
- 出片 manifest 内置弹丸价值账本、升格小球峰值/回收量和 BGRA/队列峰值，便于长局复核

## 项目结构

```text
b-Code-WBall/
  Core/                   无 WPF/AppShell 的模型、物理、时间轴与配置契约
  Application/            场景和属性文件用例
  App/                    WPF/AppShell 桌面组合根与出片适配
  WBall.sln               Visual Studio 解决方案
b-Code-Verify/
  WBallVerify/            确定性与整局模拟验证器
b-Office/                 需求、版本方案和开发约束
b-Picture/                图片素材
b-Video/                  视频素材
b-Publish/                发布产物
```

桌面层的重要模块：

```text
App/Battle/               战斗运行时、导演、经济桥、配置与剧本
App/Commands/             WBall 自定义命令
App/Presentation/         工作区与设置窗口
App/Stage/                舞台、对战区和 HUD
App/Recording/            独立出片任务与 Media Foundation 流式编码
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
win.show name=render
render.config
render.start mode=output seconds=60 seed=42 name=demo
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
- `workspace/records/`：出片任务、manifest、PNG 与 MP4
- `panels/`：控制面板配置
- `layout/`、`logs/`、`settings.json`：AppShell 布局、日志和设置

## 验证

```powershell
$env:DOTNET_EnableWriteXorExecute='0'
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Debug
```

验证器覆盖 v3.1/v3.2 回退、v3.3 同种子确定性、ProjectileRole、同阵营共享速率与积分守恒、升格梯度、超 512 队列、护盾、触杀、硬时限、剧本/预设、无头试跑，以及 `balance.*` / `preset.*` 命令烟测。`--render-smoke` 还验证双线程有界流水线、同输入抽样帧哈希、命名场景隔离、暂停/取消、PNG/MP4 降级和 1 万球自动降速/内存红线。

快速验证时间模块、独立出片、暂停/取消、PNG/MP4 降级和现场隔离：

```powershell
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Debug -- --render-smoke
```

完整验证 60 秒 1080p/30 FPS、UI Dispatcher 响应、长短片内存差和升格小球联合回收：

```powershell
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Debug -- --render-long-acceptance
```

专项性能与 300px 窄页布局：

```powershell
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Debug -- --assist-performance
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Debug -- --render-page-smoke
```

留档哈希：

```text
v3.1 rollback, seed=42 @60s: 6381A3898C0FAD65B57D43C140917A010713AA3015F601BACE14C7E5B88333F3
v3.2 rollback, seed=42 @60s: E24FD280C34B54F79DAFCAE466DE299B4B76F56B69D83EF63757B96F81BF9184
v3.3 default,  seed=42 @60s: 5A458728F1A2A4296B126E1EC2F50221EC3D212393125EDFAD62EFED12F8525B
v3.3 default,  seed=43 @60s: E3CC0CFA0E3B630DBB11372AC3F31F03DB031E144858E75D552FC1CC1C3656CA
```

## 开发约束

WBall 应用层可以自由扩展，但 AppShell 的指令语法、公共契约和停靠机制属于冻结区。开始修改前请阅读：

- [AppShell 3.0 复用合同](../2026-023-AppShell/z-Package-AppShell/AppShell.reuse.md)
- [AppShell 3.0 API 与指令手册](../2026-023-AppShell/z-Package-AppShell/docs/AppShell_API与指令手册.md)
- [AppShell 3.0 消费变更摘要](../2026-023-AppShell/z-Package-AppShell/docs/AppShell_3.0_消费变更摘要.md)
- [`b-Office/WBall_v3.1_对战区自定义与设置窗需求.md`](./b-Office/WBall_v3.1_对战区自定义与设置窗需求.md)
- [`b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md`](./b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md)
- [`b-Office/WBall_v3.2.1_结果导向离线出片与时间模块需求.md`](./b-Office/WBall_v3.2.1_结果导向离线出片与时间模块需求.md)
- [`b-Office/WBall_v3.3_同阵营积分传递与升格弹回收需求.md`](./b-Office/WBall_v3.3_同阵营积分传递与升格弹回收需求.md)
- [`b-Office/WBall_v3.4_工程卫生与质量修复需求.md`](./b-Office/WBall_v3.4_工程卫生与质量修复需求.md)

## 许可证

当前仓库未声明开源许可证。
