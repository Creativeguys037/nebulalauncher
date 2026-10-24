# CODE.md — 代码组成、数据结构与调用

面向 agent 的定位文档：先查表找到目录/类型，再顺着调用链读代码。行号是当前提交的提示，重命名后以类型名为准。

读法建议：新功能 → §1 找目录 → §2 找契约接口 → §4 找同类调用链 → §5 确认 DI 注册点 → §6 确认落盘位置。

## 1. 目录表

### 1.1 src/NebulaLauncher.Core（22 个目录）

| 目录 | 职责（一句话） | 关键类型 |
| --- | --- | --- |
| Abstractions | 全部契约接口与共享记录类型，只依赖 BCL，Core 各模块靠它解耦 | `src/NebulaLauncher.Core/Abstractions/Infrastructure.cs:4 ILauncherPaths`、`src/NebulaLauncher.Core/Abstractions/VersionServices.cs:51 IInstallService`、`src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:258 IInstanceContext` |
| Accounts | 微软设备码登录、authlib 登录、令牌加密存储与刷新 | `src/NebulaLauncher.Core/Accounts/AccountService.cs:14 AccountService`、`src/NebulaLauncher.Core/Accounts/MicrosoftDeviceCodeAuth.cs:45 MicrosoftDeviceCodeAuth`、`src/NebulaLauncher.Core/Accounts/TokenProtector.cs:12 TokenProtector` |
| Backups | 世界备份、整合包导入导出、实例打包与头像图标 | `src/NebulaLauncher.Core/Backups/BackupService.cs:12 BackupService`、`src/NebulaLauncher.Core/Backups/ModpackService.cs:25 ModpackService`、`src/NebulaLauncher.Core/Backups/WorldIconService.cs:20 WorldIconService` |
| Diagnostics | 一次性环境快照（OS/JVM/内存/磁盘），供问题报告 | `src/NebulaLauncher.Core/Diagnostics/DiagnosticsSnapshot.cs:73 DiagnosticsSnapshot` |
| Downloads | 下载队列（并发/限速/暂停）、源探测与治理、HTTP 客户端工厂、文件复用 | `src/NebulaLauncher.Core/Downloads/DownloadQueue.cs:120 DownloadQueue`、`src/NebulaLauncher.Core/Downloads/DownloadSourceGovernor.cs:31 DownloadSourceGovernor`、`src/NebulaLauncher.Core/Downloads/DownloadInfrastructure.cs:996 DownloadHttpClientFactory` |
| Help | 内置离线帮助条目，不读网络、不读文件 | `src/NebulaLauncher.Core/Help/HelpCatalog.cs:20 HelpCatalog` |
| Installing | 原版/Fabric/Quilt/Forge/NeoForge 安装、修复、安装清单写入 | `src/NebulaLauncher.Core/Installing/InstallService.cs:22 InstallService`、`src/NebulaLauncher.Core/Installing/NeoForgeInstallerBridge.cs:45 NeoForgeInstallerBridge`、`src/NebulaLauncher.Core/Installing/ComponentInspector.cs:16 ComponentInspector` |
| Instances | 实例目录定位、隔离策略、元数据、运行锁、回收站 | `src/NebulaLauncher.Core/Instances/InstanceService.cs:23 InstanceService`、`src/NebulaLauncher.Core/Instances/InstanceContext.cs:18 InstanceContext`、`src/NebulaLauncher.Core/Instances/InstanceLockService.cs:21 InstanceLockService` |
| Java | JVM 探测/下载/校验、Mojang runtime 清单、Java 选择缓存 | `src/NebulaLauncher.Core/Java/JavaService.cs:16 JavaService`、`src/NebulaLauncher.Core/Java/MojangRuntimeCatalog.cs:34 MojangRuntimeCatalog`、`src/NebulaLauncher.Core/Java/JavaCache.cs:55 JavaCache` |
| Launching | 启动参数合成、游戏进程与运行锁、预检、崩溃分析、自动世界备份 | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:24 GameLauncher`、`src/NebulaLauncher.Core/Launching/JvmArgumentBuilder.cs:39 JvmArgumentBuilder`、`src/NebulaLauncher.Core/Launching/PreflightChecker.cs:651 PreflightCheckerService` |
| Logging | 文件日志写入、日志读取与实时跟踪、崩溃包导出 | `src/NebulaLauncher.Core/Logging/FileLogger.cs:11 FileLogger`、`src/NebulaLauncher.Core/Logging/LauncherLogReader.cs:22 LauncherLogReader`、`src/NebulaLauncher.Core/Logging/LogFollower.cs:24 LogFollower` |
| Maintenance | 存储清理（缓存/日志/备份体积统计与删除） | `src/NebulaLauncher.Core/Maintenance/StorageService.cs:26 StorageService` |
| Models | 跨模块 DTO：设置、实例、下载、内容、启动、账号、版本 | `src/NebulaLauncher.Core/Models/Settings.cs:43 AppSettings`、`src/NebulaLauncher.Core/Models/Downloads.cs:261 InstallRequest`、`src/NebulaLauncher.Core/Models/Launching.cs:6 LaunchOptions` |
| Mods | 内容搜索、指纹识别、更新检查、Modrinth/CurseForge 客户端、依赖解析 | `src/NebulaLauncher.Core/Mods/ContentService.cs:18 ContentService`、`src/NebulaLauncher.Core/Mods/ModrinthService.cs:30 ModrinthService`、`src/NebulaLauncher.Core/Mods/ModrinthInstallPlanner.cs:20 ModrinthInstallPlanner` |
| Net | 网络可达性探测、代理配置与代理凭据 | `src/NebulaLauncher.Core/Net/NetworkProbe.cs:60 NetworkProbe`、`src/NebulaLauncher.Core/Net/ProxyConfigurator.cs:19 ProxyConfigurator`、`src/NebulaLauncher.Core/Net/ProxyCredentialStore.cs:14 ProxyCredentialStore` |
| Paths | 启动器数据目录/游戏目录/各类文件路径的唯一解析点 | `src/NebulaLauncher.Core/Paths/LauncherPaths.cs:19 LauncherPaths`、`src/NebulaLauncher.Core/Paths/PathProbe.cs:11 PathProbe`、`src/NebulaLauncher.Core/Paths/PathSafety.cs:22 PathSafety` |
| Platforms | 平台服务：打开目录/浏览器、系统信息、原生集成 | `src/NebulaLauncher.Core/Platforms/PlatformService.cs:46 PlatformService`、`src/NebulaLauncher.Core/Platforms/PlatformNative.cs:11 PlatformNative` |
| Resources | 嵌入资源，无 cs 代码；`WikiEntries.txt` 是 MC 百科译名表 | `src/NebulaLauncher.Core/Mods/ModNameTable.cs:204 ModNameTable.ResourceName` |
| Servers | 服务器列表落盘、`servers.dat` 导入、状态查询 | `src/NebulaLauncher.Core/Servers/ServerService.cs:15 ServerService`、`src/NebulaLauncher.Core/Servers/ServerQueryService.cs:30 ServerQueryService`、`src/NebulaLauncher.Core/Servers/ServerListDatReader.cs:22 ServerListDatReader` |
| Settings | 设置读写（`settings.json`），变更事件 | `src/NebulaLauncher.Core/Settings/SettingsService.cs:18 SettingsService` |
| Transfer | 配置包导入导出（跨机器迁移） | `src/NebulaLauncher.Core/Transfer/ProfileBundleService.cs:212 ProfileBundleService` |
| Versions | 版本清单、安装记录、收藏与隐藏、服务端下载 | `src/NebulaLauncher.Core/Versions/VersionService.cs:25 VersionService`、`src/NebulaLauncher.Core/Versions/VersionFavorites.cs:83 VersionFavorites`、`src/NebulaLauncher.Core/Versions/ServerDownloadService.cs:32 ServerDownloadService` |

### 1.2 src/NebulaLauncher.App（ViewModels / Views / Services / Controls）

| 目录 | 职责（一句话） | 关键类型 |
| --- | --- | --- |
| ViewModels | 页面状态与命令；`MainWindowViewModel` 是导航与页面工厂的根 | `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs:670 MainWindowViewModel.RegisterPages`、`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:912 HomeViewModel.LaunchAsync` |
| Views | Avalonia 视图，与 ViewModels 一一对应 | `src/NebulaLauncher.App/Views/MainWindow.axaml.cs:21 MainWindow` |
| Services | App 层服务：导航/窗口动作代理、启动参数合成、外壳进度 | `src/NebulaLauncher.App/Services/LaunchOptionsFactory.cs:41 LaunchOptionsFactory.Build`、`src/NebulaLauncher.App/Services/NavigationServiceProxy.cs:13 NavigationServiceProxy` |
| Controls | 自定义控件（页面宿主、极光背景、折叠容器、环形进度） | `src/NebulaLauncher.App/Controls/PageHost.cs:19 PageHost`、`src/NebulaLauncher.App/Controls/AuroraBackground.cs:22 AuroraBackground` |

其余子目录（Converters / Themes / Assets / Properties）只放转换器、样式令牌与静态资源，不含业务逻辑。

## 2. 接口契约表

接口全部声明在 src/NebulaLauncher.Core/Abstractions（37 个 `interface I*`，含 `IInstanceLockHandle`）。消费方以页面 ViewModel 的构造签名为证。

| 接口 | 实现类 | 消费方 |
| --- | --- | --- |
| `src/NebulaLauncher.Core/Abstractions/AccountServices.cs:4 ISettingsService` | `src/NebulaLauncher.Core/Settings/SettingsService.cs:18 SettingsService` | 全部页面 VM；`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:91 HomeViewModel` |
| `src/NebulaLauncher.Core/Abstractions/AccountServices.cs:28 IAccountService` | `src/NebulaLauncher.Core/Accounts/AccountService.cs:14 AccountService` | `src/NebulaLauncher.App/ViewModels/AccountsViewModel.cs:31 AccountsViewModel`、`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:91 HomeViewModel` |
| `src/NebulaLauncher.Core/Abstractions/AuthlibServices.cs:10 IAuthlibInjectorService` | `src/NebulaLauncher.Core/Launching/AuthlibInjectorService.cs:21 AuthlibInjectorService` | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:216 GameLauncher.LaunchAsync`（内部） |
| `src/NebulaLauncher.Core/Abstractions/ComponentServices.cs:12 IComponentInspector` | `src/NebulaLauncher.Core/Installing/ComponentInspector.cs:16 ComponentInspector` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:65 VersionsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs:71 IContentService` | `src/NebulaLauncher.Core/Mods/ContentService.cs:18 ContentService` | `src/NebulaLauncher.App/ViewModels/ModsViewModel.cs:99 ModsViewModel`、`src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs:48 WorldsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs:140 IModpackService` | `src/NebulaLauncher.Core/Backups/ModpackService.cs:25 ModpackService` | `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:132 LibraryViewModel`、`src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs:48 WorldsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs:189 IBackupService` | `src/NebulaLauncher.Core/Backups/BackupService.cs:12 BackupService` | `src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs:48 WorldsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs:250 IWorldIconService` | `src/NebulaLauncher.Core/Backups/WorldIconService.cs:20 WorldIconService` | `src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs:48 WorldsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs:267 IModrinthService` | `src/NebulaLauncher.Core/Mods/ModrinthService.cs:30 ModrinthService` | `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:132 LibraryViewModel`、`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:72 DownloadViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs:442 IModUpdateService` | `src/NebulaLauncher.Core/Mods/ModUpdateService.cs:23 ModUpdateService` | `src/NebulaLauncher.App/ViewModels/ModsViewModel.cs:99 ModsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs:486 IServerService` | `src/NebulaLauncher.Core/Servers/ServerService.cs:15 ServerService` | `src/NebulaLauncher.App/ViewModels/ServersViewModel.cs:43 ServersViewModel` |
| `src/NebulaLauncher.Core/Abstractions/CrashExportServices.cs:65 ICrashExportService` | `src/NebulaLauncher.Core/Launching/CrashExportService.cs:22 CrashExportService` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:91 HomeViewModel` |
| `src/NebulaLauncher.Core/Abstractions/CurseForgeServices.cs:17 ICurseForgeService` | `src/NebulaLauncher.Core/Mods/CurseForgeService.cs:27 CurseForgeService` | `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:132 LibraryViewModel` |
| `src/NebulaLauncher.Core/Abstractions/DownloadSourceServices.cs:12 IDownloadSourceProvider` | `src/NebulaLauncher.Core/Downloads/DownloadSourceGovernor.cs:31 DownloadSourceGovernor` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:72 DownloadViewModel` |
| `src/NebulaLauncher.Core/Abstractions/DownloadSourceServices.cs:38 IDownloadSourceTelemetry` | `src/NebulaLauncher.Core/Downloads/DownloadSourceGovernor.cs:31 DownloadSourceGovernor` | `src/NebulaLauncher.Core/Downloads/DownloadSourceProbe.cs:209 DownloadSourceProbe` |
| `src/NebulaLauncher.Core/Abstractions/DownloadSourceServices.cs:165 IDownloadSourceProbe` | `src/NebulaLauncher.Core/Downloads/DownloadSourceProbe.cs:209 DownloadSourceProbe` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:72 DownloadViewModel` |
| `src/NebulaLauncher.Core/Abstractions/FavoriteServices.cs:10 IVersionFavorites` | `src/NebulaLauncher.Core/Versions/VersionFavorites.cs:83 VersionFavorites` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:65 VersionsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/GameServices.cs:4 IGameLauncher` | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:24 GameLauncher` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:91 HomeViewModel`、`src/NebulaLauncher.App/ViewModels/ServersViewModel.cs:43 ServersViewModel` |
| `src/NebulaLauncher.Core/Abstractions/GameServices.cs:142 IPreflightChecker` | `src/NebulaLauncher.Core/Launching/PreflightChecker.cs:651 PreflightCheckerService` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:65 VersionsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/GameServices.cs:165 IJavaService` | `src/NebulaLauncher.Core/Java/JavaService.cs:16 JavaService` | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs:68 SettingsViewModel`、`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:91 HomeViewModel` |
| `src/NebulaLauncher.Core/Abstractions/Infrastructure.cs:4 ILauncherPaths` | `src/NebulaLauncher.Core/Paths/LauncherPaths.cs:19 LauncherPaths` | 全部页面 VM 与绝大多数 Core 模块 |
| `src/NebulaLauncher.Core/Abstractions/Infrastructure.cs:45 IPlatformService` | `src/NebulaLauncher.Core/Platforms/PlatformService.cs:46 PlatformService` | 全部页面 VM |
| `src/NebulaLauncher.Core/Abstractions/Infrastructure.cs:142 IAppLog` | `src/NebulaLauncher.Core/Logging/FileLogger.cs:11 FileLogger` | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs:68 SettingsViewModel`、Core 各服务内部 |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:4 IInstanceService` | `src/NebulaLauncher.Core/Instances/InstanceService.cs:23 InstanceService` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:65 VersionsViewModel`、`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:91 HomeViewModel` |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:124 IInstanceLockService` | `src/NebulaLauncher.Core/Instances/InstanceLockService.cs:21 InstanceLockService` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:65 VersionsViewModel`、`src/NebulaLauncher.Core/Launching/GameLauncher.cs:378 GameLauncher.LaunchAsync` |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:160 IInstanceLockHandle` | 由 `src/NebulaLauncher.Core/Instances/InstanceLockService.cs:69 InstanceLockService.TryAcquireAsync` 返回 | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:378 GameLauncher.LaunchAsync`（释放即解锁） |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:233 IInstanceFolderService` | `src/NebulaLauncher.Core/Instances/InstanceFolders.cs:166 InstanceFolderService` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:65 VersionsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:258 IInstanceContext` | `src/NebulaLauncher.Core/Instances/InstanceContext.cs:18 InstanceContext` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:91 HomeViewModel`、`src/NebulaLauncher.App/ViewModels/ModsViewModel.cs:99 ModsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/LogServices.cs:94 ILauncherLogReader` | `src/NebulaLauncher.Core/Logging/LauncherLogReader.cs:22 LauncherLogReader` | `src/NebulaLauncher.App/ViewModels/LogsViewModel.cs:83 LogsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/LogServices.cs:169 ILogFollowService` | `src/NebulaLauncher.Core/Logging/LogFollower.cs:24 LogFollower` | `src/NebulaLauncher.App/ViewModels/LogsViewModel.cs:83 LogsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ModProjectServices.cs:15 IModProjectInfoService` | `src/NebulaLauncher.Core/Mods/ModProjectInfoService.cs:29 ModProjectInfoService` | `src/NebulaLauncher.App/ViewModels/ModsViewModel.cs:99 ModsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ServerServices.cs:10 IServerDownloadService` | `src/NebulaLauncher.Core/Versions/ServerDownloadService.cs:32 ServerDownloadService` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:72 DownloadViewModel` |
| `src/NebulaLauncher.Core/Abstractions/ServerServices.cs:189 IServerQueryService` | `src/NebulaLauncher.Core/Servers/ServerQueryService.cs:30 ServerQueryService` | `src/NebulaLauncher.App/ViewModels/ServersViewModel.cs:43 ServersViewModel` |
| `src/NebulaLauncher.Core/Abstractions/StorageServices.cs:18 IStorageService` | `src/NebulaLauncher.Core/Maintenance/StorageService.cs:26 StorageService` | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs:68 SettingsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/TransferServices.cs:13 IProfileBundleService` | `src/NebulaLauncher.Core/Transfer/ProfileBundleService.cs:212 ProfileBundleService` | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs:68 SettingsViewModel` |
| `src/NebulaLauncher.Core/Abstractions/VersionServices.cs:4 IVersionService` | `src/NebulaLauncher.Core/Versions/VersionService.cs:25 VersionService` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:91 HomeViewModel`、`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:72 DownloadViewModel` |
| `src/NebulaLauncher.Core/Abstractions/VersionServices.cs:51 IInstallService` | `src/NebulaLauncher.Core/Installing/InstallService.cs:22 InstallService` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:72 DownloadViewModel`、`src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:65 VersionsViewModel` |

