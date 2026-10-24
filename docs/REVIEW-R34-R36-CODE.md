# R34–R36 新增代码边界与回归审查（CODE REVIEW）

> **R38–R41 处置状态**：§2.1 的 6 条 P2 **已全部修复**（R38）；§2.2 的 14 条 P3 **全部处理完毕**
> （R39：P3-1/2/4/5/6/7/8/9/10/12；R40：P3-3/13/14；R41：P3-11）。本报告保留审查时的原始结论；
> §3/§4 的「已确认无问题」与「无测试覆盖」按 R41 之后的状态仍基本成立（变化：`UpdateProcessId`、
> `LoaderVersionComparer`、IHDR 尺寸、持久化取消回滚已有测试；R40 三条仍无自动化用例）。

> 审查对象：`HANDOVER-2026-10-24-R34.md` / `R35` / `R36` 列出的 10 个改动面，及其直接调用方、UI 绑定与相关测试。
> 方法：逐文件读完整方法（不只看注释），沿调用链核对锁 / 进程 / 文件 / 取消的所有权；仓库没有 `.git`，无法按 diff 审查，按当前快照。
> 约束：本轮只新增本文件，未改动任何现有文件。**未复跑构建与测试**（`dotnet build` / `TestRunner` 会重写 `obj/`、`bin/` 与现有产物），文中的构建/用例数字引自交接文档，不是本轮实测。

## 1. 结论

**需修 20 处（6 处 P2 + 14 处 P3），当前状态不建议直接发布；无 P1。** R34–R36 的纯逻辑层质量不差：备份原子占名、导出三类文件、LauncherPack 拒绝、加载器候选合并、账户/服务器落盘回滚、退出守卫 VM、favicon 与剪贴板判据都有对应测试；但 R34 的核心交付「游戏启动后取消等待并移交后台」只剩**隐藏的 Esc 路径**：进程启动后可见的「取消」按钮被隐藏，而 `MainWindowViewModel.Escape` 仍会在首页启动面板存在时无条件执行 `CancelLaunchCommand`（无 CanExecute 守卫），相关 XAML 注释与单测还钉着「取消会释放实例锁」的旧语义，用户既无可见入口、又可能误触（P2-1）。最接近 P1 的是 P2-6：实例锁判活跟随启动器进程，用户关闭启动器后重开，同一实例可在游戏仍运行时再起一个 JVM。修复 P2-1、P2-2 后，移交路径才会真正按设计被用户触达，建议 P3-1、P3-2、P3-3、P3-13、P3-14 与它们同批处理。

## 2. 缺陷表

### 2.1 P2（应修）

