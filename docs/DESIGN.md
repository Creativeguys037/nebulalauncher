# Nebula Launcher 视觉规范

给所有写 UI 的协作者。**先读这份，再写页面。**

## 1. 设计语言

深空 / 玻璃 / 微光。三个关键词决定了所有取舍：

- **深空**：**主视觉**背景是深色，接近黑但带蓝调（`#0A0E17` → `#05070C`）。不用纯黑。内容浮在背景之上。
- **玻璃**：卡片与面板半透明、细描边、弱阴影。不堆厚重投影，靠 1px 亮描边区分层次。
- **微光**：强调色只用在需要引导视线的位置（主操作、当前选中、进度）。大面积彩色是错的。

**禁止**：
- 纯黑 `#000000` 背景、纯白 `#FFFFFF` 大面积文字块
- 高饱和色块铺满卡片
- 圆角小于 8px 的卡片（除标签 pill）
- 多于两种强调色出现在同一屏

### 1.1 两套主题

同一套设计语言有深色与浅色两个实现，键名完全一致（`Themes/Tokens.axaml` 的 `ThemeDictionaries`）。
浅色不是「反色」，而是同一份层次关系的另一种表达：

| 层 | 深色 | 浅色 |
|---|---|---|
| 窗口底 | 近黑带蓝 `#080B12` | 淡雾蓝 `#F4F6FB`（不是纯白） |
| 卡片 | 半透明白 6% | 纯白 + 1px 冷描边 |
| 侧栏 | 玻璃层 | `#E9EFF9` |
| 正文 | 近白 `#F2F6FC` | 墨蓝 `#0F1724` |
| 强调 | 亮蓝紫 | 更深一档的蓝紫（保证白底对比度 ≥ 4.5:1） |
| 光斑 | alpha 0.15–0.3 | alpha 0.07–0.11 |

写页面时**不需要处理主题差异**——只要遵守「不写颜色字面量」，两套自动生效。
例外只有自绘控件与内嵌数据（星云背景、环形进度、服务器 MOTD 协议色），见 `HANDOVER` §6.17。

## 2. 颜色

全部走 `Themes/Tokens.axaml` 里的 `{DynamicResource ...}`，**页面里不写颜色字面量**。
令牌分两套（`Dark` / `Light`），键名相同；表格里的色值只描述深色，浅色由字典并排定义。

| 令牌 | 用途 |
|---|---|
| `Surface.Base` / `Surface.Canvas` | 窗口底 / 内容区底 |
| `Surface.CardSolid` | 卡片底（配 `Border.card`） |
| `Surface.Raised` | 悬浮层、次级按钮 |
| `Surface.Hover` / `Surface.Pressed` | 交互态 |
| `Surface.Sunken` | 输入框、进度槽 |
| `Surface.Glass` / `Surface.Sidebar` | 玻璃层 / 侧栏底 |
| `Stroke.Subtle` / `.Default` / `.Strong` | 描边三档 |
| `Text.Primary` / `.Secondary` / `.Tertiary` / `.Muted` | 文字四档（`Muted` 只做装饰，浅色下对比度仅 2.65:1，不可用于正文） |
| `Text.OnAccent` | 压在主强调色/渐变上的文字 |
| `Accent.Primary` (`#4C6FFF`) | 主强调 |
| `Accent.Secondary` (`#A855F7`) | 次强调，只做渐变搭档 |
| `Accent.Soft` | 选中底色（8% alpha） |
| `Status.Success/Warning/Danger` | 状态色，配 `.Soft` 做底 |
| `Status.Danger.Edge` | 危险按钮悬停描边 |
| `Scrollbar.Thumb` | 滚动条滑块 |
| `Window.CloseHover` | 窗口关闭按钮悬停底（两主题都用红，压在上面的叉号用 `Text.OnAccent`） |

渐变：`Gradient.Accent`（蓝→紫，主按钮/进度）、`Gradient.Aurora`（蓝→紫→青，装饰）。

**新增令牌**：`ThemeDictionaries` 的 `Dark` 与 `Light` 各加一次。只加一边时编译能过、切到另一主题才在运行期抛——所以两种主题都要截图。

## 3. 排版

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

字重只有 `Normal` / `Medium` / `SemiBold` / `Bold` 四档。**不要用 Light**，深色底上会发虚。

行高：多行段落用 `TextWrapping="Wrap"` + `LineHeight="20"`。

