# 桌面启动器与 Avalonia 动效能力基准

> **方向已作废（2026-11-05）**：本文成文于 Blockwright 方向，其中「不要玻璃拟态」「哑光表面」等视觉结论不适用于已定版的现代液态玻璃方向；Avalonia 能力、标题栏、可访问性等事实结论仍有效。玻璃实现以 `docs/research/avalonia-glass-controls-20261105.md` 为准。

> 一句话结论：2023–2026 的官方材料显示，同类启动器已收敛到「**一个主行动 + 一个可分组实例库 + 一个可搜索的内容入口**」；Avalonia 11.3 能直接支撑页面级 CrossFade/PageSlide、控件级 Transitions、RenderTransform 与 Composition 动画，但**共享元素转场没有内置实现**；无边框窗口在 11.3 可用 `ExtendClientAreaToDecorationsHint` + `ExtendClientAreaChromeHints` 自绘标题栏，官方最新文档另有 `WindowDecorationProperties.ElementRole="TitleBar"` 的原生拖拽方案（版本待核）。

- 研究票：#73（父地图 #69，下游 #72 Blockwright 方向卡）
- 访问日期：全文所有来源均于 **2026-11-02** 访问
- 方法：一手来源优先（官方文档、官方 API 参考、官方仓库源码、官方设计系统、官方产品页）。事实与推断分离：推断行内标 `[推断]`，未取得一手证据的标 `[未验证]`。
- 写作范围：只写 `docs/research/`；不改 `src/`、不改 issue 状态。

---

## 0. 证据分级

| 级别 | 含义 | 本文用法 |
| --- | --- | --- |
| A | 官方文档 / 官方 API 参考 / 官方源码 / 官方设计系统 | 直接作为事实引用 |
| B | 官方博客 / 官方产品页 / 官方帮助中心 | 作为事实引用，注明「厂商自述」 |
| C | 第三方评测、社区讨论 | 只作交叉印证，不单独支撑结论 |
| I | 推断 | 行内标 `[推断]`，不进入「可直接用」栏 |

---

## 1. 同类启动器与游戏库的信息架构

### 1.1 对照表

| 产品 | 首页第一屏 | 启动主行动如何突出 | 版本/实例列表组织 | 下载/安装流程 | 证据 |
| --- | --- | --- | --- | --- | --- |
| **Prism Launcher** | 实例列表即首页；顶部菜单栏 **Add Instance**，中部空白右键也有 **Create instance** | 选中实例后由实例卡片/工具栏触发，属「先选后动」 | 实例卡片视图（源码 `instanceview/InstanceDelegate`、`VisualGroup`）；实例内部用标签页：Version / Mods / Worlds / Servers / Logs 等 | 新建向导：先选 MC 版本与 modloader，或从左侧栏选 mod 平台导入整合包 | A |
| **Modrinth App** | **Play 页**（2026-08 起合并旧 Home 与 Library）：顶部 **Jump in** 最多 5 槽（最近 + 新建实例），下方 **Library** | 官方把「让如何真正启动游戏更清楚」列为 Play 页三个设计目标之一；Jump in 卡片即一键继续 | 支持建组、多选、拖拽跨组、组内改名与排序；实例图标可自定义 | 创建菜单四选一：搜索内容 / 自定义实例 / 去 Discover / 上传 `.mrpack` 或从其他启动器导入 | B |
| **GDLauncher** | 实例列表 + 一键安装整合包入口 | 动线写成「Pick a modpack → click install → You just hit play」 | 实例彼此隔离，可分组、复制、从其他启动器导入 | 同时搜 CurseForge 与 Modrinth，依赖自动处理，**并行下载**，自动管 Java | B |
| **ATLauncher** | 顶部一级标签：**Create Pack / Instances / Packs Browser / Servers / Settings** | 启动在 Instances 标签内（代码结构），具体按钮文案未核 | 实例为独立标签；Packs Browser 是并列的内容发现入口 | Create Pack 承载新建，整合包浏览并列一级 | A（结构） |
| **CurseForge** | `[未验证]`：官方下载页被 Cloudflare 拦截，未取得应用界面一手截图 | `[未验证]` | `[未验证]` | 官方 API 站为内容模型（game → mod → file），不描述客户端 UI | — |
| **Steam 库** | `[未验证]`：官方新闻页抓取超时 | `[未验证]` | `[未验证]` | `[未验证]` | — |
| **Xbox 应用** | `[未验证]`：官网与 Microsoft Store 页均为 JS 渲染，抓不到正文 | `[未验证]` | `[未验证]` | `[未验证]` | — |

> 说明：CurseForge / Steam / Xbox 三行是本次唯一没有拿到一手 UI 证据的部分。为避免把印象写成事实，这里留空并标 `[未验证]`，对应推断集中在 §1.3，参考清单列出已尝试的官方入口，供下一轮补证。

### 1.2 可下的事实判断（A/B 级）

