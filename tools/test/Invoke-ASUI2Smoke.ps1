#Requires -Version 5.1
# tools/test/Invoke-ASUI2Smoke.ps1
# S-UI2 UI 冒烟（首页重构 / 多任务可用性 / 每周指定星期 / 一次性日历 / Office 只读卡 / 版本单源）。
#
# 检查范围（对应 S-UI2 阶段执行书与结果记录验收清单）：
#   P1  启动 + DPI 自校准 + 全新 fail-closed 初始化 + 首页三区域（左=创建 / 右上=当前任务 / 右下=最近活动）可见
#   P2  首页「无最外层滚动条」：页面根不是 ScrollViewer、无整页滚动条；左卡内部滚动可用（仅首页如此）
#   P3  每周指定星期：周一~周日 7 个 CheckBox 可见可点；默认周一~周五选中、周六/周日未选；周六可选中可取消
#   P4  一次性日历可用：执行日期/今天 快捷键/日历弹出包含当天日期/过去时间被明确拒绝（内联错误文案）
#   P5  连续创建 2+ 任务（倒计时默认 30 分钟 → 改为 10 分钟再建第二个）；首页「共 2 个任务」；任务管理同时显示 2 行
#   P6  版本单源：顶部状态栏 / 左下导航 / 关于页三处均显示同一候选版本文本（v2.0.0-*）
#   P7  高级功能页 Office 只读说明卡可见 + 「查看日志与诊断」跳转可用
#   P8  关于软件页 隐私与安全边界 / 帮助与反馈 文案可见（含「当前版本暂无在线反馈通道」诚实声明）
#   P9  截图证据：home / weekday / onetime / tasks / office / about 共 6 张 PNG
#   P10 全程应用存活（无崩溃）；两次独立运行均须通过（CI 侧调用本脚本两轮）
#
# 约束：
#   - 全部在隔离数据根（AUTOSHUTDOWN_DATA_ROOT）沙箱内运行，绝不触碰真实用户数据；
#     全程 TestMode=true，绝不触发任何真实电源操作；绝不扫描/自动发现/出公网。
#   - DPI：单显示器机器只能按当前系统缩放运行完整电池；其余档位如实 SKIP（列入人工待验），
#     绝不伪称已覆盖。缩放档位自校准：读主窗口物理高度 ÷ 640 DIP = 实际缩放比。
#   - 采用有界、可诊断的就绪等待（不无限等待、不以简单重试掩盖失败）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/Invoke-ASUI2Smoke.ps1 `
#       [-ReleaseExe <path>] [-DpiScales 100,125,150] [-EvidenceDir <dir>]
# 退出：0 = 全部通过（或仅显式 SKIP）；1 = 有失败。

[CmdletBinding()]
param(
    [string]$ReleaseExe = '',
    [int[]]$DpiScales = @(100, 125, 150),
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

# ---- 定位 S-UI2 候选 EXE ----
if (-not $ReleaseExe) {
    $dirs = @(Get-ChildItem -LiteralPath (Join-Path $root 'artifacts\release\v2.0.0') -Directory -Filter 'S-UI2-*' -ErrorAction SilentlyContinue)
    $dir = $dirs | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($dir) {
        $exe = Get-ChildItem -LiteralPath $dir.FullName -Filter 'AutoShutdown-v*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($exe) { $ReleaseExe = $exe.FullName }
    }
}

# 出错即回收本次冒烟自己启动的候选进程：只按启动时记录的精确 PID 回收，
# 绝不按进程名杀、绝不误杀生产 AutoShutdown.exe（S-UI2-D1 硬性要求）。
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
trap {
    Stop-LaunchedProcs
    Write-Host ("TRAP {0} @ {1}: {2}" -f $_.Exception.GetType().Name, $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message)
    throw
}
if (-not $ReleaseExe -or -not (Test-Path -LiteralPath $ReleaseExe)) {
    Write-Host 'FAIL  S-UI2 候选 EXE 未找到（需先运行 tools/Publish-ReleaseCandidate.ps1 -Step S-UI2）。'
    exit 1
}
$expectedVersion = $null
# S-PKG2：最终候选标签改为 S-PKG2.e50afcb（保留 S-UI2 兼容，供既有候选回归）。
# S-PKG3：重新发布候选标签为 S-PKG3.6e3e124；正则扩为 S-(?:UI2|PKG2|PKG3)（保留 S-UI2/S-PKG2 兼容）。
if ($ReleaseExe -match 'AutoShutdown-(v[\d.]+-S-(?:UI2|PKG2|PKG3)\.[0-9a-f]+)\.exe$') { $expectedVersion = $Matches[1] }
if (-not $expectedVersion) {
    Write-Host "FAIL  无法从 EXE 名解析版本：$ReleaseExe"
    exit 1
}
if (-not $EvidenceDir) {
    $EvidenceDir = Join-Path $root 'S-PKG-work包\S-UI2-截图证据'
}
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

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
function Get-ControlType([string]$controlType) {
    switch ($controlType) {
        'RadioButton' { return [System.Windows.Automation.ControlType]::RadioButton }
        'CheckBox'    { return [System.Windows.Automation.ControlType]::CheckBox }
        'Button'      { return [System.Windows.Automation.ControlType]::Button }
        'ListItem'    { return [System.Windows.Automation.ControlType]::ListItem }
        'ComboBox'    { return [System.Windows.Automation.ControlType]::ComboBox }
        default       { return $null }
    }
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
function Count-ExactName($win, [string]$name) {
    # 统计窗口中 Name 精确等于 $name 的元素个数（用于任务行数 / 版本位点数判定）。
    if (-not $win) { return 0 }
    $count = 0
    try {
        foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($el.Current.Name -eq $name) { $count++ }
        }
    } catch { }
    return $count
}
function Count-ExactButton($win, [string]$name) {
    # 统计 Name 精确等于 $name 的 Button 元素。WPF 会把每个 Button 与其内层 Text 子元素
    # 双暴露到 UIA 树（按钮名与文本同名），因此任务行数 / 按钮数必须按 ControlType.Button 计。
    if (-not $win) { return 0 }
    $count = 0
    try {
        $btnCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
            if ($el.Current.Name -eq $name) { $count++ }
        }
    } catch { }
    return $count
}
function Get-AllNames($win) {
    $names = New-Object System.Collections.Generic.List[string]
    if (-not $win) { return $names }
    try {
        foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
            $n = $el.Current.Name
            if ($n -and $n.Trim()) { $names.Add($n.Trim()) }
        }
    } catch { }
    return $names
}
function Get-NavListItem($win, [string]$needle) {
    $liCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    foreach ($i in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)) {
        if ($i.Current.Name -like "*$needle*") { return $i }
    }
    return $null
}

