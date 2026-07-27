# WBall

WBall 是一个基于 .NET 8、WPF 与 OneHistory AppShell 的可配置落球对战模拟器。

左侧落球区负责产生阵营经济，结算结果会转换为弹药、护盾与火力；右侧对战区以固定时间步进模拟炮台、弹体、领地和胜负。程序同时提供命令控制台、停靠式设置窗口、场景编辑、剧本保存和视频录制能力。

![WBall Logo](./Logo.png)

## 当前状态

- 当前应用版本：`3.1.0`
- 当前代码基线：v3.1「对战区自定义与设置窗」已交付
- 下一目标：v3.2「战斗平衡自定义、无头试跑与预设档」待开工
- 平台：Windows、`.NET 8`、WPF
- 框架：`OneHistory.AppShell.Shell 0.5.0`

v3.1 已将对战区规模、网格、炮塔位置、护罩、弹体映射和初始弹药等参数迁入 `arena_layout.json`，并提供 `arena.*` 命令族与「对战区」设置窗。

v3.2 计划增加独立的 `battle_balance.json`、`balance.*` 命令族、「战斗平衡」窗、隔离当前战局的多种子无头试跑，以及 `standard`、`rush`、`marathon` 数值预设。详细规格见 [`b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md`](./b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md)。

## 主要能力

- 左右双世界：落球经济世界与炮台对战世界相互联动
- 领地战与 direct 对照模式
- 大球、小球、齐射、直射、护盾等武器与弹药机制
- 固定 60 Hz 时间步进、种子复现和确定性哈希
- 可配置对战区、炮台、武器库、HUD 和录制参数
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
record.status
```

## 数据目录

运行数据默认保存在 `%AppData%/WBall/`，主要包括：

- `arena_layout.json`：对战区规模及弹体映射
- `turrets.json`：炮台定义
- `weapons.json`：武器库
- `workspace/scenes/`：场景文件
- `workspace/scenarios/`：对战剧本
- `panels/`：控制面板配置
- `layout/`、`logs/`、`settings.json`：AppShell 布局、日志和设置

## 验证

```powershell
$env:DOTNET_EnableWriteXorExecute='0'
dotnet run --project .\b-Code-Verify\WBallVerify\WBallVerify.csproj -c Debug
```

验证器覆盖同种子确定性、不同种子差异、领地变化、整局收敛和巨球触杀等行为。v3.1 的 `seed=42 @60s` 留档哈希为：

```text
6381A3898C0FAD65B57D43C140917A010713AA3015F601BACE14C7E5B88333F3
```

## 开发约束

WBall 应用层可以自由扩展，但 AppShell 的指令语法、公共契约和停靠机制属于冻结区。开始修改前请阅读：

- [`b-Office/二次开发演进手册.md`](./b-Office/二次开发演进手册.md)
- [`b-Office/WBall_v3.1_对战区自定义与设置窗需求.md`](./b-Office/WBall_v3.1_对战区自定义与设置窗需求.md)
- [`b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md`](./b-Office/WBall_v3.2_战斗平衡自定义与无头试跑需求.md)

## 许可证

当前仓库未声明开源许可证。
