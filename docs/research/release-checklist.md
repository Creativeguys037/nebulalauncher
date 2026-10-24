# v1.0.0 发布清单

> 调研基准：本地仓库 `D:\agent_workspace\NebulaLauncher`，工作区 = `initial commit` `7440567`（2026-10-24 15:47:15 +0800），`origin/master` 与之相同，无 tag。
> 本轮为只读调研，未构建、未提交、未执行任何 `gh release` 命令。所有数字均为磁盘实测。

## 结论（3 句以内）

现有 `docs/PUBLISH-CHECK.md` 的**流程判据（脚本行为、两道产物关、失败条件）全部仍然成立**，但其中的**体积数字全部过期**，且 HANDOVER R59 记录的 SHA256 在磁盘上已无对应文件可复核。
产物形态建议：`minimal`（框架依赖、24.72 MiB / 3 文件）做 v1.0.0 主产物，加发 `portable`（自包含单文件）做免装运行时备选，`fdd` / `folder` 不发。
阻塞项有三个：`<Version>` 在任何构建文件里都不存在（版本号只靠 SDK 默认值"碰巧"是 1.0.0）、GitHub Release 在 PRIVATE 仓库下匿名不可见、exe 未签名。

---

## A. 现有判据核对（逐条：成立 / 过期 / 缺失）

### A.1 `docs/PUBLISH-CHECK.md` §1「`build.ps1` 实际行为」

