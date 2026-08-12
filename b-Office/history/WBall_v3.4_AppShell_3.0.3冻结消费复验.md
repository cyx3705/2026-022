# WBall v3.4 AppShell 3.0.3 冻结消费复验

> 日期：2026-07-29
> WBall：3.4.0
> AppShell：3.0.3 正式包
> 结论：通过，允许作为 AppShell 3.0 最终冻结消费证据

## 消费调整

- `OneHistory.AppShell.Shell` 从精确版本 `[3.0.0]` 提升到 `[3.0.3]`。
- 包源仍为相邻项目 `2026-023-AppShell/z-Package-AppShell/feed`，未引用 AppShell 源码。
- WBall 继续显式关闭 MCP 默认启动；中央舞台继续使用 `DockSide.Center`，未修改 AppShell 公共契约。

## 门禁结果

- 全新 NuGet 缓存从正式 Z feed 强制还原：通过。
- `dotnet list package --include-transitive`：Shell 请求 `[3.0.3]`、解析 `3.0.3`；Core/Services 均解析为 `3.0.3`。
- 隔离 `ArtifactsPath` Debug 构建：0 warning / 0 error。
- 隔离 `ArtifactsPath` Release 构建：0 warning / 0 error。
- `dotnet format --verify-no-changes`：通过。
- `WBallVerify`：`VERIFY PASS`，架构隔离、时间轴、平衡配置、预设、无头模拟和命令验证全部通过。
- v3.1 回滚哈希：`6381A3898C0FAD65B57D43C140917A010713AA3015F601BACE14C7E5B88333F3`。
- v3.2 回滚哈希：`E24FD280C34B54F79DAFCAE466DE299B4B76F56B69D83EF63757B96F81BF9184`。
- v3.3 seed=42：`5A458728F1A2A4296B126E1EC2F50221EC3D212393125EDFAD62EFED12F8525B`。
- v3.3 seed=43：`E3CC0CFA0E3B630DBB11372AC3F31F03DB031E144858E75D552FC1CC1C3656CA`。

## 环境说明

首次使用默认 Debug 输出目录构建时，正在运行的用户 WBall 进程锁定了输出 DLL，导致复制门禁失败；未终止用户进程。
随后使用全新的 NuGet 缓存和隔离 `ArtifactsPath` 重跑 Debug/Release，两个配置均为 0 warning / 0 error。
该问题属于本机输出目录占用，不是 3.0.3 编译或运行契约失败。

## 结论

WBall 已从 AppShell 最终正式入口独立还原并通过全量非交互门禁。AppShell 3.0.3 未改变 WBall 的业务结果、
确定性哈希、分层边界或 MCP 默认关闭行为，可以纳入 AppShell 3.0 最终冻结证据。
