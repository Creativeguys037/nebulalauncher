# Nebula Launcher 接手文档

给接手这个仓库的人。按「怎么跑 → 怎么分层 → 哪些约定不能破 → 踩过哪些坑 → 还剩什么」组织。

**先读这三节就够开工**：§2（跑起来）、§5（不能破的约定）、§6（坑）。其余按需查。
逐轮的功能对照与出处不在本文：轮次记录见 `docs/PCL2-PORTING.md`，功能清单见 `docs/EXTENSIONS.md`，视觉规范见 `docs/DESIGN.md`。

最后更新：2026-10-24（第五十九轮，详见 `docs/HANDOVER-2026-10-24-R59.md`；第五十六轮详见
`docs/HANDOVER-2026-10-24-R56.md`，第五十四轮最终验收见 `docs/HANDOVER-2026-10-24-R54.md` 与 `docs/FINAL-SCOPE.md`）。

> **被中断过？先读 `docs/TAKEOVER.md`**：那是一页式接手文档，写了当前验证状态（2172/2172、0/0）、
> 最容易踩的坑与「下次开工固定动作」。第二十八轮已把 §5 的三个挂起任务（源测速 CLI / 统一 Java
> 安装状态机 / build.ps1 发布自检）与 `docs/PCL2-GAP.md` 的 13 条清单全部做完，清单已归零。

---

## 1. 现状

Windows 可运行的 Minecraft 启动器：Avalonia 11.3 + .NET 8，单 TFM `net8.0`。
**11 个页面 / 2358 个单元测试全绿 / 构建 0 错误 0 CS 警告 / 发布产物 23.7 MB（3 文件，`build.ps1` 自带 XAML 过期检测与两道产物关，见 `docs/PUBLISH-CHECK.md`）**。
macOS 未实机验证，但 Core 无平台编译期依赖。

| 模块 | 能力 |
|---|---|---|
| 账户 | 微软设备码登录、离线账户、第三方验证（authlib-injector / 统一通行证） |
| 版本 | 安装（原版 / Fabric / Quilt / Forge / NeoForge）、两档修复（快速 / 深度）、删除、加载器版本选择（含 Forge 推荐版、NeoForge 两族目录）、收藏置顶 / 隐藏、**「保存客户端 jar」只下载不安装** |
| 实例 | 版本隔离三态、自动建实例、克隆 / 重命名 / 图标 / 回收站 / 目录清理 / 备注、**组件面板（清单比对 + 缺失/改动 + 只修复缺失）**、**运行中徽标 + 破坏性入口禁用**、**五目录快捷芯片（存档 / 模组 / 资源包 / 截图 / 日志）** |
| 启动 | 参数组装、自动内存、GC/编码参数注入、**默认兼容/诊断参数（`-XX:-OmitStackTraceInFastThrow` + 三条 `fml.ignore*` + Java 24+ 压缩对象头 + Windows 信任库）**、**启动参数去重与覆盖（窗口参数吸收进启动选项）**、进程优先级 / 启动后窗口行为 / 自动中文 / **高性能 GPU 注册** / Log4Shell 开关、前置检查（8 项）、5 级里程碑监视、窗口标题与最大化、启动脚本导出、任务栏进度、Forge/NeoForge 分阶段进度 + 安装器 stdout 日志、**实例运行锁（同一游戏目录不可重复启动）+ 多实例并行（按目录准入、按会话停止）**、**缺 Java 一键安装**、**停止运行中的游戏** |
| 下载 | 三档下载源（自动 / 官方 / 镜像）+ 会话级自动降级、候选回退、并发与限速、跨实例同 SHA1 文件复用、Mojang `java-runtime` 清单安装、逐文件任务快照 + 只重试失败项 + 批次历史、服务端 `server.jar` 下载（清单缓存，热启动 94ms）、**Fabric API 随 Fabric 附装**、**暂停 / 继续（在途文件下完即停）** |
| 内容 | 模组/资源包/光影/数据包扫描与启停（多选批量）、**删除进回收站（可恢复 / 彻底删除）**、**四档状态筛选（全部/已启用/已禁用/可更新）**、**一键更新选中项（下载 → 校验 → 旧文件进回收站 → 段对齐替换）**、`.old` / `.litemod` 兼容、冲突与缺前置检测、Modrinth + CurseForge 在线安装、**双源合并搜索（跨站去重 + 平台加权混排 + 「另一站也有」）**、**模组卡片「去资源站查找」（跨页预填）**、依赖解析自动装前置、项目详情、更新检查、整合包导入（**自动装游戏与加载器**）与导出 `.mrpack`、**在线整合包浏览与安装**、数据包在线安装、`pack_format` 兼容提示（含 1.21.9+ 区间） |
| 数据 | 世界备份、退出后自动备份 + 轮转、**存档 `level.dat` 编辑（种子 / 模式 / 难度 / 作弊 / 保留物品栏 / 世界名，写前 `.bak`）**、**存档缩略图（`Data.icon` 提取 + 缓存）**、服务器 Ping、**服务器 Query 玩家名单（GameSpy4）+ 完整状态**、崩溃归因（75 条规则 + 肇事 mod + `hs_err` 解析）与现场归档、**崩溃建议可直接执行（六种动作）** |
| 诊断 | 日志中心页（启动器 / `latest.log` / `crash-reports`）、**实时跟随（700ms 增量 tail，截断与轮转自愈）**、外部崩溃报告导入（txt/log/zip）、崩溃报告脱敏导出（含 hs_err 与 mods 列表）、诊断信息复制、日志脱敏 |
| 外观 | 深色 / 浅色 / 跟随系统三态主题（语义令牌双字典）、星云背景、动效开关、窗口材质 |
| 窗口 | 尺寸 / 最大化记忆、窗口内快捷键（Esc / F5 / F11 / Ctrl+O / Ctrl+L） |
| 系统 | 托盘图标 + 关闭到托盘、单实例唤醒、拖拽导入、内置离线帮助（26 条）、正式版更新提示、配置包导出 / 导入、**存储占用统计与安全清理（设置页「存储」分区）** |

**真机状态**：原版 1.20.1 + Java 21 —— 到主菜单（R31）、取消等待后仍持锁 / `WM_CLOSE` 正常退出（`Stopping!`）/ 同目录第二次启动（R35，`.probe/smoke-r34/REPORT.md`）；存档备份端到端与多实例并行（R37，`.probe/smoke-r37`）。
Fabric 1.20.1：安装、`BuildCommandLineAsync`（`KnotClient`）与**真机启动到材质 atlas**（R37 重试，`.probe/smoke-r36/run4.log`），日志文本未复核。Forge 真机与真实账户登录未验。

代码规模（不含 `bin` / `obj`）：Core 153 个 `.cs`，App 56 个 `.cs` + 16 个 `.axaml`，测试 155 个文件 / 2358 例。

---

## 2. 五分钟跑起来

.NET SDK 不在系统 PATH，在 `C:\Users\MECHREUO\.dotnet8`（8.0.426）。

```powershell
cd D:\agent_workspace\NebulaLauncher
. .\env.ps1                                  # 每条新 shell 都要执行
$env:AVALONIA_TELEMETRY_OPTOUT='1'           # 必设，原因见 §6.2
```

三条最常用的命令：

```powershell
dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead -v q --nologo   # 构建
dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead -v q --nologo -m:1 -p:BuildInParallel=false   # 1502 例
.\tools\shot.ps1 -Page home -NoBuild -Configuration Lead -DataDir <演示数据> -Out <png>                        # 截图
```

### 2.1 构建

**不要用 `dotnet build NebulaLauncher.sln -c <自定义配置>`**：sln 只定义 Debug/Release，会报 `MSB4126`。按项目路径构建。
多人/多 agent 并行时各用各的 `-c`（`Lead` / `inst` / `pf2` / `comp`…），产物在 `bin/<配置>`，互不锁 obj。

### 2.2 测试

`dotnet test` 在本机不可用（testhost 被沙箱拒绝，§6.1）。用进程内运行器：

```powershell
dotnet run --project tools\TestRunner\TestRunner.csproj -v q --nologo
# 总计: 1502, 通过: 1502, 失败: 0
dotnet run --project tools\TestRunner\TestRunner.csproj -v q --nologo -- PackFormat   # 按类名过滤
```

`-m:1 -p:BuildInParallel=false` 在项目图 ≥3 时是必须的（§6.2）。`[InlineData(null)]` 会被 C# 解析成零参数用例，写 `[InlineData((string?)null)]`。

### 2.3 截图自查

不用系统截屏（窗口被遮挡时会抓到别人的界面），让应用自己渲染视觉树：

```powershell
.\tools\seed-demo-data.ps1 -DataDir D:\agent_workspace\.probe\data-me -Force

.\tools\shot.ps1 -Page versions -NoBuild -Configuration Lead -DataDir <数据> -Out <png>
.\tools\shot.ps1 -Page settings -Section game -Scroll 1000 -Size 1240x1000 -NoBuild -Configuration Lead -DataDir <数据> -Out <png>
.\tools\shot.ps1 -Page home -Theme light -NoBuild -Configuration Lead -DataDir <数据> -Out <png>
```

页面键：`home` `versions` `download` `mods` `library` `accounts` `servers` `worlds` `logs` `help` `settings`。
设置页分区键：`general` `java` `game` `download` `content` `appearance` `storage` `about`。
主题键（`-Theme`）：`dark`（默认）`light` `system`；**浅色改动必须两种主题各截一次**。

**交互态开关**（截图模式不能点击，这些参数把页面推进到「点过之后」的状态；都是正式保留能力，不是临时代码）：

| 参数 | 作用 |
|---|---|
| `-InstanceEditor` | 版本页展开第一个实例的设置面板 |
| `-InstanceNotes` | 版本页打开完整备注浮层 |
| `-ShowHidden` | 版本页打开「显示已隐藏」 |
| `-SelectAllMods` | 模组页全选（展示批量操作条） |
| `-VersionComponents` | 版本页打开组件面板（§6.32） |
| `-Section running` | 版本页让本进程自己持有第一个实例的锁，截出「运行中」徽标（§6.35） |
| `-DownloadDemo` | 下载页注入 6 条演示任务（完成 / 下载中 / 排队 / 失败），按钮有真实可点状态 |
| `-DownloadDemoPaused` | 同上并推进到暂停态（状态行 + 按钮变「继续」+ 排队行「等待」） |
| `-MissingJava` | 首页进入「缺少 Java 25」的启动失败态（安装按钮 + 体积估算，不联网） |
| `-MissingJavaVersions` | 版本页摆出同一件事的窄版区块（状态条 + 缺 Java 卡片），验证按钮不换行 |
| `-LogFollow` | 日志页进入跟随态（写一个 24 行的演示日志到 `<data>/logs` 再追加 6 行，chip「跟随中」） |

新增这类参数要改三处：`Services/ScreenshotMode.cs` 解析、`App.AttachScreenshot` 导航后设置 ViewModel 状态、`tools/shot.ps1` 透传。

`--size` 会被屏幕高度限制（1240×1700 实际出 1084 高），长页面下半部分用 `--scroll <px>` 截。

### 2.4 发布

```powershell
.\build.ps1 -Publish -Mode minimal                       # → pub\（22.7 MB / 3 文件）
.\build.ps1 -Publish -Mode minimal -OutDir D:\tmp\out    # 输出目录被占用时用
```

| 形态 | 体积 | 前提 |
|---|---|---|
| `minimal`（默认） | 22.7 MB / 3 文件 | 目标机需 .NET 8 Desktop Runtime |
| `fdd` | 26.2 MB / 48 文件 | 同上 |
| `portable` | 21.8 MB / 1 文件 | 需要 `%TEMP%\.net\<app>` 可写 |
| `folder` | 98.5 MB / 231 文件 | 无 |

不携带运行时是刻意的（PCL/HMCL 也都不带）。渲染用 WGL + Software，所以不需要 ANGLE 的 `av_libglesv2.dll`。

### 2.5 安装探针（不经过 UI 的端到端安装）

```powershell
dotnet build tools\InstallProbe\InstallProbe.csproj -c Lead -v q --nologo
dotnet .\tools\InstallProbe\bin\Lead\net8.0\NebulaLauncher.InstallProbe.dll D:\agent_workspace\.probe\probe 1.20.1 Fabric
```

参数：`<数据目录> <MC版本> <加载器> [加载器版本] [official|bmclapi]`。失败时打印完整异常链与文件名+URL。
实测耗时：Fabric 1.20.1 约 12 分钟（3650 文件 / 660 MB），Forge 1.20.1 约 52 秒（缓存命中时）。

### 2.6 真机诊断入口

| 命令 | 用途 |
|---|---|
| `NebulaLauncher.exe --probe-dir "<目录>"` | 用启动器自身进程复现写入失败，输出 `writable=YES/NO` |
| 日志 | `<data-dir>\logs\launcher-yyyyMMdd.log`；内置「日志」页可直接看 |
| 诊断信息 | 设置 → 关于 → 复制诊断信息（不含账户/令牌） |

---

## 3. 技术栈与「为什么」

| 层 | 选型 | 不能换的理由 |
|---|---|---|
| 运行时 | .NET 8，**单 TFM `net8.0`** | 未来要 macOS。禁 `net8.0-windows`、WinForms、编译期 `Microsoft.Win32.Registry` |
| UI | Avalonia 11.3.22 | 无 WebView、跨平台、Skia 渲染、XAML 动画 |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | `[ObservableProperty]` / `[RelayCommand]` 源生成器 |
| DI | Microsoft.Extensions.DependencyInjection | 构造注入，组合根在 `App.BuildServices()` |
| MC 核心 | CmlLib.Core 4.1.0 系 | 版本清单、文件校验、启动参数、微软登录。**不要重写协议层** |

平台差异全部走 `IPlatformService` 的**运行时分支**，不写 `#if WINDOWS`——这是 `dotnet publish -r osx-arm64` 不用改 Core 的唯一原因。

---

## 4. 目录与分层

```
src/NebulaLauncher.Core/        业务层，零 UI 依赖，可单测
  Abstractions/                 服务契约（只追加；新成员用默认实现，见 §5.1）
  Models/                       DTO / 设置 / 实例 / 内容 / 版本 / 下载
  Paths/ Settings/              路径与回退、设置归一化
  Logging/                      日志写入（FileLogger / LogRedactor）、尾部倒读（LauncherLogReader）
  Diagnostics/                  共享的诊断快照（崩溃导出与「复制诊断信息」共用）
  Platforms/                    IPlatformService 实现、平台纯函数（CloseBehavior / WindowMemory）
  Accounts/                     微软设备码 + authlib（AuthlibAuthService）+ 账户落盘
  Versions/                     版本扫描、服务端 jar 下载、pack_format 表、收藏
  Installing/                   安装、两档修复、安装清单、组件检查、安装器 stdout 日志
  Launching/                    启动参数、GC/编码、内存、前置检查、进程监视、崩溃归因、
                                authlib 注入、JVM 预设解析、参数切分、崩溃导出
  Instances/                    实例 CRUD、克隆/回收站/清理、当前实例上下文
  Downloads/                    DownloadQueue（快照 / 外部批次 / 批次历史）、镜像重写、
                                限速、文件复用、下载源 governor
  Java/                         Java 探测、约束表、Mojang runtime 清单
  Mods/ Backups/ Servers/       在线内容、整合包导入导出、服务器 Ping、批量文件操作
  Transfer/                     配置包导出 / 导入
  Help/                         内置帮助目录（纯数据 + 纯函数过滤）
src/NebulaLauncher.App/         Avalonia UI
  Program.cs                    入口：诊断参数、按需提权、单实例、Avalonia 启动
  Themes/                       Tokens.axaml（双主题令牌字典）+ Controls.axaml（控件样式）
  Controls/                     自绘控件：AuroraBackground / RingProgress / PageHost（均跟随主题）
  Views/ ViewModels/            11 个页面
  Services/                     导航代理、窗口动作代理、主题、托盘、任务栏进度、动效开关、截图模式、
                                首帧预热（`StartupState`，读设置与账户）
  Assets/tray.png               托盘图标（32×32）
tests/NebulaLauncher.Tests/     xunit（1502 例；**同时引用 Core 与 App**，见 §6.25）
tools/                          TestRunner / InstallProbe / shot.ps1 / seed-demo-data.ps1 / ApiDump
```

依赖方向：`App → Core`。Core 不引用 UI 类型；UI 只依赖 `Abstractions/` 的接口。

---

## 5. 不能改的约定

### 5.1 契约

`src/NebulaLauncher.Core/Abstractions/` 是唯一边界：

- 实现方可加方法，**不改已有签名**；新增成员一律带默认实现（DIM），保证「契约先落地、实现后跟上」的中间态始终可编译。
- 实例那几个新方法的默认实现抛 `NotSupportedException`：让「UI 接了按钮但服务没实现」在第一次点击时立刻暴露。
- 数据模型可以加字段，不能改字段名（`settings.json` / `accounts.json` / `instance.json` / `favorites.json` 按字段名序列化）。
- **给模型加字段后必须同步改它的 `Clone`**。`InstanceService.Clone` 就因为漏了 `Loader` / `MinecraftVersion` / `IsolationEnabled`，
  让「按加载器分组」在保存一次后全部退化成原版——单测抓住了它。`AppSettings.Clone` 对 `JvmPresets` 做深拷贝，同理。
- **`ISettingsService.LoadAsync` 是幂等的**（§6.29）。要重新读盘用 `ReloadAsync`。
- **`IAccountService.LoadAsync` 同样是幂等的**（§6.71）：启动时由 `App/Services/StartupState.cs` 在建窗口前预热一次，
  页面里再调是空操作；要读回外部改写的 `accounts.json` 用 `ReloadAsync`。
  新增「构造时不读盘、要靠页面触发加载」的服务时，必须同时给它一个首帧之前的预热点，否则就是 R59 那个 bug 的复制品。

### 5.2 页面注册

页面按 partial 文件分组注册：`MainWindowViewModel.HomePages.cs`（首页 / 版本 / 下载）、
`ContentPages.cs`（模组 / 资源 / 账户 / 服务器）、`SystemPages.cs`（世界 / 日志 / 帮助 / 设置）。

新增页面：建 `ViewModels/XxxViewModel.cs` + `Views/XxxView.axaml` → `NavigationItem.cs` 加 `NavigationKeys.Xxx` 与图标 →
`MainWindowViewModel.cs` 的 `AllKeys` / `PlaceholderMeta` / `BuildNavigation` 三处登记 → 对应 partial 里 `RegisterPage`。
`ViewLocator` 按命名约定找 View，不需要 DI 注册页面。

### 5.3 XAML

- `AvaloniaUseCompiledBindingsByDefault` 已开，**每个 UserControl 根必须写 `x:DataType`**；绑定写错会在构建期直接报错（最快的防线）
- 颜色一律 `{DynamicResource ...}`，不写颜色字面量
- **两套主题（深 / 浅）键名完全相同**：`Tokens.axaml` 的 `ThemeDictionaries` 里 `Dark` 与 `Light` 必须同键集。
  新增令牌时两个字典各加一次，只加一边会让另一个主题在运行期抛资源找不到
