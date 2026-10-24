# 发布产物检查（2026-10-23 第二十八轮更新）

目的：回答「`build.ps1` 出的东西拿到干净机器上能不能直接用」，而不是只看退出码。
所有验证都在 `.probe/` 下做，不碰仓库里的 `pub/`。

> **第二十八轮的变化**：§3 那个「未清理 obj 就会产出静默不启动的 exe」的坑已经由脚本自己堵住了
> ——`build.ps1` 现在在构建前做 XAML 中间产物过期检测、发布后跑两道产物关，失败以非 0 退出。
> 人工只需要跑一次 `-Publish` 并看一眼最后两行。细节见 §3.1。

## 1. `build.ps1` 实际行为

| 项 | 值 |
|---|---|
| 形态 | `minimal`（默认，框架依赖 + 单文件，原生库外置）/ `fdd` / `folder` / `portable` |
| RID | 默认 `win-x64`，`-Runtime` 可改 |
| 配置 | `-Configuration Release`（默认） |
| 单文件参数 | minimal：`--self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false` |
| 输出 | `pub\`（仓库根，发布前整体删除重建），`-OutDir` 可覆盖；发布后会删掉 `av_libglesv2*`（渲染改 WGL + Software，不再需要 ANGLE） |
| 附带 | 不删 `obj\<Configuration>`（脚本注释写明删了会在 ILLink 阶段失败）；改为**只清过期的 XAML 中间产物** |
| 自检 | 发布后跑 `--probe-dir`（30s，断言 `writable=YES`）与 `--screenshot`（20s，断言 PNG > 10KB）；`-SkipVerify` 可跳过 |

## 2. 实测

| 项 | 期望 | 实测 | 结论 |
|---|---|---|---|
| `-Mode minimal` 产物 | 3 个文件 | `NebulaLauncher.exe` 13.4 MB + `libSkiaSharp.dll` 9.0 MB + `libHarfBuzzSharp.dll` 1.8 MB = **23.4 MB / 3 文件** | ✅ 与脚本注释一致（注释写 21.1 MB，是 ANGLE 剔除前的旧值，已按实测更新认知） |
| `-Mode fdd` 产物 | 框架依赖多文件 | **23.4 MB / 47 文件** | ✅ |
| 是否混入 `tools/`、`Extras/`、`docs/`、`*.Tests` | 不应出现 | 递归检查：无 `Tests` / `InstallProbe` / `shot.ps1` / `seed-demo` 命中 | ✅ |
| **未清理 obj 直接 publish** | 应能运行 | **进程静默退出（exit 0，无窗口、无截图、不建 data 目录）** | ❌ 见 §3 |
| 清理后 publish（`-Clean`） | 应能运行 | 截图模式产出 PNG（145 KB），data 目录生成 `logs/` | ✅ |
| 干净首启（全新 data-dir） | 不崩、引导正常 | 首页正常渲染：「未登录 / 未安装版本 / 去下载」；统计卡全 0 | ✅ `pub2-firstrun.png` |
| 第二次启动同一 data-dir | 不重复引导、不报错 | 正常渲染，无错误对话框 | ✅ `pub2-secondrun.png` |
| 路径含中文与空格 | 不崩 | `--data-dir "…\发布 测试"` 正常，生成 `cache/` + `logs/` | ✅ `pub2-cn-path.png` |
| `--probe-dir`（控制台路径） | 打印可写性 | `writable=YES`，退出码 0 | ✅ 说明二进制本身可执行 |

## 3. 发现的问题：未清理 obj 的 publish 产物会静默不启动

现象：`build.ps1 -Publish -Mode minimal|fdd -OutDir …` 在**没有** `-Clean`、且 `obj/Release` 里已有旧产物时，
生成的 exe 退出码 0、无窗口、不写 data 目录、`--screenshot` 也不产图；同一份代码 `--probe-dir` 却能跑，
说明托管入口正常、Avalonia 初始化阶段就退出了。

判断：与 `build.ps1` 注释里写的「XAML IL 注入不全（No precompiled XAML found），用 `-Clean` 整体清理后重跑即可」是同一件事——
增量 obj 里的 XAML 编译产物与 publish 的目标配置不一致。

处置：**第二十八轮已由脚本解决**（见 §3.1）。人工流程剩下两条：

- 发布跑 `pwsh .\build.ps1 -Publish …`（不再必须手写 `-Clean`，脚本会自己判断 XAML 中间产物是否过期）；
  若不确定，仍可加 `-Clean` 做一次完全清理。
- 拿到**别人的**产物目录时，仍要自己跑 `--probe-dir` + `--screenshot` 各一次，两者都正常才算「产物可用」
  ——脚本的自检只对它自己刚发布的那份负责。

### 3.1 脚本现在怎么堵这个坑

1. **前置（构建前）**：取项目下所有 `*.axaml`（App.axaml / Views / Themes）的最新 mtime，
   与 `obj/<Configuration>` 下所有名为 `Avalonia` 的目录的最新文件比（2 秒容差）。
   过期就只清**那个目录** + 同层 `NebulaLauncher.dll`，不删整个 obj。
2. **后置关一**：`<out>\NebulaLauncher.exe --probe-dir <out>`，30 秒超时，断言 exit 0 且输出含 `writable=YES`
   （读 stdout / stderr / `nebula-probe-dir.txt` 三处）。
3. **后置关二**：`--data-dir <out>\.verify --screenshot <out>\.verify.png --page home`，20 秒超时，
   断言 exit 0 且 PNG > 10240 字节；截图偶发 `0xE0434352` 会重试 1 次。
4. 失败以非 0 退出并打印关卡名 + 实际观测值；成功路径清掉 `.verify*` / `.probe*` / `nebula-probe-dir.txt`，
   发布目录仍是 3 个文件；失败路径保留现场。

**一个环境事实**：这个 exe 写 `%TEMP%` 会被系统拒绝（`UnauthorizedAccessException`），
所以自检的验证文件全部落在产物目录里。第一次自检如果报权限错误，先怀疑这条，不是产物坏了。

实测过的四种失败都能被抓住：缺 exe、假 exe exit 1、假 exe exit 0 但没有 `writable=YES`、
假 exe 过了 probe 关却不产图——最后一种正是「exit 0 的坏产物」。

## 4. 复现命令

```powershell
cd D:\agent_workspace\NebulaLauncher
. .\env.ps1
$env:AVALONIA_TELEMETRY_OPTOUT='1'

