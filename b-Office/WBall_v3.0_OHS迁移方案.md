# WBall · v3.0 迁移方案 —— 转正入 OHS 谱系（2026-022-WBall）

> 文档性质：**迁移决策文档**（先评审、后动工；本文档批准前不做任何迁移动作）。
> 日期：2026-07-26 ｜ 目标版本：**3.0.0** ｜ 功能基线：**v2.12.5**（功能冻结,迁移不夹带功能改动）
> 现址：`C:\Users\Administrator\Desktop\WBall` ｜ 新址：`C:\OneHistory\OneHistory-Projects\2026-022-WBall`

---

## 0. 一句话定义

**v3.0 = 身份与依赖的正规化，不是功能版本**：WBall 从桌面散装项目转正为 OHS 管理的 **2026-022-WBall**，目录结构对齐 020 号项目命名规范（b 级文件夹自 Unused 剪切启用），并把随身携带的 AppShell 源码替换为 **已发布的 AppShell 0.5.0 NuGet 包**（`2026-020-OneHistoryStudio\z-Package-AppShell\feed`）。

## 1. 谱系定位（沿用已确认结论）

- 继承头：**0000-000-Template**（0 号模板），**不是** 020 —— WBall 是独立产品项目，020 的"继承"表达 Git 项目血缘而非依赖关系；
- 与 020 的关系仅是**包消费者**：引用其 z-Package-AppShell 发布的固定版本包，不引用其源码、不挂其子目录；
- 022 号位已建（当前为空目录）。

## 2. 目标目录结构（命名规则对齐 020）

```
2026-022-WBall/
├─ b-Code-WBall/            ← 源码区(现 src/App + 解决方案),命名比照 020 的 b-Code-AppShell
│   ├─ WBall.sln            ← 新建单工程解决方案(替代 APPShell.sln 四工程)
│   ├─ nuget.config         ← nuget.org + AppShell 本地 feed
│   └─ App/                 ← 现 src/App 原样迁入(WBall.csproj)
├─ b-Office/                ← 全部需求文档(现 docs/ 12 份 v1.3~v2.12.5 + 演进手册)
├─ b-Publish/               ← 发布产物位(录制出片脚本/发布包)
├─ b-Video/                 ← 视频成品位(产品即视频生成器,成片归档)
├─ b-Picture/               ← 截图/封面素材
├─ Unused/                  ← 其余备用 b 级文件夹(Altium/EPLAN/KeyShot/Module-*/Unity/References…)
├─ .gitattributes / .gitignore / Logo.png / README.md   ← 模板保留件
```

**b 级文件夹获取规则**（用户指定）：从 0000-000-Template 继承体系的 `Unused` 中**剪切**启用——`b-Office`、`b-Code`(启用后更名 `b-Code-WBall`)、`b-Publish`、`b-Picture`、`b-Video` 五个移到项目根;其余留在 `Unused` 备用。

**不迁移**的现址内容：`bin/obj`（构建产物）、`.vs`、空的 `.git` 目录（无效仓库）、`workspace`（运行时工作区,由 %AppData% 路径体系在新址首启时自建）。

## 3. 核心改动：AppShell 依赖正规化

### 3.1 现状 → 目标

| 项 | 现状(Desktop\WBall) | 目标(v3.0) |
|---|---|---|
| AppShell | 随身源码 4 工程(Core/Services/Shell + App),APPShell.sln 联编 | **删除三个 AppShell 源码工程**,仅存 App 工程 |
| 引用方式 | ProjectReference | `<PackageReference Include="OneHistory.AppShell.Shell" Version="0.5.0" />`(Core/Services 走传递依赖) |
| 包源 | nuget.org | nuget.org + 本地 feed 绝对路径:`C:\OneHistory\OneHistory-Projects\2026-020-OneHistoryStudio\z-Package-AppShell\feed` |
| 包完整性 | — | 按 `manifest\0.5.0.json` SHA-256 校验(net8.0/net8.0-windows,SDK 9.0.315) |

### 3.2 最大风险：API 对账（M2 必须先 spike）