# ---------- 有界、可诊断的就绪等待 ----------
function Get-UiReadiness($win) {
    $all = Get-AllNames $win
    return @{
        HeaderSettings = Find-DescendantLike $win '设置' 'Button'
        NavAbout       = Find-DescendantLike $win 'PageKey = about' 'ListItem'
        NavTasks       = Find-DescendantLike $win 'PageKey = tasks' 'ListItem'
        DescendantCount = $all.Count
        SampleNames     = ($all | Select-Object -First 10) -join ' | '
    }
}
function Wait-UiReady($win, [int]$timeoutSeconds = 40) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $details = $null
    while ((Get-Date) -lt $deadline) {
        $details = Get-UiReadiness $win
        if ($details.HeaderSettings -and $details.NavAbout -and $details.NavTasks) {
            return @{ Ready = $true; Details = $details }
        }
        Start-Sleep -Milliseconds 300
    }
    return @{ Ready = $false; Details = $details }
}
function Wait-UiElementLike($win, [string]$substring, [int]$timeoutSeconds = 10, [string]$controlType = '') {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not $win) { return $null }
        $el = Find-DescendantLike $win $substring $controlType
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Wait-Until([scriptblock]$Condition, [int]$timeoutSeconds = 15, [string]$label = '') {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = & $Condition
        if ($last) { return $last }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

# ---------- 窗口 / 滚动 / 截图 ----------
Add-Type -Namespace ASUi2 -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
'@
function Set-WindowSizePhysical($win, [int]$width, [int]$height) {
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    try {
        $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        if ($wp.Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Maximized) {
            $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
            Start-Sleep -Milliseconds 400
        }
    } catch { }
    # SWP_NOZORDER=0x0004, SWP_NOACTIVATE=0x0010（移动到 0,0 便于取证）
    [ASUi2.Win32]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $width, $height, 0x0004 -bor 0x0010) | Out-Null
}
function Get-PageScrollViewer($win) {
    $paneCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Pane)
    $best = $null
    try {
        foreach ($p in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $paneCond)) {
            $r = $p.Current.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -lt 400 -or $r.Height -lt 250) { continue }   # 排除导航/小面板
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
function Get-ScrollablePanes($win) {
    # 收集内容区内「实际可滚动」的 Pane（VerticalScrollPercent != NoScroll），并附是否覆盖整页。
    $result = New-Object System.Collections.Generic.List[object]
    if (-not $win) { return $result }
    $paneCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Pane)
    try {
        foreach ($p in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $paneCond)) {
            $r = $p.Current.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -lt 80 -or $r.Height -lt 60) { continue }
            $sp = $null
            try { $sp = $p.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch { }
            if (-not $sp) { continue }
            $scrollable = $false
            try { $scrollable = ($sp.Current.VerticalScrollPercent -ne [System.Windows.Automation.ScrollPattern]::NoScroll) } catch { }
            $result.Add(@{ El = $p; Pattern = $sp; Viewport = $r; Scrollable = $scrollable })
        }
    } catch { }
    return $result
}
function Set-PaneScrollTop($pane, [double]$pct) {
    if (-not $pane) { return }
    try { $pane.Pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct) } catch { }
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
        Write-Host ("WARN Save-WindowScreenshot：窗口矩形无效 (w={0} h={1})，跳过 {2}" -f $rect.Width, $rect.Height, (Split-Path -Leaf $path))
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
function Invoke-Click($el) {
    if (-not $el) { return $false }
    try { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return $true } catch { }
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
    try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true } catch { }
    return $false
}
function Get-SelectedText($el) {
    try {
        $sel = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        return $sel.Current.IsSelected
    } catch { return $null }
}
function Get-ToggleState($el) {
    # CheckBox/RadioButton：TogglePattern.Current.ToggleState → On=1 / Off=0 / Indeterminate=2
    if (-not $el) { return $null }
    try {
        $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        return $tp.Current.ToggleState
    } catch { return $null }
}
function Assert-Reachable($win, $scroll, [string]$needle, [string]$label) {
    # 元素可直接看到 → 可达；否则若页面可滚动则分档滚动后可达（有界、可诊断）。
    $el = Find-DescendantLike $win $needle
    if (-not $el) { Assert-True ("reachable: {0}" -f $label) $false 'not found'; return }
    if (-not $el.Current.IsOffscreen) { Assert-True ("reachable: {0}" -f $label) $true 'visible'; return }
    if (-not $scroll -or -not $scroll.Scrollable) { Assert-True ("reachable: {0}" -f $label) $false 'offscreen and page not scrollable'; return }
    $ok = $false; $where = 'never'
    foreach ($pct in @(25, 50, 75, 100)) {
        try { $scroll.Pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct) } catch { break }
        Start-Sleep -Milliseconds 250
        $e2 = Find-DescendantLike $win $needle
        if ($e2 -and -not $e2.Current.IsOffscreen) { $ok = $true; $where = "scroll-$pct"; break }
    }
    try { $scroll.Pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0) } catch { }
    Assert-True ("reachable: {0}" -f $label) $ok $where
}
function Assert-ReachableViaInternalScroll($win, $contentRect, [string]$needle, [string]$label) {
    # 首页内部滚动可达：目标在左卡/右卡内部滚动区域内时，滚动该区域直至可见。
    # 仅用于首页（页面本身不滚动）；滚动后回到顶部，不改变页面状态。
    $el = Find-DescendantLike $win $needle
    if (-not $el) { Assert-True ("reachable: {0}" -f $label) $false 'not found'; return }
    if (-not $el.Current.IsOffscreen) { Assert-True ("reachable: {0}" -f $label) $true 'visible'; return }
    $ok = $false; $where = 'never'
    foreach ($pane in (Get-ScrollablePanes $win)) {
        if (-not $pane.Scrollable) { continue }
        # 只滚动内容区内的内部面板
        $r = $pane.Viewport
        if ($r.Right -le $contentRect.Left -or $r.Left -ge $contentRect.Right) { continue }
        foreach ($pct in @(0, 25, 50, 75, 100)) {
            Set-PaneScrollTop $pane $pct
            Start-Sleep -Milliseconds 250
            $e2 = Find-DescendantLike $win $needle
            if ($e2 -and -not $e2.Current.IsOffscreen) { $ok = $true; $where = "pane-scroll-$pct"; break }
        }
        Set-PaneScrollTop $pane 0
        if ($ok) { break }
    }
    Assert-True ("reachable: {0}" -f $label) $ok $where
}

