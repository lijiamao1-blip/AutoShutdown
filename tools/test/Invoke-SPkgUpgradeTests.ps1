#Requires -Version 5.1
# tools/test/Invoke-SPkgUpgradeTests.ps1
# S-PKG B-2 upgrade lifecycle tests (temp sandbox only, no real user data).
#
# Covers the release-book B-2 validation set:
#   A. V1→V2 normal upgrade: backup → binary replace → data migration (app-equivalent
#      simulation mirroring RuntimeStateStore.MigrateV1Async / TasksDocumentStore.MigrateV1Async)
#      → self-check passes; V1 tasks preserved, runtime.json.v1bak written, V1 EXE in rollback slot.
#   B. Three JSON corruption classes (config / tasks / runtime) + NotFound + UnsupportedVersion:
#      classified correctly, self-check fails fail-closed (never silent default).
#   C. Failure injection + automatic rollback:
#      1) backup failure via ACL write-deny        -> upgrade aborts, original untouched
#      2) replace failure via process lock (source) -> auto-rollback restores V1 EXE + data
#      3) migration failure                         -> auto-rollback
#      4) self-check failure (migration corrupts)   -> auto-rollback
#      5) stray user file survives backup+rollback  -> no data loss
#   D. V2→V2 re-upgrade idempotency.
#
# No registry / firewall / Task Scheduler / power / real user data is touched.
# Exit: 0 = all pass; 1 = failures.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'SPkg-Lifecycle.ps1')

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # repo root
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("as-spkg-up-" + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $sandbox 'data'
$installDir = Join-Path $sandbox 'install'
$candDir = Join-Path $sandbox 'cand'
New-Item -ItemType Directory -Force -Path $dataRoot, $installDir, $candDir | Out-Null

$pass = 0; $fail = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}

# ---------- V1 fixtures (realistic V1 single-instance data root + V1 install) ----------
$v1ExeName = 'AutoShutdown-v1.0.0-win-x64.exe'
$v1Exe = Join-Path $installDir $v1ExeName
$v1Bytes = [byte[]](1..2048 | ForEach-Object { ($_ % 251) })
[System.IO.File]::WriteAllBytes($v1Exe, $v1Bytes)
# D1：既有 V1 安装目录须先声明所有权（非空目录未拥有则生命周期门禁 fail-closed 拒绝）。
Write-ASOwnerMarker -InstallDir $installDir -AppFiles $v1ExeName -CandidateName $v1ExeName

$v1Config = @{
    SchemaVersion = 1; TestMode = $true; AllowedActions = @('Shutdown', 'Restart')
    Logging = @{ Level = 'Information'; RetentionDays = 14 }
} | ConvertTo-Json -Depth 4
$v1Tasks = @{
    SchemaVersion = 1
    Tasks = @(
        @{ Id = '11111111-1111-1111-1111-111111111111'; Name = '每周备份关机'; Enabled = $true; Kind = 'Weekly' }
        , @{ Id = '22222222-2222-2222-2222-222222222222'; Name = '一次性提醒'; Enabled = $true; Kind = 'OneTime' }
    )
} | ConvertTo-Json -Depth 6
$v1Runtime = @{
    SchemaVersion = 1
    CurrentInstance = @{ TaskId = '11111111-1111-1111-1111-111111111111'; StartedAt = '2024-01-01T00:00:00Z'; State = 'Running' }
} | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), $v1Config, [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'tasks.json'), $v1Tasks, [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'runtime.json'), $v1Runtime, [System.Text.UTF8Encoding]::new($true))
# 用户私有文件（不属于应用 schema 的随机证据，必须随备份/回滚保留）
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'my-notes.txt'), 'user evidence', [System.Text.UTF8Encoding]::new($true))

# V2 candidate fixture: 主 EXE + 伴随文件（用于进程占用/目标锁注入）
$v2ExeName = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
$v2Exe = Join-Path $candDir $v2ExeName
[System.IO.File]::WriteAllBytes($v2Exe, [byte[]](255..0 | ForEach-Object { $_ }))
$companion = Join-Path $candDir 'AutoShutdown.OfficeSaveHelper.exe'
[System.IO.File]::WriteAllBytes($companion, [byte[]](0..511 | ForEach-Object { ($_ * 7) % 256 }))

