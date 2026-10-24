# NebulaLauncher 代码地图（调研笔记）

- 依据：仅代码，行号取自当前工作树；路径相对仓库根。
- 范围：src/NebulaLauncher.Core（22 个目录、约 51.9k 行）、src/NebulaLauncher.App（ViewModels 22 文件 26.4k 行）、用户数据落盘点。
- 目的：判断「项目数据结构及调用」与「代码组成」能否各自收拢成一份文档。
- 未做：未运行 dotnet build / test（沙箱限制），所有结论来自静态阅读。

---

## 1. Core 分层与模块清单

| 目录 | 文件数 | 总行数 | 职责一句话 | 关键类型（文件:行） |
| --- | --- | --- | --- | --- |
| Abstractions | 17 | 2667 | 全部对外契约（接口 + 少量共享 DTO），Core 实现与 App 之间唯一耦合面 | ILauncherPaths Infrastructure.cs:4；IAccountService AccountServices.cs:28；IInstallService VersionServices.cs:51 |
| Models | 13 | 3675 | 跨层数据模型：设置、账户、内容/整合包、下载任务、实例、Java、启动、服务器 | AppSettings Settings.cs:43；InstanceInfo Instances.cs:11；InstallRequest Downloads.cs:261 |
| Paths | 3 | 810 | 数据根/游戏根目录解析、路径探测与越界防护 | LauncherPaths.cs:113；PathProbe.cs；PathSafety.cs |
| Logging | 6 | 926 | 按天滚动文件日志、脱敏、启动器/游戏日志读取与实时跟随 | FileLogger.cs:40；LauncherLogReader.cs；LogFollower.cs |
| Net | 4 | 475 | 连通性探测（DNS/连接/超时/TLS 分类）、代理地址解析与凭据存储 | NetworkProbe.cs:11；ProxyCredentialStore.cs；ProxyConfigurator.cs |
| Settings | 2 | 622 | settings.json 读写、默认值、Changed 事件 | SettingsService.cs:52 LoadAsync、:121 SaveAsync |
| Accounts | 6 | 2413 | 账户增删改查、微软设备码登录、Authlib 登录、令牌保护、离线 UUID | AccountService.cs:105/116/939；MicrosoftDeviceCodeAuth.cs；TokenProtector.cs |
| Versions | 7 | 3872 | 已安装/远程版本枚举、清单缓存、启动记录、服务端下载、收藏与隐藏 | VersionService.cs:84 GetInstalledAsync、:131 GetRemoteAsync |
| Launching | 17 | 8873 | 启动命令构建与执行、JVM 参数、预检、Authlib 注入、崩溃分析与导出、世界自动备份 | GameLauncher.cs:216 LaunchAsync；MinecraftLauncherFactory.cs:39 Create |
| Instances | 11 | 3452 | 实例目录/元数据、隔离策略、运行锁、回收站、克隆与切换加载器 | InstanceService.cs:208 instance.json；InstanceLockService.cs:24 |
| Platforms | 6 | 1451 | 平台能力（单实例锁、窗口材质、任务栏进度、GPU 偏好、剪贴板、打开目录） | PlatformService.cs；PlatformNative.cs |
| Downloads | 9 | 4582 | 统一下载队列、镜像重写、并发与限速、换源治理、文件复用 | DownloadQueue.cs；DownloadInfrastructure.cs:21/26/55 |
| Java | 7 | 3458 | Java 探测/版本要求表、Mojang 运行时清单、运行时下载解压、手动路径与排除列表 | JavaService.cs:46 ExcludedFileName、:1615；MojangRuntimeCatalog.cs:38 |
| Installing | 11 | 4334 | 安装编排（原版 / Fabric / Forge / NeoForge / Fabric API）、修复、组件检查 | InstallService.cs:122 InstallAsync、:494 VerifyAsync |
| Mods | 16 | 11258 | 内容扫描/启停/删除/导入、Modrinth 与 CurseForge 客户端、更新检查、指纹 | ModrinthService.cs:542 InstallAsync；CurseForgeService.cs:814 |
| Backups | 7 | 5021 | 世界备份/还原/裁剪、整合包识别与导入导出、NBT 读写、存档图标缓存 | BackupService.cs:131/176/587；ModpackService.cs:369/607 |
| Servers | 6 | 2014 | 服务器收藏（servers.json）、servers.dat 导入、状态查询与 MOTD 协议 | ServerService.cs:71/482；ServerListDatReader.cs:60 |
| Maintenance | 2 | 619 | 存储占用统计与安全清理（四类别 + 保护名单） | StorageService.cs:47-62 |
| Transfer | 1 | 925 | 配置包导出/导入（manifest.json + 分条目），不整目录搬运 | ProfileBundleService.cs；ProfileBundleFiles.cs:13 |
| Help | 1 | 510 | 内置离线帮助条目与分类 | HelpCatalog.cs:3/124 |
| Diagnostics | 1 | 157 | 共享诊断快照结构（设置页「复制诊断」与崩溃导出共用字段） | DiagnosticsSnapshot.cs:6 |

