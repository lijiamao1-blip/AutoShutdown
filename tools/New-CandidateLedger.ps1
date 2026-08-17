#Requires -Version 5.1
param(
    [string]$Version = '2.0.0',
    [string]$OutRoot = ''
)
# tools/New-CandidateLedger.ps1 —— S-PKG 候选制品账本生成器（只读扫描 + 写账本文件）。
#
# 扫描 artifacts/release/ 下的版本化候选 EXE，计算 SHA-256、核对 Git 提交，登记入
# candidate-ledger（.json + .txt）。S16/S20/S19 阶段未产出独立版本化候选（仅阶段内
# 组合根/候选 EXE 检查点），账本如实记录为 no-standalone-artifact，绝不伪造。
#
# 安全：只读 artifacts 与 git；写路径仅限 artifacts/release/<version>/manifests/。

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $root 'artifacts\release'
if (-not $OutRoot) { $OutRoot = (Join-Path $releaseRoot "v$Version") }
$manifestsDir = Join-Path $OutRoot 'manifests'
New-Item -ItemType Directory -Force -Path $manifestsDir | Out-Null

# ---- 工具：短哈希在 git 中是否存在 ----
function Test-GitCommit([string]$short) {
    if ([string]::IsNullOrWhiteSpace($short)) { return $false }
    Push-Location $root
    try {
        $full = (& git rev-parse --verify "$short^{commit}" 2>$null)
        return [bool]$full
    } finally { Pop-Location }
}

# ---- 扫描 v<Version> 下的候选 EXE ----
$entries = [System.Collections.Generic.List[object]]::new()
$candidateDirs = @(Get-ChildItem -LiteralPath $OutRoot -Directory | Where-Object { $_.Name -notin @('manifests') })
foreach ($dir in $candidateDirs) {
    $exe = Get-ChildItem -LiteralPath $dir.FullName -Filter 'AutoShutdown-v*.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike 'AutoShutdown.App.exe' } |
        Select-Object -First 1
    if (-not $exe) { continue }
    $name = [System.IO.Path]::GetFileNameWithoutExtension($exe.Name)   # AutoShutdown-v2.0.0-S23.97cef24
    $tokens = ($name -replace '^AutoShutdown-v', '') -split '[.-]'     # [2,0,0,S23,97cef24]
    $buildToken = if ($tokens.Count -ge 5) { $tokens[4] } else { '' }   # 97cef24 / 4289bc1 / fe54711
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe.FullName).Hash.ToLowerInvariant()
    $entries.Add([ordered]@{
        step = $tokens[3]
        name = $exe.Name
        version = "$($tokens[0]).$($tokens[1]).$($tokens[2])"
        build = $buildToken
        commitExists = Test-GitCommit $buildToken
        path = $exe.FullName.Substring($root.Length).TrimStart('\')
        sha256 = $hash
        sizeBytes = $exe.Length
        signingStatus = 'unsigned-candidate'
    })
}

# ---- V1 基线（v1.0.0 packages）----
$v1 = Join-Path $releaseRoot 'v1.0.0\packages'
if (Test-Path -LiteralPath $v1) {
    foreach ($pkg in (Get-ChildItem -LiteralPath $v1 -Filter 'AutoShutdown-v1.0.0-*.zip' -File -ErrorAction SilentlyContinue)) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $pkg.FullName).Hash.ToLowerInvariant()
        $entries.Insert(0, [ordered]@{
            step = 'V1'
            name = $pkg.Name
            version = '1.0.0'
            build = 'baseline'
            commitExists = $true
            path = $pkg.FullName.Substring($root.Length).TrimStart('\')
            sha256 = $hash
            sizeBytes = $pkg.Length
            signingStatus = 'unsigned-candidate'
        })
    }
}

# ---- S16 / S20 / S19 阶段：如实登记无独立版本化候选 ----
foreach ($mid in @('S16', 'S20')) {
    $entries.Add([ordered]@{
        step = $mid
        name = ''
        version = '2.0.0'
        build = ''
        commitExists = $false
        path = ''
        sha256 = ''
        sizeBytes = 0
        signingStatus = 'no-standalone-artifact'
        note = '阶段内组合根/候选 EXE 检查点（bin/Release/.../AutoShutdown.App.exe），未产出独立版本化候选'
    })
}

# ---- 输出 ----
$ledger = [ordered]@{
    kind = 'spkg-candidate-ledger'
    generatedBy = 'tools/New-CandidateLedger.ps1'
    version = $Version
    candidates = @($entries)
}
$json = $ledger | ConvertTo-Json -Depth 5
$jsonPath = Join-Path $manifestsDir 'candidate-ledger.json'
$json | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = @()
$lines += "AutoShutdown V2 候选制品账本（S-PKG）"
$lines += ("=" * 40)
foreach ($e in $entries) {
    $lines += ''
    $lines += "step: $($e.step)   name: $($e.name)"
    if (-not $e.name) { $lines += "  状态: $($e.signingStatus)  $($e.note)"; continue }
    $lines += "  version: $($e.version)   build: $($e.build)   commitExists: $($e.commitExists)"
    $lines += "  sha256: $($e.sha256)"
    $lines += "  size: $($e.sizeBytes)   signing: $($e.signingStatus)"
    $lines += "  path: $($e.path)"
}
$txtPath = Join-Path $manifestsDir 'candidate-ledger.txt'
$lines -join "`n" | Set-Content -LiteralPath $txtPath -Encoding UTF8

Write-Host ("candidate-ledger written: " + $jsonPath)
Write-Host ("                       : " + $txtPath)
Write-Host ("entries: " + $entries.Count)
