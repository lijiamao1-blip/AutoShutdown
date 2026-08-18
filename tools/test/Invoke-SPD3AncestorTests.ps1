#Requires -Version 5.1
# tools/test/Invoke-SPD3AncestorTests.ps1
# S-PKG D3 最小返修装置：父链 junction（卷根→目标祖先链）越界防护。
# 纯文件操作，临时沙箱（$env:TEMP\as-spkg-d3-*），不触碰真实系统。
# 用 cmd /c mklink /J 建「父目录 junction -> outside」，传入 alias\install
# （install 本身不是 reparse point）——复现 D2 遗漏：卷根→InstallDir 的祖先链含 junction。
# 覆盖（D3 检查点）：
#   A. Test-ASFullChainSafe 守卫函数（卷根逐分量 / 词法路径 / 不存在目标查至最后已存在祖先）
#   B. ownership：alias 下安装目录（含经 alias 绑定的所有权标记）-> 门禁拒绝（reparse-point）
#   C. uninstall：同布局 -> 拒绝，outside 哨兵文件与 SHA-256 完全不变
#   D. reinstall：同布局 -> 拒绝，outside 哨兵文件与 SHA-256 完全不变
#   E. replace：同布局（含安装备份路径）-> 拒绝，outside 哨兵文件与 SHA-256 完全不变；无备份槽泄漏
#   F. rollback：Restore-ASInstallBackup / Restore-ASRollback -> 拒绝，outside 哨兵文件与 SHA-256 不变
#   G. CandidateDir 位于父 junction 下 -> replace/reinstall 拒绝，outside 不变
#   H. DataRoot 位于父 junction 下 -> Backup-ASDataRoot / uninstall(Remove) 拒绝，outside 不变
#   I. 不存在的目标位于祖先 junction 下 -> 门禁/替换拒绝，且不在 outside 创建任何目录
#   J. 正常（无 junction）路径回归：清单删除/替换/卸载仍工作，无假阳性
# Exit: 0 = all pass; 1 = failures.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'SPkg-Lifecycle.ps1')

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("as-spkg-d3-" + [guid]::NewGuid().ToString('N'))
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
    $snap = New-Object System.Collections.Generic.List[object]
    if (Test-Path -LiteralPath $Dir) {
        $base = Resolve-ASPath $Dir
        Get-ChildItem -LiteralPath $base -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
            $snap.Add([pscustomobject]@{
                Rel = $_.FullName.Substring($base.Length).TrimStart('\', '/')
                Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                Size = $_.Length
            })
        }
    }
    # 排序后的 object[]；空目录返回 0 长度数组（一元逗号防管道解包）。
    return ,@($snap.ToArray() | Sort-Object Rel)
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

# 恶意祖先安装（D3 攻击者视角）：outside\<install> 是真实目录（本身非 reparse point）；
# alias junction -> outside；伪造 owner.json，installDir 绑定词法 alias\install。
function New-AncestorInstall {
    param([string]$AliasPath, [string]$OutsideDir, [string]$InstallName, [string]$ExeName)
    $install = Join-Path $OutsideDir $InstallName
    New-Item -ItemType Directory -Force -Path $install | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $install $ExeName), [byte[]](255..128))
    Set-Content -LiteralPath (Join-Path $install 'sentinel.bin') -Value 'ANCESTOR-OUTSIDE-SENTINEL' -Encoding UTF8
    New-Junction -LinkPath $AliasPath -TargetPath $OutsideDir
    $installViaAlias = Join-Path $AliasPath $InstallName
    @{ schema = 1; app = 'AutoShutdown V2'; installDir = (Resolve-ASPath $installViaAlias); appFiles = @($ExeName, 'sentinel.bin'); candidate = $ExeName; created = (Get-Date -Format o) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $installViaAlias 'AutoShutdown.owner.json') -Encoding UTF8
    return $installViaAlias
}

function New-CleanCandidate {
    param([string]$CandidateDir, [string]$ExeName)
    New-Item -ItemType Directory -Force -Path $CandidateDir | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $CandidateDir $ExeName), [byte[]](1..96))
}

$exeB = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'

