# S-STARTUP-D1 20 轮真机循环：启动候选 → 确认主窗口 → 二次启动激活并退出 → 托盘退出 → 无残留。
# 隔离数据根（每轮全新临时目录）+ 安全 config.json；不设 AUTOSHUTDOWN_UI_TEST（需验证正常单实例转发）。
# 绝不使用正式数据根、不 taskkill、不按进程名杀进程。
param(
    [int]$Rounds = 20,
    [string]$Exe = '',
    [string]$ReportPath = '',
    [int]$WindowTimeoutSeconds = 30,
    [int]$SecondaryTimeoutSeconds = 15,
    [int]$TrayTimeoutSeconds = 25
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# S-PKG3：复用 S-STARTUP-D1-D3/D4 已验收的共享删除边界模块（ASUI3IsolatedRootCleanup.ps1），
# 不重复实现较弱删除；dot-source 同时按其契约启用 Set-StrictMode -Version 2.0。
. (Join-Path $PSScriptRoot 'ASUI3IsolatedRootCleanup.ps1')

# 受保护根：正式数据根 / 仓库目录 / 用户目录（共享模块删除边界拒绝条件）。
$formalRoot = Join-Path $env:LOCALAPPDATA 'AutoShutdown'
$repoRoot = $root
$userDir = [Environment]::GetFolderPath('UserProfile')
$protectedRoots = @($formalRoot, $repoRoot, $userDir)
if (-not $Exe) { $Exe = Join-Path $root 'artifacts\release\v2.0.0\S-STARTUP-D1-c6e99d0\AutoShutdown-v2.0.0-S-STARTUP-D1.c6e99d0.exe' }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Host ("候选 EXE 不存在: " + $Exe); exit 10 }

if (-not $ReportPath) {
    $evDir = Join-Path $root 'S-PKG-work包\S-STARTUP-D1-循环证据'
    New-Item -ItemType Directory -Force -Path $evDir | Out-Null
    $ReportPath = Join-Path $evDir ('loop-{0:yyyyMMdd-HHmmss}.csv' -f (Get-Date))
}
$reportDir = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class L32 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint wpid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
"@

