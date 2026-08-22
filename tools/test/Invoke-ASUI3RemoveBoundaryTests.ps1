#Requires -Version 5.1
# tools/test/Invoke-ASUI3RemoveBoundaryTests.ps1
# S-STARTUP-D1-D3/D4：ASUI3 隔离根删除边界聚焦测试（真实文件系统 + 真实 junction）。
#
# 覆盖（对照总顾问 D3/D4 清单）：
#   1. 合法本轮目录允许删除（Deleted + 目录确实消失）
#   2. 名称近似但不匹配拒绝（大小写/长度/尾缀），目录保留
#   3. TEMP 前缀碰撞路径拒绝（仅前缀相同但非直接子目录）
#   4. 正式数据根拒绝
#   5. 仓库目录拒绝
#   6. 根目录 / 用户目录拒绝
#   7. 目标等于受保护根（名称看似合法）拒绝
#   8. reparse point / junction 拒绝（junction 与目标哨兵均保留）；junction 链接删除仅经
#      PowerShell（DirectoryInfo.Delete() 非递归只删链接不删目标，D4），无 cmd rmdir
#   9. 目标等于临时目录拒绝
#   10. 删除拒绝必须使冒烟结果失败（Test-ASUI3CleanupRoundDecision 映射）
#   11. 测试自身清理仅删除本轮精确 sandbox 内或已登记的临时测试路径（D4）
#
# 全部删除仅经 PowerShell 在边界确认后执行（无 Bash/rm）：junction 链接用
# DirectoryInfo.Delete() 非递归只删链接不删目标；其余测试产物经删除前核对（本轮精确
# sandbox 内或已登记临时路径）后递归删除。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/Invoke-ASUI3RemoveBoundaryTests.ps1
# 退出：0 = 全部通过；1 = 有失败。

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ASUI3IsolatedRootCleanup.ps1')

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$sandbox = Join-Path $tempBase ("as-ui3-boundary-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$formalRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AutoShutdown'))
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$userDir = [IO.Path]::GetFullPath([Environment]::GetFolderPath('UserProfile'))
$protected = @($formalRoot, $repoRoot, $userDir)

$script:pass = 0; $script:fail = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}
function New-Hex([int]$Seed) {
    # 确定性 32 位小写十六进制：0e5d576da0d14ebf9f3788bf4a216cd2 基准按 Seed 偏移一个字符。
    $base = '0e5d576da0d14ebf9f3788bf4a216cd2'
    $i = $Seed % 32
    $c = $base[$i]
    $r = [char]([int][char]$c + 1)
    return $base.Substring(0, $i) + $r + $base.Substring($i + 1)
}