1. **首页正在从「列表」变成「继续 + 库」两段式。** Modrinth 官方把 Home 与 Library 合并成 Play，理由分别是「把与玩相关的一切放一处」「让如何启动更清楚」「改进实例组织」；Jump in 固定 5 槽，且新建实例也会进入，用来解决新用户首屏空白。来源：<https://github.com/modrinth/code/blob/main/packages/blog/articles/app-personalization.md>（访问 2026-11-02）——事实，B 级（厂商自述）。
2. **Prism 是「实例即首页」的极端：没有独立推荐/发现首页。** 官方 wiki 的创建路径就是顶部 Add Instance 或中部空白右键 → Create instance，说明主区域即实例画布。来源：<https://prismlauncher.org/wiki/getting-started/create-instance/>（访问 2026-11-02）——事实，A 级。
3. **Prism 把实例详情做成标签页而不是二级页面。** 源码 `launcher/ui/pages/instance/` 下并列 `VersionPage`、`ModFolderPage`、`WorldListPage`、`ServersPage`、`LogPage`、`ScreenshotsPage`、`NotesPage`；官方 wiki 的 Version 页按 Selection / Edit / Install / Advanced / Folder 分区。来源：<https://github.com/PrismLauncher/PrismLauncher/tree/develop/launcher/ui/pages/instance>、<https://prismlauncher.org/wiki/help-pages/instance-version/>（访问 2026-11-02）——事实，A 级。
4. **ATLauncher 把「建包」和「逛包」拆成两个一级标签。** `gui/tabs/` 下同时有 `CreatePackTab`、`InstancesTab`、`PacksBrowserTab`、`ServersTab`、`SettingsTab`。来源：<https://github.com/ATLauncher/ATLauncher/tree/master/src/main/java/com/atlauncher/gui/tabs>（访问 2026-11-02）——事实，A 级。
5. **「一键安装 + 自动 Java + 并行下载」已是基线能力，不再是差异点。** GDLauncher 官网把它写成默认能力，并在对比表逐项列 Prism/ATLauncher/CurseForge/Modrinth 的支持情况。来源：<https://gdlauncher.com/>（访问 2026-11-02）——厂商自述，B 级；对比表为竞品自评，只作参考。
6. **实例分组的交互范式已统一为「多选 → 建组 → 拖拽」。** Modrinth 官方描述：页面 New group 按钮、选中多实例后在 action bar 点 New group、组内改名/换实例/排序、实例可在组间拖拽。来源同第 1 条——事实，B 级。
7. **首屏空状态被当成一等设计对象。** Modrinth 同文专列 Empty states：Play 页、Hosting 页、未登录也能玩的皮肤编辑器，并给出「Welcome + 创建第一个实例」按钮式空态。来源同第 1 条——事实，B 级。
8. **Prism 的实例卡片有独立的数据代理层。** 源码有 `InstanceProxyModel` 与 `InstanceView` 分离，说明实例列表是「模型/过滤/视图」三层，而不是一次性绑定。来源：<https://github.com/PrismLauncher/PrismLauncher/tree/develop/launcher/ui/instanceview>（访问 2026-11-02）——事实，A 级。

### 1.3 平台型游戏库的推断（标 I，不作为事实）

- `[推断]` Steam 与 Xbox 这类「已拥有内容多、启动频率高」的库，首屏必然把「最近/继续」放在最上，而不是把全部库铺开；依据是它们的产品目标是降低「回到上次玩的游戏」的成本，而 Modrinth 的 Jump in 已经验证了同一条路径在启动器上成立。
- `[推断]` CurseForge 的客户端以「游戏 → 整合包 → 文件」三层内容模型组织（其官方 API 文档的实体层级即为 game → mod → file，见 <https://docs.curseforge.com/>，访问 2026-11-02），因此它的首页更可能是「选游戏 → 逛包」，而不是「实例列表」。这一点需在拿到应用截图后再确认。
- `[推断]` 对 NebulaLauncher：不必模仿平台型库的「多游戏」层级；单游戏启动器应把层级压到「继续 → 实例库 → 实例详情」三层，超过三层就会丢掉启动速度。

---

## 2. Avalonia 11.3 的能力边界

> 版本口径：仓库锁定 **Avalonia 11.3.22**（`CONVENTIONS.md` §1）。本节引用的在线文档为 2026 年版；凡文档未标版本的 API，均已尝试用 `release/11.3` 分支源码交叉核对，结果见 §2.8。

### 2.1 页面转场：可直接用

| 能力 | 结论 | 来源 |
| --- | --- | --- |
| `TransitioningContentControl` | 内置控件，`Content` 变化时播放 `PageTransition`；未设置时用主题默认转场；`PageTransition="{x:Null}"` 可整体关闭 | <https://docs.avaloniaui.net/controls/data-display/transitioningcontentcontrol>（访问 2026-11-02） |
| `CrossFade` | 内置，淡出旧视图 + 淡入新视图；官方建议用于「无方向」的 tab 式导航，高度差异大时比滑动更稳 | <https://docs.avaloniaui.net/docs/graphics-animation/page-transitions>（访问 2026-11-02） |
| `PageSlide` | 内置，横向/纵向；`forward` 参数控制方向，适合向导与前进/后退 | 同上 |
| `CompositePageTransition` | 内置，多转场并行；官方提醒子转场 `Duration` 要一致，否则会有「一个先结束」的伪影 | 同上 |
| 自定义 `IPageTransition` | 单方法 `Start(from, to, forward, cancellationToken)`；必须尊重取消令牌，否则连续导航会叠动画 | 同上 |
| `IsTransitionReversed` | `TransitioningContentControl` 属性，可让转场反向播放 | <https://docs.avaloniaui.net/controls/data-display/transitioningcontentcontrol>（访问 2026-11-02） |

**落地判断**：若现有导航是「一个 ContentControl 换 ViewModel」，改成 `TransitioningContentControl` + `CrossFade(0:0:0.18~0.22)` 是**纯 XAML 改动**，不需要自定义控件。页面高度差异大的场景（首页 → 版本列表）优先 CrossFade，不要 PageSlide。

### 2.2 控件级 Transitions：可直接用，但有类型限制

