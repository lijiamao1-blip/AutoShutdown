#Requires -Version 5.1
# tools/test/Invoke-ASCloseUI1Smoke.ps1
# S-CLOSEUI1「从运行中的进程选择关闭目标」UI 冒烟。
#
# 运行模式：与 S-UI2 一致的隔离数据根（AUTOSHUTDOWN_DATA_ROOT=随机临时目录）+ 配置 TestMode 双闸门
#   （TestMode=true / RealPowerEnabled=false → GuardedPowerService 恒走 FakePowerService，绝无真实电源）。
#   不设 AUTOSHUTDOWN_UI_TEST（该严格模式要求固定 UiTestSandbox 且 CloseApps 目标必须为空，
#   与本阶段「预置失效路径目标」冲突）。沙箱内数据与本机真实用户数据完全隔离。
#
# 验收清单：
#   A1  隔离沙箱启动应用；全程 TestMode 双闸门（config.TestMode=true、RealPowerEnabled=false）；应用存活
#   A2  软件设置页「关闭应用（关机前）」卡 + 「从运行中的进程选择」按钮可见
#   A3  进程选择窗口打开（标题正确）；8 列表头可见（选择/进程名/PID/可执行文件完整路径/窗口标题/
#       产品名称/公司名称/状态/不可选原因）；底部只读声明可见；状态「共 N 个进程」加载完成
#   A4  搜索可用：输入进程名过滤，清空恢复；刷新可用
#   A5  不可选行（AutoShutdown 自身进程）标灰且复选框禁用；可选行（winver/charmap）可勾选
#   A6  勾选两个不同路径的普通程序（winver + charmap）→ 确定 → 回到设置页，两个目标入列表（标识为文件名）；
#       默认不强杀（未授权）
#   A7  取消不改动：勾选 mstsc 后取消 → 目标列表不变
#   A8  保存关闭应用设置 → config.json 写入 2 个目标，无 PID、无强杀授权；TestMode=true
#   A9  重开选择窗：已添加目标行显示「已添加」且不可勾选；取消返回
#   B1  重载：停应用 → config.json 追加「原程序路径已失效」目标 → 重启 → 失效行显示
#       「原程序路径已失效，请重新选择运行中的程序」+「重新选择」按钮（其它正常目标不受影响）
#   B2  重新选择 → 选 mstsc → 确定 → 失效行被替换，失效提示消失
#   C1  枚举到的进程绝不被关闭/终止：winver/charmap/mstsc 全程存活（按启动时精确 PID 校验）
#   C2  无真实电源操作：全程 TestMode 双闸门；应用存活
#   D   截图证据：picker / search / settings-targets / invalid-hint / reselect / already-added
#
# 约束：
#   - 冒烟脚本自己启动测试程序（winver/charmap/mstsc），应用本身绝不启动任何进程。
#   - 出错即回收：只按启动时记录的精确 PID 回收，绝不按进程名杀，绝不误杀生产实例。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/Invoke-ASCloseUI1Smoke.ps1 `
#       [-ReleaseExe <path>] [-EvidenceDir <dir>]
# 退出：0 = 全部通过（或仅显式 SKIP）；1 = 有失败。

