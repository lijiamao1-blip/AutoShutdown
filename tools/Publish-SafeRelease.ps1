# Publish-SafeRelease.ps1
# AutoShutdown 安全测试版发布脚本（S12.4）
# 仅负责：备份已验收产物 -> Release 构建 -> 两种 win-x64 发布 -> 禁止文件检查 -> 打包 -> SHA-256/清单。
# 不修改任何源码；不写注册表；不启用开机自启动；不自动启动应用；不要求管理员权限。

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir   # tools 的上一级 = 项目根
$version = '1.0.0'

# ---- 1. 定位 .NET SDK：显式指定优先，回退 PATH ----
$knownSdk = $env:AUTOSHUTDOWN_DOTNET
$dotnet = $null
if ($knownSdk -and (Test-Path -LiteralPath $knownSdk)) {
    $dotnet = $knownSdk
} else {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}
if (-not $dotnet) {
    Write-Host '错误：未找到可用的 .NET SDK，请先安装或配置 SDK 路径。'
    exit 10
}
Write-Host "使用 SDK: $dotnet"
$sdkVersion = (& $dotnet --version)
Write-Host "SDK 版本: $sdkVersion"

# ---- 2. 发布目录（仅允许操作 artifacts\release\v<version> 下内容） ----
$releaseRoot = Join-Path $root "artifacts\release\v$version"
$stagingRoot = Join-Path $releaseRoot 'staging'
$packagesDir = Join-Path $releaseRoot 'packages'
$manifestsDir = Join-Path $releaseRoot 'manifests'
$backupsDir = Join-Path $releaseRoot 'backups'
$fdStage = Join-Path $stagingRoot 'win-x64-framework-dependent'
$scStage = Join-Path $stagingRoot 'win-x64-self-contained'

$allowedPrefix = [System.IO.Path]::GetFullPath($releaseRoot)
function Assert-AllowedPath([string]$path) {
    $full = [System.IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "错误：清理路径超出允许范围：$full"
        exit 11
    }
}

foreach ($d in @($stagingRoot, $packagesDir, $manifestsDir, $backupsDir)) {
    if (Test-Path -LiteralPath $d) {
        Assert-AllowedPath $d
        Remove-Item -Recurse -Force -LiteralPath $d
    }
}
New-Item -ItemType Directory -Force -Path $fdStage, $scStage, $packagesDir, $manifestsDir, $backupsDir | Out-Null

# ---- 3. 备份当前已验收的 Release 产物（构建前） ----
Write-Host '备份当前已验收 Release 产物...'
$binApp = Join-Path $root 'src\AutoShutdown.App\bin\Release\net8.0-windows'
$binCore = Join-Path $root 'src\AutoShutdown.Core\bin\Release\net8.0'
foreach ($src in @(
    (Join-Path $binApp 'AutoShutdown.App.exe'),
    (Join-Path $binApp 'AutoShutdown.App.dll'),
    (Join-Path $binApp 'AutoShutdown.App.deps.json'),
    (Join-Path $binApp 'AutoShutdown.App.runtimeconfig.json'),
    (Join-Path $binCore 'AutoShutdown.Core.dll'))) {
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination $backupsDir
    }
}
$backupHashes = Get-ChildItem -LiteralPath $backupsDir -File | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $($_.Name)"
}
$backupHashes | Set-Content -Encoding UTF8 (Join-Path $backupsDir 'SHA256SUMS.txt')
Write-Host "备份完成，共 $($backupHashes.Count) 个文件。"

# ---- 4. Release 构建（0 错误 0 警告门） ----
Write-Host '开始 Release 构建...'
& $dotnet build (Join-Path $root 'AutoShutdown.sln') -c Release --nologo -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) {
    Write-Host '错误：Release 构建失败，停止发布。'
    exit 20
}
Write-Host 'Release 构建通过。'

# ---- 5. 两种发布（禁用 Trim/SingleFile/ReadyToRun；不生成符号） ----
$commonArgs = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '--nologo',
    '-p:UseSharedCompilation=false',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)
$appProject = Join-Path $root 'src\AutoShutdown.App\AutoShutdown.App.csproj'

