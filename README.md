# WBall

WBall 是一个基于 .NET 8、WPF 与 OneHistory AppShell 的可配置落球对战模拟器。

左侧落球区负责产生阵营经济，结算结果会转换为弹药、护盾与火力；右侧对战区以固定时间步进模拟炮台、弹体、领地和胜负。程序同时提供命令控制台、停靠式设置窗口、场景编辑、剧本保存和视频录制能力。

![WBall Logo](./Logo.png)

## 当前状态

- 当前应用版本：`3.2.0`
- 当前代码基线：v3.2「战斗平衡自定义、无头试跑与预设档」已交付
- v3.1 回退兼容：关闭三项 v3.2 默认玩法变更后，留档哈希逐字一致
- 平台：Windows、`.NET 8`、WPF
- 框架：`OneHistory.AppShell.Shell 0.5.0`

v3.2 在 v3.1 的 `arena_layout.json` 规模配置之上新增独立的 `battle_balance.json`，把射速、升格、对消、护罩、余烬、经济映射、物理弹性和回合收敛参数开放为 `balance.*` 命令与「战斗平衡」设置窗。`balance.sim` 可在隔离实例中按多个种子试跑，不写配置、不改变当前战局；预设档只携带 arena + balance，内置 `standard`、`rush`、`marathon`。详细规格与验收数据见 [`b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md`](./b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md)。

## 主要能力

- 左右双世界：落球经济世界与炮台对战世界相互联动
- 领地战与 direct 对照模式
- 大球、小球、齐射、直射、护盾等武器与弹药机制
- 固定 60 Hz 时间步进、种子复现和确定性哈希
- 可配置对战区、战斗平衡、炮台、武器库、HUD 和录制参数
- 隔离式多种子无头试跑，支持表格/CSV、墙钟超时与取消后部分结果
- `standard`、`rush`、`marathon` 内置数值预设和用户预设往返
- 场景、线框、异形实体及属性表编辑
- 剧本保存、读取与演示场景
- 命令总线：界面操作均有对应命令，控制台可独立驱动程序
- MP4 录制与 PNG 帧保留

## 项目结构

```text
b-Code-WBall/
  App/                    WBall 主程序
  WBall.sln               Visual Studio 解决方案
b-Code-Verify/
  WBallVerify/            确定性与整局模拟验证器
b-Office/                 需求、版本方案和开发约束
b-Picture/                图片素材
b-Video/                  视频素材
b-Publish/                发布产物
```

主程序的重要模块：

```text
App/Battle/               战斗运行时、导演、经济桥、配置与剧本
App/Commands/             WBall 自定义命令
App/Model/                场景、球体、线框和实体模型
App/Presentation/         工作区与设置窗口
App/Sim/                  物理引擎
App/Stage/                舞台、对战区和 HUD
App/Recording/            视频录制与 Media Foundation 编码
```

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
record.status
```

## 数据目录

运行数据默认保存在 `%AppData%/WBall/`，主要包括：

- `arena_layout.json`：对战区规模及弹体映射
- `battle_balance.json`：战斗平衡与回合参数
- `turrets.json`：炮台定义
- `weapons.json`：武器库
- `presets/`：内置及用户数值预设
- `workspace/scenes/`：场景文件
- `workspace/scenarios/`：对战剧本
- `panels/`：控制面板配置
- `layout/`、`logs/`、`settings.json`：AppShell 布局、日志和设置

## 验证

```powershell
$env:DOTNET_EnableWriteXorExecute='0'
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Debug
```

验证器覆盖 v3.1 回退、v3.2 同种子确定性、升格梯度、超 512 队列与增量总值、禁用护盾再生、巨球触杀、硬时限、剧本/预设往返、无头试跑隔离与取消部分结果，以及 `balance.*` / `preset.*` 命令烟测。

留档哈希：

```text
v3.1 rollback, seed=42 @60s: 6381A3898C0FAD65B57D43C140917A010713AA3015F601BACE14C7E5B88333F3
v3.2 default,  seed=42 @60s: E24FD280C34B54F79DAFCAE466DE299B4B76F56B69D83EF63757B96F81BF9184
v3.2 default,  seed=43 @60s: 436DAEA13BE0430B1C2513DC0D22DB04047352E14711673BC26451490A071554
```

## 开发约束

WBall 应用层可以自由扩展，但 AppShell 的指令语法、公共契约和停靠机制属于冻结区。开始修改前请阅读：

- [`b-Office/二次开发演进手册.md`](./b-Office/二次开发演进手册.md)
- [`b-Office/WBall_v3.1_对战区自定义与设置窗需求.md`](./b-Office/WBall_v3.1_对战区自定义与设置窗需求.md)
- [`b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md`](./b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md)

## 许可证

当前仓库未声明开源许可证。
