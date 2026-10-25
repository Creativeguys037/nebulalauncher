# 坑、裁决与易错点

本文件收录两类东西：**踩过的坑**（现象明确、机制清楚、能落到一条代码位置或一条可复现命令上的失败模式）与**架构级裁决**（已经定案的技术选择，连同「为什么这样选」和「反悔要付什么」）。

适用范围：`NebulaLauncher` 全仓（Core / App / tools）。正常用法看 `README.md` 与 `CONVENTIONS.md` §8；这里是「别这样做，做了就是这样挂」的索引。

不写东西：谁在什么时候改的、没有证据的传闻、过程流水。落不到代码或命令上的条目一律不收。

## 0. 使用约定

### 0.1 每条坑的结构

严格三段、顺序固定：**症状**（你看到什么：报错原文、退出码、截图表现）→ **成因**（机制上为什么这样；为什么直觉修法无效）→ **规避**（现在的正确做法，或一条可复现命令）。

### 0.2 引用写法

- 代码位置写成 `路径:行号 类型.成员`（一个引用一个反引号单元）；命令写成完整可复制的命令行，不省略参数；提到测试时给出用例名，改坏了应当变红。

### 0.3 与 CONVENTIONS.md 的关系

硬性规范的权威定义在 `CONVENTIONS.md`，本文**不复述**它的正文，只写违反后会发生什么，并标注条款。

| CONVENTIONS.md | 主题 | 对应本文条目 |
| --- | --- | --- |
| §0 | 环境、SDK 路径、`env.ps1` | 1.2、1.3、1.9、3.1 |
| §1 | 单一 TFM、依赖版本 | 6.D1、1.4 |
| §3 | 禁 `.Result` / `.Wait()` / `GetAwaiter().GetResult()` | 2.1、2.2 |
| §3.1 | JSON 落盘代码的写法（context 登记 / `Of<T>()` / 具名请求体 / 泛型枚举转换器） | 3.16、3.17 |
| §4 | 编译绑定必须写 `x:DataType` | 4.5 |
| §8.1 | 提交前门槛 | 1.1、5.6 |
| §8.2 | `dotnet test` 不可用、TestRunner 的退出码语义 | 1.1、5.6 |
| §8.3 | 文档引用 / 行数预算 / 三态校验（`tools/DocsCheck`） | 5.9 |
| §8.4 | 应用内渲染截图 | 4.1、5.8 |
| §8.8 | 并行协作三条 | 1.8 |
| §8.9 | Debug XAML 缓存损坏 | 1.4 |

`CONVENTIONS.md` 对应条款内的回指条目在同一处维护。

---

## 1. 构建与环境

### 1.1 `dotnet test` 起不来测试宿主
- **症状**：`dotnet test` 直接失败，栈里是 `Win32Exception`（拒绝访问，错误码 5），测试进程根本没起来，一个用例都没跑。
- **成因**：测试宿主是独立子进程，被宿主沙箱拒绝；这不是代码问题，重复重试、换 `--no-build` 都不会好。同一类权限事件会同时打掉 MSBuild 的多节点通信与 Avalonia 的遥测写入，所以「一条命令突然全挂」往往是环境层。
- **规避**：改用 `tools/TestRunner/Program.cs`（`tools/TestRunner/Program.cs`）在同进程里跑 xunit 用例；它支持按类名过滤，退出码语义见 5.6。见 `CONVENTIONS.md` §8.2。

### 1.2 多节点构建「error 数 0 但生成失败」
- **症状**：`dotnet build` 末尾打印「生成失败」，同一份输出里错误数是 0；有时伴随命名管道相关字样。
- **成因**：MSBuild 多进程构建用命名管道通信，管道被拒后子节点静默失联，主进程只看到「失败」这个结论。
- **规避**：串行构建。

```powershell
dotnet build -m:1 -p:BuildInParallel=false
```

需要频繁重建时用独立配置目录（见 1.8），不要为了省事退回并行。串行复跑还能把被并行掩盖的真实错误顶出来：同一仓库并行报 0 error，加 `-m:1` 后立刻暴露 1.9 的 `MSB3061`。

