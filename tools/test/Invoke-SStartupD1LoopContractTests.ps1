#Requires -Version 5.1
# tools/test/Invoke-SStartupD1LoopContractTests.ps1
# S-PKG3：Invoke-SStartupD1Loop.ps1 源码契约 + 删除边界集成聚焦测试（总顾问审查修正项）。
#
# 覆盖：
#   A. 窗口关闭兜底不得存在任何裸 SendMessage（含 SendMessageTimeout 子串），杜绝同步等待式
#      投递在目标 UI 线程卡住时的无限阻塞；兜底仅经 PostMessage(WM_CLOSE) + 有界状态轮询，
#      明确超时边界（Close-WindowToTray 默认 $Seconds=10，deadline 轮询），超时返回 $false 判该轮 FAIL。
#   B. 不存在强杀手段：taskkill / Stop-Process / Process.Kill / .Kill( / 按进程名结束。
#   C. 隔离根删除复用共享模块 ASUI3IsolatedRootCleanup.ps1（dot-source），不重复实现较弱删除
#      （无旧版 function Remove-IsolatedRoot 的 StartsWith+Remove-Item）。
#   D. 每轮隔离根名称为共享模块登记格式 ^as-ui3-round-\d+-[0-9a-f]{32}$（否则模块拒绝删除）。
#   E. 删除前门禁：进程已退出 + 日志证据已读取；CSV 含 cleanup_status 列；
#      Refused/Error ⇒ 保留目录、本轮 FAIL、不静默吞掉。
#   F. 功能集成：循环使用的名称格式确实经共享模块删除边界可删；旧 as-d1-round 格式被拒绝（边界未放宽）。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/Invoke-SStartupD1LoopContractTests.ps1
# 退出：0 = 全部通过；1 = 有失败。

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$loopScript = Join-Path $PSScriptRoot 'Invoke-SStartupD1Loop.ps1'
$modulePath = Join-Path $PSScriptRoot 'ASUI3IsolatedRootCleanup.ps1'
$src = Get-Content -LiteralPath $loopScript -Raw -Encoding UTF8

$pass = 0; $fail = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}

Write-Host "== S-PKG3 Invoke-SStartupD1Loop source-contract + cleanup-boundary tests =="

# ---- A. 窗口关闭兜底：无裸 SendMessage；PostMessage + 有界轮询；超时返回 false ----
Assert-True 'A1: 源码不含任何 SendMessage 记号（杜绝同步等待式投递）' (-not ($src -match 'SendMessage')) 'found SendMessage token'
Assert-True 'A2: 兜底使用 PostMessage(WM_CLOSE)' ($src -match 'PostMessage\(\$hwnd') 'no PostMessage fallback'
Assert-True 'A3: 有界轮询超时边界 AddSeconds($Seconds)' ($src -match '\$deadline = \(Get-Date\)\.AddSeconds\(\$Seconds\)') 'no bounded deadline'
Assert-True 'A4: 轮询循环以 deadline 有界 while((Get-Date) -lt $deadline)' ($src -match 'while \(\(Get-Date\) -lt \$deadline\)') 'no bounded while loop'
Assert-True 'A5: 兜底默认明确超时 Close-WindowToTray($proc,[int]$Seconds=10)' ($src -match 'function Close-WindowToTray\(\$proc, \[int\]\$Seconds = 10\)') 'missing 10s default boundary'
Assert-True 'A6: 超时/失败路径返回 false 判 FAIL' ($src -match 'return \$false') 'no false-return on timeout'
Assert-True 'A7: 优先保留 UIA WindowPattern.Close 有限重试' ($src -match 'GetCurrentPattern' -and $src -match 'WindowPattern' -and $src -match '\$a -lt 3') 'UIA close retry missing'

# ---- B. 无强杀 ----
$taskkillLines = @($src -split "`r?`n" | Where-Object { $_ -match 'taskkill' })
Assert-True 'B1: taskkill 仅出现在头注释契约承诺' (($taskkillLines.Count -eq 1) -and ($taskkillLines[0] -match '^# 绝不使用正式数据根')) (($taskkillLines -join '; '))
Assert-True 'B2: 无 Stop-Process' (-not ($src -match 'Stop-Process'))
Assert-True 'B3: 无 .Kill(' (-not ($src -match '\.Kill\s*\('))

