param(
    [Parameter(Mandatory = $true)]
    [string]$DataDir,
    [switch]$Force
)

# 为隔离的数据目录灌入演示数据。
#
# 为什么需要：截图验证如果只看到空状态，就分不清「页面没做」和「本机没有数据」。
# 每个协作者用 --data-dir 时跑一次本脚本，版本页/模组页/世界页/账户页/服务器页
# 就都有内容可看，截图能真实反映布局。
#
# 生成内容：
#   <DataDir>/minecraft/versions/        4 个版本（2 原版 + Fabric + NeoForge）
#   <DataDir>/minecraft/mods/            6 个模组 jar（含 fabric.mod.json）
#   <DataDir>/minecraft/resourcepacks/   2 个资源包
#   <DataDir>/minecraft/shaderpacks/     2 个光影包
#   <DataDir>/minecraft/saves/           3 个世界目录（level.dat 的 Data.icon 带缩略图）
#   <DataDir>/accounts.json              2 个账户（离线 + 演示微软）
#   <DataDir>/servers.json               3 个服务器
#
# 用法：
#   .\tools\seed-demo-data.ps1 -DataDir D:\agent_workspace\.probe\data-ui1
#   .\tools\seed-demo-data.ps1 -DataDir D:\agent_workspace\.probe\data-ui1 -Force

$ErrorActionPreference = 'Stop'

if ((Test-Path $DataDir) -and -not $Force) {
    $existing = Get-ChildItem $DataDir -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "[seed] 目录已存在且非空：$DataDir（加 -Force 覆盖）" -ForegroundColor Yellow
        exit 0
    }
}

$mc = Join-Path $DataDir 'minecraft'
$versions = Join-Path $mc 'versions'
$mods = Join-Path $mc 'mods'
$resourcePacks = Join-Path $mc 'resourcepacks'
$shaderPacks = Join-Path $mc 'shaderpacks'
$saves = Join-Path $mc 'saves'

foreach ($d in @($DataDir, $mc, $versions, $mods, $resourcePacks, $shaderPacks, $saves,
                (Join-Path $DataDir 'logs'), (Join-Path $DataDir 'cache'),
                (Join-Path $DataDir 'backups'), (Join-Path $DataDir 'instances'))) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-JarWithFile {
    param([string]$Path, [string]$EntryName, [string]$Content)
    Remove-Item $Path -Force -ErrorAction SilentlyContinue
    $fs = [System.IO.File]::Create($Path)
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    $entry = $zip.CreateEntry($EntryName)
    $writer = New-Object System.IO.StreamWriter($entry.Open())
    $writer.Write($Content)
    $writer.Dispose()
    $zip.Dispose()
    $fs.Dispose()
}

# ---------- 版本 ----------
function New-Version {
    param([string]$Id, [string]$Type, [string]$Inherits, [string]$Time)
    $dir = Join-Path $versions $Id
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $obj = [ordered]@{
        id = $Id
        type = $Type
        time = $Time
        releaseTime = $Time
        libraries = @()
        mainClass = 'net.minecraft.client.main.Main'
    }
    if ($Inherits) { $obj.inheritsFrom = $Inherits }
    $json = $obj | ConvertTo-Json -Depth 5
    Set-Content -Path (Join-Path $dir "$Id.json") -Value $json -Encoding utf8
}

New-Version -Id '1.21.4' -Type 'release' -Inherits '' -Time '2025-03-11T10:00:00+00:00'
New-Version -Id '1.20.1' -Type 'release' -Inherits '' -Time '2023-06-12T12:00:00+00:00'
New-Version -Id 'fabric-loader-0.16.9-1.21.4' -Type 'release' -Inherits '1.21.4' -Time '2025-03-20T08:00:00+00:00'
New-Version -Id 'neoforge-21.4.50' -Type 'release' -Inherits '1.21.4' -Time '2025-04-02T09:00:00+00:00'

