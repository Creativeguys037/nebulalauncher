# PCL2 → NebulaLauncher 移植对照

对象：`D:\agent_workspace\PCL2-Source\`（Plain Craft Launcher 2.99.0.0，反编译产物，164 个 .cs / 92,270 行）
参照：`NebulaLauncher`（Avalonia 11.3 + .NET 8 + CmlLib.Core 4.1，9 个页面 / 20 个 Core 模块）
调研产物：`.probe/pcl2-research/` 下 A-launch / B-download / C-instance / D-crash-ui 四份规格，
合计 2,331 行、约 1,100 处 `文件:行号` 证据。

本文回答三件事：PCL2 有什么、Nebula 差什么、本轮抄了什么以及为什么有些没抄。

---

## 0. 一句话结论

PCL2 是「功能完整但架构是技术债样本」的 VB.NET 单体；Nebula 是「架构干净但功能面窄」的现代重写。
本轮从 PCL2 抄的是**判定逻辑与参数知识**（这些是踩坑换来的、与语言无关的资产），
不抄它的**实现形态**（静态类互调、86% 吞异常、UI 里写业务、内嵌 base64 依赖）。

---

## 1. PCL2 的实际形态（先修正三个常见误解）

| 误解 | 实况 | 证据 |
|---|---|---|
| `ModJava.JavaInit` 是 828 行的 Java 探测主体 | 只有 51 行，做配置迁移与触发刷新；真正的扫描在外部程序集 `JavaUtils`，本仓库 0 命中 `JavaSoft\|Registry\.` | `ModJava.cs:166-217`、`ModJava.cs:163` |
| `ModLoader.cs` 是加载器（Forge/Fabric）逻辑 | 是通用任务调度骨架（`LoaderBase` / `LoaderTask` / `LoaderCombo` / `LoaderWorker`）；加载器安装逻辑在 `ModDownloadLib.cs` | `ModLoader.cs:19/338/744` |
| PCL2 有主动测速、跨会话断点续传 | 都没有。多源只靠「顺序 + FailCount + 整文件重试一轮」；`PathTemp\Download` 每次任务开始清空 | `ModNet.cs:1109-1152`、`ModNet.cs:2815-2819` |

另有三处「源码里不存在」的能力（不要照文档想象）：

- **没有** `--launchTarget` 之类加载器启动参数（全树 0 命中）——这些写死在版本 JSON 里；
- **没有**游戏时长统计、**没有**托盘图标；
- **没有** `latest.log` 兜底读取——日志只来自 stdout/stderr 重定向，JVM 层崩溃时拿不到内容（`ModWatcher.cs:170-174`）。

---

## 2. 能力对照矩阵

图例：`✅` 已有且等价 / `🟡` 部分具备 / `❌` 缺失 / `⏭` 本轮不做（附原因）

### 2.1 启动链

| PCL2 能力 | Nebula 现状 | 本轮 |
|---|---|---|
| 10 阶段串行启动 + 权重进度（4/登录/15/2/2/1/1/2/1/1） | `LaunchPhase` 7 态，无权重模型 | ⏭ 权重只影响观感，不改变可观测行为 |
| 启动完成判定：窗口枚举 **或** 5 级日志里程碑 | 只有 `WaitForExit` | ✅ `GameWatcher`（里程碑 + 崩溃关键词） |
| 崩溃双判据：Loading 期退出即崩 / exit≠0 且发行年 ≥ 2012 | 只看 `exitCode != 0` | ✅ 接入 `GameLauncher` |
| 窗口接管：改标题（`{date}` / `{time}`）+ 最大化 | 无 | ✅ `IPlatformService` 窗口方法 |
| GC 族参数按 Java 版本注入 + 先清既有 `-XX:.*GC` | 全靠 CmlLib 默认 | ✅ `JvmArgumentBuilder` |
| 编码三件套（`stdout.encoding` / `stderr.encoding` / `file.encoding=COMPAT`，GBK→GB18030） | 硬编码 `Encoding.UTF8` | ✅ 同上 |
| 进程环境：PATH 追加 Java 目录、`appdata` 改写、优先级映射 | 无 | ⏭ 见 §4.1 |
| 自动内存四段斜坡（按 mod 数决定台阶，70%/40%/15% 递减） | 固定 4096 | ✅ `MemoryAdvisor` |
| `LatestLaunch.bat` 无条件导出 + accessToken 打码 | 只能「查看命令」 | ✅ `LaunchScriptExporter` + 实例面板入口 |
| 500 行环形日志缓冲作为崩溃分析唯一输入 | `CrashAnalyzer.DefaultTailLines` 已有等价 | ✅ 语义对齐 |
| 启动前/后自定义命令（`VersionAdvanceRun` / `RunWait`） | 无 | ✅ 模型 + 面板 |
| Natives 目录三级回退（版本目录 → `%APPDATA%` → `ProgramData`，全程短路径） | CmlLib 默认单级 | ⏭ 需要短路径 API，收益面窄 |
| OptiFine + Forge 的 `--tweakClass` 顺序修正 | 不支持 OptiFine | ⏭ 见 §2.6 |
| authlib-injector 三参数 + yggdrasil prefetch base64 | 全树 0 命中 | ⏭ 见 §4.2 |

### 2.2 下载与网络

| PCL2 能力 | Nebula 现状 | 本轮 |
|---|---|---|
| 多源候选回退（镜像 → 官方，302 到未知主机即弃） | ✅ 已有，且比 PCL2 更严格 | — |
| 阶梯并发多源（首源起，@N 秒加源，先完成者胜） | 串行 try | ⏭ 见 §4.3 |
| 分片下载（≥1 MiB 才分片，新分片从最落后线程尾部 40% 切） | 单线程整文件 | ⏭ 见 §4.3 |
| 分段正确性：`ContentLength == FileSize - start`，不等即判「假 206」 | 无分片，不需要 | ⏭ |
| 全局令牌桶限速 + 自适应线程下限 | 无 | ✅ `SpeedLimiter`（限速）；自适应 ⏭ |
| 进度曲线：`1-(1-raw)^0.9` 映射 0.05–0.98 | 线性 | ✅ |
| 动态超时 15s×(1+失败次数)，封顶 30s | 固定超时 + 3 次指数退避 | ⏭ 现有策略已够用 |
| 跨实例文件复用（同 sha1/size 的 libraries/assets/mods 直接 Copy） | 无 | ⏭ 见 §4.4 |
| 失败熔断（失败数超阈值整体失败） | 逐文件失败，不熔断 | 🟡 现状可接受 |
| 403/429 对 BMCLAPI 网开一面 | 无源级禁用概念 | ⏭ 现有候选回退已覆盖 |

### 2.3 本地内容与在线资源

| PCL2 能力 | Nebula 现状 | 本轮 |
|---|---|---|
| 启用/禁用改扩展名（`.jar` ↔ `.disabled` / `.old`） | ✅ `.disabled` 已有；未处理 `.old` | ✅ 补 `.old` |
| 哈希反查识别本地 mod（Modrinth SHA1 / CF MurmurHash2） | 只读 jar 元数据 | ✅ `ModFingerprint` + `ModUpdateService` |
| 哈希结果本地缓存（PCL2 写 `Cache\ModHash.ini`） | 无 | ✅ `mod-hashes.json` |
| 更新检查（`UpdateFile != null`） | 无 | ✅ 「检查更新」 |
| jar 元数据解析（`fabric.mod.json` / `mods.toml` 的 depends / breaks） | ✅ 已有，且比 PCL2 更完整（PCL2 不解析） | — |
| 依赖自动安装（装 A 时把前置 B 一起下） | ❌ | ⏭ 见 §4.5 |
| 资源站双源搜索（CurseForge + Modrinth）、中文搜索特化路径 | ✅ 双源已有；中文特化无 | 🟡 |
| 下载链接展开成多候选（CF 5 个 CDN 变体） | 单链接 + 镜像候选 | ⏭ |
| 本地资源详情（截图、百科链接、文件列表） | 只有卡片 | ⏭ 见 §4.6 |

### 2.4 实例 / 整合包 / 设置

| PCL2 能力 | Nebula 现状 | 本轮 |
|---|---|---|
| 隔离三态（never / modded / always） | ✅ 已有（默认 modded） | — |
| 实例级参数覆盖（JVM / 游戏参数 / 窗口标题 / 前后命令 / GC / 服务器） | 只有内存与 Java 路径 | ✅ 模型 + 设置面板 |
| 实例克隆 | ❌ | ⏭ 见 §4.7 |
| 实例图标（15 个内置 + 按加载器自动推荐） | `IconPath` 字段存在但无写入接口 | ⏭ 同上 |
| 实例重命名 / 删除到回收站 | 只能删除（不可恢复） | ⏭ 同上 |
| 整合包导入（8 种格式识别） | ✅ 4 种（Modrinth / CurseForge / MultiMC / 纯 zip） | — |
| 整合包导出（`.mrpack`，哈希反查在线 id，overrides 排除规则） | ❌ | ⏭ 见 §4.8 |
| 实例组件管理（安装清单 `McInstallManifest` 比对 mtime） | ❌ | ⏭ 依赖清单体系，单独立项 |
| 设置三层来源（ini / 注册表 / 实例 ini）+ 实例级缓存键 | 单一 JSON | ⏭ 现有 JSON 更适合跨平台，不换 |
| 程序化回填时禁止写盘 | `_suppressDirty` 已有等价 | ✅ |
| 25 个内置主页预设 | 无 | ⏭ 见 §4.6 |
| 帮助系统（内嵌 70 条目 + `{hint}` / `{cave}` 占位符） | 无 | ⏭ |
| 公告两段式（`notice.cfg` 编号 + `notice.json` 内容） | 无 | ⏭ |
| 启动器自更新（三段式） | 无 | ⏭ |

### 2.5 崩溃分析

| PCL2 能力 | Nebula 现状 | 本轮 |
|---|---|---|
| 规则数量 | 30 条 | ✅ → 60+ 条 |
| 三层短路（精确串 → 上下文/正则 → 兜底） | 单层遍历 | ✅ |
| `Could not reserve enough space` 二次判定（32 位 vs 内存不足） | 无 | ✅ |
| 堆栈 → 肇事 mod 归因（包名黑名单 + 回查 Mod List） | 基础版（`FindSuspectPackageNames`） | ✅ 扩充黑名单 |
| 大文件头尾截断（头 1500 + 尾 500） | 只取尾部 | ✅ |
| 文案控制符 `\e` / `\h`（自动/手动分析两种展示） | 无 | ⏭ 当前只有单入口 |
| 导出报告（改名 + UTF-8 + token/用户名等长脱敏） | 无 | ⏭ 见 §4.9 |

### 2.6 明确不抄的能力

| 能力 | 原因 |
|---|---|
| OptiFine / LiteLoader 安装 | CmlLib 无内置；PCL2 的 OptiFine 走「下载 installer → 解包 → 注入」，需要独立 3–5 天工作量；且 OptiFine 已不再支持 1.21+ 主流版本 |
| 联机（EasyTier P2P） | 依赖外部二进制与中继服务，Nebula 的定位是单机 + 服务器列表 |
| 主题贴图 / 音乐播放 / 背景视频 | 与 Nebula 现有 AuroraBackground 视觉体系冲突；PCL2 自己也把 `NewTheme` 锁死为 0 |
| 内嵌 base64 依赖 + `AssemblyResolve` | 反模式；Nebula 用 NuGet 正常依赖 |
| 设置加密用 DES + 硬件指纹派生 key | 弱算法 + 硬件指纹可被日志泄漏；Nebula 用 DPAPI（`TokenProtector`） |

---

## 3. 本轮落地的实现索引

### 3.1 启动链（T1）

| 能力 | 落点 | PCL2 依据 |
|---|---|---|
| 5 级启动里程碑 + 崩溃关键词 | `Core/Launching/GameWatcher.cs` | `ModWatcher.cs:295-380` |
| 崩溃双判据 | `Core/Launching/GameLauncher.cs` | `ModWatcher.cs:267-284` |
| 窗口接管（标题 / 最大化） | `PlatformService` + `GameLauncher` | `ModWatcher.cs:382-497` |
| 自动内存四段斜坡 | `Core/Launching/MemoryAdvisor.cs` | `PageInstanceSetup.cs:1362-1433` |
| GC / 编码参数注入 | `Core/Launching/JvmArgumentBuilder.cs` | `ModLaunch.cs:1966-2070` |
| 启动脚本导出 | `Core/Launching/LaunchScriptExporter.cs` | `ModLaunch.cs:2871-2880` |

### 3.2 内容与更新（T2）

| 能力 | 落点 | PCL2 依据 |
|---|---|---|
| SHA1 + CurseForge MurmurHash2 指纹 | `Core/Mods/ModFingerprint.cs` | `LocalResourceFile.cs:148-240` |
| 哈希缓存 `mod-hashes.json` | 同上 | `Cache\ModHash.ini` |
| 更新检查（反查项目 → 比版本） | `Core/Mods/ModUpdateService.cs` | `LocalResourceLoaders.cs:714-1096` |
| Modrinth SHA1 反查 | `IModrinthService.LookupBySha1Async` | `version_files` 端点 |
| CurseForge 指纹反查 | `ICurseForgeService.LookupByFingerprintsAsync` | `fingerprints/432` 端点（Nebula 用 `POST /v1/fingerprints` + 无符号指纹，见 `HANDOVER.md` §6.70） |
| 「检查更新」入口 | `ModsViewModel` + `ModsView.axaml` | `PageInstanceMod.cs` |

### 3.3 下载与 Java（T3）

| 能力 | 落点 | PCL2 依据 |
|---|---|---|
| 令牌桶限速 | `Core/Downloads/SpeedLimiter.cs` | `ModNet.cs:3024-3052` |
| 进度压前段曲线 | `DownloadQueue` | `ModNet.cs:372-401` |
| Java 探测结果持久化 | `Core/Java/JavaCache.cs` + `JavaService` | `Configs.JavaList` |

### 3.4 崩溃分析（T4）

| 能力 | 落点 | PCL2 依据 |
|---|---|---|
| 三层短路规则表 | `Core/Launching/CrashRules.cs` | `CrashAnalyzer.cs:702/999/1073` |
| 堆栈归因黑名单扩充 | `CrashAnalyzer.FindSuspectPackageNames` | `CrashAnalyzer.cs:1103-1280` |
| 头尾截断 | `CrashAnalyzer` | `CrashAnalyzer.cs:556` |

### 3.5 数据模型与契约（Lead）

| 变更 | 落点 |
|---|---|
| 实例级启动字段（JVM/游戏参数/前后命令/窗口标题/GC/服务器/备注/图标/managedPack） | `Models/Instances.cs` |
| 全局新设置（游戏参数/自动内存/限速/最大化/窗口标题/Java 缓存） | `Models/Settings.cs` + `SettingsService.Normalize` |
| 启动阶段枚举 `GameStartupStage`、`LaunchProgress.StartupStage` | `Models/Launching.cs` |
| 更新检查模型 `ModUpdateInfo` / `ModUpdateReport` / `ModFileHashes` | `Models/Content.cs` |
| CurseForge 指纹命中模型 | `Models/CurseForge.cs` |
| `IGameLauncher.ExportLaunchScriptAsync`、`IModUpdateService`、两个哈希反查方法 | `Abstractions/*.cs` |
| `InstanceInfo.Clone` 同步新字段（历史坑，见 HANDOVER 5.1） | `Instances/InstanceService.cs` |
| 设置页三处新 UI（自动内存 / 限速 / Java 缓存 / 游戏参数 / 窗口标题） | `SettingsViewModel` + `SettingsView.axaml` |
| 实例设置面板 + 导出脚本入口 | `VersionsViewModel` + `VersionsView.axaml` |

---

## 4. 未落地项与原因

### 4.1 进程环境（PATH / appdata / 优先级）

PCL2 把 Java 目录追加进子进程 `PATH`、把 `appdata` 指到游戏目录、按 `LaunchArgumentPriority` 调优先级。
Nebula 用 CmlLib 的 `ProcessBuilder`，`PrepareProcess` 只能改 `StartInfo` 的一部分；
改 `EnvironmentVariables` 需要 CmlLib 暴露进程创建钩子或自己重写启动段，成本大于收益（这三项只影响少数老版本模组）。

### 4.2 authlib-injector（第三方验证登录）

需要：下载并缓存 `authlib-injector.jar`、请求 API 根、base64 序列化响应塞进 `-D` 参数、失败即阻断。
属于「账号体系」而非「启动链」，且需要 UI 侧新增第三种登录方式。建议单独立项（1–2 天）。

### 4.3 分片下载与阶梯并发

Nebula 的下载器是 `SemaphoreSlim` 并发 + 每文件整流；PCL2 是每文件多分片 + 全局线程池 + 阶梯加源。
改成 PCL2 形态需要重写 `DownloadQueue` 的整个调度层，并且要和 CmlLib 的 `InstallAndBuildProcessAsync` 共存
（CmlLib 自己也管并发）。收益是在极差网络下的吞吐，风险是引入「假 206」与合并失败两类新故障。
本轮先做限速与进度曲线（这两项不改变调度结构）。

### 4.4 跨实例文件复用

PCL2 会在所有已注册的 `.minecraft` 里按 sha1/size 找同内容文件直接复制。
Nebula 的实例隔离设计里，`libraries` / `assets` / `versions` 本来就指向全局目录（见 HANDOVER 6.13），
只有 `mods` / `resourcepacks` 才在实例内，复用收益主要落在「用户从别的启动器迁移」场景。可后续做。

### 4.5 依赖自动安装

需要 Modrinth `dependencies[]` 解析 + 递归展开 + 环检测 + 用户确认框。
模型层目前 `ModrinthVersion` 没有 `Dependencies` 字段（`ParseVersions` 整段丢弃）。
这是一个完整功能（EXTENSIONS.md 条目 8 已记录），与本轮的「检查更新」共用哈希反查基础设施，建议下一轮做。

### 4.6 资源详情页 / 主页预设 / 帮助系统

三件都是纯增量 UI，不改 Core。当前优先级低于「让已有页面能正确回答更新与崩溃问题」。

### 4.7 实例克隆 / 图标 / 重命名 / 回收站

`InstanceInfo` 已有 `IconPath` 与 `IconKind`，缺的是：
`IInstanceService.CloneAsync` / `SetIconAsync` / `RenameAsync` / `MoveToTrashAsync` 四个方法 + 图标资源 + UI。
`Clone` 要复制 `mods/ config/ resourcepacks/ shaderpacks/ saves/`（可选），排除 `logs/ crash-reports/ screenshots/`。
建议下一轮与「实例组件管理」一起做。

### 4.8 整合包导出

PCL2 的导出规则可以完整复刻：哈希反查在线 id → `modrinth.index.json` → overrides 排除白名单
（`!*.log !*.dat_old !*.BakaCoreInfo !hmclversion.cfg !log4j2.xml`）。
依赖本轮的 `ModFingerprint`（已完成）与 `ModpackService.InspectAsync` 的反向实现。建议下一轮。

### 4.9 崩溃报告导出

需要：选目录、写两个 txt（启动器日志 / 游戏崩溃前输出）、UTF-8、token 与用户名等长脱敏。
`CrashAnalyzer` 已产出结构化结论，导出层是纯 IO，可随时补。

---

## 5. 验证结果

| 项 | 命令 | 结果 |
|---|---|---|
| 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c P2` | **0 警告 / 0 错误** |
| 单元测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c P2` | **601 / 601 通过**（基线 460 + 新增 141） |
| 实例设置面板 | `.\tools\shot.ps1 -Page versions -InstanceEditor -NoBuild -Configuration P2 -DataDir <演示数据> -Out <png>` | 面板完整渲染：JVM/游戏参数、窗口标题、服务器地址、启动前后命令、等待勾选、GC 下拉、保存/取消/导出脚本 |
| 设置页新项 | `shot.ps1 -Page settings -Section game -Size 1240x1500 -NoBuild ...` | 「自动分配内存」开关 + PCL2 公式说明可见，手动内存滑块在自动模式下置灰 |
| 限速设置 | `shot.ps1 -Page settings -Section download -Size 1240x1200 -NoBuild ...` | 「下载限速」滑块显示「不限速」/「N KB/s」 |
| 模组页 | `shot.ps1 -Page mods ...` | 「检查更新」按钮出现在工具条，左栏实例目标列表正常 |
| 版本页实例卡片 | `shot.ps1 -Page versions ...` | 每张实例卡片有「设置 / 打开目录 / 删除」 |
| 端到端安装 | `dotnet tools\InstallProbe\bin\P2\net8.0\NebulaLauncher.InstallProbe.dll <dir> 1.20.1 Fabric` | **成功**：3650 个文件，产出 `1.20.1` 与 `fabric-loader-0.19.5-1.20.1` |

新增测试分布：

| 测试文件 | 用例数 | 覆盖 |
|---|---|---|
| `CoreLaunch_GameWatcherTests` | 34 | 5 级里程碑推进、崩溃/关闭关键词、`[CHAT]` 排除、双判据、多线程喂入 |
| `CoreLaunch_MemoryAdvisorTests` | 13 | 四段斜坡各档位、OptiFine 档、`availableBytes=0` 兜底、1 位小数 |
| `CoreLaunch_JvmArgumentTests` | 29 | G1/ZGC/Parallel/Serial 分版本参数、旧 GC 参数清洗、编码参数让位用户自定义、936→GB18030 |
| `CoreAssets_ModFingerprintTests` | 11 | 与 PCL2 算法逐字节一致的向量、CRLF/LF 等价、缓存命中不重算、缓存损坏不抛 |
| `CoreAssets_ModUpdateTests` | 11 | Modrinth 命中 / 404 未识别 / 网络异常降级、CF `FileName` 关联、版本数字段等价 |
| `CoreDownloads_SpeedLimiterTests` | 12 | 令牌桶速率上限、不限速零开销、进度曲线端点与单调性 |
| `CoreAssets_JavaCacheTests` | 9 | 缓存命中跳过探测、mtime 变化触发重探、缓存损坏静默重建 |
| `CoreLaunch_CrashRuleTests` | 22 | 规则条数 ≥ 60、三层短路、`object heap` 二分、缺前置/重复 mod、堆栈归因门槛 |

截图留档：`.probe/lead-versions-inst.png`、`.probe/lead-instance-editor.png`、
`.probe/lead-set-game-tall.png`、`.probe/lead-set-dl-tall.png`、`.probe/lead-mods.png`。

### 5.1 未验证（明确列出，不当作已验证）

- 真实游戏进程的窗口接管与崩溃判定：只用纯函数与编译期验证。
- 启动前/后命令的实际执行：`cmd /c` 与 `/bin/sh -c` 两条路径都未跑过。
- 模组更新检查的真实网络请求：沙箱不联网，全部走假 handler。
- 更新面板的运行时渲染：截图工具无法点击「检查更新」。
- 自动内存生效后的真实 `-Xmx`：未读启动命令行确认。
- 下载限速的全程耗时：只验证了 500 ms 窗口内的字节上限。

---

## 6. 证据索引

### 6.1 反编译规格（本轮调研产物）

| 主题 | 规格文件 | 行数 |
|---|---|---|
| 启动链 / Java / 内存 / JVM 参数 / 进程监视 / 登录 | `.probe/pcl2-research/A-launch.md` | 556 |
| 下载引擎 / 安装管线 / 本地资源 / 资源站 / 任务管理 | `.probe/pcl2-research/B-download.md` | 681 |
| 整合包导入导出 / 实例 / 设置体系 / 调度 / 外观 / 公告 | `.probe/pcl2-research/C-instance.md` | 584 |
| 崩溃规则全集 / 校验框架 / 控件 / 交互 / 日志 | `.probe/pcl2-research/D-crash-ui.md` | 518 |

四份规格中所有「未确认」项都按原样保留（主要来自 PCL2 的裁剪与外部程序集：Java 扫描细节、
页面 XAML 绑定、`Modpack` / `ModAdviser` 的实现）。本文不把未确认项写成结论。

### 6.2 原始源码（后补，覆盖反编译版的「未确认」项）

反编译产物缺的三块，在原始 VB.NET 源码仓库里都能读到（用户后续拉到 `D:\agent_workspace\PCL\`）：

| 反编译版缺口 | 原始源码位置 | 关键结论 |
|---|---|---|
| Java 探测实现（`JavaUtils`） | `PCL\PCLCS\Java.cs`（252 行） | 探测命令 `java.exe -XshowSettings:properties -version`、15s 超时；32 位判 `64-bit` 缺失；候选根目录 14 条 + `JDK_HOME`/`JAVA_HOME`；排序 = 候选目录优先 + `\|Major-21\|` 最小；重解析点与 `javapath_target_`/`system32` 特殊路径筛查 |
| 配置系统（`Configs.JavaList`） | `PCL\PCLCS\Configs.cs` | Java 列表持久化的是 `{Folder, Version}`，**不是**探测结果缓存；每次使用前仍跑 `-version` 校验 |
| 安装清单 | `PCL\PCLCS\Minecraft\McInstallManifest.cs` | `SchemaVersion` / `Components` / `LastModifiedUtc` / `ManagedFiles`，靠 mtime 比对判断组件是否还在 |
| 区间求交 | `PCL\MeloongCore\Shared\General\ValueRange.cs` | Java 版本约束的 `Intersect` 语义：冲突时记 Warn 并以新约束为准 |
| 整合包实现 | `PCL\Plain Craft Launcher 2\Modules\Minecraft\ModModpack.vb`（55 KB） | 反编译版被 `throw` 阻断的分发 switch 在这里是完整的 |
| 设置页 | `PCL\Plain Craft Launcher 2\Pages\PageSetup\Settings.vb`（28 KB） | 设置项与 XAML 绑定的对应关系 |

**一处据此修正的实现判断**：PCL2 的「Java 缓存」只是列表缓存。Nebula 的 `JavaCache` 额外用
`size + LastWriteTimeUtc` 跳过子进程探测——比 PCL2 更省时，代价是「同路径同大小替换了 java.exe」
时会沿用旧结果（概率可忽略）。这不是抄错，是刻意加强，差异写在 `JavaCache` 的类注释里。

**仍未验证**：`PCL2-Rebuild` 目录是把反编译产物重新编译为 net48 工程的脚手架（`PCL2.csproj` + `fix1.py`），
不包含新逻辑，本轮未使用。

---

## 7. 第五轮（2026-10-21 晚）：把 §4 的未落地项与 PCL2 的「杂项但高频」补齐

本轮对象是 `D:\agent_workspace\PCL\`（原始 VB.NET 源码 2.99.0.0）与 `PCLCS\`（C# 子项目）。
四份补充调研覆盖启动链 / 下载链 / 实例 Java 崩溃 / 设置与系统集成，结论写进本节。

### 7.1 落地清单

| 能力 | PCL2 依据 | 落点 |
|---|---|---|
| Java 下载改走 Mojang `java-runtime` 清单（吃 piston-meta 镜像，Adoptium 兜底） | `MojangJavaDownloadTask.java`、`GameJavaVersion.java`、`JavaManager.getMojangJavaPlatform` | `Java/MojangRuntimeCatalog.cs`、`JavaService.TryInstallMojangRuntimeAsync` |
| 版本修复分快速 / 深度两档 | `DefaultDependencyManager.checkGameCompletionAsync`、`GameAssetDownloadTask` | `Installing/InstallService.Repair.cs` |
| 安装清单（组件 + 受管文件 mtime 比对） | `PCLCS\Minecraft\McInstallManifest.cs` | `Installing/InstallManifest.cs` |
| 实例克隆 / 重命名 / 图标 / 回收站 / 目录清理 | `PageInstanceSetup`、`GameInstanceIconType`、`LocalMods` 的清理入口 | `Instances/InstanceService.Admin.cs`、`InstanceService.Trash.cs`、`InstanceFileSystem.cs` |
| Modrinth 依赖解析 + 装 A 带前置 B | `ResourceVersion.vb` 的前置解析、`ModModpack` 的 dependencies | `Mods/ModrinthInstallPlanner.cs`、`ModrinthService.PlanInstallAsync / InstallPlanAsync` |
| 整合包导出 `.mrpack`（哈希反查 + overrides 排除） | `PageInstanceExport.xaml.vb`、`export_config.txt` | `Backups/ModpackExporter.cs`、`ModpackService.ExportAsync` |
| 启动后对启动器窗口的处理（保持 / 最小化 / 关闭） | `LaunchArgumentVisible` 五档 | `Models/Settings.cs`、`HomeViewModel.ApplyVisibilityAfterLaunch`、`Services/IWindowActions` |
| 游戏进程优先级 | `LaunchArgumentPriority` | `LaunchOptions.ProcessPriority`、`GameLauncher.ApplyProcessPriority` |
| 启动前把 `options.txt` 语言设为中文 | `ToolHelpChinese`、`ModLaunch.vb:1833-1879` | `GameLauncher.ApplyChineseLanguage` |
| Log4Shell 止血参数 | `ModLaunch.vb:1372-1373` | `JvmArgumentBuilder.Log4jSecurityOption` |
| 崩溃现场归档 zip | `ModCrash.vb:21-49, 878-914` | `GameLauncher.ArchiveCrashArtifacts` |
| 退出后自动备份世界 + 轮转 | HMCL 的「退出后备份」（PCL2 没有） | `BackupService.PruneAsync`、`HomeViewModel.BackupWorldsAfterExitAsync` |
| 日志凭据脱敏 | `PclLogger.FilterAccessToken / FilterUserName` | `Logging/LogRedactor.cs`、`FileLogger` |
| 单实例互斥 + 唤起已有窗口 | `PCL_SingletonMutex`、`FormMain` 窗口唤醒 | `Program.TryRedirectToRunningInstance` |
| 拖拽导入分派（jar → 模组，zip/mrpack → 整合包） | `FormMain.DragFile`、`DragHelper.vb` | `Views/MainWindow.axaml.cs`、`MainWindowViewModel.DragDrop.cs` |
| 复制诊断信息 | `ModMain.vb:975-983` | `SettingsViewModel.BuildDiagnosticsText` |
| 截图支持纵向滚动（验证长页面） | —（本项目自有工具） | `ScreenshotMode.ScrollOffset`、`App.AttachScreenshot` |

### 7.2 本轮新增的「与 PCL2 的偏差」

1. **回收站是启动器自建的 `<data>/trash/instances/`，不是系统回收站**。
   Windows 回收站要走 Shell API，macOS / Linux 各有自己的机制；自建目录跨平台一致，且能在应用内恢复。
2. **可见性从五档收敛成三档**。
   `LaunchArgumentVisible` 的「隐藏后重开 / 最小化」在当前 UI 里没有对应行为（启动器没有独立「隐藏」态），
   收敛成保持 / 最小化 / 关闭三种用户真正会用的结果。
3. **自动备份默认关闭**。
   一次全量 zip 可能几百 MB，不能替用户决定「每次退出都备份」；打开后由保留份数兜住磁盘。
4. **`options.txt` 只在当前语言是空 / `en_us` 时改写**。
   PCL2 会按版本大小写规则强制改写；这里选择「不覆盖用户已选的语言」，
   代价是 1.10 及更早版本的 `zh_CN` 写法差异没有专门处理（这些版本已不在主流使用范围）。

### 7.3 仍未抄的项（原因不变或成本过高）

| 能力 | 原因 |
|---|---|
| ~~authlib-injector / 统一通行证 / Nide 登录~~ | **第六轮已落地**（见 §8） |
| 皮肤系统（下载 / 渲染 / 上传 / 披风 / 离线皮肤包） | 需要 skinview3d 等价渲染与 Mojang API 写操作，工作量大于本轮全部条目之和 |
| OptiFine / LiteLoader 安装 | CmlLib 无内置，PCL2 走「下载 installer → 解包 → 注入」，且 OptiFine 已不支持 1.21+ |
| `JavaWrapper.jar` / `LwjglUnsafeAgent.jar` 注入 | 需要内置二进制资源；这两个 jar 解决的是特定版本组合，收益窄 |
| 分片下载 + 阶梯并发 | 需要重写调度层，风险（假 206、合并失败）高于收益 |
| ~~跨实例文件复用（同 sha1 直接复制）~~ | **第七轮已落地**（见 §9），但只覆盖 mods / resourcepacks / shaderpacks / saves |
| ~~帮助系统~~ | **第七轮已落地**（见 §9），形态改为内置纯文本 + 分类 + 加权搜索，不照抄 XAML 动态解析 |
| 公告 / 启动器自更新 / 主页预设 / 音乐 | 与本项目定位（单机启动器，无自有服务端）不符；音乐与现有视觉体系冲突 |
| 中文搜索词表（MC 百科） | 需要维护一份 2000+ 条词表，属于数据资产而非代码 |

### 7.4 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead` | 0 error |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead` | **683 / 683 通过**（基线 601 + 新增 82） |
| 设置页新增项 | `shot.ps1 -Page settings -Section game -Scroll 1000 -Size 1240x1000` | 卡片 / 开关 / 禁用态全部正确 |
| 版本页实例管理 | `shot.ps1 -Page versions -Size 1240x800` | 图标徽标、两行操作、回收站面板、修复下拉 |
| 世界页导出 | `shot.ps1 -Page worlds -Size 1240x900` | 导出卡片六项输入 + 五个选项 + 按钮禁用态 |
| 资源页 | `shot.ps1 -Page library -Size 1240x900` | 列表与目标提示正常，无绑定异常 |

新增测试分布：`CoreLaunch_StartupEnhancementsTests`（12）、`CoreAssets_MojangRuntimeTests`（19）、
`CoreLaunch_InstanceAdminTests`（16）、`CoreDownloads_RepairTests`（6）、`CoreLaunch_InstallManifestTests`（5）、
`CoreAssets_ModrinthPlanTests`（14）、`CoreAssets_ModpackExportTests`（10）。

未验证：真实网络的 Modrinth 依赖展开与 Mojang runtime 下载（沙箱无外网）、
真实游戏进程的窗口可见性 / 优先级 / 语言改写、跨卷移动的回收站退化分支、
拖拽导入的真实点击链路。

---

## 8. 第六轮（2026-10-21 深夜）：账号体系与 JVM 参数

### 8.1 §7.3 的第一项已落地：第三方验证（authlib-injector）

| 能力 | PCL2 依据 | 落点 |
|---|---|---|
| yggdrasil 登录 / 续期 / 校验（authenticate · refresh · validate） | `ModLaunch.vb:611-684, 706-879` | `Accounts/AuthlibAuthService.cs` |
| API 根规范化 + 中文错误映射（401 / 403 / 429 / 网络） | 同上 | 同上 `NormalizeApiRoot` / `TranslateHttpError` |
| 账户模型与落盘 | `CacheAuth*` / `LoginAuth*` | `Models/Accounts.cs` `AccountType.Authlib` + `AuthServerUrl`；`AccountService.cs:258/362/492` |
| authlib-injector jar 缓存（SHA-256 校验、断网退化本地缓存） | `ExtractPatch` 同款「本地优先」思路 | `Launching/AuthlibInjectorService.cs:74/361` |
| 三参数注入（`-javaagent` + `prefetched` + `side`） | `ModLaunch.vb:1101-1138` | 同上 `:201/:210`；`GameLauncher.cs:620` |
| 启动与导出脚本同时带上注入 | 同上 | `GameLauncher.cs:120-127/236-245` |
| 账户页第三方登录入口与表单 | `PageLoginAuth.xaml.vb` | `AccountsViewModel.cs:445-575`、`AccountsView.axaml:334-465` |

### 8.2 JVM 参数预设（EXTENSIONS 条目 15）

`JvmPreset` + `AppSettings.JvmPresets` / `ActiveJvmPresetId`、`JvmPresetResolver`（纯函数）、
设置页预设区、三个启动入口统一走「实例显式参数 &gt; 选中预设 &gt; 全局原文」。

### 8.3 顺带修掉的既有缺陷：CmlLib 参数切分会吞 Windows 路径

现象：用户手填 `-Djava.library.path="C:\Program Files\Java\bin"` 时，反斜杠被当转义符吃掉，
变成 `C:Program FilesJavabin`，游戏报「找不到库」而用户看不出是启动器改坏的。

根因：`GameLauncher.SplitArguments` 调 `Parser.ParseCommandLineToArguments`（CmlLib 的命令行解析器），
它按 shell 规则解释 `\`。受影响的不只是用户参数，authlib 的 `-javaagent:<jar>=<apiRoot>` 在路径含空格时同样中招。

处理：抽出 `Launching/LaunchArgumentSplitter.cs`，只认双引号、不解释反斜杠；
`GameLauncher.SplitArguments` 改为委托它，`CoreLaunch_ArgumentSplitterTests` 钉住
「反斜杠保留 / 引号内空白保留 / 空引号 / 制表符换行 / 空白输入」五类判据。

### 8.4 整合包导入补齐实例元数据

导入的实例以前只写 `Id/Name/VersionName/CreatedAt`，缺 `Loader`、`MinecraftVersion`、
`ManagedPack`——后果是导入的整合包在模组页里退化成一堆「原版」条目，且无法回答「这份实例从哪个整合包来」。
现在导入时一并写入（`ModpackService.cs:117-140`），`CoreAssets_ModpackImportTests` 覆盖
Modrinth 与普通 zip 两种来源。

### 8.5 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead` | 0 error |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead` | **765 / 765 通过** |
| 账户页 | `shot.ps1 -Page accounts -Size 1240x900` | 第三方入口卡、Authlib 徽标与 host 行 |
| 设置页 JVM 预设 | `shot.ps1 -Page settings -Section game -Scroll 1450 -Size 1240x1000` | 下拉 / 名称 / 参数 / 只读提示正确 |
| authlib 真实网络冒烟 | `.probe/authlib-smoke`（一次性程序） | 下载 1.2.8 jar 349,681 字节，SHA-256 与官方一致；littleskin prefetch 1780 字符 base64；二次命中缓存 |

新增测试：`CoreAssets_AuthlibTests`（27）、`CoreLaunch_AuthlibInjectorTests`（32）、
`CoreLaunch_JvmPresetTests`（13）、`CoreAssets_ModpackImportTests`（2）、`CoreLaunch_ArgumentSplitterTests`（8）。

未验证：真实第三方账号的「登录 → 启动 → 角色可用」全链路（需真实账号与已装版本）；
macOS / Linux 上的注入；authlib-injector 下载源无镜像加速（`authlib-injector.yushi.moe` 不在 BMCLAPI 映射表）。

---

## 9. 第七轮（2026-10-21 收尾）：帮助、更新提示、文件复用、资源详情

### 9.1 落地清单

| 能力 | PCL2 依据 | 落点 |
|---|---|---|
| 内置离线帮助（18 条、5 个分类、加权搜索） | PCL2 帮助系统（`ModMain.vb:426-611`）的信息组织方式 | `Core/Help/HelpCatalog.cs`、`HelpViewModel.cs`、`HelpView.axaml` |
| 官方正式版更新提示（同一版本只提示一次） | `ToolUpdateRelease` / `ToolUpdateReleaseLast` | `AppSettings.LastNotifiedGameVersion`、`HomeViewModel.DetectGameUpdatesAsync` |
| 跨实例 / 跨根目录文件复用（同 SHA1 直接复制） | `ModNet.vb:1474-1564` | `Downloads/FileReuseService.cs`、`DownloadQueue.RunOneAsync` 前置复用 |
| 资源项目详情（正文 / 图库 / 源码与问题链接 / 许可） | `PageDownloadCompDetail.xaml.vb` | `IModrinthService.GetProjectAsync`、`ModrinthProject.Body/GalleryUrls/...`、`LibraryViewModel` + `LibraryView` |

### 9.2 实现上的取舍

1. **帮助内容放 Core，不放 UI**。`HelpCatalog` 是纯数据 + 纯函数过滤，能单测；
   UI 只做展示。PCL2 的帮助是「解压 XAML 再用 XamlReader 解析」，跨平台重写没有等价物，
   这里换成内置纯文本 + 分类 + 搜索，信息结构一致而实现完全不同。
2. **帮助条目只写本启动器真实存在的行为**。没有全局快捷键就明写「当前版本没有」，
   不照抄别家说明——按帮助操作却找不到入口比没有帮助更糟。
3. **更新提示只覆盖正式版，且只提示一次**。快照几乎每次都有更新，提示会变成常驻噪音；
   记录已提示版本号写进 `settings.json`，避免每次打开首页弹同一条。
4. **文件复用只覆盖 mods / resourcepacks / shaderpacks / saves**。
   libraries 与 assets 的目录结构由 CmlLib 管，凭 SHA1 往 hash 目录里塞文件会破坏它的完整性判定。
5. **资源详情不做 Markdown 渲染**。正文按纯文本 + `TextWrapping` 展示，链接与图片语法剥掉；
   引入 Markdown 渲染器等于再背一个依赖，而详情页的正文价值主要在文字本身。

### 9.3 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead` | 0 error |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead` | **800 / 800 通过** |
| 帮助页 | `shot.ps1 -Page help -Size 1240x900` 与 `-Size 1024x660` | 分类、列表、正文、窄屏都不裁切 |
| 首页更新提示 | `shot.ps1 -Page home -Size 1240x900` | 提示条与错误条布局互不影响 |
| 资源页 | `shot.ps1 -Page library -Size 1240x900` | 未选项目时的空态提示正常 |

新增测试：`CoreHelp_FilterTests`（8）、`CoreDownloads_FileReuseTests`（8）、`CoreAssets_ModrinthDetailTests`（详情解析与 Markdown 剥离）。

未验证：真实多实例目录下的复用收益、>`5000` 文件时的 jar-only 索引分支、
资源详情真实网络的正文与图库渲染（沙箱只能验证解析与布局）。

---

## 10. 第八轮（2026-10-22）：可观测性、外观与两处死开关

本轮对象仍是 `D:\agent_workspace\PCL\`（VB.NET 源码 2.99.0.0），并新增一份 29 条的缺口调研
（PCL2 有 / Nebula 无，按价值与成本分档）。与往轮不同的是：本轮有两项**不是**从 PCL2 移植，
而是修 Nebula 自身的「开关存在但行为不存在」。

### 10.1 落地清单

| 能力 | 来源 | 落点 |
|---|---|---|
| 关闭到托盘 + 托盘图标（显示 / 打开游戏目录 / 退出） | **Nebula 自有缺陷**：`CloseToTray` 是死开关 | `Core/Platforms/CloseBehavior.cs`、`App/Services/TrayService.cs`、`Views/MainWindow.axaml.cs`、`Assets/tray.png` |
| 日志中心页面（启动器日志 + `latest.log` + `crash-reports`，级别过滤 / 搜索 / 自动跟随） | PCL2 `FormMain` 内嵌日志区 + `ModBase.Log`（`PCL\Plain Craft Launcher 2\FormMain.xaml.vb`） | `Core/Abstractions/LogServices.cs`、`Core/Logging/LauncherLogReader.cs`、`App/ViewModels/LogsViewModel.cs`、`Views/LogsView.axaml` |
| 下载任务明面（逐文件状态 / 失败详情 / 只重试失败项 / 批次历史） | PCL2 任务管理器（`Pages\PageSpeedLeft.xaml` 的进度、速度、剩余文件、线程数） | `Models/Downloads.cs`、`Downloads/DownloadQueue.cs`（`Snapshot` / `TasksChanged` / `History`）、`DownloadViewModel` + `DownloadView` |
| 浅色主题 + 深/浅/跟随系统三态 | **Nebula 自有技术债**（`HANDOVER` §8 第一条）；PCL2 没有主题切换 | `Themes/Tokens.axaml`（`ThemeDictionaries`）、`App/Services/ThemeService.cs`、`AppSettings.Theme` |
| 抬头区副标题在 1024 宽下消失 | 本轮发现（logs 页面截图时暴露的既有缺陷） | `Views/MainWindow.axaml` 抬头 Grid 改 `Auto,*` |

### 10.2 与 PCL2 的三处关键偏差

1. **托盘不是 PCL2 能力**。全树 grep `TrayIcon` / `NotifyIcon` 命中 0；自启、代理、文件关联、全局热键同样没有。
   本轮实现的托盘只解决 Nebula 自己的问题：设置页与帮助文案都承诺了「关闭到托盘」，而代码里没有任何实现。
   判据抽成 Core 纯函数（`ShouldExitOnClose`）的理由是两种失败都很难查：误判成隐藏会留下只能去任务管理器结束的幽灵进程，
   误判成退出会让用户以为设置无效。托盘建失败时一律退回退出，不假装隐藏。
2. **日志页不是 PCL2 日志区的复刻**。PCL2 把日志嵌在主窗体内、只保留内存中的环形缓冲；
   Nebula 的日志落盘在 `<data>/logs/`，所以这里做成独立页 + 磁盘尾部倒读（64 KB 块、默认 2000 行），
   额外覆盖 PCL2 没有的两类入口：游戏侧 `logs/latest.log` 与 `crash-reports/*.txt`。
   格式解析按两个项目各自的真实格式写（启动器 `yyyy-MM-dd HH:mm:ss.fff [LEVEL]`，游戏 `[HH:mm:ss] [thread/LEVEL]`），
   解析不出来整行透传，不猜。
3. **浅色主题没有 PCL2 对照物**。PCL2 的外观设置是自定义背景与音乐，配色仍是固定的一套；
   本轮做的是语义令牌双字典（42 个键两套完全同名），页面 XAML 一行没改——这只有在
   「页面不写颜色字面量」这条既有约定成立时才可能。代价有三处自绘控件必须单独处理：
   星云背景（`AuroraBackground`）、环形进度轨道（`RingProgress`）、服务器 MOTD 的协议色（`ServersView`）。

### 10.3 未落地项与原因

| 项 | 原因 |
|---|---|
| 安装游戏本体时的逐文件任务面板 | 安装链走 CmlLib 的 `NebulaGameInstaller`，不经过 `DownloadQueue`；要接上必须给安装器加逐文件回调（下一批 P0） |
| 启动器窗口尺寸记忆 | `MainWindow.axaml` 硬编码 1240×800；与 `AppSettings` 里已有的游戏窗口尺寸字段同名易混，需要先改名或加前缀 |
| 快捷键体系（Esc / F5 / F11 / F12） | PCL2 `FormMain.xaml.vb:801-865`；本轮 `MainWindow` 同时被托盘与主题修改，避免三处并发改同一文件 |
| `hs_err_pid*.log` 纳入崩溃分析 | 目前 `hs_err` 只出现在 `CrashRules` 的规则文案里，没有读取逻辑；改的是 `CrashAnalyzer` 的输入契约，需单独一轮 |
| 版本更新日志映射（版本 id → 中文百科条目） | PCL2 `ModDownloadLib.vb:263-318`；属数据资产（20+ 特例），不是代码 |
| 双源合并搜索 / 数据包（datapack）/ 服务端下载 | 分别需要改 `ContentKind` 枚举、`ModrinthService` 合并层、新增 `server.jar` 下载路径，均超出本轮范围 |
| 内存优化 / 百宝箱 | **实证不是 PCL2 能力**：`ModLaunch.vb:134-145` 有调用点，实现在开源版是桩（`PageOtherTest.xaml.vb:13-16` 全是「不包含百宝箱功能」） |
| 界面多语言 | PCL2 也没有：语言下拉在 `PageSetupSystem.xaml:168-183` 整段注释掉，`ModBase.vb:52` 硬编码 `zh_CN`。Nebula 的 `AppSettings.Language` 同样是死字段，待单独决策（实现或删掉） |

### 10.4 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead` | 0 错误 / 18 警告（全部为既有 CA1001 / CA1822 / CA1859 / CS0618） |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead` | **859 / 859 通过**（基线 800 + 59） |
| 托盘关闭行为 | `dotnet NebulaLauncher.dll --data-dir <隔离目录>` + `PostMessage(WM_CLOSE)` | `closeToTray=false` → 进程退出；`true` → 进程存活且窗口隐藏；`--screenshot` → 不建托盘、自行退出 |
| 主题 | `shot.ps1 -Theme light` 四张（home / settings × 深/浅） | 浅色正文对比度 Primary 17.97 / Secondary 7.75 / Tertiary 5.31 / Accent 5.64，均 ≥ 4.5 |
| 日志页 | `shot.ps1 -Page logs -Size 1240x800` 与 `1024x660`、`-Theme light` | 左列分组 4 个日志、工具条三行不重叠、正文等宽可读 |
| 下载任务面板 | `shot.ps1 -Page download` + 窄屏滚动 | 空态、5 行状态预览、失败详情展开、批次历史行均正常 |
| 抬头区布局 | `shot.ps1 -Page versions -Size 1024x660` | 修复前副标题宽 0（不可见），修复后显示「4 个已安装版本」 |
| 发布产物 | `build.ps1 -Publish -Mode minimal -OutDir <新目录>` | 22.7 MB / 3 文件；产物 `--screenshot --page logs` 正常出图（说明 `Assets/tray.png` 与新增页面在裁剪发布下都在） |

新增测试分布：`CorePlatforms_CloseBehaviorTests`（9）、`CoreSettings_ThemeTests`（11）、
`CoreLogs_LauncherLogReaderTests`（26）、`CoreDownloads_TaskSnapshotTests`（13）。

未验证：真实鼠标点击托盘菜单与「退出」项；托盘图标在通知区的视觉（无头环境看不到壳层）；
设置页点「浅色」后的即时生效（截图模式不能点击，只有编译期绑定校验 + 代码路径审查）；
真实 3000+ 文件的逐文件面板（沙箱无稳定下载源，用假 handler 与纯函数覆盖）；
日志页 2000 行 × 百 MB 级文件的渲染耗时。

---

## 11. 第九轮（2026-10-22）：把「看得到」与「够得着」补齐

本轮做的是上一轮列出的 P0 五条，外加一条从 PCL2 抄来的判定知识（`hs_err`）。
一个额外收获：本轮首次在真实网络下跑了端到端安装回归（3650 个文件），这是前八轮都没做过的事。

### 11.1 落地清单

| 能力 | 来源 | 落点 |
|---|---|---|
| 安装链逐文件进度（3650 个文件可见、失败项可单独重试） | 上一轮的自定边界 + PCL2 任务管理器形态 | `DownloadQueue.BeginExternalBatch` / `IDownloadTaskScope`、`NebulaGameInstaller`、`InstallService` |
| `hs_err_pid*.log` 纳入崩溃归因（信号 / `Problematic frame` / native 模块 + 6 条规则） | PCL2 从 hs_err 判断 native 崩溃的思路（`ModCrash.vb:21-84`） | `CrashAnalyzer.ParseHsErr` / `Analyze(log, crashReport, hsErr)`、`CrashRules` 追加 6 条（69 → 75）、`GameLauncher` 采集与归档 |
| 外部崩溃报告导入（txt / log / zip） | PCL2 `ModCrash.vb:87-123` 的手动分析入口 | `CrashAnalyzer.ReadExternalReport` / `AnalyzeExternalReport`、`HomeViewModel.ImportExternalReportAsync`、`HomeView` 结果面板 |
| 启动器窗口尺寸 / 最大化记忆 | PCL2 `FormMain.xaml.vb:380-381,655-656` | `AppSettings.LauncherWindow*`、`Core/Platforms/WindowMemory`、`MainWindow.RestoreWindowState/PersistWindowState` |
| 窗口内快捷键（Esc / F5 / F11 / Ctrl+O / Ctrl+L） | PCL2 `FormMain.xaml.vb:801-865` | `MainWindow.RegisterShortcuts`、`MainWindowViewModel.Escape`（按页面分派） |
| 实例备注接线（死字段 → 卡片可见 + 浮层） | PCL2 `PageInstanceOverall.xaml.vb:91-100` | `VersionsViewModel.EditorNotes`、`VersionsView` 备注行与浮层 |
| 模组多选批量启停 / 删除 | PCL2 `PageInstanceMod` 的批量操作 | `Core/Mods/ContentBatch.cs`、`ModsViewModel` 选中集合、`ModsView` 批量条 |

### 11.2 与 PCL2 的关键偏差

1. **hs_err 只抄判定，不抄文案**。PCL2 的 hs_err 处理是「搜关键词 → 推荐一条百科条目」；
   这里做的是解析出结构化事实（信号类型、`Problematic frame`、native 模块名），再交给规则表。
   好处是同一份 hs_err 能被单测钉住（`ParseHsErr` 是纯函数），规则文案可以独立演进。
   6 条新规则的判据全部来自「用户在真实机器上能做什么」（更新驱动 / 关覆盖层 / 查输入法 / 换 Java 位数），
   不写「可能是某某模组」这种无法验证的结论。
2. **手动导入的报告与本机崩溃状态分开**。PCL2 的导入会覆盖当前崩溃视图；
   这里 `HomeViewModel` 用两个独立状态（`Suggestions` 与本机崩溃集合 vs `ImportedSuggestions`），
   导入结果带「来源：文件名」前缀，且不会污染「上次启动崩溃」的判断。
3. **快捷键不做全局热键**。PCL2 有一部分窗口级快捷键（F11 / F12），也有别的全局行为；
   这里只用 Avalonia 的 `KeyBindings`，窗口失焦即失效——全局热键需要平台钩子，且会与其他软件抢键。
4. **窗口尺寸记忆带「下限 1024×660 / 上限 8000」**。PCL2 直接把 ini 里的值拿来用；
   这里做了夹取，原因很实际：显示器拔掉后，一个 4K 窗口在 1080p 屏上开出来就是「窗口跑到屏幕外」。
5. **批量操作的成功判据取磁盘**。PCL2 的批量启停拿返回状态当结果；这里的 `ContentService.SetEnabledAsync`
   会吞 `IOException`，所以 `ContentBatch` 逐项复核磁盘（`IsEnabledOnDisk` 三态），
   失败项留在列表里并汇总——这条是仓库既有缺陷逼出来的，不是抄来的。

### 11.3 未落地项

| 项 | 原因 |
|---|---|
| Forge / NeoForge 自带 maven 下载的逐文件进度 | 走 `ForgeInstaller` 自己的下载器，不在 `DownloadQueue` 里；要接需要包一层或改 CmlLib |
| 安装批次与普通下载的并发 | 当前互斥（安装中普通下载抛 `InvalidOperationException`）。要并发必须把队列模型从「单活动批次 + 快照」改成「批次注册表」 |
| 三档下载源 + 慢速自动降级 | PCL2 `ModDownload.vb:1213-1245`；涉及 `Settings` 与下载调度两处，本轮无空闲写域 |
| 版本收藏 / 隐藏 / 分类卡片 | PCL2 `PageDownloadInstall.xaml.vb:453-535`；纯 UI 增量，下轮做 |
| 数据包（datapack） | `ContentKind` 只有 4 个值，需要同步扫描、启停、在线安装三处 |
| 任务栏进度条 | `IPlatformService.SetTaskbarProgress` 已实现但 0 调用；接线点在 `HomeViewModel` 与 `DownloadViewModel` |
| 服务端下载（`server.jar`） | 需要新的下载目标与版本选择 UI |
| 崩溃报告 zip 导出（分享给他人） | 已有归档 zip（`crash-reports/archive`），缺「选目录导出 + 脱敏文本」这一层 |
| 界面多语言 / `AppSettings.Language` | PCL2 也没有；先决定「实现 or 删字段」 |

### 11.4 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead` | 0 错误 / 18 警告（全部既有） |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead` | **920 / 920 通过**（854 → 920，新增 61） |
| 端到端安装（真实网络） | `InstallProbe <dir> 1.20.1 Fabric` | **成功**：3650 个文件、660 MB，产出 `1.20.1` 与 `fabric-loader-0.19.5-1.20.1`；用含外部批次改造的最新代码重跑同样成功 |
| 窗口尺寸记忆 | 改 `settings.json` → 启动 → 量 PNG 尺寸 | 1400×900 → 1400×900；`--size` 覆盖设置；`launcherWindowMaximized=true` → `状态=Maximized`；`SetWindowPos` 改到 1320×840 后关闭 → settings.json 写入该值；`rememberWindowSize=false` → 文件 mtime 与内容均未变 |
| hs_err 解析 | 样本 `.probe\crash-samples\hs_err_pid12345.log` | `EXCEPTION_ACCESS_VIOLATION / nvoglv64.dll` → 「疑似显卡驱动崩溃」，摘要带来源文件名 |
| 模组批量 | 700 项真实改名 | 240–266 ms（后台线程）；`FileShare.None` 占用时 1 失败 2 成功且失败项保持 `.jar` |
| 截图 | `crash-home-import.png` / `lead-versions-notes.png` / `lead-mods-batch.png`（含 1024×660 与浅色变体） | 布局完整，无绑定异常 |

新增测试分布：`CoreInstalling_ExternalTaskScopeTests`（11）、`CoreLaunch_HsErrTests`（20）、
`CoreSettings_WindowMemoryTests`（19）、`CoreInstances_NotesTests`（5）、`AppMods_BatchTests`（8，含 700 项性能断言）。

快捷键的手工验证步骤（截图模式按不了键）：

1. `dotnet src\NebulaLauncher.App\bin\Lead\net8.0\NebulaLauncher.dll --data-dir <空目录>`
2. 版本页点「删除」出确认框 → Esc 关框；无浮层时 Esc 无反应。
3. 在任意输入框内按 Esc → 只影响输入框，不关背后的浮层；F5 仍刷新。
4. F5 在日志页 → 状态栏「日志 已刷新」；F11 最大化切换；Ctrl+O 打开游戏目录；Ctrl+L 跳日志页。
5. 改窗口大小 → 关闭 → 重开 → 尺寸还原。

未验证：`hs_err` 的真实采集与归档（需要真实 JVM native 崩溃，private 方法只有编译 + 审查）；
快捷键与批量按钮的点击级验证；700 张模组卡片的渲染（只测了文件操作耗时）；
Linux 下 `SIGSEGV` 解析（只有单测，样本来自 Windows）。

---

## 12. 第十轮（2026-10-22）：列表组织、下载策略与最后两个死接口

本轮清了 §11.3 里除「安装并发」与「双源合并搜索」之外的全部 P0。

### 12.1 落地清单

| 能力 | 来源 | 落点 |
|---|---|---|
| 版本收藏 / 置顶 / 隐藏 | PCL2 `PageDownloadInstall.xaml.vb:453-535` 的卡片分类思路 | `Core/Versions/VersionFavorites.cs`、`Abstractions/FavoriteServices.cs`、`VersionsViewModel` + `VersionsView` |
| 三档下载源 + 会话级自动降级 | PCL2 `ModDownload.vb:1213-1245`（三档偏好 + 黏性 `DlPreferMojang`） | `DownloadSourceGovernor`、`IDownloadSourceProvider`、`DownloadMirror.Candidates(url, source, preferred)`、设置页三档、下载页状态行 |
| 任务栏进度条 | PCL2 `ModLoader.vb:674-712` | `App/Services/ShellProgress.cs`、`HomeViewModel` / `DownloadViewModel` 接线 |
| 数据包（datapack） | **PCL2 没有**；Nebula 自有缺口 | `ContentKind.DataPack`、`ContentService.ScanDataPacks`、模组页第 4 个分类 |
| 帮助内容补齐 | — | `HelpCatalog` 增 5 条（theme / tray / task-panel / crash-import / versions-favorites / datapack），`crash` 更新到 75 条规则 |

顺带修的两处：

1. `ContentTarget.DirectoryNameOf(ContentKind)` 没有 `DataPack` 分支，会落进 `_ => "mods"`——任何走 `ContentDirectory()` 的新代码都会指向 mods 目录。
2. `VersionService` 拉版本清单时写死 `_source == DownloadSource.BmclApi`，改成 `DownloadMirror.PrefersMirror(_source)`；否则 `Auto` 模式下列清单会退化成官方优先，与自动语义相反。

### 12.2 与 PCL2 的三处偏差

1. **收藏不是「设置里的一个列表」**。PCL2 把收藏与隐藏混在 ini 设置里；这里单独落 `<data>/favorites.json`，
   理由与 `accounts.json` / `servers.json` 一致：这是用户数据，不是程序配置，备份与清空设置的语义不同。
   另外保留「版本已删除但收藏条目还在」——重新装回同名版本时星标自动回来，这条有测试钉着。
2. **自动降级的判据是「本会话」，不是「写进配置」**。PCL2 用 `DlPreferMojang` 做黏性记忆；这里只在内存里记，
   10 分钟窗口过后自动回到镜像优先。理由：镜像可用性按天变化，把「今天降级了」变成永久设置只会让用户下次困惑。
   判据本身是可注入时钟的状态机，三条触发条件（连续 3 次镜像失败 / 2 次慢 2 秒以上 / 手动档免疫）都有单测。
3. **数据包支持两种形态**。PCL2 只在 `.zip` 上做启用/禁用；Minecraft 实际把 `datapacks/` 下每个子目录也当数据包，
   所以这里文件与目录都要能改名启用/禁用——也正因为如此，不能复用 `ContentBatch.IsEnabledOnDisk`（它写死 `File.Exists`）。

### 12.3 决策记录

| 决策 | 结论 | 理由 |
|---|---|---|
| 下载源默认值 | **保持镜像优先（`Mirror`）**，不改成 `Auto` | 国内环境下 BMCLAPI 命中率与速度都更好；改默认会让老用户看到行为变化，而收益只是「少数镜像变慢的机器自动切源」。自动档作为可选项已足够 |
| 测试项目是否引用 App | **允许**（`tests/...Tests.csproj` 增加 `ProjectReference` 到 App，App 开放 `InternalsVisibleTo`） | `ShellProgress` 的节流与窗口生命周期强相关，下沉 Core 会把它变成「不碰窗口的服务」，语义更差。代价是 TestRunner 会连带构建 App；已在 `CONVENTIONS.md` 写明限制 |
| 隐藏版本是否删文件 | **不删**，只从列表消失 | 隐藏是「视图偏好」，删除是不可逆操作；两者混在一起会让用户不敢点 |

### 12.4 未落地项

| 项 | 原因 |
|---|---|
| 安装批次与普通下载并发 | 队列模型是「单活动批次 + 快照」，要并发得改成批次注册表（比本轮所有改动都大） |
| Forge / NeoForge maven 下载的逐文件进度 | 走 `ForgeInstaller` 自己的下载器 |
| 数据包在线安装（Modrinth `datapack`） | `ContentKind` 已就位，缺 `ModrinthService` 的 project_type 过滤与安装目标 |
| 数据包 `pack_format` 兼容校验 | 需要版本表（1.20 format 15 / 1.21 format 48 等），是数据资产 |
| `AppSettings.Language` 死字段 | 仍未决策：实现 i18n 或删字段 |
| 崩溃报告导出（选目录 + 脱敏文本） | 已有归档 zip，缺分享用导出层 |
| 服务端下载（`server.jar`） | 需要新的下载目标与 UI |
| 双源合并搜索 / 版本原地换加载器 | 改动面大，优先级低于上面几项 |

### 12.5 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead` | 0 错误 / 18 警告（全部既有） |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead` | **994 / 994 通过**（920 → 994，新增 74） |
| 收藏 / 隐藏 | 数据驱动 `favorites.json` + `-ShowHidden` 截图 | 收藏两行置顶、星标实心；隐藏行半透明 + 「已隐藏」徽标；全部隐藏时有空态与出口；1240 / 1024 各一次 |
| 下载源三档 | `settings.json` 写 `"downloadSource":"Auto"` 后截图 | 设置页三档（自动选中）+ 下载页顶部「下载源：自动 · 当前 BMCLAPI」；手动档不显示降级信息 |
| 数据包 | 4 份真实文件（启用 zip / 禁用 zip / 目录形态 / 存档级） | 「数据包」分类 4 张卡片，存档级带世界徽标，禁用态开关关闭；1024×660 不重叠 |
| 任务栏进度 | 真实启动一次启动器 | 日志 `任务栏进度已挂接：handle=0x2209EE, supported=True`（`Window.Opened` 后拿到真实 HWND） |

新增测试分布：`CoreVersions_FavoritesTests`（11）、`CoreDownloads_SourceGovernorTests`（30）、
`AppShell_ShellProgressTests`（19）、`CoreAssets_DataPackTests`（14）。

未验证：真实触发一次自动降级（沙箱无法稳定构造镜像连续失败）；「已降级到官方（原因）」状态行无截图；
任务栏进度条的视觉（无头环境看不到任务栏）；`MirrorRewritingHandler` 的遥测回灌无单测（内层 `HttpClientHandler` 不可注入）；
收藏星标 / 筛选 chip / 数据包开关的点击链路；跨进程并发写 `favorites.json`。

---

## 13. 第十一轮（2026-10-22）：把「装得上」推进到「装得明白、发得出去」

### 13.1 落地清单

| 能力 | 来源 | 落点 |
|---|---|---|
| 数据包在线安装（Modrinth `datapack`） | 上一轮留下的边界；PCL2 内容站也支持数据包分类 | `ModrinthService.ResolveDataPackTargets` / `ValidateDataPackTarget`、`LibraryViewModel` 安装位置选择、模组页空态入口 |
| 崩溃报告导出（脱敏 zip） | PCL2 `ModCrash.vb` 的报告归档 | `Launching/CrashExportService.cs`、`Diagnostics/DiagnosticsSnapshot.cs`、`LogRedactor` 三条新规则、首页导出按钮 |
| 服务端（`server.jar`）下载 | PCL2 有服务端下载入口 | `Versions/ServerDownloadService.cs`、下载页「客户端 / 服务端」分段 |
| Forge / NeoForge 分阶段进度 | 上一轮的边界 | `InstallStagePlan`（0–50 / 50–85 / 85–100）、`InstallProgressAdapter` 阶段适配器、下载页阶段行 |
| 删掉 `AppSettings.Language` 死字段 | — | `Models/Settings.cs`、`SettingsService.Normalize`、`SettingsViewModel.ApplyTo` |

### 13.2 一条重要修正：Forge 的进度回调一直存在

上一轮我记录的是「Forge / NeoForge 自带的 maven 下载不上报进度」。**这个判断是错的**，本轮用 IL 读证推翻了它：

- `CmlLib.Core.Installer.Forge 2.0.0` 的 `ForgeInstallOptions` 就有 `FileProgress` / `ByteProgress` / `InstallerOutput` 三路回调
  （`ForgeInstallerExtractor/<DownloadAndExtract>d__4` IL_00C6、`ForgeV12Installer/<installLibraries>d__8` IL_0064、
  `ForgeV12Installer/<Install>d__6` IL_0307 三个消费点）。
- 这些文件走的就是我们自己的安装器（`InstallerRunner/<Install>d__2` IL_024C → `get_GameInstaller` → `NebulaGameInstaller`），
  所以 `InstallService.cs:424-425` 早就把它们接进了聚合进度。
- 真正的缺陷是**阶段不可辨 + 比例跨阶段回退**：改造前真装实测 `97.5% → 3.0%`（往回跳 94 个点），
  然后处理器阶段停在 `100%` 干跑 35 秒。

教训：上一轮那条「边界」是从「面板里没看到 maven 文件名」推出来的，没有再往下查一层。
凡是要写进文档的「做不到」，都应该像本轮这样给出 IL / 源码级的否定证据。

### 13.3 偏差与取舍

1. **数据包在线安装必须选目标**。全局 `datapacks/` 与存档级 `saves/<world>/datapacks/` 的语义完全不同（一个影响所有世界，
   一个只影响那个存档）。所以安装按钮改成「下载并选择安装位置…」，确认前再复核存档是否还在——存档可能在选择框打开期间被删掉。
2. **崩溃导出是「给别人看」的文件**。所以 `crash-analysis.txt` 顶部写死启动器版本、导出时间与脱敏说明；
   `system.txt` 与「设置 → 关于 → 复制诊断信息」共用同一个 `DiagnosticsSnapshot.Create`，避免两处字段漂移。
3. **脱敏规则扩到路径用户名**。`LogRedactor` 新增 `C:\Users\<name>\` → `C:\Users\<user>\`（同时覆盖 `/home/` 与 `/Users/`），
   保留路径其余部分——否则一份崩溃报告里所有路径都变成 `<path>`，没人能判断是哪个目录出的问题。
   副作用：启动器自己的日志里也看不到真实用户名了（这是有意的，见 §13.4）。
4. **服务端只做原版 `server.jar`**。Paper / Forge 服务端不在此列；`eula.txt` 也不代生成——那是必须用户自己确认的文本。
5. **阶段进度的区间不是平均分**。原版 0–50%、加载器 50–85%、处理器 85–100%，并且加载器阶段的多个子批次用「剩余区间减半」分配，
   因为内层原版复检只需 1 秒、maven 要 11 秒、处理器要 35 秒——平均分会让人误以为卡住。

### 13.4 决策记录

| 决策 | 结论 | 理由 |
|---|---|---|
| `AppSettings.Language` | **删除** | 界面只有简体中文、无 i18n 基础设施，字段从未被消费方读取；恒等于默认值的死字段会让接手者以为已支持多语言。旧 `settings.json` 里的 `"language"` 被 System.Text.Json 按未知字段忽略，不影响读盘 |
| `LogRedactor` 是否脱敏主目录用户名 | **脱敏** | 崩溃报告最常见的分享场景是贴到公开论坛；`C:\Users\<真实姓名>\` 是隐私泄漏。代价是日志里的路径不再能直接复制粘贴，取舍偏向隐私 |
| 服务端清单是否落盘缓存 | **暂不落盘** | 首次扫描 4.4s（并发 16）到 35s（网络差），进程内已缓存；加 `server-versions.json` + TTL 会引入新的缓存失效问题，等有人反馈慢再说 |

### 13.5 未落地项

| 项 | 原因 |
|---|---|
| `hs_err` 文本进崩溃导出 zip | `LaunchResult` 不暴露 hs_err 文本（`GameLauncher` 私有）；要让真实崩溃也带上，需要扩 `LaunchResult` |
| 导出包含 mods 列表与 config 快照 | 任务只要求五项；「一份 zip 能复现」还需要 `mods-list.txt` |
| 服务端清单落盘缓存 | 见 §13.4 |
| NeoForge 真实冒烟 | 需要 Java 21 + 1.20.2+；API 已确认同构（`NeoForgeInstallOptions` 三项回调齐全） |
| 旧版 Forge（V5/V7/Universal）真装 | 阶段代码共用，减半区间自适应，但没有实测轨迹 |
| 安装批次与普通下载并发 | 仍是单活动批次模型 |
| 双源合并搜索 / 版本原地换加载器 | 改动面大 |
| 数据包 `pack_format` 兼容校验 | 需要「游戏版本 → format」表 |

### 13.6 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead` | 0 错误 / 22 警告（全部既有 CA1861/CA1001/CA1822/CA1859） |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead` | **1063 / 1063 通过**（994 → 1063，新增 69） |
| 数据包在线安装 | 真实 Modrinth `project_type:datapack` 搜索 | 结果页出现 VeinMiner / Terralith / Dungeons and Taverns / Towns and Towers，均带「数据包」徽标，目标显示 `<gameDir>/datapacks` |
| 崩溃导出 | 单测打开生成的 zip 逐条断言 | 五条目齐全；`abc123` / `d4e5f6a7b8c9` / `alice` 在**所有条目** 0 命中；`C:\Users\<user>\AppData\Roaming\.minecraft\config\brokenmod.toml` 保留 |
| 服务端下载 | 真实下载 `1.20.1` | `server-1.20.1.jar` 47,791,053 字节（45.58 MB），SHA1 `84194A2F286EF7C14ED7CE0090DBA59902951553`（与 manifest 一致），10.98s；二次调用 `AlreadyExisted=True` 不发请求 |
| Forge 分阶段 | `InstallProbe 1.20.1 Forge` 三次 | 三次成功（产出 `1.20.1-forge-47.4.28`）；改造后 **75 个进度样本 0 次回退**：`48.4 → 50.0 → 67.0 → 76.3 → 79.9 → 85.0 → 91 → 100`；四阶段日志齐全，耗时 原版 5.1s / 加载器 11.2s / 处理器 35.0s / 收尾 0.05s |

新增测试分布：`CoreAssets_DataPackOnlineTests`（12）、`CoreLaunch_CrashExportTests`（17）、
`CoreVersions_ServerDownloadTests`（14）、`CoreInstalling_LoaderStageTests`（25）、
`CoreSettings_WindowMemoryTests` 等既有文件的增量（1）。

未验证：数据包安装的整条点击链路与位置浮层布局；崩溃导出的 UI 点击（文件夹选择器）；
服务端「下载」按钮的点击链路与结果卡片渲染（CLI 探针跑的是同一个服务）；
Forge 阶段行在真实安装中的截图（截图模式无法触发安装）；
NeoForge 与旧版 Forge 真装；失败/取消路径的真实触发。

---

## 14. 第十二轮（2026-10-22）：补齐上一轮的口子，顺带修两处启动期浪费

### 14.1 落地清单

| 能力 | 来源 | 落点 |
|---|---|---|
| `hs_err` 进崩溃导出 + 导出含 mods 列表 | 上一轮 §13.5 的第一、二项 | `LaunchResult.HsErrLogs`、`GameLauncher.ReadHsErrSections`、`CrashExportService.BuildModsListText` |
| 资源包 / 数据包 `pack_format` 兼容提示 | 上一轮 §13.5 的最后一项 | `Versions/PackFormatTable.cs`（wiki 表格，1.6.1–1.21.8）、模组页卡片徽标 |
| 配置包导出 / 导入 | PCL2 的设置迁移需求 | `Transfer/ProfileBundleService.cs`、设置页「配置迁移」区块 |
| 服务端清单落盘缓存 | 上一轮 §13.4 的决策被推翻（有反馈说慢） | `ServerDownloadService`（TTL 6h + stale-while-revalidate）、`ServerListSnapshot` |
| **设置只加载一次** | Lead 自查：日志里「已加载设置」一次启动出现 3 次 | `ISettingsService.LoadAsync` 幂等 + `ReloadAsync` |
| **日志半行修复** | Lead 自查：日志里出现 `页面转场时长为 02026-10-22 12:50:52` | `FileLogger.EnsureTrailingNewline` |

### 14.2 两处自查出来的问题（不是移植，是自己的债）

1. **设置被加载 3 次**。六个调用方（壳 / 实例上下文 / 账户页 / 设置页 / 资源页 / 服务器页）各自 `LoadAsync`，
   每次都读盘、反序列化、触发 `Changed` → 主题与游戏目录被反复应用。
   改成「`LoadAsync` 幂等、`ReloadAsync` 强制重读」后，实测一次启动从 3 次降到 **1 次**。
   连带修了 `ProfileBundleService` 导入配置后的重载：它原本靠 `LoadAsync` 重读，语义变了必须显式 `ReloadAsync`，
   否则「导入设置」会静默不生效——这类连带影响必须跟着语义变更一起找。
2. **日志出现半行拼接**。截图模式用 `Environment.Exit(0)` 退出，正在写的那一行没写完，下个进程的第一条日志接在同一行上，
   读起来像 `页面转场时长为 02026-10-22 12:50:52.328 [INFO] …`。
   `FileLogger` 现在在首次写当日日志前检查文件末尾是否 `\n`，不是就补一个（只查一次，不增加热路径开销）。
   实测：手工造一个无换行结尾的日志文件，启动一次后新日志另起一行。

### 14.3 偏差与取舍

1. **`pack_format` 只到 1.21.8**。1.21.9 起 Mojang 改用 `min_format`/`max_format`（带小数），整数 `pack_format` 语义被取代，
   所以 1.21.9+ 一律返回 `Unknown` 并在理由里说明，不猜近似值。
2. **配置包默认不导入设置**。机器相关字段（游戏目录、Java 路径）跨机器毫无意义，
   所以导入分两步：先只合收藏与服务器，用户勾了「同时导入设置」才按白名单覆盖 9 个偏好字段。
   白名单之外的键一律不动——包括包里有的。
3. **服务端清单一度决定不落盘，本轮又改成落盘**。上一轮的判断依据是「进程内已缓存，够用」；
   实际使用中每次重开启动器都要等 4–35 秒，属于典型的高频痛点。TTL 6 小时 + stale-while-revalidate 兼顾了新鲜度与首屏速度：
   实测冷启动 14.28s（其中扫描 1.01s、拉 manifest 13s），热启动 **94ms**。
4. **导出里的 mods 列表是「快照」不是「增量」**：只列文件名、启用状态、大小与版本，不读 jar 内的依赖元数据——
   读元数据会让导出变成一次全量扫描，在几百个模组的整合包上要等十几秒。

### 14.4 决策记录

| 决策 | 结论 | 理由 |
|---|---|---|
| `LoadAsync` 语义 | **幂等**（只加载一次），需要重读用 `ReloadAsync` | 每次进页面重读重发 `Changed` 没有任何收益；外部改文件的场景没有 FileSystemWatcher 支持，本来就不成立 |
| 服务端缓存失效判据 | **只按 TTL**，不按 `latest.release` 变化 | 「latest 变了立即失效」必须先请求一次 manifest，与「命中缓存不发网络」直接冲突；TTL 6 小时已足够 |
| 配置包是否含账户 | **不含**，且在 manifest 里写明 `containsAccounts:false` | 令牌不能跨机器搬，导出一份带令牌的包等于制造泄漏面 |

### 14.5 未落地项

| 项 | 原因 |
|---|---|
| 1.21.9+ 的 `min_format` / `max_format` 支持 | 需要新的区间模型（不只是单个整数），且 `pack.mcmeta` 结构变了 |
| 服务端 `latest.release` 变化即失效 | 见 §14.4 |
| 「扫描全部版本」的真实全量扫描 | 有单测覆盖窗口逻辑，未跑真实 120 个 JSON |
| 导出包含 config 文件快照 | 会把导出体积从几百 KB 拉到几 MB，且涉及用户自改配置的隐私 |
| 跨机器实测配置包 | 合并语义在「同一台机器两个数据根」上验证过 |
| 安装批次与普通下载并发 / 双源合并搜索 / 版本原地换加载器 | 与上一轮相同，改动面大 |

### 14.6 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead` | 0 错误 / 22 警告（全部既有） |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead` | **1125 / 1125 通过**（1063 → 1125，新增 62） |
| 设置只加载一次 | 造一份 `settings.json` 的隔离数据目录，截图模式启动一次 | 日志 `已加载设置` **1 次**（改造前 3 次） |
| 日志半行修复 | 手工写一个末尾无换行的日志文件，再启动一次 | 旧半行独占一行，新日志从下一行开始 |
| 崩溃导出 | 实际导出 `crash-export-demo.zip` | 6 条目；`hs_err.txt` 含两份 `===== 文件名 =====`；`mods-list.txt` 有总数/启用/禁用与排序；token / 用户名 0 命中 |
| `pack_format` 提示 | 造 4 个不同 format 的数据包/资源包 + `lastSelectedVersion=1.21.4` | `MC 1.21.4` 目标徽标；`需 1.20.x` / `需 1.21.x` 警告徽标；匹配的无徽标；不阻止启用 |
| 服务端缓存 | 真实网络冷启动 + 热启动 | 冷 14.28s（落盘 14084 字节 / 45 条）；**热 94ms，零网络请求** |
| 配置包 | 单测打开真实 zip 逐条断言 | 无 `accounts.json`、无 `acc-42-keep-out`；服务器 2+2 → 3 条且同地址被覆盖；`theme` 覆盖而 `minecraftDirectory` 不变 |

新增测试分布：`CoreVersions_PackFormatTests`（35）、`CoreTransfer_ProfileBundleTests`（13）、
`CoreVersions_ServerDownloadTests`（14 → 22）、`CoreLaunch_CrashExportTests`（17 → 23）。

未验证：真实游戏崩溃 → 导出的端到端（需 JVM native 崩溃）；`HomeViewModel` 的 mods 收集无单测
（tests 已引用 App，可以补，本轮没补）；「正在后台更新清单」「首次加载」两种状态文案未截到；
「扫描全部版本」点击链路；跨机器导入配置包；1.21.9+ 的包不显示徽标。

---

## 15. 第十三轮（2026-10-22）：组件面板、区间语义与一条查到底的失败

### 15.1 落地清单

| 能力 | 来源 | 落点 |
|---|---|---|
| 实例组件面板（安装清单比对 + 缺失/改动提示 + 只修复缺失） | PCL2 `McInstallManifest` 比对 + 单项修复 | `Installing/ComponentInspector.cs`、`VersionsViewModel` 组件浮层 |
| 1.21.9+ 的 `min_format` / `max_format` 区间语义 | 上一轮 §14.5 首项 | `PackFormatTable.PackFormatRange` / `TryGetExpected` / `Evaluate(range)` |
| 安装器 stdout 日志通道 | 上一轮 §14.5 的「安装器 stdout 无日志」 | `Installing/InstallerOutputLog.cs`（500 字符 / 200 行 / 重复折叠 / 失败提升） |
| `HomeViewModel` mods 收集测试 + 全量扫描实测 | 上一轮 §14.6 的未验证项 | `AppHome_ModsCollectionTests`（7 例）+ 一次性全量扫描探针 |

### 15.2 NeoForge 真装：一次查到底的失败

任务要求「能跑就跑，跑不动说明原因」。结果跑不动，但排查过程本身就是结论：

1. **第一层：ctor 不匹配**。`NeoForgeInstallerBridge` 用 `Activator.CreateInstance(type, launcher, httpClient)`，
   而 `CmlLib.Core.Installer.NeoForge 4.0.1` 的 `NeoForgeInstaller` **只有 `(MinecraftLauncher)` 一个公开构造**。
   → 已改成「两参优先、单参回退」，仍保留反射方案（程序集限定名 + `Type.GetType`）。
2. **第二层：facade 自建 HttpClient**。修掉 ctor 后报 `maven.neoforged.net:443` 连接失败；
   IL 显示单参 ctor 在 IL_0012 直接 `new HttpClient()` 去查版本目录，**不接受外部客户端**，因此绕过了我们的镜像重写。
3. **第三层：该主机在本机不可达**。`Test-NetConnection maven.neoforged.net:443` → `TcpTestSucceeded=False`（32.8s 超时）；
   而 `https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml` → **HTTP 200 / 68481 字节**。
4. **Java 不是瓶颈**：`java-runtime-delta` 是 OpenJDK 21.0.7，探针选中的就是它。

结论：**要让 NeoForge 装得上，必须把「查版本目录」这一请求也接进镜像链路**，做法是绕开 facade
（`NeoForgeVersionLoader(ourHttpClient)` + `NeoFNewest` 直接安装）——那正是被推迟的「桥接重构」。

#### 15.2.1 桥接重构（已落地）

`Installing/NeoForgeInstallerBridge.cs` 按 PCL2 的顺序重写，不再用 `NeoForgeInstaller` facade（反射绕 CS0433 的做法随之删除）：

| 步骤 | PCL2 出处 | Nebula 实现 |
|---|---|---|
| 拉版本目录 | `ModDownloadLib.vb:1094-1105` | 自建 HttpClient 拉 BMCLAPI details，官方 `/api/maven/versions` 兜底；`ParseCatalogNames` 同时认 `files[]` 与 `versions[]` |
| 选构建 | `:1100` 按 `CompareVersion` 匹配 | `SelectApiName`：默认最新稳定版（跳 alpha/beta/`+`），也接受 `21.1.72` / `neoforge-21.1.72` / `1.20.1-47.1.106` 三种写法 |
| 拼 installer jar 地址 | `:1116-1119`（`UrlBase + "-installer.jar"` 再 replace 主机） | `DownloadMirror.BmclApiMap` 的 `maven.neoforged.net/releases/ → bmclapi2/maven/`；Legacy 的 `forge-` 坐标由桥接在 `IGameInstaller` 层改写 |
| 真装 | `ForgelikeInjector` 直接跑 `com.bangbang93.ForgeInstaller` | `NeoForgeInstallerVersionMapper.CreateInstaller` + `IForgeInstaller.Install`（CmlLib 负责解 installer、装库、跑 processors） |

两处与 PCL2 的偏差，都是 CmlLib 的形状逼出来的：

1. **Legacy 1.20.1 的地址要自己改**。`NeoForgeVersion.NeoForgeUrl` 把包名硬编码成 `neoforge`
   （反编译：`".../releases/net/neoforged/neoforge/" + VersionName + "/neoforge-" + VersionName + "-installer.jar"`），
   而 1.20.1 的构件在 `net/neoforged/forge` 下；`GetInstallerFile()` 也不看 `Files` 属性，覆写不进去。
   唯一的口子是 `IGameInstaller` 收到的 `GameFile`：桥接用一个装饰器按 `with { Url = ... }` 改，只动 installer jar 那一条。
2. **装完把 version.json 的 `id` 对回目录名**。CmlLib 把安装器自带的 `version.json` 原样复制成 `<目录名>.json`，
   Legacy 目录名按 PCL2 的做法去了 `1.20.1-` 前缀，`id` 就对不上，CmlLib 按目录名加载会失败。

排查时踩到的环境事实（别当成我们代码的 bug）：

1. BMCLAPI 的 `/maven/` 是缓存代理，**冷门构件会 403**——实测 `neoforge/21.1.72/...installer.jar`
   302 到 `mirrors.cernet.edu.cn` 再到 `cmcc.mirrors.ustc.edu.cn` 后 403，而被请求过的 `21.1.264` 直接 206。
   所以「装最新版」能成，「装某个旧构建」可能卡在镜像未命中 + 官方不可达。
2. **镜像的下载 CDN 必须放行**（`DownloadMirror.IsKnownDownloadHost`）。BMCLAPI 不直接回源，而是随机签发
   四族 CDN（`sakura.sld.tw` / `sakura.exp.dog` / `<N>.749333.xyz` / `open-bmcl-api.lunar.cc`）的带签名 302；
   8 次采样有 3 次首跳直接落在 `<N>.749333.xyz`。原先「未知主机就放弃镜像候选」会让安装**概率性**失败
   （卡在安装器 jar 或第 70 个库上），因为官方 `maven.neoforged.net` 不可达。修法是「稳定域名 + 形状兜底」
   （路径带 `/download/<sha1>` 或 `/bmclapi/<xx>/<sha1>` 即放行；只放行第一跳就够）。

**实测结果（2026-10-22）**：`InstallProbe 1.21.1 NeoForge` 成功——`versions/neoforge-21.1.264`
（`id=neoforge-21.1.264`、`inheritsFrom=1.21.1`、47 libraries），安装器 jar 6.99 MB、70 个库、
三个处理器（`installertools` / `AutoRenamingTool` / `binarypatcher`）都跑完。

### 15.3 偏差与取舍

1. **组件分组按 `ManagedFiles` 的 `Component`，不按 `Components`**。写清单时 `libraries` 只出现在受管文件上，
   不在 `Components` 里——按后者分组会把「库」整块漏掉。
2. **「只修复缺失文件」只在真缺失时可用**。单纯 mtime 改动不给这个入口：那是用户手动替换过文件，
   重下会覆盖他的改动。
3. **stdout 日志有上限**。Forge 安装器一次能刷几百行，`InstallerOutputLog` 限制单行 500 字符 / 每次 200 行，
   连续重复折叠；失败时最后 20 行提升为 `Info`（普通日志级别也能看到上下文）。不这么做的话日志会被安装器输出淹没。
4. **区间语义不折算整数**。1.21.9+ 的 format 是小数（资源包 69.0 / 88.0 / 97.1，数据包 88.0 / 94.1 / 121.0），
   按 wiki 原值记录；`pack_format` 与 `min_format`/`max_format` 同时存在时按区间优先。

### 15.4 已知退化（需要下一轮处理）

版本行现在有 6 个按钮（启动 / 修复 / 组件 / 打开目录 / 隐藏 / 删除），**1024×660 下版本名被挤成首字母 + 省略号**。
已加 `ToolTip` 兜底，但可读性确实退化了。修法是把「隐藏」挪进溢出菜单（`…`），涉及按钮布局参数，本轮没动。

### 15.5 未落地项

| 项 | 原因 |
|---|---|
| NeoForge 真装 | 见 §15.2.1：现代版（1.20.2+）已真装通过；Legacy 1.20.1 的坐标改写只有单测，未真装 |
| 1024 下版本行按钮溢出 | 见 §15.4 |
| `pack_format` 与 min/max 混合的优先级实测 | 实现为区间优先，未造混合样本 |
| 故障注入下的 stdout 提升 / 重复折叠 | 只有单测，Forge 真装这次既没失败也没有连续重复行 |
| 资源包区间的截图 | 数据包分类的截图已覆盖四种判定 |

### 15.6 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 全量构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead` | 0 错误 / 22 警告（全部既有） |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead` | **1189 / 1189 通过**（1125 → 1189，新增 64） |
| 组件面板 | 4 个人造版本目录 + 手工删一个 jar / 改一个 mtime | 「有问题」徽标、汇总「1 个文件缺失、1 个文件被改动」、两段文件列表、「只修复缺失文件」由禁用变可用 |
| 区间语义 | 1.21.9 游戏版本 + 5 个不同 `pack.mcmeta` | 兼容包无徽标；`需 1.20.x` / `需 ≤1.21.8` / `需 1.21.11+` / `需 26.1+` 四种文案 |
| Forge 真装（新代码） | `InstallProbe 1.20.1 Forge`，约 52 秒 | 成功，产出 `1.20.1-forge-47.4.28`；四阶段日志齐全、进度 0 次回退；日志 208 行中 201 行 `[Forge]`（199 Debug + 2 Warn） |
| 全量扫描 | 一次性探针 | 清单 920 条（103 release + 756 snapshot），扫描 859、找到 853、**0 失败、9.84 秒** |
| mods 收集 | 7 例真实文件测试 | 启用/禁用、大小、排序、空目录、路径是文件、子目录过滤、内容服务异常、上下文缺失 |

新增测试分布：`CoreInstalling_ComponentInspectorTests`（14）、`CoreVersions_PackFormatTests`（35 → 57）、
`CoreInstalling_InstallerOutputLogTests`（21）、`AppHome_ModsCollectionTests`（7）。

未验证：NeoForge 真装（§15.2）；组件面板的点击链路与「只修复缺失文件」的联网修复；
1024×660 下版本名的可读性；`pack_format` 与 min/max 混合优先级；故障注入下的日志提升/折叠。

---

## 16. 第十六轮：整合包导入闭环、HMCL / MCBBS 格式、模组页四项、平台级安装增强

本轮的唯一主题是**把 `docs/PCL2-GAP.md` 的候选清单落地**。所有条目都按「价值 ÷ 成本」从高到低取，
抄的是判据与流程，不是 PCL2 的界面。

### 16.1 整合包导入闭环（GAP 第 1 条）

**问题**：导入只做「解压 → 下载 index 文件 → 写 `instance.json`」，不装 `manifest.json` /
`modrinth.index.json` 声明的游戏与加载器。导入完得到一个指向不存在版本的实例，玩家必须自己去版本页再装一次，
且版本号常装错。

**PCL2 出处**：`ModModpack.vb` 的五条安装链都插 `McInstallLoadersGet(New McInstallRequest ...)`
（`:184-204`、`:316`、`:449`、`:520`、`:653`、`:739`）。

**落地**：`Backups/ModpackService.cs` 的导入流程重排成五段——
装游戏与加载器 → 解压覆盖层 → 按 index 下载 → 写 `instance.json`。

- 前置条件是构造注入 `IInstallService`（可选参数，缺失时退化为只解压，不抛异常）。
- **装游戏排在解压之前是刻意的**：失败时实例目录还没建，不需要回滚已解压的几百 MB 文件。
  PCL2 的做法是事后调 `ModDownloadLib.vb:McInstallFailedClearFolder` 清目录，这里用顺序避免，
  代价是「装游戏成功但解压失败」时多一次已装版本的残留（版本本身是可复用的，不算垃圾）。
- **版本名反查**：`InstallAsync` 不返回装出来的目录名，而 Forge/NeoForge 的目录名由安装器决定
  （`1.20.1-forge-47.4.28`），不等于整合包声明里的 `47.4.28`。所以按 `InstallService`
  写的 `versions/<name>/nebula-loader.json` 反查：MC 版本 + 加载器都匹配、`installedAt` 最新的那条。
  marker 缺失时退化为 `BuildLoaderVersionName`（原版与 Fabric/Quilt 的规则是确定的）。
- **加载器版本缺失必须说出来**：整合包没标注 `modLoaderVersion` 时装的是最新稳定版，
  与作者原始组合大概率不一致。PCL2 在这里是静默的，本轮改成在导入结果里给 `Warning`。

**新增契约**：`IModpackService.InstallGameOnImport`（DIM，默认 false）与 `LastImport`（DIM，默认 null）。
`ImportAsync` 签名不变（仍返回目录字符串并抛异常），结构化结果走新增的
`ModpackService.ImportWithResultAsync` 与 `LastImport`。

### 16.2 HMCL 与 MCBBS 格式（GAP 第 7 条 + 第 12 条的一半）

**格式判定顺序照抄 `ModModpack.vb:41-83`**，并且只认**根目录与一级子目录**：

| 顺序 | 键 | 结果 |
|---|---|---|
| 1 | `mcbbs.packmeta` | MCBBS |
| 2 | `mmc-pack.json` | MultiMC |
| 3 | `modrinth.index.json` | Modrinth |
| 4 | `manifest.json` | 有 `addons` → MCBBS，否则 CurseForge |
| 5 | `modpack.json` | HMCL |

改动前是「任意层级找 manifest.json」，会把「普通压缩包里恰好有一个深层 manifest.json」
误判成整合包，解压时只挑 `overrides/` 里的东西，用户得到半个包。现在这个误判不会发生（有测试）。

**HMCL**（`InstallPackHMCL`，`ModModpack.vb:494-520`）：读 `gameVersion`；覆盖层目录是
`minecraft/`（没有前导点）。加载器字段 HMCL 没有标准名（`modLoader` / `mode` 都是派生工具的扩展），
按候选名容错读，读不到就只装原版。

**MCBBS**：`manifest.json` 与 CurseForge 同构（`minecraft.version` + `modLoaders[]`），
所以版本与加载器照常装得上；差别只在 `files[]` 是 addons 引用而不是直链。
本轮**只做到识别 + 装对版本与加载器**，`addons` 逐项反查（按项目 id + 版本 id 查 Modrinth / CurseForge）
没做——它是一项独立的网络链路，失败项要有「待手动安装」出口，成本比识别高一档。

### 16.3 判据小抄：哪些抄了、哪些没抄

| 能力 | 本轮 | 判据 |
|---|---|---|
| 整合包导入装游戏与加载器 | ✅ | 抄流程与 `McInstallRequest` 的字段组合 |
| 格式判定顺序（含根/一级限定） | ✅ | 抄判据 |
| HMCL `minecraft/` 覆盖层 | ✅ | 抄判据 |
| MCBBS 的 `addons` 反查 | ❌ | 独立的网络链路 + 失败出口，单列一项 |
| PCL2 的安装进度 UI（`LoaderCombo` / `LoaderTaskbar`） | ❌ | 已被 `DownloadQueue` 的外部批次取代（§6.19） |
| PCL2 的 `MyMsgBoxInput` 输入实例名 | ❌ | 导入页已有的实例名输入框就是它，不需要弹窗 |

### 16.4 验证

| 项 | 命令 | 结果 |
|---|---|---|
| Core 构建 | `dotnet build src\NebulaLauncher.Core\NebulaLauncher.Core.csproj -c Lead` | 0 错误 / 0 警告 |
| 整合包相关测试 | `TestRunner ... -- Modpack` | **18 / 18 通过** |

新增测试 `CoreAssets_ModpackInstallTests`（6 例）：HMCL 识别、带 `addons` 的 manifest 判为 MCBBS、
深层 manifest 不误判、导入时按 marker 反查版本名并写进 `instance.json`、
加载器安装失败时不留下实例目录、关闭开关时不调安装服务。

未验证：真实 HMCL / MCBBS 整合包各导入一次（只用代码构造的 zip 测过）；
联网安装真跑一次（`InstallProbe` 能覆盖，本轮没跑）。

### 16.5 界面

新增的两处入口按本仓库 `docs/DESIGN.md` 落，没有照 PCL2 的 WPF 对话框：

- 导入预览卡多一行「安装」→ `将安装 Minecraft 1.20.1 + Forge 47.1.0`（缺加载器版本时显示「最新版」）。
- 预览卡多一个「同时安装游戏与加载器」开关（仅在整合包声明了 MC 版本时出现），关掉时的说明写明后果。
- 导入完成文案按结果分三种：带警告 / 装了游戏 / 只解压。

## 17. 第十七轮（2026-10-22）：平台与安装模块的四项增强

本轮是 `docs/HANDOVER-2026-10-22.md` 分工里的「平台与安装」模块，对应的 GAP 条目是
第 2（显卡首选项）、3（Fabric API 随附）、9（Forge 推荐版）、11（仅下载不安装）。

### 17.1 启动前把 javaw.exe 注册为高性能 GPU（GAP 第 2 条）

**问题**：双显卡笔记本上，系统默认把 `javaw.exe` 分给核显。后果是帧数掉一半，
而用户既不知道有这一步，也不知道去「设置 → 显示 → 图形」点五次。

**PCL2 出处**：`ModMain.vb:802-813 SetGPUPreference` + `ModLaunch.vb:1748-1766`
（`LaunchAdvanceGraphicCard` 打开时，对选中的 Java 和 PCL 自身各调一次）。

**落地**：
- `IPlatformService.TrySetGpuPreference(string exePath)`（DIM，默认 `false`）；
- `Platforms/PlatformService.cs` 写 `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`，
  值名是可执行文件绝对路径、值是 `GpuPreference=2;`（与系统「图形设置」逐字节一致）；
- `GameLauncher.ApplyGpuPreference` 在 `process.Start()` 之前调用；
- `AppSettings.RegisterGpuPreference`（默认 `true`）+ 设置页「游戏」分区开关。

三条取舍：

1. **只写 HKCU，绝不碰 HKLM，也不尝试提权**。PCL2 写失败时会 `RunAsAdmin` 重启自身；
   那会让启动器在用户不知情的情况下弹一次 UAC，代价远大于收益。
2. **幂等判据按段比对，不按整串比对**。值是 `<段>;<段>;` 拼起来的，Windows 自己也会往里
   追加 `SwapEffectUpgradeEnable=1;` 之类的段。判「整个字符串等于 `GpuPreference=2;`」
   会让每次启动都判定未满足，进而把用户其它段整片覆盖掉。
3. **失败只记日志，不拦启动**。组策略锁 HKCU 的 DirectX 子键时写不进去，
   这只是少一次性能优化；把游戏拦在这里是本末倒置。

`CrashRules` 里两条显卡相关的提示同步改了文案（`ModMain` 那两条规则的 `Description`）：
原本让用户自己去系统图形设置里指定，现在说明启动器默认已经做了、以及去哪关。
默认值 `true` 意味着**老配置升级后也会打开**——这是有意的，它对绝大多数用户只有好处，
且用户随时可以在设置里关掉。

### 17.2 Fabric API 随 Fabric 一起装（GAP 第 3 条）

**问题**：Fabric 生态里绝大多数模组依赖 Fabric API。漏装的表现是启动时 `NoClassDefFoundError`，
日志里那行类名对新手毫无指向性。

**PCL2 出处**：`PageDownloadInstall.xaml.vb:1014-1019 AutoSelectedFabricApi`
（默认勾选、选中列表首项）+ `:929-942 IsFabricApiCompatible`（版本兼容过滤）。

**落地**：`IInstallService.InstallAsync` 增加第 4 个可选参数 `fabricApiVersionId`；
`InstallService.TryInstallFabricApiAsync` 在加载器装完之后、写组件清单之前，
把选定的版本装进 `mods/`。

- **slug 写死 `fabric-api`**（`InstallService.FabricApiSlug`）：slug 是项目永久别名，
  而按名字搜的首条可能是第三方兼容层。只走 Modrinth：安装链路只接了 `IModrinthService`，
  也没有为 Fabric API 建 CurseForge 的 classId 常量。
- **失败不抛**。加载器本体已经在磁盘上了，Fabric API 装不上只是少一个模组；
  把它变成「整个安装失败」会让用户连版本都拿不到，而重试一次就能补上。
- **版本 Id 对不上时退回首项**（`PickFabricApiVersion`）。从打开面板到点「开始安装」
  之间可能过了几分钟，Modrinth 那边完全可能刚好撤掉那一版。
- **目标目录取 `MinecraftDirectory`**：安装阶段还没有实例，版本隔离在首次启动时才建目录
  （HANDOVER §6.10），此时 `mods/` 只能落在全局目录。

### 17.3 Forge 推荐版（GAP 第 9 条）

**问题**：Forge 的「最新构建」经常是坏的。新手按默认装到它，表现是「装完启动崩、
日志看不出原因」。官方其实在 `promotions_slim.json` 里标了每个 Minecraft 版本的推荐构建，
但安装面板从来没读过它。

**PCL2 出处**：`ModDownloadLib.vb:1351-1361`（缓存 `ForgeRecommendedList.json`）+
`:1434-1458`（列表里推荐版排首位并标「推荐版」，推荐与最新相同时只显示推荐）。

**落地**：`LoaderVersion.IsRecommended` + `LoaderMetaUrls.ForgePromotions(source)` 候选
+ `VersionService.GetForgePromotionsAsync`（内存 → 磁盘 → 网络三级缓存，TTL 24 小时）。

- **两种 JSON 形状都要认**：官方 `promotions_slim.json` 是
  `{"promos":{"1.20.1-recommended":"47.2.0"}}`，BMCLAPI `/forge/promos` 是
  `[{"name":"1.20.1-recommended","build":{"version":"47.2.0"}}]`。解析器两种都收。
- **候选顺序永远官方优先，不随下载源切换**（`LoaderMetaUrls.ForgePromotions()` 无参）。
  这是一条实测出来的例外，实测数据：

  | 源 | 体积 | `1.21.1-recommended` |
  |---|---|---|
  | `https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json` | 4 KB | **52.1.20** |
  | `https://bmclapi2.bangbang93.com/forge/promos` | 62 KB | 52.1.0（滞后二十个小版本） |

  镜像的意义是省带宽和加速，而这份 JSON 一旦拿旧值，用户会被引导去装官方已不再推荐的构建。
  4 KB 换正确性。PCL2 只读 BMCLAPI 那份（`ModDownloadLib.vb:1419`），这里刻意不照抄。
  改之前截图里显示的是 52.1.0，改之后是 52.1.20，与 `Invoke-WebRequest` 直接取官方值一致。
- **只收 `-recommended` 后缀**。`-latest` 就是「最新构建」，正是要避开的那个。
- **裸 `recommended` 键跳过**：官方文件顶层那条指「当前最新 MC 版本」的推荐构建，
  不带版本前缀，猜错会让用户装到别的版本的构建。
- **没有推荐就不出列表**：`GetLoaderVersionsAsync(Forge, ...)` 返回空，
  面板文案退化成「官方还没有标推荐构建，直接点开始安装会装最新构建」。
- **不给 NeoForge 编推荐值**：NeoForge 没有官方推荐列表。
- **缓存写盘失败不影响结果**，下次多一次网络请求而已。

Forge 的构建列表仍然没有「全量可选」：构建全集由安装器自己决定，本轮不伪造一份。

### 17.4 只下载客户端 jar，不安装（GAP 第 11 条）

**问题**：「只要同版本的 client.jar」以前必须先安装再拷出去，而安装会顺带补
一大堆 libraries 与 assets。

**PCL2 出处**：`ModDownloadLib.vb:McDownloadMenuSave` / `McDownloadSaveMenuBuild`。

**落地**：`IVersionService.SaveClientJarAsync(versionName, targetDirectory, progress, ct)`。
版本页溢出菜单（`…`）新增「保存客户端 jar…」，目录由 code-behind 的文件夹选择器取
（目录选择需要 `TopLevel`，属于视图层）。

- **只落 `downloads.client` 一个文件**。PCL2 的「另存为」会连版本 JSON 一起写；
  这里不做——一个目录里多出一个同名 JSON 只会让人以为那是可安装的版本。
- **沿 `inheritsFrom` 回溯**：Fabric / Quilt 的 profile JSON 里 `downloads` 是空对象，
  客户端 jar 永远挂在原版那一层。
- **已存在且 SHA1 对得上就不重下**（客户端 jar 动辄十几 MB）。
- **取消时删半截文件**：半截 jar 在磁盘上比没有更糟——拷给别人会直接崩在启动。

### 17.5 判据小抄

| 能力 | 本轮 | 判据 |
|---|---|---|
| 显卡首选项写入格式 | ✅ | 值名 = 绝对路径，值 = `GpuPreference=2;`，逐字节同系统 |
| 首选项幂等 | ✅ | 按段比对，不整串比对（保护用户其它段） |
| 首选项失败处理 | ✅ | 只记日志，不弹 UAC、不拦启动 |
| Fabric API 自动装 | ✅ | 抄「默认勾选 + 选首项」的判据 |
| Fabric API 版本兼容过滤 | ⚠️ | 用 Modrinth 的 `gameVersion` + `fabric` loader 过滤，**没有**复刻 PCL2 那套 `[1.20.1] 1.20.x/…` 的字符串区间算法——Modrinth 已经替我们判过了 |
| Forge 推荐版 | ✅ | 抄缓存与「推荐优先」的判据 |
| Forge 全量构建列表 | ❌ | 构建全集由安装器决定，不伪造 |
| 仅下载 client.jar | ✅ | 只落一个文件、回溯继承链、SHA1 复核 |
| 提权重启自身（`RunAsAdmin`） | ❌ | UAC 打扰的代价大于收益 |

### 17.6 验证

| 项 | 命令 | 结果 |
|---|---|---|
| Core 构建 | `dotnet build src\NebulaLauncher.Core\NebulaLauncher.Core.csproj -c plat` | 0 错误 |
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c plat` | 0 错误 / 26 警告（全部既有） |
| 全量测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c plat -m:1 -p:BuildInParallel=false` | **1291 / 1291 通过，0 失败** |
| Forge 推荐版实测 | `--page download --loader forge --mc 1.21.1` | 真实联网拉到官方推荐 `52.1.0`，徽标与默认选中都对 |
| 无推荐的版本 | `--loader forge --mc 26.3` | 文案退化为「官方还没有给 Minecraft 26.3 标推荐构建」 |
| Fabric API 实测 | `--loader fabric` | 查到并显示 `0.164.0+26.3`（最新兼容版本），开关默认开 |
| 截图 | 见下 | 亮 / 暗各一张 |

新增测试 26 例：
`CoreVersions_ForgeRecommendTests`（8）、`CorePlatforms_GpuPreferenceTests`（6）、
`CoreVersions_ClientJarSaveTests`（6）、`CoreInstalling_FabricApiTests`（6）。

未验证：
- **注册表真的被写进去了没有**。判据部分（值名规范化、幂等判定、空输入）有 6 例单测，
  但真正调 `TrySetGpuPreference` 会改测试机的显卡设置，所以没有任何断言覆盖到写盘本身。
- Fabric API 的真实下载安装（只验证了版本查询与选择逻辑）。
- 「保存客户端 jar」的端到端：菜单点击 → 文件夹选择器 → 落盘。三个环节都在视图层，
  截图模式点不了菜单。

### 17.7 界面

- 设置 → 游戏：「启动前注册高性能 GPU」开关（说明行按平台改写，非 Windows 上直接说明「没有这个选项」）。
- 下载页安装面板：Fabric 选中时出现「同时安装 Fabric API」开关，下面一行显示实际会装的版本号。
- 下载页加载器版本下拉：Forge 的推荐构建带绿色「推荐版」徽标，并且是默认选中项。
- 版本页溢出菜单（`…`）：「保存客户端 jar…」。**只加菜单项，不动三列布局**——
  1024×660 下的按钮溢出是本轮刚修好的，不能再撞一次。

截图模式的 `--loader` / `--mc` 参数是这一轮加的：加载器胶囊和版本行都只能点，
没有这两个参数就永远截不到「Fabric 选中态」与「Forge 有推荐版的那个版本」。
`--loader` 走的是 `SetLoader` 而不是直接赋 `SelectedLoader`——后者不会更新 `IsLoaderXxx`，
界面上胶囊会停在「原版」高亮上而内容区已经是 Fabric 的配置（第一次截图就是这么拍到的）。

## 18. 第十八轮（2026-10-22）：模组页四项（GAP 第 4 / 5 / 6 / 10 条）

本轮是 `docs/HANDOVER-2026-10-22.md` 分工里的「模组」模块。

### 18.1 删除进回收站（GAP 第 4 条）

**问题**：原来点删除就是 `File.Delete`。PCL2 的删除默认进回收站（只有按住 Shift 才真删），
理由是「删错了还能找回」这条比「省一点磁盘」重要得多——尤其在本启动器已经有
「一键更新」之后：更新完新版本崩了，用户第一反应就是换回旧文件。

**PCL2 出处**：`PageInstanceMod.xaml.vb:666-683 DeleteMods`
（`FileUtils.Delete(ModEntity.File.FullName, Not IsShiftPressed)`），以及
`:674` 在删除前把「当前文件 + 另一种后缀的那份」一起算进来。

**落地**：

- `IContentService.DeleteAsync(ContentItem, bool permanent, CancellationToken)`（DIM，
  默认转调旧的 `DeleteAsync(item, ct)`），加上 `ListTrashAsync` / `RestoreAsync` / `PurgeTrashAsync`；
- 新增 `ContentTrashEntry`（`Id` / `OriginalPath` / `FileName` / `TrashPath` / `Kind` /
  `DeletedAt` / `SizeBytes` / `IsDirectory`）；
- `ContentService` 的回收站落点是 `<data>/trash/contents/<trashId>/`，与实例侧的
  `<data>/trash/instances/` 并列；条目内是 `meta.json` + 原始文件名的 payload；
- `trashId` 形如 `20261029-153012-1a2b3c4d`，撞名追加 `-N`；
- 删除时**把同一个模组的启用态与禁用态一起搬走**（对应 PCL2 `:674` 的 `SelectMany`）：
  只搬一份会让列表里剩下「半个模组」，点删除像没删掉。

三条取舍：

1. **`Restore` 不覆盖同名文件**。恢复时目标路径若已被占用，改用 `name (1).ext` 落盘。
   静默覆盖会毁掉用户后来装的那一份，而这正是「撤销一次删除」最不该有的副作用。
2. **`trashId` 必须校验后才拼路径**（`IsSafeTrashId`：只允许 `[A-Za-z0-9-]`）。
   它来自 `meta.json` 之外的调用方，直接拼进 `Path.Combine` 就等于把目录穿越交给调用者。
   非法 Id 的 `RestoreAsync` 返回 `false`、`PurgeTrashAsync` 静默不动。
3. **跨卷移动退回 copy + delete**。数据目录与缓存目录不一定在同一卷，`Directory.Move`
   会抛 `IOException`；此时先复制再删原件，代价是慢一点，但「删不掉」的代价更大。

### 18.2 状态筛选四档（GAP 第 5 条）

**PCL2 出处**：`PageInstanceMod.xaml.vb:427-440 CanPassFilter`，
`FilterType.All / Enabled / Disabled / CanUpdate`。

**落地**：`ModsViewModel.ModsFilter`（`All` / `Enabled` / `Disabled` / `CanUpdate`）+
`ActiveFilter` + `IsFilterAll/Enabled/Disabled/CanUpdate` + `SetFilterCommand`，
在 `ApplyFilter()` 里作为**第二道**过滤（第一道是搜索词），与分类 chip 同排渲染。

- **「可更新」不是独立数据源**，而是从 `Updates` 派生 `HashSet<ContentItem>`。
  用 `ReferenceEqualityComparer`：扫描时每个文件只创建一个 `ContentItem`，更新结果里的
  `Item` 就是它；按值比较会把两个不同目录的同名文件混成一个。
- **没有检查结果时这一档置灰**而不是隐藏。隐藏会让工具行在不检查更新前后长度不同，
  用户会以为点了什么改变了页面结构。
- **`PruneSelection()` 必须留在 `ApplyFilter()` 末尾**。筛选之后如果还保留看不见的勾选，
  点「删除选中」会连不可见的项一起删掉——这是筛选功能最容易引入的破坏性 bug。
- 筛选筛空时的空态文案单独写（「没有已启用的模组」/「先点检查更新」），不复用搜索关键词那句：
  用户没输关键词，看到「没有匹配的内容」会以为搜索框坏了。

### 18.3 一键更新选中项（GAP 第 6 条）

**PCL2 出处**：`PageInstanceMod.xaml.vb:528-531 BtnSelectUpdate_Click`
（`SelectedMods.Where(Function(m) m.HasUpdate)`）+ `:538-560 UpdateMods`；
文件名段对齐在 `:556-581`。

**落地**：`IModUpdateService.UpdateAsync(IReadOnlyList<ModUpdateInfo>, gameDirectory, progress, ct)`
（DIM，默认返回「当前数据源未实现模组更新」），`ModUpdateService` 实现完整链路：

```
下载到 <cache>/mod-update/<guid>.part
  → 校验（字节数 + Modrinth SHA1 反查）
  → 旧文件进回收站
  → 新文件落到旧位置（同名冲突时 name (N).ext）
```

**文件名段对齐**（`ModUpdateService.AlignFileName`，public static，可单测）抄 PCL2 的做法：
两边按 `-` 切段 → 去掉相同的**前缀**段 → 去掉相同的**后缀**段 → 只替换中间那段。
`jei-1.20.1-15.2.0.27.jar` → `jei-1.20.1-15.2.0.30.jar` 得到的是
`jei-1.20.1-15.2.0.30.jar`，不是 `jei-15.2.0.30.jar`，也不是
`jei-1.20.1-15.2.0.30-1.20.1.jar`。

**为什么这一步是必须的**：新文件名若直接采用数据源的远程名，`mods/` 里会同时存在新旧两份，
加载器报 `duplicate mod` 直接崩游戏。这是「一键更新」最容易踩、也最难归因的坑。
扩展名沿用本地的（除非远程换了扩展名，例如 `.jar` → `.litemod`）。

四条取舍：

1. **只更新「已勾选 + `HasUpdate`」的条目**，不提供「全部更新」。整合包里全量更新等于重装，
   而且下载失败一半之后没人知道停在哪。
2. **先下完再动旧文件**。顺序颠倒的两个方向都错：先放新文件会撞名，先删旧文件则下载
   失败时两头空。旧文件进回收站是第三条保险（18.1 直接受益）。
3. **大小不符即丢弃，SHA1 反查失败不阻断**。字节数能挡住「下到 404 页面」这类问题；
   而 Modrinth 反查需要联网，该文件可能刚上传还没进索引——此时旧文件仍在回收站里可找回，
   没必要为了一个可能误判的环节让整个更新失败。
4. **禁用态的模组更新后仍是禁用态**（落盘后缀沿用 `.old` / `.disabled`）。
   悄悄启用一个用户特意关掉的模组，比更新失败更糟。

### 18.4 `.old` / `.litemod` 后缀（GAP 第 10 条）

**PCL2 出处**：`LocalResourceFile.vb:39/41`（`IsEnabled` 认 `.jar/.zip/.litemod`，
`EnabledName` 用 `Replace(".disabled","").Replace(".old","")`）。

**落地**：

- `ContentService.IsContentFile` 改多后缀判定（模组：`.jar` / `.litemod`；
  资源包：`.zip` / `.resourcepack`；光影：`.zip`；数据包：`.zip`）；
- `IsEnabledOnDisk` / `EnabledPathOf` / `SetEnabledAsync` 同时认 `.disabled` 与 `.old`；
- 新增 `ContentService.HasDisabledSuffix`（public static）与 `ContentBatch.LegacyDisabledSuffix`。

两处优先级是**故意相反**的：

| 场景 | 选谁 | 为什么 |
|---|---|---|
| 扫描时两种后缀并存 | `.disabled` | 本启动器生成的那份更有代表性，用户手改的 `.old` 通常是历史残留 |
| 禁用时 `.old` 已存在 | 沿用 `.old` | PCL2 原语义；生成第二份会让同一模组出现两个条目 |

扫描结果再过一次 `DeduplicateVariants`：按 `EnabledPathOf` 分组，
`IsEnabled` 优先、其次按后缀顺序取一条。不做这步的话，`.old` 与 `.disabled` 并存时
列表里会出现两行，用户删掉一行发现另一行还在，看起来像「删不掉」。

### 18.5 能力对照

| 项 | 状态 | 说明 |
|---|---|---|
| 删除进回收站 | ✅ | 默认路径，可在页面内恢复 / 彻底删除 |
| Shift 直删 | ⚠️ | 换成确认面板里的显式「彻底删除」，见下 |
| 回收站跨卷 | ✅ | copy + delete 兜底 |
| 四档筛选 | ✅ | 全部 / 已启用 / 已禁用 / 可更新 |
| 一键更新选中项 | ✅ | 下载 + 校验 + 旧文件进回收站 |
| 更新文件名段对齐 | ✅ | 抄 PCL2 `:556-581` |
| `.old` / `.litemod` | ✅ | 扫描、启用态判定、批量禁用判据都认 |
| 更新崩溃存档提示 | ✅ | 确认面板里的固定文案（PCL2 是 `MessageBox`） |
| CurseForge 指纹校验 | ❌ | 更新链路只做字节数 + Modrinth SHA1；CF 的 MurmurHash2 反查需要联网，沙箱里未跑真实请求 |

**为什么把 Shift 换成显式按钮**：截图模式点不出键盘状态，隐藏的 Shift 快捷键等于
零验收覆盖；而「彻底删除」是破坏性操作，本来就该比快捷键更难误触。
确认面板里同时给「移到回收站」和「彻底删除」两个按钮，默认动作是可恢复的那个。

### 18.6 验证

| 项 | 命令 | 结果 |
|---|---|---|
| Core 构建 | `dotnet build src\NebulaLauncher.Core\NebulaLauncher.Core.csproj -c mods` | 0 错误 |
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c mods` | 0 错误 / 25 警告（全部既有） |
| 新增测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c mods -- CoreMods_TrashAndUpdate` | **21 / 21 通过** |
| 深色截图 | `tools\shot.ps1 -Page mods -Configuration mods -Theme dark` | `mods-dark.png` |
| 浅色截图 | 同上 `-Theme light` | `mods-light.png` |
| 窄屏 | 同上 `-Size 1024x660` | `mods-narrow.png`，工具行自动折成三行，无裁切 |
| 更新面板 | 同上 `-Section updates` | `mods-updates.png`，「更新选中项」与可点的「可更新」档都在 |

`CoreMods_TrashAndUpdateTests` 覆盖：回收站（删除 / 恢复 / 彻底删除 / 连带禁用变体 /
非法 Id）、`.old` 与 `.litemod`（扫描、去重优先级、禁用时沿用、批量判据）、
`AlignFileName`（5 组段对齐 + 2 组不变）、`UpdateAsync`（完整更新链路 / 大小不符不毁旧文件 /
无新版本计跳过）。

未验证：

- **真实网络的模组更新**。单测用 `StubHttpHandler` 回固定字节，`VerifyAsync` 的 SHA1
  反查分支（`_modrinth` 非 null 且来源是 Modrinth）没有任何断言覆盖到。
- **`RestoreAsync` 与「目标已存在」的交互**。`ResolveFreePathAt` 有实现但没有对应用例。
- **Shift 与右键菜单**。PCL2 的删除有右键菜单入口，本页只有卡片上的按钮；
  菜单不是本轮 GAP 条目，没做。

## 19. 第十九轮（2026-10-22）：资源页「整合包」分区（GAP 第 8 条）

GAP 第 8 条「在线整合包浏览与安装」只做了一半：Core 侧的导入链（第 16 轮）早就能把 `.mrpack`
装成实例，但资源页只有模组 / 资源包 / 光影 / 数据包四个分类，玩家没有「找到并下载整合包」的入口。
本轮补的是这个入口。

### 19.1 PCL2 对照

| PCL2 位置 | 行为 | 本仓库落点 |
|---|---|---|
| `PageResource.xaml.vb:58` `BtnSearchInstallModPack` | 资源页上并排的第二个搜索按钮 | `LibraryView.axaml` 分类胶囊行的第五个 chip「整合包」 |
| `PageResource.xaml.vb:262` `BtnSearchInstallModPack_Click` | 把搜索上下文切到「整合包」，再走同一个搜索器 | `LibraryViewModel.SelectModPackAsync`（`IsModPackTabActive` + 重搜） |
| `ResourceSearcher.vb:77` | 按内容类型选 `Modrinth` / `CurseForge` 的项目类型 | `ModpackCatalogClient.BuildSearchFacets`：固定 `project_type:modpack` |
| `ResourceSearcher.vb:488` | 整合包单列一条下载链（先取 `.mrpack` 再交给安装器） | `InstallModPackAsync`：`DownloadQueue.DownloadAsync` → `IModpackService.ImportAsync` |
| `PageDownloadCompDetail.xaml.vb:385` `:393` | 详情页直接给「下载并安装」，安装结果回到实例列表 | 右栏安装按钮 + 导入结果条 + 「去版本页查看」/「打开实例目录」 |

PCL2 把整合包做成资源页的第五个搜索入口而不是第五个 `ContentKind`，本仓库沿用这个划分。

### 19.2 为什么「整合包」不是 `ContentKind` 值

`ContentKind` 是**本地内容目录**的键：`DirectoryNameFor` / `ContentTarget.DirectoryNameOf` /
扫描 / 版本不匹配检查等 6 处 switch 都以它为输入（HANDOVER §9）。整合包不落在内容子目录里——
它装成的是**实例**，落点是 `instances/<名字>/`，与 `mods/`、`resourcepacks/` 不是同一类东西。

加一个 `ContentKind.ModPack` 要同步改掉那 6 处 switch，而其中 4 处的语义对整合包根本不成立
（比如「版本不匹配时标黄」对整合包没有意义：包自己带了版本）。所以：

- 分区用独立的 `LibraryViewModel.IsModPackTabActive` 布尔量，四个内容分类的 `Is*TabActive`
  在它为 true 时统一变成 false（XAML 的 `Classes.active` 直接吃这四个属性）。
- 落点相关的派生属性按分区先分支：`KindLabel`、`InstallTargetHint`、`InstallTargetPath`、
  `InstallTargetName`、`InstallButtonText`、`CanInstallNow`、`EffectiveLoader`、`FilterLimitedHint`。

### 19.3 为什么要一个 `ModpackCatalogClient`

`IModrinthService.SearchAsync(ContentKind kind, ...)` 的 facets 由 `ContentKind` 决定：
`BuildSearchFacets` → `ModrinthProject.ProjectTypeOf(kind)`，后者只映射
resourcepack / shader / datapack，`_ => "mod"`。**传 `ContentKind.Mod` 必然搜出 `project_type:mod`**，
而枚举里不允许加 modpack（§19.2）。所以「搜整合包」无法由既有接口表达。

`src/NebulaLauncher.Core/Mods/ModpackCatalogClient.cs` 只补这一件事，且刻意做成薄封装：

- `BuildSearchUrl` / `BuildSearchFacets` / `DescribeStatus`：拼 URL 与状态码文案，纯函数。
- `ParseSearch`：**直接调用** `ModrinthService.ParseSearch(json, ContentKind.Mod)`，不重复解析。
- 版本列表仍走 `IModrinthService.GetVersionsAsync`；下载仍走 `DownloadQueue.DownloadAsync`；
  导入仍走 `IModpackService.ImportAsync`。没有新模型、没有新状态机。
- 失败一律「空集合 + 中文 `LastError`」（429 单列一句），不把异常抛给 UI。

不按加载器过滤：加载器由包自己声明，按当前实例的加载器筛会把别的加载器的包藏起来。

### 19.4 下载 → 导入为什么分两步

`.mrpack` 先落到 `cache/modpacks/<清洗后的文件名>`，再交给 `IModpackService.ImportAsync`。
不分两步的话导入失败时没有可回滚的「原件」，而且导入链自己要开外部下载批次
（装游戏 → 解压覆盖层 → 按 index 补文件），与 `.mrpack` 的下载必须是**先后关系**：
`DownloadQueue` 只允许一个活动批次，普通 `DownloadAsync` 在外部批次期间会直接抛
`InvalidOperationException`（HANDOVER §6.19）。所以：

1. `DownloadQueue.DownloadAsync([DownloadItem], progress, token)` —— 普通批次，`.mrpack`
   因此会出现在下载面板里，与其它下载共用同一套进度 / 重试 / 失败汇总。
2. 批次结束后才 `ImportAsync`，导入内部自己开外部批次。
3. 无论成败，`finally` 里删掉临时归档。

导入前显式设 `IModpackService.InstallGameOnImport = true`：关掉会得到一个指向不存在版本的实例
（玩家还得自己去版本页再装一次，且版本号常装错）。

### 19.5 导入后实例「在版本页可见」怎么保证

不新增事件：导入完成后调 `IInstanceContext.LoadAsync(token)`。`InstanceService.LoadAsync`
会重新枚举 `InstancesDirectory` 并逐份重读 `instance.json`，版本页进入时走的也是同一个调用。
导入本身已落盘完成，所以这里失败只吞掉刷新（版本页进入时会再读一次），不把成功说成失败。

### 19.6 验证

| 项 | 命令 | 结果 |
|---|---|---|
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c mpack -v q --nologo` | **0 错误 / 25 警告**（全部既有代码） |
| 新增测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c mpack -- AppLibrary` | **5 / 5 通过** |
| 深色截图 | `tools\shot.ps1 -Page library -Section modpack -Theme dark -Configuration mpack` | `lib-modpack-dark.png` |
| 浅色截图 | 同上 `-Theme light` | `lib-modpack-light.png` |
| 窄屏 1024×660 | 同上 `-Size 1024x660` | `lib-modpack-narrow.png` |

截图里直接连着真实 Modrinth：留空搜索框按下载量列出 Fabulously Optimized / Zombie Invade 100 Days /
Cobblemon Official Modpack，说明 `project_type:modpack` 的 facets 与解析都成立（不只是假 handler 通过）。

`AppLibrary_ModPackTests` 覆盖：搜索 URL 的分面（`project_type:modpack` + `versions:` + 排序 /
分页夹取）、假 handler 下解析出的项目与请求 URL、429 时返回空集合且 `LastError` 含「限流」、
临时归档路径的非法字符清洗与 `.mrpack` 补全、导入结果文案的三种情形与实例名拼接（含去重与 60 字截断）。

### 19.7 未验证

- **选中项目后的整条链路没有截图**。截图模式不能点击，`--section modpack` 只能停在分类上；
  版本列表 / 安装按钮 / 导入结果条依赖编译期绑定校验（`AvaloniaUseCompiledBindingsByDefault`
  会拒绝任何不存在的属性名）与代码审查，没有实际渲染到。
- **真实下载并导入一个 `.mrpack` 没有跑过**。真实网络只验证了搜索；`DownloadAsync` +
  `ImportAsync` 这一段由既有 `CoreAssets_ModpackInstallTests` 覆盖（用的是本地构造的归档），
  本轮没有在真实 Modrinth 包上端到端跑。
- **CurseForge 的整合包**。本仓库的搜索契约按内容类型分派，没有整合包这一类；
  分区固定只列 Modrinth，界面上有一句固定说明（`ModPackSourceNotice`）。
- **下载中断后重试**。`.mrpack` 用的是普通下载批次，重试语义沿用 `DownloadQueue` 既有行为，
  本轮没有针对整合包单独补断言。

---

## 20. 第二十轮（2026-10-22）：资源页排序、模组卡片右键菜单

两件事都不是"新功能"，是把已有能力接上用户的手：Modrinth 的 `index` 参数一直存在但被写死，
模组卡片上的操作一直只有两个图标按钮。

### 20.1 资源页排序

**PCL2 出处**：`PageResource.xaml.vb` 的排序下拉（`SearchSort`），以及 `ModComp.vb:SearchComp` 里
把排序拼进 CurseForge 查询的方式。PCL2 的排序对两个源都生效，因为它自己拼两个源的查询串；
本项目的 CurseForge `sortField` 固定为 2（下载量），没有把排序拼进 CF 查询，所以排序只在 Modrinth 上给。

**原状**：`ModrinthService.BuildSearchUrl` 里写死 —— 空查询 `index=downloads`（浏览热门）、
有查询 `index=relevance`（相关度）。`/v2/search` 实际支持 `relevance` / `downloads` /
`follows` / `newest` / `updated`，用户一个都选不了。

**改法**：

| 位置 | 改动 |
|---|---|
| `Core/Mods/ModrinthService.cs` | `BuildSearchUrl` 加可选 `sort` 参数；新增 `NormalizeSortIndex(sort, hasQuery)` 做**白名单**映射；`SearchAsync` 改成一行转调新的 `SearchSortedAsync` |
| `Core/Abstractions/ContentServices.cs` | `IModrinthService` 追加 `SearchSortedAsync(...)`，带 DIM 默认实现转调 `SearchAsync`（不强制其它实现重写） |
| `App/ViewModels/LibraryViewModel.cs` | `SortOptions` / `SelectedSort` / `SupportsSort`；切换排序立即重查第一页（取消上一次请求） |
| `App/Views/LibraryView.axaml` | 数据源行末尾加竖线 + 「排序」下拉 |

**为什么 `index` 必须走白名单**：它是查询串的一部分。直接 `$"&index={userValue}"` 等于把
`downloads&limit=999&x=` 这种值原样拼进 URL。`CoreMods_SearchSortTests.恶意排序值不会污染查询串`
锁住了这一点。

**为什么切换排序要自动重查**：`.mrpack` 那条链路上已经证明"改完筛选必须点刷新"会被当成 bug
（§19.7）。排序同理。代价是取消上一次请求——`_searchCts.Cancel()` 已经在 `SearchCoreAsync` 里，
这里只是提前触发。

**为什么 CurseForge / 整合包不显示排序控件**：`SupportsSort => IsModrinthSourceActive && !IsModPackTabActive`。
显示一个点了没反应的控件比不显示更糟。

### 20.2 模组卡片右键菜单

**PCL2 出处**：`PageInstanceMod.xaml.vb` 的卡片右键菜单（打开文件夹 / 复制文件名 / 禁用 / 删除 / 查看详情）。

**原状**：卡片右下有两个图标按钮（打开所在目录、删除），禁用靠开关。批量改名、去网页搜同名项目、
贴路径给反馈这三种场景都得先把卡片悬停出来再点。

**改法**：

| 位置 | 改动 |
|---|---|
| `App/ViewModels/ModsViewModel.cs` | 新增 `CopyFileNameCommand` / `CopyPathCommand` / `ToggleEnabledCommand`（都走既有的 `_platform.CopyTextAsync` 与 `ApplyEnabledAsync`） |
| `App/ViewModels/ModsViewModel.cs` | `ContentCardViewModel` 暴露 `Owner` |
| `App/Views/ModsView.axaml` | 卡片 `Border` 挂 `ContextMenu`：打开所在文件夹 / 复制文件名 / 复制完整路径 / 启用·禁用 / 删除… |

**`Owner` 为什么必须暴露**：`ContextMenu` 是弹出层，不在主视觉树里，`$parent[UserControl]`
解析不到页面 DataContext。这是 Avalonia 的既有行为（WPF 里 `PlacementTarget` 才能绕）。
菜单项里 `{Binding}` 解析到卡片自身，命令走 `{Binding Owner.xxxCommand}`。

**复制文件名而不是只复制路径**：卡片显示的是 `DisplayName`（可能是项目名），
未必等于磁盘名；`FileName` 才是磁盘上的那个。

### 20.3 验证

| 项 | 命令 | 结果 |
|---|---|---|
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead -v q --nologo` | **0 错误** |
| 新增测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead -- SearchSort` | **14 / 14 通过** |
| 截图 | `tools\shot.ps1 -Page library -Configuration Lead` | `library-sort.png`：数据源行右侧出现「排序 [相关度 ▾]」 |

`CoreMods_SearchSortTests` 覆盖：白名单内五个键原样保留、未知/越界/大小写错/注入形状的值回落默认、
`sort` 进 URL 且 facets 不变、恶意值不污染查询串。

### 20.4 未验证

- **右键菜单的渲染**。`ContextMenu` 只在右键时出现，截图模式不能点击，靠编译期绑定校验
  （`AvaloniaUseCompiledBindingsByDefault` 会拒绝不存在的属性名）与代码审查。
- **排序在真实网络下的结果差异**。只验证了 URL 构造与渲染；没有对比「相关度 vs 最新发布」两批
  真实结果是否真的不同（Modrinth 的排序在单关键词下差异很小，肉眼也难判）。

## 21. 第二十一轮（2026-10-22）：网络代理（无 / 系统 / 自定义 + 连通性测试）

设置页的「下载」分区新增「网络代理」卡片。此前全仓没有任何代理支持：`Proxy` 只出现在
`NavigationServiceProxy` 这类无关类型名里，而错误文案却在让用户「检查网络或代理后重试」——
启动器自己配不了代理。校园网 / 公司网关要求出网代理时用户只能改系统代理，
而系统代理对 .NET 是否生效取决于 `HttpClient.DefaultProxy`。

### 21.1 PCL2 对照：没有可移植的实现

**PCL2 没有 HTTP 代理设置。** 本轮与其它条目「对照后移植」不同，先说查证结论：

| 查证项 | 结果 |
|---|---|
| `Modules/Base/ModNet.vb` 全文搜 `Proxy` / `代理` / `WebProxy` | 0 匹配 |
| `Modules/Base/ModNet.vb:226` `NetRequestByClient` 的 handler 构造 | `New HttpClientHandler With {.AutomaticDecompression, .UseCookies}`，**没有** `.Proxy` |
| `Modules/Base/ModNet.vb:304` 多线程下载引擎的 `ThreadClient` | 同样只设 `.AutomaticDecompression` |
| 全仓 `*.vb` 搜 `NetSetDefaultProxy` / `SystemProxy` | 0 匹配（`Proxy` 仅命中 `ModCrash.vb:516` 的 `ChunkManager$ProxyTicketManager` 与 `PageLaunchRight.xaml.vb:92` 的 `gh-proxy.com` 域名） |

所以这轮是独立实现，不是移植。若要往 PCL2 的写法上靠，最近的插入点是
`ModNet.vb:226` 与 `:304` 的 `New HttpClientHandler With {...}` —— 那是 PCL2 里
「所有请求共用的 handler」，加一行 `.Proxy = ...` 即可；本仓库对应的是进程级
`HttpClient.DefaultProxy`（见 21.3）。

### 21.2 落点

| 位置 | 改动 |
|---|---|
| `Core/Models/Settings.cs:30` | `ProxyModes`（`none` / `system` / `custom`）常量类 |
| `Core/Models/Settings.cs:132` | `AppSettings.ProxyMode`（默认 `system`）、`ProxyAddress`、`ProxyUserName`、`ProxyPassword` |
| `Core/Models/Settings.cs:318` | `Clone()` 同步四个代理字段 |
| `Core/Settings/SettingsService.cs:310` | `NormalizeProxy`：未知模式回落 `system`；自定义但地址不可解析回落 `system`；无用户名时清掉密码；密码不 Trim |
| `Core/Settings/SettingsService.cs:290` | `ApplyNetworkProxy()`，由 `ReloadAsync` 的 3 条出口（:70 无文件 / :85 成功 / :100 损坏回退）与 `SaveAsync`（:125）各调一次 |
| `Core/Net/ProxyAddressParser.cs`（新） | 补 `http://`、只认 http/https、丢掉路径与查询串，统一成 `scheme://host[:port]` |
| `Core/Net/ProxyConfigurator.cs`（新） | `Build` / `Apply`：三种模式 → `IWebProxy` |
| `Core/Net/NetworkProbe.cs`（新） | 三个端点并发 GET，失败分 DNS / Refused / Timeout / ProxyAuth(407) / Tls / Http / Unknown |
| `App/ViewModels/SettingsViewModel.cs:1290` | 代理草稿属性、`SelectProxyModeCommand`、`TestProxyCommand`（:1373）、`ProxyProbeRow`（:2537） |
| `App/ViewModels/SettingsViewModel.cs:2058` | `ApplyFromSettings` 载入、`ApplyTo` 写回、`Validate` 拦截非法自定义地址 |
| `App/Views/SettingsView.axaml:908` | 下载分区末尾的「网络代理」`Border.card`：三张 option 卡 + 地址/用户名/密码 + 显示密码 + 测试按钮 + 结果列表（:981） |

两条容易做错的地方：

- **「无」必须返回一个显式的直连 `IWebProxy`，不能只是「不赋值」。** 不赋值等于继续跟随
  `HTTP_PROXY` / `HTTPS_PROXY` / IE 设置，而用户选「无」的语义恰恰是别管那些。
- **「系统」必须缓存 `HttpClient.DefaultProxy` 的启动值。** .NET 没有公开 API 重新构造
  `HttpEnvironmentProxy`（internal），只能在 `ProxyConfigurator` 类型初始化时抓一份留用；
  静态字段初始化先于任何方法调用，所以抓到的必定是未被本类改过的值。

### 21.3 `HttpClient.DefaultProxy` 的覆盖面

grep `new HttpClient(` / `new HttpClientHandler` / `SocketsHttpHandler` / `.Proxy =` 的结论：

| 客户端 | 显式设 `Proxy` | 被覆盖 |
|---|---|---|
| `Downloads/DownloadInfrastructure.cs:254` `MirrorRewritingHandler` | 否 | 是 |
| `Mods/ModrinthService.cs:1546` | 否 | 是 |
| `Mods/CurseForgeService.cs:1647` | 否 | 是 |
| `Mods/ModpackCatalogClient.cs:167` | 否 | 是 |
| `Mods/ModUpdateService.cs:81` | 否 | 是 |
| `Installing/NeoForgeInstallerBridge.cs` 里 CmlLib 自建客户端 | 由 CmlLib 内部构造 | 是（同进程 DefaultProxy） |

**没有漏网的 handler**：全仓不存在 `.Proxy =` 赋值，也没有 `UseProxy = false`。

### 21.4 生效时机（实测，不是推断）

`HttpClient.DefaultProxy` 在建立连接池时读取，所以：

- **已存在但还没发过请求的 `HttpClient`：立即生效。** 实测：先 `new HttpClient()`，
  再把 `DefaultProxy` 指向 `http://127.0.0.1:1`，请求 `meta.fabricmc.net` 立刻抛
  `HttpRequestException / 由于目标计算机积极拒绝，无法连接。(127.0.0.1:1)`。
- **已建好连接池的同一主机：旧连接继续用，不被打断。** 实测：同一个客户端先成功请求
  `meta.fabricmc.net`，再换代理，同一 URL 仍然 `HTTP 200`。

设置页因此只承诺「保存后对后续网络请求生效」，不承诺打断正在进行的那次下载。

### 21.5 验证

| 项 | 命令 | 结果 |
|---|---|---|
| Core 构建 | `dotnet build src\NebulaLauncher.Core\NebulaLauncher.Core.csproj -c proxy -v q --nologo` | **0 错误** |
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c proxy -v q --nologo` | **0 错误** |
| 新增测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c proxy -- CoreNet_ProxyTests` | **15 / 15 通过**（9 个 Fact/Theory 方法） |
| 全量回归 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c proxy` | **1348 / 1348 通过，0 失败** |
| 真实网络（system） | 一次性工程调 `NetworkProbe.RunAsync` | `Fabric Meta 200 / 621ms`、`BMCLAPI 200 / 202ms`、`Modrinth 200 / 908ms` |
| 真实网络（坏代理） | `custom http://127.0.0.1:1` | 三个端点全部 `Refused / ConnectionError / ~2.0s` |
| 截图 | `tools\shot.ps1 -Page settings -Section download -Configuration proxy -Theme dark/light -Scroll 520` | 卡片在深浅两套主题下都正常：三张模式卡片、「自定义」展开后三个输入框 + 显示密码 + 测试按钮 + 底部说明 |

`CoreNet_ProxyTests` 覆盖：默认值、未知模式回落、自定义无可用地址回落（含 `socks5://` 与乱码）、
地址规范化（补 scheme / 去尾斜杠 / 去路径）、凭据保留与「只有密码时清空」、`Clone` 不丢字段、
模式=无时 `DefaultProxy.IsBypassed` 为真、自定义模式的 `WebProxy.Address` 与 `NetworkCredential`、
以及 `NetworkProbe` 对 `NameResolutionError` 的分类。全部不联网。

### 21.6 未验证

- **点击「测试连通性」后界面结果列表的渲染。** 截图模式不能点击，只验证了
  `NetworkProbe` 的命令行输出与编译期绑定（`ProxyProbeResults` 的 `x:DataType` 缺失会直接编译失败）。
- **407 与 TLS 两条失败分支的真实触发。** 假 handler 只覆盖了 DNS 分类；本地没有会返回 407 的
  HTTP 代理，只能靠 `HttpRequestError.ProxyTunnelError → ProxyAuth`、`SecureConnectionError → Tls` 的映射。
- **`system` 模式在「Windows 系统代理开启」环境下的实际穿透。** 本机系统代理未开启，
  该路径与「不设置 DefaultProxy」的行为等价，无法区分。
- **Chromium / 游戏进程是否走这个代理。** 只覆盖启动器自身的 `HttpClient`；「打开网页授权」
  走系统浏览器，不受此项控制。

## 22. 第二十二轮（2026-10-22）：MCBBS 整合包 addons 逐项反查

上一轮把整合包导入做成了闭环，但 MCBBS 包的 `addons` 只有「项目 id + 文件 id」，没有直链——
`overrides/` 解压完成后，`addons` 里的模组一个都不会被下载，玩家装完发现「一个 mod 都没有」。
本节补的是这一条链路。

### 22.1 PCL2 出处

| 判据 | PCL2 位置 | 说明 |
|---|---|---|
| 读哪个文件 | `ModModpack.vb:682` | `If(GetEntry(packmeta), GetEntry(manifest.json))`：`mcbbs.packmeta` 优先，缺失回落同目录 `manifest.json` |
| `addons` 的形态 | `ModModpack.vb:714-716` | `For Each Entry In Json("addons")` + `Entry("id")` / `Entry("version")`：数组，每项是对象 |
| 哪些 id 不是内容 | `ModModpack.vb:717-725` | `game` / `forge` / `neoforge` / `fabric` / `quilt` / `optifine` 走加载器安装，不是可下载模组 |
| CurseForge `files[]` | `ModModpack.vb:162-360` `InstallPackCurseForge` | 字段名 `projectID` / `fileID` / `fileName`；MCBBS 的导出工具把同一份内容写成两种键名都见过 |
| 加载器版本来源 | `ModModpack.vb:738` | `New McComponents(Addons("game"), ModLoaderVersions, OptiFineEntry)` |

### 22.2 解析（`McbbsAddonParser`，`Backups/ModpackService.cs` 末尾）

`internal static class McbbsAddonParser`，纯函数：`JsonElement` 进、`ModpackAddonReference` 出。

- **容器形态**：`addons` / `files` 都认；数组与字典（`{"jei": "15.2.0.27"}`）都认。
- **键名宽容**：项目 id 依次尝试 `projectID` / `projectId` / `project_id` / `modId` / `addonId` / `slug` / `id`；
  文件 id 依次尝试 `fileID` / `fileId` / `versionId` / `version`；文件名 `fileName` / `filename`；
  落点 `path` / `target` / `dest`。数字与字符串都接受（`GetRawText()`）。
- **来源前缀**：`curseforge:238222` / `modrinth:abc123` 会拆出 `Source`，不做前缀猜。
- **跳过项**：加载器 id（`game` / `forge` / `fabric` / `neoforge` / `quilt` / `optifine` / `liteloader`）、
  没有项目标识的条目、非对象的数组元素。
- **内容类型**：`type` / `kind` 优先；没声明就看 `path` 前缀（`resourcepacks/` / `shaderpacks/` / `datapacks/` / `saves/`），
  再退化为「模组」。

### 22.3 反查与下载（`ModpackService.ImportMcbbsAddonsAsync` / `ResolveAddonAsync`）

逐项按三步尝试，任一步拿到可直连地址就停：

1. **CurseForge 元数据端点**：`ICurseForgeService.GetFilesAsync(modId, mcVersion, loader)`，按 `fileID` 匹配。
   能拿到权威文件名、体积与 SHA1。**key 已内置，无需用户配置**；网络失败 / 限流 / key 被拒时返回空集。
2. **forgecdn 直链推导**（不需要 key）：`addons` 自带 `fileID` + `fileName` 时用
   `CurseForgeCdn.BuildUrl(fileId, fileName)`（`Mods/CurseForgeService.cs:1749` 已有实现与实测记录）。
   没有这一步，官方接口失败时 MCBBS 包基本等于「全部进待手动安装」。
3. **Modrinth**：先按项目 id / slug 直接 `GetVersionsAsync`，`fileID` 用于匹配版本 id；
   查不到再 `SearchAsync` 兜底（addons 里的 id 有时是旧 slug 或项目名）。

不新建 `HttpClient`：反查全部走 `ICurseForgeService` / `IModrinthService`，下载全部走 `DownloadQueue`。
`ModpackService` 构造函数新增可选参数 `ICurseForgeService? curseForge = null`，DI 已在
`AddCoreContent()` 里注册 `ICurseForgeService`，组合根无需改动。

**落点**：`addons[].path` / `target` 优先；缺失时按内容类型进 `mods/`、`resourcepacks/`、`shaderpacks/`、`datapacks/`、`saves/`。
文件名优先用 `addons[].fileName`，缺失时用远程文件名（CurseForge 的 `FileName` / Modrinth 的 `FileName`）。
覆盖层已经有同名文件时**直接跳过下载**：作者在 `overrides/` 里放的改版优先于在线原件。

**失败容忍**：任何一项反查不到或下载失败只进 `ModpackImportResult.PendingAddons`，
`Success` 仍为 `true`，`instance.json` 照常写出。队列层面整体失败（例如安装批次占用）也只在
`Warning` 记一条并把这一批全算进清单，不让导入失败。

### 22.4 结果契约（只追加字段，不改既有字段）

`Models/Content.cs`：

| 类型 | 字段 | 用途 |
|---|---|---|
| `ModpackAddonReference` | `ProjectId` / `FileId` / `FileName` / `TargetPath` / `Kind` / `Source` | 一条 addons 引用的解析结果 |
| `ModpackPendingAddon` | `Name` / `ProjectId` / `FileId` / `Reason` / `WebUrl` | 「待手动安装」一条（含网页入口） |
| `ModpackImportResult` | `AddonsTotal` / `AddonsDownloaded` / `PendingAddons` / `AddonsPending` / `HasPendingAddons` / `PendingAddonsPreview` | 导入结果里的 addons 部分 |

### 22.5 界面

`WorldsViewModel`：

- `PendingAddons`（`ObservableCollection<ModpackPendingAddon>`）+ `HasPendingAddons`。
- 导入完成文案追加「；还有 N 个 mod 需要手动安装（前三条名字）」。
- `TogglePendingAddonsCommand`（查看 / 收起清单）、`OpenPendingAddonUrlCommand`（浏览器打开该条目的项目/文件页）。

`WorldsView.axaml`：导入完成卡片右侧加「查看清单」按钮（`HasPendingAddons` 才显示），
下方 `Border.sunken` 展开清单，每行「文件名 + 原因 + 打开页面」。
`--page worlds --section imported` 是截图专用分支（`WorldsViewModel.ApplyScreenshotImportResult`），
与 `ModsViewModel.ApplyScreenshotUpdates` 同一约定：只在 `ScreenshotMode.IsActive` 下生效。

### 22.6 验证

| 项 | 命令 | 结果 |
|---|---|---|
| Core 构建 | `dotnet build src\NebulaLauncher.Core\NebulaLauncher.Core.csproj -c mcbbs -v q --nologo` | **0 错误** |
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c mcbbs -v q --nologo` | **0 错误** |
| 新增测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c mcbbs -- McbbsAddon` | **9 / 9 通过**（4 个 Fact + 1 个 Theory 的 4 组数据） |
| 全量回归 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c mcbbs` | **1348 / 1348 通过，0 失败** |
| 截图 | `tools\shot.ps1 -Page worlds -Section imported -Theme dark -Size 1024x660 -Configuration mcbbs` | 导入完成卡片 + 三行清单 + 每行「打开页面」按钮正常渲染 |

`CoreAssets_McbbsAddonTests` 覆盖：字段缺失 / 未知格式（4 组 Theory）、单条 addon 的键名与数字 id、
多条里跳过加载器与无标识条目（含 `modrinth:` 前缀与按 `path` 推内容类型）、
无 `fileName` 时回退到 CurseForge 远程文件名、两家都查不到时全部进清单且导入仍成功、
CurseForge 查不到时 Modrinth 兜底落盘。网络全部是接口替身 + 固定内容 `HttpMessageHandler`，不联网。

### 22.7 未验证

- **真实 MCBBS 包的 `addons` 是否真能下到。** 手上没有真实样本，测试也不联网；
  解析层只按 PCL2 的读法与公开的 `files[]` 结构复刻，字段名用候选集兜住。
  `mcbbs.packmeta` 没有正式 schema，若某个包的键名不在这份候选集里，该项会进「待手动安装」而不是报错。
- **forgecdn 直链在「CurseForge 强制 API Key」之后是否仍可下载。** `Mods/CurseForgeService.cs:1746`
  的记录是「2026-07 起无 key 返回 401」，本机未做真实请求验证；失效时该步会自动落到第 3 步（Modrinth）或进清单。
- **`addons[].path` 指向 `saves/` 或 `datapacks/` 的真实样本。** 目前只有单测构造的样例，
  没有见过把存档当 addon 分发的包。

---

## 23. 第二十三轮（2026-10-22）：崩溃建议的动作按钮、页面级取消源释放

前一件事是把一根早就该接的线接上；后一件是还掉一项系统性技术债。

### 23.1 崩溃建议的动作按钮

**PCL2 出处**：`Modules/Minecraft/ModCrash.vb:340` 的 `Private Enum CrashReason`（25 个原因：
`Java虚拟机参数有误` / `MixinBootstrap缺失` / `内存不足` / `使用JDK` /
`Nvidia驱动不兼容导致EXCEPTION_ACCESS_VIOLATION` …）与 `:447` 的汇总（`For Each Reason In CrashReasons`）。
**PCL2 只输出原因文案，结果里不带任何可执行动作**——玩家看完还得自己去对应页面翻。
所以本节不是 port，而是把本项目自研的 `ActionId` 真正接到界面上。

**原状**：`CrashAnalyzer.Actions` 定义了 5 个动作标识（`OpenJavaSettings` / `OpenModsFolder` /
`RepairVersion` / `OpenCrashReport` / `OpenLogFolder`），`CrashRules` 的 75 条规则里有相当一部分填了值，
`CrashExportService` 还会把标识写进导出文本。但 App 侧**一次都没读过** `ActionId`——
玩家看到「去 Java 设置里换 17」，界面上没有任何一步能直接执行。

**改法**：

| 位置 | 改动 |
|---|---|
| `Core/Launching/CrashAnalyzer.cs:51` | `Actions` 新增 `DisableMod` |
| `Core/Launching/CrashAnalyzer.cs:964` | 归因到**唯一**一个 jar 时给 `DisableMod` + 文件名，多个候选仍给 `OpenModsFolder` |
| `Core/Models/Launching.cs:210` | `CrashSuggestion` 追加 `ActionArgument`（默认 null，位置参数，向后兼容） |
| `App/ViewModels/CrashSuggestionItem.cs`（新建） | 包装建议：中文标签、`HasAction`、`RunCommand`、执行期间禁点 |
| `App/ViewModels/HomeViewModel.cs` | 集合类型换成包装；`RunCrashActionAsync` 把六种动作翻译成导航 / 开目录 / 修版本 / 禁用模组 |
| `App/Views/HomeView.axaml:312/392` | 两处建议模板（本机崩溃 + 导入报告）加严重度竖条与动作按钮 |

**为什么 `DisableMod` 只在候选唯一时出现**：二分法一次只改一个变量。一次禁用五个，
等于把玩家的模组包砸掉，而且下一次崩溃会更难判断——这条判据写进了单测
（`Analyze_归因到多个肇事文件_不给DisableMod动作`）。

**为什么需要 `ActionArgument`**：建议本身是纯文本，界面上无法从文案反推要动哪个 jar。
枚举 + 关联合法的 `CrashSuggestionItem` 加一个字段是最小改法。

**为什么加包装类型而不是直接把命令挂到 `CrashSuggestion` 上**：Core 的模型不该知道 MVVM。
包装层同时承担「没有可执行动作就不显示按钮」的职责（`HasAction`）。

### 23.2 页面级取消源释放

**原状**：`AccountsViewModel` / `DownloadViewModel` / `HomeViewModel` / `SettingsViewModel`
各持有一个 `CancellationTokenSource`，每次重开流程都 `Cancel()` 后直接换新对象，旧对象从不 `Dispose()`。
`CA1001` 报了其中 6 个类型（含本轮未动的 `ServersViewModel` / `ModsViewModel`，它们在并行任务的文件域里）。

**改法**：`PageViewModelBase` 实现 `IDisposable` + `protected virtual void Dispose(bool)`，
四个页面覆写并 `Cancel → Dispose → 置 null`。

**为什么置 null**：`OnNavigatedFromAsync` 里还有 `_javaCts?.Cancel()` 这类调用。
释放后置 null，之后就是空短路，不会撞 `ObjectDisposedException`。

**为什么不在 `OnNavigatedFromAsync` 里释放**：页面是 DI 单例，切回来还要用。
释放点只有一个——容器释放（进程退出）。

### 23.3 验证

| 项 | 命令 | 结果 |
|---|---|---|
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead -v q --nologo` | **0 错误**；`CA1001` 从 6 条降到 2 条（剩下两条在并行任务的文件域） |
| 新增测试 | `TestRunner -- CrashRule` | **24 / 24** |
| 全量测试 | `TestRunner（全量）` | **1351 / 1351** |
| 截图 | `shot.ps1 -Page home -Section crash -Size 1100x760` | 崩溃面板：红色严重度竖条 + 「打开模组目录」按钮 |

`CoreLaunch_CrashRuleTests` 新增两例：单文件归因给 `DisableMod` + 文件名 + 文案含「可以直接禁用」；
多候选只给 `OpenModsFolder` 且 `ActionArgument` 为 null。

### 23.4 未验证

- **六个动作里只有「打开模组目录」与「禁用模组」在截图里看得到按钮**。其余四个
  （`OpenJavaSettings` / `RepairVersion` / `OpenCrashReport` / `OpenLogFolder`）靠编译期绑定校验与代码审查。
- **`RepairVersion` 动作真的点下去会修一次**：走 `IInstallService.RepairAsync(Quick)`，
  该链路本身有测试覆盖，但「崩溃面板 → 修复」这条组合路径没有实际点击验证。
- **`DisableMod` 在真实崩溃后的表现**：单测覆盖了「归因给出动作」，没有跑过「真的把某个 jar 改名后重启」。

## 24. 服务器 Query 协议：玩家名单与完整状态

### 24.1 背景

**PCL2 没有这个功能，这是纯扩展**，不需要去找出处。

服务器页原本只有 Server List Ping（TCP）：MOTD、在线人数（`online` / `max`）、延迟、图标。
SLP 的 `players.sample` 大多数服务器不填，「在线 3/20」点进去看不到是谁。
GameSpy4 Query（UDP 25565 同端口）能拿到完整玩家名单，作为 SLP 之外的第二条通道。

### 24.2 协议

```
C -> S : FE FD 09 00 00 00 00                      （handshake）
S -> C : 09 <challenge int32 BE>
C -> S : FE FD 00 <challenge int32 BE> 00 00 00 00 （full stat）
S -> C : 00 <session int32> <payload>               （可能拆成多个 UDP 包）
```

`payload`：`splitnum\0\x80\0` + 若干 `key\0value\0` + `\0` + `\x01player_\0\0` + 若干 `玩家名\0` + `\0`。
键为 `hostname` / `gametype` / `game_id` / `version` / `plugins` / `map` / `numplayers` /
`maxplayers` / `hostport` / `hostip`。

分片：响应超过一个 MTU 时被拆包。每包前 5 字节（type + session id）重复；
首字节低 4 位是包序号（0–3），`0x08` 位表示末包。拼装时按序号放置、去掉 5 字节头后拼接。
单包响应通常没有末包标记，用 300ms 的收尾窗口判断「没有下一片」。

### 24.3 实现

| 文件 | 内容 |
|---|---|
| `Core/Servers/ServerQueryService.cs` | 新建。UDP 交互 + 包构造 + 分片拼装 + payload 解析；解析函数全部 `internal static`，测试直接喂 `byte[]` |
| `Core/Abstractions/ServerServices.cs` | 追加 `IServerQueryService`，带默认实现（返回 `Unsupported`） |
| `Core/Models/Servers.cs` | 追加 `ServerQueryOutcome` / `ServerQueryPlayer` / `ServerQueryResult`；未改任何现有成员 |
| `Core/Servers/ServerServiceCollectionExtensions.cs` | 在 `AddCoreServers` 里注册 `IServerQueryService` |
| `App/ViewModels/ServersViewModel.cs` | `QueryPlayersCommand` + 卡片上的名单状态；`--section players` 截图分支 |
| `App/Views/ServersView.axaml` | 「玩家」按钮（`Button.pill ghost`）+ `Border.sunken` 名单面板（`Border.badge` 列名字） |

**为什么没有把 `QueryPlayersAsync` 加进 `IServerService`**：`IServerService` 定义在
`Core/Abstractions/ContentServices.cs`，该文件不在本任务的文件域内且处于并行改动窗口。
改为独立契约 `IServerQueryService` + DI 注册，接口语义更清楚（TCP 探测 / UDP 查询两条通道），
也不影响 `ServerService` 的任何现有实现方。

**失败分类**：`QueryDisabled`（UDP 端口没有监听，收到 ICMP 端口不可达）/ `Timeout`（超时无响应，
多数关闭了 `enable-query` 的服务器表现为这个）/ `ProtocolMismatch`（端口上跑的不是 Query）/
`NoResponse`（解析失败或网络不可达）/ `Unsupported`（接口缺省实现）。
`IsQueryUnavailable` 把前三种归为「看不到名单」而不是错误。

**默认参数**：超时 2 秒、尝试 2 次（含首次），只发单播 UDP，不写任何文件。

### 24.4 界面

- 每张卡片「人数」旁一个「玩家」按钮，**点了才查这一台**；不做后台轮询、不做整表批量查询
  （Query 无认证，短时大量发包会被判定为扫描）。
- 结果在卡片下方 `Border.sunken` 展开：玩家名用 `Border.badge` 排列，
  标题「在线玩家 · N」；空名单显示「当前没有玩家在线」或「服务器报告 N 人在线，但没有返回玩家名单」。
- Query 关闭 / 超时显示「该服务器未开启 Query，看不到玩家名单」，**不写进卡片错误行、不改在线状态**。
- 颜色全部 `{DynamicResource}`，未新增键（复用现有 43 对）。

### 24.5 验证

| 项 | 命令 | 结果 |
|---|---|---|
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c query -v q --nologo` | **0 错误** |
| 新增测试 | `TestRunner -- CoreServers_Query -v` | **8 / 8 通过** |
| 真实网络 | 临时用例查询 `mc.hypixel.net` | `Outcome=Timeout Available=False Elapsed=2115ms`（与预期一致：不开放 Query），临时用例已删 |
| 截图 | `shot.ps1 -Page servers -Section players -Theme dark -Size 1024x660 -Configuration query` | 第一张卡「在线玩家 · 6」+ 六枚名字徽标；第二张卡显示「该服务器未开启 Query，看不到玩家名单」 |

`CoreServers_QueryTests` 覆盖：挑战包构造、full stat 请求（大端 challenge）与 challenge 响应校验、
乱序多包拼装 + 玩家名单解析、缺片拒绝、单包字段解析、本地 UDP 假服务器端到端、
「无响应归为看不到名单」、协议不符分类。

### 24.6 未验证

- **真实开放 Query 的服务器没有找到**。本机只能确认 `mc.hypixel.net` 不响应，
  「拿到真实玩家名单」这条路径只有本地假服务器端到端用例覆盖。
- **超长响应（>1460 字节）的多片真实报文**：分片逻辑用构造的 2 片数据验证，没有真实大响应样本。
- **`hostname` 的 UTF-8 / Latin-1 差异**：Vanilla 会把非 ASCII 替换为 `?`，但第三方插件实现可能直接发
  Latin-1，当前统一按 UTF-8 解码，遇到这类服务器会显示乱码。

---

## 25. 存档 level.dat 编辑（纯扩展，PCL2 无对应功能）

已核对：PCL2 的 `Pages` 目录下没有 `PageInstanceWorld` / `PageWorld`，**它没有任何存档编辑界面**。
这一节记录的不是移植，而是「PCL2 做不到、Nebula 补上」的能力，以及为什么这么实现。

### 25.1 为什么只做这几项

玩家真正会改的只有：种子（整活 / 复现地图）、游戏模式、难度、作弊开关、
「保留物品栏」（死亡掉光装备的挫败感）。其余字段（`DataVersion`、`SpawnX`、`Time`、`DayTime`）
改错会直接让存档进不去，界面不提供入口。硬核标记只在读取侧展示，写侧留给调用方显式指定。

### 25.2 NBT 写回策略

`NbtReader` 从「只取几个字段、其余跳过」改成**把整棵标签树解析出来**：
`NbtCompound` 用有序 `List` 存「名字 → 标签」而不是 `Dictionary`（NBT 不禁止重名，字典会静默丢标签，
丢标签就是写坏存档）。写回时按原类型、原顺序重放，**不认识的模组标签也原样留在文件里**。
解析失败（非法长度、嵌套过深、未知标签类型）直接抛异常，由调用方拒绝写入，绝不产出半棵树。

| 风险 | 做法 |
|---|---|
| 写坏未知标签 | 整棵树无损重放；只改 `WorldEditRequest` 点名的字段 |
| 压缩形态被改 | `NbtDocument.IsGzip` 跟随源文件：gzip 写回还是 gzip，裸 NBT 写回还是裸 NBT |
| 写入中途断电 | 先写 `level.dat.nebula.tmp`，再 `File.Move(..., overwrite: true)` 替换；失败即删临时文件 |
| 玩家改错想回滚 | 每次写入前把原文件复制成同目录的 `level.dat.nebula.bak`（覆盖旧的一份）；**备份失败直接中止写入** |
| 写正在运行的世界 | `session.lock` 拿不到独占访问就拒绝。判据是双重的：`FileShare.None` 独占打开 + 对首字节 `FileStream.Lock(0,1)`（Java 侧 `FileChannel.lock()` 锁整个文件）。文案明说「请先退出游戏再修改存档」 |

`GameRules` 里每条规则都是 `TAG_String`（`"true"` / `"false"`），不是 `TAG_Byte`；
`GameRules` 复合不存在时新建，已有规则一条不动。`RandomSeed` 统一写 `TAG_Long`（可负数、可超 `int` 范围）。

### 25.3 代码

| 文件 | 内容 |
|---|---|
| `Core/Backups/NbtReader.cs` | 标签树（`NbtTag` / `NbtCompound` / `NbtList` / `NbtDocument`）+ 完整解析 + 摘要（`Seed` / `Difficulty` / `AllowCommands` / `KeepInventory` / `Hardcore`） |
| `Core/Backups/NbtWriter.cs`（新增） | 大端序列化，gzip 跟随 `NbtDocument.IsGzip`；空列表写 `TAG_End` 元素类型 |
| `Core/Backups/BackupService.cs` | `UpdateWorldAsync`：占用检测 → 解析 → 改字段 → `.bak` → 临时文件替换 |
| `Core/Abstractions/ContentServices.cs` | `IBackupService.UpdateWorldAsync` 带默认实现（DIM），既有实现零改动 |
| `Core/Models/Content.cs` | `WorldInfo` 追加 `GameType` / `Seed` / `Difficulty` / `AllowCommands` / `KeepInventory` / `Hardcore`；新增 `WorldEditRequest` / `WorldEditResult` |
| `App/ViewModels/WorldsViewModel.cs` | 编辑面板状态 + `OpenWorldEditor` / `SaveWorldEdit` / `CancelWorldEdit` |
| `App/Views/WorldsView.axaml` | 每张世界卡加「编辑」；面板含世界名 / 种子 / 模式 / 难度 / 作弊 / 保留物品栏 + 固定备份说明 |

`WorldEditRequest` 的字段全部可空：只改种子时不会顺带把难度、作弊、世界名重写一遍——
每一项都是一次写入风险。

### 25.4 界面

- 世界卡「备份 / 打开 / **编辑** / 删除」四个 `Button.pill`，点「编辑」后在列表上方展开
  `Border.card accentEdge` 编辑面板（与「导出整合包」「整合包预览」同一位置约定）。
- 表单：世界名 `TextBox`、种子 `TextBox.mono`、游戏模式 `ComboBox`、难度 `ComboBox`、
  作弊与保留物品栏两个 `ToggleSwitch`。
- 面板底部固定一行「改动前会自动备份为 level.dat.nebula.bak」+ `Button.accent` 保存 / `Button.ghost` 取消。
- 颜色全部 `{DynamicResource}`，未新增令牌（深/浅两套字典键集未变）。
- `--page worlds --section editor` 是截图专用分支（`WorldsViewModel.ApplyScreenshotEditor`），
  与 `ApplyScreenshotImportResult` 同一约定：只在 `ScreenshotMode.IsActive` 下生效。

### 25.5 验证

| 项 | 命令 | 结果 |
|---|---|---|
| Core 构建 | `dotnet build src\NebulaLauncher.Core\NebulaLauncher.Core.csproj -c world -v q --nologo` | **0 错误** |
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c world -v q --nologo` | **0 错误** |
| 新增测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c world -- LevelDat -v` | **8 / 8 通过** |
| 全量回归 | 同上不带过滤 | **1362 / 1367 通过，5 失败**；失败全部落在 `CoreDownloads_RepairTests`，原因是本机磁盘只剩 525 MB（修复流程自检「建议至少 1.0 GB」直接中断），与存档编辑无关 |
| 截图（深色） | `shot.ps1 -Page worlds -Section editor -Theme dark -Size 1240x1080 -Configuration world -DataDir .probe\data-world` | 面板读出「示例世界 / 种子 1234567890123 / 生存 / 普通」；卡片版本徽标 `1.20.1` |
| 截图（浅色） | 同上 `-Theme light` | 同一布局，文字可读，无新增令牌 |

`CoreAssets_LevelDatTests` 覆盖：gzip 与非 gzip 往返（种子、模式、难度、作弊、保留物品栏、世界名）、
只改种子时未知模组标签与版本信息保留、`GameRules` 其它规则不被吞掉、`.bak` 内容等于改动前、
`session.lock` 被占用时拒绝写入且文件不变、种子取 `long.MaxValue - 1` / `long.MinValue`、
缺 `Data` 段时拒绝写入。NBT 字节由测试里手写的构造器生成（**不复用 `NbtWriter`**，避免「自己写、自己读」把错误掩盖掉）。

### 25.6 未验证

- **真实存档样本**：本机 `%APPDATA%\.minecraft` 下没有 `saves` 目录，往返验证全部基于构造的 level.dat
  （字段结构与真实存档同构：`Data.Version` / `Data.GameRules` / `Data.RandomSeed` / `Data.allowCommands` 等）。
- **Windows 真机锁语义**：`session.lock` 的检测用 `FileShare.None` + `FileStream.Lock(0,1)` 双判据，
  单测用独占句柄模拟占用；没有在真正运行的 Minecraft 客户端上验证过（需要启动一次游戏）。
- **macOS / Linux**：`FileStream.Lock` 在非 Windows 抛 `PlatformNotSupportedException`，代码里忽略并退化为独占打开；未在非 Windows 上跑过。
- **超大 level.dat**（含巨量 `Data.BlockStates`）：除长度上限（16 MB / 条目）外未做流式优化，未用真实大存档测过内存占用。

---

## 26. 下载韧性两项：Modrinth 镜像兜底 + 只读目录覆盖

来源：HMCL `HMCLDownloadProvider.java:123-150`（`fallback=true` 的四条规则，其中 Modrinth 两条）与
`GameDirectoryManager.java:forceOverwriteGameDirectoriesIfReadOnly`（437-447 行）/ `forceOverwriteGameDirectoryFiles`
（309-316、357-364 行）。规则整理见 `docs/EXTENSIONS.md` 第 21、22 条。PCL2 侧没有对应实现（`PCL2` 的镜像表只覆盖
Mojang 与 Forge 系，且不处理只读目录），这两项是纯扩展。

### 26.1 Modrinth 镜像兜底

**判据只有一条：镜像只在官方「连不上」时用。** 官方 404 是「这个项目/文件不存在」，此时换镜像
只会让每次查不存在的项目都多等一轮超时（搜索 15 秒 × 重试），所以 404 / 403 直接失败。

- 规则表在 `Core/Downloads/DownloadInfrastructure.cs` 的 `ModrinthFallbackMap`：
  `https://api.modrinth.com/` → `https://mod.mcimirror.top/modrinth/`、
  `https://cdn.modrinth.com/` → `https://mod.mcimirror.top/`。
  **两条前缀不同**（API 多一段 `/modrinth`），把官方域名压成同一个前缀会把 API 路径拼错——
  与 NeoForge `BmclApiMap` 踩过的坑同源（`HANDOVER.md` §6.33）。
- `DownloadMirror.ModrinthCandidates(url, useModrinthMirror)` 是**唯一**的候选来源，顺序恒为
  「官方 → 镜像」，没有「镜像优先」这一态（和 `Candidates(url, source)` 的三态语义刻意分开）。
  开关关闭时返回单元素表，任何代码路径都拿不到镜像 URL。
- `Mods/ModrinthService.cs` 的三条请求路径都走候选表：`GetStringAsync`（搜索 / 版本 / 详情）、
  `OpenDownloadAsync`（文件下载与图标）。
  - 官方候选内层原来就有「429 / 5xx / 超时 / 网络异常各重试一次」；
  - 镜像候选**只试一轮**，不再重试：官方已经等过 15 秒 + 重试，镜像再重试会让一次搜索拖到一分钟以上；
  - 5xx / 429 / 网络异常 / 超时 → 换镜像；404 / 403 等其他 4xx → 立即失败（`FetchOutcome.Missing`）。
- 开关 `AppSettings.UseModrinthMirror`，**默认关**（第三方服务，不默认把查询内容发出去）。
  默认值的三处证据：属性无初始值（`bool` 默认 `false`）、旧 `settings.json` 缺该字段时反序列化即 `false`、
  单测 `设置克隆_镜像开关默认关且克隆保留取值` 断言 `new AppSettings().UseModrinthMirror == false`。
- `AppSettings.Clone()` 显式复制该字段（`Models/Settings.cs`），`SettingsService.Normalize` 里按约定列了一条
  说明（布尔无区间可夹，缺字段即 `false`）。设置页把它放在「下载」区的「下载源」卡片之后：
  `ToggleSwitch` + 一行说明「镜像来自第三方（mod.mcimirror.top），仅在 Modrinth 官方直连失败时使用。」

### 26.2 只读游戏目录覆盖

- `Core/Paths/PathProbe.cs` 新增 `public static string? TryMakeWritable(string directory)`：
  清 `FileAttributes.ReadOnly`，只处理**目录本身与一级文件**（递归扫 `assets/` 那种几万文件的目录代价太高），
  成功返回 `null`，失败返回中文原因。**只在 Windows 启用**：Unix 上只读位是权限位
  （`FileAttributes.ReadOnly` 在 .NET 里表现为去掉 owner 写位），语义不同，不跨平台照搬。
- `InstallService.EnsureInstallEnvironment` 两处：
  1. 进入时游戏目录本身带只读属性就清一次（`TryClearReadOnlyMinecraftDirectory`），成功记
     「游戏目录带只读属性，已清除只读属性」；
  2. 建版本/库/资源目录失败且异常是 `UnauthorizedAccessException` / `IOException` 时，再清一次并重试一次
     （只给一次机会，不给受控目录反复试探的时间）。清不掉时错误文案带「已尝试清除只读属性，未成功：<原因>」。
- 覆盖前备份：`InstallService.BackupBeforeOverwrite(path)` 把旧文件原地复制成 `<name>.bak`
  （**只保留一份，不累积**），并在 Windows 上去掉目标文件的只读位（只读文件直接覆盖会抛 `UnauthorizedAccessException`）。
  已接入两处覆盖点：`versions/<id>/<id>.json`（profile JSON 重装/覆盖）与 `versions/<id>/nebula-loader.json`
  （加载器标记重写）。备份失败只记日志、不阻断写入。

### 26.3 验证

| 项 | 命令 | 结果 |
|---|---|---|
| App 构建 | `dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c mirror -v q --nologo` | **0 错误 0 警告**（本轮新增代码；测试项目既有 `CA1822` 与本轮无关） |
| 新增测试 | `dotnet run --project tools\TestRunner\TestRunner.csproj -c mirror -- CoreDownloads_MirrorFallback -v` | **9 / 9 通过** |
| 全量回归 | 同上不带过滤 | **1400 / 1400 通过，0 失败** |
| 截图（深色） | `shot.ps1 -Page settings -Section download -Theme dark -Size 1024x660 -Configuration mirror -DataDir .probe\data-mirror` | 「Modrinth 镜像兜底」卡片在下载源下方，开关显示「关闭」 |
| 截图（浅色） | 同上 `-Theme light` | 同一布局，说明文字可读，未新增令牌（深/浅字典键集均未变） |

`CoreDownloads_MirrorFallbackTests` 的 9 例覆盖：候选顺序（API 与 CDN 两条前缀各自正确）、开关关闭时只有官方一项、
**官方 200 不碰镜像**、**官方 404 不碰镜像且 `LastError` 带 404**、官方超时换镜像并成功、官方 5xx 换镜像并成功、
开关关闭时官方 5xx 也不碰镜像、`Clone()` 保留开关且默认 `false`、只读目录清属性后可写（含清不掉时返回中文原因）、
`.bak` 内容为旧值且不累积。

### 26.4 真实网络实测（2026-10-22）

| 场景 | 结果 |
|---|---|
| Modrinth 官方搜索（开关关） | `hits=3 elapsed=998ms LastError=<null>`，命中 sodium / sodium-extra / reeses-sodium-options |
| Modrinth 官方搜索（开关开） | `hits=3 elapsed=1069ms LastError=<null>` |
| 官方不可达时镜像是否真被使用 | 本机官方直连正常（约 1 秒），**镜像未被触发**——这符合设计（只在失败后才用），但也意味着真实链路的镜像成功路径没有被实测覆盖 |
| 镜像 API `https://mod.mcimirror.top/modrinth/v2/search?...` | **HTTP 503 Service Temporarily Unavailable**（直接请求，非启动器内） |
| 镜像 CDN `https://mod.mcimirror.top/data/<project>/<icon>.webp` | HTTP 200，但 `Content-Type: text/html`、2245 字节（不是图片）——该前缀当前未正常回源 |

结论：本机网络下 Modrinth 官方可用，镜像兜底处于「备用但未启用」状态；镜像主机本身当前返回 503，
即使打开开关，在本机也只能看到「官方与镜像都无法连接」。这与风险预期一致（`mod.mcimirror.top`
是第三方服务，可用性不由本项目控制），UI 文案已覆盖这种情况。

### 26.5 未验证

- **镜像成功链路**：本机官方可达，`官方失败 → 镜像成功` 只有假 handler 的单测覆盖，没有真实镜像返回 200 的样本。
- **`options.txt` 的备份**：`options.txt` 的写入点在 `Launching`/`GameLauncher`（不在本轮文件域），
  本轮只对 `InstallService` 里的 `versions/<id>/<id>.json` 与 `nebula-loader.json` 接了
  `BackupBeforeOverwrite`。**`options.txt` 覆盖前的 `.bak` 尚未接**。
- **受组策略 / OneDrive 占位文件**：清只读属性会失败，代码路径已写（错误文案带「已尝试清除只读属性」），
  但没有在真实受控目录上跑过；只用「不存在的目录」模拟了「清不掉」的返回值。
- **非 Windows**：`TryMakeWritable` 在非 Windows 直接返回「只有 Windows 才支持自动清除只读属性」，
  未在 Linux/macOS 上跑过（该功能本身也只为 Windows 设计）。
- **镜像的路径规则**：只按 HMCL 的两条前缀做前缀替换，未核对 `mod.mcimirror.top` 是否需要额外的
  `/v2` 或 `/api` 段（当前直接照抄 HMCL 规则，503 使其无法验证）。

---

## 27. 第二十七轮（2026-10-22）：实例原地换加载器（GAP：实例维度缺换版本入口）

### 27.1 为什么需要

PCL2 的做法是「版本」就是一等公民：想给 1.20.1 加装 Fabric，就去版本页装一个
`fabric-loader-x-1.20.1`，再把 mods 目录指过去。NebulaLauncher 的实例把这件事变难了：
实例绑定的是**一个版本目录名**（`InstanceInfo.VersionName`），换版本意味着改这个字段，
但界面只提供「克隆实例」「删了重建」——两条路都要用户自己搬 mods / saves。

真实诉求是一句话：「我这个 1.20.1 实例，想加装 Fabric，mods 和存档都别动」。

### 27.2 实现方式

改的只有 `InstanceInfo.VersionName`（外加同步 `Loader` / `MinecraftVersion` 两个缓存字段）。
mods / saves / config / options.txt / servers.dat 全在实例目录（`instances/<id>/`）里，
版本目录在 `versions/<name>/`，两者天然分离——这是这个功能能做**原地**操作的前提，
不需要移动、复制、删除任何用户数据。

`InstanceService.SwitchLoaderAsync`（`Core/Instances/InstanceService.Loader.cs`）流程：

1. `FindInstanceCopy` 找实例，找不到给中文原因（列表该刷新）；
2. 用 `IVersionService.GetVersionAsync(VersionName)` **以磁盘为准**解析真实 MC 版本与加载器，
   读不到才退回 `InstanceInfo` 上的缓存字段——用户手动换过 `versions/` 里的文件时缓存会过期；
3. **同 MC 版本校验**：`targetMinecraftVersion` 非空且与解析结果不一致时直接拒绝，
   在调用安装之前（不能让用户等几十 MB 下载完才被告知不行）；
4. 同一加载器 + 未指定版本 = `AlreadyMatches`，不重装（那是「升级加载器」的语义，
   而且用户可能只是打开面板看了一眼）；
5. 调 `IInstallService.InstallLoaderAsync(..., overwriteExisting: true)`；
6. 装成功后才 `Clone` → 改三个字段 → `SaveAsync`。装失败时实例仍指向原版本；
7. 失败时清掉「装前确认不存在」的半成品版本目录；**装前就存在的目录一律不删**。

### 27.3 `overwriteExisting`：怎么做到不破坏既有 `SkipIfAlreadyInstalled`

`SkipIfAlreadyInstalled = true` 原本只出现在 `InstallService.cs` 的 Forge / NeoForge 两处，
语义是「这个版本装过就别再跑安装器」。这对「新装一个版本」是对的，对「换加载器」是错的——
换加载器的定义就是「按目标重装一遍」，遇到残缺的同名版本目录必须重装而不是跳过。

改法是给它加一个上游开关，而不是推翻既有默认值：

| 位置 | 改动 |
|---|---|
| `IInstallService.InstallAsync`（4 参） | **不动**。`RecordingInstallService` 等既有替身依赖这个签名 |
| `IInstallService.InstallLoaderAsync` | 新增，带默认实现（DIM）：转调 4 参 `InstallAsync` 并返回 `null` |
| `InstallService.InstallAsync` | 变成 3 行薄壳，转调私有 `InstallCoreAsync(..., overwriteExisting: false)` |
| `InstallService.InstallLoaderAsync` | 转调 `InstallCoreAsync(..., fabricApiVersionId: null, overwriteExisting)` |
| `InstallForgeAsync` / `InstallNeoForgeAsync` | `SkipIfAlreadyInstalled = !overwriteExisting` |

`InstallCoreAsync` 的流程与原 `InstallAsync` 逐行一致，唯一新增的是 `return installedName;`。
抽这个方法的理由不是重构：**换加载器必须知道装出来的版本目录名**，而 Forge / NeoForge 的
名字由安装器自己决定（`1.20.1-forge-47.2.0`），调用方推算不出来，`Task` 返回值又拿不到。

三点刻意不做：
- **不传 `ResultVersionName`**：它只影响「没有自动名字时返回什么」，强行指定会让实例指向一个
  根本不存在的目录（`InstallForgeAsync` 只把它当返回值用）；
- **不随装 Fabric API**：那是「新建 Fabric 实例」的便利，换加载器往用户 mods/ 里塞东西
  会让「换完之后模组列表莫名多了一个」无法解释；
- **不碰 `EnsureInstallEnvironment`**：安装前的目录准备逻辑与这个功能无关。

### 27.4 mods 兼容性提示

判定放在纯静态类 `InstanceLoaderAdvice.RequiresModConfirmation(from, to)`（`Core/Models/Instances.cs`），
而不是藏在服务实现里：界面在**弹确认之前**就要知道该不该提示，那时还没有任何安装动作。

只有四种组合可以静默执行：

| 组合 | 静默 | 理由 |
|---|---|---|
| 同一加载器换版本 | 是 | 只是升级加载器 |
| 原版 → 任意加载器 | 是 | 原版根本不读 `mods/`，那些 jar 本来就是躺着的 |
| Fabric ↔ Quilt | 是 | Quilt 是 Fabric 的上游超集 |
| 任意加载器 → 原版 | **否** | mods 目录还在，但不会再被读取 |
| Forge ↔ Fabric / Forge ↔ NeoForge 等 | **否** | jar 格式与注入方式完全不兼容 |

需要确认时界面走**两段式**：黄色提示条常驻（打开面板就能看见，不需要先点「更换」），
点「更换」后展开确认条，再点「确认更换」才真正开始下载。

### 27.5 界面

- 实例卡片第 1 行末尾加 `…` 溢出菜单（`Button Classes="ghost icon"` + `MenuFlyout`），
  里面只有「换加载器…」。卡片已有 8 个 pill 按钮，再塞文字入口会把卡片撑宽到需要横向滚动。
- `MenuFlyout` 里的命令绑定必须在 code-behind 取值（`OnSwitchLoaderClick`），
  菜单挂在独立弹出层，`$parent[ItemsControl].DataContext` 那条路径到不了 ViewModel。
- 面板与实例设置面板**共用 `Grid.Row="2"`、靠 `IsVisible` 互斥**：两个都展开会让页面变成一叠表单。
- 加载器版本下拉复用 `IVersionService.GetLoaderVersionsAsync`（与下载页同一条链路），
  Forge 的「推荐版」标记直接生效（截图里是 `54.1.22（推荐）`）。
- 确认条展开时底部「更换 / 取消」让位隐藏：同时出现两个「更换」会让人不知道点哪个。

### 27.6 测试（`tests/NebulaLauncher.Tests/CoreInstances_LoaderSwitchTests.cs`，16 例）

| 用例 | 断言要点 |
|---|---|
| 原版实例加装 Fabric | 成功、`VersionName` 落到返回的版本名、`overwriteExisting == true`、落盘后重读一致 |
| 跨 MC 版本被拒 | 失败且原因含两个版本号、**安装服务一次都没被调用** |
| Forge 换成 Fabric 需要确认 | `RequiresModConfirmation == true` 且说明非空 |
| 兼容性判定 7 组 `[Theory]` | 原版→Forge、Fabric↔Quilt、同加载器为 false；Forge↔NeoForge、Fabric→原版为 true |
| 安装失败 | 实例 `VersionName` / `Loader` 都保持原值 |
| 失败清理半成品 | 装前不存在的版本目录被删掉 |
| 已是目标加载器且未指定版本 | `AlreadyMatches == true`、**安装服务未被调用** |
| 服务未实现 `InstallLoaderAsync` | 走 DIM 返回 null → **失败而不是静默成功** |
| 实例不存在 | 失败且 `Error` 非空 |
| 磁盘版本覆盖过期缓存 | 缓存写 1.20.1、磁盘解析出 1.21 → 安装请求带 1.21 |

全部走 `IInstallService` 替身，不联网、不写真实 `versions/`。

### 27.7 未验证

- **真实换加载器全流程**：单测只覆盖「决策与回滚」。真装一次 1.21.4 → Fabric 需要联网下载，
  本轮没有跑（`InstallLoaderAsync` 的 Forge/NeoForge 分支在 `overwriteExisting: true` 下的
  真实覆盖率依赖安装器本身）。
- **装成功后 `SaveAsync` 失败的半状态**：磁盘上已有新版本目录、实例仍指旧版本。代码给出
  「可以重试一次换加载器」的提示，但没有构造该场景的测试。
- **`PredictVersionName` 的 Forge 名字**：只在「失败清理」用，按 `<mc>-<loader>-<ver>` 推算；
  若 CmlLib 某天改了命名规则，最坏结果是半成品目录没被清掉（不会误删已有目录）。