| 编号 | 严重度 | 文件:行号 | 问题 | 触发条件 | 建议修法 |
|---|---|---|---|---|---|
| P2-1 | P2 | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:249-255`；`src/NebulaLauncher.App/Views/HomeView.axaml:378-391`；`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs:428-430`；`tests/NebulaLauncher.Tests/AppLaunch_StopGameTests.cs:192-205` | 可见入口与命令能力不一致：`ShowCancelLaunch = IsLaunching && !IsLaunchSessionRunning`，进程一启动按钮就消失；但 `Escape()` 在首页 `HasLaunchPanel` 为 true 时无条件执行 `CancelLaunchCommand`（该命令无 CanExecute），所以 Esc 仍会触发 Detach。XAML 注释、`ShowCancelLaunch` 注释与单测仍断言「进程起来后取消会释放实例锁」的旧行为，用户既看不到入口又可能误触 | 首页启动进入「游戏运行中」后：想取消等待没有按钮可点；而在首页按 Esc（焦点不在输入框）会静默移交后台并把 `IsLaunching` 置 false，行为与界面说明相反 | 明确取舍：要么 `ShowCancelLaunch => IsLaunching` 并把文案改成「后台运行 / 取消等待」，更新注释与测试；要么给 `CancelLaunchCommand`/Esc 分支加 `ShowCancelLaunch` 守卫，并把 Detach 标注为 API-only |
| P2-2 | P2 | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:514-561`；`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:948-957` | 取消与退出竞态下走非 Detach 的 `catch (OperationCanceledException)`：`ShouldDetachOnCancel` 因 `HasExited=true` 返回 false，直接返回 `Started=true / ExitCode=-1 / Verdict=null`，**完全跳过 `CompleteExitAsync`**（崩溃分析、hs_err 归档、自动世界备份、退出后命令、`RecordLaunchAsync`），然后 Home 的「verdict 为 null 看退出码」兜底把 `-1` 报成「游戏异常退出」 | 取消令牌恰在进程退出瞬间触发：快速崩溃时用户点取消、或 `WaitForExitAsync` 的取消先于任务完成被观察到 | 在该 catch 里判 `processStarted && HasExited(process) && runningGame/java/watcher 可用` 时改为 `return await CompleteExitAsync(...)`；或让「已退出」也移交后台并返回真实 exit/verdict |
| P2-3 | P2 | `src/NebulaLauncher.Core/Accounts/AccountService.cs:596-612`；`src/NebulaLauncher.App/ViewModels/AccountsViewModel.cs:372-380` | `RemoveAsync` 写盘失败时只 `_log.Error` 并回滚内存，接口返回 `Task`、无 bool/异常；`ConfirmRemoveAsync` 随后无条件 `InfoText = "已移除账户…"`，用户看到「已移除」但列表里的账户又回来了 | `accounts.json` 不可写：路径被同名目录占据、权限、磁盘满（`CorePersistence_FailureTests` 已能复现该失败） | 让 `RemoveAsync` 抛 `IOException`（与 `IServerService.RemoveAsync` 一致）或返回 bool；VM 在失败分支显示原因并保留行，不打印成功文案 |
| P2-4 | P2 | `src/NebulaLauncher.App/ViewModels/ServersViewModel.cs:394-407` | 编辑服务器实现为「先 `RemoveAsync(_editingId)` 落盘、再 `AddAsync(entry)` 落盘」。第一步成功后第二步失败时，`AddAsync` 只回滚自己新加的条目，旧条目（已从磁盘删除）不会恢复——这次编辑会永久丢一条收藏 | 编辑保存时第二次写盘失败：磁盘满、杀软/同步盘占用、目录权限在两次写之间变化 | 增加 `ServerService.ReplaceAsync(oldId, entry)` 一次写盘完成替换；或 `AddAsync` 失败时把旧条目补回内存并重新落盘，失败则抛错由 UI 提示 |
| P2-5 | P2 | `src/NebulaLauncher.Core/Platforms/PlatformService.cs:173-233`；调用方 `src/NebulaLauncher.App/ViewModels/ServersViewModel.cs:583-587`（`LogsViewModel.cs:330-338`、`SettingsViewModel.cs:2234/2254` 同） | `CopyTextAsync` 在「原生 API 失败 + PowerShell 回退失败」或「所有候选命令失败」时只写日志后正常返回；调用方无法区分成功/失败，仍显示「已复制」。R34 改进了「什么算失败」，但没有把失败传出去 | Windows 剪贴板被占用且 PowerShell 回退失败；Linux 无 `wl-copy`/`xclip` 或无 `DISPLAY`；macOS `pbcopy` 失败 | 把 `CopyTextAsync` 改为 `Task<bool>`（或全部候选失败时抛异常），调用方按结果给「已复制 / 复制失败」；同步改接口注释与测试替身 |
| P2-6 | P2 | `src/NebulaLauncher.Core/Instances/InstanceLockService.cs:115-123, 249-267`；`src/NebulaLauncher.Core/Launching/GameLauncher.cs:350-365` | 锁文件写的是**启动器** PID（`Environment.ProcessId`），判活只查这个 PID。用户启动游戏后关闭启动器（默认 `CloseToTray=false`，X 直接退出；LaunchAsync 的 finally 不会执行，锁句柄不 Dispose，锁文件保留），游戏继续运行；下次启动时该锁因启动器 PID 已死被判僵死并接管，同一实例可再起一个 JVM | 启动游戏 → 关闭启动器 → 重新打开启动器 → 启动同一实例；或启动器崩溃/被结束进程。R35 真机只验证了启动器保持存活时的第二次启动 | `process.Start()` 成功后把锁文件原子改写为**游戏** PID/启动时间（`QueryAsync`、冲突文案、`InstanceLockInfo.Pid` 文档一起改）；或在有会话在跑时拦一次启动器退出（与下载守卫同一机制） |