# ---------- 模组 ----------
$modSeed = @(
    @{ File = 'fabric-api-0.92.2.jar'; Json = @{ schemaVersion = 1; id = 'fabric-api'; name = 'Fabric API'; version = '0.92.2'; description = '核心 API，几乎所有 Fabric 模组都依赖它。'; authors = @('FabricMC') } },
    @{ File = 'sodium-0.5.8.jar'; Json = @{ schemaVersion = 1; id = 'sodium'; name = 'Sodium'; version = '0.5.8'; description = '重写渲染管线，显著提升帧率。'; authors = @('CaffeineMC') } },
    @{ File = 'lithium-0.12.1.jar'; Json = @{ schemaVersion = 1; id = 'lithium'; name = 'Lithium'; version = '0.12.1'; description = '优化游戏逻辑 tick，不改变行为。'; authors = @('Jellysquid') } },
    @{ File = 'iris-1.7.2.jar'; Json = @{ schemaVersion = 1; id = 'iris'; name = 'Iris Shaders'; version = '1.7.2'; description = '光影加载器，兼容 OptiFine 光影包。'; authors = @('IrisShaders') } },
    @{ File = 'modmenu-11.1.0.jar'; Json = @{ schemaVersion = 1; id = 'modmenu'; name = 'Mod Menu'; version = '11.1.0'; description = '游戏内模组列表与管理界面。'; authors = @('shedaniel') } },
    @{ File = 'xaeros-minimap-23.9.4.jar'; Json = @{ schemaVersion = 1; id = 'xaerominimap'; name = 'Xaero''s Minimap'; version = '23.9.4'; description = '小地图与路径点。'; authors = @('xaero96') } }
)

foreach ($m in $modSeed) {
    $json = ($m.Json | ConvertTo-Json -Depth 5)
    New-JarWithFile -Path (Join-Path $mods $m.File) -EntryName 'fabric.mod.json' -Content $json
}

# 一个禁用态模组，验证 .jar.disabled 的显示
New-JarWithFile -Path (Join-Path $mods 'old-api-0.41.0.jar.disabled') `
    -EntryName 'fabric.mod.json' `
    -Content (@{ schemaVersion = 1; id = 'oldapi'; name = 'Old API (已禁用)'; version = '0.41.0'; description = '用于验证禁用态渲染。'; authors = @('Demo') } | ConvertTo-Json -Depth 5)

# ---------- 资源包 / 光影 ----------
New-JarWithFile -Path (Join-Path $resourcePacks 'VanillaPlus-1.21.zip') -EntryName 'pack.mcmeta' `
    -Content (@{ pack = @{ pack_format = 46; description = 'Vanilla+ 细节增强' } } | ConvertTo-Json -Depth 5)

New-JarWithFile -Path (Join-Path $resourcePacks 'FreshAnimations-1.9.zip') -EntryName 'pack.mcmeta' `
    -Content (@{ pack = @{ pack_format = 46; description = '怪物动画重制' } } | ConvertTo-Json -Depth 5)

New-JarWithFile -Path (Join-Path $shaderPacks 'ComplementaryReimagined.zip') -EntryName 'shaders/shader.properties' `
    -Content "name=Complementary Reimagined`nversion=r5.3"

New-JarWithFile -Path (Join-Path $shaderPacks 'BSL-8.4.zip') -EntryName 'shaders/shader.properties' `
    -Content "name=BSL Shaders`nversion=8.4"

# ---------- 世界 ----------
# level.dat 是 NBT（大端序二进制）。手写最小结构：
#   root compound → Data compound → LevelName / Version{Name} / GameType
# 不压缩，NbtReader 对 gzip 与裸 NBT 都认。
function Write-NbtBeU16 {
    param($Writer, [int]$Value)
    $b = [BitConverter]::GetBytes([uint16]$Value)
    [Array]::Reverse($b)
    $Writer.Write($b)
}

function Write-NbtBeI32 {
    param($Writer, [int]$Value)
    $b = [BitConverter]::GetBytes([int32]$Value)
    [Array]::Reverse($b)
    $Writer.Write($b)
}