# ============================================================
# A. Test-ASFullChainSafe 守卫函数
# ============================================================
Write-Host "== A. full-chain (volume root -> target) reparse guard =="
$outA = Join-Path $sandbox 'a-out'
$aliasA = Join-Path $sandbox 'a-alias'
New-Item -ItemType Directory -Force -Path $outA | Out-Null
New-Junction -LinkPath $aliasA -TargetPath $outA
$installA = Join-Path $aliasA 'install'
New-Item -ItemType Directory -Force -Path $installA | Out-Null
Set-Content -LiteralPath (Join-Path $installA 'ok.txt') -Value 'ok' -Encoding UTF8

$c1 = Test-ASFullChainSafe -Path $installA
Assert-True 'A: ancestor junction in chain -> reparse-point' ($c1.Reason -eq 'reparse-point' -and -not $c1.Ok)
Assert-True 'A: reports the alias junction itself' ($c1.ReparsePath -eq $aliasA)
$c2 = Test-ASFullChainSafe -Path $outA
Assert-True 'A: junction-free path -> ok' ($c2.Ok -and $c2.Reason -eq 'ok')
$c3 = Test-ASFullChainSafe -Path (Join-Path $aliasA 'nope\deep\file.txt')
Assert-True 'A: non-existent target under ancestor junction -> reparse-point' ($c3.Reason -eq 'reparse-point')
$w1 = Test-ASPathWithinRoot -Root $installA -Path (Join-Path $installA 'ok.txt')
Assert-True 'A: Test-ASPathWithinRoot rejects ancestor junction' ($w1.Reason -eq 'reparse-point' -and -not $w1.Ok)
$t1 = Get-ASDirTreeSafe -BaseDir $installA
Assert-True 'A: Get-ASDirTreeSafe rejects ancestor junction' (-not $t1.Ok)
Assert-True 'A: Get-ASDirTreeSafe reports alias junction' ($t1.ReparsePath -eq $aliasA)
$r1 = Get-ASRelPathReparsePoint -BaseDir $installA -RelativePath 'ok.txt'
Assert-True 'A: Get-ASRelPathReparsePoint rejects unsafe base chain' ($null -ne $r1)

# ============================================================
# B. ownership 门禁：alias 下的安装目录
# ============================================================
Write-Host "== B. ownership gate rejects install dir under ancestor junction =="
$installB = New-AncestorInstall -AliasPath (Join-Path $sandbox 'b-alias') -OutsideDir (Join-Path $sandbox 'b-out') -InstallName 'install' -ExeName $exeB
$outsideB = Join-Path $sandbox 'b-out'
$snapB = Get-DirHashSnapshot -Dir $outsideB
$gateB = Test-ASInstallOwnership -InstallDir $installB
Assert-True 'B: ownership gate rejects (reparse-point, not owned)' ($gateB.Reason -eq 'reparse-point' -and -not $gateB.Ok)
Assert-True 'B: outside sentinel + hashes unchanged after gate probe' (Test-DirSnapshotEqual -Dir $outsideB -Expected $snapB)

# ============================================================
# C. uninstall：同布局
# ============================================================
Write-Host "== C. uninstall refuses when install dir is under ancestor junction =="
$installC = New-AncestorInstall -AliasPath (Join-Path $sandbox 'c-alias') -OutsideDir (Join-Path $sandbox 'c-out') -InstallName 'install' -ExeName $exeB
$outsideC = Join-Path $sandbox 'c-out'
$dataC = Join-Path $sandbox 'c-data'
$snapC = Get-DirHashSnapshot -Dir $outsideC
$cThrew = $false
try { Invoke-ASUninstall -InstallDir $installC -Root $dataC -UserData 'Keep' | Out-Null } catch { $cThrew = $true }
Assert-True 'C: uninstall throws (ancestor junction)' $cThrew
Assert-True 'C: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideC -Expected $snapC)

