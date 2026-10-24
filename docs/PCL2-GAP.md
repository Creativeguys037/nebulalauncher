# Nebula × PCL2 能力缺口清单

> **历史清单，当前状态见 [逐项核查报告](AUDIT-2026-10-23.md)。** 新报告保留原 1–13、R1–R24 编号逐项复核。“源码里存在实现”不是完成验收：本轮发现并修正整合包、Java、参数、下载接线等问题；其余缺口与 bug 单列。下文各轮“全部落地/唯一判据”不再作为当前项目总体结论。
>
> **R34（2026-10-24）**：按 AUDIT 第四节修掉 6 条缺陷（统一 launcher 工厂 / 取消等待移交后台 / verdict 贯通 /
> CF 指纹关联 / 关闭异步 + 剪贴板退出码 / 服务器 favicon），全量 2172/2172。剩余缺口见 AUDIT §四、§五与
> `docs/HANDOVER-2026-10-24-R34.md` §4。
>
> **R35–R37（2026-10-24）**：R35 修备份并发/meta 提交与下载退出确认；R36 按独立复核补导出三类文件、
> `modpack.zip` 明确拒绝、加载器候选可回滚；R37 补两条真机验收并对剩余 L 级项（组件快照对比、
> 导出 zip/逐项勾选）给出不实现裁决（`HANDOVER-2026-10-24-R37.md` §3/§4）。全量 2211/2211。

调研对象：`D:\agent_workspace\PCL\Plain Craft Launcher 2\`（PCL2 2.99.0.0 VB.NET 源码）与
`NebulaLauncher`（Avalonia 11.3 + .NET 8，11 页 / 1189 例测试）。

**取证说明**：本轮开始时 `D:\agent_workspace\PCL2-Source\src\` 与 `PCL2-decompiled\raw-ilspy\` 均不可用
（前者首条命令可读、随后消失；后者在调研中途被移除，只剩 WPF 页面骨架）。因此 PCL2 侧证据一律取 VB.NET 原源码，
**行号只写本轮实读/实 grep 得到的**，其余一律用 `文件:方法名` 形式。Nebula 侧每条都打开源码确认——
"文档写了"不算"已实现"，本轮就查出两处反例（见第 4 节）。

**判定口径**：只有 Nebula 源码里找不到实现才算缺口。PCL2 有而 Nebula 用别的形态等价解决的
（多游戏目录 → 实例隔离，翻页 → 加载更多），进第 3 节。

---

## 0. 结论（3–5 条最值得做）

### 0.1 落地情况（第十六轮起，2026-10-23 第二十八轮收尾）

本清单的 13 条**全部落地**。第 12 条分两步完成——第十六轮到「识别 + 装对版本与加载器」，
第二十轮补上 `addons` 逐项反查；第 13 条在第二十八轮补齐。

| # | 能力 | 状态 | 落点 |
|---|---|---|---|
| 1 | 整合包导入自动装游戏与加载器 | ✅ 落地 | `Backups/ModpackService.cs`（装游戏 → 解压 → 下载 → 写实例，失败不留半成品） |
| 2 | 启动前注册高性能 GPU | ✅ 落地 | `Platforms/PlatformService.cs:419`、`Launching/GameLauncher.cs:308`、设置页开关 |
| 3 | Fabric API 随 Fabric 附带安装 | ✅ 落地 | `Installing/InstallService.cs:445`、下载页开关 |
| 4 | 内容删除进回收站 | ✅ 落地 | `Mods/ContentService.cs`（删除/恢复/彻底删除，实例回收站范式复用） |
| 5 | 模组列表状态筛选 | ✅ 落地 | `ModsViewModel` 四档筛选（全部/已启用/已禁用/可更新） |
| 6 | 模组一键更新 | ✅ 落地 | `Mods/ModUpdateService.cs`（下载 → 校验 → 旧文件进回收站 → 段对齐替换） |
| 7 | HMCL 整合包格式导入 | ✅ 落地 | `ModpackService` 的 `HmclZip` 分支（`minecraft/` 覆盖层 + `gameVersion`） |
| 8 | 在线整合包浏览与安装 | ✅ 落地 | `Mods/ModpackCatalogClient.cs` + 资源页第五个分区 + `.mrpack` 导入链 |
| 9 | Forge 推荐版标记 | ✅ 落地 | `LoaderMetaFetcher.ForgePromotions` + 三级缓存 + 下拉徽标 |
| 10 | `.old` / `.litemod` 识别 | ✅ 落地 | `ContentService.HasDisabledSuffix` / `IsContentFile` 多后缀 |
| 11 | 客户端版本「仅下载不安装」 | ✅ 落地 | `VersionService.SaveClientJarAsync` + 版本行溢出菜单 |
| 12 | MCBBS 整合包格式 | ✅ 落地 | 判定（`mcbbs.packmeta` / 带 `addons` 的 `manifest.json`）+ 版本与加载器 + **`addons` 逐项反查**（CurseForge 元数据 → forgecdn 直链 → Modrinth 兜底，全失败进「待手动安装」清单） |
| 13 | 双源合并搜索 | ✅ 落地（第二十八轮） | `Core/Mods/CrossSourceSearch.cs`（合并/去重/加权排序全在纯函数里，17 例）+ 资源页第三档「双源」；CurseForge 带内置 key 走官方搜索，档位只受服务是否接线影响 |

第 12 条的 `addons` 反查在第二十轮补齐：解析按 `ModModpack.vb:714-725` 的读法（数组 / 字典两种形态、
`id`+`version`+可选 `fileName`），解析出的引用按「CurseForge 元数据 → forgecdn 直链（按 fileId 推导）→ Modrinth」
三步反查；三步全失败只进 `PendingAddons`，导入本身仍算成功。UI 在导入完成卡片上给「查看清单」出口，
每条带「打开页面」。未验证真实 MCBBS 样本（手上没有）。

第 13 条在第二十八轮落地。此前的延后理由是「没有内置 key 时 CurseForge 只有按项目 id / 文件 id 查元数据的端点，
没有搜索端点」；key 内置后这条限制消失，两站都走完整搜索，「双源」档位只受服务是否接线影响
（不可用时说明原因，不静默降级成单源，否则用户会以为 CurseForge 侧确实没有结果）。合并算法照 PCL2
`Modules/Resource/ResourceSearcher.vb` 移植：平台加权（模组 CF 1 : MR 5，数据包 CF 10 : MR 1，
资源包/光影 CF 1 : MR 4）、`IsLike` 跨站去重（同名 + 更新时间差 ≤ 7 天 + 版本集合相同 + 非光影）、
CurseForge 先入列表保证排名稳定。约束写在 `HANDOVER.md` §6.47。

### 0.1.1 第二轮复查找出的 10 条（第二十八轮，全部落地）

`PCL2-GAP.md` 的 13 条清空之后，第二十八轮又对 PCL2 源码做了一次复查（取证记录在
`D:\agent_workspace\.probe\pcl-gap-r28.md`，覆盖 `ModLaunch.vb` 的参数组装、`McLaunchPrerun` /
`McLaunchRun` / `McLaunchPrecheck`、`Settings.vb` 的默认值、`ModWatcher.vb`、`FormMain.xaml.vb`、
`PageInstanceMod.xaml.vb`），又查出 10 条真缺口，本轮一并做完：

| # | 能力 | PCL2 出处 | 落点 |
|---|---|---|---|
| R1 | 终止运行中的游戏 | `FormMain.xaml.vb:BtnExtraShutdown_Click`、`ModWatcher.vb:WatcherStateChanged` | 版本页「运行中」徽标旁 + 首页运行态；`KillCurrentGame` → `GameLauncher` → `ProcessWait` 复核 |
| R2 | 启动器默认注入的四条 JVM 参数 | `PageSetup/Settings.vb` 的 `LaunchAdvanceJvm` 默认值 | `JvmArgumentBuilder.BuildCompatibilityArguments` + `AppSettings.RelaxForgeChecks` 开关 |
| R3 | 启动参数去重与覆盖 | `ModLaunch.vb:DeduplicateJavaArguments` | `Core/Launching/LaunchArgumentDeduplicator.cs`（含「吸收」窗口参数） |
| R4 | authlib 的 Windows 信任库参数 | `ModLaunch.vb:McLaunchArgumentsJVM`（`5252#`） | `AuthlibInjectorService.BuildTrustStoreArguments`（仅 Windows + 仅 Authlib 账户） |
| R5 | 多实例并行运行 | `ModWatcher.vb:McWatcherList`、`McInstance.TryAcquire` | `RunningGameRegistry` + 按目录准入 + 按会话停止（§6.48） |
| R6 | Java 24+ 压缩对象头 | `ModLaunch.vb:McLaunchArgumentsJVM` | `JvmArgumentBuilder.BuildCompactObjectHeadersArguments`（≥24，24 配实验解锁） |
| R7 | 内存占用三段可视化 | `PageSetupLaunch.xaml` 的 `RectRamUsed` / `RectRamGame` / `RectRamEmpty` | `Core/Launching/MemorySnapshot.cs` + 设置页内存分区（§6.46） |
| R8 | 模组卡片「去资源站查找」 | `PageInstanceMod.xaml.vb:BtnManageDownload_Click` | `ModsViewModel.LookupInLibrary` + `LibraryLookupSlot` 单槽（§6.45） |
| R9 | `--versionType` 自定义（主菜单/F3 的版本名） | `ModLaunch.vb:1529-1542` | `AppSettings.VersionArgumentInfo` + `InstanceInfo.VersionType`（三态）+ `GameLauncher.StripEmptyVersionType`（空值摘两个 token）+ 设置页与实例面板两处 UI |
| R10 | `options.txt` 语言分版本规则 | `ModLaunch.vb:1844-1864` | `GameLauncher.ApplyChineseLanguageCore`（≤10 大写否则 1.1~5 NPE 崩溃、`saves` 判据、写两次触发缓存、Yosbr 兼容） |