Write-Host '发布轻量版 (framework-dependent)...'
& $dotnet publish $appProject @commonArgs --self-contained false -o $fdStage
if ($LASTEXITCODE -ne 0) { Write-Host '错误：轻量版发布失败。'; exit 21 }

Write-Host '发布免运行库版 (self-contained)...'
& $dotnet publish $appProject @commonArgs --self-contained true -o $scStage
if ($LASTEXITCODE -ne 0) { Write-Host '错误：免运行库版发布失败。'; exit 22 }

# ---- 6. 禁止文件检查 ----
function Test-ForbiddenStage([string]$stage, [string]$label) {
    Write-Host "检查禁止文件: $label"
    $hits = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
        $n = $_.Name
        $n -like '*.pdb' -or $n -like '*.trx' -or $n -like '*.dmp' -or $n -like '*.diag' -or
        $n -like '*testhost*' -or $n -like '*xunit*' -or $n -like '*TestResults*' -or
        $n -like 's12-*' -or $n -like '*\.git*'
    }
    if ($hits) {
        Write-Host "错误：$label 包含禁止文件："
        $hits | ForEach-Object { Write-Host ('  ' + $_.FullName) }
        exit 30
    }
    return $hits.Count
}

$badText = @('D:\电脑定时关机重建完整版', 'C:\Users\', 'new-chat-5', '\.dotnet-sdk\', 'Codex')
function Test-TextLeak([string]$stage, [string]$label) {
    Write-Host "检查文本泄露: $label"
    # 只检查文本类文件；绝不读取二进制 DLL（-LiteralPath 下 -Include 不生效，故按扩展名过滤）。
    $files = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
        $_.Extension -in @('.json', '.config', '.txt', '.md', '.cmd', '.ps1')
    }
    foreach ($f in $files) {
        $content = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $content) { continue }
        foreach ($b in $badText) {
            if ($content.IndexOf($b, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                Write-Host "错误：$label 文本泄露 [$b] 于 $($f.FullName)"
                exit 31
            }
        }
    }
}

$fdForbidden = Test-ForbiddenStage $fdStage '轻量版'
$scForbidden = Test-ForbiddenStage $scStage '免运行库版'
Test-TextLeak $fdStage '轻量版'
Test-TextLeak $scStage '免运行库版'
Write-Host "禁止文件检查通过（轻量版命中 $fdForbidden，免运行库版命中 $scForbidden）。"

# ---- 7. 写入中文说明与版本文件 ----
$fdReadme = @"
AutoShutdown 安全测试版 使用说明（轻量版 / framework-dependent）
版本：v$version

1. 本包为轻量版，程序运行需要 .NET 8 Desktop Runtime x64。
   请先安装：https://dotnet.microsoft.com/download/dotnet/8.0
   （选择 “.NET Desktop Runtime 8.0.x” 的 x64 版本）
2. 将压缩包解压到新目录后，双击 AutoShutdown.App.exe 启动。
3. 首次运行会在用户数据目录（%LocalAppData%\AutoShutdown）自动生成配置与日志，无需手动创建。
"@

$scReadme = @"
AutoShutdown 安全测试版 使用说明（免运行库版 / self-contained）
版本：v$version

1. 本包已包含 .NET 8 运行库，无需另行安装；包体较大属于正常情况。
2. 将压缩包解压到新目录后，双击 AutoShutdown.App.exe 启动。
3. 首次运行会在用户数据目录（%LocalAppData%\AutoShutdown）自动生成配置与日志，无需手动创建。
"@

$safetyNote = @"
AutoShutdown 安全测试版 安全说明
版本：v$version

1. 本版本处于安全测试模式，电源服务固定为 FakePowerService，
   不会执行任何真实关机、重启、睡眠或休眠操作。
2. 默认不启用开机自启动，不写入注册表。如需开机自启动，
   请在 “软件设置” 页中显式开启并确认。
3. 本包为开发环境生成的安全测试版，不含源码、测试与诊断数据。
4. 请勿将本版本用于生产环境的关键电源管理。
"@

$versionFile = "AutoShutdown v$version (安全测试版)"

foreach ($target in @(
    @{ Stage = $fdStage; Readme = $fdReadme },
    @{ Stage = $scStage; Readme = $scReadme })) {
    $stage = $target.Stage
    $readme = $target.Readme
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $stage '使用说明.txt') -Value $readme
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $stage '安全说明.txt') -Value $safetyNote
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $stage 'VERSION.txt') -Value $versionFile
}

