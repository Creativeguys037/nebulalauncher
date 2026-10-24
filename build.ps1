param(
    [switch]$Test,
    [switch]$Clean,
    [switch]$Publish,

    # 跳过发布产物的两道自检（--probe-dir / --screenshot）。只在你明确知道产物可用、
    # 或者环境跑不了 GUI（无桌面）时使用；日常发布不要加。
    [switch]$SkipVerify,

    # 只跳过「git tag 与仓库级版本号一致」这一道发布前置校验，其余发布自检（产物 --probe-dir /
    # --screenshot）照常执行。用于还没打 tag 的本地试发布；正式发版不要加。
    [switch]$AllowUntagged,
    [ValidateSet('minimal', 'fdd', 'portable', 'folder')]
    [string]$Mode = 'minimal',
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',

    # 覆盖发布目录。默认 pub\（仓库根下，发布前会整体删除重建）；
    # 该目录下的 exe 正在运行时无法覆盖，用这个参数发布到新目录，
    # 避免要求用户先关掉启动器。切换 -Mode 时建议用 -OutDir pub\<Mode> 分开，否则互相覆盖。
    [string]$OutDir = ''
)

# Nebula Launcher 构建脚本。
#
# 发布形态（win-x64；体积随运行时版本与构建机变化，不在这里写死，实测值见
# docs/release-notes/v1.0.1.md 与本脚本 -Publish 时打印的 [build] <模式> -> <目录> (<N> MB, <M> 个文件)）：
#   fdd       几十 MB，数十个文件。框架依赖。
#                      双击即用、无解压步骤、启动快；目标机需要 .NET 8 Desktop Runtime
#                      （Windows 上很常见，启动器也会在缺失时提示）。
#   folder    近百 MB，两百多个文件。自包含不裁剪，最稳，任何机器双击即用。
#   portable  二十多 MB，1 个 exe。自包含 + 裁剪 + 压缩单文件。
#                      首次启动要把原生库解压到 %TEMP%\.net\<app>；
#                      该目录被安全软件拦截时启动会直接失败（实测本机就是这种情况），
#                      此时要么改用 fdd/folder，要么设 DOTNET_BUNDLE_EXTRACT_BASE_DIR。
#
# 为什么不做到 PCL(17MB)/HMCL(18MB) 那种量级：它们是「不携带运行时」——
# PCL 用系统自带的 .NET Framework 4.8，HMCL 依赖用户已有的 Java。
# .NET 8 不在 Windows 自带组件里，所以自包含必然多出几十 MB 运行时。
#
# 发布自检（原理与取舍见 CONVENTIONS.md §8.5 与 §8.6）：
#   - 发布前置：HEAD 上必须存在精确匹配 v<Version> 的 tag（git describe --tags --exact-match）；
#     Version 取自 Directory.Build.props。对不上直接失败，且不动旧发布目录。-AllowUntagged 只跳过这一道。
#   - 前置：obj\<Configuration> 里的 XAML 中间产物比 *.axaml 源旧时，只清 XAML 相关产物再构建。
#     不删整个 obj —— 那会让 publish 重走 restore + trim，本机实测在 ILLink 阶段失败。
#   - 后置：产物跑 --probe-dir（断言 writable=YES）与 --screenshot（20 秒内退出且 PNG > 10KB）。
#     两道关堵的都是「exit 0 但启动器起不来」：未清理 obj 时 publish 出的 exe 会静默退出
#     （退出码 0、无窗口、不产 PNG），只看退出码会把坏产物当成功。
#   - 自检失败会以非 0 退出码结束，并打印哪一关失败、实际看到什么。

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. "$root\env.ps1"

$appProjectDir = Join-Path $root 'src\NebulaLauncher.App'
$appProject = Join-Path $appProjectDir 'NebulaLauncher.App.csproj'

# 中间程序集名（csproj 的 AssemblyName）。XAML IL 注入就落在 obj 下的这一份 dll 上。
$appAssembly = 'NebulaLauncher'