R9/R10 来自同一份复查记录的「未确定」5 条里的第 3、5 条——复核 PCL2 原文后确认都是真缺口。
另外 3 条（Natives 三级回退与短路径、游戏目录含非 ASCII、实例列表缓存文件）的结论是**不是缺口**：
前两条属「依赖库已管 / 现代运行时无问题且无法构造样本」，第三条是性能项而非功能缺口。

14 条「已确认等价」（避免下轮重复调研）与上面 3 条「不是缺口」的详细依据见
`.probe/pcl-gap-r28.md` 第 2 节与第 4 节。

### 0.1.2 第三轮复查找出的 7 条（第二十九轮，全部落地）

第一轮只覆盖了 `ModLaunch.vb` 的参数组装与几个页面，还有一半模块没查。第二轮把
`ModDownload.vb`（1389 行全读）、`ModJava.vb`、`ModCrash.vb`（`CrashReason` 全表）、
`ModModpack.vb`（866 行）、`Modules/Resource/*`、`Pages/PageDownload/*`、`Pages/PageInstance/*`、
`Pages/PageLaunch/*`、`Pages/PageSetup/*`、`Modules/Base/ModNet.vb` 全部过了一遍，
取证记录在 `D:\agent_workspace\.probe\pcl-gap-r29.md`。

另有一条**复查方法本身的修正**：PowerShell 的 `Select-String -Path 'src\**\*.cs'` 里 `**` 只等价一层目录，
会漏掉 `src\<项目>\<子目录>\*.cs`。本轮改用 `Get-ChildItem -Recurse`，因此推翻了两条初判
（「导出启动脚本」与「服务端下载 UI」其实都已实现）。

| # | 能力 | PCL2 出处 | 落点 |
|---|---|---|---|
| R11 | 崩溃归因「极短的程序输出」 | `ModCrash.vb:AnalyzeCrit3`（:649-660） | `CoreLaunch_CrashRulesTests` + `CrashRules.cs`：五条件（无 `at net.` / 无 `INFO]` / 无 hs_err / 无 crash report / 长度 < 100）同时成立，排在 75 条具体规则之后 |
| R12 | 崩溃归因「版本 JSON 中存在多个 Forge」 | `ModCrash.vb:AnalyzeCrit1`（:491） | `CrashRules.cs`：精确整串 `Found multiple arguments for option fml.forgeVersion, but you asked for only one`。修复前这份日志被堆栈归因吃掉（把 `joptsimple` 当肇事包） |
| R13 | 下载源按「响应头超时」换源 | `ModDownload.vb:DlSourceLoader`（:1317-1380） | `DownloadInfrastructure.cs`：镜像元数据 5 秒 / 官方元数据 30 秒 / 资源文件 30 秒；超时仍抛 `HttpRequestException` 让 `DownloadQueue` 重试（§6.54） |
| R14 | 服务端下载生成 `Launch Server.bat` | `ModDownloadLib.vb:McDownloadMenuSaveServer`（:183-199） | `ServerDownloadService.cs`：GB18030 按 `GetACP()` 自适应、`"java"` 裸命令、EULA 说明、已存在不覆盖；「复制启动命令」与 bat 同源（§6.55） |
| R15 | rar / 7z 压缩包给可操作提示 | `ModModpack.vb:78-86` | `ModpackService.DescribeOpenFailure`：按扩展名分支，提示「解压后重压 zip」，不再透传英文异常 |
| R16 | Java 上限的加载器小版本粒度 | `ModJava.vb:GetJavaRequirement` | `JavaRequirementTable.LoaderRules`（7 条）+ `JavaLoadLimit`（补丁号级只建议、大版本级才阻断）+ `LoaderVersion` 全链路（§6.56 / §6.57） |
| R17 | 实例级登录服务器（authlib 地址 / 注册链接 / 名称） | `Settings.vb` 的 `VersionServerAuth*` 三项 + `PageLoginAuth.xaml.vb:22-30` | `InstanceInfo.AuthServerUrl/AuthRegisterUrl/AuthServerName` 三态 + `InstanceAuthServerUrl` → `GameLauncher` 用 `??` 选地址 + 实例面板「登录服务器」卡片（§6.58） |

第 8 条候选（`java_launch_wrapper` / LWJGL Unsafe Agent）**裁决不做**：要随启动器分发第三方二进制
（PCL2 源码树里也没有那两个文件，是从嵌入资源解出来的）、`-javaagent` 与 authlib 冲突、
收益场景窄。已记入 `TAKEOVER.md` §5。

另有 8 条「已确认等价」（导出启动脚本、服务端下载 UI、启动前后命令、实例级 `--versionType`、
`.litemod`/`.old`/`.disabled` 识别、本地文件 hash 反查、实例收藏、Java 自动安装与探测、lib/assets 校验、
整合包格式判定序）与 7 条「明确不做」、5 条「未确定」，见 `.probe/pcl-gap-r29.md` 第 2–4 节。

### 0.1.3 第四轮：覆盖率盘点查出的 5 条（第三十轮，全部落地）

前三轮都在「参数组装 / 崩溃 / 下载 / Java / 实例」这条线上打转，**没人统计过 113 个 `.vb` 到底查过多少个**。
这一轮先做盘点：`.probe/pcl-coverage.md` 逐文件记录 113 个 `.vb` / 43116 行的状态，再对七个未覆盖逐个判定。
（该表第三十二轮按行重算过：原写「62/20/21/7/3」虚高，实测 **49/46/11/0/7**。）
**这张表是「还有没有没抄的」的唯一判据，下次接手先看它。**

| # | 能力 | PCL2 出处 | 落点 |
|---|---|---|---|
| R18 | CurseForge 文件列表翻页取全 | `Modules/Resource/ResourceVersion.vb:236-261`（`resultCount == totalCount` 校验 + 3 次重试，issue #6224；`DistinctBy(Id)` 去重，issue #1330） | `CurseForgeService.GetFilesAsync` 翻页循环，**终止判据用服务端实际返回条数推进、对每页上限不敏感**（§6.61）。实测 JEI 从 50 条 → 3950 条 |
| R19 | Modrinth 服务端插件识别 | `ResourceVersion.vb:189-195` 三段判定（`bukkit/folia/paper/purpur/spigot` → Plugin、`datapack` → DataPack、有 loader → Mod） | `ContentKind.Plugin = 5` + `ServerPluginPolicy`；识别读**原始 `categories`**（`display_categories` 里没有 loader）、`WebUrl` 走 `/plugin/`、拒绝在任何 IO 之前（§6.62） |
| R20 | 插件的两个入口两种策略 | 同上 | 搜索安装**拒绝**（用户能改主意）、整合包导入**落 `plugins/`**（批量还原，拒绝会让整包残缺）。判断依据是用户意图不是技术类型（§6.63） |
| R21 | 文件 / 目录名校验补全 | `Modules/Base/ModValidate.vb:291`（22 项保留名）、`:294`（NTFS 8.3 短名，issue #4505） | `PathSafety` 补 `CLOCK$` / `COM0` / `LPT0`（`$` 是合法字符，只能整名比较）+ `.{2,}~\d+`（序号可多位，比 PCL2 的 `\d` 宽一档，§6.65） |
| R22 | 插件提示条与整合包落点 | `PageDownloadModResource.xaml.vb` 的详情区说明 | `LibraryView.axaml` 绑 `HasServerPluginNotice` / `ServerPluginNotice`；`ModpackService.AddonDirectoryOf(Plugin)` → `plugins/` |

