#Requires -Version 5.1
# tools/test/Invoke-SPD2ReparseTests.ps1
# S-PKG D2 最小返修装置：安装/候选/备份/DataRoot 中的 junction/symlink/reparse point 越界防护。
# 纯文件操作，临时沙箱（$env:TEMP\as-spkg-d2-*），不触碰真实系统。
# 用 cmd /c mklink /J 建临时目录 junction（不依赖开发者模式符号链接权限）。
# 覆盖（D2 检查点）：
#   A. 统一守卫函数（Test-ASPathWithinRoot / Get-ASRelPathReparsePoint / Get-ASDirTreeSafe）
#   B. Remove-ASOwnedFiles 直接注入 junction 相对路径 -> 拒绝，目录外哨兵/哈希不变
#   C. uninstall（Keep）：已拥有安装目录含 junction + 攻击性 appFiles -> 拒绝，目录外哨兵/哈希不变
#   D. reinstall：同样布局 -> 拒绝，目录外哨兵/哈希不变
#   E. replace：同样布局（含安装备份路径）-> 拒绝，目录外哨兵/哈希不变；备份槽不得泄漏外部内容
#   F. rollback：Restore-ASInstallBackup / Restore-ASRollback -> 拒绝，目录外哨兵/哈希不变
#   G. 候选目录含 junction -> 复制拒绝并自动回滚；DataRoot 含 junction -> 备份/Remove 拒绝
#   H. 正常（无 junction）路径回归：清单删除/合并复制/枚举仍工作
# Exit: 0 = all pass; 1 = failures.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'SPkg-Lifecycle.ps1')

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("as-spkg-d2-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$pass = 0; $fail = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}

# ---- 工具 ----
function New-Junction {
    param([string]$LinkPath, [string]$TargetPath)
    & cmd /c mklink /J $LinkPath $TargetPath | Out-Null
    if (-not (Test-Path -LiteralPath $LinkPath)) { throw "failed to create junction: $LinkPath -> $TargetPath" }
    $it = Get-Item -LiteralPath $LinkPath -Force
    if (-not ($it.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { throw "created link is not a reparse point: $LinkPath" }
}

function Get-DirHashSnapshot {
    param([string]$Dir)
    $snap = @()
    if (Test-Path -LiteralPath $Dir) {
        $base = Resolve-ASPath $Dir
        Get-ChildItem -LiteralPath $base -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
            $snap += [pscustomobject]@{
                Rel = $_.FullName.Substring($base.Length).TrimStart('\', '/')
                Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                Size = $_.Length
            }
        }
    }
    # 一元逗号：强制把结果数组作为单个对象输出，避免单元素数组被管道解包。
    return ,[object[]]($snap | Sort-Object Rel)
}

function Test-DirSnapshotEqual {
    param([string]$Dir, [object[]]$Expected)
    $cur = Get-DirHashSnapshot -Dir $Dir
    if ($cur.Count -ne @($Expected).Count) { return $false }
    for ($i = 0; $i -lt $cur.Count; $i++) {
        if ($cur[$i].Rel -ne $Expected[$i].Rel -or $cur[$i].Hash -ne $Expected[$i].Hash -or $cur[$i].Size -ne $Expected[$i].Size) { return $false }
    }
    return $true
}

# 恶意已拥有安装：真实 EXE + 有效所有权标记 + install\evil junction -> 目录外（哨兵文件 + 深层子目录）。
function New-MaliciousInstall {
    param([string]$InstallDir, [string]$OutsideDir, [string]$ExeName)
    New-Item -ItemType Directory -Force -Path $InstallDir, $OutsideDir | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $InstallDir $ExeName), [byte[]](255..128))
    Set-Content -LiteralPath (Join-Path $OutsideDir 'sentinel.bin') -Value 'OUTSIDE-SENTINEL-DATA' -Encoding UTF8
    New-Item -ItemType Directory -Force -Path (Join-Path $OutsideDir 'sub') | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path (Join-Path $OutsideDir 'sub') 'deep.dat'), [byte[]](1..2048 | ForEach-Object { ($_ % 251) }))
    New-Junction -LinkPath (Join-Path $InstallDir 'evil') -TargetPath $OutsideDir
    # 攻击性所有权清单：显式把 junction 之下的目录外文件声明为「应用文件」。
    Write-ASOwnerMarker -InstallDir $InstallDir -AppFiles @($ExeName, 'evil\sentinel.bin', 'evil\sub\deep.dat') -CandidateName $ExeName
}

