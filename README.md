# Nebula Launcher

一个以 Windows 为当前验证平台、采用跨平台架构的 Minecraft 启动器。Avalonia 11 + .NET 8，纯原生渲染，不内嵌 WebView。macOS 尚未完成系统能力适配与实机验收。

## 状态

**现状以 [PCL 对照核查报告](docs/AUDIT-2026-10-23.md) 为准。下面的历史“完成”标签仅表示已有实现，不表示该模块无 bug、全部 PCL 行为等价或跨平台已经验收。** 报告分别列出已实现、部分实现、未实现、已修及未修问题；原“PCL 零缺口”结论已撤回。

| 模块 | 状态 |
|---|---|
| 工程骨架 / 设计系统 / 外壳导航 | 完成 |
| Core：路径 / 日志 / 设置 / 账户 / 版本 / 启动 / 实例 | 完成 |
| Core：平台 / 下载 / Java / 安装 / 内容 / 备份 / 服务器 | 完成 |
| Core：Modrinth 在线内容 | 完成 |
| UI：首页 / 版本 / 下载 | 完成 |
| UI：模组 / 资源 / 服务器 / 账户 | 完成 |
| UI：设置 / 世界与备份 | 完成 |
| UI：日志中心（启动器 + 游戏 + 崩溃报告） | 完成 |
| 系统集成：托盘 / 关闭到托盘 / 窗口尺寸记忆 / 窗口内快捷键 / 任务栏进度 | 完成 |
| 外观：深色 / 浅色 / 跟随系统 | 完成 |
| 内容：实例备注 / 模组多选批量 / 数据包（本地 + 在线安装 + 格式兼容提示） | 完成 |
| 版本：收藏置顶 / 隐藏 | 完成 |
| 下载：三档源（自动/官方/镜像）+ 会话级自动降级 / 服务端 `server.jar`（清单缓存） | 完成 |
| 诊断：崩溃报告导入 / 脱敏导出（含 hs_err 与 mods 列表） | 完成 |
| 安装：Forge / NeoForge 分阶段进度 | 完成 |
| 迁移：配置包导出 / 导入 | 完成 |
| 实例：运行锁（防重复启动）/ 目录快捷芯片（存档·模组·资源包·截图·日志） | 完成 |
| 下载：暂停 / 继续（闸门）/ 官方 vs 镜像源测速（只提示不决策） | 完成 |
| 维护：存储占用统计 + 安全清理 / 存档缩略图 | 完成 |
| 启动：缺 Java 一键安装（失败面板直接装） | 完成 |
| 日志：实时跟随（增量 tail，700ms） | 完成 |
| 安全：代理密码 DPAPI 加密 + 旧配置自动迁移 | 完成 |

**接手/恢复开发先读 `docs/TAKEOVER.md`**（一页式现状、待办、十个坑、固定开工动作）。

## 为什么是 Avalonia 而不是 WPF / Electron

约束来自需求本身：

1. **未来要出 macOS 版** → 排除 WPF（仅 Windows）。
2. **不用 WebView** → 排除 Electron / Tauri / NW.js。
3. **动画要顺、视觉要好** → 需要成熟的合成管线与动画系统，而不是手工逐帧绘制。
4. **尽量不造轮子** → 需要现成的 MVVM 工具链与 Minecraft 协议实现。

结论：Avalonia 11（Skia 渲染、XAML 声明式动画、跨平台单 TFM）+ CmlLib.Core（版本清单、
文件校验、启动参数、微软登录）。全部平台差异收敛到 `IPlatformService`，用运行时分支而非
`#if WINDOWS`，因此 Core 一行不改就能 `dotnet publish -r osx-arm64`。

## 功能

**基础**

