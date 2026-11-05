# Avalonia 11.3 玻璃、控件模板与令牌（NebulaLauncher #70）

> 一句话结论：Avalonia 11.3.22 **有 `BlurEffect`**，所以方向卡的「同源背景副本 + blur + 白层 + 亮边/内阴影」可以落地；但它是「把副本放进卡片里再模糊」，不是 CSS `backdrop-filter` 那样模糊卡片背后的兄弟内容。控件侧多数只需 `Style` + `/template/` 改属性；只有要新增视觉层或改模板结构（ToggleSwitch 轨道/滑块、ScrollBar 轨道/Thumb、TabItem 的滑动指示器、ComboBox 玻璃弹层）时才必须 `ControlTheme` / 重写 `ControlTemplate`。

- 票：#70「控件体系与设计令牌决策」（父地图 #69）
- 日期：2026-11-05
- 适用版本：Avalonia **11.3.22** / .NET 8 / 单 TFM / 不引新 NuGet
- 写作范围：只写 `docs/research/`；不改 `src/`、不改 issue、不改其他文档。
- 证据分级：**A** = 官方文档/官方源码/本机 11.3.22 程序集；**B** = 官方博客/官方产品页；**I** = 推断；**未验证** = 没有一手证据，实施前需最小样张验证。
- 已核对仓库：`Themes/Tokens.axaml` 268 行、`Themes/Controls.axaml` 371 行、`Services/Motion.cs` 34 行；包版本见 `Directory.Packages.props` 第 9 行。

---

## 0. 先给结论

| 问题 | 结论 | 级别 |
|---|---|---|
| `ExperimentalAcrylicBorder` / `ExperimentalAcrylicMaterial` 是否存在 | 存在；`ExperimentalAcrylicBorder : Decorator`，有 `Material` / `CornerRadius` | A |
| 能否做「局部玻璃卡片」 | 可以局部摆放，但它的 `BackgroundSource=Digger` 是切到**窗口背景**，不是模糊卡片背后的兄弟控件；不是 CSS `backdrop-filter` 的替代品 | A（类型/枚举）/ I（视觉结论） |
| `BlurEffect` / `Effect` 是否存在 | 存在；`Visual.Effect` 接受 `IEffect`，`BlurEffect` 有 `Radius` | A |
| `BlurEffect` 能否只模糊元素自身内容 | 能；它作用于该 `Visual` 自己的绘制结果（含子树） | A（API 语义） |
| 能否模糊背后的兄弟元素 | 不能；没有 backdrop 采样 API | A（无此 API）/ I（结论） |
| `BoxShadow` 是否支持 `inset` | 支持；语法是 `inset 0 2 0 #fff`，**不能写 `px`** | A（源码 + 本机实测） |
| `OpacityMask` / `VisualBrush` / `RenderTargetBitmap` 能否模拟 | 可以模拟「静态/半静态的同源副本」，但不是实时 backdrop；`RenderTargetBitmap` 是快照 | A（各自 API）/ I（组合） |
| 方向卡 4 步公式 | 第 1 步需手工复制背景；第 2 步 blur 可做、saturate 不可做；第 3、4 步可基本原样做 | A/I |
| `ControlTheme` 与 `Style` 的正确分工 | 只改属性用 `Style`；完整换外观、增加模板部件用 `ControlTheme` | A |
| `Transitions.Duration` 能否绑定 `Motion.Fast` | 现有 `Motion.Fast` 是 `x:Double`，不能直接绑 `TimeSpan`；需新增 `TimeSpan` 资源或 C# 常量 | A（类型）/ 未验证（DynamicResource 细节） |
| `Easing` 能否资源化 | 能；官方 Fluent 用 `<Easing x:Key="...">0.1,0.9,0.2,1.0</Easing>` + `{StaticResource}` | A |
| 24 视口、stroke 1.7–1.9 的线性图标 | 用 `Path` + `Stroke`，不要用只填充的 `PathIcon` | A（文档/API） |
| 2px 蓝环 | 用 `FocusAdorner` + `FocusAdornerTemplate`；`:focus-visible` 可用 | A |

---

## 1. 玻璃与模糊

### 1.1 `ExperimentalAcrylicBorder` / `ExperimentalAcrylicMaterial`

**事实（A）：**

- `Avalonia.Controls.ExperimentalAcrylicBorder` 存在，继承 `Decorator`；公开 `Material`（`ExperimentalAcrylicMaterial`）与 `CornerRadius`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Controls/ExperimentalAcrylicBorder.cs>（访问 2026-11-05）。
- `ExperimentalAcrylicMaterial` 存在，属性包括 `TintColor`、`TintOpacity`、`MaterialOpacity`、`BackgroundSource`、`FallbackColor`、`PlatformTransparencyCompensationLevel`；`BackgroundSource` 的类型 `AcrylicBackgroundSource` 只有 `None` / `Digger`。API：<https://docs.avaloniaui.net/api/avalonia/media/experimentalacrylicmaterial>（访问 2026-11-05）。
- `AcrylicBackgroundSource.Digger` 的源码注释是：**“Cuts through all render layers to reveal the window background. This means if your window is transparent or blurred it will be blended with the material.”** 这直接说明它取的是窗口背景，而不是卡片正下方的兄弟控件内容。
- Windows 侧：`WinUIComposition` 组合模式要求 Windows 10 build **17134** 及以上，并且 `RenderingMode` 要包含 `AngleEgl`；`AcrylicBlur` 透明度级别要求 Windows 10 **1803** 及以上。来源：<https://docs.avaloniaui.net/docs/platform-specific-guides/windows>（访问 2026-11-05）。
- `Mica` 才是 Windows 11 的系统材质；`AcrylicBlur` 不是 Windows 11 独占。`TransparencyLevelHint` 在 Windows 上支持 `Transparent` / `AcrylicBlur` / `Mica`，但必须把窗口 `Background` 设为 `Transparent`，否则透明/模糊会被不透明背景盖住。来源同上（A）。
- `ExperimentalAcrylicBorder` 的官方 ControlCatalog 示例确实把它放在页面内部（`samples/ControlCatalog/Pages/AcrylicPage.xaml`），所以「局部摆放」在结构上没问题。

**平台限制（A）：**

| 平台 | 结论 |
|---|---|
| Windows 10 1803+ | `AcrylicBlur` 可用；需要 `WinUIComposition` + ANGLE，且窗口透明背景 |
| Windows 11 | 同上；另外可用 `Mica`（系统桌面材质） |
| macOS | 官方 Windows 文档明确只有 `Transparent`；不要假设 acrylic 可用 |
| Linux | 取决于 compositor；不是稳定可依赖的能力 |

**能不能做「局部玻璃卡片」？**

- **结构上可以局部摆放**：它是 `Decorator`，可以放进任意 `Panel`，有自己的 `CornerRadius`。
- **但它不是局部 backdrop-filter**：`Digger` 会切到窗口背景；它不会把卡片背后的 `ListBox`、图片、文字等内容当成模糊源。若卡片下面是页面背景本身（背景就是窗口背景/整页背景层），视觉上可能接近局部玻璃；若卡片要盖在任意内容上，**不能**用它替代 CSS 的 `backdrop-filter`。
- **结论**：整窗/整块背景区域可用；作为「卡片背后的内容要被糊开」的通用玻璃卡片，不推荐，标 `[推断]`，需要一张最小样张实测 `Digger` 在本项目窗口结构下的实际输出。

### 1.2 `BlurEffect` / `Effect`

**先纠正一个常见误判：Avalonia 11.3.22 不是「没有 BlurEffect」。**

本机 `Avalonia.Base.dll` 的类型列表里有 `Avalonia.Media.BlurEffect`、`ImmutableBlurEffect`、`Effect`、`DropShadowEffect` 等；`BlurEffect` 源码只有 `Radius` 属性，并实现 `IBlurEffect` / `IMutableEffect`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Media/Effects/BlurEffect.cs>（访问 2026-11-05）。

**可用写法（A）：**

```xml
<Border Background="SteelBlue" Padding="20">
  <Border.Effect>
    <BlurEffect Radius="26" />
  </Border.Effect>
</Border>
```

或使用 `Effect` 的字符串转换器：

```xml
<Border Effect="blur(26)" />
```

