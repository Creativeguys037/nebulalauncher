# 字体资产来源、许可与移除路径

本目录三份 `.ttf` 是**界面字形资产**，不是代码。它们随 `AvaloniaResource Include="Assets\**"`
打进程序集，通过 `Themes/Tokens.axaml` 的 `Font.*` 五个令牌取用（`Font.UI` / `Font.UI.Bold` /
`Font.CJK` / `Font.Numeric` / `Font.Mono`）。

## 一、文件与来源

| 文件 | 族名（family） | 来源 | 用途 |
|---|---|---|---|
| `AntoYuanHei.ttf` | `AFYuanheiCS` | 安托圆黑，用户从**爱给网**（aigei.com）获取，原文件名 `安托圆黑_爱给网_aigei_com.ttf` | 中文全部字形；`Font.CJK` 与其它令牌的 CJK 回退 |
| `Mojang-Regular.ttf` | `Mojang` | Minecraft 官方界面字体（用户提供的本地副本） | 西文与数字正文；`Font.Numeric` |
| `Mojang-Bold.ttf` | `Mojang Bold`（**独立族名**，`usWeightClass` 仍是 400） | 同上 | 需要真粗的西文/混合标题；`Font.UI.Bold` |

重命名只去掉中文与站点后缀，**没有修改字体文件本身**（字形、字重、`name` 表原样保留）。

## 二、许可口径（重要）

- 本仓库是 **public** 仓库。三份字体**不适用**本项目的 MIT 许可，**不得**因为「放在我们仓库里」
  就被当作 MIT 内容再分发、再授权或用于其它项目。这与 `CONVENTIONS.md` §9 的第三方数据口径一致。
- **安托圆黑**：版权归原作者/字体厂商所有。爱给网提供的是下载渠道，不等于授权。当前使用范围是
  「本项目界面内嵌显示」；若要商用分发或再发布安装包，需按爱给网的授权页确认商用条款，必要时购买授权。
- **Mojang 字形**：Mojang / Microsoft 版权。仅用于本启动器（Minecraft 生态工具）的界面显示。
  **不得**单独抽取、打包或作为字体产品再分发。
- 体积口径：中文不做子集化（子集会漏字），3.4 MB 换「任何汉字都不缺」；发布体积由此增加约 3.4 MB。

## 三、移除 / 替换路径

需要移除或替换某个字体时，按顺序改三处即可，**不要**只删文件（会掉资源、界面回退到系统字体）：

1. 删掉本目录里的对应 `.ttf`。
2. 改 `src/NebulaLauncher.App/Themes/Tokens.axaml` 根级 `Font.*` 令牌：把对应 `#族名` 换成新族名或系统族名。
3. 同步 `src/NebulaLauncher.App/App.axaml` 里 `Font.UI` 的首帧兜底值，以及
   `src/NebulaLauncher.App/Program.cs` 的 `Program.DefaultFontFamily`（三者必须是同一条字体链）。

文本类说明（`docs/`、`CONVENTIONS.md` §7.4）如果引用了旧族名，一并更新。