- 微软账户登录（OAuth 设备码）：**内置 public client id，开箱即用**（设备码是 public client 流程，无 secret）；需要时用环境变量 `NEBULA_MS_CLIENT_ID`（兼容 PCL2 的 `PCL_MS_CLIENT_ID`）覆盖，显式清空即禁用。另有离线账户，以及**第三方验证（authlib-injector / 统一通行证）**：填 API 根与账号密码即可登录，启动时自动注入 `-javaagent`
- 版本管理：安装 / 修复（快速 · 深度）/ 删除 / 打开目录
- 原版下载，含 Fabric / Quilt / Forge / NeoForge
- **自定义游戏目录**：设置页选择 `.minecraft` 位置，带可写性预检；冷启动按 `settings.json` 生效，运行期切换后页面即时重载
- **版本隔离**：实例目录就是版本目录（`versions/<版本名>`，与 PCL2 / HMCL 一致），mods / saves / config 互不污染，库与资源仍共用全局目录；「全局目录」是独立目标，不被当成某个版本的原版
- Java 自动探测、版本匹配、**缺失时一键下载**（Adoptium，覆盖 8 / 16 / 17 / 21 / 25）
- 启动参数配置（内存、窗口、全屏、**JVM 参数命名预设**、直连服务器、**进程优先级**、**启动后启动器窗口行为**）
- **拖拽导入**：拖 `.jar` 进窗口装模组，拖 `.zip` / `.mrpack` 识别整合包
- 下载源三档（**自动 / 官方 / BMCLAPI 镜像**）：自动档在本会话里镜像连续失败或明显慢时降到官方优先，并在下载页显示当前源与降级原因
- **下载候选回退**：镜像与官方互为候选，连接失败 / 302 到不可达 CDN / 404 时自动换源

**扩展**