`IInstanceLockHandle` 是运行锁句柄（`IAsyncDisposable`），不是服务；容器不注册它。

## 3. 跨模块数据结构

只列被两处以上共享、且改动会波及多模块的少数类型。

| 类型 | 定义 | 谁写 | 谁读 |
| --- | --- | --- | --- |
| `AppSettings` | `src/NebulaLauncher.Core/Models/Settings.cs:43 AppSettings` | `src/NebulaLauncher.Core/Settings/SettingsService.cs:121 SettingsService.SaveAsync` | 所有 VM 经 `ISettingsService.Current`；`src/NebulaLauncher.App/Services/LaunchOptionsFactory.cs:41 LaunchOptionsFactory.Build` |
| `InstanceInfo` | `src/NebulaLauncher.Core/Models/Instances.cs:11 InstanceInfo` | `src/NebulaLauncher.Core/Instances/InstanceService.cs:367 InstanceService`；整合包导入 `src/NebulaLauncher.Core/Backups/ModpackService.cs:371 ModpackService` | `src/NebulaLauncher.Core/Instances/InstanceContext.cs:18 InstanceContext`、`src/NebulaLauncher.Core/Backups/ModpackExporter.cs:631 ModpackExporter` |
| `InstallRequest` | `src/NebulaLauncher.Core/Models/Downloads.cs:261 InstallRequest` | 入口 VM（`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:1165 DownloadViewModel.InstallAsync`） | `src/NebulaLauncher.Core/Installing/InstallService.cs:158 InstallService.InstallCoreAsync` |
| `InstallStagePlan` | `src/NebulaLauncher.Core/Models/Downloads.cs:157 InstallStagePlan` | `src/NebulaLauncher.Core/Installing/InstallService.cs:1012 InstallProgressAdapter` | 安装进度条：`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:72 DownloadViewModel` |
| `ContentItem` / `ContentKind` | `src/NebulaLauncher.Core/Models/Content.cs:60 ContentItem`、`src/NebulaLauncher.Core/Models/Content.cs:4 ContentKind` | `src/NebulaLauncher.Core/Mods/ContentService.cs:18 ContentService` | 模组/资源/下载包三页；目录名由 `src/NebulaLauncher.Core/Mods/ContentService.cs:50 ContentService.DirectoryNameFor` 决定 |
| `LaunchOptions` | `src/NebulaLauncher.Core/Models/Launching.cs:6 LaunchOptions` | 唯一合成点 `src/NebulaLauncher.App/Services/LaunchOptionsFactory.cs:41 LaunchOptionsFactory.Build` | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:216 GameLauncher.LaunchAsync` |

`LaunchOptions` 的字段级注释在这一个文件里；不要在页面里手工拼字段——首页曾经漏 `VersionArgumentInfo` 导致实例的 `--versionType` 在单入口静默失效。

## 4. 三条端到端调用链

### 4.1 安装（下载页 → 磁盘上的版本目录）

1. `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:1125 DownloadViewModel` 的命令 `[RelayCommand(CanExecute = nameof(CanInstall))]` → `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:1126 DownloadViewModel.InstallAsync`。
2. 组请求：`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:1165 DownloadViewModel.InstallAsync` 构造 `InstallRequest`，`Source` 取 `_sourceProvider.Effective`，回落设置、再回落镜像。
3. 调 Core：`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs:1183 DownloadViewModel.InstallAsync` → `src/NebulaLauncher.Core/Installing/InstallService.cs:122 InstallService.InstallAsync`（多一个 `fabricApiVersionId`）。
4. 串行化：`src/NebulaLauncher.Core/Installing/InstallService.cs:158 InstallService.InstallCoreAsync` 先 `EnsureInstallEnvironment()`，再 `_paths.EnsureCreated()`，再 `TryBeginTaskScope` 走 `src/NebulaLauncher.Core/Downloads/DownloadQueue.cs:336 DownloadQueue.BeginExternalBatch`（同一时刻只允许一个安装批次）。
5. 分派：`src/NebulaLauncher.Core/Installing/InstallService.cs:192 InstallService.InstallCoreAsync` 的 switch → 原版 `:528`、Fabric/Quilt `:543`、Forge `:704`、NeoForge `:775`。
6. 原版：`src/NebulaLauncher.Core/Installing/InstallService.cs:528 InstallService.InstallVanillaAsync` → `src/NebulaLauncher.Core/Installing/InstallService.cs:842 InstallService.CreateLauncher` → `src/NebulaLauncher.Core/Launching/MinecraftLauncherFactory.cs:27 MinecraftLauncherFactory` 产出的 CmlLib 启动器；`InstallProgressAdapter` 把 CmlLib 事件转成 `InstallStagePlan`。
7. Fabric/Quilt：`src/NebulaLauncher.Core/Installing/InstallService.cs:543 InstallService.InstallFabricLikeAsync` 取 `LoaderMetaUrls.Profile` 候选地址，`node["id"] = versionName` 后写 `<versions>/<name>/<name>.json`，再让 CmlLib 沿 `inheritsFrom` 补齐原版。
8. Forge：`src/NebulaLauncher.Core/Installing/InstallService.cs:704 InstallService.InstallForgeAsync` → `EnterLoaderTempScope` 建临时目录 → `new ForgeInstaller(launcher, client)` + `ForgeInstallOptions{ JavaPath, InstallerOutput, SkipIfAlreadyInstalled = !overwriteExisting }` → `forge.Install(...)`。
9. NeoForge：`src/NebulaLauncher.Core/Installing/InstallService.cs:775 InstallService.InstallNeoForgeAsync` → `src/NebulaLauncher.Core/Installing/NeoForgeInstallerBridge.cs:45 NeoForgeInstallerBridge`（拼命令行跑官方 installer，同样是临时目录）。
10. 覆盖前备份：`src/NebulaLauncher.Core/Installing/InstallService.cs:429 InstallService.BackupBeforeOverwrite` 逐文件留 `.bak`。
11. 收尾一：`src/NebulaLauncher.Core/Installing/InstallService.cs:204 InstallService.InstallCoreAsync` 写加载器标记；`:216` 写安装清单。
12. 收尾二：`src/NebulaLauncher.Core/Installing/InstallService.cs:210 InstallService.TryInstallFabricApiAsync`（仅新建 Fabric 实例、开关开启、且查到兼容版本时）。
13. 收尾三：`src/NebulaLauncher.Core/Installing/InstallService.cs:219 InstallService.InstallCoreAsync` `taskScope?.Complete()`；异常分支 `:234` 走 `CompleteQuietly` 后 `:243` Dispose。
14. 边界：`CmlLib.Core.MinecraftLauncher.InstallAsync` / `ForgeInstaller.Install` / NeoForge installer jar 是外部边界，之后的一切都是本地文件操作。

### 4.2 启动（首页 → 游戏进程）

1. `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:911 HomeViewModel` 的命令 `[RelayCommand(CanExecute = nameof(CanStartLaunch))]` → `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:912 HomeViewModel.LaunchAsync`；代次号 `generation` 自增，用于丢弃过期进度回调。
2. 账户：`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:912 HomeViewModel.LaunchAsync` 内 `EnsureValidActiveAccountAsync`，无有效账户直接失败。
3. 目标目录：`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:986 HomeViewModel.LaunchAsync` 的 `ResolveTargetAsync(version.Name)` 经 `IInstanceContext` 按隔离策略解析（已有实例优先 → 自动建 → 全局）。
4. 合参：`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:994 HomeViewModel.LaunchAsync` → `src/NebulaLauncher.App/Services/LaunchOptionsFactory.cs:41 LaunchOptionsFactory.Build`（四个入口共用，禁止页面自行拼字段）。
5. 进 Core：`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:1001 HomeViewModel.LaunchAsync` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs:216 GameLauncher.LaunchAsync`。
6. 目录与准入：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:226 GameLauncher.LaunchAsync` `ResolveGameDirectory`；随后 `RunningGameInfo.KeyFor` + `IsInstanceRunning` 挡住重复启动。
7. 内存：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:295 GameLauncher.LaunchAsync` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs:2214 GameLauncher.ApplyAutoMemory`。
8. 解析版本：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:297 GameLauncher.LaunchAsync` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs:1307 GameLauncher.ResolveMinecraftVersionAsync`（得出 `MinecraftVersion` 与 `LoaderKind`）。
9. 环境预检：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:302 GameLauncher.LaunchAsync` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs:1341 GameLauncher.RunEnvironmentPreflightAsync`；`:686` 起 `HasBlocker` 即中止。
10. Java：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:317 GameLauncher.LaunchAsync` → `src/NebulaLauncher.Core/Java/JavaService.cs:600 JavaService.ResolveForVersionAsync`；`:340` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs:1382 GameLauncher.CheckJavaPreflight`。
11. authlib：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:354 GameLauncher.LaunchAsync` → `:1233 GameLauncher.ResolveAuthlibArgumentsAsync`；`:186`/`:196` 是纯命令行预览路径，复用同一批辅助方法。
12. 上锁：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:378 GameLauncher.LaunchAsync` → `src/NebulaLauncher.Core/Instances/InstanceLockService.cs:69 InstanceLockService.TryAcquireAsync`（**先拿锁再补文件**，半装状态不会被第二个进程踩）。
13. 造启动器：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:392 GameLauncher.LaunchAsync` → `src/NebulaLauncher.Core/Launching/MinecraftLauncherFactory.cs:27 MinecraftLauncherFactory`；`:941 GameLauncher.BuildMinecraftPath`。
14. 组参数：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:393 GameLauncher.LaunchAsync` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs:960 GameLauncher.BuildLaunchOption`；内部 `:988` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs:1126 GameLauncher.ComposeJvmArguments` → `src/NebulaLauncher.Core/Launching/JvmArgumentBuilder.cs:153 JvmArgumentBuilder.Build`，游戏侧 `:1064 GameLauncher.DeduplicateGameArguments` 去重。
15. 补齐与构建进程：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:416 GameLauncher.LaunchAsync` 调 `InstallAndBuildProcessAsync(options.VersionName, launchOption, fileProgress, byteProgress, ct)`（CmlLib 边界，下载缺失 assets/libraries）。
16. 启动前后置：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:423 GameLauncher.LaunchAsync` → `:2597 GameLauncher.RunLaunchCommandAsync`（启动前命令）；`:440` `ApplyGpuPreference`。
17. 进程与会话：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:442 GameLauncher.LaunchAsync` `new GameWatcher()`，`:460` → `:1845 GameLauncher.PrepareProcess`（输出事件绑在会话上，进程一启动就可能吐日志），`:465` `_running.TryRegister(session)`，`:482` `process.Start()`。
18. 退出：`src/NebulaLauncher.Core/Launching/GameLauncher.cs:603 GameLauncher.LaunchAsync` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs:683 GameLauncher.CompleteExitAsync`（崩溃分析、自动世界备份、游玩时长、释放实例锁）；用户取消等待走 `DetachRunningGame`，返回 `Detached = true`，锁交给后台会话。

### 4.3 内容下载（资源页 → mods/ 目录）

1. 搜索：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:1431 LibraryViewModel.SearchAsync` → `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:2680 LibraryViewModel.SearchCoreAsync`，按来源分派 Modrinth / CurseForge。
2. Modrinth 搜索：`src/NebulaLauncher.Core/Mods/ModrinthService.cs:146 ModrinthService.SearchSortedAsync`；API 基址 `src/NebulaLauncher.Core/Mods/ModrinthService.cs:33 ModrinthService.ApiBase`。
3. CurseForge 搜索：`src/NebulaLauncher.Core/Mods/CurseForgeService.cs:182 CurseForgeService.SearchAsync`；基址 `src/NebulaLauncher.Core/Mods/CurseForgeService.cs:38 CurseForgeService.DefaultApiBase`。
4. 安装入口：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:1488 LibraryViewModel.InstallAsync`。
5. CurseForge 分支：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:2161 LibraryViewModel.InstallCurseForgeAsync` → `src/NebulaLauncher.Core/Mods/CurseForgeService.cs:814 CurseForgeService.InstallAsync`。
6. Modrinth 分支（有依赖）：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:1586 LibraryViewModel.PrepareModrinthPlanAsync` → `src/NebulaLauncher.Core/Mods/ModrinthService.cs:720 ModrinthService.PlanInstallAsync` → `src/NebulaLauncher.Core/Mods/ModrinthInstallPlanner.cs:44 ModrinthInstallPlanner.PlanAsync`（递归展开前置；网络失败降级成 `Warnings`，不整体失败）。
7. 无依赖时直接执行：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:1616 LibraryViewModel.PrepareModrinthPlanAsync`；有依赖先弹确认 `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:1564 LibraryViewModel.ConfirmPlanAsync`。
8. 执行计划：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:1676 LibraryViewModel.RunPlanInstallAsync` → `src/NebulaLauncher.Core/Mods/ModrinthService.cs:761 ModrinthService.InstallPlanAsync`（逐项 `src/NebulaLauncher.Core/Mods/ModrinthService.cs:825 ModrinthService.InstallPlanAsync`）。
9. 单文件落盘：`src/NebulaLauncher.Core/Mods/ModrinthService.cs:542 ModrinthService.InstallAsync` → 目录名 `src/NebulaLauncher.Core/Mods/ContentService.cs:50 ContentService.DirectoryNameFor` → 目标路径 → `partialPath = targetPath + ".part"`（`:599`）→ `OpenDownloadAsync`（`:939`，HttpClient 流式，**不经 DownloadQueue**）→ `File.Move(partialPath, targetPath, overwrite: false)`（`:680`）；异常统一 `TryDelete(partialPath)`。
10. CurseForge 落盘：`src/NebulaLauncher.Core/Mods/CurseForgeService.cs:868 CurseForgeService.InstallAsync` 同一模式（partial + `File.Move`，`:956`）。
11. 外部边界：`api.modrinth.com`、`api.curseforge.com`（及 `mod.mcimirror.top` 镜像重写，见 `src/NebulaLauncher.Core/Downloads/DownloadInfrastructure.cs:996 DownloadHttpClientFactory`）；禁用的内容改后缀 `.disabled`，由 `src/NebulaLauncher.Core/Mods/ContentBatch.cs ContentBatch` 管理。

## 5. 依赖注入

| 问题 | 答案 |
| --- | --- |
| 组合根在哪 | `src/NebulaLauncher.App/App.axaml.cs:273 App.BuildServices`（private static，返回 `ServiceProvider`） |
| 顺序 | 路径 → 日志 → 平台 → 业务模块；注释写在 `src/NebulaLauncher.App/App.axaml.cs:273 App.BuildServices` 上方 |
| 模块分组 | `src/NebulaLauncher.Core/Platforms/CoreAssetsServiceCollectionExtensions.cs:17 CoreAssetsServiceCollectionExtensions.AddCoreAssets` 一个方法聚合下载/Java/安装/内容/备份/服务器七组；`AddCoreAssets` 必须在 `AddCoreModUpdates` 之前，后者的依赖由前者提供 |
| 每模块扩展点 | 各目录一个 `*ServiceCollectionExtensions.cs`；唯一例外是 `src/NebulaLauncher.Core/Backups/WorldIconService.cs:259 WorldIconServiceCollectionExtensions.AddCoreWorldIcons` 放在服务同文件（注册点只有一个，避免与并行改动争同一份扩展文件） |
| 代理型注册 | `src/NebulaLauncher.App/Services/NavigationServiceProxy.cs:13 NavigationServiceProxy` 与 `src/NebulaLauncher.App/Services/WindowActionsProxy.cs:10 WindowActionsProxy` 以单例注册，具体实现等窗口创建后再注入，避免 VM 构造期拿不到窗口 |
| 页面 VM 为何不注册 | `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs:715 MainWindowViewModel.Resolve` 先 `GetService`、未命中就 `ActivatorUtilities.CreateInstance`。注册会让每个页面都往组合根加一行，多协作者必然抢同一个文件；页面注册改在三个 partial（`MainWindowViewModel.HomePages.cs` / `MainWindowViewModel.ContentPages.cs` / `MainWindowViewModel.SystemPages.cs`）+ `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs:708 MainWindowViewModel.RegisterPage` |
| 可选依赖约定 | VM 构造参数全部可空；缺服务时页面降级为演示数据（`IsDemoMode`）而不是抛异常，例如 `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs:155 LibraryViewModel` |

## 6. 落盘对象

| 文件/目录 | 所属模块 | 写方 | 读方 |
| --- | --- | --- | --- |
| `settings.json` | Settings | `src/NebulaLauncher.Core/Settings/SettingsService.cs:121 SettingsService.SaveAsync` | `src/NebulaLauncher.Core/Settings/SettingsService.cs:52 SettingsService.LoadAsync` |
| `accounts.json` | Accounts | `src/NebulaLauncher.Core/Accounts/AccountService.cs:939 AccountService.PersistAsync`（令牌经 `TokenProtector` 加密） | `src/NebulaLauncher.Core/Accounts/AccountService.cs:116 AccountService.LoadCoreAsync` |
| `servers.json` | Servers | `src/NebulaLauncher.Core/Servers/ServerService.cs:466 ServerService.SaveAsync` | `src/NebulaLauncher.Core/Servers/ServerService.cs:54 ServerService.LoadAsync` |
| `servers.dat` | Servers / MC | 游戏本体 | 只读导入 `src/NebulaLauncher.Core/Servers/ServerService.cs:223 ServerService.ImportFromServersDatAsync` |
| `favorites.json` | Versions | `src/NebulaLauncher.Core/Versions/VersionFavorites.cs:172 VersionFavorites.ToggleFavoriteAsync`、`:362 VersionFavorites.SaveCoreAsync` | `src/NebulaLauncher.Core/Versions/VersionFavorites.cs:157 VersionFavorites.LoadAsync` |
| `<versions>/<名>/instance.json` | Instances | `src/NebulaLauncher.Core/Instances/InstanceService.cs:367 InstanceService`、`src/NebulaLauncher.Core/Backups/ModpackService.cs:371 ModpackService` | `src/NebulaLauncher.Core/Instances/InstanceService.cs:92 InstanceService` |
| `.nebula-run.json` | Instances（运行锁） | `src/NebulaLauncher.Core/Instances/InstanceLockService.cs:21 InstanceLockService` | 同上（进程重启后判定残留） |
| `<版本>.json` / `nebula.json` | Versions | `src/NebulaLauncher.Core/Versions/VersionService.cs:25 VersionService` | 版本列表与启动参数合成 |
| `version_manifest_v2.json` | Versions（缓存） | `src/NebulaLauncher.Core/Versions/VersionService.cs:30 VersionService.ManifestCacheFileName` | 版本列表离线兜底 |
| `forge-promotions.json` | Versions（缓存） | `src/NebulaLauncher.Core/Versions/VersionService.cs:34 VersionService.ForgePromotionsCacheFileName` | 加载器版本列表 |
| `server-versions.json` / `Launch Server.bat` | Versions / Servers | `src/NebulaLauncher.Core/Versions/ServerDownloadService.cs:32 ServerDownloadService` | 服务端下载页 |
| `mod-hashes.json` | Mods | `src/NebulaLauncher.Core/Mods/ModFingerprint.cs:26 ModFingerprint.CacheFileName` | 本地内容识别 |
| `mod-projects.json` | Mods（缓存） | `src/NebulaLauncher.Core/Mods/ModProjectInfoService.cs:32 ModProjectInfoService.CacheFileName` | 模组页项目信息 |
| `java-cache.json` | Java | `src/NebulaLauncher.Core/Java/JavaCache.cs:55 JavaCache` | Java 选择 |
| `java-excluded.json` / `java-manual.json` | Java | `src/NebulaLauncher.Core/Java/JavaService.cs:16 JavaService` | 同上 |
| `mods/` `resourcepacks/` `shaderpacks/` `datapacks/` | Mods | 目录名由 `src/NebulaLauncher.Core/Mods/ContentService.cs:50 ContentService.DirectoryNameFor` 决定 | 游戏本体 |
| `logs/` `cache/` `backups/` | Logging / Maintenance / Backups | 路径来自 `src/NebulaLauncher.Core/Paths/LauncherPaths.cs:139 LauncherPaths.LogsDirectory`、`:143 LauncherPaths.BackupsDirectory` | 日志页、存储清理页、备份页 |
| `modrinth.index.json` | Backups（导出整合包） | `src/NebulaLauncher.Core/Backups/ModpackExporter.cs:27 ModpackExporter.IndexFileName` | Modrinth 客户端 / 再次导入 |

`instances/`（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs:137 LauncherPaths.InstancesDirectory`）是旧布局，实例目录已改为 `<游戏目录>/versions/<名>/`，不要再往这里写。

## 7. 与 docs/research/code-map.md 的已知差异

| 差异 | 实际 |
| --- | --- |
| 目录表只列 21 行，正文却写「22 个目录」 | 缺的一行是 src/NebulaLauncher.Core/Resources（只放嵌入的 `WikiEntries.txt`，无 cs 代码） |
| 契约接口数写作 36 | 实际 37 个 `interface I*`，多的是 `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs:160 IInstanceLockHandle` |
| 用 `LoaderMetaFetcher.cs:17/18/21-22/30` 指代加载器 meta 地址 | 文件名是旧称，文件里唯一的类型是 `src/NebulaLauncher.Core/Downloads/LoaderMetaFetcher.cs:15 LoaderMetaUrls`；:17/:18/:21-22 是它与 Forge promotions 的常量 |
| 组合根行号（273） | 一致；其余注册行号随合并漂移，优先按方法名搜索 |
