# 文档可信度审计（doc-trust-audit）

> 历史说明：本文出现的 docs/ 下四份旧文档名（FINAL-SCOPE、EXTENSIONS、HANDOVER、PUBLISH-CHECK），都是 2026-10-24 收口删除前的文件名，只作历史核对对象的记录，正文路径不改。

数法：只读静态阅读，未运行 `dotnet build` / `dotnet test`（本机沙箱不具备执行条件）。
数法：行号一律取当前工作树（采样 2026-10-24），文档行号按 `docs/` 现有文件计。
数法：判定只认 `src/` 下 `.cs` 源码，不认 `.probe/` 产物、轮次交接与截图证据。
数法：测试数用 node fs 递归读 tests/NebulaLauncher.Tests/*.cs 后正则计数，排除 `obj/` 生成的 3 个文件。
数法：判定口径只有四档——代码存在 / 代码缺失 / 有实现但不完整 / 文档与代码不符。

## §1 非目标 12 条核对（docs/FINAL-SCOPE.md:31-46）

| # | 声称不做 | 文档出处 | 代码实况 | 判定 |
|---|---|---|---|---|
| 1 | macOS 平台支持 | docs/FINAL-SCOPE.md:35 | `src/NebulaLauncher.App/Program.cs:430` `if (OperatingSystem.IsMacOS())`；`src/NebulaLauncher.Core/Paths/LauncherPaths.cs:267` `IsMacOs`、`:285` OSX 分支；`src/NebulaLauncher.Core/Accounts/AccountService.cs:990` 注释「macOS / Linux 上令牌只做了编码」 | 有实现但不完整：单 TFM 运行时分支已在，无验收、无发布物 |
| 2 | 皮肤/披风管理、上传、缓存 | docs/FINAL-SCOPE.md:36 | 全仓 `src` 无 `ProfileAction` / `SetSkin` / `SkinService` / `CapeService`；仅 `src/NebulaLauncher.Core/Models/Accounts.cs:40-41` `SkinModel` 字段（注释「仅影响本地预览」） | 代码缺失（与文档一致） |
| 3 | Nide 专用登录流程 | docs/FINAL-SCOPE.md:37 | `src` 无 nide 命中 | 代码缺失（一致） |
| 4 | EasyTier/P2P 联机 | docs/FINAL-SCOPE.md:38 | `src` 无 easytier 命中 | 代码缺失（一致） |
| 5 | OptiFine / LiteLoader 自动安装链 | docs/FINAL-SCOPE.md:39 | 无安装链；只有 `src/NebulaLauncher.Core/Launching/CrashRules.cs:396` 起的 OptiFine 崩溃规则、`src/NebulaLauncher.Core/Mods/ModMetadataReader.cs:489` `litemod.json` 读取、`src/NebulaLauncher.Core/Models/Instances.cs:262` `InstanceIconKind.OptiFine` 图标 | 代码缺失（一致）：有识别、无安装链 |
| 6 | 组件修改器完整闭环（快照+对比+逐项恢复） | docs/FINAL-SCOPE.md:40 | `src/NebulaLauncher.Core/Installing/ComponentInspector.cs:16` 只读检查器（注释「不下载、不写清单、不改任何文件」）；`src/NebulaLauncher.Core/Versions/LoaderVersionCatalog.cs:10` 注释用「候选里包含已安装版本」替代还原 | 有实现但不完整：检查有，快照/对比/逐项恢复无 |
| 7 | 导出 zip/启动器包格式、逐文件勾选 | docs/FINAL-SCOPE.md:41 | 只有 `.mrpack`：`src/NebulaLauncher.Core/Backups/ModpackExporter.cs:11` 注释、`:27` `modrinth.index.json`、`:30` `overrides/`；无普通 zip 导出、无逐文件勾选 | 代码缺失（一致） |
| 8 | 背景图片/音乐、完整外观个性化 | docs/FINAL-SCOPE.md:42 | 背景图片**已实现**：`src/NebulaLauncher.App/Views/SettingsView.axaml.cs:24` `OnPickBackgroundImageClick`、`:42` 对话框标题「选择启动器背景图片」；`src/NebulaLauncher.Core/Transfer/ProfileBundleService.cs:64` `backgroundImagePath` 与遮罩强度（`:63`）。音乐无（`src` 无 MediaPlayer / NAudio / WaveOut / 播放器命中） | 文档与代码不符：图片已做、音乐未做；并与本文件 `:46` 直接矛盾 |
| 9 | 分片下载阶梯并发 | docs/FINAL-SCOPE.md:43 | `src/NebulaLauncher.Core/Downloads/DownloadInfrastructure.cs:766` 只有 `request.Headers.Range is not null`（`:765` 注释「Range 请求一定是断点续传的资源文件」）；并发由 `src/NebulaLauncher.Core/Downloads/DownloadQueue.cs:116` `SemaphoreSlim` 限流队列承担，不是分片 | 代码缺失（一致）：只有断点续传 |
| 10 | 自定义主页声明式事件体系 | docs/FINAL-SCOPE.md:44 | `src` 无 `ModEvent` / `EventBus` 命中 | 代码缺失（一致） |
| 11 | 内置中文搜索词表、本地模组完整更新日志入口 | docs/FINAL-SCOPE.md:45 | **两半都已落地**：`src/NebulaLauncher.Core/Mods/ModNameTable.cs:8-12` 中文译名表、`:204` 嵌入资源 `NebulaLauncher.Core.Resources.WikiEntries.txt`、`:198` 注释「978 KB / 2.6 万条」；`src/NebulaLauncher.Core/NebulaLauncher.Core.csproj:22` `<EmbeddedResource Include="Resources\WikiEntries.txt" />`。更新日志：`src/NebulaLauncher.Core/Mods/ModUpdateService.cs:914` `Changelog =`、`src/NebulaLauncher.App/ViewModels/ModsViewModel.cs:2635-2637` 面板展示 | 文档与代码不符：已实现 |
| 12 | 背景音乐与其余外观个性化（**背景图片已实现**，见 §1） | docs/FINAL-SCOPE.md:46 | 音乐无（同 #8 证据）；图片有 | 与代码一致，但与 #8 自相矛盾 |

小结：12 条里 8 条与代码一致（#2 #3 #4 #5 #7 #9 #10 #12，其中 #9 只差措辞、#12 与 #8 自相矛盾），4 条与代码不符（#1 有 macOS 分支、#6 有检查组件、#8 背景图片已实现、#11 两个半条落地）。§2 不能作为「目标已收口」的依据。

## §2 已完成 13 条核对（docs/FINAL-SCOPE.md:11-27）

| # | 声称已完成 | 代码出处 | 判定 |
|---|---|---|---|
| 1 | 11 个页面（首页/版本/实例/下载/账户/服务器/存档/模组/日志/设置/关于） | 导航注册 `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs:733-744`，实际 11 项：首页/版本/下载/模组/**资源**/账户/服务器/**世界** + 日志/**帮助**/设置。「实例」「存档」「关于」三个名字在导航里不存在（`关于` 是设置页分区：`src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs:92`） | 有实现但不完整：数量对、名字错 3 个 |
| 2 | 原版 / Fabric / Forge 安装与启动 | `src/NebulaLauncher.Core/Installing/InstallService.cs:22`、`src/NebulaLauncher.Core/Versions/LoaderVersionCatalog.cs:10`、`src/NebulaLauncher.Core/Launching/MinecraftLauncherFactory.cs:27`、`src/NebulaLauncher.Core/Installing/NeoForgeInstallerBridge.cs:204` | 代码存在 |
| 3 | 启动到主菜单、`WM_CLOSE` 正常退出、取消等待仍持锁、二次启动 | 主菜单判定靠日志里程碑：`src/NebulaLauncher.Core/Launching/GameWatcher.cs:28-44`（注释「只看进程是否存活判断不了进没进主菜单」）；取消等待移交后台：`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:1016-1020`；实例锁：`src/NebulaLauncher.Core/Instances/InstanceLockService.cs:9`。**全仓 `src` 无 `WM_CLOSE` 命中** | 有实现但不完整：能力在，`WM_CLOSE` 措辞与实现不符 |
| 4 | 存档自动备份（zip + meta）与多实例并行 | `src/NebulaLauncher.Core/Backups/BackupService.cs:129` 文件名、`:149` `ZipArchive`、`:317`/`:588` `.meta.json`、`:230` 枚举 `*.zip` | 代码存在 |
| 5 | 下载链路：换源、首块探测、body 空闲超时与降级上报 | `src/NebulaLauncher.Core/Downloads/DownloadSourceGovernor.cs:31`、`src/NebulaLauncher.Core/Downloads/DownloadSourceProbe.cs:209`、`src/NebulaLauncher.Core/Downloads/DownloadInfrastructure.cs:344` `BodyIdleTimeout = 60s`、`:632-633` `IdleTimeoutStream` | 代码存在 |
| 6 | 实例锁（PID + 进程启动时间，跨启动器按游戏判活） | `src/NebulaLauncher.Core/Instances/InstanceLockService.cs:17`「判活必须看启动时间」、`:34` 容差、`:245` 缺字段按僵死、`:293` 写自身启动时间；测试 `tests/NebulaLauncher.Tests/CoreInstances_LockTests.cs:19` | 代码存在 |
| 7 | 账户：离线 / 微软设备码 / 第三方 Yggdrasil；令牌加密与续期 | `src/NebulaLauncher.Core/Accounts/AccountService.cs:211` `AddOfflineAccountAsync`、`src/NebulaLauncher.Core/Accounts/MicrosoftDeviceCodeAuth.cs:45`、`src/NebulaLauncher.Core/Accounts/AuthlibAuthService.cs:41`、`src/NebulaLauncher.Core/Accounts/TokenProtector.cs:67` | 代码存在（`:990` 自述 macOS/Linux 上令牌等于明文） |
| 8 | 整合包导入导出（六种格式；LauncherPack 明确拒绝） | `src/NebulaLauncher.Core/Backups/ModpackService.cs:12-14` 六格式注释、`:40-42` 嗅探顺序、`:110-113`/`:201-206` `ModpackFormat.LauncherPack` 分支；导出 `src/NebulaLauncher.Core/Backups/ModpackExporter.cs:24` | 代码存在 |
| 9 | 服务器列表、favicon、`servers.dat` 读写与导入 | `src/NebulaLauncher.Core/Servers/ServerService.cs:218-240` 导入 `servers.dat`、`:612` `FaviconDataUri`；`src/NebulaLauncher.Core/Servers/ServerListDatReader.cs`；`src/NebulaLauncher.App/ViewModels/ServersViewModel.cs:823-832` 解码 | 代码存在 |
| 10 | 崩溃分析、hs_err 归档、崩溃面板复制/导出 | `src/NebulaLauncher.Core/Launching/CrashAnalyzer.cs:30`/`:192` `ExtractCrashReportPath`、`src/NebulaLauncher.Core/Launching/CrashExportService.cs:22`、`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:1674` 导入/`:1741` 导出、`src/NebulaLauncher.Core/Help/HelpCatalog.cs:348-350` 归档与导出说明 | 代码存在 |
| 11 | 代码质量：6 P2 + 14 P3 全部修复 | 判据是评审清单（`REVIEW-R34-R36-CODE.md`）而非代码，静态阅读无法复核 | 不可判定 |
| 12 | 构建 / 测试 / 发布，**2235/2235** | 静态计数 `[Fact]` 1605 + `[InlineData]` 757 = **2362** 例（详见 §5）；仓内并存 2238 / 2244 / 2358 | 文档与代码不符：数字对不上 |
| 13 | 启动器背景图片（可选 + 遮罩强度） | `src/NebulaLauncher.App/Views/SettingsView.axaml.cs:21-24`、`src/NebulaLauncher.Core/Transfer/ProfileBundleService.cs:62-64` | 代码存在 |

