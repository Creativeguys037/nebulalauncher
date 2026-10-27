# CODE.md — 代码组成、数据结构与调用

面向 agent 的定位文档：先查表找到目录/类型，再顺着调用链读代码。行号是当前提交的提示，重命名后以类型名为准。

读法建议：新功能 → §1 找目录 → §2 找契约接口 → §4 找同类调用链 → §5 确认 DI 注册点 → §6 确认落盘位置。

## 1. 目录表

### 1.1 src/NebulaLauncher.Core（24 个目录）

| 目录 | 职责（一句话） | 关键类型 |
| --- | --- | --- |
| Abstractions | 全部契约接口与共享记录类型，只依赖 BCL，Core 各模块靠它解耦 | `src/NebulaLauncher.Core/Abstractions/Infrastructure.cs ILauncherPaths（:4）`、`src/NebulaLauncher.Core/Abstractions/VersionServices.cs IInstallService（:51）`、`src/NebulaLauncher.Core/Abstractions/InstanceServices.cs IInstanceContext（:258）` |
| Accounts | 微软设备码登录、authlib 登录、令牌加密存储与刷新 | `src/NebulaLauncher.Core/Accounts/AccountService.cs AccountService（:14）`、`src/NebulaLauncher.Core/Accounts/MicrosoftDeviceCodeAuth.cs MicrosoftDeviceCodeAuth（:45）`、`src/NebulaLauncher.Core/Accounts/TokenProtector.cs TokenProtector（:12）` |
| Backups | 世界备份、整合包导入导出、实例打包与头像图标 | `src/NebulaLauncher.Core/Backups/BackupService.cs BackupService（:12）`、`src/NebulaLauncher.Core/Backups/ModpackService.cs ModpackService（:25）`、`src/NebulaLauncher.Core/Backups/WorldIconService.cs WorldIconService（:20）` |
| Diagnostics | 一次性环境快照（OS/JVM/内存/磁盘）与发布期 JSON 落盘自检 | `src/NebulaLauncher.Core/Diagnostics/DiagnosticsSnapshot.cs DiagnosticsSnapshot（:73）`、`src/NebulaLauncher.Core/Diagnostics/JsonSelfCheck.cs JsonSelfCheck.RunAsync（:32）` |
| Downloads | 下载队列（并发/限速/暂停）、源探测与治理、HTTP 客户端工厂、文件复用 | `src/NebulaLauncher.Core/Downloads/DownloadQueue.cs DownloadQueue（:120）`、`src/NebulaLauncher.Core/Downloads/DownloadSourceGovernor.cs DownloadSourceGovernor（:31）`、`src/NebulaLauncher.Core/Downloads/DownloadInfrastructure.cs DownloadHttpClientFactory（:996）` |
| Help | 内置离线帮助条目，不读网络、不读文件 | `src/NebulaLauncher.Core/Help/HelpCatalog.cs HelpCatalog（:20）` |
| Installing | 原版/Fabric/Quilt/Forge/NeoForge 安装、修复、安装清单写入 | `src/NebulaLauncher.Core/Installing/InstallService.cs InstallService（:22）`、`src/NebulaLauncher.Core/Installing/NeoForgeInstallerBridge.cs NeoForgeInstallerBridge（:45）`、`src/NebulaLauncher.Core/Installing/ComponentInspector.cs ComponentInspector（:16）` |
| Instances | 实例目录定位、隔离策略、元数据、运行锁、回收站 | `src/NebulaLauncher.Core/Instances/InstanceService.cs InstanceService（:23）`、`src/NebulaLauncher.Core/Instances/InstanceContext.cs InstanceContext（:18）`、`src/NebulaLauncher.Core/Instances/InstanceLockService.cs InstanceLockService（:21）` |
| Java | JVM 探测/下载/校验、Mojang runtime 清单、Java 选择缓存 | `src/NebulaLauncher.Core/Java/JavaService.cs JavaService（:16）`、`src/NebulaLauncher.Core/Java/MojangRuntimeCatalog.cs MojangRuntimeCatalog（:34）`、`src/NebulaLauncher.Core/Java/JavaCache.cs JavaCache（:55）` |
| Launching | 启动参数合成、游戏进程与运行锁、预检、崩溃分析、自动世界备份 | `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher（:24）`、`src/NebulaLauncher.Core/Launching/JvmArgumentBuilder.cs JvmArgumentBuilder（:39）`、`src/NebulaLauncher.Core/Launching/PreflightChecker.cs PreflightCheckerService（:651）` |
| Logging | 文件日志写入、日志读取与实时跟踪、崩溃包导出 | `src/NebulaLauncher.Core/Logging/FileLogger.cs FileLogger（:11）`、`src/NebulaLauncher.Core/Logging/LauncherLogReader.cs LauncherLogReader（:22）`、`src/NebulaLauncher.Core/Logging/LogFollower.cs LogFollower（:24）` |
| Maintenance | 存储清理（缓存/日志/备份体积统计与删除） | `src/NebulaLauncher.Core/Maintenance/StorageService.cs StorageService（:26）` |
| Models | 跨模块 DTO：设置、实例、下载、内容、启动、账号、版本 | `src/NebulaLauncher.Core/Models/Settings.cs AppSettings（:43）`、`src/NebulaLauncher.Core/Models/Downloads.cs InstallRequest（:261）`、`src/NebulaLauncher.Core/Models/Launching.cs LaunchOptions（:6）` |
| Mods | 内容搜索、指纹识别、更新检查、Modrinth/CurseForge 客户端、依赖解析 | `src/NebulaLauncher.Core/Mods/ContentService.cs ContentService（:18）`、`src/NebulaLauncher.Core/Mods/ModrinthService.cs ModrinthService（:30）`、`src/NebulaLauncher.Core/Mods/ModrinthInstallPlanner.cs ModrinthInstallPlanner（:20）` |
| Net | 网络可达性探测、代理配置与代理凭据 | `src/NebulaLauncher.Core/Net/NetworkProbe.cs NetworkProbe（:60）`、`src/NebulaLauncher.Core/Net/ProxyConfigurator.cs ProxyConfigurator（:19）`、`src/NebulaLauncher.Core/Net/ProxyCredentialStore.cs ProxyCredentialStore（:14）` |
| Paths | 启动器数据目录/游戏目录/各类文件路径的唯一解析点 | `src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths（:19）`、`src/NebulaLauncher.Core/Paths/PathProbe.cs PathProbe（:11）`、`src/NebulaLauncher.Core/Paths/PathSafety.cs PathSafety（:22）` |
| Platforms | 平台服务：打开目录/浏览器、系统信息、原生集成 | `src/NebulaLauncher.Core/Platforms/PlatformService.cs PlatformService（:46）`、`src/NebulaLauncher.Core/Platforms/PlatformNative.cs PlatformNative（:11）` |
| Resources | 嵌入资源，无 cs 代码；`WikiEntries.txt` 是 MC 百科译名表 | `src/NebulaLauncher.Core/Mods/ModNameTable.cs ModNameTable.ResourceName（:204）` |
| Serialization | 共享的 JSON 序列化辅助：从 `JsonSerializerOptions` 取 `JsonTypeInfo<T>`、`JsonNode` 写出 | `src/NebulaLauncher.Core/Serialization/JsonMeta.cs JsonMeta.Of（:30）`、`src/NebulaLauncher.Core/Serialization/JsonNodeWrite.cs JsonNodeWrite（:18）` |
| Servers | 服务器列表落盘、`servers.dat` 导入、状态查询 | `src/NebulaLauncher.Core/Servers/ServerService.cs ServerService（:15）`、`src/NebulaLauncher.Core/Servers/ServerQueryService.cs ServerQueryService（:30）`、`src/NebulaLauncher.Core/Servers/ServerListDatReader.cs ServerListDatReader（:22）` |
| Settings | 设置读写（`settings.json`），变更事件 | `src/NebulaLauncher.Core/Settings/SettingsService.cs SettingsService（:18）` |
| Skinning | 本地皮肤库：`<data>/skins/` 落盘、PNG 尺寸校验、索引读写 | `src/NebulaLauncher.Core/Skinning/LocalSkinStore.cs LocalSkinStore（:158）`、`src/NebulaLauncher.Core/Skinning/LocalSkinStore.cs LocalSkinFormat.Validate（:67）` |
| Transfer | 配置包导入导出（跨机器迁移） | `src/NebulaLauncher.Core/Transfer/ProfileBundleService.cs ProfileBundleService（:212）` |
| Versions | 版本清单、安装记录、收藏与隐藏、服务端下载 | `src/NebulaLauncher.Core/Versions/VersionService.cs VersionService（:25）`、`src/NebulaLauncher.Core/Versions/VersionFavorites.cs VersionFavorites（:83）`、`src/NebulaLauncher.Core/Versions/ServerDownloadService.cs ServerDownloadService（:32）` |