第七个未覆盖 **联机功能**（`Pages/PageLink/PageLinkMain.xaml.vb`，1116 行 / 12 个子系统）**裁决不做**，
三条前置阻塞：上游在非官方分支直接 `Throw`「开源版无法联网获取配置」(`:429-431`)、
配置与 EasyTier 二进制都来自 PCL 官方服务器、要分发第三方二进制。功能拆解与 6 个可独立交付切片
（M+M+L+M+S+S）留在 `.probe/pcl-gap-r30.md` §2.1，将来要做时直接照它分段。

另三块盲区判**不是缺口**，证据链在 `.probe/pcl-gap-r30.md` §2.4 / §2.7 / §2.8：
`ModEvent`（WPF 附加属性式声明绑定，Avalonia 用 `ICommand` 等价）、
`FormMain` 的更新部分（`ModSecret.vb` 全文是开源版空桩——`UpdateCheckByButton` 只 `Hint("该版本中不包含更新功能……")`、
`GetIdentify` 返回 `"0000-0000-0000-0000"`、`:451-453` 启动时弹窗列出被剥离的特性）、
`PageSetupSystem`（9 个入口：4 等价、5 明确不做，其中导出/导入设置在 PCL2 里只弹「功能尚在开发中」）。

### 0.1.4 第五轮：机械比对查出的 2 条（第三十二轮，全部落地）

第三十轮盘完覆盖率表后，「再复查一遍」的收益已接近零。这一轮换方法：不逐行读 VB，而是抓 PCL2 的
**155 个 URL 端点**与 **37 处 `OpenWebsite(...)` 外链调用**逐个比对 `src/`，再用「落点存在性」
验证覆盖率表里 146 个反引号落点。查出两条真缺口，都已落地：

| # | 能力 | PCL2 出处 | 落点 |
|---|---|---|---|
| R23 | 版本清单四档分类（正式 / 快照 / 远古 / 愚人节） | `Pages/PageDownload/PageDownloadInstall.xaml.vb:452-478` | `Models/Versions.cs` 的 `VersionChannelClassifier`：`1.` 开头且不含 combat/rc/experimental/pre 的 snapshot 纠偏成正式版 → 11 个愚人节 id 白名单 → UTC+2 的 4/1 兜底；下载页加两档筛选，三处显示共用（§6.66） |
| R24 | 在 MC 百科搜索 | `PageInstanceMod.xaml.vb:772` | `ModsViewModel.SearchMcmodCommand` + `McmodSearchUrl`（`site=all&filter=0` 照抄），搜索词复用 `BuildLookupQuery`（§6.67） |

同轮纠正覆盖率表：6 类落点类型名（`Card.cs` / `DialogService` / `EmptyState` / `ItemRepeater` /
`IconButton` / `StringUtils`）在 `src/` 里不存在，已改为真实落点；9 处过期的「r28/r29 列 X 缺口」改为「已落地」。

**另两条新发现裁决不做**：本地 Mod「查看更新日志」外链（`MyLocalModItem.xaml.vb:461-480`，需先给
`ContentUpdateInfo` 扩字段，已有「去资源站查找」覆盖同一意图）、登录跳过 SSL 校验
（`ModNet.vb:1916` + `Settings.vb:123`，PCL2 用 `ServicePointManager` 全局接管，Nebula 的登录走独立 `HttpClient`）。

**结论：除已裁决不做的项之外，没有未实现的 PCL2 功能。剩下的都是验证，见 `R31-BROKEN-WORK.md` §3.3。**

### 0.2 原清单之外新增的扩展（第二十轮之后）

上面 13 条 + 第五轮的 2 条是对照 PCL2 得到的能力缺口。之后的轮次转向了 PCL2 **没有**的东西——要么是它留着不做的
（服务器玩家名单、存档字段编辑），要么是本项目自己攒出来的缺口（崩溃建议只有文案没有按钮）。

| 能力 | 出处 | 落点 |
|---|---|---|
| 资源页排序（相关度 / 下载量 / 最新 / 名称） | 自研 | `Mods/ModrinthService.cs` 的 `SearchSortedAsync`、资源页排序 chip |
| 模组卡片右键菜单（复制文件名 / 路径 / 启停） | 自研 | `ModsViewModel.CopyFileNameCommand` 等、`ModsView.axaml:634` |
| HTTP 代理（无 / 系统 / 自定义 + 连通性测试） | 自研（PCL2 无 `NetSetDefaultProxy`，已核查） | `Core/Net/{ProxyAddressParser,ProxyConfigurator,NetworkProbe}.cs`、设置页「下载」区 |
| MCBBS addons 逐项反查 | port（`ModModpack.vb:714`） | 见上方第 12 条 |
| **崩溃建议的动作按钮** | 自研（PCL2 `ModCrash.vb:340` 只有 `CrashReason` 枚举文案，不带动作） | `CrashSuggestionItem` + `HomeViewModel.RunCrashActionAsync`，六种动作可执行 |
| **服务器 Query 玩家名单** | 自研（PCL2 无服务器页） | `Core/Servers/ServerQueryService.cs`（GameSpy4 UDP）+ 服务器页「玩家」按钮 |
| **存档 level.dat 编辑** | 自研（PCL2 无存档页） | `Core/Backups/{NbtReader,NbtWriter}.cs` + `BackupService.UpdateWorldAsync` + 世界页编辑面板 |
| 页面级取消源释放（消 CA1001） | 自研技术债 | `PageViewModelBase : IDisposable` + 各页 `Dispose(bool)` 覆写 |
| **实例原地换加载器** | 自研（PCL2 只能重装或另建实例；本仓库此前只有「克隆实例再改版本」的绕路文案） | `Core/Instances/InstanceService.Loader.cs` + 版本页 `…` 菜单 |
| **Modrinth 镜像兜底** | port 思路（HMCL `HMCLDownloadProvider.java:123-150` 的 `fallback=true`） | `Downloads/DownloadInfrastructure.cs` 的 `ModrinthFallbackMap` + 设置页开关（默认关） |
| **只读游戏目录覆盖** | 自研 | `Paths/PathProbe.TryMakeWritable` + `InstallService.EnsureInstallEnvironment` 重试 + 覆盖前 `.bak` |
| **文件名/目录名统一清洗** | port 规则（PCL2 `Modules/Base/ModValidate.vb` 的 `ValidateFolderName` / `ValidateFileName`） | `Core/Paths/PathSafety.cs`，取代 5 处各写一套 |
| **实例运行锁** | port 思路（PCL2 `McInstance.TryAcquire/IsLocked` 的占用检查）+ 自研 PID/启动时间判活 | `Core/Instances/InstanceLockService.cs`、`GameLauncher` 启动前拿锁 + `finally` 释放、版本页「运行中」徽标 |
| **下载暂停 / 继续** | 自研（PCL2 的下载页没有暂停；HMCL 有） | `DownloadQueue.PauseGate` + 下载页 pill 按钮 + 状态行 |
| **存储占用与安全清理** | 自研（PCL2 无存储面板） | `Core/Maintenance/StorageService.cs` + 设置页「存储」分区 |
| **存档缩略图** | 自研（PCL2 世界页不显示图标） | `Core/Backups/WorldIconService.cs` + 世界页 48×48 缩略图 |
| **缺 Java 一键安装** | port 思路（PCL2 `ModJava.vb` 的自动下载 runtime）+ 自研判定 | `Core/Java/JavaProvisionPlanner.cs` + `LaunchResult.MissingJavaMajor` + 首页失败面板与版本页状态条下的安装按钮 |
| **日志实时跟随** | 自研（PCL2 只有窗体内嵌日志区，没有跟随开关） | `Core/Logging/LogFollower.cs` + 日志页 700ms 增量跟随 |
| **实例目录快捷芯片** | port 思路（PCL2 `PageInstanceOverall.xaml.vb` 的目录入口） | `Core/Instances/InstanceFolders.cs` + 版本页芯片行 |