function Write-NbtString {
    param($Writer, [string]$Text)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    Write-NbtBeU16 $Writer $bytes.Length
    $Writer.Write($bytes)
}

function New-LevelDat {
    param([string]$Path, [string]$LevelName, [string]$VersionName, [int]$GameType, [string]$IconBase64 = '')

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([byte]0x0A); Write-NbtBeU16 $bw 0             # root compound（无名）
        $bw.Write([byte]0x0A); Write-NbtString $bw 'Data'       # Data compound
        $bw.Write([byte]0x08); Write-NbtString $bw 'LevelName'; Write-NbtString $bw $LevelName
        $bw.Write([byte]0x0A); Write-NbtString $bw 'Version'    # Version compound
        $bw.Write([byte]0x08); Write-NbtString $bw 'Name'; Write-NbtString $bw $VersionName
        $bw.Write([byte]0x00)                                    # end Version
        $bw.Write([byte]0x03); Write-NbtString $bw 'GameType'; Write-NbtBeI32 $bw $GameType
        if ($IconBase64) {
            # 真实 level.dat 的 Data.icon 就是 TAG_String，值是 base64 PNG；写在这里让世界页有缩略图可截。
            $bw.Write([byte]0x08); Write-NbtString $bw 'icon'; Write-NbtString $bw $IconBase64
        }
        $bw.Write([byte]0x00)                                    # end Data
        $bw.Write([byte]0x00)                                    # end root
    }
    finally {
        $bw.Dispose()
        $fs.Dispose()
    }
}

# 三个演示世界的 64×64 PNG 缩略图（斜条纹 + 右下角高光块，色调各不相同）。
# 内嵌 base64 而不是运行时读图片文件：seed 脚本要在任何机器上离线跑，图标是演示数据的一部分。
$iconBlue = 'iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAABGUlEQVR42u2a0Q2CQBBEl6nEBmzBBmzFAqzJfxqgEelEPkyMguBeTnIzydz3gzAcd+zLbXe9PSIiIvphjMQ4nw5UPJZXZu5Own8EyFwzeyvN+YgA2zOV8iB8piIenO81z4P220jyYP6+MzzI1+hPHvz7zDYPib1yg4fKfr/GQ+if9ZWH1n93yUOudpjxUKx/3nmI1nAvHrp16JPX9oF+GO0D9gH7gH3APmAfsA/YB+wD9gH7gH3APmAfsA+IZuiOl3usn3Vmzmsr+crR3gf+H0ArAxqeUe8bQCUDmvcK1AZg6HeonQHpDODpO6ldA6IZwNb/UxyAsIepeAakM4C2l6x4DWjVcBQ+sEspIZfBPmAfsA/YB+wDVWMCQmyIfm7hatYAAAAASUVORK5CYII='
$iconAmber = 'iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAABFElEQVR42u2awQ3CMBAEz1sKP5pILdRDWXlRAZQDD6QIHAhnmci70vo9ibJObN8oV+6XU0RExG2eIzGO00TFY31l5u4k/FuAzDXVrAznIwJsz9TKg/CZmnhwzmueB+23keTB/H1neJCv0Z88+PeZbR4Se+UGD5X9/hsPoTPrIw+tc3fNQ652qHgo1j+vPERruIWHbh365MtiZEkPqu47nLcP2AfsA/YB+4B9wD5gH7AP2AfsA/YB+4B9wD4gmqFcz4ex+0znGO8D/w+glQED/1HvG0AlA4b3CvQGYOh36H0D0hnA03fSuwZEM4Ct/6c5AGEPU/MbkM4A2l6y5jWgVcNR+MAupYRcBvuAfcA+YB+wD3SNB1mji3AJScxxAAAAAElFTkSuQmCC'
$iconGreen = 'iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAABNElEQVR42u2awQ2CQBBFh28dRLlxsAKPNmAnFmApdkHimSM9kJhw4GIZHkyIguCSlTA/+XsjeUvygV3mZSc5329mZmZFWVnAOB0PrngMZ4bc3Qn/ESBkTu+prM6b2eax3+VZ2l3nWVo37cSEumld8XD4XGfx8PlthPNw+30H8vC8RkN4ON9nfvLwv1dO86DY7yd4sPyzxngQ/Xe/8uCqHYY86OqfHg/GGu6dB2kd2vHgraVfPLcPFGUlH5APyAfkA/IB+YB8QD4gH5APyAfkA/IB+YB8gDRDsr1ebPysM+S8NpKPHOv7wP8DcGXAimfUywZgyYDVewViA3jod4h9A9QZ4KfvJHYNkGaAt/6f2QEc9jDNfgPUGeC2l2z2GuCq4Vz4wCKlBF0G+YB8QD4gH5APRI0nfZSpDKa0gMsAAAAASUVORK5CYII='