`Effect.Parse("blur(26)")` 在本机 11.3.22 实测返回 `ImmutableBlurEffect`，`Radius=26`（A，本机实测）。官方文档：<https://docs.avaloniaui.net/docs/graphics-animation/effects>（访问 2026-11-05）。

**边界（关键）：**

- `Effect` 是 `Visual` 的绘制后处理，作用于该 `Visual` **自己的绘制结果（含子树）**。它不会读取父级/兄弟已经画好的像素，因此不是 WPF 的 `BlurEffect` 跨元素效果，更不是 CSS `backdrop-filter`。本机 `Avalonia.Skia.dll` 的 `PushEffect` 实现是 `Canvas.SaveLayer(effectClipRect, paint)` 包裹当前 Visual 的绘制，而不是读取下层合成结果（A，本机反编译核对）。
- 把 `BlurEffect` 挂在卡片根节点上，会同时模糊卡片里的文字/图标；正确做法是只把 blur 挂到「背景副本」这一层，文字和边框放在它上面。
- `BlurEffect` 只有 `Radius`，**没有 `saturate()`、没有颜色矩阵/色相效果**。11.3.22 的 effect 家族只有 blur 与 drop-shadow 两类。方向卡的 `saturate(190%)` 不能原样做；只能用一层淡蓝/白色叠加近似，或接受饱和度不变。
- `Radius` 的单位是像素/DIP 级参数，和 CSS `blur(26px)` 的高斯 sigma 不是同一标度；视觉上要重新调，不要直接照抄 26。标 `[推断]`。
- 性能：effect 通常需要额外的离屏绘制；大面积、每帧变化的 blur 很贵。静态背景副本可以接受；动画/滚动中的实时 blur 必须做性能样张。标 `[推断]`。

### 1.3 `BoxShadow` 的 `inset`

**事实（A）：**

- `BoxShadow` 有 `IsInset` 属性；`BoxShadow.Parse` 支持可选的第一个 token `inset`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Media/BoxShadow.cs>（访问 2026-11-05）。
- 官方文档明确：前缀 `inset` 表示在元素内部绘制；语法顺序是 `offsetX offsetY blur spread color`，多个阴影用逗号分隔。来源：<https://docs.avaloniaui.net/docs/graphics-animation/effects>（访问 2026-11-05）。
- 本机实测：`BoxShadow.Parse("inset 0 2 0 #fff")` 成功，`IsInset=True`；`BoxShadow.Parse("inset 0 2px 0 #fff")` 抛 `FormatException`（`The input string '2px' was not in a correct format.`）。**所以方向卡里的 `inset 0 2px 0 #fff` 必须改成 `inset 0 2 0 #fff`。**

**方向卡边缘的 Avalonia 近似：**

```xml
<Border BorderBrush="#DBFFFFFF"
        BorderThickness="1"
        CornerRadius="20"
        BoxShadow="inset 0 2 0 #FFFFFF,
                   inset 0 -16 30 -16 #47103078,
                   0 26 60 -12 #38103078" />
```

- 第 1 条：顶部 2px 白亮线。
- 第 2 条：底部内阴影（`offsetY=-16`、`blur=30`、`spread=-16`、蓝色半透明）。
- 第 3 条：外投影。
- 示例里的颜色/数值应整体收进 `Tokens.axaml` 的一个 `BoxShadows` 资源（例如 `Shadow.GlassCard`），页面只引用 `{DynamicResource Shadow.GlassCard}`；不要在页面里散写颜色字面量。
- `BoxShadow` 只在 `Border` 和 `ContentPresenter` 上有；普通 `Panel`/`Grid` 没有这个属性，卡片要用 `Border` 包。官方文档同上（A）。
- `BoxShadow` 的内阴影绘制在背景之上、内容之下；如果内容需要完全不受内阴影影响，把内容放到独立的上层 `Panel`。

### 1.4 `OpacityMask`、`VisualBrush`、`RenderTargetBitmap`

| 能力 | 事实 | 用法/限制 | 级别 |
|---|---|---|---|
| `OpacityMask` | 用 brush 的 **alpha 通道**逐像素控制透明度 | 适合圆角/柔边/异形裁切；`Border.CornerRadius` 本身已能裁切子内容，简单卡片不必上 mask | A |
| `VisualBrush` | 用另一个 `Visual` 作为画刷源绘制一个区域 | 可把专用背景层画进卡片；不要引用包含卡片本身的父级，避免递归/重复绘制；实时刷新与 DPI 需实测 | A（API）/ I（组合） |
| `RenderTargetBitmap` | 把 `Visual` 渲染成位图，`Render(Visual)` 是显式快照 | 适合静态/低频更新；不是 live backdrop；每次窗口尺寸/主题/背景动画变化都要重渲染才不糊/不旧 | A（API）/ I（用途） |

官方 API：
- <https://docs.avaloniaui.net/api/avalonia/media/visualbrush>（访问 2026-11-05）
- <https://docs.avaloniaui.net/api/avalonia/media/imaging/rendertargetbitmap>（访问 2026-11-05）
- `OpacityMask` 的 alpha 语义：<https://docs.avaloniaui.net/docs/graphics-animation/clipping-and-masking>（访问 2026-11-05）

**组合判断（I）：**

「同源背景内衬 + blur」可以用下面任一方式模拟，但都不是 CSS `backdrop-filter`：

1. **专用背景副本（推荐做真实感）**：把页面的渐变 + 硬边色块抽成一个 `GlassBackdrop` 视觉/控件；页面底层放一份，每张玻璃卡片内部再放一份，按卡片在页面中的偏移做同样的坐标变换，裁到卡片圆角内，再对副本挂 `BlurEffect`。这是最接近方向卡的做法，但需要自定义控件/附加属性来做坐标同步。
2. **`VisualBrush`**：把专用背景视觉作为 `Rectangle.Fill`，用 `SourceRect`/`DestinationRect` 对齐卡片区域；外面再套 `BlurEffect`。不用复制视觉树，但需要确认源视觉在树内时的实时刷新行为。
3. **`RenderTargetBitmap`**：在窗口尺寸/主题变化时把背景渲染成位图，再用 `ImageBrush` 填进卡片；最稳定但完全是静态快照，不适合背景动画/滚动视差。
4. **`ExperimentalAcrylicBorder`**：只在「卡片背后就是窗口背景」时可用；不能把任意兄弟内容当作模糊源。

### 1.5 方向卡 4 步公式的逐条映射

| CSS 步骤 | Avalonia 11.3.22 | 结论 | 降级/近似写法 |
|---|---|---|---|
| 1. 同源背景副本 + `background-attachment: fixed` | 没有 `background-attachment`；可用重复背景层、`VisualBrush`、`RenderTargetBitmap` | **不能原样**，要手工做 | 抽 `GlassBackdrop`，卡片内放副本并对齐坐标；或用 `VisualBrush`/快照 |
| 2. `filter: blur(26px) saturate(190%)` | `BlurEffect Radius=26` 可模糊副本；没有 saturation/color-matrix | **部分可做**：blur 可以，saturate 不行，backdrop 不行 | 只 blur 副本；用淡蓝/白叠加近似饱和感；不要指望 `BlurEffect` 模糊兄弟内容 |
| 3. 白层 `34% → 19%` + 左上高光 | `LinearGradientBrush` / `RadialGradientBrush` + `Border`/`Rectangle` | **可基本原样** | 白层用 alpha 渐变；高光用径向渐变或额外小 `Border` |
| 4. 1px 白边 + `inset 0 2px 0 #fff` + 底内阴影 + 外投影 | `Border.BorderBrush` + `BoxShadow` 多条（含 `inset`） | **可基本原样**（语法要改成无 `px`） | `BoxShadow="inset 0 2 0 #fff, inset 0 -16 30 -16 #47103078, 0 26 60 -12 #38103078"` |

**一句话**：这不是「Avalonia 做不了玻璃」，而是「Avalonia 没有 backdrop-filter；要用同源副本把背景搬进卡片，再在卡片内部 blur」。方向卡的 CSS 本来就走「同源背景内衬」，所以思路是兼容的，只是第 1 步必须由我们写代码/控件完成，不能靠一个属性。

### 1.6 建议的最小落地结构

> 下面 `Glass.WhiteOverlay` / `Glass.Edge` 是建议新增的语义令牌名，不是现有 `Tokens.axaml` 里已经存在的键；现有文件只有 `Surface.Glass` / `Stroke.*`。落地时先补令牌，再写样式。