function New-CleanCandidate {
    param([string]$CandidateDir, [string]$ExeName)
    New-Item -ItemType Directory -Force -Path $CandidateDir | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $CandidateDir $ExeName), [byte[]](1..96))
}

# ============================================================
# A. 统一守卫函数
# ============================================================
Write-Host "== A. unified within-root + no-reparse guard functions =="
$rootA = Join-Path $sandbox 'a-root'
$outA = Join-Path $sandbox 'a-out'
New-Item -ItemType Directory -Force -Path $rootA, $outA | Out-Null
Set-Content -LiteralPath (Join-Path $rootA 'ok.txt') -Value 'ok' -Encoding UTF8
New-Junction -LinkPath (Join-Path $rootA 'lnk') -TargetPath $outA

$w1 = Test-ASPathWithinRoot -Root $rootA -Path (Join-Path $rootA 'lnk\x.txt')
Assert-True 'A: junction in chain -> reparse-point' ($w1.Reason -eq 'reparse-point' -and -not $w1.Ok)
$w2 = Test-ASPathWithinRoot -Root $rootA -Path (Join-Path $rootA 'ok.txt')
Assert-True 'A: normal file within root -> ok' ($w2.Ok -and $w2.Reason -eq 'ok')
# 前缀误判（根目录名是另一路径的前缀）：C:\a\install 与 C:\a\install-evil 必须区分。
$rootPref = Join-Path $sandbox 'a-install'
$evilPref = Join-Path $sandbox 'a-install-evil'
New-Item -ItemType Directory -Force -Path $rootPref, $evilPref | Out-Null
$w3 = Test-ASPathWithinRoot -Root $rootPref -Path (Join-Path $evilPref 'x.txt')
Assert-True 'A: sibling with root-name prefix rejected (not-within-root)' ($w3.Reason -eq 'not-within-root')
$rp1 = Get-ASRelPathReparsePoint -BaseDir $rootA -RelativePath 'lnk\sentinel.bin'
Assert-True 'A: rel chain reparse detected (returns junction path)' ($null -ne $rp1 -and $rp1 -ne 'TRAVERSAL')
$rp2 = Get-ASRelPathReparsePoint -BaseDir $rootA -RelativePath 'ok.txt'
Assert-True 'A: rel chain normal -> null' ($null -eq $rp2)
$rp3 = Get-ASRelPathReparsePoint -BaseDir $rootA -RelativePath '..\escape.txt'
Assert-True 'A: rel .. traversal -> TRAVERSAL' ($rp3 -eq 'TRAVERSAL')
$rp4 = Get-ASRelPathReparsePoint -BaseDir $rootA -RelativePath 'C:\Windows\evil'
Assert-True 'A: rooted rel -> TRAVERSAL' ($rp4 -eq 'TRAVERSAL')
$treeA = Get-ASDirTreeSafe -BaseDir $rootA
Assert-True 'A: tree safe scan rejects junction (Ok=false)' (-not $treeA.Ok)
Assert-True 'A: tree safe scan reports junction path' ($null -ne $treeA.ReparsePath)

