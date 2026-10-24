# NebulaLauncher 术语表

只收本仓曾被写错、或极易混的词。每条给出正确写法、曾被写成的写法，以及证明正确的那处代码。

代码证据的写法：反引号内第一段是仓库相对路径，其后每个词是符号名。

| 正确写法 | 曾被写成 | 代码证据 |
| --- | --- | --- |
| 导航页「资源」（`NavigationKeys.Library`） | 「实例」 | `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.BuildNavigation` |
| 导航页「世界」（`NavigationKeys.Worlds`） | 「存档」 | `src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.BuildNavigation` |
| 设置页的分区「关于」，不是独立导航页 | 导航页「关于」 | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel.Sections` |
| 实例清理方法 `CleanupAsync` | `CleanAsync` | `src/NebulaLauncher.Core/Instances/InstanceService.Admin.cs InstanceService.CleanupAsync` |
| 模组指纹缓存 `mod-hashes.json` | `mods.lock.json` | `src/NebulaLauncher.Core/Mods/ModFingerprint.cs ModFingerprint.CacheFileName` |
| 实例运行锁 `.nebula-run.json`，与指纹缓存不是同一个文件 | `mods.lock.json` | `src/NebulaLauncher.Core/Instances/InstanceLockService.cs InstanceLockService.LockFileName` |
| 自动内存推荐抄 PCL2 的四段斜坡，下限 512 MB | 抄 HMCL 公式 | `src/NebulaLauncher.Core/Launching/MemoryAdvisor.cs MemoryAdvisor.SuggestMaxMemoryMb MemoryAdvisor.MinSaneMaxMemoryMb` |
| 预检的两个形态：静态工具类 `PreflightChecker` 与服务实现 `PreflightCheckerService` | `PreflightChecker` 上的同名实例方法 `CheckAsync` | `src/NebulaLauncher.Core/Launching/PreflightChecker.cs PreflightChecker PreflightCheckerService` |
| 启动参数合成类 `JvmArgumentBuilder`，合成入口 `Build` | `JvmArguments` | `src/NebulaLauncher.Core/Launching/JvmArgumentBuilder.cs JvmArgumentBuilder.Build` |
| 实例图标是内联 `Geometry`（`InstanceIcons`） | 落在 App/Assets/InstanceIcons/ 资源目录；「模组图标是几何图形」（仓内没有模组图标，只有实例图标） | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs InstanceIcons.For` |