[CmdletBinding()]
param(
    [string]$ReleaseExe = '',
    [string]$EvidenceDir = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo root

if (-not $ReleaseExe) {
    $candidates = @(Get-ChildItem -LiteralPath (Join-Path $root '.build-tmp') -Directory -Filter 'S-CLOSEUI1-*' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($candidates.Count -gt 0) {
        $exe = Join-Path $candidates[0].FullName 'AutoShutdown.App.exe'
        if (Test-Path -LiteralPath $exe) { $ReleaseExe = $exe }
    }
}
if (-not $ReleaseExe -or -not (Test-Path -LiteralPath $ReleaseExe)) {
    Write-Host 'FAIL  S-CLOSEUI1 候选 EXE 未找到（需先构建到 .build-tmp/S-CLOSEUI1-* 或传 -ReleaseExe）。'
    exit 1
}

# 精确 PID 回收（只回收本次冒烟启动的进程，绝不按进程名杀、绝不误杀生产实例）。
$script:launchedPids = New-Object System.Collections.Generic.List[int]
function Add-LaunchedProc($proc) {
    if ($proc -and -not $proc.HasExited) {
        try { $script:launchedPids.Add([int]$proc.Id) } catch { }
    }
}
function Stop-LaunchedProcs() {
    foreach ($launchedPid in $script:launchedPids) {
        try {
            $lp = Get-Process -Id $launchedPid -ErrorAction SilentlyContinue
            if ($lp) { Stop-Process -Id $launchedPid -Force -ErrorAction SilentlyContinue }
        } catch { }
    }
}
$script:testProc = $null   # 主应用进程（冒烟启动，精确回收）
trap {
    Stop-LaunchedProcs
    if ($script:testProc -and -not $script:testProc.HasExited) {
        try { Stop-Process -Id $script:testProc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Remove-Item Env:AUTOSHUTDOWN_DATA_ROOT -ErrorAction SilentlyContinue
    Write-Host ("TRAP {0} @ {1}: {2}" -f $_.Exception.GetType().Name, $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message)
    throw
}

$pass = 0; $fail = 0; $skip = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}
function Note-Skip([string]$Name, [string]$Why) {
    $script:skip++; Write-Host ("  SKIP  {0}  ({1})" -f $Name, $Why)
}

# ---------- UIA 助手 ----------
function Find-Descendant($win, [string]$name) {
    if (-not $win) { return $null }
    try {
        $c = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    } catch { return $null }
}
function Find-DescendantLike($win, [string]$substring, [string]$controlType = '') {
    if (-not $win) { return $null }
    try {
        $cond = [System.Windows.Automation.Condition]::TrueCondition
        if ($controlType) {
            $ct = Get-ControlType $controlType
            $cond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
        }
        foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            $n = $el.Current.Name
            if ($n -and $n.IndexOf($substring, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return $el }
        }
        return $null
    } catch { return $null }
}
function Get-ControlType([string]$controlType) {
    switch ($controlType) {
        'Button'      { return [System.Windows.Automation.ControlType]::Button }
        'CheckBox'    { return [System.Windows.Automation.ControlType]::CheckBox }
        'RadioButton' { return [System.Windows.Automation.ControlType]::RadioButton }
        'ListItem'    { return [System.Windows.Automation.ControlType]::ListItem }
        'HeaderItem'  { return [System.Windows.Automation.ControlType]::HeaderItem }
        'DataItem'    { return [System.Windows.Automation.ControlType]::DataItem }
        'Edit'        { return [System.Windows.Automation.ControlType]::Edit }
        'Window'      { return [System.Windows.Automation.ControlType]::Window }
        'Text'        { return [System.Windows.Automation.ControlType]::Text }
        default       { return $null }
    }
}
function Wait-Like($win, [string]$substring, [int]$timeoutSeconds = 12, [string]$controlType = '') {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $el = Find-DescendantLike $win $substring $controlType
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Wait-UiElementLike($win, [string]$needle, [int]$timeoutSeconds = 12, [string]$controlType = '') {
    return Wait-Like $win $needle $timeoutSeconds $controlType
}
function Invoke-Click($el) {
    if (-not $el) { return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return $true } catch { }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
    try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true } catch { }
    return $false
}
function Get-ToggleState($el) {
    if (-not $el) { return $null }
    try {
        $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        return $tp.Current.ToggleState
    } catch { return $null }
}
function Get-NavListItem($win, [string]$needle) {
    $liCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    foreach ($i in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)) {
        if ($i.Current.Name -like "*$needle*") { return $i }
    }
    return $null
}
function Select-NavPage($win, [string]$needle, [string]$waitFor) {
    $item = Get-NavListItem $win $needle
    if (-not $item) { return $false }
    try { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { return $false }
    if (-not $waitFor) { return $true }
    return ($null -ne (Wait-Like $win $waitFor 8))
}
function Set-EditValue($edit, [string]$value) {
    if (-not $edit) { return $false }
    try { $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value); return $true } catch { return $false }
}
function Count-ControlType($win, [string]$controlType) {
    if (-not $win) { return 0 }
    $ct = Get-ControlType $controlType
    if (-not $ct) { return 0 }
    $count = 0
    try {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
        foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) { $count++ }
    } catch { }
    return $count
}
function Find-HeaderItem($win, [string]$headerText) {
    if (-not $win) { return $null }
    try {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::HeaderItem)
        foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            if ($el.Current.Name -eq $headerText) { return $el }
        }
    } catch { }
    return $null
}
function Find-RowWithCell($win, [string]$cellText) {
    # 在 DataGrid 行（ControlType.DataItem）内查找精确单元格文本（用于定位具体进程行）。
    if (-not $win) { return $null }
    try {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
        foreach ($item in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            $tc = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $cellText)
            $match = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $tc)
            if ($match) { return $item }
        }
    } catch { }
    return $null
}
function Get-CheckBoxInRow($row) {
    if (-not $row) { return $null }
    try {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
        return $row.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    } catch { return $null }
}
function Find-RowBySearch($picker, [string]$searchNeedle, [string]$cellText, [int]$timeoutSeconds = 10) {
    # DataGrid 行虚拟化：未过滤时仅实例化视口内行，无法直接按内容定位任意行。
    # 通过搜索框把列表过滤到目标进程（或少量候选），再按精确单元格文本定位行。
    $searchBox = Find-Descendant $picker '搜索进程'
    if (-not $searchBox) { return $null }
    if (-not (Set-EditValue $searchBox $searchNeedle)) { return $null }
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $row = Find-RowWithCell $picker $cellText
        if ($row) { return $row }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Row-HasCellLike($row, [string]$needle) {
    if (-not $row) { return $false }
    try {
        foreach ($el in $row.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
            $n = $el.Current.Name
            if ($n -and $n.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        }
    } catch { }
    return $false
}
function Get-PageScrollViewer($win) {
    $paneCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Pane)
    $best = $null
    try {
        foreach ($p in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $paneCond)) {
            $r = $p.Current.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -lt 400 -or $r.Height -lt 250) { continue }
            $sp = $null
            try { $sp = $p.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch { }
            if (-not $sp) { continue }
            if (-not $best -or ($r.Width * $r.Height) -gt ($best.Viewport.Width * $best.Viewport.Height)) {
                $scrollable = $false
                try { $scrollable = ($sp.Current.VerticalScrollPercent -ne [System.Windows.Automation.ScrollPattern]::NoScroll) } catch { }
                $best = @{ El = $p; Pattern = $sp; Viewport = $r; Scrollable = $scrollable }
            }
        }
    } catch { }
    return $best
}
function Assert-Reachable($win, $scroll, [string]$needle, [string]$label, [string]$controlType = '') {
    $el = Find-DescendantLike $win $needle $controlType
    if (-not $el) { Assert-True ("reachable: {0}" -f $label) $false 'not found'; return $null }
    if (-not $el.Current.IsOffscreen) { Assert-True ("reachable: {0}" -f $label) $true 'visible'; return $el }
    if (-not $scroll -or -not $scroll.Scrollable) { Assert-True ("reachable: {0}" -f $label) $false 'offscreen and page not scrollable'; return $null }
    $ok = $false; $where = 'never'; $return = $null
    foreach ($pct in @(25, 50, 75, 100)) {
        try { $scroll.Pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct) } catch { break }
        Start-Sleep -Milliseconds 250
        $e2 = Find-DescendantLike $win $needle $controlType
        if ($e2 -and -not $e2.Current.IsOffscreen) { $ok = $true; $where = "scroll-$pct"; $return = $e2; break }
    }
    try { $scroll.Pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0) } catch { }
    Assert-True ("reachable: {0}" -f $label) $ok $where
    return $return
}
Add-Type -Namespace ASCloseUI1 -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
[DllImport("user32.dll")]
public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")]
public static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);
public static void ClickAt(int x, int y) {
    SetCursorPos(x, y);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // LBUTTONDOWN
    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // LBUTTONUP
}
'@
function Bring-ToFront($win) {
    if (-not $win) { return }
    try {
        $hwnd = [IntPtr]$win.Current.NativeWindowHandle
        # HWND_TOPMOST = -1 置顶，随后 HWND_NOTOPMOST = -2 恢复层级，避免挡住后续截图。
        [ASCloseUI1.Win32]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null
        Start-Sleep -Milliseconds 200
        [ASCloseUI1.Win32]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010) | Out-Null
    } catch { }
}
function Invoke-RealClick($el) {
    # 真实鼠标点击（在元素屏幕坐标中心按下/抬起）。复选框必须用真实点击：只读 DataGrid 下
    # UIA TogglePattern 只切换视觉而不触发 CheckBox.Click（不写回 IsSelected），真实点击与
    # 用户操作等价，能触发 Click 写回。仅测试工具注入输入事件，应用本身仍绝不启动/关闭进程。
    if (-not $el) { return $false }
    try {
        $r = $el.Current.BoundingRectangle
        if ($r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) { return $false }
        $x = [int]($r.X + $r.Width / 2)
        $y = [int]($r.Y + $r.Height / 2)
        [ASCloseUI1.Win32]::ClickAt($x, $y)
        return $true
    } catch { return $false }
}
function Invoke-ClickCheckbox($picker, $el) {
    if (-not $el) { return $false }
    [void](Bring-ToFront $picker)
    Start-Sleep -Milliseconds 150
    return (Invoke-RealClick $el)
}
function Invoke-RealClickOn($hostEl, $el) {
    # 真实鼠标点击（不阻塞）：点「确定」会弹出模态确认摘要窗（其内 ShowDialog），此时若用
    # UIA InvokePattern.Invoke()，提供程序会同步等待 OnOkClick 返回，脚本会卡住直到摘要被关闭。
    # 真实点击只注入输入事件并立即返回，脚本可继续与摘要窗交互（返回修改/取消/确认添加）。
    if (-not $el) { return $false }
    [void](Bring-ToFront $hostEl)
    Start-Sleep -Milliseconds 150
    return (Invoke-RealClick $el)
}
function Find-DescendantExact($win, [string]$name, [string]$controlType = '') {
    # 精确匹配（区别于 Find-DescendantLike 的子串匹配）：用于「取消」按钮，避免被「全部取消」截胡。
    if (-not $win) { return $null }
    $cond = [System.Windows.Automation.Condition]::TrueCondition
    if ($controlType) {
        $ct = Get-ControlType $controlType
        if (-not $ct) { return $null }
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    }
    try {
        foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            if ($e.Current.Name -eq $name) { return $e }
        }
    } catch { }
    return $null
}
function Save-WindowScreenshot($win, [string]$path) {
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    try {
        $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        if ($wp.Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Minimized) {
            $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
            Start-Sleep -Milliseconds 400
        }
    } catch { }
    $rect = $win.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) {
        Write-Host ("WARN Save-WindowScreenshot：窗口矩形无效，跳过 {0}" -f (Split-Path -Leaf $path))
        return $false
    }
    $w = [Math]::Max(1, [int][Math]::Ceiling($rect.Width))
    $h = [Math]::Max(1, [int][Math]::Ceiling($rect.Height))
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return (Test-Path -LiteralPath $path)
}