| 行 | 判据 | 结论 | 证据 |
|---|---|---|---|
| :14 | 四形态 `minimal`（默认）/`fdd`/`folder`/`portable` | **成立** | `build.ps1:9-10` `[ValidateSet('minimal','fdd','portable','folder')]` + `:10` 默认 `'minimal'` |
| :15 | RID 默认 `win-x64`，`-Runtime` 可改 | **成立** | `build.ps1:11` `[string]$Runtime = 'win-x64'`；`:333` 传给 `dotnet publish -r` |
| :16 | 配置 `Release` 默认 | **成立** | `build.ps1:12` |
| :17 | minimal 参数 = `--self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false` | **成立（逐字一致）** | `build.ps1:341-345` |
| :18 | 输出 `pub\`，发布前整体删除重建；`-OutDir` 覆盖；删 `av_libglesv2*` | **成立** | `build.ps1:311`（默认 `$root\pub`）、`:314-323`（`Remove-Item -Recurse -Force`，占用时报错并给出改 `-OutDir` 的提示）、`:371-372` |
| :19 | 不删 `obj\<Configuration>`，改为只清过期 XAML 中间产物 | **成立，但不完整** | `build.ps1:67-135` 实现；**缺失的一条**：`:294` 在 `-Publish` 下传 `-Force:$Publish`，即发布模式**无条件重建** XAML 中间产物（`:83-98`），不看时间戳。PUBLISH-CHECK 只描述了"比源旧才清"这一路径 |
| :20 | 自检 `--probe-dir`（30s，`writable=YES`）+ `--screenshot`（20s，PNG > 10KB）+ `-SkipVerify` 可跳过 | **成立** | `build.ps1:171`（`WaitForExit(30000)`）、`:193`（`-notmatch 'writable=YES'`）、`:233`（`WaitForExit(20000)`）、`:241`（`Length -gt 10240`）、`:387-392`；`-SkipVerify` 定义在 `:8`，注释 `:7`「日常发布不要加」 |

### A.2 §2「实测」表 —— **数字全部过期**

| 行 | 文档写的 | 实测（`D:\agent_workspace\pub\r61\`） | 结论 |
|---|---|---|---|
| :26 | `NebulaLauncher.exe` 13.4 MB + `libSkiaSharp.dll` 9.0 MB + `libHarfBuzzSharp.dll` 1.8 MB = **23.4 MB / 3 文件** | exe **14,701,210 B**（14.02 MiB）+ Skia **9,414,216 B**（8.98 MiB）+ HarfBuzz **1,804,872 B**（1.72 MiB）= **25,921,298 B / 3 文件（24.72 MiB / 25.92 MB）** | **过期**。exe 单项就差了 1.3 MB |
| :27 | `-Mode fdd` **23.4 MB / 47 文件** | 无产物可测；`build.ps1:23` 头注释写 **26.2 MB / 48 文件**，`docs/HANDOVER.md:125` 同 26.2/48 | **自相矛盾 → 未确定**，发布前必须重测一次 |
| :28 | 不混入 `tools/`、`Extras/`、`docs/`、`*.Tests` | 无法从 r60/r61 直接证伪；`grep -ac "CmlLib"` 命中 188 次、`"NebulaLauncher.Core"` 命中 10 次，说明 exe 是单文件 bundle、托管程序集未压缩嵌入 | **需重跑**（低成本，`ls` 3 个文件即可） |
| :29-30 | 未清理 obj 直接 publish → 进程静默退出（exit 0、无窗口、不产图） | 属历史问题描述；`build.ps1:294` 的 `-Force` 已从根上挡住 | **已过期（保留为背景说明可以，不能当现行判据）** |

### A.3 §3 / §3.1 处置流程

| 行 | 判据 | 结论 | 证据 |
|---|---|---|---|
| :47 | 发布不再必须手写 `-Clean` | **成立** | `build.ps1:294`。**但文档间冲突**：`README.md:158`「发布必须带 `-Clean`」、`docs/HANDOVER.md:778`「发布必须 `-Clean`」仍在，会误导执行者 |
| :49-50 | 拿到别人的产物仍要自己跑 `--probe-dir` + `--screenshot` | **成立**（`build.ps1` 的自检只对本次发布负责） | `build.ps1:149-281` 作用范围限于 `$OutDir` |
| :54-56 | XAML 过期判据：`*.axaml` 最新 mtime vs `obj/<Config>` 下名为 `Avalonia` 的目录，2 秒容差 | **成立** | `build.ps1:59-65`（`Get-XamlSourceTime`，排除 `bin`/`obj`）、`:79-81`（`$_.Name -eq 'Avalonia'`）、`:101`（`FromSeconds(2)`）、`:93-94`/`:132-134`（连 `NebulaLauncher.dll` 一起删） |
| :61 | 成功路径清掉 `.verify*` / `.probe*` / `nebula-probe-dir.txt`，发布目录仍是 3 文件 | **成立** | `build.ps1:278-280` |
| :64-65 | 该 exe 写 `%TEMP%` 会被拒（`UnauthorizedAccessException`） | **未确定**（环境事实，本轮无法独立复现；`Program.cs:309` 写 `nebula-probe-dir.txt` 到产物目录这条实现与之自洽） | `Program.cs:266-309` |

### A.4 §4 复现命令

| 行 | 判据 | 结论 | 证据 |
|---|---|---|---|
| :74 | `. .\env.ps1` 前置 | **成立但有冗余**：`build.ps1:46` 自己就 dot-source `env.ps1`，子进程不依赖父 shell 的环境 | `build.ps1:45-46`、`env.ps1:10-16` |
| :77-78 | `pwsh -NoProfile -File .\build.ps1 -Publish -Mode minimal -OutDir …` | **成立** | 本机 `pwsh` = 7.6.6（实测 `$PSVersionTable`） |
| :92-99 | 各轮 PNG 145661–145777、体积 23.7 MB 等 | **历史记录**，v1.0.0 必须重取，不能沿用 | 最新两条产物（r60/r61）里 exe 均无 R59 符号（见 A.5） |

### A.5 缺失判据（PUBLISH-CHECK 里没有、v1.0.0 必须有）

1. **版本号核对**：发布前读 `(Get-Item .\NebulaLauncher.exe).VersionInfo` 断言 `FileVersion=1.0.0.0` / `ProductVersion=1.0.0`。当前没有任何构建文件写 `<Version>`，靠 SDK 默认值才恰好是 1.0.0（见 D 节）。
2. **SHA256 生成与随产物发布**：PUBLISH-CHECK 全文无 SHA256 步骤；HANDOVER R59 只是"记录"了一个值。
3. **zip 打包步骤**：GitHub Release 直接传 3 个散文件会让用户不知道放哪；应打 zip。
4. **tag / 提交前置条件**：当前 `git status` 有未提交项：` M docs/agents/workspace.yaml`、`?? AGENTS.md`、`?? docs/agents/domain.md`、`?? docs/agents/issue-tracker.md`、`?? docs/agents/triage-labels.md`。打 tag 前必须先决定这些是否入库。
5. **`-SkipVerify` 禁令**：`build.ps1:7` 有注释，PUBLISH-CHECK 未写进清单。正式发版**不得**加该参数。
6. **产物目录命名规范**：现有 `pub\r59`—`pub\r62` 无人能对应到轮次与内容（见 C 节）。
7. **R59 产物不可复核（硬发现）**：`D:\agent_workspace\pub\r59\` 实测为空目录；`docs/HANDOVER-2026-10-24-R59.md:103` 声称的 SHA256 `1C52BDB4ACFA402CB091D8B8C5D86892DA29C9E3A9AFEFAEA90CCB13D0CC396B` 在盘上找不到对应文件。现存 `r60`/`r61` 的 exe 是**另一个**哈希：

```
$ sha256sum pub/r60/NebulaLauncher.exe pub/r61/NebulaLauncher.exe
0906b7a8e2f2945585679c5b816b43fe5e782dc46dbe8531c82d57eb8f1c0519  pub/r60/NebulaLauncher.exe
0906b7a8e2f2945585679c5b816b43fe5e782dc46dbe8531c82d57eb8f1c0519  pub/r61/NebulaLauncher.exe
```

且这两个 exe **不含 R59 的改动**（同文件复制两份，mtime 同为 `Oct 24 14:40`）：

```
$ grep -ac "StartupState"         pub/r61/NebulaLauncher.exe   → 0
$ grep -ac "LoadBeforeFirstFrame" pub/r61/NebulaLauncher.exe   → 0
$ grep -ac "CmlLib"               pub/r61/NebulaLauncher.exe   → 188   (对照组，证明检索有效)
$ grep -ac "NebulaLauncher.Core"  pub/r61/NebulaLauncher.exe   → 10    (对照组)
```

而 `src/NebulaLauncher.App/Services/StartupState.cs` 确实存在（3105 B，mtime `Oct 24 15:13`）且已被 git 追踪（`git ls-files` 有输出）。结论：**v1.0.0 必须重新构建，不能复用任何现有产物，也不能引用 R59 的哈希。**

---

## B. 产物形态建议

### B.1 现状事实

| 形态 | 自包含 | 文件数 | 实测/自述体积 | 依据 |
|---|---|---|---|---|
| `minimal`（默认） | 否（`--self-contained false`） | 3 | **24.72 MiB / 3 文件**（实测 r60/r61） | `build.ps1:341-345`；实测 |
| `fdd` | 否 | 47–48 | 23.4 或 26.2 MB（文档矛盾） | `build.ps1:357-359`、`:23` |
| `portable` | 是 + 裁剪 + 压缩单文件 | 1 | 21.8 MB（未实测） | `build.ps1:347-356`、`:27` |
| `folder` | 是，不单文件 | 231 | 98.5 MB（未实测） | `build.ps1:360-362`、`:26` |

`minimal` 是**框架依赖**：目标机必须有 .NET 8 Desktop Runtime。仓库自己写明了这个前提的代价——`README.md:181-182`「.NET 8 不在 Windows 自带组件里，由用户自行安装」；`build.ps1:383` 在 fdd 分支打印「目标机需要 .NET 8 Desktop Runtime」。而"目标机缺 .NET 8 时的提示"在 `docs/PUBLISH-CHECK.md:108` §5 明确列为**未验证**。

### B.2 结论

1. **`minimal` 必须是 v1.0.0 主产物**。理由：它是历史上唯一被连续实测过的形态（`docs/PUBLISH-CHECK.md:92-99` 记录了五十四轮 PNG 尺寸序列，每轮都有 `writable=YES` + PNG 证据）。1.0.0 不该拿未经实测的形态当主交付。
2. **必须加发一个 self-contained 形态**，理由是首启成功率：1.0.0 的用户多半没装 .NET 8 Desktop Runtime，而缺运行时的降级行为在全仓库是**未验证项**（`docs/PUBLISH-CHECK.md:108`）。
3. 自包含三选一取 **`portable`**：
   - `folder`（98.5 MB / 231 文件）体积是 minimal 的 4 倍，还要用户解压 231 个文件，对 1.0.0 不划算；
   - `fdd`（与 minimal 同为框架依赖、体积更大、文件更多）**没有任何优势，不发**；
   - `portable` 只有 1 个文件、约 21.8 MB，是唯一能"双击即用、免装运行时"的形态。
4. **`portable` 的已知限制必须写进 Release 说明**：首次启动要把原生库解压到 `%TEMP%\.net\<app>`，该目录被安全软件拦截时会直接启动失败；`build.ps1:28-30` 明写「实测本机就是这种情况」，`README.md:171-179` 给出兜底 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`。
5. **附带条件**：`portable` 与 `folder` 在 `docs/PUBLISH-CHECK.md:107` 标为"未验证"。因此 `portable` 只有在发布前用 `build.ps1` 的**默认两道关**（不加 `-SkipVerify`）跑通才发；跑不通就只发 `minimal`，把自包含推迟到 1.0.1。