- 自绘控件（`Controls/*.cs`）不读资源字典，必须自己订阅 `ActualThemeVariantChanged` 或在 `ApplyThemeDefaults` 里按当前变体重设
- 动效时长走 `Services/Motion.Dur(...)`，关闭动效时返回 `TimeSpan.Zero`
- 文件/文件夹选择留在 code-behind，选完把路径写回 ViewModel
- 抬头区（`MainWindow.axaml`）用 `Grid ColumnDefinitions="Auto,*"`，禁止横向 `StackPanel` 塞进 `*` 列（§6.18）
- 列表页超过 50 项用 `ListBox`（虚拟化）；多选**不用** `ListBox.SelectionMode`，用每行复选框（§6.22）

### 5.4 用户数据文件

`accounts.json` / `servers.json` / `favorites.json` / `settings.json` 同在 `<data>/` 下，但**语义不同**：

- 用户数据（账户、服务器、收藏）：独立文件、原子写（`.tmp` → `File.Move`）、坏文件静默重建、发 `Changed` 事件。
- 程序配置（设置）：`settings.json`，只有它能被「恢复默认」清掉。

新加一类用户数据时照 `Versions/VersionFavorites.cs` 抄结构，**不要塞进 `settings.json`**（§6.25）。

---

## 6. 踩过的坑

按「构建环境 → 平台 → 安装/下载 → 启动 → 内容 → UI → 数据」排列。每条都是踩过的，不是推测。

### 6.1 `dotnet test` 在本机不可用

testhost 启动被沙箱拒绝（`Win32Exception 5`），`dotnet test` / `vstest` 都在启动阶段中止。用 `tools/TestRunner`。
它反射测试程序集、复用 xunit 断言，支持 `[Fact]` 与 `[Theory]+[InlineData]`；`MemberData` / `IClassFixture` 打印 SKIP。

### 6.2 本机两个构建环境问题

1. **MSBuild 多节点命名管道被拒**：项目图 ≥3（TestRunner + Tests + Core/App）时裸 `dotnet run` 会停在 restore 后报「生成失败」而 error 数为 0。
   `Directory.Build.props` 已设 `<BuildInParallel>false</BuildInParallel>`，命令再加 `-m:1 -p:BuildInParallel=false`。
2. **Avalonia BuildServices 遥测写 `%LOCALAPPDATA%\AvaloniaUI\BuildServices\buildtasks.log`** 被拒时构建报 `MSB4018 AvaloniaStatsTask`。
   构建前设 `$env:AVALONIA_TELEMETRY_OPTOUT='1'`。

### 6.3 XAML IL 增量构建不可靠

产物启动报 `No precompiled XAML found for NebulaLauncher.App` 时，说明 XAML IL 注入步骤被中断过而增量构建认为不用重做。
删掉对应配置的 `obj/<配置>` 与 `bin/<配置>` 重建即可，不要怀疑代码。

### 6.4 权限模型：Medium 进程写不了用户目录

实测：同一份 exe，用户双击（Medium 完整性）写 `%APPDATA%` 与自定义游戏目录都被拒；提权到 High 后正常。ACL 无 Deny 条目，是进程级限制。

处理（不要退回旧方案）：

1. `Program.TryRelaunchElevated`：启动时检测数据根是否可写，不可写且非管理员则 `runas` 重启自身；UAC 点否就继续。
2. `LauncherPaths` 回退链：数据根 `%APPDATA%` → `%LOCALAPPDATA%` → `<exe>\data`；默认游戏目录 `%APPDATA%\.minecraft` → `<数据根>\minecraft`。
   **用户显式指定的目录不回退**，写不了就报错。
3. `--probe-dir <目录>` 用启动器自身进程复现写入失败。

**不要用「写随机临时文件」判断目录可用**：那正是安全软件最敏感的东西，会把可用目录判成不可用。

### 6.5 安装链的三处库缺陷

1. **并发写同一文件**：CmlLib 的 `ParallelGameInstaller` 按 CPU 核数并发，文件清单里有指向同一路径的重复条目 → `IOException`。
   `NebulaGameInstaller` 改为按 Path 去重 + 固定并发 8 + 瞬时重试；**必须调基类 `Download(...)` 再 `ExecuteUpdateTask(...)`**。
2. **installer 临时目录不存在**：CmlLib 指向 `<temp>/<随机>/installer.jar` 却不建父目录。安装期间把 `TMP`/`TEMP` 指到 `cache/loader-install`。
3. **镜像缺文件**：BMCLAPI 不镜像全部路径（Forge maven 会 403）。由候选回退处理（§6.6）。

### 6.6 下载候选回退与镜像

`DownloadMirror.Candidates(url, source, preferred)` 返回「首选 + 兜底」两个候选；`MirrorRewritingHandler` 关闭自动重定向手动跟随：
镜像 302 到已知下载域之外 → 放弃镜像；404/403/5xx/连接失败 → 换下一个候选。每跳重建 `HttpRequestMessage`。

`BmclApiMap` 把 `libraries.minecraft.net` 之外的 maven 映射到 `bmclapi2.bangbang93.com/maven`。
**新加下载源要同步 `IsKnownDownloadHost`**，否则镜像重定向会被判成不可信而放弃。

### 6.7 Java 需求：以版本 JSON 为准

硬编码三段式有真实偏差：1.17 实际要 Java **16**（`java-runtime-alpha`），26.x 要 **25**（`java-runtime-epsilon`）。
`JavaRequirementTable.ReadFromDisk` 先读 `versions/<id>/<id>.json` 的 `javaVersion.majorVersion`（沿 `inheritsFrom` 找原版），读不到才查内置表。

### 6.8 Java 约束是区间，不是下界

`JavaConstraint` 记录 `(mandatory, 游戏版本区间, Java 版本区间, 需要加载器?)`：

- 1.12.2 Forge 走 LaunchWrapper → Java **≤ 8**
- 1.16.5 Forge 走 MODLAUNCHER_8 → Java **≤ 17**
- `MODDED_JAVA_*` 是 suggested，不升成强制上界（否则 1.16.5 Forge + Java 17 会被拦死）

`GameLauncher` 必须用带 `LoaderKind` 的 `ResolveForVersionAsync` 重载——只传原版版本号判不出加载器。

### 6.9 前置检查拆两段

`PreflightChecker.CheckEnvironmentAsync`（不碰 Java，2 秒预算）+ `CheckJava`（同步纯函数，零探测）。
单段实现里 Java 探测会吃满预算，导致「Java 版本不匹配」这条最有价值的 Blocker 在冷启动时被静默跳过。
`CheckJava` 参数表里没有 `IJavaService`，由测试用反射钉住。

三条兜底：`GC.GetGCMemoryInfo()` 返回 0 时跳过内存项；环境段整体超时返回已收集项；每个检查项内部各自 catch。

### 6.10 版本隔离与「当前实例」

三层：`InstanceLocator`（纯函数）+ `IsolationPolicy`（三态 never/modded/always）+ `InstanceContext`（当前实例、目录解析、目标枚举）。

**实例目录就是版本目录**：`<game>/versions/<版本名>/`，与 PCL2（`PathIndie = PathVersion`，
`ModMinecraft.vb:277`）和 HMCL（`getRunDirectory`）的版本隔离布局一致。四条连带硬约束：

- **一个版本只能有一个实例**。`CreateAsync` 对同版本是幂等的：已有实例直接返回，不重建目录、不覆盖 `instance.json`。
- **克隆必须生成新版本目录**（`<原名>-副本`，复制版本 JSON/JAR 并改写 JSON 的 `id`），否则会复制到自身。
- **删除 / 移入回收站作用于整个版本目录**，恢复时按 `meta.json` 的版本名回到 `versions/`；目标目录已存在则拒绝。
- **换加载器会换目录**：旧目录里的 mods/saves 不自动搬（PCL2 / HMCL 同样如此），成功后必须摘掉旧目录的
  `instance.json`，否则旧版本目录会被下次扫描当成第二个实例。

**所有内容页一律经 `IInstanceContext` 取目录**，不再直接读 `ILauncherPaths.MinecraftDirectory`——
旧实现里模组页永远扫全局目录，用户在实例里启动游戏、模组页却看不到那些文件。

`ResolveLaunchTargetAsync` 顺序：已有实例 → 按策略自动建 → 全局。**永不抛异常**（建实例失败退回全局，比启动不了好）。
`SelectAsync` 要做状态快照幂等，否则「页面进入 → 加载 → Changed → 重建」会无限循环。

### 6.11 CmlLib 的命令行解析器会吃掉 Windows 路径反斜杠

`Parser.ParseCommandLineToArguments` 把 `\` 当转义符：`-Djava.library.path="C:\Program Files\Java\bin"` → `C:Program FilesJavabin`。
用户完全看不出是自己写错了还是启动器吞了。现在切分走 `Launching/LaunchArgumentSplitter`（只认双引号、不解释反斜杠），
`CoreLaunch_ArgumentSplitterTests` 用 8 条判据钉住。authlib 的 `-javaagent:<含空格路径>` 也走这条路。

### 6.12 authlib-injector 的两个硬约束

1. **prefetch 必须是 `GET <apiRoot>` 的原始响应字节做 Base64**。先解析成对象再序列化会改变空白与字节序，
   injector 解码后校验失败，表现是「登录成功但游戏里没有角色」。
2. **jar 缓存要能断网兜底**。`authlib-injector.yushi.moe` 不在 BMCLAPI 映射表里；清单拉不到时退化为
   「目录里已有的最高版本 jar」，否则一次断网就让第三方账户彻底不可用。

### 6.13 CurseForge：API key 已内置，走官方接口

`CurseForgeService.EmbeddedApiKey` 是编译期 `const`，换 key 只改这一处并重新出包——它会原样进入发布产物。
用户不再填写 key：`AppSettings.CurseForgeApiKey` 字段、`SettingsService.Normalize` 的归一化、
`ProfileSettingsRedaction.Fields` 里的 `curseForgeApiKey`、`ICurseForgeService.IsConfigured` / `IsSlugOnlyMode`
全部删除，「未配置 → 返回空集合 + 引导去设置页填 key」的降级分支随之消失。

- **搜索**：官方 `/v1/mods/search`，空搜索按下载量（`sortField=2`）取热门；单源 CurseForge 支持「加载更多」翻页。
- **项目链接**：粘贴 CurseForge 项目链接时走 `api.cfwidget.com` 做 slug 精确解析——它只有项目详情端点，
  没有搜索端点，而官方搜索会把整条 URL 当关键词。纯关键词（含 `jei` 这种 slug 形态）走官方搜索。
- **直链**：`CurseForgeCdn` 按 fileId 推导 `mediafilez.forgecdn.net` 直链，只用于 cfwidget 路径没有直链的场景；
  官方接口 `downloadUrl` 为 null 时仍拒绝安装（作者关闭了第三方分发）。
- **界面**：设置页「在线资源」只剩「默认数据源」与「Base URL 覆盖」，API Key 输入框、「显示」勾选、
  「前往申请」按钮与免 key 模式说明条删除；资源页的「免 key 模式」与「双源缺少 API Key」两条提示删除。
- **双源**：CurseForge 不再有「未配置」状态，档位只受「服务是否接线」影响。
- **排序**：`sortField` 固定为 2，所以资源页的排序下拉仍只对 Modrinth 生效。

实测结论保留：`media.forgecdn.net` 是 403，不要用；`mediafilez.forgecdn.net` 与 `edge.forgecdn.net`
两个域名的可用性不一致。key 只加在官方 API 请求上；`CurseForgeBaseUrl` 指到镜像时，镜像必须自己处理或忽略 `x-api-key`。

### 6.14 崩溃归因的两条判据

- **窗口**：先认标准输出里的 `#@!@# Game crashed! Crash report saved to:` 路径，读不到才退回 `crash-reports/` 最新 txt。
- **退出**：`GameWatcher.JudgeExit` = 崩溃关键词 → 「没走到材质加载就退出」→ `exit≠0 且发行年 ≥ 2012`。
  只按退出码判会把「玩家点关闭」当成崩溃。
- 结论只能写「疑似」：包名黑名单是启发式。崩溃后自动把报告、hs_err 与 `logs` 打包到 `crash-reports/archive`。

### 6.15 文件复用只碰四类子目录

`FileReuseService` 只索引 `mods` / `resourcepacks` / `shaderpacks` / `saves`。libraries 与 assets 的 hash 目录结构由 CmlLib 管，
凭 SHA1 往里塞文件会破坏它的完整性判定。复用失败一律静默降级为正常下载，不能阻断队列。

### 6.16 托盘与「关闭到托盘」：三种失败都要有出口

判据在 `Core/Platforms/CloseBehavior.ShouldExitOnClose(closeToTray, trayAvailable, isScreenshotMode)`，
只有「开关打开 + 托盘真的建出来 + 非截图模式」才允许隐藏。三处兜底不能省：

1. **托盘建失败 → 退回退出**。窗口藏起来又没有托盘入口，是一个只能去任务管理器结束的幽灵进程。
2. **截图模式 → 不建托盘、不拦关闭**。否则 `shot.ps1` 会等一个永不退出的进程。
3. **系统注销 / 关机 → 放行**（`CloseReason` 为 `OSShutdown` / `ApplicationShutdown`）。

托盘菜单的「退出」必须先置 `ExitRequested` 再 `desktop.Shutdown()`：`Closed` 里要收图标，
而 `Closing` 里要能分辨「用户点关闭（可能该隐藏）」与「用户选退出（必须走）」。

### 6.17 浅色主题：页面不改，令牌改

页面 XAML 里没有颜色字面量，所以浅色是**换字典**：`Tokens.axaml` 的 `ThemeDictionaries` 下 `Dark` / `Light` 两套同名键。
三处例外必须单独处理，它们是自绘或内嵌数据：

1. `AuroraBackground`（窗口背景）——不跟随变体会在浅色下留一整片深色。
2. `RingProgress`（环形进度）——轨道默认 `#1FFFFFFF` 在白卡上整圈消失；
   而且**样式 setter 打不过构造函数里的本地值**，只能在代码里按变体重设。
3. `ServersView` 的 MOTD 协议色（`§a` = `#55FF55`）——白底对比度 1.3:1，需要一张浅色等价色表。

`Controls.axaml` 里 `ListBoxItem:selected` 也要覆盖：Fluent 默认用系统强调色 60% 实心，浅色下会变成整块高饱和紫。

### 6.18 横向 StackPanel 放进 `*` 列会吃掉副标题

抬头区原来是 `<Grid ColumnDefinitions="*,Auto">` 里放横向 `StackPanel`。横向 StackPanel 的测量宽度只取子元素之和，
窗口压到 1024 时它先被压成标题宽度，副标题 `TextBlock` 拿到 0 宽，整条消失（1240 下正常，只在窄屏截图时暴露）。

现在改 `ColumnDefinitions="Auto,*"`：标题 Auto、副标题占剩余并 `TextTrimming="CharacterEllipsis"`。
**窄屏截图是必做项**，只截 1240 会漏掉这类问题。

### 6.19 下载任务面板：外部批次与安装链

`DownloadQueue` 能给出逐文件快照（`Snapshot()`）、节流事件（5 次/秒）与内存批次历史（20 条）。
安装游戏本体走 CmlLib 的 `NebulaGameInstaller`，**它不经过 `DownloadAsync`**，所以走「外部批次」：

```
var scope = queue.BeginExternalBatch("安装 1.20.1");   // 已有活动批次时抛 InvalidOperationException
installer = new NebulaGameInstaller(http, scope);       // 逐文件 Report(排队/下载中/完成/跳过/失败)
scope.Complete();                                       // 写历史；未 Complete 就 Dispose 视为取消
```

三条设计约束：

1. **只有一条活动批次**。外部批次与普通 `DownloadAsync` 互斥：安装进行中，`JavaService` / `ModpackService` /
   `AuthlibInjectorService` 的普通下载会被拒绝（`InvalidOperationException`，中文文案）。要并发得先扩队列模型。
2. **进度只在能报的粒度上报**。CmlLib 的 `GameFile.Download` 没有文件内字节进度，所以「下载中」的行显示
   未知总量（`—`）而不是 `0%`；`GameFile.Hash` 不保证是 SHA1，不能塞进 `ExpectedSha1`，否则重试永远校验失败。
3. **Forge / NeoForge 的 maven 文件其实也在面板里**（走我们的安装器），缺的只是阶段可辨性，见 §6.26。

### 6.20 窗口尺寸记忆：首帧之前就要读到设置

`MainWindow` 的宽高由设置决定（`LauncherWindowWidth/Height/Maximized`），但设置是**异步加载**的。
处理：`App.OnFrameworkInitializationCompleted` 建窗口之前同步 `LoadAsync()` 一次。三条判据：

1. **截图参数优先**（`--size` > 设置 > XAML 默认），否则无人值守截图会被用户设置改坏尺寸。
2. **只在 `WindowState == Normal` 时更新尺寸**；最大化时只记 `Maximized = true`，保留上次 Normal 尺寸。
3. **`RememberWindowSize=false` 时两端都不写**，判据在 `Core/Platforms/WindowMemory.ShouldPersist`（有单测）。

`1024×660` 请求会得到 `1039×697`：`MinWidth/MinHeight` 是**客户区**下限，加非客户区（15/37）才是窗口总尺寸。历史窄屏截图全是 1039×697。

### 6.21 快捷键：Esc 要按页面分派

`KeyBindings` 挂在 `MainWindow` 上，五个键里只有 Esc 需要上下文：

- **Esc** 关「当前页真的存在的那个浮层」：版本页 9 个面板、世界页导入预览与删除确认、账户页三态登录。
  实现集中在 `MainWindowViewModel.Escape()`，调各页已有的 `CancelXxxCommand`。
  **焦点在输入框里时 Esc 必须让位**（`IsTextInputFocused()` 沿视觉祖先链判断 `TextBox` / `AutoCompleteBox` / `NumericUpDown`）。
- **F5 / Ctrl+O** 在输入框里仍要生效。
- F5 走 `PageViewModelBase.ReloadCurrentAsync()`。不要命名为 `RefreshAsync`：7 个页面都有同名私有方法，会撞名。

### 6.22 批量操作：成功判据取磁盘，不取返回值

`ContentService.SetEnabledAsync` 把 `IOException` 吞成日志后**正常返回**。批量启停若拿返回值当成功信号，
「jar 被游戏占用」会被计成成功，用户看到「已禁用 3 项」但文件还在。

所以 `Core/Mods/ContentBatch.cs` 每项改完后用 `IsEnabledOnDisk(path)` 三态复核（true/false/null），
失败项留在列表并计入汇总。整个循环放 `Task.Run`（`SetEnabledAsync` 是同步实现），700 项实测 240–300 ms。

多选用「`ItemsControl` + 每行复选框」而不是 `ListBox SelectionMode="Multiple"`：
`IsChecked` 是纯 bool、编译绑定能校验；`SelectedItems` 绑错只会在运行期静默失效，而截图模式点不动，等于无法验证。