### 1.3 `MSB4018` + `AvaloniaStatsTask`
- **症状**：构建在 XAML 编译任务上报 `MSB4018`，任务名是 `AvaloniaStatsTask`。
- **成因**：Avalonia 的构建任务在编译期尝试写遥测缓存，写入被拒时任务整体失败，表现为「XAML 编译失败」，看起来像代码错但代码没动。
- **规避**：构建前关掉遥测。

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
```

### 1.4 `No precompiled XAML found for NebulaLauncher.App`
- **症状**：Debug 构建通过，运行时报 `No precompiled XAML found for NebulaLauncher.App`；改了 XAML 之后现象依旧，好像改动没进去。
- **成因**：XAML IL 的增量编译缓存与源码不一致（改了 `.axaml` 但产物里还是旧的、或者反过来）。缓存与源码不同步时不会报编译错，只在运行时炸。
- **规避**：删掉两棵缓存树后重建——`obj/Debug` 与 `bin/Debug` 一并删，只删其一会留着半套产物。见 `CONVENTIONS.md` §8.9；发布路径有同样的坑，见 1.5。

### 1.5 发布出来的 exe 双击没反应
- **症状**：`publish` 退出码 0，产物目录齐全，双击 exe 没有任何窗口；但同一份 exe 加 `--probe-dir` 却能正常打印。
- **成因**：发布复用了 `obj` 里过期的 XAML 产物，exe 能加载但界面起不来；`--probe-dir` 走的是控制台分支，不碰界面，所以「看起来正常」反而掩盖了问题。退出码 0 只说明 MSBuild 成功，不说明产物可用。
- **规避**：发布必须清干净再产。

```powershell
powershell -File build.ps1 -Clean
```

发布脚本内置三道闸，缺一不可，手工发布时按同一标准自查：XAML 过期检测；`--probe-dir` 30 秒内断言输出 `writable=YES`；`--screenshot` 20 秒内断言生成的 PNG 大于 10240 字节。细节见发布检查清单（已并入 `CONVENTIONS.md` §8）。

### 1.6 `-OutDir` 把整个目录删了
- **症状**：给发布脚本传了一个外层的 `-OutDir`，跑完之后目标目录连同里面原有的东西一起没了。
- **成因**：发布流程会先清空目标目录再写入，它对「目标目录」的全部内容负责；指到外层就等于授权它删掉那一层的全部内容。
- **规避**：`-OutDir` 只指到最内层产物目录；删改前先确认解析出的绝对路径。整条链路见 `docs/research/release-checklist.md`。

### 1.7 产物 exe 报 `UnauthorizedAccessException`
- **症状**：发布产物在验证机上启动即抛 `UnauthorizedAccessException`，看起来像产物坏了。
- **成因**：exe 启动时往 `%TEMP%` 写探测文件被拒。真实原因是这台机器不让这个进程写系统临时目录，不是 exe 的逻辑错误；换机器复现不出来。
- **规避**：验证文件一律落产物自身目录（`AppContext.BaseDirectory`），别落系统临时目录。实现见 `src/NebulaLauncher.App/Program.cs Program.TryRelaunchElevated（:142）` 的 probe-dir 分支。

### 1.8 并行开发互相踩
- **症状**：两个人同时构建/运行，一方突然出现对方的窗口、数据目录被改、或者自己的进程被别人 kill；提交之后 `git show --stat` 里冒出别人的未跟踪文件。
- **成因**：共享 `bin`/`obj`、共享默认数据目录、有人用 `Stop-Process` 清场、`git add -A` 把整棵工作树当成自己的改动。四条都是「看起来无害」的操作。构建配置另有一个边界：`-c <自定义配置名>` 在解决方案级不成立，`dotnet build -c Lead` 报 `MSB4126: 指定的解决方案配置"Lead|Any CPU"无效`，因为 `NebulaLauncher.sln:24` 的 `SolutionConfigurationPlatforms` 只声明 `Debug|Any CPU` 与 `Release|Any CPU`；要隔离得对项目文件传 `-c`（`dotnet build src/NebulaLauncher.App/NebulaLauncher.App.csproj -c Lead`）。
- **规避**：四条硬规矩——构建在项目级传独立配置（解决方案级只能用 Debug/Release）；运行必须带 `--data-dir`；绝不 `Stop-Process` 别人的进程（含名字相同的）；提交按路径逐个 `git add <文件>`，不用 `-A`/`.`，误入后 `git reset --soft HEAD~1` 再 `git restore --staged <文件>` 修正。见 `CONVENTIONS.md` §8.8。

### 1.9 工作区子目录被文件权限锁成只读，构建写不进 `obj/`
- **症状**：`dotnet build` 报 `error MSB3061: 无法删除文件 "D:\agent_workspace\NebulaLauncher\src\NebulaLauncher.App\obj\Debug\net8.0\ref\NebulaLauncher.dll"。Access to the path 'D:\agent_workspace\NebulaLauncher\src\NebulaLauncher.App\obj\Debug\net8.0\ref\NebulaLauncher.dll' is denied.`，或 `error : Access to the path '...\tools\TestRunner\obj\<guid>.tmp' is denied.`；同一份代码换台机器就正常。
- **成因**：子进程按受限令牌写盘，而 ACL 上没有显式 Deny，所以「看权限」看不出问题。工作区根缺少当前用户的 `WRITE_OWNER`（`Get-Acl` 只剩 `TakeOwnership, Synchronize`），沙箱无法为子目录补授权，整棵工作树对 `dotnet`、`bash`、`gh` 全变成只读；同类拒绝也打掉测试宿主启动与 shell-out CLI 的管道（`failed to run git: pipe: Access is denied.`），所以别先怀疑代码。
- **规避**：用沙箱 ACL 诊断技能修一次（它备份原 DACL 并打印回滚命令）；复核方式是往 `src\NebulaLauncher.App\obj` 建一个探针文件，写不进去就还是权限问题。见 `CONVENTIONS.md` §0。

---

## 2. 异步与线程

### 2.1 `.Result` / `.Wait()` 把 UI 线程锁死
- **症状**：界面卡住不动，无响应；调试器里 UI 线程停在 `.Result`。
- **成因**：Avalonia 的同步上下文在 UI 线程上，被同步等待的异步方法要回到 UI 线程完成，而 UI 线程正等着它——经典死锁。它在低负载机器上偶尔「能过」，所以最容易被放行。
- **规避**：全链路 async/await，禁止 `.Result`、`.Wait()`、`GetAwaiter().GetResult()`（见 `CONVENTIONS.md` §3）。唯一例外是首帧前的同步预热 `src/NebulaLauncher.App/Services/StartupState.cs StartupState.LoadBeforeFirstFrame（:24）`：它成立的前提是被调用的异步方法全程 `ConfigureAwait(false)`，不依赖调用线程的同步上下文——前提一旦被破坏（新加的 `await` 忘了 `ConfigureAwait(false)`），这个例外立刻变成死锁源；改预热链路里的任何一次 `await` 都要复核这一点。

### 2.2 服务构造时不读盘，于是处处「未登录」
- **症状**：首页、启动页、实例页都显示未登录，但账户文件明明有内容；手动进设置页再返回，就好了。
- **成因**：账户服务不在构造时读盘（构造里做 IO 会拉长启动且可能死锁），首个读盘动作由页面触发。「谁先访问谁触发」在小范围里成立，一旦首帧渲染早于任何一次访问，整屏都是空态。
- **规避**：首帧前统一预热一次，见 `StartupState.LoadBeforeFirstFrame`，调用点在 `src/NebulaLauncher.App/App.axaml.cs StartupState（:56）` 内的首帧之前。回归用例 `tests/NebulaLauncher.Tests/AppStartup_StartupStateTests.cs AppStartup_StartupStateTests（:15）`。

### 2.3 改了外部文件但界面不生效
- **症状**：外部改写了设置 JSON 或导入了配置包，界面上的值没变；重启后有一定概率变对。
- **成因**：`LoadAsync` 是幂等的——它用内部 `_loaded` 标记短路，第二次调用直接返回内存值。设计上是对的（避免重复读盘、避免变更风暴），但「读盘」和「重新读盘」是两个需求。
- **规避**：需要重读时显式用 `ReloadAsync`，不要指望再调一次 `LoadAsync`。幂等判定见 `src/NebulaLauncher.Core/Settings/SettingsService.cs SettingsService.LoadAsync（:52）`；导入路径的正确调用见 `src/NebulaLauncher.Core/Transfer/ProfileBundleService.cs ProfileBundleService（:212）`。误用表现为「导入成功但没生效」，不报错。

### 2.4 把「暂停」当取消用
- **症状**：点了暂停，以为在途下载会中断；或者反过来，写了「暂停后立即清理队列」，结果在途任务继续往已清理的目录里写。
- **成因**：暂停是**闸门**不是取消。`Pause()` 只拦「启动下一个文件」，已经在跑的文件照旧下完；重试路径每次 attempt 都要重新过一次闸门。它不抛取消异常、不释放已占资源，所以任何「暂停即终态」的假设都会漏。
- **规避**：判定暂停语义以 `src/NebulaLauncher.Core/Downloads/DownloadQueue.cs DownloadQueue.Pause（:236）` 为准；外部批次（整包下载这类）不接受暂停，幂等复位点分散在多处，改动时四处都要看。回归用例 `tests/NebulaLauncher.Tests/CoreDownloads_PauseTests.cs CoreDownloads_PauseTests（:15）`。

### 2.5 批量操作卡死界面
- **症状**：批量启用 700 个模组时界面冻结 200ms+，鼠标指针变成等待。
- **成因**：逐个文件做磁盘判据（存在性、可写性）本身就是同步 IO，循环留在 UI 线程上时整段阻塞；文件数越大越明显。
- **规避**：判据取磁盘真实状态而不是内存里的返回值——`src/NebulaLauncher.Core/Mods/ContentBatch.cs ContentBatch.IsEnabledOnDisk（:86）`（三态：启用 / 禁用 / 不可判定），并把整个循环放进 `Task.Run`。只信内存返回值的写法在文件被外部改动后会说谎，见 5.8。

### 2.6 跨页预填「有时候带不过去」
- **症状**：从模组页跳到搜索页，目标实例偶尔是空的；先点别的页再回来看起来又对了。
- **成因**：页面切换内部是「点了就走」——`Navigate` 里 `_ = RunPageLifecycleAsync(...)` 丢弃了任务引用（`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.RunPageLifecycleAsync（:649）` 是它的实现）。目标页的初始化时机不由调用方控制，先 Navigate 再 Publish 就会在初始化之后才写入。
- **规避**：先 Publish 目标状态，再 Navigate；消费方取走即清（`Interlocked.Exchange`），避免同一份预填被两个页面读。

### 2.7 离开页面后定时器还在跑
- **症状**：切走后 CPU 仍被占用；日志页的跟随在别的页面时还在滚动；账户状态变化把已销毁的页面又拉起来一次。
- **成因**：订阅与计时是「激活期」语义，但代码里常常只在构造或首次加载时建一次，离开时不拆。事件订阅比定时器更难发现——它不占 CPU，只在有人触发时才炸。
- **规避**：成对出现：订阅在 `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs HomeViewModel.AttachAccountEvents（:432）` 这类激活入口，退订在离开与释放路径（`OnNavigatedFromAsync` / `Dispose(bool)`）必须都覆盖；日志跟随的定时器同样两条路都要停。

### 2.8 `async void` 与丢弃的 `Task` 吞异常
- **症状**：后台任务失败但没人报错，日志里一片安静，界面停在加载态。
- **成因**：`_ = SomeAsync()` 丢弃任务引用后，异常无人观察；`async void` 的异常直接打到未处理异常通道，进程崩或静默。两种写法的共同点是「调用方拿到控制权时已经失去了错误信号」。
- **规避**：内部触发的后台流程要么在方法内部 catch 并落到日志，要么返回可观察的任务；进入 `_ =` 丢弃之前确认它自己兜住了所有异常，并有终态复位（加载态不能只在成功路径里清）。位置：`src/NebulaLauncher.App/Views/WorldsView.axaml.cs WorldsView.OnImportModpackClick（:19）`（`async void` 事件处理器，异常只能靠方法内 try/catch 兜住）、`src/NebulaLauncher.App/Views/MainWindow.axaml.cs MainWindow.FlushAndCloseAsync（:310）`（同为 `async void`，:307 注释写明整段必须 try/catch）；同文件 `:337` 的 `_ = FlushSettingsAsync(reason)` 是丢弃任务实例。

### 2.9 `InvalidatePageCache()` 丢弃当前页，但不走离开生命周期
- **症状**：在设置页改完数据后需要重载当前页，重载后离开钩子没跑：该停的定时器还在跑、该退的订阅还在。同一份代码在正常切页时表现正常，只有走「失效重载」这条路才漏。
- **成因**：`MainWindowViewModel.InvalidatePageCache` 先清空页面缓存，再把 `CurrentPage` 置 null 并重新导航；此时「上一页」已经不存在，`OnNavigatedFromAsync` 永远不被调用。释放与离开被当成一件事，但只有后者会走生命周期钩子。
- **规避**：需要重载当前页时，先显式 await 一次 `OnNavigatedFromAsync` 再清缓存；页面里的订阅与计时器不能只挂在这一条路径上，`Dispose(bool)` 必须再兜一层。位置：`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.InvalidatePageCache（:327）`。

### 2.10 页面生命周期 fire-and-forget，连点导航就交错
- **症状**：快速连点导航时上一页的离开钩子与下一页的进入钩子交错执行，偶发「页面已销毁仍在更新」、加载态卡住、日志出现重复初始化。
- **成因**：导航路径把生命周期流程当丢弃任务启动，调用方不 await，两次导航的钩子并发跑；既没有串行队列，也没有取消令牌。
- **规避**：导航入口串行化（同一时刻只允许一个生命周期流程在跑，新导航取消旧的），或者在钩子内部自行节流；不要假设丢弃任务之后钩子按顺序完成。位置：`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.RunPageLifecycleAsync（:649）`。

### 2.11 页面订阅只在 `Dispose(bool)` 退订，而缓存回收不调 `Dispose`
- **症状**：账户切换或登录后，已经离开的账户页仍在响应账户变化事件：重复刷新、日志重复、缓存被回收后偶发对象已释放类异常。
- **成因**：`AccountsViewModel` 在构造函数里订阅 `AccountsChanged`，退订只写在 `Dispose(bool)` 里；而主窗口清空页面缓存时只丢引用、不调页面的 `Dispose`，订阅便永久留在服务上。
- **规避**：激活期建立的订阅，离开路径也要拆（见 2.7）；或者让缓存回收显式 `Dispose` 页面。位置：`src/NebulaLauncher.App/ViewModels/AccountsViewModel.cs AccountsViewModel.Dispose（:265）`、`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.InvalidatePageCache（:327）`。

### 2.12 `IAccountService.ReloadAsync` 没有生产调用方
- **症状**：手改 `accounts.json` 或换账户后界面账户列表不刷新，重启才生效；读代码时以为「Reload」会自动发生，排查方向跑偏。
- **成因**：接口上有 `ReloadAsync` 且有实现，但生产代码零调用；`AccountsViewModel` 里同名的 `ReloadAsync()` 是它自己的私有方法，与接口无关。接口留着一个没人调的方法，会让人误判存在刷新路径。
- **规避**：需要刷新账户状态就显式调一次并补用例锁住，或者直接删掉这个悬空方法，别让接口承诺不存在的自动刷新。位置：`src/NebulaLauncher.Core/Abstractions/AccountServices.cs IAccountService.ReloadAsync（:47）`、`src/NebulaLauncher.Core/Accounts/AccountService.cs AccountService.ReloadAsync（:108）`。

---

## 3. 文件与路径

### 3.1 数据目录选错了，写不进去
- **症状**：首次启动即报写入失败，或者设置能改但保存后重启丢失。
- **成因**：进程以受限完整性级别运行时，用户目录的写权限并不保证；ACL 上也没有显式 Deny，所以「看权限」看不出问题。用「写一个随机临时文件试试」判断可用性更糟——它可能在别处成功、在目标目录失败。
- **规避**：按固定回退链选根：`%APPDATA%` → `%LOCALAPPDATA%` → `<exe 目录>\data`；游戏目录 `%APPDATA%\.minecraft` → `<数据根>\minecraft`。用户显式指定的目录不回退（他要的就是那里，失败就报错）。实现见 `src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths（:19）` 与 `src/NebulaLauncher.Core/Paths/PathProbe.cs PathProbe（:11）`。

### 3.2 用户数据混进 settings.json
- **症状**：收藏、账户、窗口状态这类数据一旦写进 `settings.json`，出现「手动编辑后整份设置被覆盖」或「导出设置把他人的账户带走了」。
- **成因**：`settings.json` 是「配置」，不是「数据」。两者生命周期不同：配置可以导出、可以被用户手改；数据不行。混在一起后，任何一次整份写回都会连数据一起重置。
- **规避**：独立文件 + 原子写（写 `.tmp` 再 `File.Move` 覆盖），坏文件静默重建而不是抛到界面上。示例 `src/NebulaLauncher.Core/Versions/VersionFavorites.cs VersionFavorites（:83）`。

### 3.3 实例锁：锁错了范围、或者杀错了人
- **症状**：同一实例能被启动两次并互相破坏存档；或者上一次异常退出后实例永久「正在运行」。
- **成因**：锁的对象选错（锁进程名、锁全局）就会误伤；存活判定只看 PID 会因 PID 复用而永久锁死；写入不是原子的会留下半个锁文件。
- **规避**：锁按**游戏目录**维度；存活判据是 PID + 启动时间（容差 1 秒，`src/NebulaLauncher.Core/Instances/InstanceLockService.cs InstanceLockService（:21）`）；写入用唯一命名的 tmp 再 `File.Move`（不覆盖）；释放只删自己持有的令牌；读失败一律当僵死（宁可放行也不要永久锁死）；锁文件建不出来时退化成 no-op 放行。回归用例 `tests/NebulaLauncher.Tests/CoreInstances_LockTests.cs CoreInstances_LockTests（:19）`。

### 3.4 清理把不该删的删了
- **症状**：清理缓存后实例或存档消失；或者反过来，符号链接目标被连带删除。
- **成因**：拼出来的路径没规范化就做「以数据根开头」判断，`..` 与大小写差异都能绕过；`ReparsePoint` 走的是链接目标，删的是链接之外的东西。
- **规避**：先 `Path.GetFullPath` 规范化，再要求结果严格位于数据根之下（Windows 上用 `OrdinalIgnoreCase` 比较），跳过 `ReparsePoint`；日志按最后写入时间保留 7 天，缓存类别排除 `loader-install`（安装器临时目录，删了会打断在跑的安装）。实现 `src/NebulaLauncher.Core/Maintenance/StorageService.cs StorageService（:26）`，回归用例 `tests/NebulaLauncher.Tests/CoreMaintenance_StorageTests.cs CoreMaintenance_StorageTests（:14）`。

### 3.5 文件名判定踩 Windows 的三个坑
- **症状**：用户建了一个叫 `CON` 的实例，之后目录操作全失败；或者判断「是不是 8.3 短名」时把 `MyMod~12.jar` 判错。
- **成因**：三件事同时成立——`$` 是合法文件名字符（不能按正则元字符思路排除）；保留名必须**整名**比较（`CON.txt` 也非法）；8.3 短名的序号可以多位（`~\d+` 而不是 `~\d`）。
- **规避**：统一走 `src/NebulaLauncher.Core/Paths/PathSafety.cs PathSafety.ShortNamePattern（:61）` 这类集中判定，别在业务代码里手写正则。

### 3.6 实例目录：存相对名、不缓存绝对路径
- **症状**：换了数据根之后旧实例全部指向不存在的位置；或者扫描实例时凭空多出一堆空目录。
- **成因**：绝对路径被写进数据文件后，数据根一变全部失效；扫描过程顺手建目录会把「不存在的实例」变成「空实例」。
- **规避**：实例目录只存相对名，绝对路径每次现算（`src/NebulaLauncher.Core/Instances/InstanceFolders.cs InstanceFolders（:19）`）；扫描是只读操作，不建目录；内容页一律经实例上下文取目录（`src/NebulaLauncher.Core/Instances/InstanceContext.cs InstanceContext（:18）`，见 6.D3）。

### 3.7 启动参数里的反斜杠被吃掉
- **症状**：Windows 路径参数在传给游戏时反斜杠消失或成对解释错，游戏读不到资源。
- **成因**：上游命令行解析器按 shell 规则处理反斜杠（`\\"` 之类的转义），Windows 路径里的 `\` 与 `"` 撞上就改写。用它解析用户自定义 JVM 参数会静默改内容。
- **规避**：自己实现切分，只认双引号、不做反斜杠转义：`src/NebulaLauncher.Core/Launching/LaunchArgumentSplitter.cs LaunchArgumentSplitter.Split（:18）`。回归用例 `tests/NebulaLauncher.Tests/CoreLaunch_ArgumentSplitterTests.cs CoreLaunch_ArgumentSplitterTests（:10）`。

### 3.8 安装器报临时目录不存在
- **症状**：Forge/NeoForge 安装器启动即失败，报临时目录不可用。
- **成因**：受限环境下系统临时目录不可写（同 1.7），安装器自己去那儿建目录就失败。
- **规避**：安装期把 TMP/TEMP 指到数据根下的 `cache/loader-install`，安装结束随缓存清理。实现 `src/NebulaLauncher.Core/Installing/NebulaGameInstaller.cs NebulaGameInstaller（:37）`；该目录已被缓存清理排除（见 3.4）。

### 3.9 日志第一行和上一行粘在一起
- **症状**：日志里两条记录挤在同一行，形如 `...[12:00:01] 上一条消息[12:00:02] 新消息`。
- **成因**：日志以追加模式打开，若上一次进程退出时最后一行没有换行符，新进程的第一条写入直接接在末尾。只在「跨进程、且上一次以半行结束」时出现。
- **规避**：首次写当日日志前补一个换行，且只检查一次（不能每条都检查，会拖慢写入）：`src/NebulaLauncher.Core/Logging/FileLogger.cs FileLogger.EnsureTrailingNewline（:83）`。

### 3.10 日志跟随读错内容
- **症状**：日志页把同一行显示两遍；或者文件被截断后界面停在旧内容；或者跟随一次读进几十 MB 卡住界面。
- **成因**：跟随实现要同时处理四种边界——文件以半行结束（不能推进偏移，否则该行永远读不全）、文件变短（截断）、偏移前一个字节不是换行（被轮转，内容已不是原来那段）、单次读取量无上限。
- **规避**：判定集中在 `src/NebulaLauncher.Core/Logging/LogFollower.cs LogFollower（:24）`：半行不推进偏移，`length < offset` 判截断，`offset-1` 非换行判轮转，单次读取封顶 256KB。回归用例 `tests/NebulaLauncher.Tests/CoreLogs_FollowTests.cs CoreLogs_FollowTests（:11）`。

### 3.11 密码明文落盘、或者被导出带走
- **症状**：配置文件里出现代理密码明文；或者导出的配置包在别人机器上还原出了自己的代理凭据。
- **成因**：磁盘上只应存密文（`dpapi:` / `b64:` 前缀），但内存对象里是明文；任何「把字段序列化出去」的路径都会带出明文，包括配置包导出与设置导出两处。
- **规避**：保存时用副本脱敏，解密失败按「未设置」处理（换机器后 DPAPI 必然解不开）；配置包导出必须同时剥掉 `proxyPassword` 与 `proxyPasswordProtected` 两个键，只剥一个等于没剥。实现 `src/NebulaLauncher.Core/Net/ProxyCredentialStore.cs ProxyCredentialStore（:14）`，键白名单见 5.7。

### 3.12 配置包把不该带的东西带走了
- **症状**：把配置包发给别人，对方机器上出现了自己的账户；或者导入后本地设置被整份覆盖。
- **成因**：导出走的是「复制整个数据目录」的实现，账户文件天然在里面；导入走的是「整体替换」，于是本机私有配置（窗口尺寸、缩放、主题这类机器相关项）被对方的值覆盖。
- **规避**：配置包绝不含 `accounts.json`；设置导出剔除 4 个机器相关键；导入设置按 9 个白名单键逐键覆盖，不整份替换。实现 `src/NebulaLauncher.Core/Transfer/ProfileBundleService.cs ProfileBundleService（:212）`，键集断言见 5.7。

### 3.13 `resourcepacks/<名字>/` 文件夹形态不在扫描范围
- **症状**：把资源包以文件夹形式放进实例的 `resourcepacks/` 下，启动器里看不到；把同样的内容压成 `.zip` 放进去却能识别。
- **成因**：内容扫描只枚举文件，不枚举目录；资源包的扩展名判据只认 `.zip` 与 `.resourcepack`。目录本身进不了候选集，于是被静默跳过——不报错、不提示，最容易误判成「用户放错位置」。
- **规避**：识别资源包时把「目录形态」当独立分支处理（列目录、按 `pack.mcmeta` 判有效），不要指望在扩展名过滤上补特例；要支持就先列目录再过滤。位置：`src/NebulaLauncher.Core/Mods/ContentService.cs ContentService.ScanFiles（:1193）`、`src/NebulaLauncher.Core/Mods/ContentService.cs ContentService.IsContentFile（:1252）`。

### 3.14 唯一化命名是「先查后建」，并发下 TOCTOU
- **症状**：两个导入或安装流程并发时，偶发一方被覆盖，或者落盘时报「文件已存在」；单线程跑一百次都复现不出来。
- **成因**：唯一化流程先循环查「名字是否已被占用」，再在之后的步骤里创建目录；检查与创建之间没有原子占位，两个流程可以同时判定同一个名字为空闲。
- **规避**：用「原子占位」做唯一化——先创建，成功者胜，撞了就换下一个名字重试；不要靠先查后建。位置：`src/NebulaLauncher.Core/Backups/ModpackService.cs ModpackService.ResolveUniqueVersionName（:1798）`、`src/NebulaLauncher.Core/Backups/ModpackService.cs ModpackService.IsVersionNameTaken（:1843）`。

### 3.15 整包安装成功 ≠ 可启动：首启才补 client jar
- **症状**：整合包安装完成后首次启动，预检报一条「加载器安装可能不完整」的 Warning，日志显示临时下载约 20 MB；断网时首启直接失败。
- **成因**：整包安装写继承版 `version.json` 时不写 `jar` 字段，补客户端 jar 这一步被推迟到首次启动之前、由启动库完成。安装流程结束时文件并不齐，所以「安装成功」不代表「能启动」。
- **规避**：预检把它当已知 Warning 呈现而不是错误，修复动作指向 `CrashAnalyzer.Actions.RepairVersion`；要让离线首启可用，就得在安装阶段预取 client jar，别留给首启。位置：`src/NebulaLauncher.Core/Backups/ModpackService.cs ModpackService.WriteInheritedVersionJsonAsync（:584）`、`src/NebulaLauncher.Core/Launching/PreflightChecker.cs PreflightChecker.CheckLoaderAsync（:417）`。

### 3.16 裁剪发布下 System.Text.Json 反射序列化被整关掉
- **症状**：便携版（`portable` 形态）里点「保存设置」弹红字 `保存设置失败：Reflection-based serialization has been disabled for this application. Either use the source generator APIs or explicitly configure the 'JsonSerializerOptions.TypeInfoResolver' property.`，设置不落盘；同一份代码在开发构建与 `minimal` 形态下正常，本地怎么点都复现不出来。凡走 `JsonSerializer` 的路径一起坏：设置、账户、实例、整合包、备份、Java 缓存、收藏、服务器列表。读取路径更静默——`src/NebulaLauncher.Core/Settings/SettingsService.cs SettingsService.LoadAsync（:52）` catch 之后把坏文件备份、回退默认值、只留一行 `_log.Warn`，用户看到的是「设置被重置」，看不出是序列化问题。
- **成因**：这是 System.Text.Json 在「反射序列化不可用」时抛的固定文案，判据是该进程的 `JsonSerializer.IsReflectionEnabledByDefault == false`。仓内没有任何一处设置这个开关，所以开关来自发布形态：`build.ps1` 的 `portable` 分支在命令行传 `-p:PublishTrimmed=true`，覆盖了 `src/NebulaLauncher.App/NebulaLauncher.App.csproj` 里写死的 `<PublishTrimmed>false</PublishTrimmed>`；开发构建与 `minimal` 不裁剪，反射元数据齐全，于是「代码看着没问题」。直觉修法全部无效——给某个 `JsonSerializerOptions` 再加转换器、改 `JsonSerializerDefaults` 都救不了，转换器本身同样靠反射取元数据。报错入口：`src/NebulaLauncher.Core/Settings/SettingsService.cs SettingsService.SaveAsync（:121）`（`JsonSerializer.Serialize` 在 `:135`），异常文案在 `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel.SaveAsync（:2595）`（`:2633`）。
- **规避**（2026-10 已修完，见议题 #26）：落盘类型登记 `JsonSerializerContext`、调用点用 `options.Of<T>()` 取元数据（写法见 `CONVENTIONS.md` §3.1）；`src/NebulaLauncher.Core/NebulaLauncher.Core.csproj` 开 `<IsAotCompatible>true</IsAotCompatible>`，漏改在 `dotnet build` 期报 IL2026 / IL2072 / IL3050；发布自检第 2 关 `--selftest-json`（`src/NebulaLauncher.Core/Diagnostics/JsonSelfCheck.cs JsonSelfCheck.RunAsync（:32）`）在真实裁剪产物上再验七条落盘通路，新增落盘文件要同步加进 `JsonSelfCheck`。**「发布形态先别碰裁剪」这条路没采纳**：`portable` 必须发。反证：摘掉 `SettingsJsonContext.DefaultOptions` 的 `TypeInfoResolver` 再发一次 portable，`--selftest-json` 立刻 `json-selfcheck.FAIL.unexpected;NotSupportedException: JsonTypeInfo metadata for type 'NebulaLauncher.Core.Models.AppSettings' was not provided by TypeInfoResolver of type '<null>'.`、`summary=0/1`、exit 4——闸门确实能抓到，不是摆设。

### 3.17 `JsonSerializerContext.Default` 会换掉你的 options 口径
- **症状**：为修裁剪把调用点从「传自己的 options」改成 `JsonSerializer.Serialize(x, SomeJsonContext.Default.T)`，`dotnet build` 0 警告 0 错误，但单测大面积失败：`settings.json` 写出 PascalCase（`MinecraftDirectory`）而不是 camelCase，枚举写成数字，`downloadSource` 往返全变 `Mirror`，带注释 / 尾逗号的旧配置读不回，Java 缓存、收藏、回收站、令牌全部读不回。
- **成因**：`JsonSerializerContext.Default` 携带的是**生成器默认口径**的 `JsonTypeInfo`；`JsonSerializer.Serialize(value, JsonTypeInfo)` 用的是 typeInfo 自带那份 options，调用点传不进去。生成器默认口径没有 camelCase 策略、没有 `PropertyNameCaseInsensitive`、不挂 `JsonStringEnumConverter`、不忽略注释与尾逗号——落盘格式当场变，旧文件当场读不回。「context 只提供元数据、开关靠传参」这句话只对非泛型重载 + context 工厂成立，在这个重载上不成立。
- **规避**：要保留原口径就用 `options.GetTypeInfo(typeof(T))` 取回 `JsonTypeInfo<T>`：`src/NebulaLauncher.Core/Serialization/JsonMeta.cs JsonMeta.Of（:30）` 提供 `SomeOptions.Of<T>()`，元数据由 context 的 `TypeInfoResolver` 提供、按 (options, 类型) 缓存，不经过反射也不会被裁剪掉。只有「原来根本没传 options」的调用点才该用 `Context.Default.T`（口径本来就一致）。

---

## 4. UI 与渲染

### 4.1 横向 StackPanel 放进 `*` 列，兄弟元素消失
- **症状**：标题正常，副标题在某个宽度区间里完全看不到；改窗口大小又出现。
- **成因**：横向 `StackPanel` 的可用宽度不传递给子元素——它按「要多少给多少」测量，放进 `*` 列后列宽分配与内容互相压制，最终把后面的元素压成 0 宽。在宽屏下所有内容都不缺空间，所以肉眼测不出来。
- **规避**：列宽改 `Auto,*`，文本加 `TextTrimming`；改动布局必须做 1024 宽截图（1240 宽下这个坑不会出现）。截图流程见 `CONVENTIONS.md` §8.4。

### 4.2 样式 setter 打不过构造函数里的本地值
- **症状**：给控件写了主题样式，运行后部分控件完全不变；把样式搬到更具体的选择器上仍然无效。
- **成因**：Avalonia 的优先级里本地值高于样式 setter，构造函数里赋的值就是本地值。样式系统没错，是它在正确地输给一个看不见的赋值。
- **规避**：控件里不要在构造函数写死视觉参数，或改用可控的属性/绑定。换主题时只有三处必须单独处理（`src/NebulaLauncher.App/Controls/AuroraBackground.cs AuroraBackground（:22）`、`src/NebulaLauncher.App/Controls/RingProgress.cs RingProgress（:13）`、ServersView 的 MOTD 协议色），其余换一套资源字典即可。

### 4.3 `ColumnDefinition.Width` 绑不上 ViewModel
- **症状**：想把两段进度条按比例分配宽度，绑定 `ColumnDefinition.Width` 后毫无反应，也没有绑定错误提示。
- **成因**：`ColumnDefinition` 不在可视树上，拿不到继承的数据上下文。现象容易误判成「控件没有 DataContext」。
- **规避**：不用列宽表达比例。内存三段条的做法是叠两条 `ProgressBar`（下层总量、上层已用），由视图模型算宽度。位置：`src/NebulaLauncher.App/Views/SettingsView.axaml SettingsView（:538）`（538-550 的注释块说明「两条 ProgressBar 叠在同一个格子里」而不是拼 Border 宽度）、`src/NebulaLauncher.App/Views/SettingsView.axaml SettingsView（:552）`（同一个 Grid 里三条 `<ProgressBar Minimum="0" Maximum="1"`：蓝/红按 `IsMemoryOverCommit` 互斥可见，黄条叠在上层）。

### 4.4 用夹过的比例相加判超额，永远不会触发
- **症状**：内存分配明显超额，界面却不报警；或者反过来，刚好卡在阈值上抖动。
- **成因**：每段的比例都做过 clamp（超过 100% 会压回 100%），把压过的比例相加比较，超额部分已经被丢掉。
- **规避**：比字节，不比比例——`游戏堆 + 已用 > 总量` 这类判据直接用字节量。实现 `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs SettingsViewModel.IsOverCommit（:1314）`。

### 4.5 `ListBox` 多选的 `SelectedItems` 绑定静默失效
- **症状**：`SelectionMode=Multiple` 下把选中项绑到视图模型，绑定不生效也不报错；截图里点击行没有任何视觉变化。
- **成因**：`SelectedItems` 不是可绑定的依赖属性，写上去编译通过、运行期什么也不发生。
- **规避**：用 `ItemsControl` + 每行一个复选框，选中状态做成行视图模型上的可绑定属性。同类问题在 `CONVENTIONS.md` §4 的编译绑定条目下另有说明（漏写 `x:DataType` 也是编译过、运行错）。

### 4.6 关窗口关出无响应进程
- **症状**：点关闭后窗口消失、进程还在；或者相反，本该退出却还在托盘里。
- **成因**：关闭有三条出口（关到托盘 / 真正退出 / 系统关机），三条的判定条件不同，散落在各处时必然漏掉一条。
- **规避**：判定收敛到一个纯函数：`src/NebulaLauncher.Core/Platforms/CloseBehavior.cs CloseBehavior.ShouldExitOnClose（:26）`。三个参数缺一不可——托盘没建起来时必须退回「退出」而不是「关到托盘」（否则无出口）；截图模式不建托盘也不拦关闭；系统关机一律放行。

### 4.7 窗口尺寸记错、越记越小
- **症状**：设了 `1024×660`，下次启动变成 `1039×697`；或者最小化状态下关闭，下次以最小化尺寸打开。
- **成因**：两个独立原因——尺寸参数在有的场景下是客户区下限而不是外框，多出边框与标题栏；在 `WindowState != Normal` 时保存会把最小化尺寸写进配置。
- **规避**：首帧前读设置（截图参数 `--size` 优先），只在 `WindowState == Normal` 时更新；关闭记忆时两端都不写。判定 `src/NebulaLauncher.Core/Platforms/WindowMemory.cs WindowMemory.ShouldPersist（:21）`。

### 4.8 刷新键名撞车
- **症状**：按 F5 调用了某个页面的刷新，但调到了私有方法，或者七个页面行为不一致。
- **成因**：多个页面各自有同名私有刷新方法，命令绑定指向谁取决于写在哪里，编译期不报错。
- **规避**：基类统一入口 `src/NebulaLauncher.App/ViewModels/PageViewModelBase.cs PageViewModelBase.ReloadCurrentAsync（:38）`，不要再新增名为 `RefreshAsync` 的成员。

### 4.9 Esc 把用户正在输入的内容吞掉
- **症状**：在搜索框里按 Esc，本意是清空/退出输入，结果把整个页面关掉或回退。
- **成因**：Esc 处理挂在窗口级，输入控件拿到焦点时没有标志位让位。
- **规避**：Escape 的实现集中在一处并先判断焦点是否在输入控件：`src/NebulaLauncher.App/ViewModels/MainWindowViewModel.cs MainWindowViewModel.Escape（:510）`。

### 4.10 图标解码失败导致整行空白
- **症状**：某些模组/整合包的图标位置是空白，不是首字母占位。
- **成因**：图标链是两段（下载 → 解码 → 显示），解码器遇到不支持的格式时抛异常或返回坏值，把占位层也一起顶掉。实际数据里 Modrinth 的图标是 webp、CurseForge 有 jpg，扩展名不可靠。
- **规避**：解码失败返回空值，让首字母占位透出，绝不抛到界面层：`src/NebulaLauncher.App/Views/ModsView.axaml.cs PathToImageConverter（:22）`。

### 4.11 「跟随账户」提示换了账户仍是旧文案
- **症状**：切换活动账户后，重新打开同一个实例的版本编辑面板，「跟随账户」那一行提示还是上一个账户的说法；改动一下相关输入框才变成新的。
- **成因**：提示文本每次取值都实时读当前活动账户，但变更通知只在「跟随账户」开关或认证地址字段被 setter 改动时才重发；装载面板时把开关写成它已有的值（通常是 true），属性值没变就不触发通知，提示便不会重算。页面也没有订阅账户变化事件。
- **规避**：账户切换本身要触发提示重算（订阅账户变化，或让相关 setter 强制通知），不要把刷新押在「开关值变化」这一条路径上。位置：`src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs VersionsViewModel.EditorAuthServerHint（:1953）`。

### 4.12 账户类型文案三处各写一份，首页那份把三态压成二元
- **症状**：第三方验证账户在账户页显示「第三方验证」，切到首页同一账户的徽标却显示「离线账户」；微软与离线账户两页一致，只有第三方验证分叉。
- **成因**：账户类型是三态（离线 / 微软 / 第三方验证），三处界面各写一份文案判定，首页那份写成 `account.Type == AccountType.Microsoft ? "微软账户" : "离线账户"`，`Authlib` 与 `Offline` 一起落进 else。两份字面量并存时，改一处不会带动另一处，也不报错。
- **规避**：类型文案只允许一处映射——`src/NebulaLauncher.App/ViewModels/AccountTypeDisplay.cs:14 AccountTypeDisplay.BadgeOf`，短标签由它派生 `src/NebulaLauncher.App/ViewModels/AccountTypeDisplay.cs:26 AccountTypeDisplay.ShortBadgeOf`，详情拼装同处的 `AccountTypeDisplay.DetailOf`；三处渲染改调它。用例 `tests/NebulaLauncher.Tests/AppAccounts_TypeDisplayTests.cs`、`tests/NebulaLauncher.Tests/AppHome_AccountBadgeTests.cs`，改回分散判定会变红。见 6.D12。

---

## 5. 测试与验证

### 5.1 加了属性但克隆漏了新字段
- **症状**：给启动参数模型加一个属性，某条启动路径没有带上它，游戏行为异常但完全不报错。
- **成因**：手写克隆容易漏；每加一个字段都可能漏一次，靠人看 diff 不可靠。
- **规避**：反射守护——逐属性比对，漏了就点名哪个属性没拷。实现 `src/NebulaLauncher.Core/Launching/GameLauncher.cs GameLauncher.CloneWithMaxMemory（:2258）`，守护用例 `tests/NebulaLauncher.Tests/CoreLaunch_LaunchOptionsCloneTests.cs CoreLaunch_LaunchOptionsCloneTests（:16）`。加字段时这个用例会红，这就是它的用途。

### 5.2 只断言「返回了匹配」挡不住请求形状错误
- **症状**：反查指纹的用例全绿，线上却一直是「未识别」；抓包才发现路径写成了单数、指纹按有符号发出。
- **成因**：假服务端只要「匹配到就算成功」，就会同时接受错误路径与错误编码；而调用方把 4xx 降级成「未识别」，错误被吞成正常返回值。两层叠加，测试与运行时都安静。
- **规避**：请求形状用例必须同时断言 path 与 body 的精确形状（这里是复数路径 + 无符号发送）：`tests/NebulaLauncher.Tests/CoreMods_CurseForgeFingerprintTests.cs CoreMods_CurseForgeFingerprintTests（:17）` 里的 `指纹反查_请求用复数路径且指纹按无符号发送`。

### 5.3 「只有一个入口」靠人守不住
- **症状**：新增第二个构造启动参数的入口后，行为与旧入口悄悄分叉；很久以后才发现某个选项只在一个入口生效。
- **成因**：分散构造的成本由未来的自己承担，代码评审看不出来，跑起来也不报错。
- **规避**：源码守护——断言唯一构造点只出现在工厂里，别处出现 `new LaunchOptions` 即失败。守护用例 `tests/NebulaLauncher.Tests/AppLaunch_OptionsFactoryTests.cs AppLaunch_OptionsFactoryTests（:16）`，唯一工厂 `src/NebulaLauncher.App/Services/LaunchOptionsFactory.cs LaunchOptionsFactory（:24）`（见 6.D4）。

### 5.4 分页终止判据依赖 pageSize
- **症状**：某个接口一改默认每页条数，抓取就提前结束或死循环；换成另一个服务端又正常。
- **成因**：用「请求的 pageSize」推进行号，而服务端实际可能少给（限额、过滤、末页不满）；一旦实际返回少，行号推进过多直接跳过内容，推进过少则原地打转。
- **规避**：用服务端实际返回条数推进（`index += 实际条数`，直到 `index >= totalCount`）；再叠按 Id 去重、双上限防死循环、`totalCount <= 0` 退回单页。合并路径见 `src/NebulaLauncher.Core/Mods/CrossSourceSearch.cs CrossSourceSearch.Merge（:357）`，用例在 `tests/NebulaLauncher.Tests/CoreMods_CurseForgePagingTests.cs`。

### 5.5 `Select-String` 的 `**` 只等价一层目录
- **症状**：全仓搜索某个符号，结论是「只有一处」，实际有三处；据此改出来的结论是错的。
- **成因**：`-Path 'src\**\*.cs'` 里 `**` 不是一个任意深度的通配，只等价一层目录。漏掉更深的文件不会有任何提示。
- **规避**：涉及「全仓有几处」的核实，用递归枚举显式喂给 `Select-String`（`Get-ChildItem -Recurse -Include *.cs src | Select-String -Pattern 'new LaunchOptions'`），或直接用 grep 工具；凡由搜索结果支撑的结论（尤其是「只有一处」「不存在」），换一种搜索方式复核一次。

### 5.6 测试运行器的 SKIP 不是通过
- **症状**：用例明明没跑，报告里也没有失败，于是当成通过；或者带 `null` 参数的用例直接报 `TargetParameterCountException: Parameter count mismatch.`。
- **成因**：`MemberData` / `ClassData` / `IClassFixture` 这类用例在简化运行器里可能打印 SKIP；`[InlineData(null)]` 被当成「没有参数」而不是「一个 null 参数」；退出码语义与常见预期不同（有失败为 1），且默认不做输出详细化。
- **规避**：以 `tools/TestRunner/Program.cs` 的输出语义为准：SKIP 要人工确认原因，别把「没跑到」当「过了」；带 `null` 的场景写成独立 `[Fact]` 或换哨兵值，别用 `[InlineData(null)]`；日常按类名过滤跑单类。见 `CONVENTIONS.md` §8.2。

### 5.7 白名单键集必须写死在测试里
- **症状**：导出的配置包多带了一个键，直到别人机器上被覆盖才发现。
- **成因**：白名单如果用「遍历现有键再过滤」实现，新增键会自动通过——白名单变成了黑名单的补集，保护消失。
- **规避**：把 9 个键的集合字面量写进测试断言，导出/导入任一侧多一个或少一个都要红。用例 `tests/NebulaLauncher.Tests/CoreTransfer_ProfileBundleTests.cs`。

### 5.8 样式重构用肉眼验收会漏
- **症状**：合并样式类之后界面「看起来一样」，但某个页面的圆角或间距变了，很久没人发现。
- **成因**：肉眼对像素级改动不敏感，尤其是渐变、阴影、亚像素对齐。
- **规避**：重构前后同一参数截图逐像素对比，通过标准是「差异像素数为 0」或「最大通道差不超过 4」。截图见 `CONVENTIONS.md` §8.4，视觉基线同在该节。批量操作的正确性同理——断言磁盘状态而不是返回值（`tests/NebulaLauncher.Tests/AppMods_BatchTests.cs AppMods_BatchTests（:14）` 里的 `批量禁用_被占用的文件计入失败且其余项继续`）。

### 5.9 文档里的代码引用会静默漂移
- **症状**：文档写着「某接口声明在 Abstractions」「某方法是 `SaveAsync`」「`docs/CODE.md:49` 那段」——代码一改，路径还打得开，符号却早改名或挪了位置；人工 `grep` 也搜不出来，因为文档里只是一段纯文本。
- **成因**：Markdown 里的路径、符号与行号没有编译期校验，改名重构时文档不会被带上；行号更是每加一行就失效。
- **规避**：代码引用统一写成反引号单元「仓库相对路径[:行号] 符号名」，提交前跑 `tools/DocsCheck`——路径与符号都必须真实存在，同时卡住行数预算（单份 400 行 / 总量 1000 行）。见 `CONVENTIONS.md` §8.3。

---

## 6. 架构级裁决

已经定案的取舍。改之前先读「反悔代价」一列——那一列通常比「为什么」更长。

| # | 决定了什么 | 为什么 | 反悔代价 |
| --- | --- | --- | --- |
| D1 | 单一 TFM `net8.0`，禁 `net8.0-windows`、禁 `#if WINDOWS` | 平台差异收敛到平台抽象层，业务代码一套；条件编译会让同一条路径在两个平台上分别腐化 | 跨平台能力要从平台层重建，业务层里所有平台分支都得重新审一遍；App 层引用面几乎全要动 |
| D2 | 实例目录 = 版本目录 `<game>/versions/<版本名>` | 一个实例就是一组「版本 + 加载器 + 模组」的完整快照，克隆/回收/换加载器都变成目录操作 | 旧布局迁移 + 克隆、回收站、换加载器、文件复用、整合包导入五处连带重写；用户已有的实例需要迁移脚本 |
| D3 | 内容页一律经 `IInstanceContext` 取目录，全局目标的版本身份必须为空 | 页面既能看全局也能看实例时，「当前目录」有两个来源，静默分叉几乎必然发生 | 模组页与实例内容会立刻出现两套路径解析；已有用例（含全局目标为空的断言）需要重写 |
| D4 | 启动参数只有一个构造点 `LaunchOptionsFactory` | 多入口必然分叉，且分叉不报错（见 5.3） | 再多加一个入口会立刻被源码守护挡住；要么改守护放宽（等于放弃保护），要么回工厂加分支 |
| D5 | 服务端插件两个入口刻意不一致：搜索拒绝、整合包落 `plugins/` | 服务端整合包的结构与客户端不同，把服务端插件塞进 `mods/` 会让客户端启动即崩；宁可拒绝也不产生半残结果 | 要统一就必须先做出服务端专用的装载路径；否则两种情况之一必然「能装上但跑不起来」，错误往「不崩」方向漏是刻意的 |
| D6 | `LoadAsync` 幂等，重读一律走 `ReloadAsync` | 重复读盘会引出变更风暴与竞态；幂等让调用方不必关心调用次数 | 幂等短路一旦被删，六处调用方每次启动都要重读；若反过来把所有调用点改成 `ReloadAsync`，启动会变慢且会触发多余的重建 |
| D7 | 下载暂停用闸门而不是取消令牌 | 取消会中断在途文件、留下半截产物；闸门只挡「启动下一个」 | 改成取消就要补「半截文件恢复」与「暂停期间进度归属」，排队语义会整体变化 |
| D8 | 实例运行锁按游戏目录 + PID + 启动时间 | 按目录才防得住多开破坏同一份存档；加启动时间才不会被 PID 复用永久锁死 | 退回「只看 PID」会出现永久锁；退回「锁进程名」会误伤其他实例 |
| D9 | 下载源 Auto 降级状态只在内存；手动档只提示、不换源 | 「今天镜像慢」不该变成永久配置；用户手动选定的源要免疫自动降级 | 落盘就等于把一次性网络抖动写成长期设置；去掉自动降级则弱网环境完全不可用。手动档的失败计数同样是内存态：它只用来在状态行提醒「镜像看起来坏了、可在设置里改用自动或官方源」，当前使用的源一个字节都不换 |
| D10 | 内置 CurseForge API key，不再让用户填 | 反查与搜索对普通用户必须开箱可用 | key 随产物分发，必须换 key 得重新发布；同时要保证无 key 时不崩（错误往「不崩」方向漏） |
| D11 | Java 上限只在大版本级阻断，补丁号级仅提示 | 用具体补丁号做硬门槛，会在相邻补丁上误杀（8u320 硬挡会拦掉 8u322） | 改成精确版本比对就会重现误杀，且误杀发生在启动前，用户没有自救路径 |
| D12 | 账户类型文案只有一处映射 `AccountTypeDisplay`，短标签由长标签派生 | 三处各写一份判定时首页那份塌缩成二元，把「第三方验证」显示成「离线账户」（见 4.12）；两份字面量并存就还能各自漂移 | 拆回分散判定要改 `src/NebulaLauncher.App/ViewModels/HomeViewModel.cs` 的 `AccountBadge`、`src/NebulaLauncher.App/ViewModels/AccountsViewModel.cs` 的 `ActiveTypeLabel` 与 `TypeLabel` 三处，并失去「一处改名处处跟随」的保证；没有守护拦得住重新分叉 |

### 6.1 裁决与坑的联动

- D3 的反面就是 3.6：实例目录只在上下文里解析，绝对路径不缓存。
- D4 的守护在 5.3；放宽守护等于放弃这条裁决。
- D6 的反面就是 2.3：幂等不能靠「再调一次」绕过。
- D7 的反面就是 2.4：闸门语义下的暂停不是终态。
- D8 的反面就是 3.3：锁的三个组成缺一不可。
- D12 的反面就是 4.12：类型文案只有一个映射，短标签由长标签派生。
