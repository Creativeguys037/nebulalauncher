# 功能完成度（9 块三态）

本文件由工具生成，请勿手改。

- 生成命令：`dotnet run --project tools/DocsCheck/DocsCheck.csproj -c Release -- --emit-features docs/FEATURES.md`
- 生成日期：2026-10-27

## 三态定义

- **已实现**：代码探测点与测试探测点都命中。
- **待验证**：代码探测点命中，测试探测点缺失。
- **不存在**：代码探测点未命中。

## 1. 装得上（安装与修复）

从零装到能启动，坏了能就地修复 —— 三态：已实现

| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |
| --- | --- | --- | --- | --- |
| `src/NebulaLauncher.App/Views/VersionsView.axaml VersionsView` | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs VersionsViewModel` | `src/NebulaLauncher.Core/Installing/InstallService.cs InstallService.InstallAsync` | `src/NebulaLauncher.Core/Installing/InstallManifest.cs McInstallManifest.RelativePath`（`install-manifest.json`） | 已实现 |

## 2. 下得动（下载与换源）

多源并发下载，卡住自动换源 —— 三态：已实现

| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |
| --- | --- | --- | --- | --- |
| `src/NebulaLauncher.App/Views/DownloadView.axaml DownloadView` | `src/NebulaLauncher.App/ViewModels/DownloadViewModel.cs DownloadViewModel` | `src/NebulaLauncher.Core/Downloads/DownloadQueue.cs DownloadQueue.DownloadAsync` | `src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.CacheDirectory`（`java-cache.json`、`mod-hashes.json`、`mod-projects.json`） | 已实现 |

## 3. 开得起来（启动与运行时）

点一下就把游戏带起来，参数与内存自动算 —— 三态：已实现

| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |
| --- | --- | --- | --- | --- |
| `src/NebulaLauncher.App/Views/HomeView.axaml HomeView` | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel` | `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.LaunchAsync` | `src/NebulaLauncher.Core/Instances/InstanceLockService.cs InstanceLockService.LockFileName`（`.nebula-run.json`） | 已实现 |

## 4. 管得住内容（模组与整合包）

模组、整合包、资源包按实例归类管理 —— 三态：已实现

| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |
| --- | --- | --- | --- | --- |
| `src/NebulaLauncher.App/Views/ModsView.axaml ModsView` | `src/NebulaLauncher.App/ViewModels/ModsViewModel.cs ModsViewModel` | `src/NebulaLauncher.Core/Mods/ContentService.cs ContentService.DirectoryNameFor` | `src/NebulaLauncher.Core/Mods/ModFingerprint.cs ModFingerprint.CacheFileName`（`mod-hashes.json`） | 已实现 |

## 5. 存得下（实例与世界）

实例、世界与备份都落在磁盘上，删了能捞回来 —— 三态：已实现

| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |
| --- | --- | --- | --- | --- |
| `src/NebulaLauncher.App/Views/WorldsView.axaml WorldsView` | `src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs WorldsViewModel` | `src/NebulaLauncher.Core/Backups/BackupService.cs BackupService` | `src/NebulaLauncher.Core/Instances/InstanceService.cs InstanceService`（实例目录下 `instance.json`）、`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.BackupsDirectory`（`backups/`） | 已实现 |

## 6. 连得上（账户与服务器）

微软账户登录与服务器状态查询 —— 三态：已实现

| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |
| --- | --- | --- | --- | --- |
| `src/NebulaLauncher.App/Views/AccountsView.axaml AccountsView` | `src/NebulaLauncher.App/ViewModels/AccountsViewModel.cs AccountsViewModel` | `src/NebulaLauncher.Core/Accounts/MicrosoftDeviceCodeAuth.cs MicrosoftDeviceCodeAuth` | `src/NebulaLauncher.Core/Accounts/AccountService.cs AccountService.PersistAsync`（`accounts.json`）、`src/NebulaLauncher.Core/Servers/ServerService.cs ServerService`（`servers.json`） | 已实现 |

## 7. 看得见（日志与崩溃）

日志实时看，崩溃能导出成可分享的包 —— 三态：已实现

| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |
| --- | --- | --- | --- | --- |
| `src/NebulaLauncher.App/Views/LogsView.axaml LogsView` | `src/NebulaLauncher.App/ViewModels/LogsViewModel.cs LogsViewModel` | `src/NebulaLauncher.Core/Logging/LauncherLogReader.cs LauncherLogReader` | `src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.LogsDirectory`（`logs/` 下的 `.log` 文件） | 已实现 |

## 8. 改得动（设置与数据）

设置可回退，实例可换加载器、可从回收站捞回 —— 三态：已实现

| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |
| --- | --- | --- | --- | --- |
| `src/NebulaLauncher.App/Views/SettingsView.axaml SettingsView` | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel` | `src/NebulaLauncher.Core/Instances/InstanceService.Loader.cs InstanceService.SwitchLoaderAsync` | `src/NebulaLauncher.Core/Settings/SettingsService.cs SettingsService.SaveAsync`（`settings.json`） | 已实现 |

## 9. 换得动（皮肤与材质）

本地皮肤库、皮肤站换肤、官方账户换肤三条链路都在，披风只有皮肤站能传 —— 三态：已实现

| 入口页面 | ViewModel | Core 服务 | 落盘文件 | 三态 |
| --- | --- | --- | --- | --- |
| `src/NebulaLauncher.App/Views/SkinsView.axaml SkinsView` | `src/NebulaLauncher.App/ViewModels/SkinsViewModel.cs SkinsViewModel` | `src/NebulaLauncher.Core/Skinning/LocalSkinStore.cs LocalSkinStore` | `src/NebulaLauncher.Core/Skinning/LocalSkinStore.cs LocalSkinStore.DirectoryPath`（`<data>/skins/` 与 `index.json`） | 已实现 |
