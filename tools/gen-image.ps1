# 素材生成：调用火山方舟 Seedream 出图，供 UI 需要配图时使用。
#
# 为什么有这个脚本：界面需要背景图、空态插图、场景图时，外部素材站的授权与风格都不可控，
# 占位色块又过不了视觉验收。这里用同一个模型、同一套参数在仓库内生成，素材可复现、可重生成。
#
# 密钥不进仓库：仓库是 public。来源优先级 —— -ApiKey 参数 > 环境变量 ARK_API_KEY > 仓库根 .local/ark.key
# （.local/ 已在 .gitignore 里）。把键写进代码、票面或提交历史都算泄露。
#
# 用法：
#   pwsh -NoProfile -File tools/gen-image.ps1 -Prompt "深色玻璃启动器图标，圆角方块，蓝红渐变" -Out .scratch/assets/icon.png
#   pwsh -NoProfile -File tools/gen-image.ps1 -Prompt "..." -Out src/NebulaLauncher.App/Assets/hero.png -Size 2K
#   pwsh -NoProfile -File tools/gen-image.ps1 -Prompt "..." -Out .scratch/assets/x.png -Size 1024x1024 -Watermark
#
# 退出码：0 成功；1 参数/密钥/接口/落盘失败。失败时打印接口原文，不要猜原因。

[CmdletBinding()]
param(
    # 画面描述（中文可直接写）。
    [Parameter(Mandatory = $true)]
    [string]$Prompt,

    # 输出图片路径，后缀决定格式（.png / .jpg）。相对路径按当前目录解析。
    [Parameter(Mandatory = $true)]
    [string]$Out,

    [ValidateSet('1K', '2K', '4K', '1024x1024', '1280x720', '720x1280', '1536x1024', '1024x1536')]
    [string]$Size = '2K',

    [string]$Model = 'doubao-seedream-5-0-flash-260915',

    [string]$ApiKey,

    [switch]$Watermark,

    [ValidateSet('url', 'b64_json')]
    [string]$ResponseFormat = 'url',

    [int]$TimeoutSec = 180,

    # 只打印将要发送的请求，不真正调用（不花钱），用于核对参数与密钥来源。
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$endpoint = 'https://ark.cn-beijing.volces.com/api/v3/images/generations'

function Resolve-ArkKey {
    param([string]$Explicit)

    if ($Explicit) { return @{ Key = $Explicit; From = '-ApiKey 参数' } }

    if ($env:ARK_API_KEY) { return @{ Key = $env:ARK_API_KEY; From = '环境变量 ARK_API_KEY' } }

    $repoRoot = Split-Path -Parent $PSScriptRoot
    $keyFile = Join-Path $repoRoot '.local/ark.key'
    if (Test-Path -LiteralPath $keyFile) {
        $raw = (Get-Content -LiteralPath $keyFile -Raw).Trim()
        if ($raw) { return @{ Key = $raw; From = ".local/ark.key" } }
    }

    throw @'
找不到火山方舟 API Key。三选一：
  1) 传 -ApiKey <key>
  2) 设环境变量：$env:ARK_API_KEY = '<key>'
  3) 把 key 写进仓库根的 .local/ark.key（该目录已在 .gitignore 里，不会被提交）
'@
}

function Write-Fail {
    param([string]$Message)
    [Console]::Error.WriteLine($Message)
    exit 1
}

$resolved = try { Resolve-ArkKey -Explicit $ApiKey } catch { Write-Fail $_.Exception.Message }
if (-not $resolved.Key) { Write-Fail 'API Key 为空。' }

# 路径按仓库根解析，避免不同工作目录下把图写到奇怪位置。
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($Out)) {
    $Out = Join-Path (Get-Location).Path $Out
}
$outDir = Split-Path -Parent $Out
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

$payload = [ordered]@{
    model           = $Model
    prompt          = $Prompt
    response_format = $ResponseFormat
    size            = $Size
    stream          = $false
    watermark       = [bool]$Watermark
}
$json = $payload | ConvertTo-Json -Depth 5 -Compress

Write-Host "[gen-image] 模型：$Model"
Write-Host "[gen-image] 尺寸：$Size"
Write-Host "[gen-image] 密钥来源：$($resolved.From)"
Write-Host "[gen-image] 输出：$Out"

if ($DryRun) {
    Write-Host '[gen-image] DryRun：以下请求体不会发送'
    Write-Host $json
    exit 0
}

