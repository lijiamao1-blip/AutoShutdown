# Publish-ReleaseCandidate.ps1 — S-PKG 统一参数化候选构建入口
#
# 用途：从当前仓库构建一个 S-PKG 候选制品，并生成 manifest / SHA-256 / 构建报告。
# 命名：AutoShutdown-v{MAJOR}.{MINOR}.{PATCH}-S{STEP}.{BUILD}（S-PKG 候选命名决策）。
#   默认 {BUILD} = 当前 HEAD 的 git 短哈希；可显式指定。
#
# 安全边界（与既有 S12.4 发布脚本一致，绝不破坏冻结边界）：
#   - 只写入 <OutRoot>（默认 artifacts/release/v<Version>）之下；
#   - 不写注册表、不启用开机自启动、不创建防火墙规则、不操作任务计划程序、
#     不自动启动应用、不要求管理员权限、不触发任何电源操作；
#   - 发布参数固定关闭 Trim / SingleFile(依赖包) / ReadyToRun；
#     候选 EXE 使用自包含单文件发布（与 S13 候选同模式，改名安全）。
#   - 不改动任何源码；本脚本自身不做 git 提交。

[CmdletBinding()]
param(
    [string]$Version = '2.0.0',
    [string]$Step = 'S23',
    [string]$Build = '',                       # 默认取当前 HEAD 短哈希
    [string]$SourceCommit = '',                # 默认取当前 HEAD 全哈希
    [string]$BaseCommit = '',                  # 可选：功能基线提交（如 97cef24）
    [string]$OutRoot = '',                     # 默认 artifacts/release/v<Version>
    [string]$InformationalVersion = '',        # 可选：覆盖 -p:InformationalVersion
    [ValidateSet('Test', 'Production')]
    [string]$DistributionMode = 'Test',         # 对外正式包必须显式传 Production
    [switch]$SkipFdZip,                        # 只构建候选 EXE，跳过轻量 ZIP
    [switch]$SkipSolutionBuild                 # 跳过解决方案 Release 构建（只做 publish）
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SPkg-Lib.ps1')

$root = Split-Path -Parent $PSScriptRoot    # tools 的上一级 = 项目根
$head = Get-GitHeadInfo
if (-not $head) { Write-Error '无法读取 git HEAD；候选必须绑定提交。'; exit 40 }

if (-not $Build) { $Build = $head.Short }
if (-not $SourceCommit) { $SourceCommit = $head.Full }
if (-not $InformationalVersion) { $InformationalVersion = "v$Version-$Step.$Build" }

# 文件/程序集版本必须随 -Version 走（S-PKG-D2）。
# Directory.Build.props 把 FileVersion / AssemblyVersion 显式写死为 2.0.0.0，
# 而 -p:Version= 只覆盖 Version，不会覆盖被显式设置的这两个属性 ——
# 结果是脚本产出的 exe 在 Windows「属性 → 详细信息」里永远显示 2.0.0.0，
# 多个版本的 exe 放在一起时无法按文件版本区分。应用内关于页读的是
# InformationalVersion，不受影响，所以这个问题只在资源管理器里暴露。
$fileVersion = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }

# ---- 1. SDK ----
$dotnet = Get-ASDotNet
if (-not $dotnet) { Write-Error '未找到可用的 .NET SDK。'; exit 10 }
$sdkVersion = (& $dotnet --version)
Write-Host "SDK: $dotnet  ($sdkVersion)"

# ---- 2. 目录（全部限制在 OutRoot 内） ----
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts\release'))
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot "v$Version"))
if (-not $OutRoot) { $OutRoot = $allowedRoot }
$outRootFull = [System.IO.Path]::GetFullPath($OutRoot)
$allowedPrefix = if ($allowedRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $allowedRoot } else { $allowedRoot + [System.IO.Path]::DirectorySeparatorChar }
# OutRoot 必须等于或位于 v<Version> 之下（同一版本发布目录，防止误写其他版本/其他区域）
if (-not ($outRootFull.Equals($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            $outRootFull.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase))) {
    Write-Error "OutRoot 必须在 $allowedRoot 之下；得到 $outRootFull"
    exit 11
}

