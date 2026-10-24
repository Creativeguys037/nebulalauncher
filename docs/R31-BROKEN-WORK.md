# R31 中断任务清单

> **历史快照（2026-10-23 第三十一轮）。** 文中所有「仍缺」项已在 R32–R49 处理完毕，
> 当前状态与最终范围见 [`FINAL-SCOPE.md`](FINAL-SCOPE.md)、[`TAKEOVER.md`](TAKEOVER.md)、
> [`AUDIT-2026-10-23.md`](AUDIT-2026-10-23.md)。本文件保留中断事实，不再更新数字。

**写作原因**：第三十一轮末尾 token 耗尽，队员任务被中断。本文档只记录**中断事实**：
谁断了、断在哪、留了什么、已经补了什么、还有什么没做。

本文档**不做规划**：不排序优先级、不提实现方案、不指定下一轮做什么。方向由接手的人定。

成果汇报见 `docs/HANDOVER-2026-10-23-R31.md` 与 `docs/TAKEOVER.md`。

---

## 0. 代码树当前状态（实测）

```
dotnet build src\NebulaLauncher.App\NebulaLauncher.App.csproj -c Lead5   → 0 个警告 / 0 个错误
dotnet run --project tools\TestRunner\TestRunner.csproj -c Lead5 …       → 总计 2042, 通过 2042, 失败 0
pwsh .\build.ps1 -Clean -Publish -Mode minimal -OutDir …\pub-r31          → 23.6 MB / 3 文件，PNG 145668 字节
```

`src/` 下 `TODO` / `FIXME` / `NotImplementedException` 命中 0 处。15 处 `NotSupportedException`
全部位于 `Abstractions/`，是契约的默认实现（项目约定「只追加成员且必须带默认实现」），不是遗留。

---

## 1. 中断总览

| 队员 | 任务 | 中断时产出 | 已补 | 仍缺 |
|---|---|---|---|---|
| `build-verify` | task-28 插件提示条 + 整合包落点 | **编译不过的代码树** | ✅ 全部补完 | 2 条测试/证据缺口（§2.4） |
| `mod-lookup` | task-29 真机启动冒烟 | **零产出** | ⚠️ 6 步完成 4 步 | 2 步未执行（§3.3） |
| `probe-cli` | task-30 `ResourceFetcher` headers 模式 | **零产出** | ✅ 全部补完 | 无 |
| `b11deaa3…`（后台 subagent） | 不明 | **零产出、无收尾消息** | — | 无法归属（§5） |

三个具名队员的中断形态一致：**没有失败原因、没有收尾消息**。中断位置也一致——
都在「主文件改完、接线与测试未做」这一步。

---

## 2. `build-verify` / task-28

### 2.1 它落地了什么（这部分保留在树上）

`src/NebulaLauncher.Core/Backups/ModpackService.cs`：

| 位置 | 内容 |
|---|---|
| `:1635` | `HintKeys = ["loaders", "categories", "tags"]` |
| `:1645` | `PluginHints = ["plugin", "bukkit", "folia", "paper", "purpur", "spigot"]` |
| `:1817` | `KindOf(string? declared, string? targetPath, IReadOnlyList<string>? hints = null)`，插件先判、再判路径前缀 |
| `AddonDirectoryOf` | 加 `ContentKind.Plugin => "plugins"` |
| `WebUrlOf` | section switch 加 `ContentKind.Plugin => "plugin"` |

### 2.2 它中断时的状态

`ModpackService.cs:1757` 的 `ParseEntry` 调用了 `ListAny(...)`，该函数从未被写入：

```
error CS0103: 当前上下文中不存在名称"ListAny"
```

`NebulaLauncher.Core` 编译不过。

写域内另两个产物**不存在**：
- `src/NebulaLauncher.App/Views/LibraryView.axaml`：一行未改
- `tests/NebulaLauncher.Tests/CoreBackups_ModpackPluginTests.cs`：文件未创建

### 2.3 Lead 补入的内容

1. `ListAny(JsonElement, string[])` → `List<string>?`（`ModpackService.cs`，紧跟 `ScalarAny` 之后）。
   接受数组与标量两种形态；键优先级与 `ScalarAny` 一致。返回类型用 `List<string>?` 而非
   `IReadOnlyList<string>?`，后者触发 `CA1859`。
