#Requires -Version 5.1
# tools/test/Invoke-SStartupD1TrayExit.ps1
# S-STARTUP-D1 托盘退出触发器：对指定 PID 的应用，通过系统通知区域托盘图标的
# 右键上下文菜单「退出程序」正常退出。绝不强制结束进程、绝不按进程名清理。
#
# 实现（S-STARTUP-D1-D2 加固）：优先用 Win11 托盘溢出区窗口 scoped UIA 检索
# （TopLevelWindowForOverflowXamlIsland，先 ESC 归一化前台、点 chevron「显示隐藏的图标」
# 展开）——桌面全量 FindAll 数秒~数十秒，scoped 仅数毫秒；若溢出区未找到，回退桌面级
# 精确 Name 检索（图标直接显示在任务栏时的场景）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/Invoke-SStartupD1TrayExit.ps1 `
#       [-TargetPid <pid>] [-TimeoutSeconds 20]
# 退出：0 = 已退出且无残留；2 = 未定位托盘图标；3 = 未找到「退出程序」菜单项；
#       4 = 超时未退出。
param(
    [int]$TargetPid = 0,
    [int]$TimeoutSeconds = 20
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Tray32 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint wpid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
"@

function Send-Escape {
    try { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') | Out-Null } catch { }
    Start-Sleep -Milliseconds 300
}
function Click-Right([int]$x, [int]$y) {
    [Tray32]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 80
    [Tray32]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)  # RIGHTDOWN
    Start-Sleep -Milliseconds 40
    [Tray32]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)  # RIGHTUP
}
function Find-Exact([string]$Name) {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
# Win11 系统托盘溢出区是 explorer 的持久顶层窗口，类名 TopLevelWindowForOverflowXamlIsland，
# 用 chevron 切换可见性。scoped 到该窗口搜索才快（桌面全量 FindAll 数秒~数十秒）。
function Get-FlyoutHwnd {
    $exp = Get-Process -Name explorer -ErrorAction SilentlyContinue | Select-Object -First 1
    $script:FlyoutHwnd = [IntPtr]::Zero
    $script:FlyoutVis = $false
    if (-not $exp) { return }
    [Tray32]::EnumWindows({
        param($hWnd, $lParam)
        [uint32]$wpid = 0
        [Tray32]::GetWindowThreadProcessId($hWnd, [ref]$wpid) | Out-Null
        if ($wpid -eq $exp.Id) {
            $class = New-Object System.Text.StringBuilder 256
            [Tray32]::GetClassName($hWnd, $class, 256) | Out-Null
            if ($class.ToString() -eq 'TopLevelWindowForOverflowXamlIsland') {
                $script:FlyoutHwnd = $hWnd
                $script:FlyoutVis = [Tray32]::IsWindowVisible($hWnd)
                return $false
            }
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
}
function Find-TrayIconInFlyout {
    try {
        $flyEl = [System.Windows.Automation.AutomationElement]::FromHandle($script:FlyoutHwnd)
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '电脑自动关机助手')
        foreach ($e in $flyEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            $rr = $e.Current.BoundingRectangle
            if ($rr.Width -gt 0 -and $rr.Width -lt 100 -and $rr.Height -lt 100 -and $e.Current.ClassName -eq 'SystemTray.NormalButton') {
                return $e
            }
        }
    } catch { }
    return $null
}
# 右键托盘图标并点击「退出程序」；返回 $true/$false（菜单未找到）。
function Open-TrayMenuAndExit($icon) {
    $r = $icon.Current.BoundingRectangle
    if ($r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) { return $false }
    $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
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
    Write-Host '已调用「退出程序」。'
    return $true
}
# 主例程：返回 0=已点击退出程序；2=未定位托盘图标；3=菜单未找到。
function Invoke-TrayExit {
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
        if ($chev) {
            try { $chev.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() | Out-Null } catch { }
            Start-Sleep -Milliseconds 1500
            $null = Get-FlyoutHwnd
        }
    }
    if ($script:FlyoutHwnd -ne [IntPtr]::Zero -and $script:FlyoutVis) {
        $icon = Find-TrayIconInFlyout
        if ($icon) {
            if (Open-TrayMenuAndExit $icon) { return 0 }
            return 3
        }
    }
    # 回退：图标直接显示在任务栏（不在溢出区）时桌面级精确检索
    $icon2 = Find-Exact '电脑自动关机助手'
    if ($icon2) {
        if (Open-TrayMenuAndExit $icon2) { return 0 }
        return 3
    }
    return 2
}

if ($TargetPid -le 0) {
    $candidates = @(Get-Process -Name 'AutoShutdown*' -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 0) { Write-Host '未发现 AutoShutdown 进程。'; exit 0 }
    $p = $candidates | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { $p = $candidates[0] }
    $TargetPid = $p.Id
}
$proc = Get-Process -Id $TargetPid -ErrorAction Stop
Write-Host ("目标 PID: {0}  名称: {1}" -f $proc.Id, $proc.ProcessName)

$result = Invoke-TrayExit
if ($result -eq 2) { Write-Error '未能定位托盘图标（可能在折叠区域或图标已隐藏）。'; exit 2 }
if ($result -eq 3) { Write-Error '未能找到「退出程序」菜单项。'; exit 3 }

# 有界等待进程退出
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$exited = $false
while ((Get-Date) -lt $deadline) {
    $proc.Refresh()
    if ($proc.HasExited) { $exited = $true; break }
    Start-Sleep -Milliseconds 200
}
if ($exited) {
    Write-Host ("进程已退出。ExitCode={0}" -f $proc.ExitCode)
} else {
    Write-Host '进程在超时时间内未退出。'
    exit 4
}
# 目标 PID 的进程表条目在退出后可能瞬时残留（HasExited 已触发但条目尚未回收），
# 不视为残留；统计其余 AutoShutdown 进程（若另有 PID 则视为真实残留）。
$residual = @(Get-Process -Name 'AutoShutdown*' -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -ne $TargetPid })
Write-Host ("残留 AutoShutdown 进程数(不含目标PID): {0}" -f $residual.Count)
exit 0
