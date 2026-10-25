# Nebula Launcher — 协作与代码规范

本文件对该仓库内所有协作者（人类与 AI）生效，改动代码前先读完。这里只写规范与禁令：项目是什么见 [docs/PROJECT.md](docs/PROJECT.md)，代码组成与调用链见 [docs/CODE.md](docs/CODE.md)，功能三态见 [docs/FEATURES.md](docs/FEATURES.md)，坑与裁决见 [docs/PITFALLS.md](docs/PITFALLS.md)，术语见 [GLOSSARY.md](GLOSSARY.md)。

## 0. 环境

本机 .NET SDK **不在系统 PATH**，位于 `C:\Users\MECHREUO\.dotnet8`。开新 shell 先：

```powershell
cd D:\agent_workspace\NebulaLauncher
. .\env.ps1        # 设置 DOTNET_ROOT / PATH 并打印版本，之后 dotnet 才可用
```

构建用 `dotnet build -v q --nologo`（或 `.\build.ps1`，可加 `-Test` / `-Publish`）。
测试**不用** `dotnet test`——本机沙箱拒绝 testhost 启动（`Win32Exception 5`），用 §8.2 的 TestRunner。

## 1. 技术栈（已定，不得擅自更换）

| 层 | 选型 | 说明 |
|---|---|---|
| 运行时 | .NET 8 (`net8.0`) | 单 TFM。**不得**使用 `net8.0-windows` |
| UI | Avalonia 11.3.22 | 跨平台，无 WebView |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | 源生成器 `[ObservableProperty]` / `[RelayCommand]` |
| DI | Microsoft.Extensions.DependencyInjection 8.0.1 | 构造函数注入 |
| MC 核心 | CmlLib.Core 4.1.0 系 | 版本安装、启动、微软登录 |

**为什么单 TFM**：未来要出 macOS 版。平台差异一律走 `RuntimeInformation.IsOSPlatform(...)` 或 `IPlatformService` 的运行时分支；禁止 `#if WINDOWS` 之类编译期开关，禁止 `net8.0-windows` 专属 API（注册表、`System.Drawing`、WinForms）。

## 2. 目录与写作用域

```
src/NebulaLauncher.Core/     纯业务层，零 UI 依赖，可单元测试
  Abstractions/              接口契约（已冻结，见第 5 节）
  Models/                    数据传输对象
  Accounts|Versions|Installing|Launching|Java|Mods|Instances|Downloads|Settings|Servers|Backups|Logging
src/NebulaLauncher.App/      Avalonia UI
  Themes/                    设计令牌（深 / 浅双字典）与控件样式
  Controls/                  自绘控件
  Views/ + ViewModels/       页面
  Services/                  导航、对话框、Toast、动效开关
  Platforms/                 平台实现（IPlatformService 的 Windows 版）
tests/NebulaLauncher.Tests/  xunit
tools/DocsCheck/             文档引用 / 行数预算 / 功能三态校验（第 8.3 节）
```

**只改自己任务声明里的目录。** 跨目录需要改公共契约时，先在群里说明。

## 3. 代码规范

- 命名空间：`NebulaLauncher.Core.*` / `NebulaLauncher.*`（App 侧根命名空间是 `NebulaLauncher`，不是 `NebulaLauncher.App`）。
- 所有 IO / 网络方法带 `CancellationToken`，返回 `Task`，**不返回 `void` async**。
- **禁止 `.Result` / `.Wait()` / `GetAwaiter().GetResult()`**，UI 线程会死锁。
  **唯一例外**：`src/NebulaLauncher.App/Services/StartupState.cs` 的首帧预热（设置与账户要在建窗口之前进内存）。
  例外成立的前提是被调的 `LoadAsync` 全程 `ConfigureAwait(false)`，新增预热项必须逐行确认，否则就是真死锁。
- 异常只在上边界吞：服务内部捕获后写日志并返回结果对象，不把原始异常抛给 ViewModel。
- `null` 用 `?` 显式标注；集合返回 `IReadOnlyList<T>`，返回空集合而不是 `null`。
- 注释用中文，只写「为什么」，不写「做了什么」。公开 API 写 `///` 摘要。
- 不引新 NuGet 包。确需新增，先改 `Directory.Packages.props` 并在群里说。

### 3.1 JSON 落盘代码的写法

