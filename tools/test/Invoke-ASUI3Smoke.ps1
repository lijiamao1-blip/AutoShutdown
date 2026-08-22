#Requires -Version 5.1
# tools/test/Invoke-ASUI3Smoke.ps1
# S-UI3 UIA 冒烟（≥2 连续轮；S-STARTUP-D1-D2 测试工具最小修正 A–D）。
#
#   A. 每轮独立精确 AUTOSHUTDOWN_DATA_ROOT：全新临时隔离根（as-ui3-round-N-<guid>）+
#      安全 config.json（TestMode=true / RealPowerEnabled=false / StartWithWindows=false /
#      RunCommands 空 / CloseApps 空 / MinimizeToTrayOnClose=true）。绝不复用共享
#      UiTestSandbox 的 tasks.json；绝不删除/清空/覆盖正式数据目录；清理仅针对本脚本创建、
#      且路径位于系统临时目录内的隔离根（无无界递归删除）；每轮记录隔离根绝对路径并校验
#      其与正式数据根不同。
#   B. 有界条件轮询替代固定 1000ms 等待：任务行数 / 「停止」按钮数 / 当前任务卡状态 /
#      每周面板展开 / 「共 N 个任务」均为有界轮询（显式总超时 + 300ms 轮询间隔）。
#      超时即 FAIL 并输出实际计数；无无限重试、无多跑求成功、无盲目延长等待。
#   C. 分步验证：
#      (1) 窗口关闭 → 主窗口隐藏（MainWindowHandle=0）+ 进程仍存活——不把「关闭到托盘」
#          误判为「退出失败」；
#      (2) 真实托盘菜单「退出程序」按本轮精确 PID 执行（绝不强制结束进程、绝不按进程名
#          清理），有界等待退出，确认 0 个 AutoShutdown 残留，日志含
#          TrayExitRequested → ApplicationStopping → ApplicationStopped。
#   D. 每轮完整控制台输出落盘（ui3-round-N-console.log）；保留 launcher/app/UIA 日志、
#      截图与退出证据（ui3-round-N-applogs）；临时文件仅经 PowerShell 在已验证精确路径上
#      删除（无 Bash/rm）。
#
# 约束：
#   - 本轮实例直接以隔离根启动（不设 AUTOSHUTDOWN_UI_TEST——隔离根不是固定 UiTestSandbox，
#     UiTestEnvironment 仅在声明该变量时才校验固定沙箱）。
#   - 启动器（tools/Start-AutoShutdownUiTest.ps1）仍在每轮用于验证「已有实例时入口拒绝启动」
#     （其前置检查在触碰共享沙箱之前即拒绝，退出码非 0）。
#   - 应用绝不执行真实电源：全程 TestMode 双闸门（config.TestMode=true / RealPowerEnabled=false）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/Invoke-ASUI3Smoke.ps1 `
#       [-EvidenceDirectory <dir>] [-Exe <path>] [-Rounds 2]
# 退出：0 = 全部轮通过；1 = 有失败。

[CmdletBinding()]
param(
    [string]$EvidenceDirectory = '',
    [string]$Exe = '',
    [int]$Rounds = 2
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$launcher = Join-Path $root 'tools\Start-AutoShutdownUiTest.ps1'
$trayTool = Join-Path $root 'tools\test\Invoke-SStartupD1TrayExit.ps1'
$formalRoot = Join-Path $env:LOCALAPPDATA 'AutoShutdown'

if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $root 'S-PKG-work包\S-UI3-验收证据'
}
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null

$script:pass = 0
$script:fail = 0
$script:roundLog = ''

# ---------- 控制台 + 本轮日志双写（D：每轮完整输出落盘） ----------
function Write-Out([string]$msg, [string]$logPath = $script:roundLog) {
    Write-Host $msg
    if ($logPath) {
        $dir = Split-Path -Parent $logPath
        if ($dir -and (Test-Path -LiteralPath $dir)) {
            Add-Content -LiteralPath $logPath -Value $msg -Encoding UTF8
        }
    }
}

function Assert-True([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) {
        $script:pass++
        Write-Out ("PASS  {0}" -f $Name)
    } else {
        $script:fail++
        Write-Out ("FAIL  {0}  {1}" -f $Name, $Detail)
    }
}

# ---------- UIA 助手 ----------
function Find-Like($Scope, [string]$Text, [System.Windows.Automation.ControlType]$Type = $null) {
    if (-not $Scope) { return $null }
    $condition = [System.Windows.Automation.Condition]::TrueCondition
    if ($Type) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $Type)
    }
    foreach ($element in $Scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($element.Current.Name -and $element.Current.Name.IndexOf($Text, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $element
        }
    }
    return $null
}