十七条自研功能的关键约束，写代码时别把它们当「可以省掉的验证」：

1. **崩溃建议的禁用动作只在归因唯一时出现。** 候选不唯一时禁用谁都是猜，猜错等于把玩家的模组包砸掉。
2. **Query 只能按需查询，不能对整张服务器列表轮询。** UDP 广播式请求，批量打会被当作端口扫描。
3. **写 `level.dat` 前必须有 `.bak`，未知标签必须原样保留，`session.lock` 被占用必须拒绝。**
   写坏玩家的存档不可恢复，这三条是硬要求。
4. **换加载器只允许同 MC 版本内进行，且 Forge↔Fabric 这类互换必须过二段确认。**
   跨大版本换会让存档与 mods 同时失效；两种模组格式互不兼容，静默执行等于把玩家的模组包废掉。
5. **Modrinth 镜像只在官方连不上时才用，官方 404 绝不能触发镜像。** 404 是「项目不存在」，
   不是网络问题；若把它也当成失败去试镜像，每查一个不存在的项都要多等一整轮超时，而且会把
   「没这个项目」误报成「网络不好」。镜像开关默认关——第三方服务不能默认把用户的查询发出去。
6. **文件名清洗必须在落盘前完成，不能等 `Directory.CreateDirectory` 抛异常。**
   Windows 对结尾点号是静默吞掉、对 `CON` 这类保留名才是报错，两种失败形态不一致，
   靠捕获异常挡不住第一种。
7. **`overwriteExisting` 的默认值必须是 `false`。** 它直达 Forge/NeoForge 的 `SkipIfAlreadyInstalled`，
   默认翻转会让所有既有安装路径重新跑一遍安装器。
8. **运行锁必须按「游戏目录」而不是实例名加锁，判活必须同时比 PID 与进程启动时间。**
   没选实例时启动走全局 `.minecraft`，同一个目录可能被两条路径指向；只比 PID 会撞上系统复用，
   结果是一个早就不存在的游戏让实例再也启动不了。锁文件损坏 / 取不到进程信息时一律按僵死接管。
9. **下载暂停是闸门不是取消。** 在途请求必须下完（取消会清空批次快照、丢掉历史），
   而「不再启动新文件」要覆盖跳过 / 复用 / 重试三条路径——少一条就会出现「暂停了还在下」。
   四条收尾路径（入口 / 成功 / 取消 / 异常）都必须复位闸门，否则下一次下载一个文件都起不来。
10. **存储清理必须先 `Path.GetFullPath` 规范化再判是否在数据根之下，且只删白名单类别。**
    日志按最后写入时间保留 7 天（文件名日期不可靠），`cache/loader-install` 不进「下载缓存」类别——
    安装失败现场的日志与临时文件是排障的唯一线索。
11. **存档图标必须校验 PNG magic 与两道上限（96KB base64 / 64KB 解码），缓存按 `level.dat` 的
    (mtime, length) 失效。** `level.dat` 由游戏写、也可能被第三方工具改写，任何解析失败都要返回 null
    而不是抛——世界页上一张坏图标不该让整页列表加载失败。
12. **「一键安装 Java」只在真的能装时才给按钮。** 已装满足就不给；需求与加载器上界冲突（例如 1.12.2 Forge 要
    Java 8 却配了 21）或主版本不在可下载集合里，必须退回「手动配置」——给按钮等于把用户送去多下一次下载，
    装完还是起不来。装完的顺序是「写回 `Settings.JavaPath` → `SaveAsync` → 强制重扫」，缺最后一步会继续用旧缓存报缺 Java。
13. **日志增量读取：半行不推进偏移、截断与轮转必须自愈、单次读取封顶。** 半行推进会让一条被切开的堆栈显示两遍且
    偏移永久错位；`latest.log` 被 JVM 改名重建时不自愈就等于「跟随一次后永远不动」；不封顶则一次追几十 MB。
14. **实例目录表只存相对名，绝对路径用 `GetGameDirectory(instanceId)` 现拼，枚举时不建目录。**
    版本隔离开关会改游戏目录，缓存绝对路径必然过期；目录名与实例名可以不一致（重命名只改 `Name`）；
    `screenshots/` 天然常常不存在，扫描期替用户建目录等于改他的游戏目录。
15. **源测速必须用不带 `MirrorRewritingHandler` 的 `HttpClient`，且只提示不决策。**
    公用客户端会把官方 URL 改写成镜像，两边测出来是同一个地址，结论永远是「一样快」；
    测速结果写进设置等于把「今天的网络」变成永久配置，换网后一直误导。排序里吞吐 ≤0 要罚分，
    否则除零得 ∞ 会被当成最快。
16. **代理密码落盘只允许密文，明文只活在内存里。** 保存走副本（`PersistableCopy`），
    迁移在加载时当场落盘（多数用户此后再不点保存）；解不开按未设置、不清磁盘；密文解出来等于当前明文时保持原密文
    （DPAPI 带随机盐，重加密会让配置文件无缘无故变动）；配置包导出必须同时剥明文与密文两个键。
17. **发布必须 `-Clean`，产物要过两道关（`--probe-dir` + 干净 `--data-dir` 的 `--screenshot`）。**
    未清理 `obj/<Configuration>` 时 publish 出的 exe 会静默退出（exit 0、无窗口、不产截图），
    「退出码 0」不能当发布成功的证据。

以下为原始调研结论，保留原文以便对照。

---

1. **导入整合包装完不能玩（第 1 条）**。这个功能本身存在，但它只解压文件、下载 `index` 里声明的 jar、写
   `instance.json`，**不安装** `manifest.json` / `modrinth.index.json` 声明的那套游戏与加载器。PCL2 的五条安装链
   共用 `McInstallLoadersGet`，所以"装整合包"必然包含"装版本"。这是已有功能的闭环缺口，优先级高于本清单其余全部。
2. **启动前把 `javaw.exe` 注册为高性能 GPU（第 2 条）**。写一个 `HKCU` 值换双显卡笔记本的帧数；PCL2 在预启动阶段
   无条件做，Nebula 只在崩溃规则文案里让用户自己去系统图形设置点五次。成本 S，收益当次可见。
3. **模组一键更新真正落地（第 6 条）**。"检查更新"只产出展示行（`ModUpdateRowViewModel` 类注释写明"不在这里改任何
   模型字段"），没有下载替换动作。玩家看到"有新版本"却点不动，比没有这个功能更糟。
4. **Fabric API 随 Fabric 一起装（第 3 条）**。装 Fabric 的玩家绝大多数需要它，漏装的表现是启动直接崩在
   `NoClassDefFoundError`。PCL2 在安装页自动选中最新兼容版本。
5. **内容删除进回收站（第 4 条）**。`ContentService.DeleteAsync` 是 `File.Delete`，误删只能靠外部备份；
   Nebula 已有实例回收站的范式可复用。

---

## 1. 候选清单（表格 + 逐条说明）

排序按"价值 ÷ 成本"。价值 = 玩家能感知的改善；成本 S ≤ 半天、M ≤ 2 天、L > 2 天（单人，含测试）。

