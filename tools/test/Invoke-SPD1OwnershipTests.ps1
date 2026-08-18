#Requires -Version 5.1
# tools/test/Invoke-SPD1OwnershipTests.ps1
# S-PKG D1 安装所有权与破坏性路径加固测试（纯文件操作，临时沙箱，不触碰真实系统）。
# 覆盖（D1 检查点）：
#   A. 危险路径硬守卫 Test-ASForbiddenPath（fs 根 / 用户主目录 / SystemRoot / 仓库根 / artifacts）
#   B. Test-ASInstallOwnership 状态机（new / empty / owned / no-marker / bad-marker /
#      path-mismatch / forbidden）
#   C. 非空未拥有目录的破坏性操作 fail-closed（Replace / Uninstall / Reinstall /
#      Restore-ASInstallBackup 一律拒绝，不删除任何文件）
#   D. 有效所有权下生命周期操作成功；清单式删除只删 appFiles，非应用文件原样保留
#   E. DataRoot 保护：备份根所有权绑定校验；UserData=Remove 对误指/非应用备份拒绝
# Exit: 0 = all pass; 1 = failures.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'SPkg-Lifecycle.ps1')

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # repo root
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("as-spkg-d1-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$pass = 0; $fail = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}

# ============================================================
# A. 危险路径硬守卫（fs 根 / 用户主目录 / SystemRoot / 仓库根 / artifacts）
# ============================================================
Write-Host "== A. forbidden path guards =="
$fsRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($env:SystemDrive + '\'))
Assert-True 'filesystem root forbidden' (Test-ASForbiddenPath $fsRoot)
# UserProfile 为空的非交互环境：安全跳过该断言（不因 GetFullPath('') 异常中断）；
# 真实桌面环境保持原断言，不降低覆盖。
$userProfile = [Environment]::GetFolderPath('UserProfile')
if ([string]::IsNullOrWhiteSpace($userProfile)) {
    Write-Host '  SKIP  user home forbidden (UserProfile empty in non-interactive environment)'
} else {
    Assert-True 'user home forbidden' (Test-ASForbiddenPath $userProfile)
}
Assert-True 'SystemRoot forbidden' (Test-ASForbiddenPath $env:SystemRoot)
Assert-True 'repo root forbidden' (Test-ASForbiddenPath $root)
Assert-True 'artifacts forbidden' (Test-ASForbiddenPath (Join-Path $root 'artifacts'))
Assert-True 'artifacts subtree forbidden' (Test-ASForbiddenPath (Join-Path $root 'artifacts\release\v2.0.0'))
Assert-True 'temp sandbox not forbidden' (-not (Test-ASForbiddenPath $sandbox))
Assert-True 'repo root via ownership gate -> forbidden' ((Test-ASInstallOwnership $root).Reason -eq 'forbidden')

# ============================================================
# B. Test-ASInstallOwnership 状态机
# ============================================================
Write-Host "== B. ownership state machine =="
$newDir = Join-Path $sandbox 'st-new'
$oNew = Test-ASInstallOwnership $newDir
Assert-True 'new dir -> Ok' $oNew.Ok
Assert-True 'new dir -> reason new' ($oNew.Reason -eq 'new')

$emptyDir = Join-Path $sandbox 'st-empty'
New-Item -ItemType Directory -Force -Path $emptyDir | Out-Null
$oEmpty = Test-ASInstallOwnership $emptyDir
Assert-True 'empty dir -> Ok' $oEmpty.Ok
Assert-True 'empty dir -> reason empty' ($oEmpty.Reason -eq 'empty')

$noMarkerDir = Join-Path $sandbox 'st-nomarker'
New-Item -ItemType Directory -Force -Path $noMarkerDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $noMarkerDir 'a.exe'), [byte[]](1..10))
$oNoMarker = Test-ASInstallOwnership $noMarkerDir
Assert-True 'non-empty no marker -> not Ok' (-not $oNoMarker.Ok)
Assert-True 'non-empty no marker -> no-marker' ($oNoMarker.Reason -eq 'no-marker')

$ownedDir = Join-Path $sandbox 'st-owned'
New-Item -ItemType Directory -Force -Path $ownedDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $ownedDir 'a.exe'), [byte[]](1..10))
Write-ASOwnerMarker -InstallDir $ownedDir -AppFiles 'a.exe' -CandidateName 'a.exe'
$oOwned = Test-ASInstallOwnership $ownedDir
Assert-True 'valid marker -> Ok' $oOwned.Ok
Assert-True 'valid marker -> owned' ($oOwned.Reason -eq 'owned')