# ---------- app 等价迁移模拟（镜像 RuntimeStateStore / TasksDocumentStore 的 GATE-Q3 行为） ----------
$script:MigrateSim = {
    param([string]$Root)
    if (-not $Root) { $Root = (Get-ASDataRoot) }
    $tasksPath = Join-Path $Root 'tasks.json'
    $rtPath = Join-Path $Root 'runtime.json'
    if (Test-Path -LiteralPath $tasksPath) {
        $doc = Get-Content -LiteralPath $tasksPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -ne $doc.PSObject.Properties['SchemaVersion'] -and [int]$doc.SchemaVersion -eq 1) {
            $doc.SchemaVersion = 2   # 无损：仅提升 schema，任务原样保留
            $doc | ConvertTo-Json -Depth 8 |
                Set-Content -LiteralPath $tasksPath -Encoding UTF8
        }
    }
    if (Test-Path -LiteralPath $rtPath) {
        $rt = Get-Content -LiteralPath $rtPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -ne $rt.PSObject.Properties['SchemaVersion'] -and [int]$rt.SchemaVersion -eq 1 -and
            $null -ne $rt.PSObject.Properties['CurrentInstance']) {
            Copy-Item -LiteralPath $rtPath -Destination (Join-Path $Root 'runtime.json.v1bak') -Force
            @{ SchemaVersion = 2; Instances = @{} } | ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath $rtPath -Encoding UTF8
        }
    }
}

# ---------- A. V1→V2 正常升级 ----------
Write-Host "== A. V1->V2 normal upgrade =="

$hCfg = Test-ASJsonHealth -Path (Join-Path $dataRoot 'config.json')
Assert-True 'V1 config classified Valid (schema 1)' ($hCfg.Status -eq 'Valid')
$hTasks = Test-ASJsonHealth -Path (Join-Path $dataRoot 'tasks.json')
Assert-True 'V1 tasks classified Valid (schema 1)' ($hTasks.Status -eq 'Valid')

$up = Invoke-ASUpgrade -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'upgrade' -ExpectedVersion 'PKG.fe54711' -MigrationScript $script:MigrateSim
Assert-True 'upgrade returned self-check Ok' ($up.SelfCheck.Ok)
Assert-True 'V2 exe installed' (Test-Path -LiteralPath (Join-Path $installDir $v2ExeName))
Assert-True 'V2 exe hash == candidate' ((Get-FileHash -LiteralPath (Join-Path $installDir $v2ExeName) -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $v2Exe -Algorithm SHA256).Hash)
$rollSlot = Join-Path $dataRoot 'backups\spkg\rollback-install'
Assert-True 'rollback slot holds V1 exe' (Test-Path -LiteralPath (Join-Path (Join-Path $rollSlot 'install') $v1ExeName))
$tasksAfter = Get-Content -LiteralPath (Join-Path $dataRoot 'tasks.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-True 'V1 tasks preserved after migration (2 tasks)' (@($tasksAfter.Tasks).Count -eq 2)
Assert-True 'tasks schema bumped to 2' ([int]$tasksAfter.SchemaVersion -eq 2)
Assert-True 'runtime.json.v1bak written' (Test-Path -LiteralPath (Join-Path $dataRoot 'runtime.json.v1bak'))
Assert-True 'user evidence preserved through upgrade' (Test-Path -LiteralPath (Join-Path $dataRoot 'my-notes.txt'))
$sc = Test-ASSelfCheck -InstallDir $installDir -Root $dataRoot -ExpectedVersion 'PKG.fe54711'
Assert-True 'self-check passes after upgrade+migration' ($sc.Ok)

# ---------- B. 三类 JSON 损坏 + NotFound + UnsupportedVersion（fail-closed） ----------
Write-Host "== B. corruption classes (fail-closed) =="

[System.IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), '{not valid', [System.Text.UTF8Encoding]::new($true))
$h1 = Test-ASJsonHealth -Path (Join-Path $dataRoot 'config.json')
Assert-True 'corrupt config -> Corrupt' ($h1.Status -eq 'Corrupt')
Assert-True 'corrupt config -> self-check fails' (-not (Test-ASSelfCheck -InstallDir $installDir -Root $dataRoot -ExpectedVersion 'PKG.fe54711').Ok)
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), $v1Config, [System.Text.UTF8Encoding]::new($true))

[System.IO.File]::WriteAllText((Join-Path $dataRoot 'tasks.json'), '{oops', [System.Text.UTF8Encoding]::new($true))
$h2 = Test-ASJsonHealth -Path (Join-Path $dataRoot 'tasks.json')
Assert-True 'corrupt tasks -> Corrupt' ($h2.Status -eq 'Corrupt')
Assert-True 'corrupt tasks -> self-check fails' (-not (Test-ASSelfCheck -InstallDir $installDir -Root $dataRoot -ExpectedVersion 'PKG.fe54711').Ok)
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'tasks.json'), $v1Tasks, [System.Text.UTF8Encoding]::new($true))