2. `LibraryView.axaml` 新增一条 `Border Classes="toneBar"`，绑
   `HasServerPluginNotice` / `ServerPluginNotice`，`Foreground="{DynamicResource Status.Warning}"`，
   `TextWrapping="Wrap"`。位置在 `IsFilterLimited` 的 toneBar 之后。
3. `CoreBackups_ModpackPluginTests.cs`（23 例）：七种插件声明、三种加载器数组键、标量形态、
   `path: plugins/...` 路径推断、fabric/forge/neoforge/quilt 回归、datapack/resourcepack/shader/world 回归、
   无类型默认 `Mod`、空数组不触发、`AddonDirectoryOf` 六种 kind、枚举全值反向守护（Plugin 永不落 `mods/`）、
   `WebUrlOf` 走 `/plugin/`、CurseForge 数字 id 仍走 CF 链接。

### 2.4 仍缺（客观罗列，无排序）

| # | 缺什么 | 现状 |
|---|---|---|
| 1 | 提示条的像素证据 | 绑定过编译期校验；`CoreMods_PluginKindTests.cs:309-322` 断言 `CanInstallNow=false` + `HasServerPluginNotice=true` + 文案含 `plugins/`。**没有任何截图显示过这个提示条** |
| 2 | 整合包 `overrides/` 自带 `plugins/` 的用例 | 现有 23 例覆盖 `addons` 数组的 kind 判定，未覆盖 `overrides` 解压路径 |

---

## 3. `mod-lookup` / task-29

### 3.1 它中断时的状态

零产出。`D:\agent_workspace\.probe\smoke-r31` 目录当时不存在，说明未执行任何工具调用。

### 3.2 Lead 执行到哪一步

