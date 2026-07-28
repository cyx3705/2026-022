# 2026-022-WBall · OHS Git 规则台账

> v3.4 V34-10 交付物。日期：2026-07-28
> 目的：仓库里**每一种实际存在的格式**都有明确处置（track / ignore / LFS / binary），不留"未决"。
> 依据：`git ls-files` 的真实清单，不是猜测。

## 1. 处置总表

| 格式 | 数量 | 位置 | 处置 | 规则来源 |
|---|---:|---|---|---|
| `.cs` | 61 | `b-Code-WBall`、`b-Code-Verify` | track（`text eol=lf`） | v3.4 项目段 |
| `.md` | 11 | `b-Office`、各 README | track（`text eol=lf`） | v3.4 项目段 |
| `.csproj` / `.sln` / `.props` / `.config` / `.xml` / `.yml` / `.manifest` / `.xaml` | 各 1~2 | 根、工程目录 | track（`text`，`.sln` 用 `eol=crlf`） | v3.4 项目段 |
| `bin/` `obj/` `.vs/` | 原 185 个 | 两个工程 | **ignore + 已从索引移除** | v3.4 V34-01 |
| `.png` / `.jpg` / `.BMP` / `.Zip` | 4 | `Logo.png`、`b-Picture`、`Unused` | track（`binary`） | v3.4 项目段 |
| `.PcbDoc` `.PcbLib` `.SchDoc` `.SchLib` `.PrjPcb` `.PrjPcbStructure` `.SchDocPreview` | 6 | `Unused/b-Altium-Designer` | track（`binary`：禁 EOL 转换与文本 diff） | v3.4 项目段 |
| `.prt` `.par` `.asm` `.cfg` `.SLDPRT` `.SLDASM` `.dwg` `.mcam` | 8 | `Unused/b-Module-*` | track（`binary`） | v3.4 项目段 |
| `.xlsx` `.doc` `.pptx` | 3 | `Unused/b-Office` | track（`binary`） | v3.4 项目段 |
| `.mp4` `.mov` `.avi` `.pdf` `.stp` 等大媒体 | 0（暂无） | `b-Video`、`b-Publish` 预留 | **LFS** | OHS baseline/managed 段 |
| `.eod` `.eox` `.lck` `.slk` `.xlk` `.flk` `.elk` | **248（68 MB）** | 全在 `Unused/b-EPLAN/N0000.edb/**` | **待你拍板**，见 §2 | OHS baseline 已按扩展名 ignore |

## 2. 唯一未决项：`Unused/b-EPLAN/N0000.edb`（68 MB / 248 文件）

事实：

- OHS 基线 `.gitignore` 已经把 `*.eod *.eox *.lck *.slk *.xlk *.flk *.elk` 列为忽略；
- 但这 248 个文件是**在忽略规则之前就已入库**的，`.gitignore` 对已跟踪文件无效，所以它们仍在索引里；
- 它们全部位于 `Unused/`（模板继承下来的 EPLAN 空项目数据库），与 WBall 产品代码无关。

三种处置，需你选一个（v3.4 §2.2 规定不得擅自删除未授权内容，所以本期只登记不动手）：

| 方案 | 动作 | 结果 |
|---|---|---|
| A（建议） | `git rm -r --cached Unused/b-EPLAN/N0000.edb` | 索引瘦 68 MB，本机文件保留；与 OHS 自己的忽略规则一致 |
| B | 整个 `Unused/b-EPLAN` 保持跟踪，并在 `.gitattributes` 标 `binary` | 仓库继续背 68 MB，但 EPLAN 模板随仓库可复现 |
| C | 移出仓库另存（外部模板库） | 仓库最干净，但模板不再随项目走 |

## 3. `git.rule.scan` 实测与"未决为 0"能否达成

已通过 MCP 连上运行中的 OneHistoryStudio 2.7.3（`POST http://127.0.0.1:8737/mcp`，JSON-RPC）
实跑 `git_rule_scan` / `git_rule_gaps` / `git_rule_suggest` / `git_rule_list`。实测：

| 时点 | 覆盖率 | 未决文件 |
|---|---:|---:|
| 本轮开工前 | 75.3% | 137 |
| 文本格式改用规范行 `text eol=lf` 后 | **95.3%** | **26** |

### 3.1 扫描器只认两种规范行（这是上一版规则失效的原因）

`GitFileRuleService.ParseCanonicalAttribute` 用 `SetEquals` 精确比对属性集合，只接受：

- LFS：`filter=lfs diff=lfs merge=lfs -text`
- LF：`text eol=lf`

`text eol=crlf`、`-text`、`binary` 一律解析为 null → 该格式记为"未决"。所以规则要被扫描器认，
必须逐字使用上面两种写法（`.editorconfig` 的 `end_of_line` 必须同步为 `lf`，否则格式门禁会红）。

### 3.2 剩余 26 个未决为什么不能归零

剩下的全是二进制与无扩展名文件：`*.zip`(4)、无扩展名(3)、`*.schdocpreview`(2)，
以及 `*.pcbdoc/*.pcblib/*.prjpcb/*.prjpcbstructure/*.schdoc/*.schlib/*.dwg/*.prt/*.asm/*.cfg/*.par/
*.sldasm/*.sldprt/*.mcam/*.xlsx/*.doc/*.pptx` 各 1 个。

`git_rule_suggest` 对它们的建议是"**Git**（二进制，直接入库）"，即 `track=true, lfs=false, lf=false`。
但该组合在扫描器**自己的写入路径**里写不出任何行 —— `GitFileRuleService.SetAsync` 只在
`track && lfs` 或 `track && lf` 时才往 `.gitattributes` 追加行；两者都为 false 时既不写属性、
也不写忽略，于是 `ReadDefinitions` 读不到声明，该格式必然回到"未决"。

旁证：OHS 自家的 `0000-000-Template` 项目实测也是 **86.8% 覆盖 / 38 未决**，未决项同样是
`*.md`(已在 022 修好)、`*.zip`、`*.pcbdoc` 这一类。所以"未决为 0"在当前工具模型下，
对含二进制资产的项目并不可达。

### 3.3 若要强行归零：唯一可表达的办法是 LFS（待拍板）

把这 17 种 CAD/Office 二进制格式声明为 LFS 规范行即可被扫描器认成"已跟踪管"，代价：

- 这些文件（`Unused/` 下的模板占位件，合计约 1.2 MB）改为 LFS 指针存储；
- 需要 `git lfs` 在每台检出机器上可用（本仓库 baseline 已对视频/PDF 用 LFS，前提已具备）。

本期未做：v3.4 §2.2 规定不借质量修复之名改动无关资产；这 26 个文件与 WBall 产品代码无关。
需要归零就一句话，我按 LFS 规范行补上并重跑扫描。

## 4. 规则维护纪律

1. OHS 的 `baseline` / `managed` 两段由工具生成，**不要手改**；项目自有规则一律写在这两段**之外**
   （`.gitignore` 与 `.gitattributes` 都已如此），否则 OHS 重写时会被抹掉；
2. 新增一种格式时，先在本台账登记处置，再提交文件；
3. 生成物、锁文件、临时产物一律 ignore，且新增时同步确认索引里没有历史残留
   （`git ls-files | grep -E "(^|/)(bin|obj|\.vs)/"` 必须为空 —— CI 已把这条做成门禁）。

—— 台账结束 ——