### 2.2 P3（提示）

| 编号 | 严重度 | 文件:行号 | 问题 | 触发条件 | 建议修法 |
|---|---|---|---|---|---|
| P3-1 | P3 | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:523-548, 583-588` | `detached = true` 在 `progress?.Report(...)` 与 `DetachRunningGame(...)` **之前**置位。这两步任一抛异常（自定义同步 `IProgress`、Dispatcher 已关闭）时，finally 仍会注销会话并 `instanceLock.Dispose()`，但因为 `detached` 已为 true 而跳过 `process.Dispose()`：游戏还在跑，锁已经没了——正是本轮要消除的状态 | 取消等待时进度接收方抛异常；或 `Task.Run` 排程失败（OOM/调度器已释放） | 先让 `DetachRunningGame` 成功拿到所有权，再置 `detached` 并清空 `runningGame/instanceLock`；`progress.Report` 包 try/catch（与 `ReportStartupStage` 一致） |
| P3-2 | P3 | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:523, 724-727` | 取消分支先 `stopwatch.Stop()`，后台 `CompleteExitAsync` 再读同一个 Stopwatch；移交后玩的几小时不会进入 `RecordLaunchAsync` 与 `LaunchResult.Duration`，版本页「游玩时长」偏短 | 取消等待后游戏继续运行一段时间再退出 | 取消分支不要 `Stop()`；由 `CompleteExitAsync` 统一 Stop，Detached 结果读当前 `Elapsed` 即可 |
| P3-3 | P3 | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:709-749`；`src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:939-946` | 后台任务的 `LaunchResult`（含 `CrashAnalysis`/`CrashReport`/`HsErrLogs`）被丢弃，只通过 progress 发一句 `Exited` 文案。取消等待后游戏崩溃时，首页没有崩溃面板、建议和「打开/导出崩溃报告」入口 | 取消等待（当前经首页 Esc 或探针）后游戏崩溃；P2-1 修复后成为常规路径 | 增加 `SessionExited` 事件（携带 LaunchResult）由首页订阅，或让 `LaunchProgress` 携带 verdict/crash 摘要 |
| P3-4 | P3 | `src/NebulaLauncher.App/Views/MainWindow.axaml.cs:214-215, 260-301, 326-333` | `_pendingWindowStateChanged` 只在「下载确认后退出」路径被消费。第一次关闭因下载被拦、用户点「继续下载」后，内存里的窗口尺寸已改；之后再正常关闭时 `CaptureWindowState()` 与内存值相同返回 false，`CloseAfterSave` 直接放行，尺寸/最大化状态永远不落盘 | 改窗口大小 → 有下载时点 X → 取消退出 → 下载结束再关闭启动器 | `CloseAfterSave(e, stateChanged || _pendingWindowStateChanged)`，保存成功后清空标记；`FlushSettingsInBackground` 同样使用合并后的布尔 |
| P3-5 | P3 | `src/NebulaLauncher.Core/Backups/BackupService.cs:176-202, 573-590` | `WriteMetaAsync` 只把非取消异常转成 false 并回滚归档；调用方的 token 在 `MoveIntoFreePath` 之后、meta 写完之前被取消时抛 OCE，归档留在目录里且没有 meta，`ListBackupsAsync` 会把它当手动备份，自动轮转永远清不掉 | 备份过程中取消恰落在归档改名与 meta 写入之间；或关闭应用取消 `File.WriteAllTextAsync` | 捕获 `WriteMetaAsync` 的 OCE，删除归档后重抛；或先写临时 meta，再与归档一起提交 |
| P3-6 | P3 | `src/NebulaLauncher.Core/Versions/LoaderVersionCatalog.cs:49-56` | 已安装版本按字符串 `OrderByDescending(StringComparer.OrdinalIgnoreCase)`：`47.9.0` 会排在 `47.10.0` 前，`20.2.62` 会排在 `20.2.100` 前，回滚候选顺序错误。现有测试选的 `36.2.39/45.0.40/47.2.0` 恰好字典序与数值序一致，掩盖了问题 | Forge/NeoForge 同时安装多位段（10 段以上）构建 | 用按段数值比较的版本比较器替代字符串序；测试补 `47.9.0` / `47.10.0` 一组 |
| P3-7 | P3 | `src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:3136-3148, 3164, 3224-3230` | `IsLoadingLoaderSwitchVersions` 只在「当前 requestId 命中」的 finally 里复位；请求 A 加载中时切到 Vanilla，请求 B 在设置 loading 之前就提前 return，A 的 finally 因 id 不匹配也不复位 → 转圈与下拉禁用永久停留，直到下一次非 Vanilla 请求完成 | 打开 Forge/NeoForge 换加载器面板，在列表返回前点「原版」 | 所有 early-return 分支按当前 requestId 复位；或把 loading 的设置/复位放进以 requestId 为条件的 try/finally |
| P3-8 | P3 | `src/NebulaLauncher.Core/Platforms/PlatformService.cs:898-909, 921-932` | 10 秒超时只包住 `WaitForExitAsync`；`StandardInput.WriteAsync` 没有超时/取消。候选命令若在读取 stdin 前卡住（等锁、等显示、等授权），调用方会无限 await——比「失败」更糟 | `wl-copy`/`xclip`/PowerShell 在读取 stdin 之前阻塞（无 DISPLAY、被挂起、安全软件拦截） | 先建 CTS，再用 `WriteAsync(...).WaitAsync(token)`，把写入与等待都纳入同一超时；写失败/超时同样 `TryKillProcess` |
| P3-9 | P3 | `src/NebulaLauncher.App/Services/ServerFaviconDecoder.cs:23, 65-70, 88-105` | 只限制 base64 解码后的字节数（512 KB），不限制 PNG 声明尺寸。恶意/异常服务器可用 512 KB 以内的 PNG 声明超大宽高，`new Bitmap` 在 UI 线程分配巨量内存或长时间解码 | 服务器 SLP 返回体积合规但 IHDR 声明 10000×10000 的 PNG（图标正常只有 64×64） | 解码前校验 PNG IHDR 宽高（可限 256×256），超限直接返回 null；或在 `Bitmap` 构造后检查 `PixelSize` |
| P3-10 | P3 | `src/NebulaLauncher.App/Services/TrayService.cs:97-124, 353-387` | `OnExit` / `OnOpenGameDirectory` 在 `await` 之后直接调 `_desktop.Shutdown()` / `_window.Close()` / 改 VM 属性。Avalonia 的托盘菜单激活在部分平台来自原生线程（本文件的 `ShowMainWindow` 已按此假设 `Dispatcher.UIThread.Post`），await 后没有回到 UI 线程 | Linux/X11 从托盘菜单点「退出」或「打开游戏目录」；Windows 回调线程若变化同样受影响 | 整个处理器入口用 `Dispatcher.UIThread.InvokeAsync` 包住，或 await 后 `Post` 到 UI 线程再 Shutdown/Close/SetStatus |
| P3-11 | P3 | `src/NebulaLauncher.Core/Accounts/AccountService.cs:193-203, 262-273, 598-610`；`src/NebulaLauncher.Core/Servers/ServerService.cs:134-150, 186-197` | 回滚只处理 `Persist/Save` 返回 false；两者对 OCE 都是 rethrow，取消发生在写盘时内存改动不会回滚。微软/第三方登录路径甚至会把写盘阶段的 OCE 记成「登录已取消」，而账户已在内存并激活 | 登录、切页、取消操作让 CTS 落在「内存已改、落盘未完成」窗口 | 调用方 catch OCE → 恢复内存 → 重抛；或把取消也按失败处理并返回可展示原因，保证成功/失败/取消三条路径的内存与磁盘一致 |
| P3-12 | P3 | `src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs:1524-1533, 1542-1562`；`src/NebulaLauncher.Core/Backups/ModpackService.cs:105-112, 196-207` | LauncherPack 预览没有对应分支：`FormatText` 显示「未知格式」、`InstallPlanText` 显示「未声明游戏版本，只能解压文件」，而导入入口明确拒绝并说「先解压再导入内层」——预览与实际行为相反 | 拖入/选择含 `modpack.zip`/`modpack.mrpack` 的分发包 | 为 LauncherPack 增加 `FormatText`/`InstallPlanText`（写明需先解压），并隐藏「同时安装游戏」开关；预览阶段就把下一步说清楚 |
| P3-13 | P3 | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:1041-1080`；`src/NebulaLauncher.Core/Launching/GameLauncher.cs:709-749, 755-775` | Detach 返回后 `IsLaunching` 立刻为 false；进程退出瞬间 `RunningGames` 因 `IsAlive=false` 不再返回该会话，但后台的自动备份/退出命令仍持有实例锁。此时「停止游戏」会显示「已停止」，马上再启动会撞锁；冲突文案把锁持有者 PID（启动器自身）说成「游戏在运行」，并建议到任务管理器结束它 | 按 Esc 取消等待（或 P2-1 修复后点按钮）→ 停止游戏 → 清理尚未完成时立刻再启动同一实例 | 让 `GameLauncher` 暴露「退出收尾中」状态并在准入判定里等待；`DescribeLockConflictAsync` 区分「游戏运行中」与「退出收尾中」 |
| P3-14 | P3 | `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs:925-926, 1125-1188`；`src/NebulaLauncher.Core/Launching/GameLauncher.cs:685, 709-727` | Detach 后旧的 `Progress<LaunchProgress>` 仍活着。旧会话退出时会在 UI 线程执行 `ApplyProgress(Exited)`，把 `LaunchPhaseText`/`LaunchDetail`/`LaunchRatio` 改成「游戏已退出」，并 `ShellProgress.Clear()`；若此时用户已发起下一次启动，新面板的阶段文案与任务栏进度会被旧会话覆盖 | 取消等待（Esc）→ `IsLaunching` 变 false → 立刻启动另一个实例 → 旧游戏退出（同进程内多开） | 给进度回调加会话代次（Home 记下当前 `sessionId`/启动序号，旧回调直接丢弃），或 Detach 时换成一个只更新 `LaunchDetail` 的弱进度对象 |