# ============================================================
# B. Remove-ASOwnedFiles 直接注入 junction 相对路径（原始漏洞路径）
# ============================================================
Write-Host "== B. Remove-ASOwnedFiles refuses junction rel path (original vuln) =="
$installB = Join-Path $sandbox 'b-install'
$outsideB = Join-Path $sandbox 'b-outside'
$exeB = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
New-MaliciousInstall -InstallDir $installB -OutsideDir $outsideB -ExeName $exeB
$snapBOut = Get-DirHashSnapshot -Dir $outsideB
$snapBIn = Get-DirHashSnapshot -Dir $installB
$gateB = Test-ASInstallOwnership -InstallDir $installB
Assert-True 'B: ownership gate sees reparse-point (not no-marker)' ($gateB.Reason -eq 'reparse-point')
$bThrew = $false
try { Remove-ASOwnedFiles -InstallDir $installB -AppFiles @('evil\sentinel.bin') | Out-Null } catch { $bThrew = $true }
Assert-True 'B: Remove-ASOwnedFiles throws on junction rel path' $bThrew
Assert-True 'B: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideB -Expected $snapBOut)
Assert-True 'B: junction still present' (Test-Path -LiteralPath (Join-Path $installB 'evil'))
Assert-True 'B: install files unchanged' (Test-DirSnapshotEqual -Dir $installB -Expected $snapBIn)

# ============================================================
# C. uninstall（Keep）
# ============================================================
Write-Host "== C. uninstall refuses when install dir contains junction =="
$installC = Join-Path $sandbox 'c-install'
$outsideC = Join-Path $sandbox 'c-outside'
$dataC = Join-Path $sandbox 'c-data'
New-MaliciousInstall -InstallDir $installC -OutsideDir $outsideC -ExeName $exeB
$snapCOut = Get-DirHashSnapshot -Dir $outsideC
$snapCIn = Get-DirHashSnapshot -Dir $installC
$cThrew = $false
try { Invoke-ASUninstall -InstallDir $installC -Root $dataC -UserData 'Keep' | Out-Null } catch { $cThrew = $true }
Assert-True 'C: uninstall throws (reparse under install dir)' $cThrew
Assert-True 'C: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideC -Expected $snapCOut)
Assert-True 'C: install dir unchanged' (Test-DirSnapshotEqual -Dir $installC -Expected $snapCIn)
Assert-True 'C: junction still present' (Test-Path -LiteralPath (Join-Path $installC 'evil'))

# ============================================================
# D. reinstall
# ============================================================
Write-Host "== D. reinstall refuses when install dir contains junction =="
$installD = Join-Path $sandbox 'd-install'
$outsideD = Join-Path $sandbox 'd-outside'
$dataD = Join-Path $sandbox 'd-data'
$candD = Join-Path $sandbox 'd-cand'
New-MaliciousInstall -InstallDir $installD -OutsideDir $outsideD -ExeName $exeB
New-CleanCandidate -CandidateDir $candD -ExeName 'AutoShutdown-v2.0.0-S23.exe'
$snapDOut = Get-DirHashSnapshot -Dir $outsideD
$snapDIn = Get-DirHashSnapshot -Dir $installD
$dThrew = $false
try { Invoke-ASReinstall -CandidateDir $candD -InstallDir $installD -Root $dataD | Out-Null } catch { $dThrew = $true }
Assert-True 'D: reinstall throws (reparse under install dir)' $dThrew
Assert-True 'D: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideD -Expected $snapDOut)
Assert-True 'D: install dir unchanged' (Test-DirSnapshotEqual -Dir $installD -Expected $snapDIn)
Assert-True 'D: junction still present' (Test-Path -LiteralPath (Join-Path $installD 'evil'))

# ============================================================
# E. replace（含安装备份路径）
# ============================================================
Write-Host "== E. replace refuses when install dir contains junction =="
$installE = Join-Path $sandbox 'e-install'
$outsideE = Join-Path $sandbox 'e-outside'
$dataE = Join-Path $sandbox 'e-data'
$candE = Join-Path $sandbox 'e-cand'
New-MaliciousInstall -InstallDir $installE -OutsideDir $outsideE -ExeName $exeB
New-CleanCandidate -CandidateDir $candE -ExeName 'AutoShutdown-v2.0.0-S23.exe'
$snapEOut = Get-DirHashSnapshot -Dir $outsideE
$snapEIn = Get-DirHashSnapshot -Dir $installE
$eThrew = $false
try { Replace-ASBinary -CandidateDir $candE -InstallDir $installE -Root $dataE -Tag 'd2e' | Out-Null } catch { $eThrew = $true }
Assert-True 'E: replace throws (reparse under install dir)' $eThrew
Assert-True 'E: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideE -Expected $snapEOut)
Assert-True 'E: install dir unchanged' (Test-DirSnapshotEqual -Dir $installE -Expected $snapEIn)
Assert-True 'E: junction still present' (Test-Path -LiteralPath (Join-Path $installE 'evil'))
$slotE = Join-Path $dataE 'backups\spkg\rollback-install'
Assert-True 'E: backup slot did not leak outside content' (-not (Test-Path -LiteralPath (Join-Path $slotE 'install\evil\sentinel.bin')))