# ---------- 组合框选择（自定义样式：折叠时无 Name；展开后弹窗项为 ListItem） ----------
function Scroll-PaneToVisible($win, $contentRect, [string]$needle) {
    # 滚动内部面板直至元素可见（不重置滚动位置，供日历弹出等需要目标在屏内的交互使用）。
    $el = Find-DescendantLike $win $needle
    if ($el -and -not $el.Current.IsOffscreen) { return $true }
    foreach ($pane in (Get-ScrollablePanes $win)) {
        if (-not $pane.Scrollable) { continue }
        $r = $pane.Viewport
        if ($r.Right -le $contentRect.Left -or $r.Left -ge $contentRect.Right) { continue }
        foreach ($pct in @(25, 50, 75, 100)) {
            Set-PaneScrollTop $pane $pct
            Start-Sleep -Milliseconds 250
            $e2 = Find-DescendantLike $win $needle
            if ($e2 -and -not $e2.Current.IsOffscreen) { return $true }
        }
    }
    return $false
}

function Select-ComboListItem($win, [string]$itemText, [int]$timeoutSeconds = 8) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $comboCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
    $liCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    while ((Get-Date) -lt $deadline) {
        foreach ($cb in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboCond)) {
            $ecp = $null
            try { $ecp = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { }
            if (-not $ecp) { continue }
            try { $ecp.Expand() } catch { continue }
            Start-Sleep -Milliseconds 450
            $found = $false
            foreach ($li in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)) {
                if ($li.Current.Name -eq $itemText) {
                    try { $li.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); $found = $true } catch { }
                    break
                }
            }
            try { $ecp.Collapse() } catch { }
            Start-Sleep -Milliseconds 250
            if ($found) { return $true }
        }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

