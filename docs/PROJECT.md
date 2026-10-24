# Nebula Launcher 项目描述

本文回答四个问题：这个项目是什么、给谁用、为什么这样选技术、东西都落在哪。后面几节记录当前状态、明确不做的范围与核对方式。

除特别说明外，文中事实按 2026-10-24 的工作树核对；测试数字在这一天用文末命令现取。

## 一句话定位

Nebula Launcher 是一个以 Windows 为首发验证平台、按单 TFM 跨平台架构编写的 Minecraft Java 版桌面启动器：原生 Avalonia 界面，不内嵌 WebView，把版本安装、实例与模组管理、启动、崩溃诊断收在同一个窗口里（`src/NebulaLauncher.App/Program.cs Program.BuildAvaloniaApp（:388）`）。

它是桌面启动器，不是服务端面板，也不是联机平台。服务器相关能力只做到「收藏 + Server List Ping」这一层（`src/NebulaLauncher.Core/Servers/ServerService.cs ServerService（:15）`）。

## 目标用户

- **同时维护多个版本或整合包的 Java 版玩家。** 一个实例就是 `versions/<版本名>/` 下一个独立游戏目录，mods、saves、config 天然隔离；这个布局与 PCL2 的 `PathIndie = PathVersion`、HMCL 的 `getRunDirectory` 一致（`src/NebulaLauncher.Core/Instances/InstanceService.cs InstanceService（:23）`）。
- **需要离线账户或第三方验证的玩家。** 账户类型只有三种：离线、微软设备码、authlib-injector 第三方验证（`src/NebulaLauncher.Core/Models/Accounts.cs AccountType（:6）`）；微软登录内置 public client id，不需要用户自建应用（`src/NebulaLauncher.Core/Accounts/MicrosoftDeviceCodeAuth.cs MicrosoftDeviceCodeAuth（:45）`）。
- **中文用户。** 界面文案为中文；下载源提供官方与 BMCLAPI 镜像的自动降级；模组卡片用内置的 MC 百科译名表显示中文名（`src/NebulaLauncher.Core/Mods/ModNameTable.cs ModNameTable（:21）`）。

不面向的场景见文末「明确不做的事」：皮肤管理、联机穿透、OptiFine 自动安装、启动器包导出等。

## 技术选型

### .NET 8：`net8.0` 单 TFM

**理由。** 单 TFM 是跨平台承诺的技术前提：同一份代码基在 Windows / macOS / Linux 上用运行时分支选择平台行为，而不是靠 `#if WINDOWS` 编出不同版本（`Directory.Build.props:4 TargetFramework`）。.NET 8 同时满足三件事：CmlLib.Core 的运行时要求、`Microsoft.Extensions.DependencyInjection` 的构造函数注入、以及 Avalonia 11 的支持矩阵。异步下载、`System.Text.Json`、`HttpClient` 这些基础设施都在 BCL 里，不需要再引一套。

**代价。**

- 目标机没有 .NET 8 Desktop Runtime 时，框架依赖形态直接起不来；自包含形态则要多带几十 MB 运行时（见「发布形态」）。
- 禁止 `net8.0-windows` 专属 API，意味着注册表、`System.Drawing`、WinForms 都不能用。所有 Win32 能力必须收在 `IPlatformService` 后面，并且在非 Windows 上返回 `false` 或 `null`（`src/NebulaLauncher.Core/Platforms/PlatformService.cs PlatformService.SupportsSecureTokenStorage（:91）`、`:89 PlatformService.SupportsWindowMaterial`）。
- 平台差异是运行时分支，编译器帮不上忙：漏写一个平台分支，只会在那台机器上运行期暴露。因此 Core 的跨平台路径、Java 探测、剪贴板都有独立单测。

### Avalonia 11.3.22

