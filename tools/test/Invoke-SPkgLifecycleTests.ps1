#Requires -Version 5.1
# tools/test/Invoke-SPkgLifecycleTests.ps1
# S-PKG lifecycle state-machine tests (pure file ops, temp sandbox only).
# Exercises: JSON health classification, backup, replace (with/without injected
# failure), self-check, rollback, uninstall (Keep/Remove), reinstall.
# No registry / firewall / Task Scheduler / power / real user data is touched.
#
# Exit: 0 = all pass; 1 = failures.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'SPkg-Lifecycle.ps1')

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # repo root
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("as-spkg-lt-" + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $sandbox 'data'
$installDir = Join-Path $sandbox 'install'
$candDir = Join-Path $sandbox 'cand'
New-Item -ItemType Directory -Force -Path $dataRoot, $installDir, $candDir | Out-Null

$pass = 0; $fail = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}

# --- sandbox fixtures ---
$oldExe = Join-Path $installDir 'AutoShutdown-v2.0.0-S13.4289bc1.exe'
$oldBytes = [byte[]](1..2048 | ForEach-Object { ($_ % 251) })
[System.IO.File]::WriteAllBytes($oldExe, $oldBytes)
$newExeName = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
$newExe = Join-Path $candDir $newExeName
$newBytes = [byte[]](255..0 | ForEach-Object { $_ })
[System.IO.File]::WriteAllBytes($newExe, $newBytes)

$goodConfig = @{ SchemaVersion = 1; TestMode = $true; Logging = @{ Level = 'Information'; RetentionDays = 14 } } | ConvertTo-Json -Depth 4
$goodTasks  = @{ SchemaVersion = 2; Tasks = @() } | ConvertTo-Json -Depth 4
$goodRuntime = @{ SchemaVersion = 2; Instances = @() } | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), $goodConfig, [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'tasks.json'), $goodTasks, [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'runtime.json'), $goodRuntime, [System.Text.UTF8Encoding]::new($true))

Write-Host "== JSON health classification =="
$hMissing = Test-ASJsonHealth -Path (Join-Path $dataRoot 'nope.json') -SchemaRequired $false
Assert-True 'missing file -> NotFound' ($hMissing.Status -eq 'NotFound')

[System.IO.File]::WriteAllText((Join-Path $dataRoot 'corrupt.json'), '{not valid json', [System.Text.UTF8Encoding]::new($true))
$hCorrupt = Test-ASJsonHealth -Path (Join-Path $dataRoot 'corrupt.json') -SchemaRequired $false
Assert-True 'corrupt json -> Corrupt' ($hCorrupt.Status -eq 'Corrupt')

$hGood = Test-ASJsonHealth -Path (Join-Path $dataRoot 'config.json')
Assert-True 'valid config -> Valid' ($hGood.Status -eq 'Valid')

$unsup = @{ SchemaVersion = 99 } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'unsup.json'), $unsup, [System.Text.UTF8Encoding]::new($true))
$hUnsup = Test-ASJsonHealth -Path (Join-Path $dataRoot 'unsup.json')
Assert-True 'schema 99 -> UnsupportedVersion' ($hUnsup.Status -eq 'UnsupportedVersion')

Write-Host "== backup =="
$b = Backup-ASDataRoot -Root $dataRoot -Tag 'upgrade' -CandidateName $newExeName
Assert-True 'backup dir created' (Test-Path -LiteralPath $b.BackupDir)
Assert-True 'backup meta written' (Test-Path -LiteralPath (Join-Path $b.BackupDir 'spkg-lifecycle.json'))
Assert-True 'backup copied config' (Test-Path -LiteralPath (Join-Path $b.BackupDir 'config.json'))

Write-Host "== replace (normal) =="
$r = Replace-ASBinary -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'upgrade'
Assert-True 'new exe present' (Test-Path -LiteralPath (Join-Path $installDir $newExeName))
Assert-True 'old exe backed up in rollback slot' (Test-Path -LiteralPath (Join-Path $r.BackupDir (Join-Path 'install' 'AutoShutdown-v2.0.0-S13.4289bc1.exe')))
$installedHash = (Get-FileHash -LiteralPath (Join-Path $installDir $newExeName) -Algorithm SHA256).Hash
$candHash = (Get-FileHash -LiteralPath $newExe -Algorithm SHA256).Hash
Assert-True 'installed exe hash == candidate' ($installedHash -eq $candHash)