### 6.23 下载源三档：`Auto` 的降级状态不落盘

`DownloadSource` 是 `Auto` / `Official` / `Mirror`，其中 `BmclApi = Mirror` 是**同值别名**——历史 `settings.json` 里的
`"BmclApi"` 由 `DownloadSourceJsonConverter` 读成 `Mirror`，写出去固定 `"Mirror"`。

`Auto` 语义：**先镜像、劣化后改官方，10 分钟窗口过后回到镜像**。三条边界：

1. **降级状态只在内存里**（`DownloadSourceGovernor`）——写进设置会让「今天镜像慢」变成永久配置。
2. **判据要注入时钟**（`Func<DateTimeOffset>`），否则 10 分钟窗口没法测。
3. **手动档免疫**：选 `Official` / `Mirror` 时 governor 不参与排序。

采样口径的真实限制：传输层只能测到**响应头**为止的时间，测不到完整下载耗时。所以「镜像慢」定义为
「镜像放弃前耗时 − 官方回退成功耗时 > 2 秒」；镜像成功且慢的情况没有可比样本，不计入。

### 6.24 数据包：文件与目录两种形态

`ContentKind.DataPack` 落两处：全局 `<gameDir>/datapacks/` 与存档级 `<gameDir>/saves/<world>/datapacks/`。
形态有两种——`.zip` 文件，以及**一个目录**（Minecraft 把 `datapacks/` 下每个子目录当数据包）。三处坑：

1. **`ContentBatch.IsEnabledOnDisk` 写死 `File.Exists`**，目录形态改名成功会被判成失败。
   数据包走 `ContentService.IsEnabledOnDisk`（`Directory.Exists` 优先，再退回文件）。
2. **`ContentTarget.DirectoryNameOf` 必须有 `DataPack` 分支**，否则落进 `_ => "mods"`。
3. **目录形态没有扩展名**：启用/禁用是 `<name>` ↔ `<name>.disabled`（对整个目录改名）。

### 6.25 用户数据独立落盘；测试项目同时引用 Core 与 App

- `favorites.json` 与 `accounts.json` / `servers.json` 同级，**不进 `settings.json`**：它是用户数据不是程序配置。
  版本删除后条目保留（重装同名版本星标自动回来），有测试钉着。
- `tests/NebulaLauncher.Tests.csproj` 引用 Core **与** App，App 加了 `InternalsVisibleTo`。
  原因是 `ShellProgress` 的节流逻辑与窗口生命周期强相关，下沉 Core 语义更差。
  **限制**：测试里不得触碰 Avalonia 类型（窗口、控件、Dispatcher），只能测纯逻辑与接口替身；
  一旦某条测试需要 `Application.Current`，说明被测逻辑放错了层。

### 6.26 Forge 的进度回调一直存在，缺的是阶段

历史文档写过「Forge / NeoForge 的 maven 下载不上报进度」。**这是错的**：
`CmlLib.Core.Installer.Forge 2.0.0` 的 `ForgeInstallOptions` 就有 `FileProgress` / `ByteProgress` / `InstallerOutput`，
且这些文件走的就是我们的 `NebulaGameInstaller`。真正的缺陷是两件事：

1. **比例跨阶段回退**：原版文件一批走到 97.5%，加载器阶段开新批又从 3.0% 开始——往回跳 94 个点。
   修法：`InstallStagePlan` 把三阶段映射到固定区间（原版 0–50 / 加载器 50–85 / 处理器 85–100），**只增不减**。
2. **阶段不可辨**：处理器阶段要 35 秒却不产出下载事件，进度停在 100% 干跑。
   修法：用安装器 stdout 的第一行作为「进入处理器阶段」的信号，并在下载页显示阶段文案。

加载器阶段内部的多个子批次（内层原版复检 1s / 安装器 jar 5s / maven 11s）用「剩余区间减半」逐批分配——
平均分会让人误以为卡住。

**教训**：写进文档的「做不到」必须有 IL / 源码级否定证据。当年那条结论是从「面板里没看到 maven 文件名」推出来的。

### 6.27 崩溃导出：脱敏要脱到能发出去，但不能脱到看不懂

`CrashExportService` 产出 `nebula-crash-<时间>.zip`，六个条目：分析结论、游戏输出尾部、`hs_err`、
启动器日志尾部、**mods 列表**、系统快照。

脱敏规则（`LogRedactor`）四条，缺一条都会导致「发出去才知道漏了」：

