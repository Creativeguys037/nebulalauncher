# NebulaLauncher 扩展功能清单

调研范围：`D:\agent_workspace\hmcl\`（55 个 HMCL 源文件，逐文件实读）、`D:\agent_workspace\.probe\research-launchers.md`、`src\NebulaLauncher.Core\` 全部实现与 `Abstractions\` 全部接口。核查时间 2026-10-21 21:0x，此时本轮①②③④ 的部分产物已落盘，§0.1 第 5 条记录了被覆盖的项。
所有条目都给出 `文件:类/方法` 级来源；HMCL 侧未落盘或未读到的实现标注为「未确认」，不做推测引用。

## 0. 结论摘要
| 编号 | 功能 | 价值 | 成本 | 依赖 | 优先级 |
|---|---|---|---|---|---|
| 1 | 启动前置检查（PreflightCheck：内存/位宽/路径/磁盘/加载器完整性） | 高 | M | 无 | P0 |
| 2 | Java 约束集升级：上下界 + 加载器条件 | 高 | M | 无 | P0 |
| 3 | 本地模组依赖检查：缺前置 / 重复 modid | 高 | S | 无 | P0 |
| 4 | Java 扫描扩展：Mojang runtime + 其他启动器 + 自定义路径 | 高 | S | 无 | P0 |
| 5 | 崩溃报告深度解析：路径标记 + 肇事 mod 定位 + 规则扩容 | 高 | S | 无 | P0 |
| 6 | 实例级校验与修复（快速 / 深度两档） | 高 | M | 无 | P1 |
| 7 | Java 下载改用 Mojang `java-runtime` manifest | 中高 | M | 无 | P1 |
| 8 | Modrinth 依赖解析 + 自动安装前置 | 高 | M | 无（需补 `ModrinthVersion` 模型） | P1 |
| 9 | 模组更新检查（mods.lock.json + sha1 反查） | 高 | M | 无 | P1 |
| 10 | 整合包导出（`.mrpack`） | 中高 | M | 建议先做 9 | P1 |
| 11 | 实例克隆 | 中高 | M | 无 | P1 |
| 12 | 实例图标 | 中 | S | 无 | P1 |
| 13 | 自动内存分配（按物理内存推荐 `-Xmx`） | 高 | S | 无 | P1 |
| 14 | 世界备份轮转 + 退出后自动备份 | 中高 | S | 无 | P1 |
| 15 | JVM 参数预设（全局预设 + 实例覆盖） | 高 | S | 无 | P1 |
| 16 | 实例 `managedPack` 元数据 | 中高 | S | 无 | P1 |
| 17 | 实例重命名 + 删除到回收站（可恢复） | 中高 | S | 无 | P1 |

### 0.0 实现进度（2026-10-21 第二轮）

**P0 全部五项已实现**，落地位置与验证方式见 `HANDOVER.md` 的 6.15–6.18：

| 条目 | 状态 | 落点 |
|---|---|---|
| 1 启动前置检查 | ✅ | `Launching/PreflightChecker.cs`、`GameLauncher.cs`、`Models/Launching.cs:102`、`Abstractions/GameServices.cs:35 IPreflightChecker` |
| 2 Java 约束上下界 | ✅ | `Models/Java.cs:68 JavaConstraint`、`Java/JavaRequirementTable.cs:137 AllowedRange` / `:183 Evaluate`、`JavaService.cs:199 GetAllowedRange` |
| 3 本地模组依赖检查 | ✅ | `Mods/ModMetadataReader.cs`、`Mods/ContentService.cs:200 DetectDependencyIssues`、`Models/Content.cs` |
| 4 Java 扫描扩展 | ✅ | `Platforms/PlatformService.cs:363-385`、`Java/JavaService.cs:523 ExpandOfficialRuntimeLayout` / `:407 CollectCandidates` |
| 5 崩溃报告深度解析 | ✅ | `Launching/CrashAnalyzer.cs:385/414/443`、`GameLauncher.cs:464`、`Models/Launching.cs:78` |

与条目 1/2 的一处偏差（已在代码注释标明原因）：

- 条目 2 设计的 `AllowedRange("1.20.1", Forge) = (17,17)` 实际实现为 `(17, int.MaxValue)`。
  强制上界会把「1.16.5 Forge + Java 17」这类合法组合拦死；MODDED_JAVA_* 在 HMCL 里本就是 suggested。
  真正会崩的组合（LaunchWrapper ≤1.12.999、MODLAUNCHER_8 区间）仍按 mandatory 上界阻断。
- 条目 1 的 `CheckAsync` 拆成了 `CheckEnvironmentAsync`（不碰 Java，保留 2 秒预算）+
  `CheckJava`（同步纯函数，零探测）。原因：单段实现里 Java 探测会吃满预算，
  导致「Java 版本不匹配」这条最有价值的 Blocker 在冷启动时被静默跳过。

### 0.0.1 实现进度（2026-10-21 第五轮）

P1 与 P2 的绝大多数条目已落地，落点与 PCL2 依据见 `docs/PCL2-PORTING.md` 第 7 节：

| 条目 | 状态 | 落点 |
|---|---|---|
| 6 实例级校验与修复（两档） | ✅ | `Installing/InstallService.Repair.cs`、`Installing/InstallManifest.cs` |
| 7 Java 下载改用 Mojang manifest | ✅ | `Java/MojangRuntimeCatalog.cs`、`JavaService.TryInstallMojangRuntimeAsync` |
| 8 Modrinth 依赖解析 + 自动装前置 | ✅ | `Mods/ModrinthInstallPlanner.cs`、`ModrinthService.PlanInstallAsync / InstallPlanAsync` |
| 10 整合包导出 `.mrpack` | ✅ | `Backups/ModpackExporter.cs`、`ModpackService.ExportAsync` |
| 11 实例克隆 | ✅ | `Instances/InstanceService.Admin.cs` `CloneAsync` |
| 12 实例图标 | ✅ | 同上 `SetIconAsync` + `VersionsViewModel.InstanceIcons`（12 个自绘 Path） |
| 13 自动内存分配 | ✅ | `Launching/MemoryAdvisor.cs`（第四轮） |
| 14 世界备份轮转 + 退出后自动备份 | ✅ | `BackupService.PruneAsync`、`HomeViewModel.BackupWorldsAfterExitAsync` |
| 17 实例重命名 + 删除到回收站 | ✅ | `InstanceService.Admin.cs` `RenameAsync`、`InstanceService.Trash.cs` |
| 19 实例运行目录清理 | ✅ | `InstanceService.Admin.cs` `CleanupAsync` |
| 15 JVM 参数预设（全局预设 + 实例覆盖） | ✅ | `Models/Settings.cs` `JvmPreset`、`Launching/JvmPresetResolver.cs`、`SettingsViewModel.cs:758-931`（第六轮） |
| 21 只读游戏目录覆盖（`*.bak`） | ⏭ 不采用 | 改扩展名做覆盖在隔离设计下会与 mod 启用态混淆；只读目录应以「无法写入」直接报错，而不是静默改路径 |
| 22 实例多根目录 + Modrinth 镜像兜底 | ❌ | 镜像部分本机不可达（见 §0.4）；多根目录会让 `libraries` / `assets` 归属变复杂，收益低于引入的错误面 |
| 16 实例 `managedPack` 元数据 | ✅ | `ModpackService.cs:117-140` 导入时写入（第六轮），`CoreAssets_ModpackImportTests` 覆盖 |
| 跨实例文件复用（PCL2 `ModNet.vb:1474-1564`） | ✅ | `Downloads/FileReuseService.cs`、`DownloadQueue` 前置复用（第七轮） |
| 资源项目详情（正文 / 图库 / 链接） | ✅ | `ModrinthService.GetProjectAsync`、`LibraryViewModel` + `LibraryView`（第七轮） |
| 内置离线帮助 | ✅ | `Core/Help/HelpCatalog.cs`、`HelpViewModel` + `HelpView`（第七轮） |

### 0.0.2 实现进度（2026-10-22 第八轮）

本轮的输入不是 HMCL 清单，而是「PCL2 有 / Nebula 无」的 29 条缺口调研（原始表见
`PCL2-PORTING.md` §10.3 与 §10.5）。已落地四项 + 一处布局修复：

| 项 | 类别 | 落点 |
|---|---|---|
| 关闭到托盘 + 托盘图标 | Nebula 死开关修复（PCL2 无托盘） | `Core/Platforms/CloseBehavior.cs`、`App/Services/TrayService.cs` |
| 日志中心页（启动器 / 游戏 / 崩溃报告） | 可观测性 | `Core/Logging/LauncherLogReader.cs`、`LogsViewModel` + `LogsView` |
| 下载任务明面（逐文件 / 失败重试 / 历史） | 可观测性 | `DownloadQueue.Snapshot/TasksChanged/History`、`DownloadView` 两张卡片 |
| 浅色主题 + 深/浅/跟随系统 | 外观（技术债清零） | `Themes/Tokens.axaml` 双字典、`Services/ThemeService.cs`、`AppSettings.Theme` |
| 抬头区副标题 1024 宽下消失 | 布局缺陷 | `Views/MainWindow.axaml` 抬头 Grid 改 `Auto,*` |

### 0.0.3 实现进度（2026-10-22 第九轮）

第九轮把 §0.0.3（上一版）的 P0 五条全部落地，并补了两条 P1：

| 项 | 类别 | 落点 |
|---|---|---|
| 安装链逐文件进度 | 可观测性（上轮遗留边界） | `DownloadQueue.BeginExternalBatch` / `IDownloadTaskScope`、`NebulaGameInstaller`、`InstallService` |
| `hs_err_pid*.log` 纳入崩溃分析 | 崩溃归因 | `CrashAnalyzer.ParseHsErr`、`CrashRules` 追加 6 条（69 → 75）、`GameLauncher.FindRecentHsErrLogs` |
| 崩溃报告 / 日志 zip 手动导入 | 崩溃归因 | `CrashAnalyzer.AnalyzeExternalReport`、`HomeViewModel.ImportExternalReportAsync` |
| 启动器窗口尺寸 / 最大化记忆 | 系统集成 | `AppSettings.LauncherWindow*`、`Platforms/WindowMemory`、`MainWindow.PersistWindowState` |
| 窗口内快捷键（Esc / F5 / F11 / Ctrl+O / Ctrl+L） | 系统集成 | `MainWindow.RegisterShortcuts`、`MainWindowViewModel.Escape` |
| 实例备注接线 | 死字段修复 | `VersionsViewModel.EditorNotes`、`VersionsView` 备注行与浮层 |
| 模组多选批量启停 / 删除 | 内容管理 | `Core/Mods/ContentBatch.cs`、`ModsViewModel` 选中集合、`ModsView` 批量条 |

### 0.0.4 实现进度（2026-10-22 第十轮）

第十轮把上一版的 P0 全部落地，P1 的数据包也一并做了：

| 项 | 类别 | 落点 |
|---|---|---|
| 版本收藏 / 置顶 / 隐藏 | 列表组织 | `Core/Versions/VersionFavorites.cs`（独立 `favorites.json`）、`VersionsViewModel` + `VersionsView` |
| 三档下载源 + 会话级自动降级 | 网络策略 | `DownloadSourceGovernor`、`IDownloadSourceProvider`、`DownloadMirror.Candidates(url, source, preferred)` |
| 任务栏进度条 | 系统集成 | `App/Services/ShellProgress.cs`（节流 10 次/秒）、`HomeViewModel` / `DownloadViewModel` |
| 数据包（datapack）扫描与启停 | 内容管理 | `ContentKind.DataPack`、`ContentService.ScanDataPacks`（文件与目录两种形态）、模组页第 4 分类 |

顺带修复：`ContentTarget.DirectoryNameOf(DataPack)`（原来会落进 `_ => "mods"`）、`VersionService` 的下载源判断改用 `PrefersMirror`。

### 0.0.5 实现进度（2026-10-22 第十一轮）

| 项 | 类别 | 落点 |
|---|---|---|
| 数据包在线安装（Modrinth `datapack`） | 内容管理 | `ModrinthService.ResolveDataPackTargets` / `ValidateDataPackTarget`、`LibraryViewModel` 安装位置选择 |
| 崩溃报告导出（脱敏 zip） | 诊断 | `Launching/CrashExportService.cs`、`Diagnostics/DiagnosticsSnapshot.cs`、`LogRedactor` 主目录用户名脱敏 |
| 服务端（`server.jar`）下载 | 下载 | `Versions/ServerDownloadService.cs`、下载页「客户端 / 服务端」分段 |
| Forge / NeoForge 分阶段进度 | 可观测性 | `InstallStagePlan`（0–50 / 50–85 / 85–100）、`InstallProgressAdapter`、下载页阶段行 |
| 删掉 `AppSettings.Language` 死字段 | 清理 | `Models/Settings.cs`、`SettingsService.Normalize`、`SettingsViewModel.ApplyTo` |

**一条修正**：上一版把「Forge maven 下载不上报进度」列为 P1。本轮用 IL 读证推翻——`ForgeInstallOptions` 本来就有 `FileProgress` / `ByteProgress` / `InstallerOutput`，`InstallService` 也早就接进了聚合进度。真正的缺口是阶段不可辨与比例回退（`97.5% → 3.0%`），已按分阶段进度修掉。

### 0.0.6 实现进度（2026-10-22 第十二轮）

| 项 | 类别 | 落点 |
|---|---|---|
| `hs_err` 进崩溃导出 + 导出含 `mods-list.txt` | 诊断 | `LaunchResult.HsErrLogs`、`GameLauncher.ReadHsErrSections`、`CrashExportService.BuildModsListText` |
| 资源包 / 数据包 `pack_format` 兼容提示 | 内容管理 | `Versions/PackFormatTable.cs`（wiki 表格 1.6.1–1.21.8）、模组页卡片徽标 |
| 配置包导出 / 导入 | 迁移 | `Transfer/ProfileBundleService.cs`、设置页「配置迁移」区块 |
| 服务端清单落盘缓存 | 性能 | `ServerDownloadService`（TTL 6h + SWR）、`ServerListSnapshot` |
| 设置只加载一次 | 启动性能 | `ISettingsService.LoadAsync` 幂等 + `ReloadAsync` |
| 日志半行修复 | 可观测性 | `FileLogger.EnsureTrailingNewline` |

### 0.0.7 实现进度（2026-10-22 第十三轮）

| 项 | 类别 | 落点 |
|---|---|---|
| 实例组件面板（清单比对 + 缺失/改动 + 只修复缺失） | 实例管理 | `Installing/ComponentInspector.cs`、`VersionsViewModel` 组件浮层 |
| 1.21.9+ `min_format` / `max_format` | 内容管理 | `PackFormatTable.PackFormatRange` / `TryGetExpected` / `Evaluate(range)` |
| 安装器 stdout 日志通道 | 可观测性 | `Installing/InstallerOutputLog.cs`（500 字符 / 200 行 / 折叠 / 失败提升） |
| `HomeViewModel` mods 收集测试 + 全量扫描实测 | 质量 | `AppHome_ModsCollectionTests`（7 例）；全量扫描 859 个 JSON / 0 失败 / 9.84s |

**一条卡住的**：NeoForge 真装仍未跑通。根因链条是 `NeoForgeInstaller` 只有单参构造（已修）→ 该构造自建 `HttpClient` 绕过镜像 → `maven.neoforged.net` 在本机不可达（BMCLAPI 的同路径返回 200）。要通必须绕开 facade。

### 0.0.8 下一批候选（按价值/成本排序，未开工）

| 优先级 | 项 | 依据 / 说明 |
|---|---|---|
| P0 | NeoForge 桥接重构（绕开 facade 走镜像） | 见 §0.0.7 的根因链；`NeoForgeVersionLoader(ourHttpClient)` + `NeoFNewest` 直接安装 |
| P0 | 版本行按钮溢出（1024 下版本名被挤成首字母） | 6 个按钮太多，把「隐藏」挪进 `…` 溢出菜单 |
| P1 | 故障注入下的 stdout 提升 / 重复折叠 | 只有单测；需要一个可控失败的安装样本 |
| P1 | `pack_format` 与 min/max 混合优先级实测 | 实现为区间优先，未造混合样本 |
| P1 | 组件面板的「只修复缺失文件」联网修复实测 | 按钮链路已通，修复本身未跑 |
| P2 | mtime 比对在 FAT / 网络盘上的容差 | 只在 NTFS 实测 |
| P2 | 安装批次与普通下载并发 / 双源合并搜索 / 版本原地换加载器 | 与历轮相同，改动面大 |

| 优先级 | 项 | 依据 / 说明 |
|---|---|---|
| P0 | 1.21.9+ 的 `min_format` / `max_format` | 官方已改用区间语义，整数 `pack_format` 不再够用 |
| P0 | `HomeViewModel` 的 mods 收集单测 | tests 已引用 App，可以直接测；目前只有编译期校验 |
| P1 | 「扫描全部版本」的真实全量验证 | 窗口逻辑有单测，真实 120 个 JSON 未跑 |
| P1 | 导出包含 config 文件快照 | 会把体积拉到几 MB，需先决定脱敏范围 |
| P1 | NeoForge / 旧版 Forge 真装验证 | 阶段代码已共用，缺实测轨迹 |
| P1 | 安装器 stdout 落日志通道 | 目前只用来判定处理器阶段，排「处理器为什么失败」时无原始输出 |
| P2 | 安装批次与普通下载并发 | 仍是单活动批次模型 |
| P2 | 双源合并搜索、版本原地换加载器 | 需要改 `ModrinthService` 合并层 / 复用 `InstallManifest` |

### 0.0.9 实现进度（2026-10-22 第十六轮起，收尾于第二十轮）

把 `docs/PCL2-GAP.md` 的 13 条候选清单全部处理完（12 条落地、1 条主动延后），
并顺带清掉 §0.0.8 的两个 P0。

| 项 | 类别 | 落点 |
|---|---|---|
| **NeoForge 桥接重构**（§0.0.8 P0） | 安装 | `Installing/NeoForgeInstallerBridge.cs`（不碰 facade；1.21.1 与 1.20.1 Legacy 两条真装都通过） |
| **版本行按钮溢出**（§0.0.8 P0） | 界面 | `VersionsView.axaml` 的 `…` 溢出菜单；1024×660 下版本名完整 |
| NeoForge 版本选择 | 安装 | `LoaderMetaUrls.NeoForgeCatalog` + `VersionService.GetNeoForgeVersionsAsync`（1.21.1 下 259 个构建） |
| 整合包导入闭环 | 内容 | `Backups/ModpackService.cs`（装游戏 → 解压 → 下载 → 写实例） |
| HMCL / MCBBS 整合包识别 | 内容 | `ModpackService.DetectFormat`（只认根与一级子目录，`addons` 判 MCBBS） |
| **MCBBS `addons` 逐项反查** | 内容 | `ModpackService.cs:1053-1854`（CurseForge 元数据 → forgecdn 直链 → Modrinth 兜底 → 待手动安装清单） |
| 在线整合包浏览与安装 | 内容 | `Mods/ModpackCatalogClient.cs` + 资源页第五分区 |
| 模组删进回收站 / 四档筛选 / 一键更新 / `.old` 兼容 | 内容 | `Mods/ContentService.cs`、`Mods/ModUpdateService.cs`、`ModsViewModel` |
| **模组卡片右键菜单** | 内容 | `ModsViewModel.CopyFileName/CopyPath/ToggleEnabled` + `ModsView.axaml` 的 `ContextMenu` |
| **资源页搜索排序** | 内容 | `ModrinthService.NormalizeSortIndex` + `LibraryViewModel.SelectedSort`（相关度/下载量/关注数/最新/最近更新） |
| **HTTP 代理设置** | 网络 | `Core/Net/{ProxyAddressParser,ProxyConfigurator,NetworkProbe}.cs` + 设置页「下载」分区；生效点 `HttpClient.DefaultProxy` |
| 高性能 GPU 注册 / Fabric API 附装 / Forge 推荐版 / 仅下载不安装 | 启动·安装 | `PlatformService.TrySetGpuPreference`、`InstallService.TryInstallFabricApiAsync`、`LoaderMetaUrls.ForgePromotions`、`VersionService.SaveClientJarAsync` |

仍未开工的 §0.0.8 项：故障注入下的 stdout 提升、`pack_format` 混用实测、组件面板联网修复、
mtime 在 FAT/网络盘的容差、安装批次与普通下载并发、双源合并搜索（版本原地换加载器已在第二十四轮落地）。

### 0.0.10 实现进度（2026-10-22 第二十三轮）

本轮的判断依据变了：`PCL2-GAP.md` 的 13 条清单已经清完，剩下的都是 PCL2 自己不做的方向。
所以这一轮做的是「PCL2 也没有、但这个启动器确实缺」的三件事。

| 能力 | 分类 | 落点 |
|---|---|---|
| 崩溃建议的动作按钮（六种动作可执行，含「归因唯一时禁用肇事 mod」） | 自研 | `ViewModels/CrashSuggestionItem.cs`、`HomeViewModel.RunCrashActionAsync`、`HomeView.axaml:312/392` |
| 服务器 Query 玩家名单（GameSpy4 UDP，含分片拼装与「未开启 Query」区分） | 自研 | `Core/Servers/ServerQueryService.cs`、`Abstractions/ServerServices.cs:189 IServerQueryService`（DIM）、服务器页「玩家」按钮 |
| 存档 `level.dat` 编辑（种子 / 模式 / 难度 / 作弊 / 保留物品栏 / 世界名） | 自研 | `Core/Backups/NbtReader.cs`（完整标签树）、`NbtWriter.cs`（新建）、`BackupService.UpdateWorldAsync`、世界页编辑面板 |
| 页面级取消源释放（消 CA1001 的一半） | 技术债 | `PageViewModelBase : IDisposable` + 四个页面的 `Dispose(bool)` 覆写 |

三条自研功能各带一条「不能省」的约束，写在 `PCL2-GAP.md` §0.2 末尾：
禁用动作只在归因唯一时出现；Query 只能按需查询不能轮询；写 `level.dat` 前必须有 `.bak`、
未知标签必须保留、`session.lock` 被占用必须拒绝。

### 0.0.11 实现进度（2026-10-22 第二十四轮）

这一轮的重点从「补功能」挪到了「补韧性 + 清债」：三项新能力都是「网络或磁盘不理想时还能用」，
外加一次全项目 0 警告的收口。

| 能力 | 分类 | 落点 |
|---|---|---|
| 实例原地换加载器（同 MC 版本内保留 mods / 存档 / config） | 自研 | `Core/Instances/InstanceService.Loader.cs`、`Models/Instances.cs:364 InstanceLoaderSwitchResult`、`:403 InstanceLoaderAdvice`、版本页 `…` 菜单 + 面板 |
| Modrinth 镜像兜底（官方失败才用，默认关） | port 思路（HMCL） | `Downloads/DownloadInfrastructure.cs:53 ModrinthFallbackMap`、`Mods/ModrinthService.cs:875 FetchAsync`（三态 `FetchOutcome`）、设置页「下载」区 |
| 只读游戏目录覆盖（清 `ReadOnly` + 重试一次 + 覆盖前 `.bak`） | 自研 | `Paths/PathProbe.cs:98 TryMakeWritable`、`InstallService.EnsureInstallEnvironment`、`:428 BackupBeforeOverwrite` |
| 文件名/目录名统一清洗（保留名 / 结尾点号 / 100 字符上限） | port 规则（PCL2 `ModValidate.vb`） | `Core/Paths/PathSafety.cs`，5 处调用点改为委托 |
| 全项目 0 警告（App 20 → 0，Core 7 → 0） | 技术债 | 见 `HANDOVER.md` §8.3：1 个真缺陷（`LibraryViewModel` 隐藏 `Dispose`）+ 9 个 `CA1822` 分析器看不到 XAML + 其余可具体化项 |

四条新约束同步写进 `PCL2-GAP.md` §0.2 末尾：换加载器限定同 MC 版本且互换要确认；
镜像只在官方连不上时用（404 不算）；文件名清洗必须在落盘前完成；`overwriteExisting` 默认必须 `false`。

用户要「架构能跑通、界面好用」，所以这一轮的三项都做了截图自查（深/浅两套），
`versions` 页与 `settings` 页各两张，落在 `.probe/versions-loader-{dark,light}.png` 与
`.probe/mirror-settings-{dark,light}.png`。

### 0.0.12 实现进度（2026-10-22 第二十五轮）

`PCL2-GAP.md` 的 13 条清单清空之后，这一轮挑了「用户每天都会撞上、但现在只能靠手动绕过」的四件事。
四条都不是 PCL2 的现成功能：PCL2 有实例占用检查（`McInstance.TryAcquire/IsLocked`）但没有下载暂停、
没有存储占用面板、世界页也不显示缩略图。

| 能力 | 分类 | 落点 |
|---|---|---|
| 实例运行锁（同一游戏目录不可重复启动 + 版本页「运行中」徽标 + 破坏性入口禁用） | port 思路（PCL2 `McInstance`）+ 自研判活 | `Core/Instances/InstanceLockService.cs`、`Abstractions/InstanceServices.cs:119 IInstanceLockService`、`Models/Instances.cs:113 InstanceLockInfo`、`Launching/GameLauncher.cs`（拿锁/放锁/冲突文案）、`VersionsViewModel.RefreshRunningStateAsync`、`VersionsView.axaml` 徽标 |
| 下载暂停 / 继续（在途文件下完即停，不取消在途请求） | 自研（HMCL 级体验） | `Downloads/DownloadQueue.cs:212-270 Pause/Resume/IsPaused` + `:1586 PauseGate`、`Models/Downloads.cs:99 DownloadProgress.IsPaused`、下载页顶部 pill 按钮 + 状态行、`--download-demo` 截图开关 |
| 存储占用与安全清理（设置页「存储」分区） | 自研 | `Core/Maintenance/StorageService.cs`、`Abstractions/StorageServices.cs`、`Models/Storage.cs`、`SettingsViewModel` 存储区（12 属性 / 6 命令）、`SettingsView.axaml` 存储分区 + 清理确认浮层 |
| 存档缩略图（`level.dat` 的 `Data.icon` → 缓存 PNG → 世界页 48×48） | 自研 | `Core/Backups/WorldIconService.cs`、`Models/Content.cs WorldInfo.HasIcon/IconCachePath`、`Backups/BackupService.ScanWorldsAsync`、`WorldsViewModel`（并发 3 异步加载）、`WorldsView.axaml` 缩略图 + 占位块 |

四条「不能省」的约束写在 `HANDOVER.md` §6.35–§6.37 与 `PCL2-GAP.md` §0.2 末尾：
锁必须按游戏目录而不是实例名、判活必须 PID + 启动时间、读失败一律按僵死接管、释放只删自己令牌的那份；
暂停是闸门不是取消、闸门必须覆盖重试路径、四条收尾路径都要复位；
清理必须规范化路径并白名单、日志按 mtime 保留 7 天；
图标必须校验 PNG magic 与双上限、缓存按 `level.dat` 的 (mtime, length) 失效。

验证口径：Core 与 App 各 `-t:Rebuild` **0 错误 0 警告**，`TestRunner` **1434/1434**（新增 34 例：
锁 8 + 暂停 6 + 存储 9 + 图标 11）。截图 12 张落在 `.probe/{instlock,dlpause,storage,worldico}-*.png`，
外加合并态复查四张 `.probe/r25-*.png`。

仍未做：真实游戏启动下的抢锁失败路径、真实 3000 文件安装中途暂停、存储清理的真实 junction 样本、
图标在真实游戏存档上的表现（本机 `%APPDATA%\.minecraft` 没有 `saves`）。

### 0.0.13 实现进度（2026-10-22 第二十六轮）

这一轮做的是「启动不起来的下一步」和「看不下去的两处体验」——都不是新模块，而是把已有能力接上最后一厘米。

| 能力 | 分类 | 落点 |
|---|---|---|
| 缺 Java 一键安装（首页失败面板 + 版本页状态条 → 下载 → 写回设置 → 强制重扫） | port 思路（PCL2 `ModJava.vb` 的自动下载）+ 自研判定 | `Core/Java/JavaProvisionPlanner.cs`、`Models/Launching.cs LaunchResult.MissingJavaMajor`、`Launching/GameLauncher.cs`、`HomeViewModel` + `HomeView.axaml:444-486`、`VersionsViewModel` + `VersionsView.axaml`（Lead 收口时接线） |
| 日志实时跟随（700ms 增量 tail） | 自研 | `Core/Logging/LogFollower.cs`、`Abstractions/LogServices.cs`（追加 `ILogFollowService`）、`LogsViewModel` 定时器 + 5000 行上限、`LogsView.axaml` 跟随 chip |
| 实例目录快捷芯片（存档 / 模组 / 资源包 / 截图 / 日志） | port 思路（PCL2 `PageInstanceOverall.xaml.vb`） | `Core/Instances/InstanceFolders.cs`、`Abstractions/InstanceServices.cs`（追加 `IInstanceFolderService`）、`VersionsView.axaml:325-363` |

三条不能省的约束写在 `HANDOVER.md` §6.38–§6.40 与 `PCL2-GAP.md` §0.2 末尾：
只有真的能装才给按钮（上界冲突与不可下载主版本退回手动配置）、装完必须按「写回 → 保存 → 强制重扫」顺序；
日志增量必须半行不推进偏移 + 截断/轮转自愈 + 单次读取封顶；实例目录表只存相对名、枚举不建目录、路径取自 `GetGameDirectory`。

顺带修掉一处既有缺陷（`HomeView.axaml:387-404`）：失败文案原来放在横向 `StackPanel` 里，
`TextWrapping` 因可用宽度无限而不生效，长的缺 Java 文案溢出卡片被裁——与 §6.18 是同一类坑。

验证口径：Core 与 App `-t:Rebuild` **0 错误 0 警告**，`TestRunner` **1467/1467**（新增 33 例：
Java 规划 11 + 日志跟随 12 + 实例目录 10）。截图落在 `.probe/javafix-*.png`、`.probe/logs-follow-*.png`、
`.probe/dirs-versions-*.png`，Lead 合并态复查三张 `.probe/r26-*.png`。

仍未做：真实 50 MB 下载全链路与中途取消、真实游戏 `latest.log` 的跟随、
日志轮转在「新文件更长且旧偏移处仍是换行」时的一次漏判、「已安装 → 重新启动」的点击级验证。

### 0.0.14 实现进度（2026-10-22 第二十七轮）

这一轮清的是「长期挂着的候选」——三条都在上一轮的 §11「下一轮最该做的三件事」里：

| 能力 | 分类 | 落点 |
|---|---|---|
| 下载源测速（官方 vs 镜像，只提示不决策） | 自研（`DownloadSourceGovernor` 的只读旁路） | `Core/Downloads/DownloadSourceProbe.cs`（`SourceProbeRanking` 纯函数 + 单次测量）、`Abstractions/DownloadSourceServices.cs`（追加 `IDownloadSourceProbe` 与四个模型）、`DownloadView.axaml:72-128` |
| 代理密码 DPAPI 加密 + 旧配置迁移 | 自研（与 `TokenProtector` 同一套前缀约定，熵与降级语义不同） | `Core/Net/ProxyCredentialStore.cs`、`Models/Settings.cs`（追加 `ProxyPasswordProtected` + `Clone` 同步）、`Settings/SettingsService.cs`（`LoadProxyCredential` / `PersistableCopy`）、`Transfer/ProfileBundleService.cs`（双剥）、`SettingsView.axaml` 代理区 |
| 发布产物首启验证 | 验证 + 文档 | `docs/PUBLISH-CHECK.md`（新建）、`build.ps1` 未改但流程被钉死为「必须 `-Clean`」 |

三条不能省的约束写在 `HANDOVER.md` §6.41–§6.43：测速必须用不带 `MirrorRewritingHandler` 的客户端
（否则两边测同一个地址）、只提示不写设置；密码落盘只有密文、加载即迁移并当场落盘、解不开按未设置、
配置包同时剥明文与密文两个键；发布必须 `-Clean`，产物要过 `--probe-dir` + 干净 `--data-dir` 截图两道关。

验证口径：`-c Lead` 构建 **0 错误 0 警告**，`TestRunner` **1502/1502**（新增 36 例：
源测速 19 + 代理凭据 16，另 1 例为迁移落盘补充）。截图落在 `.probe/speed-*.png`、
`.probe/r27-proxy-*.png`、`.probe/pub2-*.png`。

仍未做：真实网络测速（无点击能力、无 CLI 入口）、跨 Windows 账户解密的真实路径、`portable` / `folder`
两种发布形态、发布产物的真实双击。

### 0.0.15 实现进度（2026-10-23 第二十八轮）

这一轮先是把上一轮留下的三件事做完（§0.0.14 的「仍未做」里第一条就是测速没有 CLI 入口），
接着把 `PCL2-GAP.md` 的清单清空。做完之后又对 PCL2 源码做了一次复查，查出八条新缺口，一并落地：

| 能力 | 分类 | 落点 |
|---|---|---|
| 源测速 CLI（`--probe-sources`） | 自研（把 27 轮的页内测速暴露成脚本入口） | `Core/Downloads/SourceProbeCli.cs`（`FormatReport` / `ExitCode` / `DecisionText` 三个纯函数）、`Program.cs`（`TryProbeSources`，与 `--probe-dir` 同级提前 `return`，两个豁免列表都加了） |
| 统一 Java 安装状态机 | 自研（三处各写一套 → 一份实现） | `App/ViewModels/JavaInstallState.cs`（411 行：7 相 / 6 结果 / 3 份文案 / 2 种落点）、首页与版本页各持一个实例、设置页 Java 分区改用它 |
| 发布自检 | 自研 | `build.ps1`：`Get-XamlSourceTime` + `Clear-StaleXamlIntermediates` + `Invoke-PublishVerify`（两道产物关）+ `-SkipVerify` |
| 默认注入的 JVM 参数 | port（`ModLaunch.vb:McLaunchArgumentsJVM`、`Settings.vb` 默认值） | `JvmArgumentBuilder.BuildCompatibilityArguments` / `BuildCompactObjectHeadersArguments`、`AuthlibInjectorService.BuildTrustStoreArguments`、`AppSettings.RelaxForgeChecks` |
| 启动参数去重与覆盖 | port（`ModLaunch.vb:DeduplicateJavaArguments`） | `Core/Launching/LaunchArgumentDeduplicator.cs`（含窗口参数「吸收」进 `MLaunchOption`） |
| 模组卡片「去资源站查找」 | port（`PageInstanceMod.xaml.vb:BtnManageDownload_Click`） | `ModsViewModel.LookupInLibrary` / `BuildLookupQuery`、`LibraryViewModel.ApplyLookup`、`LibraryLookupSlot` 单槽、`ModsView.axaml:653` |
| 停止运行中的游戏 | port（`FormMain.xaml.vb:BtnExtraShutdown_Click`） | `GameLauncher.KillCurrentGame` → `ProcessWait.UntilAsync` 复核；版本页徽标旁 + 首页运行态两个入口 |
| 内存三段可视化 | port（`PageSetupLaunch.xaml` 的 `RectRamUsed` / `RectRamGame` / `RectRamEmpty`） | `Core/Launching/MemorySnapshot.cs` + 设置页内存分区（两条 `ProgressBar` 叠放 + 三个图例 + 超额红字） |
| `--versionType` 自定义（主菜单/F3 版本名） | port（`ModLaunch.vb:1529-1542`） | `AppSettings.VersionArgumentInfo` + `InstanceInfo.VersionType`（三态：null 跟随全局 / "" 关闭 / 值）+ `GameLauncher.StripEmptyVersionType`（空值摘两个 token）+ 设置页「版本信息」与实例编辑面板两处 UI |
| `options.txt` 语言分版本规则 | port（`ModLaunch.vb:1844-1864`） | `GameLauncher.ApplyChineseLanguageCore`：≤10 后两位大写（否则 1.1~5 NPE 崩溃）、`saves` 判据、写两次触发缓存、Yosbr mod 兼容 |
| 双源合并搜索 | port（`Modules/Resource/ResourceSearcher.vb:292-537`） | `Core/Mods/CrossSourceSearch.cs`（`DownloadWeight` / `Score` / `IsSameProject` / `Merge`，17 例）、资源页第三档「双源」 |
| 多实例并行运行 | port（`ModWatcher.vb:McWatcherList`、`McInstance.TryAcquire`） | `RunningGame` + `RunningGameRegistry`、`IGameLauncher` 追加三个成员、版本页按行会话状态、首页「另有 N 个实例在运行」 |
| 缺陷修复：`CloneWithMaxMemory` 漏三字段 | 自研（原有写法的注释就提醒过，实际漏了） | `GameLauncher.CloneWithMaxMemory` + `CoreLaunch_LaunchOptionsCloneTests` 反射守护 |

不能省的约束写在 `HANDOVER.md` §6.44–§6.52，共九条：发布自检的三道闸、跨页预填的顺序、
内存条叠放与字节比、双源「先配对再 Merge」、多开的占位与日志隔离、`CloneWithMaxMemory` 的反射守护、
去重的两种语义、JVM 参数的按档注入、合并类重构的像素对比验收。

验证口径：`-c Lead` 构建 **0 错误 0 警告**，`TestRunner` **1740/1740**（1502 → 1740，新增 238 例、
14 个测试文件），`build.ps1 -Publish` 23.5 MB / 3 文件且两道自检通过。截图落在 `.probe/r28-*.png`、
`.probe/multi-versions.png`、`.probe/mem-*.png`、`.probe/dual-library.png`、`.probe/lookup-*.png`、
`.probe/stop-*.png`、`.probe/javast-*.png`、`.probe/vtype-*.png`。

仍未做：真机跑通一次启动进主菜单（唯一主缺口）、Java 24/25 真机、双开端到端、双源态与右键菜单的截图
（截图模式无点击能力）、「其它占用 > 0」的内存黄段（本机截图时负载接近 0）。

### 0.0.16 实现进度（2026-10-23 第二十九轮）

这一轮把 PCL2 复查推进到**第三轮**：前两轮只覆盖了 `ModLaunch.vb` 的参数组装与几个页面，这一轮通读
`ModDownload.vb`（1389 行）、`ModJava.vb`、`ModCrash.vb`（`CrashReason` 全表）、`ModModpack.vb`（866 行）、
`Modules/Resource/*`、`Pages/PageDownload/*`、`Pages/PageInstance/*`、`Pages/PageSetup/*`，
Nebula 侧 202 个源文件递归复核。查出 7 条真缺口、8 条已确认等价、7 条明确不做、5 条未确定。

| 能力 | 分类 | 落点 |
|---|---|---|
| 四个启动入口统一走工厂 | 自研（修 P0 缺陷） | `App/Services/LaunchOptionsFactory.cs`——唯一构造点。服务器页「加入服务器」原来不设 `GameDirectory`，启动全局 `.minecraft`，隔离实例的模组全失效且绕过实例运行锁 |
| 崩溃归因「极短的程序输出」 | port（`ModCrash.vb:AnalyzeCrit3` :649-660） | `CrashRules`：五条件同时成立（无 `at net.` / 无 `INFO]` / 无 hs_err / 无 crash report / 长度 < 100），排在 75 条具体规则之后 |
| 崩溃归因「版本 JSON 多个 Forge」 | port（`ModCrash.vb:AnalyzeCrit1` :491） | `CrashRules`：精确整串 `Found multiple arguments for option fml.forgeVersion, …`。修复前这份日志被堆栈归因吃掉（把 `joptsimple` 当肇事包，指引用户去 mods 二分） |
| 下载源按响应头超时换源 | port（`ModDownload.vb:DlSourceLoader` :1317-1380） | `DownloadInfrastructure`：镜像元数据 5 秒 / 官方元数据 30 秒 / 资源文件 30 秒；超时仍抛 `HttpRequestException` 让 `DownloadQueue` 重试 |
| 服务端 `Launch Server.bat` | port（`ModDownloadLib.vb:McDownloadMenuSaveServer` :183-199） | `ServerDownloadService`：GB18030 按 `GetACP()` 自适应、`"java"` 裸命令、EULA 说明、已存在不覆盖；「复制启动命令」与之同源 |
| rar / 7z 可操作提示 | port（`ModModpack.vb:78-86`） | `ModpackService.DescribeOpenFailure`：按扩展名分支，提示「解压后重压 zip」，不透传英文异常 |
| Java 上限按加载器小版本分档 | port（`ModJava.vb:GetJavaRequirement`） | `JavaRequirementTable.LoaderRules`（7 条）+ `JavaLoadLimit`（补丁号级只建议、大版本级才阻断）+ `InstanceInfo.LoaderVersion` 落盘 + 全链路经工厂传递 |
| 实例级登录服务器（authlib） | port（`Settings.vb:VersionServerAuth*`、`PageLoginAuth.xaml.vb:22-30`） | `InstanceInfo.AuthServerUrl/AuthRegisterUrl/AuthServerName` 三态 + `LaunchOptions.InstanceAuthServerUrl` + `GameLauncher` 用 `??` 选地址 + 实例面板「登录服务器」卡片 |

不能省的约束写在 `HANDOVER.md` §6.53–§6.60，共八条：多入口各写一遍选项对象必然漏、handler 层拿不到
「要 headers 还是 body」、.NET 8 的 `Encoding.Default` 恒为 UTF-8、补丁号级只能做建议、`LoaderVersion`
是缓存不是三态、实例 authlib 只覆盖地址、崩溃兜底要五条件且在具体规则之后、`Select-String` 的 `**`
只等价一层目录。

验证口径：`-c Lead` 构建 **0 错误 0 警告**，`TestRunner` **1939/1939**（1740 → 1939，新增 199 例、
9 个测试文件），`build.ps1 -Publish` 23.6 MB / 3 文件且两道自检通过。截图落在 `.probe/authlib-instance.png`、
`.probe/servers-factory.png`，bat 四格对照的真机输出在 `.probe/bat-check/`。

仍未做：真机启动进主菜单、authlib 真实连通性、系统 ACP 切 65001、`8u320` 建议级提示未接 UI、
下载超时窗口对 `ResponseContentRead` 请求含 body。

### 0.0.18 实现进度（2026-10-23 第三十二轮）

这一轮回答的是「PCL2 的功能还有没有没抄的」。不再人工通读 43116 行 VB（前三轮复查收益递减），
改用四个机械信号定位漏项：PCL2 的 155 个 URL 端点、37 处 `OpenWebsite(...)` 外链调用、
覆盖率表 146 个落点存在性、113 行状态分布的重新统计。只对命中的条目人工看源码。

| 能力 | 分类 | 落点 |
|---|---|---|
| 版本清单四档分类 | port（`PageDownloadInstall.xaml.vb:452-478`） | `Models/Versions.cs` 新增 `VersionChannel.Special` 与纯函数 `VersionChannelClassifier.Classify(id, type, releaseTime)`：`1.` 开头的 snapshot 先纠偏成正式版（排除 combat/rc/experimental/pre）→ 11 个愚人节 id 白名单 → UTC+2 的 4/1 日期兜底。下载页加「远古版 / 愚人节版」筛选；已装版本、首页、服务器下拉三处共用 |
| 在 MC 百科搜索 | port（`PageInstanceMod.xaml.vb:772`） | `ModsViewModel.SearchMcmodCommand` + `McmodSearchUrl`（`https://www.mcmod.cn/s?key=<编码>&site=all&filter=0`）；模组卡片右键菜单项。搜索词复用 `BuildLookupQuery`，与「去资源站查找」同一套清洗 |
| 覆盖率表按行重算 | 自研（纠正虚高） | `.probe/pcl-coverage.md` 统计由「62/20/21/7/3」重算为 **49/46/11/0/7**；6 类不存在的落点类型名（`Card.cs` / `DialogService` / `EmptyState` / `ItemRepeater` / `IconButton` / `StringUtils`）改为真实落点；9 处「r28/r29 列 X 缺口」改为「已落地」并补落点 |

**明确记录不做（本轮新发现的两条）**：本地 Mod 的「查看更新日志」外链（`MyLocalModItem.xaml.vb:461-480`，
需要先给 `ContentUpdateInfo` 扩 `ChangelogUrls` 字段，已有「去资源站查找」覆盖同一意图）、
登录时跳过 SSL 证书校验（`ModNet.vb:1916` / `Settings.vb:123` 的 `ToolDownloadCert`，PCL2 用
`ServicePointManager` 一处全局接管，Nebula 的登录走独立 `HttpClient`，落地要改 client 生命周期）。

验证口径：`-c Lead` 构建 **0 错误 0 警告**，`TestRunner` **2082/2082**（2042 → 2082，新增 40 例：
分类器 30 例 + MC 百科 URL 3 例 + 筛选接线 6 例 + 误标纠偏断言 1 例），`build.ps1 -Publish` 23.6 MB / 3 文件、
两道自检通过（PNG 145661 字节）。

仍未验证：两条新增能力的点击/像素证据（截图模式没有对应档位开关与右键能力）；
`R31-BROKEN-WORK.md` §3.3 的四条真机项一条未动（正常退出、二次启动、多开、加载器真机）。

### 0.0.17 实现进度（2026-10-23 第三十轮）

这一轮的起点是**覆盖率盘点**：前三轮复查都集中在「参数组装 / 崩溃 / 下载 / Java / 实例」这条线上，
没人统计过 PCL2 那 113 个 `.vb` 到底查过多少个。盘完（`.probe/pcl-coverage.md`：已覆盖 62 / 等价 20 /
明确不做 21 / 未覆盖 7 / 不适用 3）发现整片盲区，本轮修掉其中五条、裁决不做两条。

| 能力 | 分类 | 落点 |
|---|---|---|
| CurseForge 文件列表翻页取全 | port（`ResourceVersion.vb:236-261`） | `CurseForgeService.GetFilesAsync`：原来单次 `pageSize=50`（JEI 的 3950 个文件只显示 50 条），现翻页 + 按 Id 去重 + 全局降序；终止判据用服务端返回条数推进，**对每页上限不敏感** |
| Modrinth 服务端插件识别 | port（`ResourceVersion.vb:189-195` 三段判定） | `ContentKind.Plugin = 5` + `ServerPluginPolicy`；识别读**原始 `categories`**（展示分类里没有 loader）、`WebUrl` 走 `/plugin/`、拒绝在 IO 之前 |
| 插件两个入口两种策略 | 自研（判断依据是用户意图） | 搜索安装拒绝、整合包导出入落 `plugins/`——批量还原场景拒绝会让整包残缺；`DirectoryNameFor(Plugin)` 也返回 `plugins/`，错误往「不崩」的方向漏 |
| 保留名 + NTFS 8.3 短名 | port（`ModValidate.vb:291` / `:294`） | `PathSafety` 补 `CLOCK$` / `COM0` / `LPT0`（`$` 是合法文件名字符，只能整名比较）+ `.{2,}~\d+`（序号可多位，比 PCL2 的 `\d` 宽一档） |
| 插件提示条接 XAML | 自研（VM 属性已有，绑定缺） | `LibraryView.axaml` 详情区绑 `HasServerPluginNotice` / `ServerPluginNotice` |
| PCL2 全模块覆盖率盘点 | 自研（长期缺的度量） | `.probe/pcl-coverage.md`（113 个 `.vb` / 43116 行逐个判定 + 落点 + 依据），判断「还有没有没抄的」看它 |

**裁决不做**：联机功能（`PageLinkMain.xaml.vb` 1116 行 / 12 个子系统）——上游在非官方分支直接
`Throw`「开源版无法联网获取配置」(:429-431)、配置与 EasyTier 二进制都来自 PCL 官方服务器、
要分发第三方二进制。功能拆解与 6 个可独立交付切片留在 `.probe/pcl-gap-r30.md` §2.1。
另三块盲区判不是缺口并给了证据链：`ModEvent`（WPF 声明绑定，Avalonia 用 `ICommand` 等价）、
`FormMain` 更新部分（`ModSecret.vb` 是开源版空桩）、`PageSetupSystem`（9 个入口：4 等价、5 不做）。

不能省的约束写在 `HANDOVER.md` §6.61–§6.65，共五条：分页取全对上限不敏感、`Plugin` 的三个坑、
同一语义两个入口给不同答案、覆盖率盘点表是唯一判据、`$` 与 8.3 短名。

验证口径：`-c Lead3` 构建 **0 错误 0 警告**，`TestRunner` **2033/2033**（1939 → 2033，新增 94 例、
11 个测试文件），`build.ps1 -Publish` 23.6 MB / 3 文件且两道自检通过。

仍未做：真机启动进主菜单、插件提示条无截图证据、CF 翻页在 429 下的表现、CF 侧的插件识别
（搜索固定 `classId=6`，不会混进 5）。

## 0.1 口径与事实核查（写报告前先纠正三条错误前提）
1. **Modrinth `dependencies[]` 并没有「已解析但未用」，而是完全没解析。**
   `Mods/ModrinthService.cs:618 ParseVersions` 只取 `id` / `project_id` / `version_number` / `version_type` / `game_versions` / `loaders` / `files[].*` / `hashes.sha1` / `changelog`，没有任何 `dependencies` 分支；`Models/Modrinth.cs:58-96 ModrinthVersion` 也没有对应字段。全仓 `grep dependenc` 只命中 `ModpackService.cs:149`（整合包 `dependencies` 段）与 `ModMetadataReader.cs:171`（`mods.toml` 的 `dependencies.*` 段）。所以条目 8 的第一步是补模型与解析，不是「接上已有数据」。
2. **下载候选列表化已经部分实现，不列入本清单。**
   `Downloads/DownloadInfrastructure.cs:59 Candidates` + `MirrorRewritingHandler.SendAsync`（同文件 155-216）已经做了「首选 + 兜底」两候选与 404/403/5xx 换源、手动跟随重定向、未知主机判定；`Downloads/LoaderMetaFetcher.cs:14 LoaderMetaUrls` 已覆盖 Fabric 的 `fabric-meta` 镜像。剩余缺口只有 Modrinth / CurseForge 的镜像候选（条目 22）。
3. **崩溃报告「读 crash-reports 最新 txt」已经实现，缺口在解析深度与可操作性。**
   `Launching/GameLauncher.cs:485-518 TryReadLatestCrashReport` 已按修改时间取最新 `crash-reports/*.txt`；`Launching/CrashAnalyzer.cs:15-65` 已内置 7 条规则。缺的是「按游戏自报路径精确取报告」「从 `Description:` 抽堆栈定位肇事 mod」「报告路径回传给 UI 以便打开」（条目 5）。
4. **P2 观察项是条目 18–22**：Java 探测缓存（18）、实例运行目录清理（19）、启动脚本导出（20）、只读目录覆盖（21）、实例多根目录 + Modrinth 镜像兜底（22）。P2 不代表不做，只是排在本轮四项与 P0/P1 之后。
5. **版本隔离三态已在本轮落地，不在本清单内**：写入时（2026-10-21 21:0x）`src/NebulaLauncher.Core` 已出现 `Instances/IsolationPolicy.cs:IsolationPolicyResolver`、`Models/Instances.cs:IsolationPolicyKind`、`Models/Settings.cs:IsolationPolicy`（默认 `"modded"`）、`Instances/InstanceContext.cs:ResolveLaunchTargetAsync`，能力与本条原设计等价（策略三态 + 首次启动自动建实例 + 回退全局）。同理 `Settings.cs:CurseForgeBaseUrl` 已允许用户填 `mod.mcimirror.top/curseforge` 之类镜像，所以条目 22 的镜像部分只保留 Modrinth。

## 0.2 已实现能力对照（下列内容不进入本清单）

| 能力 | 现有实现 |
|---|---|
| 原版 + Fabric/Quilt/Forge/NeoForge 安装 | `Installing/InstallService.cs:56 InstallAsync`（四个分支 232/246/300/342） |
| 启动参数组装 | `Launching/GameLauncher.cs:331 BuildLaunchOption`，`Models/Launching.cs:4 LaunchOptions` |
| Java 探测（注册表 + 厂商目录 + PATH + JAVA_HOME + scoop/.jdks） | `Platforms/PlatformService.cs:324 DiscoverJavaSearchRoots`、`Java/JavaService.cs:327 CollectCandidates` |
| Adoptium Java 下载 | `Java/JavaService.cs:287 DownloadRuntimeAsync` |
| 版本清单 / 本地扫描 / 加载器识别 | `Versions/VersionService.cs:54/83/264`，`BrokenReason` 在 301-341 |
| Modrinth 搜索与安装（sha1 校验、同名重命名、图标缓存） | `Mods/ModrinthService.cs:80/118/158` |
| 实例（版本隔离）建立与删除 | `Instances/InstanceService.cs:112/192`，隔离目录见 `Launching/GameLauncher.cs:312` |
| 当前实例上下文 + 隔离三态（本轮①） | `Abstractions/InstanceServices.cs:31 IInstanceContext`、`Instances/InstanceContext.cs`、`Instances/IsolationPolicy.cs`、`Models/Instances.cs:61 IsolationPolicyKind` |
| CurseForge 客户端（key 内置）+ 镜像地址配置（本轮④） | `Abstractions/CurseForgeServices.cs`、`Mods/CurseForgeService.cs:35 EmbeddedApiKey`、`Models/Settings.cs:258 CurseForgeBaseUrl` |
| 项目链接的 cfwidget 精确解析 + forgecdn 直链推导（本轮④收尾） | `CurseForgeService.cs:238 GetProjectBySlugAsync`（cfwidget，只在粘贴项目链接时走）、`:1941 CurseForgeSlugParser`、`:2018 CurseForgeCdn`（forgecdn fileId 推导）；详见 `HANDOVER.md` 6.13 |
| 模组页按「加载器 → 版本」分组（本轮②③） | `App/ViewModels/ModsViewModel.cs`、`App/Views/ModsView.axaml` |
| 手动世界备份 / 还原 | `Backups/BackupService.cs:88/201` |
| 服务器 Ping（MOTD 上色 + favicon） | `Servers/ServerService.cs:148`，`Servers/MinecraftProtocol.cs` |
| 崩溃归因（日志尾部 + 最新 crash-report 文本） | `Launching/CrashAnalyzer.cs:76`、`Launching/GameLauncher.cs:453` |
| 下载镜像候选与失败换源 | `Downloads/DownloadInfrastructure.cs:59/155` |
| 模组重复 / 版本不匹配检测（模组页手动触发） | `Mods/ContentService.cs:167/200`，接在 `App/ViewModels/ModsViewModel.cs:263 CheckConflicts` |
| 整合包导入（Modrinth / CurseForge / MultiMC / 普通 zip） | `Backups/ModpackService.cs:32/76` |

## 0.3 本轮四项的排除说明

本轮正在做：①实例上下文 ②模组页按「版本+加载器」分组 ③光影/资源包按版本加载 ④CurseForge 客户端。
本清单不重复这四项。核查时的落地进度已反映在 §0.2：①已落地 `IInstanceContext` 与隔离三态，④已落地 `CurseForgeService`（内置 key）与 `CurseForgeBaseUrl`，因此原「版本隔离三态」条目改为「实例重命名 + 删除到回收站」，条目 22 的镜像部分缩到 Modrinth。其余的项都在 `Abstractions/` 上新增方法或新增文件，不与四项抢同一批文件。

## 0.4 镜像现状（2026-10-21 实测，影响条目 22 与 CurseForge 方案）

| 端点 | 结果 |
|---|---|
| `https://mod.mcimirror.top/curseforge/v1/mods/search?gameId=432` | **503 / 570**（HMCL `HMCLDownloadProvider.java:148` 用的就是它） |
| `https://api.curseforge.com/v1/mods/search?gameId=432` 无 key | **403** |
| `https://api.curse.tools/v1/cf/mods/search` | 403 |
| `https://cfproxy.fly.dev/v1/mods/search` | DNS 失败 |
| `https://www.curseforge.com/api/v1/mods/search` | 403（Cloudflare 挡站内 API） |
| `https://api.cfwidget.com/minecraft/mc-mods/{slug}` | **200**（项目详情 + 全量文件，无搜索端点） |
| `https://mediafilez.forgecdn.net/files/{id/1000:0000}/{id%1000}/{name}` | **200**（按 fileId 推导直链，无需 key） |
| `https://media.forgecdn.net/files/...` | 403，不要用 |

结论：条目 22 的「CurseForge 镜像兜底」在本机不可行；CurseForge 走官方接口（key 内置），cfwidget 只在粘贴项目链接时做 slug 精确解析。
镜像恢复后也不需要改代码——把 `AppSettings.CurseForgeBaseUrl` 填成镜像地址即可复用官方 API 路径；
但 CDN 要映射成 `mod.mcimirror.top/files/...`，`mediafilez` 的推导规则不能直接套。

---

## 1. 启动前置检查（PreflightCheck）

- **来源**：
  - `JavaVersionConstraint.java:JavaVersionConstraint` 枚举 13 条约束（`isMandatory()` 区分强制/建议），调用点 `LauncherHelper.java:checkGameState`（449-570 行）在启动前逐条判定并弹「自动改用推荐 Java」确认框。
  - `HMCLGameRepository.java:getAutoAllocatedMemory(long available, Platform platform)`（514-531 行）：预留 512 MiB、8 GiB 阈值、32 位封顶 768 MB。
  - `HMCLGameRepository.java:shouldIsolateNewInstance`（435-443 行）说明「新建实例时按 modded 判定」，是启动前判定加载器状态的同源依据。
- **问题**：`GameLauncher.LaunchAsync` 目前只有两道前置判定：版本 json 是否存在（`GameLauncher.cs:115-122`）与是否找到满足下界的 Java（同文件 139-152）。用户把 `-Xmx` 设成 8192 但物理内存只有 8 GB、用 32 位 Java 配 4 GB 堆、路径里带 `;`、`mods/` 里两个 modid 冲突，都要等到进程启动后读崩溃日志才知道。
- **做法**：
  1. 新增 `src/NebulaLauncher.Core/Launching/PreflightChecker.cs`，纯函数式，不依赖进程启动：
     ```csharp
     public sealed record PreflightItem(PreflightSeverity Severity, string Title, string Detail, string? ActionId);
     public sealed record PreflightReport(IReadOnlyList<PreflightItem> Items) { public bool HasBlocker { get; } }
     public static class PreflightChecker
     {
         public static async Task<PreflightReport> CheckAsync(
             LaunchOptions options, ILauncherPaths paths, IJavaService java,
             IContentService content, IPlatformService platform, CancellationToken ct = default);
     }
     ```
  2. `Models/Launching.cs` 追加 `PreflightSeverity`（Info/Warning/Blocker）与 `PreflightItem`，避免 UI 层再造一套字符串解析。
  3. 检查项（每条都复用现有能力，不新增依赖）：

     | 检查 | 复用 | 失败文案（Blocker 级） |
     |---|---|---|
     | 游戏目录可写 | `Paths/PathProbe.cs:23 TestWritable` | 没有写入权限（原因） |
     | 磁盘剩余 ≥ 1 GB | `Paths/PathProbe.cs:169 CheckFreeSpace` | 磁盘可用空间不足（剩余 X） |
     | 路径含 `!` `;` | `string.Contains` | 路径含有会破坏命令行的字符 `!`/`;` |
     | `-Xmx` 与物理内存的关系 | `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` + 写入阈值参考 `HMCLGameRepository.getAutoAllocatedMemory`（预留 512 MiB 后再按 80%/20% 递减） | `-Xmx` > 物理内存 → Blocker「内存设置超出物理内存」；> 70% → Warning「建议不超过 X」 |
     | 32 位 Java 请求堆 > 1.5 GB | `Models/Java.cs:15 Is64Bit` | 32 位 Java 最多约 1.5 GB |
     | Java 版本上下界 | 条目 2 的约束集 | 当前 Java 17，该版本要求 Java 21 / 上限 Java 8 |
     | 加载器完整性 | `Versions/VersionService.cs:83 GetVersionAsync` 的 `Loader` + `BrokenReason`，再查 `versions/<id>/<id>.jar` 与 `libraries/` 关键库 | Forge 安装不完整，建议修复（条目 6） |
     | 模组冲突 / 版本不匹配 | `Mods/ContentService.cs:167 DetectConflicts`、`200 FindVersionMismatch` | 同上，带「禁用」动作 |
  4. 接入点：`Launching/GameLauncher.cs:102 LaunchAsync`，在现有版本 json 检查之后、`ResolveForVersionAsync` 之前调用；`HasBlocker` 为真时直接返回 `LaunchResult(false, -1, ...)`，把 `Detail` 拼成 `FailureReason`；`Warning` 级随 `LaunchProgress` 推给 UI 顶部提示条。
  5. 抽象层：`Abstractions/GameServices.cs` 增 `IPreflightChecker`（返回 `PreflightReport`），`LaunchingServiceCollectionExtensions.cs` 注册；`IGameLauncher.BuildCommandLineAsync` 复用同一 checker，让「查看命令」也能提前暴露问题。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~Preflight"
  ```
  新增用例：`MaxMemoryMb = 999999` 返回 Blocker 且标题为「内存设置超出可用内存」；`JavaPath` 指向 32 位 Java 且 `MaxMemoryMb = 4096` 返回 Blocker；正常组合返回 `HasBlocker == false`。手动验证：设置页把最大内存拖到 999999 后点启动，游戏不启动、首页出现中文原因。
- **风险**：物理内存探测在容器 / WSL 下返回宿主值，需对 `TotalAvailableMemoryBytes <= 0` 兜底为「跳过该项」；`-Xmx` 与模组实际占用无因果关系，70% 只能给 Warning 不能给 Blocker（建议：内存项默认 Warning，仅当 `MaxMemoryMb > 物理内存` 时升为 Blocker）；跨平台 `PathProbe.TestWritable` 会对只读目录做真实写探测，放在启动路径上需加 200 ms 超时缓存，否则每次启动多一次磁盘往返。

## 2. Java 约束集升级：上下界 + 加载器条件

- **来源**：
  - `JavaVersionConstraint.java:JavaVersionConstraint`（33-223 行）13 条约束：`VANILLA`、`GAME_JSON`（仅 1.7.10+ 且版本 JSON 有 `javaVersion`）、`MODDED_JAVA_7/8/16/17/21`（Forge + 游戏版本区间）、`LAUNCH_WRAPPER`（≤1.12.999 且 launchwrapper < 1.13 → Java ≤ 1.8.999）、`VANILLA_JAVA_8_51`（≥1.13 要求 ≥ 1.8.0_51）、`VANILLA_LINUX_JAVA_8`（Linux x64 + ≤1.12.999 → Java ≤ 8）、`VANILLA_X86`（ARM64 + <1.6 → 必须 x86）、`MODLAUNCHER_8`（1.16.3–1.17.1 的 Forge 需排除 JDK-8273826 的 Java 版本）。
  - `JavaManager.java:findSuitableJava`（318-360 行）：mandatory / suggested 两级打分，`chooseJava`（301-310 行）选「最接近目标」的版本。
  - `GameJavaVersion.java:getMinimumJavaVersion`（42-54 行）：`26.1+→25`、`1.20.5+→21`、`1.18+→17`、`1.17+→16`、`1.13+→8`。
- **问题**：`Models/Java.cs:52 JavaRequirement(int RequiredMajor, string Reason)` 只有下界；`Java/JavaService.cs:171 ResolveForVersionAsync` 用 `Where(Satisfies)` + `OrderBy(MajorVersion)` 选「最低满足版本」（204-208 行）。后果：1.12.2 的 Forge 实例会被判给 Java 17（LaunchWrapper 需要 ≤ Java 8，实际会 `ClassCastException`）；1.16.5 Forge 会被判给 Java 21（MODLAUNCHER_8 区间，实际需 ≤ 17）；1.7.10 Forge 会被判给任意高版本。`CoreLaunch_CrashAnalyzerTests` / `CoreAssets_JavaRequirementTests` 只测下界，看不到这些。
- **做法**：
  1. `Models/Java.cs` 增记录类型（不动现有 `JavaRequirement`，避免 252 个用例大面积改）：
     ```csharp
     public readonly record struct JavaConstraint(
         bool Mandatory,
         (int Min, int Max) GameMinor,          // 用 JavaRequirementTable.TryParseRelease 的 minor/patch 表达区间
         (int Min, int Max) Java,
         LoaderKind? RequiresLoader = null,
         string? Id = null);                     // "LAUNCH_WRAPPER" 等，便于日志与测试断言
     ```
  2. `Java/JavaRequirementTable.cs` 增 `public static readonly JavaConstraint[] All`（照抄 13 条，逐条在同一文件注释里标 HMCL 出处行号），并增：
     ```csharp
     public static (JavaConstraint? Mandatory, JavaConstraint? Suggested) Evaluate(
         string minecraftVersion, LoaderKind loader, int? jsonJavaMajor, int javaMajor);
     public static (int Min, int Max)? AllowedRange(string minecraftVersion, LoaderKind loader);
     ```
     规则顺序与 HMCL 一致：`GAME_JSON`（jsonJavaMajor 存在且 mc ≥ 1.7.10）优先做 mandatory 下界；`LAUNCH_WRAPPER` 与 `MODLAUNCHER_8` 是 mandatory 上界；`MODDED_JAVA_*` 是 suggested（不阻断，只提示）。
  3. `Core/Java/JavaService.cs:171 ResolveForVersionAsync` 改为：先按 `AllowedRange` 过滤（无满足者返回 null 并在日志写明「需要 Java 8，本机只有 17/21」），再在剩余集合里按 `OrderBy(MajorVersion)` 取最低；`GetRequirement`（160 行）保留给 UI 文案，新增 `GetAllowedRange` 供条目 1 使用。
  4. `LoaderKind` 由 `VersionService.GetVersionAsync` 的解析结果提供（`Versions/VersionService.cs:433 DetectLoader`），Forge/NeoForge 之外一律传 `Vanilla`。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~JavaConstraint"
  ```
  用例断言：`AllowedRange("1.12.2", LoaderKind.Forge)` 上界 ≤ 8；`AllowedRange("1.16.5", LoaderKind.Forge)` 上界 ≤ 17；`AllowedRange("1.20.1", LoaderKind.Forge)` = (17,17)；`AllowedRange("1.20.5", LoaderKind.Vanilla)` 下界 21；`Evaluate("1.17", LoaderKind.Vanilla, jsonJavaMajor: null, javaMajor: 16)` 为通过。
- **风险**：上界过严会把能跑的配置判死（`MODDED_JAVA_*` 在 HMCL 里本来就不是 mandatory，抄的时候必须保持 suggested，否则 1.16.5 Forge + Java 17 的合法组合会被拦）；`GAME_JSON` 依赖本地版本 json，未安装时只能查表，要与 `ReadFromDisk`（63 行）的「读不到返回 null」语义保持一致；`LoaderKind` 判断依赖版本目录名，用户手改目录名会误判，需以 json 的 `mainClass` 兜底（`GameComponentAnalyzer.java:122 isModded` 的判据可照抄）。

## 3. 本地模组依赖检查：缺前置 / 重复 modid

- **来源**：
  - `CrashReportAnalyzer.java:Rule.MOD_RESOLUTION_MISSING`（77 行，`Could not find required mod: (?<sourcemod>.*) requires (?<destmod>.*)`）、`MOD_RESOLUTION_MISSING_MINECRAFT`（78 行）、`FORGE_FOUND_DUPLICATE_MODS`（75 行）、`DUPLICATED_MOD`（71 行）——这些规则的存在证明「缺前置」「重复 mod」是最高频的两类崩溃。
  - `CrashReportAnalyzer.java:Rule.MOD_RESOLUTION`（73 行）与 `MOD_RESOLUTION_COLLECTION`（79 行）：Fabric / Quilt 的解析失败文本。
  - `hmcl` 未落盘「模组列表页做前置检查」的实现（`ModListPage` 只在 `GameInstancePage.java:59` 被引用），因此本条只借规则文本，不借 UI 流程。
- **问题**：`Mods/ModMetadataReader.cs:161 ReadToml` 的 `dependencies` 分支（169-182 行）只取 `modId == "minecraft"` 的 `versionRange`；`ReadFabricMinecraftRange`（349-375 行）只读 `depends.minecraft`。也就是：jar 里明明写着 `depends: ["fabric-api >= 0.9"]` / `mods.toml` 的 `[[dependencies.xxx]] modId="jei"`，启动器读完后全部丢弃。用户只能靠崩溃后翻 `ModResolutionException` 才知道缺前置。
- **做法**：
  1. `Mods/ModMetadataReader.cs` 的 `ModMetadata`（9-29 行）增：
     ```csharp
     public IReadOnlyList<ModDependency> Depends { get; init; } = Array.Empty<ModDependency>();
     public IReadOnlyList<ModDependency> Breaks  { get; init; } = Array.Empty<ModDependency>();
     ```
     新增 `internal sealed record ModDependency(string ModId, string? VersionRange, bool Optional);`
  2. `ReadToml`（161 行）：遍历所有 `dependencies.<parent>` section，跳过 `minecraft`（保留给 `McVersionRange`），其余收进 `Depends`；`ReadMod` 的 fabric 分支（99-126 行）读 `depends` 与 `breaks` 两个对象，`minecraft` 仍走 `ReadFabricMinecraftRange`。
  3. `Models/Content.cs:13 ContentItem` 增 `IReadOnlyList<ModDependency> Depends`（`init`，保持不可变；`ModDependency` 放 `Models/Content.cs` 以便 UI 直接用）。
  4. `Abstractions/ContentServices.cs:4 IContentService` 增：
     ```csharp
     IReadOnlyList<ModDependencyIssue> DetectDependencyIssues(IReadOnlyList<ContentItem> mods);
     ```
     `Models/Content.cs` 增 `ModDependencyIssue(string ModId, string MissingId, string RequiredRange, IReadOnlyList<string> Files)`。
  5. `Mods/ContentService.cs` 实现（紧邻 `DetectConflicts`:167）：按已启用 mod 的 `Id` 建 `HashSet`（大小写不敏感，与 `DetectConflicts` 的 `StringComparer.OrdinalIgnoreCase` 一致），对每个 `Depends` 判定缺失；`Optional == true` 归为 Info，不阻断。
  6. `App/ViewModels/ModsViewModel.cs:263 CheckConflicts`（当前是同步方法）里追加遍历 `DetectDependencyIssues`，文案：`缺少前置 jei（要求 >= 15.0），当前模组：xxx.jar`；`SelectedTarget?.Target.MinecraftVersion`（287 行）已经是现成的目标版本来源，不需要再读全局设置。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~ModMetadata"
  ```
  用例：构造两个 in-memory zip——A 的 `fabric.mod.json` 写 `"depends": {"fabric-api": ">=0.9"}`，B 只提供 `fabric-api` 时无问题、B 缺失时 `DetectDependencyIssues` 返回一条；`mods.toml` 含两个 `[[dependencies.xxx]]` 时 `Depends.Count == 2` 且不含 `minecraft`。手动验证：mods 目录只放 JEI（依赖 fabric-api）→ 模组页点「检查冲突」出现缺前置提示。
- **风险**：`mods.toml` 的 section 名形如 `dependencies.<parentModId>`，`TomlLite`（`Mods/TomlLite.cs`）是否支持带点 section 名需先验证——不支持时要先扩展 `TomlDocument.Sections`；Forge 的 `modId` 与 jar 内 `modId` 大小写/别名不一致（历史上存在 `cfm` vs `MrCrayfishFurnitureMod`）会误报，建议缺前置只给 Warning 不阻断启动；`ContentItem` 加 `init` 字段会牵动 `ModelSmokeTests` 与 `CoreAssets_ContentTests` 的构造点，需一并补默认值。

## 4. Java 扫描扩展：Mojang runtime + 其他启动器 + 自定义路径

- **来源**：
  - `JavaManager.java:searchPotentialJavaExecutables`（371-501 行）：`searcher.searchAllOfficialJava` 扫官方 runtime（432-443 行），`HMCL_JRES` 环境变量（463-471 行），PATH 里跳过 `\common files\oracle\java\`（451-455 行）。
  - `JavaManager.java:Searcher.searchAllOfficialJava`（786-823 行）与 `tryAddJavaInComponentDir`（729-768 行）：官方 runtime 的目录形状是 `runtime/<component>/<platform>/<component>/bin/java`，且会读同目录的 `<component>.sha1` 清单做存在性校验。
  - `JavaManager.java:queryJavaInRegistryKey`（868-884 行）：要求子键下存在 `MSI` 子键才采信 `JavaHome`，避免采到残留注册表项。
- **问题**：`Platforms/PlatformService.cs:324 DiscoverJavaSearchRoots` 只有 Program Files 厂商目录、`%LOCALAPPDATA%\Programs`、`.jdks`、scoop 与 HKLM `JavaSoft` 注册表；`Java/JavaService.cs:327 CollectCandidates` 只认 `JAVA_HOME` / `JDK_HOME` / PATH。最常见的一类用户是「只装过官方启动器」，其 Java 在 `%APPDATA%\.minecraft\runtime\java-runtime-delta\windows-x64\...`，NebulaLauncher 扫不到，只能让用户手动下载一份（几十到上百 MB）。
- **做法**：
  1. `Platforms/PlatformService.cs:324 DiscoverJavaSearchRoots` 增（Windows 分支）：
     - `%APPDATA%\.minecraft\runtime`
     - `%LOCALAPPDATA%\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime`
     - `%ProgramFiles(x86)%\Minecraft Launcher\runtime`
     - `%APPDATA%\.hmcl\java`、`%APPDATA%\PrismLauncher\java`、`%APPDATA%\ModrinthApp\meta\java_versions`
  2. 新增 `JavaService` 侧的「官方 runtime 布局」展开（`runtime/<component>/<platform>/<component>/bin` 四层，HMCL `tryAddJavaInComponentDir` 同构），放在 `Java/JavaService.cs:327 CollectCandidates` 之前，与 `_platform.JavaSearchRoots` 的通用展开并列；平台键用条目 7 的 `MojangJavaPlatform` 映射。
  3. `Java/JavaService.cs:327 CollectCandidates` 增环境变量 `NEBULA_JAVA_PATHS`（`Path.PathSeparator` 分隔，语义照抄 HMCL 的 `HMCL_JRES`），并在 PATH 遍历时跳过包含 `\common files\oracle\java\` 的条目。
  4. `Platforms/PlatformService.cs:399 ReadJavaHomesFromRegistry` 增 HKCU 视图与厂商键（`Eclipse Adoptium`、`Microsoft\JDK`、`Azul Systems\Zulu`、`BellSoft\Liberica`、`Semeru\JDK`），读取 `JavaHome` / `InstallationPath`，并保留「找不到就静默返回空」的现有语义。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~JavaSearchRoots"
  ```
  用例：构造临时目录树 `runtime/java-runtime-delta/windows-x64/java-runtime-delta/bin/javaw.exe`（空文件即可，只断言候选路径被枚举），断言 `CollectCandidates` 产出的路径包含它；`NEBULA_JAVA_PATHS` 设为一个含 java 的临时目录时被收集。手动验证：在没有手动指定 Java 的机器上打开设置 → Java，列表里出现「Java 21（来源 runtime）」。
- **风险**：扫更多目录意味着启动时更多子进程探测（现在 `DetectAsync` 最多 64 个候选、4 并发、每个 10 秒超时），目录扩一倍会让首次探测从数秒涨到十几秒——必须同时做条目 18 的缓存，或至少把官方 runtime 的 sha1 清单校验降级为「只看文件存在」；macOS/Linux 分支要保持空操作（`PlatformService.cs:367-390` 现有结构已按平台分支，新增项放在 Windows 块内即可）；用户把 runtime 目录移到别处时不报错、只是扫不到，不要抛异常。

## 5. 崩溃报告深度解析：路径标记 + 肇事 mod 定位 + 规则扩容

- **来源**：
  - `CrashReportAnalyzer.java:CRASH_REPORT_LOCATION_PATTERN`（182 行）与 `findCrashReport(String log)`（184-192 行）：游戏崩溃时 stdout 会打印 `#@!@# Game crashed! Crash report saved to: #@!@# <path>`，HMCL 用它精确定位报告文件，而不是猜「最新修改的 txt」。
  - `extractCrashReport(String rawLog)`（194-199 行）：从 `---- Minecraft Crash Report ----` 到 `#@!@# Game crashed!` 之间截取，避免把整段调试日志当报告。
  - `CRASH_REPORT_STACK_TRACE_PATTERN`（201 行）+ `findKeywordsFromCrashReport`（218-250 行）+ `PACKAGE_KEYWORD_BLACK_LIST`（204-216 行）：从 `Description:` 后的堆栈里过滤掉 `net` / `minecraft` / `java` / `forge` / `fabricmc` 等通用包名，剩下的第一个包名基本就是肇事 mod。
  - `CrashReportAnalyzer.java:Rule`（36-149 行）60+ 条规则：与 NebulaLauncher 现有 7 条相比，多出 `JVM_32BIT`（40 行）、`TOO_OLD_JAVA`（39 行，带 `expected` 组）、`JAVA_VERSION_IS_TOO_HIGH`（119 行）、`MOD_FILES_ARE_DECOMPRESSED`（107 行）、`INCOMPLETE_FORGE_INSTALLATION`（129 行）、`DUPLICATED_MOD`（71 行）、`MOD_RESOLUTION_MISSING`（77 行）。
- **问题**：`Launching/GameLauncher.cs:485 TryReadLatestCrashReport` 取「目录里最新的 txt」，同一实例连续崩溃两次或用户手动复制旧报告时会读错；`CrashAnalyzer` 的规则只做整段 `Contains`，拿不到「是哪个 mod 崩的」；`Models/Launching.cs:69 CrashReport` 没有报告路径字段，UI 无法提供「打开崩溃报告」。
- **做法**：
  1. `Launching/CrashAnalyzer.cs` 增三个纯函数（便于单测）：
     ```csharp
     public static string? ExtractCrashReportPath(string? log);          // 正则 #@!@# Game crashed! Crash report saved to: #@!@# (?<p>.*)
     public static string? ExtractCrashSection(string? log);             // ---- Minecraft Crash Report ---- 到标记之间
     public static IReadOnlyList<string> FindSuspectPackageNames(string? crashReport);
     ```
     `FindSuspectPackageNames` 照抄 HMCL 的 `STACK_TRACE_LINE_PATTERN` 取 `at <method>(<file>)`，按 `.` 切包名，过滤 `PACKAGE_KEYWORD_BLACK_LIST`（先抄 HMCL 那份 200+ 词），按出现顺序去重返回。
  2. `Launching/CrashAnalyzer.cs:15-65 Rules` 从 7 条扩到「HMCL 高价值 20 条 + PCL 常见 10 条」，每条仍是 `(Patterns, Title, Detail, Severity)`；新增 `Suggestion` 增加可选动作枚举（`OpenJavaSettings` / `OpenModsFolder` / `RepairVersion` / `OpenCrashReport`），`Models/Launching.cs:76 CrashSuggestion` 加 `string? ActionId`。
  3. `Models/Launching.cs:69 CrashReport` 加 `string? CrashReportPath`；`GameLauncher.AnalyzeCrash`（453 行）改为：先 `ExtractCrashReportPath(tailText)` 拿到路径并读文件，读不到才退回 `TryReadLatestCrashReport`；把路径装进 `CrashReport`。
  4. `LaunchResult.CrashAnalysis`（`Models/Launching.cs:40`）保持字符串不变（避免破坏 UI 绑定），新增 `LaunchResult.CrashReport`（可空对象），`App/ViewModels/HomeViewModel.cs` 的崩溃面板增加「打开崩溃报告」「打开 mods 目录」两个按钮，走 `IPlatformService.RevealInFileManagerAsync`（`Abstractions/Infrastructure.cs:65`）。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~CrashAnalyzer"
  ```
  用例：喂一段含 `#@!@# Game crashed! Crash report saved to: #@!@# C:\x\crash-reports\crash-2026-01-01.txt` 的日志，断言 `ExtractCrashReportPath` 返回该路径；喂 HMCL 风格的 `Description: Ticking entity` + 堆栈里出现 `com.example.mymod.Mixin`，断言 `FindSuspectPackageNames` 首个元素为 `example`（`com` 在黑名单里）；现有 7 条规则的 8 个用例必须仍全绿。
- **风险**：包名黑名单是启发式，误判（把玩家的 `com.foo` 判成肇事者）概率不低，结论只能表述为「疑似」并同时给出原文片段；`Description:` 在 Forge / Fabric / vanilla 三种报告里的位置不同，正则要允许前导空格；新规则扩大后可能出现「一条日志命中 6 条规则」的噪音，需保留现有 `matchedTitles` 去重（`CrashAnalyzer.cs:104`）并按 Severity 排序截断到前 3 条。

## 6. 实例级校验与修复（快速 / 深度两档）

- **来源**：
  - `DefaultDependencyManager.java:checkGameCompletionAsync`（232-252 行）：`Task.allOf` 三路并行——客户端 jar（缺失或 0 字节就重下，`GameDownloadTask`）、资源（`GameAssetDownloadTask`）、库（`GameLibrariesTask`），全部带 `integrityCheck` 参数。
  - `GameLibrariesTask.java:shouldDownloadLibrary`（90-121 行）：`!integrityCheck` 时只判 `Files.isRegularFile`，`integrityCheck` 时才校验 `DownloadInfo.validateChecksum` + `LibraryDownloadTask.checksumValid` + zip 完整性（`ZIP_INTEGRITY_CHECK_HANDLER.checkIntegrity`）。
  - `GameAssetDownloadTask.java:89-116`：资源对象同样分「不存在才下」（95-101 行）与「`integrityCheck` 且 sha1 不匹配才下」（97-98 行）两档；全量 hash 数万资源文件的耗时代价见 `.probe/research-launchers.md:§6.5`（HMCL 源码本身没有写这个数字）。
  - `GameAssetIndexDownloadTask.java:76-94`：asset index 文件先用 sha1 校验，sha1 不可用时退化为「能否解析成 `AssetIndex`」，解析失败即重下（`GameAssetIndexMalformedException`，108 行）。
  - `GameVerificationFixTask.java:execute`（64-73 行）：MC < 1.6 且 `hasComponent(FORGE)` 时，用 `CompressingUtils.createWritableZipFileSystem` 打开 `versions/<id>/<id>.jar`，删掉 `META-INF/MOJANG_C.DSA` 与 `META-INF/MOJANG_C.SF`。
  - `GameInstancePage.java:220-260`：实例页把「重下 asset index」「清库」「清资源」「清垃圾文件」拆成四个独立动作，而不是一个笼统的「修复」。
- **问题**：`Abstractions/VersionServices.cs:37 VerifyAsync(string versionName, ...)` 只有一档，`Installing/InstallService.cs:198` 的实现就是「重跑 `MinecraftLauncher.InstallAsync`」——CmlLib 的检查粒度是「文件存在即跳过」，被截断 / 被替换的 jar 不会发现。pre-1.6 Forge 的签名冲突、asset index 损坏、client jar 为 0 字节这三类高频问题都没有入口。
- **做法**：
  1. `Models/Downloads.cs:54 InstallRequest` 增枚举：
     ```csharp
     public enum RepairDepth { Quick = 0, Deep = 1 }
     public sealed record RepairReport(int Missing, int Mismatched, int Redownloaded, IReadOnlyList<string> Fixed, IReadOnlyList<string> Failed);
     ```
  2. `Abstractions/VersionServices.cs:28 IInstallService` 增 `Task<RepairReport> RepairAsync(string versionName, RepairDepth depth, IProgress<DownloadProgress>? progress, CancellationToken ct)`；`VerifyAsync` 保留（现调用点 `App/ViewModels/VersionsViewModel.cs:490` 不动，改为内部转调 `RepairAsync(Quick)`）。
  3. `Installing/InstallService.cs` 新增 `RepairAsync` 实现：
     - Quick：现有 `InstallAsync` 路径（CmlLib 补缺失文件）+ 版本 json 能否解析（`Versions/VersionService.cs:301` 已有 `BrokenReason` 判据）+ client jar 存在且 `Length > 0`。
     - Deep：自己遍历 `versions/<id>/<id>.json` 与 `inheritsFrom` 链，对 `libraries[].downloads.artifact{path,sha1}` 与 `assetIndex` 指向的 objects 逐个比 sha1；不匹配的加入 `DownloadQueue.DownloadItem`（`Downloads/DownloadQueue.cs:10` 已支持 `ExpectedSha1`），复用现有并发与重试。
     - pre-1.6 Forge 清理：MC < 1.6 且 `VersionInfo.Loader is Forge` 时，用 `ZipArchive`（`System.IO.Compression`，已在 `Mods/ModMetadataReader.cs` 使用）以 Update 模式打开 `versions/<id>/<id>.jar`，`Delete` 掉 `META-INF/MOJANG_C.DSA`、`META-INF/MOJANG_C.SF`（不存在则忽略）。
  4. UI：`App/ViewModels/VersionsViewModel.cs` 的修复按钮改下拉两档，Deep 前弹确认框写明「将重新校验 libraries 与 assets，可能耗时数分钟」。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~Repair"
  ```
  用例（用假 `HttpMessageHandler` 提供 sha1 与内容）：删掉 `libraries/` 下一个 jar → `RepairAsync(Deep)` 后文件回来且报告 `Redownloaded >= 1`；把一个 jar 内容改成不同字节但保持存在 → Quick 不动、Deep 重下；构造 `versions/x/x.jar` 为 0 字节 → Quick 触发重下；构造含 `META-INF/MOJANG_C.SF` 的 1.5.2 Forge jar → 修复后该 entry 不存在。
- **风险**：Deep 档全量 hash 在低端机上可能超过 10 分钟且大量小文件 IO，必须可取消、必须显示进度、默认不选；`assetIndex` 的 sha1 在 Mojang 侧会被回改（`GameAssetIndexDownloadTask.java:96-97` 注释），因此 asset index 只做「能解析」判定、不做 sha1 判定，否则会无限重下；改 `IInstallService` 是接口冻结边界（`Abstractions/`），`InstallService.cs:21` 是当前唯一实现、测试项目里没有该接口的 fake，但 App 层所有调用点（`App/ViewModels/VersionsViewModel.cs:490`、`DownloadViewModel.cs`）都要跟着改签名，建议用默认接口实现（DIM）把 `RepairAsync` 挂上去以减少改动面。

## 7. Java 下载改用 Mojang `java-runtime` manifest

- **来源**：
  - `MojangJavaDownloadTask.java:JAVA_LIST_URL`（48 行）：`https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json`。
  - `MojangJavaDownloadTask.java` 构造器（58-79 行）：从 all.json 的 `downloads[platform][component]` 取候选，要求 `JavaInfo.parseVersion(version.name()) >= javaVersion.majorVersion()`，再取 `manifest.url`。
  - `MojangJavaDownloadTask.java:execute`（87-165 行）：逐文件走 `raw` 或 `lzma`（lzma 下载后解压并校验解压结果的 sha1），`isExecutable()` 时 `FileUtils.setExecutable`，`RemoteLink` 建符号链接；`postExecute`（178-190 行）先解到 tempDir 再整体换目录，避免半成品。
  - `GameJavaVersion.java`（33-73 行）：`jre-legacy`(8)、`java-runtime-alpha`(16)、`java-runtime-beta`(17)、`java-runtime-delta`(21)、`java-runtime-epsilon`(25) 与 `component` 的对应关系。
  - `JavaManager.java:getMojangJavaPlatform`（82-106 行）：平台键 `windows-x86` / `windows-x64` / `windows-arm64` / `linux` / `linux-i386` / `mac-os` / `mac-os-arm64`。
- **问题**：`Java/JavaService.cs:287 DownloadRuntimeAsync` 走 Adoptium `api.adoptium.net`。这个域名不在 `Downloads/DownloadInfrastructure.cs:15 BmclApiMap` 里，也就没有镜像候选，国内网络经常直接超时；而 Mojang 的 `piston-meta` 已经被映射到 `bmclapi2.bangbang93.com`（同文件 20 行），下载 Mojang runtime 天然吃镜像。另外 Adoptium 的版本与版本 json 里的 `javaVersion.component` 不对应，无法回答「这个实例该用哪份 runtime」。
- **做法**：
  1. 新增 `src/NebulaLauncher.Core/Java/MojangRuntimeCatalog.cs`：
     ```csharp
     public static string? PlatformKey(bool isWindows, bool isMac, bool isLinux, bool is64Bit, bool isArm);
     public static string ComponentFor(int major);   // 8→jre-legacy,16→java-runtime-alpha,17→…-beta,21→…-delta,25→…-epsilon
     public static Task<JavaRuntimePackage?> ResolveAsync(HttpClient http, int major, string platformKey, CancellationToken ct);
     ```
     `ResolveAsync` 解析 all.json 拿 `manifest.url`，返回 `Models/Java.cs:32 JavaRuntimePackage`（`DownloadUrl` = manifest URL，`ArchiveType` 用自定义值 `"manifest"`）。
  2. `Java/JavaService.cs:287 DownloadRuntimeAsync`：先试 `MojangRuntimeCatalog.ResolveAsync`（走现有 `DownloadHttpClientFactory`，从而吃到 piston-meta 镜像），拿不到再退回 `BuildAdoptiumUrl`（318-323 行）。`InstallRuntimeAsync`（218 行）新增 manifest 分支：逐文件下载到 staging → 校验 sha1 → 设置可执行位（`File.SetUnixFileMode`）→ 整体 `PromoteToTarget`（516 行已有该函数）。
  3. `Models/Java.cs:32 JavaRuntimePackage` 增 `string? Component`、`IReadOnlyList<JavaRuntimeFile>? Files`，供 manifest 分支使用。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~MojangRuntime"
  ```
  用例：内置一份 all.json 片段（`windows-x64` / `java-runtime-delta`）→ `ResolveAsync` 返回非空且 `DownloadUrl` 以 `piston-meta` 开头；`PlatformKey(true,false,false,true,false) == "windows-x64"`；`ComponentFor(21) == "java-runtime-delta"`。手动验证：设置 → Java → 下载 Java 21，日志里出现 `bmclapi2.bangbang93.com/v1/products/java-runtime/...`。
- **风险**：Mojang runtime 只有 8/16/17/21/25 五个组件，Java 11、Java 22 等中间版本没有——必须保留 Adoptium 兜底；lzma 格式解压需要 `LZMAInputStream`（HMCL 用 `org.tukaani.xz`），.NET 没有内置 lzma 解码，第一版只支持 `raw` 下载（体积更大但可行），`lzma` 分支留 TODO；manifest 里可能出现符号链接（`RemoteLink`），Windows 建链接需权限，遇到就跳过并记日志。

## 8. Modrinth 依赖解析 + 自动安装前置

- **来源**：
  - 本项 **HMCL 侧未确认**：HMCL 的模组下载/依赖递归在 `ModDownloadListPage` / `Modpack` 相关类里，这些文件不在 `hmcl/` 落盘集合中（只在 `HMCLGameRepository.java:25-26` 出现 `import org.jackhuang.hmcl.modpack.ModAdviser` / `Modpack` 的引用）。
  - 可用依据是 Modrinth `GET /v2/project/{id}/version` 响应里的 `dependencies[]`（字段 `version_id` / `project_id` / `file_name` / `dependency_type` ∈ `required|optional|incompatible|embedded`），以及 `.probe/research-launchers.md:§6.9` 记录的实测请求与「`required` 递归、`optional` 提示、`incompatible` 阻断、`embedded` 跳过、深度上限 8」。
  - 现状证据：`Mods/ModrinthService.cs:618 ParseVersions` 完全丢弃该数组，`Models/Modrinth.cs:58 ModrinthVersion` 无字段（见 §0.1 第 1 条）。
- **问题**：用户装 JEI / Sodium 时不会知道自己还要装 Fabric API / Cloth Config；装完启动崩在 `ModResolutionException`。现有崩溃规则里虽然匹配了「依赖缺失」（`Launching/CrashAnalyzer.cs:60-64`），但那是事后补救。
- **做法**：
  1. `Models/Modrinth.cs` 增：
     ```csharp
     public enum ModrinthDependencyType { Required, Optional, Incompatible, Embedded }
     public sealed record ModrinthDependency(string? VersionId, string? ProjectId, string? FileName, ModrinthDependencyType Type);
     ```
     `ModrinthVersion`（58-96 行）加 `IReadOnlyList<ModrinthDependency> Dependencies { get; init; } = Array.Empty<ModrinthDependency>();`
  2. `Mods/ModrinthService.cs:618 ParseVersions` 增解析分支（`item.dependencies` 数组，`dependency_type` 映射到枚举，未知值按 `Required` 保守处理）。
  3. `Abstractions/ContentServices.cs:73 IModrinthService` 增：
     ```csharp
     Task<Models.ModrinthInstallPlan> PlanInstallAsync(ModrinthVersion root, string? gameVersion, LoaderKind loader, CancellationToken ct);
     Task<Models.ModrinthInstallResult> InstallPlanAsync(Models.ModrinthInstallPlan plan, string gameDirectory, ContentKind kind, IProgress<DownloadProgress>? progress, CancellationToken ct);
     ```
     `Models/Modrinth.cs` 增 `ModrinthInstallPlan(IReadOnlyList<ModrinthVersion> Required, IReadOnlyList<ModrinthVersion> Optional, IReadOnlyList<string> Blocked)`。
  4. `PlanInstallAsync` 实现：`HashSet<string> visited` 防环（key 用 `projectId`），队列式展开 `Required`；`Incompatible` 记进 `Blocked` 并终止该分支；`Optional` 只收集不递归；每层对 `GetVersionsAsync(projectId, gameVersion, loader)` 取第一个（已按发布时间降序，`ParseVersions` 669 行）。深度上限 8。
  5. UI：`App/ViewModels/ModsViewModel.cs` 的安装确认框展示「将同时安装 3 个前置：fabric-api、cloth-config…」，`Blocked` 非空时禁止继续。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~ModrinthDependencies"
  ```
  用例：内置一段 `/v2/project/jei/version` 响应（含 2 个 `required`、1 个 `optional`、1 个 `incompatible`），断言 `Dependencies.Count == 4` 且类型映射正确；用假 handler 让递归返回「A 依赖 B、B 依赖 A」，断言 `PlanInstallAsync` 不死循环且 `Required.Count == 2`。手动验证：真实网络装一个依赖 Fabric API 的模组，`mods/` 下同时出现前置 jar。
- **风险**：Modrinth 部分项目用 `version_id` 而不是 `project_id` 指定依赖，需二次请求 `/v2/version/{id}` 才能拿到 projectId，会放大请求数（建议先只处理 `project_id` 非空的依赖，`version_id` 分支留 TODO 并降级为 Optional）；递归自动装包会在用户不知情时多下几十 MB，确认框必须列出全部清单与总大小；`loader` 过滤参数用现有 `LoaderCategoryOf`（`ModrinthService.cs:560`），Quilt 项目常以 Fabric 构建发布，需要在 loader 匹配上做 `Quilt → ["quilt","fabric"]` 的宽松匹配，否则会漏装。

## 9. 模组更新检查（mods.lock.json + sha1 反查）

- **来源**：
  - 本项 **HMCL 侧未确认**：HMCL 的模组更新检查在 `ModListPage` / `ModUpdateCheckTask`，未在 `hmcl/` 落盘。
  - 可用依据：`Mods/ModrinthService.cs:158 InstallAsync` 与 `Models/Modrinth.cs:98 ModrinthInstallResult`——安装结果只有 `FilePath` / `FileName` / `Bytes` / `Renamed`，**不记录 `ProjectId` / `VersionId`**，所以现在无法回答「这个 jar 是哪个项目、有没有新版」。
  - Modrinth 侧能力：`GET /v2/version_file/{hash}?algorithm=sha1` 反查版本（`.probe/research-launchers.md:§6.9` 记录实测可用），`InstallAsync` 已经逐块算 sha1 并在不匹配时丢弃文件（`Mods/ModrinthService.cs:240-279`）。
  - 崩溃规则旁证：`CrashReportAnalyzer.java:Rule.DUPLICATED_MOD`（71 行）说明「同一 mod 两个版本共存」是高频问题，更新检查顺带能解决它。
- **问题**：用户装了 40 个模组后，没有任何入口知道「哪些能升级、哪些已停更、哪些是两个版本共存」。模组页只有一个「检查冲突」按钮（`App/ViewModels/ModsViewModel.cs:263 CheckConflicts`）。
- **做法**：
  1. 新增 `src/NebulaLauncher.Core/Mods/ModLockService.cs`：
     ```csharp
     public sealed record ModLockEntry(string RelativePath, string Sha1, string? ProjectId, string? VersionId, string? FileName, DateTimeOffset InstalledAt);
     public sealed class ModLockService
     {
         public Task<IReadOnlyList<ModLockEntry>> LoadAsync(string gameDirectory, CancellationToken ct = default);
         public Task SaveAsync(string gameDirectory, IReadOnlyList<ModLockEntry> entries, CancellationToken ct = default);
         public Task<bool> RecordAsync(string gameDirectory, string filePath, string sha1, string? projectId, string? versionId, CancellationToken ct = default);
     }
     ```
     文件落在 `<gameDirectory>/mods.lock.json`，与 `.probe/research-launchers.md:§6.9` 的 schema 一致。
  2. `Mods/ModrinthService.cs:158 InstallAsync` 落盘成功后调用 `RecordAsync`（需要把 `ILauncherPaths` 现成注入，`ModrinthService` 构造器已有 `ILauncherPaths? paths`，53-58 行）。
  3. `Abstractions/ContentServices.cs:4 IContentService` 增 `Task<IReadOnlyList<ModUpdate>> CheckUpdatesAsync(string gameDirectory, string? gameVersion, LoaderKind loader, CancellationToken ct)`；`Models/Content.cs` 增 `ModUpdate(ContentItem Item, string? ProjectId, string CurrentVersion, string? LatestVersion, string? DownloadUrl, bool HasUpdate)`。
  4. 新增 `Mods/ModVersionResolver.cs`：无 lock 记录时用 jar 的 sha1 调 `/v2/version_file/{sha1}?algorithm=sha1`；有记录时直接 `/v2/project/{projectId}/version` 取最新。二者失败都降级为「未识别」，不阻断。
  5. UI：模组页加「检查更新」按钮，列表按 `HasUpdate` 分组，批量更新复用 `InstallAsync`（覆盖安装时先删旧文件，注意 `Models/Content.cs:45 IsEnabled` 的 `.disabled` 后缀）。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~ModLock"
  ```
  用例：临时目录写 jar → `RecordAsync` 后 `LoadAsync` 读回；lock 里记录 `projectId=p1`，假 handler 返回更高 `date_published` 的版本 → `CheckUpdatesAsync` 的 `HasUpdate == true`；handler 返回 404 → `HasUpdate == false` 且不抛异常。
- **风险**：`mods.lock.json` 与用户手动拖入的 jar 混用会让列表一半「已识别」一半「未识别」，UI 必须给出明确分组而不是把它们都标成「无更新」；Modrinth 对未收录的 jar 返回 404，这是常态而非错误，不能写日志 ERROR；批量更新会改动 `mods/`，必须在游戏运行时禁用（`IGameLauncher.IsGameRunning`，`Abstractions/GameServices.cs:18`）。

## 10. 整合包导出（`.mrpack`）

- **来源**：
  - `GameInstancePage.java:286-291 export()` → `Instances.exportInstance(gameInstance)`：HMCL 实例页把导出放在右键菜单（`GameInstancePage.java:390`，i18n key `modpack.export`）。`Instances` 类未落盘，导出实现细节未确认。
  - `HMCLGameRepository.java:duplicateInstance`（331-379 行）展示了「实例打包/复制时该排除什么」：`ModAdviser.MODPACK_BLACK_LIST` + `srcId.id() + ".jar"` + `srcId.id() + ".json"` + 可选 `saves`，并用 `Modpack.acceptFile(path, blackList, null)` 过滤。`Modpack` 类未落盘，黑名单内容未确认。
  - 现有导入侧的格式依据：`Backups/ModpackService.cs:137-204 ReadModrinth`（读 `modrinth.index.json` 的 `name` / `versionId` / `dependencies` / `files[].path`）与 `328-377 ExtractOverrides`（`overrides/` 前缀）。
- **问题**：`Backups/ModpackService.cs` 只有 `InspectAsync` / `ImportAsync`（`Abstractions/ContentServices.cs:31-42`），没有导出。用户配好一套 100 个模组的实例后，无法分享或迁移；换机器只能整目录拷贝，把 `libraries/`、`assets/`、`logs/` 几十 GB 一起带走。
- **做法**：
  1. `Abstractions/ContentServices.cs:31 IModpackService` 增：
     ```csharp
     Task<Models.ModpackExportResult> ExportAsync(string instanceId, string targetArchivePath, Models.ModpackExportOptions options, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default);
     ```
     `Models/Content.cs` 增 `ModpackExportOptions(bool IncludeSaves, bool IncludeConfig, bool IncludeResourcePacks, string Name, string Version, bool PreferModrinthHashes)` 与 `ModpackExportResult(bool Success, string ArchivePath, int Files, int UnresolvedMods, long Bytes, string? Error)`。
  2. 新增 `src/NebulaLauncher.Core/Backups/ModpackExporter.cs`：
     - 遍历 `<instanceRoot>/mods/*.jar`，用现有 `Mods/ModMetadataReader.ReadFile` 取元数据，用 `Mods/ModLockService`（条目 9）取 `projectId`/`versionId`；有 lock 记录且 `PreferModrinthHashes` 时写进 `files[]`，无记录的 jar 作为 `overrides/mods/xxx.jar` 原样打包并计入 `UnresolvedMods`。
     - `modrinth.index.json` 按 `Backups/ModpackService.cs:137` 的读取结构反向生成：`formatVersion: 1`、`game: "minecraft"`、`versionId`、`name`、`summary`、`dependencies{minecraft, fabric-loader|forge|neoforge|quilt-loader}`（Loader 与 LoaderVersion 取自 `Models/Versions.cs:44 VersionInfo`）、`files[{path, hashes{sha1,sha512}, env{client,server}, downloads[], fileSize}]`。
     - overrides 目录：`config/`、`resourcepacks/`、`shaderpacks/`、`options.txt`，以及 `IncludeSaves` 时的 `saves/`；排除 `logs/`、`crash-reports/`、`screenshots/`、`downloads/`（对齐 `HMCLGameRepository.duplicateInstance` 的黑名单思路）。
     - 打包用 `ZipArchive`（已有用法见 `Backups/BackupService.cs:111-146`）。
  3. `App/ViewModels/` 对应实例列表（本轮①的实例上下文页面）加「导出为整合包」菜单项。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~ModpackExport"
  ```
  往返用例：构造实例目录（2 个 mod + config/a.txt + saves/w1/level.dat）→ `ExportAsync` → 用 `ModpackService.InspectAsync` 读回，断言 `Format == ModrinthModpack`、`MinecraftVersion` 正确、`Files.Count == 2`；再 `ImportAsync` 到新实例，断言 mods 数量一致。排除用例：断言 zip 里没有 `logs/` 与 `crash-reports/`。
- **风险**：`.mrpack` 的 `hashes.sha512` 也必须提供，只给 sha1 会导致部分第三方工具拒绝；`projectId` 未知的 jar 只能进 overrides，导出包体积可能等于原 mods 目录（对分享场景可接受，但要在结果里报 `UnresolvedMods`）；`options.txt` 里有按键绑定与语言设置，跨用户分享会覆盖对方配置，建议默认不打包并放进选项。

## 11. 实例克隆

- **来源**：
  - `HMCLGameRepository.java:duplicateInstance(GameInstanceID srcId, GameInstanceID dstId, boolean copySaves)`（331-379 行）：先 `openDraft()` 占位，`FileUtils.copyDirectory(srcDir, dstDir, path -> Modpack.acceptFile(path, blackList, null))`，黑名单包含 `MODPACK_BLACK_LIST` + 源实例的 `id.jar`/`id.json`，`copySaves == false` 时加 `saves`；复制后单独处理 jar 改名（350-354 行）；`draft.commit()` 成功才对外可见，失败 `abort` 并删除目标目录。
  - `GameInstancePage.java:308-311 duplicate()`：实例页右键菜单入口（`SVG.FOLDER_COPY`，389 行）。
  - `InstanceLocator.cs:51 MakeUniqueName`（NebulaLauncher 已有）可复用作命名策略。
- **问题**：`Abstractions/InstanceServices.cs:4 IInstanceService` 只有 `CreateAsync` / `SaveAsync` / `DeleteAsync` / `GetGameDirectory`。想做「同一版本、不同模组组合」只能手抄目录，容易漏 `config/` 或把 `saves/` 一起带过去。
- **做法**：
  1. `Abstractions/InstanceServices.cs:4` 增：
     ```csharp
     Task<Models.InstanceInfo> CloneAsync(string sourceInstanceId, string? newName = null, bool includeSaves = false, IProgress<double>? progress = null, CancellationToken ct = default);
     ```
  2. `Instances/InstanceService.cs` 实现：
     - 源目录 = `GetGameDirectory(sourceInstanceId)`；目标 = `CreateAsync(MakeUniqueName(...), source.VersionName)` 拿到的目录。
     - 复制白名单目录：`mods/`、`config/`、`resourcepacks/`、`shaderpacks/`、`defaultconfigs/`、`kubejs/`、`scripts/`，可选 `saves/`；复制 `options.txt`、`servers.dat`、`instance.json`。`instance.json` 需按当前 `Models/Instances.cs:7 InstanceInfo` 的全部字段重建：`Id` 换新、`Name` 用唯一名、`CreatedAt = UtcNow`、`LastPlayedAt = null`、`TotalPlayTimeSeconds = 0`，`VersionName` / `Loader` / `MinecraftVersion` / `IsolationEnabled` / `MaxMemoryMb` / `JavaPath` / `IconPath` 原样继承。
     - 排除：`logs/`、`crash-reports/`、`screenshots/`、`downloads/`、`.cache/`，以及目标实例自己的 `instance.json`（最后单独写）。
     - 复制用 `File.Copy` + 进度回调（按文件数，不按字节，避免大文件拖动进度条抖动）；`FileShare.ReadWrite` 打开源文件以容忍游戏占用（对齐 `Backups/BackupService.cs:127` 的 `FileShare.ReadWrite` 用法）。
  3. 失败语义：中途异常删除目标目录（与源隔离），返回异常给 UI，不留下半个实例。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~InstanceClone"
  ```
  用例：源实例放 `mods/a.jar`、`config/b.txt`、`saves/w1/level.dat`、`logs/latest.log` → `includeSaves: false` 克隆后，目标有 `a.jar`/`b.txt`、无 `saves`、无 `logs`；`includeSaves: true` 时有 `w1`；两个实例 `Id` 不同、`Name` 自动加 ` (2)`（复用 `InstanceLocator.MakeUniqueName`）；克隆后的 `Loader` / `MinecraftVersion` 与源一致（`InstanceInfo` 新增字段不能丢）。
- **风险**：大实例（10 GB mods + 1 GB saves）复制没有取消点会让 UI 假死，必须传 `CancellationToken` 并在每 64 个文件检查一次；Windows 上 `File.Copy` 遇到只读属性文件会抛，需先清 `FileAttributes.ReadOnly`（学习条目 21 的处理方式）；源实例正在运行时克隆可能复制到写了一半的 `options.txt`，建议 UI 在 `IsGameRunning` 时提示「建议先关闭游戏」。

## 12. 实例图标

- **来源**：
  - `GameInstanceIconType.java:GameInstanceIconType`（26-73 行）：15 个内置图标（`DEFAULT`/`GRASS`/`CHEST`/`CHICKEN`/`COMMAND`/`OPTIFINE`/`CRAFT_TABLE`/`FABRIC`/`FORGE`/`NEO_FORGE`/`FURNACE`/`QUILT`/`APRIL_FOOLS`/`CLEANROOM`/`LEGACY_FABRIC`），并提供 `getIconType(ModLoaderType)`（47-58 行）与 `getIconType(GameComponentType)`（60-73 行）两个映射——即「按加载器自动选图标」。
  - `GameSettings.java:155-160`：`icon = newSettingProperty("icon", GameInstanceIconType.DEFAULT)`，图标是实例级设置的持久化字段，不是 UI 临时状态。
- **问题**：`Models/Instances.cs:16 IconPath` 字段早就存在，但没有任何写入接口，`IInstanceService`（`Abstractions/InstanceServices.cs:4`）没有 `SetIconAsync`，UI 也没有入口。9 个实例在列表里长得一模一样。
- **做法**：
  1. `Models/Instances.cs` 增：
     ```csharp
     public enum InstanceIconKind { Default, Grass, Chest, Command, CraftTable, Furnace, Chicken, Optifine, Fabric, Quilt, Forge, NeoForge, Custom }
     ```
     `InstanceInfo` 加 `InstanceIconKind IconKind { get; set; } = InstanceIconKind.Default;`（保留现有 `IconPath` 给自定义图片）。
  2. `Abstractions/InstanceServices.cs:4` 增 `Task SetIconAsync(string instanceId, Models.InstanceIconKind kind, string? customImagePath = null, CancellationToken ct = default);`，实现写 `instance.json`（`InstanceService.SaveAsync` 已有原子写：临时文件 + `File.Move(overwrite)`，144-189 行）。
  3. 图标资源放 `src/NebulaLauncher.App/Assets/InstanceIcons/`（新建），命名与枚举一一对应；`App/Views/` 的实例卡片用 `IconKind` 走 `DataTemplate` 选择器，`Custom` 时回落到 `IconPath`。
  4. 自动推荐：新建实例时按 `VersionInfo.Loader`（`Models/Versions.cs:54`）映射到 `Fabric/Quilt/Forge/NeoForge`，逻辑放 `Instances/InstanceLocator.cs`（纯函数，便于测试），照抄 `GameInstanceIconType.getIconType` 的映射表。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~InstanceIcon"
  ```
  用例：`SetIconAsync(id, InstanceIconKind.Forge)` 后重新 `LoadAsync`，断言 `Instances[i].IconKind == Forge`；`InstanceLocator.SuggestIcon(LoaderKind.NeoForge) == InstanceIconKind.NeoForge`。手动验证：改图标 → 重启启动器 → 仍是该图标。
- **风险**：`InstanceInfo` 是可变类并参与 `Clone`（`InstanceService.cs:271-282`），加字段必须同步 `Clone`，否则保存后再读会丢；自定义图片路径可能是实例目录外的绝对路径，`instance.json` 会记录宿主机路径，克隆/导出到别的机器会失效——建议自定义图片复制进 `<instanceRoot>/icon.png` 并只存相对路径。

## 13. 自动内存分配（按物理内存推荐 `-Xmx`）

- **来源**：
  - `HMCLGameRepository.java:getAutoAllocatedMemory(long available, Platform platform)`（514-531 行）：先扣 512 MiB 给 off-heap 与启动器自身；可用 ≤ 8 GiB 时取 80%；超过 8 GiB 时取 `min(8 GiB × 0.8 + 超出部分 × 0.2, 16 GiB)`；32 位平台再封顶 768 MiB。
  - `GameSettings.java:PROPERTY_AUTO_MEMORY`（465-473 行）：`autoMemory` 默认 `true`，`PROPERTY_MAX_MEMORY`（489-496 行）默认 `SUGGESTED_MEMORY`；自动与手填是互斥开关。
  - 崩溃侧旁证：`CrashReportAnalyzer.java:Rule.OUT_OF_MEMORY`（51 行）与 `JVM_32BIT`（40 行）——两处都指向「内存设置不当」是第一批崩溃成因。
- **问题**：`Models/Settings.cs:26 MaxMemoryMb = 4096` 是写死默认值，`App/ViewModels/SettingsViewModel.cs` 只有一个滑块。16 GB 机器上 4096 偏小（大型整合包 OOM），8 GB 机器上 4096 尚可，32 GB 机器上用户根本不知道该填多少。
- **做法**：
  1. 新增 `src/NebulaLauncher.Core/Launching/MemoryAdvisor.cs`：
     ```csharp
     public static class MemoryAdvisor
     {
         public static long TotalPhysicalBytes();                       // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes，失败返回 0
         public static int SuggestMaxMemoryMb(long totalBytes, bool is64Bit);   // 照抄 HMCL 公式
         public static Models.MemoryAssessment Assess(int configuredMb, long totalBytes, bool is64Bit);
     }
     ```
     `Models/Launching.cs` 增 `MemoryAssessment(bool Ok, bool OverPhysical, int SuggestedMb, string? Message)`。
  2. `Models/Settings.cs` 增 `public bool AutoMaxMemory { get; set; } = true;`（默认开，与 HMCL 一致），保留 `MaxMemoryMb` 作为「自动关闭后的值」。
  3. `App/ViewModels/SettingsViewModel.cs` 加「按本机内存自动推荐」开关与一行说明（`建议值：12 GB（物理 32 GB）`）；`App/Views/SettingsView.axaml` 在内存滑块旁展示 `MemoryAdvisor.Assess` 的结论。
  4. 启动时若 `AutoMaxMemory` 为真，`GameLauncher.LaunchAsync` 用 `SuggestMaxMemoryMb` 覆盖 `options.MaxMemoryMb`，并在 `LaunchProgress` 里写一句「已按物理内存自动分配 12 GB」。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~MemoryAdvisor"
  ```
  用例（按 HMCL 公式手算，断言用 MB 区间避免 GiB/MB 混淆）：`SuggestMaxMemoryMb(16 GiB, true)` ∈ [7900, 8300]（15.5 GiB usable → 6.4+1.5 = 7.9 GiB ≈ 8090 MB）；`SuggestMaxMemoryMb(8 GiB, true)` ≈ 6144 MB；`SuggestMaxMemoryMb(32 GiB, true)` ≈ 11366 MB；`SuggestMaxMemoryMb(32 GiB, false) == 768`；`Assess(999999, 16 GiB, true).OverPhysical == true`。手动验证：设置页显示与物理内存匹配的建议值。
- **风险**：`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` 在容器里返回 cgroup 限额，可能远小于物理内存——正好符合需求，但要在 UI 上说明「按可用内存」而不是「物理内存」；`AutoMaxMemory` 与实例级 `InstanceInfo.MaxMemoryMb`（`Models/Instances.cs:25`）的优先级必须写死：实例显式值 > 全局自动值，否则用户给某个大整合包单独设的值会被自动值悄悄覆盖。

## 14. 世界备份轮转 + 退出后自动备份

- **来源**：
  - **HMCL 侧未确认**：HMCL 的世界管理与备份在 `WorldListPage` / `WorldBackupTask`，未在 `hmcl/` 落盘（`GameInstancePage.java:60 worldListTab` 只是引用）。
  - 现有实现缺口证据：`Backups/BackupService.cs:164 ListBackupsAsync` 无任何过滤参数，`DeleteAsync`（250 行）只能单条删除；`Abstractions/ContentServices.cs:45 IBackupService` 没有「保留策略」概念。备份只能手动点，长期使用会堆成几十 GB。
  - 自动触发的落点：`Launching/GameLauncher.cs:220` `WaitForExitAsync` 之后（同位置已有 `RecordLaunchAsync` 调用，229-236 行），可复用的写盘容错方式见 `Backups/BackupService.cs:140-144`（`IOException` 跳过单个文件继续备份）。
- **问题**：玩家最常见的灾难是「存档被模组/误操作写坏」，而备份必须手动点。备份目录无上限，用户也不会主动清理。
- **做法**：
  1. `Abstractions/ContentServices.cs:45 IBackupService` 增：
     ```csharp
     Task<Models.BackupPruneResult> PruneAsync(string? worldName = null, int keep = 0, CancellationToken ct = default);
     Task<Models.BackupEntry> CreateAutoAsync(string worldDirectory, CancellationToken ct = default);
     ```
     `Models/Content.cs` 增 `BackupPruneResult(int Deleted, int Kept, long FreedBytes)`。
  2. `Backups/BackupService.cs`：`CreateAutoAsync` 用固定 note `"auto"` 并复用 `CreateAsync`（88-162 行）；`PruneAsync` 按 `WorldName` 分组、按 `CreatedAt` 倒序，保留前 `keep` 条，删除其余 zip 与同名 `.meta.json`（`DeleteAsync` 已有成对删除逻辑，250-274 行）；`ListBackupsAsync` 增可选 `worldName` 参数（保持旧签名重载）。
  3. `Models/Settings.cs` 增 `public int BackupKeepPerWorld { get; set; } = 5;`、`public bool AutoBackupOnExit { get; set; } = true;`、`public bool AutoBackupKeepOnlyAuto { get; set; } = true;`（只轮转自动备份，不动用户手动备份）。
  4. `Launching/GameLauncher.cs:220` 之后：`AutoBackupOnExit` 为真且退出码为 0 或崩溃（两种情况都要备份，崩溃时更该备份）时，对该实例 `saves/*` 逐个 `CreateAutoAsync`，再 `PruneAsync(worldName, keep)`。整体放在 `Task.Run` 里并吞掉异常（备份失败不能影响启动结果）。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~BackupRotation"
  ```
  用例：同一世界连备 6 次、`PruneAsync(keep: 3)` → `ListBackupsAsync` 剩 3 条且 `Deleted == 3`；手动备份（note 非 `auto`）不被删；`CreateAutoAsync` 生成的 zip 能被 `RestoreAsync` 还原（复用 `CoreAssets_BackupTests.cs` 的既有断言风格）。
- **风险**：退出时备份大存档（500 MB+）会让「游戏关闭」到「启动器恢复」之间卡住几十秒，必须异步且可跳过（给一个「后台备份中」的状态，而不是阻塞退出）；崩溃场景下 `saves/` 可能处于半写状态，备份出来的是损坏快照，建议只备 `saves/` 下 `level.dat` 与 `region/` 且标注「崩溃后备份」；`PruneAsync` 删除是不可逆操作，`keep <= 0` 时应拒绝执行而不是删光。

## 15. JVM 参数预设（全局预设 + 实例覆盖）

- **来源**：
  - `GameSettings.java:PROPERTY_JVM_OPTIONS`（405-412 行）、`PROPERTY_NO_JVM_OPTIONS`（417-424 行）、`PROPERTY_NO_OPTIMIZING_JVM_OPTIONS`（429-436 行）、`PROPERTY_GAME_ARGS`（610-617 行）——JVM 参数、关闭全部 JVM 参数、关闭优化参数、游戏参数是四个独立可继承项。
  - `GameSettings.java` 的 `Instance` / `Preset` / `Effective` 三层（82-232、861-940 行）与 `isOverridden`（902-906 行）：预设定义默认值，实例只覆盖差异项。
  - `LauncherHelper.java:makeLaunchScript(Path scriptFile)`（145 行）：参数最终要能落成脚本，说明参数组装必须是可导出的结构化数据，而不是一段拼接字符串。
- **问题**：`Models/Settings.cs:34 ExtraJvmArguments` 是一个全局字符串，`LaunchOptions.ExtraJvmArguments`（`Models/Launching.cs:30`）原样透传。用户给 1.12.2 加 `-Dfile.encoding=UTF-8`、给 1.20.1 加 `--add-opens`、给大型包加 `-XX:+UseG1GC -XX:G1NewSizePercent=20`，只能每次手改全局字符串，且没有「这套参数叫什么」的概念。
- **做法**：
  1. `Models/Settings.cs` 增：
     ```csharp
     public sealed class JvmPreset
     {
         public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
         public string Name { get; set; } = "默认";
         public string Arguments { get; set; } = string.Empty;
         public IReadOnlyList<string>? AppliesToVersions { get; set; }   // 空 = 全部
         public IReadOnlyList<Models.LoaderKind>? AppliesToLoaders { get; set; }
     }
     public List<JvmPreset> JvmPresets { get; set; } = new();
     public string? ActiveJvmPresetId { get; set; }
     ```
     `Models/Instances.cs:7 InstanceInfo` 增 `public string? JvmPresetId { get; set; }` 与 `public string? ExtraJvmArguments { get; set; }`。
  2. 新增 `src/NebulaLauncher.Core/Launching/JvmArguments.cs`：
     ```csharp
     public static string Compose(AppSettings settings, InstanceInfo? instance, VersionInfo version);
     public static IReadOnlyList<string> Split(string? args);   // 直接复用 GameLauncher.SplitArguments 的实现，提到公共位置
     ```
     优先级：实例 `ExtraJvmArguments` > 实例绑定预设 > 全局 `ActiveJvmPresetId` > 全局 `ExtraJvmArguments`；`AppliesTo*` 不匹配时跳过预设。
  3. `Launching/GameLauncher.cs:348-352` 改为调用 `JvmArguments.Compose`；`BuildCommandLineAsync` 同源，保证「查看命令」与实际启动一致。
  4. UI：设置页新增「JVM 参数预设」列表（增删改 + 应用范围），实例设置页加下拉选择预设。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~JvmArguments"
  ```
  用例：全局预设 `-XX:+UseG1GC` 且实例未覆盖 → `Compose` 含该参数；实例设 `-Xmx6G` 覆盖 → 输出只含实例值；预设 `AppliesToLoaders = [Forge]` 但版本是 Fabric → 跳过。手动验证：实例设置页切换预设后「查看启动命令」的参数随之变化。
- **风险**：参数拆分沿用 `Parser.ParseCommandLineToArguments`（`GameLauncher.cs:573`）的容错行为——引号未闭合时按空格切分，用户写 `-Dfoo="a b"` 可能被拆错，预设 UI 需要给「参数校验」提示；`--add-opens` 一类参数在旧 Java 上会导致启动失败，预设若带适用范围不匹配会自动跳过，但全局 `ExtraJvmArguments` 仍会原样透传（保持现有行为，不静默过滤）；`AppSettings` 加字段会改变 `settings.json` 形状，旧文件读入时 `JvmPresets` 为 null，`SettingsService` 的 JSON 选项需容忍 null 并回填默认值（`Settings/SettingsService.cs` 现有 `LoadAsync` 已用 `JsonSerializer`，需加 `PropertyNameCaseInsensitive` 与默认值初始化）。

## 16. 实例 `managedPack` 元数据

- **来源**：
  - `GameInstancePage.java:118 currentInstanceUpgradable.set(gameInstance != null && gameInstance.isModpack())` 与 `272 updateGame()`：实例页区分「整合包实例」与「普通实例」，整合包实例才有「更新」入口。
  - `ModpackService` 相关类引用见 `HMCLGameRepository.java:25-26`（`ModAdviser` / `Modpack`），落盘材料里没有它们的实现，`managedPack` 字段形状**未确认**；此处按 `.probe/research-launchers.md:§6.10` 给出的 schema（`type` / `id` / `name` / `versionId` / `url`）设计。
  - 现有实现缺口：`Backups/ModpackService.cs:107-119 ImportAsync` 写 `InstanceInfo` 时只填 `Id` / `Name` / `VersionName` / `CreatedAt`，来源信息（哪个 mrpack、哪个版本）写进 `_log` 就丢了。
- **问题**：导入的整合包和用户自己配的实例在数据结构上完全一样。要做「检查整合包更新」（条目 10 的下一步）就必须知道来源，否则会把用户自己加的 mod 一起覆盖。
- **做法**：
  1. `Models/Instances.cs` 增：
     ```csharp
     public sealed class ManagedPackInfo
     {
         public string Type { get; set; } = "modrinth";   // modrinth | curseforge | mmc
         public string? ProjectId { get; set; }
         public string? Name { get; set; }
         public string? VersionId { get; set; }
         public string? Url { get; set; }
         public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
         public string? SourceArchive { get; set; }
     }
     ```
     `InstanceInfo` 加 `public ManagedPackInfo? ManagedPack { get; set; }`，并在 `InstanceService.Clone`（271-282 行）里同步复制。
  2. `Backups/ModpackService.cs:76 ImportAsync`：把 `ModpackPreview.Format`（`Models/Content.cs:89`）映射为 `Type`，`Name`/`Version` 写进 `ManagedPackInfo`，`SourceArchive` 记原压缩包路径。
  3. `Models/Content.cs` 的 `ModpackPreview` 增 `string? ProjectId` 与 `string? VersionId`（Modrinth 的 `modrinth.index.json` 里有 `versionId`，`ReadModrinth`:143 已解析但只存 `Version`；CurseForge 的 `manifest.json` 有 `projectID`，当前 `ReadCurseForge`:261-304 没读）。
  4. UI：实例卡片显示「整合包 · <名称> v<版本>」，并提供「检查整合包更新」入口（先只做「比对当前 versionId 与远程最新 versionId」，不实现自动更新）。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~ManagedPack"
  ```
  用例：构造最小 `.mrpack`（`modrinth.index.json` 带 `name`/`versionId`/`dependencies`）→ `ImportAsync` → `LoadAsync` → 断言 `ManagedPack.Type == "modrinth"` 且 `VersionId` 与 index 一致；手动导入的普通 zip → `ManagedPack == null`。
- **风险**：旧实例（本轮①之前创建的）没有该字段，反序列化为 null 是正常态，UI 不能把它当成错误；`SourceArchive` 记绝对路径会随目录移动失效，建议只存文件名或干脆不持久化；CurseForge 的 `manifest.json` 用 `projectID`（大写 ID）而 Modrinth 用 `project_id`，字段映射要在同一处集中处理，避免两套解析。

## 17. 实例重命名 + 删除到回收站（可恢复）

> 本条原为「版本隔离三态」，核查时发现该能力已由本轮①落地（`Instances/IsolationPolicy.cs:IsolationPolicyResolver`、`Models/Instances.cs:IsolationPolicyKind`、`Models/Settings.cs:IsolationPolicy`、`Instances/InstanceContext.cs:ResolveLaunchTargetAsync`），已从清单移除，替换为本条。

- **来源**：
  - `DefaultGameRepository.java:renameInstance(GameInstanceID from, GameInstanceID to)`（420-429 行）：走 `openDraft()` → `draft.rename` → `draft.commit()`，任何失败返回 `false` 而不是抛异常。
  - `DefaultGameRepository.java:moveInstanceFiles(baseDirectory, from, to)`（372-412 行）：依次移动目录 → `<old>.json` → `<old>.jar`，**任一步失败逐级回滚**（把 `to` 移回 `from`），并在回滚失败时用 `addSuppressed` 保留原始异常。
  - `DefaultGameRepository.java:removeInstanceFromDisk(GameInstanceID id)`（441-493 行）：已注册实例走 draft 移除并发布快照；未注册的孤儿目录先 `Files.move(file, file + "_removed")`，再 `FileUtils.moveToTrash(removedFile)`，只有移入回收站失败才退化为删 json（472-478 行）。
  - `HMCLGameRepository.java:isValidInstanceId` / `FORBIDDEN_INSTANCE_IDS`（472-487 行）：重命名时要挡住与整合包配置文件冲突的名字（`modpack` / `minecraftinstance` / `manifest`），并做 Windows 大小写不敏感判重。
- **问题**：`Instances/InstanceService.cs:192 DeleteAsync` 直接 `Directory.Delete(directory, recursive: true)`，删掉一个配好 100 个模组的实例没有任何回收手段；`IInstanceService`（`Abstractions/InstanceServices.cs:4-19`）也没有重命名入口，用户只能改 `instance.json` 或重建实例。目录名是 12 位随机 Id，改目录名没有意义，缺的是**显示名的重命名**与**删除的可撤销**。
- **做法**：
  1. `Abstractions/InstanceServices.cs:4 IInstanceService` 增：
     ```csharp
     Task<Models.InstanceInfo> RenameAsync(string instanceId, string newName, CancellationToken ct = default);
     Task<Models.InstanceTrashEntry?> MoveToTrashAsync(string instanceId, CancellationToken ct = default);
     Task<IReadOnlyList<Models.InstanceTrashEntry>> ListTrashAsync(CancellationToken ct = default);
     Task<bool> RestoreFromTrashAsync(string trashId, CancellationToken ct = default);
     Task PurgeTrashAsync(CancellationToken ct = default);
     ```
     `Models/Instances.cs` 增 `InstanceTrashEntry(string TrashId, string OriginalId, string Name, string VersionName, DateTimeOffset DeletedAt, long SizeBytes)`。
  2. `Paths/LauncherPaths.cs` 增 `string InstancesTrashDirectory { get; }` = `Path.Combine(RootDirectory, "instances-trash")`（**放在 `instances/` 之外**，这样 `InstanceService.LoadAsync` 的枚举逻辑天然不会把回收站当实例扫进来，`Instances/InstanceService.cs:59`）。`Abstractions/Infrastructure.cs:4 ILauncherPaths` 同步加属性。
  3. `Instances/InstanceService.cs`：
     - `RenameAsync`：只改 `Name`（`instance.json` 的显示名），复用现有 `SaveAsync` 的原子写（144-189 行）；重名检查用 `InstanceLocator.MakeUniqueName`（`InstanceLocator.cs:51`）的同名判定逻辑，或直接允许重名但提示。
     - `MoveToTrashAsync`：`Directory.Move(source, Path.Combine(InstancesTrashDirectory, $"{instanceId}-{timestamp:yyyyMMddHHmmss}"))`；跨卷失败（`IOException`）时退化为「复制后删除」，并写 `trash.json` 记录 `InstanceTrashEntry`；成功后从 `_instances` 移除。
     - `DeleteAsync`（192 行）保留为「彻底删除」，供回收站内二次确认使用。
  4. UI：实例删除按钮文案改为「移到回收站（可在设置中恢复）」，设置页加「回收站（N 项，占用 X）」列表 + 恢复/清空。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~InstanceTrash"
  ```
  用例：删除实例 → `instances/` 下目录消失、`instances-trash/` 下出现且含 `trash.json`、`Instances.Count` 减 1；`RestoreFromTrashAsync` 后实例回到 `Instances` 且 `Name`/`VersionName`/`Loader` 与删除前一致；`PurgeTrashAsync` 后回收站目录为空且 `ListTrashAsync` 返回 0；跨卷场景用不同根目录模拟（无法模拟时至少覆盖 `Directory.Move` 抛 `IOException` 的分支）。
- **风险**：回收站会占磁盘（一个实例几十 GB），必须提供「清空回收站」并显示占用总量，否则等于把删除问题转成了磁盘问题；`Directory.Move` 在同卷是原子的、跨卷会抛 `IOException`，退化路径必须保留原始异常信息；`trash.json` 与 `instance.json` 同时存在时以 `trash.json` 为准（用户可能手改过）；重命名只改显示名，`VersionName` 与目录 Id 不变，避免牵动 `InstanceContext` 的 Key 语义。

## 18. Java 探测结果缓存（P2）

- **来源**：`JavaManager.java:Searcher`（503-886 行）整套缓存：`loadCache`（521-570 行）/ `saveCache`（572-612 行）读写 `javaCache.json`，`createCacheKey`（614-669 行）用 `bin/java` 的 `size + lastModified`、`release` 文件的 sha1、`lib/rt.jar` 的 `size + lastModified` 组合成 key，文件变化才重新探测；`CACHE_MAJOR_VERSION`/`CACHE_MINOR_VERSION`（515-516 行）给缓存格式带版本号。
- **问题**：`Java/JavaService.cs:41 DetectAsync` 只做「本次进程内」的 `_cache`（27 行），每次启动启动器都要对最多 64 个候选跑 `java -XshowSettings:properties -version`（`ProbeAsync`:93，10 秒超时、4 并发）。冷启动到「Java 设置页可用」要等几秒到十几秒。
- **做法**：`Java/JavaService.cs` 增 `_paths.CacheDirectory/java-cache.json`；key 同 HMCL（`javaw.exe` 的 size+mtime + 同目录 `release` 的 sha1）。`DetectAsync(forceRefresh: false)` 先读缓存并校验 key，命中则跳过子进程。`Models/Java.cs:4 JavaInfo` 无需改（缓存的是 `JavaInfo` 整体）。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~JavaCache"
  ```
  第一次 `DetectAsync` 后删掉假 java 可执行文件，第二次 `DetectAsync(forceRefresh: false)` 仍返回缓存条目、耗时 < 100 ms；`forceRefresh: true` 则不返回。
- **风险**：缓存文件可能被安全软件锁定导致写失败（必须吞异常）；`release` 文件不存在的老 JDK（Java 8 之前）没有 key，退回每次探测；缓存里存的是绝对路径，用户移动 JDK 后要能靠 key 失配自动剔除。

## 19. 实例运行目录清理（logs / crash-reports）（P2）

- **来源**：`HMCLGameRepository.java:clean(Path directory)`（310-315 行）删除 `crash-reports/`、`logs/` 与目录下所有 `*.log`；`clean(GameInstanceID)`（317-320 行）对基础目录与实例运行目录各清一次。`GameInstancePage.java:258 clearJunkFiles()` 是 UI 入口。
- **问题**：`mods/ContentService.DeleteAsync` 只能删单个内容项；长期使用后 `logs/` 与 `crash-reports/` 会累积到几百 MB，用户只能手动去文件夹删。
- **做法**：`Abstractions/InstanceServices.cs` 增 `Task<long> CleanAsync(string instanceId, bool logs = true, bool crashReports = true, CancellationToken ct = default);`（返回释放字节数）；`Instances/InstanceService.cs` 实现，删除前逐个统计大小用于「已释放 X」提示；UI 放实例右键菜单。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~InstanceClean"
  ```
  造 `logs/a.log`、`crash-reports/b.txt`、`mods/c.jar` → 清理后前两者消失、`mods/c.jar` 保留、返回值等于两者字节和。
- **风险**：`latest.log` 常被游戏占用，删除会抛 `IOException`（跳过并继续，不要整体失败）；不要顺手删 `screenshots/`（用户资产）；清理不可逆，UI 需二次确认。

## 20. 启动脚本导出（P2）

- **来源**：`LauncherHelper.java:makeLaunchScript(Path scriptFile)`（145 行）：HMCL 能把当前实例的完整启动命令落成脚本文件。`GameInstancePage.java:279 generateLaunchScript()` 是 UI 入口（右键菜单 `SVG.SCRIPT`，385 行）。
- **问题**：`IGameLauncher.BuildCommandLineAsync`（`Abstractions/GameServices.cs:7`）已经能产出完整命令行字符串，但只能显示、不能保存。用户想用别的工具（如 IDE 调试、Profiler）拉起同一个实例时得手抄。
- **做法**：`Abstractions/GameServices.cs:4 IGameLauncher` 增 `Task<bool> ExportLaunchScriptAsync(LaunchOptions options, string targetPath, CancellationToken ct = default);`；`Launching/GameLauncher.cs` 用 `BuildCommandLineAsync` 的结果生成 `.bat`（`@echo off` + `chcp 65001` + 换行分段）或 `.sh`（`#!/bin/sh` + `"$@"` 风格），命令里含空格的参数由 `Compose`（560 行）已处理引号。UI 入口放在实例/版本右键菜单。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~LaunchScript"
  ```
  `ExportLaunchScriptAsync` 后文件存在且包含 `BuildCommandLineAsync` 的返回值（逐 token 子串断言）；Windows 上以 `.bat` 结尾、Linux 上 `.sh` 且首行是 shebang。
- **风险**：脚本会写入账户 `AccessToken`（`GameLauncher.cs:337` 的 `MSession`），这是敏感信息——导出前必须提示，并提供「导出离线模式脚本」选项（用空 token 占位）；脚本里的 Java 路径与游戏目录是绝对路径，换机器不可用；把脚本落在实例目录会在删除实例时一起丢掉，默认落在用户选择的路径。

## 21. 只读游戏目录覆盖（`*.bak`）（P2）

- **来源**：`GameDirectoryManager.java:forceOverwriteGameDirectoryFiles`（309-316、357-364 行）+ `forceOverwriteGameDirectoriesIfReadOnly`（437-447 行）：当游戏目录配置文件被标记只读时，备份后覆盖而不是直接失败。备份文件名的具体规则在 `SettingsManager.forceOverwrite*` 里，未落盘，**具体后缀未确认**。
- **问题**：`Paths/PathProbe.TestWritable`（23 行）遇到只读目录直接返回中文原因，`Installing/InstallService.cs:121 EnsureInstallEnvironment` 据此拒绝安装。用户从别处拷来的 `.minecraft` 目录常带只读属性，此时只能手动改属性。
- **做法**：`Paths/PathProbe.cs` 增 `public static string? TryMakeWritable(string directory);`（清 `FileAttributes.ReadOnly`，失败返回原因）；`InstallService.EnsureInstallEnvironment`（121 行）在探测失败且原因是只读时，调用它重试一次，成功后继续并在日志里记「已清除只读属性」。对单个文件（`versions/<id>/<id>.json`、`options.txt`）覆盖前先复制成 `*.bak`（保留一份，不累积）。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~Writable"
  ```
  临时目录设 `ReadOnly` 后 `TryMakeWritable` 返回 null 且目录可写；对文件覆盖函数断言生成 `xxx.bak` 且内容为旧值。
- **风险**：清只读属性在受控目录（组策略、OneDrive 占位文件）上无效，仍需回落报错；`*.bak` 会污染 `versions/` 目录（游戏扫版本目录时可能报错），建议放 `<launcher>/backups/readonly/` 下而不是原地；不要在 Linux/macOS 上照抄 Windows 的 `ReadOnly` 语义（Unix 上只读位是权限，`FileAttributes.ReadOnly` 表现不同），该功能只在 Windows 分支启用。

## 22. 实例多根目录 + Modrinth 镜像兜底（P2）

- **来源（多根目录）**：`GameDirectoryManager.java:rebuildGameDirectories`（250-265 行）把 local 与 user 两个 store 合并成可见列表，同 ID 时 local 优先；`GameDirectory.java:GameDirectory`（40-210 行）是「id + path + 自定义名」的稳定实体；`GameDirectoryManager.java:init`（155-218 行）恢复上次选中的目录。
- **来源（镜像兜底）**：`HMCLDownloadProvider.java:rules`（123-150 行）最后四条带 `fallback=true`（146-149 行）：`api.modrinth.com → mod.mcimirror.top/modrinth`、`cdn.modrinth.com → mod.mcimirror.top`、`api.curseforge.com → mod.mcimirror.top/curseforge`、`edge.forgecdn.net → mod.mcimirror.top`；`getCandidates`（62-116 行）里 `fallback` 为真时镜像只排在官方之后（官方失败才用）。其中 CurseForge 两条已由本轮④的 `AppSettings.CurseForgeBaseUrl`（`Models/Settings.cs:75`）覆盖，本条只做 Modrinth 两条。
- **问题**：`Instances/InstanceService.cs` 只扫 `<launcher>/instances`（`LauncherPaths.InstancesDirectory`），装在别的盘的用户无法把实例列出来；`Mods/ModrinthService.cs:16-18` 的注释明确「Modrinth 的 API 与 CDN 没有国内镜像」，且它的 `HttpClient` 是自建的（不挂 `MirrorRewritingHandler`），所以既没有镜像候选、也没有「失败换下一个主机」的能力。
- **做法（多根目录）**：`Models/Settings.cs` 增 `public List<string> ExtraInstanceRoots { get; set; } = new();`；`Instances/InstanceService.LoadAsync`（46 行）改为遍历 `[InstancesDirectory, ..ExtraInstanceRoots]` 下的一级子目录，撞 `Id` 时后者加 `-2` 后缀；`GetGameDirectory`（226 行）需反查实例属于哪个根。
- **做法（镜像兜底）**：`Downloads/DownloadInfrastructure.cs` 增一组 `FallbackOnly` 规则并让 `Candidates(url, source)` 支持「官方在前、镜像在后」的第三态；`Mods/ModrinthService.cs` 的 `_http` 改为可选注入该 handler，仅在首次请求抛 `HttpRequestException`/超时时重试镜像。镜像在设置里可关闭（默认关，避免默认把查询发到第三方）。
- **判据**：
  ```powershell
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~InstanceRoots"
  dotnet test tests\NebulaLauncher.Tests\NebulaLauncher.Tests.csproj --filter "FullyQualifiedName~MirrorFallback"
  ```
  多根目录：两个根下各放一个 `instance.json` → `LoadAsync` 后 `Instances.Count == 2`，撞 ID 时第二个带 `-2`。镜像兜底：假 handler 对官方 URL 返 502、对镜像 URL 返 200 → `Candidates` 顺序断言与最终成功断言。
- **风险**：多根目录会让 `DeleteAsync`（`InstanceService.cs:192`）的路径推导出错（现在假设在 `InstancesDirectory` 下），必须改成从实例的根推导，否则可能删错目录；跨盘实例无法用 `File.Move` 做原子提交，删除只能直接 `Directory.Delete`；镜像兜底会让 Modrinth 请求在官方超时后多等一个超时周期（15 秒 × 2），必须复用同一个 `CancellationTokenSource` 而不是串行等待，否则「检查更新」会变得极慢；`mod.mcimirror.top` 是第三方服务，可用性不由本项目控制，且默认关闭。

---

## 附：读过的 HMCL 文件与对应条目

| HMCL 文件 | 支撑条目 |
|---|---|
| `DefaultDependencyManager.java` | 6 |
| `GameLibrariesTask.java` / `GameAssetDownloadTask.java` / `GameAssetIndexDownloadTask.java` | 6 |
| `GameVerificationFixTask.java` | 6 |
| `JavaVersionConstraint.java` / `GameJavaVersion.java` / `JavaManager.java` | 1, 2, 4, 7, 18 |
| `MojangJavaDownloadTask.java` | 7 |
| `DefaultGameRepository.java` | 17（`renameInstance` / `moveInstanceFiles` / `removeInstanceFromDisk`） |
| `HMCLGameRepository.java` | 1, 10, 11, 13, 16, 17, 19 |
| `GameComponentAnalyzer.java` | 2, 17 |
| `CrashReportAnalyzer.java` | 3, 5, 9, 13 |
| `GameInstanceIconType.java` / `GameSettings.java` | 12, 13, 15 |
| `GameInstancePage.java` | 6, 10, 11, 16, 19, 20 |
| `LauncherHelper.java` | 1, 2, 15, 20 |
| `GameDirectoryManager.java` / `GameDirectory.java` | 21, 22 |
| `HMCLDownloadProvider.java` | 22 |

## 附：未确认项（不写入上表结论）

1. HMCL 的整合包导出实现类 `Instances`、黑名单 `ModAdviser.MODPACK_BLACK_LIST`、`Modpack.acceptFile` 均未落盘，条目 10 只借了调用点与菜单文案，具体打包规则未确认。
2. HMCL 的模组更新检查（`ModListPage` / 更新任务）未落盘，条目 9 的方案来自 NebulaLauncher 自身缺口与 Modrinth API 能力，不是 HMCL 移植。
3. HMCL 的世界备份 / 自动备份策略（`WorldListPage`）未落盘，条目 14 的「保留 N 份」是自拟策略，不是 HMCL 行为。
4. `managedPack` 字段的确切 JSON 形状未确认（`Modpack` / `HMCLGameInstance` 未落盘），条目 16 按 `research-launchers.md:§6.10` 的 schema 设计。
5. 只读目录覆盖的备份文件名规则在 `SettingsManager.forceOverwrite*`（未落盘），条目 21 只写「生成 `*.bak`」的等价语义。
6. 服务器列表 `servers.dat` 导入导出：HMCL 对应实现未落盘；NebulaLauncher 侧 `Backups/NbtReader.cs` 只有只读解析（`internal`），要做导出需先写 NBT writer。价值中、成本 M，未列入正式条目，待本轮④完成后评估是否值得做。