### 1.2 src/NebulaLauncher.App（ViewModels / Views / Services / Controls）

| 目录 | 职责（一句话） | 关键类型 |
| --- | --- | --- |
| ViewModels | 页面状态与命令；`MainWindowViewModel` 是导航与页面工厂的根 | `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.RegisterPages（:670）`、`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel.LaunchAsync（:912）` |
| Views | Avalonia 视图，与 ViewModels 一一对应 | `src/NebulaLauncher.App/Views/MainWindow.axaml.cs MainWindow（:21）` |
| Services | App 层服务：导航/窗口动作代理、启动参数合成、外壳进度 | `src/NebulaLauncher.App/Services/LaunchOptionsFactory.cs LaunchOptionsFactory.Build（:41）`、`src/NebulaLauncher.App/Services/NavigationServiceProxy.cs NavigationServiceProxy（:13）` |
| Controls | 自定义控件（页面宿主、极光背景、折叠容器、环形进度、3D 皮肤模型） | `src/NebulaLauncher.App/Controls/PageHost.cs PageHost（:19）`、`src/NebulaLauncher.App/Controls/AuroraBackground.cs AuroraBackground（:22）`、`src/NebulaLauncher.App/Controls/SkinModelView.cs SkinModelView` |
| Skinning | 皮肤客户端与渲染管线：皮肤站浏览、皮肤站换肤、官方换肤、profile 解析、软光栅 | `src/NebulaLauncher.App/Skinning/SkinLibraryClient.cs SkinLibraryClient`、`src/NebulaLauncher.App/Skinning/AuthlibSkinClient.cs AuthlibSkinClient`、`src/NebulaLauncher.App/Skinning/MojangSkinClient.cs MojangSkinClient` |

其余子目录（Converters / Themes / Assets / Properties）只放转换器、样式令牌与静态资源，不含业务逻辑。

## 2. 接口契约表

接口分三处声明，合计 41 个 `interface I*`：`src/NebulaLauncher.Core/Abstractions` 下 37 个（含 `IInstanceLockHandle`）；Core 模块内 2 个——`src/NebulaLauncher.Core/Downloads/DownloadQueue.cs IDownloadTaskScope（:85）` 与 `src/NebulaLauncher.Core/Platforms/PlatformNative.cs ITaskbarList3（:113）`；App 层 2 个——`src/NebulaLauncher.App/Services/INavigationService.cs INavigationService（:10）` 与 `src/NebulaLauncher.App/Services/IWindowActions.cs IWindowActions（:10）`，后两者由代理类实现、在 App 组合根单例注册，不进 Core 契约。消费方以页面 ViewModel 的构造签名为证。