# ---- D4：PowerShell 只删 junction 链接本身（DirectoryInfo.Delete() 非递归），无 cmd rmdir ----
function Remove-ASUI3JunctionLink {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Link,
        [Parameter(Mandatory = $true)][string]$RegisteredLink,
        [Parameter(Mandatory = $true)][string]$TempRoot,
        [string]$SentinelName = 'sentinel.txt'
    )
    # 返回状态：'Deleted' | 'NotFound' | 'Refused' | 'Error'。
    # 删除前逐项核对（任一不满足即 'Refused'，链接保留）：
    #   1) 绝对路径；2) 是本测试创建并登记的精确链接路径；3) 目录名严格匹配
    #      ^as-ui3-round-\d+-[0-9a-f]{32}$；4) Get-Item 显示 ReparsePoint；
    #   5) 链接实际目标解析后，目标目录内哨兵文件存在。
    # 删除仅经 DirectoryInfo.Delete()（对 junction 只删链接本身，绝不递归进入目标）。
    # 删除后核对：链接不存在、目标目录仍存在、哨兵仍存在。
    $linkFull = $null
    $tempFull = $null
    $regFull = $null
    try { $linkFull = [IO.Path]::GetFullPath($Link) } catch { return 'Refused' }
    try { $tempFull = [IO.Path]::GetFullPath($TempRoot).TrimEnd('\') } catch { return 'Refused' }
    try { $regFull = [IO.Path]::GetFullPath($RegisteredLink).TrimEnd('\') } catch { return 'Refused' }

    # 1) 绝对路径
    if (-not [IO.Path]::IsPathRooted($linkFull)) { return 'Refused' }
    # 2) 与登记的精确链接路径一致（直接子目录 + 路径相等）
    if (-not $linkFull.TrimEnd('\').Equals($regFull, [System.StringComparison]::OrdinalIgnoreCase)) { return 'Refused' }
    $parent = [IO.Path]::GetDirectoryName($regFull)
    if (-not $parent -or -not $parent.TrimEnd('\').Equals($tempFull, [System.StringComparison]::OrdinalIgnoreCase)) { return 'Refused' }
    # 3) 目录名严格匹配
    if ([IO.Path]::GetFileName($regFull) -notmatch '^as-ui3-round-\d+-[0-9a-f]{32}$') { return 'Refused' }
    if (-not (Test-Path -LiteralPath $linkFull)) { return 'NotFound' }
    # 4) Get-Item 显示 ReparsePoint
    $item = $null
    try { $item = Get-Item -LiteralPath $linkFull -Force -ErrorAction Stop } catch { return 'Refused' }
    if (-not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { return 'Refused' }
    # 5) 目标解析 + 哨兵存在
    $target = $null
    try { $target = $item.Target } catch { }
    if (-not $target) { return 'Refused' }
    $targetFull = $null
    try { $targetFull = [IO.Path]::GetFullPath([string]$target).TrimEnd('\') } catch { return 'Refused' }
    if (-not (Test-Path -LiteralPath $targetFull -PathType Container)) { return 'Refused' }
    if (-not (Test-Path -LiteralPath (Join-Path $targetFull $SentinelName))) { return 'Refused' }

    # 删除：只删链接本身（DirectoryInfo.Delete() 对 junction 非递归）
    try { $item.Delete() } catch { return 'Error' }

    # 删除后核对
    if (Test-Path -LiteralPath $linkFull) { return 'Error' }
    if (-not (Test-Path -LiteralPath $targetFull -PathType Container)) { return 'Error' }
    if (-not (Test-Path -LiteralPath (Join-Path $targetFull $SentinelName))) { return 'Error' }
    return 'Deleted'
}

# ---- D4：递归清理前的目标核对（本轮精确测试 sandbox 或已登记的临时测试路径） ----
function Remove-ASUI3BoundaryCleanupTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$Sandbox,
        [Parameter(Mandatory = $true)][string]$TempRoot,
        [string[]]$RegisteredLeaves = @()
    )
    # 返回状态：'Deleted' | 'NotFound' | 'Refused' | 'Error'。
    # 删除前核对绝对目标必须处于：本轮精确测试 sandbox 内（直接或后代），
    # 或已登记的系统临时目录直接子目录（$RegisteredLeaves 中的叶子名）；
    # 否则 'Refused'（不删除）。仅确认后才递归删除。
    $targetFull = $null
    $tempFull = $null
    $sandboxFull = $null
    try { $targetFull = [IO.Path]::GetFullPath($Target) } catch { return 'Refused' }
    try { $tempFull = [IO.Path]::GetFullPath($TempRoot).TrimEnd('\') } catch { return 'Refused' }
    try { $sandboxFull = [IO.Path]::GetFullPath($Sandbox).TrimEnd('\') } catch { return 'Refused' }
    if (-not [IO.Path]::IsPathRooted($targetFull)) { return 'Refused' }
    $targetTrim = $targetFull.TrimEnd('\')
    if ($targetTrim.Equals($tempFull, [System.StringComparison]::OrdinalIgnoreCase)) { return 'Refused' }
    # 在 sandbox 内（含精确 sandbox 根本身，即本轮创建的 as-ui3-boundary-<guid>）？
    $inSandbox = $targetTrim.Equals($sandboxFull, [System.StringComparison]::OrdinalIgnoreCase) -or
        $targetTrim.StartsWith($sandboxFull + '\', [System.StringComparison]::OrdinalIgnoreCase)
    # 或已登记临时直接子目录？
    $isRegistered = $false
    if (-not $inSandbox) {
        $parent = [IO.Path]::GetDirectoryName($targetTrim)
        if ($parent -and $parent.TrimEnd('\').Equals($tempFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            $leaf = [IO.Path]::GetFileName($targetTrim)
            foreach ($r in $RegisteredLeaves) {
                if ([string]::Equals($leaf, $r, [System.StringComparison]::OrdinalIgnoreCase)) { $isRegistered = $true; break }
            }
        }
    }
    if (-not $inSandbox -and -not $isRegistered) { return 'Refused' }
    if (-not (Test-Path -LiteralPath $targetFull)) { return 'NotFound' }
    try {
        Remove-Item -LiteralPath $targetFull -Recurse -Force -ErrorAction Stop
        return 'Deleted'
    } catch {
        return 'Error'
    }
}

Write-Host "== S-STARTUP-D1-D3/D4 ASUI3 remove-boundary focused tests =="

# ---- 1. 合法本轮目录允许删除 ----
$legal = Join-Path $tempBase ("as-ui3-round-1-" + (New-Hex 1))
New-Item -ItemType Directory -Force -Path $legal | Out-Null
Set-Content -LiteralPath (Join-Path $legal 'config.json') -Value '{}' -Encoding UTF8
$st = Remove-ASUI3IsolatedRoot -Target $legal -TempRoot $tempBase -ProtectedRoots $protected
Assert-True '1: 合法本轮目录允许删除(Deleted)' ($st -eq 'Deleted') $st
Assert-True '1: 合法本轮目录确实被删除' (-not (Test-Path -LiteralPath $legal))

# ---- 2. 名称近似但不匹配拒绝（大小写 / 长度 / 尾缀 / 非数字轮次） ----
# 逐条构造后组合（PowerShell @() 内嵌方法调用表达式会被解析为单个字符串，故分开赋值）。
$n1 = ("as-ui3-round-1-" + (New-Hex 2)).Substring(0, 42) + 'F'   # 末位大写 F（非 [0-9a-f]）
$n2 = ("as-ui3-round-1-" + (New-Hex 3)).Substring(0, 41)         # 十六进制长度不足 32
$n3 = "as-ui3-round-1-" + (New-Hex 4) + '-extra'                 # 尾缀
$n4 = "as-ui3-round-X-" + (New-Hex 5)                            # 非数字轮次
$nearCases = @($n1, $n2, $n3, $n4)
$caseNo = 0
foreach ($leaf in $nearCases) {
    $caseNo++
    $near = Join-Path $tempBase $leaf
    New-Item -ItemType Directory -Force -Path $near | Out-Null
    $st = Remove-ASUI3IsolatedRoot -Target $near -TempRoot $tempBase -ProtectedRoots $protected
    Assert-True ("2.{0}: 名称近似但不匹配拒绝(Refused) [{1}]" -f $caseNo, $leaf) ($st -eq 'Refused') $st
    Assert-True ("2.{0}: 拒绝后目录仍保留 [{1}]" -f $caseNo, $leaf) (Test-Path -LiteralPath $near)
}

# ---- 3. TEMP 前缀碰撞路径拒绝（前缀相同但非直接子目录） ----
$fakeTemp = Join-Path $sandbox 'tmp'
$evilTemp = Join-Path $sandbox 'tmp-evil'
New-Item -ItemType Directory -Force -Path $fakeTemp, $evilTemp | Out-Null
$collision = Join-Path $evilTemp ("as-ui3-round-1-" + (New-Hex 6))
New-Item -ItemType Directory -Force -Path $collision | Out-Null
$st = Remove-ASUI3IsolatedRoot -Target $collision -TempRoot $fakeTemp -ProtectedRoots @()
Assert-True '3: TEMP 前缀碰撞路径拒绝(Refused)' ($st -eq 'Refused') $st
Assert-True '3: 前缀碰撞路径未被删除' (Test-Path -LiteralPath $collision)

# ---- 4. 正式数据根拒绝 ----
$st = Remove-ASUI3IsolatedRoot -Target $formalRoot -TempRoot ([IO.Path]::GetDirectoryName($formalRoot)) -ProtectedRoots $protected
Assert-True '4: 正式数据根拒绝(Refused)' ($st -eq 'Refused') $st
Assert-True '4: 正式数据根未被删除' (Test-Path -LiteralPath $formalRoot)

# ---- 5. 仓库目录拒绝 ----
$st = Remove-ASUI3IsolatedRoot -Target $repoRoot -TempRoot ([IO.Path]::GetDirectoryName($repoRoot)) -ProtectedRoots $protected
Assert-True '5: 仓库目录拒绝(Refused)' ($st -eq 'Refused') $st
Assert-True '5: 仓库目录未被删除' (Test-Path -LiteralPath $repoRoot)

# ---- 6. 根目录 / 用户目录拒绝 ----
$drive = [IO.Path]::GetPathRoot($tempBase)
$st = Remove-ASUI3IsolatedRoot -Target $drive -TempRoot $tempBase -ProtectedRoots $protected
Assert-True '6: 驱动器根拒绝(Refused)' ($st -eq 'Refused') $st
$st = Remove-ASUI3IsolatedRoot -Target $userDir -TempRoot ([IO.Path]::GetDirectoryName($userDir)) -ProtectedRoots $protected
Assert-True '6: 用户目录拒绝(Refused)' ($st -eq 'Refused') $st
Assert-True '6: 用户目录未被删除' (Test-Path -LiteralPath $userDir)

# ---- 7. 目标等于受保护根（名称看似合法）拒绝 ----
$guard = Join-Path $sandbox 'guard'
New-Item -ItemType Directory -Force -Path $guard | Out-Null
$legalName = Join-Path $guard ("as-ui3-round-2-" + (New-Hex 7))
New-Item -ItemType Directory -Force -Path $legalName | Out-Null
$st = Remove-ASUI3IsolatedRoot -Target $legalName -TempRoot $guard -ProtectedRoots @($legalName)
Assert-True '7: 目标等于受保护根(ProtectedRoot)拒绝' ($st -eq 'Refused') $st
Assert-True '7: 受保护根目标未被删除' (Test-Path -LiteralPath $legalName)

# ---- 8. reparse point / junction 拒绝；junction 链接仅经 PowerShell 删除（D4） ----
$juncTarget = Join-Path $sandbox ('junc-target-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $juncTarget | Out-Null
Set-Content -LiteralPath (Join-Path $juncTarget 'sentinel.txt') -Value 'OUTSIDE-SENTINEL' -Encoding UTF8
$junc = Join-Path $tempBase ("as-ui3-round-9-" + (New-Hex 8))
& cmd /c mklink /J $junc $juncTarget | Out-Null   # D4：junction 创建允许（mklink），删除必须 PowerShell
$jItem = Get-Item -LiteralPath $junc -Force
Assert-True '8: 前置 junction 已创建且为 ReparsePoint' (($jItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
$st = Remove-ASUI3IsolatedRoot -Target $junc -TempRoot $tempBase -ProtectedRoots $protected
Assert-True '8: junction/reparse point 拒绝删除(Refused)' ($st -eq 'Refused') $st
Assert-True '8: junction 链接仍保留' (Test-Path -LiteralPath $junc)
Assert-True '8: junction 目标哨兵未被删除' (Test-Path -LiteralPath (Join-Path $juncTarget 'sentinel.txt'))
# D4：清理 junction 链接——仅 PowerShell（DirectoryInfo.Delete() 只删链接，无 cmd rmdir），
# 删除前核对绝对/登记/名称/ReparsePoint/目标哨兵，删除后核对链接消失、目标与哨兵仍在。
$stJunc = Remove-ASUI3JunctionLink -Link $junc -RegisteredLink $junc -TempRoot $tempBase
Assert-True '8: junction 链接经 PowerShell 只删链接(Deleted)' ($stJunc -eq 'Deleted') $stJunc
Assert-True '8: 删除后 junction 链接不存在' (-not (Test-Path -LiteralPath $junc))
Assert-True '8: 删除后 junction 目标目录仍存在' (Test-Path -LiteralPath $juncTarget)
Assert-True '8: 删除后 junction 目标哨兵仍存在' (Test-Path -LiteralPath (Join-Path $juncTarget 'sentinel.txt'))

# ---- 9. 目标等于临时目录拒绝 ----
$st = Remove-ASUI3IsolatedRoot -Target $tempBase -TempRoot $tempBase -ProtectedRoots $protected
Assert-True '9: 目标等于临时目录拒绝(Refused)' ($st -eq 'Refused') $st

# ---- 10. 删除拒绝必须使冒烟结果失败（轮次判定映射） ----
Assert-True '10: 清理状态 Refused ⇒ 本轮判 FAIL' (Test-ASUI3CleanupRoundDecision -Status 'Refused')
Assert-True '10: 清理状态 Error ⇒ 本轮判 FAIL' (Test-ASUI3CleanupRoundDecision -Status 'Error')
Assert-True '10: 清理状态 Deleted ⇒ 不判 FAIL' (-not (Test-ASUI3CleanupRoundDecision -Status 'Deleted'))
Assert-True '10: 清理状态 NotFound ⇒ 不判 FAIL' (-not (Test-ASUI3CleanupRoundDecision -Status 'NotFound'))

# ---- 清理测试自身产物（D4：递归删除前核对绝对目标处于本轮精确 sandbox 内或已登记临时路径） ----
# 被拒目录在测试中被正确保留以证明“拒绝”，断言完成后在此显式清理，避免污染系统临时目录。
# 全部清理仅经 PowerShell；junction 链接已按 8 单独以 PowerShell 只删链接删除。
foreach ($leaf in $nearCases) {
    $p = Join-Path $tempBase $leaf
    if (Test-Path -LiteralPath $p) {
        $stCln = Remove-ASUI3BoundaryCleanupTarget -Target $p -Sandbox $sandbox -TempRoot $tempBase -RegisteredLeaves $nearCases
        if ($stCln -ne 'Deleted' -and $stCln -ne 'NotFound') { Write-Host ('  CLEAN-WARN nearCase status=' + $stCln) }
    }
}
if (Test-Path -LiteralPath $collision) {
    $stCln = Remove-ASUI3BoundaryCleanupTarget -Target $collision -Sandbox $sandbox -TempRoot $tempBase
    if ($stCln -ne 'Deleted' -and $stCln -ne 'NotFound') { Write-Host ('  CLEAN-WARN collision status=' + $stCln) }
}
if (Test-Path -LiteralPath $legalName) {
    $stCln = Remove-ASUI3BoundaryCleanupTarget -Target $legalName -Sandbox $sandbox -TempRoot $tempBase
    if ($stCln -ne 'Deleted' -and $stCln -ne 'NotFound') { Write-Host ('  CLEAN-WARN legalName status=' + $stCln) }
}
$stCln = Remove-ASUI3BoundaryCleanupTarget -Target $sandbox -Sandbox $sandbox -TempRoot $tempBase
if ($stCln -ne 'Deleted' -and $stCln -ne 'NotFound') { Write-Host ('  CLEAN-WARN sandbox status=' + $stCln) }

Write-Host ""
Write-Host ("AS-UI3 REMOVE BOUNDARY TESTS: pass={0} fail={1}" -f $script:pass, $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