- 模组 / 资源包 / 光影包 / **数据包**扫描、启用禁用、冲突检测、版本不匹配提示、缺前置检测
- **模组信息与中文名**：元数据读取覆盖 `fabric.mod.json`、`quilt.mod.json`、`META-INF/mods.toml` / `neoforge.mods.toml`、**`mcmod.info`（Forge 1.7.10–1.12.2）**、`litemod.json`；进入模组页后后台按 SHA1 / CurseForge 指纹**批量**反查资源站，卡片显示 **MC 百科译名**（内置译名表）、资源站原名、摘要与项目图标，结果落盘缓存 6 小时
- **版本收藏置顶与隐藏**：星标置顶、只看收藏、隐藏不删文件（数据存独立的 `favorites.json`）
- **在线资源库**：页内显式选择安装目标（实例 / 已安装版本 / 全局目录），按目标版本与加载器搜索 Modrinth / CurseForge，一键安装；安装前展示**前置依赖清单与总大小**，`required` 依赖自动一起装（深度上限 8、防环）；**项目详情**给出正文、图库、源码与问题链接
- **在线整合包**：Modrinth / CurseForge 双站搜索（可双源合并），搜索不按游戏版本筛，版本列表取全部构建并支持按游戏版本再筛；下载后导入为新实例（`.mrpack` 与 CurseForge `.zip`）
- 整合包导入（`.mrpack` / CurseForge / MultiMC）与**导出 `.mrpack`**（哈希反查在线条目，未识别模组进 overrides）
- 实例管理：**克隆 / 重命名 / 图标 / 移入回收站（可恢复） / 目录清理**
- 世界存档备份与还原、**退出后自动备份 + 每个世界保留份数轮转**
- 服务器状态实时查询（Server List Ping）+ MOTD 着色 + 延迟
- 崩溃日志分析，给出中文成因与处理建议（优先读取 `crash-reports/*.txt`），**崩溃后自动打包报告与日志到 `crash-reports/archive`**
- **版本两档修复**：快速（补缺失文件）/ 深度（逐文件校验 libraries 与 assets 的 SHA1）
- **Java 下载优先走 Mojang `java-runtime` 清单**（吃 piston-meta 镜像），Adoptium 兜底
- **跨实例文件复用**：同 SHA1 的模组 / 资源包已经在本机别处存在时直接复制，不重复下载
- **内置离线帮助**：26 条功能说明与故障排查，按标题 / 关键词 / 正文加权搜索
- **官方正式版更新提示**：有新正式版且本机未安装时在首页提示一次
- **日志中心页**：启动器日志、`logs/latest.log`、`crash-reports/*.txt` 三处统一查看，支持级别过滤、关键字搜索、自动跟随、一键定位
- **下载任务明面**：逐文件状态（安装游戏本体也逐个文件可见）、失败详情与 URL、只重试失败项、最近 20 次批次历史（不落盘）
- **崩溃归因增强**：解析 `hs_err_pid*.log`（信号 / Problematic frame / native 模块，共 75 条规则），支持**导入他人给的 crash-report / hs_err / zip** 直接分析，结论带来源文件名；可**导出一份脱敏 zip**（分析结论 + 游戏输出 + hs_err + 启动器日志 + 系统快照 + mods 列表，token 与用户名已抹掉）直接发出去求助
- **窗口尺寸与最大化记忆**；窗口内快捷键（Esc / F5 / F11 / Ctrl+O / Ctrl+L）；安装与启动时**任务栏进度条**同步
- **实例备注**（卡片可见、长文本浮层）与**模组多选批量启停 / 删除**（逐项容错，700 项约 240 ms）
- **数据包（datapack）**：全局与存档级两处扫描，`.zip` 与目录两种形态都能启停，卡片显示 `pack_format` 与描述；**可从 Modrinth 直接搜索安装**（装前选全局或某个存档）；资源包与数据包都带**格式兼容提示**（`需 1.20.x` 之类，不阻止启用）
- **服务端下载**：下载页「服务端」分段，从 Mojang 清单取 `server.jar`，下载后校验 SHA1，附启动命令与 eula 说明；清单落盘缓存 6 小时，热启动 94ms 出列表
- **Forge / NeoForge 分阶段进度**：安装进度按「原版文件 → 加载器下载 → 处理器执行」三段映射，不再出现进度条从 97% 掉回 3%
- **配置迁移**：导出一个只含收藏 / 服务器 / 非机器相关设置的 zip（不含账户与令牌），换机器时导入合并
- **托盘图标 + 关闭到托盘**：最小化到通知区，托盘菜单可显示窗口 / 打开游戏目录 / 退出
- **深色 / 浅色 / 跟随系统**三态主题，语义令牌双字典，切换立即生效
- 星云动态背景、页面转场、环形进度、可关闭的动效开关

## 构建

.NET SDK **不在系统 PATH**，先加载环境脚本：

```powershell
cd NebulaLauncher
. .\env.ps1          # 设置 DOTNET_ROOT，本机 SDK 在 C:\Users\MECHREUO\.dotnet8
```

```powershell
dotnet build -v q --nologo          # 构建
.\build.ps1 -Test                   # 构建 + 跑全部测试
.\build.ps1 -Publish                # 框架依赖单文件发布 → pub\
.\build.ps1 -Publish -SingleFile    # 单文件 exe（见下方说明）
```

### 测试

本机沙箱拒绝 testhost 启动（`Win32Exception 5`），`dotnet test` 不可用。用进程内运行器：

```powershell
dotnet run --project tools/TestRunner/TestRunner.csproj -v q --nologo -m:1 -p:BuildInParallel=false
# 总计: 1502, 通过: 1502, 失败: 0
```

`-m:1 -p:BuildInParallel=false` 不是可选项：本机沙箱拒绝 MSBuild 多节点之间的命名管道通信，
项目图 ≥3 个时不加这两个参数会「0 error 但生成失败」。

它反射测试程序集、复用 xunit 断言，支持 `[Fact]` 与 `[Theory]+[InlineData]`。单类过滤在命令末尾加 `-- <类名片段>`。

构建前建议设 `$env:AVALONIA_TELEMETRY_OPTOUT='1'`：Avalonia 的 BuildServices 遥测会写
`%LOCALAPPDATA%`，该路径被安全策略拒绝时构建会报 `MSB4018 AvaloniaStatsTask`（与代码无关）。