function Click-Right([int]$x, [int]$y) {
    [L32]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 80
    [L32]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [L32]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
}
function Click-Left([int]$x, [int]$y) {
    [L32]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 80
    [L32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [L32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}
function Find-Exact([string]$Name) {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Send-Escape {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}') | Out-Null
    } catch { }
    Start-Sleep -Milliseconds 300
}
# Win11 系统托盘溢出区是 explorer 的持久顶层窗口，类名 TopLevelWindowForOverflowXamlIsland，
# 用 chevron 切换可见性。scoped 到该窗口搜索才快（桌面全量 FindAll 数秒~数十秒）。
function Get-FlyoutHwnd {
    $exp = Get-Process -Name explorer -ErrorAction SilentlyContinue | Select-Object -First 1
    $script:FlyoutHwnd = [IntPtr]::Zero
    $script:FlyoutVis = $false
    if (-not $exp) { return }
    [L32]::EnumWindows({
        param($hWnd, $lParam)
        [uint32]$wpid = 0
        [L32]::GetWindowThreadProcessId($hWnd, [ref]$wpid) | Out-Null
        if ($wpid -eq $exp.Id) {
            $class = New-Object System.Text.StringBuilder 256
            [L32]::GetClassName($hWnd, $class, 256) | Out-Null
            if ($class.ToString() -eq 'TopLevelWindowForOverflowXamlIsland') {
                $script:FlyoutHwnd = $hWnd
                $script:FlyoutVis = [L32]::IsWindowVisible($hWnd)
                return $false
            }
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
}

function Get-FormalSnapshot {
    $formalRoot = Join-Path $env:LOCALAPPDATA 'AutoShutdown'
    $sandbox = Join-Path $formalRoot 'UiTestSandbox'
    if (-not (Test-Path -LiteralPath $formalRoot)) { return @() }
    return @(Get-ChildItem -LiteralPath $formalRoot -File -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { -not $_.FullName.StartsWith($sandbox + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object FullName |
        ForEach-Object { '{0}|{1}|{2}' -f $_.FullName, $_.Length, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
}

# 托盘退出：归一化状态(ESC) → 打开溢出区 → 图标右键 → 「退出程序」；返回 $true/$false
function Invoke-TrayExit([int]$TargetPid) {
    Send-Escape
    Start-Sleep -Milliseconds 400

    # 定位溢出区窗口；若已打开则再 ESC 一次确保关闭，再打开（chevron 是切换开关）
    $null = Get-FlyoutHwnd
    if ($script:FlyoutVis) {
        Send-Escape
        Start-Sleep -Milliseconds 600
        $null = Get-FlyoutHwnd
    }
    if (-not $script:FlyoutVis) {
        $chev = Find-Exact '显示隐藏的图标'
        if (-not $chev) { return $false }
        try { $chev.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() | Out-Null } catch { return $false }
        Start-Sleep -Milliseconds 1500
        $null = Get-FlyoutHwnd
    }
    if ($script:FlyoutHwnd -eq [IntPtr]::Zero -or -not $script:FlyoutVis) { return $false }

    # 在溢出区窗口子树内 scoped 搜索托盘图标（快速；桌面全量 FindAll 极慢）
    $icon = $null
    try {
        $flyEl = [System.Windows.Automation.AutomationElement]::FromHandle($script:FlyoutHwnd)
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '电脑自动关机助手')
        foreach ($e in $flyEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            $rr = $e.Current.BoundingRectangle
            if ($rr.Width -gt 0 -and $rr.Width -lt 100 -and $rr.Height -lt 100 -and $e.Current.ClassName -eq 'SystemTray.NormalButton') {
                $icon = $e; break
            }
        }
    } catch { return $false }
    if (-not $icon) { return $false }
    $r = $icon.Current.BoundingRectangle
    $cx = [int]($r.X + $r.Width/2); $cy = [int]($r.Y + $r.Height/2)

    Click-Right $cx $cy
    Start-Sleep -Milliseconds 900

    $item = Find-Exact '退出程序'
    if (-not $item) {
        Send-Escape
        Click-Right $cx $cy
        Start-Sleep -Milliseconds 900
        $item = Find-Exact '退出程序'
    }
    if (-not $item) { return $false }
    try { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() | Out-Null } catch { return $false }
    return $true
}

# 窗口关闭 → 隐藏到托盘：经 UIA WindowPattern.Close() 触发应用「最小化到托盘」，
# 有界轮询主窗口句柄归零（隐藏）且进程仍存活（S-PKG3 专项要求；绝不强杀）。
# 激活转发后 MainWindowHandle 句柄缓存可能过期，UIA FromHandle+Close 会间歇抛
# ElementNotAvailableException；采用 ①UIA Close（有限重试）→ ②Win32 PostMessage(WM_CLOSE) 兜底，
# 仍只触发应用自己的窗口关闭逻辑（等价于点窗口关闭按钮），绝不强杀/按名结束。
# 兜底禁用同步等待式消息投递（目标 UI 线程卡住时可能无限阻塞），改用非阻塞
# PostMessage + 下方有界轮询：真实超时边界为 $Seconds 秒，超时即返回 $false 判该轮 FAIL。
function Close-WindowToTray($proc, [int]$Seconds = 10) {
    if (-not $proc -or $proc.HasExited) { return $false }

    # ① UIA WindowPattern.Close()，有限重试（每次重新 Refresh 读句柄）
    $viaUia = $false
    for ($a = 0; $a -lt 3; $a++) {
        $proc.Refresh()
        if ($proc.HasExited) { break }
        $hwnd = $proc.MainWindowHandle
        if ($hwnd -eq 0) { return $true }   # 已隐藏到托盘
        try {
            $el = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            $pattern = $el.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            $pattern.Close()
            $viaUia = $true
            break
        } catch {
            Write-Host ("    [winClose] UIA Close 第 {0} 次失败: {1} (hwnd=0x{2:X})" -f ($a+1), $_.Exception.GetType().Name, $hwnd)
            Start-Sleep -Milliseconds 500
        }
    }

    # ② UIA 兜底失败则用 Win32 PostMessage(WM_CLOSE)（等价用户点窗口关闭；仍走应用关闭→隐藏到托盘）
    if (-not $viaUia -and -not $proc.HasExited) {
        $proc.Refresh()
        $hwnd = $proc.MainWindowHandle
        if ($hwnd -ne 0) {
            Write-Host ("    [winClose] 兜底 PostMessage WM_CLOSE hwnd=0x{0:X}" -f $hwnd)
            [L32]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        }
    }

    # 有界轮询窗口隐藏（MainWindowHandle 归零）
    # 诊断采样：若设置 $env:SPKG3_WINCLOSE_TRACE，则记录每次采样的 hwnd/可见性/进程存活
    $trace = $env:SPKG3_WINCLOSE_TRACE
    $deadline = (Get-Date).AddSeconds($Seconds)
    $swT = [System.Diagnostics.Stopwatch]::StartNew()
    while ((Get-Date) -lt $deadline) {
        $proc.Refresh()
        if ($trace) {
            $curH = $proc.MainWindowHandle
            $vis = $false
            if ($curH -ne 0) { try { $vis = [L32]::IsWindowVisible($curH) } catch { } }
            Add-Content -LiteralPath $trace -Value ("{0,6}ms hwnd=0x{1:X} vis={2} exited={3} pid={4}" -f $swT.ElapsedMilliseconds, $curH, $vis, $proc.HasExited, $proc.Id) -Encoding UTF8
        }
        if ($proc.HasExited) { return $false }
        if ($proc.MainWindowHandle -eq 0) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function New-IsolatedRoot([int]$round) {
    # S-PKG3：每轮隔离根命名为共享删除模块登记格式 as-ui3-round-N-<32hex>，
    # 使 Remove-ASUI3IsolatedRoot 的名称/位置边界可直接确认（不另设较弱删除）。
    $d = Join-Path ([IO.Path]::GetTempPath()) ("as-ui3-round-{0}-{1}" -f $round, [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    $config = [ordered]@{
        SchemaVersion = 1; TestMode = $true; RealPowerEnabled = $false; DefaultWarningSeconds = 60
        DefaultSnoozeSeconds = 300; StartWithWindows = $false
        AllowedActions = @('Shutdown','Restart','Sleep','Hibernate'); MinimizeToTrayOnClose = $true
        Logging = @{ Level = 'Information'; RetentionDays = 14 }
        CloseApps = @{ GracefulTimeoutSeconds = 30; Targets = @() }
        RunCommands = @{ DefaultTimeoutSeconds = 30; Whitelist = @{ Allow = @() }; Commands = @() }
    }
    $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $d 'config.json') -Encoding UTF8
    return $d
}
# S-PKG3：隔离根清理经共享模块 Remove-ASUI3IsolatedRoot（D3/D4 已验收删除边界）。
# 删除前附加两项门禁：① 进程已退出（主实例已退且无残留）；② 日志证据已读取
# （log_path 非空；失败轮证据已在判 FAIL 分支复制到 round-N-fail-*）。
# 返回 'Deleted' | 'NotFound' | 'Refused' | 'Error'；Refused/Error 时调用方保留目录并判该轮 FAIL。
function Invoke-ASUI3GuardedCleanup([string]$Target, [string[]]$ProtectedRoots, [bool]$ProcessExited, [bool]$LogEvidenceOk) {
    if (-not $ProcessExited) { return 'Refused' }
    if (-not $LogEvidenceOk) { return 'Refused' }
    return (Remove-ASUI3IsolatedRoot -Target $Target -TempRoot ([IO.Path]::GetTempPath()) -ProtectedRoots $ProtectedRoots)
}

$header = 'round,primary_pid,window_handle,t_window_ms,secondary_pid,t_secondary_ms,secondary_exitcode,foreground_activated,window_close_ok,log_forward_ok,tray_ok,t_tray_ms,primary_exited,residual,formal_unchanged,log_tray_seq_ok,cleanup_status,log_path,result'
Set-Content -LiteralPath $ReportPath -Value $header -Encoding UTF8

$pass = 0
$fail = 0
$envBackup = $env:AUTOSHUTDOWN_DATA_ROOT

for ($i = 1; $i -le $Rounds; $i++) {
    $roundResult = 'PASS'
    $primaryPid = ''; $handle = ''; $tWindow = ''
    $secondaryPid = ''; $tSecondary = ''; $secondaryExitCode = ''
    $activated = 'FALSE'; $windowCloseOk = 'FALSE'; $forwardOk = 'FALSE'; $trayOk = 'FALSE'; $tTray = ''
    $primaryExited = 'FALSE'; $residual = ''; $formalUnchanged = 'FALSE'; $logTraySeq = 'FALSE'; $cleanupStatus = 'Pending'; $logPath = ''
    $problems = @()

    $dataRoot = New-IsolatedRoot $i
    $formalBefore = Get-FormalSnapshot

    $pre = @(Get-Process -Name 'AutoShutdown*' -ErrorAction SilentlyContinue)
    if ($pre.Count -gt 0) {
        $problems += ("前置残留进程 " + $pre.Count)
    }

    $env:AUTOSHUTDOWN_DATA_ROOT = $dataRoot

    # 归一化前台环境：关闭上一轮遗留的托盘溢出区/菜单，避免壳窗口占据前台干扰激活证据
    Send-Escape
    Start-Sleep -Milliseconds 400

    # ---- 1) 启动主实例 ----
    $sw1 = [System.Diagnostics.Stopwatch]::StartNew()
    $p1 = Start-Process -FilePath $Exe -PassThru
    $primaryPid = $p1.Id
    $deadline = (Get-Date).AddSeconds($WindowTimeoutSeconds)
    $hwnd = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline) {
        $p1.Refresh()
        if ($p1.HasExited) { break }
        if ($p1.MainWindowHandle -ne 0) { $hwnd = $p1.MainWindowHandle; break }
        Start-Sleep -Milliseconds 150
    }
    $sw1.Stop()
    $tWindow = $sw1.ElapsedMilliseconds
    $handle = $hwnd
    if ($hwnd -eq 0) { $problems += '主窗口未出现' }
    if ($p1.HasExited) { $problems += ('主实例提前退出 code=' + $p1.ExitCode) }

    # ---- 2) 二次启动（激活转发） ----
    if ($p1 -and -not $p1.HasExited) {
        $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
        $p2 = Start-Process -FilePath $Exe -PassThru
        $secondaryPid = $p2.Id
        $exited2 = $p2.WaitForExit($SecondaryTimeoutSeconds * 1000)
        $sw2.Stop()
        $tSecondary = $sw2.ElapsedMilliseconds
        if ($exited2) { $secondaryExitCode = $p2.ExitCode } else { $problems += '副实例未在有限时间退出' }
        if (-not $exited2 -and $p2) { $problems += ('副实例残留 pid=' + $p2.Id) }

        $p1.Refresh()
        if ($p1.HasExited) { $problems += '主实例被次实例顶替或已退出' }
        else {
            $probe = Get-Process -Id $p1.Id -ErrorAction SilentlyContinue
            if (-not $probe) { $problems += '主实例进程消失' }
            elseif ($probe.MainWindowHandle -eq 0) { $problems += '主实例窗口句柄丢失' }
            else {
                $fcDeadline = (Get-Date).AddSeconds(5)
                $lastFg = [IntPtr]::Zero
                while ((Get-Date) -lt $fcDeadline) {
                    $fg = [L32]::GetForegroundWindow()
                    $lastFg = $fg
                    if ($fg -eq $p1.MainWindowHandle) { $activated = 'TRUE'; break }
                    Start-Sleep -Milliseconds 200
                }
                if ($activated -ne 'TRUE') {
                    $problems += ('未观测到主窗口置于前台（激活证据不足；fg=0x{0:X} target=0x{1:X}）' -f $lastFg, $p1.MainWindowHandle)
                }
            }
        }
    }

    # ---- 2.5) 窗口关闭 → 隐藏到托盘（主进程仍存活） ----
    if ($p1 -and -not $p1.HasExited) {
        $windowCloseOk = (Close-WindowToTray $p1)
        if (-not $windowCloseOk) { $problems += '窗口关闭未隐藏到托盘或进程未存活' }
    } else {
        $problems += '窗口关闭前主实例已退出'
    }

    # ---- 3) 托盘退出主实例 ----
    if ($p1 -and -not $p1.HasExited) {
        $sw3 = [System.Diagnostics.Stopwatch]::StartNew()
        $trayOk = (Invoke-TrayExit $p1.Id)
        $sw3.Stop()
        $exited3 = $p1.WaitForExit($TrayTimeoutSeconds * 1000)
        $tTray = $sw3.ElapsedMilliseconds
        if ($trayOk -and $exited3) { $primaryExited = 'TRUE' }
        else { $problems += ('托盘退出未完成 ok=' + $trayOk + ' exited=' + $exited3) }
    }

    # ---- 4) 日志检查（隔离根内） ----
    $logDir = Join-Path $dataRoot 'logs'
    $logFile = Get-ChildItem -Path $logDir -Recurse -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($logFile) {
        $logPath = $logFile.FullName
        $logText = Get-Content -LiteralPath $logFile.FullName -Raw -Encoding UTF8
        if ($logText -notmatch 'PrimaryInstanceAcquired') { $problems += '日志缺 PrimaryInstanceAcquired' }
        if ($logText -notmatch 'ApplicationStarted') { $problems += '日志缺 ApplicationStarted' }
        if ($logText -notmatch 'MainWindowActivated') { $problems += '日志缺 MainWindowActivated' }
        if ($logText -match 'ActivationForwardFailed') { $problems += '日志含 ActivationForwardFailed' }
        elseif ($activated -eq 'TRUE') { $forwardOk = 'TRUE' }
        if ($logText -match 'StartupFailed|StartupTimedOut|CrashRecoveryTimeout') { $problems += '日志含启动失败事件' }
        # S-PKG3 专项：托盘退出日志顺序 TrayExitRequested → ApplicationStopping → ApplicationStopped
        # 注意副实例启动时也会写一条 ApplicationStopped（在 TrayExitRequested 之前），
        # 因此必须从 TrayExitRequested 的位置之后搜索后续两条，才命中主实例托盘退出序列。
        $iTrayExit = $logText.LastIndexOf('TrayExitRequested')
        $iStopping = -1; $iStopped = -1
        if ($iTrayExit -ge 0) {
            $iStopping = $logText.IndexOf('ApplicationStopping', $iTrayExit)
            if ($iStopping -ge 0) { $iStopped = $logText.IndexOf('ApplicationStopped', $iStopping) }
        }
        if ($iTrayExit -ge 0 -and $iStopping -gt $iTrayExit -and $iStopped -gt $iStopping) { $logTraySeq = 'TRUE' }
        else { $problems += '日志缺 TrayExitRequested→ApplicationStopping→ApplicationStopped 顺序' }
    } else {
        $problems += '未找到隔离根日志'
    }

    # ---- 5) 残留检查 ----
    $residualList = @(Get-Process -Name 'AutoShutdown*' -ErrorAction SilentlyContinue)
    $residual = $residualList.Count
    if ($residual -gt 0) {
        $problems += ("残留进程 " + $residual)
        foreach ($rp in $residualList) { $problems += ('  残留 pid=' + $rp.Id) }
    }

    # ---- 6) 正式数据目录前后一致 ----
    $formalAfter = Get-FormalSnapshot
    $formalUnchanged = ((($formalBefore -join "`n") -eq ($formalAfter -join "`n")) -and ($formalBefore.Count -eq $formalAfter.Count))
    if (-not $formalUnchanged) { $problems += '正式数据目录变化' }

    $env:AUTOSHUTDOWN_DATA_ROOT = $envBackup

    if ($problems.Count -gt 0) {
        $roundResult = 'FAIL'; $fail++
        # 保留失败轮证据：完整日志目录 + 控制台诊断
        $failDir = Join-Path (Split-Path -Parent $ReportPath) ("round-{0}-fail-{1}" -f $i, $primaryPid)
        New-Item -ItemType Directory -Force -Path $failDir | Out-Null
        if (Test-Path -LiteralPath (Join-Path $dataRoot 'logs')) {
            Copy-Item -Path (Join-Path $dataRoot 'logs') -Destination $failDir -Recurse -Force -ErrorAction SilentlyContinue
            Get-ChildItem -Path (Join-Path $failDir 'logs') -Recurse -File -ErrorAction SilentlyContinue |
                ForEach-Object { Write-Host ("    日志文件: {0}  {1} 字节" -f $_.Name, $_.Length) }
        }
        $diagLog = Get-ChildItem -Path (Join-Path $failDir 'logs') -Recurse -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($diagLog) {
            Write-Host '    --- 日志尾部 ---'
            Get-Content -LiteralPath $diagLog.FullName -Encoding UTF8 | Select-Object -Last 25 | ForEach-Object { Write-Host ("      " + $_) }
            Write-Host '    --- 日志结束 ---'
        }
    }

    # ---- 6.5) 隔离根清理：复用共享删除模块（D3/D4 已验收边界）+ 进程退出/日志证据双门禁 ----
    #      Refused/Error ⇒ 保留目录、本轮 FAIL、写入 CSV cleanup_status，绝不静默吞掉。
    $procExitedOk = $true
    if ($p1 -and -not $p1.HasExited) { $procExitedOk = $false }
    if ($residual -gt 0) { $procExitedOk = $false }
    $cleanupStatus = Invoke-ASUI3GuardedCleanup -Target $dataRoot -ProtectedRoots $protectedRoots -ProcessExited $procExitedOk -LogEvidenceOk ($logPath -ne '')
    if ($cleanupStatus -ne 'Deleted' -and $cleanupStatus -ne 'NotFound') {
        if ($roundResult -eq 'PASS') { $roundResult = 'FAIL'; $fail++ }
        $problems += ('隔离根清理未确认 status=' + $cleanupStatus + '（目录保留）')
    }

    if ($roundResult -eq 'PASS') { $pass++ }

    $row = '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},"{17}",{18}' -f `
        $i, $primaryPid, $handle, $tWindow, $secondaryPid, $tSecondary, $secondaryExitCode, `
        $activated, $windowCloseOk, $forwardOk, $trayOk, $tTray, $primaryExited, $residual, $formalUnchanged, $logTraySeq, $cleanupStatus, $logPath, $roundResult
    Add-Content -LiteralPath $ReportPath -Value $row -Encoding UTF8
    Write-Host ("第 {0}/{1} 轮: {2}  PID={3}  tWindow={4}ms  tSecondary={5}ms  activated={6}  winClose={7}  traySeq={8}  tTray={9}ms  residual={10}  formalUnchanged={11}  cleanup={12}" -f `
        $i, $Rounds, $roundResult, $primaryPid, $tWindow, $tSecondary, $activated, $windowCloseOk, $logTraySeq, $tTray, $residual, $formalUnchanged, $cleanupStatus)
    foreach ($pb in $problems) { Write-Host ("    ! " + $pb) }
}

$env:AUTOSHUTDOWN_DATA_ROOT = $envBackup
Write-Host ("==== 完成: 通过 {0} / 失败 {1} / 共 {2} ====" -f $pass, $fail, $Rounds)
Write-Host ("报告: " + $ReportPath)
if ($fail -gt 0) { exit 1 }
exit 0
