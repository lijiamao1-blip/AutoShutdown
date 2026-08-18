#Requires -Version 5.1
# tools/test/Invoke-SPD4CandidateTreeTests.ps1
# S-PKG D4 最小返修装置：候选树子 junction 越界（候选整树预检前置，防半安装/备份残留）。
# 纯文件操作，临时沙箱（$env:TEMP\as-spkg-d4-*），不触碰真实系统。
# 用 cmd /c mklink /J 建「候选根正常、候选子目录 junction -> outside」的隔离路径，
# 复现 D3 遗漏：Invoke-ASReinstall 先删除旧 EXE/owner marker，随后 Copy-ASDirContents
# 才拒绝 junction，造成半安装；replace 在复制期拒绝前已写入并交换备份槽。
# 覆盖（D4 检查点）：
#   A. Test-ASCandidateTreeSafe 预检函数（候选整树 / 候选根 junction / 缺失候选）
#   B. reinstall：候选子目录 junction -> 预检即拒，旧 EXE/owner/用户文件/安装目录字节级不变（无半安装）
#   C. replace（既有 rollback-install 槽）：预检即拒，槽不变、无 rollback-install.new、安装不变
#   D. replace（全新数据根）：预检即拒，backups\spkg 未被创建（无任何新备份写入）
#   E. reinstall（安装目录不存在）：预检即拒，安装目录未被创建
#   F. 候选顶层文件 symlink（环境支持时实际运行；不支持明确标「环境跳过」，不伪称已执行）
#   G. 正常（无 junction）候选树回归：replace/reinstall 正常成功，无假阳性
# Exit: 0 = all pass; 1 = failures.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'SPkg-Lifecycle.ps1')

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("as-spkg-d4-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$pass = 0; $fail = 0; $skip = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}
function Note-Skip([string]$Name, [string]$Detail) {
    $script:skip++; Write-Host ("  SKIP  {0}  {1}" -f $Name, $Detail)
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

function Get-FileSha256 {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# 建立攻击布局：候选根正常、候选子目录 junction -> outside。
function New-CandidateWithSubJunction {
    param([string]$CandidateDir, [string]$SubName, [string]$OutsideDir, [string]$ExeName)
    New-Item -ItemType Directory -Force -Path $CandidateDir | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $CandidateDir $ExeName), [byte[]](1..96))
    Set-Content -LiteralPath (Join-Path $CandidateDir 'manifest.txt') -Value 'candidate manifest' -Encoding UTF8
    New-Junction -LinkPath (Join-Path $CandidateDir $SubName) -TargetPath $OutsideDir
    return $CandidateDir
}

function New-OwnedInstall {
    param([string]$InstallDir, [string]$OldExeName)
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $InstallDir $OldExeName), [byte[]](200..255))
    Set-Content -LiteralPath (Join-Path $InstallDir 'user-notes.txt') -Value 'user file' -Encoding UTF8
    @{ schema = 1; app = 'AutoShutdown V2'; installDir = (Resolve-ASPath $InstallDir); appFiles = @($OldExeName, 'user-notes.txt'); candidate = $OldExeName; created = (Get-Date -Format o) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $InstallDir 'AutoShutdown.owner.json') -Encoding UTF8
    return $InstallDir
}

$exeOld = 'AutoShutdown-v1.0.0-win-x64.exe'
$exeCand = 'AutoShutdown-v2.0.0-S23.exe'

# ============================================================
# A. Test-ASCandidateTreeSafe 预检函数
# ============================================================
Write-Host "== A. candidate tree pre-check (Test-ASCandidateTreeSafe) =="
$candA = Join-Path $sandbox 'a-cand'
$outA = Join-Path $sandbox 'a-out'
New-Item -ItemType Directory -Force -Path $candA, $outA | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $candA $exeCand), [byte[]](1..96))
New-Item -ItemType Directory -Force -Path (Join-Path $candA 'plugins') | Out-Null
$okA = Test-ASCandidateTreeSafe -CandidateDir $candA
Assert-True 'A: clean candidate tree -> ok' ($okA.Ok)
$candAj = Join-Path $sandbox 'a-candj'
New-Item -ItemType Directory -Force -Path $candAj | Out-Null
New-Junction -LinkPath (Join-Path $candAj 'sub') -TargetPath $outA
$rejA = Test-ASCandidateTreeSafe -CandidateDir $candAj
Assert-True 'A: candidate subdir junction -> rejected' (-not $rejA.Ok)
Assert-True 'A: reports the subdir junction path' ($rejA.ReparsePath -eq (Join-Path $candAj 'sub'))
$candRootJ = Join-Path $sandbox 'a-candrootj'
New-Junction -LinkPath $candRootJ -TargetPath $outA
$rejA2 = Test-ASCandidateTreeSafe -CandidateDir $candRootJ
Assert-True 'A: candidate root junction -> rejected' (-not $rejA2.Ok)
$missA = Test-ASCandidateTreeSafe -CandidateDir (Join-Path $sandbox 'a-missing')
Assert-True 'A: missing candidate -> rejected (not found)' (-not $missA.Ok)