结论：目录划分与实际职责基本一一对应；Mods（11.3k）、Launching（8.9k）、Backups（5.0k）三处是体量与耦合的双高峰。

---

## 2. Abstractions 接口契约清单

成员数 = 方法 + 属性 + 事件粗算；「实现」列以 DI 注册行定位（组合根见 §6）。

| 接口 | 契约文件:行 | 成员数 | 实现 | 主要消费方 |
| --- | --- | --- | --- | --- |
| ILauncherPaths | Infrastructure.cs:4 | 14 | LauncherPaths.cs:377 AddLauncherPaths | 所有 VM 与多数 Core 服务 |
| IPlatformService | Infrastructure.cs:45 | 18 | PlatformServiceCollectionExtensions.cs:11 | 全部 11 个 VM |
| IAppLog | Infrastructure.cs:142 | 5 | LoggingServiceCollectionExtensions.cs:21 | 全 Core |
| ISettingsService | AccountServices.cs:4 | 5 | CoreSettingsServiceCollectionExtensions.cs:15 | HomeViewModel.cs:94、DownloadViewModel.cs:75、SettingsViewModel.cs:69 |
| IAccountService | AccountServices.cs:28 | 13 | AccountServiceCollectionExtensions.cs:14 | HomeViewModel.cs:92、AccountsViewModel.cs:33、ServersViewModel.cs:47 |
| IAuthlibInjectorService | AuthlibServices.cs:10 | 3 | LaunchingServiceCollectionExtensions.cs:22 | GameLauncher（内部） |
| IComponentInspector | ComponentServices.cs:12 | 1 | InstallServiceCollectionExtensions.cs:15 | VersionsViewModel.cs:77 |
| IContentService | ContentServices.cs:71 | 13 | ContentServiceCollectionExtensions.cs:11 | ModsViewModel.cs:101、WorldsViewModel.cs:51、HomeViewModel.cs:97 |
| IModpackService | ContentServices.cs:140 | 3 | BackupsServiceCollectionExtensions.cs:12 | LibraryViewModel.cs:146、WorldsViewModel.cs:50 |
| IBackupService | ContentServices.cs:189 | 8 | BackupsServiceCollectionExtensions.cs:11 | WorldsViewModel.cs:49、HomeViewModel.cs:98 |
| IWorldIconService | ContentServices.cs:250 | 1 | WorldIconService.cs:261 AddCoreWorldIcons | WorldsViewModel.cs:55 |
| IModrinthService | ContentServices.cs:267 | 12 | ContentServiceCollectionExtensions.cs:12 | LibraryViewModel.cs:134、DownloadViewModel.cs:82 |
| IModUpdateService | ContentServices.cs:442 | 4 | ModServiceCollectionExtensions.cs:24 | ModsViewModel.cs:105 |
| IServerService | ContentServices.cs:486 | 6 | ServerServiceCollectionExtensions.cs:11 | ServersViewModel.cs:45 |
| ICrashExportService | CrashExportServices.cs:65 | 2 | LoggingServiceCollectionExtensions.cs:25 | HomeViewModel.cs:104 |
| ICurseForgeService | CurseForgeServices.cs:17 | 12 | ContentServiceCollectionExtensions.cs:13 | LibraryViewModel.cs:135 |
| IDownloadSourceProvider | DownloadSourceServices.cs:12 | 4 | DownloadServiceCollectionExtensions.cs:26 | DownloadQueue、VersionService、DownloadViewModel.cs:79 |
| IDownloadSourceTelemetry | DownloadSourceServices.cs:38 | 1 | DownloadServiceCollectionExtensions.cs:27 | DownloadSourceGovernor |
| IDownloadSourceProbe | DownloadSourceServices.cs:165 | 1 | DownloadSourceProbe.cs:555 | DownloadViewModel.cs:83 |
| IVersionFavorites | FavoriteServices.cs:10 | 6 | VersionServiceCollectionExtensions.cs:23 | VersionsViewModel.cs:76 |
| IGameLauncher | GameServices.cs:4 | 8 | LaunchingServiceCollectionExtensions.cs:20 | HomeViewModel.cs:96、VersionsViewModel.cs:68、ServersViewModel.cs:48 |
| IPreflightChecker | GameServices.cs:142 | 1 | LaunchingServiceCollectionExtensions.cs:21 | GameLauncher（内部） |
| IJavaService | GameServices.cs:165 | 11 | JavaServiceCollectionExtensions.cs:14 | SettingsViewModel.cs:70、VersionsViewModel.cs:80、HomeViewModel.cs:106 |
| IInstanceService | InstanceServices.cs:4 | 15 | InstanceServiceCollectionExtensions.cs:14 | HomeViewModel.cs:95、VersionsViewModel.cs:72 |
| IInstanceLockService | InstanceServices.cs:124 | 2 | InstanceServiceCollectionExtensions.cs:31 | VersionsViewModel.cs:78、GameLauncher |
| IInstanceFolderService | InstanceServices.cs:233 | 3 | InstanceServiceCollectionExtensions.cs:44 | VersionsViewModel.cs:79 |
| IInstanceContext | InstanceServices.cs:258 | 9 | InstanceServiceCollectionExtensions.cs:17 | 7 个 VM（Mods/Library/Worlds/Logs/Servers/Home/Versions） |
| ILauncherLogReader | LogServices.cs:94 | 3 | LoggingServiceCollectionExtensions.cs:22 | LogsViewModel.cs:84 |
| ILogFollowService | LogServices.cs:169 | 1 | LogFollowServiceCollectionExtensions.cs:15 | LogsViewModel.cs:88 |
| IModProjectInfoService | ModProjectServices.cs:15 | 2 | ModServiceCollectionExtensions.cs:27 | ModsViewModel.cs:106、LibraryViewModel |
| IServerDownloadService | ServerServices.cs:10 | 4 | VersionServiceCollectionExtensions.cs:30 | DownloadViewModel.cs:80 |
| IServerQueryService | ServerServices.cs:189 | 1 | ServerServiceCollectionExtensions.cs:15 | ServersViewModel.cs:46 |
| IStorageService | StorageServices.cs:18 | 4 | StorageServiceCollectionExtensions.cs:19 | SettingsViewModel.cs:75 |
| IProfileBundleService | TransferServices.cs:13 | 3 | CoreSettingsServiceCollectionExtensions.cs:30 | SettingsViewModel.cs:74 |
| IVersionService | VersionServices.cs:4 | 7 | VersionServiceCollectionExtensions.cs:15 | HomeViewModel.cs:93、VersionsViewModel.cs:66、DownloadViewModel.cs:73 |
| IInstallService | VersionServices.cs:51 | 5 | InstallServiceCollectionExtensions.cs:12 | DownloadViewModel.cs:74、VersionsViewModel.cs:67、HomeViewModel.cs:105 |