- `Transitions` 可挂任意 `Control`，监听属性变化并按缓动插值；与关键帧动画不同，**转场在控件隐藏时不会暂停**。来源：<https://docs.avaloniaui.net/docs/graphics-animation/control-transitions>（访问 2026-11-02）——A 级。
- 必须用与属性类型匹配的转场类，官方完整对照：`BoolTransition` / `BoxShadowsTransition` / `BrushTransition` / `ColorTransition` / `CornerRadiusTransition` / `DoubleTransition` / `FloatTransition` / `IntegerTransition` / `PointTransition` / `SizeTransition` / `ThicknessTransition` / `TransformOperationsTransition` / `VectorTransition`。来源同上——A 级。
- **限制（关键）**：WPF 风格的 `RotateTransform` / `ScaleTransform` **不能**被 transition；要过渡 `RenderTransform` 必须用 CSS 风格字符串（`rotate(45deg)`、`translate(12px)`、`scale(1.02)`）。来源同上——A 级。
- 因此 Blockwright 若用「位移/切角」做 hover 反馈，属性写法必须是 `RenderTransform="translate(0px,-2px)"` 这类字符串，而不是 `<TranslateTransform>` 对象。

### 2.3 RenderTransform vs LayoutTransform：性能分界

- RenderTransform **不参与布局**、不触发 measure/arrange，官方明确「更快（无 re-layout）」；LayoutTransform 每帧触发布局重算，官方建议不要在性能敏感场景动画化。来源：<https://docs.avaloniaui.net/docs/graphics-animation/render-vs-layout-transforms>（访问 2026-11-02）——A 级。
- Avalonia 的 `RenderTransformOrigin` 默认 **50%,50%**（WPF 是 0,0）。抄 WPF 代码时这是真实坑。来源同上——A 级。
- 官方取舍表：动画/悬停/视差 → RenderTransform；需要相邻控件让位、旋转文字标签 → LayoutTransform。来源同上——A 级。

### 2.4 关键帧 Animation：可直接用，注意「隐藏即暂停」

- 关键帧动画定义在 Style 里，`Duration` 格式 `"小时:分钟:秒"`，支持 `Delay`、`IterationCount`、`PlaybackDirection`、`FillMode`、`Easing`。来源：<https://docs.avaloniaui.net/docs/graphics-animation/keyframe-animations>（访问 2026-11-02）——A 级。
- **默认 `PlaybackBehavior` 会在控件不可见时暂停动画**，重新可见时从暂停处继续；`PlaybackBehavior="Always"` 可改。该行为只作用于关键帧动画，不影响 Transitions 与 Composition（官方明示）。来源同上——A 级。
- 从代码跑动画用 `Animation.RunAsync(control, ct)`；无限循环的动画任务不会结束，除非取消或控件离开可视树。来源同上——A 级。

### 2.5 Composition 动画：能上渲染线程，但属性面很窄

- Composition 动画「直接在渲染线程运行」，官方定位是「需要程序化控制、需要渲染线程性能、或需要 XAML 关键帧不支持的属性」。来源：<https://docs.avaloniaui.net/docs/graphics-animation/composition-animations>（访问 2026-11-02）——A 级。
- **可动画属性只有三个**：`CompositionVisual.Offset`（Vector3D）、`Opacity`（float）、`Size`（Vector）。来源同上——A 级。
- 用法：`ElementComposition.GetElementVisual(control)` 取 visual，`visual.Compositor` 造动画，`StartAnimation("Offset", anim)` 显式播放；或设 `ImplicitAnimations` 让属性变化自动补间（`this.FinalValue` 表达式关键帧）。来源同上——A 级。
- 官方明确：样式驱动场景用关键帧/Transitions 更简单，不要为了「渲染线程」而用 Composition。来源同上——A 级。

**落地判断**：Composition 适合「列表项重排补间」「卡片进入的位移+淡入」，但只有 Offset/Opacity/Size，**做不了圆角、颜色、切角形状的动画**；这三类仍靠 Transitions（Brush/CornerRadius）或自绘。

### 2.6 共享元素转场：没有内置，需要自定义

- `TransitioningContentControl` 只接受 `IPageTransition`；官方列出的内置实现只有 `CrossFade`、`PageSlide`、`CompositePageTransition`；自定义路径是「实现 `IPageTransition`」，官方示例做的是缩放旧视图再展开新视图，**不是共享元素**。来源：<https://docs.avaloniaui.net/docs/graphics-animation/page-transitions>（访问 2026-11-02）——A 级。
- 交叉核对：`release/11.3` 分支源码树中不存在名为 `SharedElementTransition` 的文件（按 `WindowDecoration` 与动画类型检索均无命中），`Avalonia.Animation` 文档站也只有 page transitions / control transitions / composition animations 三篇。来源：<https://github.com/AvaloniaUI/Avalonia/tree/release/11.3>（访问 2026-11-02）——A 级。**该结论以「未发现」为限，不等于绝对不存在。**
- **结论**：共享元素转场（卡片 → 详情页、图标从 A 点飞到 B 点）在 11.3 **必须自己写**。可行路径两条：
  1. 用 `IPageTransition` + 手动计算起止 `Bounds`，在新旧视觉之间插值一个临时 `Border`（自绘 overlay）；
  2. 用 Composition 的 `Offset`/`Size` 做「伪共享元素」，但只能动位置/大小/透明度，形状与内容需提前克隆。
- `[推断]` 对 Blockwright 而言，共享元素转场是锦上添花，首版不应投入；CrossFade + 卡片自身 `RenderTransform` 位移已足够表达「进入详情」。

