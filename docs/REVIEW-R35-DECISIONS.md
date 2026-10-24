# R35 §2 裁决复核（独立复核）

复核对象：`docs/HANDOVER-2026-10-24-R35.md` §2 的 5 行裁决。
复核方式：只读源码与文档，逐条对照 Nebula 与本地 PCL2 源；未修改任何布局/业务代码，未重新跑构建与测试。
判据：Nebula 用户能否完成同一任务，而不是控件是否长得一样。
路径缩写：`N:` = NebulaLauncher 仓库根；`P:` = `../PCL/Plain Craft Launcher 2`。

## 1. 结论

| # | R35 裁决 | 复核结论 | 一行理由 |
|---|---|---|---|
| 1 | 组件修改器安装前后对比 / 逐项恢复 → 不单独实现 | **需要修正** | PCL 的「还原」是撤销到进入修改页时的组件快照，不是「换回原版」；当前源有 8 个逐组件还原按钮，且 Forge 旧构建在 Nebula 下拉里不存在，回滚能力实际缺失。 |
| 2 | 恢复原始名称 → 不单独实现 | **维持（理由需改写）** | 该按钮是实例/版本目录名重命名草稿的撤销（`OriginalName = Task.Input.Name`），不是 `PredictVersionName` 或「换到原版」能替代的语义；Nebula 的重命名对话框预填当前名且可取消，用户能力不缺。 |
| 3 | 嵌套整合包 `modpack.zip` / `modpack.mrpack` → 不实现 | **需要修正（结论可收窄）** | 「无多游戏目录 + 空文件夹交互」成立；但「解压后的客户端能被普通压缩包链覆盖」不成立：`DetectFormat` 不识别 `modpack.zip`，`PlainZip` 只把整包解到实例根、`VersionName` 为空。 |
| 4 | 完整导出选项 / 多格式 → 已存在，不是缺口 | **需要修正** | 现只有 `.mrpack` + 5 个粗粒度开关；PCL 是 zip/mrpack 双格式、约 25 个导出选项 + 逐存档/资源包/光影包选择 + 游戏设置/服务器列表/Java/启动器/导出配置；`LaunchScriptExporter` 是启动脚本，不是整合包导出。 |
| 5 | 其余历史明确不做清单 → 沿用历史裁决 | **需要修正（引用范围）** | `TAKEOVER.md` §5 不含 Nide 与自定义主页事件；`AUDIT-2026-10-23.md` §5 明确说这两项「不等价 / 证据不足」，不能以「沿用历史裁决」关闭；另「更新日志 / 中文词表」未在该行列名。 |

## 2. 逐条证据

### 2.1 第 1 条：组件修改器安装前后对比 / 逐项恢复

**PCL 的实际交互不是「还原到原版」，是「撤销到进入修改页时的快照」。**

- `P:Pages/PageDownload/PageDownloadModify.vb:1-5` 类注释直接写明：「版本修改页：完整还原已安装组件后，复用安装页的选择交互。」
- `:299-355`：进入页面时把当前组件存成 `InitialTarget = GetSelectedComponents()`，并把每个组件的初始值塞进对应按钮的 `Tag`：
  `BtnMinecraftRestore.Tag = VanillaData`、`BtnOptiFineRestore.Tag = SelectedOptiFine`、`BtnForgeRestore.Tag = SelectedForge`…（:338-345）。
- `:474-519`：`RefreshComparisons` 逐组件比较 `InitialTarget` 与当前选择；`:521-577` 的 `ShowComparison` 渲染「旧版本 → 新版本」，并在 `IsChanged` 时显示还原按钮。
- `:579-632`：`RestoreComponent` 处理 8 个按钮，把对应 `Selected*` 设回按钮 `Tag` 中的初始值；它不是「把加载器从版本 JSON 里摘掉」。
- `P:Pages/PageDownload/PageDownloadInstall.xaml:41-50`：还原按钮的样式 ToolTip 是「撤销更改」。
- `P:Pages/PageDownload/PageDownloadInstall.xaml:105,135,165,195,225,257,287,317`：8 个组件还原按钮，分别是 Minecraft、Forge、NeoForge、Fabric、Fabric API、OptiFine、OptiFabric、LiteLoader；`:69` 另有独立的名称还原按钮。
- `P:Pages/PageDownload/PageDownloadModify.vb:583-603`：`BtnMinecraftRestore` 不仅恢复原版选择，还把 OptiFine/Forge/NeoForge/Fabric/LiteLoader/Fabric API/OptiFabric 全部恢复为初始值——它是「撤销本页所有组件改动」，不是「只留原版」。

