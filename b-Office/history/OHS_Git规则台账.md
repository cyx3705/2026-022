# 2026-022-WBall · OHS Git 规则台账

> v3.4 V34-10 交付物。日期：2026-07-29
> 目的：仓库里**每一种实际存在的格式**都有明确处置（track / ignore / LFS / binary），不留"未决"。
> 依据：`git ls-files` 的真实清单，不是猜测。

## 1. 处置总表

| 格式 | 数量 | 位置 | 处置 | 规则来源 |
|---|---:|---|---|---|
| `.cs` | 73 | `b-Code-WBall`、`b-Code-Verify` | track（`text eol=lf`） | v3.4 项目段 |
| `.md` | 41 | `b-Office`、各 README | track（`text eol=lf`） | v3.4 项目段 |
| `.csproj` / `.sln` / `.props` / `.config` / `.xml` / `.yml` / `.manifest` / `.xaml` | 1~4 | 根、工程目录 | track（`text eol=lf`） | v3.4 项目段 |
| `bin/` `obj/` `.vs/` | 原 185 个 | 两个工程 | **ignore + 已从索引移除** | v3.4 V34-01 |
| `.png` / `.jpg` / `.BMP` / `.Zip` | 4 | `Logo.png`、`b-Picture`、`Unused` | track（`binary`） | v3.4 项目段 |
| `.PcbDoc` `.PcbLib` `.SchDoc` `.SchLib` `.PrjPcb` `.PrjPcbStructure` `.SchDocPreview` | 6 | `Unused/b-Altium-Designer` | track（`binary`：禁 EOL 转换与文本 diff） | v3.4 项目段 |
| `.prt` `.par` `.asm` `.cfg` `.SLDPRT` `.SLDASM` `.dwg` `.mcam` | 8 | `Unused/b-Module-*` | track（`binary`） | v3.4 项目段 |
| `.xlsx` `.doc` `.pptx` | 3 | `Unused/b-Office` | track（`binary`） | v3.4 项目段 |
| `.mp4` `.mov` `.avi` `.pdf` `.stp` 等大媒体 | 0（暂无） | `b-Video`、`b-Publish` 预留 | **LFS** | OHS baseline/managed 段 |
| `.eod` `.eox` `.lck` `.slk` `.xlk` `.flk` `.elk` | 0 | 旧 `Unused/b-EPLAN/N0000.edb/**` 已退出当前索引和工作树 | ignore | OHS baseline |

## 2. EPLAN 历史模板处置

`Unused/b-EPLAN/N0000.edb` 的 248 个模板数据库文件已退出当前索引和工作树，OHS baseline
继续忽略对应锁文件/数据库扩展名。历史 Git 对象仍然存在，本台账不宣称历史仓库已经重写或瘦身。

## 3. `git.rule.scan` 实测与"未决为 0"能否达成

已通过 MCP 连上运行中的 OneHistoryStudio 2.7.3（`POST http://127.0.0.1:8737/mcp`，JSON-RPC）
实跑 `git_rule_scan` / `git_rule_gaps` / `git_rule_suggest` / `git_rule_list`。实测：

| 时点 | 覆盖率 | 未决文件 |
|---|---:|---:|
| 本轮开工前 | 75.3% | 137 |
| 文本格式改用规范行 `text eol=lf` 后 | **95.3%** | **26** |
| 3.4.0 收口改动提交前 | 89.2% | 53（其中 27 个是尚未入索引的新 `.cs/.csproj`） |

### 3.1 扫描器只认两种规范行（这是上一版规则失效的原因）

`GitFileRuleService.ParseCanonicalAttribute` 用 `SetEquals` 精确比对属性集合，只接受：

- LFS：`filter=lfs diff=lfs merge=lfs -text`
- LF：`text eol=lf`

`text eol=crlf`、`-text`、`binary` 一律解析为 null → 该格式记为"未决"。所以规则要被扫描器认，
必须逐字使用上面两种写法（`.editorconfig` 的 `end_of_line` 必须同步为 `lf`，否则格式门禁会红）。

### 3.2 提交后剩余 26 个未决为什么不能归零

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

这 26 个文件与 WBall 产品代码无关。V3.4 接受该扫描器表达能力例外，不为追求覆盖率数字
把小型继承模板二进制强制迁入 LFS；待 OHS 支持普通 Git 二进制声明后再归零。

## 4. 规则维护纪律

1. OHS 的 `baseline` / `managed` 两段由工具生成，**不要手改**；项目自有规则一律写在这两段**之外**
   （`.gitignore` 与 `.gitattributes` 都已如此），否则 OHS 重写时会被抹掉；
2. 新增一种格式时，先在本台账登记处置，再提交文件；
3. 生成物、锁文件、临时产物一律 ignore，且新增时同步确认索引里没有历史残留
   （`git ls-files | grep -E "(^|/)(bin|obj|\.vs)/"` 必须为空 —— CI 已把这条做成门禁）。

—— 台账结束 ——