### B.3 不需要的东西

- 不需要 `win-arm64` / `win-x86`：仓库无相关验证记录，且 `Directory.Build.props:4` 单 TFM `net8.0`。
- 不需要"安装包（MSI/Inno）"：仓库无安装器代码或配置，1.0.0 用"解压即用 zip"。
- 不需要框架依赖的第二个形态（= fdd），见 B.2.3。

---

## C. 目录与命名方案

### C.1 现状（编号已断裂）

```
D:\agent_workspace\pub\
  r59\   (空)            mtime Oct 24 15:23
  r60\   3 文件           mtime Oct 24 14:40   ← 同一 exe，早于 r59
  r61\   3 文件           mtime Oct 24 14:40   ← 与 r60 逐字节相同
  r62\   (空)            mtime Oct 24 14:55
```

两个空目录、两个重复目录、目录时间顺序与编号顺序相反。根因：`build.ps1:314-316` 在每次发布前 **`Remove-Item -Recurse -Force` 整个 `$OutDir`**，所以一个目录名既可能被覆盖成新产物、也可能被清空后中断。轮次号无法承担版本标识职责。

### C.2 v1.0.0 方案（建议照此执行）

```
D:\agent_workspace\pub\v1.0.0\
├─ NebulaLauncher-v1.0.0-win-x64\              ← minimal 解压即用目录（3 文件）
│  ├─ NebulaLauncher.exe
│  ├─ libSkiaSharp.dll
│  └─ libHarfBuzzSharp.dll
├─ NebulaLauncher-v1.0.0-win-x64.zip           ← Release 附件 1（由上面的目录打包）
├─ NebulaLauncher-v1.0.0-win-x64-portable.exe  ← Release 附件 2（portable 唯一产物，需改名）
└─ SHA256SUMS.txt                              ← Release 附件 3
```

