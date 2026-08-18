#Requires -Version 5.1
# tools/test/Invoke-ASUI1Smoke.ps1
# S-UI1 UI 可用性冒烟（发布前 UI 可用性 / 导航页 / 诊断中心收尾）。
#
# 检查范围（对应 S-UI1 阶段执行书 E 节与补充专项）：
#   P1  启动 + 窗口缩放自校准 + 900×580 小窗口（物理像素按 DPI 换算）＋ 全新 fail-closed + 恢复入口
#   P2  首页：8 种时间模式 / 5 种电源动作文字完整可见、可点击、不重叠（WrapPanel 自适应换行）
#       以及页面纵向滚动可达（真正可操作滚动条）
#   P3  各页面（任务/高级/网络唤醒/日志诊断/设置/关于）可达 + 纵向滚动 + 无水平溢出
#   P4  网络唤醒页：WoL 目标字段带可见标签（机器名称/MAC/广播地址/UDP 端口）+ 格式提示
#   P5  日志与诊断页：日志/刷新/筛选/搜索/复制/打开目录/安全自检/导出脱敏包/截图入口可见可点
#   P6  关于软件页：产品名/真实版本/构建提交/unsigned-candidate/数据目录/日志目录/模式/配置状态/隐私边界
#       且全 UI 不再出现硬编码 "v1.0.0 测试版" / "后续开放" 占位
#   P7  截图证据：首页顶 / 首页滚动后 / 网络唤醒 / 日志诊断 / 关于 共 5 张 PNG
#
# 约束：
#   - 全部在隔离数据根（AUTOSHUTDOWN_DATA_ROOT）沙箱内运行，绝不触碰真实用户数据；
#     全程 TestMode=true，绝不触发任何真实电源操作；绝不扫描/自动发现/出公网。
#   - DPI：单显示器机器只能按当前系统缩放运行完整电池；其余档位如实 SKIP（列入人工待验），
#     绝不伪称已覆盖。缩放档位自校准：读主窗口物理高度 ÷ 640 DIP = 实际缩放比。
#   - 采用有界、可诊断的就绪等待（不无限等待、不以简单重试掩盖失败）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/test/Invoke-ASUI1Smoke.ps1 `
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

# ---- 定位 S-UI1 候选 EXE ----
if (-not $ReleaseExe) {
    $dirs = @(Get-ChildItem -LiteralPath (Join-Path $root 'artifacts\release\v2.0.0') -Directory -Filter 'S-UI1-*' -ErrorAction SilentlyContinue)
    $dir = $dirs | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($dir) {
        $exe = Get-ChildItem -LiteralPath $dir.FullName -Filter 'AutoShutdown-v*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($exe) { $ReleaseExe = $exe.FullName }
    }
}