| # | 能力 | 价值 | 成本 | 一句话 |
|---|---|---|---|---|
| 1 | 整合包导入自动装游戏与加载器 | 高 | M | 现有导入流程缺最后一步，装完不能启动 |
| 2 | 启动前注册高性能 GPU | 中高 | S | 一个注册表键换双显卡机器帧数 |
| 3 | Fabric API 随 Fabric 附带安装 | 中高 | S | 漏装即崩，装机率极高 |
| 4 | 内容删除进回收站 | 中高 | S | 误删可恢复，复用实例回收站范式 |
| 5 | 模组列表状态筛选 | 中 | S | 只显示可更新 / 已禁用 |
| 6 | 模组一键更新（下载替换 + 旧文件留档） | 高 | M | 检查更新目前只有展示层 |
| 7 | HMCL 整合包格式导入 | 中高 | S | 国内第二大来源，只差一个 `modpack.json` 分支 |
| 8 | 在线整合包浏览与安装 | 高 | L | 缺第 5 个内容分类，可复用 .mrpack 导入链 |
| 9 | Forge 推荐版标记 | 中 | S | 默认选中推荐版而不是最新版 |
| 10 | `.old` / `.litemod` 识别 | 中低 | S | 迁移用户不出现"重复条目" |
| 11 | 客户端版本"仅下载不安装" | 中低 | S | 给别人发客户端 / 离线留档 |
| 12 | MCBBS 整合包格式 | 中 | M | 依赖 addons 列表逐项反查 |
| 13 | 双源合并搜索 | 中 | L | 同一次搜索命中两个站并去重 |

### 1. 整合包导入后自动安装游戏与加载器

- **能力**：导入时按 `manifest.json` 的 `minecraft.version` + `modLoaders[].id`（或 `modrinth.index.json` 的 `dependencies`）解析出 MC 版本与加载器版本，先装"原版 + 加载器"，再解压 overrides。
- **PCL2 出处**：`ModModpack.vb:184-204`（解析 `minecraft.modLoaders`，`forge-` / `neoforge-` / `fabric-` 前缀，未知加载器走 `NotifyIncompatibleLoader`）；`:316 / :449 / :520 / :653 / :739` 五种格式的安装链都插 `McInstallLoadersGet(New McInstallRequest ...)`。
- **Nebula 现状**：`Core/Backups/ModpackService.cs:99-139` 只有"解压 → 下载 index 文件 → 写 `instance.json`"三步，构造函数（`:30-40`）没有 `IInstallService` / `IVersionService` 依赖；`ModpackPreview.LoaderVersion`（`Models/Content.cs:285`）已解析出来但全仓无人读取。
- **价值**：导入完成后实例可直接启动。现在导入 Forge 整合包得到的是"有一堆 mod、但指向一个不存在的 Forge 版本"的实例，玩家得自己去版本页再装一次，且版本号常装错。
- **成本**：M。触及 `Backups/ModpackService.cs`、`Backups/BackupsServiceCollectionExtensions.cs`（DI）、`App/ViewModels/WorldsViewModel.cs:852-896`（进度与失败文案）。
- **契约**：不动 `Abstractions/`（`IVersionService.InstallAsync` 已够用）。**风险**：加载器安装失败时覆盖文件已落盘，需要"部分成功 + 清理"语义——PCL2 用 `ModDownloadLib.vb:McInstallFailedClearFolder` 兜这一层，Nebula 没有对应物。

### 2. 启动前把 Java 注册为高性能 GPU

- **能力**：启动前写 `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` 下以 `javaw.exe` 全路径为名、值为 `GpuPreference=2;` 的项，让 Windows 把游戏分到独显；设置页给一个开关（默认开）。
- **PCL2 出处**：`ModMain.vb:802-813 SetGPUPreference`（值格式、幂等检查、失败抛异常）；`ModLaunch.vb:1746-1766` 在 `McLaunchPrerun` 里按 `LaunchAdvanceGraphicCard` 调用，失败会尝试提权重启自身再试一次。
- **Nebula 现状**：全仓 0 命中 `GpuPreference`；`CrashRules.cs:638 / 736` 只在文案里教用户去"系统 → 图形设置"手动指定 `javaw.exe`；`Abstractions/Infrastructure.cs:91-100` 的 `IPlatformService` 没有任何注册表项方法。
- **价值**：Intel 核显 + 独显的笔记本上帧数直接变化；启动器替用户做完那五次点击，且玩家不需要知道什么是"图形设置"。
- **成本**：S。`Platforms/PlatformService.cs` 加实现、`Abstractions/Infrastructure.cs` 加带默认实现的接口成员、`Models/Settings.cs` + `SettingsService.Normalize` 加开关、`Launching/GameLauncher.cs:1154 PrepareProcess` 调用。
- **契约**：动 `Abstractions/`（`TrySetGpuPreference(string exePath)`，`PlatformService` 是唯一实现类，默认实现返回 false）。**风险**：非 Windows 无此概念，默认实现必须静默返回；只写 `HKCU`，不碰 `HKLM`（避免提权与"改了系统设置"的观感）。

### 3. Fabric API 随 Fabric 一起安装

- **能力**：安装页选中 Fabric 后，自动列出与该 MC 版本兼容的 Fabric API 版本并默认选中最新一个，随加载器一起装进 `mods/`。
- **PCL2 出处**：`PageDownloadInstall.xaml.vb:1014-1019`（`AutoSelectedFabricApi` 首次加载即选中列表第 0 项）、`:929-942 IsFabricApiCompatible`（按 `[1.20.1] Fabric API 0.92.2+1.20.1` 这类显示名解析兼容性）、`:1141` 把 `fabricApiVersion` 传进安装请求。相关 loader：`:78 / :90 / :987`。
- **Nebula 现状**：全仓只有注释与搜索热词提到 Fabric API（`Mods/ModrinthInstallPlanner.cs:203` 解释 Quilt 为何要放宽 fabric 前置），`Installing/InstallService.cs:348 InstallFabricLikeAsync` 装完加载器就结束，没有任何随附下载分支。
- **价值**：新手装 Fabric 后大半模组需要它；漏装的表现是启动崩在 `NoClassDefFoundError`，而日志里那行类名对新手毫无指向性。
- **成本**：S。`InstallService.InstallAsync` 加可选参数、复用已存在的 `IModrinthService.SearchAsync` / `GetVersionsAsync` / `InstallAsync`、`VersionsViewModel` 安装面板加一行开关。
- **契约**：动 `Abstractions/VersionServices.cs`（`InstallAsync` 加可选参，新成员必须带默认值否则所有测试桩失效）。**风险**：只走 Modrinth（PCL2 也只查一处），没有为 Fabric API 建 CurseForge 的 classId 常量；Fabric API 的 Modrinth slug 固定为 `fabric-api`，写死可接受但要注释来源。

### 4. 内容删除进回收站

- **能力**：删除模组 / 资源包 / 光影 / 数据包时移到 `<data>/trash/contents/`，列表里可恢复或彻底删除；按 Shift 才永久删除。
- **PCL2 出处**：`PageInstanceMod.xaml.vb:666-683 DeleteMods`——`FileUtils.Delete(path, Not IsShiftPressed)`，第二参数即"送系统回收站"，Shift 才直删；批量与单项删除共用这一条路径。
- **Nebula 现状**：`Mods/ContentService.cs:162-184 DeleteAsync` 直接 `File.Delete` / `Directory.Delete`；实例侧已有自建回收站（`Instances/InstanceService.Trash.cs` 的 `ListTrashAsync` / `RestoreFromTrashAsync`），但两者不共用任何代码。
- **价值**：误删一个 200 模组的目录不再等于重装；"更新完崩了，换回旧版"这个场景才成立（与第 6 条互为基础）。
- **成本**：S。`ContentService.DeleteAsync` 改移动、`Abstractions/ContentServices.cs` 加三个成员、UI 复用 `VersionsView` 的回收站浮层。
- **契约**：动 `Abstractions/`（`IContentService` 加 `ListTrashAsync` / `RestoreAsync` / `PurgeTrashAsync`，带默认空实现）。**风险**：跨卷移动退化为"复制 + 删除"，大文件耗时；必须保留彻底删除出口，否则磁盘只增不减。

### 5. 模组列表状态筛选