因此 R35 的三处表述均不成立：

1. 「PCL 的 5 个还原按钮」：当前本地 PCL 源是 8 个组件还原 + 1 个名称还原，不是 5 个；这个计数与 `.probe/pcl-gap-r29.md:95-99` 的旧附录一致，但该附录漏了 Fabric API / OptiFabric / LiteLoader，且把 `RestoreComponent` 描述成「摘掉版本 JSON 的 libraries/arguments」，与当前 `PageDownloadModify.vb:579-632` 的 UI 选择重置不符。
2. 「服务于 OptiFine/LiteLoader 组合」：还原按钮覆盖 Minecraft/Forge/NeoForge/Fabric/Fabric API/OptiFabric/LiteLoader，不只是这两个。
3. 「没有叠加组合时逐组件还原与换回原版是同一个操作」：反例是 Forge 47.2.0 → 47.3.0，按「还原」回到 47.2.0，按「换回原版」则没有 Forge；两者不是同一操作。

**Nebula 现状：能换加载器，但不能完成「回滚到修改前的那一个加载器版本」。**

- `N:src/NebulaLauncher.Core/Instances/InstanceService.Loader.cs:20-174`：`SwitchLoaderAsync` 支持目标 `Vanilla`，通过 `InstallLoaderAsync` 装新版本并改 `VersionName`、`Loader`、`LoaderVersion`；`:55-62` 明确拒绝跨 Minecraft 版本；`:70-81` 同加载器 + 未指定版本时直接返回 `AlreadyMatches`。
- `N:src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:2956-2982`：目标加载器含 `Vanilla`；`:3094-3096` 当前状态文案只有「当前 {MC 版本} · {加载器名}」，没有当前加载器 build；`:3108-3118` 切换目标；`:3144-3147` 原版分支只给「原版不需要加载器版本」的提示；`:3254-3272` 执行切换。
- `N:src/NebulaLauncher.App/Views/VersionsView.axaml:659-683`：原版/Fabric/Quilt/Forge/NeoForge 胶囊；`:687-710` 加载器版本下拉。
- `N:src/NebulaLauncher.Core/Versions/VersionService.cs:304-321`：Forge 的版本列表**只返回官方推荐版一条**（注释明确说 Forge 没有可选版本全集）；`:326-329` NeoForge 才是完整目录；`:331-342` Fabric/Quilt 走 meta 列表。
- `N:src/NebulaLauncher.Core/Installing/InstallService.cs:746-754`：后端 `ForgeInstaller.Install(mc, request.LoaderVersion, options)` **支持指定任意 Forge 构建**。也就是说，回滚能力不是 Core 缺失，而是 UI 没有把「当前/旧构建」放进可选路径。
- `N:src/NebulaLauncher.Core/Models/Instances.cs:574-611`：`InstanceLoaderAdvice` 只产出「换到原版后 mods 不会加载」这类后果说明，不是逐组件新旧对比。
- `N:src/NebulaLauncher.Core/Installing/ComponentInspector.cs:37-171`：组件检查只报告当前状态（版本、缺失/改动文件），没有进入修改前的快照，因此也不能替代 PCL 的前后对比。

**判定：** 完整的「组件组合编辑面板」可以因 OptiFine/LiteLoader 等历史裁决而不做；但把「逐项恢复」整体判为「换回原版」是错误等价。用户可感知的缺口是：改了 Forge/Fabric/NeoForge 版本后，想回到改动前的版本；Fabric/NeoForge 还能从列表里手动选回，Forge 在 Nebula 下拉里只有推荐版，实际做不到。

