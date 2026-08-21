# S-STARTUP-D1 托盘退出触发器：对指定 PID 的应用，通过系统通知区域托盘图标的
# 右键上下文菜单「退出程序」正常退出。绝不 taskkill、不按进程名杀进程。
param(
    [int]$TargetPid = 0,
    [int]$TimeoutSeconds = 20
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Mouse32 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}
"@

function Send-RightClick([int]$x, [int]$y) {
    [Mouse32]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 100
    [Mouse32]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)  # RIGHTDOWN
    Start-Sleep -Milliseconds 50
    [Mouse32]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)  # RIGHTUP
}

function Find-ByName([string]$Name) {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ByNameContains([string]$Fragment) {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    foreach ($e in $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -and $e.Current.Name.IndexOf($Fragment, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $e
        }
    }
    return $null
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

# 1) 在系统托盘查找图标（可能被折叠 → 先展开溢出区）
$btn = Find-ByName '电脑自动关机助手'
if (-not $btn) {
    $chevron = Find-ByNameContains '隐藏' -or $null
    if (-not $chevron) { $chevron = Find-ByNameContains 'chevron' }
    if ($chevron) {
        $r = $chevron.Current.BoundingRectangle
        $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
        [Mouse32]::SetCursorPos($cx, $cy) | Out-Null
        Start-Sleep -Milliseconds 100
        [Mouse32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [Mouse32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 500
        $btn = Find-ByName '电脑自动关机助手'
    }
}
if (-not $btn) { Write-Error '未能定位托盘图标（可能在折叠区域或图标已隐藏）。'; exit 2 }
Write-Host ('托盘图标: ' + $btn.Current.Name)

# 2) 右键打开上下文菜单
$r = $btn.Current.BoundingRectangle
$cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
Send-RightClick $cx $cy

# 3) 等待「退出程序」菜单项并调用
$menuItem = $null
$deadline = (Get-Date).AddSeconds(8)
while ((Get-Date) -lt $deadline -and -not $menuItem) {
    $menuItem = Find-ByName '退出程序'
    if (-not $menuItem) { Start-Sleep -Milliseconds 200 }
}
if (-not $menuItem) { Write-Error '未能找到「退出程序」菜单项。'; exit 3 }
$invoke = $menuItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$invoke.Invoke()
Write-Host '已调用「退出程序」。'

# 4) 等待进程退出
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
$residual = @(Get-Process -Name 'AutoShutdown*' -ErrorAction SilentlyContinue)
Write-Host ("残留 AutoShutdown 进程数: {0}" -f $residual.Count)
exit 0