### 2.7 性能注意事项（官方口径）

| 注意点 | 官方说法 | 来源 |
| --- | --- | --- |
| 虚拟化 | `ListBox` 默认虚拟化；父容器给无限高度（如 `StackPanel`）会关闭虚拟化，必须用 `Grid RowDefinitions="*"` 或 `DockPanel` 约束高度 | <https://docs.avaloniaui.net/docs/app-development/performance>（访问 2026-11-02） |
| 变高列表 | `VirtualizingStackPanel` 按固定高度估算滚动范围，变高项会造成滚动条跳动；官方建议统一 `Height`/`MinHeight`，或扁平化层级 | 同上 |
| 缓冲 | `VirtualizingStackPanel.BufferFactor` 默认 0；设 1 会多保留一屏高度的项，滚动更顺但更吃内存 | 同上 |
| 位图缓存 | 复杂但不常变的视觉用 `BitmapCache`，`RenderAtScale` 控制清晰度；文字密集内容建议 `SnapsToDevicePixels=True` + `EnableClearType=True` | 同上 |
| 阴影 | 每个 `BoxShadow` 增加一次独立渲染 pass，尽量少用 | 同上 |
| 命中测试 | 透明元素也参与 hit test，不需要交互的覆盖层设 `IsHitTestVisible="False"` | 同上 |
| GPU 纹理缓存 | Skia 默认缓存约 28 MB，超出后大图每帧重新上传，导致卡顿；大图/图集场景需调 `SkiaOptions` | 同上 |
| 位图缩放 | Avalonia 11 默认 `LowQuality`；缩小的图标/截图应显式设 `RenderOptions.BitmapInterpolationMode="HighQuality"` | <https://docs.avaloniaui.net/docs/graphics-animation/image-interpolation>（访问 2026-11-02） |
| UI 线程 | 关键帧/Transitions 在 UI 线程；Composition 在渲染线程。任何回到 UI 线程的 `await` 续体都可能卡动画 | 综合上述文档 `[推断]` |

`[推断]` 对本项目最现实的三条：①实例/版本列表必须 `ListBox`/`ItemsControl` + 虚拟化，不要 `StackPanel` 包一切；②`BitmapCache` 只给「静态大背景/复杂卡片」，不要给正在做 Transitions 的元素（缓存每帧失效）；③动画只碰 `Opacity` + `RenderTransform`，不碰 `Width/Height/Margin`。

### 2.8 11.3 可用性核对（源码级）

| API | 11.3 分支是否存在 | 证据 |
| --- | --- | --- |
| `Window.ExtendClientAreaToDecorationsHint` | 是 | `Window.cs`（AvaloniaUI/Avalonia，release/11.3）定义 `StyledProperty<bool>` |
| `Window.ExtendClientAreaTitleBarHeightHint` | 是 | `WindowDecorationsPage.axaml.cs`（AvaloniaUI/Avalonia，release/11.3）赋值 |
| `ExtendClientAreaChromeHints`（`NoChrome`/`SystemChrome`/`PreferSystemChrome`/`OSXThickTitleBar`） | 是 | 同上；枚举 `ExtendClientAreaChromeHints.cs`（AvaloniaUI/Avalonia） |
| `Window.OffScreenMargin` / `WindowDecorationMargin` | 是 | 同上，测试页用它们调整 `Padding` 与内容 `Margin` |
| `WindowDecorationProperties.ElementRole` | **未确认** | 官方最新文档有示例，但 `release/11.3` 分支源码树未见该类型文件；需在 11.3.22 程序集反射确认 |

来源：<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Controls/Window.cs>、<https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/samples/IntegrationTestApp/Pages/WindowDecorationsPage.axaml.cs>（访问 2026-11-02）。

---

## 3. 无边框窗口 + 自定义标题栏

### 3.1 Avalonia 侧的两条路

**路线 A（11.3 确定可用）：`ExtendClientAreaToDecorationsHint` + chrome hints**

- `Window.ExtendClientAreaToDecorationsHint = true` 把客户区扩展到装饰区；配合 `ExtendClientAreaTitleBarHeightHint` 指定标题栏高度，`ExtendClientAreaChromeHints` 选择 `NoChrome` / `SystemChrome` / `PreferSystemChrome` / `OSXThickTitleBar`。来源：11.3 源码测试页（同上）——A 级。
- 扩展后必须用 `OffScreenMargin`（系统按钮让出的区域）设置窗口 `Padding`，用 `WindowDecorationMargin` 设置内容 `Margin`，否则内容会被系统 caption 按钮压住。来源同上——A 级。
- 拖拽需自己实现：在标题栏区域处理 `PointerPressed` 并调用 `BeginMoveDrag`；双击最大化需自己判断 `ClickCount == 2` 并切换 `WindowState`。`[推断]`（源码未直接给出示例，属常规做法）。

**路线 B（官方最新文档，版本待核）：`WindowDecorationProperties.ElementRole="TitleBar"`**

- 官方文档给出：`ExtendClientAreaToDecorationsHint="True"` + `WindowDecorations="None"`，给标题栏元素标 `WindowDecorationProperties.ElementRole="TitleBar"`；标了 `TitleBar` 的区域**支持原生拖拽与双击最大化**，区域内交互控件（按钮、输入框）**正常接收输入**。来源：<https://docs.avaloniaui.net/docs/app-development/window-management>（访问 2026-11-02）——A 级（文档），但 11.3.22 是否含此 API 未确认（见 §2.8）。
- 平台差异：`WindowDecorations.None` 三平台支持；`ExtendClientAreaToDecorationsHint` 在 Windows/macOS 支持、Linux **有限支持**。来源同上——A 级。