裁剪发布（`-Mode portable`）关掉了 System.Text.Json 的反射元数据，裸反射调用只在真实产物里炸，编译期与单测都看不见（见 `docs/PITFALLS.md` 3.16、3.17）。改任何落盘 / HTTP JSON 代码时按四条写：

- **每个落盘类型在定义它的那个文件尾登记 `JsonSerializerContext`**，不集中到一处：`[JsonSerializable(typeof(AppSettings))] internal sealed partial class SettingsJsonContext : JsonSerializerContext { }`。被登记的类型必须是 `internal`（或 `public`）且位于命名空间层——source generator 引用不到 `private` 嵌套类型，也引用不到匿名类型。
- **调用点用 `options.Of<T>()` 取元数据**（`src/NebulaLauncher.Core/Serialization/JsonMeta.cs JsonMeta.Of（:30）`），例如 `JsonSerializer.Serialize(persisted, JsonOptions.Of<AppSettings>())`。这份 `JsonSerializerOptions` 必须挂 `TypeInfoResolver = new XxxJsonContext()`；**不要用 `SomeJsonContext.Default.T`**——它携带的是生成器默认口径，会把这份 options 的命名策略、大小写不敏感、枚举转换器与注释容错一起换掉（`docs/PITFALLS.md` 3.17）。
- **匿名类型不能直接序列化**：HTTP 请求体一律写 `internal sealed record`，字段名用 `[property: JsonPropertyName("...")]` 钉死——record 参数名默认按 PascalCase 落盘，而 API 要的是小写驼峰。
- **枚举转换器用泛型版本**：写 `JsonStringEnumConverter<DownloadSource>` 而不是 `JsonStringEnumConverter`（后者靠运行时反射枚举字段，AOT / 裁剪下过不了 IL3050）；手写转换器要排在通用转换器前面，否则 `"BmclApi"` 这类历史名会被当成未知值。

两道闸：`src/NebulaLauncher.Core/NebulaLauncher.Core.csproj` 开着 `<IsAotCompatible>true</IsAotCompatible>`，漏改在 `dotnet build` 期报 IL2026 / IL2072 / IL3050；发布自检第 2 关 `--selftest-json`（§8.5）在真实裁剪产物上再验七条落盘通路，新增落盘文件要同步加进 `src/NebulaLauncher.Core/Diagnostics/JsonSelfCheck.cs JsonSelfCheck.RunAsync（:32）`。

## 4. XAML 规范

- `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>` 已开。
  **每个 View 必须在根元素写 `x:DataType="vm:XXXViewModel"`**，否则编译报错。
- 绑定走 `{Binding Property}`（相对 DataContext）；控件模板内用 `{TemplateBinding}` 或 `{Binding $parent[Type].Prop}`。
- 控件样式集中在 `Themes/`，页面里不写行内颜色字面量，用 `{DynamicResource ...}` 取令牌。
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

统一在 `App/App.axaml.cs` 的 `ConfigureServices` 里调用。**不要直接改那个文件**，在任务里说明「需要在 ConfigureServices 增加一行 XXX」，由 Lead 合并。

## 7. 怎么画界面

写页面前读完本节。本节自足，不需要再翻别的文档。

### 7.1 设计语言：深空 / 玻璃 / 微光

- **深空**：主视觉背景是深色，接近黑但带蓝调（`#0A0E17` → `#05070C`），不用纯黑；内容浮在背景之上。
- **玻璃**：卡片与面板半透明、细描边、弱阴影。不堆厚重投影，靠 1px 亮描边区分层次。
- **微光**：强调色只用在需要引导视线的位置（主操作、当前选中、进度）。大面积彩色是错的。

**禁止**：纯黑 `#000000` 背景、纯白 `#FFFFFF` 大面积文字块；高饱和色块铺满卡片；圆角小于 8px 的卡片（标签 pill 除外）；一屏出现多于两种强调色。

### 7.2 两套主题

同一套设计语言有深色与浅色两个实现，键名完全一致（`src/NebulaLauncher.App/Themes/Tokens.axaml` 的 `ThemeDictionaries`）。浅色不是反色，而是同一份层次关系的另一种表达：

| 层 | 深色 | 浅色 |
|---|---|---|
| 窗口底 | 近黑带蓝 `#080B12` | 淡雾蓝 `#F4F6FB`（不是纯白） |
| 卡片 | 半透明白 6% | 纯白 + 1px 冷描边 |
| 侧栏 | 玻璃层 | `#E9EFF9` |
| 正文 | 近白 `#F2F6FC` | 墨蓝 `#0F1724` |
| 强调 | 亮蓝紫 | 更深一档的蓝紫（保证白底对比度 ≥ 4.5:1） |
| 光斑 | alpha 0.15–0.3 | alpha 0.07–0.11 |

