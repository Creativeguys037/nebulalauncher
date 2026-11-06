# Tokens 结构表（草案）

- 票：#70（父地图 #69）
- 规则：`Dark` 与 `Light` 两个 `ThemeDictionaries` **键名必须完全一致**；只加一边编译能过、切主题才在运行期掉资源。
- 增量策略：现有 45 个键全部保留，只新增 `Glass.*` / `Edge.*` / `Motion.Ease.*` / `FontSize.Display*`；旧值改值不改名。
- 旧键处置：`Accent.Secondary` 改为品牌红 `#B53131`（两套主题同值）；`Gradient.Aurora` 改为新方向的装饰渐变（浅 `#1668FF → #37D6FF`、深 `#E80018 → #B53131`），保留键名以免改引用它的页面；`Palette.*` 同步新色板（`Blue #1668FF`、新增 `Red #E80018`、`Brand #B53131`），`Palette.Violet*` 标注废弃但保留键。
- 值单位：颜色 `#AARRGGBB`；圆角 `CornerRadius`；字号与时长 `double`。

## 1. 表面

| 令牌 | Light | Dark | 用途与限制 |
|---|---|---|---|
| `Surface.Base` | `#E8F0FF` | `#0A0205` | 窗口底；浅色须与 `Gradient.Canvas` 一起用 |
| `Surface.Canvas` | `#F7FAFF` | `#0F0407` | 内容区底，比 Base 亮一层 |
| `Surface.Card` | `#B3FFFFFF` | `#0FE80018` | 玻璃卡片底；深色红 6% |
| `Surface.CardSolid` | `#FFFFFF` | `#1A0810` | 不透明卡片（表格、日志） |
| `Surface.Raised` | `#CCFFFFFF` | `#1A0810` | 浮起层、次级按钮 |
| `Surface.Hover` | `#F0F4FF` | `#1FE80018` | 悬停底 |
| `Surface.Pressed` | `#E2EAFC` | `#29E80018` | 按下底 |
| `Surface.Sunken` | `#E9EFFC` | `#140305` | 输入框、进度槽 |
| `Surface.Glass` | `#57FFFFFF` | `#14E80018` | 玻璃面板（侧栏、浮层） |
| `Surface.Sidebar` | `#EDF3FF` | `#12050A` | 左侧图标栏底 |

## 2. 玻璃（新增组）

| 令牌 | Light | Dark | 用途与限制 |
|---|---|---|---|
| `Glass.Tint` | `#57FFFFFF` | `#0FE80018` | 玻璃元素底色（白 34% / 红 6%） |
| `Glass.Edge` | `#DBFFFFFF` | `#2EB53131` | 1px 亮边；深色用品牌红 18% |
| `Glass.TopLight` | `#FFFFFF` | `#14FFFAFA` | `inset 0 2 0` 顶亮线（语法不带 px） |
| `Glass.InnerShadow` | `#47103078` | `#66000000` | `inset 0 -16px 30px -16px` 底内阴影 |
| `Glass.Shadow` | `#38103078` | `#99000000` | 外投影 `0 26px 60px` |

> 限制：Avalonia 11.3 **有** `BlurEffect`，但它只作用于元素自身的绘制结果，不读取背后的兄弟内容，所以玻璃不能做 CSS `backdrop-filter`。可行做法是同源背景副本 + `BlurEffect`（只挂在副本层）；`saturate` 无内建，只能靠叠加近似。`BoxShadow` 的 `inset` 语法**不带 px**（`inset 0 2 0 #fff`），带 px 会解析失败。

## 3. 描边

| 令牌 | Light | Dark | 用途 |
|---|---|---|---|
| `Stroke.Subtle` | `#140F1724` | `#14FFFAFA` | 分隔线、卡片边 |
| `Stroke.Default` | `#240F1724` | `#24FFFAFA` | 控件边界 |
| `Stroke.Strong` | `#3D0F1724` | `#3DFFFAFA` | 悬停、强调边界 |
| `Stroke.Focus` | `#1668FF` | `#E80018` | 键盘焦点 2px 环 |

## 4. 文字

| 令牌 | Light | Dark | 用途与限制 |
|---|---|---|---|
| `Text.Primary` | `#0A0D14` | `#FFFAFA` | 正文与标题 |
| `Text.Secondary` | `#39445A` | `#F0F0F0` | 次要正文 |
| `Text.Tertiary` | `#6B7891` | `#DBD6D2` | 说明、元信息 |
| `Text.Muted` | `#8A93A6` | `#80DBD6D2` | 只做装饰与禁用，不作正文 |
| `Text.OnAccent` | `#FFFFFF` | `#FFFAFA` | 压在 `Accent.Primary` 上 |
| `Text.OnBrand` | `#FFFAFA` | `#FFFAFA` | 压在 `Accent.Brand` 上 |

## 5. 强调