# 出错即回收本次冒烟启动的候选进程（按候选 EXE 名精确匹配，绝不波及生产 AutoShutdown.exe）。
$script:smokeExeBase = [IO.Path]::GetFileNameWithoutExtension($ReleaseExe)
trap {
    if ($script:smokeExeBase) {
        Get-Process -Name $script:smokeExeBase -ErrorAction SilentlyContinue | ForEach-Object {
            try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
    Write-Host ("TRAP {0} @ {1}: {2}" -f $_.Exception.GetType().Name, $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message)
    throw
}
if (-not $ReleaseExe -or -not (Test-Path -LiteralPath $ReleaseExe)) {
    Write-Host 'FAIL  S-UI1 候选 EXE 未找到（需先运行 tools/Publish-ReleaseCandidate.ps1 -Step S-UI1）。'
    exit 1
}
$expectedVersion = $null
if ($ReleaseExe -match 'AutoShutdown-(v[\d.]+-S-UI1\.[0-9a-f]+)\.exe$') { $expectedVersion = $Matches[1] }
if (-not $expectedVersion) {
    Write-Host "FAIL  无法从 EXE 名解析版本：$ReleaseExe"
    exit 1
}
if (-not $EvidenceDir) {
    $EvidenceDir = Join-Path $root 'S-PKG-work包\S-UI1-截图证据'
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
        'TextBox'     { return [System.Windows.Automation.ControlType]::Edit }
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
        NavSettings    = Find-DescendantLike $win 'PageKey = settings' 'ListItem'
        DescendantCount = $all.Count
        SampleNames     = ($all | Select-Object -First 10) -join ' | '
    }
}
function Wait-UiReady($win, [int]$timeoutSeconds = 40) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $details = $null
    while ((Get-Date) -lt $deadline) {
        $details = Get-UiReadiness $win
        if ($details.HeaderSettings -and $details.NavAbout -and $details.NavSettings) {
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

# ---------- 窗口 / 滚动 / 截图 ----------
Add-Type -Namespace ASUi1 -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
'@
function Set-WindowSizePhysical($win, [int]$width, [int]$height) {
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    # 若窗口处于最大化，先还原为普通窗口（SetWindowPos 对最大化窗口的尺寸调整无效）。
    try {
        $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        if ($wp.Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Maximized) {
            $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
            Start-Sleep -Milliseconds 400
        }
    } catch { }
    # SWP_NOZORDER=0x0004, SWP_NOACTIVATE=0x0010（移动到 0,0 便于取证）
    [ASUi1.Win32]::SetWindowPos($hwnd, [IntPtr]::Zero, 0, 0, $width, $height, 0x0004 -bor 0x0010) | Out-Null
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
function Save-WindowScreenshot($win, [string]$path) {
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    # 若窗口被最小化，BoundingRectangle 会退化为 0×0；先还原再截图，否则截到空图。
    try {
        $wp = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
        if ($wp.Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Minimized) {
            $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
            Start-Sleep -Milliseconds 400
        }
    } catch { }
    $rect = $win.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) {
        Write-Host ("WARN Save-WindowScreenshot：窗口矩形无效 (w={0} h={1} x={2} y={3})，跳过 {4}" -f $rect.Width, $rect.Height, $rect.X, $rect.Y, (Split-Path -Leaf $path))
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
# 像素级「文字不裁切」判定（真实渲染底稿，不依赖字体度量）。
# 在按钮内容区（去除 12DIP 内边距与边框）扫描文字墨迹的左右最远列；
# 文本若被裁切或溢出，墨迹最右列会越过内容右缘；内容区外的边框/内边距不参与扫描。
function Get-TextInkExtent($bitmap, [System.Windows.Rect]$screenRect, [System.Windows.Rect]$winRect, [double]$scale) {
    $padPx = [int][Math]::Round(12 * $scale / 100.0)
    $ox = [int]$winRect.X; $oy = [int]$winRect.Y
    $bx0 = [int][Math]::Max(0, [int][Math]::Ceiling($screenRect.Left - $ox))
    $bx1 = [int][Math]::Min($bitmap.Width - 1, [int][Math]::Floor($screenRect.Right - $ox) - 1)
    $by0 = [int][Math]::Max(0, [int][Math]::Ceiling($screenRect.Top - $oy))
    $by1 = [int][Math]::Min($bitmap.Height - 1, [int][Math]::Floor($screenRect.Bottom - $oy) - 1)
    $contentLeft = $bx0 + $padPx
    $contentRight = $bx1 - $padPx
    # 垂直取按钮中间 70% 行带（文字垂直居中）
    $bandH = [int][Math]::Max(3, [int][Math]::Round(($by1 - $by0) * 0.7))
    $bandTop = $by0 + [int][Math]::Max(0, [int][Math]::Round((($by1 - $by0) - $bandH) / 2.0))
    $bandBot = [int][Math]::Min($by1, $bandTop + $bandH)
    $rightmost = -1; $leftmost = -1; $inkCount = 0
    for ($x = $contentLeft; $x -le $contentRight; $x++) {
        $minL = 999.0; $maxL = -1.0
        for ($y = $bandTop; $y -le $bandBot; $y++) {
            $px = $bitmap.GetPixel($x, $y)
            $lum = 0.299 * $px.R + 0.587 * $px.G + 0.114 * $px.B
            if ($lum -lt $minL) { $minL = $lum }
            if ($lum -gt $maxL) { $maxL = $lum }
        }
        if (($maxL - $minL) -ge 28) {
            $inkCount++
            if ($rightmost -lt 0) { $leftmost = $x }
            $rightmost = $x
        }
    }
    return @{
        InkCount = $inkCount; Leftmost = $leftmost; Rightmost = $rightmost
        ContentLeft = $contentLeft; ContentRight = $contentRight
        Fits = ($inkCount -ge 1 -and $rightmost -le $contentRight + 4 -and $leftmost -ge $contentLeft - 4)
    }
}
function Invoke-Click($el) {
    # RadioButton/CheckBox 用 TogglePattern.Toggle()，Button 用 InvokePattern.Invoke()，
    # ListBox 项用 SelectionItemPattern.Select()——每个模式用各自正确的方法。
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
# 文本宽度估算（GDI，96DPI 与 WPF DIP 近似；用于判断按钮是否被横向裁切）。
function Measure-TextWidth([string]$text, [double]$fontSizeDips) {
    $bmp = New-Object System.Drawing.Bitmap(1, 1)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $font = New-Object System.Drawing.Font('Microsoft YaHei UI', ($fontSizeDips * 0.75), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)
    $w = $g.MeasureString($text, $font).Width
    $font.Dispose(); $g.Dispose(); $bmp.Dispose()
    return $w
}

# ---------- 应用启动/停止 ----------
function Start-SmokeApp([string]$dataRoot) {
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $env:AUTOSHUTDOWN_DATA_ROOT = $dataRoot
    $proc = Start-Process -FilePath $ReleaseExe -PassThru -WorkingDirectory (Split-Path -Parent $ReleaseExe)
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
        $reason = ("UI-not-ready: descendants={0} header={1} navAbout={2} navSettings={3} sample=[{4}]" -f
            $d.DescendantCount, $d.HeaderSettings, $d.NavAbout, $d.NavSettings, $d.SampleNames)
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

Write-Host "== S-UI1 UI smoke ($expectedVersion) =="
Write-Host "EXE: $ReleaseExe"

$sandboxes = New-Object System.Collections.Generic.List[string]
function New-SmokeSandbox([string]$tag) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("as-ui1-$tag-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $script:sandboxes.Add($dir)
    return $dir
}

# ======================================================================
# 主电池（单会话）：在系统当前缩放下执行全部检查
# ======================================================================
$batteryRan = $false
function Invoke-UI1Battery([int]$scale, [string]$tag) {
    $script:batteryRan = $true
    $sb = New-SmokeSandbox $tag
    $data = Join-Path $sb 'data'
    Write-Host "`n-- 启动（DPI ${scale}% 档） --"
    $launch = Start-SmokeApp $data
    Assert-True 'process alive' (-not $launch.Proc.HasExited) $launch.Reason
    Assert-True 'window found' ($null -ne $launch.Win) $launch.Reason
    if (-not $launch.Win) { Stop-SmokeApp $launch.Proc; return }
    $win = $launch.Win

    # ---- 缩放档位（已由注册表 AppliedDPI 判定；窗口初始尺寸仅作记录） ----
    $initRect = $win.Current.BoundingRectangle
    Write-Host ("  档位 scale={0}% （初始窗口物理 {1}x{2}）" -f $scale, [int]$initRect.Width, [int]$initRect.Height)

    # ---- P1：窗口缩放后置为 900×580（DIP） ----
    $physW = [int][Math]::Round(900 * $scale / 100.0)
    $physH = [int][Math]::Round(580 * $scale / 100.0)
    Set-WindowSizePhysical $win $physW $physH
    Start-Sleep -Milliseconds 800
    $r = $win.Current.BoundingRectangle
    Assert-True 'P1: 窗口物理宽 ≈ 900×scale' ([Math]::Abs($r.Width - $physW) -le 3) ("got={0} want={1}" -f $r.Width, $physW)
    Assert-True 'P1: 窗口物理高 ≈ 580×scale' ([Math]::Abs($r.Height - $physH) -le 3) ("got={0} want={1}" -f $r.Height, $physH)
    Assert-True 'P1: 版本文本正确' ($null -ne (Find-Descendant $win $expectedVersion))

    # ---- P1：全新 fail-closed + 恢复入口 ----
    Assert-True 'P1: fresh 配置不可用 header' ($null -ne (Find-Descendant $win '配置不可用'))
    Assert-True 'P1: fresh NOT 安全测试模式' ($null -eq (Find-Descendant $win '安全测试模式'))
    $homeBtn = Find-DescendantLike $win '首页' 'Button'
    Assert-True 'P1: 首页 button(恢复导航) 存在' ($null -ne $homeBtn)
    if ($homeBtn) { [void](Invoke-Click $homeBtn) }
    $initBtn = Wait-UiElementLike $win '初始化安全配置' 8 'Button'
    Assert-True 'P1: 初始化安全配置 button(首页, 可恢复)' ($null -ne $initBtn)

    # ---- 初始化 -> 安全测试模式（双闸门：TestMode=true） ----
    if ($initBtn) {
        [void](Invoke-Click $initBtn)
        $modeHeader = Wait-UiElementLike $win '安全测试模式' 12
        Assert-True 'P1: init 后 安全测试模式 header' ($null -ne $modeHeader)
        $cfg = $null
        $cfgPath = Join-Path $data 'config.json'
        $cfgDeadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $cfgDeadline -and -not (Test-Path -LiteralPath $cfgPath)) { Start-Sleep -Milliseconds 300 }
        if (Test-Path -LiteralPath $cfgPath) { $cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json }
        Assert-True 'P1: init 后 config.json 写入' ($null -ne $cfg)
        if ($cfg) {
            Assert-True 'P1: config.TestMode=true' ($cfg.TestMode -eq $true)
            Assert-True 'P1: config 未开启真实电源' ($cfg.RealPowerEnabled -ne $true)
        }
    } else { Note-Skip 'P1 init' '未找到初始化按钮' }

    # 回到首页（页切换稳定后检查首页布局）
    $hb2 = Find-DescendantLike $win '首页' 'Button'
    if ($hb2) { [void](Invoke-Click $hb2) }
    $modeText = Wait-UiElementLike $win '时间模式' 8
    Assert-True 'P2: 首页 时间模式 区可见' ($null -ne $modeText)

    # ---- P2：8 种时间模式 + 5 种动作：完整可见/可点/不重叠 ----
    $pageScroll = Get-PageScrollViewer $win
    Assert-True 'P2: 页面纵向滚动条可用' ($null -ne $pageScroll -and $pageScroll.Scrollable)
    $viewport = if ($pageScroll) { $pageScroll.Viewport } else { $win.Current.BoundingRectangle }

    $modes = @('倒计时', '今天指定时间', '每天固定时间', '每周工作日', '下个工作日', '每月第N个工作日', '一次性指定', '空闲触发')
    $modeRects = New-Object System.Collections.Generic.List[System.Windows.Rect]
    $modeOverlap = $false
    foreach ($m in $modes) {
        $el = Find-DescendantLike $win $m 'RadioButton'
        if (-not $el) { Assert-True ("P2: 时间模式 '{0}' 存在" -f $m) $false 'not found'; continue }
        $name = $el.Current.Name
        Assert-True ("P2: 时间模式 '{0}' 可见" -f $m) (-not $el.Current.IsOffscreen)
        $br = $el.Current.BoundingRectangle
        # 文字不裁切：由 home-top 截图后的像素墨迹判定（Get-TextInkExtent）负责
        # 视口内（不横向溢出）
        Assert-True ("P2: 时间模式 '{0}' 在视口内(不溢出)" -f $m) ($br.Right -le $viewport.Right + 2) ("right={0} vpRight={1}" -f [int]$br.Right, [int]$viewport.Right)
        # 可点击（Toggle 选中）
        [void](Invoke-Click $el)
        $isSel = Get-SelectedText $el
        Assert-True ("P2: 时间模式 '{0}' 可点击" -f $m) ($isSel -eq $true) ("selected={0}" -f $isSel)
        $modeRects.Add($br)
    }
    # 重叠检查（两两不相交）
    for ($i = 0; $i -lt $modeRects.Count; $i++) {
        for ($j = $i + 1; $j -lt $modeRects.Count; $j++) {
            $a = $modeRects[$i]; $b = $modeRects[$j]
            if (-not ($a.Right -le $b.Left -or $b.Right -le $a.Left -or $a.Bottom -le $b.Top -or $b.Bottom -le $a.Top)) { $modeOverlap = $true }
        }
    }
    Assert-True 'P2: 8 种时间模式彼此不重叠' (-not $modeOverlap)

    # 模式切换后对应输入区出现（代表性模式）
    $cb = Find-DescendantLike $win '倒计时' 'RadioButton'
    if ($cb) { [void](Invoke-Click $cb); Start-Sleep -Milliseconds 400; Assert-True 'P2: 倒计时面板(小时/分钟/秒)' ($null -ne (Find-Descendant $win '秒')) }
    $cb = Find-DescendantLike $win '空闲触发' 'RadioButton'
    if ($cb) { [void](Invoke-Click $cb); Start-Sleep -Milliseconds 400; Assert-True 'P2: 空闲阈值面板' ($null -ne (Find-Descendant $win '空闲阈值')) }
    $cb = Find-DescendantLike $win '每月第N个工作日' 'RadioButton'
    if ($cb) { [void](Invoke-Click $cb); Start-Sleep -Milliseconds 400; Assert-True 'P2: 每月第几个工作日 选择器' ($null -ne (Find-Descendant $win '每月第几个工作日')) }

    $actions = @('关机', '重启', '睡眠', '休眠', '唤醒他机')
    $actRects = New-Object System.Collections.Generic.List[System.Windows.Rect]
    $actOverlap = $false
    foreach ($a in $actions) {
        $el = Find-DescendantLike $win $a 'RadioButton'
        if (-not $el) { Assert-True ("P2: 动作 '{0}' 存在" -f $a) $false 'not found'; continue }
        Assert-True ("P2: 动作 '{0}' 可见" -f $a) (-not $el.Current.IsOffscreen)
        $br = $el.Current.BoundingRectangle
        # 文字不裁切：由 home-top 截图后的像素墨迹判定（Get-TextInkExtent）负责
        Assert-True ("P2: 动作 '{0}' 在视口内(不溢出)" -f $a) ($br.Right -le $viewport.Right + 2) ("right={0} vpRight={1}" -f [int]$br.Right, [int]$viewport.Right)
        [void](Invoke-Click $el)
        $isSel = Get-SelectedText $el
        Assert-True ("P2: 动作 '{0}' 可点击" -f $a) ($isSel -eq $true) ("selected={0}" -f $isSel)
        $actRects.Add($br)
    }
    for ($i = 0; $i -lt $actRects.Count; $i++) {
        for ($j = $i + 1; $j -lt $actRects.Count; $j++) {
            $a = $actRects[$i]; $b = $actRects[$j]
            if (-not ($a.Right -le $b.Left -or $b.Right -le $a.Left -or $a.Bottom -le $b.Top -or $b.Bottom -le $a.Top)) { $actOverlap = $true }
        }
    }
    Assert-True 'P2: 5 种动作彼此不重叠' (-not $actOverlap)
    $wolRb = Find-DescendantLike $win '唤醒他机' 'RadioButton'
    if ($wolRb) { [void](Invoke-Click $wolRb); Start-Sleep -Milliseconds 400; Assert-True 'P2: 唤醒他机 → WoL 目标选择器' ($null -ne (Find-Descendant $win '目标机器')) }

    # P2 截图：首页顶部（8 模式 + 5 动作完整可见）
    $shotTop = Join-Path $EvidenceDir ('home-top-{0}percent.png' -f $scale)
    [void](Save-WindowScreenshot $win $shotTop)
    Assert-True 'P7: 截图 首页顶部 已保存' (Test-Path -LiteralPath $shotTop)

    # P2 文字不裁切（像素级）：在 home-top 截图上按按钮矩形扫描墨迹，验证文本在内容区(去内边距)内
    if (Test-Path -LiteralPath $shotTop) {
        $bmp = New-Object System.Drawing.Bitmap($shotTop)
        $winRectNow = $win.Current.BoundingRectangle
        foreach ($m in $modes) {
            $el = Find-DescendantLike $win $m 'RadioButton'
            if (-not $el) { continue }
            $ink = Get-TextInkExtent $bmp $el.Current.BoundingRectangle $winRectNow $scale
            Assert-True ("P2: 时间模式 '{0}' 文字不裁切" -f $m) $ink.Fits ("inkR={0} cR={1} inkL={2} cL={3} n={4}" -f $ink.Rightmost, $ink.ContentRight, $ink.Leftmost, $ink.ContentLeft, $ink.InkCount)
        }
        foreach ($a in $actions) {
            $el = Find-DescendantLike $win $a 'RadioButton'
            if (-not $el) { continue }
            $ink = Get-TextInkExtent $bmp $el.Current.BoundingRectangle $winRectNow $scale
            Assert-True ("P2: 动作 '{0}' 文字不裁切" -f $a) $ink.Fits ("inkR={0} cR={1} inkL={2} cL={3} n={4}" -f $ink.Rightmost, $ink.ContentRight, $ink.Leftmost, $ink.ContentLeft, $ink.InkCount)
        }
        $bmp.Dispose()
    }

    # P2 滚动可达：创建任务按钮 / 当前任务区 / 最近活动 需滚动后可见
    Assert-Reachable $win $pageScroll '创建任务' '创建任务 button'
    Assert-Reachable $win $pageScroll '当前任务' '当前任务 区'
    Assert-Reachable $win $pageScroll '最近活动' '最近活动 区'
    # 滚动到底后截图
    if ($pageScroll) {
        try { $pageScroll.Pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100) } catch { }
        Start-Sleep -Milliseconds 600
    }
    $shotBottom = Join-Path $EvidenceDir ('home-scrolled-{0}percent.png' -f $scale)
    [void](Save-WindowScreenshot $win $shotBottom)
    Assert-True 'P7: 截图 首页滚动后 已保存' (Test-Path -LiteralPath $shotBottom)
    if ($pageScroll) { try { $pageScroll.Pattern.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0) } catch { } }

    # ---- P3：各页面可达 + 纵向滚动 + 无水平溢出 ----
    $pages = @(
        @{ Needle = 'PageKey = tasks';    WaitFor = '任务管理';     Key = '新建任务' },
        @{ Needle = 'PageKey = advanced'; WaitFor = '高级功能';     Key = '无人值守' },
        @{ Needle = 'PageKey = wol';      WaitFor = '网络唤醒';     Key = '目标机器配置' },
        @{ Needle = 'PageKey = logs';     WaitFor = '日志与诊断';   Key = '日志目录' },
        @{ Needle = 'PageKey = settings'; WaitFor = '软件设置';     Key = '开机自启动' },
        @{ Needle = 'PageKey = about';    WaitFor = '关于软件';     Key = '隐私与安全边界' }
    )
    $allNames = New-Object System.Collections.Generic.List[string]
    foreach ($pg in $pages) {
        $okNav = Select-NavPage $win $pg.Needle $pg.WaitFor
        Assert-True ("P3: 导航可达 {0}" -f $pg.WaitFor) $okNav
        if (-not $okNav) { continue }
        $sv = Get-PageScrollViewer $win
        Assert-True ("P3: {0} 纵向滚动条可用" -f $pg.WaitFor) ($null -ne $sv)
        if ($sv) {
            # 无水平溢出：页面滚动区内的主要控件右缘不超出视口
            $types = @([System.Windows.Automation.ControlType]::Button, [System.Windows.Automation.ControlType]::RadioButton,
                       [System.Windows.Automation.ControlType]::CheckBox, [System.Windows.Automation.ControlType]::ComboBox,
                       [System.Windows.Automation.ControlType]::Edit)
            $conds = $types | ForEach-Object {
                New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $_)
            }
            $orCond = New-Object System.Windows.Automation.OrCondition($conds)
            $ctrlEls = $sv.El.FindAll([System.Windows.Automation.TreeScope]::Descendants, $orCond)
            $vp = $sv.Viewport
            $violations = New-Object System.Collections.Generic.List[string]
            foreach ($ce in $ctrlEls) {
                $cr = $ce.Current.BoundingRectangle
                if ($cr.IsEmpty) { continue }
                if ($cr.Right -gt $vp.Right + 2 -or $cr.Left -lt $vp.Left - 2) {
                    $violations.Add(("{0}(right={1})" -f $ce.Current.Name, [int]$cr.Right))
                }
            }
            Assert-True ("P3: {0} 无水平溢出" -f $pg.WaitFor) ($violations.Count -eq 0) ($violations -join ' | ')
        }
        $keyEl = Find-DescendantLike $win $pg.Key
        Assert-True ("P3: {0} 关键控件可见 {1}" -f $pg.WaitFor, $pg.Key) ($null -ne $keyEl -and -not $keyEl.Current.IsOffscreen)
        if ($keyEl) { Assert-True ("P3: {0} 关键控件可用" -f $pg.WaitFor) ($keyEl.Current.IsEnabled) ("enabled={0}" -f $keyEl.Current.IsEnabled) }
        foreach ($n in (Get-AllNames $win)) { $allNames.Add([string]$n) }
    }

    # ---- P4：网络唤醒页 WoL 字段标签 ----
    $okWol = Select-NavPage $win 'PageKey = wol' '网络唤醒'
    Assert-True 'P4: 导航到 网络唤醒' $okWol
    if ($okWol) {
        foreach ($lbl in @('机器名称', 'MAC 地址', '广播地址（可选）', 'UDP 端口（可选）')) {
            $l = Wait-UiElementLike $win $lbl 6
            Assert-True ("P4: WoL 标签 '{0}' 可见" -f $lbl) ($null -ne $l -and -not $l.Current.IsOffscreen)
        }
        Assert-True 'P4: MAC 格式提示' ($null -ne (Wait-UiElementLike $win 'MAC 格式：AA:BB:CC:DD:EE:FF' 6))
        Assert-True 'P4: 仅局域网说明' ($null -ne (Wait-UiElementLike $win '不扫描、不自动发现、不出公网' 6))
        Assert-True 'P4: 局域网远程控制入口' ($null -ne (Wait-UiElementLike $win '局域网远程控制入口' 6))
        $shotWol = Join-Path $EvidenceDir ('wol-{0}percent.png' -f $scale)
        [void](Save-WindowScreenshot $win $shotWol)
        Assert-True 'P7: 截图 网络唤醒 已保存' (Test-Path -LiteralPath $shotWol)
    }

    # ---- P5：日志与诊断页功能可见 ----
    $okLogs = Select-NavPage $win 'PageKey = logs' '日志与诊断'
    Assert-True 'P5: 导航到 日志与诊断' $okLogs
    if ($okLogs) {
        foreach ($b in @('刷新日志', '复制日志路径', '打开日志目录', '复制诊断摘要', '运行安全自检', '导出脱敏诊断包', '保存当前窗口截图')) {
            $be = Wait-UiElementLike $win $b 6 'Button'
            Assert-True ("P5: 功能按钮 '{0}' 可见可用" -f $b) ($null -ne $be -and -not $be.Current.IsOffscreen -and $be.Current.IsEnabled)
        }
        # 复制选中记录：无选中时禁用，选中一条日志后启用——可用性须通过交互验证（不是无条件可用）。
        $copySel = Wait-UiElementLike $win '复制选中记录' 6 'Button'
        Assert-True 'P5: 功能按钮 复制选中记录 可见' ($null -ne $copySel -and -not $copySel.Current.IsOffscreen)
        Assert-True 'P5: 复制选中记录 初始禁用(未选中日志)' ($null -ne $copySel -and -not $copySel.Current.IsEnabled)
        # 两个下拉（日志文件 / 级别筛选）：自定义样式致 ComboBox 的 UIA Name 为空，折叠时选中值文本也不暴露。
        # 展开后弹窗项会进入窗口树（ListItem，Name=选项文本），据此验证内容已加载且可操作。
        $comboCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
        $liAllCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboCond)
        Assert-True 'P5: 日志/级别 两个下拉存在' ($combos.Count -ge 2)
        $fileItems = 0; $filterItems = 0; $comboIdx = 0
        foreach ($cb in $combos) {
            $comboIdx++
            if ($comboIdx -gt 2) { break }
            try {
                $ecp = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                $ecp.Expand(); Start-Sleep -Milliseconds 500
                if ($comboIdx -eq 1) {
                    # 日志文件下拉（文档序第一个）：弹窗项 = autoshutdown-*.log
                    foreach ($li in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liAllCond)) {
                        if ($li.Current.Name -match '^autoshutdown-\d{4}-\d{2}-\d{2}\.log$') { $fileItems++ }
                    }
                } else {
                    # 级别筛选下拉：固定 5 项（全部/错误/警告/信息/调试）
                    $filterNames = @('全部', '错误', '警告', '信息', '调试')
                    foreach ($li in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liAllCond)) {
                        if ($filterNames -contains $li.Current.Name) { $filterItems++ }
                    }
                }
                try { $ecp.Collapse(); Start-Sleep -Milliseconds 200 } catch { }
            } catch { }
        }
        Assert-True 'P5: 日志文件下拉已加载(自动刷新)' ($fileItems -ge 1)
        Assert-True 'P5: 级别筛选下拉显示 全部/错误/警告/信息/调试' ($filterItems -ge 5)
        Assert-True 'P5: 仅错误/警告 勾选' ($null -ne (Find-DescendantLike $win '仅错误/警告' 'CheckBox'))
        Assert-True 'P5: 包含隐私信息(默认脱敏说明)' ($null -ne (Find-DescendantLike $win '包含隐私信息'))
        Assert-True 'P5: 诊断状态文本' ($null -ne (Find-DescendantLike $win '就绪'))
        # 选中一条日志记录 → 复制选中记录 由禁用转可用并执行复制（RelayCommand 经 CommandManager 在空闲时重询）。
        # 日志条目 ListBoxItem 的 UIA Name 是 LogEntryRow 记录的 ToString（"LogEntryRow { Time = yyyy-MM-ddTHH:... }"），
        # 与最近活动(RecentActivityItem)及导航项(NavItem) 可区分。
        $logItem = $null
        $liCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        foreach ($li in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)) {
            if ($li.Current.Name -match '^LogEntryRow \{ Time = \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}') { $logItem = $li; break }
        }
        Assert-True 'P5: 日志记录已加载' ($null -ne $logItem)
        if ($logItem) {
            try { $logItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { }
            $copyEnabled = $false
            $copyDeadline = (Get-Date).AddSeconds(6)
            while ((Get-Date) -lt $copyDeadline) {
                $copySel = Find-DescendantLike $win '复制选中记录' 'Button'
                if ($copySel -and $copySel.Current.IsEnabled) { $copyEnabled = $true; break }
                Start-Sleep -Milliseconds 300
            }
            Assert-True 'P5: 选中日志后 复制选中记录 可用' $copyEnabled
            if ($copyEnabled -and $copySel) {
                Assert-True 'P5: 复制选中记录 可点击' (Invoke-Click $copySel)
                # 剪贴板可能被其他进程占用（自动化环境常见 CLIPBRD_E_CANT_OPEN）：
                # 复制失败只应显示状态文本，绝不能把未处理异常抛回导致应用崩溃。应用存活即回归通过。
                Start-Sleep -Milliseconds 600
                Assert-True 'P5: 复制后应用仍存活(剪贴板失败不崩溃)' (-not $launch.Proc.HasExited)
            } else { Note-Skip 'P5 复制选中记录' '选中日志后按钮仍未启用' }
        }
        # 运行安全自检 → 6 项自检结果出现（只读、不修复）
        $scBtn = Find-DescendantLike $win '运行安全自检' 'Button'
        if ($scBtn -and (Invoke-Click $scBtn)) {
            $summary = Wait-UiElementLike $win '安全自检全部通过' 12
            Assert-True 'P5: 安全自检完成(只读全部通过)' ($null -ne $summary)
            foreach ($item in @('配置健康', '数据目录可写性', '本地任务', '任务计划同步', 'WoL 目标合法性', '远程监听/TLS')) {
                Assert-True ("P5: 自检项 '{0}' 出现" -f $item) ($null -ne (Find-DescendantLike $win $item))
            }
        } else { Note-Skip 'P5 自检' '运行安全自检按钮不可点' }
        $shotLogs = Join-Path $EvidenceDir ('logs-{0}percent.png' -f $scale)
        [void](Save-WindowScreenshot $win $shotLogs)
        Assert-True 'P7: 截图 日志与诊断 已保存' (Test-Path -LiteralPath $shotLogs)
    }

    # ---- P6：关于软件页 ----
    $okAbout = Select-NavPage $win 'PageKey = about' '关于软件'
    Assert-True 'P6: 导航到 关于软件' $okAbout
    if ($okAbout) {
        Assert-True 'P6: 产品名' ($null -ne (Wait-UiElementLike $win '电脑自动关机助手' 6))
        Assert-True 'P6: 真实版本' ($null -ne (Find-DescendantLike $win $expectedVersion))
        Assert-True 'P6: 构建提交 标签' ($null -ne (Find-DescendantLike $win '构建提交'))
        Assert-True 'P6: unsigned-candidate 如实标示' ($null -ne (Find-DescendantLike $win 'unsigned-candidate'))
        foreach ($label in @('数据目录', '日志目录', '运行模式', '配置状态', '隐私与安全边界', '帮助与反馈')) {
            Assert-True ("P6: 关于页 '{0}' 可见" -f $label) ($null -ne (Find-DescendantLike $win $label))
        }
        foreach ($n in (Get-AllNames $win)) { $allNames.Add([string]$n) }
        $shotAbout = Join-Path $EvidenceDir ('about-{0}percent.png' -f $scale)
        [void](Save-WindowScreenshot $win $shotAbout)
        Assert-True 'P7: 截图 关于软件 已保存' (Test-Path -LiteralPath $shotAbout)
    }

    # ---- P6b：全 UI 无硬编码占位文案 ----
    $joined = ($allNames | Select-Object -Unique) -join ' '
    Assert-True 'P6b: 不再出现 "v1.0.0 测试版"' ($joined.IndexOf('v1.0.0 测试版') -lt 0)
    Assert-True 'P6b: 不再出现 "后续开放" 占位' ($joined.IndexOf('后续开放') -lt 0)

    Stop-SmokeApp $launch.Proc
}

# ======================================================================
# 按 DPI 档位执行：仅当前系统缩放运行完整电池；其余档位如实 SKIP
# ======================================================================
# 先启动一次只读自校准（最小会话）交叉校验。缩放档位判定以系统已应用的 DPI 为准
# （HKCU\Control Panel\Desktop\WindowMetrics\AppliedDPI：96=100%, 120=125%, 144=150%），
# 不依赖启动瞬间的窗口尺寸——窗口可能被系统最大化/还原，按高度推断会不稳定（B-4 已见 150%/100%/242% 抖动）。
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
        Invoke-UI1Battery $s ("d$s")
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
Write-Host ("S-UI1 UI SMOKE: pass={0} fail={1} skip={2}" -f $pass, $fail, $skip)
if ($fail -gt 0) { exit 1 }
exit 0