# ---- C. 复用共享删除模块 ----
Assert-True 'C1: dot-source ASUI3IsolatedRootCleanup.ps1' ($src -match '\. \(Join-Path \$PSScriptRoot ''ASUI3IsolatedRootCleanup\.ps1''\)') 'missing dot-source'
Assert-True 'C2: 不再存在旧版较弱删除 function Remove-IsolatedRoot' (-not ($src -match 'function Remove-IsolatedRoot')) 'old weaker delete function present'
Assert-True 'C3: 调用共享 Remove-ASUI3IsolatedRoot' ($src -match 'Remove-ASUI3IsolatedRoot') 'missing shared delete call'
Assert-True 'C4: 定义受保护根 formal/repo/user' (($src -match '\$formalRoot') -and ($src -match '\$repoRoot') -and ($src -match '\$userDir')) 'missing protected roots'

# ---- D. 隔离根名称格式 ----
Assert-True 'D1: 隔离根名称使用共享模块登记格式 as-ui3-round-N-<32hex>' ($src -match '"as-ui3-round-\{0\}-\{1\}"') 'name format not module-eligible'

# ---- E. 清理门禁与 CSV ----
Assert-True 'E1: CSV 头含 cleanup_status 列' ($src -match 'cleanup_status') 'cleanup_status column missing'
Assert-True 'E2: 删除前验证进程已退出 (procExitedOk)' ($src -match 'procExitedOk') 'process-exited guard missing'
Assert-True 'E3: 删除前验证日志证据已读取 (LogEvidenceOk)' ($src -match 'LogEvidenceOk') 'log-evidence guard missing'
Assert-True 'E4: Refused/Error 保留目录并判 FAIL（不静默吞掉）' ($src -match '隔离根清理未确认') 'silent-swallow risk'

# ---- F. 功能集成：循环名称格式可删；旧格式拒绝（边界未放宽） ----
. $modulePath
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$protected = @((Join-Path $env:LOCALAPPDATA 'AutoShutdown'), $repoRoot, [Environment]::GetFolderPath('UserProfile'))

$sample = Join-Path $tempBase ('as-ui3-round-1-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $sample | Out-Null
Set-Content -LiteralPath (Join-Path $sample 'config.json') -Value '{}' -Encoding UTF8
$st = Remove-ASUI3IsolatedRoot -Target $sample -TempRoot $tempBase -ProtectedRoots $protected
Assert-True 'F1: 循环格式隔离根经共享模块删除(Deleted)' ($st -eq 'Deleted') $st
Assert-True 'F2: 目录确实被删除' (-not (Test-Path -LiteralPath $sample))

$illegal = Join-Path $tempBase ('as-d1-round-1-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $illegal | Out-Null
$stIllegal = Remove-ASUI3IsolatedRoot -Target $illegal -TempRoot $tempBase -ProtectedRoots $protected
Assert-True 'F3: 旧 as-d1-round 格式仍被共享模块拒绝(Refused)' ($stIllegal -eq 'Refused') $stIllegal
Assert-True 'F4: 拒绝后目录保留' (Test-Path -LiteralPath $illegal)
# 清理测试产物（D4 口径）：illegal 为本测试创建的系统临时直接子目录、名称精确匹配，
# 删除前核对该绝对路径后递归删除。
$legalPath = [IO.Path]::GetFullPath($illegal)
$tempTrim = $tempBase.TrimEnd('\')
$par = [IO.Path]::GetDirectoryName($legalPath)
if (Test-Path -LiteralPath $legalPath) {
    if ($par -and $par.TrimEnd('\').Equals($tempTrim, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $legalPath) -match '^as-d1-round-\d+-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $legalPath -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host '  CLEAN-WARN illegal 路径边界确认失败，保留目录'
    }
}

Write-Host ""
Write-Host ("S-STARTUP-D1-LOOP CONTRACT TESTS: pass={0} fail={1}" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
exit 0