$badAppDir = Join-Path $sandbox 'st-badapp'
New-Item -ItemType Directory -Force -Path $badAppDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $badAppDir 'a.exe'), [byte[]](1..10))
@{ schema = 1; app = 'Not AutoShutdown'; installDir = $badAppDir; appFiles = @('a.exe') } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $badAppDir 'AutoShutdown.owner.json') -Encoding UTF8
$oBadApp = Test-ASInstallOwnership $badAppDir
Assert-True 'wrong app marker -> bad-marker' ($oBadApp.Reason -eq 'bad-marker' -and -not $oBadApp.Ok)

$badSchemaDir = Join-Path $sandbox 'st-badschema'
New-Item -ItemType Directory -Force -Path $badSchemaDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $badSchemaDir 'a.exe'), [byte[]](1..10))
@{ schema = 99; app = 'AutoShutdown V2'; installDir = $badSchemaDir; appFiles = @('a.exe') } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $badSchemaDir 'AutoShutdown.owner.json') -Encoding UTF8
$oBadSchema = Test-ASInstallOwnership $badSchemaDir
Assert-True 'wrong schema marker -> bad-marker' ($oBadSchema.Reason -eq 'bad-marker' -and -not $oBadSchema.Ok)

$mismatchDir = Join-Path $sandbox 'st-mismatch'
New-Item -ItemType Directory -Force -Path $mismatchDir | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $mismatchDir 'a.exe'), [byte[]](1..10))
@{ schema = 1; app = 'AutoShutdown V2'; installDir = (Join-Path $sandbox 'elsewhere'); appFiles = @('a.exe') } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $mismatchDir 'AutoShutdown.owner.json') -Encoding UTF8
$oMismatch = Test-ASInstallOwnership $mismatchDir
Assert-True 'marker bound to other path -> path-mismatch' ($oMismatch.Reason -eq 'path-mismatch' -and -not $oMismatch.Ok)

# ============================================================
# C. 非空未拥有目录的破坏性操作 fail-closed（拒绝且不删除任何文件）
# ============================================================
Write-Host "== C. fail-closed rejection (unowned non-empty) =="
$denyDir = Join-Path $sandbox 'c-deny'
$candDeny = Join-Path $sandbox 'c-cand'
$dataC = Join-Path $sandbox 'data-c'
New-Item -ItemType Directory -Force -Path $denyDir, $candDeny | Out-Null
$denyOld = 'AutoShutdown-v1.0.0-win-x64.exe'
$denyNew = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
[System.IO.File]::WriteAllBytes((Join-Path $denyDir $denyOld), [byte[]](1..2048 | ForEach-Object { ($_ % 251) }))
[System.IO.File]::WriteAllBytes((Join-Path $candDeny $denyNew), [byte[]](255..0))
$preCount = @(Get-ChildItem -LiteralPath $denyDir -File -Force).Count
$preHash = (Get-FileHash -LiteralPath (Join-Path $denyDir $denyOld) -Algorithm SHA256).Hash

$repThrew = $false
try { Replace-ASBinary -CandidateDir $candDeny -InstallDir $denyDir -Root $dataC | Out-Null } catch { $repThrew = $true }
Assert-True 'replace rejects unowned non-empty' $repThrew
Assert-True 'replace left install files untouched' (@(Get-ChildItem -LiteralPath $denyDir -File -Force).Count -eq $preCount)
Assert-True 'replace left exe hash untouched' ((Get-FileHash -LiteralPath (Join-Path $denyDir $denyOld) -Algorithm SHA256).Hash -eq $preHash)

$unThrew = $false
try { Invoke-ASUninstall -InstallDir $denyDir -Root $dataC | Out-Null } catch { $unThrew = $true }
Assert-True 'uninstall rejects unowned non-empty' $unThrew
Assert-True 'uninstall left install files untouched' (@(Get-ChildItem -LiteralPath $denyDir -File -Force).Count -eq $preCount)

$riThrew = $false
try { Invoke-ASReinstall -CandidateDir $candDeny -InstallDir $denyDir -Root $dataC | Out-Null } catch { $riThrew = $true }
Assert-True 'reinstall rejects unowned non-empty' $riThrew
Assert-True 'reinstall left install files untouched' (@(Get-ChildItem -LiteralPath $denyDir -File -Force).Count -eq $preCount)