| 接口 | 实现类 | 消费方 |
| --- | --- | --- |
| `src/NebulaLauncher.Core/Abstractions/AccountServices.cs ISettingsService（:4）` | `src/NebulaLauncher.Core/Settings/SettingsService.cs SettingsService（:18）` | 全部页面 VM；`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:91）` |
| `src/NebulaLauncher.Core/Abstractions/AccountServices.cs IAccountService（:28）` | `src/NebulaLauncher.Core/Accounts/AccountService.cs AccountService（:14）` | `src/NebulaLauncher.App/ViewModels/AccountsViewModel.cs AccountsViewModel（:31）`、`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:91）` |
| `src/NebulaLauncher.Core/Abstractions/AuthlibServices.cs IAuthlibInjectorService（:10）` | `src/NebulaLauncher.Core/Launching/AuthlibInjectorService.cs AuthlibInjectorService（:21）` | `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:216）`（内部） |
| `src/NebulaLauncher.Core/Abstractions/ComponentServices.cs IComponentInspector（:12）` | `src/NebulaLauncher.Core/Installing/ComponentInspector.cs ComponentInspector（:16）` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs VersionsViewModel（:65）` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs IContentService（:71）` | `src/NebulaLauncher.Core/Mods/ContentService.cs ContentService（:18）` | `src/NebulaLauncher.App/ViewModels/ModsViewModel.cs ModsViewModel（:99）`、`src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs WorldsViewModel（:48）` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs IModpackService（:140）` | `src/NebulaLauncher.Core/Backups/ModpackService.cs ModpackService（:25）` | `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel（:132）`、`src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs WorldsViewModel（:48）` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs IBackupService（:189）` | `src/NebulaLauncher.Core/Backups/BackupService.cs BackupService（:12）` | `src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs WorldsViewModel（:48）` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs IWorldIconService（:250）` | `src/NebulaLauncher.Core/Backups/WorldIconService.cs WorldIconService（:20）` | `src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs WorldsViewModel（:48）` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs IModrinthService（:267）` | `src/NebulaLauncher.Core/Mods/ModrinthService.cs ModrinthService（:30）` | `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel（:132）`、`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel（:72）` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs IModUpdateService（:442）` | `src/NebulaLauncher.Core/Mods/ModUpdateService.cs ModUpdateService（:23）` | `src/NebulaLauncher.App/ViewModels/ModsViewModel.cs ModsViewModel（:99）` |
| `src/NebulaLauncher.Core/Abstractions/ContentServices.cs IServerService（:486）` | `src/NebulaLauncher.Core/Servers/ServerService.cs ServerService（:15）` | `src/NebulaLauncher.App/ViewModels/ServersViewModel.cs ServersViewModel（:43）` |
| `src/NebulaLauncher.Core/Abstractions/CrashExportServices.cs ICrashExportService（:65）` | `src/NebulaLauncher.Core/Launching/CrashExportService.cs CrashExportService（:22）` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:91）` |
| `src/NebulaLauncher.Core/Abstractions/CurseForgeServices.cs ICurseForgeService（:17）` | `src/NebulaLauncher.Core/Mods/CurseForgeService.cs CurseForgeService（:27）` | `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel（:132）` |
| `src/NebulaLauncher.Core/Abstractions/DownloadSourceServices.cs IDownloadSourceProvider（:12）` | `src/NebulaLauncher.Core/Downloads/DownloadSourceGovernor.cs DownloadSourceGovernor（:31）` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel（:72）` |
| `src/NebulaLauncher.Core/Abstractions/DownloadSourceServices.cs IDownloadSourceTelemetry（:38）` | `src/NebulaLauncher.Core/Downloads/DownloadSourceGovernor.cs DownloadSourceGovernor（:31）` | `src/NebulaLauncher.Core/Downloads/DownloadSourceProbe.cs DownloadSourceProbe（:209）` |
| `src/NebulaLauncher.Core/Abstractions/DownloadSourceServices.cs IDownloadSourceProbe（:165）` | `src/NebulaLauncher.Core/Downloads/DownloadSourceProbe.cs DownloadSourceProbe（:209）` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel（:72）` |
| `src/NebulaLauncher.Core/Abstractions/FavoriteServices.cs IVersionFavorites（:10）` | `src/NebulaLauncher.Core/Versions/VersionFavorites.cs VersionFavorites（:83）` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs VersionsViewModel（:65）` |
| `src/NebulaLauncher.Core/Abstractions/GameServices.cs IGameLauncher（:4）` | `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher（:24）` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:91）`、`src/NebulaLauncher.App/ViewModels/ServersViewModel.cs ServersViewModel（:43）` |
| `src/NebulaLauncher.Core/Abstractions/GameServices.cs IPreflightChecker（:142）` | `src/NebulaLauncher.Core/Launching/PreflightChecker.cs PreflightCheckerService（:651）` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs VersionsViewModel（:65）` |
| `src/NebulaLauncher.Core/Abstractions/GameServices.cs IJavaService（:165）` | `src/NebulaLauncher.Core/Java/JavaService.cs JavaService（:16）` | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel（:68）`、`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:91）` |
| `src/NebulaLauncher.Core/Abstractions/Infrastructure.cs ILauncherPaths（:4）` | `src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths（:19）` | 全部页面 VM 与绝大多数 Core 模块 |
| `src/NebulaLauncher.Core/Abstractions/Infrastructure.cs IPlatformService（:45）` | `src/NebulaLauncher.Core/Platforms/PlatformService.cs PlatformService（:46）` | 全部页面 VM |
| `src/NebulaLauncher.Core/Abstractions/Infrastructure.cs IAppLog（:142）` | `src/NebulaLauncher.Core/Logging/FileLogger.cs FileLogger（:11）` | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel（:68）`、Core 各服务内部 |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs IInstanceService（:4）` | `src/NebulaLauncher.Core/Instances/InstanceService.cs InstanceService（:23）` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs VersionsViewModel（:65）`、`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:91）` |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs IInstanceLockService（:124）` | `src/NebulaLauncher.Core/Instances/InstanceLockService.cs InstanceLockService（:21）` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs VersionsViewModel（:65）`、`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:378）` |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs IInstanceLockHandle（:160）` | 由 `src/NebulaLauncher.Core/Instances/InstanceLockService.cs InstanceLockService.TryAcquireAsync（:69）` 返回 | `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:378）`（释放即解锁） |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs IInstanceFolderService（:233）` | `src/NebulaLauncher.Core/Instances/InstanceFolders.cs InstanceFolderService（:166）` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs VersionsViewModel（:65）` |
| `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs IInstanceContext（:258）` | `src/NebulaLauncher.Core/Instances/InstanceContext.cs InstanceContext（:18）` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:91）`、`src/NebulaLauncher.App/ViewModels/ModsViewModel.cs ModsViewModel（:99）` |
| `src/NebulaLauncher.Core/Abstractions/LogServices.cs ILauncherLogReader（:94）` | `src/NebulaLauncher.Core/Logging/LauncherLogReader.cs LauncherLogReader（:22）` | `src/NebulaLauncher.App/ViewModels/LogsViewModel.cs LogsViewModel（:83）` |
| `src/NebulaLauncher.Core/Abstractions/LogServices.cs ILogFollowService（:169）` | `src/NebulaLauncher.Core/Logging/LogFollower.cs LogFollower（:24）` | `src/NebulaLauncher.App/ViewModels/LogsViewModel.cs LogsViewModel（:83）` |
| `src/NebulaLauncher.Core/Abstractions/ModProjectServices.cs IModProjectInfoService（:15）` | `src/NebulaLauncher.Core/Mods/ModProjectInfoService.cs ModProjectInfoService（:29）` | `src/NebulaLauncher.App/ViewModels/ModsViewModel.cs ModsViewModel（:99）` |
| `src/NebulaLauncher.Core/Abstractions/ServerServices.cs IServerDownloadService（:10）` | `src/NebulaLauncher.Core/Versions/ServerDownloadService.cs ServerDownloadService（:32）` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel（:72）` |
| `src/NebulaLauncher.Core/Abstractions/ServerServices.cs IServerQueryService（:189）` | `src/NebulaLauncher.Core/Servers/ServerQueryService.cs ServerQueryService（:30）` | `src/NebulaLauncher.App/ViewModels/ServersViewModel.cs ServersViewModel（:43）` |
| `src/NebulaLauncher.Core/Abstractions/StorageServices.cs IStorageService（:18）` | `src/NebulaLauncher.Core/Maintenance/StorageService.cs StorageService（:26）` | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel（:68）` |
| `src/NebulaLauncher.Core/Abstractions/TransferServices.cs IProfileBundleService（:13）` | `src/NebulaLauncher.Core/Transfer/ProfileBundleService.cs ProfileBundleService（:212）` | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel（:68）` |
| `src/NebulaLauncher.Core/Abstractions/VersionServices.cs IVersionService（:4）` | `src/NebulaLauncher.Core/Versions/VersionService.cs VersionService（:25）` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:91）`、`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel（:72）` |
| `src/NebulaLauncher.Core/Abstractions/VersionServices.cs IInstallService（:51）` | `src/NebulaLauncher.Core/Installing/InstallService.cs InstallService（:22）` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel（:72）`、`src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs VersionsViewModel（:65）` |
| `src/NebulaLauncher.Core/Downloads/DownloadQueue.cs IDownloadTaskScope（:85）` | `src/NebulaLauncher.Core/Downloads/DownloadQueue.cs ExternalTaskScope（:1376）` | `src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.TryBeginTaskScope（:254）`、`src/NebulaLauncher.Core/Installing/NebulaGameInstaller.cs NebulaGameInstaller（:41）` |
| `src/NebulaLauncher.Core/Platforms/PlatformNative.cs ITaskbarList3（:113）` | 由 COM 类型创建：`src/NebulaLauncher.Core/Platforms/PlatformNative.cs PlatformNative.CreateTaskbarList（:132）` | `src/NebulaLauncher.Core/Platforms/PlatformService.cs PlatformService.EnsureTaskbar（:574）` |
| `src/NebulaLauncher.App/Services/INavigationService.cs INavigationService（:10）` | `src/NebulaLauncher.App/Services/NavigationServiceProxy.cs NavigationServiceProxy（:13）` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel（:76）` 等各页面 VM |
| `src/NebulaLauncher.App/Services/IWindowActions.cs IWindowActions（:10）` | `src/NebulaLauncher.App/Services/WindowActionsProxy.cs WindowActionsProxy（:10）` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:103）` |