结论：36 个接口、平均 5.7 个成员、全部单实现 —— 契约面本身可索引；难写的是语义（如 IInstallService.InstallLoaderAsync 默认返回 null 表示「不需要单独装加载器」，VersionServices.cs:95）。

---

## 3. 关键数据模型

### 3.1 设置（Models/Settings.cs）

| 类型 | 位置 | 要点 |
| --- | --- | --- |
| AppSettings | Settings.cs:43 | 单对象配置，序列化到 `<data>/settings.json`（Settings.cs:42 注释），约 50 个属性 |
| DownloadSource / ProxyModes | Settings.cs:15 / :30 | 下载源与代理模式枚举，反序列化须兼容历史字符串（Settings.cs:12） |
| JvmPreset | Settings.cs:401 | 自定义 JVM 预设，随 AppSettings.JvmPresets:338 内联落盘 |

AppSettings 分组索引（文档化时按组列，不逐条解释）：

| 组 | 属性:行号 |
| --- | --- |
| 路径与 Java | MinecraftDirectory:51、JavaPath:54、EnableJavaCache:215 |
| 内存与 JVM | MinMemoryMb:56(512)、MaxMemoryMb:58(4096)、AutoMaxMemory:158、ExtraJvmArguments:118、JvmPresets:338、ActiveJvmPresetId:343 |
| 窗口与外观 | WindowWidth:61、WindowHeight:63、Fullscreen:65、LauncherWindowWidth:88、LauncherWindowHeight:91、LauncherWindowMaximized:111、RememberWindowSize:116、Theme:224、EnableAnimations:227、EnableBackgroundEffects:230、WindowMaterial:233、BackgroundImagePath:99、BackgroundImageOverlay:105 |
| 下载 | DownloadSpeedLimitKb:164、DownloadThreads:221、DownloadSource:218、UseModrinthMirror:268 |
| 代理 | ProxyMode:173、ProxyAddress:179、ProxyUserName:182、ProxyPassword:192、ProxyPasswordProtected:198 |
| 游戏启动 | RelaxForgeChecks:133、ExtraGameArguments:140、VersionArgumentInfo:152、MaximizeGameWindow:203、GameWindowTitle:209、GameProcessPriority:303、AutoChineseLanguage:309、CloseLauncherAfterLaunch:293、LauncherVisibilityOnLaunch:300、RegisterGpuPreference:321 |
| 内容与整合包 | CurseForgeBaseUrl:258、ContentSource:271、IsolationPolicy:280 |
| 会话记忆 | LastSelectedVersion:240、LastInstanceId:246、LastAccountId:282、LastNotifiedGameVersion:349 |
| 托盘与更新 | CloseToTray:284、CheckUpdatesOnStartup:290 |
| 备份与崩溃 | ArchiveCrashReports:312、AutoBackupWorldsOnExit:329、WorldBackupRetention:332(20) |