### 2.2 第 2 条：恢复原始名称

- `P:Pages/PageDownload/PageDownloadModify.vb:14`：`Private OriginalName As String`。
- `:301`：`OriginalName = Task.Input.Name`——`Task.Input` 是 `PageInstanceLeft.Instance`（当前选中的实例），`McInstance.Name` 是版本目录最后一段（`P:Modules/Minecraft/ModMinecraft.vb:284-286`）。
- `:310-313`：`TextSelectName.Text = OriginalName`，校验规则是 `New ValidateFolderName(McFolderSelected & "versions", IgnoreList:=New List(Of String) From {OriginalName})`；这个文本框是版本目录名。
- `:646-665`：`UpdateOriginalNameVisibility` 只在文本框与 `OriginalName` 不同时显示按钮；`BtnOriginalNameRestore_Click` 直接把文本设回 `OriginalName`。
- `P:Modules/Minecraft/ModMinecraft.vb:814-850`：`McInstance.Rename` 真正做的事是移动版本目录、改 jar/natives 名、改 JSON id。
- `P:Pages/PageDownload/PageDownloadInstall.xaml:48`：名称还原按钮复用 `ModifyRestoreButton`，ToolTip 是「撤销更改」。
- 另一种同名能力是版本列表右键的「复原名称」（`P:Pages/PageSelectLeft.xaml.vb:46-49,270-295`），它恢复的是 `McFolder.Types.RenamedVanilla` 的游戏文件夹显示名，也不是加载器版本名。

所以这个按钮是「重命名版本目录的草稿撤销」，不是「恢复加载器版本名」，更不是「换到原版时由安装器自然恢复」。

需要区分一个容易混淆的点：换到原版确实会把 `InstanceInfo.VersionName` 设成 MC 版本名（`N:src/NebulaLauncher.Core/Instances/InstanceService.Loader.cs:132-141`，`InstallService.cs:540` 返回 `minecraftVersion`）。但这不是 PCL 按钮的语义：(a) 它不改 `InstanceInfo.Name`（显示名）；(b) 原始版本目录名不一定是原版名，可能是 Forge 合成名或自定义名；(c) PCL 按钮是撤销重命名草稿，不要求切加载器。R35 把这三件事混成了一条因果链。

Nebula 这边：

- `N:src/NebulaLauncher.Core/Instances/InstanceService.Admin.cs:138-174`：`RenameAsync` 只改 `InstanceInfo.Name`，不碰目录；目录名是 `Id`（`:157-159` 注释明确说改显示名不碰路径）。
- `N:src/NebulaLauncher.App/ViewModels/VersionsViewModel.cs:2727-2795`：重命名浮层打开时 `RenameDraft = target.Name`，有 `CancelRename`；用户改坏了点「取消」即可，不需要单独的「恢复原名」按钮。
- `N:src/NebulaLauncher.Core/Instances/InstanceService.Loader.cs:87-89,267-292`：`PredictVersionName` 只用于「失败后清理」和「装前判断目标目录是否已存在」，不是最终返回名；`:132-141` 换加载器只改 `VersionName/Loader/LoaderVersion`，不改 `InstanceInfo.Name`。
- `N:src/NebulaLauncher.Core/Installing/InstallService.cs:540`：原版安装实际返回 `request.ResultVersionName ?? minecraftVersion`。

**判定：** 不单独实现「恢复原始名称」的结论可以维持，但 R35 的理由是事实错误。正确理由应是：Nebula 的实例显示名与版本目录名分离，重命名只改显示名且对话框预填当前名，不存在 PCL 那种「修改版本时顺带重命名版本目录」的流程，因此没有独立的「恢复原名」需求；这与 `PredictVersionName` 无关。

### 2.3 第 3 条：嵌套整合包 `modpack.zip` / `modpack.mrpack`

**PCL 的行为：**