function Wait-Like($Scope, [string]$Text, [int]$Seconds = 15, [System.Windows.Automation.ControlType]$Type = $null) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $element = Find-Like $Scope $Text $Type
        if ($element) { return $element }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Invoke-Element($Element) {
    if (-not $Element -or -not $Element.Current.IsEnabled) { return $false }
    try {
        $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
        return $true
    } catch {
        try {
            $pattern = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $pattern.Select()
            return $true
        } catch { return $false }
    }
}

function Count-Buttons($Scope, [string]$Name) {
    if (-not $Scope) { return 0 }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $count = 0
    foreach ($element in $Scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($element.Current.Name -eq $Name) { $count++ }
    }
    return $count
}

# ---------- 有界条件轮询（B） ----------
function Wait-ButtonCount($Scope, [string]$Name, [int]$Expected, [int]$Seconds = 15) {
    # 轮询实际 UI 条件（「停止」按钮数 = 任务行数）；显式总超时 + 300ms 间隔；
    # 超时返回实际计数，由调用方在 FAIL Detail 中输出。
    $deadline = (Get-Date).AddSeconds($Seconds)
    $actual = -1
    while ((Get-Date) -lt $deadline) {
        $actual = Count-Buttons $Scope $Name
        if ($actual -eq $Expected) { return $actual }
        Start-Sleep -Milliseconds 300
    }
    return $actual
}