# 发布（脚本自带 XAML 过期检测 + 两道产物关；-Clean 仍可用）
pwsh -NoProfile -File .\build.ps1 -Publish -Mode minimal -OutDir D:\agent_workspace\.probe\publish-minimal2

# 只想跳过自检时
pwsh -NoProfile -File .\build.ps1 -Publish -Mode minimal -OutDir … -SkipVerify

# 手工复核（拿到别人的产物时）
cd D:\agent_workspace\.probe\publish-minimal2
.\NebulaLauncher.exe --probe-dir .            # 期望 writable=YES

# 干净首启（截图模式是唯一可观测通道）
.\NebulaLauncher.exe --data-dir D:\agent_workspace\.probe\fresh2 `
    --screenshot D:\agent_workspace\.probe\pub2-firstrun.png --page home
```

第五十四轮实际产物：`minimal` 23.7 MB / 3 文件，`自检 1/2 通过 writable=YES`，
`自检 2/2 通过 PNG 145683 字节`，`[exit] 0`（产物在 `D:\agent_workspace\.probe\pub-r54`）。
第五十三轮 PNG 145661（`pub-r53`）；第五十二轮 145751；第五十一轮 145683；第五十轮无代码改动；
第四十九轮 145751；第四十八轮无代码改动；第四十七轮 145751；第四十六轮 145683；
第四十五/四十四/四十二轮无代码改动；第四十三轮 145751；第四十一轮 145661；第四十轮 145661；
第三十九轮 145751；第三十八轮 145683；第三十六轮 23.7 MB / PNG 145683；
第三十五轮 23.6 MB / PNG 145661；第三十四轮最终产物 `pub-r34b` 为 PNG 145777（首次 `pub-r34` 145751）。
各轮 PNG 相差 27–116 字节，属正常抖动（同一台机器、同一页面、构建号与时间戳变化）。
背景图截图另存 `.probe/bg-home.png` / `bg-home2.png` / `bg-home3.png`（不同遮罩与修复前后的渲染证据）。

## 5. 未验证

- 真实双击启动（本会话没有交互桌面：GUI 进程无法保持，`Start-Process` 直接阻塞）；
  因此「窗口能不能开出来」只能间接由截图模式证明。
- 只读 data-dir / 无写权限回退（需要模拟 ACL，本轮未做）。
- `portable` 与 `folder` 两种形态未验证（portable 的 `%TEMP%\.net` 解压在本机被安全软件拦过，见脚本注释）。
- 目标机缺 .NET 8 Desktop Runtime 时的提示（fdd 形态），需要一台干净机器。