写页面**不需要处理主题差异**——只要不写颜色字面量，两套自动生效。例外只有自绘控件与内嵌数据（星云背景、环形进度、服务器 MOTD 协议色）。

### 7.3 颜色：只用令牌

全部走 `Themes/Tokens.axaml` 的 `{DynamicResource ...}`，页面里不写颜色字面量。令牌分 `Dark` / `Light` 两套，键名相同；下表色值只描述深色。

| 令牌 | 用途 |
|---|---|
| `Surface.Base` / `Surface.Canvas` | 窗口底 / 内容区底 |
| `Surface.CardSolid` | 卡片底（配 `Border.card`） |
| `Surface.Raised` | 悬浮层、次级按钮 |
| `Surface.Hover` / `Surface.Pressed` | 交互态 |
| `Surface.Sunken` | 输入框、进度槽 |
| `Surface.Glass` / `Surface.Sidebar` | 玻璃层 / 侧栏底 |
| `Stroke.Subtle` / `.Default` / `.Strong` | 描边三档 |
| `Text.Primary` / `.Secondary` / `.Tertiary` / `.Muted` | 文字四档（`Muted` 只做装饰，浅色下对比度 2.65:1，不可用于正文） |
| `Text.OnAccent` | 压在主强调色 / 渐变上的文字 |
| `Accent.Primary` (`#4C6FFF`) | 主强调 |
| `Accent.Secondary` (`#A855F7`) | 次强调，只做渐变搭档 |
| `Accent.Soft` | 选中底色（8% alpha） |
| `Status.Success` / `.Warning` / `.Danger` | 状态色，配 `.Soft` 做底 |
| `Status.Danger.Edge` | 危险按钮悬停描边 |
| `Scrollbar.Thumb` | 滚动条滑块 |
| `Window.CloseHover` | 关闭按钮悬停底（两主题都用红，叉号用 `Text.OnAccent`） |

渐变：`Gradient.Accent`（蓝→紫，主按钮 / 进度）、`Gradient.Aurora`（蓝→紫→青，装饰）。

**新增令牌**：`Dark` 与 `Light` 两个字典各加一次。只加一边编译能过、切到另一主题才在运行期抛，所以两种主题都要截图。

### 7.4 排版

| 样式类 | 字号 | 用途 |
|---|---|---|
| `TextBlock.h1` | 34 | 页面主标题（少用） |
| `TextBlock.h2` | 26 | 区块大标题 |
| `TextBlock.h3` | 20 | 卡片标题 |
| 默认 | 14 | 正文 |
| `.secondary` | 14 | 次要正文（降色） |
| `.muted` | 12 | 说明、元信息 |
| `.caption` | 11 | 极小标签 |
| `.mono` | 12 | 路径、日志、UUID |

字重只有 `Normal` / `Medium` / `SemiBold` / `Bold` 四档，**不要用 Light**（深色底上发虚）。多行段落用 `TextWrapping="Wrap"` + `LineHeight="20"`。

### 7.5 间距与圆角

间距取 4 的倍数：`4 / 8 / 12 / 16 / 20 / 24 / 32 / 40`。卡片内边距 `18`（`Border.card` 已设）；卡片之间 `12`～`16`；页面左右边距 `30`（MainWindow 已提供）；区块标题与内容之间 `12`。

圆角：`Radius.Xs(4)` 标签 · `Radius.Sm(8)` 按钮 / 输入框 · `Radius.Md(12)` 列表项 / 导航 · `Radius.Lg(16)` 卡片 · `Radius.Pill(999)` 胶囊 / 进度条。

### 7.6 控件用法

```xml
<!-- 卡片 -->
<Border Classes="card">...</Border>
<Border Classes="card hoverable">...</Border>   <!-- 可点 / 可悬停 -->
<Border Classes="card accentEdge">...</Border>  <!-- 强调卡片 -->

<!-- 按钮 -->
<Button Classes="accent" Content="开始游戏" />
<Button Classes="ghost" Content="取消" />
<Button Classes="icon" Content="…" />
<Button Classes="pill" Content="筛选" />
<Button Classes="danger" Content="删除" />
```