# 有效绑定备份在手，Restore-ASInstallBackup 仍须因所有权拒绝（fail-closed 优先于绑定）。
$bkpDir = Join-Path $sandbox 'c-backup'
New-Item -ItemType Directory -Force -Path $bkpDir, (Join-Path $bkpDir 'install') | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path (Join-Path $bkpDir 'install') $denyOld), [byte[]](1..2048 | ForEach-Object { ($_ % 251) }))
Set-Content -LiteralPath (Join-Path $bkpDir '_complete.marker') -Value (Get-Date -Format o) -Encoding UTF8
@{ schema = 1; app = 'AutoShutdown V2'; kind = 'install-rollback'; sourceInstallDir = (Resolve-ASPath $denyDir); hadInstall = $true; candidateFiles = @($denyNew) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $bkpDir 'backup.json') -Encoding UTF8
$rbThrew = $false
try { Restore-ASInstallBackup -InstallDir $denyDir -BackupDir $bkpDir | Out-Null } catch { $rbThrew = $true }
Assert-True 'rollback rejects unowned non-empty (even with valid binding)' $rbThrew
Assert-True 'rollback left install files untouched' (@(Get-ChildItem -LiteralPath $denyDir -File -Force).Count -eq $preCount)

# ============================================================
# D. 有效所有权下生命周期操作成功 + 清单式删除
# ============================================================
Write-Host "== D. valid ownership operations + manifest deletion =="
$ownDir = Join-Path $sandbox 'd-owned'
$candOwn = Join-Path $sandbox 'd-cand'
$dataD = Join-Path $sandbox 'd-data'
New-Item -ItemType Directory -Force -Path $ownDir, $candOwn, $dataD | Out-Null
$ownOld = 'AutoShutdown-v1.0.0-win-x64.exe'
$ownNew = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
[System.IO.File]::WriteAllBytes((Join-Path $ownDir $ownOld), [byte[]](1..1024 | ForEach-Object { ($_ % 251) }))
[System.IO.File]::WriteAllText((Join-Path $ownDir 'user-notes.txt'), 'user file', [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllBytes((Join-Path $candOwn $ownNew), [byte[]](255..0))
[System.IO.File]::WriteAllText((Join-Path $dataD 'config.json'), '{"SchemaVersion":1,"TestMode":true}', [System.Text.UTF8Encoding]::new($true))
# 所有权清单只声明应用 EXE，不声明 user-notes.txt（非应用文件必须保留）。
Write-ASOwnerMarker -InstallDir $ownDir -AppFiles $ownOld -CandidateName $ownOld

# D1: replace on owned dir（新 EXE 就位、陈旧旧版移除、非应用文件保留）
$repD = Replace-ASBinary -CandidateDir $candOwn -InstallDir $ownDir -Root $dataD -Tag 'd1'
Assert-True 'replace on owned dir succeeds' ($null -ne $repD)
Assert-True 'new exe installed' (Test-Path -LiteralPath (Join-Path $ownDir $ownNew))
Assert-True 'stale old exe removed' (-not (Test-Path -LiteralPath (Join-Path $ownDir $ownOld)))
Assert-True 'non-app user file preserved through replace' (Test-Path -LiteralPath (Join-Path $ownDir 'user-notes.txt'))
Assert-True 'owner marker refreshed after replace' ((Test-ASInstallOwnership $ownDir).Reason -eq 'owned')

# D2: reinstall on owned dir（只删清单文件，非应用文件保留）
$riD = Invoke-ASReinstall -CandidateDir $candOwn -InstallDir $ownDir -Root $dataD
Assert-True 'reinstall on owned dir succeeds' ($null -ne $riD)
Assert-True 'reinstall keeps user file' (Test-Path -LiteralPath (Join-Path $ownDir 'user-notes.txt'))
Assert-True 'reinstall restores exe + marker' ((Test-Path -LiteralPath (Join-Path $ownDir $ownNew)) -and (Test-Path -LiteralPath (Join-Path $ownDir 'AutoShutdown.owner.json')))

# D3: uninstall Keep on owned dir（清单式删除：应用 EXE 移除、非应用文件保留）
$uKeep = Invoke-ASUninstall -InstallDir $ownDir -Root $dataD -UserData 'Keep'
Assert-True 'uninstall Keep removes app exe' (-not (Test-Path -LiteralPath (Join-Path $ownDir $ownNew)))
Assert-True 'uninstall Keep preserves non-app user file' (Test-Path -LiteralPath (Join-Path $ownDir 'user-notes.txt'))

# D4: replace into nonexistent / empty dir（新安装放行并写所有权标记）
$fresh = Join-Path $sandbox 'd-fresh'
$repFresh = Replace-ASBinary -CandidateDir $candOwn -InstallDir $fresh -Root $dataD -Tag 'd1'
Assert-True 'fresh install succeeds' ($null -ne $repFresh)
Assert-True 'fresh install writes owner marker' (Test-Path -LiteralPath (Join-Path $fresh 'AutoShutdown.owner.json'))
Assert-True 'fresh install now owned' ((Test-ASInstallOwnership $fresh).Reason -eq 'owned')
$emptyD = Join-Path $sandbox 'd-empty'
New-Item -ItemType Directory -Force -Path $emptyD | Out-Null
$repEmpty = Replace-ASBinary -CandidateDir $candOwn -InstallDir $emptyD -Root $dataD -Tag 'd1'
Assert-True 'replace into empty dir succeeds' ($null -ne $repEmpty)
Assert-True 'empty-dir install now owned' ((Test-ASInstallOwnership $emptyD).Reason -eq 'owned')

# D5: uninstall Remove on owned dir + 应用标记的 spkg 备份（全部清理）
Backup-ASDataRoot -Root $dataD -Tag 'd1' | Out-Null
Assert-True 'spkg backups app-owned' (Test-ASSpkgBackupsOwned -DataRoot $dataD)
$ownDir2 = Join-Path $sandbox 'd-owned2'
New-Item -ItemType Directory -Force -Path $ownDir2 | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $ownDir2 $ownNew), [byte[]](255..0))
Write-ASOwnerMarker -InstallDir $ownDir2 -AppFiles $ownNew -CandidateName $ownNew
$uRem = Invoke-ASUninstall -InstallDir $ownDir2 -Root $dataD -UserData 'Remove'
Assert-True 'uninstall Remove clears app-owned backups' (-not (Test-Path -LiteralPath (Join-Path $dataD 'backups\spkg')))
Assert-True 'uninstall Remove removed owned exe' (-not (Test-Path -LiteralPath (Join-Path $ownDir2 $ownNew)))