## §3 命名与调用不符清单

| 文档说 | 代码实际 | 出处 |
|---|---|---|
| 模组更新检查用 `mods.lock.json` + sha1 反查，新增 `Mods/ModLockService.cs` + `Mods/ModVersionResolver.cs` | 无此两文件、全仓 `src` 无 `mods.lock` 命中；实际指纹缓存是 `mod-hashes.json`（`src/NebulaLauncher.Core/Mods/ModFingerprint.cs:26` `CacheFileName`），更新检查在 `src/NebulaLauncher.Core/Mods/ModUpdateService.cs:23`/`:104`，运行锁是 `.nebula-run.json`（`src/NebulaLauncher.Core/Instances/InstanceLockService.cs:9`） | docs/EXTENSIONS.md:710 |
| JVM 预设新增 `Launching/JvmArguments.cs`，提供 `Compose` / `Split` | 无 `JvmArguments.cs`；实际 `src/NebulaLauncher.Core/Launching/JvmArgumentBuilder.cs:39`、`src/NebulaLauncher.Core/Launching/JvmPresetResolver.cs:11`、`src/NebulaLauncher.Core/Launching/LaunchArgumentSplitter.cs` | docs/EXTENSIONS.md:866 |
| 实例图标落在 `App/Assets/InstanceIcons/` 资源目录 | 该目录不存在；`src/NebulaLauncher.App/Assets/` 只有 `tray.png`；图标是 `Geometry` 路径内联（`src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:3852` `InstanceIcons.For(Info.IconKind)`、`:2684` `IconChoices`），服务端写入 `src/NebulaLauncher.Core/Instances/InstanceService.Admin.cs:285` | docs/EXTENSIONS.md:792 |
| 启动到主菜单用 `WM_CLOSE` 判定 | 全仓 `src` 无 `WM_CLOSE`；进主菜单靠 stdout 里程碑（`src/NebulaLauncher.Core/Launching/GameWatcher.cs:37` `Consume`），窗口侧只有 `EnumWindows`（`src/NebulaLauncher.Core/Platforms/PlatformNative.cs:157-161`）与 `TrySetWindowTitle`（`src/NebulaLauncher.Core/Platforms/PlatformService.cs:427`） | docs/FINAL-SCOPE.md:17、`:62` |
| 页面为「实例 / 存档 / 关于」 | 导航实际是「资源 / 世界 / 帮助」，`关于` 是设置页分区而非独立页 | docs/FINAL-SCOPE.md vs（:15） `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs:735-744`、`src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs:92` |
| `Launching/PreflightChecker.cs` 里 `CheckAsync(LaunchOptions, ILauncherPaths, IJavaService, IContentService, IPlatformService, CancellationToken)` 是实例方法 | `PreflightChecker` 是 static 工具类（`src/NebulaLauncher.Core/Launching/PreflightChecker.cs:30`），服务形态在 `:651` `internal sealed class PreflightCheckerService : IPreflightChecker`，接口在 `src/NebulaLauncher.Core/Abstractions/GameServices.cs:142`/`:156`；`CheckAsync` 同名实例方法实际属 `ModUpdateService`（`src/NebulaLauncher.Core/Mods/ModUpdateService.cs:104`） | docs/EXTENSIONS.md:472 |
| 实例清理方法叫 `CleanAsync` | 实际 `CleanupAsync`（`src/NebulaLauncher.Core/Instances/InstanceService.Admin.cs:333`，接口 `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:69`） | docs/EXTENSIONS.md:980 |
| 自动内存分配抄 HMCL 公式（预留 512MiB、8GiB 阈值） | 实际抄 PCL2 四段斜坡：`src/NebulaLauncher.Core/Launching/MemoryAdvisor.cs:4`「抄 PCL2 `PageInstanceSetup.GetRam` 的四段斜坡」、`:27` `MinSaneMaxMemoryMb = 512`、`:24` `FallbackMaxMemoryMb = 4096` | docs/EXTENSIONS.md:814 |
| `JavaRequirementTable.cs:137` `AllowedRange` / `:183` `Evaluate`；`JavaService.cs:199` `GetAllowedRange` | 当前行号：`AllowedRange` 在 `src/NebulaLauncher.Core/Java/JavaRequirementTable.cs:341`/`:403`，`Evaluate` 在 `:434`/`:503`，`All` 在 `:58`；`GetAllowedRange` 在 `src/NebulaLauncher.Core/Abstractions/GameServices.cs:182`/`:197`。同类行号漂移还有 §4 的 `JavaService.cs:523`（实际 `ExpandOfficialRuntimeLayout` 定义在 `src/NebulaLauncher.Core/Java/JavaService.cs:1127`） | docs/EXTENSIONS.md:513、`:576` |

