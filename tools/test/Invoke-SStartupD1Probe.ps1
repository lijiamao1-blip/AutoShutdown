# S-STARTUP-D1 真机探针：启动候选 → 确认主窗口 → 枚举进程窗口 → 尝试托盘退出。
# 只读/隔离：全新临时数据根，不触碰正式数据目录，不 taskkill。
param(
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $Exe) { $Exe = Join-Path $root 'src\AutoShutdown.App\bin\Release\net8.0-windows\win-x64\AutoShutdown.App.exe' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "候选 EXE 不存在：$Exe" }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function New-IsolatedRoot {
    $d = Join-Path ([IO.Path]::GetTempPath()) ("as-d1-probe-" + [Guid]::NewGuid().ToString('N'))
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

function Find-TrayButton([string]$Name) {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Send-RightClick([int]$x, [int]$y) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Mouse32 {
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}
"@
    [Mouse32]::SetCursorPos($x, $y) | Out-Null
    [Mouse32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)  # LEFTDOWN
    [Mouse32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)  # LEFTUP
}

$dataRoot = New-IsolatedRoot
Write-Host "隔离数据根: $dataRoot"
$old = $env:AUTOSHUTDOWN_DATA_ROOT
$env:AUTOSHUTDOWN_DATA_ROOT = $dataRoot
try {
    $p = Start-Process -FilePath $Exe -PassThru
    Write-Host "PID=$($p.Id)"
    $deadline = (Get-Date).AddSeconds(30)
    $hwnd = 0
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { break }
        if ($p.MainWindowHandle -ne 0) { $hwnd = $p.MainWindowHandle; break }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "MainWindowHandle=$hwnd  HasExited=$($p.HasExited)"
    if ($hwnd -eq 0 -and -not $p.HasExited) { throw '主窗口未出现。' }
    if ($p.HasExited) { throw "进程提前退出，ExitCode=$($p.ExitCode)" }

    # 枚举进程窗口
    Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class EnumWin {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
}
"@
    Write-Host '--- 进程窗口 ---'
    [EnumWin]::EnumWindows({
        param($hWnd, $lParam)
        [uint32]$pid = 0
        [EnumWin]::GetWindowThreadProcessId($hWnd, [ref]$pid) | Out-Null
        if ($pid -eq $p.Id) {
            $title = New-Object System.Text.StringBuilder 256
            $class = New-Object System.Text.StringBuilder 256
            [EnumWin]::GetWindowText($hWnd, $title, 256) | Out-Null
            [EnumWin]::GetClassName($hWnd, $class, 256) | Out-Null
            $vis = [EnumWin]::IsWindowVisible($hWnd)
            $ex = [EnumWin]::GetWindowLong($hWnd, -20)
            Write-Host ("hWnd=0x{0:X} class={1} title={2} vis={3} exstyle=0x{4:X}" -f $hWnd, $class, $title, $vis, $ex)
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null

    # 尝试 UIA 找托盘按钮
    $btn = Find-TrayButton '电脑自动关机助手'
    if ($btn) {
        Write-Host ('UIA 托盘按钮: ' + $btn.Current.Name + ' rect=' + $btn.Current.BoundingRectangle)
        $r = $btn.Current.BoundingRectangle
        Send-RightClick ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 800
        # 找“退出程序”菜单项
        $menuItem = $null
        $menuCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '退出程序')
        $menuItem = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $menuCond)
        Write-Host ('菜单项存在: ' + ($null -ne $menuItem))
        if ($menuItem) {
            $invoke = $menuItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $invoke.Invoke()
        }
    } else {
        Write-Host 'UIA 未找到托盘按钮（可能需展开溢出区域）。'
    }

    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { break }
        Start-Sleep -Milliseconds 200
    }
    Write-Host ("托盘退出后 HasExited={0} ExitCode={1}" -f $p.HasExited, $(if ($p.HasExited) { $p.ExitCode } else { 'N/A' }))

    # 残留检查
    $residual = @(Get-Process -Name 'AutoShutdown.App' -ErrorAction SilentlyContinue)
    Write-Host ("残留 AutoShutdown.App 进程数: {0}" -f $residual.Count)
    if ($residual.Count -gt 0) {
        Write-Host '残留进程将等待其自然退出（不 taskkill）。'
    }

    # 日志：列出数据根下日志目录最后几行
    $logs = Get-ChildItem -Path (Join-Path $dataRoot 'logs') -Recurse -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 2
    foreach ($log in $logs) {
        Write-Host ("日志: " + $log.FullName)
        Get-Content -LiteralPath $log.FullName -Tail 25 | ForEach-Object { Write-Host "   $_" }
    }
} finally {
    $env:AUTOSHUTDOWN_DATA_ROOT = $old
    if ($p -and -not $p.HasExited) {
        Write-Host "探针实例未退出（PID $($p.Id)），等待 10 秒后仍存在则保留由用户决定。"
    }
}