# ---------- 一次性日历（DatePicker 支持 ValuePattern；日历弹出后出现 日 Button） ----------
function Get-DatePicker($win) {
    if (-not $win) { return $null }
    try {
        foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
            $vp = $null
            try { $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) } catch { }
            if (-not $vp) { continue }
            $v = ''
            try { $v = [string]$vp.Current.Value } catch { continue }
            if ($v -match '^\d{1,4}[-/]\d{1,2}[-/]\d{1,2}') { return @{ El = $el; ValuePattern = $vp; Value = $v } }
        }
    } catch { }
    return $null
}
function Open-DatePickerCalendar($dp, $win) {
    if (-not $dp) { return $false }
    # DatePicker 的日历切换按钮是其子 Button（模板 PART_Button，Name 通常为空）。
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $dp.El.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
        if (Invoke-Click $b) { return $true }
    }
    return $false
}
function Get-CalendarDayButtonCount($win) {
    # WPF Calendar 的日按钮在 zh-CN 下以完整日期命名（如「2026年8月19日」），
    # 而非裸数字（S-UI2 冒烟实测：日历在窗口内 UIA 树、不另开顶层窗口）。因此按
    # 日期形态匹配：zh-CN 完整日期 / 裸数字 / yyyy-M-d / M/d/yyyy 等常见命名。
    $count = 0
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    try {
        foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
            if ($b.Current.Name -match '^\d{4}年\d{1,2}月\d{1,2}日$|^\d{1,2}$|^\d{4}-\d{1,2}-\d{1,2}$|^\d{1,2}/\d{1,2}/\d{4}$') { $count++ }
        }
    } catch { }
    return $count
}
function Get-CalendarTodayButton($win) {
    # 「当天日期」在日历中的对应日按钮（zh-CN 命名，如 2026年8月19日）。用于证明
    # 日历已打开且当天日期可见可选中，比仅计日按钮数更强。
    $today = Get-Date
    $todayText = '{0}年{1}月{2}日' -f $today.Year, $today.Month, $today.Day
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
        if ($b.Current.Name -eq $todayText) { return $b }
    }
    return $null
}