# ============================================================
# B. reinstall：候选子目录 junction -> 预检即拒，无半安装
# ============================================================
Write-Host "== B. reinstall refuses before deleting (candidate subdir junction) =="
$outB = Join-Path $sandbox 'b-out'
New-Item -ItemType Directory -Force -Path $outB | Out-Null
Set-Content -LiteralPath (Join-Path $outB 'sentinel.bin') -Value 'B-OUTSIDE-SENTINEL' -Encoding UTF8
$candB = New-CandidateWithSubJunction -CandidateDir (Join-Path $sandbox 'b-cand') -SubName 'plugins' -OutsideDir $outB -ExeName $exeCand
$installB = New-OwnedInstall -InstallDir (Join-Path $sandbox 'b-install') -OldExeName $exeOld
$dataB = Join-Path $sandbox 'b-data'
New-Item -ItemType Directory -Force -Path $dataB | Out-Null
$snapOutB = Get-DirHashSnapshot -Dir $outB
$oldHashB = Get-FileSha256 -Path (Join-Path $installB $exeOld)
$ownerB = Get-Content -LiteralPath (Join-Path $installB 'AutoShutdown.owner.json') -Raw -Encoding UTF8
$snapInstallB = Get-DirHashSnapshot -Dir $installB
$bThrew = $false
try { Invoke-ASReinstall -CandidateDir $candB -InstallDir $installB -Root $dataB | Out-Null } catch { $bThrew = $true }
Assert-True 'B: reinstall throws (candidate subdir junction)' $bThrew
Assert-True 'B: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outB -Expected $snapOutB)
Assert-True 'B: old EXE SHA-256 unchanged' ((Get-FileSha256 -Path (Join-Path $installB $exeOld)) -eq $oldHashB)
Assert-True 'B: owner marker content unchanged' ((Get-Content -LiteralPath (Join-Path $installB 'AutoShutdown.owner.json') -Raw -Encoding UTF8) -eq $ownerB)
Assert-True 'B: user file unchanged' (((Get-Content -LiteralPath (Join-Path $installB 'user-notes.txt') -Raw -Encoding UTF8).Trim()) -eq 'user file')
Assert-True 'B: install dir complete (no half-install)' (Test-DirSnapshotEqual -Dir $installB -Expected $snapInstallB)

# ============================================================
# C. replace（既有 rollback-install 槽）：预检即拒，槽不变、无 rollback-install.new
# ============================================================
Write-Host "== C. replace refuses before backup slot (candidate subdir junction, existing slot) =="
$outC = Join-Path $sandbox 'c-out'
New-Item -ItemType Directory -Force -Path $outC | Out-Null
Set-Content -LiteralPath (Join-Path $outC 'sentinel.bin') -Value 'C-OUTSIDE-SENTINEL' -Encoding UTF8
$candC = New-CandidateWithSubJunction -CandidateDir (Join-Path $sandbox 'c-cand') -SubName 'plugins' -OutsideDir $outC -ExeName $exeCand
$installC = New-OwnedInstall -InstallDir (Join-Path $sandbox 'c-install') -OldExeName $exeOld
$dataC = Join-Path $sandbox 'c-data'
$slotC = Join-Path $dataC 'backups\spkg\rollback-install'
New-Item -ItemType Directory -Force -Path $slotC | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $slotC $exeOld), [byte[]](240..255))
Set-Content -LiteralPath (Join-Path $slotC 'backup.json') -Value '{"schema":1,"app":"AutoShutdown V2"}' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $slotC '_complete.marker') -Value (Get-Date -Format o) -Encoding UTF8
$snapOutC = Get-DirHashSnapshot -Dir $outC
$snapSlotC = Get-DirHashSnapshot -Dir $slotC
$snapInstallC = Get-DirHashSnapshot -Dir $installC
$oldHashC = Get-FileSha256 -Path (Join-Path $installC $exeOld)
$ownerC = Get-Content -LiteralPath (Join-Path $installC 'AutoShutdown.owner.json') -Raw -Encoding UTF8
$cThrew = $false
try { Replace-ASBinary -CandidateDir $candC -InstallDir $installC -Root $dataC -Tag 'd4c' | Out-Null } catch { $cThrew = $true }
Assert-True 'C: replace throws (candidate subdir junction)' $cThrew
Assert-True 'C: existing rollback-install slot unchanged' (Test-DirSnapshotEqual -Dir $slotC -Expected $snapSlotC)
Assert-True 'C: no rollback-install.new written' (-not (Test-Path -LiteralPath (Join-Path $dataC 'backups\spkg\rollback-install.new')))
Assert-True 'C: install dir old EXE SHA-256 unchanged' ((Get-FileSha256 -Path (Join-Path $installC $exeOld)) -eq $oldHashC)
Assert-True 'C: owner marker content unchanged' ((Get-Content -LiteralPath (Join-Path $installC 'AutoShutdown.owner.json') -Raw -Encoding UTF8) -eq $ownerC)
Assert-True 'C: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outC -Expected $snapOutC)