## §4 功能块 22 条核对（docs/EXTENSIONS.md:472-1032）

| 功能块 | 文档出处 | 实现位置 | 判定 |
|---|---|---|---|
| §1 启动前置检查 | docs/EXTENSIONS.md:472 | `src/NebulaLauncher.Core/Launching/PreflightChecker.cs:30`、`:651` `PreflightCheckerService`、`src/NebulaLauncher.Core/Abstractions/GameServices.cs:142` | 有实现（形态与文档不符） |
| §2 Java 约束上下界 | docs/EXTENSIONS.md:513 | `src/NebulaLauncher.Core/Java/JavaRequirementTable.cs:24`/`:58`/`:341`/`:434`/`:177` `GetLoaderLimit` | 有实现（行号错） |
| §3 本地模组依赖检查 | docs/EXTENSIONS.md:546 | `src/NebulaLauncher.Core/Abstractions/ContentServices.cs:133` `DetectDependencyIssues`、`src/NebulaLauncher.Core/Launching/PreflightChecker.cs:542` 调用、`src/NebulaLauncher.App/ViewModels/ModsViewModel.cs:1010` | 有实现 |
| §4 Java 扫描扩展（Mojang runtime / 其他启动器 / 自定义路径） | docs/EXTENSIONS.md:576 | `src/NebulaLauncher.Core/Java/JavaService.cs:1033` `NEBULA_JAVA_PATHS`、`:1068`/`:1127` `ExpandOfficialRuntimeLayout`、`:107` `CollectCandidates`、`src/NebulaLauncher.Core/Java/JavaPathList.cs:96` | 有实现（行号错） |
| §5 崩溃报告深度解析 | docs/EXTENSIONS.md:599 | `src/NebulaLauncher.Core/Launching/CrashAnalyzer.cs:30`/`:192`、`src/NebulaLauncher.Core/Launching/CrashRules.cs:396`、`src/NebulaLauncher.Core/Models/Launching.cs:362` hs_err 关键行、`src/NebulaLauncher.Core/Launching/GameLauncher.cs:1737` `CrashReportPath` | 有实现（行号错） |
| §6 实例级校验与修复两档 | docs/EXTENSIONS.md:625 | `src/NebulaLauncher.Core/Installing/InstallService.Repair.cs:17`、`src/NebulaLauncher.Core/Abstractions/VersionServices.cs:108-112`、`src/NebulaLauncher.Core/Installing/ComponentInspector.cs:16` | 有实现 |
| §7 Java 下载用 Mojang manifest | docs/EXTENSIONS.md:654 | `src/NebulaLauncher.Core/Java/MojangRuntimeCatalog.cs:34`、`src/NebulaLauncher.Core/Java/JavaService.cs:745`/`:775` `TryInstallMojangRuntimeAsync` | 有实现 |
| §8 Modrinth 依赖解析 | docs/EXTENSIONS.md:680 | `src/NebulaLauncher.Core/Mods/ModrinthInstallPlanner.cs:20`、`src/NebulaLauncher.Core/Mods/ModrinthService.cs:720` `PlanInstallAsync`、`src/NebulaLauncher.Core/Abstractions/ContentServices.cs:399` | 有实现 |
| §9 模组更新检查（mods.lock.json + sha1 反查） | docs/EXTENSIONS.md:710 | 文档所述方案不存在；实际 `src/NebulaLauncher.Core/Mods/ModUpdateService.cs:23`/`:104` + `src/NebulaLauncher.Core/Mods/ModFingerprint.cs:23`/`:26` `mod-hashes.json` | 文档与代码不符：功能在，方案名与文件全错 |
| §10 整合包导出 `.mrpack` | docs/EXTENSIONS.md:741 | `src/NebulaLauncher.Core/Backups/ModpackExporter.cs:11`/`:24`/`:27`、`src/NebulaLauncher.Core/Backups/ModpackService.cs:359` | 有实现 |
| §11 实例克隆 | docs/EXTENSIONS.md:767 | `src/NebulaLauncher.Core/Instances/InstanceService.Admin.cs:21`、`src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:31` | 有实现 |
| §12 实例图标 | docs/EXTENSIONS.md:792 | `src/NebulaLauncher.Core/Instances/InstanceService.Admin.cs:285` `SetIconAsync`、`src/NebulaLauncher.Core/Models/Instances.cs:262` `InstanceIconKind`、`src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:2684`/`:3852` | 有实现（资源目录不存在） |
| §13 自动内存分配 | docs/EXTENSIONS.md:814 | `src/NebulaLauncher.Core/Launching/MemoryAdvisor.cs:21`/`:24`/`:27`/`:49` | 有实现（公式来源错，见 §3） |
| §14 世界备份轮转 + 退出后自动备份 | docs/EXTENSIONS.md:842 | `src/NebulaLauncher.Core/Backups/BackupService.cs:332` `PruneAsync`、`src/NebulaLauncher.Core/Launching/AutomaticWorldBackup.cs:44` | 有实现 |
| §15 JVM 参数预设 | docs/EXTENSIONS.md:866 | `src/NebulaLauncher.Core/Models/Settings.cs:335-359` `JvmPresets`/`ActiveJvmPresetId`、`src/NebulaLauncher.Core/Launching/JvmArgumentBuilder.cs:39`、`src/NebulaLauncher.Core/Launching/JvmPresetResolver.cs:11` | 有实现（文件名错） |
| §16 实例 `managedPack` 元数据 | docs/EXTENSIONS.md:903 | `src/NebulaLauncher.Core/Backups/ModpackService.cs:359` 写入、`:1854-1855` 格式映射注释 | 有实现 |
| §17 实例重命名 + 删除到回收站 | docs/EXTENSIONS.md:935 | `src/NebulaLauncher.Core/Instances/InstanceService.Admin.cs:245` `RenameAsync`、`src/NebulaLauncher.Core/Instances/InstanceService.Trash.cs`、`src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:53`/`:57` | 有实现 |
| §18 Java 探测结果缓存 | docs/EXTENSIONS.md:968 | `src/NebulaLauncher.Core/Java/JavaCache.cs:7-8`、`src/NebulaLauncher.Core/Maintenance/StorageService.cs:56` `java-cache.json` | 有实现 |
| §19 实例运行目录清理 | docs/EXTENSIONS.md:980 | `src/NebulaLauncher.Core/Instances/InstanceService.Admin.cs:333` `CleanupAsync`、`src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:69` | 有实现（命名不符） |
| §20 启动脚本导出 | docs/EXTENSIONS.md:992 | `src/NebulaLauncher.Core/Launching/LaunchScriptExporter.cs:18` | 有实现 |
| §21 只读游戏目录 `*.bak` 覆盖 | docs/EXTENSIONS.md:1004（§0.0.1 标「不采用」） | 无实现 | 代码缺失（与文档一致） |
| §22 实例多根目录 + Modrinth 镜像兜底 | docs/EXTENSIONS.md:1016 | 多根目录无（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs:137` 单根 `InstancesDirectory`）；镜像兜底**已实现**：`src/NebulaLauncher.Core/Mods/ModrinthService.cs:23-25` 注释「镜像兜底：默认关闭」、开关 `UseModrinthMirror`（`src/NebulaLauncher.Core/Settings/SettingsService.cs:369`、`src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs:1408`） | 有实现但不完整：一半做了、文档说不做 |

## §5 测试数与版本号一致性

数法：`tests/NebulaLauncher.Tests` 下 158 个 `.cs`，排除 `obj/` 生成 3 个 → 155 个源文件（与 docs/HANDOVER.md:40「测试 155 个文件」一致）。正则计 `[Fact]` / `[Theory]` / `[InlineData]`；`[MemberData]` 0、`[ClassData]` 0、`Skip=` 0、无「`[Theory]` 缺 `[InlineData]`」的方法。

| 项 | 实际值 |
|---|---|
| 测试源文件 | 155（docs/HANDOVER.md 写（:40） 155，正确） |
| `[Fact]` | 1605 |
| `[Theory]` | 152 |
| `[InlineData]` | 757 |
| xUnit 用例数 = `[Fact]` + `[InlineData]` | **2362** |
| 含 `[Theory]` 自身计 | 2514 |

| 文档数字 | 出处 | 与 2362 的差 | 判定 |
|---|---|---|---|
| 1502 / 1502 | `README.md:116`（`# 总计: 1502, 通过: 1502, 失败: 0`） | -860 | 严重过时，不可用 |
| 2235 / 2235 | docs/FINAL-SCOPE.md:26 | -127 | 历史快照，覆盖不到当前树 |
| 2238 / 2238 | docs/FINAL-SCOPE.md:60（R54） | -124 | 同上 |
| 2244 / 2244 | docs/FINAL-SCOPE.md:65（R55） | -118 | 同上 |
| 2358 / 2358 | docs/HANDOVER.md:20、docs/HANDOVER-2026-10-24-R59.md:100 | -4 | 最接近，仍差 4 |