```xml
<Grid>
  <!-- 1) 裁切到卡片圆角的背景副本 -->
  <Border ClipToBounds="True"
          CornerRadius="{DynamicResource Radius.Xl}">
    <Grid>
      <local:GlassBackdropCopy Effect="blur(26)"
                               IsHitTestVisible="False" />
      <Border Background="{DynamicResource Glass.WhiteOverlay}" />
    </Grid>
  </Border>

  <!-- 2) 亮边 + 内阴影 + 外投影（不参与命中） -->
  <Border BorderBrush="{DynamicResource Glass.Edge}"
          BorderThickness="1"
          CornerRadius="{DynamicResource Radius.Xl}"
          BoxShadow="inset 0 2 0 #FFFFFF,
                     inset 0 -16 30 -16 #47103078,
                     0 26 60 -12 #38103078"
          IsHitTestVisible="False" />

  <!-- 3) 内容在最上层 -->
  <ContentPresenter Content="{TemplateBinding Content}"
                    Margin="{TemplateBinding Padding}" />
</Grid>
```

`GlassBackdropCopy` 是**自定义控件/绘制层**，不是新 NuGet；它只负责在卡片坐标系里重画页面背景（渐变 + 3 个硬边圆 + 柔光），并设置 `ClipToBounds`。如果首版不实现它，就退化为：

```xml
<Border Background="{DynamicResource Surface.Glass}"
        BorderBrush="{DynamicResource Glass.Edge}"
        BorderThickness="1"
        CornerRadius="{DynamicResource Radius.Xl}"
        BoxShadow="inset 0 2 0 #FFFFFF,
                   inset 0 -16 30 -16 #47103078,
                   0 26 60 -12 #38103078" />
```

这是「半透明 + 亮边 + 内阴影」的近似，不会有真实背景糊开的效果；在浅蓝背景上差距比深色背景更明显。`[推断]`

### 1.7 平台/性能注意

- `ExperimentalAcrylicBorder`：Windows 10 1803+（AcrylicBlur）；macOS/Linux 不可依赖；必须有 `FallbackColor`/不透明 fallback。A。
- `BlurEffect`：跨平台走 Skia，但大面积实时 blur 是性能风险；静态卡片副本可接受，滚动/动画中必须实测。I。
- `BoxShadow`：每个阴影增加绘制 pass；方向卡一张卡片 3 条阴影，列表里不要每行都叠满。官方性能页建议少用阴影（仓库既有笔记也记录过）。A/I。
- 多层玻璃卡片叠加（卡片里再套卡片）会让 blur/阴影成本叠加；首版限制为「页面级背景副本 + 单层卡片」。I。

---

## 2. ControlTemplate 重写边界

### 2.0 判据：先看模板里的属性是怎么来的

对每个控件，先看 Fluent 模板内部件：

1. **`{TemplateBinding X}`**：改控件自身的 `X` 属性（`Style` Setter）就能透传，通常不需要 `/template/`。
2. **`{DynamicResource Y}` 或字面量**：改控件自身属性没用；要么用 `Style Selector="Control /template/ 部件"` 改内部件属性，要么覆盖资源 `Y`；要新增层/改结构则必须重写模板。
3. **硬编码尺寸/行列/层数**：`Style` 改不了，必须 `ControlTheme` / 重写 `ControlTemplate`。
4. **模板里没有的部件**：`/template/` 选择器不会凭空创建；必须重写。
5. **`BoxShadow` 只存在于 `Border` / `ContentPresenter`**：卡片、按钮阴影要落到这两类部件上，不能指望 `Grid` 有 `BoxShadow`。

### 2.1 控件矩阵

“Style 够”指只改颜色/字体/圆角/内边距/简单边框；“完整玻璃”指方向卡要求的背景副本 + blur + 白层 + 内阴影。

| 控件 | Fluent 模板关键结构 | 只改 Style 够吗 | 需要 `/template/` 吗 | 完整玻璃要 ControlTheme/Template 吗 | 判据 |
|---|---|---|---|---|---|
| **Button** | 只有一个 `ContentPresenter#PART_ContentPresenter`，`Background`/`BorderBrush`/`BorderThickness`/`CornerRadius`/`Padding` 都是 `TemplateBinding` | 颜色/圆角/字体/内边距够 | 建议把背景/阴影落到 `ContentPresenter` | **是**（要背景副本 + blur + 多层） | 模板没有额外层；Fluent 还占用 `.accent` 类并直接设置 `ContentPresenter` 背景，见 §2.2 |
| **TextBox** | `Border#PART_BorderElement` + `DockPanel#PART_InnerDockPanel` + `TextPresenter`；外层 Border 的 `Background`/`BorderBrush`/`CornerRadius` 是 `TemplateBinding` | 白底/描边/圆角/焦点色够 | 是（`PART_BorderElement` 的 hover/focus/inset） | **是**（要独立白层/高光/模糊副本） | 模板只有一层 Border；内阴影可用 `BoxShadow` 落在该 Border，但背景副本没有位置 |
| **ComboBox** | `Border#Background`、`Border#HighlightBackground`、`Popup` 内 `Border#PopupBorder`；`PopupBorder.Background` 是 `{DynamicResource ComboBoxDropDownBackground}` | 外框颜色/圆角够 | 是（`Background`/`HighlightBackground`/`PopupBorder`） | **是**（Popup 里要玻璃层/阴影/模糊） | 外层 `Background` 不会自动传到 PopupBorder；PopupBorder 是硬编码资源 |
| **CheckBox** | `Border#PART_Border`、`Border#NormalRectangle`（20×20）、`Path#CheckGlyph`；外框 `Background`/`BorderBrush`/`CornerRadius` 是 `TemplateBinding`，勾选框内部是固定资源和尺寸 | 颜色/圆角/文字够；尺寸不够 | 是（`NormalRectangle` / `CheckGlyph` 颜色） | **是**（要改 20×20 尺寸、勾选块玻璃化） | 20×20 是模板硬编码；`Style` 不能改内部尺寸 |
| **ToggleSwitch** | `Border#OuterBorder`（40×20）、`Border#SwitchKnobBounds`（40×20）、`Canvas#PART_SwitchKnob`（20×20）、`Ellipse#SwitchKnobOn/Off`（10×10）；颜色全是 `DynamicResource` | 颜色够；尺寸/形状不够 | 是（轨道/滑块颜色） | **是**（方向卡的大轨道/玻璃滑块） | 宽高和圆角都是模板硬编码，`Style` 只能改颜色/透明度 |
| **ListBox / ListBoxItem** | `ListBox` 是 `Border` + `ScrollViewer` + `ItemsPresenter`；`ListBoxItem` 是单个 `ContentPresenter#PART_ContentPresenter`，其 `Background`/`BorderBrush`/`CornerRadius` 是 `TemplateBinding` | 行背景/圆角/前景/选中色够 | 是（`ContentPresenter` 的选中态） | **行内左侧蓝条/额外层** 用 `ItemContainerTheme`；整行玻璃副本要模板 | 单 ContentPresenter 没有左侧条/多层结构；`ItemContainerTheme` 是正确入口 |
| **ScrollBar** | `Rectangle#TrackRect`（Fill/Stroke 全是 `DynamicResource`）、`Thumb` 使用独立 `FluentScrollBarThumb` ControlTheme（内部一个 `Border`） | 颜色/大致宽度部分够 | 是（`TrackRect` / `Thumb`） | **是**（细窄轨道、圆角胶囊、隐藏箭头） | 轨道和箭头是模板固定结构；`Thumb` 还是嵌套主题，改几何必须重写 |
| **TabControl / TabItem** | `TabControl` 是 `Border` + `DockPanel` + `ItemsPresenter` + `ContentPresenter`；`TabItem` 有 `Border#PART_LayoutRoot` 和 `Border#PART_SelectedPipe` | TabControl 背景/内边距够；TabItem 颜色/静态胶囊可能够 | 是（`PART_SelectedPipe` / `PART_LayoutRoot`） | **滑动胶囊/分段指示器必须**；静态选中胶囊可先试 Style + `/template/` | Fluent 默认是下划线 `PART_SelectedPipe`，没有共享滑动块；滑动动画需要自绘 |
| **ToolTip** | 单个 `Border#PART_LayoutRoot` + `ContentPresenter`；`Background`/`BorderBrush`/`BorderThickness`/`Padding`/`CornerRadius` 都是 `TemplateBinding` | **黑底/白字/8 圆角够** | 可选（内容字号/内边距） | **仅当**要玻璃层/内阴影时才需要 | 不需要为了黑底白字重写模板；这是最容易过度重写的一个 |
| **ProgressBar** | 根 `Border` + `Panel#DeterminateRoot` + `Border#PART_Indicator`；`PART_Indicator.Background` 是 `{TemplateBinding Foreground}` | 高度/圆角/轨道色/渐变填充够 | 可选（`PART_Indicator` 细节） | **不确定进度流动/多层玻璃** 才需要 | `Foreground` 是 `IBrush`，直接给 `LinearGradientBrush` 即可 |
| **Window** | Fluent `Window` 模板：`Panel` + `PART_TransparencyFallback` + 背景 Border + `VisualLayerManager` + `PART_ContentPresenter` | 背景/前景/字体/透明级别够 | 一般不需要 | **不建议为了玻璃重写**；会破坏 `VisualLayerManager`/弹层/窗口装饰 | `Window` 是特殊宿主，不是普通卡片；标题栏用窗口属性/自绘 |
| **Dialog** | Avalonia 11.3 **没有内建 `Dialog` 控件**；只有 `Window.ShowDialog` / 自建 `OverlayLayer` 浮层 | 无内建模板可改 | — | **不适用**：自建 `Window` 或 `Panel` 浮层，按玻璃卡片处理 | 不要去找 `Dialog` ControlTheme；它不是内置控件 |