## 3. 已确认没问题的关键路径

以下均为读码确认；标【测试】的另有现存单测（本轮未复跑，理由见文首）。

- **正常退出收尾顺序**：`CompleteExitAsync` 先做内存/文件收尾（记录、归档、自动备份、退出后命令），之后 `LaunchAsync` 的 finally 才 `Unregister` + `instanceLock.Dispose()`，锁不会提前放（`GameLauncher.cs:609-688, 572-589`）。
- **Detach 的存活时所有权转移**：`ShouldDetachOnCancel` 只在「已启动 + 未退出 + 有会话」时返回 true；后台 finally 是会话/锁/进程的唯一释放点；finally 在 detached 时跳过注销与 Dispose（`GameLauncher.cs:541-543, 696-748`）。【测试】`CoreLaunch_ExitVerdictTests` 的真值表与 Detached 结果构造。
- **verdict 优先三个入口一致**：Home 在 `Detached` 之后才判崩溃；Versions/Servers 都是 `Verdict == Crashed || (Verdict is null && ExitCode != 0)`（`HomeViewModel.cs:939-957`、`VersionsViewModel.cs:1062-1072`、`ServersViewModel.cs:542-551`）。【测试】`BuildExitResult` 带出 verdict、exit 0 判崩溃、老版本非零码判正常。
- **备份并发命名**：临时文件带 GUID、`File.Move(overwrite:false)` 原子占名、`-N` 重试、1000 次后 GUID 兜底；`*.partial` 不会被 `*.zip` 列出（`BackupService.cs:136-207, 660-692`）。【测试】8 并发同世界全部成功且路径互异、4 并发自动备份全部带 meta。
- **备份 meta 失败回滚**：`WriteMetaAsync` 返回 false 时删除归档并抛 `IOException`，不会留下假装成功的孤儿 zip（`BackupService.cs:195-202`）——见 P3-5 的 OCE 例外。
- **导出三类文件**：`CollectOverrideFiles` 收集 `defaultconfigs/`、`options.txt`、`servers.dat`，三个开关默认值与 UI 绑定一致（`ModpackExporter.cs:242-295`、`WorldsViewModel.cs:914-920, 1063-1076`、`WorldsView.axaml:178-184`）。【测试】3 例。
- **LauncherPack 识别与拒绝**：根/一级目录检测、在创建实例目录之前返回明确错误，普通 zip 不受影响（`ModpackService.cs:542-607, 194-207`）。【测试】4 例。
- **加载器候选合并**：按加载器 + MC 版本过滤、去重、给远程同版本补 `IsInstalled`、空 MC 版本原样返回（`LoaderVersionCatalog.cs:20-85`）。【测试】5 例（顺序问题见 P3-6）。
- **持久化回滚（非取消路径）**：离线账户添加失败回滚内存与 active、移除失败放回原位；服务器增/删/导入失败回滚并抛 `IOException`（`AccountService.cs:183-208, 567-613`；`ServerService.cs:99-203`）。【测试】6 例。
- **地址解析**：`[::1]:25570`、裸 IPv6、非法端口、空串、未闭合方括号；`GameLauncher` 与 `ServerService.PingAsync` 共用（`ServerAddressParser.cs`、`GameLauncher.cs:2638-2644`、`ServerService.cs:318-334`）。【测试】15 例。`ServerListDatReader.TrySplitAddress` 仍是独立实现，见 §4。
- **退出守卫 VM**：无队列放行、有活动批次拦下并开覆盖层、取消后再问、确认后只触发一次事件（`MainWindowViewModel.ExitGuard.cs:37-64`、`MainWindow.axaml:250-282`）。【测试】4 例。
- **favicon**：data URI 前缀/base64/PNG 魔数/512 KB 上限全部返回 false 不抛，UI 有固定 32×32 槽与首字母兜底（`ServerFaviconDecoder.cs:13-75`、`ServersView.axaml:139-164`）。【测试】data URI 层。
- **剪贴板判据**：Linux 先 `wl-copy` 后 `xclip`、macOS `pbcopy`，只有退出码 0 算成功（`PlatformService.cs:19-40, 943-949`）。【测试】链顺序与 `Succeeded` 真值表。
- **launcher 构造唯一入口**：全仓 `new MinecraftLauncher` 只出现在 `MinecraftLauncherFactory`；`InstallService` 与 `GameLauncher`（命令构建 + 启动补全）都走它；生产 DI 注入 `DownloadHttpClientFactory.Client`（`MinecraftLauncherFactory.cs:49-61`、`InstallService.cs:842-846`、`GameLauncher.cs:182, 369`，grep 无第二处构造）。
- **退出守卫的覆盖层**：窗口级 Border 位于内容之后、有背景，能挡住标题栏与页面点击；`Escape()` 的通用分发不处理 `IsExitDownloadConfirmOpen`（首页按 Esc 取消启动的问题见 P2-1，与本覆盖层无关）（`MainWindow.axaml:250-282`）。