1. `--accessToken <值>` / `accessToken":"…"` / `Authorization: Bearer …` —— 命令行与 JSON 两种形态。
2. `Setting user: xxx` / `Logged in as xxx` / `UUID of player` —— 账户名的三种真实形态。
3. `C:\Users\<name>\` / `/home/<name>/` / `/Users/<name>/` → 用户名段替换，**路径其余部分保留**。
   全替换成 `<path>` 会让报告失去判读价值。
4. `clientToken` / `uuid`。

副作用：启动器自己的日志里也看不到真实用户名了。这是有意的（隐私优先）。
系统快照必须与「设置 → 关于 → 复制诊断信息」共用 `DiagnosticsSnapshot.Create`，两处各写一份必然漂移。

### 6.28 数据包在线安装：目标必须让用户选

全局 `datapacks/` 影响所有世界，存档级 `saves/<world>/datapacks/` 只影响那一个。默认猜一个就是错：

- 安装按钮是「下载并选择安装位置…」，浮层里列全局 + 每个存档。
- 确认前用 `ValidateDataPackTarget` 复核存档是否还在——浮层打开期间存档可能被删。
- 数据包搜索**不能带 loader facet**（数据包没有 loader），带上去结果为空；`ModrinthService` 只对 `Mod` 加 loader。

### 6.29 `LoadAsync` 是幂等的，重读要用 `ReloadAsync`

六个调用方（壳、实例上下文、账户页、设置页、资源页、服务器页）原本各自 `LoadAsync`，每次都读盘、反序列化、
触发 `Changed` → 主题与游戏目录被反复应用。实测一次启动出现 3 次「已加载设置」，现在 **1 次**。

`ISettingsService.LoadAsync` **只加载一次**，第二次起直接返回。需要「外部改写后重新读取」的调用方走 `ReloadAsync`：

- `ProfileBundleService` 导入配置后必须 `ReloadAsync`（改成 `LoadAsync` 会让导入**静默不生效**）；
- 将来接 `FileSystemWatcher` 也走 `ReloadAsync`。

改这条语义时**必须全仓搜 `LoadAsync` 的调用方**：凡是「写完盘再读一遍以刷新内存 / 触发事件」的都要换成 `ReloadAsync`。

### 6.30 日志首行拼接：进程被杀在半行上

截图模式用 `Environment.Exit(0)` 退出，正在写的那一行可能没写完（没有换行符）。
下个进程 `File.AppendAllText` 会接在同一行后面，日志里出现
`页面转场时长为 02026-10-22 12:50:52.328 [INFO] …` 这种看起来像乱码的行。

`FileLogger.EnsureTrailingNewline` 在**首次写当日日志前**检查末尾字节是不是 `\n`，不是就补一个；只检查一次。
读不出来就照常写——排版问题不值得让日志失败。

### 6.31 配置包：只搬「非机器相关」的东西

`Transfer/ProfileBundleService.cs` 导出 `nebula-profile-<时间>.zip`：

- 含 `favorites.json` / `servers.json` / 脱敏后的 `settings.json` / `manifest.json`；
- **绝不包含** `accounts.json`（令牌不能跨机器搬），manifest 里写死 `containsAccounts:false`；
- `settings.json` 导出时删掉 `lastAccountId` / `lastInstanceId` / `minecraftDirectory` / `javaPath`；
- 导入默认只合收藏与服务器；设置要用户勾选后才按 **9 个白名单键**逐键覆盖（包里没出现的键不动，
  否则会用反序列化默认值顶掉本机配置）。

改白名单时要同步改测试里的「白名单键集」断言，否则「新加一个键但导入不生效」很难被发现。

### 6.32 组件面板：分组按 `ManagedFiles`，不按 `Components`

`ComponentInspector` 按安装清单里**受管文件的 `Component` 字段**分组。写清单时 `libraries` 只出现在
`ManagedFiles` 上、不在 `Components` 里——只按后者分组会把「库」整块漏掉，用户看到 5 个组件里有 2 个文件有问题，
却不知道是哪个库。

另外两条：

- **mtime 容差 2 秒**（与 `McInstallManifest` 内部同一个数字）。FAT 的时间精度是 2 秒，容差小于它会产生假「已改动」。
- **「只修复缺失文件」只在 `MissingCount > 0` 时可用**。单纯 mtime 改动不给这个入口——那是用户手动替换过文件，重下会覆盖他的改动。

### 6.33 NeoForge：桥接绕开 facade（已重写，取代旧的「三层原因」）

**旧状态：NeoForge 安装不可用。** 三层原因（留着当历史，别再顺着它查）：

1. 桥接原先用反射 `Activator.CreateInstance(type, launcher, httpClient)`，而 `CmlLib.Core.Installer.NeoForge 4.0.1`
   的 `NeoForgeInstaller` **只有 `(MinecraftLauncher)` 一个公开构造**（`Forge 2.0.0` 里同名类型才有两参）。
2. 那个单参构造体内直接 `new HttpClient()` 去查版本目录，**不接受外部客户端**，所以我们的
   `MirrorRewritingHandler` 完全没参与。
3. `maven.neoforged.net` 在本机不可达（TCP 32.8s 超时）。

**现在的实现**（`Installing/NeoForgeInstallerBridge.cs`，照 PCL2 `ModDownloadLib.vb:1094-1119` 的顺序自己做）：

1. **查目录用我们自己的 HttpClient**。BMCLAPI 的 maven details API 优先，官方 `/api/maven/versions` 兜底：
   - `https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases/net/neoforged/{neoforge|forge}`
   - `https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/{neoforge|forge}`

   两个地址的 JSON 形状不同（前者 `files[]` + `type=DIRECTORY`，后者 `versions[]`），`ParseCatalogNames` 两种都认。
2. **选版本 → 构造 `NeoForgeVersion(mcVersion, apiName)`**。1.20.1 走 Legacy 坐标 `net/neoforged/forge`、
   构建名形如 `1.20.1-47.1.106`；1.20.2+ 是 `net/neoforged/neoforge`、形如 `21.1.264`。默认取最新稳定版
   （跳过 alpha/beta/`+`）。
3. **`NeoForgeInstallerVersionMapper.CreateInstaller` → `IForgeInstaller.Install(path, gameInstaller, options)`**。
   installer jar 与全部 maven 构件仍由 `NebulaGameInstaller` + 共用 HttpClient 下载，所以镜像重写生效。

配套的两处改动：

- `DownloadMirror.BmclApiMap` 加了 `("https://maven.neoforged.net/releases/", "https://bmclapi2.bangbang93.com/maven/")`。
  **前缀必须带 `/releases/`**：写成 `maven.neoforged.net/` 整段会把版本目录接口 `/api/maven/versions/releases/...`
  也改成 `/maven/api/maven/...`（404），那就永远查不到目录。
- Legacy 的 installer jar 在 `net/neoforged/forge` 下，而 `NeoForgeVersion.NeoForgeUrl` 把包名硬编码成 `neoforge`
  （不可覆写，`GetInstallerFile()` 也不看 `Files`）。CmlLib 唯一放得进手的缝是 `IGameInstaller`：
  桥接在那一层用 `GameFile` 的 `with` 表达式把 installer jar 的 URL 换成 `forge-` 坐标，其它文件一律不动。

**反射绕 CS0433 的做法本轮删掉了**：两个包里只有 `NeoForgeInstaller` 一个类型全文名相同，而新实现根本不碰 facade，
`NeoForgeVersion` / `NeoForgeInstallerVersionMapper` / `IForgeInstaller` / `NeoForgeInstallOptions` 都能直接 new。

**坑：BMCLAPI 的 `/maven/` 是缓存代理，冷门构件会 403。** 实测 `neoforge/21.1.72/...installer.jar` 走
302 → `mirrors.cernet.edu.cn` → `cmcc.mirrors.ustc.edu.cn` → **403**；而最近被请求过的 `21.1.264` 直接 **206**。
也就是说「装最新版」能成，「装某个旧构建」可能卡在镜像未命中、官方又不可达。失败消息里带文件名与地址
（`InstallerFileException`），面板上能看出是哪一个文件。

**关键配套（`DownloadMirror.IsKnownDownloadHost`）：镜像的下载 CDN 必须放行。** 这一条不修，前面全部白做——
BMCLAPI 不再直接回源，而是**随机**签发各家 CDN 的带签名 302，实测一天内见到四族：

| 首跳目标 | 形状 |
|---|---|
| `bmclapi<N>.sakura.sld.tw` / `bmclapi<N>.sakura.exp.dog` | `/download/<sha1>?name=…&s=…&e=…` |
| `<N>.749333.xyz` / `bmclapi<N>.sakura.exp.dog` | 同上 |
| `open-bmcl-api.lunar.cc` | 同上 |
| `minio.749333.xyz`（第二跳） | `/bmclapi/<xx>/<sha1>?X-Amz-…`（S3 预签名） |

`FollowAsync` 原先对「镜像重定向到未知主机」直接放弃镜像候选，而这类构件的官方源
（`maven.neoforged.net`）在国内不可达 → 必然失败。**8 次采样有 3 次首跳直接落在 `<N>.749333.xyz`**，
所以失败是概率性的：同一份代码有时装得上、有时卡在第 1 个或第 70 个库上。
修法两步：① 稳定的几族域名单列进 `IsKnownDownloadHost`；② 加**形状兜底**（路径里带 40 位十六进制摘要
且含 `/download/` 或 `/bmclapi/` 段即放行），否则域名轮换永远追不上。
只需放行第一跳——`IsMirrorUrl` 只认 `bangbang93.com`，跟到 CDN 之后就不再算镜像候选了。

**本轮实测（2026-10-22）**：`InstallProbe <数据目录> 1.21.1 NeoForge` →
installer jar(6.99 MB) → 70 个库 → `installertools` / `AutoRenamingTool` / `binarypatcher` 处理器 →
`versions/neoforge-21.1.264`（`id=neoforge-21.1.264`、`inheritsFrom=1.21.1`、47 个 libraries）。**成功。**
Legacy 1.20.1 未真装：`net/neoforged/forge/1.20.1-47.1.106/forge-...-installer.jar` 在镜像上实测 200、
而 CmlLib 自己算的 `net/neoforged/neoforge/...` 实测 404，坐标改写方向已由单测与探针双向确认。

### 6.34 安装器 stdout：有上限才敢写日志

Forge 安装器一次能刷几百行。`InstallerOutputLog` 的限制：单行 500 字符、每次安装 200 行、连续重复折叠成
「（重复 N 次）」；含 `error` / `failed` / `exception` 的行走 `Warn`，其余 `Debug`；
**失败时最后 20 行提升为 `Info`**（普通日志级别也能看到上下文）。没有上限的话日志会被安装器输出淹没。

实测：Forge 1.20.1 一次安装产出 201 行 `[Forge]`（199 Debug + 2 Warn），撞到 200 行上限后写一条提示。

### 6.35 实例运行锁：按游戏目录锁，按 PID + 启动时间判活

`Core/Instances/InstanceLockService.cs`，锁文件 `<gameDir>/.nebula-run.json`。

- **按游戏目录锁，不按实例名**：没有选实例时启动走全局 `.minecraft`，实例目录也可能被复制或改名；
  「同一个 saves/mods 目录被两个 JVM 同时写」才是要防的事。`GameLauncher` 在**解析出游戏目录之后、补齐文件之前**拿锁，
  `finally` 释放，抢不到返回 `LaunchResult(false, -1, elapsed, "该实例已有游戏在运行（PID …）")`，不抛。
- **写入必须原子**：唯一命名的 `.tmp`（带令牌前缀）→ `File.Move(不覆盖)` 是唯一互斥点。
  先写后改名的半截 JSON 会被读成损坏 → 按僵死接管 → 保护整体失效。
- **判活必须是 PID + 进程启动时间**（容差 1 秒）：只比 PID 挡不住系统复用，会永久锁死实例。
- **任何读失败都当僵死**：进程被杀、文件被手改、跨用户取不到 `StartTime`——全都接管。
  反过来（读失败就判「有人在跑」）的代价是实例再也启动不了。
- **释放只删自己令牌的那份锁**：判活容差把我们误判成僵死时，别人可能已经接管，此时删文件等于关掉他的保护。
- 目录建不出来 / 权限不足时返回 no-op 句柄**放行**：把游戏挡在启动器外面比少一次重复启动检查严重得多。

### 6.36 下载暂停是闸门，不是取消

`DownloadQueue.Pause()/Resume()`，闸门是 `PauseGate`（`TaskCompletionSource` 版异步布尔门）。

- **暂停只拦「启动新文件」**：在途请求照常下完，不中断、不抛取消。
- 不用现成原语的原因：`ManualResetEventSlim` 会让 64 个下载任务各占一个线程池线程干等（饥饿）；
  `SemaphoreSlim` 表达「关闭」必须有人持令牌，那次等待可能发生在 UI 线程上；用取消令牌表达暂停会清空批次快照、丢历史。
- 闸门位置：`RunOneAsync` 拿到并发槽之后、`IsUpToDate` 之前（覆盖跳过 / 复用 / 下载三种出口），
  **重试循环每次 attempt 开头再等一次**——否则「退避期间被暂停」会绕过暂停直接重发请求。
- 复位四处：新批次入口、成功收尾、取消、异常。少一处就会出现「上一次暂停把闸门永久关着」。
- 外部批次（安装链 `BeginExternalBatch`）**不接受暂停**：安装器按自己的调度跑，界面把按钮置灰并说明原因。
- 暂停中点取消必须能立刻收尾：闸门等待带批次取消令牌。

### 6.37 存储清理：路径规范化 + 白名单 + 保留策略

`Core/Maintenance/StorageService.cs`，四类 `cache` / `logs` / `installer` / `trash`。

- **`cache` 的统计与清理都排除 `cache/loader-install`**：前者避免重复计入，后者保证「只清缓存」不会抹掉安装失败现场
  （安装器临时文件由 `installer` 类别独立负责）。
- **清理前先 `Path.GetFullPath` 规范化，再要求严格位于数据根之下**（Windows 用 `OrdinalIgnoreCase`，数据根本身也拒绝），
  并跳过 `FileAttributes.ReparsePoint`（不跟随链接 / 联接）。构造 `<data>/../outside-cache` 的越界路径有专门单测钉住。
- **日志按最后写入时间保留 7 天**，不按文件名日期：只有 `launcher-yyyyMMdd.log` 带日期，崩溃导出、安装器输出、
  用户改名的文件都没有。正在写入的当日日志不受影响。
- 绝不触碰 `settings/accounts/servers/favorites/instances/versions/libraries/assets/backups/saves`；
  每个条目独立 `try/catch`，失败计入 `FailedItems`，清理永远不抛。

### 6.38 缺 Java 一键安装：只有「真的能装」时才给按钮

`Core/Java/JavaProvisionPlanner.cs` 是纯函数，界面只渲染它的结论：

- **已装满足**（≥ 需求主版本且落在加载器允许上界内）→ 不给按钮，避免「装了还是不能用」的循环。
- **需求与上界冲突**（如 1.12.2 Forge 要 Java 8、却写了 Java 21）→ 退回「手动配置」，不给一键安装：
  装多少版本都满足不了，给按钮等于把用户送去多下一次下载。
- **主版本不在可下载集合**（25/21/17/16/8，与设置页 `JavaDownloadOptions` 同源，有测试钉死顺序）→ 手动配置。
- 其余才给「安装 Java N（约 xx MB）」；体积是估算值，用途只是让用户知道要下几十 MB。

装完的写回顺序不能颠倒：`Settings.JavaPath = 新路径` → `SaveAsync()` → `DetectAsync(forceRefresh: true)`。
少了强制重扫，界面会继续用旧的探测缓存说「本机没有 Java」。下载用自己的 `CancellationTokenSource`，
与启动流程的 CTS 分开——取消下载不该连带打断启动。

**版本页与首页共用同一套判据**：`VersionsViewModel` 拿到 `LaunchResult.MissingJavaMajor` 后走同一个
`JavaProvisionPlanner`，只是展示位置不同（嵌在状态条下、页面更窄）。它自己只多两件事：
`OnNavigatedFromAsync` 停掉在途下载的 CTS；装完之后把按钮变成「已安装 Java N，重新启动」，
省掉用户回版本列表再找一遍的动作。

### 6.39 日志跟随：增量读取的三条硬规矩

`Core/Logging/LogFollower.cs`：

- **半行不推进偏移**：只吐到最后一个 `\n`，没写完的那一行下次再读。否则日志里一条被切开的堆栈会显示两遍，
  而且 `offset` 落在行中间以后永远对不齐（§6.30 同一类问题）。
- **截断与轮转都必须自愈**：`length < offset` → `Truncated` 从 0 重读；`offset-1` 不是 `\n` → `Rotated` 从 0 重读。
  游戏会在启动时把 `logs/latest.log` 改名成 `yyyy-MM-dd-N.log.gz` 再新建同名文件，不自愈就等于「跟随一次后永远不动」。
- **单次读取封顶 256KB**：日志被外部工具一次性灌几十 MB 时，跟随循环不能把整份读进内存；
  界面上限 5000 行，超出丢头部并提示。

定时器只在「页面激活 && 选中文件 && 开关打开」时跑，`OnNavigatedFromAsync` 与 `Dispose(bool)` 都要停表——
否则页面释放后还在每 700ms 读一次盘。

### 6.40 实例目录芯片：只给相对名，扫描不建目录

`Core/Instances/InstanceFolders.cs`：

- **目录表只存相对名**（`saves` / `mods` / `resourcepacks` / `screenshots` / `logs`），绝对路径在打开时用
  「该实例的真实游戏目录」现拼。版本隔离开关变化后，同一个实例的绝对路径会变，缓存绝对路径必然过期。
- **路径必须取自 `IInstanceService.GetGameDirectory(instanceId)`**，不能用实例名拼：目录名与实例名可以不一致
  （重命名只改 `Name`）。
- **枚举不写盘**：扫描期建目录等于替用户改游戏目录；`screenshots` 本来就常常不存在，
  所以「不存在」是正常态，芯片显示为空心点 + 弱化文案，点击时才 `Directory.CreateDirectory`。

---

### 6.41 下载源测速：只提示，不决策

`Core/Downloads/DownloadSourceProbe.cs`：

- **必须自建一个不带 `MirrorRewritingHandler` 的 `HttpClient`**。公用客户端会把官方 URL 改写成镜像地址，
  拿它测「官方 vs 镜像」等于两边测同一个地址，结果永远是「两边一样快」。代理仍然生效
  （`ProxyConfigurator` 改的是 `HttpClient.DefaultProxy`，与 handler 无关）。
- **只做一次测量、只给建议**：不写 `settings.json`、不动 `DownloadSourceGovernor` 的当前源。
  今天的网络结论会在换网后变成错误配置，要不要切由用户自己去设置页决定。
- 排序是纯函数（`SourceProbeRanking`）：综合分 = 首字节 + 1MB 换算的传输毫秒；吞吐 ≤0 或非有限时罚 3000ms，
  否则除零得 ∞ 会被当成「最快」；两侧差距 <30%（分母取较大值）判「差不多」，不给建议。
- 上限：单次 3s、总预算 8s、并发 2、单目标读 256KB 封顶；失败一律记失败原因，不抛（用户取消才抛）。

### 6.42 代理密码：内存有明文，磁盘只有密文

`Core/Net/ProxyCredentialStore.cs` + `SettingsService`：

- 落盘字段是 `AppSettings.ProxyPasswordProtected`（`dpapi:` / `b64:` 前缀 + base64），
  `ProxyPassword` 只作为「本次会话用的明文」与「旧配置迁移来源」存在，写盘时恒为 null。
- **保存用副本**（`PersistableCopy()`），不把明文抹在 `Current` 上：代理配置器与界面读的都是内存明文，
  否则每次保存都要解密一次，解密失败会让当前会话直接断网。
- **迁移当场落盘**：加载时发现「明文存在、密文为空」→ 加密写进密文字段并立刻 `SaveAsync` 一次。
  多数用户装完之后再也不会点「保存设置」，明文不该留在磁盘上等他。写盘失败只记日志，下次启动重试。
- **解不开就是未设置**（换机器 / 换 Windows 账户 / 密文被截断）：不清磁盘、不改密文，用户重填即可。
- 密码不参与幂等判定：密文解出来等于当前明文时保持原密文不动（DPAPI 带随机盐，每次重加密都会变，
  配置文件会无缘无故变动）。
- 配置包导出要同时剥 `proxyPassword` **和** `proxyPasswordProtected`：后者换账户就能解，比明文更危险。
- 界面上不再有「显示密码」（密码框留空 = 保持不变，占位与说明行讲清存储方式）。

### 6.43 发布产物的两条硬规矩

见 `docs/PUBLISH-CHECK.md`。两条要背下来的：

- **发布必须 `-Clean`**：`obj/<Configuration>` 里留着旧 XAML 编译产物时，publish 出来的 exe
  会静默退出（exit 0、无窗口、不建 data 目录、`--screenshot` 也不产图），而同一份二进制的 `--probe-dir`
  却能正常打印——所以「退出码 0」完全不能当发布成功的证据。
- **拿到产物先做两件事**：`--probe-dir .`（能不能执行）+ 在全新 `--data-dir` 上跑一次 `--screenshot`
  （能不能渲染）。两个都过才算「可用产物」；只测一个会把「托管入口正常、Avalonia 起不来」漏过去。

第二十八轮之后这两件事**脚本已经做了**，见 §6.44。

### 6.44 `build.ps1` 自带发布自检，别再用「exit 0」判断成功

`-Publish` 之后有三道闸：XAML 中间产物过期检测（构建前）、`--probe-dir`（30 秒超时 + 断言 `writable=YES`）、
`--screenshot`（20 秒超时 + 断言 PNG > 10KB）。任何一道失败都以非 0 退出并打印关卡名与观测值。
`-SkipVerify` 可跳过后两道。

过期检测只清 `obj/<Configuration>` 下名为 `Avalonia` 的目录 + 同层 `NebulaLauncher.dll`，**不删整个 obj**
（删了会在 ILLink 阶段失败）。

一个环境事实：这个 exe 写 `%TEMP%` 会被系统拒绝（`UnauthorizedAccessException`），所以验证文件必须落在
产物目录里。第一次自检失败如果报的是权限，先怀疑这条而不是产物坏了。

### 6.45 跨页预填：先发布请求再导航，且只发一次搜索

「模组卡片 → 去资源站查找」这类跨页预填，顺序与生命周期都不能随意：

1. **先 `Publish` 再 `Navigate`**。`Navigate` 内部是 `_ = RunPageLifecycleAsync(...)`，反序会在
   目标页取槽时槽还是空的。
2. 目标页必须在**自己的自动搜索之前**取走请求，否则先搜空串再搜一次：界面闪两下、多打一次请求。
3. 有请求时要**绕过「页面已初始化且有结果」的早退分支**，否则搜索框是新词、列表是旧结果。
4. 取走即清空（`Interlocked.Exchange` 单槽），同一份请求不会执行两次。
5. 预填词是**项目名不是文件名**：文件名带 `-1.20.1-forge-15.2.0.27` 这类后缀，直接搜命中率极低。
   `DisplayName` 与文件名主干逐字符不同 → 它来自 jar 元数据，直接用；否则剥版本段。

### 6.46 内存三段条：叠两个 `ProgressBar`，超额判定用字节

- **不要用三个 `Border` 拼像素宽**：`ColumnDefinition.Width` 在 Avalonia 绑不上 VM（`ColumnDefinition`
  不是控件，没有 DataContext）。用两条 `ProgressBar` 叠在同一格：底层蓝条 = 已用 + 本实例，
  上层黄条（`Background=Transparent`）= 其它占用。宽度自适应、窄窗不溢出、零转换器。
- **超额判定必须比字节**，不能把两个夹过的比例相加：`GameRatio` 夹在 0..1，堆比上限还大时它也是 1，
  `usedRatio + gameRatio > 1` 永远判不出来。用 `GameHeapBytes > TotalBytes - UsedBytes`。
- 文案写「**可分配**内存」不写「本机内存」：`TotalAvailableMemoryBytes` 在容器/WSL/沙箱里是 cgroup 限制。

### 6.47 双源合并：先配对再 Merge，被丢掉的那条之后看不见

`Core/Mods/CrossSourceSearch.cs` 的 `Merge` 只留一条，被去重丢掉的那条在结果列表里不存在了——
所以「另一站也有」的徽标必须**在 Merge 之前**按 `IsSameProject` 配对好。

其余三条：

- 平台权重是「同一内容在两站下载量差多少倍」的经验系数（模组 CF 1 : MR 5，数据包 CF 10 : MR 1，
  资源包/光影 CF 1 : MR 4），不是「哪个站更好」。
- CurseForge 先于 Modrinth 进列表：两侧并行请求，按到达顺序拼会让同一组关键词两次搜索得到两种排名。
- limit 取 `PageSize * 2`：按 `PageSize` 截掉的条目在下一页请求里已跳过，等于永久丢失。

### 6.48 多开：占位要在 `Process.Start()` 之前，日志尾按会话隔离

- 准入判据是「**该实例目录**在不在跑」，不是全局。`Instances/InstanceLockService` 已经按游戏目录加锁，
  这就是「同一目录不能两个 JVM」的正确性边界，**不要绕过它去支持多开**。
- `TryRegister` 必须放在 `Process.Start()` **之前**：入口准入到 Start 之间隔着下载与前置检查，可达数分钟，
  不留占位就会并发起两个。
- 日志尾与崩溃归因必须挂在**每次启动**的上下文里。否则 A 实例的崩溃日志会被归因到 B
  （`AnalyzeCrash` 只读该会话的尾巴）。
- 停止要按会话：`KillGame(sessionId)`。`IsGameRunning` 保留「任一在跑」的语义给首页/托盘用。
- 停完要**复核**（轮询到会话消失，超时返回最后一次复核结果），只信内存里的状态会留下假「运行中」。
- 不按进程名扫 `javaw`。启动器重启后 `_process` 为 null，按钮应当隐藏而不是猜。

### 6.49 `CloneWithMaxMemory` 有反射守护，加字段漏拷会先红

`LaunchOptions` 的字段改动必须同步 `GameLauncher.CloneWithMaxMemory`，否则「自动分配内存」这条路径上
新增设置会静默回落默认值。现在有 `CoreLaunch_LaunchOptionsCloneTests` 的反射守护用例：造一个所有属性
都非默认值的样本，Clone 后逐项比对，失败信息直接点名漏了哪个属性。

新加字段时如果它本来就该被改写，加进该用例的排除列表并在注释里写理由。

### 6.50 启动参数去重有两种语义，且「只做列表内去重」不够

`Core/Launching/LaunchArgumentDeduplicator.cs`：

| | 语义 |
|---|---|
| 游戏参数 | 同键**后出现者覆盖**（MC 对重复 `--width` / `--uuid` 是「两者都失效」） |
| JVM 参数 | 只丢「键和值都相同」的余项，不改值也不丢后者（JVM 对重复 `-XX` 取最后一个） |
| `--tweakClass` | 全部保留 |

**只做列表内去重不够**：CmlLib 把额外参数追加在自建参数之后，`--width 854` 与用户的 `--width 1280`
会并存，MC 仍判全部无效。所以要把 `--width/--height/--fullscreen/--demo` 从额外参数里**吸收**进
`MLaunchOption` 的属性。

`-Xmx` / `-Xms` 不同：CmlLib 4.1.0 逐项检测，用户写了就不再生成自己的。两条用例钉住了这个行为，
升级 CmlLib 后若变化会先红。

### 6.51 默认注入的 JVM 参数按条件分档，猜错就是起不来

- `-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT` **只能在 Windows** 加：它在 Unix 上不是合法 KeyStore
  类型，JVM 抛 `KeyStoreException` 直接起不来。
- `-XX:+UseCompactObjectHeaders` 只在 Java 主版本 ≥ 24 加；24 配 `UnlockExperimentalVMOptions`，
  25+ 不配；**主版本探测不到（0）就不加**——猜错是 `Unrecognized VM option`。
- 三条 `fml.ignore*` 是放宽 Forge 签名校验，受 `AppSettings.RelaxForgeChecks` 控制；接进启动参数
  要走 `LaunchOptions.RelaxForgeChecks`（四个 `new LaunchOptions` 的地方都要接，含服务器页）。
- `-XX:-OmitStackTraceInFastThrow` 无条件加，不受开关控制：它换的是崩溃日志有堆栈，
  而 `CrashRules` 的规则全靠堆栈。

### 6.52 合并类重构用像素对比验收，不用眼睛

「三处状态机合成一份」这类重构，行为零退化的证据是**旧代码与新代码各截同尺寸同数据目录的图，逐像素比**。
第二十八轮的 `JavaInstallState` 合并用这个办法得出 `0/992000 px`、`maxdelta ≤ 4`（抗锯齿噪声）。

做法：旧实现留一个 `-c <old>` 构建，新实现用 `-c <new>`，同一 `--data-dir`、同一 `--page` / `--section`，
各截一组，再用脚本比字节差。看起来一样不算证据。

### 6.53 多个入口各手写一遍同一张选项对象，必然漏

第二十九轮查明：`ServersViewModel` 的「加入服务器」启动**完全不设 `GameDirectory`**，于是启动全局
`.minecraft`——用户在隔离实例里配好的模组全失效，而且因为落在别的目录，绕过了按目录加的实例运行锁。

这是同类问题的第三次（`RelaxForgeChecks` 漏接一个入口、`VersionArgumentInfo` 漏了首页、这次是整段
实例解析都没有）。修法不是补第四个入口，而是抽 `App/Services/LaunchOptionsFactory.cs` 作为**唯一**构造点，
并加两条守护：ViewModel 源文件里不许出现 `new LaunchOptions`；工厂里恰好一处构造。

**判据**：凡「同一份逻辑在 N 个入口各写一遍」，第 N+1 次加字段就会漏，且症状是静默分叉
（同一个实例在 A 页生效、在 B 页不生效），不报错。

### 6.54 `DelegatingHandler` 拿不到「上层要 headers 还是完整 body」

做「下载源按超时换源」时想只对「等响应头」阶段计时，实测做不到：

- `HttpRequestMessage.Options` 里没有 `HttpCompletionOption` 键（`HttpClient` 不写进去）；
- 反过来往里 `Set` 也改不动内层 `SocketsHttpHandler`——真回环 socket + 20 ms 阈值 + 200 ms 慢 body，
  仍然 20 ms 就超时。

所以窗口在 handler 层对 `ResponseContentRead` 的请求会连 body 一起覆盖。按「候选来源 × 请求类型」分档
（镜像元数据 5 秒 / 官方元数据 30 秒 / 资源文件 30 秒）是唯一可行的近似，且镜像误判时换到官方是宽松档、
最终仍成功。要真正 headers-only 只能在调用点用 `ResponseHeadersRead`。

**别改 `HttpClient.Timeout` 全局值**：那会同时掐掉几十 MB 文件的正常慢速下载。

### 6.55 .NET 8 里 `Encoding.Default` 恒为 UTF-8，不能当系统代码页判据

PCL2 的 `.bat` 编码判据是 `If Encoding.Default.Equals(Encoding.UTF8) Then UTF8 Else GB18030`。
在 .NET Framework 成立，在 **.NET 8 里 `Encoding.Default` 恒为 UTF-8**——照搬会让 936 的机器也写 UTF-8，
`echo` 的中文全部乱码。

正确做法是 P/Invoke `kernel32!GetACP()`：`65001` → UTF-8（**必须无 BOM**，BOM 会让首行 `@echo off`
变成非法命令），其它 → GB18030。

真机四格对照（代码页 × 编码）只有 `936 + GB18030` 与 `65001 + UTF-8` 两格全对。

### 6.56 Java 上限的补丁号级只能做「建议」，大版本级才能「阻断」

PCL2 的加载器上限表里既有 `< J17` / `< J22` 这种大版本边界，也有 `8u320` 这种补丁号边界。

把 `8u320` 写成硬阻断会在 `8u322` 上误杀——补丁号粒度的判断本来就不该阻断启动。所以分两级：
大版本级进阻断，补丁号级进建议（`JavaLoadLimit.IsBlocking`）。

**两件事要顺带记住**：
1. `AllowedRange("1.19.4", Forge)` 的上界是 `int.MaxValue` **不是 21**——既有规则对 1.19.4 Forge 只给了
   下界，上界「不限」，加载器规则才把它压到 19/21。
2. Forge 1.16.x 新 build 的 `< J24` 被既有 `MODLAUNCHER_8`（16003-17001 → Java ≤ 17）压住，取交集后真实
   上界仍是 17，新规则只出现在文案里。有一条用例把这件事钉死，放宽 `MODLAUNCHER_8` 时它会立刻暴露。

### 6.57 `LoaderVersion` 不是三态，它的语义是缓存

`VersionType` / `AuthServerUrl` 是三态（null 跟随 / `""` 关闭 / 值覆盖）。`LoaderVersion` 不同：
加载器版本号要么有要么没有，没有「空串 = 明确关闭」这种东西。它的语义是**缓存**——null = 还不知道，
非空 = 上次从磁盘解析出的结果。

所以 `InstanceContext.SelectAsync` 回写它时**只在解析到真值时才写**：拿 null 盖掉已有值等于把上次成功的
结果也丢了。

它是经 `LaunchOptions` 走工厂传下去的，**不给 `LaunchAsync` 加参数**：判定发生在 `CheckJavaPreflight`，
那里只有 `LaunchOptions`；做成方法参数就得让四个入口各传一遍，少传一个就是静默分叉（见 §6.53）。

### 6.58 实例级 authlib 只覆盖地址，且账户类型是硬门槛

PCL2 的 `VersionServerAuthServer` 是实例级设置。Nebula 实现时守两条：

1. **只覆盖服务器地址**，用户名密码仍走账户——`instance.json` 会被打包分享，存凭据等于泄漏。
   有用例直接读落盘 JSON 断言不含 `password` / `accessToken` / `refreshToken`。
2. **账户不是 `Authlib` 类型时不注入**（与 PCL2 `只看地址非空` 有意不同）：导入的整合包会带着服主的地址，
   而用户常用账户可能是离线 / 微软账户；若「有地址就注入」，这类用户每次启动都会先下 jar 再预取元数据，
   断网时直接被「注入失败」阻断启动。

三态里 `""` 表示「本实例明确不注入（裸连）」，所以 `GameLauncher` 用 `??` 不用 `FirstNonEmpty`
——空串不能被当成「没设置」回落账户。

### 6.59 崩溃归因的兜底规则要五条件同时成立，且排在具体规则之后

「游戏没能启动」这条（PCL2 `ModCrash.vb:AnalyzeCrit3`）的判据是：无 `at net.`、无 `INFO]`、无 hs_err、
无 crash report、长度 < 100，**五个同时**。只判长度会把「Forge 加载器把日志刷到别处」误判。

另一个反例值得记：同一份日志修复前被**堆栈归因**吃掉——把 `joptsimple`（一个 Forge 参数库）当成肇事包，
指引用户去 mods 目录做二分，而问题根本不在 mods 里。加规则后 Crit1 非空即短路，堆栈归因不再跑。

### 6.60 `Select-String -Path 'src\**\*.cs'` 的 `**` 只等价一层目录

第二条复查因此漏掉了一批文件，并因此得出两个错误结论（「导出启动脚本」与「服务端下载 UI」其实都有）。

**搜索源码一律用 `Get-ChildItem -Recurse -Include *.cs | Select-String`**，或 `grep` 工具的递归模式。
之前所有用 `**` 走过的结论都要按这个复核。

### 6.61 分页取全必须对「每页上限」不敏感

第三十轮修 CurseForge 文件列表（原来只发一次 `pageSize=50`，JEI 的 3950 个文件只显示 50 条）。

关键不是「把 50 改成 1000」——**文档写 50、上游传 10000 并靠 `resultCount == totalCount` 判成功，
说明上限根本不可信**。终止判据要这样写：

```
index += page.ResultCount        ← 用服务端实际返回的条数推进，不是请求的 pageSize
直到 index >= TotalCount
```

服务端接受 1000 → 一页收工；按 50 截断 → 每页老实前进 50，翻 79 页照样取全。请求的 `pageSize`
只是「期望值」，**不参与任何正确性判断**。

另外三条必备（少一条就会静默出错）：
- **去重按 Id**，不是按文件名（CF 并发更新时会返回重复行，issue #1330）；
- **防死循环上限**（页数 + 总条数双上限，命中记 Warn）；
- **`pagination` 缺失或 `totalCount <= 0` 退回单页 + Warn**，不抛异常（越界请求会返回 `totalCount=0`）。

最后一条尤其容易踩：`index` 越界时 CF 返回 `data=0` 且 `totalCount=0`，如果不判「<=0 就停」，
`index >= TotalCount` 变成 `0 >= 0` 恰好成立而侥幸退出；换个服务端就会死循环。

### 6.62 `ContentKind.Plugin` 的三个坑

服务端插件（Bukkit/Paper 系）被当成客户端模组装进 `mods/` 会让游戏**启动即崩**——Bukkit 插件调用
服务端才有的类。判定顺序必须插件优先，且有三个方面极易写错：

1. **识别要读原始 `categories`，不能读 `display_categories`**。Modrinth 把 loaders 从展示分类里过滤掉了，
   只看展示分类永远判不出插件。搜索端两个键一起取，详情端把 `loaders` ∪ `categories` 喂给判定。
2. **`WebUrl` 要单独走 `/plugin/`**。API 的 `project_type` 对插件返回 `mod`，但网页详情页在 `/plugin/` 下；
   用 API 的值拼网址会落到「找不到」页面。
3. **拒绝要发生在任何 IO 之前**。用例同时断言「目录不存在」「HTTP 请求数为 0」。

### 6.63 同一个「不能装」，两个入口给不同答案

服务端插件在两个入口的处理**刻意不一致**：

| 入口 | 策略 | 理由 |
|---|---|---|
| 搜索安装（一个个挑） | 拒绝 + 灰按钮 + 一行说明 | 用户能改主意去服务端装；下载完才发现不能装是浪费 |
| 整合包导入（批量还原） | 落 `plugins/`，不拒 | 逐个拒绝会让整包残缺，用户还得自己找齐 |

判断依据是**用户意图**而不是技术类型：批量还原场景里「不装进客户端、也不丢内容」的中间态比「拒绝」更合适。
`ContentService.DirectoryNameFor(Plugin)` 也返回 `plugins/`——漏网路径最坏落到客户端不加载的目录，
而不是让游戏崩。**错误要往「不崩」的方向漏。**

### 6.64 覆盖率盘点表是「还有没有没抄的」唯一判据

第三轮复查之前，谁都没统计过 PCL2 那 113 个 `.vb` 到底查过多少个。盘完发现有一整片模块从没被碰过
（联机页 1116 行、`ResourceVersion` 427 行、`ModValidate` 424 行…）。

`.probe/pcl-coverage.md` 现在逐文件记录了状态（已覆盖 / 等价 / 明确不做 / 未覆盖 / 不适用）+ 落点 + 依据。
**下次判断「抄完了没有」先看它**，不要凭印象说「主要功能都做了」。

盘点还顺带产出两条硬证据：`ModSecret.vb` 是开源版空桩（自更新/公告在 PCL2 自己都没有），
`PageLinkMain.xaml.vb:429-431` 在非官方分支直接 `Throw`「开源版无法联网获取配置」。

### 6.65 `$` 是合法文件名字符，保留名只能整名比较；8.3 短名序号可以多位

`CLOCK$` 这类保留名拦不住——`$` 在 `Path.GetInvalidFileNameChars` 里是合法字符，字符白名单放行，
只能靠「去扩展名后整名比较」。

NTFS 8.3 短名（`PROGRA~1`）的序号可以多位：PCL2 `ModValidate.vb:294` 写 `.{2,}~\d`（只认一位），
第 10 个同名前缀的文件是 `~10`，会被漏掉。实现用 `\d+`。`ab~`（无序号）、`a~1`（序号前仅 1 字符）、
`ab~x` 仍应放行。

### 6.66 版本渠道分类：纠偏必须排在愚人节白名单之前，4/1 要用 UTC+2

PCL2 `PageDownloadInstall.xaml.vb:452-478` 的三档判定，顺序不能调换：

1. `snapshot` 且 id 以 `1.` 开头且不含 `combat` / `rc` / `experimental` / `pre` → **正式版**。
   Mojang 清单里 `1.20.1`、`1.14` 这类正式版确实被标成了 `snapshot`，不纠偏就会被塞进快照列表。
   `1.RV-Pre1` 因为含 `pre` 逃过纠偏，继续走下一步——这个例子正好证明「先纠偏、再白名单」的顺序是对的。
2. id 白名单（`20w14infinite` / `20w14∞` / `3d shareware v1.34` / `1.RV-Pre1` / `15w14a` / `2.0` /
   `22w13oneblockatatime` / `23w13a_or_b` / `24w14potato` / `25w14craftmine` / `26w14a`）→ 愚人节版。
3. 兜底：`releaseTime` 换算到 **UTC+2** 后是 4 月 1 日 → 愚人节版。用 UTC 会把欧洲时间 4/1 上午
   发布的版本落到 3/31（边界用例：`UTC 3/31 22:00` 进、`21:59` 不进）。

分类器是纯函数，被下载清单、已装版本扫描、服务器版本下拉三处共用——**同一个版本在不同页面必须显示同一档**，
这是它不写成三个 `switch` 的唯一理由。

### 6.67 MC 百科的搜索参数少一个就落到子域筛选

`https://www.mcmod.cn/s?key=<编码>&site=all&filter=0`：`site=all` 决定跨子站搜，`filter=0` 决定不套预设筛选。
少任一个都会落到「只搜模组」，搜不到教程与整合包。PCL2 `PageInstanceMod.xaml.vb:772` 就是这么写的。
查询词要 `Uri.EscapeDataString`：中文项目名（虚无世界）与带空格/撇号的名字（`Xaero's Minimap`）都真实存在。