命名规则：

- 版本标识用 `v1.0.0`，**不再用轮次号**。轮次号只属于 `docs/HANDOVER-*.md`，不进文件系统。
- 附件名三段固定：`<产品>-<版本>-<RID>[-portable].<ext>`，全小写 RID，全 ASCII，不含空格。
- 目录内的可执行文件名保持 `NebulaLauncher.exe`（`build.ps1:152` 与自检的 `Join-Path $OutDir 'NebulaLauncher.exe'` 硬编码这个名字，改名会让自检失效）。

### C.3 执行命令（含一个必须绕开的坑）

```powershell
cd D:\agent_workspace\NebulaLauncher

# 1) 先建外层目录；build.ps1 会把这个 -OutDir 整体删掉重建，
#    所以 -OutDir 必须指向最内层，不能指向 pub\v1.0.0 本身（否则 zip 会被连带删除）
New-Item -ItemType Directory -Force D:\agent_workspace\pub\v1.0.0 | Out-Null

# 2) 主产物
pwsh -NoProfile -File .\build.ps1 -Publish -Mode minimal `
    -OutDir D:\agent_workspace\pub\v1.0.0\NebulaLauncher-v1.0.0-win-x64

# 3) 备选产物（不加 -SkipVerify；失败就不发 portable）
pwsh -NoProfile -File .\build.ps1 -Publish -Mode portable `
    -OutDir D:\agent_workspace\pub\v1.0.0\_portable

# 4) portable 只有一个 NebulaLauncher.exe，改名后放进发布目录
Move-Item D:\agent_workspace\pub\v1.0.0\_portable\NebulaLauncher.exe `
          D:\agent_workspace\pub\v1.0.0\NebulaLauncher-v1.0.0-win-x64-portable.exe
Remove-Item D:\agent_workspace\pub\v1.0.0\_portable -Recurse -Force

# 5) 打包（保留顶层目录，用户解压得到 NebulaLauncher-v1.0.0-win-x64\）
cd D:\agent_workspace\pub\v1.0.0
tar -a -c -f NebulaLauncher-v1.0.0-win-x64.zip NebulaLauncher-v1.0.0-win-x64
# 备选：Compress-Archive -Path .\NebulaLauncher-v1.0.0-win-x64 -DestinationPath .\NebulaLauncher-v1.0.0-win-x64.zip -CompressionLevel Optimal
```

`tar -a` 走 Windows 自带的 bsdtar，不会写出 `__MACOSX` 之类杂项；`Compress-Archive` 在本机 PowerShell 7 也可用，二者择一。

### C.4 旧目录与仓库内空 `pub/` 的处理

- **`pub\r59`—`pub\r62`**：不在 git 里（`pub/` 在 `.gitignore:5`），留着不影响仓库。建议归档而非删除，因为它们是目前仅存的产物体积证据：

```powershell
New-Item -ItemType Directory -Force D:\agent_workspace\pub\_legacy | Out-Null
Move-Item D:\agent_workspace\pub\r59,D:\agent_workspace\pub\r60,`
          D:\agent_workspace\pub\r61,D:\agent_workspace\pub\r62 `
          D:\agent_workspace\pub\_legacy\
```

- **仓库根的空 `pub/`**（`D:\agent_workspace\NebulaLauncher\pub\`）：它是 `build.ps1:311` 的默认落点。`.gitignore:5` 写着 `pub/`，`git ls-files pub` 输出为空 → **git 从不追踪它**，空目录对仓库零影响。
  - 处理：**保留，不删**（删了下次 `-Publish` 不传 `-OutDir` 时脚本会自己重建）。
  - **不要**加 `.gitkeep`：`build.ps1:314-316` 发布前会把整个目录删掉，`.gitkeep` 会被一起删除；而且加它也改变不了"不追踪"的事实，只是噪音。

---

## D. 版本号落地位置

### D.1 现状：没有任何显式版本号

- `Directory.Build.props`（44 行）的 `<PropertyGroup>`（`:3-15`）**没有 `<Version>` / `<AssemblyVersion>` / `<FileVersion>` / `<InformationalVersion>`**（grep 全仓库 `*.props` 零命中）。
- `src/NebulaLauncher.App/NebulaLauncher.App.csproj`（49 行）与 `src/NebulaLauncher.Core/NebulaLauncher.Core.csproj`（25 行）同样没有。
- 于是版本号来自 SDK 默认 `1.0.0`。实测 `pub\r61\NebulaLauncher.exe`：

```
FileVersion   = 1.0.0.0
ProductVersion= 1.0.0
ProductName   = NebulaLauncher
CompanyName   = NebulaLauncher
```

即：**1.0.0 是"碰巧"命中，不是被写下来的**。这是 v1.0.0 的头号缺口——下一次改版本无处可改。

### D.2 唯一需要改的地方

`Directory.Build.props:3` 那个 `<PropertyGroup>` 内加一行：

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <Version>1.0.0</Version>          <!-- ← 新增，唯一版本号来源 -->
  ...
```