- `P:Modules/Minecraft/ModModpack.vb:44-77`：根目录或一级子目录出现 `modpack.zip` / `modpack.mrpack` 时判为 `PackType = 9`（带启动器的压缩包）。
- `:764-823`：`InstallPackLauncherPack` 要求用户选一个空文件夹；把整包解压到该文件夹；按 `FileVersionInfo.ProductName` 找包内自带的启动器，可选「换用」；把 `TargetFolder & ".minecraft\..."` 加入游戏文件夹列表；再递归 `ModpackInstall` 里面的 `modpack.*`。
- `:826-862`：普通压缩包走 `InstallPackCompress`，先扫描 `versions/<ver>/<ver>.json` 定位 `.minecraft` 根，同样要求空文件夹。
- 这套「带启动器整包」不是罕见格式：PCL 自己的导出页默认勾选「PCL 启动器程序」（`P:Pages/PageInstance/PageInstanceExport.xaml:262-268`），`P:Pages/PageInstance/PageInstanceExport.xaml.vb:704-716` 会先压出内层 `modpack.mrpack`，再把整个目录压成外层 zip。也就是说，PCL 用户默认导出的就是 PackType 9 的包。

**Nebula 现状：**

- `N:src/NebulaLauncher.Core/Backups/ModpackService.cs:33-40`：`FormatKeys` 只有 `mcbbs.packmeta`、`mmc-pack.json`、`modrinth.index.json`、`manifest.json`、`modpack.json`，没有 `modpack.zip` / `modpack.mrpack`。
- `:519-547`：`DetectFormat` 只看根与一级子目录；找不到上述键就返回 `ModpackFormat.PlainZip`。
- `:1015-1105`：`ExtractOverrides` 对 `PlainZip` 的覆盖前缀是空字符串，整包所有条目按原相对路径解到实例目录。
- `N:src/NebulaLauncher.Core/Models/Content.cs:427,447`：`PlainZip` 预览没有 `MinecraftVersion`，`RequiresGameInstall` 为 false；`ModpackService.cs:285-299` 因此把 `InstanceInfo.VersionName` 写成空字符串，并跳过游戏安装。

结果：把一个 PCL 默认导出的外层 zip 拖进 Nebula，会被当成普通压缩包，实例目录里出现 `PCL.exe`、`.minecraft/`、`modpack.zip`，`VersionName` 为空，实例无法按导入内容启动；内层 `modpack.*` 不会被自动识别。R35 说「解压后的客户端本身能被普通整合包/压缩包识别链覆盖」——只对「用户手动解出内层 modpack 再导入」成立，对**外层包**不成立，而外层包正是该条要处理的对象。

「多游戏目录 + 选择空文件夹」这条理由本身成立：`N:src/NebulaLauncher.Core/Abstractions/Infrastructure.cs:4-18` 只有一个 `MinecraftDirectory` + 全局 `VersionsDirectory`；`N:src/NebulaLauncher.Core/Instances/InstanceContext.cs:170-241` 的实例也是从全局 `versions/` 解析版本。PCL 的整包迁移把一整个 `.minecraft` 当独立游戏根，这确实不是 Nebula 当前实例模型的直接映射。

**判定：** 结论可以收窄为「不做带启动器的整包目录迁移」，但不能写成「普通压缩包链已覆盖」。最小可做的用户能力见 §3.3。

### 2.4 第 4 条：完整导出选项 / 多格式

**Nebula 实际暴露的选项：**

- `N:src/NebulaLauncher.Core/Models/Content.cs:603-628`：`ModpackExportOptions` 有 `Name`、`Version`、`Author`、`IncludeSaves`、`IncludeConfig`、`IncludeResourcePacks`、`IncludeShaderPacks`、`PreferModrinthHashes`、`MinecraftVersion`。
- `N:src/NebulaLauncher.App/Views/WorldsView.axaml:115-117`：卡片明确标 `.mrpack`；`:163-178` 是 5 个复选框（包含存档 / 包含配置 / 包含资源包 / 包含光影包 / 优先使用在线哈希），不是 R35 说的 4 个；`:137-145` 是整合包名称/版本；`:148-161` 是保存位置。
- `N:src/NebulaLauncher.App/ViewModels/WorldsViewModel.cs:1054-1064`：只把这 5 个布尔值 + 名称/版本/目标 MC 版本传入；`Author` 没有 UI，`MinecraftVersion` 由所选实例自动填，不是可选「目标版本」。
- `N:src/NebulaLauncher.App/Views/WorldsView.axaml.cs:77-90`：保存类型只有 `*.mrpack`，没有 zip。