### 2.2 逐控件说明（只列关键判据）

#### Button

Fluent `Button.xaml` 的模板只有 `ContentPresenter#PART_ContentPresenter`，五个外观属性全部 `TemplateBinding`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/Button.xaml>（访问 2026-11-05）。

- 只改颜色/圆角/字体/内边距：`Style` 够。
- 但 **不要复用 Fluent 已占用的 `.accent` 类**：Fluent 自己在 `^.accent` 里直接设置 `ContentPresenter#PART_ContentPresenter` 的 `Background` / `BorderBrush` / `Foreground`（源码 70–94 行），你在外层 `Button.accent` 上设 `Background` 不一定能盖住模板内部的直接 Setter。建议用 `.primary` 类，并把背景落到 `ContentPresenter`：

```xml
<Style Selector="Button.primary /template/ ContentPresenter#PART_ContentPresenter">
  <Setter Property="Background" Value="{DynamicResource Gradient.Accent}" />
  <Setter Property="BoxShadow" Value="{DynamicResource Shadow.Glow}" />
</Style>
```

- 要背景副本 + blur + 白层 + 内阴影：必须自定义 `ControlTemplate`，因为默认模板只有一个 ContentPresenter，没有放副本/白层的地方。

#### TextBox

Fluent `TextBox.xaml` 模板有 `Border#PART_BorderElement`，其 `Background` / `BorderBrush` / `BorderThickness` / `CornerRadius` 是 `TemplateBinding`；`PART_InnerDockPanel` 负责文本/水印/滚动。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/TextBox.xaml>（访问 2026-11-05）。

- 只改白底、描边、圆角、焦点色：`Style` + `/template/ Border#PART_BorderElement` 够；现有 `Controls.axaml` 已经这么用（hover/focus）。
- 内阴影：`PART_BorderElement` 是 `Border`，可以加 `BoxShadow`，所以内阴影不必重写模板。
- 独立白层 + 背景副本 + blur：模板只有一层 Border，必须重写。

#### ComboBox

Fluent `ComboBox.xaml` 的模板有 `Border#Background`、`Border#HighlightBackground`，以及 `Popup` 内的 `Border#PopupBorder`。关键：`PopupBorder.Background` 是 `{DynamicResource ComboBoxDropDownBackground}`，不是 `{TemplateBinding Background}`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ComboBox.xaml>（访问 2026-11-05，弹出层约 159–183 行）。

- 只改外框：`Style` 够（`Background`/`BorderBrush`/`CornerRadius`）。
- 改 Popup 背景：必须用 `ComboBox /template/ Border#PopupBorder`，或覆盖 `ComboBoxDropDownBackground` 资源；**只改 `ComboBox.Background` 不会影响下拉面板**。
- Popup 里加玻璃层/阴影/模糊：必须重写模板（PopupBorder 只有一个 Border）。
- 箭头翻转：`DropDownGlyph` 是 `PathIcon`，可以用 `[IsDropDownOpen=true]` 属性选择器 + `RenderTransform="rotate(180deg)"` + `Transitions`，不必重写模板；但要注意 `PathIcon` 的 `RenderTransformOrigin` 默认 50%,50%。
- 弹出层是独立顶层，`/template/` 能否在打开的 Popup 中稳定命中需要一次运行验证；标 `[未验证]`。

#### CheckBox

Fluent 模板：`Border#PART_Border`（外层）、`Border#NormalRectangle`（20×20 勾选框）、`Path#CheckGlyph`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/CheckBox.xaml>（访问 2026-11-05）。

- 颜色、圆角、文字：`Style` + `/template/ Border#NormalRectangle` / `Path#CheckGlyph` 够；`CornerRadius` 是 TemplateBinding，能直接改。
- 尺寸 20×20 是硬编码；要 24×24 或玻璃底座，必须重写模板/ControlTheme。不要试图用 `Width`/`Height` 改 `CheckBox` 控件本身来放大勾选框，内部 `NormalRectangle` 不会跟着变。

#### ToggleSwitch

Fluent 模板的尺寸全部硬编码：`OuterBorder` 40×20、`SwitchKnobBounds` 40×20、`PART_SwitchKnob` 20×20、`SwitchKnobOn/Off` 10×10；颜色是 `DynamicResource`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ToggleSwitch.xaml>（访问 2026-11-05）。

- 只改颜色：`Style` + `/template/ Border#SwitchKnobBounds` / `Ellipse#SwitchKnobOn/Off` 够；现有 `Controls.axaml` 已用 `SwitchKnobBounds`。
- 改尺寸/圆角/滑块形状/玻璃轨道：必须 `ControlTheme` 重写模板；`Style` 改不到硬编码的 Width/Height/CornerRadius。
- 重写时保留 `TemplatedControl.IsTemplateFocusTarget="True"`（Fluent 放在 `SwitchAreaGrid` 上），否则 `FocusAdorner` 找不到焦点目标。

#### ListBox / ListBoxItem

Fluent `ListBox` 模板只是 `Border` + `ScrollViewer` + `ItemsPresenter`；`ListBoxItem` 是单个 `ContentPresenter#PART_ContentPresenter`，`Background`/`BorderBrush`/`CornerRadius` 都 TemplateBinding。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ListBox.xaml>、<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ListBoxItem.xaml>（访问 2026-11-05）。

- 行背景/圆角/前景/选中色：`Style` + `/template/ ContentPresenter#PART_ContentPresenter` 够；现有 `Controls.axaml` 已这么做。
- 左侧 3px 蓝条/额外高光层：`ItemContainerTheme`（`ControlTheme TargetType="ListBoxItem"`）或自定义 `ItemTemplate` 包一层；只改 `ListBoxItem.Background` 做不出结构。
- `ListBox` 本身的背景/边框/内边距：`Style` 够。

#### ScrollBar

Fluent `ScrollBar` 的垂直/水平模板都有 `Rectangle#TrackRect`（Fill/Stroke 是 `DynamicResource`）和 `Thumb`；`Thumb` 使用独立 `FluentScrollBarThumb` ControlTheme，模板里只有一个 `Border`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ScrollBar.xaml>（访问 2026-11-05）。

- 只改颜色：`Style` + `/template/ Rectangle#TrackRect` / `Thumb` 可以；现有 `Controls.axaml` 的 `ScrollBar /template/ Thumb` 是有效的，因为 Thumb 的 `Background` 会 TemplateBinding 到它自己的 Border。
- 改轨道宽度/圆角胶囊/隐藏箭头：必须 `ControlTheme` 重写 ScrollBar 模板；`TrackRect`/`RepeatButton` 结构固定，`Style` 改不了。
- `ScrollBarSize` 等资源可以全局覆盖，但会影响所有方向和所有滚动条；属于令牌级改动，不是局部样式。

#### TabControl / TabItem