### 7.7 页面结构

每个页面是一个 `UserControl`，顶部不重复画标题——标题由 MainWindow 的页面抬头区显示（`PageViewModelBase.Title` / `Subtitle`）。

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:NebulaLauncher.ViewModels"
             x:Class="NebulaLauncher.Views.HomeView"
             x:DataType="vm:HomeViewModel">
  <ScrollViewer>
    <StackPanel Spacing="16">
      ...
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

- **必须写 `x:DataType`**，编译绑定已全局开启，漏了直接编译报错。
- 页面根用 `ScrollViewer`，内容用 `StackPanel Spacing="16"` 纵向排列。
- 列表用 `ItemsControl` + `DataTemplate`，超过 50 项改用 `ListBox`（虚拟化）。
- 空状态必须实现：没有版本 / 没有模组 / 没有账户时显示引导文案，不留白屏。

### 7.8 动效

- 过渡时长一律从 `src/NebulaLauncher.App/Services/Motion.cs` 的 `Motion.Dur(...)` 取，**不硬编码**；动效开关关闭时它返回 `TimeSpan.Zero`。
- 可动画的属性只用 `Opacity`、`Background`、`BorderBrush`、`RenderTransform`、`Width/Height`（小范围）。
- **不要**动画 `Margin`、`Padding` 或大范围 `Width/Height`——触发完整重排，60fps 掉帧。
- 悬停反馈 140–160ms，页面转场 200–280ms；300ms 以上只用于进度条。
- 循环动画（背景、加载）必须能被 `Motion.Enabled` 关掉。

### 7.9 图标

用 SVG path data（24×24 视口）。仓库已有 `NavigationItem.Icon*` 常量；新增图标追加到 `src/NebulaLauncher.App/ViewModels/NavigationItem.cs` 或页面自己的静态类，**不要**引图标字体（跨平台字体不一致）。

```xml
<Path Data="{StaticResource SomeIconData}"
      Stroke="{DynamicResource Text.Tertiary}"
      StrokeThickness="1.6" StrokeLineCap="Round" StrokeJoin="Round"
      Stretch="Uniform" Width="18" Height="18" />
```

### 7.10 多选与批量操作

列表需要多选时用「`ItemsControl` + 每行复选框」，不要用 `ListBox SelectionMode="Multiple"`：

- `IsChecked` 是纯 bool，编译绑定能在构建期校验；`SelectedItems` 绑错只在运行期静默失效，而截图模式点不动，等于无法验证。
- `ListBoxItem:selected` 的强调色实心底会和卡片 hover 态打架（`Themes/Controls.axaml` 已覆盖过这条样式）。

批量操作条在列表上方、紧贴工具条：左「已选 N 项」+「全选」+「清除选择」，右三个动作（启用 / 禁用 / 删除）。无选中时动作按钮禁用而不是隐藏——按钮消失会让人以为页面变了。删除必须二次确认，样式用 `toneBar danger`（与单条删除一致）。批量结果用一条可关闭的结果条显示「成功 N / 失败 M」并列出失败项，**不要**用弹窗。

### 7.11 提交前的界面自检

- [ ] 页面里没有颜色字面量（XAML 里出现 `#RRGGBB` 就是错的）
- [ ] 每个 `UserControl` 有 `x:DataType`
- [ ] 加载中 / 空 / 错误三种状态都有对应 UI
- [ ] 长文本 `TextWrapping="Wrap"`，不会把窗口撑破
- [ ] 窗口缩到 `1024×660` 时布局不重叠、不裁切（抬头副标题也要看）
- [ ] 关掉动效（`Motion.Enabled = false`）后功能仍完整
- [ ] 深色与浅色各截一张（`shot.ps1 -Theme dark|light`），两种主题下文字都可读
- [ ] 新增令牌在 `Dark` 与 `Light` 两个字典里都存在

## 8. 怎么验

### 8.1 提交前门槛

1. `dotnet build -v q --nologo` → 0 error。
2. 涉及逻辑的改动补 xunit 测试，TestRunner 全量通过（见 8.2）。
3. 涉及 UI 的改动**必须截图自查**（见 8.4），确认无 XAML 绑定异常、无布局重叠、无文字裁切。
4. 报告写清楚：改了哪些文件、跑了什么命令、输出是什么、哪些没验证。

### 8.2 测试：`dotnet test` 在本机不可用