# ---------- 测试程序（冒烟脚本自己启动；应用绝不启动任何进程） ----------
function Start-TestProgram([string]$exePath) {
    $proc = Start-Process -FilePath $exePath -PassThru
    Add-LaunchedProc $proc
    Start-Sleep -Milliseconds 600
    return $proc
}

# ---------- 应用启动/停止（隔离数据根 + TestMode 双闸门） ----------
function Start-SmokeApp([string]$dataRoot) {
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $env:AUTOSHUTDOWN_DATA_ROOT = $dataRoot
    $proc = Start-Process -FilePath $ReleaseExe -PassThru -WorkingDirectory (Split-Path -Parent $ReleaseExe)
    $script:testProc = $proc
    $win = $null
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { return @{ Proc = $proc; Win = $null; Reason = 'exited' } }
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
        $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $cond)
        if ($candidate -and $candidate.Current.Name -eq '电脑自动关机助手') { $win = $candidate; break }
        Start-Sleep -Milliseconds 400
    }
    if (-not $win) {
        $tc = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '电脑自动关机助手')
        $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $tc)
    }
    if (-not $win -or $proc.HasExited) {
        return @{ Proc = $proc; Win = $win; Reason = 'no window' }
    }
    return @{ Proc = $proc; Win = $win; Reason = 'ok' }
}
function Stop-SmokeApp($proc) {
    if ($proc -and (-not $proc.HasExited)) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        $w = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $w -and -not $proc.HasExited) { Start-Sleep -Milliseconds 200 }
    }
    Remove-Item Env:AUTOSHUTDOWN_DATA_ROOT -ErrorAction SilentlyContinue
}
function Init-SafeConfig($win) {
    # 全新沙箱：应用 fail-closed 显示「配置不可用」，需在首页点击「初始化安全配置」。
    Assert-True 'A1: fresh 配置不可用 header' ($null -ne (Find-Descendant $win '配置不可用'))
    $homeBtn = Find-DescendantLike $win '首页' 'Button'
    Assert-True 'A1: 首页 button(恢复导航) 存在' ($null -ne $homeBtn)
    if ($homeBtn) { [void](Invoke-Click $homeBtn); Start-Sleep -Milliseconds 600 }
    $initBtn = Wait-UiElementLike $win '初始化安全配置' 8 'Button'
    Assert-True 'A1: 初始化安全配置 button 存在' ($null -ne $initBtn)
    if (-not $initBtn) { return $false }
    [void](Invoke-Click $initBtn)
    $modeHeader = Wait-UiElementLike $win '安全测试模式' 12
    Assert-True 'A1: init 后 安全测试模式 header' ($null -ne $modeHeader)
    return ($null -ne $modeHeader)
}
function Find-PickerWindow($ownerWin, [int]$timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $tc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '从运行中的进程选择关闭目标')
    while ((Get-Date) -lt $deadline) {
        # 选择窗是主窗口的 owned 窗口（Owner=MainWindow）：UIA 把它暴露为主窗口的直接子元素，
        # 而不是桌面根下的顶层子元素。用 Children 浅层搜索（避免整树遍历），并容错 UIA 超时
        # （窗口刚打开、UI 线程忙于初始加载时，首次查询可能超时，重试即可）。
        try {
            $picker = $ownerWin.FindFirst([System.Windows.Automation.TreeScope]::Children, $tc)
            if ($picker) { return $picker }
        } catch { }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Open-ProcessPicker($win, [string]$scrollNeedle = '从运行中的进程选择') {
    $pageScroll = Get-PageScrollViewer $win
    $btn = Assert-Reachable $win $pageScroll $scrollNeedle ('按钮: {0}' -f $scrollNeedle) 'Button'
    if (-not $btn) { return $null }
    [void](Invoke-Click $btn)
    return (Find-PickerWindow $win 12)
}
function Close-PickerByCancel($picker) {
    # 精确匹配「取消」按钮（子串匹配会命中「全部取消」）。
    $cancelBtn = Find-DescendantExact $picker '取消' 'Button'
    if ($cancelBtn) { [void](Invoke-Click $cancelBtn); Start-Sleep -Milliseconds 600 }
}
function Get-CheckBoxToggled($el) {
    # 返回 $null（无法读取）或 [bool]（On=$true/Off=$false）。
    $ts = Get-ToggleState $el
    if ($null -eq $ts) { return $null }
    return ($ts -eq [System.Windows.Automation.ToggleState]::On)
}
function Find-TextByRegex($win, [string]$pattern) {
    # 按 UIA Name 正则匹配文本元素（用于「已选择 N 项」这类动态文本）。
    if (-not $win) { return $null }
    try {
        foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
            $n = $e.Current.Name
            if ($n -and $n -match $pattern) { return $e }
        }
    } catch { }
    return $null
}
function Get-CountSelected($picker) {
    $el = Find-TextByRegex $picker '^已选择 \d+ 项$'
    if (-not $el) { return $null }
    if ($el.Current.Name -match '^已选择 (\d+) 项$') { return [int]$Matches[1] }
    return $null
}
function Find-SummaryWindow($picker, $mainWin, [int]$timeoutSeconds = 12) {
    # 确认摘要窗是选择窗的 owned 窗口（Owner=ProcessPickerWindow），标题固定
    # 「确认添加运行中的程序目标」（重选模式仅按钮/正文文案变化）。依次在
    # 选择窗子级、主窗口子级、桌面根子级查找，容错 UIA 首次查询超时。
    $title = '确认添加运行中的程序目标'
    $tc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $title)
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($host_ in @($picker, $mainWin)) {
            if (-not $host_) { continue }
            foreach ($scope in @([System.Windows.Automation.TreeScope]::Children,
                                  [System.Windows.Automation.TreeScope]::Descendants)) {
                try {
                    $w = $host_.FindFirst($scope, $tc)
                    if ($w) { return $w }
                } catch { }
            }
        }
        try {
            $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Children, $tc)
            if ($w) { return $w }
        } catch { }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