版本号：`src/NebulaLauncher.App/NebulaLauncher.App.csproj` 与 `src/NebulaLauncher.Core/NebulaLauncher.Core.csproj` 均无 `<Version>` 字段，`Directory.Build.props` 也无版本号；文档中的 R49/R54/R55/R59 是轮次编号，不是产品版本。产品版本号不可核对（未确定）。

## §6 结论：哪几类文档结论可判死

1. **docs/FINAL-SCOPE.md §2「明确不做」整表可判死**：12 条里 4 条与代码不符（#1 有 macOS 分支、#6 有检查组件、#8 背景图片已实现、#11 中文词表与更新日志都已落地），且 #8 与 #46 自相矛盾。该表不能作为「目标已收口」的依据。
2. **docs/FINAL-SCOPE.md §1/§4 的全部数字与轮次证据可判死**：:26/:60/:65 三个测试数互斥（2235/2238/2244），实际 2362；:20 的 `CoreInstances_LockTests` 与 :17 的 `WM_CLOSE` 都不成立。凡以 `.probe/`、R31–R55 交接为唯一证据的条目，一律不可复核。
3. **docs/EXTENSIONS.md 的全部「落点」可判死**：22 个功能块里至少 8 处文件名/方法名/行号错（`mods.lock.json`、`ModLockService`、`ModVersionResolver`、`JvmArguments.cs`、`Assets/InstanceIcons/`、`CleanAsync`、`JavaRequirementTable.cs:137/:183`、HMCL 内存公式）。功能本身大多存在，但文档给出的调用关系与被调用代码对不上，不能当 API 参考。
4. **`README.md` 的测试数与「完成」矩阵可判死**：:116 的 1502 差 860 例，:12/:15 的能力矩阵停在很早的轮次。
5. **不可信的写法本身也可判死**：把 docs/TAKEOVER.md、`AUDIT` 第五节、`.probe/` 截图当作证据链。这三类产物不在当前仓内或不可执行，任何以它们为唯一依据的结论都无复核路径。

可判死的三类：**非目标/范围声明**、**轮次交接里的验收数字**、**以 `.probe/` 或已删文档为唯一证据的条目**。可继续用的只有 docs/HANDOVER.md 的代码规模（155（:40） 文件）与本文按 `src/` 复核出的实现位置。