**理由。** 需求先排除了两条路：WPF 只跑 Windows；Electron / Tauri / NW.js 都要内嵌 WebView。剩下的要求是成熟的合成与动画管线、XAML 声明式界面、现成的 MVVM 工具链，以及能直接复用的 Minecraft 协议实现。Avalonia 11 用 Skia 渲染、单 TFM 跨平台，配 CommunityToolkit.Mvvm 的源生成器与 `Microsoft.Extensions.DependencyInjection` 构成完整闭环（`src/NebulaLauncher.App/NebulaLauncher.App.csproj Avalonia（:26）`）。

**代价。**

- 控件生态比 WPF 小，Avalonia 只给基础控件，文件选择、剪贴板回退、窗口材质、任务栏进度这些平台集成仍要自己接（`src/NebulaLauncher.App/Views/SettingsView.axaml.cs SettingsView.OnPickBackgroundImageClick（:24）`）。
- 原生渲染库必须随产物分发。`libSkiaSharp.dll` 与 `libHarfBuzzSharp.dll` 无法塞进单文件而不解压，这就是 minimal 形态最少 3 个文件（exe + `libSkiaSharp.dll` + `libHarfBuzzSharp.dll`，`build.ps1` 的 minimal 分支注释列了组成）的原因（`src/NebulaLauncher.App/Program.cs Program.BuildAvaloniaApp（:388）`）。
- 没有 WebView 就没有 HTML/CSS 生态，富文本正文要自己解析与排版，不能直接套网页组件。

### 其余固定选型

CommunityToolkit.Mvvm 8.4.2 提供 `[ObservableProperty]` / `[RelayCommand]` 源生成器；依赖注入用 `Microsoft.Extensions.DependencyInjection` 8.0.1；Minecraft 清单、安装与启动交给 CmlLib.Core 4.1.0 系；微软登录没有用 CmlLib 的内置实现，而是自研设备码流程，产物转成与 CmlLib 兼容的会话；测试用 xUnit（`Directory.Packages.props:18 CommunityToolkit.Mvvm`、`src/NebulaLauncher.Core/Accounts/MicrosoftDeviceCodeAuth.cs MicrosoftDeviceCodeAuth（:45）`、`tests/NebulaLauncher.Tests/NebulaLauncher.Tests.csproj xunit（:12）`）。

## 代码结构

仓库只有两个产品项目，依赖方向单一：

- `src/NebulaLauncher.Core/`：纯业务层，零 UI 依赖。`Abstractions/` 放接口契约，`Models/` 放 DTO，其余按域分目录（Accounts、Installing、Launching、Mods、Servers、Backups、Java、Downloads、Instances、Paths…）。Core 不引用 Avalonia（`src/NebulaLauncher.Core/NebulaLauncher.Core.csproj CmlLib.Core（:9）`）。
- `src/NebulaLauncher.App/`：Avalonia 外壳、视图、ViewModel 与 DI 组装。只引用 Core，不反向被 Core 引用（`src/NebulaLauncher.App/NebulaLauncher.App.csproj NebulaLauncher.Core（:41）`）。
- `tests/NebulaLauncher.Tests/`：xUnit 测试，覆盖 Core 的纯逻辑与 App 的若干源码守护断言。
- `tools/`：开发期工具。`tools/TestRunner` 是本机跑测试的入口，原因见「测试规模」。

导航固定 11 项：首页、版本、下载、模组、资源、账户、服务器、世界、日志、帮助、设置（`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.BuildNavigation（:731）`）。「关于」不是独立页，是设置页里的一个分区（`src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel.AppVersion（:2114）`）。

## 用户数据落在哪

### 启动器数据根

数据根按「平台默认 → LocalAppData → exe 目录旁 `data/`」的顺序选第一个可写的目录，因为受限环境里 `%APPDATA%` 可能连 `settings.json` 都写不进去（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths（:37）`）。

平台默认值：

- Windows：`%APPDATA%\NebulaLauncher`（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.ResolveDefaultRoot（:272）`）。
- macOS：`~/Library/Application Support/NebulaLauncher`（同上）。
- Linux：`$XDG_DATA_HOME/nebula-launcher`，未设置时 `~/.local/share/nebula-launcher`（同上）。