# ---- 前置自检：obj\<Configuration> 里的 XAML 中间产物是不是比 XAML 源旧 ----
#
# 判据：obj 下所有 Avalonia\ 目录（Avalonia 的 XAML 编译产物集：references / resources /
# Resources.Inputs.cache）里最新的文件时间，与项目下所有 *.axaml 的最新修改时间比较。
# 中间产物更旧就说明这份 obj 没反映当前 XAML，publish 会产出静默不启动的 exe（见文件头）。
function Get-XamlSourceTime {
    $sources = @(Get-ChildItem -Path $appProjectDir -Recurse -File -Filter '*.axaml' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })

    if ($sources.Count -eq 0) { return $null }
    return ($sources | Measure-Object -Property LastWriteTime -Maximum).Maximum
}

function Clear-StaleXamlIntermediates {
    param([switch]$Force)

    $objConfig = Join-Path $appProjectDir (Join-Path 'obj' $Configuration)
    if (-not (Test-Path $objConfig)) {
        Write-Host "[build] obj\$Configuration 不存在，跳过 XAML 过期检查" -ForegroundColor DarkGray
        return
    }

    $sourceTime = Get-XamlSourceTime
    if ($null -eq $sourceTime) { return }

    $xamlDirs = @(Get-ChildItem -Path $objConfig -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'Avalonia' })
    if ($xamlDirs.Count -eq 0) { return }

    if ($Force) {
        # 发布模式无条件重建：时间戳新 ≠ XAML IL 已注入。msbuild 因文件占用 / 中断写出的
        # 中间程序集可能带新时间戳却缺预编译 XAML，publish 出来就是「No precompiled XAML found」
        # 的静默死产物（POST 自检能抓到，但不如从根上不产出）。多花几秒换可启动。
        Write-Host "[build] 发布模式：强制重建 XAML 中间产物" -ForegroundColor DarkGray

        foreach ($dir in $xamlDirs) {
            Remove-Item $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue

            # XAML IL 注入在中间程序集里：只删 Avalonia 目录会让重跑的 XAML 编译往旧 dll 上再注入一遍。
            $assembly = Join-Path (Split-Path -Parent $dir.FullName) "$appAssembly.dll"
            if (Test-Path $assembly) { Remove-Item $assembly -Force -ErrorAction SilentlyContinue }
        }

        return
    }

    # 容差 2 秒：文件系统时间戳精度与构建读写抖动（与安装清单的既有 mtime 容差一致）。
    $tolerance = [TimeSpan]::FromSeconds(2)
    $stale = @()
    $artifactTime = $null

    foreach ($dir in $xamlDirs) {
        $files = @(Get-ChildItem -Path $dir.FullName -Recurse -File -ErrorAction SilentlyContinue)
        $newest = $null
        if ($files.Count -gt 0) {
            $newest = ($files | Measure-Object -Property LastWriteTime -Maximum).Maximum
            if ($null -eq $artifactTime -or $newest -gt $artifactTime) { $artifactTime = $newest }
        }

        if ($null -eq $newest -or $newest -lt $sourceTime - $tolerance) {
            $stale += [pscustomobject]@{ Path = $dir.FullName; Time = $newest }
        }
    }

    if ($stale.Count -eq 0) {
        Write-Host ("[build] XAML 中间产物是最新的（源 {0:yyyy-MM-dd HH:mm:ss} / 中间 {1:yyyy-MM-dd HH:mm:ss}）" -f $sourceTime, $artifactTime) -ForegroundColor DarkGray
        return
    }

    Write-Host ("[build] XAML 中间产物比源旧（源 {0:yyyy-MM-dd HH:mm:ss}），清理 {1} 处：" -f $sourceTime, $stale.Count) -ForegroundColor Yellow

    foreach ($item in $stale) {
        $when = if ($null -eq $item.Time) { '空目录' } else { $item.Time.ToString('yyyy-MM-dd HH:mm:ss') }
        Write-Host "  - $($item.Path)（$when）" -ForegroundColor DarkGray
        Remove-Item $item.Path -Recurse -Force -ErrorAction SilentlyContinue

        # XAML IL 是注入到中间程序集里的：只删 Avalonia 目录、留下 dll 时，
        # 重跑的 XAML 编译会往旧 dll 上再注入一遍。删掉 dll 让 CoreCompile 一起重跑。
        $assembly = Join-Path (Split-Path -Parent $item.Path) "$appAssembly.dll"
        if (Test-Path $assembly) { Remove-Item $assembly -Force -ErrorAction SilentlyContinue }
    }
}