[System.IO.File]::WriteAllText((Join-Path $dataRoot 'runtime.json'), '] not json', [System.Text.UTF8Encoding]::new($true))
$h3 = Test-ASJsonHealth -Path (Join-Path $dataRoot 'runtime.json') -SchemaRequired $false
Assert-True 'corrupt runtime -> Corrupt (parseable required)' ($h3.Status -eq 'Corrupt')
Assert-True 'corrupt runtime -> self-check fails' (-not (Test-ASSelfCheck -InstallDir $installDir -Root $dataRoot -ExpectedVersion 'PKG.fe54711').Ok)
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'runtime.json'), $v1Runtime, [System.Text.UTF8Encoding]::new($true))

$hMissing = Test-ASJsonHealth -Path (Join-Path $dataRoot 'nope.json') -SchemaRequired $false
Assert-True 'missing file -> NotFound' ($hMissing.Status -eq 'NotFound')

$unsup = @{ SchemaVersion = 99 } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'unsup.json'), $unsup, [System.Text.UTF8Encoding]::new($true))
$hUnsup = Test-ASJsonHealth -Path (Join-Path $dataRoot 'unsup.json')
Assert-True 'schema 99 -> UnsupportedVersion' ($hUnsup.Status -eq 'UnsupportedVersion')

$noSchema = @{ Foo = 1 } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'noschema.json'), $noSchema, [System.Text.UTF8Encoding]::new($true))
$hNoSchema = Test-ASJsonHealth -Path (Join-Path $dataRoot 'noschema.json')
Assert-True 'missing SchemaVersion -> Invalid' ($hNoSchema.Status -eq 'Invalid')
Remove-Item -LiteralPath (Join-Path $dataRoot 'unsup.json'), (Join-Path $dataRoot 'noschema.json') -Force

# ---------- C. 失败注入 + 自动回退 ----------
Write-Host "== C. failure injection + automatic rollback =="

# 每个失败用例都从干净的 V1 状态开始（模拟升级前的真实环境），回退断言才有意义。
function Reset-V1State {
    if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force }
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $installDir, $dataRoot | Out-Null
    [System.IO.File]::WriteAllBytes($v1Exe, $v1Bytes)
    # D1：每个失败用例从干净的 V1 状态开始，重建安装目录后须重新声明所有权。
    Write-ASOwnerMarker -InstallDir $installDir -AppFiles $v1ExeName -CandidateName $v1ExeName
    [System.IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), $v1Config, [System.Text.UTF8Encoding]::new($true))
    [System.IO.File]::WriteAllText((Join-Path $dataRoot 'tasks.json'), $v1Tasks, [System.Text.UTF8Encoding]::new($true))
    [System.IO.File]::WriteAllText((Join-Path $dataRoot 'runtime.json'), $v1Runtime, [System.Text.UTF8Encoding]::new($true))
    [System.IO.File]::WriteAllText((Join-Path $dataRoot 'my-notes.txt'), 'user evidence', [System.Text.UTF8Encoding]::new($true))
}

# C1) 备份失败：ACL 写拒绝（finally 还原）→ 升级必须中止，原安装原样保留
Reset-V1State
$spkgDir = Join-Path $dataRoot 'backups\spkg'
New-Item -ItemType Directory -Force -Path $spkgDir | Out-Null
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$denyAdded = $false
try {
    & icacls $spkgDir /deny "*${sid}:(OI)(CI)W" | Out-Null
    $denyAdded = $true
    $threw = $false
    try { Invoke-ASUpgrade -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'upgrade2' -ExpectedVersion 'PKG.fe54711' -MigrationScript $script:MigrateSim | Out-Null } catch { $threw = $true }
    Assert-True 'backup failure aborts upgrade (throws)' $threw
    Assert-True 'backup failure leaves V2 exe NOT installed' (-not (Test-Path -LiteralPath (Join-Path $installDir 'AutoShutdown-v2.0.0-PKG.fe54711.exe')))
    Assert-True 'backup failure keeps V1 exe in place' (Test-Path -LiteralPath $v1Exe)
    Assert-True 'backup failure keeps user evidence' (Test-Path -LiteralPath (Join-Path $dataRoot 'my-notes.txt'))
} finally {
    if ($denyAdded) { & icacls $spkgDir /reset | Out-Null }
}