`IInstanceLockHandle` 是运行锁句柄（`IAsyncDisposable`），不是服务；容器不注册它。

## 3. 跨模块数据结构

只列被两处以上共享、且改动会波及多模块的少数类型。

| 类型 | 定义 | 谁写 | 谁读 |
| --- | --- | --- | --- |
| `AppSettings` | `src/NebulaLauncher.Core/Models/Settings.cs AppSettings（:43）` | `src/NebulaLauncher.Core/Settings/SettingsService.cs SettingsService.SaveAsync（:121）` | 所有 VM 经 `ISettingsService.Current`；`src/NebulaLauncher.App/Services/LaunchOptionsFactory.cs LaunchOptionsFactory.Build（:41）` |
| `InstanceInfo` | `src/NebulaLauncher.Core/Models/Instances.cs InstanceInfo（:11）` | `src/NebulaLauncher.Core/Instances/InstanceService.cs InstanceService（:367）`；整合包导入 `src/NebulaLauncher.Core/Backups/ModpackService.cs ModpackService（:371）` | `src/NebulaLauncher.Core/Instances/InstanceContext.cs InstanceContext（:18）`、`src/NebulaLauncher.Core/Backups/ModpackExporter.cs ModpackExporter（:631）` |
| `InstallRequest` | `src/NebulaLauncher.Core/Models/Downloads.cs InstallRequest（:261）` | 入口 VM（`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel.InstallAsync（:1165）`） | `src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallCoreAsync（:158）` |
| `InstallStagePlan` | `src/NebulaLauncher.Core/Models/Downloads.cs InstallStagePlan（:157）` | `src/NebulaLauncher.Core/Installing/InstallService.cs InstallProgressAdapter（:1012）` | 安装进度条：`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel（:72）` |
| `ContentItem` / `ContentKind` | `src/NebulaLauncher.Core/Models/Content.cs ContentItem（:60）`、`src/NebulaLauncher.Core/Models/Content.cs ContentKind（:4）` | `src/NebulaLauncher.Core/Mods/ContentService.cs ContentService（:18）` | 模组/资源/下载包三页；目录名由 `src/NebulaLauncher.Core/Mods/ContentService.cs ContentService.DirectoryNameFor（:50）` 决定 |
| `LaunchOptions` | `src/NebulaLauncher.Core/Models/Launching.cs LaunchOptions（:6）` | 唯一合成点 `src/NebulaLauncher.App/Services/LaunchOptionsFactory.cs LaunchOptionsFactory.Build（:41）` | `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:216）` |

`LaunchOptions` 的字段级注释在这一个文件里；不要在页面里手工拼字段——首页曾经漏 `VersionArgumentInfo` 导致实例的 `--versionType` 在单入口静默失效。

## 4. 三条端到端调用链

### 4.1 安装（下载页 → 磁盘上的版本目录）