- **能力**：除文本搜索外，按"全部 / 已启用 / 已禁用 / 可更新"四档过滤，与批量勾选联动。
- **PCL2 出处**：`PageInstanceMod.xaml.vb:427-440 CanPassFilter`（四档 + `FilterType` 枚举）、`:443 ChangeFilter`、`:134`（列表渲染前先过筛）。
- **Nebula 现状**：`App/ViewModels/ModsViewModel.cs:1379-1395 ApplyFilter` 只认 `SearchText` 与 `ActiveKind`（分类 chip）；`_hasUpdates` 只控制"更新列表"面板是否展开，不影响主卡片列表。
- **价值**：几十上百个模组时，"哪些被禁用了""哪些能更新"目前只能肉眼翻卡片；这是批量操作的前置能力。
- **成本**：S。`ModsViewModel.ApplyFilter` + `ModsView.axaml` 加一行 chip；"可更新"态从既有 `Updates` 集合派生，不新增网络请求。
- **契约**：不动 `Abstractions/`。**风险**：`ApplyFilter` 结尾会调 `PruneSelection`（定义 `:550`、调用 `:1393`）裁掉不可见项的勾选——必须保留，否则"筛选状态下批量删除"会误删看不见的项。

### 6. 模组一键更新（下载替换 + 旧文件留档）

- **能力**：对"有新版本"的条目执行下载、按 `sha1` 校验、替换旧文件、旧文件进回收站；更新前提示"可能崩存档"，且只作用在已勾选项上。
- **PCL2 出处**：`PageInstanceMod.xaml.vb:528-531 BtnSelectUpdate_Click`（只取"已选中且 HasUpdate"）、`:538-560 UpdateMods`（更新前警告 + `DownloadAvailable` 过滤 + 新旧文件名段对齐后替换）。
- **Nebula 现状**：`Mods/ModUpdateService.cs:74 CheckAsync` 只产出 `ModUpdateReport`；`App/ViewModels/ModsViewModel.cs:1580-1583` 的注释写明「只做展示：数据全部来自 `ModUpdateInfo`，不在这里改任何模型字段」，全仓没有"用 `UpdateInfo` 去下载并替换"的代码。
- **价值**：玩家看到"有新版本"能直接点完；目前这个列表只能看不能动，用户会当成缺陷报告。
- **成本**：M。`ModUpdateService.UpdateAsync`（复用 `IModrinthService.InstallAsync` / `ICurseForgeService.InstallAsync` / `ContentService.DeleteAsync`（第 4 条）/ `DownloadQueue.BeginExternalBatch`）、`ModsViewModel` 批量条加按钮、`ModsView` 确认面板。
- **契约**：动 `Abstractions/ContentServices.cs`（`IModUpdateService.UpdateAsync` 带默认"未实现"实现）。**风险**：文件名变化会在 `mods/` 留下新旧两份（PCL2 用文件名段对齐解决），不做这一步的结果是"更新完崩在重复 modid"。

### 7. HMCL 整合包格式导入

- **能力**：识别 zip 里的 `modpack.json`（HMCL 格式，含子目录形态），读 `name` / `version` / `author` / `launchInfo`（加载器与 MC 版本），解压 `.minecraft/` 覆盖层。
- **PCL2 出处**：`ModModpack.vb:44 / :55` 与 `:64 / :75`（`mcbbs.packmeta` 与 `modpack.json` 的判定顺序，含子目录探测）、`:498-520 InstallPackHMCL`（读 JSON 与 `PackInstance`）。
- **Nebula 现状**：`Models/Content.cs:261-268 ModpackFormat` 只有 `ModrinthModpack / CurseForgeZip / MultiMcZip / PlainZip`；`ModpackService.InspectAsync`（`:42-84`）按 `modrinth.index.json` → `mmc-pack.json` → `manifest.json` 判定，找不到就归 `PlainZip`——等于把 HMCL 包当普通压缩包解开，玩家得到一堆文件但没有实例元数据。
- **价值**：从 HMCL / PCL2 迁移来的人手里最多的就是这种包，国内整合包作者也大量用 HMCL 导出。
- **成本**：S。`ModpackService.InspectAsync` 加一个分支、`ModpackFormat` 追加值、`ManagedPackTypeOf`（`:517`）与 `WorldsViewModel` 的格式文案（`:1062-1065`）各加一行。
- **契约**：动 `Models/Content.cs` 的枚举（只能追加，不改已有数值——仓库既有约定）。**风险**：HMCL 的 `launchInfo` 字段名跨版本变过，必须容错读取；缺字段时降级为 `PlainZip` 而不是抛错。

### 8. 在线整合包浏览与安装

- **能力**：资源页加"整合包"分区，搜索 Modrinth/CurseForge 的 `modpack` 项目，安装时把 `.mrpack`/`.zip` 下到临时文件再走现有 `ModpackService.ImportAsync`。
- **PCL2 出处**：`PageResource.xaml.vb:58`（`BtnSearchInstallModPack` 仅在该分类出现）、`:262 BtnSearchInstallModPack_Click`、`ResourceSearcher.vb:77 / :488`（`ResourceTypes.ModPack` 参与搜索与结果合并）、`PageDownloadCompDetail.xaml.vb:385 / :393`（详情页为整合包走独立保存/安装分支）。
- **Nebula 现状**：`App/ViewModels/LibraryViewModel.cs:114-123` 只有 Mod / ResourcePack / ShaderPack / DataPack 四分类；`Models/Modrinth.cs:65-70 ProjectTypeOf` 只映射四类；`IModrinthService` / `IContentService` 无任何 `modpack` 分支。
- **价值**：这是启动器最主流的获客路径——玩家想玩"整合包"而不是"一个个装模组"。现在只能从外部下载 zip 再拖进来。
- **成本**：L。两种做法：(a) 加 `ContentKind.ModPack`，要同步改 `DirectoryNameFor`、`ContentTarget.DirectoryNameOf`、扫描、`FindVersionMismatch` 等所有 `switch`（HANDOVER §9 的既有约定）；(b) 资源页单开"整合包"分区，不复用 `ContentKind`，只复用 `ModpackService.ImportAsync`。建议 (b)，改动面小一个数量级。
- **契约**：方案 (b) 只在 `Abstractions/ContentServices.cs` 加一个 `InstallModpackAsync(projectId, versionId)`。**风险**：CurseForge 的整合包检索要按 classId 查询，本仓库的搜索契约按内容类型分派、没有整合包这一类，该分区只列 Modrinth——UI 必须写明，否则又是"搜不到 = 坏了"。

### 9. Forge 推荐版标记

- **能力**：Forge 版本列表顶部单独标出"推荐版"（来自 `promotions_slim.json` 的 recommended）并默认选中。
- **PCL2 出处**：`ModDownloadLib.vb:1351-1361`（把 recommended 条目单独插到列表首位并加"推荐版"说明）、`:1434-1458 McDownloadForgeRecommendedRefresh`（拉 promoted 列表并落盘缓存）、`:1468 McDownloadForgeRecommendedGet`。
- **Nebula 现状**：`Models/Versions.cs:74-81 LoaderVersion` 只有 `Version / Stable / GameVersion`；`Stable` 只对 Fabric 有值（`Downloads/LoaderMetaFetcher.cs` 只覆盖 Fabric / Quilt），Forge / NeoForge 走 CmlLib 时没有任何"推荐"概念。
- **价值**：新手按默认装到最新 Forge 构建（经常是坏的），这是"装完启动崩、又查不出原因"的常见来源之一。
- **成本**：S。`Versions/VersionService.cs:GetLoaderVersionsAsync` 加一次 promoted 请求 + `LoaderVersion.IsRecommended`；缓存可套用 `ServerListSnapshot` 的 TTL + stale-while-revalidate 模式。
- **契约**：动 `Abstractions/VersionServices.cs`（只加模型字段，不算破坏契约）。**风险**：该 JSON 只有 Forge，NeoForge 无官方推荐——不要为它编一个；网络失败时列表照常可用，只是没有推荐标记。

### 10. `.old` / `.litemod` 识别