### 3.2 账户（Models/Accounts.cs）

| 类型 | 位置 | 要点 |
| --- | --- | --- |
| AccountType | Accounts.cs:6 | Offline=0 / Microsoft=1 / Authlib=2 |
| Account | Accounts.cs:22 | Id:24、Type:26、Username:29、Uuid:32、AccessToken:34、RefreshToken:36、ExpiresAt:38、SkinModel:41、AuthServerUrl:47、LastLoginAt:50、IsExpired:52 |
| AccountResult | Accounts.cs:58 | (Success, Message, Account)，服务层统一返回形态 |
| AuthlibProfileOption / AuthlibInjection | Accounts.cs:82 / :88 | 外置登录档案选择与注入参数 (JarPath, ApiRoot, JvmArguments) |

### 3.3 版本与实例

| 类型 | 位置 | 要点 |
| --- | --- | --- |
| RemoteVersion | Versions.cs:29 | 远程清单条目：Id:31、Type:33、Url:35、ReleaseTime:37 |
| VersionInfo | Versions.cs:140 | Name:143、Channel:145、InheritsFrom:148、Loader:150、LoaderVersion:153、MinecraftVersion:156、LastPlayedAt:158、TotalPlayTimeSeconds:160、SizeOnDisk:163、BrokenReason:166 |
| LoaderVersion | Versions.cs:170 | Version:172、Stable:174、GameVersion:176、IsRecommended:186、IsInstalled:194 |
| InstanceInfo | Instances.cs:11 | 落 instance.json：Loader:39、MinecraftVersion:42、IsolationEnabled:56、JvmArguments:64、GameArguments:67、LaunchBefore/AfterCommand:73/76、AuthServerUrl/RegisterUrl/ServerName:153-168、ManagedPack:174 |
| IsolationPolicyKind / InstanceLoaderSwitchResult / InstanceCleanupReport | Instances.cs:329 / :529 / :517 | 隔离策略、切换加载器结果、清理报告 |

### 3.4 下载与安装

| 类型 | 位置 | 要点 |
| --- | --- | --- |
| DownloadTaskInfo | Downloads.cs:4 | Id:6、Name:9、Group:12、TotalBytes:14、DownloadedBytes:16、State:18、Url:26、DestinationPath:29、Sha1:32、Sequence:35 |
| DownloadTaskState / DownloadProgress | Downloads.cs:90 / :106 | 状态机与进度快照 |
| InstallStage / InstallStagePlan | Downloads.cs:135 / :157 | 三段式安装阶段（原版/加载器/处理器）与进度映射 |
| InstallRequest | Downloads.cs:261 | MinecraftVersion:263、Loader:265、LoaderVersion:268、ResultVersionName:271、Source:273 |
| RepairDepth / RepairReport | Downloads.cs:294 / :309 | 修复深度与报告 |

### 3.5 内容与在线资源

Content.cs 是最大模型文件：ContentKind:4、ContentItem:60、ModConflict:134、ModDependency:140、WorldInfo:274、BackupEntry:378、Modpack* 系列（398-665）。Modrinth.cs:9-256 与 CurseForge.cs:22-178 各持来源侧模型、互不复用 —— 字段形状差异需要适配层（Mods/CrossSourceSearch.cs）。

结论：模型层可文档化，但只在「按主题分组 + 字段要点 + 行号索引」粒度成立；逐条解释 AppSettings 的 50 个属性或 Modpack 的 20 余个类型，会吃掉整份文档预算。

---

## 4. 端到端调用链

### 4.1 安装原版 + 加载器