Fluent `TabControl` 模板是 `Border` + `DockPanel` + `ItemsPresenter` + `ContentPresenter`；`TabItem` 模板有 `Border#PART_LayoutRoot` 和 `Border#PART_SelectedPipe`（选中下划线）。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/TabControl.xaml>、<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/TabItem.xaml>（访问 2026-11-05）。

- TabControl 背景/内边距：`Style` 够。
- TabItem 静态选中胶囊（蓝底白字、圆角、隐藏下划线）：可以先试 `Style` + `/template/ PART_SelectedPipe` / `PART_LayoutRoot`；`TabItem.Background`/`Foreground`/`CornerRadius` 是 TemplateBinding。
- 共享滑动胶囊/分段控件（选中块在 Tab 之间移动）：Fluent 模板没有这个结构，必须自定义 `TabControl`/`TabItem` 模板或单独做 `ItemsControl` 分段控件；`Style` 做不到。

#### ToolTip

Fluent `ToolTip.xaml` 的模板是单个 `Border#PART_LayoutRoot` + `ContentPresenter`，`Background`/`BorderBrush`/`BorderThickness`/`Padding`/`CornerRadius` 都是 `TemplateBinding`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ToolTip.xaml>（访问 2026-11-05）。

- **黑底白字、8px 圆角：`Style` 就够**，不需要重写模板。
- 只有要玻璃层/内阴影/背景副本时才需要 `ControlTheme`。

#### ProgressBar

Fluent 模板有 `Border#PART_Indicator`，其 `Background` 是 `{TemplateBinding Foreground}`；轨道是根 Border 的 `Background`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ProgressBar.xaml>（访问 2026-11-05）。

- 4–6px 高、圆角、轨道色、蓝→青渐变：`Style` 够（`Foreground` 设 `LinearGradientBrush`）。
- 不确定进度的流动条/多色动画：模板里有 `IndeterminateProgressBarIndicator` 等固定结构，需要 `ControlTheme` 改动画/层。

#### Window

Fluent `Window.xaml` 模板包含 `PART_TransparencyFallback`、背景 Border、`VisualLayerManager`、`PART_ContentPresenter`；`VisualLayerManager` 负责弹层/装饰层。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/Window.xaml>（访问 2026-11-05）。

- 玻璃窗口背景：用 `Window.Background` + `TransparencyLevelHint`，或放一个 `ExperimentalAcrylicBorder` 作为窗口内容底层；**不要重写 Window 模板**，容易丢 `VisualLayerManager` 和弹层。
- 自绘标题栏：用 `ExtendClientAreaToDecorationsHint` / `WindowDecorations` + 自绘内容；窗口三键是普通 `Button`，按 Button 的规则处理。
- 最新文档里的 `WindowDecorationProperties.ElementRole` 在本机 **11.3.22 的 `Avalonia.Controls.dll` 中未列出**；不要按最新文档直接写，标 `[未验证]`（与 `docs/research/ui-redesign-baseline.md` 的提醒一致）。

#### Dialog

Avalonia 11.3 **没有内建 `Dialog` 控件**。官方 `Window management` 只提供 `Window.ShowDialog<T>`（<https://docs.avaloniaui.net/docs/app-development/window-management>，访问 2026-11-05）。因此：
- 模态对话框：新建 `Window`，设置 `WindowStartupLocation="CenterOwner"`，用 `ShowDialog`；
- 页内浮层：`Panel` + 半透明遮罩 + 玻璃 `Border`，自己处理焦点陷阱/Esc；
- 不存在“Dialog 的默认 Fluent 模板”可重写；玻璃效果按 §1.6 的卡片方式做。

---

## 3. `ControlTheme` vs `Style`

### 3.1 正确机制

**`ControlTheme`（A）：**

- 存在 `Avalonia.Styling.ControlTheme`，通常放在 `ResourceDictionary`，没有 `Selector`，用 `TargetType` 指定目标控件；可以用 `x:Key="{x:Type Button}"` 作为全局隐式主题，也可以用字符串 key + `Theme="{StaticResource ...}"` 只应用到某些实例。
- 一个控件同一时间只能有一个 `ControlTheme`；它可以整体替换 Fluent 的默认外观，支持 `BasedOn` 继承另一个主题。
- 它本身就是一种 `StyleBase`，可以包含嵌套 `Style Selector="^:pointerover"` 等状态。
- 全局替换时 `x:Key="{x:Type ToggleSwitch}"` 会遮住 Fluent 的同键主题；若还想 `BasedOn` 默认主题，先用 `{StaticResource {x:Type ToggleSwitch}}` 做一次解析验证，避免自引用/找不到资源（`[未验证]`）。
- 官方文档：<https://docs.avaloniaui.net/docs/styling/control-themes>（访问 2026-11-05）。

**`Style`（A）：**

- 放在 `Styles` 集合里，有 `Selector`；样式会累积，不能像 ControlTheme 那样整体移除。
- 作用域：放在 `Application.Styles` 是全局；放在 `Window.Styles` 只作用于该窗口及其内容；放在 `UserControl.Styles` 只作用于该用户控件。
- 官方文档：<https://docs.avaloniaui.net/docs/styling/styles>（访问 2026-11-05）。

### 3.2 优先级（重要）

官方 `Property value precedence` 给出：`Animation` > `LocalValue` > `StyleTrigger` > `Template` > `Style` > `Inherited` > `Unset`。来源：<https://docs.avaloniaui.net/docs/properties/value-precedence>（访问 2026-11-05）。

`ControlTheme` 没有单独出现在这张表里；11.3.22 源码的 `FramePriority` 把主题帧排在普通 Style 帧之后（`StyleTheme` 低于 `Style`），因此：

- **同等条件下，应用级 `Style` 可以覆盖 `ControlTheme` 的 Setter**；但这是实现细节，不是文档承诺，升级时要复核。A（源码）/ I（概括）。
- `TemplateBinding` 属于 `Template` 优先级（4）；普通的 `Style`（5）低于它，所以“在控件上设 `Background`”不一定能改变模板内部直接设置的值；`StyleTrigger`（3，例如 `:pointerover`）高于 `Template`，所以 `/template/` 的触发器通常能覆盖模板内部件。
- 动画（1）最高；正在做 Transition 的属性不要同时用 Style 硬抢值。

### 3.3 `Style Selector="^ /template/ ..."` 什么时候有效

官方 `Style selector syntax` 明确：`/template/` 选择的是**控件模板内部**的视觉树，不是逻辑树；`^` 是嵌套选择器的父级引用，必须出现在子选择器开头。来源：<https://docs.avaloniaui.net/docs/styling/style-selector-syntax>（访问 2026-11-05）。

有效条件：

1. 父选择器确实匹配到目标控件（例如 `TextBox` / `Button` / `ListBoxItem`）；
2. `^` 位于嵌套 Style 的开头；
3. 模板里确实有该类型 + 该 `x:Name` 的部件（例如 `Border#PART_BorderElement`）；
4. 该部件没有被更高优先级的 LocalValue/Animation 锁死；`StyleTrigger` 通常能压过模板 Setter，但普通 `Style` 不一定；
5. 目标不在 `DataTemplate` 或独立的 Popup 根里；`/template/` 不跨数据模板。

顶层写法要用完整选择器，例如：

```xml
<Style Selector="TextBox:pointerover /template/ Border#PART_BorderElement">
  <Setter Property="BorderBrush" Value="{DynamicResource Stroke.Strong}" />
</Style>
```

嵌套写法：

```xml
<Style Selector="TextBox">
  <Style Selector="^:focus /template/ Border#PART_BorderElement">
    <Setter Property="BorderBrush" Value="{DynamicResource Accent.Primary}" />
  </Style>
</Style>
```

**必须重写模板的情况：**

- 模板里没有你要的部件（例如 ToggleSwitch 的 40×20 轨道、ScrollBar 的 `TrackRect`、TabItem 的滑动块）。
- 你要新增一层（背景副本、白层、高光、内阴影层）。
- 你要改内部布局/行列/层数/尺寸。
- 部件的属性由模板硬编码（`DynamicResource`/字面量）且没有可覆盖的控件属性。

### 3.4 实践选择