# ============================================================
# E. DataRoot 保护（误指 / 非应用备份拒绝；危险数据根拒绝）
# ============================================================
Write-Host "== E. data-root misdirection protection =="
$dataE1 = Join-Path $sandbox 'e-data1'
$dataE2 = Join-Path $sandbox 'e-data2'
New-Item -ItemType Directory -Force -Path $dataE1, $dataE2 | Out-Null
[System.IO.File]::WriteAllText((Join-Path $dataE1 'config.json'), '{}', [System.Text.UTF8Encoding]::new($true))
# E1 创建备份根（owner.json 绑定 E1），再把 backups 原样拷到 E2 → E2 的 owner.json 仍绑定 E1
Backup-ASDataRoot -Root $dataE1 -Tag 'e1' | Out-Null
Copy-Item -LiteralPath (Join-Path $dataE1 'backups') -Destination (Join-Path $dataE2 'backups') -Recurse -Force
Assert-True 'E1 backups app-owned' (Test-ASSpkgBackupsOwned -DataRoot $dataE1)
Assert-True 'misbound spkg owner not owned' (-not (Test-ASSpkgBackupsOwned -DataRoot $dataE2))

# E2: uninstall Remove 对误指数据根拒绝，且不删除备份
$installE = Join-Path $sandbox 'e-install'
New-Item -ItemType Directory -Force -Path $installE | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $installE 'a.exe'), [byte[]](1..10))
Write-ASOwnerMarker -InstallDir $installE -AppFiles 'a.exe' -CandidateName 'a.exe'
$uEThrew = $false
try { Invoke-ASUninstall -InstallDir $installE -Root $dataE2 -UserData 'Remove' | Out-Null } catch { $uEThrew = $true }
Assert-True 'uninstall Remove refuses misbound spkg owner' $uEThrew
Assert-True 'misbound backups preserved (not deleted)' (Test-Path -LiteralPath (Join-Path $dataE2 'backups\spkg'))