| 步 | 位置 | 说明 |
| --- | --- | --- |
| 1 | DownloadViewModel.cs:1126 InstallAsync() | 页面按钮入口 |
| 2 | DownloadViewModel.cs:765 / :829 / :1045 | 取远程版本、已装版本、加载器版本列表 |
| 3 | DownloadViewModel.cs:1183 | `_install.InstallAsync(request, progress, token, fabricApiVersionId)` |
| 4 | InstallService.cs:122 InstallAsync | 分发入口 |
| 5 | InstallService.cs:139 InstallLoaderAsync → :158 InstallCoreAsync | 加载器选择与核心流程 |
| 6a | InstallService.cs:528 InstallVanillaAsync | 纯原版：交给 CmlLib 安装器 |
| 6b | InstallService.cs:543 InstallFabricLikeAsync | 564 取 profile JSON 候选 URL → 572 下载 → 578 改写 id → 587 写 `<版本目录>/<版本名>.json` → 593-595 由 CmlLib 沿 inheritsFrom 补齐库与资源；615 再装 Fabric API |
| 6c | InstallService.cs:704 InstallForgeAsync | 723 原版 0–50% → 730 `new ForgeInstaller(launcher, _client)` → 743 LoaderDownload（处理器 50–100%）→ 756 Finalize；748/752 按是否指定版本二选一 |
| 6d | InstallService.cs:775 InstallNeoForgeAsync | 790 原版 → 809 加载器下载 → 811 `NeoForgeInstallerBridge.InstallAsync` → 820 Finalize |
| 7 | InstallService.cs:842 CreateLauncher → Launching/MinecraftLauncherFactory.cs:39 | 43/56 注入 NebulaGameInstaller 与并发上限；68-79 摘掉 JavaFileExtractor |
| 8 | 外部库 | CmlLib.Core：`MinecraftLauncher.InstallAsync`、`ForgeInstaller.Install` |
| 9 | 外部端点 | 版本清单 VersionService.cs:28；加载器 meta LoaderMetaFetcher.cs:17/18/21-22/30；镜像 DownloadInfrastructure.cs:21/26 |

### 4.2 启动游戏

| 步 | 位置 | 说明 |
| --- | --- | --- |
| 1 | HomeViewModel.cs:912 LaunchAsync() → :1001 | `_launcher.LaunchAsync(options, progress, token)` |
| 1' | VersionsViewModel.cs:1050 | 版本页同一入口 |
| 2 | GameServices.cs:13 契约 → GameLauncher.cs:216 实现 | |
| 3 | GameLauncher.cs:941 BuildMinecraftPath | 构造 CmlLib MinecraftPath |
| 4 | GameLauncher.cs:1341 RunEnvironmentPreflightAsync → :1382 CheckJavaPreflight → :1442 ResolveMissingJavaMajor | 环境预检（Java / 内存 / 实例锁） |
| 5 | GameLauncher.cs:1233 ResolveAuthlibArgumentsAsync | 外置登录注入 |
| 6 | GameLauncher.cs:1126 ComposeJvmArguments → :1064 DeduplicateGameArguments → :960 BuildLaunchOption | JVM 与游戏参数合成 |
| 7 | GameLauncher.cs:1485 ApplyGpuPreference / :1523 ApplyChineseLanguage / :1688 ApplyProcessPriority | 启动前处理 |
| 8 | 外部库 | CmlLib.Core 启动；配置来源统一为 MinecraftLauncherFactory.cs:39 |
| 9 | GameLauncher.cs:683 CompleteExitAsync / :1713 ArchiveCrashArtifacts | 退出收尾与崩溃归档；CrashAnalyzer.cs 负责分析 |

### 4.3 Modrinth / CurseForge 搜索 → 下载 → 安装

| 步 | 位置 | 说明 |
| --- | --- | --- |
| 1 | LibraryViewModel.cs:2796 / :2830 | 分别调 `_modrinth.SearchSortedAsync` 与 `_curseforge.SearchAsync` |
| 2 | ModrinthService.cs:146 SearchSortedAsync；ModrinthService.cs:33 ApiBase | CurseForgeService.cs:38 DefaultApiBase、:35 内置 x-api-key |
| 3 | LibraryViewModel.cs:1913-1924 | 整合包分区：`_modpack.InstallGameOnImport` + `ApplyModPackImport` |
| 4 | LibraryViewModel.cs:1015 | `_modrinth.InstallAsync(...)` 单文件安装 |
| 5 | ModrinthService.cs:542 InstallAsync → :607 OpenDownloadAsync → :939 | 直接以 HttpClient 流式下载，不经 DownloadQueue |
| 6 | LibraryViewModel.cs:1709 → ModrinthService.cs:761 InstallPlanAsync → :825 | 批量安装计划，逐项回落 InstallAsync |
| 7 | CurseForgeService.cs:814 InstallAsync | 主/备 CDN：CurseForgeService.cs:2352/2355 |
| 8 | 落盘 | 内容写入 gameDirectory；禁用靠改后缀 ContentBatch.cs:54 `.disabled` |
| 9 | 镜像与覆盖 | api.modrinth.com → mod.mcimirror.top（DownloadInfrastructure.cs:55）；Settings.CurseForgeBaseUrl:258 可覆盖 |

结论：三条链的外部库/HTTP 端点边界清楚，可用统一格式写进一份文档；复杂度集中在分支（6a-6d、加载器检测、镜像降级），需要阶段表 + 行号锚点，而非叙述。

---

## 5. 页面 ↔ ViewModel ↔ Core 服务

页面注册在 MainWindowViewModel 三个 partial：HomePages.cs:11-13（Home/Versions/Download）、ContentPages.cs:13-16（Mods/Library/Accounts/Servers）、SystemPages.cs:11-14（Worlds/Logs/Help/Settings）；导航 8 + 3 见 MainWindowViewModel.cs:731-744。