# ============================================================
# F. rollback（Restore-ASInstallBackup / Restore-ASRollback）
# ============================================================
Write-Host "== F. rollback refuses when install dir contains junction =="
$installF = Join-Path $sandbox 'f-install'
$outsideF = Join-Path $sandbox 'f-outside'
$dataF = Join-Path $sandbox 'f-data'
New-MaliciousInstall -InstallDir $installF -OutsideDir $outsideF -ExeName $exeB
# 有效绑定备份槽（backup.json 绑定本绝对路径 + _complete.marker + saved\<install 名>）
$slotF = Join-Path $dataF 'backups\spkg\rollback-install'
New-Item -ItemType Directory -Force -Path $slotF, (Join-Path $slotF 'install') | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path (Join-Path $slotF 'install') $exeB), [byte[]](10..20))
Set-Content -LiteralPath (Join-Path $slotF '_complete.marker') -Value (Get-Date -Format o) -Encoding UTF8
@{ schema = 1; app = 'AutoShutdown V2'; kind = 'install-rollback'; sourceInstallDir = (Resolve-ASPath $installF); hadInstall = $true; candidateFiles = @('AutoShutdown-v2.0.0-S23.exe') } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $slotF 'backup.json') -Encoding UTF8
$snapFOut = Get-DirHashSnapshot -Dir $outsideF
$snapFIn = Get-DirHashSnapshot -Dir $installF
$fThrew = $false
try { Restore-ASInstallBackup -InstallDir $installF -BackupDir $slotF | Out-Null } catch { $fThrew = $true }
Assert-True 'F1: Restore-ASInstallBackup throws (reparse under install dir)' $fThrew
Assert-True 'F1: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideF -Expected $snapFOut)
Assert-True 'F1: install dir unchanged' (Test-DirSnapshotEqual -Dir $installF -Expected $snapFIn)

# F2: Restore-ASRollback（数据备份存在 + 恶意安装目录）
$dataF2 = Join-Path $sandbox 'f2-data'
$installF2 = Join-Path $sandbox 'f2-install'
$outsideF2 = Join-Path $sandbox 'f2-outside'
New-Item -ItemType Directory -Force -Path $dataF2 | Out-Null
Set-Content -LiteralPath (Join-Path $dataF2 'config.json') -Value '{"SchemaVersion":1,"TestMode":true}' -Encoding UTF8
Backup-ASDataRoot -Root $dataF2 -Tag 'upgrade' | Out-Null
New-MaliciousInstall -InstallDir $installF2 -OutsideDir $outsideF2 -ExeName $exeB
$snapF2Out = Get-DirHashSnapshot -Dir $outsideF2
$snapF2In = Get-DirHashSnapshot -Dir $installF2
$f2Threw = $false
try { Restore-ASRollback -InstallDir $installF2 -Root $dataF2 -Tag 'upgrade' | Out-Null } catch { $f2Threw = $true }
Assert-True 'F2: Restore-ASRollback throws (reparse under install dir)' $f2Threw
Assert-True 'F2: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideF2 -Expected $snapF2Out)
Assert-True 'F2: install dir unchanged' (Test-DirSnapshotEqual -Dir $installF2 -Expected $snapF2In)

