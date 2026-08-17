#Requires -Version 5.1
# tools/test/Invoke-SPBoundaryTests.ps1
# S-PKG B-3 system-integration boundary tests (temp sandbox + read-only system snapshots).
#
# Verifies the S-PKG lifecycle never leaks into the app-owned system integrations:
#   - Auto-start  : HKCU\Software\Microsoft\Windows\CurrentVersion\Run 值 AutoShutdown.Desktop
#   - Firewall    : 应用不管理防火墙（S23 LAN 远程控制由用户人工放行）
#   - Task Sched  : 应用自有的 \AutoShutdown V2\ 任务文件夹（任务名 AutoShutdownV2::{guid}）
# Approach: run the FULL lifecycle (backup -> replace -> selfcheck -> rollback ->
# uninstall Keep -> reinstall -> uninstall Remove) against an isolated temp data root,
# while taking READ-ONLY before/after snapshots of the three integration points and
# asserting they are unchanged. Requires-elevation queries (firewall / task scheduler)
# are best-effort: on access denial they are recorded as SKIP, not failure (the static
# contract tests already prove the lifecycle contains no such commands).
#
# No registry / firewall / task-scheduler write, no real user data, no power is touched.
# Exit: 0 = all pass (or only explicit SKIPs); 1 = failures.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'SPkg-Lifecycle.ps1')

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # repo root
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("as-spkg-bd-" + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $sandbox 'data'
$installDir = Join-Path $sandbox 'install'
$candDir = Join-Path $sandbox 'cand'
New-Item -ItemType Directory -Force -Path $dataRoot, $installDir, $candDir | Out-Null

$pass = 0; $fail = 0; $skip = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}
function Note-Skip([string]$Name, [string]$Why) {
    $script:skip++; Write-Host ("  SKIP  {0}  ({1})" -f $Name, $Why)
}

# ---------- 沙箱夹具（与 B-2 同构的 V1 数据根 + V2 候选） ----------
$v1Exe = Join-Path $installDir 'AutoShutdown-v1.0.0-win-x64.exe'
[System.IO.File]::WriteAllBytes($v1Exe, [byte[]](1..1024 | ForEach-Object { ($_ % 251) }))
# D1：既有 V1 安装目录须先声明所有权（非空目录未拥有则生命周期门禁 fail-closed 拒绝）。
Write-ASOwnerMarker -InstallDir $installDir -AppFiles 'AutoShutdown-v1.0.0-win-x64.exe' -CandidateName 'AutoShutdown-v1.0.0-win-x64.exe'
$v2ExeName = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
[System.IO.File]::WriteAllBytes((Join-Path $candDir $v2ExeName), [byte[]](255..0 | ForEach-Object { $_ }))
[System.IO.File]::WriteAllBytes((Join-Path $candDir 'AutoShutdown.OfficeSaveHelper.exe'), [byte[]](0..255 | ForEach-Object { $_ }))
$goodConfig = @{ SchemaVersion = 1; TestMode = $true } | ConvertTo-Json
$goodTasks  = @{ SchemaVersion = 2; Tasks = @() } | ConvertTo-Json -Depth 4
$goodRuntime = @{ SchemaVersion = 2; Instances = @{} } | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'config.json'), $goodConfig, [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'tasks.json'), $goodTasks, [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $dataRoot 'runtime.json'), $goodRuntime, [System.Text.UTF8Encoding]::new($true))

# ---------- 系统集成点只读快照 ----------
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Get-AutoStartSnapshot {
    $item = Get-ItemProperty -LiteralPath $runKeyPath -Name 'AutoShutdown.Desktop' -ErrorAction SilentlyContinue
    if ($null -ne $item) { return [string]$item.'AutoShutdown.Desktop' }
    return $null
}
function Get-FirewallSnapshot {
    # 匹配本应用命名的防火墙规则（应用不管理防火墙，期望无此类规则或前后一致）
    $rules = @(Get-NetFirewallRule -ErrorAction Stop |
        Where-Object { $_.DisplayName -like '*AutoShutdown*' -or $_.Name -like '*AutoShutdown*' } |
        ForEach-Object { $_.Name } | Sort-Object)
    return ($rules -join '|')
}
function Get-TaskSchedulerSnapshot {
    # 文件夹不存在时 Get-ScheduledTask 会抛"找不到对象"（属正常空态），用 SilentlyContinue
    # 得到空列表；真正需要权限的失败留给外层 catch 判 SKIP。
    $tasks = @(Get-ScheduledTask -TaskPath '\AutoShutdown V2\' -ErrorAction SilentlyContinue |
        ForEach-Object { $_.TaskName } | Sort-Object)
    return ($tasks -join '|')
}