# ============================================================
# D. replace（全新数据根）：预检即拒，无任何新备份写入
# ============================================================
Write-Host "== D. replace refuses before ANY backup write (fresh data root) =="
$outD = Join-Path $sandbox 'd-out'
New-Item -ItemType Directory -Force -Path $outD | Out-Null
Set-Content -LiteralPath (Join-Path $outD 'sentinel.bin') -Value 'D-OUTSIDE-SENTINEL' -Encoding UTF8
$candD = New-CandidateWithSubJunction -CandidateDir (Join-Path $sandbox 'd-cand') -SubName 'plugins' -OutsideDir $outD -ExeName $exeCand
$installD = New-OwnedInstall -InstallDir (Join-Path $sandbox 'd-install') -OldExeName $exeOld
$dataD = Join-Path $sandbox 'd-data'
New-Item -ItemType Directory -Force -Path $dataD | Out-Null
$snapOutD = Get-DirHashSnapshot -Dir $outD
$snapInstallD = Get-DirHashSnapshot -Dir $installD
$dThrew = $false
try { Replace-ASBinary -CandidateDir $candD -InstallDir $installD -Root $dataD -Tag 'd4d' | Out-Null } catch { $dThrew = $true }
Assert-True 'D: replace throws (candidate subdir junction, fresh data)' $dThrew
Assert-True 'D: backups\spkg NOT created (no backup residue)' (-not (Test-Path -LiteralPath (Join-Path $dataD 'backups\spkg')))
Assert-True 'D: install dir unchanged' (Test-DirSnapshotEqual -Dir $installD -Expected $snapInstallD)
Assert-True 'D: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outD -Expected $snapOutD)

# ============================================================
# E. reinstall（安装目录不存在）：预检即拒，安装目录未被创建
# ============================================================
Write-Host "== E. reinstall refuses before install dir creation (fresh install) =="
$outE = Join-Path $sandbox 'e-out'
New-Item -ItemType Directory -Force -Path $outE | Out-Null
Set-Content -LiteralPath (Join-Path $outE 'sentinel.bin') -Value 'E-OUTSIDE-SENTINEL' -Encoding UTF8
$candE = New-CandidateWithSubJunction -CandidateDir (Join-Path $sandbox 'e-cand') -SubName 'plugins' -OutsideDir $outE -ExeName $exeCand
$installE = Join-Path $sandbox 'e-install'
$dataE = Join-Path $sandbox 'e-data'
New-Item -ItemType Directory -Force -Path $dataE | Out-Null
$snapOutE = Get-DirHashSnapshot -Dir $outE
$eThrew = $false
try { Invoke-ASReinstall -CandidateDir $candE -InstallDir $installE -Root $dataE | Out-Null } catch { $eThrew = $true }
Assert-True 'E: reinstall throws (candidate subdir junction, fresh install)' $eThrew
Assert-True 'E: install dir NOT created' (-not (Test-Path -LiteralPath $installE))
Assert-True 'E: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outE -Expected $snapOutE)

