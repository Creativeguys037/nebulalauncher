# 仓库多余物盘点

盘点时间：2026-10-24 16:0x（Asia/Shanghai）
盘点方式：只读。git log / status / ls-files / du / find / grep / md5sum，未执行 build 或 test，未修改任何源码或配置。
盘点范围：`D:\agent_workspace\NebulaLauncher` 全仓 + 仓外 `D:\agent_workspace\pub`、`D:\agent_workspace\.probe`。

## 概览

一句话结论：**仓库的「乱」不在代码，而在文档的证据链**——代码侧几乎零死物（全仓 0 个 `[Obsolete]`、0 个 `#if false`、0 段注释掉的代码；TODO/FIXME/HACK 仅 2 处且其中 1 处是测试夹具字符串），而 `docs/` 有 **171 行引用指向不存在的文件**，其中 **109 行**指向仓外 `.probe/` 下的研究底稿、冒烟日志与验收截图（**89 个引证路径全部消失**：PCL2 覆盖率盘点、`pcl-gap-r28/r29/r30`、PCL2-PORTING 的 4 份规格、HMCL 调研、R31–R55 全部冒烟报告、30+ 张截图、hs_err 崩溃样本），这些底稿**已全部丢失**，导致 docs/EXTENSIONS.md（127 KB）与 docs/PCL2-PORTING.md（166 KB）两个最大文档的核心取证无法复核。

| 项 | 实测值 | 判定 |
|---|---|---|
| git | 1 个 commit（`7440567 initial commit`），423 个已跟踪文件，remote `origin` → `Creativeguys037/nebulalauncher` | 正常 |
| 真未跟踪文件 | **4 个**（`AGENTS.md`、docs/agents/{domain,issue-tracker,triage-labels}.md），共 8.2 KB | 应进仓 |
| 已跟踪但本地有改动 | 1 个：`docs/agents/workspace.yaml`（+3 行 `layout: single`） | 应提交 |
| 被忽略的构建产物 | 8 个 `bin/`+`obj/` 目录条目 / **440 个文件** / **460 MiB** | 已是忽略态，可整体删 |
| `docs/` 体积 | 17 个 `.md` + `agents/` 4 文件 = **654 KiB**（669127 B） | — |
| 失效引用 | **171 行**（TAKEOVER 19 处 + 旧交接 27 处 + REVIEW-R43-R51 2 处 + `.probe` 底稿/截图 109 行） | 需裁决 |
| 仓外 `pub` | **50 MB**，r60 与 r61 的 3 个文件 **MD5 全同** → 25 MB 是纯副本 | 删一份 |
| 仓外 `.probe` | **9.2 MB**，3 张 PNG 里 2 张 MD5 相同 → 唯一视觉证据只有 2 张 | 归档 2 张 |
| 代码内死字段 | **1 个**：`AppSettings.CheckUpdatesOnStartup`（`src/NebulaLauncher.Core/Models/Settings.cs:290`，注释 `:286-289`） | 可删可留 |
| `artifacts/`、`TestResults/`、`.vs/`、`*.log`、`scripts/` | **均不存在** | 无需处理 |

---

## A. 未跟踪文件

`git status --porcelain` 全量输出只有 5 行（`--ignored` 不算）。基线里「约 444 个未跟踪文件」= 4 个真未跟踪 + 440 个被忽略的 `bin/obj` 文件，实测吻合。

### A.1 真未跟踪（`??`，4 个）