$fwBefore = $null; $fwQueryable = $true
try { $fwBefore = Get-FirewallSnapshot } catch { $fwQueryable = $false }
$tsBefore = $null; $tsQueryable = $true
try { $tsBefore = Get-TaskSchedulerSnapshot } catch { $tsQueryable = $false }
$asBefore = Get-AutoStartSnapshot

Write-Host "== B-3 boundary: full lifecycle leaves system integrations untouched =="

# ---------- 运行完整生命周期（全部限定在沙箱） ----------
$backup = Backup-ASDataRoot -Root $dataRoot -Tag 'bd'
Assert-True 'backup in sandbox' (Test-Path -LiteralPath $backup.BackupDir)
$rep = Replace-ASBinary -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot -Tag 'bd'
Assert-True 'replace installed V2 exe' (Test-Path -LiteralPath (Join-Path $installDir $v2ExeName))
$sc = Test-ASSelfCheck -InstallDir $installDir -Root $dataRoot -ExpectedVersion 'PKG.fe54711'
Assert-True 'selfcheck ok' $sc.Ok
Restore-ASRollback -InstallDir $installDir -Root $dataRoot -Tag 'bd' | Out-Null
Assert-True 'rollback restored V1 exe' (Test-Path -LiteralPath (Join-Path $installDir 'AutoShutdown-v1.0.0-win-x64.exe'))
Invoke-ASUninstall -InstallDir $installDir -Root $dataRoot -UserData 'Keep' | Out-Null
Assert-True 'uninstall Keep removed install files' (-not (Test-Path -LiteralPath (Join-Path $installDir $v2ExeName)))
Invoke-ASReinstall -CandidateDir $candDir -InstallDir $installDir -Root $dataRoot | Out-Null
Assert-True 'reinstall present' (Test-Path -LiteralPath (Join-Path $installDir $v2ExeName))
Invoke-ASUninstall -InstallDir $installDir -Root $dataRoot -UserData 'Remove' | Out-Null
Assert-True 'uninstall Remove cleared spkg backups' (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'backups\spkg')))

# ---------- 系统快照对比（只读） ----------
$asAfter = Get-AutoStartSnapshot
Assert-True 'auto-start registry value unchanged' (($asBefore -eq $asAfter)) ("before='$asBefore' after='$asAfter'")

if ($fwQueryable) {
    $fwAfter = Get-FirewallSnapshot
    Assert-True 'firewall rules unchanged' ($fwBefore -eq $fwAfter) ("before='$fwBefore' after='$fwAfter'")
} else {
    Note-Skip 'firewall rules unchanged' 'Get-NetFirewallRule 需管理员权限；静态契约测试已覆盖'
}
if ($tsQueryable) {
    $tsAfter = Get-TaskSchedulerSnapshot
    Assert-True 'task scheduler folder unchanged' ($tsBefore -eq $tsAfter) ("before='$tsBefore' after='$tsAfter'")
} else {
    Note-Skip 'task scheduler folder unchanged' 'Get-ScheduledTask 需相应权限；静态契约测试已覆盖'
}

# ---------- 源码边界：应用不管理防火墙，LAN RC 靠人工放行 ----------
$appFiles = @(Get-ChildItem -Path (Join-Path $root 'src') -Filter '*.cs' -Recurse -File -ErrorAction SilentlyContinue)
$hasFirewallCmd = $false
foreach ($f in $appFiles) {
    $txt = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if ($txt -match 'New-NetFirewallRule|netsh advfirewall|netsh firewall') { $hasFirewallCmd = $true; break }
}
Assert-True 'app source manages no firewall' (-not $hasFirewallCmd)
Assert-True 'app owns auto-start value name' ($null -ne (Get-ChildItem -Path (Join-Path $root 'src') -Filter '*.cs' -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue } | Select-String -Pattern 'AutoShutdown.Desktop' -SimpleMatch | Select-Object -First 1))

# ---- cleanup ----
Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("BOUNDARY TESTS: pass={0} fail={1} skip={2}" -f $pass, $fail, $skip)
if ($fail -gt 0) { exit 1 }
exit 0
