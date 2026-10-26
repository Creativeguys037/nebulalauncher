# 皮肤 3D 低模自绘：可行性验证

> 一句话结论：**算法完全成立**。纯 CPU 手写 4×4 矩阵 + 透视投影 + 背面剔除 + 画家算法 + 逐面纹理填充，能正确渲染 Minecraft 人形低模（Steve 粗臂 / Alex 细臂 / 披风），512×512 单帧 **3.8~5.0 ms**，无需任何新 NuGet 包。

- 依据：两条**独立实现**互相印证 —— ①父 agent 的 Python 原型（`PIL` 软光栅，numpy 向量化后 9.9 ms/帧）；②子 agent 的 C# 原型（手写 `Mat4`/`Vec3`，软光栅，3.8 ms/帧）。
- 验证方式：**可运行代码 + 自检断言 + 方向标记探针**，不是纸面推演。
- 时间：2026-10-25。
- 适用：`NebulaLauncher` 皮肤页的 3D 预览卡与侧边栏常驻卡。

## 一、结论

| 问题 | 答案 | 证据 |
| --- | --- | --- |
| 投影数学是否成立 | **成立** | 8 项矩阵自检全 PASS |
| 六个方向 UV 是否对得上 | **6/6 全对** | 六面纯色方向探针（下详） |
| 有无面穿透 / 排序错乱 | **无** | 开/关背面剔除画面差异 **0 像素** |
| Alex 细手臂能否支持 | **能** | 臂宽 4→3、臂心 ±6→±5.5、外层 4.5→3.5 |
| 每帧耗时 | **3.8~5.0 ms**（512×512） | 100 次热态中位 5.04 ms，P95 5.64 ms |

## 二、性能拆解（这是最关键的数字）

子 agent 的 C# 原型实测：

```text
02-steve-front.png   中位 5.47ms  faces=72 backculled=50 drawn=22
  投影 0.05ms + 排序 0.00ms + 光栅 2.66ms + 打包 2.48ms
core(投影+排序+着色) 中位 3.80ms  P95 4.15ms
```

**投影 + 排序合计仅 0.06 ms**，几乎所有成本在逐像素采样与 alpha 混合。

这意味着：`DrawingContext` 那条路若把光栅交给 Avalonia/Skia（`DrawImage` 自带采样），**可能比纯 CPU 软光栅更快**，因为省掉了自己逐像素写内存和 bitmap 打包。这是对本仓有利的结论。

## 三、方向标记探针（最强的正确性证据）

把六个面涂成六种纯色，逐一断言主色占比：

| 视角 | 期望 | 实测 |
| --- | --- | --- |
| yaw=0 | 正面红 | 99.8% |
| yaw=180 | 背面蓝 | 99.8% |
| yaw=90 | 左侧绿 | 99.2% |
| yaw=270 | 右侧黄 | 99.2% |
| 俯视 | 顶面品红 | 88.8% |
| 仰视 | 底面青 | 56.4% |

**6/6 全对。** 这种验证方式能抓住纯色皮肤下肉眼看不出 UV 左右反了的问题。

## 四、必须记住的坑（按致命程度排序）