## 4. 间距

4 的倍数：`4 / 8 / 12 / 16 / 20 / 24 / 32 / 40`。

- 卡片内边距：`18`（`Border.card` 已设）
- 卡片之间：`12`～`16`
- 页面左右边距：`30`（已由 MainWindow 提供）
- 区块标题与内容之间：`12`

## 5. 圆角

`Radius.Xs(4)` 标签 · `Radius.Sm(8)` 按钮/输入框 · `Radius.Md(12)` 列表项/导航 ·
`Radius.Lg(16)` 卡片 · `Radius.Pill(999)` 胶囊/进度条。

## 6. 控件用法

```xml
<!-- 卡片 -->
<Border Classes="card">...</Border>
<Border Classes="card hoverable">...</Border>   <!-- 可点/可悬停 -->
<Border Classes="card accentEdge">...</Border>  <!-- 强调卡片 -->

<!-- 按钮 -->
<Button Classes="accent" Content="开始游戏" />
<Button Classes="ghost" Content="取消" />
<Button Classes="icon" Content="…" />
<Button Classes="pill" Content="筛选" />
<Button Classes="danger" Content="删除" />
```

## 7. 动效规则

- 所有过渡时长从 `Services.Motion.Dur(...)` 取，**不要硬编码**。动效开关关闭时会返回 `TimeSpan.Zero`。
- 可动画的属性只用：`Opacity`、`Background`、`BorderBrush`、`RenderTransform`、`Width/Height`（小范围）。
- **不要**动画 `Margin`、`Padding`、`Width/Height` 大范围变化——会触发完整重排，60fps 掉帧。
- 悬停反馈 140–160ms，页面转场 200–280ms，状态变化 300ms 以上只用于进度条。
- 循环动画（背景、加载）必须能被 `Motion.Enabled` 关掉。

## 8. 页面结构约定

每个页面是一个 `UserControl`，顶部不重复画标题——标题由 MainWindow 的页面抬头区显示
（`PageViewModelBase.Title` / `Subtitle`）。

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

## 9. 图标

用 SVG path data（24×24 视口），仓库里已有 `NavigationItem.Icon*` 常量。新增图标追加到
`ViewModels/NavigationItem.cs` 或页面自己的静态类，**不要**引图标字体（跨平台字体不一致）。

```xml
<Path Data="{StaticResource SomeIconData}"
      Stroke="{DynamicResource Text.Tertiary}"
      StrokeThickness="1.6" StrokeLineCap="Round" StrokeJoin="Round"
      Stretch="Uniform" Width="18" Height="18" />
```

### 9.1 多选与批量操作

列表需要多选时，用「`ItemsControl` + 每行复选框」而不是 `ListBox SelectionMode="Multiple"`：

- `IsChecked` 是纯 bool，编译绑定能在构建期校验；`SelectedItems` 绑错只会在运行期静默失效，而截图模式点不动，等于无法验证。
- `ListBoxItem:selected` 的强调色实心底会和卡片 hover 态打架（`Controls.axaml` 已经覆盖过一次这条样式）。

批量操作条的布局：**出现在列表上方、紧贴工具条**，左「已选 N 项」+「全选」+「清除选择」，右三个动作按钮（启用 / 禁用 / 删除）。
无选中时动作按钮禁用而不是隐藏——按钮消失会让用户以为是页面变了。
删除必须二次确认，样式用 `toneBar danger`（与单条删除一致）。
批量结果用一条可关闭的结果条显示「成功 N / 失败 M」并列出失败项，**不要**用弹窗。

## 10. 自检清单

提交 UI 前逐条过：

- [ ] 页面里没有颜色字面量（`#RRGGBB` 出现在 XAML 里就是错的）
- [ ] 每个 `UserControl` 有 `x:DataType`
- [ ] 加载中 / 空 / 错误三种状态都有对应 UI
- [ ] 长文本 `TextWrapping="Wrap"`，不会把窗口撑破
- [ ] 窗口缩到 `1024×660` 时布局不重叠、不裁切（抬头副标题也要看，见 `HANDOVER` §6.18）
- [ ] 关掉动效（`Motion.Enabled = false`）后功能仍完整
- [ ] 深色与浅色各截一张（`shot.ps1 -Theme dark|light`），两种主题下文字都可读
- [ ] 新增令牌在 `Dark` 与 `Light` 两个字典里都存在