**落地建议**：先按路线 B 写（代码最短、原生行为最全），在 11.3.22 程序集里反射 `WindowDecorationProperties`；不存在则回退路线 A，并把「标题栏高度 + OffScreenMargin」抽成一个 `ChromeMetrics` 服务，避免每个窗口手算。

### 3.2 Microsoft 对自定义标题栏的硬性要求（A 级）

来源：<https://learn.microsoft.com/en-us/windows/apps/develop/title-bar>（访问 2026-11-02，页面标注 Last updated 2026-10-13）。

1. **标题栏由六部分组成**：标题栏矩形、标题文本、系统图标、系统菜单（点图标或右键标题栏）、caption 按钮（最小化/最大化-还原/关闭）。
2. **caption 按钮区域归系统所有**：可以画背景，但**不要放任何期望用户交互的 UI**，该区域输入由系统处理。系统按钮的位置与尺寸由 `LeftInset`/`RightInset`/`Height` 告知。
3. **交互内容必须声明为透传区**：在 WinUI 里要用 `InputNonClientPointerSource.SetRegionRects(Passthrough, rects)` 把搜索框/头像等区域标出来，否则点击会被标题栏吞掉；窗口尺寸变化时要重算。
4. **拖拽区应沿顶边、与视觉标题栏重合**：官方 Do 条目明确「定义沿应用画布顶边的拖拽区，匹配系统标题栏位置，便于用户发现」；把拖拽区放到别处会让用户找不到。
5. **高度**：含交互内容时示例用 **48px** 高，并设 `PreferredHeightOption = Tall` 让 caption 按钮同高；无交互内容时 32px 即可。
6. **高 DPI**：`AppWindow` 用物理像素；`LeftInset`/`RightInset` 及区域矩形都要除以 `RasterizationScale`（WPF 用 `DpiChanged` 的 `NewDpi`）。这是自定义标题栏在 150% 缩放下错位的头号原因。
7. **激活态**：窗口失活时必须改变标题栏文字/图标/按钮颜色，至少不能与激活态完全一样。
8. **RTL**：左右都要留 padding 列，保证 caption 按钮在 RTL 下也在正确一侧。

`[推断]` 这 8 条里，2/4/6/7 是 Avalonia 路线 A 必须自己补的；路线 B 的 `ElementRole` 若生效，2/4 由框架处理，6/7 仍需自己核对。

### 3.3 可访问性（Avalonia 官方 + Microsoft 规范）

**Avalonia（A 级）** 来源：<https://docs.avaloniaui.net/docs/app-development/accessibility>（访问 2026-11-02）。

- 可访问性走 `AutomationPeer`：Windows 用 UI Automation、macOS 用 NSAccessibility、Linux 用 AT-SPI2，三平台均为 Full support。
- 必用的附加属性：`AutomationProperties.Name`（图标按钮必须显式给）、`HelpText`、`LabeledBy`、`AutomationId`（稳定、不随语言变）、`AcceleratorKey`/`AccessKey`、`ControlTypeOverride`（自绘控件伪装成标准类型）、`LandmarkType`（`Navigation`/`Main`/`Search` 等，便于读屏跳转）、`HeadingLevel`、`LiveSetting`（`Polite`/`Assertive`，用于「正在下载 45%」这类动态状态）、`ItemStatus`/`ItemType`、`AccessibilityView`（装饰元素设 `Raw` 移出控件树）。
- 键盘：所有可交互控件必须 Tab 可达，`TabIndex` 控制顺序；`HotKey`/`KeyBinding` 提供快捷键；自定义模板要保证 `:focus-visible` 有可见 `FocusAdorner`。
- 自定义控件必须重写 `OnCreateAutomationPeer`，至少覆盖 `GetAutomationControlTypeCore` 与 `GetNameCore`。
- 官方检查清单：焦点可见、图标有名称、表单有标签、动态内容用 `LiveSetting`、**颜色不是唯一信息载体**、正文对比度 ≥ 4.5:1（大字 ≥ 3:1）、装饰元素移出自动化树。

**对标题栏的具体含义**：
- 三个窗口按钮（最小化/最大化/关闭）必须是真实 `Button` 且带 `AutomationProperties.Name`（「最小化」「最大化」「关闭」），不能是 `Border` + `PointerPressed`。
- 标题栏区域标 `LandmarkType="Banner"`，主内容区标 `Main`，侧栏标 `Navigation`。
- 若标题栏里有搜索框，`AutomationProperties.Name="搜索"` + `AccessKey`，且 Tab 顺序在窗口按钮之前（符合阅读顺序）。
- 高 DPI 下图标用矢量（`Path`/`StreamGeometry`），不要位图放大；`RenderOptions.BitmapInterpolationMode="HighQuality"` 只救急。

---

## 4. Minecraft 视觉基因 → 现代桌面 UI

### 4.1 能从 Minecraft 提取什么（结构性母题）

> 说明：Mojang 未公开发布可引用的 UI 品牌规范（本次未找到官方 style guide），因此本节是**设计推演（I）**，不是对官方规范的引用。可核验的只有 Minecraft 作为方块沙盒游戏的公开事实（方块、16×16 贴图、3×3 工作台合成网格）。