function Wait-NoIdleState($win, [int]$Seconds = 15) {
    # 有界轮询：等待首页「当前任务」卡离开空态「当前没有活动任务」
    #（首条任务创建完成的真实 UI 信号，替代固定 1200ms 等待）。
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $idle = Find-Like $win '当前没有活动任务'
        if (-not $idle) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# ---------- 正式数据目录快照 / 隔离根 ----------
function Get-FormalDataSnapshot {
    if (-not (Test-Path -LiteralPath $formalRoot)) { return @() }
    return @(Get-ChildItem -LiteralPath $formalRoot -File -Recurse -Force -ErrorAction SilentlyContinue |
        Sort-Object FullName |
        ForEach-Object {
            '{0}|{1}|{2}' -f $_.FullName, $_.Length, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        })
}

function New-IsolatedRoot([int]$round) {
    $d = Join-Path ([IO.Path]::GetTempPath()) ("as-ui3-round-{0}-{1}" -f $round, [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    # 枚举值必须是数字：应用以默认 System.Text.Json 反序列化（无 JsonStringEnumConverter），
    # 字符串名（'Shutdown'/'Information'）会触发 JsonException → 配置 Invalid → 不进入测试模式。
    # 与共享沙箱既有有效 config.json 一致：PowerAction.Shutdown=1..Hibernate=4，LogLevel.Information=1。
    $config = [ordered]@{
        SchemaVersion = 1; TestMode = $true; RealPowerEnabled = $false; DefaultWarningSeconds = 60
        DefaultSnoozeSeconds = 300; StartWithWindows = $false
        AllowedActions = @(1,2,3,4); MinimizeToTrayOnClose = $true
        Logging = @{ Level = 1; RetentionDays = 14 }
        CloseApps = @{ GracefulTimeoutSeconds = 30; Targets = @() }
        RunCommands = @{ DefaultTimeoutSeconds = 30; Whitelist = @{ Allow = @() }; Commands = @() }
    }
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $d 'config.json') -Encoding UTF8
    return $d
}

function Remove-IsolatedRoot([string]$d) {
    # A：仅删除本脚本创建的、路径位于系统临时目录内的隔离根；绝不触碰正式数据目录。
    $tempFull = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $targetFull = $null
    try { $targetFull = [IO.Path]::GetFullPath($d) } catch { return }
    if (-not $targetFull.StartsWith($tempFull, [StringComparison]::OrdinalIgnoreCase)) { return }
    try { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
}

# ---------- 窗口 / 截图 / 关闭到托盘 ----------
function Save-WindowScreenshot($Window, [string]$Path) {
    $rect = $Window.Current.BoundingRectangle
    if ($rect.IsEmpty) { return $false }
    $bitmap = New-Object Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
        return $true
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Close-WindowGracefully($Window) {
    if (-not $Window) { return $false }
    try {
        $pattern = $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $pattern.Close()
        return $true
    } catch {
        Write-Out 'WARN  无法通过窗口关闭模式关闭应用；未结束进程。'
        return $false
    }
}

function Wait-WindowHidden($proc, [int]$Seconds = 10) {
    # C1：有界轮询主窗口句柄归零（窗口关闭 → 隐藏到托盘）。
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { return $false }
        if ($proc.MainWindowHandle -eq 0) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Wait-ProcessExited($proc, [int]$Seconds = 10) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

# ---------- 候选 EXE：显式指定，否则构建当前源码（保证测 HEAD 对应源码） ----------
$head = (& git -C $root rev-parse HEAD 2>&1).Trim()
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw '无法读取真实 Git HEAD。' }
$buildLog = Join-Path $EvidenceDirectory ('ui3-build-{0}.log' -f $head.Substring(0, 7))
if (-not $Exe) {
    $out = Join-Path $root ('.build-tmp\S-UI3-' + $head.Substring(0, 7))
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Write-Out ("== 构建当前源码 (HEAD {0}) ==" -f $head.Substring(0, 7)) $buildLog
    & dotnet build (Join-Path $root 'src\AutoShutdown.App\AutoShutdown.App.csproj') -c Release --nologo -p:OutputPath=$out 2>&1 | ForEach-Object { Write-Out ($_ | Out-String).TrimEnd() $buildLog }
    if ($LASTEXITCODE -ne 0) { throw ('Release 构建失败，不会启动任何旧程序。' + " log=$buildLog") }
    $Exe = Join-Path $out 'AutoShutdown.App.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) { throw "候选 EXE 不存在: $Exe" }
Write-Out ("EXE: {0}" -f $Exe) $buildLog
Write-Out ("Evidence: {0}" -f $EvidenceDirectory) $buildLog

# ---------- 每轮汇总报告（D：每轮结果落盘） ----------
$reportPath = Join-Path $EvidenceDirectory ('ui3-smoke-{0:yyyyMMdd-HHmmss}.csv' -f (Get-Date))
Set-Content -LiteralPath $reportPath -Value 'round,data_root,pid,stop_count,tray_exit_code,residual,formal_unchanged,result' -Encoding UTF8

# 启动前必须无 AutoShutdown 实例
$pre = @(Get-Process -Name 'AutoShutdown*' -ErrorAction SilentlyContinue)
if ($pre.Count -gt 0) {
    Write-Out ("FAIL  启动前存在 AutoShutdown 实例：" + $pre.Count)
    exit 1
}

$anyFail = $false
$envBackup = $env:AUTOSHUTDOWN_DATA_ROOT

for ($i = 1; $i -le $Rounds; $i++) {
    $script:roundLog = Join-Path $EvidenceDirectory ("ui3-round-{0}-console.log" -f $i)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $script:roundLog) | Out-Null
    Set-Content -LiteralPath $script:roundLog -Value ("== S-UI3 UIA SMOKE round {0} ({1}) ==" -f $i, (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) -Encoding UTF8
    $roundPid = ''
    $stopCount = -1
    $trayCode = -1
    $residual = -1
    $formalUnchanged = $false
    $roundFail = $false
    $proc = $null
    $win = $null
    $failAtRoundStart = $script:fail

    $before = Get-FormalDataSnapshot

    # A：每轮全新隔离数据根
    $dataRoot = New-IsolatedRoot $i
    Write-Out ("A: 本轮数据根 = " + $dataRoot)
    $dataFull = [IO.Path]::GetFullPath($dataRoot)
    $formalFull = [IO.Path]::GetFullPath($formalRoot)
    $differsFormal = (-not $dataFull.TrimEnd('\').Equals($formalFull.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) -and
                     (-not $dataFull.StartsWith($formalFull + '\', [StringComparison]::OrdinalIgnoreCase))
    Assert-True 'A: 数据根独立于正式数据根' $differsFormal ("dataRoot={0} formalRoot={1}" -f $dataRoot, $formalRoot)

    # 启动本轮实例（直接启动；不设 AUTOSHUTDOWN_UI_TEST）
    $env:AUTOSHUTDOWN_DATA_ROOT = $dataRoot
    $proc = Start-Process -FilePath $Exe -PassThru -WorkingDirectory (Split-Path -Parent $Exe)
    $roundPid = $proc.Id

    # 有界等待主窗口出现
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { break }
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
        $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $cond)
        if ($candidate -and $candidate.Current.Name -eq '电脑自动关机助手') { $win = $candidate; break }
        Start-Sleep -Milliseconds 400
    }
    Assert-True '主窗口成功出现' ($null -ne $win -and -not $proc.HasExited)
    if (-not $win -or $proc.HasExited) {
        $roundFail = $true
        Write-Out ('FAIL 主窗口未出现；PID=' + $roundPid)
    } else {
        # 安全测试模式双闸门：隔离根模式下不设 AUTOSHUTDOWN_UI_TEST，头部横幅为
        # config 驱动的「安全测试模式」+「当前不会执行真实系统电源操作」；
        # UI 测试专属长横幅仅在声明 AUTOSHUTDOWN_UI_TEST 时显示，此处不应出现。
        $banner = Wait-Like $win '安全测试模式' 20
        Assert-True '安全测试模式横幅可见' ($null -ne $banner -and -not $banner.Current.IsOffscreen)
        $noRealPower = Wait-Like $win '当前不会执行真实系统电源操作' 10
        Assert-True '双闸门：不执行真实系统电源提示可见' ($null -ne $noRealPower -and -not $noRealPower.Current.IsOffscreen)

        # 第二次调用入口必须拒绝，且现有 GUI 仍存活；启动器不结束或转发到现有实例。
        $samePid = $proc.Id
        $launcherOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $launcher 2>&1
        $secondExit = $LASTEXITCODE
        foreach ($line in $launcherOut) { Write-Out ("  [launcher] " + $line) }
        $stillAlive = Get-Process -Id $samePid -ErrorAction SilentlyContinue
        Assert-True '已有实例时入口拒绝启动' ($secondExit -ne 0) ("exit={0}" -f $secondExit)
        Assert-True '拒绝启动未结束已有实例' ($null -ne $stillAlive)

        # ---- 创建两条任务（B：有界条件轮询替代固定等待） ----
        $create1 = Wait-Like $win '创建任务' 15 ([System.Windows.Automation.ControlType]::Button)
        Assert-True '创建第一条模拟任务' (Invoke-Element $create1)
        Assert-True '第一条任务创建完成(当前任务卡状态变化)' (Wait-NoIdleState $win 15)

        $weekly = Wait-Like $win '每周指定星期' 10 ([System.Windows.Automation.ControlType]::RadioButton)
        Assert-True '切换每周模式以创建不同任务' (Invoke-Element $weekly)
        $sun = Wait-Like $win '周日' 10 ([System.Windows.Automation.ControlType]::CheckBox)
        Assert-True '每周指定星期面板已展开(周日可见)' ($null -ne $sun)

        $create2 = Wait-Like $win '创建任务' 10 ([System.Windows.Automation.ControlType]::Button)
        Assert-True '创建第二条模拟任务' (Invoke-Element $create2)
        $twoTasks = Wait-Like $win '共 2 个任务' 15
        Assert-True '首页显示两条任务' ($null -ne $twoTasks)

        $tasksNav = Wait-Like $win 'PageKey = tasks' 10 ([System.Windows.Automation.ControlType]::ListItem)
        if (-not $tasksNav) {
            $tasksNav = Wait-Like $win '任务管理' 5 ([System.Windows.Automation.ControlType]::ListItem)
        }
        Assert-True '进入任务管理页' (Invoke-Element $tasksNav)
        $stopCount = Wait-ButtonCount $win '停止' 2 15
        Assert-True '任务管理显示两条任务' ($stopCount -eq 2) ("停止按钮数={0}" -f $stopCount)

        $shot = Join-Path $EvidenceDirectory ('S-UI3-uia-two-tasks-round-{0}.png' -f $i)
        Assert-True '保存 UIA 截图证据' (Save-WindowScreenshot $win $shot) $shot

        # ---- C1：窗口关闭 → 隐藏到托盘（进程仍存活，非退出失败） ----
        [void](Close-WindowGracefully $win)
        $hidden = Wait-WindowHidden $proc 10
        Assert-True 'C1: 关闭窗口后主窗口隐藏' $hidden
        Assert-True 'C1: 关闭窗口后进程仍存活(隐藏到托盘)' (-not $proc.HasExited)
    }

    # ---- C2：真实托盘菜单「退出程序」按本轮精确 PID ----
    if ($proc -and -not $proc.HasExited) {
        $trayOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $trayTool -TargetPid $proc.Id -TimeoutSeconds 25 2>&1
        $trayCode = $LASTEXITCODE
        foreach ($line in $trayOut) { Write-Out ("  [tray] " + $line) }
        Assert-True 'C2: 托盘「退出程序」按精确 PID 完成' ($trayCode -eq 0) ("trayExit={0}" -f $trayCode)
        $exited = Wait-ProcessExited $proc 10
        Assert-True 'C2: 进程按托盘退出已结束' $exited
    } else {
        $trayCode = 99
        Assert-True 'C2: 托盘「退出程序」按精确 PID 完成' $false '进程已退出或从未出现'
    }
    # 本轮目标 PID 的进程表条目在退出后可能瞬时残留，不视为残留；统计其余 AutoShutdown 进程。
    $residualList = @(Get-Process -Name 'AutoShutdown*' -ErrorAction SilentlyContinue |
        Where-Object { $null -eq $proc -or $_.Id -ne $proc.Id })
    $residual = $residualList.Count
    Assert-True 'C2: 无 AutoShutdown 残留进程' ($residual -eq 0) ("residual={0}" -f $residual)

    # ---- C2：日志含 TrayExitRequested → ApplicationStopping → ApplicationStopped ----
    $logDir = Join-Path $dataRoot 'logs'
    $logFile = Get-ChildItem -Path $logDir -Recurse -Filter '*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $logPath = ''
    if ($logFile) {
        $logPath = $logFile.FullName
        $logText = Get-Content -LiteralPath $logFile.FullName -Raw -Encoding UTF8
        $idxTray = $logText.IndexOf('TrayExitRequested')
        $idxStop = $logText.IndexOf('ApplicationStopping')
        $idxStopped = $logText.IndexOf('ApplicationStopped')
        Assert-True 'C2: 日志含 TrayExitRequested' ($idxTray -ge 0)
        Assert-True 'C2: 日志含 ApplicationStopping' ($idxStop -ge 0)
        Assert-True 'C2: 日志含 ApplicationStopped' ($idxStopped -ge 0)
        Assert-True 'C2: 日志顺序 TrayExitRequested→ApplicationStopping→ApplicationStopped' ($idxTray -ge 0 -and $idxTray -lt $idxStop -and $idxStop -lt $idxStopped)
        if ($logText -match 'StartupFailed|StartupTimedOut|CrashRecoveryTimeout') {
            Assert-True '日志无启动失败事件' $false '检测到启动失败事件'
        }
    } else {
        Assert-True '找到本轮应用日志' $false '日志目录为空'
    }

    # 正式数据目录前后一致
    $after = Get-FormalDataSnapshot
    $formalUnchanged = (($before -join "`n") -eq ($after -join "`n"))
    Assert-True '正式数据目录未改变' $formalUnchanged

    # ---- D：保留本轮 app 日志到证据目录 ----
    if ($logFile -and (Test-Path -LiteralPath $logDir)) {
        $appLogDest = Join-Path $EvidenceDirectory ("ui3-round-{0}-applogs" -f $i)
        New-Item -ItemType Directory -Force -Path $appLogDest | Out-Null
        Copy-Item -Path (Join-Path $logDir '*') -Destination $appLogDest -Recurse -Force -ErrorAction SilentlyContinue
        Write-Out ('D: 本轮 app 日志已复制到 ' + $appLogDest)
    }

    # ---- 清理：仅本脚本创建的隔离根；残留进程绝不强杀 ----
    $env:AUTOSHUTDOWN_DATA_ROOT = $envBackup
    if ($proc -and -not $proc.HasExited) {
        $roundFail = $true
        Write-Out ('FAIL 本轮实例未退出 (pid=' + $proc.Id + ')，保留隔离根证据；绝不强制结束进程。')
    } else {
        Remove-IsolatedRoot $dataRoot
    }

    # ---- 本轮结果判定（基于本轮新增失败数） ----
    if ($script:fail -gt $failAtRoundStart) { $roundFail = $true }
    $result = if ($roundFail) { 'FAIL' } else { 'PASS' }
    $row = '{0},{1},{2},{3},{4},{5},{6},{7}' -f $i, $dataRoot, $roundPid, $stopCount, $trayCode, $residual, $formalUnchanged, $result
    Add-Content -LiteralPath $reportPath -Value $row -Encoding UTF8
    Write-Out ("第 {0}/{1} 轮: {2}  PID={3}  停止按钮数={4}  trayExit={5}  residual={6}  formalUnchanged={7}" -f `
        $i, $Rounds, $result, $roundPid, $stopCount, $trayCode, $residual, $formalUnchanged)

    if ($roundFail) {
        $anyFail = $true
        if ($proc -and -not $proc.HasExited) { break }
    }
}

$env:AUTOSHUTDOWN_DATA_ROOT = $envBackup
$summary = ("S-UI3 UIA SMOKE: pass={0} fail={1} skip=0  rounds={2}" -f $script:pass, $script:fail, $Rounds)
Write-Out $summary
Write-Out ("报告: " + $reportPath)
Write-Out ("Evidence: " + $EvidenceDirectory)
if ($anyFail -or $script:fail -gt 0) { exit 1 }
exit 0
