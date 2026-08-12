# WBall 本地测试候选区

`b-Publish/` 参考 OneHistoryStudio 的单槽候选规则，保存最近一次通过指定验证套件的本地 Debug 测试版本。这里不是正式发布仓，也不是源码真值；除本说明外，所有内容均为可重建本机产物并由 Git 忽略。

## 目录

| 目录 | 用途 | 生命周期 |
| --- | --- | --- |
| `candidate/` | 最近一次成功的开发测试候选，包含 `WBall.exe` 和验证报告 | 新候选原子替换旧候选；旧测试候选不进历史 |
| `history/` | 预留给未来正式发布历史 | 普通测试部署不得写入 |
| `work/` | 构建、复制和回滚事务的同卷临时区 | 每次运行前清理；成功后删除 |
| `quarantine/` | 保存失败构建或失败候选，便于诊断 | 下一次测试运行前清理；成功运行后删除 |

`current/`、`verification/` 和仓库根 `stage/` 均不是现行槽位，不得重新建立。

正式发布采用第二层根目录 `z-Package/`。它只保留最近一次通过 Debug/Release 构建、Fast、Full、Release publish、manifest 和 checksum 二次复验的正式可消费快照；不得从 `candidate/` 手工复制或把普通 Debug 候选改名为正式包。

## 生成测试候选

日常快速候选：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-WBall\eng\Test-Deploy-WBall.ps1 -Suite Fast
```

合并前候选：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-WBall\eng\Test-Deploy-WBall.ps1 -Suite Fast,Full
```

可选套件：`Fast`、`Full`、`RenderSmoke`、`PageSmoke`、`AssistFixes`、`FriendlyAbsorbSmoke`、`GameplayFixes`、`AssistPerformance`。其中 `FriendlyAbsorbSmoke` 持续向友军大球光环注入小球，硬性检查实体峰值、即时等值增长、零待吸收值、小球不互吸及碰撞/吸收互斥；`GameplayFixes` 检查当前配置开战不回退以及护盾槽积分真正转换为可抵消护盾。脚本只构建并投放 Debug 测试候选，不创建正式 Release 包，不提交 Git，也不修改 `%AppData%/WBall/` 用户数据。

## 候选合同

成功候选必须至少包含：

- `WBall.exe`；
- `AppShell.Shell.dll` 3.0.3；
- `development-verification.json`。

验证报告记录产品版本、源 Commit、工作树是否脏、执行套件、SDK、运行时、AppShell 版本、可执行文件 SHA-256 和 UTC 时间。`sourceDirty=true` 的候选允许用于本地测试，但不能被解释为正式发布证据。

候选替换使用同卷目录事务：先验证新候选，再把旧候选移动到 `work/` 作为回滚备份，提升新候选并二次验证；任何一步失败都隔离失败内容并恢复旧候选。

## 正式发布

正式提升要求源码与现行合同已提交且相关工作树洁净：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-WBall\eng\Publish-WBall.ps1 -Publish
```

脚本先生成并复验 Release `candidate/`，再原子提升到 `z-Package/`。正式包包含 `release/<version>.json` 与 `release/<version>.sha256`，逐文件记录相对路径、字节数和 SHA-256。旧正式包只有在新包复验成功后才移入 `b-Publish/history/package-*`；脚本不执行 Git 提交、推送、程序重启或用户数据迁移。