只加 `<Version>1.0.0</Version>` 即可让 SDK 派生 `AssemblyVersion=1.0.0.0`、`FileVersion=1.0.0.0`、`InformationalVersion=1.0.0`。若想让三段/四段关系显式，可再补 `<AssemblyVersion>1.0.0.0</AssemblyVersion>` 与 `<FileVersion>1.0.0.0</FileVersion>`（非必需）。

放在 `Directory.Build.props` 而不是 app csproj 的理由：仓库有 7 个 csproj（`src` 2 个、`tests` 1 个、`tools` 4 个），版本号应一处生效；放根目录 props 对测试/工具项目无害。

### D.3 生效链路（谁读这个版本号）

| 消费者 | 位置 | 读取方式 | 是否需改动 |
|---|---|---|---|
| 设置页「关于」分区 | `src/NebulaLauncher.App/ViewModels/SettingsViewModel.cs:2114-2129`（`AppVersion`）→ `src/NebulaLauncher.App/Views/SettingsView.axaml:1507` `Text="{Binding AppVersion}"` | `AssemblyInformationalVersionAttribute`，剥 `+hash` | **不需要**，改 `<Version>` 后自动变 |
| 诊断信息 / 崩溃报告导出 | `src/NebulaLauncher.Core/Diagnostics/DiagnosticsSnapshot.cs:136-148`（`LauncherVersionOf`） | 同上 | 不需要 |
| 配置包 manifest | `src/NebulaLauncher.Core/Transfer/ProfileBundleService.cs:913-924`（`ResolveLauncherVersion`） | `assembly.GetName().Version` → 得 `1.0.0.0` | 不需要 |
| 启动日志里的 `GameLauncherVersion` | `src/NebulaLauncher.Core/Launching/GameLauncher.cs:979` | `GetName().Version?.ToString(3) ?? "1.0.0"` → 得 `1.0.0` | 不需要 |
| Win32 应用清单 | `src/NebulaLauncher.App/app.manifest:3` `<assemblyIdentity version="1.0.0.0" …>` | 原生 SxS manifest，**不参与**托管程序集版本 | **可不动**（现值恰好一致；改了也不会影响上面四处） |

**关于页说明**：仓库**没有独立的 About 页**（`ls src/NebulaLauncher.App/Views` 共 16 个 `.axaml`，无 `AboutView`）。`docs/FINAL-SCOPE.md:12` 说的"关于"是设置页的一个分区，代码里对应 `SettingsViewModel.cs:2112` 的注释 `// ---------------- 关于 ----------------`，界面在 `SettingsView.axaml:1500-1510`（`版本` / `.NET` 两行）。所以"写进关于页"= 已存在的 `SettingsView.axaml:1507`，无需新增界面。

### D.4 验证命令

```powershell
(Get-Item D:\agent_workspace\pub\v1.0.0\NebulaLauncher-v1.0.0-win-x64\NebulaLauncher.exe).VersionInfo |
    Format-List FileVersion,ProductVersion,ProductName
# 期望：FileVersion=1.0.0.0  ProductVersion=1.0.0
```

---

## E. GitHub Release 草稿要点与命令骨架

### E.1 事实前置

- 远端：`origin https://github.com/Creativeguys037/nebulalauncher.git`（`git remote -v`）。
- `gh` 2.103.2 已登录账号 `Creativeguys037`，token scopes 含 **`repo`**（足以建 draft release）。
- 仓库**没有** `.github/` 目录（`find .github` 无输出）→ 无 workflow、无 issue template、无 `release.yml`。发布流程全手工，构建机是唯一可信产物来源。
- 仓库**没有** tag（`git tag -l` 空），只有一个提交 `7440567`。
- 仓库**没有** LICENSE、CHANGELOG（见 H 节）。

### E.2 tag 与标题

| 项 | 值 |
|---|---|
| tag 名 | `v1.0.0`（annotated tag；带 `v` 前缀） |
| draft 标题 | `Nebula Launcher v1.0.0` |
| 目标提交 | 默认分支 `master` 当前 HEAD（打 tag 前先确保工作区已定稿，见 G.4） |
| 草稿标志 | `--draft`（**不加** `--prerelease`） |

### E.3 Release 说明结构（中文，面向用户）

建议文件：`docs/release-notes/v1.0.0.md`（本轮未创建，需 lead 决定后新建）。七段：

1. **一句话定位**
   `Nebula Launcher 1.0.0 —— 面向 Windows 的 Minecraft 启动器首个正式版：原版 / Fabric / Quilt / Forge / NeoForge 安装与启动、账户、实例隔离、模组与整合包、存档备份、崩溃归因，全部在一个原生界面里。`