| Minecraft 母题 | 可转译的现代 UI 手法 | 不要做什么 |
| --- | --- | --- |
| 方块是 16×16 的逻辑单元，世界由整数网格构成 | 4px/8px 基础网格；卡片与列表行按网格倍数取尺寸；列对齐形成可见「key lines」 | 不要把 16px 像素格直接放大成 UI 栅格 |
| 原版方块贴图是哑光的，靠色块与噪点区分，几乎没有高光 | 哑光表面：纯色/微噪点底 + 1px 半透明描边；层级靠明度差而不是阴影 | 不要玻璃拟态、不要大面积渐变、不要内外斜面 |
| 红石/青金/铜/绿宝石各有用途 | 信号色语义化：只用于主行动、选中态、进度、错误；一屏不超过两种强调色 | 不要用草绿/钻石青铺底 |
| 工作台是 3×3 固定槽位 | 卡片内固定分区：图标区 / 标题区 / 元信息区 / 动作区，列宽固定 | 不要自由漂浮的卡片布局 |

### 4.2 明确避免

- 像素画图标、8-bit/点阵字体、`RenderOptions.EdgeMode="Aliased"` 式的硬放大（官方文档明确 `Aliased` 是给像素画用的，见 <https://docs.avaloniaui.net/docs/graphics-animation/image-interpolation>，访问 2026-11-02）。
- 原版 Minecraft GUI 的斜面/凹槽边框、材质贴图铺底、噪点叠加。
- 大面积高饱和色块。

### 4.3 三个「几何秩序 + 现代排版」的非游戏案例

**案例 1：IBM Carbon 2x Grid（几何秩序的范式）**

- 官方定义：2x Grid 是「排版、布局、图标与插画的几何基础」；基本单位是 **8px 的 mini unit**，列宽、行高、盒尺寸、margin、padding 都是 mini unit 的倍数。来源：<https://carbondesignsystem.com/building-blocks/foundations/2x-grid/overview>（访问 2026-11-02）——A 级。
- 核心是「除以二或乘以二形成视觉节奏」：流式网格靠除法，固定网格靠乘法；断点边界上两者对齐。来源同上——A 级。
- 官方强调 **key lines**：无论网格怎么建，整体布局要有可见的垂直/水平对齐线，「眼睛能跟着内容走，视觉和谐感增强」。来源同上——A 级。
- 固定尺寸阶：8 / 16 / 24 / 32 / 48 / 64 / 80 px（1x–10x mini unit）；盒尺寸尽量取 1:1、2:1、2:3、3:2、4:3、16:9 比例。来源同上——A 级。
- **可迁移结论**：Blockwright 的「4px 网格」可以升级为「4px 子网格 + 8px 主网格」：文字与图标对 4px，容器与间距对 8px；列表行的 key line 必须贯穿表头与行。

**案例 2：Vercel Geist（网格 + 等宽数字 + 现代排版）**

- Geist 官方把 Grid 描述为「a huge part of the new Vercel aesthetic」，并提供一个带可见 guide line 的 `Grid` 组件；最佳实践明确：Grid 用于「rule lines 与 cell borders 本身就是设计」的二维布局，普通卡片列表用普通 grid 即可，且**不要嵌套超过一层**（guide 重叠会变成视觉噪音）。来源：<https://vercel.com/geist/grid>（访问 2026-11-02）——A 级。
- 可访问性：guide 是装饰，应 `aria-hidden`；cell 可点击时每个 cell 要有独立 focus ring，tab 顺序与阅读顺序一致；guide 对比度在明暗两主题都要 ≥ 3:1。来源同上——A 级。
- 排版：Geist 的字号阶从 Heading 72 到 14、Button 16/14/12、Label 20–12；**Label 13 明确使用 Tabular 数字**（「用于数字，保证间距一致」），Label 12/13/14 各有 Mono 变体，用于与相邻字号配对。来源：<https://vercel.com/geist/typography>（访问 2026-11-02）——A 级。
- **可迁移结论**：Blockwright 的「等宽数字」不是风格偏好，而是有官方先例的排版决策：版本号、实例大小、下载进度、时间戳统一用 tabular/mono 数字，正文用无衬线；这同时解决列表纵向对齐。

**案例 3：Microsoft Fluent 2（桌面端现代语言的参照）**

- Fluent 2 是微软跨 Web/iOS/Android/Windows 的设计系统，官方提供组件、设计工具与可访问性资源（如 A11y Focus Order、Color Contrast Checker 插件）。来源：<https://fluent2.microsoft.design/>（访问 2026-11-02）——A 级。
- **仅作参考**：Fluent 2 的圆角、材质与层次偏柔和，与 Blockwright 的硬边/切角语言相反；可借用的是它的**桌面窗口习惯**（标题栏高度、caption 按钮位置、焦点顺序）与可访问性工具链，而不是它的视觉表皮。

### 4.4 转译规则（给方向卡直接用）

| 层 | 规则 | Avalonia 实现 |
| --- | --- | --- |
| 网格 | 4px 子网格 / 8px 主网格；容器尺寸取 8 的倍数 | `Grid` + 统一 `Thickness` 令牌；行高 48/56/64 |
| 表面 | 哑光纯色 + 1px 半透明描边；不用阴影表达层级 | `Border` + `BorderThickness="1"`；少用 `BoxShadow` |
| 切角 | 只用于一级容器与主按钮；列表行用直角 | 自绘 `StreamGeometry` 或 `Path`；不要每行一个 |
| 信号色 | 主行动 1 种 + 状态 1 种；其余中性 | `DynamicResource` 令牌；禁止页面内颜色字面量（仓库规范 §7） |
| 数字 | 版本号/大小/进度/时间用 tabular 或 mono | `FontFamily` 指定等宽；`TextBlock` 右对齐 |
| 图标 | 线性 1.5–2px 描边、24px 网格 | `PathIcon`/`StreamGeometry`，矢量 |
| 动效 | 120–220ms；只动 Opacity/RenderTransform | `Transitions` + `CrossFade` |