# E3: 危险数据根（仓库根）拒绝备份，且不产生任何写入
$bThrew = $false
try { Backup-ASDataRoot -Root $root -Tag 'forbidden' | Out-Null } catch { $bThrew = $true }
Assert-True 'backup into repo root refused' $bThrew

# E4: backup.json 绑定校验——跨安装目录回滚拒绝
$bkpA = Join-Path $sandbox 'e-bkpA'
$installA = Join-Path $sandbox 'e-installA'
$installB = Join-Path $sandbox 'e-installB'
New-Item -ItemType Directory -Force -Path $bkpA, $installA, $installB | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $installA 'a.exe'), [byte[]](1..10))
[System.IO.File]::WriteAllBytes((Join-Path $installB 'b.exe'), [byte[]](1..10))
@{ schema = 1; app = 'AutoShutdown V2'; kind = 'install-rollback'; sourceInstallDir = (Resolve-ASPath $installA); hadInstall = $true; candidateFiles = @() } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $bkpA 'backup.json') -Encoding UTF8
$bindOk = Test-ASBackupBinding -BackupDir $bkpA -InstallDir $installA
Assert-True 'backup binding ok for matching install dir' $bindOk.Ok
$bindBad = Test-ASBackupBinding -BackupDir $bkpA -InstallDir $installB
Assert-True 'backup binding rejects different install dir' (-not $bindBad.Ok)
Assert-True 'backup binding reason = backup-path-mismatch' ($bindBad.Reason -eq 'backup-path-mismatch')

# ============================================================
# F. 嵌套回归：候选含子目录且安装槽已有同名子目录 → 逐子项、逐级合并复制，
#    绝不 Copy-Item <源子目录> -Destination <已存在同名子目录> 造成 <目标>\<同名>\… 嵌套
#    （B-4 冒烟暴露的真 bug：候选 win-x64-framework-dependent 目录被嵌套成
#     win-x64-framework-dependent\win-x64-framework-dependent，卸载后成为孤儿残留。）
# ============================================================
Write-Host "== F. nesting regression (candidate subdir onto existing same-name subdir) =="
$nestDir = Join-Path $sandbox 'f-install'
$candNest = Join-Path $sandbox 'f-cand'
$dataF = Join-Path $sandbox 'f-data'
New-Item -ItemType Directory -Force -Path $nestDir, $candNest, $dataF | Out-Null
$nestSub = Join-Path $nestDir 'win-x64-framework-dependent'
$candSub = Join-Path $candNest 'win-x64-framework-dependent'
New-Item -ItemType Directory -Force -Path $nestSub, $candSub, (Join-Path $candSub 'zh-Hant') | Out-Null
$nestOld = 'AutoShutdown-v1.0.0-win-x64.exe'
$nestNew = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
[System.IO.File]::WriteAllBytes((Join-Path $nestSub 'existing.dll'), [byte[]](1..64))
[System.IO.File]::WriteAllBytes((Join-Path $nestDir $nestOld), [byte[]](65..128))
[System.IO.File]::WriteAllBytes((Join-Path $candSub 'candidate.dll'), [byte[]](129..192))
[System.IO.File]::WriteAllBytes((Join-Path $candSub 'zh-Hant\local.resources.dll'), [byte[]](193..255))
[System.IO.File]::WriteAllBytes((Join-Path $candNest $nestNew), [byte[]](1..32))
Write-ASOwnerMarker -InstallDir $nestDir -AppFiles @($nestOld, 'win-x64-framework-dependent\existing.dll') -CandidateName $nestOld
$repF = Replace-ASBinary -CandidateDir $candNest -InstallDir $nestDir -Root $dataF -Tag 'nest'
Assert-True 'replace with subdir candidate succeeds' ($null -ne $repF)
Assert-True 'no double-nesting of subdir' (-not (Test-Path -LiteralPath (Join-Path $nestSub 'win-x64-framework-dependent')))
Assert-True 'candidate file merged into existing subdir' (Test-Path -LiteralPath (Join-Path $nestSub 'candidate.dll'))
Assert-True 'pre-existing subdir file preserved' (Test-Path -LiteralPath (Join-Path $nestSub 'existing.dll'))
Assert-True 'nested localized subdir file present' (Test-Path -LiteralPath (Join-Path $nestSub 'zh-Hant\local.resources.dll'))
Assert-True 'ownership ok after subdir merge' ((Test-ASInstallOwnership $nestDir).Reason -eq 'owned')

# ---- cleanup ----
Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("D1 OWNERSHIP TESTS: pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
exit 0