$headers = @{ Authorization = "Bearer $($resolved.Key)" }
# 显式送 UTF-8 字节：中文 prompt 走 -Body 字符串时容易被宿主按系统代码页编码，接口会当成乱码。
$bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($json)

$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $resp = Invoke-RestMethod -Uri $endpoint -Method Post -Headers $headers `
        -ContentType 'application/json; charset=utf-8' -Body $bodyBytes -TimeoutSec $TimeoutSec
}
catch {
    $detail = $_.Exception.Message
    if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = $_.ErrorDetails.Message }
    Write-Fail "接口调用失败：`n$detail"
}
$sw.Stop()

$item = $null
if ($resp.PSObject.Properties.Name -contains 'data' -and $resp.data -and $resp.data.Count -gt 0) {
    $item = $resp.data[0]
}
if (-not $item) { Write-Fail "接口返回里没有 data[0]：`n$($resp | ConvertTo-Json -Depth 6)" }

function Get-ImageFormat {
    param([byte[]]$Bytes)

    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xD8 -and $Bytes[2] -eq 0xFF) { return '.jpg' }
    if ($Bytes.Length -ge 8 -and $Bytes[0] -eq 0x89 -and $Bytes[1] -eq 0x50 -and $Bytes[2] -eq 0x4E -and $Bytes[3] -eq 0x47) { return '.png' }
    if ($Bytes.Length -ge 5 -and $Bytes[0] -eq 0x47 -and $Bytes[1] -eq 0x49 -and $Bytes[2] -eq 0x46) { return '.gif' }
    if ($Bytes.Length -ge 12 -and $Bytes[0] -eq 0x52 -and $Bytes[1] -eq 0x49 -and $Bytes[2] -eq 0x46 -and $Bytes[3] -eq 0x46 -and
        $Bytes[8] -eq 0x57 -and $Bytes[9] -eq 0x45 -and $Bytes[10] -eq 0x42 -and $Bytes[11] -eq 0x50) { return '.webp' }
    return $null
}

try {
    if ($item.PSObject.Properties.Name -contains 'b64_json' -and $item.b64_json) {
        [System.IO.File]::WriteAllBytes($Out, [Convert]::FromBase64String($item.b64_json))
    }
    elseif ($item.PSObject.Properties.Name -contains 'url' -and $item.url) {
        Invoke-WebRequest -Uri $item.url -OutFile $Out -TimeoutSec $TimeoutSec | Out-Null
    }
    else {
        Write-Fail "返回项里既没有 url 也没有 b64_json：`n$($item | ConvertTo-Json -Depth 6)"
    }
}
catch {
    Write-Fail "下载/落盘失败：`n$($_.Exception.Message)"
}

if (-not (Test-Path -LiteralPath $Out)) { Write-Fail "文件没有生成：$Out" }

# 接口返回的实际编码不受 size/后缀控制（实测 Seedream 回的是 JPEG，哪怕目标写 .png）。
# 扩展名写错会让后面读图的人按错误格式解析，所以按文件头纠正一次，并把最终路径说清楚。
$headBytes = [System.IO.File]::ReadAllBytes($Out)
$realExt = Get-ImageFormat -Bytes $headBytes[0..([Math]::Min(15, $headBytes.Length - 1))]
if (-not $realExt) { Write-Fail "生成的文件不是可识别的 PNG/JPEG/GIF/WebP：$Out" }

$wantedExt = [System.IO.Path]::GetExtension($Out).ToLowerInvariant()
if ($wantedExt -and $wantedExt -ne $realExt -and
    -not ($wantedExt -eq '.jpeg' -and $realExt -eq '.jpg')) {
    $corrected = [System.IO.Path]::ChangeExtension($Out, $realExt)
    Move-Item -LiteralPath $Out -Destination $corrected -Force
    Write-Host "[gen-image] 注意：接口返回的是 $($realExt.TrimStart('.').ToUpperInvariant())，已改名为 $corrected"
    $Out = $corrected
}
$len = (Get-Item -LiteralPath $Out).Length
if ($len -le 0) { Write-Fail "生成的文件是空的：$Out" }

Write-Host "[gen-image] 成功：$Out（$len 字节，耗时 $([math]::Round($sw.Elapsed.TotalSeconds,1))s）"
Write-Host '[gen-image] 下一步：亲眼看图（read_image），确认没有畸形、错字、多余水印，再放进页面。'
Write-Output $Out