游戏目录是另一个概念：它可以在设置里改，优先级是启动参数/测试覆盖 > `settings.json` 的 `minecraftDirectory` > 平台默认（`src/NebulaLauncher.Core/Models/Settings.cs AppSettings.MinecraftDirectory（:51）`、`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.MinecraftDirectory（:115）`）。Windows 默认是 `%APPDATA%\.minecraft`（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.ResolveDefaultMinecraft（:297）`）。

### 顶层四个配置文件

四个文件都在数据根下，互不覆盖：

- `settings.json`：全部启动器设置。含游戏目录、Java 路径、内存上下限、游戏窗口与启动器窗口尺寸、下载源与代理、主题、背景图路径与遮罩强度等（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.SettingsFile（:147）`、`src/NebulaLauncher.Core/Models/Settings.cs AppSettings（:43）`）。
- `accounts.json`：账户列表、当前账户、令牌与过期时间。Windows 上令牌用 DPAPI（当前用户作用域）加密；macOS / Linux 上只做 Base64 编码，并在保存时写警告日志（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.AccountsFile（:145）`、`src/NebulaLauncher.Core/Accounts/AccountService.cs AccountService（:988）`、`src/NebulaLauncher.Core/Accounts/TokenProtector.cs TokenProtector（:15）`）。
- `servers.json`：收藏的服务器列表与备注。服务器条目只有地址、端口、名称、备注与添加时间（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.ServersFile（:149）`、`src/NebulaLauncher.Core/Models/Servers.cs ServerEntry（:4）`）。
- `favorites.json`：版本收藏与隐藏。它不改版本目录，只记录版本名与时间（`src/NebulaLauncher.Core/Versions/VersionFavorites.cs VersionFavorites.FilePath（:108）`）。

