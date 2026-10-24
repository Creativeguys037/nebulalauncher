# Nebula Launcher — 协作与代码规范

本文件对该仓库内所有协作者（人类与 AI）生效。改动代码前先读完。

## 0. 环境

本机 .NET SDK **不在系统 PATH**，位于 `C:\Users\MECHREUO\.dotnet8`。

每次开新 shell 后先：

```powershell
cd D:\agent_workspace\NebulaLauncher
. .\env.ps1
```

`env.ps1` 会设置 `DOTNET_ROOT` / `PATH` 并打印版本。之后 `dotnet` 命令才可用。

构建：

```powershell
dotnet build -v q --nologo          # 全量
.\build.ps1                          # 同上 + 可选 -Test / -Publish
```

测试：

```powershell
dotnet test --nologo -v q
```

## 1. 技术栈（已定，不得擅自更换）

| 层 | 选型 | 说明 |
|---|---|---|
| 运行时 | .NET 8 (`net8.0`) | 单 TFM。**不得**使用 `net8.0-windows` |
| UI | Avalonia 11.3.22 | 跨平台，无 WebView |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | 源生成器 `[ObservableProperty]` / `[RelayCommand]` |
| DI | Microsoft.Extensions.DependencyInjection 8.0.1 | 构造函数注入 |
| MC 核心 | CmlLib.Core 4.1.0 系 | 版本安装、启动、微软登录 |

**为什么单 TFM**：未来要出 macOS 版。所有平台差异走 `RuntimeInformation.IsOSPlatform(...)` 或
`IPlatformService` 的运行时分支，禁止 `#if WINDOWS` 之类编译期开关，禁止 `net8.0-windows` 专属 API
（`Microsoft.Win32.Registry`、`System.Drawing`、WinForms）。

## 2. 目录与写作用域

```
src/NebulaLauncher.Core/     纯业务层，零 UI 依赖，可单元测试
  Abstractions/              接口契约（已冻结，见第 5 节）
  Models/                    数据传输对象
  Accounts|Versions|Installing|Launching|Java|Mods|Instances|Downloads|Settings|Servers|Backups|Logging
src/NebulaLauncher.App/      Avalonia UI
  Themes/                    设计令牌与控件样式
  Controls/                  自绘控件
  Views/ + ViewModels/       页面
  Services/                  导航、对话框、Toast
  Platforms/                 平台实现（IPlatformService 的 Windows 版）
tests/NebulaLauncher.Tests/  xunit
```

**只改自己任务声明里的目录。** 跨目录需要改公共契约时，先在群里说明。

## 3. 代码规范

- 命名空间：`NebulaLauncher.Core.*` / `NebulaLauncher.*`（App 侧根命名空间是 `NebulaLauncher`，不是 `NebulaLauncher.App`）。
- 所有 IO / 网络方法带 `CancellationToken`，返回 `Task`，**不返回 `void` async**。
- **禁止 `.Result` / `.Wait()` / `GetAwaiter().GetResult()`**，UI 线程会死锁。
  **唯一例外**：`App/Services/StartupState.cs` 的首帧预热（设置与账户要在建窗口之前就在内存里，
  见 HANDOVER §6.20 / §6.71）。例外成立的前提是被调的 `LoadAsync` 全程 `ConfigureAwait(false)`，
  新增预热项时必须逐行确认这一点，否则就是真死锁。
- 异常只在上边界吞：服务内部捕获后写日志并返回结果对象，不把原始异常抛给 ViewModel。
- `null` 用 `?` 显式标注；集合返回 `IReadOnlyList<T>`，返回空集合而不是 `null`。
- 注释用中文，只写「为什么」，不写「做了什么」。公开 API 写 `///` 摘要。
- 不引新 NuGet 包。确需新增，先改 `Directory.Packages.props` 并在群里说。

## 4. XAML 规范

- `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>` 已开。
  **每个 View 必须在根元素写 `x:DataType="vm:XXXViewModel"`**，否则编译报错。
- 绑定走 `{Binding Property}`（相对 DataContext）；控件模板内用 `{TemplateBinding}` 或
  `{Binding $parent[Type].Prop}`。
- 控件样式集中在 `Themes/`，页面里不写行内颜色字面量。用 `{DynamicResource ...}` 取令牌。
- 不使用 `async void` 事件处理器以外的异步写法；`async void` 内必须 try/catch。

## 5. 已冻结的契约

`src/NebulaLauncher.Core/Abstractions/` 下的接口是 Core 与 UI 的共同边界。

- 实现方（Core）不得改签名，需要新增能力时**追加**方法并给默认实现。
- 消费方（UI）只依赖接口，不 `new` 具体实现。

## 6. DI 注册约定

每个模块在自己的目录下提供扩展方法，文件名与类名固定：