### UI 截图

不用系统截屏（窗口被遮挡或没有焦点时会抓到别人的界面），改为让应用自己渲染视觉树：

```powershell
.\tools\seed-demo-data.ps1 -DataDir .probe\data-me -Force   # 4 版本 / 7 模组 / 3 世界等
.\tools\shot.ps1 -Page mods -NoBuild -Configuration Lead `
    -DataDir .probe\data-me -Out .probe\mods.png

# 设置页的非默认分区（截图模式无法点击，用 -Section 指定）
.\tools\shot.ps1 -Page settings -Section game -NoBuild -Configuration Lead `
    -DataDir .probe\data-me -Out .probe\settings-game.png

# 浅色主题（深色是默认，不需要传）
.\tools\shot.ps1 -Page home -Theme light -NoBuild -Configuration Lead `
    -DataDir .probe\data-me -Out .probe\home-light.png
```

应用自带参数 `--screenshot <png> [--page <key>] [--section <key>] [--delay <ms>] [--data-dir <dir>] [--size 1024x660] [--scroll <px>] [--theme dark|light|system]`，
截图后进程自动退出。页面键：`home` `versions` `download` `mods` `library` `accounts` `servers` `worlds` `logs` `help` `settings`。
`--scroll` 用于截长页面（如设置页「游戏」分区）首屏之外的内容；`--theme` 用于验证浅色令牌。

### 发布形态

实测体积（win-x64）：

| 命令 | 体积 | 前提 |
|---|---|---|
| `.\build.ps1 -Clean -Publish`（默认 minimal） | **23.4 MB / 3 个文件** | 目标机需 .NET 8 Desktop Runtime |
| `.\build.ps1 -Clean -Publish -Mode fdd` | 23.4 MB / 47 个文件 | 同上 |

**发布必须带 `-Clean`**：`obj/<Configuration>` 里留着旧 XAML 编译产物时，publish 出的 exe 会静默退出
（exit 0、无窗口、`--screenshot` 不产图），而同一二进制的 `--probe-dir` 却能正常打印。
完整实测见 `docs/PUBLISH-CHECK.md`。
| `.\build.ps1 -Publish -Mode portable` | 21.8 MB / 1 个 exe | 需要 `%TEMP%\.net\<app>` 可写 |
| `.\build.ps1 -Publish -Mode folder` | 98.5 MB / 231 个文件 | 无 |

默认形态：托管程序集打包进单个 exe（10.3 MB），原生库外置为两个 dll——
`libSkiaSharp.dll`（9.0 MB，渲染）与 `libHarfBuzzSharp.dll`（1.7 MB，文本整形）。
这两个是原生库，运行时必须落盘，无法塞进 exe 而不解压，所以最小是 3 个文件。

渲染已改为 WGL + Software（见 `Program.BuildAvaloniaApp`），因此不再需要 ANGLE 的
`av_libglesv2.dll`（5.2 MB）。

`portable`（真正单文件）需要首次启动把原生库解压到 `%TEMP%\.net\<app>`；
该路径被安防软件拦截时会直接启动失败：

```
Failure processing application bundle.
Failed to create directory [...\Temp\.net\NebulaLauncher\<hash>] for extracting bundled files.
```

遇到就用默认的 minimal，或设 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 指向可写目录。

为什么不是 PCL(17MB) / HMCL(18MB) 那种量级——它们**不携带运行时**：PCL 用 Windows 自带的
.NET Framework 4.8，HMCL 依赖玩家已有的 Java。.NET 8 不在系统自带组件里，由用户自行安装。

### macOS（未来）

Core 层零改动，Avalonia 侧只需换运行时：

```powershell
dotnet publish src/NebulaLauncher.App -c Release -r osx-arm64 --self-contained true
```

平台差异都在 `IPlatformService` 的运行时分支里（窗口材质、Java 搜索路径、文件管理器
打开方式、令牌加密方式），不会因缺少 Windows API 而编译失败。

## 目录

```
src/NebulaLauncher.Core/     业务层，零 UI 依赖，可单测
  Abstractions/              服务契约（冻结）
  Models/                    数据模型
  Paths/                     跨平台路径
  Logging/ Settings/ Accounts/ Versions/ Launching/ Instances/   ← 启动链
  Platforms/ Downloads/ Java/ Installing/ Mods/ Backups/ Servers/ ← 资源链