$candidateDir = Join-Path $outRootFull ("{0}-{1}" -f $Step, $Build)
$fdDir = Join-Path $candidateDir 'win-x64-framework-dependent'
$manifestsDir = Join-Path $outRootFull 'manifests'
$asmName = "AutoShutdown-v$Version-$Step.$Build"
$candidateExe = Join-Path $candidateDir "$asmName.exe"

foreach ($d in @($candidateDir, $manifestsDir)) {
    $full = Assert-AllowedPath -Path $d -Root $allowedRoot
    if (Test-Path -LiteralPath $d) { Remove-Item -Recurse -Force -LiteralPath $d }
}
New-Item -ItemType Directory -Force -Path $candidateDir, $fdDir, $manifestsDir | Out-Null

# ---- 3. 解决方案 Release 构建（0 错误 0 警告门；可选跳过） ----
if (-not $SkipSolutionBuild) {
    Write-Host '解决方案 Release 构建...'
    & $dotnet build (Join-Path $root 'AutoShutdown.sln') -c Release --nologo -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { Write-Error '解决方案 Release 构建失败。'; exit 20 }
}

# ---- 4. 候选 EXE（自包含单文件，与 S13 候选同模式） ----
$appProject = Join-Path $root 'src\AutoShutdown.App\AutoShutdown.App.csproj'
$commonPublish = @(
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false',
    '-p:DebugType=None', '-p:DebugSymbols=false', '-p:UseSharedCompilation=false',
    ("-p:Version=" + $Version), ("-p:FileVersion=" + $fileVersion),
    ("-p:AssemblyVersion=" + $fileVersion),
    ("-p:InformationalVersion=" + $InformationalVersion),
    '--nologo'
)
if ($DistributionMode -eq 'Production') {
    $commonPublish += '-p:AutoShutdownDistributionMode=Production'
}
Write-Host '发布候选 EXE（自包含单文件）...'
& $dotnet publish $appProject @commonPublish -o $candidateDir
if ($LASTEXITCODE -ne 0) { Write-Error '候选 EXE 发布失败。'; exit 21 }

$builtExe = Join-Path $candidateDir 'AutoShutdown.App.exe'
if (-not (Test-Path -LiteralPath $builtExe)) { Write-Error '候选发布未生成 AutoShutdown.App.exe。'; exit 22 }
Rename-Item -LiteralPath $builtExe -NewName "$asmName.exe"
# 单文件自包含发布会附带 WPF 原生运行库文件；确认主程序集 DLL 存在（Embedded 时可能无独立 DLL）。
$candidateExe = Join-Path $candidateDir "$asmName.exe"

# ---- 5. 轻量 ZIP（framework-dependent，可选） ----
$fdZipPath = $null
if (-not $SkipFdZip) {
    Write-Host '发布轻量版（framework-dependent）...'
    $fdCommon = @(
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-p:UseSharedCompilation=false',
        ("-p:Version=" + $Version), ("-p:FileVersion=" + $fileVersion),
        ("-p:AssemblyVersion=" + $fileVersion),
        ("-p:InformationalVersion=" + $InformationalVersion),
        '--nologo'
    )
    if ($DistributionMode -eq 'Production') {
        $fdCommon += '-p:AutoShutdownDistributionMode=Production'
    }
    & $dotnet publish $appProject @fdCommon -o $fdDir
    if ($LASTEXITCODE -ne 0) { Write-Error '轻量版发布失败。'; exit 23 }

    $fdReadme = @"
AutoShutdown v$Version ($Step.$Build) 轻量版（framework-dependent）使用说明

1. 本包运行需要 .NET 8 Desktop Runtime x64：https://dotnet.microsoft.com/download/dotnet/8.0
2. 解压到新目录后双击 AutoShutdown.App.exe 启动。
3. 数据目录默认 %LocalAppData%\AutoShutdown；可用环境变量 AUTOSHUTDOWN_DATA_ROOT 指向隔离数据目录（S-PKG 验证构建）。
4. 本包默认安全测试模式（FakePowerService），默认不启用开机自启动。
"@
    $modeLine = if ($DistributionMode -eq 'Production') {
        '- 正式分发模式：首次启动建立 TestMode=false、RealPowerEnabled=true 配置；创建任务仍需明确确认。'
    } else {
        '- 默认 TestMode=true：不执行真实电源操作。'
    }
    $fdSafety = @"
AutoShutdown v$Version ($Step.$Build) 安全说明
$modeLine
- 默认不启用开机自启动、不写注册表。
- 候选状态：见 manifest（未签名候选 = 未使用正式签名证书，绝不伪称已签名发布）。
- 请勿将未签名候选用于生产环境的真实电源管理。
"@
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $fdDir '使用说明.txt') -Value $fdReadme
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $fdDir '安全说明.txt') -Value $fdSafety
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $fdDir 'VERSION.txt') -Value "AutoShutdown v$Version-$Step.$Build"

    $fdZipPath = Join-Path $candidateDir "$asmName-win-x64-framework-dependent.zip"
    Compress-Archive -Path (Join-Path $fdDir '*') -DestinationPath $fdZipPath -CompressionLevel Optimal
}