---

## 5. 两栏汇总

> 下表每条「依据」指回正文对应章节，其来源 URL 与访问日期（2026-11-02）见该节及 §8。

### 5.1 可直接用于 Avalonia 本项目

| 项 | 做法 | 依据 |
| --- | --- | --- |
| 页面转场 | `TransitioningContentControl` + `CrossFade` 180–220ms；高度差异大时不用 PageSlide | §2.1 |
| 控件反馈 | `Transitions` + `DoubleTransition`（Opacity）/ `TransformOperationsTransition`（RenderTransform） | §2.2 |
| 悬停/按压位移 | CSS 风格 `RenderTransform="translate(0px,-2px)"`，不要用 `TranslateTransform` 对象 | §2.2 |
| 列表性能 | `ListBox` + 约束高度 + 固定行高；必要时 `BufferFactor` | §2.7 |
| 静态复杂背景 | `BitmapCache` + `SnapsToDevicePixels` + `EnableClearType` | §2.7 |
| 自定义标题栏 | 优先 `WindowDecorationProperties.ElementRole="TitleBar"`；不可用则 `ExtendClientAreaToDecorationsHint` + `ExtendClientAreaChromeHints` | §3.1、§2.8 |
| 标题栏尺寸 | 含交互内容 48px，纯标题 32px；预留 `OffScreenMargin` | §3.2 |
| 图标缩放 | `RenderOptions.BitmapInterpolationMode="HighQuality"`（仅图标/截图，不全局） | §2.7 |
| 可访问性 | 图标按钮 `AutomationProperties.Name`；`LandmarkType`；动态状态 `LiveSetting`；焦点可见 | §3.3 |
| 网格 | 4px 子网格 / 8px 主网格；key line 贯穿表头与行 | §4.3 |

### 5.2 仅作参考

| 项 | 为什么只作参考 |
| --- | --- |
| Modrinth 的 Play 页两段式（Jump in + Library） | 信息架构可借鉴，但其大圆角、彩色图标、卡片阴影与 Blockwright 的哑光/切角语言冲突 |
| Prism 的实例内标签页 | 适合「一个实例很多配置」；Nebula 的版本页若以列表为主，标签页会稀释首屏 |
| ATLauncher 的「建包/逛包」双一级标签 | 适合内容平台型启动器；Nebula 若不做整合包市场，不需要 |
| Composition 动画 | 只有 Offset/Opacity/Size，表达力有限，首版不必引入 |
| 共享元素转场 | 无内置，自绘成本高，首版不做 |
| GDLauncher 的云分享实例码 | 需要后端，超出本次 UI 范围 |
| Fluent 2 的圆角与材质 | 与硬边/哑光方向相反，只借窗口习惯与可访问性工具 |
| IBM Carbon 的 16 列断点体系 | 桌面单窗口用不上这么多断点，只借 8px mini unit 与 key line 原则 |

---

## 6. 对 Blockwright 方向的三条具体建议

> 三条建议的「为什么」均引自 §1.2、§2.6、§4.3，来源 URL 与访问日期（2026-11-02）见对应章节。

### 建议 1（能做）：把「继续游戏」做成首屏唯一的高对比条带

- **做什么**：实例列表页顶部加一条 5 槽（窄屏 3 槽）的横向「继续」带，卡片高度固定 96px，左侧 48px 方形图标（矢量或 `HighQuality` 缩放），右侧两行：实例名 + 版本/加载器（等宽数字）；整卡是一个 `Button`，hover 只做 `Opacity` 0.9→1 + `translate(0,-2px)`。
- **为什么**：Modrinth 官方把 Jump in 定为 5 槽并让新建实例进入，目标是「让如何启动更清楚」；Prism 的纯列表模型对新用户不友好。
- **成本**：纯 XAML + 一个 `ItemsControl`，无新依赖。
- **验收**：1240×800 下首屏可见「继续」带 + 至少 2 行实例；1024×660 下「继续」带可横向滚动且不挤压列表。

### 建议 2（不能做 / 首版不做）：共享元素转场与「方块飞入」式过场

- **不能做什么**：不要承诺「点击实例卡，卡片展开成详情页」这类共享元素动画。Avalonia 11.3 没有内置 `SharedElementTransition`，`IPageTransition` 只能拿到 from/to 两个 Visual，自绘 overlay 要处理裁剪、滚动容器坐标、DPI 与取消，工作量远超一个方向卡样张。
- **替代**：CrossFade 180ms + 详情页顶部保留同一张图标（静态），视觉上已足够「连续」。
- **风险**：若方向卡画了共享元素动效、实现阶段降级，评审会判定方向不可落地。

### 建议 3（风险）：4px 网格 + 切角 + 哑光表面，容易在 1024×660 下变成「一屏全是边框」

- **风险**：切角需要自绘 `Geometry`/`Path`，每个卡片一个 `Path` 会增加渲染 pass；再叠加 1px 描边与 4px 对齐，低分辨率下会出现「边框比内容显眼」的密度问题。
- **缓解**：①切角只用于一级容器（侧栏选中态、主行动按钮），列表行用直角 + 左侧 2px 信号色条；②描边用 `BorderThickness="1"` + 半透明画刷，不要用 `BoxShadow` 模拟；③1024×660 下行高从 56 降到 48，字号不变。
- **验证**：方向卡样张必须同时给 1240×800 与 1024×660 两张，否则无法判断密度是否撑住。

