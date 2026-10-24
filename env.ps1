# 本机 .NET SDK 不在系统默认位置，所有构建命令前先 dot-source 本文件。
# 用法： . .\env.ps1 ;  然后 dotnet build

$sdkRoot = 'C:\Users\MECHREUO\.dotnet8'
if (-not (Test-Path (Join-Path $sdkRoot 'dotnet.exe'))) {
    Write-Host "[env] 找不到 SDK: $sdkRoot" -ForegroundColor Red
    return
}

$env:DOTNET_ROOT = $sdkRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
if ($env:PATH -notlike "*$sdkRoot*") {
    $env:PATH = "$sdkRoot;$env:PATH"
}

Write-Host "[env] dotnet $(dotnet --version) @ $sdkRoot" -ForegroundColor DarkGray