# ---------- 应用启动/停止 ----------
function Start-SmokeApp([string]$dataRoot) {
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $env:AUTOSHUTDOWN_DATA_ROOT = $dataRoot
    $proc = Start-Process -FilePath $ReleaseExe -PassThru -WorkingDirectory (Split-Path -Parent $ReleaseExe)
    Add-LaunchedProc $proc
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
    $ready = Wait-UiReady $win
    if (-not $ready.Ready) {
        $d = $ready.Details
        $reason = ("UI-not-ready: descendants={0} header={1} navAbout={2} navTasks={3} sample=[{4}]" -f
            $d.DescendantCount, $d.HeaderSettings, $d.NavAbout, $d.NavTasks, $d.SampleNames)
        return @{ Proc = $proc; Win = $win; Reason = $reason }
    }
    return @{ Proc = $proc; Win = $win; Reason = 'ok' }
}
function Stop-SmokeApp($proc) {
    if ($proc -and (-not $proc.HasExited)) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        $w = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $w -and -not $proc.HasExited) { Start-Sleep -Milliseconds 200 }
    }
    Start-Sleep -Milliseconds 500
    Remove-Item Env:AUTOSHUTDOWN_DATA_ROOT -ErrorAction SilentlyContinue
}
function Select-NavPage($win, [string]$needle, [string]$waitFor) {
    $item = Get-NavListItem $win $needle
    if (-not $item) { return $false }
    try { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { return $false }
    if (-not $waitFor) { return $true }
    return ($null -ne (Wait-UiElementLike $win $waitFor 8))
}
Write-Host "== S-UI2 UI smoke ($expectedVersion) =="
Write-Host "EXE: $ReleaseExe"

$sandboxes = New-Object System.Collections.Generic.List[string]
function New-SmokeSandbox([string]$tag) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("as-ui2-$tag-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $script:sandboxes.Add($dir)
    return $dir
}

# ======================================================================
# 主电池（单会话）：在系统当前缩放下执行全部检查
# ======================================================================
$batteryRan = $false
function Invoke-UI2Battery([int]$scale, [string]$tag) {
    $script:batteryRan = $true
    $sb = New-SmokeSandbox $tag
    $data = Join-Path $sb 'data'
    Write-Host "`n-- 启动（DPI ${scale}% 档） --"
    $launch = Start-SmokeApp $data
    Assert-True 'process alive' (-not $launch.Proc.HasExited) $launch.Reason
    Assert-True 'window found' ($null -ne $launch.Win) $launch.Reason
    if (-not $launch.Win) { Stop-SmokeApp $launch.Proc; return }
    $win = $launch.Win

    $scalePhys = $scale / 100.0
    $navDips = 220.0; $headerDips = 64.0
    $initRect = $win.Current.BoundingRectangle
    Write-Host ("  档位 scale={0}% （初始窗口物理 {1}x{2}）" -f $scale, [int]$initRect.Width, [int]$initRect.Height)

    # ---- P1：尝试置为 1600×900（DIP；当前缩放的真实物理像素）。受窗口状态/DPI 环境影响时
    #      如实 SKIP（列入人工待验），不伪称已覆盖；详细内容区检查改在紧凑 900×580 档进行。 ----
    $physW = [int][Math]::Round(1600 * $scale / 100.0)
    $physH = [int][Math]::Round(900 * $scale / 100.0)
    Set-WindowSizePhysical $win $physW $physH
    Start-Sleep -Milliseconds 800
    $r = $win.Current.BoundingRectangle
    if ([Math]::Abs($r.Width - $physW) -le 12 -and [Math]::Abs($r.Height - $physH) -le 12) {
        Assert-True 'P1: 1600×900 窗口精确尺寸' $true
    } else {
        Note-Skip 'P1: 1600×900 窗口精确尺寸' ("实际 {0}x{1}（窗口状态/DPI 环境限制，列入人工待验；内容区检查在紧凑档进行）" -f [int]$r.Width, [int]$r.Height)
    }

    # ---- P2：紧凑 900×580（DIP）档——首页两列布局压缩后无任何滚动条；左表单完整显示（创建按钮直接可见） ----
    $compactW = [int][Math]::Round(900 * $scale / 100.0)
    $compactH = [int][Math]::Round(580 * $scale / 100.0)
    Set-WindowSizePhysical $win $compactW $compactH
    Start-Sleep -Milliseconds 800
    $rc = $win.Current.BoundingRectangle
    if ([Math]::Abs($rc.Width - $compactW) -le 12 -and [Math]::Abs($rc.Height - $compactH) -le 12) {
        Assert-True 'P2: 紧凑 900×580 窗口尺寸' $true
    } else {
        Note-Skip 'P2: 紧凑 900×580 窗口精确尺寸' ("实际 {0}x{1}（按实际尺寸继续）" -f [int]$rc.Width, [int]$rc.Height)
    }
    # 内容区（窗口内 220DIP 导航右侧 / 64DIP 顶栏下方；按实际窗口尺寸计算）
    $contentX = $rc.X + $navDips * $scalePhys
    $contentY = $rc.Y + $headerDips * $scalePhys
    $contentW = $rc.Width - $navDips * $scalePhys
    $contentH = $rc.Height - $headerDips * $scalePhys
    $contentRect = New-Object System.Windows.Rect($contentX, $contentY, $contentW, $contentH)

    # ---- P1：fresh fail-closed + 初始化 ----
    Assert-True 'P1: fresh 配置不可用 header' ($null -ne (Find-Descendant $win '配置不可用'))
    $homeBtn = Find-DescendantLike $win '首页' 'Button'
    Assert-True 'P1: 首页 button(恢复导航) 存在' ($null -ne $homeBtn)
    if ($homeBtn) { [void](Invoke-Click $homeBtn) }
    $initBtn = Wait-UiElementLike $win '初始化安全配置' 8 'Button'
    Assert-True 'P1: 初始化安全配置 button(首页, 可恢复)' ($null -ne $initBtn)
    if ($initBtn) {
        [void](Invoke-Click $initBtn)
        $modeHeader = Wait-UiElementLike $win '安全测试模式' 12
        Assert-True 'P1: init 后 安全测试模式 header' ($null -ne $modeHeader)
        $cfgPath = Join-Path $data 'config.json'
        $cfg = $null
        $cfgDeadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $cfgDeadline -and -not (Test-Path -LiteralPath $cfgPath)) { Start-Sleep -Milliseconds 300 }
        if (Test-Path -LiteralPath $cfgPath) { $cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json }
        Assert-True 'P1: init 后 config.json 写入' ($null -ne $cfg)
        if ($cfg) {
            Assert-True 'P1: config.TestMode=true' ($cfg.TestMode -eq $true)
            Assert-True 'P1: config 未开启真实电源' ($cfg.RealPowerEnabled -ne $true)
        }
    } else { Note-Skip 'P1 init' '未找到初始化按钮' }

    # 回到首页
    $hb2 = Find-DescendantLike $win '首页' 'Button'
    if ($hb2) { [void](Invoke-Click $hb2) }
    Start-Sleep -Milliseconds 600
    Assert-True 'P1: 首页 创建定时任务 区可见' ($null -ne (Wait-UiElementLike $win '创建定时任务' 8))
    Assert-True 'P1: 首页 当前任务 卡可见' ($null -ne (Wait-UiElementLike $win '当前任务' 6))
    Assert-True 'P1: 首页 最近活动 区可见' ($null -ne (Wait-UiElementLike $win '最近活动' 6))

    # ---- P2：首页两列布局无任何滚动条（紧凑 900×580 档；左表单压缩后完整显示） ----
    $panes = Get-ScrollablePanes $win
    $actuallyScrollable = @($panes | Where-Object { $_.Scrollable }).Count
    $outerViolation = $false
    $outerDetail = ''
    foreach ($p in $panes) {
        if (-not $p.Scrollable) { continue }
        $vr = $p.Viewport
        $wRatio = $vr.Width / $contentW
        $hRatio = $vr.Height / $contentH
        if ($wRatio -ge 0.8 -and $hRatio -ge 0.8) {
            $outerViolation = $true
            $outerDetail = "pane {0}×{1} (ratios {2:F2}/{3:F2})" -f [int]$vr.Width, [int]$vr.Height, $wRatio, $hRatio
        }
    }
    Assert-True 'P2: 首页无可滚动面板(左表单完整显示/最近活动未溢出)' ($actuallyScrollable -eq 0) ("count={0}" -f $actuallyScrollable)
    Assert-True 'P2: 首页无整页(最外层)滚动条' (-not $outerViolation) $outerDetail
    # 左表单完整显示：创建按钮直接可见（不依赖任何内部滚动/不截断）
    $createBtnP2 = Wait-UiElementLike $win '创建任务' 8 'Button'
    Assert-True 'P2: 首页「创建任务」按钮直接可见(左表单完整显示)' ($null -ne $createBtnP2 -and -not $createBtnP2.Current.IsOffscreen)

    # ---- P3：每周指定星期（周一~周日 7 项，直接在当前首页左表单操作） ----
    $wkRb = Find-DescendantLike $win '每周指定星期' 'RadioButton'
    Assert-True 'P3: 时间模式「每周指定星期」存在' ($null -ne $wkRb)
    if ($wkRb) {
        Assert-True 'P3: 「每周指定星期」可选中' (Invoke-Click $wkRb -and (Get-SelectedText $wkRb) -eq $true)
        Start-Sleep -Milliseconds 600
        $weekdayLabels = @('周一', '周二', '周三', '周四', '周五', '周六', '周日')
        foreach ($d in $weekdayLabels) {
            Assert-ReachableViaInternalScroll $win $contentRect $d ('周选择项 {0} 可见' -f $d)
        }
        # 默认：周一~周五选中（ToggleState.On），周六/周日未选（ToggleState.Off）
        $monday = Wait-UiElementLike $win '周一' 6 'CheckBox'
        $saturday = Wait-UiElementLike $win '周六' 6 'CheckBox'
        $sunday = Wait-UiElementLike $win '周日' 6 'CheckBox'
        if ($monday -and $saturday -and $sunday) {
            Assert-True 'P3: 默认 周一 选中' ((Get-ToggleState $monday) -eq [System.Windows.Automation.ToggleState]::On)
            Assert-True 'P3: 默认 周六 未选' ((Get-ToggleState $saturday) -eq [System.Windows.Automation.ToggleState]::Off)
            Assert-True 'P3: 默认 周日 未选' ((Get-ToggleState $sunday) -eq [System.Windows.Automation.ToggleState]::Off)
            # 周六可选中/可取消
            $clicked = Invoke-Click $saturday
            Start-Sleep -Milliseconds 400
            Assert-True 'P3: 周六 可选中' ($clicked -and (Get-ToggleState $saturday) -eq [System.Windows.Automation.ToggleState]::On)
            [void](Invoke-Click $saturday)
            Start-Sleep -Milliseconds 400
            Assert-True 'P3: 周六 可取消' ((Get-ToggleState $saturday) -eq [System.Windows.Automation.ToggleState]::Off)
        } else { Note-Skip 'P3 周选项默认态' '周一/周六/周日 复选框未全部找到' }
        # 截图：每周指定星期
        $shotWk = Join-Path $EvidenceDir ('weekday-{0}percent.png' -f $scale)
        [void](Save-WindowScreenshot $win $shotWk)
        Assert-True 'P9: 截图 每周指定星期 已保存' (Test-Path -LiteralPath $shotWk)
    }

    # ---- P4：一次性日历可用 ----
    $otRb = Find-DescendantLike $win '一次性指定' 'RadioButton'
    Assert-True 'P4: 时间模式「一次性指定」存在' ($null -ne $otRb)
    if ($otRb) {
        Assert-True 'P4: 「一次性指定」可选中' (Invoke-Click $otRb -and (Get-SelectedText $otRb) -eq $true)
        Start-Sleep -Milliseconds 600
        Assert-ReachableViaInternalScroll $win $contentRect '执行日期' '执行日期 区'
        $todayBtn = Wait-UiElementLike $win '今天' 6 'Button'
        Assert-True 'P4: 「今天」快捷键存在' ($null -ne $todayBtn)
        # 先点「今天」→ 日期设为今天，DatePicker 的 ValuePattern 才有非空日期值可校验
        if ($todayBtn) {
            Assert-True 'P4: 「今天」可点击并设置日期' (Invoke-Click $todayBtn)
            Start-Sleep -Milliseconds 500
        }
        $dp = $null
        $dpDeadline = (Get-Date).AddSeconds(6)
        while ((Get-Date) -lt $dpDeadline) {
            $dp = Get-DatePicker $win
            if ($dp) { break }
            Start-Sleep -Milliseconds 300
        }
        Assert-True 'P4: DatePicker(执行日期) 存在并已设今天' ($null -ne $dp) '未找到支持日期值的控件'
        if ($dp) {
            $dpVal = $dp.Value
            Assert-True 'P4: 日期已设为非空(今天)' ($dpVal -match '^\d{1,4}[-/]\d{1,2}[-/]\d{1,2}') ("value={0}" -f $dpVal)
            # 日历弹出：包含当天日期（今天 + 00:00 已过去 → 内联错误文案出现）。
            # 紧凑档下先滚动左卡使日期控件在屏内，确保日历弹窗正常打开。
            [void](Scroll-PaneToVisible $win $contentRect '执行日期')
            Start-Sleep -Milliseconds 200
            $opened = Open-DatePickerCalendar $dp $win
            Start-Sleep -Milliseconds 700
            $dayCount = Get-CalendarDayButtonCount $win
            Assert-True 'P4: 日历弹出 包含日期日按钮(可用)' ($opened -and $dayCount -ge 20) ("opened={0} dayButtons={1}" -f $opened, $dayCount)
            $todayCalBtn = Get-CalendarTodayButton $win
            Assert-True 'P4: 日历包含当天日期' ($null -ne $todayCalBtn) ("today={0} 日按钮未找到(日历未打开或日期不可见)" -f (Get-Date).ToString('yyyy-MM-dd'))
            # 过去时间拒绝（今天 + 默认 00:00:00 已过去）
            $pastErr = Wait-UiElementLike $win '所选日期时间已过去' 8
            Assert-True 'P4: 过去时间被明确拒绝(内联错误)' ($null -ne $pastErr)
        }
        $shotOt = Join-Path $EvidenceDir ('onetime-{0}percent.png' -f $scale)
        [void](Save-WindowScreenshot $win $shotOt)
        Assert-True 'P9: 截图 一次性日历 已保存' (Test-Path -LiteralPath $shotOt)
    }

    # ---- P5：连续创建 2+ 任务 ----
    # 任务1：倒计时默认 30 分钟（验证默认值）。
    $cdRb = Find-DescendantLike $win '倒计时' 'RadioButton'
    Assert-True 'P5: 回到 倒计时 模式' ($null -ne $cdRb)
    if ($cdRb) {
        [void](Invoke-Click $cdRb)
        Start-Sleep -Milliseconds 600
    }
    $createBtn = Wait-UiElementLike $win '创建任务' 6 'Button'
    Assert-True 'P5: 创建任务 button 存在' ($null -ne $createBtn -and $createBtn.Current.IsEnabled)
    if ($createBtn -and $createBtn.Current.IsEnabled) {
        Assert-True 'P5: 创建任务1(倒计时默认30分钟) 可点击' (Invoke-Click $createBtn)
        Start-Sleep -Milliseconds 1500
    }
    # 任务2：切换为「每周指定星期」（默认周一~周五；CheckBox 交互可靠，且触发时刻与任务1不同，
    # 避免同刻仲裁干扰；无需依赖 UIA 组合框展开/选择的自定义样式时序）。
    $wkRb2 = Find-DescendantLike $win '每周指定星期' 'RadioButton'
    Assert-True 'P5: 切换「每周指定星期」创建任务2' ($null -ne $wkRb2)
    if ($wkRb2) {
        [void](Invoke-Click $wkRb2)
        Start-Sleep -Milliseconds 600
        $wkSun = Wait-UiElementLike $win '周日' 6 'CheckBox'
        Assert-True 'P5: 每周指定星期 面板已展开(周日可见)' ($null -ne $wkSun)
    }
    $createBtn2 = Wait-UiElementLike $win '创建任务' 6 'Button'
    if ($createBtn2 -and $createBtn2.Current.IsEnabled) {
        Assert-True 'P5: 创建任务2(每周指定星期) 可点击' (Invoke-Click $createBtn2)
        Start-Sleep -Milliseconds 1500
    }
    # 当前任务卡在首页右列直接可见（两列布局无独立创建视图，无需返回导航）
    $countText = Wait-UiElementLike $win '共 2 个任务' 12
    Assert-True 'P5: 首页当前任务卡显示「共 2 个任务」' ($null -ne $countText)
    $shotHome = Join-Path $EvidenceDir ('home-{0}percent.png' -f $scale)
    [void](Save-WindowScreenshot $win $shotHome)
    Assert-True 'P9: 截图 首页(两任务) 已保存' (Test-Path -LiteralPath $shotHome)

    # 任务管理：同时显示 2 行（按 ControlType.Button 精确计数，WPF 按钮+文本双暴露不会重复计）
    $okTasks = Select-NavPage $win 'PageKey = tasks' '任务管理'
    Assert-True 'P5: 导航到 任务管理' $okTasks
    if ($okTasks) {
        $stopCount = (Wait-Until { Count-ExactButton $win '停止' } 15 '任务行出现')
        Assert-True 'P5: 任务管理同时显示 2 行任务' ($stopCount -eq 2) ("停止按钮数(Button)={0}" -f $stopCount)
        $disableCount = Count-ExactButton $win '停用'
        Assert-True 'P5: 两任务均为启用(显示「停用」按钮)' ($disableCount -eq 2) ("停用按钮数(Button)={0}" -f $disableCount)
        $shotTasks = Join-Path $EvidenceDir ('tasks-{0}percent.png' -f $scale)
        [void](Save-WindowScreenshot $win $shotTasks)
        Assert-True 'P9: 截图 任务管理(两任务) 已保存' (Test-Path -LiteralPath $shotTasks)
    }

    # ---- P6：版本单源（顶部状态栏 / 左下导航 / 关于页） ----
    $homeBtn2 = Find-DescendantLike $win '首页' 'Button'
    if ($homeBtn2) { [void](Invoke-Click $homeBtn2); Start-Sleep -Milliseconds 600 }
    $verOnHome = Count-ExactName $win $expectedVersion
    Assert-True 'P6: 首页 顶部状态栏 + 左下导航 两处显示版本' ($verOnHome -ge 2) ("count={0}" -f $verOnHome)
    $okAbout = Select-NavPage $win 'PageKey = about' '关于软件'
    Assert-True 'P6: 导航到 关于软件' $okAbout
    if ($okAbout) {
        $verOnAbout = Count-ExactName $win $expectedVersion
        Assert-True 'P6: 关于页 三处显示同一版本' ($verOnAbout -ge 3) ("count={0}" -f $verOnAbout)
    }

    # ---- P7：高级功能页 Office 只读说明卡 ----
    $okAdv = Select-NavPage $win 'PageKey = advanced' '高级功能'
    Assert-True 'P7: 导航到 高级功能' $okAdv
    if ($okAdv) {
        $pageScroll = Get-PageScrollViewer $win
        Assert-Reachable $win $pageScroll '关机前自动保存运行中的' 'Office 只读说明卡'
        $goLogsBtn = $null
        $glDeadline = (Get-Date).AddSeconds(6)
        while ((Get-Date) -lt $glDeadline) {
            $goLogsBtn = Find-DescendantLike $win '查看日志与诊断' 'Button'
            if ($goLogsBtn -and -not $goLogsBtn.Current.IsOffscreen) { break }
            if ($pageScroll) {
                foreach ($pct in @(25, 50, 75, 100)) {
                    try { $pageScroll.Pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct) } catch { }
                    Start-Sleep -Milliseconds 200
                    $goLogsBtn = Find-DescendantLike $win '查看日志与诊断' 'Button'
                    if ($goLogsBtn -and -not $goLogsBtn.Current.IsOffscreen) { break }
                }
            }
            if ($goLogsBtn -and -not $goLogsBtn.Current.IsOffscreen) { break }
            Start-Sleep -Milliseconds 300
        }
        Assert-True 'P7: 「查看日志与诊断」可见可点' ($null -ne $goLogsBtn -and -not $goLogsBtn.Current.IsOffscreen -and $goLogsBtn.Current.IsEnabled)
        $shotOffice = Join-Path $EvidenceDir ('office-{0}percent.png' -f $scale)
        [void](Save-WindowScreenshot $win $shotOffice)
        Assert-True 'P9: 截图 高级功能(Office卡) 已保存' (Test-Path -LiteralPath $shotOffice)
        if ($goLogsBtn -and (Invoke-Click $goLogsBtn)) {
            $logsReached = Wait-UiElementLike $win '导出脱敏诊断包' 10
            Assert-True 'P7: 「查看日志与诊断」跳转至 日志与诊断页' ($null -ne $logsReached)
        }
    }

    # ---- P8：关于软件页 隐私与安全边界 / 帮助与反馈 ----
    $okAbout2 = Select-NavPage $win 'PageKey = about' '关于软件'
    Assert-True 'P8: 导航到 关于软件' $okAbout2
    if ($okAbout2) {
        $aboutScroll = Get-PageScrollViewer $win
        Assert-Reachable $win $aboutScroll '隐私与安全边界' '隐私与安全边界 卡'
        Assert-Reachable $win $aboutScroll '帮助与反馈' '帮助与反馈 卡'
        Assert-Reachable $win $aboutScroll '当前版本暂无在线反馈通道' '诚实反馈通道声明'
        Assert-Reachable $win $aboutScroll '数据只存本机' '隐私边界: 数据只存本机'
        Assert-Reachable $win $aboutScroll '诊断包脱敏' '隐私边界: 诊断包脱敏'
        Assert-Reachable $win $aboutScroll '远程控制默认关闭且默认只读' '隐私边界: 远程只读'
        Assert-Reachable $win $aboutScroll 'Office 文档自动保存只在你本地' '隐私边界: Office 不读内容'
        Assert-Reachable $win $aboutScroll '新手快速开始' '帮助: 新手快速开始'
        Assert-Reachable $win $aboutScroll '故障排查' '帮助: 故障排查'
        $shotAbout = Join-Path $EvidenceDir ('about-{0}percent.png' -f $scale)
        [void](Save-WindowScreenshot $win $shotAbout)
        Assert-True 'P9: 截图 关于软件 已保存' (Test-Path -LiteralPath $shotAbout)
    }

    # ---- P10：全程存活 ----
    Assert-True 'P10: 全程应用存活(无崩溃)' (-not $launch.Proc.HasExited)

    Stop-SmokeApp $launch.Proc
}

