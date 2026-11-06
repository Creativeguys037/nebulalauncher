# 液态玻璃与三键胶囊：实现方案对比

日期：2026-11-06 · 状态：**待确认**（确认后并入 `docs/UI-STRUCTURE.md`）
范围：窗口三键改为「玻璃胶囊」，主页停在搜索栏右侧，切页横移到右上角，回主页反向复位。

---

## 1. 需求拆成两件事

这两件事的技术风险完全不同，不要混在一起评估：

| # | 要什么 | 难在哪 | 依赖 |
|---|---|---|---|
| **A. 胶囊外观** | 真液态玻璃：边缘折射、色散、厚度感 | 需要「采样背后的画面并弯曲它」，不是调透明度 | 渲染层能力 |
| **B. 胶囊运动** | 横移与页面切换同步、回主页反向复位、有液态的形变感 | 位置计算 + 曲线 + 与 220ms 淡入对齐 | 纯动画，无技术风险 |

B 一定能做，差别只在曲线好不好看。**A 才是要选路线的地方。**

## 2. 四条路线（按「能不能在 Avalonia 里落地」筛）

| 路线 | 代表实现 | 做法 | 视觉真实度 | 落地成本 | Avalonia 可行性 |
|---|---|---|---|---|---|
| **A0 现有静态玻璃** | 本仓 `controls:GlassPanel` | 背景副本 + blur + 顶边高光 + 内阴影 | 无折射，只有模糊与高光 | 已有 | ✅ 已在用 |
| **A1 CSS/SVG 位移滤镜** | [hyalite](https://github.com/VII-Cae/hyalite--liquid-glass)（SDF lens map + `feDisplacementMap`）、[nordify/css-liquid-glass](https://github.com/nordify/css-liquid-glass) | 按元素尺寸算出透镜图，喂给 SVG 滤镜，`backdrop-filter: url(#…)` 让浏览器弯折背后画面 | **真折射**，边缘会「折叠」出液态漩涡 | 中：要写 lens map 计算 + 滤镜 | ❌ **浏览器专有**：`backdrop-filter: url()` 只有 Chromium 渲染，Avalonia 没有这个概念 |
| **A2 WebGL / WebGPU shader** | [rdev/liquid-glass-react](https://github.com/rdev/liquid-glass-react)（6.6k★）、[naughtyduk/liquidGL](https://github.com/naughtyduk/liquidGL)、[shuding/liquid-glass](https://github.com/shuding/liquid-glass) | 抓背景贴图 → 片元 shader 做折射、色散、弹性 | 最真（可做色散与旋涡） | 高：一整条渲染管线 | ⚠️ 控件级 WebGL 不可行；Avalonia 的 GL 后端不开放给单个控件用 |
| **A3 Skia RuntimeEffect** | [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass)（Compose Multiplatform，4.5k★） | 背景快照 → Skia `RuntimeShader`（SkSL）在 GPU 上采样与弯折 | **真折射**，可做色散与厚度 | 中高：写一次 shader，全 L2 面板复用 | ✅ **推荐**：Avalonia 底层就是 Skia，`ISkiaSharpApiLeaseFeature` 能拿到 `SKCanvas` 与 `SKRuntimeEffect` |

三条判定：

1. **A1 不能选。** hyalite 自己写得很清楚：`supported()` 只在「SVG backdrop filter 真的渲染」的地方为 true（Chromium）。我们的产品不是浏览器。它的价值是**告诉我「液态感」的物理参数该怎么调**（bevel 37 / thickness 59 / slope 2.7 / 超过 slope 1 会折叠出漩涡），这些数可以搬进 A3。
2. **A2 不能选。** rdev 的方案在 Safari/Firefox 连位移都看不见；换成 Avalonia 更没有载体。
3. **A0 不够。** 现在的 `GlassPanel` 是「背景副本 + 模糊」，透得出背景颜色，但边缘不会弯折，就是一块磨砂玻璃。要「液态」必须上 A3。

## 3. 推荐：A3，且复用现有三层结构

现有 `GlassPanel` 已经是「背景副本 → 模糊 → 高光」三层，A3 只是把中间那层的**模糊**换成**折射 shader**：

```text
现在：L1 背景副本 ──blur──> 白色高光层
A3  ：L1 背景副本 ──SKSL: 边缘位移 + 色散──> 白色高光层 + 亮边
```

这样做的三个好处：

- 背景副本机制（`GlassBackdrop` 整窗唯一、玻璃卡复制子树）不用动，`docs/UI-STRUCTURE.md` 的 L1/L2 分层原样成立。
- 胶囊、侧栏、页面主卡共用同一个 shader，只是 `bevel` / `thickness` / 圆角不同——参数化，不是三份实现。
- Compose 侧的 AndroidLiquidGlass 已经证明「Skia + 背景快照 + RuntimeShader」这条路在桌面级框架里跑得动。

风险与退路：

| 风险 | 退路 |
|---|---|
| `ISkiaSharpApiLeaseFeature` 在部分后端（软件渲染）拿不到 | 检测失败就退回 A0 静态玻璃（现有 `GlassPanel` 不动） |
| shader 每帧采样背景位图，移动时会闪 | 胶囊只在**切换页面的 260ms 内**重算，静止时用缓存位图 |
| SkSL 调试成本高 | 先做「只有边缘位移、没有色散」的最小版本，色散后加 |

## 4. 样板阶段用什么

样板只验证**观感与动效**，不验证 shader。所以样板用 A0 的加强版：

- 底色 `backdrop-filter: blur(20px) saturate(180%)`
- 边缘：1px 白亮边 + 内顶高光 + 内底阴影 + 外侧柔光（复用 `Shadow.Glass` 的思路）
- 假色散：边缘用两层错位的极淡蓝/白描边模拟
- 三键是**白色实块**，hover 时实块变蓝（关闭键变危险红），胶囊本身不参与 hover 变色

这样样板在 Chrome headless 里截出来的图，就是「A3 落地后应该长成什么样」的基线；真渲染时把模糊层换成 shader，外观不必重画。

## 5. 运动：曲线与时长（草案）

| 项 | 值 | 理由 |
|---|---|---|
| 胶囊横移 | **260ms**，`cubic-bezier(.22,.9,.24,1)` | 与页内已有的 `--ease` 同一条曲线；260ms 让眼睛跟得上，又不会拖 |
| 回主页复位 | **220ms**，同曲线 | 回退比前进轻一档，和 PageHost 的 220ms 淡入同步 |
| 页面内容淡入 | 220ms（已有） | 不动 |
| 胶囊移动时的形变 | 宽高比 1.00 → 1.06 → 1.00，跟随主曲线 | 液态感来自「先被拉长再回弹」，纯位移不像液体 |
| 三键实块 | 位移后 80ms 内完成 hover 色切换（`--ease`，120ms） | 不让颜色抢位移的注意力 |

**同步规则**（这条要进契约）：胶囊的横移**必须与页面切换同一帧开始**，不允许「页面先换、胶囊后到」或反之。实现上由同一个触发点同时驱动两个 transition，胶囊不独立监听导航事件。

## 6. 确认前需要你拍板的两件事

1. **主页的搜索栏放哪。** 现在实现里 TitleBar 只有「页面标题 + 副标题 + 三键」，主页没有搜索框（搜索在各页 Row1 工具条）。要让胶囊「停在搜索栏右侧」，主页就得在 TitleBar 里加一条搜索框——这会让主页 TitleBar 比其他页多一个元素。三个选项：
   - (a) 主页 TitleBar 加搜索框，其他页不加（胶囊位移 = 搜索框宽度 + 间距）
   - (b) 主页 TitleBar 不加，胶囊只做「贴右 → 稍微内缩」，不做大位移
   - (c) 恢复样板 `home-glass.html` 的 62 高 topbar（等于推翻 D1，全部页面画布要重算）
   样板暂时按 **(a)** 做，因为只有它能做出你要的「横移」幅度。
2. **色散要不要。** A3 能加，但会给 1024 档的小胶囊带来噪点。建议先不做，等真机看效果。

---

参考：
- [VII-Cae/hyalite--liquid-glass](https://github.com/VII-Cae/hyalite--liquid-glass) — SDF lens map + SVG 位移，参数与「折叠产生漩涡」的物理说明
- [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass) — Compose Multiplatform，Skia 系同路线
- [rdev/liquid-glass-react](https://github.com/rdev/liquid-glass-react) — WebGL 参数面（displacement / aberration / elasticity）
- [naughtyduk/liquidGL](https://github.com/naughtyduk/liquidGL) — WebGPU/WebGL 交互式实现