2. **本版功能摘要**（6–8 条，只写用户能感知的；素材取 `docs/HANDOVER.md:25-35` 的模块表 + `docs/FINAL-SCOPE.md:10-28`）：版本安装与启动、账户（微软设备码 / 离线 / 第三方验证）、实例隔离与组件修复、模组与整合包（Modrinth + CurseForge 双源）、存档备份与世界编辑、崩溃归因（75 条规则）与日志中心、主题与托盘。
3. **系统要求**
   `Windows 10 1809 及以上 / x64；.NET 8 Desktop Runtime（框架依赖版必需，下载地址 https://dotnet.microsoft.com/download/dotnet/8.0）；至少 200 MB 可用空间；启动 Minecraft 另需相应版本的 Java（启动器可在缺 Java 时一键安装）。`
4. **安装与运行**（两个附件分别说明）
   - `NebulaLauncher-v1.0.0-win-x64.zip`：解压到任意目录（**不要**只把 exe 单独拷出来，旁边两个原生 dll 必需），双击 `NebulaLauncher.exe`；需先装 .NET 8 Desktop Runtime。
   - `NebulaLauncher-v1.0.0-win-x64-portable.exe`：单文件，免装运行时；首次启动会解压到 `%TEMP%\.net\NebulaLauncher`，若被安全软件拦截，设置环境变量 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 指向可写目录后再启动。
5. **已知限制**（每条都在文档里有出处，不要写没有出处的限制）
   - exe 未做代码签名，Windows SmartScreen 会提示"未知发布者"。
   - 真实双击首启未在交互桌面上验收（`docs/PUBLISH-CHECK.md:104-105`）。
   - 真实微软账户登录、Forge 真机启动未验（`docs/HANDOVER.md:38`、`docs/HANDOVER-2026-10-24-R59.md:120-122`）。
   - macOS 未适配（`README.md:3`）。
   - 便携单文件版依赖 `%TEMP%` 可写（`README.md:171-179`）。
6. **校验和**：贴 `SHA256SUMS.txt` 的全部内容（见 F 节），并给一行验证命令。
7. **未签名说明**：一句事实 + 一句可操作
   `本版可执行文件未使用代码签名证书签名，因此 Windows 可能显示"Windows 已保护你的电脑"。请用第 6 节的 SHA256 核对文件完整性后再决定是否运行（属性 → 数字签名 为空属预期）。`

### E.4 命令骨架（**不要执行**）

```powershell
cd D:\agent_workspace\NebulaLauncher

# ── 前置核对 ──────────────────────────────────────────────
git status --short                 # 必须为空或只剩已决定的改动
git log --oneline -1               # 确认 HEAD 是定稿提交
git tag -l v1.0.0                  # 必须为空（已存在则先删：git tag -d v1.0.0）

# ── 1) 打 tag 并推远端（annotated） ───────────────────────
git tag -a v1.0.0 -m "Nebula Launcher v1.0.0"
git push origin v1.0.0

# ── 2) 建 draft Release ──────────────────────────────────
gh release create v1.0.0 `
  --draft `
  --title "Nebula Launcher v1.0.0" `
  --notes-file docs\release-notes\v1.0.0.md `
  "D:\agent_workspace\pub\v1.0.0\NebulaLauncher-v1.0.0-win-x64.zip" `
  "D:\agent_workspace\pub\v1.0.0\NebulaLauncher-v1.0.0-win-x64-portable.exe" `
  "D:\agent_workspace\pub\v1.0.0\SHA256SUMS.txt"

# ── 3) 核对草稿 ──────────────────────────────────────────
gh release view v1.0.0 --json isDraft,name,tagName,assets,url
gh release view v1.0.0 --web       # 人工看一眼渲染与附件

# ── 4) 发布由人在网页点「Publish release」（见 G.7） ──────
```

要点：
- 附件用**绝对路径**给 `gh`，避免 cwd 歧义；文件名即远端 asset 名，所以本地必须先按 C.2 命名好。
- `--notes-file` 的文件必须在工作区里（`gh` 直接读本地文件，不入 git 也能用，但建议入库以便追溯）。
- 若某次只发 `minimal`（`portable` 未过自检），把 portable 那一行删掉，并在 notes §4 里删掉对应段落——不要留"文件不存在"的说明。
- 私有仓库下 `gh release create` 正常可用；匿名访问该 Release URL 返回 404（见 H.2）。

---

## F. 校验和做法

### F.1 生成

```powershell
cd D:\agent_workspace\pub\v1.0.0
Get-ChildItem -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Get-FileHash -Algorithm SHA256 |
    ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) } |
    Out-File -FilePath SHA256SUMS.txt -Encoding ascii
```

- **文件名**：`SHA256SUMS.txt`（大写 SUMS，跟随 GNU 惯例；无扩展歧义，txt 便于直接双击查看）。
- **格式**：每行 `<64 位小写十六进制>␣␣<文件名>`，**两个空格**分隔（GNU coreutils 的二进制模式格式，`sha256sum -c SHA256SUMS.txt` 可直接校验）。
- **小写**：`Get-FileHash` 默认输出大写；用 `.ToLowerInvariant()` 转小写，才能被 `sha256sum -c` 与大多数校验工具直接吃。
- **只列会发布出去的文件**：zip 与 portable exe；不要把解压目录里的 3 个原始文件也列进去（那三个的哈希从 zip 里再算即可），否则校验清单与附件对不上。
- **生成顺序**：必须在 zip 打包**之后**、上传**之前**。