本机沙箱拒绝 testhost 启动（`Process.EnableRaisingEvents` → `OpenProcess` → Win32Exception 5），`dotnet test` / `dotnet vstest` 都在启动阶段中止。改用进程内运行器：

```powershell
dotnet run --project tools/TestRunner/TestRunner.csproj -v q --nologo -m:1 -p:BuildInParallel=false
dotnet run --project tools/TestRunner/TestRunner.csproj -v q --nologo -m:1 -p:BuildInParallel=false -- Crash
```

`-m:1 -p:BuildInParallel=false` 不是可选项：本机沙箱拒绝 MSBuild 多节点之间的命名管道通信，项目图 ≥3 个时不加会「0 error 但生成失败」。运行器反射测试程序集、复用 xunit 断言，支持 `[Fact]` 与 `[Theory]+[InlineData]`；`MemberData` / `ClassData` / `IClassFixture` 打印 SKIP 而不是静默忽略。退出码有失败为 1。

**测试项目引用范围**：`tests/NebulaLauncher.Tests/NebulaLauncher.Tests.csproj` 同时引用 Core、App 与 `tools/DocsCheck/DocsCheck.csproj`（三引用）——加 App 引用是为 `ShellProgress` 这类「节流逻辑与窗口生命周期强相关」的服务，下沉 Core 反而语义更差；加 DocsCheck 引用是为让引用/预算/三态校验器的行为也能被 xunit 直接测。限制：**测试里不得触碰 Avalonia 类型**（`Window`、控件、`Dispatcher`、`Application.Current`），只能测纯逻辑与接口替身；某条测试需要 `Application.Current`，说明被测逻辑放错了层。

### 8.3 引用与预算校验：`tools/DocsCheck`

文档里的代码引用、站内链接与行数预算由工具校验，不靠人眼。工作树根目录下跑：

```powershell
dotnet run --project tools/DocsCheck/DocsCheck.csproj -c Release --
dotnet run --project tools/DocsCheck/DocsCheck.csproj -c Release -- --json
dotnet run --project tools/DocsCheck/DocsCheck.csproj -c Release -- --emit-features docs/FEATURES.md
```

代码引用写法：反引号单元内的首段是仓库相对路径（`src/` `tests/` `tools/` `docs/` 开头，或以 `.md` 结尾），其后跟符号名（可选），行号写在符号后的括号里、只作提示（旧的 `路径:行号 符号` 形态工具仍接受）：

```text
`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.CacheDirectory（:141）`
`src/NebulaLauncher.Core/Paths/LauncherPaths.cs LauncherPaths.CacheDirectory`
`tests/NebulaLauncher.Tests/DocsCheck_CommandTests.cs（:150）`
```

路径与符号都得在工作树里真实存在，路径缺失与符号缺失一样算失败。另有站内链接校验：`[文本](相对路径.md)` 形式的目标文件必须存在（`http(s)://`、`#锚点`、以 `/` 开头的不算）。

默认扫 5 份文档：`docs/PROJECT.md`、`docs/CODE.md`、`docs/FEATURES.md`、`docs/PITFALLS.md`、`GLOSSARY.md`（`--docs` 可换）。这 5 份缺任何一份都算失败——只有显式加 `--allow-missing` 才降级成「缺失（跳过）」。预算：单份 400 行、总量 1000 行。约束层（`CONVENTIONS.md`、`README.md`、`AGENTS.md`）与证据链（`docs/agents/`、`docs/research/`、`docs/release-notes/`）不占这 1000 行额度，但引用照样要校验：`--evidence <路径>...` 只做引用与链接校验、不进行数预算。全仓 16 份文档的口径是：

```powershell
dotnet run --project tools/DocsCheck/DocsCheck.csproj -c Release -- --evidence CONVENTIONS.md README.md AGENTS.md docs/agents/domain.md docs/agents/issue-tracker.md docs/agents/triage-labels.md docs/research/code-map.md docs/research/doc-trust-audit.md docs/research/junk-audit.md docs/research/release-checklist.md docs/release-notes/v1.0.3.md
```

release notes 进这份清单是为了让它的代码位置指针也被校验：`docs/release-notes/v1.0.3.md` 的已知限制各带一个 `路径 符号（:行号）`，指错或代码挪位时退出码非 0。

退出码：