### 6.68 「全局」不是某个版本的原版；实例目录必须在游戏目录里

第五十五轮由用户现场截图暴露的两条误判（模组页顶部只剩「资源包 / 光影包 / 数据包」，全局目标显示成「1.21 · 原版」）：

1. **全局目标不能带版本身份**。`InstanceContext.ListTargetsAsync` 原先把 `lastSelectedVersion` 填进
   `ContentTarget.VersionName / Loader / MinecraftVersion`。模组页按加载器分组，于是全局目标落进「原版」组、
   副标题显示「1.21 · 原版」，`CanLoadMods` 再据此收起「模组」入口——用户既看不到也装不了
   `D:\Minecraft\.minecraft\mods`。现在这三个字段对全局目标全为 `null`，左栏独立置顶，
   `MetaText` 写「所有版本共用」；`CanLoadMods` 对全局目标恒为 true。

2. **实例目录不能落在启动器数据目录**。`InstancesDirectory => <data>/instances` 让
   `D:\Minecraft\.minecraft` 下只剩 `versions / libraries / assets`：版本确实装在指定目录，
   但玩家内容（mods / saves / config）全在 `C:\Users\<user>\AppData\Roaming\NebulaLauncher\instances\<id>`。
   改成 `versions/<版本名>` 之后，模组页目标路径与空状态都指向
   `D:\Minecraft\.minecraft\versions\<版本>\mods`。

**判据**：全局目标的 `VersionName` / `MinecraftVersion` 必须为 null；左栏第一项必须是「全局目录」；
每个实例的绝对路径必须位于 `<game>/versions/<版本名>` 之下。三条都有单测钉住
（`CoreLaunch_InstanceContextTests.GlobalTarget_DoesNotBindToAnyVersion`、`AppMods_VanillaTargetTests`）。

**连带改动**（改布局时必须一起看）：克隆改为复制版本目录并改写 JSON id；移入回收站改为整目录移动、
`meta.json` 记版本名；换加载器成功后删旧目录的 `instance.json`；`FileReuseService` 只把带
`instance.json` 的版本目录当复用根；整合包导入落 `versions/<版本名>`（纯 zip 无版本信息时用整合包名）；
`LauncherPaths.InstancesDirectory` 只剩向后兼容语义，不要再往里写。

**PCL2 的自动隔离判据也照搬了**：`IsolationPolicyResolver.ShouldIsolate` 多一个
`versionDirectoryHasContent` 参数，`versions/<版本>/mods` 或 `saves` 存在时恒隔离——
设置页改成「不隔离」不该让已有内容突然读不到。

**旧布局一次性迁移**：`InstanceService.LoadAsync` 启动时把 `<data>/instances/<id>` 搬到
`versions/<版本名>`。三种情况都有出口：目标不存在 → 整目录移动；目标是无实例的版本目录 → 合并内容并写
`instance.json`；目标已被别的实例占用 → 另建 `<版本名>-旧实例`（复制版本本体 + 合并内容）。
失败保留原目录并记 Warn，下次启动重试。

**多实例 = 多版本目录**：一个版本可以有多个实例，但每个实例独占一个版本目录。版本页
「为选中版本新建实例」在版本已有实例时走克隆（`<版本名>-副本`，`IncludeMods` / `IncludeSaves` 等全 false），
只复制版本本体、内容从空开始；不存在「同目录两个实例」。

### 6.69 本地内容图标：两条来源，缺一条就是「整类没有图标」

图标链路分两段，任意一段断掉的表现都是「卡片只剩首字母」：

1. **包内嵌图标**（离线）：模组的 `fabric.mod.json.icon` / `quilt_loader.metadata.icon` /
   `mods.toml.logoFile` / `mcmod.info.logoFile`；**声明字段缺失时回落** `assets/<modid>/icon.png`、
   `assets/<modid>/logo.png`。资源包 / 光影包 / 数据包是包根 `pack.png`，光影包再加一个
   `shaders/pack.png` 次级候选。所有候选都只在该条目真实存在时才被读到，猜错无可观察行为。
2. **资源站 logo**（联网）：`ModProjectInfoService` 按 SHA1 / CurseForge 指纹反查项目，下载到
   `<data>/cache/{modrinth,curseforge}-icons/`。**这一条原先只对 `ContentKind.Mod` 生效**，
   而光影包几乎从不带 `pack.png`——于是光影分类的图标恒为空（2026-10-24 用户报的就是这件事）。
   现在四类内容都参与，但资源包 / 光影包 / 数据包**只在没有内嵌图标时**参与：为一张本来就在包里的
   `pack.png` 去把几十 MB 的资源包算两遍哈希、再发一轮请求不划算。

渲染侧不需要改：卡片 `Image` 走 `PathToImage`（`Views/ModsView.axaml.cs`），解码失败返回 null 让首字母占位透出；
静态缓存按路径键住，而图标缓存文件名是内容哈希，所以不会读到过期图片。

**两条环境事实**：
- Modrinth 图标实际是 **webp**（`cache/modrinth-icons/*.webp`），CurseForge 的 logo 有 png 也有 **jpg**。
  Skia 按内容解码，`PathToImage` 不看扩展名；不要因为「缓存目录里没有 .png」就去改缓存写入逻辑。
- 只在 CurseForge 上架的模组（1.7.10 的 IC2 / JEI Legacy）jar 里没有 logo、`mcmod.info` 的 `logoFile`
  是空串，图标只能来自资源站。它们曾经长期「有名字没图标」，根因不是网络而是指纹接口写错（§6.70）。

**判据**：新增一类本地内容（新的 `ContentKind`）时，必须同时回答「包内图标在哪」「资源站是否有项目」
「目录形态怎么算哈希」三个问题，否则新分类上线即无图标。实测数据在 `.probe/mk-icon-data.ps1`
（生成 `.probe/data-icons`，每种内容各一个带图标 + 一个不带图标的包），
截图命令 `shot.ps1 -Page mods -Section {mods,resourcepacks,shaderpacks,datapacks}`。

### 6.70 CurseForge 指纹接口：复数路径 + 无符号指纹，写错就是「永不识别」

`POST /v1/fingerprints`（**复数**），请求体 `{"fingerprints": [2948113514, …]}` 必须是**无符号** uint32。
代码里两处都写错过，症状完全相同：CurseForge 明明收录了模组，却始终识别不出来——
名字、译名、图标、更新检查全部退化为「未识别」，而且**不报错**（补全把未识别当正常结果）。

| 写错的方式 | 服务端响应（2026-10-24 实测） |
|---|---|
| `POST /v1/fingerprint`（单数） | `HTTP 404`，空响应体 |
| 指纹按 int32 补码写成负数 | `HTTP 400`，`{"errors":{"$.fingerprints[0]":["The JSON value could not be converted to System.UInt32…"]}}` |

正确请求对 IC2 2.2.828 与 JEI Legacy 1.7.10 都命中：`data.exactMatches[].file.modId` = 242638 / 1554879，
随后 `POST /v1/mods` 取项目、下载 logo，卡片出图。

响应侧的 `file.fileFingerprint` 仍是 **int32 形态**（高位为 1 时是负数），
`ParseFingerprintMatches` 的补码还原不能跟着删。

**判据**：这类「请求形状错 → 服务端 4xx → 调用方降级成未识别」的链路，必须有**请求形状用例**
（`CoreMods_CurseForgeFingerprintTests.指纹反查_请求用复数路径且指纹按无符号发送` 断言 path 与 body），
只断言「返回了匹配」的用例挡不住它：handler 替身会照着错误的路径也返回数据。

### 6.71 账户不在构造时读盘：不预热就是「刚打开程序未登录」

`AccountService` 只在 `LoadAsync` 时才把 `accounts.json` 读进内存，而旧实现里**唯一**的调用方是账户页
（`AccountsViewModel.ReloadAsync` 的 `_serviceLoaded` 分支）。于是首帧之后的所有消费者拿到的都是空账户：

- 首页账户卡片恒为「未登录」（`HomeViewModel.LoadAccount` 读 `ActiveAccount`）；
- 版本页点启动被 `if (_accounts.ActiveAccount is null)` 挡下，提示「还没有账户」；
- 首页 / 版本页 / 服务器页三个启动入口的 `EnsureValidActiveAccountAsync` 返回 null，退化成「未登录」启动失败
  （`GameLauncher` 自己不读账户：账户由 `LaunchOptionsFactory.Build` 传进 `LaunchOptions`）。

症状是「去账户页转一圈回来就正常」——那一趟把盘读进了内存。这不是缓存过期，是**加载时机**。

修法两条，缺一不可：

1. **首帧之前同步预热**（`App/Services/StartupState.cs`）：与窗口尺寸读设置同一个位置（§6.20）。
   首页是在 `new MainWindow(Services)` 里构造的，晚一步就来不及。账户 `LoadAsync` 内部全 `ConfigureAwait(false)`，
   在 UI 线程上同步等待不会死锁；读失败只写日志，不阻断启动（读不出来也要能进设置页修）。
2. **`LoadAsync` 幂等 + `ReloadAsync` 强制重读**：与 `ISettingsService` 同一语义（§6.29）。
   不幂等的话，预热之后第一次进账户页就会用磁盘旧状态把内存里刚改完的当前账户盖回去。
   「已加载」只在**读成功**时置位（文件不存在算成功）：读失败也标记的话，一次瞬时 IO 失败
   会让本次会话永久停在「无账户」，而生产代码里没有 `ReloadAsync` 的调用方来救它。

外加一条显示侧的：首页订阅 `AccountsChanged`，**只在页面激活期间订阅**（进入 `OnNavigatedToAsync` 挂、离开即退订）。
首页是常驻缓存页，停在本页时不会再走 `OnNavigatedToAsync`，登录完成 / 令牌续期必须能当场反映到卡片上；
挂在构造函数里则会让「被 `InvalidateStalePages` 丢弃的旧页面」一直留在账户服务的订阅表上。

**判据**：凡是「服务在构造时不读盘、靠页面触发加载」的实现，都必须有一个明确的预热点，且该点在
**第一个消费者读属性之前**执行。守护用例：`AppStartup_StartupStateTests.预热_把磁盘上的当前账户读进内存`
（真实 `AccountService` + 一份已有 `accounts.json` 的冷启动）、`预热_读盘抛异常不阻断启动`，
以及 `CoreLaunch_AccountServiceTests.LoadAsync_IsIdempotent_AndReloadPicksUpExternalWrites`（幂等 + 事件次数 + `ReloadAsync`）。
同一份数据目录的改动前后对照（`pub\r61` vs `pub\r59`，`--page home`）写在 `docs/HANDOVER-2026-10-24-R59.md` §3.1。