### F.2 随产物发布

1. `SHA256SUMS.txt` 作为 Release 的**第三个附件**上传（`gh release create` 里多一个参数即可，见 E.4）。
2. 同时把它的**全文**贴进 Release notes 第 6 节。理由：私有仓库的附件下载需要登录，notes 正文是唯一能被截图/转述的部分。
3. notes 里给用户一行验证命令：

```powershell
Get-FileHash .\NebulaLauncher-v1.0.0-win-x64.zip -Algorithm SHA256
# 与 SHA256SUMS.txt 第一行比对（忽略大小写）
```

### F.3 不要做的事

- 不要只把 exe 的哈希写进 HANDOVER 就算完（R59 就是这么做的，结果是产物被别的轮次覆盖后哈希永久失去对应文件，见 A.5.7）。
- 不要用 MD5/SHA1 作为发布校验（`build.ps1` 里 SHA1 是给 Minecraft 文件复用用的，与发布完整性无关）。

---

## G. 用户手动动作清单

标 **[阻塞]** 的是缺了它 v1.0.0 无法产出/无法发布；其余是决策或验收。

1. **[阻塞] 决定 `portable` 发不发** —— 影响附件数量与 notes 内容。建议先跑一次 `-Mode portable` 看两道关是否通过，再定。
2. **[阻塞] 落 `<Version>1.0.0</Version>`** 到 `Directory.Build.props:3` 的 `<PropertyGroup>`（见 D.2）。这是唯一改源码的动作，必须由人授权（本轮只读）。
3. **[阻塞] 决定工作区未提交项**：` M docs/agents/workspace.yaml`、`?? AGENTS.md`、`?? docs/agents/domain.md`、`?? docs/agents/issue-tracker.md`、`?? docs/agents/triage-labels.md`。打 tag 前必须 commit 或 stash，否则 tag 指向的提交与"发布的版本"不一致。
4. **[阻塞] 执行构建**（G.1 的两条 `build.ps1` 命令）。不要加 `-SkipVerify`。
5. **[阻塞] 核对版本号**：`(Get-Item …\NebulaLauncher.exe).VersionInfo` → `FileVersion=1.0.0.0`、`ProductVersion=1.0.0`（D.4）。
6. **[阻塞] 生成 `SHA256SUMS.txt` 并打包 zip**（F.1 + C.3 第 5 步）。
7. **真机验收（必须人在有交互桌面的机器上做）**：双击 `NebulaLauncher.exe` → 窗口出现、首页渲染、首启引导正常；关掉再开一次；托盘图标与"关闭到托盘"；中文+空格路径下启动一次。依据：`docs/PUBLISH-CHECK.md:104-105` 明确「本会话没有交互桌面，窗口能不能开出来只能间接由截图模式证明」。
8. **干净机验收（无 .NET 8 的机器）**：① 框架依赖版：确认缺运行时的提示文案与跳转正常（`docs/PUBLISH-CHECK.md:108` 的未验证项）；② portable 版：确认能起来、`%TEMP%\.net\NebulaLauncher` 能建。
9. **[阻塞] 打 tag 并推送**：`git tag -a v1.0.0 -m "…"` + `git push origin v1.0.0`（写操作，需人执行）。
10. **[阻塞] 建 draft Release**：E.4 第 2 步的 `gh release create`。
11. **人工审阅 draft**：附件 3 个、notes 七段齐全、§6 校验和与 `SHA256SUMS.txt` 逐字一致、§7 未签名说明在。
12. **[阻塞] 点 Publish**：在 GitHub 网页把 draft 改为 published（`gh release edit v1.0.0 --draft=false` 也可，但建议人眼过一遍再点）。
13. **决定是否买代码签名证书**：不买 → 保留未签名说明；买 → 走 EV/OV 证书 + `signtool`，并重写 notes §7。
14. **决定仓库可见性**：PRIVATE 保持 → Release 只在有 repo 权限的人之间可见；要对外 → 转 public（见 H.2）。

---

## H. 风险与未决

### H.1 未签名 exe 的拦截（高风险，无技术解法）

- `minimal` 的 exe 是 .NET 单文件 bundle（`build.ps1:341-345`），且旁边还需落盘 `libSkiaSharp.dll` / `libHarfBuzzSharp.dll`。SmartScreen 对"未知发布者 + 用户新下载的 exe"必然拦一次；单文件 bundle 的解压行为也常见于启发式杀软的行为告警。
- 无法在本轮验证（无干净机、无签名证书）。可做的只有：Release notes 写清 SHA256 与"属性→数字签名 为空属预期"，让用户自行比对后选择"仍要运行"。
- 结论：**不构成发布阻塞**，但必须在 notes §5/§7 写明，否则首批用户会当成恶意软件。