| 目标 | 选择 |
|---|---|
| 只改颜色、字体、圆角、内边距、简单边框 | `Style` |
| 改模板内部已有部件的颜色/边框/焦点态 | `Style` + `/template/` |
| 给列表项换整套容器（左侧条/多层） | `ListBox.ItemContainerTheme`（ControlTheme） |
| 给某类控件整体换外观（ToggleSwitch、ScrollBar、TabItem） | `ControlTheme`（可 `BasedOn` 基础主题） |
| 只给一个实例换模板 | `Style` 设 `Template` 也可，但不可整体移除；优先 `ControlTheme` + `Theme` |
| 玻璃卡片/浮层 | 自建 `Border`/自定义控件，不走 Fluent 控件主题 |

---

## 4. Transitions 与令牌

### 4.1 `Duration`：现有 `Motion.Fast` 不能直接绑

**事实（A）：**

- `TransitionBase.Duration` 是 `DirectProperty<TransitionBase, TimeSpan>`；`Easing` 是 `DirectProperty<TransitionBase, Easing>`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Animation/TransitionBase.cs>（访问 2026-11-05）。
- `Tokens.axaml` 现有 `Motion.Fast` 是 `<x:Double>150</x:Double>`（毫秒），不是 `TimeSpan`。
- 因此 **`Duration="{DynamicResource Motion.Fast}"` 类型不匹配**；`Duration` 要 `TimeSpan`，`Motion.Fast` 给的是 `double`。

**可行写法 A（官方 Fluent 同款，推荐）：**

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="using:System">
  <sys:TimeSpan x:Key="Motion.Fast.Span">00:00:00.150</sys:TimeSpan>
  <sys:TimeSpan x:Key="Motion.Normal.Span">00:00:00.220</sys:TimeSpan>

  <Easing x:Key="Motion.Ease.Standard">0.22,0.9,0.24,1</Easing>
</ResourceDictionary>
```

```xml
<BrushTransition Property="Background"
                 Duration="{StaticResource Motion.Fast.Span}"
                 Easing="{StaticResource Motion.Ease.Standard}" />
```

官方 Fluent `SplitView.xaml` 就是这么做的：`<sys:TimeSpan x:Key="SplitViewPaneAnimationOpenDuration">00:00:00.2</sys:TimeSpan>`，`<Easing x:Key="SplitViewPaneAnimationEasing">0.1,0.9,0.2,1.0</Easing>`，然后 `Duration="{StaticResource ...}"`、`Easing="{StaticResource ...}"`。来源：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/SplitView.xaml>（访问 2026-11-05）。

**可行写法 B（C# 单一源）：**

```csharp
public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(150);
public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(220);
```

```xml
<BrushTransition Property="Background"
                 Duration="{x:Static services:Motion.Fast}" />
```

优点：和 `Motion.Dur`/`Motion.Enabled` 在同一个 C# 类型里，减少 XAML 与 C# 两份时长漂移。限制：`x:Static` 是编译期静态值，运行时 `Motion.Enabled=false` 不会自动改已加载的 `Transitions`；需要像 `PageHost`/`CollapseHost` 那样重建 `Transitions`，或接受「只对新控件生效」。

**关于 `{DynamicResource}`：**

- `Duration` 是 `DirectProperty`，`DynamicResourceExtension` 可以作为绑定目标；`ValueStore.AddBinding` 对 direct property 也有处理路径（源码 `Avalonia.Base`）。A。
- 但官方 Fluent 没有用 `Duration="{DynamicResource ...}"`，只用了 `{StaticResource}`；社区主题有 `Duration="{DynamicResource ControlAnimationDuration}"` 的用法，但 11.3.22 未在官方源中找到实例。**在 `Transitions` 对象内用 `DynamicResource` 的行为标 `[未验证]`；实施前做一次最小 XAML 编译/运行。**
- 如果资源会随主题切换（深浅色时长不同），才值得用 DynamicResource；本项目时长不随主题变，用 `StaticResource` 更稳。

### 4.2 `Easing` 资源化

**可行（A）：**

```xml
<Easing x:Key="Motion.Ease.Standard">0.22,0.9,0.24,1</Easing>
```

`Easing` 有 `[TypeConverter(typeof(EasingTypeConverter))]`；四段数字会解析成 `SplineEasing`。本机实测 `Easing.Parse("0.22,0.9,0.24,1")` 返回 `SplineEasing`，`Ease(0.5)=0.95696...`（A）。

也可以用具体类：

```xml
<CubicEaseOut x:Key="Motion.Ease.Out" />
```

```xml
<DoubleTransition Property="Opacity"
                  Duration="{StaticResource Motion.Fast.Span}"
                  Easing="{StaticResource Motion.Ease.Out}" />
```

**限制：**

- `Easing` / `Duration` 是 `Transition` 的属性，不是 `Control` 的属性；不能写 `<Setter Property="Easing">` 到控件 Style 上。必须写在 `Transitions` 对象里的 `<DoubleTransition>` 等元素上。
- `Easing` 对象可以复用（同一实例被多个 Transition 引用）；`SplineEasing` 是配置值，不要在运行时修改。
- `Transitions` 本身是 `Animatable.Transitions` 的 `StyledProperty`；一个 Style Setter 设置的是整个 `Transitions` 集合。资源切换/主题切换不会自动重建集合；需要重建时用代码（现有 `PageHost` 是正确参照）。
- 现有的 `Motion.cs` 只有 `Dur`/`Enabled`，并没有读取 `Tokens.axaml` 的 `Motion.*`；`Tokens.axaml` 第 262 行注释“C# 侧 Motion.cs 同步读取”与实现不一致。A（仓库）。

### 4.3 建议的令牌结构

```text
Motion.Fast.Span     TimeSpan  150ms
Motion.Normal.Span   TimeSpan  220ms
Motion.Slow.Span     TimeSpan  320ms
Motion.Ease.Standard Easing    cubic-bezier(.22,.9,.24,1)
Motion.Ease.Exit     Easing    cubic-bezier(.4,0,1,1)
```

- 时长给 `Transitions` 用 `TimeSpan` 资源；
- C# 侧保留 `Motion.Dur(int)` 供代码创建的 Transition 使用；
- 两边都从同一组数值生成/校对，避免 `Tokens.axaml` 的 `x:Double` 和 `Transition.Duration` 各写一份。

---

## 5. 图标

### 5.1 `PathIcon`、`StreamGeometry`、`Path` 的差别

| 方式 | 是什么 | 着色 | 适合 | 限制 | 级别 |
|---|---|---|---|---|---|
| `PathIcon` | `TemplatedControl`，只有 `Data`（`Geometry`）；内部用 `Foreground` 填充 | 继承/设置 `Foreground`，天然跟随父控件 | 面性/填充图标；按钮、菜单里的单色图标 | **没有 `Stroke`/`StrokeThickness`**；线性 SVG 直接塞进去会被当填充轮廓 | A |
| `StreamGeometry` | 轻量、不可变 `Geometry`，可放 `ResourceDictionary` | 无颜色；只是几何 | 复用 path data；`Path` / `PathIcon` 的 `Data` | 不是控件；不能单独显示 | A |
| `Path` + `Data` | `Shape` 控件，有 `Fill` / `Stroke` / `StrokeThickness` / `StrokeLineCap` / `StrokeJoin` / `Stretch` | `Fill`/`Stroke` 显式设置；`Stroke` 不继承 `Foreground` | **24 视口、1.7–1.9 线性图标** | 需要自己绑定颜色；参与布局 | A |

官方 `PathIcon` 文档明确说 `Foreground` 是“brush used to fill the icon”，并可从父控件继承：<https://docs.avaloniaui.net/controls/media/pathicon>（访问 2026-11-05）。
`Path` 的 stroke 属性：<https://docs.avaloniaui.net/docs/graphics-animation/shapes-and-geometries>（访问 2026-11-05）。

### 5.2 24 视口、stroke 1.7–1.9 的推荐落法

**推荐：`StreamGeometry` 资源 + `Path` + `Stroke`。**

```xml
<Application.Resources>
  <StreamGeometry x:Key="Icon.Play">M 8 5 L 19 12 L 8 19 Z</StreamGeometry>
</Application.Resources>
```

```xml
<Path Data="{StaticResource Icon.Play}"
      Width="24" Height="24"
      Stretch="Uniform"
      Fill="Transparent"
      Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
      StrokeThickness="1.8"
      StrokeLineCap="Round"
      StrokeJoin="Round" />