> PCL2 的对照：它的登录态存在设置缓存里，随 `Setup.Load()` 在启动时一起读进内存（`ModLaunch.vb:410-470`
> 直接读 `Settings.Get("LoginType")` / `LoginLegacyName`），没有任何一轮 UI 依赖「进过账户页」。

---

## 7. 视觉与截图纪律

**不要用系统截屏**：窗口被遮挡、无焦点、多实例并行时会抓到别人的界面。用 `tools/shot.ps1`（应用内渲染）。

看不到数据不等于页面坏了，先用 `seed-demo-data.ps1` 灌演示数据。截图失败读 `<data-dir>/logs/*.log`；
偶发一次 `0xE0434352` 是启动时序问题，重试即可。

每次 UI 改动的最低截图组：目标页面 1240×800、目标页面 1024×660、目标页面浅色。涉及弹层/交互态的用 §2.3 的开关。

---

## 8. 已知问题与未验证

### 8.1 必须知道的功能缺口

| 项 | 状态 | 说明 |
|---|---|---|
| **NeoForge 安装** | ✅ 真装通过 | 桥接已重写（不碰 facade），自己拉 BMCLAPI 版本目录 + `IForgeInstaller.Install`。新式：`InstallProbe 1.21.1 NeoForge` → `versions/neoforge-21.1.264`（47 libraries）。Legacy：`InstallProbe 1.20.1 NeoForge 1.20.1-47.1.106` → `versions/neoforge-47.1.106`（`inheritsFrom=1.20.1`、34 libraries、`BootstrapLauncher`），约 35 分钟（srg 重命名慢一个量级） |
| NeoForge 版本选择 | ✅ 可选 | `VersionService.GetNeoForgeVersionsAsync` 拉两族 maven 目录（`neoforge` / `forge`），按目录名反推 MC 版本过滤，1.21.1 下 259 个可安装构建，默认最新稳定版 |
| 真实启动进主菜单 | ✅ 真机通过（R31） | `.probe/smoke-r31` 探针走 `IInstallService` + `IGameLauncher`（不绕过任何一层）：1.20.1 装 3641 文件 / 788 MB，进程启动到 atlas 建完 **92 秒**，`Realms Notification Availability checker` 出现即主菜单就绪；离线账户的 401 是预期降级。未验：正常退出 / 二次启动 / 多开 / 加载器真机，见 `docs/R31-BROKEN-WORK.md` §3.3 |
| 版本行按钮溢出 | ✅ 已修 | 「隐藏」等入口进 `…` 溢出菜单，1024×660 下版本名为「启动 / 修复 / …」三按钮且版本名完整 |
| 微软账户登录 | 未验证 | 需要真实账户交互 |
| 第三方验证登录 | 只到协议层 | 认证与注入链路有 59 例单测 + jar 真实下载冒烟；「登录 → 启动 → 角色可用」未跑 |
| MCBBS 整合包的 addons 反查 | ✅ 落地 | CurseForge 元数据 → forgecdn 直链（按 fileId 推导）→ Modrinth 兜底，全失败进「待手动安装」清单且导入仍算成功。未验证真实 MCBBS 样本（手上没有） |
| HTTP 代理 | ✅ 落地 | 无 / 系统 / 自定义 + 凭据 + 连通性测试，生效点 `HttpClient.DefaultProxy`。未做：407/TLS 分支真实触发、密码本地加密（现为明文写 `settings.json`，UI 已告知） |
| 服务器 Query 玩家名单 | ✅ 落地 | GameSpy4 UDP（`Core/Servers/ServerQueryService.cs`），含分片拼装与「未开启 Query」区分。未验证：真实开放 Query 的服务器（本机只测到 `mc.hypixel.net` 超时）、真实多片大响应、Latin-1 主机名 |
| 存档 `level.dat` 编辑 | ✅ 落地 | NBT 完整标签树读 + 写回（`NbtWriter.cs`），未知标签原样保留、写前 `.bak`、`session.lock` 被占用拒绝。未验证：真实存档样本（本机 `%APPDATA%\.minecraft` 无 `saves`）、运行中游戏的锁语义、非 Windows 的 `FileStream.Lock` 分支 |
| 崩溃建议的动作按钮 | ✅ 落地 | 六种动作可执行；「禁用肇事 mod」只在归因唯一时出现。未验证：真实点击（截图无点击能力）、`RepairVersion` 动作的端到端 |
| 实例运行锁 | ✅ 落地 | `Core/Instances/InstanceLockService.cs`：锁文件 `<gameDir>/.nebula-run.json`，原子写 + PID/启动时间判活 + 僵死接管 + 令牌释放（§6.35）。未验证：抢锁失败的端到端（需真实 Java 与已装版本）、真机点「启动」后的徽标刷新、两进程并发抢锁压测、非 Windows |
| 下载暂停 / 继续 | ✅ 落地 | `DownloadQueue.Pause/Resume` + `PauseGate`，闸门在并发槽之后、`IsUpToDate` 之前，重试每次 attempt 再等；外部安装批次不可暂停（§6.36）。未验证：点击级、真实 3000 文件安装中途暂停、真实网络在途请求 |
| 存储占用与清理 | ✅ 落地 | 设置页「存储」分区：四类占用（cache / logs / installer / trash）+ 单类清理 + 选中项清理 + 确认浮层，路径规范化 + 白名单 + 日志保留 7 天（§6.37）。未验证：真实点击、真实 junction 样本 |
| 存档缩略图 | ✅ 落地 | `Core/Backups/WorldIconService.cs` 读 `Data.icon`（base64 → PNG magic → 双上限 96KB base64 / 64KB 解码），缓存 `<data>/cache/world-icons/<sha1(世界路径)>.png` + 旁记 `level.dat`（mtime, length）失效；世界页 48×48 圆角缩略图、异步加载并发 3、无图标走占位块。未验证：真实游戏写出的存档图标、200 个带图标世界的刷新耗时 |
| 缺 Java 一键安装 | ✅ 落地（首页 + 版本页） | `Core/Java/JavaProvisionPlanner.cs` + `LaunchResult.MissingJavaMajor` + 首页失败面板与版本页状态条下的「安装 Java N（约 xx MB）」→ `DownloadRuntimeAsync` → 写回 `Settings.JavaPath` → 强制重扫（§6.38）。未验证：真实 50 MB 下载全链路、下载中途切页取消、加载器版本名下 `GetAllowedRange` 会退化为无上界、装完「重新启动」的点击 |
| 日志实时跟随 | ✅ 落地 | `Core/Logging/LogFollower.cs` 增量读取 + 700ms `DispatcherTimer`；半行不推进偏移、截断/轮转自愈、单次 ≤256KB、界面 5000 行上限（§6.39）。未验证：真实游戏写 `latest.log` 的跟随（本机无可用 Java）、“已暂停”态与跟随中手工刷新的点击路径、>5000 行实时流 |
| 实例目录快捷芯片 | ✅ 落地 | `Core/Instances/InstanceFolders.cs`（五项固定表）+ 版本页芯片行（实心 / 空心点区分是否存在），打开前才 `CreateDirectory`（§6.40）。未验证：真实点击的三步副作用、DI 未注册时的真机降级 |
| 下载源测速 | ✅ 落地 | 下载页「源测速」+ 一行结论；`SourceProbeRanking` 纯函数排序、30% 内判持平；自建不带改写 handler 的客户端（§6.41）。第二十八轮补了 `--probe-sources` CLI 入口，真实测过：官方 377ms / 754 KB/s、镜像 703ms / 447 KB/s、`decision=official`、`EXIT=0` |
| 代理密码加密 | ✅ 落地 | `ProxyCredentialStore` DPAPI + `ProxyPasswordProtected` 落盘 + 加载即迁移并落盘 + 配置包双剥（§6.42）。非 Windows 退化为 base64 并在日志与界面上如实说明。未验证：换 Windows 账户后「解不开 → 按未设置」的真实路径、`b64:` 退化分支的真机（本机 DPAPI 可用） |
| 发布产物首启 | ✅ 自动验证 | `docs/PUBLISH-CHECK.md`：minimal 23.5 MB / 3 文件；第二十八轮把「须 `-Clean`」与两道产物关做进 `build.ps1`（§6.44），不再依赖人工记得 |
| 运行中游戏「停止」 | ✅ 落地 | 版本页徽标旁 + 首页运行态各一个入口，只杀本会话进程、杀完复核、不破坏实例锁释放顺序（§6.48）。未验证：8 秒超时分支（会拖慢用例，只测了异常快路径） |
| 多实例并行 | ✅ 落地 | 按实例目录准入、按会话停止、日志尾与崩溃归因按会话隔离；同目录仍只能一个（走既有实例锁，未放宽）。未验证：真起两个 JVM 的端到端 |
| 默认注入的 JVM 参数 | ✅ 落地 | 四条兼容/诊断参数 + Windows 信任库 + Java 24+ 压缩对象头，按平台/版本/开关分档（§6.51）。未验证：Java 24/25 真机（本机无 JDK 24） |
| 启动参数去重 | ✅ 落地 | 游戏参数后覆盖 / JVM 保留先者 / `--tweakClass` 全留；窗口类参数「吸收」进 `MLaunchOption`（§6.50）。未验证：`--server` / `--port` 与实例「自动进服」并存时仍会重复 |
| 双源合并搜索 | ✅ 落地 | 资源页第三档「双源」，合并去重排序在 Core 纯函数（17 例）。CurseForge 带内置 key 走官方搜索，档位只受服务是否接线影响（§6.13、§6.47）。未验证：双源态的截图（无对应启动参数）、真实两站限流差异对排名的影响 |
| 内存三段可视化 | ✅ 落地 | 设置页内存分区：可分配 / 其它占用 / 本实例分配 + 超额红字提示（§6.46）。未验证：「其它占用 > 0」的黄段截图（本机截图时 `MemoryLoadBytes` 接近 0） |
| `--versionType` 自定义 | ✅ 落地 | MC 主菜单与 F3 显示的版本名；实例三态（null 跟随全局 / `""` 关闭 / 值）→ 全局兜底；**空值必须把 `--versionType` 和它的值两个 token 一起摘掉**，否则主菜单显示字面量 `${version_type}`。设置页与实例面板两处 UI。未验证：界面交互（勾选解锁）只有回填截图 |
| `options.txt` 语言分版本 | ✅ 落地 | 原来一律写 `zh_cn`，**1.1~5 会 NPE 崩溃**；现按 `vanilla x ≤ 10` 把后两位大写；`saves` 目录存在时只做大小写规范化；写两次（`-` → 目标值）触发缓存；Yosbr mod 路径。未验证：只读/占用文件的异常分支 |
| 四个启动入口统一走工厂 | ✅ 落地 | `App/Services/LaunchOptionsFactory.cs` 是唯一构造点。修掉的 P0：服务器页「加入服务器」原来**不设 `GameDirectory`**，启动全局 `.minecraft`，隔离实例的模组全失效且绕过实例运行锁（§6.53）。守护测试：ViewModel 里不许出现 `new LaunchOptions` |
| 崩溃归因「极短输出」兜底 | ✅ 落地 | 五条件同时成立（无 `at net.` / 无 `INFO]` / 无 hs_err / 无 crash report / 长度 < 100），排在 75 条具体规则之后（§6.59） |
| 崩溃归因「版本 JSON 多个 Forge」 | ✅ 落地 | 精确整串匹配。修复前这份日志被堆栈归因吃掉，把 `joptsimple` 当肇事包、指引用户去 mods 目录二分 |
| 下载源按响应头超时换源 | ✅ 落地 | 镜像元数据 5 秒 / 官方元数据 30 秒 / 资源文件 30 秒；超时仍抛 `HttpRequestException` 让 `DownloadQueue` 重试。未验证：真机镜像抽风场景（只有假 handler 用例） |
| 服务端 `Launch Server.bat` | ✅ 落地 | GB18030 按 `GetACP()` 自适应（§6.55）、`"java"` 裸命令、已存在不覆盖、写失败只 warn。真机四格对照验过 |
| rar / 7z 压缩包提示 | ✅ 落地 | 按扩展名分支（异常类型区分不了意图），提示给「解压后重压 zip」的动作，不透传英文异常 |
| Java 上限按加载器小版本分档 | ✅ 落地 | 7 条 PCL2 规则；补丁号级只做建议、大版本级才阻断（§6.56）；`LoaderVersion` 全链路打通（§6.57）。未验证：`8u320` 建议级没有 UI 消费 |
| 实例级登录服务器（authlib） | ✅ 落地 | 三字段三态（`null` 跟随账户 / `""` 关闭 / 值覆盖）+ 实例面板「登录服务器」卡片；只覆盖地址不存凭据；账户非 `Authlib` 型是硬门槛（§6.58）。未验证：真实验证服连通性 |
| PCL2 全模块覆盖率盘点 | ✅ 落地（R32 重算） | `.probe/pcl-coverage.md`：113 个 `.vb` / 43116 行逐个判定，**按行实测 已覆盖 49 / 等价 46 / 明确不做 11 / 未覆盖 0 / 不适用 7**。原写 62/20/21/7/3 是虚高（把「等价」记成「已覆盖」），且表里 6 类落点类型名不存在，R32 已逐条纠正（§6.64） |
| CurseForge 文件列表翻页取全 | ✅ 落地 | 原来单请求 `pageSize=50`，JEI 的 3950 个文件只显示 50 条；现翻页 + 按 Id 去重 + 全局降序。终止判据对每页上限不敏感（§6.61）。实测 3950 条 / 0 重复 / 3.6 秒 |
| Modrinth 服务端插件识别 | ✅ 落地 | `ContentKind.Plugin = 5`；识别读原始 `categories` 不读 `display_categories`、`WebUrl` 走 `/plugin/`、拒绝在任何 IO 之前（§6.62）；搜索安装拒绝、整合包落 `plugins/`（§6.63）。未验证：提示条无截图证据 |
| 保留名 + NTFS 8.3 短名 | ✅ 落地 | 补 `CLOCK$` / `COM0` / `LPT0`（`$` 是合法字符，只能整名比较）与 `.{2,}~\d+`（序号可多位，比 PCL2 的 `\d` 宽一档，§6.65） |
| 版本清单四档分类 | ✅ 落地（R32） | `VersionChannelClassifier.Classify`：新增 `Special`（愚人节版）档、纠正 Mojang 误标成 `snapshot` 的 1.x、4/1 按 UTC+2 兜底；下载页加「远古版 / 愚人节版」两档筛选，已装版本 / 首页 / 服务器下拉共用同一分类器（§6.66）。30 例单测 |
| 在 MC 百科搜索 | ✅ 落地（R32） | 模组卡片右键「在 MC 百科搜索」，搜索词复用 `BuildLookupQuery`，URL 为 `https://www.mcmod.cn/s?key=…&site=all&filter=0`（参数照抄 PCL2，§6.67）。未验证：点击路径（截图模式无右键能力） |
| 本地内容图标 | ✅ 落地（R56） | 四类内容（模组 / 资源包 / 光影包 / 数据包）都有图标来源：包内嵌（`icon` / `logoFile` / `assets/<modid>/icon.png` / `pack.png` / `shaders/pack.png`）+ 资源站 logo（四类内容都参与，非模组仅在内嵌图标缺失时）。**端到端实测**：真实 IC2 2.2.828 与 JEI Legacy 1.7.10 经 CurseForge 指纹命中出图（§6.69、§6.70） |
| CurseForge 指纹反查 | ✅ 修复（R56） | 原实现请求 `POST /v1/fingerprint`（单数）并按 int32 补码发负数 → 404 / 400，导致 CF 侧身份、译名、图标、更新检查全部静默失败。现为 `POST /v1/fingerprints` + 无符号数组，并有请求形状用例钉住（§6.70） |
| 联机功能（`PageLinkMain`） | ⛔ 裁决不做 | 1116 行 / 12 个子系统。三条前置阻塞：上游在非官方分支直接 `Throw`「开源版无法联网获取配置」(:429-431)、配置与 EasyTier 二进制都来自 PCL 官方服务器、要分发第三方二进制。功能拆解与 6 个可独立切片留在 `.probe/pcl-gap-r30.md` §2.1 |

### 8.2 未验证清单