# ============================================================
# D. reinstall：同布局
# ============================================================
Write-Host "== D. reinstall refuses when install dir is under ancestor junction =="
$installD = New-AncestorInstall -AliasPath (Join-Path $sandbox 'd-alias') -OutsideDir (Join-Path $sandbox 'd-out') -InstallName 'install' -ExeName $exeB
$outsideD = Join-Path $sandbox 'd-out'
$dataD = Join-Path $sandbox 'd-data'
$candD = Join-Path $sandbox 'd-cand'
New-CleanCandidate -CandidateDir $candD -ExeName 'AutoShutdown-v2.0.0-S23.exe'
$snapD = Get-DirHashSnapshot -Dir $outsideD
$dThrew = $false
try { Invoke-ASReinstall -CandidateDir $candD -InstallDir $installD -Root $dataD | Out-Null } catch { $dThrew = $true }
Assert-True 'D: reinstall throws (ancestor junction)' $dThrew
Assert-True 'D: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideD -Expected $snapD)

# ============================================================
# E. replace（含安装备份路径）
# ============================================================
Write-Host "== E. replace refuses when install dir is under ancestor junction =="
$installE = New-AncestorInstall -AliasPath (Join-Path $sandbox 'e-alias') -OutsideDir (Join-Path $sandbox 'e-out') -InstallName 'install' -ExeName $exeB
$outsideE = Join-Path $sandbox 'e-out'
$dataE = Join-Path $sandbox 'e-data'
$candE = Join-Path $sandbox 'e-cand'
New-CleanCandidate -CandidateDir $candE -ExeName 'AutoShutdown-v2.0.0-S23.exe'
$snapE = Get-DirHashSnapshot -Dir $outsideE
$eThrew = $false
try { Replace-ASBinary -CandidateDir $candE -InstallDir $installE -Root $dataE -Tag 'd3e' | Out-Null } catch { $eThrew = $true }
Assert-True 'E: replace throws (ancestor junction)' $eThrew
Assert-True 'E: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideE -Expected $snapE)
Assert-True 'E: no backup slot created under data root' (-not (Test-Path -LiteralPath (Join-Path $dataE 'backups\spkg')))

# ============================================================
# F. rollback
# ============================================================
Write-Host "== F. rollback refuses when install dir is under ancestor junction =="
# F1: Restore-ASInstallBackup（backup.json 绑定词法 alias 路径 + _complete.marker + saved）
$installF = New-AncestorInstall -AliasPath (Join-Path $sandbox 'f-alias') -OutsideDir (Join-Path $sandbox 'f-out') -InstallName 'install' -ExeName $exeB
$outsideF = Join-Path $sandbox 'f-out'
$slotF = Join-Path $sandbox 'f-slot'
New-Item -ItemType Directory -Force -Path $slotF, (Join-Path $slotF 'install') | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path (Join-Path $slotF 'install') $exeB), [byte[]](10..20))
Set-Content -LiteralPath (Join-Path $slotF '_complete.marker') -Value (Get-Date -Format o) -Encoding UTF8
@{ schema = 1; app = 'AutoShutdown V2'; kind = 'install-rollback'; sourceInstallDir = (Resolve-ASPath $installF); hadInstall = $true; candidateFiles = @('AutoShutdown-v2.0.0-S23.exe') } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $slotF 'backup.json') -Encoding UTF8
$snapF = Get-DirHashSnapshot -Dir $outsideF
$fThrew = $false
try { Restore-ASInstallBackup -InstallDir $installF -BackupDir $slotF | Out-Null } catch { $fThrew = $true }
Assert-True 'F1: Restore-ASInstallBackup throws (ancestor junction)' $fThrew
Assert-True 'F1: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideF -Expected $snapF)

# F2: Restore-ASRollback（干净数据根备份 + 恶意祖先安装目录）
$dataF2 = Join-Path $sandbox 'f2-data'
$installF2 = New-AncestorInstall -AliasPath (Join-Path $sandbox 'f2-alias') -OutsideDir (Join-Path $sandbox 'f2-out') -InstallName 'install' -ExeName $exeB
$outsideF2 = Join-Path $sandbox 'f2-out'
New-Item -ItemType Directory -Force -Path $dataF2 | Out-Null
Set-Content -LiteralPath (Join-Path $dataF2 'config.json') -Value '{"SchemaVersion":1,"TestMode":true}' -Encoding UTF8
Backup-ASDataRoot -Root $dataF2 -Tag 'upgrade' | Out-Null
$snapF2 = Get-DirHashSnapshot -Dir $outsideF2
$f2Threw = $false
try { Restore-ASRollback -InstallDir $installF2 -Root $dataF2 -Tag 'upgrade' | Out-Null } catch { $f2Threw = $true }
Assert-True 'F2: Restore-ASRollback throws (ancestor junction)' $f2Threw
Assert-True 'F2: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outsideF2 -Expected $snapF2)