# ============================================================
# G. 候选目录 / DataRoot 含 junction
# ============================================================
Write-Host "== G. candidate / DataRoot junction refusal =="
# G1: 候选目录含 junction -> replace 复制拒绝并自动回滚；外部不变；安装目录恢复替换前状态。
$installG1 = Join-Path $sandbox 'g1-install'
$outsideG1 = Join-Path $sandbox 'g1-outside'
$dataG1 = Join-Path $sandbox 'g1-data'
$candG1 = Join-Path $sandbox 'g1-cand'
New-Item -ItemType Directory -Force -Path $installG1, $candG1, $outsideG1 | Out-Null
$g1Old = 'AutoShutdown-v1.0.0-win-x64.exe'
$g1New = 'AutoShutdown-v2.0.0-S23.exe'
[System.IO.File]::WriteAllBytes((Join-Path $installG1 $g1Old), [byte[]](200..255))
Write-ASOwnerMarker -InstallDir $installG1 -AppFiles $g1Old -CandidateName $g1Old
New-CleanCandidate -CandidateDir $candG1 -ExeName $g1New
New-Junction -LinkPath (Join-Path $candG1 'evil') -TargetPath $outsideG1
Set-Content -LiteralPath (Join-Path $outsideG1 'sentinel.bin') -Value 'G1-OUTSIDE' -Encoding UTF8
$snapG1Out = Get-DirHashSnapshot -Dir $outsideG1
$snapG1In = Get-DirHashSnapshot -Dir $installG1
$g1Threw = $false
try { Replace-ASBinary -CandidateDir $candG1 -InstallDir $installG1 -Root $dataG1 -Tag 'd2g1' | Out-Null } catch { $g1Threw = $true }
Assert-True 'G1: replace with junction in candidate throws' $g1Threw
Assert-True 'G1: candidate junction target (outside) unchanged' (Test-DirSnapshotEqual -Dir $outsideG1 -Expected $snapG1Out)
Assert-True 'G1: install dir rolled back to pre-replace state' (Test-DirSnapshotEqual -Dir $installG1 -Expected $snapG1In)
Assert-True 'G1: no junction copy landed in install' (-not (Test-Path -LiteralPath (Join-Path $installG1 'evil')))

# G2: DataRoot 顶层含 junction -> Backup-ASDataRoot 拒绝，不产生写入。
$dataG2 = Join-Path $sandbox 'g2-data'
$outsideG2 = Join-Path $sandbox 'g2-outside'
New-Item -ItemType Directory -Force -Path $dataG2, $outsideG2 | Out-Null
Set-Content -LiteralPath (Join-Path $dataG2 'config.json') -Value '{}' -Encoding UTF8
New-Junction -LinkPath (Join-Path $dataG2 'evil') -TargetPath $outsideG2
Set-Content -LiteralPath (Join-Path $outsideG2 'sentinel.bin') -Value 'G2-OUTSIDE' -Encoding UTF8
$snapG2Out = Get-DirHashSnapshot -Dir $outsideG2
$g2Threw = $false
try { Backup-ASDataRoot -Root $dataG2 -Tag 'd2g2' | Out-Null } catch { $g2Threw = $true }
Assert-True 'G2: backup refuses when DataRoot contains junction' $g2Threw
Assert-True 'G2: DataRoot junction target unchanged' (Test-DirSnapshotEqual -Dir $outsideG2 -Expected $snapG2Out)
Assert-True 'G2: no backups created under junctioned DataRoot' (-not (Test-Path -LiteralPath (Join-Path $dataG2 'backups')))