| 码 | 含义 |
|---|---|
| 0 | 全过 |
| 1 | 引用 / 站内链接 / 必需文档 / 版本口径有问题 |
| 2 | 行数预算越界 |
| 3 | 上面两类都有 |

（参数本身写错另计，退出码 64。）

第 6 个检查器查版本口径漂移：`Directory.Build.props` 的 `<Version>`、§8.7 发布命令里的 tag、`docs/PROJECT.md` 的版本口径三处必须一致；`docs/release-notes` 目录下必须存在与版本号同名的 `v<版本>.md` 且含四节（概览、安装与体积、已知限制、证据链与签名说明）；三处文档里再出现本轮已废弃的两句旧口径原话（一句自称体积表权威、一句让对外引用轮次号）即失败。失败消息逐条给文件名、行号或缺失的节名，不用人眼比对三份文件。它只在「根目录有 `Directory.Build.props` 或 `NebulaLauncher.sln`」时生效——`--root` 指向只含文档的临时树时跳过，报告里显示「未检查」而不是「通过」。

`--emit-features docs/FEATURES.md` 按 `tools/DocsCheck/feature-blocks.json` 里的探测点重新生成该文档，生成命令里的构建配置取自环境变量 `NEBULA_BUILD_CONFIG`（缺省 `Release`）。

文档体系里踩过的坑与规避手段见 `docs/PITFALLS.md`。

### 8.4 UI 截图：用应用内渲染，不要截系统屏幕

`tools/shot.ps1`（背后是 `--screenshot` 启动参数）让应用把自己的视觉树渲染成 PNG，与窗口焦点、遮挡、DPI 无关，结果可复现。

```powershell
# 一次性准备有内容的隔离数据（4 版本 / 7 模组 / 3 世界 / 2 账户 / 3 服务器）
.\tools\seed-demo-data.ps1 -DataDir D:\agent_workspace\.probe\data-<你的标识> -Force

# 截图
.\tools\shot.ps1 -Page versions -NoBuild -Configuration <你的配置> `
    -DataDir D:\agent_workspace\.probe\data-<你的标识> -Out D:\agent_workspace\.probe\v.png