旧布局的 `<data>/instances/<id>` 仍保留属性，但只用于兼容迁移；新实例一律落在版本目录里（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.InstancesDirectory（:137）`）。

### 每个实例的配置

一个实例 = `<游戏目录>/versions/<版本名>/` 加同目录下的 `instance.json`。目录名是权威版本名，手改 `instance.json` 里的 `versionName` 不会把实例指到别的目录（`src/NebulaLauncher.Core/Instances/InstanceService.cs InstanceService（:23）`、`:71 InstanceService.LoadAsync`）。

`instance.json` 里放的是实例级覆盖：显示名、加载器与加载器版本缓存、原版版本号、是否版本隔离、实例级 Java 与内存、实例级 JVM/游戏参数、启动前后命令、窗口标题、备注、版本类型、图标种类、GC 偏好、直连服务器地址等（`src/NebulaLauncher.Core/Models/Instances.cs InstanceInfo（:10）`）。

### 清缓存删什么、不删什么

存储管理只有四个可清理类别（`src/NebulaLauncher.Core/Maintenance/StorageService.cs StorageService.BuildDefinitions（:424）`）：

- **下载缓存**：`<data>/cache` 下的下载缓存、图标与头像缓存，清掉后按需重建；`cache/loader-install` 不在这一类里。
- **日志**：`<data>/logs`，只删 7 天前的文件，当天正在写的日志不动。
- **安装器临时文件**：`cache/loader-install`，加载器安装器解包与下载的残留。
- **回收站**：`<data>/trash`，实例与内容回收站；清掉后已删除内容不可恢复。

清理范围之外还有一道硬保护：`settings.json`、`accounts.json`、`servers.json`、`favorites.json`、`java-excluded.json`、`java-cache.json` 永不进入删除列表；`instances/`、`versions/`、`libraries/`、`assets/`、`backups/`、`saves/`、`minecraft/` 这些顶层目录也被挡住（`src/NebulaLauncher.Core/Maintenance/StorageService.cs StorageService（:49）`）。清理前还会校验目标必须在数据根内，防止类别定义写错时越界删到用户数据（`src/NebulaLauncher.Core/Maintenance/StorageService.cs StorageService（:474）`）。

结论：清「下载缓存」只动 `<data>/cache` 里可重建的内容，不碰四个顶层配置文件、不碰版本与游戏目录、不碰备份与存档。日志按 7 天窗口清，安装器残留与回收站是另外两个独立类别。

## 当前状态

### 功能面

账户支持离线、微软设备码、第三方 authlib 验证；版本安装覆盖原版、Fabric、Quilt、Forge、NeoForge；下载有三档源（自动 / 官方 / 镜像）与会话级降级；内容侧覆盖模组、资源包、光影包、数据包的扫描、启停、冲突与前置检查；整合包支持 `.mrpack` 与 CurseForge `.zip` 导入、`.mrpack` 导出；服务器支持收藏、Server List Ping、`servers.dat` 导入；世界支持备份与轮转；日志中心覆盖启动器日志、游戏日志与崩溃报告；系统集成有托盘、关闭到托盘、窗口尺寸记忆、任务栏进度；外观有深色、浅色、跟随系统与背景图。

这些能力的具体入口以导航 11 页为准，不在本文逐项展开。

### 测试规模

测试数字由 `tools/DocsCheck` 现取，不用 `grep` 数属性字面量（`[Fact]` 出现在字符串或注释里会虚增）。在工作树根目录跑：

```bash
dotnet run --project tools/DocsCheck/DocsCheck.csproj -c Release --
# 报告末尾那行就是本次口径，例如：
# 【测试数】Fact 1691 + InlineData 757 = 2448
```

2026-10-24 快照：xUnit 用例数 = `[Fact]` 1691 + `[InlineData]` 757 = **2448**。每个 `[InlineData]` 是一条独立用例，所以 `[Theory]` 本身不另计。测试框架是 xUnit（`tests/NebulaLauncher.Tests/NebulaLauncher.Tests.csproj xunit（:12）`）。

本机沙箱拒绝 `dotnet test` 启动 testhost，仓库因此带 `tools/TestRunner` 作为等价入口；它反射测试程序集跑 xunit 用例，报出的条数比上面的属性计数少几条（不可实例化的测试类不计），判通过看它的输出，判文档规模看 `tools/DocsCheck`。

仓内旧文档里的 2235、2238、2244、2358、2362 都是历史轮次快照，不代表当前工作树。本文以工具现取的数字为准。

### 发布形态

`build.ps1` 提供四种 `-Mode`，参数默认是 `minimal`（`build.ps1:10`）。四种形态的技术差异全部由 publish 参数决定：

- **minimal（默认）**：框架依赖 + 单文件，托管程序集打进 exe，原生库外置（`--self-contained false` + `-p:PublishSingleFile=true` + `-p:IncludeNativeLibrariesForSelfExtract=false`，`build.ps1:337` 分支）。目标机需要 .NET 8 Desktop Runtime；不需要 `%TEMP%` 解压。
- **fdd**：框架依赖、多文件（`--self-contained false`，`build.ps1:357` 分支）。同样需要 .NET 8 Desktop Runtime，双击即用、无解压步骤。
- **folder**：自包含、多文件目录（`--self-contained true` + `-p:PublishSingleFile=false`，`build.ps1:360` 分支），任何机器双击即用。
- **portable**：自包含、单文件、裁剪压缩（`--self-contained true` + `-p:PublishSingleFile=true` + `-p:PublishTrimmed=true` + `-p:TrimMode=partial` + `-p:EnableCompressionInSingleFile=true`，`build.ps1:347` 分支）；首次启动要把原生库解压到 `%TEMP%\.net\<app>`，该目录被安防软件拦截时启动会直接失败。

体积与文件数以 `build.ps1` 构建时打印的 `[build] <模式> -> <目录> (<N> MB, <M> 个文件)` 为准，复算命令 `.\build.ps1 -Publish -Mode <模式> -OutDir pub\<模式>`；这些数字随 .NET 运行时版本与构建机变化，文档不写死。

体积差距来自运行时：PCL 用系统自带的 .NET Framework 4.8，HMCL 依赖用户已有的 Java，所以它们能做到十几 MB；.NET 8 不在 Windows 自带组件里，自包含必然多带几十 MB（`build.ps1:32`）。

发布脚本有两道产物自检：`--probe-dir` 断言数据目录可写并打印 `writable=YES`，`--screenshot` 要求 20 秒内退出且 PNG 大于 10 KB。两道关堵的都是「退出码 0 但启动不起来」（`build.ps1:39`；早前发布检查记录也列过同一条）。

### 已发布版本的口径

版本号的唯一来源是 `Directory.Build.props` 的 `<Version>1.0.0</Version>`（票 #20 引入）。关于页与崩溃导出的 `system.txt` 都取 `SettingsViewModel.AppVersion`，它走 `DiagnosticsSnapshot.DisplayVersionOf`：读程序集的 `AssemblyInformationalVersionAttribute`、在 `+` 处截断构建哈希、再补 `v` 前缀（`src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel.AppVersion（:2119）`）。

对外描述版本一律用发布号 `v1.0.0`；轮次号（R59、`pub-rNN` 这类）只是内部开发标识，不进对外文案。产物默认放在仓库根的 `pub\`（已在 `.gitignore` 里）。

历史轮次记录没有删：`docs/research/` 下的旧审计与验收记录、以及 git 历史里的 R59 / `pub-rNN` 提交，都按原文留在原处，不回头改写。它们只作开发过程标识，不再是任何对外口径，也不参与版本号校验。

## 明确不做的事：12 条逐条复核

来源是已删的最终范围清单（FINAL-SCOPE）§2 的 12 条「明确不做」。2026-10-24 逐条对照 `src/` 复核，结论分三类：仍成立、改判、删除。

复核结果：8 条仍成立（第 2、3、4、5、7、9、10、12 条），4 条改判（第 1、6、8、11 条），没有需要删除的条目。其中第 8 与第 12 条在原文里自相矛盾，本文给出统一结论。

### 1. macOS 平台支持 —— 改判

原声明「不做 macOS」与代码不符，但也不能读成「macOS 已支持」。

已落地的是单 TFM 下的运行时分支：macOS 数据根与游戏目录（`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.IsMacOs（:267）`）、Java 可执行名（`src/NebulaLauncher.Core/Platforms/PlatformService.cs PlatformService.JavaExecutableName（:112）`）、剪贴板 `pbcopy`（`src/NebulaLauncher.Core/Platforms/PlatformService.cs ClipboardCommandChain（:19）`）、用 `open` 打开目录与链接（`src/NebulaLauncher.Core/Platforms/PlatformService.cs PlatformService.OpenFolderAsync（:117）`）、界面字体 PingFang（`src/NebulaLauncher.App/Program.cs Program.DefaultFontFamily（:421）`）。

未落地的是系统能力与验收：安全令牌存储只在 Windows 上成立（`src/NebulaLauncher.Core/Platforms/PlatformService.cs PlatformService.SupportsSecureTokenStorage（:91）`），窗口材质、任务栏进度、游戏窗口控制这类 Win32 能力在非 Windows 上返回 `false` 或 `null`（`src/NebulaLauncher.Core/Platforms/PlatformService.cs PlatformService（:242）`），也没有 macOS 发布物与实机验收记录。

准确表述：Windows 是首发与验收平台；macOS 的架构与路径分支已就绪，系统适配与实机验收未完成。

### 2. 皮肤 / 披风管理、上传、缓存 —— 不做

代码里只有 `Account.SkinModel` 一个 `classic` / `slim` 字段，注释明确写「仅影响本地预览」；没有皮肤或披风服务、上传链路、缓存（`src/NebulaLauncher.Core/Models/Accounts.cs Account.SkinModel（:41）`）。

### 3. Nide 专用登录流程 —— 不做

账户类型只有离线、微软、Authlib 三种，没有 Nide 专用服务端或注册/角色交互（`src/NebulaLauncher.Core/Models/Accounts.cs AccountType（:6）`）。`grep -rn nide src tests tools` 的命中全是 `unidentified`、`cyanide` 这类无关词（「未识别」状态与译名表里的模组名），没有 Nide 功能代码。

### 4. EasyTier / P2P 联机 —— 不做

服务器条目只有主机、端口、名称、备注，状态查询走 Server List Ping；没有房间、邀请、节点配置或第三方穿透二进制（`src/NebulaLauncher.Core/Models/Servers.cs ServerEntry（:4）`）。

### 5. OptiFine / LiteLoader 自动安装链 —— 不做

加载器枚举只有原版、Fabric、Quilt、Forge、NeoForge，没有 OptiFine / LiteLoader 的安装分支（`src/NebulaLauncher.Core/Models/Versions.cs LoaderKind（:19）`）。仓库里存在的只是识别能力：崩溃规则里的 OptiFine 条目（`src/NebulaLauncher.Core/Launching/CrashRules.cs CrashRules（:396）`）、`litemod.json` 元数据读取（`src/NebulaLauncher.Core/Mods/ModMetadataReader.cs ModMetadataReader（:489）`）、图标枚举里的 OptiFine（`src/NebulaLauncher.Core/Models/Instances.cs InstanceIconKind（:262）`）。识别不等于安装链。

### 6. 组件修改器完整闭环 —— 改判

「检查组件」已经做了：`ComponentInspector` 是纯读盘检查器，报缺失文件与 mtime 被改动的文件，明确「不下载、不写清单、不改任何文件」（`src/NebulaLauncher.Core/Installing/ComponentInspector.cs ComponentInspector.InspectAsync（:16）`）。

「安装前快照 + 新旧对比 + 逐项恢复」仍不做。现有能力是检查，以及在同一 MC 版本上原地换加载器、mods 与存档留在旧目录（`src/NebulaLauncher.Core/Instances/InstanceService.Loader.cs InstanceService.SwitchLoaderAsync（:20）`），不是完整的修改器闭环。

### 7. 导出 zip / 启动器包、逐文件勾选 —— 不做

导出只有 `.mrpack`（`modrinth.index.json` + `overrides/`），入口是 `ModpackService.ExportAsync`（`src/NebulaLauncher.Core/Backups/ModpackService.cs ModpackService.ExportAsync（:420）`）。

导出选项是类别开关：存档、配置、资源包、光影包、`options.txt`、`defaultconfigs`、`servers.dat`；没有逐文件勾选，也没有普通 zip 或其它启动器包格式（`src/NebulaLauncher.Core/Models/Content.cs ModpackExportOptions（:619）`）。

### 8. 背景图片 / 音乐 —— 改判，且与第 12 条统一

**背景图片已做。** 设置里有 `BackgroundImagePath` 与 `BackgroundImageOverlay`（`src/NebulaLauncher.Core/Models/Settings.cs AppSettings.BackgroundImagePath（:99）`），主窗口按路径加载、失效时回退底色并写状态栏（`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.ApplyBackgroundImage（:186）`），设置页有文件选择器（`src/NebulaLauncher.App/Views/SettingsView.axaml.cs SettingsView.OnPickBackgroundImageClick（:24）`）。

**背景音乐没做。** 仓库里没有 `MediaPlayer`、NAudio、WaveOut 之类的播放器代码。

已删的最终范围清单第 8 条写「背景图片/音乐不做」，第 12 条又括注「背景图片已实现」，两处矛盾。统一结论：**背景图片已做，背景音乐不做。**

### 9. 分片下载阶梯并发 —— 不做

下载层只有 Range 断点续传：带 `Range` 头的请求按续传处理，不是把单个文件切片并发（`src/NebulaLauncher.Core/Downloads/DownloadInfrastructure.cs:766`）。并发由下载队列的信号量限流承担，上限可配（`src/NebulaLauncher.Core/Downloads/DownloadQueue.cs DownloadQueue.MaxConcurrency（:199）`）。

### 10. 自定义主页声明式事件体系 —— 不做

导航是固定的 11 项，页面在 `MainWindowViewModel` 里直接注册，没有 `ModEvent`、`EventBus` 或自定义主页加载入口（`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.BuildNavigation（:731）`）。页面扩展走 MVVM 的 `ICommand`，不是 PCL 那套声明式事件格式。

### 11. 内置中文搜索词表、本地模组完整更新日志入口 —— 改判

**译名表已做。** `Resources/WikiEntries.txt` 作为嵌入资源打进 Core（约 955 KB、2.6 万条），运行时按资源站 slug 查 MC 百科中文名（`src/NebulaLauncher.Core/NebulaLauncher.Core.csproj:22`、`src/NebulaLauncher.Core/Mods/ModNameTable.cs ModNameTable（:198）`、`:201 ModNameTranslations`）。模组卡片显示的中文名来自这张表。

**更新日志有摘要。** 在线更新检查把版本 changelog 带回来（`src/NebulaLauncher.Core/Mods/ModUpdateService.cs ModUpdateService（:914）`），模组面板显示前 160 字（`src/NebulaLauncher.App/ViewModels/ModsViewModel.cs ModsViewModel（:2635）`）。

**仍缺的两半。** PCL 式中文分词搜索与拼音匹配没有实现，跨源搜索的注释明确说 `TranslatedName` 这一路判据本仓库没有对应数据源（`src/NebulaLauncher.Core/Mods/CrossSourceSearch.cs CrossSourceSearch（:246）`）；也没有独立的「完整更新日志」入口，只有截断摘要。

准确表述：中文译名显示已落地，中文搜索分词与完整更新日志入口未落地。

### 12. 背景音乐与其余外观个性化 —— 与第 8 条统一

背景音乐不做，背景图片已做；深浅主题、跟随系统、遮罩强度已做（`src/NebulaLauncher.Core/Models/Settings.cs AppSettings.Theme（:224）`、`:105 AppSettings.BackgroundImageOverlay`）。原条目里「其余外观个性化」是一个兜底说法，具体到代码，外观部分能落地的就是主题、背景图与遮罩，没有音乐与启动音效。

## 已知边界

- 微软账户的真实登录于 2026-10-24 用户实机验证通过（按裁决不复跑、仓内不留证据）；代码侧覆盖设备码流程与令牌持久化（`src/NebulaLauncher.Core/Accounts/MicrosoftDeviceCodeAuth.cs MicrosoftDeviceCodeAuth（:45）`）。
- macOS 没有发布物与实机验收，见第 1 条。
- `portable` 形态依赖 `%TEMP%\.net\<app>` 可写；该路径被安防软件拦截时启动失败，改用 `fdd` / `folder`。
- 仓内仍有旧口径：已删的最终范围清单里的测试数 2235、页面名「实例/存档/关于」，README 的静态能力矩阵停在早期轮次。本文与上面的测试命令是当前口径；发现冲突时以代码与本文为准。

## 核对方式

```bash
cd <repo>
git log --oneline -1

grep -rho '\[Fact\]' tests/NebulaLauncher.Tests --include=*.cs | wc -l
grep -rho '\[InlineData' tests/NebulaLauncher.Tests --include=*.cs | wc -l

pwsh ./build.ps1 -Clean -Publish -Mode minimal -OutDir <out>
```

最后一条会重建并自检发布产物；不发布时不需要跑。测试入口用 `tools/TestRunner`，不要直接依赖 `dotnet test`（本机沙箱拒绝 testhost）。