- **能力**：扫描与启停同时认 `.old`（PCL2 与部分工具的禁用后缀）与 `.litemod`（LiteLoader 模组）；禁用时若同名 `.old` 已存在则沿用 `.old`，不再生成 `.disabled`。
- **PCL2 出处**：`LocalResourceFile.vb:39`（`IsEnabled` 认 `.jar` / `.zip` / `.litemod`）、`:41`（`EnabledName` 同时剥 `.disabled` 与 `.old`）、`PageInstanceMod.xaml.vb:465`（禁用时优先 `.old`）、`:674`（删除时同时匹配两种后缀）。
- **Nebula 现状**：`Mods/ContentService.cs:20` 只有 `DisabledSuffix = ".disabled"`；`:660-661` 模组只认 `.jar` / `.jar.disabled`；`Mods/ContentBatch.cs:IsEnabledOnDisk` 同样写死 `File.Exists` + `.disabled`。
- **价值**：从 PCL2/HMCL 迁来的实例里 `.old` 文件现在是"看不见也管不了"；禁用时还会生成第二份后缀导致同一模组出现两个文件。
- **成本**：S。`ContentService` 的 `EnabledPathOf` / `IsEnabledOnDisk` / 扩展名判定、`ContentBatch.IsEnabledOnDisk`；测试补"两种后缀同时存在"的三态用例。
- **契约**：不动 `Abstractions/`。**风险**：`.old` 与 `.disabled` 并存时必须有确定优先级，否则启停会来回跳；`.litemod` 的启用语义与 `.jar` 相同，但不能按 LiteLoader 加载器分组（Nebula 不认这个加载器）。

### 11. 客户端版本"仅下载不安装"

- **能力**：版本列表每行提供"保存到目录"——只把客户端 jar（必要时含 libraries）拉到用户指定目录，不写 `versions/`、不改 `instance.json`。
- **PCL2 出处**：`ModDownloadLib.vb:McDownloadListItem`（第三参 `IsSaveOnly`）、`McDownloadSaveMenuBuild` / `McDownloadMenuSave`、服务端对应 `McDownloadMenuSaveServer`。
- **Nebula 现状**：`Abstractions/VersionServices.cs:31 InstallAsync` 是唯一入口且落点固定在 `versions/`；`Versions/ServerDownloadService.cs` 已有"选目录下载单文件"的完整实现，可直接作模板。
- **价值**：给别人拷一份客户端、离线留档、或只想拿同版本的客户端 jar 喂给别处；现在只能装了再拷出去。
- **成本**：S。`VersionService` 加 `DownloadFilesAsync(versionName, targetDirectory)`、`VersionsViewModel` 溢出菜单加一项。
- **契约**：动 `Abstractions/VersionServices.cs`（新成员带默认实现）。**风险**：CmlLib 的安装器不接受"只下不装"，必须自己按版本 JSON 的 `downloads.client` 拉；若要含 `libraries` 等于重写一小段安装逻辑，人力紧时可只做"客户端 jar 单文件"并在 UI 上写明。

### 12. MCBBS 整合包格式

- **能力**：识别 `mcbbs.packmeta`（addons 里是 `modrinth` / `curseforge` 项目引用），逐项反查直链下载，再解压 `overrides/`。
- **PCL2 出处**：`ModModpack.vb:44 / :64`（`mcbbs.packmeta` 判定，优先于 `manifest.json`）、`:682`（读 packmeta，失败回落 `manifest.json`）。
- **Nebula 现状**：`ModpackFormat` 无此值；`ModpackService.ReadModrinthDownloads`（`:106`）只读 `modrinth.index.json` 的 `files[].downloads[0]`，没有"按项目 id + 版本 id 反查直链"的分支。
- **价值**：MCBBS 时代的大量整合包仍在国内流传，这些包没有 `manifest.json`，现在会被当普通 zip 解开，玩家看到一堆文件却启动不了。
- **成本**：M。`ModpackService` 加解析、复用 `IModrinthService.GetVersionsAsync` / `ICurseForgeService.GetFilesAsync` 反查、失败项进"待手动安装"清单。
- **契约**：只追加枚举值。**风险**：`mcbbs.packmeta` 的 schema 没有正式文档，只能按 PCL2 的读法复刻；反查失败必须有出口，不能整包失败。

### 13. 双源合并搜索

- **能力**：一次搜索同时请求 Modrinth 与 CurseForge，把同一项目跨平台识别为一条。
- **PCL2 出处**：`ResourceSearcher.vb:77`（`ModPack` 与 `Mod` 走不同计数与合并口径）、`:488`（结果按类型合并）、`ResourceProject.vb` 的跨平台同一性判据与批量补项目信息。
- **Nebula 现状**：`App/ViewModels/LibraryViewModel.cs` 用 `ActiveSource`（`ContentSourceKind`）二选一，两个源的搜索结果从不交叉，`ContentSourceKind` 只用于详情与安装的源切换。
- **价值**：国内用户常遇到"Modrinth 有、CF 没有"或反之，现在要手动切两次并肉眼比对；合并后"这个模组哪个站有"变成一行字。
- **成本**：L。需要合并层 + 跨源同一性判据（slug 归一化）+ 去重 + 详情页"另一个源也有"提示；两站都有完整搜索，合并结果才是双向的。
- **契约**：动 `Abstractions/ContentServices.cs`（新增 `SearchMergedAsync`，不动已有方法）。**风险**：误合并（两个不同项目同名）比不合并更糟，判据要保守到"slug 完全一致才合并"。

---

## 2. 建议重新评估的「明确不抄」项

逐条复核 `docs/PCL2-PORTING.md` §2.6 / §7.3 / §10.3 / §11.3 / §12.3 / §13.3 / §14.5 / §15.5 列出的不抄项。
结论：**没有一项需要翻案**；下表是本轮判据，写下来避免下一轮重复讨论。

| 能力 | 复核结论 | 判据 |
|---|---|---|
| OptiFine / LiteLoader 安装 | 维持不抄 | CmlLib 仍无内置；PCL2 是"下 installer → 解包 → `ForgelikeInjector` 注入"，与 `InstallService` 不共享中间产物；且 1.21+ 已无 OptiFine |
| EasyTier 联机 | 维持不抄 | `PageLinkMain.xaml.vb` 全程依赖外部二进制与中继服务（`GetTargetPeers` / `ProcessStart`），Nebula 无服务端 |
| 主题贴图 / 音乐 / 背景视频 | 维持不抄 | `ModMusic.vb` 属 WPF 媒体栈；Nebula 的 `AuroraBackground` 是自绘，两套视觉体系互斥 |
| 内嵌 base64 依赖 + `AssemblyResolve` | 维持不抄 | 反模式；PCL2 那份 `.resx` 有 4.7 MB，Nebula 用 NuGet |
| DES + 硬件指纹派生 key | 维持不抄 | Nebula 的 `Accounts/TokenProtector.cs` 用 DPAPI；"跨机器解不开"本身就是需求 |
| 皮肤系统（下载/渲染/上传/披风） | 维持不抄 | `PageLaunchLeft.xaml.vb` 的四套皮肤加载入口（Microsoft / Legacy / Nide / Auth）已是一个独立子系统；本轮未找到能低成本切分的子集 |
| 分片下载 + 阶梯并发 | 维持不抄 | 要重写 `DownloadQueue` 的单活动批次模型，且与 CmlLib 自带并发冲突；三档源 + 会话级降级已覆盖主要场景 |
| 公告 / 启动器自更新 | 维持不抄 | 需要自有分发端点；Nebula 的"正式版更新提示"指向 GitHub Release，语义足够 |
| 中文搜索词表（MC 百科） | **已翻案：2026-10-24 引入数据文件，代码侧只做 slug→译名查表** | 原判据是「属数据资产，不引词表这段代码无意义」，前提是这套数据只服务搜索。用户要求模组页显示中文名后，判据变了：卡片上的译名是刚需，搜索只是顺带。落地方式与 PCL 不同——不抄 `ResourceSearcher.vb` 的中文分词与拼音匹配，只保留 `WikiEntry.cs` 的解析规则（`ModNameTable`），并把 `PCLCS/Resource/WikiEntries.txt` 原样放进 `src/NebulaLauncher.Core/Resources/`。**使用条件**：按 PCL《存储库合理使用指南》署名（见 README「数据与许可」），且不得把本启动器命名为与 PCL 混淆的名称。 |
| 内存优化 / 百宝箱 | 维持不抄 | 实证为桩：`PageOtherTest.xaml.vb:MemoryOptimize` 只调 `NtSetSystemInformation` 改系统缓存，与"游戏内存"无关 |
| 界面多语言 | 维持不抄 | PCL2 自己也没有；Nebula 已删 `AppSettings.Language` 死字段，重开只会再造一个死字段 |
| 模拟浏览器 UA / SSL 校验开关 | 维持不抄 | PCL2 的 `SimulateBrowserHeaders`（`ModNet.vb`）服务于 OptiFine 官网反爬与自家 API 签名；Nebula 面对的端点都接受标准 UA |