**导出器实际打包的内容：**

- `N:src/NebulaLauncher.Core/Backups/ModpackExporter.cs:191-215`：只扫根 `mods/`，且只认 `.jar` / `.jar.disabled`；`:281-288` 把 `.jar.disabled` 改回 `.jar` 写进包——导出会把作者禁用的模组重新启用，且 `.old` 完全不进包。
- `:217-266`：只按 4 个整目录收集 `config/`、`resourcepacks/`、`shaderpacks/`、`saves/`；`:290-295` 只排除 `logs/`、`crash-reports/`；没有 `options.txt`、`defaultconfigs/`、`servers.dat`、`screenshots/`、`schematics/`、`coremods/`、`lib/`，也没有按存档/资源包/光影包逐项选择。PCL 的 Mod 设置规则还排除 `config/jei/world/`、`config/worldedit/` 等个人信息（`P:Pages/PageInstance/PageInstanceExport.xaml:106`），Nebula 会整目录打包。
- `:299-307`：在线哈希只处理 `mods/`；配置、资源包、光影、存档全部走 `overrides/`。
- `:449-457`：`dependencies` 写 minecraft + 加载器；这部分与 PCL 一致。

**PCL 导出页包含什么：**

- `P:Pages/PageInstance/PageInstanceExport.xaml:40-46` 游戏本体；`:47-73` 游戏本体设置（`options.txt|configureddefaults/`）、个人信息（`hotbar.nbt|command_history.txt`）、OptiFine 设置；`:74-162` Mod 及已禁用 Mod、整合包重要数据、Mod 设置、TaCZ、沉浸画、JEI/EMI/帕秋莉个人信息；`:163-198` 资源包/光影包及每个包/设置的子项；`:199-254` 截图、结构、录像、单人存档（可逐世界选）、协议、服务器列表；`:255-280` 版本文件夹中的 Java、PCL 启动器程序及其个性化内容；`:283-305` 高级选项（打包资源文件、仅从 Modrinth 下载）与配置导入/导出。
- `P:Pages/PageInstance/PageInstanceExport.xaml.vb:54-85`：动态扫描 `resourcepacks`/`texturepacks`/`saves`/`shaderpacks` 下的每个 zip/文件夹，生成逐项复选框。
- `:323-407`：导出配置可保存/读取，配置里含 `RulesOverrides`（逐文件规则）、`ExtraFiles`（追加文件）、`PackPath`（导出路径）。
- `:446-452`：保存类型有 `zip` 与 `mrpack` 两种；`:461-470` 控制 Java、PCL、资源文件；`:538-571` 复制 Java、PCL 本体与个性化内容；`:704-716` 生成内层 `modpack.mrpack` + 外层 zip。
- 默认值：PCL 启动器程序默认勾选（`:262-268`），因此默认导出是可直接分发的 zip 整包，而不是裸 `.mrpack`。

**引用错误：**

- `N:src/NebulaLauncher.Core/Launching/LaunchScriptExporter.cs:5-17` 明确写的是「把一条完整启动命令落成可执行脚本」，`:21-64` 产出 `.bat`/`.sh`；它不是整合包导出，不能用来证明「多格式」。
- `AUDIT-2026-10-23.md:146` 原本写的是「部分」；`:171` 还把「`PageInstanceExport`→启动脚本/配置包」明确列为映射错误，并说应停止据此宣布零缺口。R35 恰好重复了这个被审计否定的映射。

**判定：** 「完整导出选项 / 多格式 → 已存在，不是缺口」是明显高估。准确结论是「有 `.mrpack` 导出，属于部分实现」；用户可感知的损失包括：键位/视频设置（`options.txt`）、`defaultconfigs/`、服务器列表、按存档/资源包/光影逐项选择、禁用模组的禁用态、`.zip` 分发格式、Java/启动器打包、导出配置复用。