# ---- 后置自检：产物能不能执行（--probe-dir）、JSON 通路通不通（--selftest-json）、能不能渲染（--screenshot） ----
function Stop-Verify {
    param([string]$Stage, [string[]]$Details)

    Write-Host "[build] 自检失败 [$Stage]" -ForegroundColor Red
    foreach ($line in $Details) {
        if ($null -ne $line -and $line -ne '') { Write-Host "  $line" -ForegroundColor Red }
    }

    throw "自检失败：$Stage"
}

function Invoke-PublishVerify {
    param([string]$OutDir)

    $exe = Join-Path $OutDir 'NebulaLauncher.exe'
    if (-not (Test-Path $exe)) {
        Stop-Verify 'probe-dir' @("找不到产物：$exe")
    }

    # portable 是压缩单文件，运行时要先解包到 DOTNET_BUNDLE_EXTRACT_BASE_DIR（默认 %TEMP%\.net）。
    # 本机安全软件会拦这个 exe 写临时目录，表现是第 1 关直接报
    # "Failed to create directory [C:\...\Temp\.net\NebulaLauncher\<pid>] for extracting bundled files"，
    # 与产物好坏无关（同一份产物手工设这个变量重跑即通过）。
    # 自检把解包目录钉在产物目录里，判定结果就只反映产物本身。
    $bundleDir = Join-Path $OutDir '.bundle'
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleDir

    # ---- 第 1 关：--probe-dir，断言 writable=YES ----
    Write-Host '[build] 自检 1/3：--probe-dir' -ForegroundColor DarkGray

    # 应用会把探测结果同时写进 exe 同级的 nebula-probe-dir.txt（无控制台时的落点），
    # 两路都读：stdout 重定向在 WinExe 下不保证有内容。
    $probeTxt = Join-Path $OutDir 'nebula-probe-dir.txt'
    if (Test-Path $probeTxt) { Remove-Item $probeTxt -Force -ErrorAction SilentlyContinue }
    $stdout = Join-Path $OutDir '.probe.stdout.txt'
    $stderr = Join-Path $OutDir '.probe.stderr.txt'
    Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue

    $proc = Start-Process -FilePath $exe -ArgumentList @('--probe-dir', "`"$OutDir`"") -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    if (-not $proc.WaitForExit(30000)) {
        try { $proc.Kill() } catch { }
        Stop-Verify 'probe-dir' @('30 秒内没有退出（进程挂住），已强制结束')
    }

    $code = $proc.ExitCode
    $output = @()
    foreach ($path in @($probeTxt, $stdout, $stderr)) {
        if (Test-Path $path) {
            $text = Get-Content $path -Raw -ErrorAction SilentlyContinue
            if (-not [string]::IsNullOrWhiteSpace($text)) { $output += $text.Trim() }
        }
    }

    $outputText = ($output -join "`n")
    if ([string]::IsNullOrWhiteSpace($outputText)) {
        $outputText = '(stdout / stderr / nebula-probe-dir.txt 三处都没有内容)'
    }

    if ($code -ne 0) {
        Stop-Verify 'probe-dir' @("exit code=$code（期望 0）", '实际输出：', $outputText)
    }
    if ($outputText -notmatch 'writable=YES') {
        Stop-Verify 'probe-dir' @("输出里没有 writable=YES", '实际输出：', $outputText)
    }

    Write-Host '[build] 自检 1/3 通过：writable=YES' -ForegroundColor Green
    # ---- 第 2 关：--selftest-json，断言所有 JSON 落盘通路都通过 ----
    #
    # 堵的是 #26：PublishTrimmed=true 关掉 System.Text.Json 的反射元数据后，
    # settings.json / servers.json / 实例 / java-paths 这些写入路径会失败，
    # 而第 1 关（--probe-dir）和第 3 关（--screenshot）都不碰序列化，坏产物照样能过。
    Write-Host '[build] 自检 2/3：--selftest-json' -ForegroundColor DarkGray

    # 与应用自己的落盘文件同样读两路：WinExe 下 stdout 重定向不保证有内容。
    $selfTxt = Join-Path $OutDir 'nebula-selftest-json.txt'
    if (Test-Path $selfTxt) { Remove-Item $selfTxt -Force -ErrorAction SilentlyContinue }
    $selfOut = Join-Path $OutDir '.selftest.stdout.txt'
    $selfErr = Join-Path $OutDir '.selftest.stderr.txt'
    Remove-Item $selfOut, $selfErr -Force -ErrorAction SilentlyContinue

    $proc = Start-Process -FilePath $exe -ArgumentList @('--selftest-json') -PassThru `
        -RedirectStandardOutput $selfOut -RedirectStandardError $selfErr

    if (-not $proc.WaitForExit(120000)) {
        try { $proc.Kill() } catch { }
        Stop-Verify 'selftest-json' @('120 秒内没有退出（进程挂住），已强制结束')
    }

    $code = $proc.ExitCode
    $selfLines = @()
    foreach ($path in @($selfTxt, $selfOut, $selfErr)) {
        if (Test-Path $path) {
            $text = Get-Content $path -Raw -ErrorAction SilentlyContinue
            if (-not [string]::IsNullOrWhiteSpace($text)) { $selfLines += $text.Trim() }
        }
    }

    $selfText = ($selfLines -join "`n")
    if ([string]::IsNullOrWhiteSpace($selfText)) {
        $selfText = '(stdout / stderr / nebula-selftest-json.txt 三处都没有内容)'
    }

    if ($code -ne 0) {
        Stop-Verify 'selftest-json' @("exit code=$code（期望 0）", '实际输出：', $selfText)
    }
    if ($selfText -match 'json-selfcheck\.FAIL') {
        Stop-Verify 'selftest-json' @('有 JSON 通路失败：', $selfText)
    }
    if ($selfText -notmatch 'json-selfcheck\.summary=(\d+)/\1') {
        Stop-Verify 'selftest-json' @('没有 summary 行，或 passed 与 total 不等：', $selfText)
    }

    $summaryLine = ($selfText -split "`n" | Where-Object { $_ -match 'json-selfcheck\.summary=' } | Select-Object -First 1)
    Write-Host "[build] 自检 2/3 通过：$summaryLine" -ForegroundColor Green

    # ---- 第 2 关：--screenshot，断言 20 秒内退出且 PNG > 10KB ----
    #
    # 坏产物的特征是「exit 0、无窗口、不产图」；截图模式是唯一能证明 Avalonia 真的起来了的通道。
    # 截图失败时应用会写 <png>.error.txt 后照样以 0 退出（App.axaml.cs），所以 PNG 缺失必须单独判。
    #
    # 截图本身有偶发失败：本机实测约一次 0xE0434352（.NET 未处理异常，日志里没有错误行），
    # 同一份产物重跑即成功（历史交接记录里记着这条）。坏产物则会稳定失败，所以重试不会放过真问题。
    Write-Host '[build] 自检 3/3：--screenshot（20 秒超时）' -ForegroundColor DarkGray

    $dataDir = Join-Path $OutDir '.verify'

    # 截图与诊断文件都落在产物目录，不写 %TEMP%：本机安全软件会拦这个 exe 写临时目录
    # （Program.TryProbeDirectory 的注释记着同一条），表现出来就是截图阶段 0xE0434352。
    $png = Join-Path $OutDir '.verify.png'
    $pngError = "$png.error.txt"
    $shotOut = Join-Path $OutDir '.verify.stdout.txt'
    $shotErr = Join-Path $OutDir '.verify.stderr.txt'

    $attempts = 2
    $failure = $null
    $bytes = 0

    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        Remove-Item $png, $pngError, $shotOut, $shotErr -Force -ErrorAction SilentlyContinue

        # 每次都从空 data-dir 开始：第二次启动会读到上一次写下的 settings，掩盖首启问题。
        if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue }

        $proc = Start-Process -FilePath $exe -PassThru -ArgumentList @(
            '--data-dir', "`"$dataDir`"",
            '--screenshot', "`"$png`"",
            '--page', 'home') `
            -RedirectStandardOutput $shotOut -RedirectStandardError $shotErr

        if (-not $proc.WaitForExit(20000)) {
            try { $proc.Kill() } catch { }
            $failure = [string[]]@('20 秒内没有退出（超时），已强制结束', "PNG: $png")
            Write-Host "[build] 第 $attempt 次截图：20 秒超时，已强制结束" -ForegroundColor Yellow
            continue
        }

        $code = $proc.ExitCode
        if ((Test-Path $png) -and ((Get-Item $png).Length -gt 10240) -and $code -eq 0) {
            $bytes = (Get-Item $png).Length
            $failure = $null
            break
        }

        $failure = @()
        if (-not (Test-Path $png)) {
            $failure += "没有产出 PNG：$png"
        }
        else {
            $failure += "PNG 只有 $((Get-Item $png).Length) 字节（要求 > 10240）"
        }
        $failure += "exit code=$code（期望 0）"

        if (Test-Path $pngError) {
            $failure += @('应用自己记下的错误（<png>.error.txt）：', (Get-Content $pngError -Raw).Trim())
        }

        if (Test-Path $shotErr) {
            $errText = Get-Content $shotErr -Raw -ErrorAction SilentlyContinue
            if (-not [string]::IsNullOrWhiteSpace($errText)) { $failure += @('stderr：', $errText.Trim()) }
        }

        if ($attempt -lt $attempts) {
            Write-Host "[build] 第 $attempt 次截图失败（exit code=$code），重试一次" -ForegroundColor Yellow
        }
    }

    if ($null -ne $failure) {
        Stop-Verify 'screenshot' $failure
    }

    Write-Host "[build] 自检 3/3 通过：PNG $bytes 字节" -ForegroundColor Green

    # 成功时清掉验证残留：发布目录要能直接分发，不带 .verify / 截图 / 日志 / 探测输出。
    # 失败路径不清理（Stop-Verify 之前就 throw 了），现场留给排查。
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $bundleDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $png, $pngError, $shotOut, $shotErr, $probeTxt, $stdout, $stderr, $selfTxt, $selfOut, $selfErr -Force -ErrorAction SilentlyContinue
    Write-Host '[build] 已清理验证残留（失败时这些文件会保留，便于排查）' -ForegroundColor DarkGray
}

# ---- 发布前置：git tag 必须与仓库级版本号一致 ----
#
# 堵的是「打的 tag 和产物里报的版本对不上」：产物里的版本来自 Directory.Build.props 的 <Version>，
# tag 来自发布者手动敲的 `git tag -a`，两边没有任何东西保证一致。发布前在这里比对，对不上就不发布。
# 必须在删除旧发布目录之前跑：tag 打错时旧产物要原样留着。
function Assert-ReleaseTag {
    param([switch]$Skip)

    $propsPath = Join-Path $root 'Directory.Build.props'
    if (-not (Test-Path $propsPath)) {
        throw "找不到仓库级版本来源：$propsPath，无法校验发布 tag"
    }

    $version = $null
    try {
        [xml]$props = Get-Content -Path $propsPath -Raw -Encoding utf8
    }
    catch {
        throw "无法解析 $propsPath（读 <Version>）：$($_.Exception.Message)"
    }

    # PropertyGroup 是数组（文件里有多个），取第一个带非空 <Version> 的。
    foreach ($group in @($props.Project.PropertyGroup)) {
        if ($null -eq $group) { continue }
        $candidate = [string]$group.Version
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $version = $candidate.Trim()
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "在 $propsPath 里读不到非空的 <Version>，无法校验发布 tag"
    }

    $expectedTag = "v$version"

    if ($Skip) {
        Write-Host "[build] 已跳过 tag 校验（-AllowUntagged）：期望 tag 为 $expectedTag；产物自检照常执行" -ForegroundColor Yellow
        return
    }

    # 用退出码判断结果：外部命令的非 0 退出不会抛异常。关掉「原生命令错误即抛」的偏好变量，
    # 免得无 tag 的 128 被当成异常、丢掉期望 tag 这条提示。
    $PSNativeCommandUseErrorActionPreference = $false

    if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "找不到 git 命令，无法校验发布 tag。期望 tag: $expectedTag"
    }

    $describe = @(& git describe --tags --exact-match 2>$null)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "当前 HEAD 上没有精确匹配的 git tag（git describe --tags --exact-match 退出码 $exitCode）。期望 tag: $expectedTag"
    }

    $actualTag = if ($describe.Count -gt 0) { ([string]$describe[0]).Trim() } else { '' }
    if ($actualTag -ne $expectedTag) {
        throw "git tag 与仓库级版本号不一致：HEAD 上的 tag 是 '$actualTag'，期望 tag: $expectedTag（改 Directory.Build.props 的 <Version>，或按版本重打 tag）"
    }

    Write-Host "[build] tag 校验通过：$actualTag 与 <Version> $version 一致" -ForegroundColor Green
}

if ($Clean) {
    Get-ChildItem -Path $root -Recurse -Directory -Include bin, obj |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[build] 已清理 bin/obj" -ForegroundColor DarkGray
}

Push-Location $root
$buildFailed = $false
try {
    # 前置自检：obj 里的 XAML 中间产物比源旧时定点清掉（不删整个 obj，理由见 publish 段注释）。
    # 发布模式强制重建：时间戳新不代表 XAML IL 已注入，坏中间产物会产出「No precompiled XAML found」的 exe。
    Clear-StaleXamlIntermediates -Force:$Publish

    # publish 自身会构建，不需要先单独 build（单独 build 在本机会偶发撞 Core.dll 文件锁，
    # 那会把整个流程误判为失败）。只有非发布路径才显式构建。
    if (-not $Publish) {
        dotnet build $appProject -c $Configuration -v q --nologo
        if ($LASTEXITCODE -ne 0) { throw "构建失败 (exit $LASTEXITCODE)" }
    }

    if ($Test) {
        # 本机沙箱拒绝 MSBuild 多节点间的命名管道通信：项目图 ≥3 个时裸跑会「0 error 但生成失败」。
        # -m:1 与 BuildInParallel=false 一起用才能稳定跑完；Don'tWarnOnDirtyObj 只是压掉由此产生的噪音。
        dotnet run --project "$root\tools\TestRunner\TestRunner.csproj" -c $Configuration -v q --nologo -m:1 -p:BuildInParallel=false
        if ($LASTEXITCODE -ne 0) { throw "测试失败 (exit $LASTEXITCODE)" }
    }

    if ($Publish) {
        # 发布前置：tag 与版本对不上就到此为止，不删旧产物、不构建。
        Assert-ReleaseTag -Skip:$AllowUntagged

        $outDir = if ($OutDir) { $OutDir } else { "$root\pub" }
        $outDir = [System.IO.Path]::GetFullPath($outDir)

        if (Test-Path $outDir) {
            try {
                Remove-Item $outDir -Recurse -Force -ErrorAction Stop
            }
            catch {
                throw "无法清理发布目录：$outDir。`n" +
                      "该目录下的 NebulaLauncher.exe 可能正在运行（原生 dll 被占用）。`n" +
                      "关闭启动器后重试，或用 -OutDir 指定一个新目录。"
            }
        }

        # 注意：不要在这里删 obj/<Configuration>。删除会让 publish 重新走 restore + trim，
        # 在本机网络下容易失败（实测会在 ILLink 阶段以 exit 1 结束且不给可用产物）。
        # XAML IL 注入不全的问题（No precompiled XAML found）改由前面的
        # Clear-StaleXamlIntermediates 定点处理：只清 obj 下的 Avalonia\ 目录与中间程序集，
        # 不碰 restore / nuget 缓存。

        $common = @(
            'publish', $appProject,
            '-c', $Configuration, '-r', $Runtime, '-v', 'q', '--nologo', '-o', $outDir
        )

        switch ($Mode) {
            'minimal' {
                # 框架依赖 + 单文件（托管程序集打进 exe），原生库外置。
                # 3 个文件：托管单文件 exe 十几 MB + libSkiaSharp 9.0 MB + libHarfBuzzSharp 1.7 MB；
                # 2026-10-24 实测合计 25,926,442 字节（24.72 MiB）。
                # 不需要 %TEMP% 解压，双击即用。
                $publishArgs = $common + @(
                    '--self-contained', 'false',
                    '-p:PublishSingleFile=true',
                    '-p:IncludeNativeLibrariesForSelfExtract=false'
                )
            }
            'portable' {
                $publishArgs = $common + @(
                    '--self-contained', 'true',
                    '-p:PublishSingleFile=true',
                    '-p:PublishTrimmed=true',
                    '-p:TrimMode=partial',
                    '-p:EnableCompressionInSingleFile=true',
                    '-p:IncludeNativeLibrariesForSelfExtract=true'
                )
            }
            'fdd' {
                $publishArgs = $common + @('--self-contained', 'false')
            }
            'folder' {
                $publishArgs = $common + @('--self-contained', 'true', '-p:PublishSingleFile=false')
            }
        }

        dotnet @publishArgs

        if ($LASTEXITCODE -ne 0) { throw "发布失败 (exit $LASTEXITCODE)" }

        # 渲染已改为 WGL + Software（见 Program.BuildAvaloniaApp），ANGLE 的
        # av_libglesv2.dll（5.2 MB）不再需要，发布后直接剔除。
        Get-ChildItem $outDir -Recurse -Filter 'av_libglesv2*' -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

        $files = Get-ChildItem $outDir -Recurse -File
        $sizeMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
        Write-Host "[build] $Mode -> $outDir ($sizeMb MB, $($files.Count) 个文件)" -ForegroundColor Green
        Write-Host "[build] 运行: $(Join-Path $outDir 'NebulaLauncher.exe')" -ForegroundColor Green

        if ($Mode -eq 'portable') {
            Write-Host "[build] 注意：portable 需要 %TEMP%\.net\<app> 可写（发布自检不受此限，它把解包目录钉在产物目录）。被安全软件拦截时启用会失败，改用 -Mode fdd 或 -Mode folder。" -ForegroundColor DarkGray
        }
        if ($Mode -eq 'fdd') {
            Write-Host "[build] 目标机需要 .NET 8 Desktop Runtime。" -ForegroundColor DarkGray
        }

        # 产物自检放在统计之后：验证会往 outDir 里建 .verify 数据目录，不想污染上面的文件数统计。
        if ($SkipVerify) {
            Write-Host '[build] 已跳过产物自检（-SkipVerify）' -ForegroundColor Yellow
        }
        else {
            Invoke-PublishVerify -OutDir $outDir
        }
    }
}
catch {
    Write-Host "[build] 失败：$($_.Exception.Message)" -ForegroundColor Red
    $buildFailed = $true
}
finally {
    Pop-Location
}

if ($buildFailed) { exit 1 }