# ============================================================
# G. CandidateDir 位于父 junction 下
# ============================================================
Write-Host "== G. candidate dir under ancestor junction refused =="
$outG1 = Join-Path $sandbox 'g1-out'
$aliasG1 = Join-Path $sandbox 'g1-alias'
New-Item -ItemType Directory -Force -Path $outG1 | Out-Null
Set-Content -LiteralPath (Join-Path $outG1 'sentinel.bin') -Value 'G1-OUTSIDE' -Encoding UTF8
New-Junction -LinkPath $aliasG1 -TargetPath $outG1
$candG1 = Join-Path $aliasG1 'cand'
New-Item -ItemType Directory -Force -Path $candG1 | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $candG1 'AutoShutdown-v2.0.0-S23.exe'), [byte[]](1..96))
$installG1 = Join-Path $sandbox 'g1-install'
$dataG1 = Join-Path $sandbox 'g1-data'
New-Item -ItemType Directory -Force -Path $installG1 | Out-Null
$g1Old = 'AutoShutdown-v1.0.0-win-x64.exe'
[System.IO.File]::WriteAllBytes((Join-Path $installG1 $g1Old), [byte[]](200..255))
Write-ASOwnerMarker -InstallDir $installG1 -AppFiles $g1Old -CandidateName $g1Old
$snapG1 = Get-DirHashSnapshot -Dir $outG1
$g1Threw = $false
try { Replace-ASBinary -CandidateDir $candG1 -InstallDir $installG1 -Root $dataG1 -Tag 'd3g1' | Out-Null } catch { $g1Threw = $true }
Assert-True 'G1: replace refuses when candidate under ancestor junction' $g1Threw
Assert-True 'G1: candidate junction target (outside) unchanged' (Test-DirSnapshotEqual -Dir $outG1 -Expected $snapG1)
Assert-True 'G1: install dir untouched' (Test-Path -LiteralPath (Join-Path $installG1 $g1Old))
$g1riThrew = $false
try { Invoke-ASReinstall -CandidateDir $candG1 -InstallDir $installG1 -Root $dataG1 | Out-Null } catch { $g1riThrew = $true }
Assert-True 'G1: reinstall refuses when candidate under ancestor junction' $g1riThrew
Assert-True 'G1: outside still unchanged after reinstall attempt' (Test-DirSnapshotEqual -Dir $outG1 -Expected $snapG1)

# ============================================================
# H. DataRoot 位于父 junction 下
# ============================================================
Write-Host "== H. data root under ancestor junction refused =="
$outH = Join-Path $sandbox 'h-out'
$aliasH = Join-Path $sandbox 'h-alias'
New-Item -ItemType Directory -Force -Path $outH | Out-Null
Set-Content -LiteralPath (Join-Path $outH 'sentinel.bin') -Value 'H-OUTSIDE' -Encoding UTF8
New-Junction -LinkPath $aliasH -TargetPath $outH
$dataH = Join-Path $aliasH 'dat'
New-Item -ItemType Directory -Force -Path $dataH | Out-Null
Set-Content -LiteralPath (Join-Path $dataH 'config.json') -Value '{"SchemaVersion":1,"TestMode":true}' -Encoding UTF8
$snapH = Get-DirHashSnapshot -Dir $outH
$hThrew = $false
try { Backup-ASDataRoot -Root $dataH -Tag 'd3h' | Out-Null } catch { $hThrew = $true }
Assert-True 'H: backup refuses when data root under ancestor junction' $hThrew
Assert-True 'H: data root junction target unchanged' (Test-DirSnapshotEqual -Dir $outH -Expected $snapH)
Assert-True 'H: no backups created under junctioned data root' (-not (Test-Path -LiteralPath (Join-Path $outH 'dat\backups')))
$installH = Join-Path $sandbox 'h-install'
New-Item -ItemType Directory -Force -Path $installH | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $installH $exeB), [byte[]](11..33))
Write-ASOwnerMarker -InstallDir $installH -AppFiles $exeB -CandidateName $exeB
$h2Threw = $false
try { Invoke-ASUninstall -InstallDir $installH -Root $dataH -UserData 'Remove' | Out-Null } catch { $h2Threw = $true }
Assert-True 'H: uninstall Remove refuses when data root under ancestor junction' $h2Threw
Assert-True 'H: data root junction target unchanged after uninstall Remove attempt' (Test-DirSnapshotEqual -Dir $outH -Expected $snapH)