WBall 随身的 AppShell 源码是早期模板复制件，与发布版 0.5.0 之间**可能存在 API 漂移**（ShellConfig 字段、ToolWindowDescriptor、面板 JSON 机制、CommandDescriptor 签名、AppPaths/ShellLog 构造等）。迁移动工前必须做**编译对账 spike**：拷贝 App 工程 → 替换为包引用 → 全量编译 → 列出全部不兼容点。结果三种走向：

- ✅ 零漂移：直接迁；
- ⚠️ 小漂移：App 侧适配（自由区改法,逐条记录进迁移文档附录）；
- ⛔ 大漂移（0.5.0 缺 WBall 必需能力）：**停手上报**，拍板"A. 降级适配 / B. 等 AppShell 发新版 / C. 暂缓迁移"。

### 3.3 构建注意

- 沿用 `DOTNET_EnableWriteXorExecute=0`（CET 环境变量,本机血泪）；
- App.csproj 的 `AppVersion` 升 **3.0.0**；场景 format5、%AppData%\WBall 配置、剧本/录制路径**零改动**（老用户数据即插即用）。

## 4. Git 与 OHS 注册

1. **血缘**：以 0000-000-Template 为基建立 022 仓库（模板 `.git` 谱系复制），继承头保持 0 号模板；
2. **注册**：迁移完成后经 OHS `proj.scan` / `proj.create` 将 2026-022-WBall 纳入项目管理，`proj.commit` 做首次版本留痕；
3. 提交纪律：迁移期一次结构提交 + 一次依赖改造提交，不混合。

## 5. 验证清单（迁移即验收）

1. `dotnet build` 零警告零错误（包引用还原自本地 feed，哈希与 manifest 一致）；
2. 无头验证 harness（WBallVerify 指向新 bin）全绿：确定性哈希、整局收敛出唯一胜者、巨球斩满盾台定向测试；
3. 实机 `WBall.exe --exec "demo.play seed=42"`：冷启动 Plinko 落球盘 + 领地战完整可玩，与 v2.12.5 行为一致；
4. 面板/布局/场景/剧本等 %AppData% 旧数据无损兼容；
5. 版本号 3.0.0；`docs`→`b-Office` 文档齐套（含本文件）。

## 6. 迁移步骤（批准后执行）

```
M0 本文档评审拍板（含 §7 问题）
 → M1 模板落位:Template 谱系复制到 022;Unused 剪切启用 b-Office/b-Code-WBall/b-Publish/b-Picture/b-Video
 → M2 ★API 对账 spike★:包引用替换 + 全量编译,漂移清单出炉(大漂移即停手上报)
 → M3 代码迁入:src/App → b-Code-WBall/App;新 WBall.sln;nuget.config;版本 3.0.0
 → M4 验证清单 §5 全绿
 → M5 OHS 注册 + 首次提交;docs 全量迁入 b-Office
 → M6 旧 Desktop\WBall 封存(只读保留,验收一周后再议删除)
```

## 7. 待拍板问题

| # | 问题 | 建议默认 |
|---|---|---|
| Q1 | 源码区命名 `b-Code-WBall`? | ✅ 比照 020 的 b-Code-AppShell 后缀式 |对
| Q2 | 0.5.0 API 有漂移时的适配策略 | App 侧适配优先;缺能力则停手上报（§3.2） |适配优先
| Q3 | 旧 Desktop\WBall 处置 | 验收后只读封存,不立即删除（回滚保障） |先验收，后期准备删除
| Q4 | b-Video 是否启用 | ✅ 建议启用（产品即视频生成器,成片需归档位） |启用
| Q5 | WBallVerify 无头验证工程去向 | 建议一并转正入 `b-Code-WBall/Verify`（现躺在临时 scratchpad,重建成本低但有验证资产价值） |没有规定只有一个b-code文件夹，你可以新建

---

**批准本文档后回复"开冲迁移"即执行 M1~M6；对任一项有异议请直接改本文件或口头拍板。**

—— 文档结束 ——