# G3: DataRoot\backups 是 junction -> 备份创建备份根前拒绝。
$dataG3 = Join-Path $sandbox 'g3-data'
$outsideG3 = Join-Path $sandbox 'g3-outside'
New-Item -ItemType Directory -Force -Path $dataG3, $outsideG3 | Out-Null
Set-Content -LiteralPath (Join-Path $dataG3 'config.json') -Value '{}' -Encoding UTF8
New-Junction -LinkPath (Join-Path $dataG3 'backups') -TargetPath $outsideG3
Set-Content -LiteralPath (Join-Path $outsideG3 'sentinel.bin') -Value 'G3-OUTSIDE' -Encoding UTF8
$snapG3Out = Get-DirHashSnapshot -Dir $outsideG3
$g3Threw = $false
try { Backup-ASDataRoot -Root $dataG3 -Tag 'd2g3' | Out-Null } catch { $g3Threw = $true }
Assert-True 'G3: backup refuses when DataRoot\backups is a junction' $g3Threw
Assert-True 'G3: backups-junction target unchanged' (Test-DirSnapshotEqual -Dir $outsideG3 -Expected $snapG3Out)

# G4: 备份根内含 junction -> uninstall UserData=Remove 拒绝且不删除外部内容。
$dataG4 = Join-Path $sandbox 'g4-data'
$installG4 = Join-Path $sandbox 'g4-install'
$outsideG4 = Join-Path $sandbox 'g4-outside'
New-Item -ItemType Directory -Force -Path $dataG4, $installG4, $outsideG4 | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $installG4 $exeB), [byte[]](11..33))
Write-ASOwnerMarker -InstallDir $installG4 -AppFiles $exeB -CandidateName $exeB
Backup-ASDataRoot -Root $dataG4 -Tag 'd2g4' | Out-Null
New-Junction -LinkPath (Join-Path $dataG4 'backups\spkg\evil') -TargetPath $outsideG4
Set-Content -LiteralPath (Join-Path $outsideG4 'sentinel.bin') -Value 'G4-OUTSIDE' -Encoding UTF8
$snapG4Out = Get-DirHashSnapshot -Dir $outsideG4
$g4Threw = $false
try { Invoke-ASUninstall -InstallDir $installG4 -Root $dataG4 -UserData 'Remove' | Out-Null } catch { $g4Threw = $true }
Assert-True 'G4: uninstall Remove refuses when backups root contains junction' $g4Threw
Assert-True 'G4: backups junction target unchanged' (Test-DirSnapshotEqual -Dir $outsideG4 -Expected $snapG4Out)

# ============================================================
# H. 正常（无 junction）路径回归
# ============================================================
Write-Host "== H. normal (junction-free) path regression =="
$installH = Join-Path $sandbox 'h-install'
$candH = Join-Path $sandbox 'h-cand'
$dataH = Join-Path $sandbox 'h-data'
New-Item -ItemType Directory -Force -Path $installH, $candH, $dataH | Out-Null
$hOld = 'AutoShutdown-v1.0.0-win-x64.exe'
$hNew = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
[System.IO.File]::WriteAllBytes((Join-Path $installH $hOld), [byte[]](1..64))
[System.IO.File]::WriteAllText((Join-Path $installH 'user-notes.txt'), 'user file', [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllBytes((Join-Path $candH $hNew), [byte[]](65..96))
Write-ASOwnerMarker -InstallDir $installH -AppFiles $hOld -CandidateName $hOld
$repH = Replace-ASBinary -CandidateDir $candH -InstallDir $installH -Root $dataH -Tag 'd2h'
Assert-True 'H: normal replace succeeds' ($null -ne $repH)
Assert-True 'H: new exe installed' (Test-Path -LiteralPath (Join-Path $installH $hNew))
Assert-True 'H: user file preserved' (Test-Path -LiteralPath (Join-Path $installH 'user-notes.txt'))
Assert-True 'H: install dir still owned (no false reparse rejection)' ((Test-ASInstallOwnership $installH).Reason -eq 'owned')
$uH = Invoke-ASUninstall -InstallDir $installH -Root $dataH -UserData 'Keep'
Assert-True 'H: normal uninstall Keep succeeds' ($null -ne $uH)
Assert-True 'H: app exe removed' (-not (Test-Path -LiteralPath (Join-Path $installH $hNew)))
Assert-True 'H: user file preserved after uninstall' (Test-Path -LiteralPath (Join-Path $installH 'user-notes.txt'))

# ---- cleanup ----
Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("D2 REPARSE TESTS: pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
exit 0