1. `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel（:1125）` 的命令 `[RelayCommand(CanExecute = nameof(CanInstall))]` → `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel.InstallAsync（:1126）`。
2. 组请求：`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel.InstallAsync（:1165）` 构造 `InstallRequest`，`Source` 取 `_sourceProvider.Effective`，回落设置、再回落镜像。
3. 调 Core：`src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel.InstallAsync（:1183）` → `src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallAsync（:122）`（多一个 `fabricApiVersionId`）。
4. 串行化：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallCoreAsync（:158）` 先 `EnsureInstallEnvironment()`，再 `_paths.EnsureCreated()`，再 `TryBeginTaskScope` 走 `src/NebulaLauncher.Core/Downloads/DownloadQueue.cs DownloadQueue.BeginExternalBatch（:336）`（同一时刻只允许一个安装批次）。
5. 分派：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallCoreAsync（:192）` 的 switch → 原版 `:528`、Fabric/Quilt `:543`、Forge `:704`、NeoForge `:775`。
6. 原版：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallVanillaAsync（:528）` → `src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.CreateLauncher（:842）` → `src/NebulaLauncher.Core/Launching/MinecraftLauncherFactory.cs MinecraftLauncherFactory（:27）` 产出的 CmlLib 启动器；`InstallProgressAdapter` 把 CmlLib 事件转成 `InstallStagePlan`。
7. Fabric/Quilt：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallFabricLikeAsync（:543）` 取 `LoaderMetaUrls.Profile` 候选地址，`node["id"] = versionName` 后写 `<versions>/<name>/<name>.json`，再让 CmlLib 沿 `inheritsFrom` 补齐原版。
8. Forge：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallForgeAsync（:704）` → `EnterLoaderTempScope` 建临时目录 → `new ForgeInstaller(launcher, client)` + `ForgeInstallOptions{ JavaPath, InstallerOutput, SkipIfAlreadyInstalled = !overwriteExisting }` → `forge.Install(...)`。
9. NeoForge：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallNeoForgeAsync（:775）` → `src/NebulaLauncher.Core/Installing/NeoForgeInstallerBridge.cs NeoForgeInstallerBridge（:45）`（拼命令行跑官方 installer，同样是临时目录）。
10. 覆盖前备份：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.BackupBeforeOverwrite（:429）` 逐文件留 `.bak`。
11. 收尾一：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallCoreAsync（:204）` 写加载器标记；`:216` 写安装清单。
12. 收尾二：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.TryInstallFabricApiAsync（:210）`（仅新建 Fabric 实例、开关开启、且查到兼容版本时）。
13. 收尾三：`src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallCoreAsync（:219）` `taskScope?.Complete()`；异常分支 `:234` 走 `CompleteQuietly` 后 `:243` Dispose。
14. 边界：`CmlLib.Core.MinecraftLauncher.InstallAsync` / `ForgeInstaller.Install` / NeoForge installer jar 是外部边界，之后的一切都是本地文件操作。

### 4.2 启动（首页 → 游戏进程）

1. `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel（:911）` 的命令 `[RelayCommand(CanExecute = nameof(CanStartLaunch))]` → `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel.LaunchAsync（:912）`；代次号 `generation` 自增，用于丢弃过期进度回调。
2. 账户：`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel.LaunchAsync（:912）` 内 `EnsureValidActiveAccountAsync`，无有效账户直接失败。
3. 目标目录：`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel.LaunchAsync（:986）` 的 `ResolveTargetAsync(version.Name)` 经 `IInstanceContext` 按隔离策略解析（已有实例优先 → 自动建 → 全局）。
4. 合参：`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel.LaunchAsync（:994）` → `src/NebulaLauncher.App/Services/LaunchOptionsFactory.cs LaunchOptionsFactory.Build（:41）`（四个入口共用，禁止页面自行拼字段）。
5. 进 Core：`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel.LaunchAsync（:1001）` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:216）`。
6. 目录与准入：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:226）` `ResolveGameDirectory`；随后 `RunningGameInfo.KeyFor` + `IsInstanceRunning` 挡住重复启动。
7. 内存：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:295）` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.ApplyAutoMemory（:2214）`。
8. 解析版本：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:297）` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.ResolveMinecraftVersionAsync（:1307）`（得出 `MinecraftVersion` 与 `LoaderKind`）。
9. 环境预检：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:302）` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.RunEnvironmentPreflightAsync（:1341）`；`:686` 起 `HasBlocker` 即中止。
10. Java：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:317）` → `src/NebulaLauncher.Core/Java/JavaService.cs JavaService.ResolveForVersionAsync（:600）`；`:340` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.CheckJavaPreflight（:1382）`。
11. authlib：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:354）` → `:1233 GameLauncher.ResolveAuthlibArgumentsAsync`；`:186`/`:196` 是纯命令行预览路径，复用同一批辅助方法。
12. 上锁：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:378）` → `src/NebulaLauncher.Core/Instances/InstanceLockService.cs InstanceLockService.TryAcquireAsync（:69）`（**先拿锁再补文件**，半装状态不会被第二个进程踩）。
13. 造启动器：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:392）` → `src/NebulaLauncher.Core/Launching/MinecraftLauncherFactory.cs MinecraftLauncherFactory（:27）`；`:941 GameLauncher.BuildMinecraftPath`。
14. 组参数：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:393）` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.BuildLaunchOption（:960）`；内部 `:988` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.ComposeJvmArguments（:1126）` → `src/NebulaLauncher.Core/Launching/JvmArgumentBuilder.cs JvmArgumentBuilder.Build（:153）`，游戏侧 `:1064 GameLauncher.DeduplicateGameArguments` 去重。
15. 补齐与构建进程：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:416）` 调 `InstallAndBuildProcessAsync(options.VersionName, launchOption, fileProgress, byteProgress, ct)`（CmlLib 边界，下载缺失 assets/libraries）。
16. 启动前后置：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:423）` → `:2597 GameLauncher.RunLaunchCommandAsync`（启动前命令）；`:440` `ApplyGpuPreference`。
17. 进程与会话：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:442）` `new GameWatcher()`，`:460` → `:1845 GameLauncher.PrepareProcess`（输出事件绑在会话上，进程一启动就可能吐日志），`:465` `_running.TryRegister(session)`，`:482` `process.Start()`。
18. 退出：`src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync（:603）` → `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.CompleteExitAsync（:683）`（崩溃分析、自动世界备份、游玩时长、释放实例锁）；用户取消等待走 `DetachRunningGame`，返回 `Detached = true`，锁交给后台会话。

### 4.3 内容下载（资源页 → mods/ 目录）