唯一记录在案的边界：**`launcher_profiles.json` 同步**（`ModLaunch.vb:1768-1787`）不在"明确不抄"清单里，但本轮**也不建议做**——
它只对"第三方地图/存档工具读取正版账户"有意义，属极低频场景，写坏这个文件反而会让 Forge 安装器认不出已登录用户。

---

## 3. 已确认无缺口的方向（列出来避免下一轮重复调研）

| 方向 | Nebula 现状（证据） | PCL2 对应 |
|---|---|---|
| 多源候选回退 + 未知主机判定 | `Downloads/DownloadInfrastructure.cs` 的 `Candidates` / `MirrorRewritingHandler` | `ModDownload.vb:DlSourceOrder` |
| 三档下载源 + 会话级自动降级 | `Downloads/DownloadSourceGovernor.cs`、`IDownloadSourceProvider` | `ModDownload.vb:DlSourceOrder` 系列 |
| CurseForge 多 CDN 候选 | `Mods/CurseForgeService.cs:1772` 起的 `CurseForgeCdn.CandidateUrls`（mediafilez ↔ edge 互换） | `ResourceVersion.vb:414-418 ParseCurseForgeDownloadUrls`（**文档曾称未抄，实际已有**） |
| 令牌桶限速 + 进度曲线 | `Downloads/SpeedLimiter.cs`、`Downloads/DownloadQueue.cs` | `ModNet.vb` 的速度限制段 |
| 逐文件任务面板 / 只重试失败项 / 批次历史 | `DownloadQueue` 的 `Snapshot` / `TasksChanged` / `History`、`DownloadView` | `PageSpeedLeft.xaml.vb`（任务列表刷新） |
| 安装链逐文件进度 + 分阶段进度 | `DownloadQueue.BeginExternalBatch`、`InstallStagePlan`、`InstallProgressAdapter` | `ModLoader.vb` + `PageSpeedLeft.xaml.vb` |
| 版本隔离三态 + 自动建实例 | `Instances/IsolationPolicy.cs`、`InstanceContext.ResolveLaunchTargetAsync` | `PageInstanceSetup.xaml.vb` 的隔离设置段 |
| 实例克隆 / 重命名 / 图标 / 回收站 / 目录清理 / 备注 | `InstanceService.Admin.cs`、`InstanceService.Trash.cs` | `PageInstanceOverall.xaml.vb` |
| 两档版本修复 + 组件面板 | `InstallService.Repair.cs`、`Installing/ComponentInspector.cs` | `McInstallManifest` 比对 |
| 依赖自动安装（递归 + 环检测 + optional 收集） | `Mods/ModrinthInstallPlanner.cs` | `ModModpack.vb` 的 dependencies 段 |
| 哈希反查（Modrinth SHA1 / CF MurmurHash2）+ 本地缓存 | `Mods/ModFingerprint.cs`、`ModUpdateService` | `LocalResourceFile.vb` 的哈希反查 |
| 崩溃归因（75 条规则 + 肇事 mod + `hs_err`） | `Launching/CrashRules.cs`、`CrashAnalyzer.ParseHsErr` | `ModCrash.vb` |
| 崩溃报告脱敏导出（含 hs_err 与 mods 列表） | `Launching/CrashExportService.cs`、`Diagnostics/DiagnosticsSnapshot.cs` | `ModCrash.vb` 归档 |
| 启动里程碑监视 + 崩溃双判据 + 窗口接管 | `Launching/GameWatcher.cs`、`GameLauncher.TakeOverGameWindowAsync` | `ModWatcher.vb` |
| GC / 编码 / Log4Shell / 进程优先级 / 自动中文 | `JvmArgumentBuilder`、`GameLauncher.ApplyChineseLanguage:938`、`ApplyProcessPriority:997` | `ModLaunch.vb` 的 JVM 参数段与 `:2129-2134` 优先级档 |
| 自动内存四段斜坡 | `Launching/MemoryAdvisor.cs` | `PageInstanceSetup.xaml.vb` 的内存推荐滑块 |
| Java 探测 / 手动添加 / 排除列表 / Mojang runtime 安装 | `Java/JavaService.cs:348 AddManualAsync`、`:375 GetExcludedAsync`、`Java/MojangRuntimeCatalog.cs` | `ModJava.vb:228-290 ManuallyImportJava` 等 |
| authlib-injector（三参数 + jar 缓存 + 前台登录） | `Launching/AuthlibInjectorService.cs`、`Accounts/AuthlibAuthService.cs` | `ModLaunch.vb` 的 Authlib 段 |
| 服务器 Ping（MOTD 上色 + favicon）+ `servers.dat` 导入 + 一键直连 | `Servers/ServerService.cs`、`Servers/MinecraftProtocol.cs`、`Models/Instances.cs:95 ServerAddress` | PCL2 无此完整链路 |
| 世界备份轮转 / 退出后自动备份 / 世界导出 | `Backups/BackupService.cs`、`WorldsViewModel` | HMCL 的退出后备份（PCL2 没有） |
| 整合包导出 `.mrpack`（哈希反查 + overrides 排除 + `IncludeSaves` / `IncludeConfig`） | `Backups/ModpackExporter.cs`、`Models/Content.cs:302-314` | `PageInstanceExport.xaml.vb` |
| 数据包（文件与目录双形态 + `pack_format` 区间 + 在线安装） | `ContentService.ScanDataPacks`、`Versions/PackFormatTable.cs` | PCL2 只在 zip 上做启停 |
| 资源详情（正文 / 图库 / 链接 / 版本列表 / 依赖） | `ModrinthService.GetProjectAsync`、`LibraryViewModel` | `PageDownloadCompDetail.xaml.vb:Load_OnFinish` |
| 搜索分页 | `LibraryViewModel.cs:28` 的 `PageSize = 20` 与 `:805 LoadMoreAsync` | `PageResource.xaml.vb:198-207` 翻页按钮 |
| 日志中心 + 外部崩溃报告导入 | `Logging/LauncherLogReader.cs`、`CrashAnalyzer.AnalyzeExternalReport` | PCL2 只有窗体内嵌日志区 |
| 配置包导出/导入（不含账户与机器相关字段） | `Transfer/ProfileBundleService.cs` | PCL2 的 ini 迁移 |
| 窗口尺寸记忆 / 窗口内快捷键 / 托盘 / 单实例唤醒 | `Platforms/WindowMemory.cs`、`MainWindow.RegisterShortcuts`、`Services/TrayService.cs` | PCL2 **没有**托盘（全树 0 命中），这块是 Nebula 自有能力 |
| 深浅主题三态 | `Themes/Tokens.axaml` 双字典、`Services/ThemeService.cs` | PCL2 **没有**主题切换 |
| 多游戏目录（`.minecraft` 多根） | 以"实例 + 隔离 + `InstanceContext.ListTargetsAsync`"替代 | `PageSelectLeft.xaml.vb:AddFolder` 的多根目录；Nebula 不引入第二套目录归属模型 |
| 实例原地换加载器 / 改 MC 版本 | **有意不做**：`HelpCatalog.cs:134` 写明"克隆一份改版本比原地改安全得多"，`VersionsViewModel.cs:1640 CloneAsync` 是官方替代路径 | `PageDownloadModify.vb:ReloadSelected` / `RefreshComparisons`（新旧组件对比 + 恢复）；成本 L，且原地改会破坏 `ManagedFiles` 清单 |

---

## 4. 本轮抽出的两处「文档与实现不符」

不属于缺口，但会影响下一轮判断，记录下来：

1. `PCL2-PORTING.md` §2.3 写"启用/禁用改扩展名：✅ 已补 `.old`"。实际 `ContentService.cs:20` 只有 `.disabled`，
   `.old` 既不识别也不生成（见第 10 条）。
2. `PCL2-PORTING.md` §2.3 把"下载链接展开成多候选（CF 5 个 CDN 变体）"标为未抄。实际
   `CurseForgeCdn.CandidateUrls` 已做 mediafilez/edge 互换，只差 `media.` 前缀与 `-service.overwolf.wtf` 归一
   （PCL2 自己也只把这两条当兜底）。