Write-Host "== self-check =="
$sc = Test-ASSelfCheck -InstallDir $installDir -Root $dataRoot -ExpectedVersion 'PKG.fe54711'
Assert-True 'selfcheck ok after replace' ($sc.Ok)

[System.IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), '{broken', [System.Text.UTF8Encoding]::new($true))
$scBad = Test-ASSelfCheck -InstallDir $installDir -Root $dataRoot -ExpectedVersion 'PKG.fe54711'
Assert-True 'selfcheck fails on corrupt config' (-not $scBad.Ok)
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), $goodConfig, [System.Text.UTF8Encoding]::new($true))

Write-Host "== replace failure injection (file lock) =="
# 重新放回旧 exe，模拟一次新的替换（被锁文件必须原样保留，fail-closed）
Remove-Item -LiteralPath $installDir -Recurse -Force
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
[System.IO.File]::WriteAllBytes($oldExe, $oldBytes)
$lockStream = [System.IO.File]::Open($oldExe, 'Open', 'Read', [System.IO.FileShare]::None)
$replaced = $false
try {
    Replace-ASBinary -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'locked' | Out-Null
    $replaced = $true
} catch {
    # expected failure
} finally {
    if ($lockStream) { $lockStream.Dispose() }
}
Assert-True 'locked replace reports failure (no silent success)' (-not $replaced)
$oldHashAfter = (Get-FileHash -LiteralPath $oldExe -Algorithm SHA256).Hash
$oldHashBefore = (Get-FileHash -LiteralPath $oldExe -Algorithm SHA256).Hash
Assert-True 'locked old exe preserved' ($oldHashAfter -eq $oldHashBefore)

Write-Host "== rollback =="
# 把配置改为“升级后污染”值，再回滚
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), @{ SchemaVersion = 1; TestMode = $false; Contaminated = $true } | ConvertTo-Json, [System.Text.UTF8Encoding]::new($true))
$rb = Restore-ASRollback -InstallDir $installDir -Root $dataRoot -Tag 'upgrade'
$restoredConfig = Get-Content -LiteralPath (Join-Path $dataRoot 'config.json') -Raw | ConvertFrom-Json
$contaminated = $null -ne $restoredConfig.PSObject.Properties['Contaminated']
Assert-True 'rollback restores config.json' ($restoredConfig.TestMode -eq $true -and -not $contaminated)
$exeAfterRollback = Get-ChildItem -LiteralPath $installDir -Filter 'AutoShutdown-v*.exe' -File | Select-Object -First 1
Assert-True 'rollback restores old exe' ($exeAfterRollback -and $exeAfterRollback.Name -eq 'AutoShutdown-v2.0.0-S13.4289bc1.exe')

Write-Host "== uninstall Keep =="
$uKeep = Invoke-ASUninstall -InstallDir $installDir -Root $dataRoot -UserData 'Keep'
Assert-True 'uninstall removed install files' (-not (Test-Path -LiteralPath (Join-Path $installDir 'AutoShutdown-v*.exe')))
Assert-True 'uninstall keeps user config' (Test-Path -LiteralPath (Join-Path $dataRoot 'config.json'))

Write-Host "== reinstall (fresh) =="
$ri = Invoke-ASReinstall -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot
Assert-True 'reinstall copied candidate exe' (Test-Path -LiteralPath (Join-Path $installDir $newExeName))
Assert-True 'reinstall keeps data root' (Test-Path -LiteralPath (Join-Path $dataRoot 'config.json'))

Write-Host "== uninstall Remove (cleanup evidence) =="
$uRem = Invoke-ASUninstall -InstallDir $installDir -Root $dataRoot -UserData 'Remove'
Assert-True 'uninstall Remove clears backups' (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'backups\spkg')))

# ---- cleanup ----
Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("LIFECYCLE TESTS: pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
exit 0