```

- `Fill="Transparent"`：避免默认黑色填充；线性图标只描边。
- `StrokeLineCap="Round"` / `StrokeJoin="Round"`：对应方向卡的圆头/圆角。
- `Stretch="Uniform"`：24×24 源在 24×24 显示时基本 1:1；如果要缩到 16/20，stroke 会随几何一起缩放，需要按尺寸档单独调 `StrokeThickness`，或用 `Viewbox` 并接受缩放后的视觉线宽。I。
- 如果图标源是 24 视口、`stroke-width=1.8`，直接照抄坐标；不要用 `PathIcon` 再转一遍，否则线宽会变成填充轮廓的宽度，后续无法调。

### 5.3 让线性图标跟随 `Foreground`

`PathIcon` 直接继承 `Foreground`：

```xml
<Button Foreground="{DynamicResource Text.Primary}">
  <PathIcon Data="{StaticResource Icon.Save}" Width="16" Height="16" />
</Button>
```

`Path` 没有 `Foreground` 属性，必须显式绑定：

```xml
<Path Data="{StaticResource Icon.Play}"
      Stroke="{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}"
      StrokeThickness="1.8" />
```

或者在自定义 `Icon` 控件模板里用 `{TemplateBinding Foreground}`：

```xml
<ControlTheme x:Key="NebulaIcon" TargetType="local:NebulaIcon">
  <Setter Property="Template">
    <ControlTemplate>
      <Path Data="{TemplateBinding Data}"
            Stroke="{TemplateBinding Foreground}"
            StrokeThickness="{TemplateBinding StrokeThickness}"
            StrokeLineCap="Round"
            StrokeJoin="Round" />
    </ControlTemplate>
  </Setter>
</ControlTheme>
```

- 选中态反白：把父控件 `Foreground` 设为 `Text.OnAccent`，`Path` 的绑定会自动跟随；不要为同一个图标维护两份颜色资源。A/I。
- 禁用态：父控件 `Foreground` 变 `Text.Muted`，绑定同样跟随；不需要单独改 `Path`。

---

## 6. 焦点环

### 6.1 机制

- `Control.FocusAdorner` 的类型是 `StyledProperty<ITemplate<Control>?>`；默认值来自 `AdornerLayer.DefaultFocusAdorner`。源码：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Controls/Control.cs>（访问 2026-11-05）。
- `Control.OnGotFocus` 只在 `NavigationMethod.Tab` 或 `NavigationMethod.Directional` 时创建/添加焦点环；鼠标点击不会显示。这天然接近 `:focus-visible` 的语义。源码：`Control.cs` 的 `OnGotFocus`（同上）。
- 焦点环会挂到 `GetTemplateFocusTarget()` 返回的模板部件；Fluent 的 `ToggleSwitch` 用 `TemplatedControl.IsTemplateFocusTarget="True"` 标在 `SwitchAreaGrid` 上。自定义模板必须保留这个标记，否则没有可装饰的目标。A（源码）。
- `FocusAdornerTemplate` 是 XAML 里的专用模板类型；测试用例：<https://github.com/AvaloniaUI/Avalonia/blob/master/tests/Avalonia.Markup.Xaml.UnitTests/Xaml/StyleTests.cs>（访问 2026-11-05，约 154–160 行）。

### 6.2 2px 蓝环写法

```xml
<Style Selector="Button">
  <Setter Property="FocusAdorner">
    <FocusAdornerTemplate>
      <!-- 默认 AdornerLayer 会裁到控件边界；要画外环必须关掉它的裁切 -->
      <Border AdornerLayer.IsClipEnabled="False"
              Margin="-3"
              BorderBrush="{DynamicResource Stroke.Focus}"
              BorderThickness="2"
              CornerRadius="{DynamicResource Radius.Sm}"
              IsHitTestVisible="False" />
    </FocusAdornerTemplate>
  </Setter>
</Style>
```

- `Margin="-3"` 让 2px 环画在控件外侧，不遮内容；如果控件本身有 1px 边框，按 `-2`/`-3` 调。
- **关键**：`AdornerLayer.IsClipEnabled` 默认 `True`，焦点环会被裁到被装饰元素的 `Bounds`；不关掉它，负 margin 的外环会被裁掉。若不想关裁切，就把 `Margin` 设为 `0`，让 2px 环画在控件内侧（会轻微压住内容边缘）。源码：`AdornerLayer.ArrangeOverride` / `UpdateClip`（`Avalonia.Controls/Primitives/AdornerLayer.cs`，访问 2026-11-05）。若默认 xmlns 解析不到 `AdornerLayer`，在文件里补 `xmlns:primitives="using:Avalonia.Controls.Primitives"`，写成 `primitives:AdornerLayer.IsClipEnabled="False"`。
- `CornerRadius` 要与目标控件一致；Button/TextBox/ListBoxItem 的圆角不同，建议按控件分别设，或做一个 `FocusRing` 资源 + 每类控件覆盖 `CornerRadius`。
- 文本框内部已经用 `PART_BorderElement` 改焦点边框；如果再加外环，视觉上会是“双层焦点”，二选一：要么内部描边变蓝，要么外环。方向卡的 2px 蓝环建议用外环，内部边框保持 1px。

### 6.3 `:focus-visible`

- 11.3 的 `Pseudoclasses` 文档列出 `:focus-visible`：控件有焦点且应显示视觉指示时激活；`Button` 等控件可用。来源：<https://docs.avaloniaui.net/docs/styling/pseudoclasses>（访问 2026-11-05）。
- Fluent 的 `ComboBox` 模板自己就用 `:focus-visible` 控制 `HighlightBackground`（源码 220–234 行），说明它在 11.3 的 Fluent 主题里是实际使用的，不只是文档存在。
- 用法：

```xml
<Style Selector="TextBox:focus-visible /template/ Border#PART_BorderElement">
  <Setter Property="BorderBrush" Value="{DynamicResource Stroke.Focus}" />
  <Setter Property="BorderThickness" Value="2" />
</Style>
```

- `ComboBox` 的 Fluent 主题把 `FocusAdorner` 设为 `{x:Null}`，并用 `HighlightBackground` 做焦点：要给它加外环，需要显式在 `ComboBox` 的 Style/ControlTheme 里设 `FocusAdorner`，或者用 `:focus-visible /template/ Border#HighlightBackground` 改内部高亮。A。
- `:focus` 与 `:focus-visible` 的差别：`:focus` 包含鼠标点击导致的焦点；`:focus-visible` 只在应显示指示时激活。优先用 `:focus-visible` 做视觉焦点，避免鼠标点击也亮环。A（文档语义）/ I（具体控件行为）。

---

## 7. 对 #70 的落地建议

1. **玻璃卡片首版用「降级近似」而不是硬做真 backdrop**：`Border` 半透明底 + 1px 亮边 + `BoxShadow` 内阴影/外投影。若方向评审坚持要「背景被糊开」，第二阶段再做 `GlassBackdropCopy`（同源副本 + `BlurEffect`），不要用 `ExperimentalAcrylicBorder` 冒充局部卡片。
2. **控件分三档写规范**：
   - A 档只改属性：Button（除 `.accent` 冲突）、TextBox（表面/焦点）、ToolTip（黑底白字）、ProgressBar（渐变/高度）、ListBox/ListBoxItem（颜色/圆角）。
   - B 档 `Style + /template/`：TextBox `PART_BorderElement`、ComboBox 外框/PopupBorder 颜色、CheckBox 勾选块颜色、ListBoxItem 选中态、TabItem 静态胶囊、ScrollBar Thumb 颜色。
   - C 档必须 `ControlTheme`/模板：ToggleSwitch 轨道/滑块几何、ScrollBar 细轨道/胶囊/箭头、TabItem 滑动胶囊、ComboBox 玻璃 Popup、任何要背景副本/多层的卡片。
3. **图标统一 `Path` + `Stroke`**：24 视口、`StrokeThickness=1.8`、`StrokeLineCap=Round`、`StrokeJoin=Round`；`StreamGeometry` 放 `Themes/Icons.axaml`，按语义命名；不要用 `PathIcon` 承载线性描边。
4. **焦点环统一 `FocusAdorner` + `:focus-visible`**：2px `Stroke.Focus` 蓝环；文本框可选内部 2px 边框，不叠加双层；`ComboBox` 要显式覆盖其 `FocusAdorner="{x:Null}"`。
5. **动效令牌拆成 `TimeSpan`**：不要让 `Transitions.Duration` 直接吃 `x:Double` 的 `Motion.Fast`；新增 `Motion.Fast.Span` 等 `sys:TimeSpan` 资源，或 C# `TimeSpan` 常量 + `x:Static`；`Easing` 用 `<Easing>` 资源复用，官方 Fluent 已验证这种写法。
6. **不引新 NuGet**：以上全部是 Avalonia 11.3.22 内建类型；`GlassBackdropCopy` 是项目内自定义控件，不需要新包。