# C2) 替换失败：源候选进程占用（FileShare.None）→ 自动回滚恢复 V1 EXE + 原数据
Reset-V1State
$lockStream = [System.IO.File]::Open($v2Exe, 'Open', 'Read', [System.IO.FileShare]::None)
$threw2 = $false; $errMsg = ''
try {
    Invoke-ASUpgrade -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'upgrade3' -ExpectedVersion 'PKG.fe54711' -MigrationScript $script:MigrateSim | Out-Null
} catch { $threw2 = $true; $errMsg = $_.Exception.Message } finally { if ($lockStream) { $lockStream.Dispose() } }
Assert-True 'replace failure throws' $threw2
Assert-True 'replace failure rolled back to V1 exe' (Test-Path -LiteralPath (Join-Path $installDir $v1ExeName))
Assert-True 'replace failure did not leave partial V2 exe' (-not (Test-Path -LiteralPath (Join-Path $installDir $v2ExeName)))
Assert-True 'replace failure preserved user evidence' (Test-Path -LiteralPath (Join-Path $dataRoot 'my-notes.txt'))
Assert-True 'replace failure restored V1 tasks schema' ((Get-Content -LiteralPath (Join-Path $dataRoot 'tasks.json') -Raw -Encoding UTF8 | ConvertFrom-Json).SchemaVersion -eq 1)

# C3) 迁移失败 → 自动回滚
Reset-V1State
$badMigrate = { param([string]$Root) throw 'simulated migration crash' }
$threw3 = $false
try { Invoke-ASUpgrade -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'upgrade4' -ExpectedVersion 'PKG.fe54711' -MigrationScript $badMigrate | Out-Null } catch { $threw3 = $true }
Assert-True 'migration failure throws' $threw3
Assert-True 'migration failure rolled back to V1 exe' (Test-Path -LiteralPath (Join-Path $installDir $v1ExeName))
Assert-True 'migration failure restored V1 tasks schema' ((Get-Content -LiteralPath (Join-Path $dataRoot 'tasks.json') -Raw -Encoding UTF8 | ConvertFrom-Json).SchemaVersion -eq 1)

# C4) 自检失败（迁移把 tasks 写成 schema 3 的损坏态）→ 自动回滚
Reset-V1State
$corruptingMigrate = {
    param([string]$Root)
    $doc = Get-Content -LiteralPath (Join-Path $Root 'tasks.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $doc.SchemaVersion = 3   # 非法：V2 只支持 schema 2 → 自检必须失败并回滚
    $doc | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Root 'tasks.json') -Encoding UTF8
}
$threw4 = $false
try { Invoke-ASUpgrade -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'upgrade5' -ExpectedVersion 'PKG.fe54711' -MigrationScript $corruptingMigrate | Out-Null } catch { $threw4 = $true }
Assert-True 'self-check failure throws' $threw4
Assert-True 'self-check failure rolled back to V1 exe' (Test-Path -LiteralPath (Join-Path $installDir $v1ExeName))
Assert-True 'self-check failure restored V1 config' ((Get-Content -LiteralPath (Join-Path $dataRoot 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json).TestMode -eq $true)

# C5) 完整回滚后数据与 V1 EXE 均可恢复（与发布一一对应）
Reset-V1State
Invoke-ASUpgrade -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'upgrade' -ExpectedVersion 'PKG.fe54711' -MigrationScript $script:MigrateSim | Out-Null
$rb = Restore-ASRollback -InstallDir $installDir -Root $dataRoot -Tag 'upgrade'
Assert-True 'explicit rollback restores V1 exe' (Test-Path -LiteralPath (Join-Path $installDir $v1ExeName))
Assert-True 'explicit rollback restores user evidence' (Test-Path -LiteralPath (Join-Path $dataRoot 'my-notes.txt'))
Assert-True 'explicit rollback restores V1 config' ((Get-Content -LiteralPath (Join-Path $dataRoot 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json).TestMode -eq $true)

# ---------- D. V2→V2 再升级幂等 ----------
Write-Host "== D. V2->V2 re-upgrade idempotency =="
Reset-V1State
# 先正常升级到 V2，再对 V2 数据根做第二次升级（数据已迁移，无需 MigrationScript）
Invoke-ASUpgrade -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'upgrade' -ExpectedVersion 'PKG.fe54711' -MigrationScript $script:MigrateSim | Out-Null
$up2 = Invoke-ASUpgrade -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'upgrade6' -ExpectedVersion 'PKG.fe54711'
Assert-True 'V2->V2 re-upgrade self-check Ok' ($up2.SelfCheck.Ok)
Assert-True 'V2->V2 keeps tasks schema 2' ((Get-Content -LiteralPath (Join-Path $dataRoot 'tasks.json') -Raw -Encoding UTF8 | ConvertFrom-Json).SchemaVersion -eq 2)
Assert-True 'V2->V2 keeps V1 backup evidence' (Test-Path -LiteralPath (Join-Path $dataRoot 'runtime.json.v1bak'))

# ---- cleanup ----
Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("UPGRADE TESTS: pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
exit 0