# ============================================================
# I. 不存在的目标位于祖先 junction 下
# ============================================================
Write-Host "== I. non-existent target under ancestor junction refused (no create outside) =="
$outI = Join-Path $sandbox 'i-out'
$aliasI = Join-Path $sandbox 'i-alias'
New-Item -ItemType Directory -Force -Path $outI | Out-Null
New-Junction -LinkPath $aliasI -TargetPath $outI
$freshI = Join-Path $aliasI 'fresh\install'
$oI = Test-ASInstallOwnership -InstallDir $freshI
Assert-True 'I: ownership of non-existent target under junction -> reparse-point (not new)' ($oI.Reason -eq 'reparse-point' -and -not $oI.Ok)
Assert-True 'I: nothing created in outside for non-existent target' (-not (Test-Path -LiteralPath (Join-Path $outI 'fresh')))
$candI = Join-Path $sandbox 'i-cand'
New-CleanCandidate -CandidateDir $candI -ExeName 'AutoShutdown-v2.0.0-S23.exe'
$dataI = Join-Path $sandbox 'i-data'
$snapI = Get-DirHashSnapshot -Dir $outI
$iThrew = $false
try { Replace-ASBinary -CandidateDir $candI -InstallDir $freshI -Root $dataI -Tag 'd3i' | Out-Null } catch { $iThrew = $true }
Assert-True 'I: replace into non-existent target under junction throws' $iThrew
Assert-True 'I: outside unchanged after replace attempt' (Test-DirSnapshotEqual -Dir $outI -Expected $snapI)
Assert-True 'I: no install created in outside' (-not (Test-Path -LiteralPath (Join-Path $outI 'fresh')))

# ============================================================
# J. 正常（无 junction）路径回归
# ============================================================
Write-Host "== J. normal (junction-free) path regression =="
$installJ = Join-Path $sandbox 'j-install'
$candJ = Join-Path $sandbox 'j-cand'
$dataJ = Join-Path $sandbox 'j-data'
New-Item -ItemType Directory -Force -Path $installJ, $candJ, $dataJ | Out-Null
$jOld = 'AutoShutdown-v1.0.0-win-x64.exe'
$jNew = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
[System.IO.File]::WriteAllBytes((Join-Path $installJ $jOld), [byte[]](1..64))
[System.IO.File]::WriteAllText((Join-Path $installJ 'user-notes.txt'), 'user file', [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllBytes((Join-Path $candJ $jNew), [byte[]](65..96))
Write-ASOwnerMarker -InstallDir $installJ -AppFiles $jOld -CandidateName $jOld
$repJ = Replace-ASBinary -CandidateDir $candJ -InstallDir $installJ -Root $dataJ -Tag 'd3j'
Assert-True 'J: normal replace succeeds (no false rejection)' ($null -ne $repJ)
Assert-True 'J: new exe installed' (Test-Path -LiteralPath (Join-Path $installJ $jNew))
Assert-True 'J: install dir still owned' ((Test-ASInstallOwnership $installJ).Reason -eq 'owned')
$uJ = Invoke-ASUninstall -InstallDir $installJ -Root $dataJ -UserData 'Keep'
Assert-True 'J: normal uninstall Keep succeeds' ($null -ne $uJ)
Assert-True 'J: user file preserved after uninstall' (Test-Path -LiteralPath (Join-Path $installJ 'user-notes.txt'))

# ---- cleanup ----
Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("D3 ANCESTOR TESTS: pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
exit 0
