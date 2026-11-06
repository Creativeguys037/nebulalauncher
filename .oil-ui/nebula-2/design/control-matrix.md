# 控件矩阵（草案）

- 票：#70（父地图 #69）
- 覆盖：票面 10 类核心 + 4 个共享容器；Menu / ContextMenu / Slider / NumericUpDown / TreeView 标「按需定义」，不预建。
- 实现档：**A** = 只改属性；**B** = 改 `/template/` 内部件；**C** = 重写 `ControlTheme`（不用页面级 `Style` 侵入）。
- 焦点统一：2px `Stroke.Focus` 环 + 2px 外扩，键盘可见、鼠标点击不显示。
- 加载统一：`Classes="loading"` + `IsEnabled=false`，内容位换 14px `ProgressRing`，宽度锁死，不改变控件外框尺寸。

## 1. 核心控件

| 控件 | 变体 | 尺寸 | 圆角 | default | hover | focus | pressed | disabled | loading | 档 |
|---|---|---|---|---|---|---|---|---|---|---|
| Button | primary（`Accent.Primary` 实心）/ secondary（玻璃白底）/ ghost / icon / danger（`Status.Danger` + 图标） | 32 / 36 / 44 高；icon 36×36 | 16 | primary 蓝/红底白字 + `Shadow.Glow` | 上浮 1px、阴影扩大（150ms） | 2px 焦点环 | 底色压深一档、下沉 0 | 40% 不透明、无阴影、`Cursor=Arrow` | 文字换 `ProgressRing`、宽度锁死 | A |
| TextBox | 单行 / 多行 / 搜索（带图标） | 36 高（多行自适应，最小 72） | 12 | 白 62% 底、1px `Stroke.Default`、13px | 底色转纯白 | 2px 焦点环、描边转主色 | — | 40% 不透明 | 右侧 14px `ProgressRing` | B |
| ComboBox | 单选 / 可搜索 | 36 高 | 12 | 同 TextBox，右侧 16px 箭头 | 底色转纯白 | 2px 焦点环 | 展开时箭头翻转 180° | 40% 不透明 | — | C（下拉 Popup 玻璃层） |
| CheckBox | 单选 / 组 | 框 18×18，行高 28 | 4（框） | 1.5px `Stroke.Strong` 空框 | 框描边转主色 | 2px 焦点环 | 主色实心 + 白勾 | 40% 不透明 | — | B |
| ToggleSwitch | 开 / 关 | 44×24 轨，滑块 20 | 999 | 轨 `Stroke.Default`、白滑块 | 轨色加深 | 2px 焦点环 | — | 40% 不透明 | — | B |
| ListBoxItem | 普通 / 可选 / 带右元数据 | 行高 52（紧凑 44） | 12 | 透明底、`Text.Secondary` | 白 62% 底 + 描边 + 右移 2px | 2px 焦点环 | 主色 10% 底 | 40% 不透明 | 行内右侧 14px `ProgressRing` | B |
| ScrollBar | 纵向 / 横向 | 8 宽（hover 10） | 999 | 半透明胶囊滑块、无轨道底 | 加宽到 10、透明度提高 | — | 拖动时加深 | — | — | C |
| TabControl / TabItem | 同级视图（`TabControl`）/ 筛选分段（`ToggleButton` 组，不共用类） | 36 高 | 999 | 底槽 `Surface.Sunken`、未选文字 `Text.Secondary` | 文字转 `Text.Primary` | 2px 焦点环 | 滑块跟随，220ms | 40% 不透明 | — | C |
| ToolTip | 单行 / 多行 | 最大 280 宽 | 8 | 黑底（`#0A0D14`）白字、11px、10,6 内边距 | — | — | — | — | — | C |
| Dialog | 确认 / 表单 / 危险确认 | 最小 420 宽，最大 720 | 24 | 玻璃面板 + `Shadow.Overlay`、遮罩黑 40% | — | 首焦点落在主行动 | — | 主行动禁用时说明原因 | 主行动 loading | C（自建 Overlay） |

## 2. 共享容器

| 控件 | 变体 | 尺寸 | 圆角 | default | hover | focus | disabled | loading | 档 |
|---|---|---|---|---|---|---|---|---|---|
| Card / GlassPanel | `Border.card` / `Border.glass` / `Border.sunken` | padding 20（hero 28） | 20（hero 28，内层递减 12） | 玻璃层 + `Shadow.Glass` | 上浮 3px、阴影加深、描边转纯白 | 可聚焦卡片 2px 环 | 40% 不透明 | 骨架条 | B |
| ProgressBar | 确定 / 不确定 | 6 高（细 4） | 999 | 槽 `Surface.Sunken`、填充 `Gradient.Progress` | — | — | 槽色加深 | 不确定态流动条 | B |
| Badge / StatusPill | 中性 / 成功 / 警告 / 危险 | 高 20，padding 8,2 | 999 | `.Soft` 底 + 对应状态色文字、11px | — | — | — | — | A |
| 窗口三键 | 最小化 / 最大化 / 关闭 | 33×29（1024 下 29×25，图标 10px） | 8 | 透明底、灰蓝图标 | 白 8% 底；关闭转 `Window.CloseHover` + 白图标 | 2px 环 | — | — | A |

## 3. 状态语言约定

- **hover 用上浮，不用放大**：`RenderTransform="translate(0,-1px)"`（卡片 -3px），150ms `Motion.Ease.Out`。
- **pressed 用压深，不用缩放**：底色降一档，`Opacity 0.9`。
- **disabled 统一 40%**，`Cursor=Arrow`，必须说明原因的位置用 ToolTip。
- **loading 是状态，不是变体**：同一控件加 `loading` 类；主行动 loading 时文字换成 `ProgressRing`，宽度锁死避免布局跳动。
- **危险动作**：`Status.Danger` + 图标 + 二次确认；永远不与 `Button.primary` 同屏相邻。
- **颜色不是唯一载体**：状态必须同时有图标或文字；深色主题里尤其重要（红既主色又危险）。

## 4. 按需定义（不预建）

| 控件 | 触发条件 |
|---|---|
| Menu / ContextMenu | 出现右键菜单需求时，按 ToolTip 的深底白字语言补 |
| Slider | 音量/内存分配出现连续调节时补 |
| NumericUpDown | 设置页需要精确数值输入时补 |
| TreeView | 内容目录需要层级浏览时补 |

## 5. 未定义项

- `GlassPanel` 需要一个小型自定义控件：页面的背景副本（渐变 + 硬边色块）按卡片位置做同源偏移、裁到圆角内、在副本层挂 `BlurEffect`，文字与边框放在副本之上。归属控件层（#84），外壳（#85）提供可复制的 `GlassBackdrop`。
- `ExperimentalAcrylicBorder` 的 `Material` 已核实存在，可作窗口级可选项；它取的是窗口背景而不是卡片背后的控件内容，不能替代局部玻璃。
- ComboBox 下拉 Popup 的玻璃圆角需要 C 档重写，成本最高；若实施票判定成本过高，允许降级为纯色下拉（记为已知偏差）。