# ============================================================
# F. 候选顶层文件 symlink（环境支持时实际运行；不支持明确标「环境跳过」）
# ============================================================
Write-Host "== F. candidate top-level file symlink (environment-gated) =="
$outF = Join-Path $sandbox 'f-out'
New-Item -ItemType Directory -Force -Path $outF | Out-Null
Set-Content -LiteralPath (Join-Path $outF 'sentinel.bin') -Value 'F-OUTSIDE-SENTINEL' -Encoding UTF8
$candF = Join-Path $sandbox 'f-cand'
New-Item -ItemType Directory -Force -Path $candF | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $candF $exeCand), [byte[]](1..96))
$fileLink = Join-Path $candF 'evil-link.bin'
$linkCreated = $false
# 文件 symlink 需要管理员/开发者模式；不满足时 mklink 以「权限不足」失败，PS 5.1 会生成
# 终止错误（即便 2>&1）。用 try/catch 包裹：失败即明确标为「环境跳过」，不伪称已执行。
try {
    & cmd /c mklink $fileLink (Join-Path $outF 'sentinel.bin') 2>&1 | Out-Null
    $linkItem = Get-Item -LiteralPath $fileLink -Force -ErrorAction SilentlyContinue
    if ($null -ne $linkItem -and ($linkItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { $linkCreated = $true }
} catch { }
if ($linkCreated) {
    $rejF = Test-ASCandidateTreeSafe -CandidateDir $candF
    Assert-True 'F: candidate top-level file symlink -> pre-check rejects' (-not $rejF.Ok)
    $installF = New-OwnedInstall -InstallDir (Join-Path $sandbox 'f-install') -OldExeName $exeOld
    $dataF = Join-Path $sandbox 'f-data'
    New-Item -ItemType Directory -Force -Path $dataF | Out-Null
    $snapOutF = Get-DirHashSnapshot -Dir $outF
    $fThrew = $false
    try { Invoke-ASReinstall -CandidateDir $candF -InstallDir $installF -Root $dataF | Out-Null } catch { $fThrew = $true }
    Assert-True 'F: reinstall throws (file symlink in candidate)' $fThrew
    Assert-True 'F: outside sentinel + hashes unchanged' (Test-DirSnapshotEqual -Dir $outF -Expected $snapOutF)
} else {
    Note-Skip 'F: candidate top-level file symlink' 'environment does not support file symlink creation (mklink: insufficient privilege); explicitly skipped, NOT faked as executed'
}

# ============================================================
# G. 正常（无 junction）候选树回归
# ============================================================
Write-Host "== G. normal (junction-free) candidate tree regression =="
$outG = Join-Path $sandbox 'g-out'
$candG = Join-Path $sandbox 'g-cand'
$installG = Join-Path $sandbox 'g-install'
$dataG = Join-Path $sandbox 'g-data'
New-Item -ItemType Directory -Force -Path $outG, $candG, $installG, $dataG | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $candG $exeCand), [byte[]](1..96))
Set-Content -LiteralPath (Join-Path $candG 'manifest.txt') -Value 'candidate manifest' -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Join-Path $candG 'plugins') | Out-Null
Set-Content -LiteralPath (Join-Path (Join-Path $candG 'plugins') 'plugin.dll') -Value 'PLUGIN' -Encoding UTF8
New-OwnedInstall -InstallDir $installG -OldExeName $exeOld
$repG = Replace-ASBinary -CandidateDir $candG -InstallDir $installG -Root $dataG -Tag 'd4g'
Assert-True 'G: normal replace succeeds (no false rejection)' ($null -ne $repG)
Assert-True 'G: normal replace installed new exe' (Test-Path -LiteralPath (Join-Path $installG $exeCand))
Assert-True 'G: normal replace copied subdir contents' (Test-Path -LiteralPath (Join-Path $installG 'plugins\plugin.dll'))
Assert-True 'G: install still owned after replace' ((Test-ASInstallOwnership $installG).Reason -eq 'owned')
$candG2 = Join-Path $sandbox 'g-cand2'
New-Item -ItemType Directory -Force -Path $candG2 | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $candG2 $exeCand), [byte[]](2..97))
$riG = Invoke-ASReinstall -CandidateDir $candG2 -InstallDir $installG -Root $dataG
Assert-True 'G: normal reinstall succeeds (no false rejection)' ($null -ne $riG)
Assert-True 'G: install still owned after reinstall' ((Test-ASInstallOwnership $installG).Reason -eq 'owned')

# ---- cleanup ----
Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("D4 CANDIDATE TREE TESTS: pass={0} fail={1} skip={2}" -f $pass, $fail, $skip)
if ($fail -gt 0) { exit 1 }
exit 0