src/NebulaLauncher.App/      Avalonia UI
  Themes/                    设计令牌（深/浅双字典）与控件样式
  Controls/                  自绘控件（星云背景、环形进度、页面转场，均跟随主题）
  Views/ ViewModels/         页面（含日志中心）
  Services/                  导航、动效开关、主题切换、托盘、截图模式
  Assets/                    托盘图标
  Converters/                通用展示转换器
tests/NebulaLauncher.Tests/  xunit
docs/DESIGN.md               视觉规范
docs/HANDOVER.md             接手文档（环境、分层、坑、验证方式）
CONVENTIONS.md               协作与代码规范
```

## 数据位置

| 平台 | 启动器数据 | 默认 .minecraft |
|---|---|---|
| Windows | `%APPDATA%\NebulaLauncher` | `%APPDATA%\.minecraft` |
| macOS | `~/Library/Application Support/NebulaLauncher` | `~/Library/Application Support/minecraft` |
| Linux | `$XDG_DATA_HOME/nebula-launcher` | `~/.minecraft` |

`.minecraft` 可在「设置 → 游戏 → 游戏目录」改成任意可写位置，保存在
`settings.json` 的 `minecraftDirectory`；留空/删除该字段即回到平台默认。
命令行 `--minecraft-dir` 优先于设置，便于并行实例与截图隔离。

账户令牌在 Windows 上经 DPAPI 加密；其他平台仅文件权限保护，日志里会明确记录这一点。

微软登录的 client id 解析顺序：构造参数 → `NEBULA_MS_CLIENT_ID` → `PCL_MS_CLIENT_ID` → 内置默认值
（见 `MicrosoftDeviceCodeAuth.DefaultClientId`）。内置值只用于公共客户端设备码流程，
不包含任何密钥；如被微软撤回，按同一顺序覆盖即可，无需改代码。

## 开源依赖

- [Avalonia](https://avaloniaui.net/) — 跨平台 XAML UI
- [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core) — Minecraft 版本 / 文件 / 启动
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM 源生成器
- [Microsoft.Extensions.DependencyInjection](https://github.com/dotnet/runtime) — DI

协议实现参考 Minecraft 社区文档：版本清单（piston-meta）、Fabric / Quilt meta API、
Server List Ping（1.7+）、Modrinth 整合包格式。

## 数据与许可

模组中文名来自 **MC 百科**（mcmod.cn）条目，数据文件
`src/NebulaLauncher.Core/Resources/WikiEntries.txt` 取自
[PCL2](https://github.com/Meloong-Git/PCL) 的 `PCLCS/Resource/WikiEntries.txt`，
解析规则移植自同仓库 `PCLCS/Resource/WikiEntry.cs`。

- 本仓库不是 PCL 的官方衍生版本，也未获 PCL 作者背书；与该数据/规则相关的部分
  是按 PCL《存储库合理使用指南》使用的第三方内容。
- 署名：PCL（Plain Craft Launcher）作者 **龙腾猫跃**；译名数据整理者 **MC 百科**。
- 若需移除或替换该数据文件，删掉 `Resources/WikiEntries.txt` 并移除 csproj 里的
  `<EmbeddedResource>` 项即可：`ModNameTranslations.Count` 会变成 0，模组卡片回退成
  显示资源站原名，其余功能不受影响（`CoreMods_LegacyMetadataTests.译名表_嵌入资源可加载`
  会失败，那正是这条移除路径的哨兵测试）。