| 项 | 说明 |
|---|---|
| 点击级交互 | 截图模式不能点击；面板开关、批量按钮、文件选择器都靠编译期绑定校验 + 代码审查 |
| 快捷键按键级 | 命令挂接与 Esc 的输入框让位有编译期校验，手工步骤见 `PCL2-PORTING.md` §11.4 |
| 游戏窗口接管 / 启动前后命令 | 只在纯函数与编译层验证 |
| `hs_err` 的真实采集与归档 | 解析有 20 例单测，但 `FindRecentHsErrLogs` / `ArchiveCrashArtifacts` 要真实 JVM native 崩溃才能验证 |
| 托盘菜单点击与图标视觉 | 原生菜单无法在无头环境点击；关闭行为用 `PostMessage(WM_CLOSE)` 验证过。若是黑图标，把 `Assets/tray.png` 换成 `.ico` |
| 任务栏进度条的视觉 | 无头环境看不到任务栏；`Attach` 拿到真实 HWND 有日志，`Report` 链路由 19 例单测的 RecordingPlatform 证明 |
| 700 张模组卡片的渲染 | 只测了 700 项文件操作耗时（240–300 ms）；`ItemsControl + WrapPanel` 无虚拟化是既有结构 |
| 自动降级的真实触发 | 三条判据有 30 例单测，但无法稳定构造「镜像连续失败」；「已降级到官方（原因）」状态行无截图 |
| Forge 阶段行的真实截图 | 阶段行只在安装中可见；由 25 例单测 + 三次真装日志（0 次回退）证明 |
| 组件面板的「只修复缺失文件」 | 按钮链路已通；修复本身会联网，未实跑 |
| 崩溃导出端到端 | 需真实 JVM native 崩溃；zip 内容与脱敏由 23 例单测（打开真实 zip 逐条断言）覆盖 |
| 跨机器导入配置包 | 合并语义在「同一台机器两个数据根」上验证过 |
| macOS / Linux | 只编译通过，平台分支未实机运行 |
| 125% / 150% DPI | 截图按逻辑像素出图，未测缩放 |
| 资源详情窄窗布局 | 1240×900 已验证，1024×660 未截图 |
| 文件复用的 >5000 文件分支 | 只验证代码路径；jar-only 索引未实测 |
| 真实崩溃 → 导出 zip | 需要 JVM native 崩溃；`ReadHsErrSections` / `BuildExitResult` 有单测，接线只有编译 + 审查 |
| mtime 比对在 FAT / 网络盘 | 只在 NTFS 实测；2 秒容差按 `McInstallManifest` 既有常量走 |
| `pack_format` 与 min/max 混合 | 实现为区间优先，未造混合样本 |
| 实例锁的并发抢锁 | 单测用两个 `InstanceLockService` 实例共享同一磁盘文件模拟；没有「两个真实启动器进程同时抢」的压测 |
| 下载暂停的点击与真实网络 | 命令绑定只有编译期校验；在途请求由假 handler 模拟，没在真实 3000 文件安装里中途暂停 |
| 存储清理的链接样本 | 按 `FileAttributes.ReparsePoint` 跳过，但没造真实 junction / symlink 做端到端 |
| 图标缓存的进程内解码复用 | `PathToImageConverter` 有进程级静态 Bitmap 缓存：`level.dat` 变更后缓存 png 会重写，但同一进程内可能仍显示旧图，直到重启 |
| 存储分区的 F5 / 清理后刷新 | 代码路径已通（`ReloadCurrentAsync` 覆写 + 清理后重扫），真实按键与点击未跑 |
| 缺 Java 安装的真实下载 | 未真下运行时（本机没有能启动的 26.x）；「装完写回设置 → 重扫 → 重启成功」只有代码路径 |
| 真实游戏日志的跟随 | demo 是启动器自己往真实文件追加；JVM 写 `latest.log` 的节奏、以及启动瞬间轮转的时序未实测 |
| 日志跟随的轮转漏判 | 新文件恰好更长且旧 `offset` 处仍是换行时，会漏判一次轮转（只多显示旧内容，不丢新内容），`LogFollower` 类注释已写明 |
| 实例目录芯片的点击 | `CreateDirectory → OpenFolderAsync → 弱化态消失` 三步没有点击级证据（截图无点击能力） |
| 下载源测速的真实网络 | 只有假 handler 与 demo 摆拍；「点按钮 → 结果行 → 再点显示测速中」没有点击级证据 |
| 代理密码的跨账户解密 | 本机 DPAPI 可用，`b64:` 退化分支与非 Windows 路径只有单测覆盖；「换个 Windows 账户后按未设置处理」未实跑 |
| 发布产物的真实双击 | 本会话没有交互桌面（GUI 进程留不住），「窗口能开」只能由 `--screenshot` 间接证明；`portable` / `folder` 两种形态没验 |
| 真实启动进主菜单（第二十八轮之后仍是唯一主缺口） | 本轮所有启动相关改动（停止入口、多开、参数去重、默认 JVM 参数）都只有纯函数与假进程测试，没有一次真的起 JVM |
| Java 24 / 25 的压缩对象头 | 本机无 JDK 24，`-XX:+UnlockExperimentalVMOptions -XX:+UseCompactObjectHeaders` 的联合合法性依据 PCL2 原文与 JEP 450/519 |
| 双开的端到端 | `LaunchAsync` 的并发准入只由 `RunningGameRegistry` 单测与 `CanStartInstance` 判据覆盖，没真起两个 JVM |
| 「杀不掉」分支 | 8 秒超时路径没有自动化测试（会把用例拖到十几秒），只测了「Kill 抛异常」快路径 |
| 双源态的截图 | `--section` 只能选内容分类，没有进入「双源」的启动参数；徽标与提示条靠编译期绑定校验 + 单测 |
| 右键菜单展开态 | `--screenshot` 没有点击能力，`ContextMenu.Open()` 需要新开关；菜单项存在靠编译期绑定校验 |
| 内存条「其它占用」黄段 | 本机截图时 `MemoryLoadBytes` 接近 0，该路径只有单测 |
| 发布自检的 portable 形态 | `%TEMP%` 在 portable 下受限，且删中间 dll 后 `PublishTrimmed` 重跑的 ILLink 表现未测 |
| `--versionType` 的界面交互 | 「取消勾选 → TextBox 解锁」只有塞数据回填的截图，没有真点击 |
| `options.txt` 写失败分支 | 只读 / 被占用文件抛 `IOException` → warn 不阻断，该路径没有专门用例（构造只读文件会污染临时目录权限） |
| authlib 的真实连通性 | `-javaagent` 到真实验证服没测过；注册按钮只用 `RecordingPlatform` 断言了 URL 被交出，没在实践中点过 |
| bat 的 BOM 行为 | `task-19` 报告 936+UTF-8(BOM) 让首行变 `'﻿@echo'`；`task-22` 在 Windows 11 cmd 上未能复现（两版逐行相同）。已固定无 BOM（更稳），差异本身没有解释 |
| 系统 ACP 切到 65001 | 要改区域设置并重启，只验证了编码选择函数 + 无 BOM + `chcp 65001` 下脚本可执行；932 等代码页只测了选择函数 |
| 下载超时窗口对 `ResponseContentRead` 含 body | handler 层能力天花板（§6.54），要真正 headers-only 得改调用点用 `ResponseHeadersRead` |
| `8u320` 建议级提示 | `JavaLoadLimit.Exceeds` 与 `ParseJavaVersion` 都就绪，但没有任何 UI 消费这条建议 |
| 加载器上限的兜底分支 | `VersionsViewModel:3597` 的「按实例解析目录抛异常、改用全局目录」分支里 `LoaderVersion` 为 null，Java 判定退回粗粒度（与改动前一致） |
| 插件提示条的可见性 | `HasServerPluginNotice` 的 true/false 有 VM 级用例，XAML 绑定由编译期绑定校验保证，但「提示条真的显示出来」没有像素证据——截图模式选不中带插件的项目 |
| CF 翻页在 429 下的表现 | 会退化成交出已取部分 + `LastError`（UI 显示「限流」而不是完整列表）；没加退避、没做页间并发 |
| `FilesPageSize = 1000` 的服务端接受度 | 只在本机网络验证被接受；若某天压回 50，功能仍正确，只是 JEI 需要 79 次请求 |
| CF 侧的插件识别 | `CurseForgeService` 搜索固定 `classId=6`，不会混进 5（Bukkit Plugins），所以「按返回项 classId 判插件」这条路径当前不存在；要支持需改两处（`ClassIdOf` + `ParseSearch` 读每项 classId） |
| 整合包 `overrides/` 自带 `plugins/` | 解压不改目录结构，行为正确，但没有专门用例 |

### 8.3 技术债

- ~~页面 ViewModel 的 `CancellationTokenSource` 未实现 `IDisposable`~~ 已在第二十三轮处理：`PageViewModelBase`
  实现 `IDisposable` + `protected virtual void Dispose(bool)`，`AccountsViewModel` / `DownloadViewModel` /
  `HomeViewModel` / `SettingsViewModel` 四个页面覆写并 `Cancel → Dispose → 置 null`。
  剩下的 `ServersViewModel` / `ModsViewModel` 是同一条 CA1001，文件在并行任务域里，下一轮顺手补。
  logs 页面刻意改用序号防过期绕开，不需要 CTS。
- 界面仅简体中文，无 i18n 基础设施（`AppSettings.Language` 死字段已在第十一轮删除）。
- **世界页图标的「已解码位图」没有失效通道**：`WorldIconService` 会按 `level.dat` 重写缓存文件，
  但 `PathToImageConverter` 的进程级静态缓存按路径键住，同一进程内看到的仍可能是旧图。
  要修得给转换器加「按文件版本失效」或改成每卡片自己持有 `Bitmap`（会牵动模组页的 700 卡片内存表现）。
- **存储分区只在停在该分区时响应 F5**（`SettingsViewModel.ReloadCurrentAsync` 覆写）；其它分区按 F5 仍只重读设置。
  这是刻意的（避免每次 F5 都去扫几百个目录），但如果以后有人给存储扫描加了缓存，要连带改这里。
- 存储扫描结果不落盘：每次进分区扫一次，「上次统计时间」只存在于本次会话。
- ~~**版本页的缺 Java 区块与首页各写一套渲染**~~ 已在第二十八轮合并：`App/ViewModels/JavaInstallState.cs`
  一份实现，三处各持一个实例；差异（主版本来源 / 路径落点 / 三份文案 / 进度域 / 可见性）在 API 上落下来，
  像素对比零退化（§6.52、§3）。
- **日志跟随的轮转判据有一处漏判**：`offset-1` 恰好是新文件里的换行时不会判成轮转，
  表现只是多显示旧内容，不丢新内容；要修得比对尾部指纹（inode / 首 64 字节），代价大于收益，先记着。
- ~~**`build.ps1` 没解决「未清理 obj 就发布」这一步**~~ 已在第二十八轮解决：脚本在构建前检测
  XAML 中间产物是否比源旧，过期**只清 `obj/<Configuration>` 下的 `Avalonia` 目录**（不删整个 obj，
  删了会在 ILLink 阶段失败），并在发布后跑两道产物关（§6.44）。
- **两套密码/凭据保护并存**：账户令牌走 `TokenProtector`（解不开就重新登录），代理密码走
  `ProxyCredentialStore`（解不开按未设置）。熵与语义都不同，刻意没合并；若以后要加「导出加密配置」，
  得先想清楚两者在导出包里的处置差异。
- **安装批次期间普通下载被拒绝**（`InvalidOperationException`，§6.19 第 1 条）——要并发得扩队列模型，不是加锁能解决的。
- ~~`NeoForgeInstallerBridge` 用反射绕 CS0433~~ 已消除：新桥接不碰 `NeoForgeInstaller` facade，其余类型全文名不冲突（§6.33）。
- ~~NeoForge 版本列表页仍是空的~~ 已通：`LoaderMetaUrls.NeoForgeCatalog` + `VersionService.GetNeoForgeVersionsAsync`
  拉两族 maven 目录。注意「首选组整族优先、兜底组只在整族失败后试」——官方 `maven.neoforged.net`
  在本机 TCP 超时，若在镜像后面紧跟官方兜底，每次都会白等两个 21 秒连接超时（截图里表现为「加载中…」永不结束）。
- `LoaderMetaUrls.ForgePromotions` 的候选**永远官方优先、不随下载源切换**：BMCLAPI 的 `/forge/promos` 滞后
  二十个小版本（实测 `52.1.0` vs 官方 `52.1.20`）。这一条与 PCL2 刻意不同，别「优化」回去。
- `JavaRequirementTable` 的规则永远滞后于 Mojang。
- 浅色主题仍有首帧闪烁（窗口尺寸修了同类问题，主题没有——设置在窗口建好后才应用）。
- ~~20 个编译警告~~ 已在第二十四轮清零：`Core` 与 `App` 两个项目 `-t:Rebuild` 都是 **0 错误 0 警告**。
  做法分三类，没有一处是「加个 `#pragma` 了事」：
  - **真缺陷**：`LibraryViewModel` 自己声明了 `public void Dispose()`，把 `PageViewModelBase.Dispose()`
    隐藏掉了（`CS0108`）——容器按 `IDisposable` 调用时命中基类版本，子类的 5 个 CTS 永远不释放。
    改成覆写 `Dispose(bool)` 并置 null（`_modpackCatalog` 是 `readonly`，只释放不置 null）。
    `ServersViewModel` / `ModsViewModel` 的 `CA1001` 同批补上。
  - **分析器看不到 XAML**：`CA1822` 的 9 个成员全部被编译绑定引用（`SettingsViewModel` 的 `ServiceWarning`
    被 4 个页面共用、`WorldsViewModel` 的 4 个删除确认命令走 `$parent[ItemsControl]`）。改成 static 会让
    绑定路径在编译期解析不到，所以在 `Directory.Build.props` 对 App 项目 `NoWarn` 掉 `CA1822`，
    注释里逐条列了成员与 `axaml` 行号。Core 与测试项目不受影响。
  - **确实是死代码/可具体化**：`CA1859` ×3（`EnterLoaderTempScope` / `BuildServices` / `Brush`）、
    `CA1822` ×3（`BuildRedactedSettings` / `BuildDataPackItem`）、`CA1863` + `CA1305`（`InstallerOutputLog`
    的重复计数格式串改用 `CompositeFormat` + `InvariantCulture`，否则阿拉伯语等区域会把数字写成非 ASCII 形，
    日志没法 grep）、`CS0618`（`e.Data.GetFiles()` → `e.DataTransfer.TryGetFiles()`，
    Avalonia 11.3.22 的 `DataTransferExtensions.TryGetFiles(IDataTransfer)` 返回同样的 `IStorageItem` 序列）。

---

## 9. 常见任务怎么做

| 任务 | 步骤 |
|---|---|
| 加一个页面 | `XxxViewModel` + `XxxView.axaml`（根写 `x:DataType`）→ `NavigationItem` 加键与图标 → `AllKeys` / `PlaceholderMeta` / `BuildNavigation` → 对应 partial 里 `RegisterPage` |
| 加一个 Core 服务 | `Abstractions/` 加接口（新成员带默认实现）→ 目录下实现 → 目录下 `AddXxx()` 扩展 → `App.BuildServices()` |
| 改视觉令牌 | 只改 `Themes/Tokens.axaml`，页面里不写颜色；`Dark` 与 `Light` 两个字典各改一次 |
| 加托盘菜单项 | `App/Services/TrayService.cs` 的 `BuildMenu`；菜单动作失败只写日志，不要弹窗 |
| 改关闭行为 | 判据只改 `Core/Platforms/CloseBehavior.cs`（有单测），窗口侧只负责执行 |
| 加日志源 | `Core/Logging/LauncherLogReader.cs` 的枚举与解析；新格式先在 `FileLogger` / 游戏日志里确认真实样子 |
| 加设置项 | `Models/Settings.cs` 加属性 → `SettingsService.Normalize` 加校验 → **`Clone` 同步** → 设置页表单 → 需要即时生效就订阅 `Changed` |
| 加下载源规则 | `DownloadMirror.BmclApiMap` 加前缀（长前缀在前）；新域名同步 `IsKnownDownloadHost` |
| 改自动降级判据 | 只改 `Downloads/DownloadSourceGovernor.cs` 的阈值与 `Report`；时钟必须走注入的 `Func<DateTimeOffset>` |
| 加加载器 meta 源 | `LoaderMetaUrls` 返回候选列表（首选在前），不要写死单 URL |
| 加崩溃规则 | `Launching/CrashRules.cs`；文案只写「疑似」。`hs_err` 专属规则在文件末尾的 `HsErr` 段 |
| 加一条脱敏规则 | `Core/Logging/LogRedactor.cs` 加 `Regex` 接到 `Redact`；必须同时有「命中」与「不误伤」两条测试（URL 里的 `/home/` 曾被误伤） |
| 加一类本地内容 | `ContentKind` 追加值（不改已有数值）→ `ContentService.DirectoryNameFor` 与 `ContentTarget.DirectoryNameOf` **两处**都要加 → 扫描/启停按「文件 + 目录」两种形态想 → 模组页加分类 chip |
| 加一类用户数据文件 | 参考 `Versions/VersionFavorites.cs`：独立 json、原子写、坏文件静默重建、`Changed` 事件 |
| 要「外部改文件后重读设置」 | 调 `ISettingsService.ReloadAsync`，不要调 `LoadAsync`（后者幂等） |
| 改配置包白名单 | `Transfer/ProfileBundleService.cs` 的 `ProfileSettingsWhitelist` + 测试里的键集断言 |
| 加一个分阶段进度 | `Models/Downloads.cs` 的 `InstallStagePlan` 加区间与 `Describe`；映射必须只增不减（§6.26） |
| 加一项前置检查 | 不依赖 Java 的放 `CheckEnvironmentAsync`，依赖已选 Java 的放 `CheckJava`；每项内部必须各自 catch |
| 改 Java 允许区间 | 只改 `JavaRequirementTable.All` 与 `AllowedRange`；`mandatory` 与 suggested 要分清 |
| 页面要「当前游戏目录」 | 注入 `IInstanceContext`，用 `GameDirectory` / `Current` / `ListTargetsAsync` |
| 加在线数据源 | `Abstractions/` 加接口 → `Models/` 加模型 → `Mods/` 实现（失败写 `LastError`，不抛）→ 页面按 `ContentSourceKind` 切换 |
| 改帮助内容 | `Core/Help/HelpCatalog.cs` 的 `BuildTopics()`；新分类同步 `Categories`。**只写真实存在的行为** |
| 加客户端快捷键 | `MainWindow.axaml.cs` 的 `RegisterShortcuts()`；需要页面上下文的（Esc）放 `MainWindowViewModel.Escape()`，不加全局热键 |
| 让某操作显示在下载面板 | `DownloadQueue.BeginExternalBatch(name)` 拿 `IDownloadTaskScope`，逐项 `Report(...)` 后 `Complete()`；不要另建一套进度 UI |
| 批量操作磁盘文件 | 复用 `Core/Mods/ContentBatch.cs` 的「逐项 try/catch + 磁盘复核」，不要信 `SetEnabledAsync` 的返回值 |
| 加一个组件检查维度 | `Installing/ComponentInspector.cs` 的 `CheckFile` / `KindOf`；容差与 `McInstallManifest` 保持一致 |
| 截设置页分区 / 长页 | `shot.ps1 -Section game -Scroll 1000`；设置页存储分区是 `-Section storage` |
| 给某目录加运行锁 | 注入 `IInstanceLockService`（`AddInstanceLocks()`）：`TryAcquireAsync(gameDirectory)` 返回 `null` 就是「有进程在跑」，放行失败要走到 `LaunchResult.FailureReason`，释放只走 `Dispose`（内部比对令牌）。判活逻辑不要另写一套（§6.35） |
| 给某个长任务加暂停 | 参考 `PauseGate`：闸门必须放在「拿到并发槽之后、真正干活之前」，且复位点覆盖入口 / 成功 / 取消 / 异常四条路径（§6.36） |
| 加一类可清理目录 | `Models/Storage.cs` 的 `StorageCategoryKeys` + `StorageService` 的类别表 + 路径白名单 + `CoreMaintenance_StorageTests` 里的越界用例（§6.37） |
| 新增截图交互态 | `Services/ScreenshotMode.cs` 解析 → `App.AttachScreenshot` 设置 ViewModel 状态 → `tools/shot.ps1` 透传；开关是正式能力，不是临时代码（§2.3） |
| 加「缺失依赖一键补齐」类按钮 | 先写 Core 纯函数判定「能不能装」（`JavaProvisionPlanner` 是范式）：只有真的能装才给按钮，装不了就退回手动配置（§6.38） |
| 给日志页加实时能力 | 增量读取放 Core（`LogFollower`）：半行不推进偏移、截断/轮转自愈、单次读取封顶；定时器只在页面激活时跑（§6.39） |
| 加实例级目录入口 | 目录表只存相对名，绝对路径用 `GetGameDirectory(instanceId)` 现拼；枚举不建目录，打开前才创建（§6.40） |
| 诊断写入失败 | `NebulaLauncher.exe --probe-dir "<目录>"`，看 exe 同级 `nebula-probe-dir.txt` |
| 端到端安装回归 | `InstallProbe <目录> 1.20.1 Fabric`（约 12 分钟）或 `1.20.1 Forge`（约 1 分钟） |
| 看安装器原始输出 | `grep '\[Forge\]\|\[NeoForge\]' <data-dir>\logs\launcher-*.log` |

### 多人 / 多 agent 并行

1. **构建配置隔离**：各用各的 `-c`（`Lead` / `inst` / `pf2` / `comp`…），产物在 `bin/<配置>`，互不锁 obj。
2. **数据目录隔离**：每个实例带 `--data-dir`，否则并发写同一份 `accounts.json` / `settings.json`。
3. **不杀别人的进程**：禁止 `Stop-Process` 任何 `dotnet` / `NebulaLauncher`。
4. 临时调试代码必须在提交前删干净，自查：`grep 'TEMP-SHOT|PREVIEW'`（当前 0 命中）。
5. 并行时中途构建失败多半是别人正在改文件，等 10–30 秒重试；**不要**去改别人的文件「修好它」。