```
Core/Accounts/AccountServiceCollectionExtensions.cs   → IServiceCollection AddAccounts(this IServiceCollection)
```

统一在 `App/App.axaml.cs` 的 `ConfigureServices` 里调用。**不要直接改那个文件**，
在任务里说明「需要在 ConfigureServices 增加一行 XXX」，由 Lead 合并。

## 7. 验证门槛

提交前必须：

1. `dotnet build -v q --nologo` → 0 error。
2. 涉及逻辑的改动补 xunit 测试，`dotnet test` 通过（本机受限时用下面的 TestRunner）。
3. 涉及 UI 的改动，**必须截图自查**，确认无 XAML 绑定异常、无布局重叠、无文字裁切。
4. 报告里写清楚：改了哪些文件、跑了什么命令、输出是什么、哪些没验证。

### 7.1 测试：`dotnet test` 在本机不可用

本机沙箱拒绝 testhost 启动（`Process.EnableRaisingEvents` → `OpenProcess` → Win32Exception 5），
`dotnet test` / `dotnet vstest` 都会在启动阶段中止。改用进程内运行器：

```powershell
dotnet run --project tools/TestRunner/TestRunner.csproj -v q --nologo          # 全量
dotnet run --project tools/TestRunner/TestRunner.csproj -v q --nologo -- Crash # 按类名过滤
```

它反射测试程序集、复用 xunit 断言，支持 `[Fact]` 与 `[Theory]+[InlineData]`；
`MemberData` / `ClassData` / `IClassFixture` 会打印 SKIP 而不是静默忽略。
退出码：有失败为 1。

**测试项目引用范围**：`tests/NebulaLauncher.Tests.csproj` 同时引用 Core 与 App（App 侧开放了 `InternalsVisibleTo`）。
加 App 引用是为 `ShellProgress` 这类「节流逻辑与窗口生命周期强相关」的服务——下沉 Core 反而语义更差。
限制：**测试里不得触碰 Avalonia 类型**（`Window`、控件、`Dispatcher`、`Application.Current`），
只能测纯逻辑与接口替身。某条测试一旦需要 `Application.Current`，说明被测逻辑放错了层，应该往上抽纯函数。

### 7.2 UI 截图：用应用内渲染，不要截系统屏幕

`tools/shot.ps1`（背后是 `--screenshot` 启动参数）。它让应用把自己的视觉树渲染成 PNG，
与窗口焦点、遮挡、DPI 无关，落地结果可复现。

```powershell
# 一次性准备有内容的隔离数据（4 版本 / 7 模组 / 3 世界 / 2 账户 / 3 服务器）
.\tools\seed-demo-data.ps1 -DataDir D:\agent_workspace\.probe\data-<你的标识> -Force

# 截图
.\tools\shot.ps1 -Page versions -NoBuild -Configuration <你的配置> `
    -DataDir D:\agent_workspace\.probe\data-<你的标识> -Out D:\agent_workspace\.probe\v.png
```

可用页面键：`home` `versions` `download` `mods` `library` `accounts` `servers` `worlds` `logs` `help` `settings`。
主题键：`-Theme dark|light|system`（默认 `dark`）。两个主题都要截，见 `docs/DESIGN.md` §10 自检清单。

等价的手工命令：

```powershell
dotnet $bin\NebulaLauncher.dll --screenshot out.png --page home --delay 4000 --data-dir <dir>
```

截图失败时读 `<data-dir>\logs\*.log`；偶发一次 `0xE0434352` 是启动时序问题，重试即可。

### 7.3 并行协作的三条硬规则

多人/多 agent 同时改这个仓库时：

1. **构建配置隔离**：每人用自己的 `-c` 配置（`ui1` / `ui2` / `ui3` / `Lead` …），
   产物落在 `bin/<配置>/`，互不锁 obj。
2. **数据目录隔离**：每个实例必须带 `--data-dir`，否则会并发写同一份
   `accounts.json` / `servers.json` / `settings.json` 互相覆盖。
3. **不杀别人的进程**：禁止 `Stop-Process` 任何 `dotnet` / `NebulaLauncher` 进程。
   `--screenshot` 模式自己会退出。

构建偶发 `NebulaLauncher.Core.dll ... being used by another process` 时，等 10–20 秒重试，
不要删别人的 `obj/` `bin/`。

### 7.4 Debug 配置 XAML 缓存损坏

如果 `bin\Debug` 启动报 `No precompiled XAML found for NebulaLauncher.App`，说明 XAML IL 步骤
被中断过而增量构建跳过了它。删掉 `src/NebulaLauncher.App/obj/Debug` 与 `bin/Debug` 重建即可。

## 8. 提交纪律

- 一次只解决一个问题，改动尽量小。
- 不重排 import、不整体格式化、不改无关文件。
- 不删别人的代码来「让编译过」。