Write-Host "== S-CLOSEUI1 UI smoke =="
Write-Host "EXE: $ReleaseExe"

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("as-closeui1-" + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $sandbox 'data'
$evidence = if ($EvidenceDir) { $EvidenceDir } else { Join-Path $root 'S-PKG-work包\S-CLOSEUI1-D1-截图证据' }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$winverExe  = Join-Path (${env:SystemRoot}) 'System32\winver.exe'
$charmapExe = Join-Path (${env:SystemRoot}) 'System32\charmap.exe'
$mstscExe   = Join-Path (${env:SystemRoot}) 'System32\mstsc.exe'
$invalidPath = 'C:\Windows\System32\as-closeui1-gone-missing.exe'

# ======================================================================
# 阶段 A：主流程（选择→添加→保存→重开）
# ======================================================================
Write-Host "`n== A: 启动 + 初始化安全配置 =="
$launch = Start-SmokeApp $dataRoot
Assert-True 'A1: 应用进程存活' (-not $launch.Proc.HasExited) $launch.Reason
Assert-True 'A1: 主窗口出现' ($null -ne $launch.Win) $launch.Reason
if (-not $launch.Win) { Stop-SmokeApp $launch.Proc; Stop-LaunchedProcs; exit 1 }
$win = $launch.Win

if (-not (Init-SafeConfig $win)) { Stop-SmokeApp $launch.Proc; Stop-LaunchedProcs; exit 1 }
$cfgPath = Join-Path $dataRoot 'config.json'
$initCfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-True 'A1: init 后 config.json 写入' ($null -ne $initCfg)
if ($initCfg) {
    Assert-True 'A1: config.TestMode=true（双闸门）' ($initCfg.TestMode -eq $true)
    Assert-True 'A1: config 未开启真实电源' ($initCfg.RealPowerEnabled -ne $true)
}

# 启动两个不同路径的普通测试程序（冒烟脚本启动；应用绝不启动任何进程）。
$procWinver  = Start-TestProgram $winverExe
$procCharmap = Start-TestProgram $charmapExe
Assert-True 'A5: 测试程序已启动(winver)' (-not $procWinver.HasExited)
Assert-True 'A5: 测试程序已启动(charmap)' (-not $procCharmap.HasExited)

# 导航到 软件设置
$okSettings = Select-NavPage $win 'PageKey = settings' '软件设置'
Assert-True 'A2: 导航到 软件设置' $okSettings
if (-not $okSettings) { Stop-SmokeApp $launch.Proc; Stop-LaunchedProcs; exit 1 }
$pageScroll = Get-PageScrollViewer $win
$pickerBtn = Assert-Reachable $win $pageScroll '从运行中的进程选择' '关闭应用卡: 从运行中的进程选择 按钮' 'Button'
Assert-True 'A2: 关闭应用卡标题可见' ($null -ne (Find-DescendantLike $win '关闭应用（关机前）'))

# ---- A3：打开进程选择窗口 ----
$picker = Open-ProcessPicker $win
Assert-True 'A3: 进程选择窗口打开（标题正确）' ($null -ne $picker)
if (-not $picker) { Stop-SmokeApp $launch.Proc; Stop-LaunchedProcs; exit 1 }
$readiness = Wait-Like $picker '个进程' 15
Assert-True 'A3: 窗口加载完成（状态含「共 N 个进程」）' ($null -ne $readiness)

foreach ($h in @('选择','进程名','PID','可执行文件完整路径','窗口标题','产品名称','公司名称','状态/不可选原因')) {
    Assert-True ("A3: 列头可见: {0}" -f $h) ($null -ne (Find-HeaderItem $picker $h))
}
Assert-True 'A3: 底部只读声明可见' ($null -ne (Find-DescendantLike $picker '这里只读取当前进程信息'))
# S-CLOSEUI1-D1：顶栏两个筛选开关默认开启；工具栏三按钮；已选择数量初始 0。
$filterWin = Find-Descendant $picker '仅显示有窗口'
$filterSel = Find-Descendant $picker '仅显示可选项'
Assert-True 'A3: 筛选「仅显示有窗口」默认开启' (($null -ne $filterWin) -and (Get-CheckBoxToggled $filterWin) -eq $true)
Assert-True 'A3: 筛选「仅显示可选项」默认开启' (($null -ne $filterSel) -and (Get-CheckBoxToggled $filterSel) -eq $true)
$countSel0 = Get-CountSelected $picker
Assert-True 'A3: 已选择数量可见(初始 0 项)' ($null -ne $countSel0 -and $countSel0 -eq 0) ("count={0}" -f $countSel0)
Assert-True 'A3: 工具栏「全选当前可用项」存在' ($null -ne (Find-DescendantExact $picker '全选当前可用项' 'Button'))
Assert-True 'A3: 工具栏「取消当前选择」存在' ($null -ne (Find-DescendantExact $picker '取消当前选择' 'Button'))
Assert-True 'A3: 工具栏「取消全部选择」存在' ($null -ne (Find-DescendantExact $picker '取消全部选择' 'Button'))
$shotPicker = Join-Path $evidence 'picker.png'
[void](Bring-ToFront $picker); Start-Sleep -Milliseconds 200
[void](Save-WindowScreenshot $picker $shotPicker)
Assert-True 'D: 截图 进程选择窗口 已保存' (Test-Path -LiteralPath $shotPicker)

# ---- A4：搜索 + 刷新 ----
$searchBox = Find-Descendant $picker '搜索进程'
Assert-True 'A4: 搜索框存在' ($null -ne $searchBox)
if ($searchBox) {
    Assert-True 'A4: 搜索「winver」过滤' (Set-EditValue $searchBox 'winver')
    Start-Sleep -Milliseconds 600
    $rowCount = Count-ControlType $picker 'DataItem'
    Assert-True 'A4: 搜索后仅剩 winver 行' ($rowCount -eq 1) ("DataItem 数={0}" -f $rowCount)
    Assert-True 'A4: charmap 行被过滤隐藏' ($null -eq (Find-RowWithCell $picker $charmapExe))
    $shotSearch = Join-Path $evidence 'search.png'
    [void](Bring-ToFront $picker); Start-Sleep -Milliseconds 150
    [void](Save-WindowScreenshot $picker $shotSearch)
    Assert-True 'D: 截图 搜索过滤 已保存' (Test-Path -LiteralPath $shotSearch)
    [void](Set-EditValue $searchBox '')
    Start-Sleep -Milliseconds 600
    # 清空后恢复全部行：行虚拟化下视口内实例化多行（>1 即可证明已恢复）。
    $rowCountFull = Count-ControlType $picker 'DataItem'
    Assert-True 'A4: 清空搜索恢复全部行(行数>1)' ($rowCountFull -gt 1) ("DataItem 数={0}" -f $rowCountFull)
}
$refreshBtn = Find-DescendantLike $picker '刷新' 'Button'
Assert-True 'A4: 刷新按钮可用' ($null -ne $refreshBtn -and $refreshBtn.Current.IsEnabled)
if ($refreshBtn) {
    Assert-True 'A4: 刷新可点击' (Invoke-Click $refreshBtn)
    Assert-True 'A4: 刷新后状态更新' ($null -ne (Wait-Like $picker '个进程' 12))
}

# ---- D1：工具栏交互（已选数量实时更新 / 全选当前可用项 / 取消当前选择 / 取消全部选择） ----
$toolAll = Find-DescendantExact $picker '全选当前可用项' 'Button'
$toolCur = Find-DescendantExact $picker '取消当前选择' 'Button'
$toolAllClear = Find-DescendantExact $picker '取消全部选择' 'Button'
if ($toolAll) {
    Assert-True 'D1: 全选当前可用项(可点击)' (Invoke-Click $toolAll)
    Start-Sleep -Milliseconds 800
    $countAll = Get-CountSelected $picker
    Assert-True 'D1: 全选后已选择数量>0' ($null -ne $countAll -and $countAll -gt 0) ("count={0}" -f $countAll)
}
if ($toolCur) {
    $beforeCur = Get-CountSelected $picker
    Assert-True 'D1: 取消当前选择(可点击)' (Invoke-Click $toolCur)
    Start-Sleep -Milliseconds 600
    $afterCur = Get-CountSelected $picker
    # 全选只勾可见+可选行，取消当前只清可见行；本环境无可隐藏的可选项，故归零或减少。
    Assert-True 'D1: 取消当前只清可见行(数量减少或归零)' ($null -ne $afterCur -and $afterCur -lt $beforeCur) ("{0}->{1}" -f $beforeCur, $afterCur)
}
if ($toolAllClear) {
    Assert-True 'D1: 取消全部选择(可点击)' (Invoke-Click $toolAllClear)
    Start-Sleep -Milliseconds 600
    $countClear = Get-CountSelected $picker
    Assert-True 'D1: 取消全部后已选择数量=0' ($null -ne $countClear -and $countClear -eq 0) ("count={0}" -f $countClear)
}

# ---- A5：不可选行（AutoShutdown 自身）标灰且复选框禁用；可选行可勾选 ----
# 默认「仅显示可选项」开启会隐藏所有不可选行（含自身进程）：先关闭该筛选才能看到自身进程行。
$filterSel = Find-Descendant $picker '仅显示可选项'
if ($filterSel -and (Get-CheckBoxToggled $filterSel)) { [void](Invoke-Click $filterSel); Start-Sleep -Milliseconds 500 }
# 通过搜索框定位目标行（DataGrid 行虚拟化：未过滤时仅实例化视口内行，无法直接按内容定位）。
$selfName = [IO.Path]::GetFileNameWithoutExtension($ReleaseExe)
$selfRow = Find-RowBySearch $picker $selfName $selfName
Assert-True 'A5: 找到 AutoShutdown 自身进程行' ($null -ne $selfRow)
if ($selfRow) {
    $selfCheck = Get-CheckBoxInRow $selfRow
    Assert-True 'A5: 自身进程复选框禁用（不可勾选）' ($null -ne $selfCheck -and -not $selfCheck.Current.IsEnabled)
    Assert-True 'A5: 自身进程行显示不可选原因' (Row-HasCellLike $selfRow '自身进程')
}
# 恢复「仅显示可选项」：后续搜索可选行不依赖该筛选。
if ($filterSel -and -not (Get-CheckBoxToggled $filterSel)) { [void](Invoke-Click $filterSel); Start-Sleep -Milliseconds 400 }
# 搜索过滤会虚拟化掉其它行：必须在当前搜索存活时立即定位并断言该行及其复选框，
# 再切换下一个搜索（避免对陈旧元素断言）。
$winverRow  = Find-RowBySearch $picker 'winver' $winverExe
Assert-True 'A5: winver 行可选' ($null -ne $winverRow)
$winverCheck  = Get-CheckBoxInRow $winverRow
Assert-True 'A5: winver 复选框可用' ($null -ne $winverCheck -and $winverCheck.Current.IsEnabled)
$charmapRow = Find-RowBySearch $picker 'charmap' $charmapExe
Assert-True 'A5: charmap 行可选' ($null -ne $charmapRow)
$charmapCheck = Get-CheckBoxInRow $charmapRow
Assert-True 'A5: charmap 复选框可用' ($null -ne $charmapCheck -and $charmapCheck.Current.IsEnabled)

# ---- A6：勾选 winver + charmap → 确定 → 确认摘要 → 确认添加 → 入列表 ----
# 搜索过滤会虚拟化掉其它行，勾选前必须重新定位当前行。
$wCheck2 = Get-CheckBoxInRow (Find-RowBySearch $picker 'winver' $winverExe)
Assert-True 'A6: 勾选 winver' ($null -ne $wCheck2 -and $wCheck2.Current.IsEnabled -and (Invoke-ClickCheckbox $picker $wCheck2))
$cCheck2 = Get-CheckBoxInRow (Find-RowBySearch $picker 'charmap' $charmapExe)
Assert-True 'A6: 勾选 charmap' ($null -ne $cCheck2 -and $cCheck2.Current.IsEnabled -and (Invoke-ClickCheckbox $picker $cCheck2))
$countSel2 = Get-CountSelected $picker
Assert-True 'A6: 已选择数量=2（实时更新）' ($null -ne $countSel2 -and $countSel2 -eq 2) ("count={0}" -f $countSel2)
$okBtn = Find-DescendantLike $picker '确定' 'Button'
Assert-True 'A6: 确定按钮存在' ($null -ne $okBtn)
if ($okBtn) { [void](Invoke-RealClickOn $picker $okBtn) }
# S-CLOSEUI1-D1：确定后不立即返回，先出现确认摘要窗口（程序名 / PID / 完整路径 / 去重数）。
$summary = Find-SummaryWindow $picker $win
Assert-True 'A6: 确认摘要窗口出现' ($null -ne $summary)
if ($summary) {
    Assert-True 'A6: 摘要只按完整路径匹配提示' ($null -ne (Find-DescendantLike $summary '只按以下完整路径匹配'))
    Assert-True 'A6: 摘要不按进程名自动兜底提示' ($null -ne (Find-DescendantLike $summary '不会按进程名自动兜底'))
    Assert-True 'A6: 摘要不强杀权限提示' ($null -ne (Find-DescendantLike $summary '不会自动授予强制结束权限'))
    Assert-True 'A6: 摘要显示去重后新增数量(2 个目标)' ($null -ne (Find-DescendantLike $summary '最终将新增 2 个目标'))
    Assert-True 'A6: 摘要显示当前 PID(仅本次展示)' ($null -ne (Find-DescendantLike $summary 'PID '))
    Assert-True 'A6: 摘要显示窗口标题(仅展示)' ($null -ne (Find-DescendantLike $summary '窗口标题：'))
    $shotSummary = Join-Path $evidence 'summary-add.png'
    [void](Bring-ToFront $summary); Start-Sleep -Milliseconds 150
    [void](Save-WindowScreenshot $summary $shotSummary)
    Assert-True 'D: 截图 确认添加摘要 已保存' (Test-Path -LiteralPath $shotSummary)
    $confirmAdd = Find-DescendantExact $summary '确认添加' 'Button'
    Assert-True 'A6: 摘要「确认添加」按钮' ($null -ne $confirmAdd)
    if ($confirmAdd) { [void](Invoke-RealClickOn $summary $confirmAdd) }
    Start-Sleep -Milliseconds 900
}
Assert-True 'A6: 确认添加后窗口关闭' ($null -eq (Find-PickerWindow $win 2))

$pageScroll2 = Get-PageScrollViewer $win
$added1 = Assert-Reachable $win $pageScroll2 'winver.exe' '目标: winver.exe 已入列表' 'CheckBox'
$added2 = Assert-Reachable $win $pageScroll2 'charmap.exe' '目标: charmap.exe 已入列表' 'CheckBox'
Assert-True 'A6: 两个目标已加入（标识为文件名）' ($null -ne $added1 -and $null -ne $added2)
if ($added1) { Assert-True 'A6: 默认不强杀(winver 未授权)' ((Get-ToggleState $added1) -eq [System.Windows.Automation.ToggleState]::Off) }
if ($added2) { Assert-True 'A6: 默认不强杀(charmap 未授权)' ((Get-ToggleState $added2) -eq [System.Windows.Automation.ToggleState]::Off) }

# ---- A7：取消不改动（勾选 mstsc 后取消） ----
$procMstsc = Start-TestProgram $mstscExe
Assert-True 'A7: 测试程序已启动(mstsc)' (-not $procMstsc.HasExited)
$picker2 = Open-ProcessPicker $win
Assert-True 'A7: 再次打开选择窗' ($null -ne $picker2)
if ($picker2) {
    $null = Wait-Like $picker2 '个进程' 12
    $refreshBtn3 = Find-DescendantLike $picker2 '刷新' 'Button'
    if ($refreshBtn3) { [void](Invoke-Click $refreshBtn3); Start-Sleep -Milliseconds 800 }
    $mstscRow = Find-RowBySearch $picker2 'mstsc' $mstscExe
    Assert-True 'A7: mstsc 行可选' ($null -ne $mstscRow)
    $mstscCheck = Get-CheckBoxInRow $mstscRow
    if ($mstscCheck -and $mstscCheck.Current.IsEnabled) {
        Assert-True 'A7: 勾选 mstsc（随后返回修改/取消）' (Invoke-ClickCheckbox $picker2 $mstscCheck)
    } else {
        Assert-True 'A7: 勾选 mstsc（随后返回修改/取消）' $false 'mstsc 复选框不可用'
    }
    # D1：确定 → 摘要 → 返回修改 → 选择窗保留，勾选保留。
    $okBtn2 = Find-DescendantLike $picker2 '确定' 'Button'
    if ($okBtn2) { [void](Invoke-RealClickOn $picker2 $okBtn2) }
    $summary2 = Find-SummaryWindow $picker2 $win
    Assert-True 'A7: 摘要窗口出现(返回修改流程)' ($null -ne $summary2)
    if ($summary2) {
        $backBtn = Find-DescendantExact $summary2 '返回修改' 'Button'
        Assert-True 'A7: 摘要「返回修改」按钮' ($null -ne $backBtn)
        if ($backBtn) { [void](Invoke-RealClickOn $summary2 $backBtn) }
        Start-Sleep -Milliseconds 700
        Assert-True 'A7: 返回修改后选择窗未关闭' ($null -ne (Find-PickerWindow $win 2))
        $mRow2 = Find-RowBySearch $picker2 'mstsc' $mstscExe
        $mCheck2 = Get-CheckBoxInRow $mRow2
        Assert-True 'A7: 返回修改后 mstsc 勾选保留' ($null -ne $mCheck2 -and (Get-ToggleState $mCheck2) -eq [System.Windows.Automation.ToggleState]::On)
    }
    # D1：再走取消流程：确定 → 摘要 → 取消 → 集合不变。
    $okBtn3 = Find-DescendantLike $picker2 '确定' 'Button'
    if ($okBtn3) { [void](Invoke-RealClickOn $picker2 $okBtn3) }
    $summary3 = Find-SummaryWindow $picker2 $win
    Assert-True 'A7: 摘要窗口出现(取消流程)' ($null -ne $summary3)
    if ($summary3) {
        $sumCancel = Find-DescendantExact $summary3 '取消' 'Button'
        Assert-True 'A7: 摘要「取消」按钮' ($null -ne $sumCancel)
        if ($sumCancel) { [void](Invoke-RealClickOn $summary3 $sumCancel) }
    }
    Start-Sleep -Milliseconds 800
    Assert-True 'A7: 摘要取消后窗口关闭' ($null -eq (Find-PickerWindow $win 2))
    $still2 = ($null -ne (Assert-Reachable $win (Get-PageScrollViewer $win) 'winver.exe' 'winver 仍在' 'CheckBox'))
    $noMstsc = $null -eq (Find-DescendantLike $win 'mstsc.exe')
    Assert-True 'A7: 取消后列表不变(winver 仍在)' $still2
    Assert-True 'A7: 取消后列表不变(mstsc 未加入)' $noMstsc
}

# ---- A8：保存关闭应用设置 → 配置写入 ----
$saveBtn = Assert-Reachable $win (Get-PageScrollViewer $win) '保存关闭应用设置' '保存按钮' 'Button'
Assert-True 'A8: 保存按钮可达' ($null -ne $saveBtn)
if ($saveBtn) { Assert-True 'A8: 保存可点击' (Invoke-Click $saveBtn) }
Start-Sleep -Milliseconds 1200
$cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-True 'A8: config.json 存在' ($null -ne $cfg)
if ($cfg) {
    $targets = @($cfg.CloseApps.Targets)
    Assert-True 'A8: 配置含 2 个目标' ($targets.Count -eq 2) ("count={0}" -f $targets.Count)
    $withPid = @($targets | Where-Object { $null -ne $_.ProcessId }).Count
    $withForce = @($targets | Where-Object { $_.ForceKillAllowed -eq $true }).Count
    Assert-True 'A8: 无目标持久化 PID' ($withPid -eq 0)
    Assert-True 'A8: 无目标被授予强杀' ($withForce -eq 0)
    $hasWinver = @($targets | Where-Object { $_.ExecutablePath -eq $winverExe }).Count -eq 1
    $hasCharmap = @($targets | Where-Object { $_.ExecutablePath -eq $charmapExe }).Count -eq 1
    Assert-True 'A8: 配置含 winver 目标' $hasWinver
    Assert-True 'A8: 配置含 charmap 目标' $hasCharmap
    Assert-True 'A8: config.TestMode=true' ($cfg.TestMode -eq $true)
}

# ---- A9：重开选择窗：已添加行显示「已添加」且不可勾选 ----
$picker3 = Open-ProcessPicker $win
Assert-True 'A9: 重开选择窗' ($null -ne $picker3)
if ($picker3) {
    $null = Wait-Like $picker3 '个进程' 12
    $refreshBtn4 = Find-DescendantLike $picker3 '刷新' 'Button'
    if ($refreshBtn4) { [void](Invoke-Click $refreshBtn4); Start-Sleep -Milliseconds 800 }
    # 默认「仅显示可选项」开启会隐藏已添加（不可选）行：先关闭该筛选才能看到「已添加」标灰。
    $filterSelA9 = Find-Descendant $picker3 '仅显示可选项'
    if ($filterSelA9 -and (Get-CheckBoxToggled $filterSelA9)) { [void](Invoke-Click $filterSelA9); Start-Sleep -Milliseconds 500 }
    $wRow = Find-RowBySearch $picker3 'winver' $winverExe
    Assert-True 'A9: winver 行显示「已添加」' (($null -ne $wRow) -and (Row-HasCellLike $wRow '已添加'))
    $wCheckA9 = Get-CheckBoxInRow $wRow
    Assert-True 'A9: winver 已添加行复选框禁用' ($null -ne $wCheckA9 -and -not $wCheckA9.Current.IsEnabled)
    # 截图：当前搜索过滤仅显示 winver 一行（「已添加」标灰可见）。
    $shotAlready = Join-Path $evidence 'picker-already-added.png'
    [void](Bring-ToFront $picker3); Start-Sleep -Milliseconds 150
    [void](Save-WindowScreenshot $picker3 $shotAlready)
    Assert-True 'D: 截图 已添加标灰 已保存' (Test-Path -LiteralPath $shotAlready)
    $cRow2 = Find-RowBySearch $picker3 'charmap' $charmapExe
    Assert-True 'A9: charmap 行显示「已添加」' (($null -ne $cRow2) -and (Row-HasCellLike $cRow2 '已添加'))
    $cCheckA9 = Get-CheckBoxInRow $cRow2
    Assert-True 'A9: charmap 已添加行复选框禁用' ($null -ne $cCheckA9 -and -not $cCheckA9.Current.IsEnabled)
    Close-PickerByCancel $picker3
    Start-Sleep -Milliseconds 700
    Assert-True 'A9: 取消关闭选择窗' ($null -eq (Find-PickerWindow $win 2))
}

# ======================================================================
# 阶段 B：升级场景「原程序路径已失效」→ 重新选择
# ======================================================================
Write-Host "`n== B: 失效路径 重新选择 =="
Stop-SmokeApp $launch.Proc

# 在应用自身写出的合法 config 上追加一个失效目标（应用升级后，已保存路径指向的程序已被卸载）。
$cfg2 = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
$list = @($cfg2.CloseApps.Targets)
$list += [pscustomobject]@{ ExecutablePath = $invalidPath; ForceKillAllowed = $false }
$cfg2.CloseApps.Targets = $list
$cfg2 | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $cfgPath -Encoding UTF8
Write-Host '  info 已向 config.json 追加失效目标（仅一条，其余为应用自身写入）'

$launch2 = Start-SmokeApp $dataRoot
Assert-True 'B1: 重载后应用进程存活' (-not $launch2.Proc.HasExited) $launch2.Reason
Assert-True 'B1: 重载后主窗口出现' ($null -ne $launch2.Win) $launch2.Reason
if (-not $launch2.Win) { Stop-SmokeApp $launch2.Proc; Stop-LaunchedProcs; exit 1 }
$win = $launch2.Win
Assert-True 'B1: 重载后仍为安全测试模式' ($null -ne (Wait-Like $win '安全测试模式' 15))
$okSettings2 = Select-NavPage $win 'PageKey = settings' '软件设置'
Assert-True 'B1: 重载后导航到 软件设置' $okSettings2
if (-not $okSettings2) { Stop-SmokeApp $launch2.Proc; Stop-LaunchedProcs; exit 1 }

# 失效行提示 + 重新选择按钮；正常目标仍在。
$pageScrollB = Get-PageScrollViewer $win
$invalidHint = Assert-Reachable $win $pageScrollB '原程序路径已失效' '失效路径提示: 原程序路径已失效' 'Text'
Assert-True 'B1: 失效路径提示可见' ($null -ne $invalidHint)
Assert-True 'B1: 正常目标 winver 仍在' ($null -ne (Assert-Reachable $win $pageScrollB 'winver.exe' '目标: winver.exe 仍在' 'CheckBox'))
Assert-True 'B1: 「重新选择」按钮可见' ($null -ne (Assert-Reachable $win $pageScrollB '重新选择' '重新选择 按钮' 'Button'))
$shotInvalid = Join-Path $evidence 'invalid-hint.png'
[void](Bring-ToFront $win); Start-Sleep -Milliseconds 200
[void](Save-WindowScreenshot $win $shotInvalid)
Assert-True 'D: 截图 失效路径提示 已保存' (Test-Path -LiteralPath $shotInvalid)

# B2a：重新选择 空确认（不勾选任何进程）不得删除旧目标（D1 修复回归）。
$reselectBtnA = Assert-Reachable $win (Get-PageScrollViewer $win) '重新选择' '重新选择 按钮(空确认回归)' 'Button'
Assert-True 'B2a: 「重新选择」按钮可达(空确认回归)' ($null -ne $reselectBtnA)
if ($reselectBtnA) {
    [void](Invoke-Click $reselectBtnA)
    $pickerE = Find-PickerWindow $win 12
    Assert-True 'B2a: 重新选择 打开选择窗(空确认回归)' ($null -ne $pickerE)
    if ($pickerE) {
        $null = Wait-Like $pickerE '个进程' 12
        $okBtnE = Find-DescendantLike $pickerE '确定' 'Button'
        Assert-True 'B2a: 确定按钮存在(空确认回归)' ($null -ne $okBtnE)
        if ($okBtnE) { [void](Invoke-RealClickOn $pickerE $okBtnE) }
        Start-Sleep -Milliseconds 700
        # 空确认：摘要必须不出现、选择窗必须仍在（原目标绝不被删除）。
        Assert-True 'B2a: 空确认后选择窗仍在(未误删旧目标)' ($null -ne (Find-PickerWindow $win 2))
        Assert-True 'B2a: 空确认后未出现确认摘要' ($null -eq (Find-SummaryWindow $pickerE $win 3))
        Assert-True 'B2a: 空确认提示「未选择任何程序，原目标保持不变」' ($null -ne (Find-DescendantLike $pickerE '未选择任何程序，原目标保持不变'))
        Close-PickerByCancel $pickerE
        Start-Sleep -Milliseconds 700
        Assert-True 'B2a: 空确认取消后选择窗关闭' ($null -eq (Find-PickerWindow $win 2))
    }
    Assert-True 'B2a: 空确认后原失效目标仍保留' ($null -ne (Find-DescendantLike $win '原程序路径已失效'))
}

# B2：重新选择 → mstsc → 确定 → 确认替换 → 失效行被替换，失效提示消失。
$reselectBtn = Assert-Reachable $win (Get-PageScrollViewer $win) '重新选择' '重新选择 按钮(点击)' 'Button'
Assert-True 'B2: 「重新选择」按钮可达' ($null -ne $reselectBtn)
if ($reselectBtn) {
    [void](Invoke-Click $reselectBtn)
    $picker4 = Find-PickerWindow $win 12
    Assert-True 'B2: 重新选择 打开选择窗' ($null -ne $picker4)
    if ($picker4) {
        $null = Wait-Like $picker4 '个进程' 12
        $refreshBtn5 = Find-DescendantLike $picker4 '刷新' 'Button'
        if ($refreshBtn5) { [void](Invoke-Click $refreshBtn5); Start-Sleep -Milliseconds 800 }
        $cRow = Find-RowBySearch $picker4 'mstsc' $mstscExe
        Assert-True 'B2: mstsc 行可选' ($null -ne $cRow)
        $cCheck = Get-CheckBoxInRow $cRow
        Assert-True 'B2: 勾选 mstsc' ($null -ne $cCheck -and $cCheck.Current.IsEnabled -and (Invoke-ClickCheckbox $picker4 $cCheck))
        $okBtn4 = Find-DescendantLike $picker4 '确定' 'Button'
        if ($okBtn4) { [void](Invoke-RealClickOn $picker4 $okBtn4) }
        # D1：确定后先出现确认摘要（重选模式显示原路径/新路径/确认替换），确认替换后才替换旧行。
        $summaryB = Find-SummaryWindow $picker4 $win
        Assert-True 'B2: 确认摘要窗口出现(重选模式)' ($null -ne $summaryB)
        if ($summaryB) {
            Assert-True 'B2: 摘要显示原路径' ($null -ne (Find-DescendantLike $summaryB '原路径：'))
            Assert-True 'B2: 摘要显示新路径' ($null -ne (Find-DescendantLike $summaryB '新路径：'))
            Assert-True 'B2: 摘要含「确认替换」按钮' ($null -ne (Find-DescendantExact $summaryB '确认替换' 'Button'))
            $shotReselectSummary = Join-Path $evidence 'reselect-summary.png'
            [void](Bring-ToFront $summaryB); Start-Sleep -Milliseconds 150
            [void](Save-WindowScreenshot $summaryB $shotReselectSummary)
            Assert-True 'D: 截图 重新选择确认摘要 已保存' (Test-Path -LiteralPath $shotReselectSummary)
            $replaceBtn = Find-DescendantExact $summaryB '确认替换' 'Button'
            if ($replaceBtn) { [void](Invoke-RealClickOn $summaryB $replaceBtn) }
            Start-Sleep -Milliseconds 900
        }
        Assert-True 'B2: 确认替换后选择窗关闭' ($null -eq (Find-PickerWindow $win 2))
    }
    $pageScroll3 = Get-PageScrollViewer $win
    $hasMstsc = ($null -ne (Assert-Reachable $win $pageScroll3 'mstsc.exe' '目标: mstsc.exe 已入列表' 'CheckBox'))
    $goneHint = $null -eq (Find-DescendantLike $win '原程序路径已失效')
    Assert-True 'B2: 失效行被替换(mstsc 入列表)' $hasMstsc
    Assert-True 'B2: 失效提示已消失' $goneHint
}
$shotTargets = Join-Path $evidence 'settings-targets.png'
[void](Bring-ToFront $win); Start-Sleep -Milliseconds 200
[void](Save-WindowScreenshot $win $shotTargets)
Assert-True 'D: 截图 设置页目标列表 已保存' (Test-Path -LiteralPath $shotTargets)

# ======================================================================
# C：安全边界确认
# ======================================================================
Write-Host "`n== C: 安全边界 =="
Start-Sleep -Milliseconds 300
Assert-True 'C1: winver 全程存活(未被关闭)' (-not $procWinver.HasExited)
Assert-True 'C1: charmap 全程存活(未被关闭)' (-not $procCharmap.HasExited)
Assert-True 'C1: mstsc 全程存活(未被关闭)' (-not $procMstsc.HasExited)
Assert-True 'C2: 应用全程存活(无崩溃)' (-not $launch2.Proc.HasExited)
$cfgFinal = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-True 'C2: config.TestMode=true' ($cfgFinal.TestMode -eq $true)
Assert-True 'C2: config RealPowerEnabled=false(无真实电源)' ($cfgFinal.RealPowerEnabled -ne $true)

# ======================================================================
# cleanup：只回收本冒烟启动的进程（精确 PID）
# ======================================================================
Stop-SmokeApp $launch2.Proc
Stop-LaunchedProcs
Remove-Item Env:AUTOSHUTDOWN_DATA_ROOT -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("S-CLOSEUI1 UI SMOKE: pass={0} fail={1} skip={2}" -f $pass, $fail, $skip)
if ($fail -gt 0) { exit 1 }
exit 0