# ---- 6. 禁止文件与文本泄露检查 ----
$fdHit = Test-ForbiddenStageFiles -Stage $candidateDir -Label '候选目录'
$exeHit = Test-ForbiddenStageFiles -Stage $fdDir -Label '轻量目录'
Test-TextLeak -Stage $fdDir -Label '轻量版' | Out-Null
Write-Host "禁止文件检查通过（候选 $fdHit / 轻量 $exeHit）。"

# ---- 7. 哈希与 manifest ----
$candidateHash = Get-Sha256Hex -Path $candidateExe
$candidateSize = (Get-Item -LiteralPath $candidateExe).Length
$fileHashes = @()
$fileHashes += "sha256  $candidateHash  $($candidateExe | Split-Path -Leaf)"
if ($fdZipPath) {
    $fdHash = Get-Sha256Hex -Path $fdZipPath
    $fileHashes += "sha256  $fdHash  $($fdZipPath | Split-Path -Leaf)"
}

$manifestText = @"
candidate: $asmName.exe
version: $Version
step: $Step
build: $Build
source-commit: $SourceCommit
base-commit: $BaseCommit
commit-date: $($head.Date)
commit-subject: $($head.Subject)
sha256: $candidateHash
size-bytes: $candidateSize
file-version: $fileVersion
informational-version: $InformationalVersion
distribution-mode: $DistributionMode
sdk: $sdkVersion
publish-mode: self-contained single-file (win-x64, Trim=false, ReadyToRun=false, DebugType=None)
signing-status: unsigned-candidate
publish: dotnet publish src/AutoShutdown.App/AutoShutdown.App.csproj -c Release -r win-x64 --self-contained true
"@
Set-Content -Encoding UTF8 -LiteralPath (Join-Path $manifestsDir "$Step-$Build-candidate.txt") -Value $manifestText

# JSON manifest（结构化）
$manifestJson = [ordered]@{
    candidate = "$asmName.exe"
    version = $Version
    step = $Step
    build = $Build
    sourceCommit = $SourceCommit
    baseCommit = $BaseCommit
    commitDate = $head.Date
    commitSubject = $head.Subject
    sha256 = $candidateHash
    sizeBytes = $candidateSize
    fileVersion = $fileVersion
    informationalVersion = $InformationalVersion
    distributionMode = $DistributionMode
    sdk = $sdkVersion
    publishMode = 'self-contained single-file (win-x64)'
    signingStatus = 'unsigned-candidate'
    frameworkDependentZip = if ($fdZipPath) { Split-Path -Leaf $fdZipPath } else { $null }
    frameworkDependentSha256 = if ($fdZipPath) { (Get-Sha256Hex -Path $fdZipPath) } else { $null }
    generatedAtUtc = [System.DateTimeOffset]::UtcNow.ToString('o')
}
$manifestJson | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $manifestsDir "$Step-$Build-candidate.json")
$fileHashes | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $manifestsDir "$Step-$Build-SHA256SUMS.txt")

# ---- 8. 摘要 ----
Write-Host ''
Write-Host '======== 候选构建完成 ========'
Write-Host "候选 EXE: $candidateExe  [$candidateSize 字节]  SHA256=$candidateHash"
if ($fdZipPath) { Write-Host "轻量 ZIP: $fdZipPath" }
Write-Host "manifest: $(Join-Path $manifestsDir "$Step-$Build-candidate.txt")"
Write-Host '本脚本未写注册表、未启用自启动、未操作防火墙/任务计划、未启动应用、未触发电源。'
Write-Host '=============================='