## 4. 无测试覆盖的路径清单

1. **`LaunchAsync` 的 Detach 全链路**：锁/会话所有权转移、后台 finally、`windowTask` 取消、后台结果的消费，以及旧会话的 progress 回调在新一次启动后覆盖新面板（P3-14）。现有测试只覆盖 `ShouldDetachOnCancel` 纯函数与 `LaunchResult{Detached}` 的字段形状；R35/R36 真机是探针直调 API，不经过 UI。
2. **取消与进程退出的竞态（P2-2）**：让 `WaitForExitAsync` 在进程刚退出时取消，验证仍走 `CompleteExitAsync`、结果不被 UI 误报崩溃。当前无 seam 可用。
3. **首页「取消等待」的可达性与 Detached UI 分支（P2-1）**：无 VM 测试；`AppLaunch_StopGameTests` 的「首页取消按钮只在进程未起来时可见」只钉住了按钮可见性，没有覆盖 `Escape()` → `CancelLaunchCommand` 这条仍可触发 Detach 的隐藏路径。
4. **MainWindow 关闭链**：`OnClosing` → `ShouldConfirmExit` → `ExitAfterDownloadConfirm` → `SaveAsync` → 二次 `Close`，以及 `_pendingWindowStateChanged`（P3-4）。受 `CONVENTIONS §7.1` 限制不能碰 Avalonia 类型，只有 VM 的 `ShouldConfirmExit` 4 例。
5. **`TrayService.OnExit`** 的 2 秒保存上限、超时后继续退出、非 UI 线程（P3-10）。
6. **备份 meta 失败/取消回滚（P3-5）**：`CoreBackups_AutomaticTests.UnreadableWorld_DoesNotPublishIncompleteBackup` 是源文件锁失败（发生在归档前），`Cancellation_DoesNotPublishPartialArchive` 在写入中取消（`Move` 前）；「`Move` 后、meta 前」没有用例。
7. **`MoveIntoFreePath` 的边界**：1000 次重试、GUID 兜底、非 `IOException`、候选名被目录占用时 `File.Exists` 为 false 的分支。
8. **账户移除失败的用户信号** 与微软/第三方登录在落盘阶段被取消（留在内存且激活）：只测了离线添加/移除回滚（P2-3、P3-11）。
9. **`ServerService` 的 OCE 回滚**：Add/Remove/Import 只测了 `SaveAsync` 返回 false 的路径（P3-11）。
10. **`VersionsViewModel.RefreshLoaderSwitchVersionsAsync`**：合并接线、RequestId 竞态、Vanilla 抢占导致 loading 卡死（P3-7）。只有 `LoaderVersionCatalog.MergeInstalled` 的纯函数测试。
11. **`MinecraftLauncherFactory`**：client 注入、`JavaFileExtractor` 移除、`NebulaGameInstaller` 替换均无测试；也没有「启动补全请求经过镜像/代理」的集成断言。
12. **`PlatformService.WriteThroughStdinAsync`**：进程起不来、stdin 写入失败、10 秒超时、非 0 退出码。本机 Windows 不能启动 `wl-copy`/`xclip`，现有测试只覆盖纯数据与谓词，以及 `ClipboardWriter` 钩子。
13. **`ServerFaviconDecoder.TryDecode`** 的 Bitmap 路径与尺寸上限（P3-9）：测试进程无 Avalonia 渲染后端，只测 data URI 层。
14. **LauncherPack 的 UI 文案**（P3-12）：Core 的识别/拒绝有测试，`WorldsViewModel.FormatText`/`InstallPlanText`、拖拽预览文案无测试。
15. **实例锁跨启动器重启**（P2-6）：`CoreInstances_LockTests` 明确断言 `handle.Info.Pid == Environment.ProcessId`（启动器进程），没有「启动器退出、游戏仍在运行」的用例，也没有「锁应记录游戏 PID」的契约。
16. **退出守卫与启动补全的下载**：`GameLauncher` 的补齐使用 `MinecraftLauncherFactory` 的 HttpClient，不走 `DownloadQueue` 批次，关闭启动器时不会被守卫拦下；该路径既无测试也无文档。
17. **跨解析器一致性**：`ServerListDatReader.TrySplitAddress` 未走 `ServerAddressParser`（对 `[::1]:25570` 保持整串 + 25565，对未加括号的 IPv6:端口整串当 host）。是否会被端到端误用取决于该形态是否真实出现，未验证，也没有一致性测试。
18. **`_launchCts` 生命周期**：Detach 后不再 Dispose、新一次启动才 Dispose；后台不使用它，风险低，但无测试。