$worlds = @(
    @{ Name = '远古遗迹'; Mode = '生存'; GameType = 0; Version = '1.21.4'; Size = 5242880; Icon = $iconBlue },
    @{ Name = '创造实验场'; Mode = '创造'; GameType = 1; Version = '1.20.1'; Size = 900000; Icon = $iconAmber },
    @{ Name = 'Nebula 测试世界'; Mode = '生存'; GameType = 0; Version = '1.21.4'; Size = 2621440; Icon = $iconGreen }
)

foreach ($w in $worlds) {
    $wd = Join-Path $saves $w.Name
    New-Item -ItemType Directory -Path $wd -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $wd 'region') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $wd 'playerdata') -Force | Out-Null

    New-LevelDat -Path (Join-Path $wd 'level.dat') -LevelName $w.Name -VersionName $w.Version -GameType $w.GameType -IconBase64 $w.Icon

    # 填充到目标体积，让「大小」一列有真实差异
    $remaining = [int64]$w.Size
    $i = 0
    while ($remaining -gt 0) {
        $chunk = [Math]::Min($remaining, 262144)
        $bytes = New-Object byte[] $chunk
        [System.Random]::new($i).NextBytes($bytes)
        [System.IO.File]::WriteAllBytes((Join-Path $wd "region\r.$i.0.mca"), $bytes)
        $remaining -= $chunk
        $i++
    }
}

# ---------- 账户 ----------
$accounts = [ordered]@{
    activeAccountId = 'acc-offline-demo'
    accounts = @(
        [ordered]@{
            id = 'acc-offline-demo'
            type = 'Offline'
            username = 'NebulaPilot'
            uuid = '34f2c1a3d5e64b8fa0c2d7e1b9a83c45'
            skinModel = 'classic'
            lastLoginAt = (Get-Date).AddHours(-3).ToString('o')
        },
        [ordered]@{
            id = 'acc-offline-alt'
            type = 'Offline'
            username = 'Silent_Sky_'
            uuid = '069a79f444e94726a5befca90e38aaf5'
            skinModel = 'slim'
            lastLoginAt = (Get-Date).AddDays(-2).ToString('o')
        }
    )
}
$accounts | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $DataDir 'accounts.json') -Encoding utf8

# ---------- 服务器 ----------
$servers = @(
    [ordered]@{ id = 'srv-hypixel'; name = 'Hypixel'; host = 'mc.hypixel.net'; port = 25565; note = '全球最大小游戏服' },
    [ordered]@{ id = 'srv-local'; name = '本地创造服'; host = '127.0.0.1'; port = 25565; note = '本机开服测试用' },
    [ordered]@{ id = 'srv-demo'; name = '离线示例服务器'; host = 'play.example.com'; port = 25565; note = '演示离线状态' }
)
$servers | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $DataDir 'servers.json') -Encoding utf8

Write-Host "[seed] 已生成演示数据到 $DataDir" -ForegroundColor Green
Write-Host "       版本 4 / 模组 7 / 资源包 2 / 光影 2 / 世界 3 / 账户 2 / 服务器 3"