---

## 10. 交接检查清单

- [ ] `. .\env.ps1` + `$env:AVALONIA_TELEMETRY_OPTOUT='1'` 后 `-t:Rebuild` 构建 **0 错误 0 警告**（Core 与 App；测试项目另有 30 条既有 CA/xUnit 警告，见 §8.3）
- [ ] `TestRunner -c Lead` 跑出 **1467/1467**
- [ ] `seed-demo-data` + `shot.ps1` 能出图（`home` / `versions` / `library` / `worlds` / `logs` / `help` / `settings -Section game -Scroll 1000` / `settings -Section storage`）
- [ ] 本轮三个新能力各截一张：`home -MissingJava`、`logs -LogFollow`、`versions`（目录芯片）；版本页缺 Java 区块单独一张 `versions -MissingJavaVersions`
- [ ] 浅色主题能出图：`shot.ps1 -Page home -Theme light`；深色同页无回归
- [ ] 窄屏不丢抬头副标题：`shot.ps1 -Page versions -Size 1024x660`（§6.18）
- [ ] 设置页能保存；改游戏目录后页面即时切换；窗口尺寸记忆生效（改尺寸 → 关闭 → 重开，尺寸还原）
- [ ] `build.ps1 -Clean -Publish -Mode minimal` 出 23.4 MB / 3 文件，且 `--probe-dir` 与干净 `--data-dir` 截图两道关都过（§6.43）
- [ ] `InstallProbe` 跑一次 1.20.1 Fabric（约 12 分钟）或 Forge（约 1 分钟）
- [ ] `--probe-dir` 在可写目录返回 `writable=YES`
- [ ] 若目标机器限制写用户目录：确认提权 / 目录回退 / 诊断命令仍可用（§6.4）
- [ ] 新增页面的 `NavigationKeys` 在三处都登记了、`RegisterPage` 也加了
- [ ] `grep 'TEMP-SHOT\|PREVIEW'` 在 `src` / `tests` / `tools` 下 0 命中
- [ ] 新增的设置字段同步了 `AppSettings.Clone`；新增的用户数据文件没有塞进 `settings.json`

---

## 11. 历史轮次去哪看

| 内容 | 位置 |
|---|---|
| PCL2 / HMCL 功能对照、逐轮「抄了什么、为什么没抄」 | `docs/PCL2-PORTING.md`（逐轮记录）+ `docs/HANDOVER-2026-10-23-R32.md`（最新一轮） |
| 扩展功能清单、实现状态、下一批候选 | `docs/EXTENSIONS.md`（§0.0.18 是本轮，§0.0.8 仍是未开工候选池） |
| 发布产物检查表 | `docs/PUBLISH-CHECK.md` |
| 视觉规范（颜色/字号/间距/圆角/动效/双主题） | `docs/DESIGN.md` |
| 编码与协作规范 | `CONVENTIONS.md` |
| 构建 / 发布细节 | `README.md`、`build.ps1` 头部注释 |

十三轮下来的形状：第八轮补托盘、日志中心、下载任务明面、浅色主题；第九轮补安装链逐文件进度、`hs_err`、
外部报告导入、窗口尺寸记忆与快捷键、实例备注、模组批量；第十轮补版本收藏/隐藏、三档下载源、任务栏进度、数据包；
第十一轮补数据包在线安装、崩溃脱敏导出、服务端下载、Forge 分阶段进度，删掉 `Language` 死字段；
第十二轮补 `hs_err` 与 mods 列表进导出、`pack_format` 兼容提示、配置包、服务端缓存、设置幂等、日志半行；
第十三轮补组件面板、1.21.9+ 区间语义、安装器 stdout 日志，并查清 NeoForge 的三层失败原因；
第十六轮（`HANDOVER-2026-10-22-R16.md`）按 `PCL2-GAP.md` 的 13 条清单集中落地：整合包导入闭环、
HMCL/MCBBS 识别、在线整合包分区、模组页四项、平台级安装四项、NeoForge 真装通；
同日晚些时候的第二十轮（同文件 §6）补：HTTP 代理设置、MCBBS `addons` 逐项反查、资源页排序、
模组卡片右键菜单、Legacy NeoForge 1.20.1 真装通。
第二十三轮（`PCL2-PORTING.md` §23–§25）转向 PCL2 没有的能力：崩溃建议的六种动作按钮（含「归因唯一时禁用肇事 mod」）、
服务器 Query 玩家名单（GameSpy4 UDP）、存档 `level.dat` 编辑（NBT 写回 + `.bak` + `session.lock` 占用拒绝）、
页面级取消源释放。三条自研功能的关键约束写在 `PCL2-GAP.md` §0.2 末尾。

第二十四轮（`PCL2-PORTING.md` §26–§27）继续做 PCL2 没有的东西，外加一次全仓质量收口：
**实例原地换加载器**（`InstanceService.Loader.cs`：同 MC 版本内把实例的 `VersionName` 指向新的加载器版本，
mods / 存档 / config / `servers.dat` 一个字节不动，因为「版本目录」与「实例目录」本来就是分离的；
跨 MC 版本拒绝，Forge↔Fabric 这类互换要过二段确认）、
**Modrinth 镜像兜底**（`mod.mcimirror.top`，规则取 HMCL 的 `fallback=true` 语义，**只在官方连不上时才用**——
官方 404 是「项目不存在」，不能触发镜像，否则每查一个不存在的项都多等一轮超时；
开关默认关，第三方服务不能默认把查询发出去）、
**只读目录覆盖**（`PathProbe.TryMakeWritable` 清 `FileAttributes.ReadOnly`，`EnsureInstallEnvironment`
探测失败时重试一次，覆盖前留一份原地 `.bak`）、
**文件名清洗统一**（`Core/Paths/PathSafety.cs`：原来 5 处各写一套，只有一处去结尾点号，没有一处处理
Windows 保留名 CON/PRN/AUX 与 100 字符上限——`World.` 会被静默建成 `World`、`CON` 直接建不出来、
超长名字直到写盘才失败）、
以及**全项目 0 警告**（见 §8.3）。

第二十五轮（`HANDOVER-2026-10-22-R25.md`）在 `PCL2-GAP.md` 的 13 条清单清空之后，转向「用户每天都会撞上的四件事」：
**实例运行锁**（`Core/Instances/InstanceLockService.cs`，锁文件按**游戏目录**而不是实例名，PID + 进程启动时间判活，
损坏 / 取不到进程一律按僵死接管，释放只删自己令牌的那份；版本页显示「运行中」并禁用删除 / 修复 / 换加载器 / 清理）、
**下载暂停 / 继续**（`PauseGate` 是异步闸门不是取消：在途文件下完即停，重试循环每次 attempt 都要再过闸门，
外部安装批次不可暂停）、
**存储占用与清理**（设置页新增「存储」分区，四类占用 + 单类 / 选中项清理 + 确认浮层，路径规范化 + 白名单 + 日志保留 7 天）、
**存档缩略图**（读 `level.dat` 的 `Data.icon`，PNG magic + 双上限校验，按 `level.dat` 的 mtime/长度失效的磁盘缓存，
世界页 48×48 圆角缩略图 + 无图标占位块）。四条能力的关键约束分别写在 §6.35–§6.37 与 §6.35 的判活段。

第二十六轮（`HANDOVER-2026-10-22-R26.md`）接着补「启动不起来的下一步」与「看不下去的两处体验」：
**缺 Java 一键安装**（`Core/Java/JavaProvisionPlanner.cs` 纯函数判定「能不能装」，首页失败面板直接给
「安装 Java N（约 xx MB）」→ 下载 → 写回 `Settings.JavaPath` → 强制重扫；上界冲突与不可下载主版本退回手动配置）、
**日志实时跟随**（`Core/Logging/LogFollower.cs` 增量读取 + 700ms 定时器；半行不推进偏移、截断与轮转自愈、
单次 ≤256KB、界面保留 5000 行）、
**实例目录快捷芯片**（`Core/Instances/InstanceFolders.cs` 五项固定表 + 版本页芯片行，实心/空心点区分是否存在，
打开前才建目录）。约束写在 §6.38–§6.40。

第二十七轮（`HANDOVER-2026-10-22-R27.md`）把「长期挂着的候选」清掉三条：
**下载源测速**（`Core/Downloads/DownloadSourceProbe.cs`：自建不带改写 handler 的客户端，排序是纯函数，
30% 内判持平，只提示不写设置、不碰 governor）、
**代理密码 DPAPI 加密与迁移**（`Core/Net/ProxyCredentialStore.cs`：磁盘只存 `proxyPasswordProtected`，
加载即迁移并落盘，解不开按未设置，配置包双剥，界面取消「显示密码」）、
**发布产物验证**（`docs/PUBLISH-CHECK.md`：23.4 MB / 3 文件、无测试与工具混入、干净首启/二次启动/中文路径通过，
并查清「未清理 obj 就 publish 会产出静默不启动的 exe」）。约束写在 §6.41–§6.43。

明确不抄的（皮肤系统、OptiFine 安装、分片下载、公告/自更新/音乐、中文搜索词表、内存优化/百宝箱）
在 `PCL2-PORTING.md` §7.3 / §10.3 / §11.3 / §12.3 / §13.3 逐条写了理由——
其中「内存优化」经实证是 PCL2 开源版里的桩，不是可抄的能力。

第二十八轮（`HANDOVER-2026-10-23-R28.md`）把挂起的三件事与最后一轮缺口一起清空：
`TAKEOVER.md` §5 的**源测速 CLI 入口**（`--probe-sources`，三行固定格式 + 退出码）、
**统一 Java 安装状态机**（`JavaInstallState` 一份实现三处共用，像素对比零退化）、
**发布自检**（`build.ps1`：XAML 中间产物过期检测 + 产物两道关）；
以及 `PCL2-GAP.md` 清单归零后新查出的十条真缺口——**停止运行中的游戏**、
**默认注入的 JVM 参数**（四条 + Windows 信任库 + Java 24+ 压缩对象头）、
**启动参数去重与覆盖**（含「吸收」窗口参数，只做列表内去重不够）、
**模组卡片去资源站查找**（跨页预填 + 只搜一次）、**内存三段可视化**、
**双源合并搜索**（PCL2 `ResourceSearcher` 的权重与 `IsLike` 去重）、
**多实例并行运行**（按目录准入 + 按会话停止 + 日志尾按会话隔离）、
**`--versionType` 自定义**（空值要连键和值一起删）、
**`options.txt` 语言分版本规则**（原来 1.1~5 会 NPE 崩溃）。
约束写在 §6.44–§6.52，`docs/PCL2-GAP.md` 第 13 条同时完成。

第二十九轮（`HANDOVER-2026-10-23-R29.md`）做了**第二轮 PCL2 缺口复查**（第一轮只覆盖参数组装与几个页面，
一半模块没查）并清空全部 7 条已确认缺口：

- **四个启动入口统一走 `LaunchOptionsFactory`**——不是抄 PCL2，是复查中撞见的 P0 缺陷：
  服务器页「加入服务器」不设 `GameDirectory`，启动全局 `.minecraft`，隔离实例的模组全失效且绕过运行锁（§6.53）；
- **崩溃归因两条**（极短输出兜底五条件 / 版本 JSON 多个 Forge 的精确串，§6.59）；
- **下载源按响应头超时换源**（镜像 5 秒 / 官方与资源 30 秒，§6.54）；
- **服务端 `Launch Server.bat`**（GB18030 按 `GetACP()` 自适应，§6.55）+ **rar/7z 可操作提示**；
- **Java 上限按加载器小版本分档**（7 条 PCL2 规则，补丁号只做建议，§6.56）+ `LoaderVersion` 全链路打通（§6.57）；
- **实例级登录服务器**（authlib 三态，§6.58）。

第 8 条缺口（`java_launch_wrapper` / LWJGL Unsafe Agent）裁决为**不做**：要分发第三方二进制、
`-javaagent` 与 authlib 冲突、收益场景窄（R29 文档 §8）。其中 `JLW` / `LUA` 已记入 `TAKEOVER.md` §5。
约束写在 §6.53–§6.60，其中 **§6.60 是复查方法本身的修正**（`Select-String -Path 'src\**\*.cs'` 的
`**` 只等价一层目录，会漏 `src\<项目>\<子目录>\*.cs`，因此推翻了两条初判）。

第三十轮（`HANDOVER-2026-10-23-R30.md`）的起点是一次**覆盖率盘点**——前三轮复查都集中在
「参数组装 / 崩溃 / 下载 / Java / 实例」这条线上，没人统计过 PCL2 那 113 个 `.vb` 到底查过多少个。
盘完（`.probe/pcl-coverage.md`：已覆盖 62 / 等价 20 / 明确不做 21 / 未覆盖 7 / 不适用 3）发现整片盲区，
本轮修掉其中五条、裁决不做两条：

- **CurseForge 文件列表翻页取全**（原来单次 `pageSize=50`，JEI 的 3950 个文件只显示 50 条，
  实测修复后 3950 条 / 0 重复 / 3.6 秒；终止判据对每页上限不敏感，§6.61）；
- **Modrinth 服务端插件识别**（`ContentKind.Plugin = 5`，识别读原始 `categories` 不读 `display_categories`、
  `WebUrl` 走 `/plugin/`、拒绝在 IO 之前，§6.62；搜索安装拒绝而整合包落 `plugins/`，§6.63）；
- **保留名补 `CLOCK$` / `COM0` / `LPT0` + NTFS 8.3 短名 `.{2,}~\d+`**（§6.65）；
- **插件提示条接 XAML + 整合包插件落 `plugins/`**（收口 `task-27` 的两条未尽项）。

裁决不做：**联机功能**（`PageLinkMain.xaml.vb` 1116 行 / 12 个子系统）——三条前置阻塞：上游在非官方分支
直接 `Throw`「开源版无法联网获取配置」(:429-431)、配置与 EasyTier 二进制都来自 PCL 官方服务器、
要分发第三方二进制；功能拆解与 6 个可独立交付切片留在 `.probe/pcl-gap-r30.md` §2.1。
另三块盲区判**不是缺口**并给了证据链：`ModEvent`（WPF 附加属性式声明绑定，Avalonia 用 ICommand 等价）、
`FormMain` 更新部分（`ModSecret.vb` 全文是开源版空桩，PCL2 自己就没有自更新与联网公告）、
`PageSetupSystem`（9 个入口：4 等价、5 明确不做，其中导出/导入设置在 PCL2 里就只弹「尚在开发中」）。

约束写在 §6.61–§6.67，其中 **§6.64 把覆盖率盘点表定为「还有没有没抄的」唯一判据**，§6.66–§6.67 是第三十二轮补的两条。

第三十一轮（`HANDOVER-2026-10-23-R31.md`）第一次给出端到端证据：`C:\Program Files\Eclipse Adoptium\jdk-21.0.12.101-hotspot`
一直在机器上，挂了三轮的「本机没有 Java 21」是没验证过的假设。写 console 探针走 `IInstallService` + `IGameLauncher`
（不绕过任何一层）装 1.20.1（3641 文件 / 788 MB）并启动，**92 秒到 atlas 建完，`Realms Notification Availability checker`
出现即进主菜单**。同轮把 `ResourceFetcher.GetStringAsync` 改成 `ResponseHeadersRead`（换源窗口原本对「头快 body 慢」失效），
并补上插件提示条 XAML + 整合包插件落 `plugins/`。中断事实见 `docs/R31-BROKEN-WORK.md`。

第三十二轮（`HANDOVER-2026-10-23-R32.md`）回答「还有没有没抄的」：用四个机械信号（URL 端点、外链跳转、落点存在性、
状态分布）替代人工通读，查出两条漏项并补齐——**版本清单四档分类**（`VersionChannelClassifier`：愚人节版档、
Mojang 误标 1.x 纠偏、4/1 按 UTC+2 兜底，§6.66）与**模组卡片「在 MC 百科搜索」**（§6.67）。
同时纠正覆盖率表：统计由「62/20/21/7/3」按行重算为 **49/46/11/0/7**，6 类不存在的落点类型名改为真实落点（§6.64）。
结论：**除已裁决不做的项之外，没有未实现的 PCL2 功能**；剩下的都是验证项。

### 下一轮（R32 视角）

功能清单已空，剩下全是验证。按「成本最低、能证伪最多」排：

1. **正常退出路径**：探针现在到点就 kill，`latest.log` 里没有 `Stopping!`。要验的是退出时的存档备份 / 实例锁释放 /
   崩溃报告归档三条链路；改探针为「检测到主菜单后向窗口发 close」或延长观察窗后手工退。
2. **第二次启动 + 多开**：探针已带 `SKIP_INSTALL=1`，加跑一遍即可；多开要起两个实例目录。
3. **Forge / Fabric 真机**：Java 21 在机器上，装个 Fabric 1.20.1 成本很低；`LoaderVersion` / `--versionType` /
   JVM 参数分档 / Java 上限判定目前只有单测。
4. **像素证据两条**：插件提示条（`HasServerPluginNotice`）、下载页新增的两档筛选按钮。截图模式没有对应档位开关，
   要么加参数，要么接受编译期绑定校验。

长期挂着的两条不变：**组件新旧对比 + 恢复**（`PageDownloadModify.vb:ReloadSelected`，成本 L，要先有「安装前快照」）、
**`--server` / `--port` 与实例「自动进服」并存时仍会重复**（§6.50）。

### 第五十六轮（R56 视角）

起点是用户报「本地内容页不显示 mod 和光影图标」。实测把问题拆成两条断链：

1. **包内嵌图标的提取不全**：光影包与数据包的读取路径压根没读图标（`ReadShaderPack()` 返回空元数据、
   `BuildDataPackItem` 不看 `pack.png`），模组只认声明字段、不认 `assets/<modid>/icon.png`。
2. **资源站图标只对模组生效**：`ModProjectInfoService` 与 `ModsViewModel` 都按 `ContentKind.Mod` 早退，
   而光影包几乎从不带 `pack.png` —— 于是光影分类的图标恒为空。
3. 顺着「为什么 CF 上架的模组也没图标」查出**真正的长期缺陷**：CurseForge 指纹接口写成了
   `POST /v1/fingerprint`（单数）且按 int32 补码发负数，服务端分别回 404 / 400，
   而调用方把 4xx 降级成「未识别」不报错 —— 名字、译名、图标、更新检查全部静默失效（§6.70）。

修复后**真实样本端到端出图**：`industrialcraft-2-2.2.828-experimental.jar` 与
`jei-legacy-1.7.10-1.2.0.jar` 经 CurseForge 指纹命中（modId 242638 / 1554879），
卡片显示 CF 项目名、IC2 带 MC 百科译名、两张 logo 落到 `cache/curseforge-icons/`。
约束与判据写在 §6.69–§6.70；全量测试 2277/2277。