---

## 8. 未验证 / 实施前必须做的最小样张

1. **`ExperimentalAcrylicBorder` 的局部卡片效果**：`Digger` 在 NebulaLauncher 的窗口结构下到底显示窗口背景还是页面背景；在不同 Windows 10/11、不同 `CompositionMode` 下截图对比。`[未验证]`
2. **`BlurEffect` 的真实性能**：在 1240×800 窗口、两张玻璃卡片、背景副本 26px blur 下测帧时间；滚动/窗口 resize 时是否掉帧。`[未验证]`
3. **`VisualBrush` 引用树内 `Visual` 的刷新/递归行为**：是否随源视觉变化实时更新，是否会把卡片自身也画进去。`[未验证]`
4. **`Duration="{DynamicResource ...}"` 在 `Transitions` 对象内**：官方 Fluent 只用 `StaticResource`；做一次最小 XAML 编译 + 主题切换，确认不抛错。`[未验证]`
5. **`ComboBox` Popup 的 `/template/ Border#PopupBorder`**：Popup 打开后是独立顶层，确认样式在真实弹出层里生效。`[未验证]`
6. **`WindowDecorationProperties.ElementRole`**：本机 11.3.22 的 `Avalonia.Controls.dll` 未列出该类型；不要按最新文档直接写，先用反射/最小窗口确认。`[未验证]`
7. **`BoxShadow` 在 `ContentPresenter` 上的 inset + `CornerRadius` 裁切**：Button 的 ContentPresenter 加内阴影时，圆角外的阴影是否被裁切；需要截图确认。`[未验证]`

---

## 9. 参考清单

> 以下来源均于 **2026-11-05** 访问；官方文档/源码为 A 级，官方产品页为 B 级。本笔记未使用 B 级材料作为结论依据。

### 官方文档

- Control themes — <https://docs.avaloniaui.net/docs/styling/control-themes>
- Styles — <https://docs.avaloniaui.net/docs/styling/styles>
- Style selector syntax（含 `/template/`）— <https://docs.avaloniaui.net/docs/styling/style-selector-syntax>
- Control template walkthrough — <https://docs.avaloniaui.net/docs/styling/control-template-walkthrough>
- Property value precedence — <https://docs.avaloniaui.net/docs/properties/value-precedence>
- Pseudoclasses（含 `:focus-visible`）— <https://docs.avaloniaui.net/docs/styling/pseudoclasses>
- Effects（`BlurEffect`、`BoxShadow` inset、`OpacityMask`）— <https://docs.avaloniaui.net/docs/graphics-animation/effects>
- Control transitions — <https://docs.avaloniaui.net/docs/graphics-animation/control-transitions>
- Easing functions — <https://docs.avaloniaui.net/docs/graphics-animation/easing-functions>
- Clipping and masking — <https://docs.avaloniaui.net/docs/graphics-animation/clipping-and-masking>
- Shapes and geometries（`Path`/`Stroke`/`StreamGeometry`）— <https://docs.avaloniaui.net/docs/graphics-animation/shapes-and-geometries>
- Adding icons — <https://docs.avaloniaui.net/docs/graphics-animation/adding-icons>
- PathIcon — <https://docs.avaloniaui.net/controls/media/pathicon>
- Windows platform guide（WinUIComposition / AcrylicBlur / Mica）— <https://docs.avaloniaui.net/docs/platform-specific-guides/windows>
- Window management（`ShowDialog`、`TransparencyLevelHint`）— <https://docs.avaloniaui.net/docs/app-development/window-management>
- `ExperimentalAcrylicBorder` API — <https://docs.avaloniaui.net/api/avalonia/controls/experimentalacrylicborder>
- `ExperimentalAcrylicMaterial` API — <https://docs.avaloniaui.net/api/avalonia/media/experimentalacrylicmaterial>
- `VisualBrush` API — <https://docs.avaloniaui.net/api/avalonia/media/visualbrush>
- `RenderTargetBitmap` API — <https://docs.avaloniaui.net/api/avalonia/media/imaging/rendertargetbitmap>
- `Control` API（`FocusAdorner`）— <https://docs.avaloniaui.net/api/avalonia/controls/control>

### 官方源码（`release/11.3` 分支）

- `BoxShadow.cs`（`IsInset`/parser）— <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Media/BoxShadow.cs>
- `BlurEffect.cs` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Media/Effects/BlurEffect.cs>
- `Effect.cs`（`Effect.Parse("blur(...)")`）— <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Media/Effects/Effect.cs>
- `ExperimentalAcrylicBorder.cs` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Controls/ExperimentalAcrylicBorder.cs>
- `ExperimentalAcrylicMaterial.cs` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Media/ExperimentalAcrylicMaterial.cs>
- `AcrylicBackgroundSource.cs`（`Digger` 注释）— <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Media/AcrylicBackgroundSource.cs>
- `ServerCompositionExperimentalAcrylicVisual.cs` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionExperimentalAcrylicVisual.cs>
- `TransitionBase.cs`（`Duration`/`Easing` 为 direct property）— <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Animation/TransitionBase.cs>
- `Control.cs`（`FocusAdorner`/`OnGotFocus`）— <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Controls/Control.cs>
- `AdornerLayer.cs`（`DefaultFocusAdorner`）— <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Controls/Primitives/AdornerLayer.cs>
- `Easing.cs`（类型转换器）— <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Base/Animation/Easings/Easing.cs>
- Fluent `Button.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/Button.xaml>
- Fluent `TextBox.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/TextBox.xaml>
- Fluent `ComboBox.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ComboBox.xaml>
- Fluent `CheckBox.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/CheckBox.xaml>
- Fluent `ToggleSwitch.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ToggleSwitch.xaml>
- Fluent `ListBox.xaml` / `ListBoxItem.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ListBox.xaml>、<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ListBoxItem.xaml>
- Fluent `ScrollBar.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ScrollBar.xaml>
- Fluent `TabControl.xaml` / `TabItem.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/TabControl.xaml>、<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/TabItem.xaml>
- Fluent `ToolTip.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ToolTip.xaml>
- Fluent `ProgressBar.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/ProgressBar.xaml>
- Fluent `Window.xaml` — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/Window.xaml>
- Fluent `SplitView.xaml`（`sys:TimeSpan` + `Easing` 资源写法）— <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Themes.Fluent/Controls/SplitView.xaml>
- `FocusAdornerTemplate` 测试用例 — <https://github.com/AvaloniaUI/Avalonia/blob/master/tests/Avalonia.Markup.Xaml.UnitTests/Xaml/StyleTests.cs>

### 本机程序集核对（A）

- `%USERPROFILE%/.nuget/packages/avalonia/11.3.22/lib/net8.0/Avalonia.Base.dll`
- `%USERPROFILE%/.nuget/packages/avalonia/11.3.22/lib/net8.0/Avalonia.Controls.dll`
- `%USERPROFILE%/.nuget/packages/avalonia.themes.fluent/11.3.22/lib/net8.0/Avalonia.Themes.Fluent.dll`
- `%USERPROFILE%/.nuget/packages/avalonia.skia/11.3.22/lib/net8.0/Avalonia.Skia.dll`（`PushEffect` 的 `SaveLayer` 语义）
- 本机实测：`BlurEffect` 类型存在；`Effect.Parse("blur(26)")` 返回 `ImmutableBlurEffect`；`BoxShadow.Parse("inset 0 2 0 #fff")` 成功、`inset 0 2px 0 #fff` 失败；`Easing.Parse("0.22,0.9,0.24,1")` 返回 `SplineEasing`。

### 仓库内参考

- `.oil-ui/nebula-2/explore/glass-direction.md`（方向卡）
- `src/NebulaLauncher.App/Themes/Tokens.axaml`（268 行）
- `src/NebulaLauncher.App/Themes/Controls.axaml`（371 行）
- `src/NebulaLauncher.App/Services/Motion.cs`（34 行）
- `Directory.Packages.props`（Avalonia 11.3.22）
- `docs/research/ui-redesign-baseline.md`（同仓 Avalonia 动效/窗口能力基线）