```

页面键：`home` `versions` `download` `mods` `library` `accounts` `servers` `worlds` `logs` `help` `settings`。
主题 `-Theme dark|light|system`（默认 `dark`），**深色与浅色各截一张**；设置页的非默认分区用 `-Section <key>`，长页面首屏之外用 `--scroll <px>`。等价手工命令：

```powershell
dotnet $bin\NebulaLauncher.dll --screenshot out.png --page home --delay 4000 --data-dir <dir>
```

截图失败读 `<data-dir>\logs\*.log`；偶发一次 `0xE0434352` 是启动时序问题，重试即可。

### 8.5 发布判据

`build.ps1` 四种 `-Mode`（默认 `minimal`），RID 默认 `win-x64`，配置 `Release`，输出到 `pub\`（发布前整体删除重建，`-OutDir` 可覆盖）：

| 形态 | 内容 | 实测体积（win-x64，2026-10-25） | 前提 |
|---|---|---|---|
| `minimal`（默认） | 框架依赖 + 单文件，原生库外置 | 25.3 MB / 3 文件 | 目标机需 .NET 8 Desktop Runtime |
| `fdd` | 框架依赖多文件 | 26.2 MB / 48 文件 | 同上 |
| `folder` | 自包含、不裁剪 | 98.5 MB / 231 文件 | 无 |
| `portable` | 自包含、裁剪、单个 exe | 24.1 MB / 1 文件 | `%TEMP%\.net\<app>` 可写 |

这张表是最近一次 win-x64 发布实测记录，**每次发布以当版 release notes 为准**：`minimal` 与 `portable` 两行在 2026-10-25 的 `v1.0.1` 发布前实测（`minimal` 解压后 26,510,634 字节 / 3 文件，`portable` 25,273,560 字节 / 1 文件）；`fdd` 与 `folder` 两行是更早一次实测，未在本次重测，发布这两种形态时现测并补进 release notes。仓内旧记录里的 23.4 MB / 47 文件是 ANGLE 剔除前或标注错误的口径；`v1.0.0` 的对应数字是 `minimal` 25,926,442 字节 / `portable` 24,958,622 字节（那时 JSON 通路在便携版里是坏的，见 `docs/PITFALLS.md` 3.16）。

**产物可用 = 三道关都过**（`build.ps1` 发布后自动跑；拿到别人的产物时要自己跑一次）：

1. `<out>\NebulaLauncher.exe --probe-dir <out>`：30 秒内退出码 0 且输出含 `writable=YES`。
2. `<out>\NebulaLauncher.exe --selftest-json`：120 秒内退出码 0、输出里没有 `json-selfcheck.FAIL`、且 `json-selfcheck.summary=<N>/<N>` 的两个数字相同——它在真实产物里跑七条 JSON 落盘往返（`src/NebulaLauncher.Core/Diagnostics/JsonSelfCheck.cs JsonSelfCheck.RunAsync（:32）`）。
3. `--data-dir <out>\.verify --screenshot <out>\.verify.png --page home`：20 秒内退出码 0 且 PNG > 10240 字节。

三道关堵的都是「退出码 0 但用不了」：前两条分别挡「启动不起来」和「启动了但设置存不住」——裁剪发布关掉 System.Text.Json 反射元数据时，只有真跑一遍落盘才会露（见 `docs/PITFALLS.md` 3.16）；第三条挡「启得起来但不渲染」：`obj/<Configuration>` 里留着过期 XAML 中间产物时，publish 出的 exe 会静默退出（exit 0、无窗口、不产图），而同一二进制的 `--probe-dir` 却正常。脚本自己在构建前做过期检测并只清过期的 XAML 中间产物；不确定时加 `-Clean` 做完全清理。`-SkipVerify` 只用于本地试验，**正式发版不得加**。

第 2 关把单文件解包目录钉在产物目录（`DOTNET_BUNDLE_EXTRACT_BASE_DIR`），所以它的结论只反映产物本身，不受 `%TEMP%\.net` 可写性影响：早前便携版在 `%TEMP%` 被拒时，第 1 关直接报 `Failure processing application bundle.`、exit -2147450720。

一个环境事实：这个 exe 写 `%TEMP%` 会被系统拒绝（`UnauthorizedAccessException`），所以自检的验证文件都落在产物目录里。

**发布前置：HEAD 必须精确 tag 到 `v<Version>`。** `-Publish` 的第一件事是读 `Directory.Build.props` 的 `<Version>`、算出期望 tag（如 `v1.0.1`），再比对 `git describe --tags --exact-match` 的输出；没有 tag、或 tag 名与版本对不上，都在删除旧产物与 publish 之前失败，非 0 退出并打印「期望 tag: v1.0.1」。所以流程是**先打 tag 再构建**，§8.7 里的 `git tag -a` 因此提前成 §8.6 的第一步。`-AllowUntagged` 只跳过这一道，产物自检照跑；它只用于本地试验，**正式发版不得加**。

### 8.6 发布命令（照抄）

```powershell
cd D:\agent_workspace\NebulaLauncher
. .\env.ps1
$env:AVALONIA_TELEMETRY_OPTOUT='1'

# 0) 先打本地 tag：-Publish 会校验 HEAD 上的 tag 与 <Version> 一致（不打就失败）
git status --short            # 必须为空或只剩已决定的改动
git log --oneline -1          # HEAD 是定稿提交
git tag -l v1.0.3             # 必须为空
git tag -a v1.0.3 -m "Nebula Launcher v1.0.3"

# 主产物：-OutDir 必须指向最内层目录，build.ps1 会把它整体删掉重建
New-Item -ItemType Directory -Force D:\agent_workspace\pub\v1.0.3 | Out-Null
pwsh -NoProfile -File .\build.ps1 -Publish -Mode minimal `
    -OutDir D:\agent_workspace\pub\v1.0.3\NebulaLauncher-v1.0.3-win-x64

# 备选产物（不加 -SkipVerify；不过关就不发 portable）
pwsh -NoProfile -File .\build.ps1 -Publish -Mode portable -OutDir D:\agent_workspace\pub\v1.0.3\_portable
Move-Item D:\agent_workspace\pub\v1.0.3\_portable\NebulaLauncher.exe `
          D:\agent_workspace\pub\v1.0.3\NebulaLauncher-v1.0.3-win-x64-portable.exe
Remove-Item D:\agent_workspace\pub\v1.0.3\_portable -Recurse -Force

# 打包 → 校验和 → 上传（顺序不能换）
cd D:\agent_workspace\pub\v1.0.3
tar -a -c -f NebulaLauncher-v1.0.3-win-x64.zip NebulaLauncher-v1.0.3-win-x64
$shaLines = Get-ChildItem -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Get-FileHash -Algorithm SHA256 |
    ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) }