| 路径 | 体积 | 是什么 | 默认动作 | 依据 |
|---|---|---|---|---|
| `AGENTS.md` | 758 B | 仓库根 agent 入口，指向 `CONVENTIONS.md` 与 docs/agents/* | **保留（git add）** | 被 `docs/agents/domain.md` 体系需要；是仓库规范的一部分，不是临时物 |
| `docs/agents/domain.md` | 1757 B | single-context 域文档布局约定 | **保留（git add）** | `AGENTS.md` 引用它 |
| `docs/agents/issue-tracker.md` | 4317 B | GitHub Issues（`gh` CLI）操作约定 | **保留（git add）** | `AGENTS.md` 引用它 |
| `docs/agents/triage-labels.md` | 1402 B | triage 五状态标签映射表 | **保留（git add）** | `AGENTS.md` 引用它 |

### A.2 已跟踪但工作区有改动（`M`，1 个）

| 路径 | 差异 | 默认动作 | 依据 |
|---|---|---|---|
| `docs/agents/workspace.yaml` | +3 行：`layout: single` 及其注释（域文档布局记录） | **保留，提交** | 是 `docs/agents/domain.md` 结论的落盘，属正常演进 |

### A.3 临时脚本 / 备份 / 日志

| 类型 | 实测 | 判定 |
|---|---|---|
| `*.ps1` | 4 个：`build.ps1`（19186 B）、`env.ps1`（621 B）、`tools/shot.ps1`（8.0 KB）、`tools/seed-demo-data.ps1`（16 KB） | 全部**已在 git 跟踪中**，全部**活**（见 F 节），无临时脚本 |
| `*.py` / `*.tmp` / `*.bak` | 0 个 | 无 |
| `*.log` | 0 个（仓内） | 无 |
| `.probe` 内的 `smoke-*` 日志 | **0 个，已全部丢失** | 见 D 节与 C 节 |

结论：**A 节没有需要删除的东西**，只有「4 个 md + 1 个 yaml 改动没提交」。用户感知的「多余的东西」不在这层。

---

## B. docs 文档归属

`docs/` 17 个 `.md` + `agents/`（4 文件）。判定标准是**引用活性**：被 `README.md` / `AGENTS.md` / `CONVENTIONS.md` / docs/HANDOVER.md / 代码引用即活。

| # | 路径 | 体积 | 被引用文件数 | 是什么 | 默认动作 | 依据 |
|---|---|---|---|---|---|---|
| 1 | docs/HANDOVER.md | 139799 B | 35 | 接手总文档（§1 现状 / §2 跑起来 / §5 约定 / §6 坑 / §11 历史） | **保留** | 全仓最高引用密度，接手第一入口 |
| 2 | docs/PCL2-PORTING.md | 166720 B | 5 | PCL2 → Nebula 逐轮移植记录 | **保留**，但需修 C 节的 `.probe` 死链 | 唯一逐轮移植台账 |
| 3 | docs/EXTENSIONS.md | 127169 B | 11 | 扩展功能清单与实现状态 | **保留**，同样需修死链 | §0.0.18 是当前轮，§0.0.8 是候选池 |
| 4 | docs/PCL2-GAP.md | 56115 B | 8 | PCL2 能力缺口清单 | **保留**，修死链 | 与 EXTENSIONS 互补 |
| 5 | docs/AUDIT-2026-10-23.md | 33642 B | 17 | 对照 PCL 的现状核查（README 指定为准） | **保留** | `README.md:7` 明写「现状以本文为准」 |
| 6 | docs/REVIEW-R34-R36-CODE.md | 24752 B | 4 | R34–R36 代码审查报告（6 P2 + 14 P3） | **归档 → docs/archive/reviews/** | 单次审查记录，缺陷已处理；仅被 4 处历史引用 |
| 7 | docs/REVIEW-R35-DECISIONS.md | 24504 B | 3 | R35 裁决复核 | **归档 → docs/archive/reviews/** | 同上 |
| 8 | docs/R31-BROKEN-WORK.md | 10879 B | 3 | R31 中断任务清单 | **归档 → docs/archive/** | 「中断任务」这一状态已结束（后续轮次已接力），正文仍被引作历史 |
| 9 | docs/FINAL-SCOPE.md | 6137 B | 5 | 最终范围与完成判定 | **保留 + 必须修** | §77 声称「这不是 git 仓库」与事实矛盾（见 C.4） |
| 10 | docs/PUBLISH-CHECK.md | 7686 B | 6 | 发布产物检查表 | **保留 + 更新** | `build.ps1:36` 引用；但头部仍写「2026-10-23 第二十八轮更新」，当前已第五十九轮 |
| 11 | docs/DESIGN.md | 8287 B | 7 | 视觉规范 | **保留** | `README.md:212`、`CONVENTIONS.md:146` 引用 |
| 12 | docs/HANDOVER-2026-10-24-R54.md | 2452 B | 1（`HANDOVER.md:9`） | 第五十四轮：最终验收 | **归档 → docs/archive/handover/** | 内容已汇入 `FINAL-SCOPE.md:60-63` 与 `HANDOVER.md` |
| 13 | docs/HANDOVER-2026-10-24-R55.md | 6864 B | 1（`FINAL-SCOPE.md:75`） | 第五十五轮：修微软登录 | **归档 → docs/archive/handover/** | `FINAL-SCOPE.md:75` 称它「最近交接」，实际已被 R56–R59 取代，该行已过期 |
| 14 | docs/HANDOVER-2026-10-24-R56.md | 8450 B | 1（`HANDOVER.md:9`） | 第五十六轮：本地内容图标三条断链 | **归档 → docs/archive/handover/** | 内容已折进 `HANDOVER.md:1249-1250` 表格 |
| 15 | docs/HANDOVER-2026-10-24-R57.md | 11236 B | 0 个 `.md` 全名引用 / 1 个简写「R57」引用（`HANDOVER-2026-10-24-R58.md`） | 第五十七轮：筛选来源 + CF 整合包 | **归档 → docs/archive/handover/** | 唯一引用方 R58 本身也几乎无人引；`HANDOVER.md` §11 无入口 |
| 16 | docs/HANDOVER-2026-10-24-R58.md | 12603 B | 0 个 `.md` 全名引用 / 2 个简写引用（R57、R59） | 第五十八轮：新整包算新版本 / 双源分页 / R57 遗留 | **归档 → docs/archive/handover/** | 引用方都是其它单轮交接，属闭环互引；`HANDOVER.md` 无入口 |
| 17 | docs/HANDOVER-2026-10-24-R59.md | 13632 B | 1（`HANDOVER.md:8`） | 第五十九轮：修登陆态不保持 | **保留** | `HANDOVER.md:8` 显式标为「最后更新」指向的轮次文档 |
| — | `docs/agents/`（4 文件） | 8.2 KB | 1（`AGENTS.md`） | agent 协作约定 | **保留** | 见 A.1 |

### B.1 六份单轮交接与主 `HANDOVER.md` 的实际关系

`HANDOVER.md` 是 139 KB 的滚动总文档，17 个章节各有固定职责；docs/HANDOVER-2026-10-24-R5x.md 是**单轮施工记录**，两者不是同一层：

- `HANDOVER.md:8-9` 只显式点了 3 份：R59（最后更新）、R56、R54（最终验收）。
- `HANDOVER.md:1433` §11「历史轮次去哪看」的表格里，**一份 R54–R59 都没有**；表里反而写着「docs/HANDOVER-2026-10-23-R32.md（最新一轮）」——这份文件不存在。
- R55 的正文只在 `FINAL-SCOPE.md:54,64,75` 出现，而 `FINAL-SCOPE.md:75` 那句「最近交接：R55」已被 R56–R59 四轮超越。
- R57 / R58 在全体在册文档中**零引用**：R57 只被 R58 引，R58 只被 R57 与 R59 的数据对比句提到。

即：**6 份里有 5 份已经是归档态**（R54/R55/R56/R57/R58），只有 R59 是「当前轮」，同时「当前轮」这一角色在下一轮会自然迁移。建议归档后把 `HANDOVER.md:8-9` 与 §11 表格改成「最近三份 + 归档目录」的稳定入口，不要再逐轮追链接。

---

## C. 失效引用

方法：先提取全仓 `*.{md,cs,ps1,csproj,sln,yaml}` 里所有 `*.md` 形式的路径 token（88 个唯一值），再逐个 `test -e`；对不存在的按「目标」分组并回溯全部引用行。下表只保留**真失效**项，已排除三类假阳性：

- `docs/agents/domain.md` 里的 `0001-event-sourced-orders.md` / `0002-postgres-for-write-model.md`——那是域建模技能的模板示例，不是本仓文件；
- `tests/NebulaLauncher.Tests/CoreAssets_DataPackTests.cs:125`、`tests/NebulaLauncher.Tests/CoreLaunch_CrashExportTests.cs:224-244` 里的 `notes.md`、`hs_err_pid*.log`——测试夹具的临时文件名字符串；
- `GLOSSARY.md` / `GLOSSARY-MAP.md` / `CONTEXT.md` / `CONTEXT-MAP.md`——docs/agents/domain.md:9,12 明确写了「等第一次真正写下词条时再建」「旧名已废弃」，属**约定的前置空缺**，不是失效引用。

合计 **171 行引用**：C.1 `TAKEOVER.md` 19 处、C.2 旧交接 27 处、C.3 `REVIEW-R43-R51-CODE.md` 2 处（以上按「处」计，一处可含多个文件 token），C.5 仓外 `.probe/` **109 行**（按行计）。

`.probe` 的口径（C.5 详述）：全仓 123 行提到 `.probe`，被引用 **95 个唯一路径 token**；其中 **6 个是命令/默认路径模板**（`.probe\data-me`、`.probe\probe-data`、`.probe\data-ui1`、`.probe\shot.png`、`.probe\v.png`、`.probe\data-<你的标识>`），重跑即重新生成，**不算死链**（14 行）；其余 **89 个引证性路径全部不存在**，计约 109 行、122 次出现。

方法与口径说明：C.1–C.3 的「处」= 引用行 × 该行点到的缺失文件数；C.5 的「行」= 含 `.probe` 的物理行数。全部经 `grep -rn` + `test -e` 实测，计数排除本报告自身（`docs/research/`）。

### C.1 docs/TAKEOVER.md（不存在，19 处引用）

| 引用位置 | 引用形态 | 建议 |
|---|---|---|
| `README.md:34` | 「接手/恢复开发先读 docs/TAKEOVER.md」 | **改指向** → docs/HANDOVER.md §1+§5+§6（新接手第一步就撞死链，最高危） |
| docs/HANDOVER.md:11 | 「被中断过？先读 docs/TAKEOVER.md」并引其状态数字（2172/2172） | **改指向** → 同一页 §2 与 §10；数字以 `HANDOVER.md:20`（2358）为准 |
| docs/FINAL-SCOPE.md:15 | docs/TAKEOVER.md §0 作为 11 页面来源 | 删引用，保留「11 个页面」结论 |
| docs/FINAL-SCOPE.md:73 | 「文档入口」三件套之一 | **改指向** → docs/HANDOVER.md |
| docs/HANDOVER-2026-10-24-R55.md:59 | 更新任务表里「docs/TAKEOVER.md / 头部、版本表、状态行」 | 随该文档一起归档，无需单独改 |
| docs/R31-BROKEN-WORK.md:4 | Markdown 链接 `[TAKEOVER.md](TAKEOVER.md)` | 随该文档归档 |
| docs/R31-BROKEN-WORK.md:12,208,215 | 路径引用 + 状态表 + 内容引述 | 随归档 |
| docs/HANDOVER.md:1501,1526 | 内容引述（「`TAKEOVER.md` §5 的源测速 CLI 入口」） | 删「进 TAKEOVER.md §5」这类导航语，保留结论 |
| docs/PCL2-GAP.md:112 | 内容引述 | 同上 |
| docs/AUDIT-2026-10-23.md:171 | 表格里评价 TAKEOVER.md 的结论 | **声明历史不可回溯**（该行描述的是已不存在文件的说法，无法核对） |
| docs/REVIEW-R35-DECISIONS.md:16,129,133,135,162,165 | 6 处内容引述，含具体行号 `TAKEOVER.md:176-184`、`:180,182` | 随该文档归档；行号引用天然不可回溯，归档时加一句「原文已丢失」 |

### C.2 已不在仓的历史轮次交接（27 处引用）

被引用但不存在的文件清单：`HANDOVER-2026-10-22.md`、`HANDOVER-2026-10-22-R16/R25/R26/R27.md`、`HANDOVER-2026-10-23-R28/R29/R30/R31/R32.md`、`HANDOVER-2026-10-24-R34/R35/R36/R37/R38/R39/R40/R41.md`，以及简写形 `R35.md`。

| 引用位置 | 指向 | 建议 |
|---|---|---|
| docs/HANDOVER.md:1437 | `HANDOVER-2026-10-23-R32.md`（标注为「最新一轮」） | **删引用**：最新一轮是 R59，这句本身已过期 |
| docs/HANDOVER.md:1449 | `HANDOVER-2026-10-22-R16.md` | **声明历史不可回溯**：改成「第十六轮（原文已不在仓，仅本段摘要）」 |
| docs/HANDOVER.md:1471,1480,1488 | R25 / R26 / R27 | 同上 |
| docs/HANDOVER.md:1500,1514,1530,1551,1557 | R28 / R29 / R30 / R31 / R32 | 同上（共 10 行，是 §11「十三轮下来的形状」整段） |
| docs/PCL2-PORTING.md:1070,1231 | `HANDOVER-2026-10-22.md`（分工来源） | 改写成「早期分工表（原文已不在仓）」 |
| docs/PCL2-GAP.md:7,11 | R34 / R37 | 删引用，保留裁决结论 |
| docs/AUDIT-2026-10-23.md:105 | R34 / `R35.md`（简写） | 删引用或改指向 `REVIEW-R34-R36-CODE.md`（该文件在） |
| docs/AUDIT-2026-10-23.md:137,201,203,206,210 | R36 / R38 / R39 / R40 / R41 | 声明历史不可回溯 |
| docs/FINAL-SCOPE.md:41 | R36 §3 | 改指向 docs/REVIEW-R34-R36-CODE.md |
| docs/R31-BROKEN-WORK.md:12,209 | `HANDOVER-2026-10-23-R31.md` | 随该文档归档 |
| docs/REVIEW-R34-R36-CODE.md:8 | R34 / R35 / R36 | 随该文档归档 |
| docs/REVIEW-R35-DECISIONS.md:3,132,164 | R35 / R30 / R32（含 `:45-51`、`:25` 行号） | 随该文档归档，加「原文已丢失」注 |

### C.3 docs/REVIEW-R43-R51-CODE.md（不存在，2 处引用）

| 引用位置 | 建议 |
|---|---|
| docs/AUDIT-2026-10-23.md:261 | 该行自述「本轮结束时仍在进行」，说明这份审查从未落盘 → **删引用** |
| docs/HANDOVER-2026-10-24-R54.md:37 | 同处给出「4 P2 + 9 P3」数字，但依据文件不存在 → **声明历史不可回溯**，或从备份恢复 |

### C.4 与事实矛盾的陈述（非死链，但必须改）

| 位置 | 原文 | 事实 |
|---|---|---|
| docs/FINAL-SCOPE.md:76 | 「**这不是 git 仓库**：没有提交历史，改动追溯只能靠文件时间戳与 `docs/` 下的逐轮交接」 | 已是 git 仓库：`master` 1 个 commit `7440567`、423 个已跟踪文件、remote `origin` 指向 `Creativeguys037/nebulalauncher` |
| docs/FINAL-SCOPE.md:75 | 「最近交接：docs/HANDOVER-2026-10-24-R55.md」 | 最新是 R59 |
| docs/HANDOVER.md:20 | 「2358 个单元测试全绿」 | 与同仓文档冲突：R54 记 2238、R55 记 2244、R56 记 2277、R59 记 2358。数字本身无法自证（本盘点未运行测试） |
| docs/PUBLISH-CHECK.md:1 | 「2026-10-23 第二十八轮更新」 | 已到第五十九轮；且文中体积在 23.4 / 23.5 / 23.6 / 23.7 MB 间漂移 |

### C.5 仓外 `.probe/` 底稿、冒烟日志与截图（123 行引用，其中 109 行构成死链）

**这是本仓库最严重的证据链断裂**：docs/EXTENSIONS.md（127 KB）与 docs/PCL2-PORTING.md（166 KB）的取证底稿全在仓外且已删除。

实测口径（`grep -rn` + `test -e`，排除本报告自身）：

| 指标 | 数值 |
|---|---|
| 提到 `.probe` 的引用行 | **123 行**，分布在 14 个文件 |
| 被引用的 `.probe/` 下唯一路径 token | **95 个** |
| 其中「命令/默认路径模板」（重跑即重新生成，**不算死链**） | **6 个**：`.probe\data-me`、`.probe\probe-data`、`.probe\data-ui1`、`.probe\shot.png`、`.probe\v.png`、`.probe\data-<你的标识>` 占位符 |
| 其中「引证性路径」（「证据在…」「截图见…」「`.probe/x.md` 记录了…」） | **89 个，全部不存在**；合计 122 次出现、约 109 行 |
| 磁盘上仅存的 `.probe` 引用目标 | 1 个：`.probe\data-r59`（1 行） |

6 个非死链模板行（列在此处仅为排除，**无需任何动作**）：`README.md:132,134,138,142`、docs/HANDOVER.md:84、`CONVENTIONS.md:138,142`、tools/seed-demo-data.ps1:23,24、tools/shot.ps1:2,39、tools/InstallProbe/Program.cs:21,22,28。

#### C.5.1 研究底稿类（最重要，可复核性直接受损）

| 被引用的不存在文件 | 唯一引用位置 | 该文件原本承载什么 | 建议 |
|---|---|---|---|
| `.probe/pcl-coverage.md`（9 次） | docs/AUDIT-2026-10-23.md:174；docs/EXTENSIONS.md:372,389,399；docs/HANDOVER.md:1029,1243,1532；docs/PCL2-GAP.md:121 | 113 个 PCL2 `.vb` / 43116 行的逐文件判定（已覆盖 49 / 等价 46 / 明确不做 11 / 未覆盖 0 / 不适用 7） | **优先找回并归档 → docs/research/pcl2/**；找不回就在 `EXTENSIONS.md:372` 处声明「覆盖率数字无底稿可复核」 |
| `.probe/pcl-gap-r28.md`（3 次） | docs/PCL2-GAP.md:64,86 | `ModLaunch.vb` 参数组装 / `McLaunchPrerun` 取证 | 同上 |
| `.probe/pcl-gap-r29.md`（4 次） | docs/PCL2-GAP.md:94,116；docs/REVIEW-R35-DECISIONS.md:35 | 第二轮 PCL2 缺口复查 + 7 条「明确不做」5 条「未确定」 | 同上 |
| `.probe/pcl-gap-r30.md`（5 次） | docs/EXTENSIONS.md:403；docs/HANDOVER.md:1251,1544；docs/PCL2-GAP.md:136,138 | 联机（`PageLinkMain`）等 4 块的切片拆分 + §2.4/§2.7/§2.8 盲区判定证据 | 同上；`PCL2-GAP.md:136` 明说「将来要做时直接照它分段」→ 这份缺失会让该计划无法执行 |
| `.probe/pcl2-research/{A-launch,B-download,C-instance,D-crash-ui}.md`（4 次 + 目录 2 次） | docs/PCL2-PORTING.md:5,289,290,291,292 | 2239 行 PCL2 规格（556 / 681 / 584 / 518 行四份） | **优先找回**：这是 `PCL2-PORTING.md` 的结构性依据 |
| `.probe/research-launchers.md`（7 次） | docs/EXTENSIONS.md:3,630,684,715,729,907,1057 | HMCL 55 个源文件调研（§6.5 / §6.9 / §6.10） | 同上；`EXTENSIONS.md:1057` 有「条目 16 按 research… 的形状写」的直接依赖 |
| `.probe/mk-icon-data.ps1` + `.probe/data-icons`（4 次） | docs/HANDOVER.md:1127；docs/HANDOVER-2026-10-24-R56.md:11 | 内容图标合成样本（每种内容各一个带图标 + 一个不带） | 脚本可按 R56 描述重写；样本数据重生即可 → **声明不可复核** |

#### C.5.2 真机冒烟报告与测试日志类

| 被引用的不存在文件 | 唯一引用位置 | 原本承载什么 | 建议 |
|---|---|---|---|
| `.probe/smoke-r31/REPORT.md`（4 次） | docs/AUDIT-2026-10-23.md:175；docs/R31-BROKEN-WORK.md:102,212 | 首份真机冒烟报告（`latest.log` 原文 + 判据命中表） | 声明历史不可回溯 |
| `.probe/smoke-r34/REPORT.md`（5 次） | docs/AUDIT-2026-10-23.md:107,188,191；docs/HANDOVER.md:37 | 退出 / 二次启动验证 | 同上 |
| `.probe/smoke-r36/run4.log`、`run-forge3.log`（4 次） | docs/AUDIT-2026-10-23.md:194,230；docs/HANDOVER.md:38 | Fabric / Forge 真机启动日志 | 同上 |
| `.probe/smoke-r37/REPORT.md`（2 次） | docs/AUDIT-2026-10-23.md:193；docs/HANDOVER.md:37 | 多实例并行验证 | 同上 |
| `.probe/smoke-r42/REPORT.md`、`run5.log`、`run6.log`（6 次） | docs/AUDIT-2026-10-23.md:215,227；docs/FINAL-SCOPE.md:65；docs/HANDOVER-2026-10-24-R54.md | 启动→退出→备份→二次启动全链路 | 同上 |
| `.probe/smoke-r46`（1 次） | docs/AUDIT-2026-10-23.md:245 | 微软登录 public client id 实测 | 同上 |
| `.probe/smoke-r55`、`run2.log`（4 次） | docs/FINAL-SCOPE.md:65；docs/HANDOVER-2026-10-24-R55.md | 微软登录设备码真机 | 同上 |
| `.probe/audit-full-tests.log`（2 次） | docs/AUDIT-2026-10-23.md:179 | 2113/2113 全绿日志 | 同上 |
| `.probe/audit-backups-test.log` | docs/AUDIT-2026-10-23.md 备份测试段 | 备份恢复测试日志 | 同上 |
| `.probe/audit-profiles-{light,dark}.png`（各 1 次） | docs/AUDIT-2026-10-23.md 主题截图段 | 深浅色主题验收截图 | 同上 |
| `.probe/crash-samples/hs_err_pid12345.log`（2 次） | docs/PCL2-PORTING.md:617；`tests/NebulaLauncher.Tests/CoreLaunch_HsErrTests.cs:19`（XML 注释声称「与样本一致」） | `EXCEPTION_ACCESS_VIOLATION / nvoglv64.dll` 真实 hs_err 样本 | **值得单独找回**：测试注释直接以它为参照；找不回则删 `CoreLaunch_HsErrTests.cs:19` 的「样本一致」表述 |

#### C.5.3 截图类（30+ 张，全部随 `.probe` 删除而消失）

| 组 | 代表路径 | 引用位置 | 建议 |
|---|---|---|---|
| 背景图验收 | `.probe/bg-home.png`、`bg-home2.png`、`bg-home3.png` | docs/AUDIT-2026-10-23.md:151,256,266；docs/FINAL-SCOPE.md:27；docs/PUBLISH-CHECK.md:100 | 声明不可复核；若要重拍用 `tools/shot.ps1` 现成命令 |
| R51–R53 发布自检 | `.probe/pub2-firstrun.png`、`pub2-…` | docs/PUBLISH-CHECK.md | 同上 |
| lead 验收截图（6 张） | `.probe/lead-mods.png`、`lead-versions-inst.png`、`lead-set-game-tall.png`、`lead-set-dl-tall.png`、`lead-instance-editor.png` | docs/PUBLISH-CHECK.md、docs/HANDOVER-2026-10-24-R5x.md | 同上 |
| 图标/内容验收 | `.probe/icons-*.png`（含 `icons-shaders-light/1024`、`icons-real-cf-published/fixed`）、`.probe/dual-library.png`、`.probe/multi-versions.png`、`.probe/servers-factory.png`、`.probe/authlib-instance.png`、`.probe/settings-game.png`、`.probe/mods.png`、`.probe/home-light.png`、`.probe/v.png` | docs/HANDOVER-2026-10-24-R56.md、docs/EXTENSIONS.md、docs/PUBLISH-CHECK.md、`README.md` | 同上 |

#### C.5.4 数据目录与其它前缀（工作区引用）

`data-cf`、`data-cf-pub`、`data-world`、`data-mirror`、`data-clone/minecraft`、`data-ui1`、`publish-minimal2`、`pub-r54`、`pub-r55`、`pub-r56`、`fresh2`、`cfverify`、`authlib-smoke`、`bat-check/`、`probe`，以及模式化前缀 `.probe/r25-`…`.probe/r28-`、`.probe/mem-`、`.probe/stop-`、`.probe/speed-`、`.probe/lookup-`、`.probe/logs-follow-`、`.probe/javast-`、`.probe/javafix-`、`.probe/mirror-settings-`、`.probe/dirs-versions-`、`.probe/vtype-`、`.probe/versions-loader-`、`.probe/icons-`。

这些是每轮验证时建的临时工作区名，多数只出现在叙述性句子里（「本轮在 `.probe/data-cf` 里验了…」）。**建议统一处理**：不逐个修补，而是在 `docs/` 头部加一条全局声明：*「R20–R59 期间所有真机验证产物存于仓外 `D:\agent_workspace\.probe\`，该目录未纳入版本控制且已于 2026-10-24 后清理；文中 `.probe/*` 引用均为历史记录，不可复核。」* ——一条声明替代 109 行逐处修补。

---

## D. 仓库外产物（pub / .probe）

### D.1 `D:\agent_workspace\pub` — 50 MB

| 路径 | 文件数 | 体积 | 时间戳 | 与其它目录的关系 |
|---|---|---|---|---|
| `pub/r59/` | 0 | 0 | — | 空 |
| `pub/r60/` | 3 | 25 MB | exe 2026-10-24 14:40；原生库 2024-11-07 / 2025-04-26 | — |
| `pub/r61/` | 3 | 25 MB | 同上 | **3 个文件与 r60 MD5 全部相同** |
| `pub/r62/` | 0 | 0 | — | 空 |

MD5 实测：

| 文件 | r60 | r61 |
|---|---|---|
| `NebulaLauncher.exe`（14701210 B） | `00521917de6f4c56b184bdcd8a30f4dc` | 同 |
| `libSkiaSharp.dll`（9414216 B） | `ef1fabce43fe32ca83260481253f5476` | 同 |
| `libHarfBuzzSharp.dll`（1804872 B） | `e0c86bbd88d6b8f5643c1ab1c050a4ee` | 同 |

结论：**50 MB 里只有 25 MB 是唯一内容**，`r61` 是 `r60` 的逐字节副本。

| 判定 | 依据 |
|---|---|
| **保留在仓外，不进 git** | 仓库根 `.gitignore:5` 已写 `pub/`，但那只覆盖**仓内**的 `NebulaLauncher/pub/`；仓外 `D:\agent_workspace\pub` 不在 git 管辖范围，天然不进仓——现状已正确，无需加规则 |
| **删除 `pub/r61/`**（25 MB） | 与 `r60` 逐字节相同，无独立证据价值 |
| **删除空的 `pub/r59/`、`pub/r62/`** | 0 文件 |
| 保留 `pub/r60/` 作为「23.7 MB / 3 文件」的可核对样本 | docs/PUBLISH-CHECK.md、`FINAL-SCOPE.md:61` 都引用这个形态 |

### D.2 `D:\agent_workspace\.probe` — 9.2 MB

| 路径 | 体积 | 时间戳 | 是什么 | 判定 |
|---|---|---|---|---|
| `.probe/data-r59/` | **8.63 MB**（9050852 B，56 文件，`du -sh` 报 8.7M） | 2026-10-24 15:07 | `tools/seed-demo-data.ps1:179-199` 生成的演示数据目录：`cache/version_manifest_v2.json`(278138 B)、`accounts.json`(561 B)、`servers.json`(466 B)、`logs/launcher-20261024.log`(2014 B)，以及 **3 个世界的 34 个 `.mca` region 文件（每个恒为 262144 B）**——远古遗迹 / 创造实验场 / Nebula 测试世界；另有 6 个 mod jar(~280–296 B)、4 个 versions、2 个 resourcepacks、2 个 shaderpacks | **删**：运行期演示数据，可随时重生（`seed-demo-data.ps1` 重跑即得，`HANDOVER.md:84` 给了命令）；体积 94% 来自可重生成的 region 文件 |
| `.probe/r59-home.png` | 168302 B | 2026-10-24 15:07 | R59 首页截图（1240×800） | **归档** |
| `.probe/r59-home-final.png` | 168302 B | 2026-10-24 15:15 | 与上一张 **MD5 相同**（`92270afd922436e5177dd1af90e54333`） | **删**（重复） |
| `.probe/r61-home.png` | 168302 B | 2026-10-24 15:07 | R61 首页截图，MD5 `c9658fc4e5c00d81362057869566a005`（与 r59 不同） | **归档** |
| 所有 `smoke-*/`、`pcl-coverage.md`、`pcl-gap-r2x.md`、`pcl2-research/`、`research-launchers.md`、`crash-samples/`、30+ 张 `*.png` | **0** | — | **已全部丢失**，但仍被 **89 个路径 / 109 行**文档引用 | 见 C.5 |

`logs/launcher-20261024.log` 内容（2 KB，3 次启动记录）只含「设置文件不存在，使用默认设置」「已加载 2 个账户」「已加载 0 个实例」「窗口尺寸来自截图参数：1240x800」「任务栏进度已挂接」——是截图工具的运行痕迹，无独立证据价值。

| 判定 | 依据 |
|---|---|
| **2 张 PNG 归档进仓**（336 KB） | 它们是 R59 / R61 首页唯一留存的视觉证据，而 docs/HANDOVER.md:1182 明确用 `pub\r61` vs `pub\r59` 的对照做论证；建议放 docs/evidence/screenshots/r59-home.png、`r61-home.png` |
| **`.probe/` 其余全删** | `data-r59` 可用 `seed-demo-data.ps1` 重生；重复 PNG 删 |
| **`.probe/` 不进仓** | 它是截图工具的默认工作目录（`tools/shot.ps1:2` 默认 `-Out D:\agent_workspace\.probe\shot.png`），属本机暂存区；若要保留目录约定，在 `.gitignore` 加 `/docs/evidence/` 之外的 `.probe/` 无意义（仓外） |

### D.3 附带观察（超出本次强制范围，但同属「多余物」候选）

`D:\agent_workspace\` 根下还有：`audit-client.md`(14671 B)、`audit-scripts.md`(19553 B)、`packycode-audit-report.md`(8602 B) —— 三份都是 `dsh-packycode-api` 的第三方安全审计报告，**与本仓库无关**；`install-gh-cli.sh`(11907 B) 是环境安装脚本；`PCL/`(37 MB)、`hmcl/`(1.2 MB) 是 PCL2 / HMCL 源码对照原料（被 `EXTENSIONS.md:3`、`PCL2-GAP.md:64` 引用为调研来源）；`docs/agents/workspace.yaml` 是**仓库内同名文件的副本**（1.0 KB，内容不同源）。建议：三份 audit 报告与 `install-gh-cli.sh` 移出（与本仓无关），`PCL/`、`hmcl/` 保留作为对照原料并在 `docs/research/` 记一行来源路径。

---

## E. 代码内死物

### E.1 标记扫描结果（全仓 `src` / `tests` / `tools`，209 + 155 + 4 个 `.cs`）

| 标记 | 命中数 | 明细 |
|---|---|---|
| `[Obsolete]` / `Obsolete(` | **0** | — |
| `#if false` / `#if FALSE` | **0** | — |
| `TODO` / `FIXME` / `HACK` | **2** | 见下 |
| `XXX` | **1** | `tests/NebulaLauncher.Tests/CoreAssets_ModrinthPlanTests.cs:126`：`RootVersion("XXXX", "x.jar", ...)` —— **测试夹具的哈希字符串，不是标记** |
| 注释掉的整段代码 | **0** | 误报 2 处均为解释性注释：`src/NebulaLauncher.App/Services/TrayService.cs:374`、`src/NebulaLauncher.App/Views/MainWindow.axaml.cs:319`（都以 `// await …` 开头，是在解释线程上下文，无代码语义） |

唯一实质命中：`src/NebulaLauncher.Core/Models/Settings.cs:287`，内容是死字段的自我说明注释，不是待办。

### E.2 `Settings.CheckUpdatesOnStartup` — 已知死字段核对

| 项 | 实测 |
|---|---|
| 声明位置 | `src/NebulaLauncher.Core/Models/Settings.cs:290`（注释 `:286-289`，属性 `:290`） |
| 类型/默认值 | `public bool CheckUpdatesOnStartup { get; set; } = true;` |
| 注释原文 | 「历史字段，已无 UI 入口也无消费方：仓库里没有发布端点，PCL2 开源版的自更新也是空桩（PCL2-TODO A3 / B73）。保留属性只为让旧 settings.json 往返不丢字段。」 |
| 全仓消费方 | **只有测试**：`tests/NebulaLauncher.Tests/CoreLaunch_SettingsServiceTests.cs:50`（写入 `false`）、`:78`（断言 `Assert.False`）。**零生产代码消费** |
| 注释自述核对 | **成立**。UI 侧（`.axaml` / ViewModel）无任何绑定；Core 侧无读取方 |
| 建议 | **保留**（删属性会让旧 `settings.json` 里 `"checkUpdatesOnStartup": true` 在反序列化时被静默丢弃，属破坏性变更），但把注释里的「PCL2-TODO A3 / B73」改成一句可追溯的裁决依据。若用户确认不再关心旧配置兼容，则连属性 + 那两行测试一起删 |

### E.3 其它「历史名称」字段核对

| 位置 | 内容 | 是否有活消费方 | 判定 |
|---|---|---|---|
| `src/NebulaLauncher.Core/Models/Settings.cs:21-24` | `BmclApi = Mirror`（枚举别名，注释「历史名称，等价于 Mirror」） | 无独立消费方，但有**反序列化兼容价值**（旧 `settings.json` 里写的字面量是 `"BmclApi"`） | **保留** |
| `src/NebulaLauncher.Core/Models/Settings.cs:293-298` | `CloseLauncherAfterLaunch`（注释「`LauncherVisibilityOnLaunch` 保留向后兼容」） | 是 | **保留** |

### E.4 未引用资源文件

| 资源 | 体积 | 是否被使用 | 依据 |
|---|---|---|---|
| `src/NebulaLauncher.Core/Resources/WikiEntries.txt` | **978285 B**（956 KiB，是仓内最大单文件） | **被使用** | `src/NebulaLauncher.Core/NebulaLauncher.Core.csproj:22` 的 `<EmbeddedResource Include="Resources\WikiEntries.txt" />`；`src/NebulaLauncher.Core/Mods/ModNameTable.cs:204` 以 `ResourceName = "NebulaLauncher.Core.Resources.WikiEntries.txt"` 加载；`tests/NebulaLauncher.Tests/CoreMods_LegacyMetadataTests.cs:262` 断言条目数非 0（防「漏打包」）→ **三处硬引用，删了就编译过但功能静默失效** |
| `src/NebulaLauncher.App/Assets/tray.png` | — | **被使用** | `Assets/` 下**只有这一个文件**，被 1 处引用 |

结论：**不存在未引用的资源文件**。`Assets/` 只有 1 个文件也说明资源没有堆积。

---

## F. tools 与脚本

### F.1 四个探针工程

| 工程 | 行数 | 在 `.sln` 里 | 外部引用 | 作用（据 `Program.cs` 头部） | 默认动作 |
|---|---|---|---|---|---|
| `tools/TestRunner/` | 135 | **是** | `build.ps1:306`；`CONVENTIONS.md:108,118,119`；`HANDOVER.md:59` | 进程内 xunit 运行器。注释写明存在理由：本机沙箱拒绝 testhost 启动（`OpenProcess` → `Win32Exception 5`），`dotnet test` / `dotnet vstest` 都在启动阶段中止，故反射测试程序集+复用 xunit 断言 | **保留（构建路径的一部分）** |
| `tools/InstallProbe/` | 138 | 否 | `HANDOVER.md:134,135,644,1206,1401,1424`；`PCL2-PORTING.md:254,615,779`；tests/.../CoreInstalling_NeoForgeTests.cs:11 | 端到端安装回归：`InstallProbe <数据目录> <MC版本> <加载器>`，真网络装原版/Fabric/Forge/NeoForge | **保留**（真装验证的唯一入口，9 处文档依赖） |
| `tools/NeoForgeProbe/` | 271 | 否 | **仅 1 处**：`tests/NebulaLauncher.Tests/CoreInstalling_NeoForgeTests.cs:11`（注释里的取证出处） | NeoForge 反射取证：`types`（打印类型签名）/ `fetch`（端点可达性）/ `catalog <mc>`（桥接逻辑走一遍） | **待用户裁决**：NeoForge 已真装通过（`HANDOVER.md:1206`），探针使命结束；但它是那 271 行桥接逻辑的唯一独立复现器 |
| `tools/ApiDump/` | 45 | 否 | **仅 1 处**：`HANDOVER.md:196` 的目录清单提到名字 | 反射打印 `CmlLib.Core*` / `XboxAuthNet.Game` 六个程序集的公开 API 签名（依赖调研用） | **待用户裁决 / 可删**：45 行一次性工具，调研结论已落进 `PCL2-PORTING.md`，无复现需求 |

`tools/` 的 138 MB 全部来自 tools/TestRunner/bin（138 M）+ `obj`（462 K），源码部分 4 个工程合计仅 45 KB。

### F.2 脚本

| 脚本 | 体积 | 引用 | 作用 | 判定 |
|---|---|---|---|---|
| `build.ps1` | 19186 B | `README.md`、`HANDOVER.md:20,776`、`CONVENTIONS.md`、`PUBLISH-CHECK.md` 多处 | 构建/发布：XAML 过期检测 + 两道产物关 + `-Publish` 出 `pub\` | **保留** |
| `env.ps1` | 621 B | `HANDOVER.md:53`（`. .\env.ps1` 是每条新 shell 的第一步） | 把 `C:\Users\MECHREUO\.dotnet8` 塞进 `DOTNET_ROOT` 与 `PATH`，找不到就红字报错 return | **保留**，但它是**本机绑定**（写死用户目录 + SDK 版本 8.0.426），换机器必失效；这属于必须保留的环境记录，不是多余物 |
| `tools/shot.ps1` | 8.0 KB | `CONVENTIONS.md:133,141`；`HANDOVER.md:59,86`；`DESIGN.md:177`；`HANDOVER-*-R56.md:12,68,69`；`HANDOVER-*-R59.md:107` | 截图工具：31 个参数驱动 `--screenshot`，把视觉树渲染成 PNG | **保留**（视觉纪律的执行入口） |
| `tools/seed-demo-data.ps1` | 16 KB | `CONVENTIONS.md:138`；`HANDOVER.md:84`；`HANDOVER-*-R59.md:107,127` | 给隔离数据目录灌演示数据（4 版本 / 6 模组 / 世界 / 账户 / 服务器），避免截图只能看到空状态 | **保留** |

**无 `scripts/` 目录**（不存在），无孤儿脚本。

---

## G. 需要用户裁决的 Top 10

| # | 事项 | 数字 | 三个可选动作 | 推荐 | 依据 |
|---|---|---|---|---|---|
| 1 | docs/TAKEOVER.md 不存在但被 19 处引用，`README.md:34` 让新接手者第一步就读它 | 19 处 | ① 重建一页式接手页 ② 把 19 处引用改指向 docs/HANDOVER.md ③ 只在引用处标「已废弃」 | **②**（重建会再产生一份与 `HANDOVER.md` 竞争的总文档） | `README.md:34`、`HANDOVER.md:11` 是强入口 |
| 2 | `.probe/` 下研究底稿、冒烟报告、验收截图全部丢失，但仍被 **89 个路径 / 109 行**引用，是两个最大文档的取证基础 | 89 个死链路径 / 109 行引用 | ① 从备份找回并放进 docs/research/pcl2/ ② 加一条全局「历史记录不可复核」声明 ③ 忽略 | **①，找不回则②** | `EXTENSIONS.md`(127 KB)、`PCL2-PORTING.md`(166 KB) 的核心数字都来自它们 |
| 3 | 旧交接 R16–R41 共 17 份不存在却被引用（含带行号的精确引述） | 27 处 | ① 从 git/备份恢复 ② 改为「原文已不在仓」并删链接 ③ 保留现状 | **②** | `HANDOVER.md:1449-1557` 整段 §11 都指向死链 |
| 4 | 6 份单轮交接 R54–R59 在处理层已过期 | 6 份 / **53.9 KiB**（55237 B） | ① 全归档到 docs/archive/handover/ ② 只归档 R54/R55/R56/R57/R58、留 R59 ③ 全留 | **②**（留当前轮 R59，其余归档） | R57/R58 零在册引用；`HANDOVER.md:8-9` 只引 R54/R56/R59 |
| 5 | docs/FINAL-SCOPE.md:76 写「这不是 git 仓库」与事实矛盾 | 1 处 | ① 直接改 ② 保留并加「已过时」标注 | **①** | 已有 1 commit + 423 跟踪文件 + origin remote |
| 6 | 仓外 `pub/r61` 与 `pub/r60` 逐字节相同 | 25 MB 重复 | ① 删 `r61` ② 删 `r60` ③ 都留 | **①** | 3 个文件 MD5 全同 |
| 7 | 仓外 `.probe` 截图有重复、运行期数据可重生 | 9.2 MB | ① 归档 2 张 PNG 进 docs/evidence/ 并删其余 ② 全留 ③ 全删（含丢掉唯一视觉证据） | **①** | `r59-home.png` 与 `r59-home-final.png` MD5 相同；`data-r59` 可由 `seed-demo-data.ps1` 重生 |
| 8 | `AGENTS.md` + `docs/agents/` 4 文件未跟踪 | 5 个 / 8.2 KB | ① 提交 ② 不提交 | **①** | 这是仓库规范文件，且被 `AGENTS.md` 互相引用 |
| 9 | `tools/ApiDump`（45 行）与 `tools/NeoForgeProbe`（271 行）不在 `.sln`、几乎无引用 | 2 个工程 / 33 KB | ① 都删 ② 都留 ③ 只留 `NeoForgeProbe` | **③**（`NeoForgeProbe` 是 271 行桥接逻辑的唯一复现器；`ApiDump` 是一次性调研工具） | 引用数分别 1 / 1，且都是「目录清单」或「注释」级 |
| 10 | `REVIEW-R43-R51-CODE.md` 被引用但从未落盘（原文自述「本轮结束时仍在进行」） | 2 处 | ① 补档 ② 删引用 ③ 声明历史 | **②** | `AUDIT-2026-10-23.md:261` 自己承认未完成 |

---

## H. 建议的批量动作

以下命令序列**仅供裁决后执行，本盘点未执行任何一条**。命令均为 Windows `bash` / `git` 写法，工作目录 `D:\agent_workspace\NebulaLauncher`。

### H.1 立即可做（零风险，不删内容）

```bash
# 1) 让仓库规范文件进跟踪（G-8）
cd /d/agent_workspace/NebulaLauncher
git add AGENTS.md docs/agents/domain.md docs/agents/issue-tracker.md docs/agents/triage-labels.md docs/agents/workspace.yaml
git status --porcelain          # 期望：5 个 A/M，无 ??

# 2) 删构建产物（460 MiB，全部在 .gitignore 内，可随时重建）
rm -rf src/NebulaLauncher.App/bin src/NebulaLauncher.App/obj \
       src/NebulaLauncher.Core/bin src/NebulaLauncher.Core/obj \
       tests/NebulaLauncher.Tests/bin tests/NebulaLauncher.Tests/obj \
       tools/TestRunner/bin tools/TestRunner/obj

# 3) 删仓库内空 pub/ 目录（0 文件，git 本就不跟踪）
rmdir pub    # 注意：若 build.ps1 -Publish 需要它，build 会自建
```

### H.2 归档（先建目录，再移动，保留 git 历史）

```bash
cd /d/agent_workspace/NebulaLauncher
mkdir -p docs/archive/handover docs/archive/reviews docs/evidence/screenshots docs/research

# 单轮交接归档（G-4）
git mv docs/HANDOVER-2026-10-24-R54.md docs/archive/handover/
git mv docs/HANDOVER-2026-10-24-R55.md docs/archive/handover/
git mv docs/HANDOVER-2026-10-24-R56.md docs/archive/handover/
git mv docs/HANDOVER-2026-10-24-R57.md docs/archive/handover/
git mv docs/HANDOVER-2026-10-24-R58.md docs/archive/handover/
# 保留 docs/HANDOVER-2026-10-24-R59.md（当前轮）

# 历史审查与中断记录归档
git mv docs/REVIEW-R34-R36-CODE.md docs/archive/reviews/
git mv docs/REVIEW-R35-DECISIONS.md docs/archive/reviews/
git mv docs/R31-BROKEN-WORK.md docs/archive/

# 仓外唯一视觉证据进仓（G-7）
cp /d/agent_workspace/.probe/r59-home.png docs/evidence/screenshots/r59-home.png
cp /d/agent_workspace/.probe/r61-home.png docs/evidence/screenshots/r61-home.png
```

### H.3 删仓外重复与暂存物（G-6 / G-7）

```bash
# 删与 r60 逐字节相同的 r61，以及空目录
rm -rf /d/agent_workspace/pub/r61 /d/agent_workspace/pub/r59 /d/agent_workspace/pub/r62

# .probe：先确认截图已复制进仓，再清空
rm -f  /d/agent_workspace/.probe/r59-home-final.png   # 与 r59-home.png MD5 相同
rm -rf /d/agent_workspace/.probe/data-r59              # 可用 tools/seed-demo-data.ps1 重生
# 若 G-2 的找回失败，最后再执行：
# rm /d/agent_workspace/.probe/r59-home.png /d/agent_workspace/.probe/r61-home.png
```

### H.4 引用修复（纯文本编辑，按 G-1 / G-3 / G-5 / G-10）

```bash
# 先看清所有待改行
grep -rn "TAKEOVER\.md" --include='*.md' .                 # 19 处
grep -rn "HANDOVER-2026-10-2[234]-R[0-9]*\.md" --include='*.md' docs/HANDOVER.md   # §11 整段
grep -rn "REVIEW-R43-R51-CODE" --include='*.md' .          # 2 处
grep -n  "这不是 git 仓库" docs/FINAL-SCOPE.md              # 1 处

# 改完自检：下面应为 0
grep -rhoE '[A-Za-z0-9_./-]+\.md' --include='*.md' . | sed 's|^\./||' | sort -u \
  | while read -r r; do [ -e "$r" ] || echo "MISSING: $r"; done | grep -v '\.probe/' | wc -l
```

### H.5 提交

```bash
cd /d/agent_workspace/NebulaLauncher
git status --porcelain
git commit -m "chore: 归档单轮交接与历史审查，清理失效引用与重复产物"
```

### H.6 执行顺序与依赖

| 顺序 | 步骤 | 前置 | 失败后果 |
|---|---|---|---|
| 1 | G-2 找回 `.probe` 底稿 | 无 | 拿不到就只能走「声明不可复核」分支；其余步骤不受阻 |
| 2 | H.1（提交规范文件 + 删构建产物） | 无 | 无 |
| 3 | H.2（归档 + 截图进仓） | 需 1 的结论 | 直接归档会让 C.5 的 89 个死链路径永久留在文档里 |
| 4 | H.4（引用修复） | 需 3 的最终路径 | 路径写错会再生新死链 |
| 5 | H.3（删仓外重复） | 需 3 确认截图已复制 | 早于 3 会丢唯一视觉证据 |
| 6 | H.5（提交） | 需 2–5 | — |