1. **MVP 乘法顺序写反 = 静默全空图**。列向量约定下必须是 `proj * view * model`；写成 `model * view * proj` 会让所有面被近平面剔除，**不报错、只出空图**。
2. **屏幕空间三角形绕向符号反了**。NDC 的 y 向上、屏幕 y 向下，翻转后正面在屏幕上是**顺时针**，叉积为负。判错就丢光正面，同样是空图。
3. **重心坐标的 invArea 不能取绝对值**。面积与分子同号，取绝对值会把权重全体翻号。
4. **左右侧面 UV 容易反**。`skinview3d` 按**观察者看角色的左右**命名，`+X` 是角色**左手**。纯色皮肤下肉眼几乎看不出。
5. **bottom 面要翻转 180°**。MC 1.0.0 起官方实现是 `uvBottom = [b0,b1,b3,b2]`，不翻转会让鞋底纹路方向错。
6. **浮点 UV 渗边**。插值出的 u/v 必须钳到 `[uMin, uMax-1e-3]`，否则 `floor` 落到隔壁纹理格子，出 1px 错色。
7. **双层 overlay 尺寸不是简单 +1px**。头部外层 9×9×9（各边 +0.5），身体/四肢各边 +0.25（8.5×12.5×4.5），且各有独立 UV 区域。
8. **光照会破坏按绝对 RGB 的断言**。定向光把纯红压成 `0xD50000`，校验必须用归一化色相或相对亮度。
9. **Y 轴方向**。模型空间 y 向上、屏幕 y 向下，投影时要做 `1 - ndcY`。
10. **透视校正插值**。直接对 u/v 线性插值在 32px 小模型上看不太出来，但严格做法是 `u = Σ(wᵢ·uᵢ/wᵢ) / Σ(wᵢ/wᵢ)`。

## 五、移植到本仓的难度评估

**数学部分：低风险。** 纯 C#、零依赖，可直接搬 `Vec3`/`Mat4`/`LookAt`/`Perspective`/重心插值。投影+排序仅 0.06ms。

**绘制部分：中风险。** `Avalonia.DrawingContext` 没有「透视校正纹理映射」原语：

- `DrawImage` 只能画**轴对齐矩形**，不能贴斜四边形。
- 可行做法：自己算仿射，用 `PushTransform(Matrix)` + clip 画三角形。Avalonia 的 `Matrix` 是 2D 仿射，**能表达三角形到三角形的映射**（3 点定仿射）。每面 2 个三角 × 约 22~34 个可见面 ≈ 44~68 次 transform+clip+DrawImage。
- 单色 `StreamGeometry` 填充没有透视校正问题，但会丢掉皮肤细节，不可取。

**最大不确定性（必须实测）**：44~68 个 geometry 在 Avalonia 下的 tessellation 性能。子 agent 测的是 SkiaSharp 裸像素，**没测 Avalonia 的 `DrawingContext` 路径**。旋转动画 60fps 下是否掉帧，必须做单帧基准后再定。

## 六、证据文件

子 agent 原型产物在系统临时目录（沙箱限制，仓外）：

```text
C:\Users\MECHREUO\AppData\Local\Temp\dsh-GhtICi\skin3d-proto\
  out\00-skin-64x64.png          生成的 64×64 皮肤
  out\02-steve-front.png          Steve 正面
  out\03-steve-side-45.png        Steve 侧面 45°
  out\04-steve-back.png           Steve 背面
  out\05-steve-cape.png           Steve 含披风
  out\06-alex-front.png           Alex 正面（细手臂）
  out\07-alex-side-45.png         Alex 侧面
  out\08-alex-back.png            Alex 背面
  out\09-alex-cape.png            Alex 含披风
  out\10-compare-wide-vs-slim.png 粗/细手臂同视角对比
  out\report.txt                  计时与自检全文
  Math3D.cs / Model.cs / Renderer.cs / SkinTexture.cs / MathSelfCheck.cs
```

源码参考（子 agent 用来「抄」而非自造的权威源）：

- `https://unpkg.com/skinview3d@3.4.2/libs/model.js` —— `setUVs()` 是 UV 布局的事实标准
- `https://minecraft.wiki/w/Skin?action=raw` —— 内层强制不透明、外层 +1px/+0.25px、bottom 翻转 180°

**本机环境事实（重要）**：

- `D:\agent_workspace\.probe\` **不可写** —— DSH 沙箱 `workspace-write` 只允许会话工作区。
- `dotnet build` 在本沙箱**跑不了** —— MSBuild 的 `Csc` 任务需要命名管道收 stdout，被禁止（`MSB3883 / 拒绝访问`）。绕过办法：直接调 `csc.dll` 手工编译。这与 `CONVENTIONS.md` §8.2 记录的「`dotnet test` 不可用」是同一类沙箱限制。
