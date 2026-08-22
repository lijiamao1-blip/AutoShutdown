#Requires -Version 5.1
# tools/test/Invoke-ASUI3RemoveBoundaryTests.ps1
# S-STARTUP-D1-D3：ASUI3 隔离根删除边界聚焦测试（真实文件系统 + 真实 junction）。
#
# 覆盖（对照总顾问 D3 清单）：
#   1. 合法本轮目录允许删除（Deleted + 目录确实消失）
#   2. 名称近似但不匹配拒绝（大小写/长度/尾缀），目录保留
#   3. TEMP 前缀碰撞路径拒绝（仅前缀相同但非直接子目录）
#   4. 正式数据根拒绝
#   5. 仓库目录拒绝
#   6. 根目录 / 用户目录拒绝
#   7. 目标等于受保护根（名称看似合法）拒绝
#   8. reparse point / junction 拒绝（junction 与目标哨兵均保留）
#   9. 目标等于临时目录拒绝
#   10. 删除拒绝必须使冒烟结果失败（Test-ASUI3CleanupRoundDecision 映射）
#
# 全部删除仅经 PowerShell Remove-Item 在边界确认后执行；本脚本自身的测试沙箱由
# PowerShell 清理（junction 链接用非递归 rmdir 只删链接不删目标）。
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

Write-Host "== S-STARTUP-D1-D3 ASUI3 remove-boundary focused tests =="

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

# ---- 8. reparse point / junction 拒绝 ----
$juncTarget = Join-Path $sandbox ('junc-target-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $juncTarget | Out-Null
Set-Content -LiteralPath (Join-Path $juncTarget 'sentinel.txt') -Value 'OUTSIDE-SENTINEL' -Encoding UTF8
$junc = Join-Path $tempBase ("as-ui3-round-9-" + (New-Hex 8))
& cmd /c mklink /J $junc $juncTarget | Out-Null
$jItem = Get-Item -LiteralPath $junc -Force
Assert-True '8: 前置 junction 已创建且为 ReparsePoint' (($jItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
$st = Remove-ASUI3IsolatedRoot -Target $junc -TempRoot $tempBase -ProtectedRoots $protected
Assert-True '8: junction/reparse point 拒绝删除(Refused)' ($st -eq 'Refused') $st
Assert-True '8: junction 链接仍保留' (Test-Path -LiteralPath $junc)
Assert-True '8: junction 目标哨兵未被删除' (Test-Path -LiteralPath (Join-Path $juncTarget 'sentinel.txt'))
# 清理 junction 链接（非递归 rmdir 只删链接不删目标）
& cmd /c rmdir $junc | Out-Null

# ---- 9. 目标等于临时目录拒绝 ----
$st = Remove-ASUI3IsolatedRoot -Target $tempBase -TempRoot $tempBase -ProtectedRoots $protected
Assert-True '9: 目标等于临时目录拒绝(Refused)' ($st -eq 'Refused') $st

# ---- 10. 删除拒绝必须使冒烟结果失败（轮次判定映射） ----
Assert-True '10: 清理状态 Refused ⇒ 本轮判 FAIL' (Test-ASUI3CleanupRoundDecision -Status 'Refused')
Assert-True '10: 清理状态 Error ⇒ 本轮判 FAIL' (Test-ASUI3CleanupRoundDecision -Status 'Error')
Assert-True '10: 清理状态 Deleted ⇒ 不判 FAIL' (-not (Test-ASUI3CleanupRoundDecision -Status 'Deleted'))
Assert-True '10: 清理状态 NotFound ⇒ 不判 FAIL' (-not (Test-ASUI3CleanupRoundDecision -Status 'NotFound'))

# ---- 清理测试自身产物（仅 PowerShell；junction 链接已按 8 单独移除） ----
# 被拒目录在测试中被正确保留以证明“拒绝”，断言完成后在此显式清理，避免污染系统临时目录。
foreach ($leaf in $nearCases) {
    $p = Join-Path $tempBase $leaf
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue }
}
if (Test-Path -LiteralPath $collision) { Remove-Item -LiteralPath $collision -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path -LiteralPath $legalName) { Remove-Item -LiteralPath $legalName -Recurse -Force -ErrorAction SilentlyContinue }
Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("AS-UI3 REMOVE BOUNDARY TESTS: pass={0} fail={1}" -f $script:pass, $script:fail)
if ($script:fail -gt 0) { exit 1 }
exit 0