### H.2 PRIVATE 仓库的 Release 可见性（结构性约束）

- 私有仓库的 Release 只对有 repo 读权限的账号可见；未登录或非协作者访问 Release URL / API 返回 404，附件下载需要登录。
- 直接后果：`v1.0.0` 在当前仓库状态下**无法作为公开下载渠道**，只能作为内部存档/交付渠道。
- 若要"发给人下载"，只有三条路：转 public、加协作者、或在别处（网盘/自有站点）镜像产物。**本轮未决**，需用户明确 1.0.0 的受众。

### H.3 LICENSE（未决，转 public 前必须解决）

- 仓库无 LICENSE / COPYING / NOTICE（`ls -a | grep -Ei 'license|changelog|notice'` 零命中）。
- `README.md:245-259`「数据与许可」已写明第三方内容：`src/NebulaLauncher.Core/Resources/WikiEntries.txt` 来自 PCL2（作者龙腾猫跃）与 MC 百科，按 PCL《存储库合理使用指南》使用，并给出移除路径。这段是**合规声明**，不能替代项目自身的许可证。
- 私有仓库可以不写；一旦转 public，无 LICENSE 等于"保留所有权利"，他人不可合法使用/分发。建议 MIT 或显式专有声明。**未决**。

### H.4 CHANGELOG（建议不加）

- 仓库无 `CHANGELOG.md`。已有的变更记录在 `docs/HANDOVER-*.md`（逐轮）与 `docs/PCL2-PORTING.md`（功能对照），但都是**面向开发者**的。
- 建议：1.0.0 不引入 `CHANGELOG.md`，把 Release notes 当唯一面向用户的变更说明，避免两份文档同时维护。若坚持要，则以 `docs/release-notes/` 目录按版本号累积，每份 notes 即一条 CHANGELOG 记录。**未决**。

### H.5 R59 产物哈希不可复核（已在 A.5.7 举证）

`docs/HANDOVER-2026-10-24-R59.md:103` 的 `1C52BDB4ACFA402CB091D8B8C5D86892DA29C9E3A9AFEFAEA90CCB13D0CC396B` 在磁盘上无对应文件；现存产物哈希是 `0906b7a8…` 且不含 R59 代码。**必须在 v1.0.0 构建后作废这条记录**，并在新的 HANDOVER 里写新哈希 + 产物路径（路径要写 `v1.0.0` 而不是轮次号）。

### H.6 文档里的体积数字四套并存（会误导验收）

| 位置 | 写的 minimal 体积 |
|---|---|
| `build.ps1:339` 注释 | 21.1 MB / 3 文件（10.4 + 9.0 + 1.7） |
| `docs/HANDOVER.md:124` | 22.7 MB / 3 文件 |
| `docs/PUBLISH-CHECK.md:26` | 23.4 MB / 3 文件（13.4 + 9.0 + 1.8） |
| `docs/HANDOVER.md:20` | 23.7 MB |
| `docs/HANDOVER-2026-10-24-R59.md:103` | 24.7 MB / 3 文件 |
| **实测 r60/r61** | **24.72 MiB（25,921,298 B）/ 3 文件** |

建议 v1.0.0 定稿后一次性对齐这五处（以及 `README.md:155` 的 23.4 MB、`:164` 的「单个 exe 10.3 MB」）。本轮只读，未改。

### H.7 `portable` / `folder` 从未验证（`docs/PUBLISH-CHECK.md:107`）

`portable` 要进 v1.0.0，必须先用 `build.ps1` 默认两道关跑一遍（不加 `-SkipVerify`）；`folder` 建议直接不发。另一个未知数：`portable` 走 `PublishTrimmed=true` + `TrimMode=partial`（`build.ps1:351-352`），裁剪掉的首启路径在仓库里没有任何验证记录（`docs/HANDOVER.md:1295` 明确「发布自检的 portable 形态 … 未测」）。

### H.8 `-Mode fdd` 的文件数/体积自相矛盾

`docs/PUBLISH-CHECK.md:27` 写 47 文件 / 23.4 MB，`build.ps1:23` 与 `docs/HANDOVER.md:125` 写 48 文件 / 26.2 MB。两者都没留实测环境。因为 B 节建议**不发 fdd**，这条不影响 v1.0.0；但若有人后续按 PUBLISH-CHECK 说"fdd 已实测通过"就会误判。**未确定**。

### H.9 构建环境依赖未固化

- `global.json:3` 锁定 SDK `8.0.426`（`rollForward: latestFeature`），`env.ps1:4` 指向 `C:\Users\MECHREUO\.dotnet8`。换机器/换账号后 `build.ps1:46` 的 dot-source 会打印"找不到 SDK"并 `return`（`env.ps1:5-8`），但**不会失败退出**——随后 `dotnet` 可能落到另一个 SDK 版本上，版本号与产物随之不同。
- 这是 1.0.0 可复现性的薄弱点：建议在 Release notes 或 HANDOVER 里记下"v1.0.0 由 SDK 8.0.426 构建"。**未决**（是否要把它做成硬断言）。
