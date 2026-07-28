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

## 3. `git.rule.scan` 未决数归零的口径

v3.4 §4.7.5 要求 OHS `git.rule.scan` 未决为 0。本台账覆盖的是**仓库侧**：所有实际格式都已有
`.gitattributes` / `.gitignore` 明文规则。扫描器本身属 OHS（2026-020）工具链，需在 OHS 里对
本仓库跑一次 `git.rule.scan` 确认口径一致 —— 该步骤不在 022 工作树内，需在 OHS 侧执行。
若扫描器把 §2 的 248 个文件计为未决，按 §2 方案 A 处置后即归零。

## 4. 规则维护纪律

1. OHS 的 `baseline` / `managed` 两段由工具生成，**不要手改**；项目自有规则一律写在这两段**之外**
   （`.gitignore` 与 `.gitattributes` 都已如此），否则 OHS 重写时会被抹掉；
2. 新增一种格式时，先在本台账登记处置，再提交文件；
3. 生成物、锁文件、临时产物一律 ignore，且新增时同步确认索引里没有历史残留
   （`git ls-files | grep -E "(^|/)(bin|obj|\.vs)/"` 必须为空 —— CI 已把这条做成门禁）。

—— 台账结束 ——
