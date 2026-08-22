#Requires -Version 5.1
# tools/test/ASUI3IsolatedRootCleanup.ps1
# S-UI3 UIA 冒烟隔离根删除边界（S-STARTUP-D1-D3）。
#
# 由 Invoke-ASUI3Smoke.ps1 与 Invoke-ASUI3RemoveBoundaryTests.ps1 共享。
#
# 只允许删除本轮创建并登记的精确绝对目录（as-ui3-round-N-<32hex>），删除前全部边界确认：
#   - GetFullPath 后目标必须是系统临时目录的**直接子目录**（绝不只做 StartsWith 前缀判断）；
#   - 目录名严格匹配 ^as-ui3-round-\d+-[0-9a-f]{32}$；
#   - 目标不等于临时目录、驱动器根、用户目录、正式数据根、仓库目录（受保护根）；
#   - 目标及从系统临时目录到目标的路径组件均无 FileAttributes.ReparsePoint
#     （junction / symlink / mount point 一律拒绝）；
#   - 删除仅经 PowerShell Remove-Item 在已验证精确路径上执行（无 Bash/rm）。
#
# 边界无法确认时保留目录，并由调用方判本轮 FAIL（Test-ASUI3CleanupRoundDecision）。

Set-StrictMode -Version 2.0

function Test-ASUI3NoReparsePoint {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    # 返回 $false 即该路径组件为 reparse point 或无法读取属性（fail-closed）。
    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    } catch {
        return $false
    }
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { return $false }
    return $true
}

function Test-ASUI3IsolatedRootEligible {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$TempRoot,
        [string[]]$ProtectedRoots = @()
    )
    # 返回 $true 仅当 $Target 是可按边界删除的隔离根；任何一项不满足即 $false（保留目录）。
    $targetFull = $null
    $tempFull = $null
    try { $targetFull = [IO.Path]::GetFullPath($Target) } catch { return $false }
    try { $tempFull = [IO.Path]::GetFullPath($TempRoot).TrimEnd('\') } catch { return $false }

    # 1) 必须是绝对路径。
    if (-not [IO.Path]::IsPathRooted($targetFull)) { return $false }

    # 2) 目录名严格匹配本轮登记模式。
    if ([IO.Path]::GetFileName($targetFull) -notmatch '^as-ui3-round-\d+-[0-9a-f]{32}$') { return $false }

    # 3) 目标是系统临时目录的直接子目录（精确父目录相等，绝非 StartsWith 前缀）。
    $parent = [IO.Path]::GetDirectoryName($targetFull)
    if (-not $parent) { return $false }
    if (-not $parent.TrimEnd('\').Equals($tempFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }

    # 4) 目标 != 临时目录。
    if ($targetFull.TrimEnd('\').Equals($tempFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }

    # 5) 目标与 TempRoot 均不得等于任一受保护根（正式数据根 / 仓库目录 / 用户目录等）。
    foreach ($p in $ProtectedRoots) {
        if (-not $p) { continue }
        $pf = $null
        try { $pf = [IO.Path]::GetFullPath($p).TrimEnd('\') } catch { continue }
        if ($targetFull.TrimEnd('\').Equals($pf, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
        if ($tempFull.Equals($pf, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
    }

    # 6) 目标 != 驱动器根。
    $drive = [IO.Path]::GetPathRoot($targetFull)
    if ($drive -and $targetFull.TrimEnd('\').Equals($drive.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) { return $false }

    # 7) 目标及从临时目录到目标的路径组件均无 ReparsePoint（junction/symlink/mount point 一律拒绝）。
    $current = $targetFull
    while ($current) {
        if (-not (Test-ASUI3NoReparsePoint -Path $current)) { return $false }
        $curTrim = $current.TrimEnd('\')
        if ($curTrim.Equals($tempFull, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $next = [IO.Path]::GetDirectoryName($current)
        if (-not $next -or $next.Equals($current, [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
        $current = $next
    }
    return $true
}

function Remove-ASUI3IsolatedRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$TempRoot,
        [string[]]$ProtectedRoots = @()
    )
    # 返回状态：'Deleted' | 'NotFound' | 'Refused' | 'Error'。
    #   - 'Deleted'  ：边界确认且删除完成；
    #   - 'NotFound' ：目标不存在，无需删除；
    #   - 'Refused'  ：边界任一环节无法确认（名称/位置/受保护根/reparse point），保留目录；
    #   - 'Error'    ：删除尝试失败，目录仍保留。
    if (-not (Test-ASUI3IsolatedRootEligible -Target $Target -TempRoot $TempRoot -ProtectedRoots $ProtectedRoots)) {
        return 'Refused'
    }
    if (Test-Path -LiteralPath $Target -PathType Container) {
        # 删除前最后再校验一次目标本身无 reparse point（闭合检查与删除之间的替换窗口）。
        if (-not (Test-ASUI3NoReparsePoint -Path $Target)) { return 'Refused' }
        try {
            Remove-Item -LiteralPath $Target -Recurse -Force -ErrorAction Stop
            return 'Deleted'
        } catch {
            return 'Error'
        }
    } elseif (Test-Path -LiteralPath $Target) {
        # 名称匹配但存在的是非目录项：拒绝，绝不删除文件。
        return 'Refused'
    }
    return 'NotFound'
}

function Test-ASUI3CleanupRoundDecision {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Status)
    # 返回 $true 表示该清理状态必须使本轮 FAIL（边界未确认，目录保留）；
    # 返回 $false 表示清理已确认（Deleted/NotFound），本轮可按其余断言正常判定。
    return ($Status -ne 'Deleted' -and $Status -ne 'NotFound')
}
