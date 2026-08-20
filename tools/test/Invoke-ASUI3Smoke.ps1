#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$EvidenceDirectory = ''
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
if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $root 'S-PKG-work包\S-UI3-验收证据'
}
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null

$pass = 0
$fail = 0
$script:window = $null
$script:process = $null

function Assert-True([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) {
        $script:pass++
        Write-Host ("PASS  {0}" -f $Name)
    } else {
        $script:fail++
        Write-Host ("FAIL  {0}  {1}" -f $Name, $Detail)
    }
}

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
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $count = 0
    foreach ($element in $Scope.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($element.Current.Name -eq $Name) { $count++ }
    }
    return $count
}

function Get-FormalDataSnapshot {
    $formalRoot = Join-Path $env:LOCALAPPDATA 'AutoShutdown'
    $sandbox = Join-Path $formalRoot 'UiTestSandbox'
    if (-not (Test-Path -LiteralPath $formalRoot)) { return @() }
    return @(Get-ChildItem -LiteralPath $formalRoot -File -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { -not $_.FullName.StartsWith($sandbox + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object FullName |
        ForEach-Object {
            '{0}|{1}|{2}' -f $_.FullName, $_.Length, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        })
}

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

function Close-WindowGracefully {
    if (-not $script:window) { return }
    try {
        $pattern = $script:window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        $pattern.Close()
    } catch {
        Write-Host 'WARN  无法通过窗口关闭模式关闭应用；未结束进程。'
    }
}

try {
    $before = Get-FormalDataSnapshot
    $existing = @(Get-Process -Name 'AutoShutdown*' -ErrorAction SilentlyContinue)
    Assert-True '启动前不存在 AutoShutdown 实例' ($existing.Count -eq 0) ("count={0}" -f $existing.Count)
    if ($existing.Count -ne 0) { exit 1 }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $launcher
    $launcherExit = $LASTEXITCODE
    Assert-True '一键入口退出码为 0' ($launcherExit -eq 0) ("exit={0}" -f $launcherExit)
    if ($launcherExit -ne 0) { exit 1 }

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not $script:process) {
        $candidate = Get-Process -Name 'AutoShutdown.App' -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($candidate) { $script:process = $candidate }
        else { Start-Sleep -Milliseconds 300 }
    }
    Assert-True '主窗口成功出现' ($null -ne $script:process)
    if (-not $script:process) { exit 1 }
    $script:window = [System.Windows.Automation.AutomationElement]::FromHandle($script:process.MainWindowHandle)

    $banner = Wait-Like $script:window '安全测试模式——不会执行真实系统电源操作' 20
    Assert-True '安全测试模式横幅可见' ($null -ne $banner -and -not $banner.Current.IsOffscreen)

    # 第二次调用入口必须拒绝，且现有 GUI 仍存活；启动器不结束或转发到现有实例。
    $samePid = $script:process.Id
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $launcher
    $secondExit = $LASTEXITCODE
    $stillAlive = Get-Process -Id $samePid -ErrorAction SilentlyContinue
    Assert-True '已有实例时入口拒绝启动' ($secondExit -ne 0) ("exit={0}" -f $secondExit)
    Assert-True '拒绝启动未结束已有实例' ($null -ne $stillAlive)

    $create1 = Wait-Like $script:window '创建任务' 15 ([System.Windows.Automation.ControlType]::Button)
    Assert-True '创建第一条模拟任务' (Invoke-Element $create1)
    Start-Sleep -Milliseconds 1200

    $weekly = Wait-Like $script:window '每周指定星期' 10 ([System.Windows.Automation.ControlType]::RadioButton)
    Assert-True '切换每周模式以创建不同任务' (Invoke-Element $weekly)
    Start-Sleep -Milliseconds 600
    $create2 = Wait-Like $script:window '创建任务' 10 ([System.Windows.Automation.ControlType]::Button)
    Assert-True '创建第二条模拟任务' (Invoke-Element $create2)

    $twoTasks = Wait-Like $script:window '共 2 个任务' 15
    Assert-True '首页显示两条任务' ($null -ne $twoTasks)

    $tasksNav = Wait-Like $script:window 'PageKey = tasks' 10 ([System.Windows.Automation.ControlType]::ListItem)
    if (-not $tasksNav) {
        $tasksNav = Wait-Like $script:window '任务管理' 5 ([System.Windows.Automation.ControlType]::ListItem)
    }
    Assert-True '进入任务管理页' (Invoke-Element $tasksNav)
    Start-Sleep -Milliseconds 1000
    $stopButtons = Count-Buttons $script:window '停止'
    Assert-True '任务管理显示两条任务' ($stopButtons -eq 2) ("停止按钮数={0}" -f $stopButtons)

    $shot = Join-Path $EvidenceDirectory 'S-UI3-uia-two-tasks.png'
    Assert-True '保存 UIA 截图证据' (Save-WindowScreenshot $script:window $shot) $shot

    Close-WindowGracefully
    $script:process.WaitForExit(15000) | Out-Null
    Assert-True '通过窗口正常关闭应用' $script:process.HasExited

    $after = Get-FormalDataSnapshot
    $formalUnchanged = (($before -join "`n") -eq ($after -join "`n"))
    Assert-True '正式数据目录未改变（排除独立 UiTestSandbox）' $formalUnchanged

    $config = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'AutoShutdown\UiTestSandbox\config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True '沙箱保持 TestMode=true' ($config.TestMode -eq $true)
    Assert-True '沙箱未允许真实电源' ($config.RealPowerEnabled -ne $true)
    Assert-True '沙箱 RunCommands 为空' (@($config.RunCommands.Commands).Count -eq 0)
    Assert-True '沙箱 CloseApps 目标为空' (@($config.CloseApps.Targets).Count -eq 0)
} finally {
    if ($script:process -and -not $script:process.HasExited) { Close-WindowGracefully }
}

Write-Host ("S-UI3 UIA SMOKE: pass={0} fail={1} skip=0" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
exit 0