探针：`D:\agent_workspace\.probe\smoke-r31\`（console，`ProjectReference` 引 Core）。
报告：`.probe/smoke-r31/REPORT.md`。

任务原定 6 步，实际执行 4 步：

| 步 | 内容 | 状态 |
|---|---|---|
| 1 | 装 1.20.1 | ✅ 3641 文件 / 788 MB（jar 23,028,853 字节 / libraries 58 MB / assets 598 MB / 1 个 index） |
| 2 | 组 options | ✅ 用 `new LaunchOptions` 手工填（`LaunchOptionsFactory` 在 App 项目，Core 探针引不到） |
| 3 | 指定 Java 21 | ✅ `jdk-21.0.12.101-hotspot\bin\java.exe` |
| 4 | 启动并判定进主菜单 | ✅ 见下 |
| 5 | 正常退出 | ❌ **未执行** |
| 6 | 第二次启动 | ❌ **未执行** |

第 4 步的实际结果：

```
[22:02:07] 进程启动（Java 21）
[22:03:39] Created: … minecraft:textures/atlas/*.png-atlas   ← 11 个 atlas 全建完
[22:03:44] [Realms Notification Availability checker #1/INFO] ← 主菜单就绪
```

从进程启动到 atlas 建完 **92 秒**（首次，assets 首读）。判据命中：`Setting user:` /
`Backend library: LWJGL` / `Sound engine started` / 无 `Exception in thread "main"`。
离线账户的 `Failed to verify authentication … 401` 是预期降级，游戏继续起。

原任务第 2 步要求「`GameDirectory` 设成实例目录以顺带回归 R29 的 P0」，实际设的是 minecraft 目录，
**该 P0 未回归验证**。

### 3.3 仍缺（客观罗列，无排序）

| # | 缺什么 | 现状 |
|---|---|---|
| 1 | 正常退出路径 | 探针观察窗到点后调 `KillCurrentGame()`（`Process.Kill(entireProcessTree: true)`），`latest.log` 无 `Stopping!`，`LaunchResult.ExitCode = -1`。退出时的存档备份 / 实例锁释放 / 崩溃报告归档三条链路均未在真机跑过 |
| 2 | 第二次启动 | 未执行 |
| 3 | Forge / Fabric 加载器真机 | 只跑过原版。`LoaderVersion` / `--versionType` / JVM 参数分档 / Java 上限判定均只有单测覆盖 |
| 4 | R29 的 P0 回归 | 第 2 步未按原任务要求走实例目录 |

### 3.4 中断期间撞到的事实（记录，非建议）

| 事实 | 证据 |
|---|---|
| `GameLauncher` 独占持有 `logs/latest.log` | 游戏运行中外部 `File.ReadAllText` 抛 `IOException: The process cannot access the file … because it is being used by another process`。探针因此崩掉，`exit 1`。用 `FileShare.ReadWrite` 可读 |
| 60 秒观察窗不足 | 首次到主菜单需 92 秒 |
| `LaunchAsync` 启动前补 788 MB | 即使显式给了 `JavaPath`，仍下载 assets + runtime（日志 `DownloadingAssets 788/788 MB`）。这是既有行为 |
| 数据根落点 | 探针用 `LauncherPaths(null, null)`，根被解析到 exe 目录下的 `data`，不是 `%APPDATA%\NebulaLauncher`（`ResolveWritableRoot()` 的回退顺序所致） |

中断时的复现命令（已落盘的 788 MB 在 `.probe\smoke-r31\bin\Release\net8.0\data\`）：

```powershell
cd D:\agent_workspace\.probe\smoke-r31
. D:\agent_workspace\NebulaLauncher\env.ps1
$env:AVALONIA_TELEMETRY_OPTOUT='1'
$env:SKIP_INSTALL='1'
dotnet run -c Release -- 1.20.1 60
```

---

## 4. `probe-cli` / task-30

队员零产出（`LoaderMetaFetcher.cs` 的 mtime 未变，一次工具调用都没执行）。Lead 全部补完。

`src/NebulaLauncher.Core/Downloads/LoaderMetaFetcher.cs`
- 新增 `using System.Text;`
- `GetStringAsync`（`:133-176`）改调私有 `ReadStringAsync`：
  `GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)` → `EnsureSuccessStatusCode()`
  → `ReadAsStreamAsync(ct)` → `StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true)`
  → `ReadToEndAsync(cancellationToken)`（`#if NET8_0_OR_GREATER` 守卫）
- 新增 `ResolveEncoding(string? charset)`：空白/未知 → `Encoding.UTF8`；
  否则 `Encoding.GetEncoding(charset.Trim().Trim('"'))`，捕获 `ArgumentException`
- `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)` 分支的注释
  补明了超时的两个发生位置（headers 与 body）

`tests/NebulaLauncher.Tests/CoreDownloads_ResourceFetcherTests.cs`（9 例，通过）：
正常 JSON / 无 charset / `gb18030` 中文 body / 未知 charset / 404 带状态码 /
读 body 阶段超时归「请求超时」/ 预取消 token 原样抛 / 首个 404 次个 200 / 全失败列两个地址。

无剩余工作。

---

## 5. 无法恢复的一条

后台 subagent `b11deaa3-6f5b-49fc-bcd2-043774d141aa` 报 `failed before it finished`，
无收尾消息、无任何文件产出、无工具调用痕迹。无法判定其原任务，也没有可回收的工作。

---

## 6. 本轮子 agent 的失败形态（纯观察，不含处置意见）

三轮累计派出 8 个具名队员，5 个中断（62%）。本轮 3 个全中断。

| 队员 | 中断前的最后动作 | 已写文件 | 未写文件 |
|---|---|---|---|
| `build-verify` | 写完 `ModpackService.cs` 的调用方 | `ModpackService.cs` | `LibraryView.axaml`、测试文件 |
| `mod-lookup` | 无 | 无 | 全部 |
| `probe-cli` | 无 | 无 | 全部 |

`build-verify` 的中断可被编译期捕获（`CS0103`），另两个不可（零产出，无错误信号）。

---

## 7. 文档索引（本轮改动）

| 文档 | 状态 |
|---|---|
| `docs/TAKEOVER.md` | 已重写（R31 收尾点） |
| `docs/HANDOVER-2026-10-23-R31.md` | 新建 |
| `docs/HANDOVER.md` | 计数更新 2033 → 2042 |
| `docs/PUBLISH-CHECK.md` | 更新为 R31 数字（23.6 MB / PNG 145668 字节） |
| `.probe/smoke-r31/REPORT.md` | 真机冒烟报告（`latest.log` 原文 + 判据命中表 + 复现命令） |
| 本文档 | 中断事实记录 |

另：`docs/HANDOVER.md` §11 与 `docs/TAKEOVER.md` §5 已纠正一条长期错误记载——
「`8u320` 建议级提示未接 UI」不成立。`PreflightChecker.CheckLoaderLimit` 对 `IsBlocking=false`
输出 `Warning`，`GameLauncher.ReportPreflight` 通过 `progress.Report` 上报，
`CoreJava_LoaderLimitWiringTests` 有 3 条用例覆盖（`:204-217` Blocker 档、`:226-239` Warning 档、
`:242-251` `1.8.0_320` 返回空）。