### 2.5 第 5 条：其余历史明确不做清单

- `TAKEOVER.md:176-184` 的「明确不做的」实际列表是：皮肤系统、OptiFine/LiteLoader 安装、EasyTier、联机、启动器自更新、`java_launch_wrapper`/LWJGL Unsafe Agent、主题贴图/音乐对决、base64 依赖注入、DES 硬件指纹、分片下载阶梯并发、公告、中文搜索词表、内存优化、i18n、UA 伪装、`launcher_profiles.json`、本地 Mod「查看更新日志」外链、登录跳过 SSL、macOS 系统能力。
- 该列表**没有 Nide**，也**没有自定义主页事件 / `ModEvent`**。
- `AUDIT-2026-10-23.md:143` 对 Nide 的判断是「未完整实现；通用 Yggdrasil/Authlib 能力不能直接等同专用地址、注册/角色交互与皮肤流程」；`:147` 对自定义主页事件体系写的是「通用 MVVM ICommand 不是 PCL ModEvent 的自定义内容/事件格式兼容；未发现完整同等用户功能，原『框架等价』证据不足」。
- 历史上确实有过相反判断：`docs/HANDOVER-2026-10-23-R30.md:45-51` 判 `ModEvent.vb`「不是缺口」，理由是用 `ICommand` 等价；`docs/HANDOVER-2026-10-23-R32.md:25` 把 Nide 标为「判死」。但 AUDIT 是更晚的现状核查，并且明确判定「ICommand 等价」这条证据不足；R35 用「沿用历史裁决」把 AUDIT 的未决项直接关闭，没有给出源码依据。
- R35 行内还漏了 `AUDIT` §5 的第 10 项「本地模组完整更新日志入口、内置中文搜索词表」；这一项在 `TAKEOVER.md:180,182` 有历史依据，应该显式列名，而不是混在「其余」里。

**判定：** 不能把这一行整体写成「沿用 TAKEOVER §5」。应拆成「有历史依据的维持不做」与「需要重新裁决 / 仍属未决」两组；Nide 与自定义主页事件至少要把 AUDIT 的反证写进去，否则就是无依据关闭。

## 3. 需要修正的最小能力与落点

### 3.1 第 1 条：补「加载器版本回滚 + 当前版本显示」

1. `VersionsViewModel.LoaderSwitchCurrentText`（`:3094-3096`）加上当前加载器 build，例如「当前 1.20.1 · Forge 47.2.0」，让面板本身能承担「旧 → 新」对比的一半。
2. 新增「恢复当前版本 / 撤销更改」命令：调用 `SwitchLoaderAsync(id, row.Info.Loader, row.Info.LoaderVersion)`；Core 已支持指定版本（`InstanceService.Loader.cs:64-174`、`InstallService.cs:746-754`），不需要改 Core。
3. Forge 不必等远程列表：把 `row.Info.LoaderVersion` 作为合成选项塞进 `LoaderSwitchVersions`，或在命令里直接传旧 build；这样能绕过 `VersionService.cs:304-321` 只返回推荐版的限制。
4. 测试落点：`tests/NebulaLauncher.Tests/CoreInstances_LoaderSwitchTests.cs` 增加「Forge 47.2.0 → 47.3.0 → 恢复 47.2.0」用例；VM 层加一条命令复位用例。
5. 完整的 OptiFine/LiteLoader 组件面板、跨 MC 版本原地修改可以继续不做；但不能再以「等价于换回原版」作为理由。

### 3.2 第 4 条：先把「部分实现」写清楚，再补最影响可迁移性的几项