1. 搜索：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel.SearchAsync（:1431）` → `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel.SearchCoreAsync（:2680）`，按来源分派 Modrinth / CurseForge。
2. Modrinth 搜索：`src/NebulaLauncher.Core/Mods/ModrinthService.cs ModrinthService.SearchSortedAsync（:146）`；API 基址 `src/NebulaLauncher.Core/Mods/ModrinthService.cs ModrinthService.ApiBase（:33）`。
3. CurseForge 搜索：`src/NebulaLauncher.Core/Mods/CurseForgeService.cs CurseForgeService.SearchAsync（:182）`；基址 `src/NebulaLauncher.Core/Mods/CurseForgeService.cs CurseForgeService.DefaultApiBase（:38）`。
4. 安装入口：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel.InstallAsync（:1488）`。
5. CurseForge 分支：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel.InstallCurseForgeAsync（:2161）` → `src/NebulaLauncher.Core/Mods/CurseForgeService.cs CurseForgeService.InstallAsync（:814）`。
6. Modrinth 分支（有依赖）：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel.PrepareModrinthPlanAsync（:1586）` → `src/NebulaLauncher.Core/Mods/ModrinthService.cs ModrinthService.PlanInstallAsync（:720）` → `src/NebulaLauncher.Core/Mods/ModrinthInstallPlanner.cs ModrinthInstallPlanner.PlanAsync（:44）`（递归展开前置；网络失败降级成 `Warnings`，不整体失败）。
7. 无依赖时直接执行：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel.PrepareModrinthPlanAsync（:1616）`；有依赖先弹确认 `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel.ConfirmPlanAsync（:1564）`。
8. 执行计划：`src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel.RunPlanInstallAsync（:1676）` → `src/NebulaLauncher.Core/Mods/ModrinthService.cs ModrinthService.InstallPlanAsync（:761）`（逐项 `src/NebulaLauncher.Core/Mods/ModrinthService.cs ModrinthService.InstallPlanAsync（:825）`）。
9. 单文件落盘：`src/NebulaLauncher.Core/Mods/ModrinthService.cs ModrinthService.InstallAsync（:542）` → 目录名 `src/NebulaLauncher.Core/Mods/ContentService.cs ContentService.DirectoryNameFor（:50）` → 目标路径 → `partialPath = targetPath + ".part"`（`:599`）→ `OpenDownloadAsync`（`:939`，HttpClient 流式，**不经 DownloadQueue**）→ `File.Move(partialPath, targetPath, overwrite: false)`（`:680`）；异常统一 `TryDelete(partialPath)`。
10. CurseForge 落盘：`src/NebulaLauncher.Core/Mods/CurseForgeService.cs CurseForgeService.InstallAsync（:868）` 同一模式（partial + `File.Move`，`:956`）。
11. 外部边界：`api.modrinth.com`、`api.curseforge.com`（及 `mod.mcimirror.top` 镜像重写，见 `src/NebulaLauncher.Core/Downloads/DownloadInfrastructure.cs DownloadHttpClientFactory（:996）`）；禁用的内容改后缀 `.disabled`，由 `src/NebulaLauncher.Core/Mods/ContentBatch.cs ContentBatch` 管理。

### 4.4 侧边栏 3D 皮肤卡（账户 → 像素）

1. 壳层创建卡片 VM：`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel（:64）` → `InitializeSkinCardAsync`（丢弃任务前已就地 try/catch 落日志，见 PITFALLS 2.8）；关闭时 `ReleaseSkinCard` 拆订阅。
2. VM 跟随账户：`src/NebulaLauncher.App/ViewModels/SkinCardViewModel.cs SkinCardViewModel.InitializeAsync`，订阅 `IAccountService.AccountsChanged`，每次变更 `ApplyAsync`（用 `Interlocked.Exchange` 取消上一次在途请求）。
3. 取皮肤地址：`src/NebulaLauncher.App/Skinning/SkinProfileParser.cs`。`BuildProfileUrl` 由 `Account.Uuid` 与 `AuthServerUrl` 拼会话服务器地址（第三方把 `/yggdrasil` 换成 `/sessionserver`，官方走 `sessionserver.mojang.com`）；`ParseProfile` 解 `properties[]` 里 `name=textures` 的 base64 载荷，得到 `SKIN.url` / `CAPE.url` / `metadata.model`。
4. 下载与解码：`src/NebulaLauncher.App/Skinning/SkinTextureLoader.cs SkinTextureLoader.LoadAsync` → `DownloadTextureAsync` → `DecodePng`（`WriteableBitmap.Decode` + `Lock` + `Marshal.Copy`，只认 `Bgra8888` / `Rgba8888`，统一成 `0xAARRGGBB` 数组）。任何一步失败都退 `SkinVisual.Default` 并 `IAppLog.Warn`，**卡片永不空白**。
5. 几何：`src/NebulaLauncher.App/Skinning/SkinMeshBuilder.cs Build` 按 MC 像素建 6 个部件（Steve 臂宽 4 / Alex 3，脚底 y=0 头顶 y=32），有皮肤时再叠 6 个外层（各边 +0.5 单位即纹理上的 +0.25）。UV 用 `BuildUv` 的 mojang 展开公式；**披风例外**，走 `BuildCapeUv`——披风图整块只有 22 宽，等宽公式会把背面推到 31 列而取到透明像素。
6. 投影与剔除：`src/NebulaLauncher.App/Skinning/SkinProjector.cs Project`。矩阵顺序 `projection * view * model`，屏幕绕向叉积为负才算正面，最后按深度从远到近排序（画家算法）。
7. 光栅：`src/NebulaLauncher.App/Skinning/SkinRasterizer.cs FillQuad` 用增量式边缘函数填三角形，`invArea` 保留符号，透视校正插值 `uv/w`，UV 钳到 `[uMin, uMax-1e-3]` 防止 `floor` 渗到隔壁格子。
8. 上屏：`src/NebulaLauncher.App/Controls/SkinModelView.cs` 继承 `Control`，按 `IRenderRoot.RenderScaling` 建 `WriteableBitmap`（`Bgra8888` + `Premul`），逐帧 `SkinRenderer.Render` → 转 BGRA 预乘 → `Marshal.Copy` → `context.DrawImage`。33ms 定时器自转；`Motion.Enabled=false` 时停表（真静止）；左键拖动改 yaw/pitch。

**与原票实现的偏离**：票面写的是「几何算出屏幕多边形 → `PushTransform` + clip 贴三角形」，实际改成**纯 CPU 软光栅到 `WriteableBitmap`**。理由：① 测试禁止触碰 Avalonia 类型（CONVENTIONS §8.2），像素级断言（六方向主色占比）只有纯 C# 光栅器能喂；② 同一份光栅代码给产品用，测试才真的证明了产品正确；③ 单帧 220×280 实测中位 6.2ms，60fps 预算 16.7ms 有余量，且避开了 68 个 geometry 的 tessellation 性能未知与相邻三角形接缝。

### 4.5 换肤（皮肤页 → 磁盘与远端材质）

1. 页面状态：`src/NebulaLauncher.App/ViewModels/SkinsViewModel.cs SkinsViewModel` 管网络/本地两个页签、三条上传通道与行内反馈；可选依赖全部走 `GetService(...) as X ?? new X(...)`，没注册也能独立可用。
2. 本地库落盘：`src/NebulaLauncher.Core/Skinning/LocalSkinStore.cs LocalSkinStore.ImportAsync`（磁盘 PNG）与 `src/NebulaLauncher.Core/Skinning/LocalSkinStore.cs LocalSkinStore.SaveAsync`（内存字节，网络皮肤「下载到本地」走这里）→ 校验 → 写文件 → 写索引；`LocalSkinFormat.Validate` 只管「能不能当皮肤用」（宽 64 的倍数、高 32 的倍数、`slim` 必须 64×64）。
3. 送官方前的本地拦截：`src/NebulaLauncher.App/Skinning/SkinUploadPreflight.cs SkinUploadPreflight.Check` 在本地库规则之上再叠官方规则（只收 64×64 与 64×32），这样用户看到的是「你这张是 128×64」而不是云端 400。
4. 皮肤站换肤：`src/NebulaLauncher.App/Skinning/AuthlibSkinClient.cs AuthlibSkinClient.UploadSkinAsync` / `UploadCapeAsync` / `DeleteSkinAsync`，multipart 的 `model` 由 `AuthlibSkinClient.ModelFieldOf` 产出（只认 `slim` 与空串）。
5. 官方换肤：`src/NebulaLauncher.App/Skinning/MojangSkinClient.cs MojangSkinClient.UploadSkinAsync` / `ResetSkinAsync`，`variant` 由 `MojangSkinClient.VariantOf` 产出（只认 `classic` / `slim`）。401 是令牌过期，引导去账户页刷新，**不自动重试**。
6. 能力判定：`src/NebulaLauncher.App/Skinning/OfficialSkinActions.cs OfficialSkinActions.Of` 决定当前账户能不能用官方换肤；`src/NebulaLauncher.App/Skinning/SkinProfileParser.cs SkinProfileParser.ParseUploadableTextures` 解 `uploadableTextures`，不到那时不要假设能传。
7. 反馈与冷却：`src/NebulaLauncher.App/Skinning/SkinUploadFeedback.cs SkinUploadFeedback.For` 把 `SkinUploadResult` 折成一句话 + 有无重试 / 有无「去账户页」；`src/NebulaLauncher.App/Skinning/SkinUploadThrottle.cs SkinUploadThrottle` 拦同一渠道的连点（默认 15 秒）。
8. 广播与回写：成功后调 `src/NebulaLauncher.App/Skinning/SkinChangeNotifier.cs SkinChangeNotifier.Notify`，侧边栏卡见 4.4；体型经 `src/NebulaLauncher.App/Skinning/SkinModelMapping.cs SkinModelMapping.ToAccountValue` 写进 `Account.SkinModel`。

## 5. 依赖注入

| 问题 | 答案 |
| --- | --- |
| 组合根在哪 | `src/NebulaLauncher.App/App.axaml.cs App.BuildServices（:273）`（private static，返回 `ServiceProvider`） |
| 顺序 | 路径 → 日志 → 平台 → 业务模块；注释写在 `src/NebulaLauncher.App/App.axaml.cs App.BuildServices（:273）` 上方 |
| 模块分组 | `src/NebulaLauncher.Core/Platforms/CoreAssetsServiceCollectionExtensions.cs CoreAssetsServiceCollectionExtensions.AddCoreAssets（:17）` 一个方法聚合下载/Java/安装/内容/备份/服务器七组；`AddCoreAssets` 必须在 `AddCoreModUpdates` 之前，后者的依赖由前者提供 |
| 每模块扩展点 | 各目录一个 `*ServiceCollectionExtensions.cs`；唯一例外是 `src/NebulaLauncher.Core/Backups/WorldIconService.cs WorldIconServiceCollectionExtensions.AddCoreWorldIcons（:261）` 放在服务同文件（注册点只有一个，避免与并行改动争同一份扩展文件） |
| 代理型注册 | `src/NebulaLauncher.App/Services/NavigationServiceProxy.cs NavigationServiceProxy（:13）` 与 `src/NebulaLauncher.App/Services/WindowActionsProxy.cs WindowActionsProxy（:10）` 以单例注册，具体实现等窗口创建后再注入，避免 VM 构造期拿不到窗口 |
| 页面 VM 为何不注册 | `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.Resolve（:715）` 先 `GetService`、未命中就 `ActivatorUtilities.CreateInstance`。注册会让每个页面都往组合根加一行，多协作者必然抢同一个文件；页面注册改在三个 partial（`MainWindowViewModel.HomePages.cs` / `MainWindowViewModel.ContentPages.cs` / `MainWindowViewModel.SystemPages.cs`）+ `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.RegisterPage（:708）` |
| 可选依赖约定 | VM 构造参数全部可空；缺服务时页面降级为演示数据（`IsDemoMode`）而不是抛异常，例如 `src/NebulaLauncher.App/ViewModels/LibraryViewModel.cs LibraryViewModel（:155）` |

## 6. 落盘对象

| 文件/目录 | 所属模块 | 写方 | 读方 |
| --- | --- | --- | --- |
| `settings.json` | Settings | `src/NebulaLauncher.Core/Settings/SettingsService.cs SettingsService.SaveAsync（:121）` | `src/NebulaLauncher.Core/Settings/SettingsService.cs SettingsService.LoadAsync（:52）` |
| `accounts.json` | Accounts | `src/NebulaLauncher.Core/Accounts/AccountService.cs AccountService.PersistAsync（:939）`（令牌经 `TokenProtector` 加密） | `src/NebulaLauncher.Core/Accounts/AccountService.cs AccountService.LoadCoreAsync（:116）` |
| `servers.json` | Servers | `src/NebulaLauncher.Core/Servers/ServerService.cs ServerService.SaveAsync（:466）` | `src/NebulaLauncher.Core/Servers/ServerService.cs ServerService.LoadAsync（:54）` |
| `servers.dat` | Servers / MC | 游戏本体 | 只读导入 `src/NebulaLauncher.Core/Servers/ServerService.cs ServerService.ImportFromServersDatAsync（:223）` |
| `favorites.json` | Versions | `src/NebulaLauncher.Core/Versions/VersionFavorites.cs VersionFavorites.ToggleFavoriteAsync（:172）`、`:362 VersionFavorites.SaveCoreAsync` | `src/NebulaLauncher.Core/Versions/VersionFavorites.cs VersionFavorites.LoadAsync（:157）` |
| `<versions>/<名>/instance.json` | Instances | `src/NebulaLauncher.Core/Instances/InstanceService.cs InstanceService（:367）`、`src/NebulaLauncher.Core/Backups/ModpackService.cs ModpackService（:371）` | `src/NebulaLauncher.Core/Instances/InstanceService.cs InstanceService（:92）` |
| `.nebula-run.json` | Instances（运行锁） | `src/NebulaLauncher.Core/Instances/InstanceLockService.cs InstanceLockService（:21）` | 同上（进程重启后判定残留） |
| `<版本>.json` / `nebula.json` | Versions | `src/NebulaLauncher.Core/Versions/VersionService.cs VersionService（:25）` | 版本列表与启动参数合成 |
| `version_manifest_v2.json` | Versions（缓存） | `src/NebulaLauncher.Core/Versions/VersionService.cs VersionService.ManifestCacheFileName（:30）` | 版本列表离线兜底 |
| `forge-promotions.json` | Versions（缓存） | `src/NebulaLauncher.Core/Versions/VersionService.cs VersionService.ForgePromotionsCacheFileName（:34）` | 加载器版本列表 |
| `server-versions.json` / `Launch Server.bat` | Versions / Servers | `src/NebulaLauncher.Core/Versions/ServerDownloadService.cs ServerDownloadService（:32）` | 服务端下载页 |
| `mod-hashes.json` | Mods | `src/NebulaLauncher.Core/Mods/ModFingerprint.cs ModFingerprint.CacheFileName（:26）` | 本地内容识别 |
| `mod-projects.json` | Mods（缓存） | `src/NebulaLauncher.Core/Mods/ModProjectInfoService.cs ModProjectInfoService.CacheFileName（:32）` | 模组页项目信息 |
| `java-cache.json` | Java | `src/NebulaLauncher.Core/Java/JavaCache.cs JavaCache（:55）` | Java 选择 |
| `java-excluded.json` / `java-manual.json` | Java | `src/NebulaLauncher.Core/Java/JavaService.cs JavaService（:16）` | 同上 |
| `mods/` `resourcepacks/` `shaderpacks/` `datapacks/` | Mods | 目录名由 `src/NebulaLauncher.Core/Mods/ContentService.cs ContentService.DirectoryNameFor（:50）` 决定 | 游戏本体 |
| `logs/` `cache/` `backups/` | Logging / Maintenance / Backups | 路径来自 `src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.LogsDirectory（:139）`、`:143 LauncherPaths.BackupsDirectory` | 日志页、存储清理页、备份页 |
| `<data>/skins/` 与 `skins/index.json` | Skinning（本地皮肤库） | `src/NebulaLauncher.Core/Skinning/LocalSkinStore.cs LocalSkinStore.ImportAsync`（PNG 本体）、`LocalSkinStore.SaveAsync`（内存字节）；索引在 `src/NebulaLauncher.Core/Skinning/LocalSkinStore.cs LocalSkinStore.IndexPath` | 皮肤页本地页签；索引坏掉时按文件 mtime 退化重建。该目录在清理保护名单内（`ProtectedTopLevelNames`） |
| `modrinth.index.json` | Backups（导出整合包） | `src/NebulaLauncher.Core/Backups/ModpackExporter.cs ModpackExporter.IndexFileName（:27）` | Modrinth 客户端 / 再次导入 |

`instances/`（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.InstancesDirectory（:137）`）是旧布局，实例目录已改为 `<游戏目录>/versions/<名>/`，不要再往这里写。