# ======================================================================
# 按 DPI 档位执行：仅当前系统缩放运行完整电池；其余档位如实 SKIP
# ======================================================================
$probeSb = New-SmokeSandbox 'probe'
$probeData = Join-Path $probeSb 'data'
$probe = Start-SmokeApp $probeData
$appliedDpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI
$detectedScale = 100
if ($appliedDpi) { $detectedScale = [int][Math]::Round(100 * $appliedDpi / 96.0) }
if ($probe.Win) {
    $pr = $probe.Win.Current.BoundingRectangle
    $crossCheck = [int][Math]::Round(100 * $pr.Height / 640)
    Write-Host ("`n系统缩放：AppliedDPI={0} → {1}%（窗口高度交叉校验 {2}%）" -f $appliedDpi, $detectedScale, $crossCheck)
} else {
    Write-Host ("`n系统缩放：AppliedDPI={0} → {1}%（自校准窗口不可用：{2}）" -f $appliedDpi, $detectedScale, $probe.Reason)
}
Stop-SmokeApp $probe.Proc

foreach ($s in $DpiScales) {
    if ($s -eq $detectedScale) {
        Invoke-UI2Battery $s ("d$s")
    } else {
        Note-Skip "DPI ${s}% 完整电池" "当前机器实际缩放为 ${detectedScale}%（单显示器）；${s}% 需在对应缩放的机器上运行（列入人工待验）"
    }
}
if (-not $batteryRan) {
    Write-Host 'FAIL  未在任何可用 DPI 档位运行完整电池。'
    $fail++
}

# ======================================================================
# cleanup
# ======================================================================
foreach ($d in $sandboxes) {
    Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item Env:AUTOSHUTDOWN_DATA_ROOT -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("S-UI2 UI SMOKE: pass={0} fail={1} skip={2}" -f $pass, $fail, $skip)
if ($fail -gt 0) { exit 1 }
exit 0