| 页面 | ViewModel（构造行） | 注入的 Core 服务 | 依赖的 Core 模块 |
| --- | --- | --- | --- |
| 主页 | HomeViewModel.cs:91-106 | 15 个，含 IGameLauncher、IInstallService、IContentService、IBackupService、ICrashExportService | Launching / Installing / Mods / Backups |
| 版本 | VersionsViewModel.cs:65-80 | 15 个，含 IComponentInspector、IInstanceLockService、IInstanceFolderService、IJavaService | Versions / Instances / Java |
| 下载 | DownloadViewModel.cs:72-83 | IVersionService、IInstallService、DownloadQueue、IDownloadSourceProvider/Probe、IServerDownloadService、IModrinthService | Downloads / Installing / Versions |
| Mod 管理 | ModsViewModel.cs:99-108 | IServiceProvider 手工取：IContentService、IModUpdateService、IModProjectInfoService、IInstanceContext | Mods / Instances |
| 资源库 | LibraryViewModel.cs:132-153 | IModrinthService、ICurseForgeService、IModpackService、DownloadQueue、ModpackCatalogClient | Mods / Backups / Downloads |
| 账户 | AccountsViewModel.cs:31-42 | IAccountService、IPlatformService；服务缺失降级为示例数据（:36） | Accounts |
| 服务器 | ServersViewModel.cs:43-53 | IServerService、IServerQueryService、IAccountService、IGameLauncher | Servers / Launching |
| 世界 | WorldsViewModel.cs:48-55 | IBackupService、IModpackService、IContentService、IWorldIconService | Backups / Mods |
| 日志 | LogsViewModel.cs:83-88 | ILauncherLogReader、ILogFollowService、IInstanceContext | Logging |
| 帮助 | HelpViewModel.cs:22 | IPlatformService、ILauncherPaths；内容全来自 HelpCatalog.Topics（:27） | Help |
| 设置 | SettingsViewModel.cs:68-75 | ISettingsService、IJavaService、IProfileBundleService、IStorageService；六分区 :85-90 | Settings / Java / Transfer / Maintenance |

结论：页面-服务映射是「一个页面 2-6 个服务」的规整结构；例外是 Mods/Library/Accounts/Servers 走 IServiceProvider 手工取服务，这层间接在容器里查不到，文档必须显式标注。

---

## 6. DI 注册点

组合根 `src/NebulaLauncher.App/App.axaml.cs:273 BuildServices()`，按调用顺序：

| 序 | 调用 | 注册项 |
| --- | --- | --- |
| 1 | App.axaml.cs:285 AddLauncherPaths；:289-294 手工 new LauncherPaths | ILauncherPaths |
| 2 | :297 AddCoreLogging（LoggingServiceCollectionExtensions.cs:18） | IAppLog、ILauncherLogReader、ICrashExportService |
| 3 | :301 AddCoreLogFollow（LogFollowServiceCollectionExtensions.cs:12） | ILogFollowService |
| 4 | :304 AddCorePlatforms（PlatformServiceCollectionExtensions.cs:9） | IPlatformService |
| 5 | :305 AddCoreSettings（CoreSettingsServiceCollectionExtensions.cs:12） | ISettingsService |
| 6 | :306 AddAccounts（AccountServiceCollectionExtensions.cs:10） | IAccountService |
| 7 | :307 AddCoreVersions（VersionServiceCollectionExtensions.cs:11） | IVersionService、IVersionFavorites、IServerDownloadService |
| 8 | :308 AddGameLaunching（LaunchingServiceCollectionExtensions.cs:17） | IGameLauncher、IPreflightChecker、IAuthlibInjectorService |
| 9 | :309-311 AddInstances / AddInstanceLocks / AddInstanceFolders | IInstanceService、IInstanceContext / IInstanceLockService / IInstanceFolderService |
| 10 | :312 AddCoreAssets（CoreAssetsServiceCollectionExtensions.cs:17） | 聚合 7 组：Platforms、Downloads、Java、Installing、Content、Backups、Servers |
| 11 | :315 AddCoreWorldIcons（WorldIconService.cs:261） | IWorldIconService |
| 12 | :319 AddCoreModUpdates（ModServiceCollectionExtensions.cs:22） | IModUpdateService、IModProjectInfoService |
| 13 | :322 AddCoreProfileTransfer（CoreSettingsServiceCollectionExtensions.cs:26） | IProfileBundleService |
| 14 | :325 AddCoreStorage（StorageServiceCollectionExtensions.cs:15） | IStorageService |
| 15 | :328 AddDownloadSourceProbe（DownloadSourceProbe.cs:555） | IDownloadSourceProbe |
| 16 | :331-332 | INavigationService → NavigationServiceProxy |
| 17 | :335-336 | IWindowActions → WindowActionsProxy |