### 6.1 JSON 序列化的代码落点

落盘表的每一项都经 System.Text.Json。元数据由编译期生成、与落盘类型同文件写在文件尾的 `partial class <模块>JsonContext : JsonSerializerContext` 提供；调用点经 `src/NebulaLauncher.Core/Serialization/JsonMeta.cs JsonMeta.Of（:30）` 从各自的 `JsonSerializerOptions` 取 `JsonTypeInfo<T>`。

迁移中被改形的类型：请求体的匿名对象换成具名记录（`src/NebulaLauncher.Core/Mods/CurseForgeService.cs ModIdsQueryRequest（:2412）`、`src/NebulaLauncher.Core/Mods/ModrinthService.cs VersionFilesRequest（:2079）`），原本的私有嵌套缓存类型提到命名空间层（`src/NebulaLauncher.Core/Mods/ModProjectInfoService.cs CacheFile（:731）`、`src/NebulaLauncher.Core/Mods/ModFingerprint.cs HashCacheFile（:560）`、`src/NebulaLauncher.Core/Instances/InstanceService.Trash.cs TrashMetadata（:415）`）。

| 差异 | 实际 |
| --- | --- |
| 目录表只列 21 行，正文却写「22 个目录」 | 缺的一行是 src/NebulaLauncher.Core/Resources（只放嵌入的 `WikiEntries.txt`，无 cs 代码） |
| 契约接口数写作 36 | 实际 37 个 `interface I*`，多的是 `src/NebulaLauncher.Core/Abstractions/InstanceServices.cs IInstanceLockHandle（:160）` |
| 用 `LoaderMetaFetcher.cs:17/18/21-22/30` 指代加载器 meta 地址 | 文件名是旧称，文件里唯一的类型是 `src/NebulaLauncher.Core/Downloads/LoaderMetaFetcher.cs LoaderMetaUrls（:15）`；:17/:18/:21-22 是它与 Forge promotions 的常量 |
| 组合根行号（273） | 一致；其余注册行号随合并漂移，优先按方法名搜索 |