# 必须 LF 行尾：Out-File -Encoding ascii 产 CRLF，会让 sha256sum -c 报文件名带 \r 然后 FAILED
[IO.File]::WriteAllText((Join-Path $PWD 'SHA256SUMS.txt'), (($shaLines -join "`n") + "`n"))
```

`SHA256SUMS.txt` 每行 `<64 位小写十六进制>␣␣<文件名>`（两个空格分隔，GNU 格式，`sha256sum -c` 可直接校验），只列会发布出去的文件。行尾必须是 LF：CRLF 会让 `sha256sum -c` 把 `\r` 算进文件名而报 `No such file or directory`（2026-10-24 实测）。

### 8.7 GitHub Release

```powershell
git tag -l v1.0.3             # 必须存在：§8.6 第一步已在本机打过
git push origin v1.0.3
gh release create v1.0.3 --draft --title "Nebula Launcher v1.0.3" `
  --notes-file docs\release-notes\v1.0.3.md `
  "D:\agent_workspace\pub\v1.0.3\NebulaLauncher-v1.0.3-win-x64.zip" `
  "D:\agent_workspace\pub\v1.0.3\NebulaLauncher-v1.0.3-win-x64-portable.exe" `
  "D:\agent_workspace\pub\v1.0.3\SHA256SUMS.txt"
gh release view v1.0.3 --json isDraft,name,tagName,assets,url
```

附件给绝对路径（文件名即远端 asset 名）；`--notes-file` 要指向工作区里已写好的本版说明。**「Publish release」由人在网页点**，命令只建草稿。仓库已转 public（2026-10-25），该 Release 匿名访问返回 200、附件可直接下载；若将来转回私有，需在 notes 里同步说明匿名访问返回 404。release notes 必须写明：可执行文件未签名（Windows 可能报「已保护你的电脑」），并贴 SHA256 全文与一行校验命令。

### 8.8 并行协作的三条硬规则

1. **构建配置隔离**：每人用自己的 `-c` 配置（`ui1` / `ui2` / `ui3` / `Lead` …），产物落在 `bin/<配置>/`，互不锁 obj。
2. **数据目录隔离**：每个实例必须带 `--data-dir`，否则会并发写同一份 `accounts.json` / `servers.json` / `settings.json` 互相覆盖。
3. **不杀别人的进程**：禁止 `Stop-Process` 任何 `dotnet` / `NebulaLauncher` 进程；`--screenshot` 模式自己会退出。

构建偶发 `NebulaLauncher.Core.dll ... being used by another process` 时等 10–20 秒重试，不要删别人的 `obj/` `bin/`。

### 8.9 Debug 配置 XAML 缓存损坏

`bin\Debug` 启动报 `No precompiled XAML found for NebulaLauncher.App`，说明 XAML IL 步骤被中断过、增量构建跳过了它。删掉 App 项目下 `obj/Debug` 与 `bin/Debug` 重建即可。

## 9. 第三方数据与署名

模组中文名来自 **MC 百科**（mcmod.cn）条目，数据文件 `src/NebulaLauncher.Core/Resources/WikiEntries.txt` 取自 [PCL2](https://github.com/Meloong-Git/PCL) 的 `PCLCS/Resource/WikiEntries.txt`，解析规则移植自同仓库 `PCLCS/Resource/WikiEntry.cs`（落点是 `src/NebulaLauncher.Core/Mods/ModNameTable.cs`）。

- 本仓库不是 PCL 的官方衍生版本，也未获 PCL 作者背书；与该数据 / 规则相关的部分按 PCL《存储库合理使用指南》使用。
- 署名：PCL（Plain Craft Launcher）作者 **龙腾猫跃**；译名数据整理者 **MC 百科**。
- 移除路径：删掉 `src/NebulaLauncher.Core/Resources/WikiEntries.txt` 并移除 `src/NebulaLauncher.Core/NebulaLauncher.Core.csproj` 里的 `<EmbeddedResource>` 项——译名表变空，模组卡片回退显示资源站原名，其余功能不受影响（`CoreMods_LegacyMetadataTests` 里的嵌入资源哨兵测试会失败，那正是这条移除路径的探针）。

## 10. 提交纪律

- 一次只解决一个问题，改动尽量小。
- 不重排 import、不整体格式化、不改无关文件。
- 不删别人的代码来「让编译过」。