# ---- 8. 打包（ZIP 解压后直接得到单一应用目录） ----
$fdZip = Join-Path $packagesDir "AutoShutdown-v$version-win-x64-framework-dependent.zip"
$scZip = Join-Path $packagesDir "AutoShutdown-v$version-win-x64-self-contained.zip"
Write-Host '压缩轻量版...'
Compress-Archive -Path (Join-Path $fdStage '*') -DestinationPath $fdZip -CompressionLevel Optimal
Write-Host '压缩免运行库版...'
Compress-Archive -Path (Join-Path $scStage '*') -DestinationPath $scZip -CompressionLevel Optimal

# ---- 9. 清单：SHA-256、文件清单、构建报告 ----
$shaLines = foreach ($zip in @($fdZip, $scZip)) {
    "$((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)  $(Split-Path -Leaf $zip)"
}
$shaLines | Set-Content -Encoding UTF8 (Join-Path $manifestsDir 'SHA256SUMS.txt')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$manifestLines = @()
foreach ($zip in @($fdZip, $scZip)) {
    $manifestLines += "=== $(Split-Path -Leaf $zip) ==="
    $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
    foreach ($entry in ($z.Entries | Sort-Object FullName)) {
        $manifestLines += ('{0,12}  {1}' -f $entry.Length, $entry.FullName)
    }
    $z.Dispose()
    $manifestLines += ''
}
$manifestLines | Set-Content -Encoding UTF8 (Join-Path $manifestsDir 'FILE-MANIFEST.txt')

$fdInfo = Get-Item -LiteralPath $fdZip
$scInfo = Get-Item -LiteralPath $scZip
$fdHash = (Get-FileHash -LiteralPath $fdZip -Algorithm SHA256).Hash
$scHash = (Get-FileHash -LiteralPath $scZip -Algorithm SHA256).Hash
$fdCount = Get-ChildItem -LiteralPath $fdStage -Recurse -File | Measure-Object | Select-Object -ExpandProperty Count
$scCount = Get-ChildItem -LiteralPath $scStage -Recurse -File | Measure-Object | Select-Object -ExpandProperty Count

$report = @"
# AutoShutdown v$version 安全测试版发布报告（BUILD-REPORT）

- 构建时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
- SDK 版本：$sdkVersion
- 发布参数：Release / win-x64 / PublishSingleFile=false / PublishTrimmed=false / PublishReadyToRun=false / DebugType=None

## 轻量版（framework-dependent）
- ZIP：$($fdInfo.FullName)
- ZIP 大小：$($fdInfo.Length) 字节
- 文件数量：$fdCount
- SHA-256：$fdHash

## 免运行库版（self-contained）
- ZIP：$($scInfo.FullName)
- ZIP 大小：$($scInfo.Length) 字节
- 文件数量：$scCount
- SHA-256：$scHash

## 安全检查
- IPowerService 注册：FakePowerService（发布链未变更）
- 禁止文件命中：轻量版 $fdForbidden / 免运行库版 $scForbidden
- 文本泄露检查：通过（未发现开发绝对路径/用户名/SDK 路径）
- 注册表：未写入
- 开机自启动：未启用
- 应用：未自动启动

## 备份
- 位置：$backupsDir
- 内容：构建前已验收的 Release 产物（exe/dll/deps/runtimeconfig + Core.dll + SHA256SUMS.txt）
"@
Set-Content -Encoding UTF8 (Join-Path $manifestsDir 'BUILD-REPORT.md') -Value $report

# ---- 10. 摘要 ----
Write-Host ''
Write-Host '======== 发布完成 ========'
Write-Host "轻量版: $($fdInfo.FullName)  [$($fdInfo.Length) 字节]"
Write-Host "免运行库版: $($scInfo.FullName)  [$($scInfo.Length) 字节]"
Write-Host "SHA-256 清单: $(Join-Path $manifestsDir 'SHA256SUMS.txt')"
Write-Host '本脚本未写入注册表、未启用开机自启动、未自动启动应用。'
Write-Host '=========================='
exit 0