---

## 7. 无法验证 / 待补清单

| 项 | 状态 | 说明 |
| --- | --- | --- |
| CurseForge 桌面应用的首页与安装流程 | **未取得一手 UI 证据** | 官方下载页返回 Cloudflare 安全验证；官方 API 站只描述内容模型，不描述客户端 |
| Steam 库的首页模块与排序 | **未取得一手 UI 证据** | `store.steampowered.com/news/app/593110` 与 `oldnews/191068` 均抓取超时/失败 |
| Xbox 应用首页卡片模型 | **未取得一手 UI 证据** | `xbox.com` 与 `apps.microsoft.com` 页面为 JS 渲染，正文为空 |
| `WindowDecorationProperties.ElementRole` 是否在 11.3.22 | **未确认** | 官方文档有示例；`release/11.3` 分支源码树未见该类型文件 |
| 共享元素转场在 11.3 的类型清单 | **未发现**（非绝对不存在） | 文档站无此页；11.3 源码树无同名文件 |
| Minecraft 官方 UI/品牌规范 | **不存在可引用文本** | 未找到 Mojang 公开 style guide；§4.1 已标为设计推演 |
| 标题栏可访问性的 Windows 专用细节（Narrator 对 caption 的朗读策略） | 未查 | 需实测；Avalonia 侧只保证 AutomationPeer 暴露 |

---

## 8. 参考清单（全部 2026-11-02 访问）

### Avalonia 官方（A 级）

1. Setting page transitions — <https://docs.avaloniaui.net/docs/graphics-animation/page-transitions>
2. TransitioningContentControl — <https://docs.avaloniaui.net/controls/data-display/transitioningcontentcontrol>
3. Setting control transitions — <https://docs.avaloniaui.net/docs/graphics-animation/control-transitions>
4. Render transforms vs layout transforms — <https://docs.avaloniaui.net/docs/graphics-animation/render-vs-layout-transforms>
5. Using keyframe animations — <https://docs.avaloniaui.net/docs/graphics-animation/keyframe-animations>
6. Composition animations — <https://docs.avaloniaui.net/docs/graphics-animation/composition-animations>
7. Image interpolation — <https://docs.avaloniaui.net/docs/graphics-animation/image-interpolation>
8. Performance optimization — <https://docs.avaloniaui.net/docs/app-development/performance>
9. Window management（自定义标题栏）— <https://docs.avaloniaui.net/docs/app-development/window-management>
10. Accessibility — <https://docs.avaloniaui.net/docs/app-development/accessibility>
11. 11.3 源码：Window.cs — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/src/Avalonia.Controls/Window.cs>
12. 11.3 源码：WindowDecorationsPage.axaml.cs — <https://github.com/AvaloniaUI/Avalonia/blob/release/11.3/samples/IntegrationTestApp/Pages/WindowDecorationsPage.axaml.cs>
13. 11.3 源码树 — <https://github.com/AvaloniaUI/Avalonia/tree/release/11.3>

### Microsoft 官方（A 级）

14. Title bar customization — <https://learn.microsoft.com/en-us/windows/apps/develop/title-bar>
15. Fluent 2 Design System — <https://fluent2.microsoft.design/>

### 同类产品

16. Prism Launcher — Creating an Instance — <https://prismlauncher.org/wiki/getting-started/create-instance/>（A）
17. Prism Launcher — Version 页 — <https://prismlauncher.org/wiki/help-pages/instance-version/>（A）
18. Prism Launcher 源码 — 实例页目录 — <https://github.com/PrismLauncher/PrismLauncher/tree/develop/launcher/ui/pages/instance>（A）
19. Prism Launcher 源码 — 实例视图 — <https://github.com/PrismLauncher/PrismLauncher/tree/develop/launcher/ui/instanceview>（A）
20. Modrinth 官方博客 — More ways to personalize your library（2026-08-17）— <https://github.com/modrinth/code/blob/main/packages/blog/articles/app-personalization.md>（B）
21. Modrinth 官方博客 — Introducing Modrinth App Beta（2023-08-05）— <https://github.com/modrinth/code/blob/main/packages/blog/articles/modrinth-app-beta.md>（B）
22. GDLauncher 官网 — <https://gdlauncher.com/>（B）
23. ATLauncher 源码 — 标签页目录 — <https://github.com/ATLauncher/ATLauncher/tree/master/src/main/java/com/atlauncher/gui/tabs>（A）
24. CurseForge 开发者文档（仅内容模型）— <https://docs.curseforge.com/>（A，但不覆盖客户端 UI）

### 设计系统案例（A 级）

25. IBM Carbon — 2x Grid — <https://carbondesignsystem.com/building-blocks/foundations/2x-grid/overview>
26. Vercel Geist — Grid — <https://vercel.com/geist/grid>
27. Vercel Geist — Typography — <https://vercel.com/geist/typography>

### 本项目

28. [CONVENTIONS.md](../../CONVENTIONS.md) §1 技术栈、§7 设计语言（仓库内）

### 已尝试但未取得正文的入口（供下一轮补证）

29. CurseForge 应用下载页 — <https://www.curseforge.com/download/app>（Cloudflare 安全验证）
30. Steam 官方新闻（客户端更新）— <https://store.steampowered.com/oldnews/191068>、<https://store.steampowered.com/news/app/593110>（抓取超时）
31. Xbox PC 应用 — <https://www.xbox.com/en-US/apps/xbox-app-on-pc>、<https://apps.microsoft.com/detail/9MV0B5HZVK9Z>（JS 渲染，正文为空）
