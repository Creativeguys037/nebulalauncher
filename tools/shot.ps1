param(
    [string]$Out = 'D:\agent_workspace\.probe\shot.png',
    [string]$Page = 'home',
    [string]$Configuration = 'Debug',
    [int]$DelayMs = 4000,
    [string]$DataDir = '',
    [string]$Section = '',
    [string]$Size = '',
    [int]$Scroll = 0,
    [string]$Theme = '',
    [string]$Loader = '',
    [string]$McVersion = '',
    [switch]$InstanceEditor,
    [switch]$InstanceNotes,
    [switch]$SelectAllMods,
    [switch]$ShowHidden,
    [switch]$VersionComponents,
    [switch]$DownloadDemo,
    [switch]$DownloadDemoPaused,
    [switch]$LogFollow,
    [switch]$MissingJava,
    [switch]$MissingJavaVersions,
    [switch]$SourceProbe,
    [switch]$NoBuild
)

# Nebula Launcher 截图工具（应用内渲染）。
#
# 设计要点：
#   1. 不截系统屏幕 —— 窗口被遮挡/无焦点/多实例并行时都会抓到错误内容。
#      改由应用自己把视觉树渲染到 RenderTargetBitmap（--screenshot 模式），
#      与窗口可见性完全无关。
#   2. 不杀进程 —— 杀掉 dotnet 会连带杀掉队友正在运行/截图的实例。
#      截图进程自己结束后退出。
#   3. 数据目录隔离 —— 每个使用者用各自的 --data-dir，避免并发写坏
#      accounts.json / servers.json / settings.json。
#
# 用法：
#   .\tools\shot.ps1 -Page home -NoBuild -Configuration ui1 -DataDir D:\agent_workspace\.probe\data-ui1
#
# 页面键：home / versions / download / mods / accounts / servers / worlds / settings

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. "$root\env.ps1"

if (-not $NoBuild) {
    dotnet build "$root\src\NebulaLauncher.App\NebulaLauncher.App.csproj" -c $Configuration -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[shot] 构建失败 (Configuration=$Configuration)" -ForegroundColor Red
        exit 1
    }
}

$bin = "$root\src\NebulaLauncher.App\bin\$Configuration\net8.0"
if (-not (Test-Path "$bin\NebulaLauncher.dll")) {
    Write-Host "[shot] 找不到构建产物: $bin（先去掉 -NoBuild 跑一次）" -ForegroundColor Red
    exit 1
}

if (-not $DataDir) {
    $DataDir = Join-Path $root "artifacts\data-$Configuration"
}

New-Item -ItemType Directory -Path (Split-Path -Parent $Out) -Force | Out-Null
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
Remove-Item $Out -Force -ErrorAction SilentlyContinue
Remove-Item "$Out.error.txt" -Force -ErrorAction SilentlyContinue

$argList = @(
    'NebulaLauncher.dll',
    '--screenshot', $Out,
    '--page', $Page,
    '--delay', $DelayMs,
    '--data-dir', $DataDir
)

if ($Section) {
    $argList += @('--section', $Section)
}

if ($InstanceEditor) {
    $argList += '--instance-editor'
}

# 模组页：勾选全部内容。批量操作条在无选中时是禁用态，截图只能证明「条存在」。
if ($SelectAllMods) {
    $argList += '--select-all-mods'
}

# 版本页：展开第一个实例的完整备注浮层（只能点按钮出现）。
if ($InstanceNotes) {
    $argList += '--instance-notes'
}

# 版本页：打开「显示已隐藏」，验证隐藏行、行内徽标与「取消隐藏」按钮。
if ($ShowHidden) {
    $argList += '--show-hidden'
}

# 版本页：展开第一个版本的组件面板（只能点版本行的「组件…」出现）。
if ($VersionComponents) {
    $argList += '--version-components'
}

# 主题覆盖：dark / light / system。不传则用 settings.json 里的值。
if ($Theme) {
    $argList += @('--theme', $Theme)
}

# 下载页：预选加载器（原版 / Fabric / Quilt / Forge / NeoForge）与游戏版本。
# 这两个状态只能点击产生，没有参数就截不到「Forge 推荐版徽标」「Fabric API 开关」这类区块。
if ($Loader) {
    $argList += @('--loader', $Loader)
}

if ($McVersion) {
    $argList += @('--mc', $McVersion)
}

# 下载页：注入演示任务并把面板摆到「有活动批次」。
# 任务面板只有真实下载时才有行，截图模式不能点击也不能真的联网，没有这个开关
# 就截不到「暂停 / 继续」按钮与任务列表的真实布局。
if ($DownloadDemo) {
    $argList += '--download-demo'
}

# 与上一条配套：把演示批次停在「已暂停」，截状态行文案与排队行的「等待」。
if ($DownloadDemoPaused) {
    $argList += '--download-demo-paused'
}

# 日志页：写一份演示日志并真实跟随一次，截「跟随中」chip 与刚追加的新行。
if ($LogFollow) {
    $argList += '--log-follow'
}

# 首页：把页面推进「缺少 Java 25」的启动失败态，截「安装 Java N」按钮与进度行。
# 真实路径要跑一次启动并联网下载运行时，截图模式既不能点击也不该真下 50 MB。
if ($MissingJava) {
    $argList += '--missing-java'
}

# 版本页：同一件缺 Java 的事，但区块嵌在状态条下、页面更窄，按钮是否换行只能单独截一张。
if ($MissingJavaVersions) {
    $argList += '--missing-java-versions'
}

# 下载页：摆出一次「源测速」结论（官方 vs BMCLAPI 镜像）。
# 测速要真发网络请求且结果随当天网络变化，截图要可复现；文案仍由 Core 的排序纯函数生成。
if ($SourceProbe) {
    $argList += '--source-probe'
}

# 长页面下半部分：窗口高度受屏幕限制拉不高时，用纵向滚动偏移截首屏之外的内容。
if ($Scroll -gt 0) {
    $argList += @('--scroll', $Scroll)
}

# 截长页：窗口高度设大一些，让整页内容都在首帧可见（App 内渲染不受屏幕尺寸限制）。
if ($Size) {
    $argList += @('--size', $Size)
}

$errFile = Join-Path $env:TEMP ([System.IO.Path]::GetRandomFileName() + '.shot.err')
$proc = Start-Process -FilePath 'dotnet' -ArgumentList $argList `
    -WorkingDirectory $bin -PassThru -RedirectStandardError $errFile -WindowStyle Hidden

if (-not $proc.WaitForExit(90000)) {
    $proc.Kill()
    Write-Host "[shot] 超时未退出" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $Out)) {
    Write-Host "[shot] 未生成截图（退出码 $($proc.ExitCode)）" -ForegroundColor Red
    if (Test-Path "$Out.error.txt") { Get-Content "$Out.error.txt" -Raw }
    if (Test-Path $errFile) { Get-Content $errFile -Raw | Select-Object -First 20 }
    exit 1
}

$size = (Get-Item $Out).Length
Write-Host "[shot] $Page -> $Out ($([math]::Round($size / 1KB)) KB)  data=$DataDir" -ForegroundColor Green