| 令牌 | Light | Dark | 用途与限制 |
|---|---|---|---|
| `Accent.Primary` | `#1668FF` | `#E80018` | 主行动、选中、进度；一屏只有一种 |
| `Accent.Brand` | `#B53131` | `#B53131` | 跨主题品牌形象色；装饰、渐变尾、次级强调 |
| `Accent.Soft` | `#1A1668FF` | `#1FE80018` | 选中底色（10–12% alpha） |
| `Accent.Glow` | `#401668FF` | `#40E80018` | 主行动辉光，只用于主按钮与当前项 |

## 6. 状态

| 令牌 | Light | Dark | 用途 |
|---|---|---|---|
| `Status.Success` | `#0B7A57` | `#34D399` | 成功、运行中 |
| `Status.Warning` | `#92400E` | `#FBBF24` | 警告、可更新 |
| `Status.Danger` | `#C42020` | `#B53131` | 删除、不可逆；**必须带图标或文案** |
| `Status.Info` | `#0E7490` | `#22D3EE` | 中性提示 |
| `Status.*.Soft` | 各 8–12% alpha | 各 12% alpha | 状态徽标底 |
| `Status.Danger.Edge` | `#4DC42020` | `#4DB53131` | 危险按钮描边 |

> 深色里 `Accent.Primary`（#E80018）与 `Status.Danger`（#B53131）是两支红：主行动永不用 Danger 色，删除永不用 Primary 色。

## 7. 渐变

| 令牌 | Light | Dark |
|---|---|---|
| `Gradient.Canvas` | `#E8F0FF → #C9DCFF → #A8C6FF → #8FB4FF`（152°） | `#0A0205 → #1A0810`（152°） |
| `Gradient.Accent` | `#1668FF → #0B3FCC` | `#E80018 → #B53131` |
| `Gradient.Progress` | `#1668FF → #37D6FF` | `#E80018 → #FF6B6B` |
| `Gradient.Sheen` | 白 0 → 7% → 0 | 白 0 → 8% → 0 |

## 8. 阴影

| 令牌 | Light | Dark | 用途 |
|---|---|---|---|
| `Shadow.Card` | `0 2 8 0 #140F1724` | `0 2 8 0 #66000000` | 卡片静置 |
| `Shadow.CardHover` | `0 10 24 -6 #1F0F1724` | `0 10 28 -6 #99000000` | 卡片悬停 |
| `Shadow.Overlay` | `0 26 60 -12 #38103078` | `0 26 60 -12 #B3000000` | Dialog、浮层 |
| `Shadow.Glow` | `0 0 24 0 #331668FF` | `0 0 24 0 #40E80018` | 主行动辉光 |
| `Shadow.Glass` | `inset 0 2 0 #FFFFFF, inset 0 -16 30 -16 #47103078, 0 26 60 -12 #38103078` | `inset 0 2 0 #14FFFAFA, inset 0 -16 30 -16 #66000000, 0 26 60 -12 #99000000` | 玻璃元素的组合阴影（`inset` 不带 px） |

## 9. 形状与字体（主题无关，字典根部）

| 组 | 键 | 值 |
|---|---|---|
| 圆角 | `Radius.Sm` / `Md` / `Lg` / `Xl` / `X2l` / `X3l` / `Pill` | 8 / 12 / 16 / 20 / 24 / 28 / 999 |
| 字号 | `FontSize.Caption` → `DisplayLg` | 11 / 12 / 13 / 15 / 17 / 20 / 26 / 29 / 38 |
| 字重 | `FontWeight.Normal` / `Medium` / `SemiBold` / `Bold` | 400 / 500 / 600 / 700 |
| 字体 | `Font.UI` | `Microsoft YaHei UI, Segoe UI, sans-serif` |
| 等宽 | `Font.Mono` | `Cascadia Code, Consolas, monospace` |
| 间距 | 刻度（`Thickness` 内联） | 4 / 8 / 12 / 16 / 20 / 24 / 32 / 40 |

## 10. 动效

| 令牌 | 值 | 用途 |
|---|---|---|
| `Motion.Instant` | 90ms | 按下、勾选 |
| `Motion.Fast` | 150ms | hover、图标变色 |
| `Motion.Normal` | 220ms | 状态切换、展开 |
| `Motion.Slow` | 320ms | 页面转场 |
| `Motion.Ease.Out` | `cubic-bezier(.22,.9,.24,1)` | 全部 UI 动效默认 |
| `Motion.Ease.InOut` | `cubic-bezier(.4,0,.2,1)` | 循环与装饰动画 |

> 实现：`Motion.cs` 暴露 `TimeSpan` 静态字段（`Motion.Fast` 等）与 `Easing` 实例，XAML 用 `{x:Static services:Motion.Fast}`；`Motion.Enabled=false` 时 `Motion.Dur()` 返回 `TimeSpan.Zero`。

## 11. 组件层

| 令牌 | Light | Dark | 用途 |
|---|---|---|---|
| `Scrollbar.Thumb` | `#33000000` | `#40FFFAFA` | 滚动条滑块 |
| `Window.CloseHover` | `#E81123` | `#B53131` | 关闭按钮悬停底 |