1. 新增 `IncludeGameSettings`（`options.txt` + `defaultconfigs/`）与 `IncludeServers`（`servers.dat`），在 `ModpackExportOptions` / `ModpackExporter.CollectOverrideFiles` / `WorldsViewModel.cs:1054-1064` / `WorldsView.axaml:163-178` 四处接线；这是当前导出最容易丢的用户数据。
2. 保持禁用态：`CollectModFiles` 不要把 `.jar.disabled` 改成 `.jar`；要么原样写回 `.disabled`，要么增加「包含已禁用 Mod」开关（PCL 是后者，默认不勾）。
3. 逐项选择（每个存档/资源包/光影包）与 `.zip` 输出先明确标为「未实现」，不要写「完整导出已存在」；如果要做，`ModpackExporter` 需要接收选择列表并增加 zip 写出路径。
4. `Author` 与 `MinecraftVersion` 目前没有 UI：要么补输入，要么从「六项」描述里删掉，避免把模型字段当成用户能力。

### 3.3 第 3 条：把结论收窄，或补一个不依赖多游戏目录的小切片

1. 最小切片：`ModpackService.DetectFormat`（`:519-547`）增加根/一级的 `modpack.zip` / `modpack.mrpack` 键；把内层 zip 解到临时文件后递归 `ImportWithResultAsync`，忽略包内启动器与外层 `.minecraft`。这条路径不需要「空文件夹 + 多游戏目录」。
2. 若不做切片，至少不要静默落到 `PlainZip`：在 `ImportWithResultAsync` 检测到外层包含 `modpack.*` 时给出明确提示（「这是带启动器的整包，请解压后导入内层 modpack.*」），不要创建 `VersionName` 为空的实例。
3. 结论文字改为「不做带启动器的整包目录迁移；外层包内嵌 modpack 的递归导入另议」，删掉「普通压缩包链已覆盖」。

### 3.4 第 5 条：把引用改成真实来源，再决定 Nide / 自定义主页事件

1. 把单行拆成「能力 / 历史来源 / 现状」三列；皮肤、OptiFine、EasyTier、分片、i18n、公告、UA、macOS 等直接引 `TAKEOVER.md:176-184`。
2. Nide：引 `P:Pages/PageLaunch/PageLoginNide.xaml.vb:29-36,72-77`（BaseUrl + 用户名/密码 + 注册/找回链接）与 Nebula 的 Authlib 账户能力；要么给出新的「可用通用 Authlib 覆盖」源码论证，要么标为「未决」，不能只写「沿用」。
3. 自定义主页事件：引 `P:Modules/ModEvent.vb` 与 `HANDOVER-2026-10-23-R30.md:45-51` 的历史判断，同时写明 AUDIT §5 的反证；在重新裁决前保持「未决」。
4. 把 AUDIT §5 第 10 项「更新日志 / 中文词表」补进清单，引 `TAKEOVER.md:180,182`。

## 4. 本轮无法判定

- **Forge 旧构建的真实安装链路**：`InstallService.cs:746-754` 与 `CoreJava_LoaderLimitWiringTests.cs:95-103` 证明后端支持指定 build，但测试用的是 `StubInstallService`，没有真机装过非推荐版 Forge；实际网络/安装器行为未验证。
- **切换到 Vanilla 的真机行为**：`InstanceService.Loader.cs:20-174` 的 Vanilla 分支代码存在，但 `CoreInstances_LoaderSwitchTests.cs` 没有 Vanilla 用例，R35 的真机回归也只覆盖原版 1.20.1 启动，不是「Forge → Vanilla」。
- **Nide 是否真能被「Authlib 账户 + BaseUrl」完整覆盖**：AUDIT 说不能等同，r29 说可以；没有可用的 Nide 服务端做登录/注册/角色/皮肤实测，无法给确定结论。
- **带启动器整包的优先级与存量**：没有统计数据说明多少用户会用 PCL 默认导出的 zip 迁移；这是产品取舍，不是源码能判定的。
- **`.mrpack` 产物在 PCL/HMCL/Modrinth App 的互操作**：本轮没有拿导出产物做三方导入实测；只能确认格式字段与现有测试，不确认所有消费端行为。
- **逐项导出选择是否为用户刚需**：PCL 有该能力，但 Nebula 用户是否需要按存档/资源包逐项勾选，属于产品判断；本轮只确认「当前不存在」，不判断「必须做」。