- AddCoreAssets 展开（CoreAssetsServiceCollectionExtensions.cs:19-25）：AddCorePlatforms、AddCoreDownloads、AddCoreJava、AddCoreInstalling、AddCoreContent、AddCoreBackups、AddCoreServers。
- AddCoreDownloads 展开（DownloadServiceCollectionExtensions.cs:13/26/27/30/36/42）：DownloadSourceGovernor（订阅 settings.Changed）、IDownloadSourceProvider、IDownloadSourceTelemetry、DownloadHttpClientFactory、FileReuseService、DownloadQueue。
- 页面 ViewModel 不在组合根注册：MainWindowViewModel.cs:715-722 先 `Services.GetService`，未命中则 `ActivatorUtilities.CreateInstance<T>(Services)`。

结论：DI 拓扑 = 17 行调用链 + 7 个聚合扩展，可逐条列完；聚合扩展隐藏了一层归属，文档必须展开 AddCoreAssets 与 AddCoreDownloads。

---

## 7. 用户数据文件读写方

数据根由 ILauncherPaths.RootDirectory 决定（LauncherPaths.cs:113，默认 %APPDATA%/NebulaLauncher，解析逻辑 :272）；文件名常量集中在 LauncherPaths.cs:145-149。

| 文件 | 路径定义 | 读 | 写 | 备注 |
| --- | --- | --- | --- | --- |
| accounts.json | LauncherPaths.cs:145 | AccountService.cs:116 LoadCoreAsync → :158 | AccountService.cs:939 PersistAsync → :968 序列化、:985 临时文件+替换 | 令牌经 TokenProtector 保护 |
| settings.json | LauncherPaths.cs:147 | SettingsService.cs:52 → :75；InstanceContext.cs:145 另只读一次 | SettingsService.cs:121 → :137-138 | 临时文件替换；LauncherPaths.cs:319-328 反向读它取自定义游戏目录 |
| servers.json | LauncherPaths.cs:149 | ServerService.cs:54 → :71 | ServerService.cs:466 → :482-484 | 导入前必须先 Load（ServerService.cs:32） |
| favorites.json | VersionFavorites.cs:108 | VersionFavorites.cs:157 → :292 | VersionFavorites.cs:362 → :383-385；切换入口 :172/:218 | 与 servers.json 同级，tmp+move |
| instance.json（每实例一份） | InstanceService.cs:208 | InstanceService.cs:92 / :367；Trash.cs:350 | InstanceService.cs:211-215；Trash.cs:400-402；ModpackService.cs:369-370 | 目录名即 Id |
| .nebula-run.json（运行锁） | InstanceLockService.cs:24 | :197 / :243 | :130-131 / :171-172 | 与游戏进程 pid 绑定 |
| servers.dat | 游戏目录（Minecraft 原生） | ServerListDatReader.cs:60；ServerService.cs:223 | 不写（只导入） | ModpackExporter.cs:273 导出可带上 |
| 版本目录 <name>.json + nebula.json | VersionService.cs:31 | VersionService.cs:1103 ScanVersionAsync 等 | VersionService.cs:239 RecordLaunchAsync → :278-280 | 启动次数/时长记在版本 json 旁 |
| version_manifest_v2.json（缓存） | VersionService.cs:30 | VersionService.cs:142 / :193 | VersionService.cs:1410-1420 | 镜像 URL :29 |
| forge-promotions.json（缓存） | VersionService.cs:34 | VersionService.cs:931 | :999 | TTL 见 :37/:39 |
| server-versions.json（缓存） | ServerDownloadService.cs:35 | :843 | :909-913 | 顺带写 `Launch Server.bat`（:43/:529） |
| mod-hashes.json | ModFingerprint.cs:26 | ModFingerprint 内部 | ModFingerprint.cs:508 起（Interlocked 单写者） | 指纹缓存 |
| mod-projects.json | ModProjectInfoService.cs:32 | ModProjectInfoService 内部 | :701 起 | 在线工程信息缓存 |
| java-excluded.json / java-manual.json | JavaService.cs:46 / :52 | JavaPathList.cs:18 | JavaPathList | 用户数据，不进 settings |
| java-cache.json | JavaCache.cs:58 | JavaCache.cs:102 | JavaCache.cs:147-150 | 探测结果缓存 |
| backups/*.zip + *.meta.json | BackupService.cs:131/:137/:176 | BackupService.cs:612 | BackupService.cs:587-588 | meta 让列表页不必开 zip |
| trash/instances、trash/contents | InstanceService.Trash.cs:15；ContentService.cs:47 | Trash.cs:115-120；ContentService.cs:279 起 | Trash.cs:50/176/271 | 两级回收站 |
| logs/launcher-YYYYMMDD.log | FileLogger.cs:40 | LauncherLogReader.cs | FileLogger.cs:21 | 按天滚动 |
| runtimes/java-* | JavaService.cs:671 / :795 | JavaService | JavaService | 运行时自解压目录 |

- 原任务提到的 `mods.lock.json` **在代码中不存在**。最近似的两个是 ModFingerprint.cs:26 的 `mod-hashes.json`（指纹缓存）与 InstanceLockService.cs:24 的 `.nebula-run.json`（运行锁）。文档若提 `mods.lock.json`，需先确认为规划中的设计。

结论：共 19 类落盘对象；真正的「用户数据结构」只有 5 个顶层 JSON（accounts/settings/servers/favorites + instance.json），其余是缓存、实例内元数据与回收站。

---

## 8. 可行性与风险

### 8.1 「项目数据结构及调用」能否用一份文档讲清

能，但必须分体例：

- 结构与契约（36 个接口、13 个 Models 文件、100+ 类型、19 类落盘对象）信息量约 600 行表格；全部塞进一份 400 行文档会挤掉调用链。建议：接口与模型做成**生成型索引**（一类型一行：名称、位置、字段数、一句话），正文只深讲 4 个核心对象 —— AppSettings、InstanceInfo、InstallRequest/InstallStagePlan、ContentItem。
- 调用链（安装 / 启动 / 内容下载）适合正文，因为分支与顺序无法从签名推出；但每条链必须带行号锚点。
- 预计单份文档 350-450 行：索引表约 200 行、调用链正文约 120 行、边界说明 50 行。

### 8.2 哪些部分只能做索引/生成

| 内容 | 为什么正文写不动 | 建议 |
| --- | --- | --- |
| AppSettings 50 个属性 | 逐个解释价值低且易过期 | 表格索引：组 + 属性:行号 + 默认值；只解释 5 个非显然项 |
| Models 全量类型（100+） | 3675 行、类型密度高，正文必然写成流水账 | 生成：从 `public (class\|record\|enum)` 抽名称/行号/字段数 |
| 接口成员签名（36 接口 / 约 200 成员） | 签名自解释 | 生成索引；正文只讲 6 个有隐含语义的（IInstallService、IInstanceContext、IDownloadSourceProvider、IModrinthService、IProfileBundleService、IComponentInspector） |
| DI 注册全表 | 扩展方法层层聚合，人工维护必漏 | 生成：抓 `Add*` 调用与 `AddSingleton/AddTransient` 配对；文档固定记录 App.axaml.cs:273-336 的 17 行骨架 |
| 用户数据文件读写点 | 19 类文件 × 读/写/临时替换，分散在 15 个文件 | 生成索引（文件 → 常量定义行 → 读写方法行）；正文只留 5 个顶层 JSON + 回收站 |
| 页面 ↔ VM ↔ 服务映射 | 11 行表格稳定 | 人工维护（§5） |

### 8.3 「代码组成（方便 agent 改项目）」的覆盖边界

- 可覆盖：目录职责（§1）、接口→实现→消费方（§2）、DI 骨架（§6）、改动入口定位（页面 → VM 构造行 → Core 服务）。
- 不可覆盖：行为语义与坑位。例：InstallService.cs:543 只写 profile JSON 让 CmlLib 补全、MinecraftLauncherFactory.cs:68-79 必须摘掉 JavaFileExtractor、ModrinthService.cs:542 的下载不经 DownloadQueue —— 这类「为什么这样写」散在注释里（如 MinecraftLauncherFactory.cs:8-26 整段说明），只能靠代码注释索引，不能靠文档正文复述。
- 建议：代码组成文档以「目录 + 契约 + 入口」为骨架（约 250 行），每节附一句「改 X 先读哪个文件」。

### 8.4 风险

| 风险 | 影响 | 处理 |
| --- | --- | --- |
| 行号漂移 | 本文档全部结论绑行号，一次重构即失效 | 索引表标注生成命令；正文锚点尽量用「方法名 + 文件」而非纯行号 |
| 静态阅读未验证 | 未跑 build/test，个别分支可能漏（Forge/NeoForge 失败回滚未逐行追） | 调用链标注置信度；InstallService.cs:494 VerifyAsync 与 InstallService.Repair.cs 未展开 |
| 单文档超长 | 超 400 行后维护成本反超收益 | 拆为「数据结构索引（生成）+ 调用链正文 + 代码组成」三份，或一份 400 行 + 生成附录 |
| 隐含约定 | 「页面 VM 不在容器注册」「Mods 页面缺服务即降级示例数据」等 | 必须在代码组成文档里显式写，否则 agent 改动会踩空 |

---

## 附：本次未查清项

- tools/ 目录内容未盘点。
- tests/NebulaLauncher.Tests 的覆盖分布未盘点。
- InstallService.Repair.cs（1067 行）与 CrashAnalyzer/CrashRules（2020 行）只记入口，未展开分支。
- Forge/NeoForge 安装失败后的回滚路径未验证。
