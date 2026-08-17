#Requires -Version 5.1
# tools/test/Invoke-ASReleaseSmoke.ps1
# S-PKG B-4 release-acceptance smoke: 8 GUI checks against the RC release EXE,
# each in an isolated temp data root (AUTOSHUTDOWN_DATA_ROOT). Never touches real
# user data, a real install, or any power operation. Reads the UI via UIAutomation;
# asserts config semantics against the sandbox config.json. Uses UIA to CLICK
# (Init -> healthy TestMode; nav to settings -> remote-card defaults) — nothing else
# is mutated. TestMode stays on the whole time, so no real shutdown can fire.
#
# Checks:
#   C1 启动与窗口      : window + correct title + version text (fresh state)
#   C2 全新安装 fail-closed + 恢复入口 : 配置不可用 + 初始化安全配置 button, 非 安全测试模式
#   C3 关键分区渲染(首页, 初始化后)    : 创建区/时间模式/电源动作/任务区/调度服务/配置状态
#   C4 初始化 -> 安全测试模式         : header + 服务运行中 + config.json(TestMode=true, 不自启, AllowedActions 受限)
#   C5 远程区默认(设置页)             : 启用远程=关 / 强制TLS=开 / 白名单只读 / PIN未生成 / 未监听 / 按钮齐全
#   C6 损坏配置 fail-closed           : 窗口存活 + 配置不可用 + 配置加载失败, 非 安全测试模式
#   C7 回滚后可启动                   : lifecycle backup->replace->selfcheck->rollback 后启动安装槽 EXE
#   C8 重装后可启动 + 卸载 Keep/Remove: uninstall Keep(保数据) -> reinstall -> 启动 -> uninstall Remove(清备份)
#
# Exit: 0 = all pass (or only explicit SKIPs); 1 = failures.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo root
$releaseExe = Join-Path $root 'artifacts\release\v2.0.0\PKG-fe54711\AutoShutdown-v2.0.0-PKG.fe54711.exe'
$expectedVersion = 'v2.0.0-PKG.fe54711'

if (-not (Test-Path -LiteralPath $releaseExe)) {
    Write-Host "FAIL  release EXE not found: $releaseExe  (B-1 产物未保留在磁盘)"
    exit 1
}

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'SPkg-Lifecycle.ps1')

$pass = 0; $fail = 0; $skip = 0
function Assert-True([string]$Name, [bool]$Cond, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host ("  PASS  {0}" -f $Name) }
    else { $script:fail++; Write-Host ("  FAIL  {0}  {1}" -f $Name, $Detail) }
}
function Note-Skip([string]$Name, [string]$Why) {
    $script:skip++; Write-Host ("  SKIP  {0}  ({1})" -f $Name, $Why)
}

# ---------- UIA helpers ----------
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
function Get-CheckBoxToggle($win, [string]$substring) {
    $el = Find-DescendantLike $win $substring 'CheckBox'
    if (-not $el) { return 'NOTFOUND' }
    try {
        return [string]$el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
    } catch { return 'NOPATTERN' }
}
function Get-NavListItem($win, [string]$needle) {
    $liCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    foreach ($i in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)) {
        if ($i.Current.Name -like "*$needle*") { return $i }
    }
    return $null
}

# ---------- app launch / stop helpers ----------
function Start-ReleaseApp([string]$dataRoot, [string]$exePath = $releaseExe) {
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $env:AUTOSHUTDOWN_DATA_ROOT = $dataRoot
    $proc = Start-Process -FilePath $exePath -PassThru -WorkingDirectory (Split-Path -Parent $exePath)
    $win = $null
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { return @{ Proc = $proc; Win = $null; Reason = 'exited' } }
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
        $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $cond)
        # WPF 先建无名窗口，须等到主窗口标题出现才算就绪
        if ($candidate -and $candidate.Current.Name -eq '电脑自动关机助手') { $win = $candidate; break }
        Start-Sleep -Milliseconds 400
    }
    if (-not $win) {
        $tc = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '电脑自动关机助手')
        $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $tc)
    }
    Start-Sleep -Seconds 3
    return @{ Proc = $proc; Win = $win; Reason = 'ok' }
}
function Stop-ReleaseApp($proc) {
    if ($proc -and (-not $proc.HasExited)) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
        $w = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $w -and -not $proc.HasExited) { Start-Sleep -Milliseconds 200 }
    }
    Start-Sleep -Milliseconds 500   # 确保文件句柄释放（卸载 Keep 需删除安装槽 EXE）
    Remove-Item Env:AUTOSHUTDOWN_DATA_ROOT -ErrorAction SilentlyContinue
}

$smokeSandboxes = New-Object System.Collections.Generic.List[string]
function New-SmokeSandbox([string]$tag) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("as-asmoke-$tag-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $script:smokeSandboxes.Add($dir)
    return $dir
}

Write-Host "== B-4 release-acceptance smoke ($expectedVersion) =="

# ======================================================================
# Session A : fresh install state -> init -> healthy TestMode -> settings
# (C1 C2 C4 C3 C5)
# ======================================================================
$sb = New-SmokeSandbox 'a'
$dataA = Join-Path $sb 'data'
Write-Host "`n-- C1 启动与窗口 (fresh state) --"
$launch = Start-ReleaseApp $dataA
Assert-True 'process alive' (-not $launch.Proc.HasExited) $launch.Reason
Assert-True 'window found' ($null -ne $launch.Win)
if ($launch.Win) {
    $win = $launch.Win
    Assert-True 'window title correct' ($win.Current.Name -eq '电脑自动关机助手') ("got='{0}'" -f $win.Current.Name)
    $verEl = Find-Descendant $win $expectedVersion
    Assert-True 'version text = v2.0.0-PKG.fe54711' ($null -ne $verEl)

    Write-Host "`n-- C2 全新安装 fail-closed + 恢复入口 (fresh, 未初始化) --"
    Assert-True 'fresh: 配置不可用 header' ($null -ne (Find-Descendant $win '配置不可用'))
    Assert-True 'fresh: NOT 安全测试模式' ($null -eq (Find-Descendant $win '安全测试模式'))
    Assert-True 'fresh: config.json NOT auto-written' (-not (Test-Path -LiteralPath (Join-Path $dataA 'config.json')))
    # fresh 启动初始页有时落在任务页 → 强制回首页后再断言「初始化安全配置」入口
    $freshHome = Find-DescendantLike $win '首页' 'Button'
    Assert-True 'fresh: 首页 button(恢复导航) 存在' ($null -ne $freshHome)
    if ($freshHome) {
        try {
            $freshHome.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $initSeen = $false
            $idl = (Get-Date).AddSeconds(8)
            while ((Get-Date) -lt $idl) {
                if (Find-Descendant $win '初始化安全配置') { $initSeen = $true; break }
                Start-Sleep -Milliseconds 400
            }
            Assert-True 'fresh: 初始化安全配置 button(首页, 可恢复)' $initSeen
        } catch {
            Assert-True 'fresh: 首页可点击' $false $_.Exception.Message
        }
    }

    Write-Host "`n-- C4 初始化 -> 安全测试模式 --"
    $initBtn = Find-Descendant $win '初始化安全配置'
    if ($initBtn) {
        $invoke = $null
        try { $invoke = $initBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) } catch { }
        if ($invoke) {
            $invoke.Invoke()
            Start-Sleep -Seconds 3
            Assert-True 'init: 安全测试模式 header' ($null -ne (Find-Descendant $win '安全测试模式'))
            Assert-True 'init: 服务运行中' ($null -ne (Find-Descendant $win '服务运行中'))
            Assert-True 'init: 配置状态卡' ($null -ne (Find-Descendant $win '配置状态'))
            Assert-True 'init: 安全有效' ($null -ne (Find-Descendant $win '安全有效'))
            $cfg = $null
            if (Test-Path -LiteralPath (Join-Path $dataA 'config.json')) {
                $cfg = Get-Content -LiteralPath (Join-Path $dataA 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            }
            Assert-True 'init: config.json written' ($null -ne $cfg)
            if ($cfg) {
                Assert-True 'config: TestMode=true' ($cfg.TestMode -eq $true)
                Assert-True 'config: StartWithWindows=false' ($cfg.StartWithWindows -eq $false)
                $allowed = @($cfg.AllowedActions)
                Assert-True 'config: AllowedActions 受限(关机/重启/睡眠/休眠)' ($allowed.Count -eq 4) ("count={0}" -f $allowed.Count)
            }

            Write-Host "`n-- C3 关键分区渲染 (首页, 初始化后) --"
            Assert-True 'home: 创建定时任务区' ($null -ne (Find-Descendant $win '创建定时任务'))
            Assert-True 'home: 时间模式' ($null -ne (Find-Descendant $win '时间模式'))
            Assert-True 'home: 倒计时(时间模式)' ($null -ne (Find-DescendantLike $win '倒计时' 'RadioButton'))
            Assert-True 'home: 关机(电源动作)' ($null -ne (Find-DescendantLike $win '关机' 'RadioButton'))
            Assert-True 'home: 创建任务 button' ($null -ne (Find-Descendant $win '创建任务'))
            Assert-True 'home: 当前任务区' ($null -ne (Find-Descendant $win '当前任务'))
            Assert-True 'home: 调度服务区' ($null -ne (Find-Descendant $win '调度服务'))
            Assert-True 'home: 运行正常' ($null -ne (Find-Descendant $win '运行正常'))

            Write-Host "`n-- C5 远程区默认 (设置页) --"
            $navSettings = Get-NavListItem $win 'PageKey = settings'
            Assert-True 'settings nav item present' ($null -ne $navSettings)
            if ($navSettings) {
                try { $navSettings.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { }
                Start-Sleep -Seconds 2
                Assert-True 'remote: 局域网远程控制 card' ($null -ne (Find-Descendant $win '局域网远程控制'))
                Assert-True 'remote: 启用远程控制(默认关)' ((Get-CheckBoxToggle $win '启用远程控制') -eq 'Off')
                Assert-True 'remote: 强制 TLS(默认开)' ((Get-CheckBoxToggle $win '强制 TLS') -eq 'On')
                Assert-True 'remote: 白名单-查询状态(只读默认开)' ((Get-CheckBoxToggle $win '允许查询状态') -eq 'On')
                Assert-True 'remote: 白名单-任务清单(只读默认开)' ((Get-CheckBoxToggle $win '允许任务清单') -eq 'On')
                Assert-True 'remote: 白名单-触发关机(高危默认关)' ((Get-CheckBoxToggle $win '允许远程触发关机') -eq 'Off')
                Assert-True 'remote: 白名单-取消关机(高危默认关)' ((Get-CheckBoxToggle $win '允许远程取消关机') -eq 'Off')
                Assert-True 'remote: 当前 PIN 未生成' ($null -ne (Find-DescendantLike $win '当前 PIN：未生成'))
                Assert-True 'remote: 暂无已配对设备' ($null -ne (Find-Descendant $win '暂无已配对设备'))
                Assert-True 'remote: 未监听/默认安全' ($null -ne (Find-DescendantLike $win '当前状态：未监听'))
                Assert-True 'remote: 轮换 PIN button' ($null -ne (Find-Descendant $win '轮换 PIN'))
                Assert-True 'remote: 保存并应用 button' ($null -ne (Find-Descendant $win '保存并应用'))
            }
        } else { Note-Skip 'C4/C3/C5' '初始化按钮无 InvokePattern' }
    } else { Note-Skip 'C4/C3/C5' 'fresh 态找不到初始化按钮' }
} else {
    Assert-True 'window found' $false 'app 未弹出窗口或已退出'
}
Stop-ReleaseApp $launch.Proc

# ======================================================================
# Session B : corrupt config -> fail-closed (C6)
# ======================================================================
Write-Host "`n-- C6 损坏配置 fail-closed --"
$sb2 = New-SmokeSandbox 'b'
$dataB = Join-Path $sb2 'data'
New-Item -ItemType Directory -Force -Path $dataB | Out-Null
[System.IO.File]::WriteAllText((Join-Path $dataB 'config.json'), '{"SchemaVersion": 1, "TestMode": ', [System.Text.Encoding]::UTF8)
$launchB = Start-ReleaseApp $dataB
Assert-True 'corrupt: 进程存活(不崩溃)' (-not $launchB.Proc.HasExited) $launchB.Reason
if ($launchB.Win) {
    Assert-True 'corrupt: 配置不可用 header' ($null -ne (Find-Descendant $launchB.Win '配置不可用'))
    Assert-True 'corrupt: 配置加载失败，请检查设置' ($null -ne (Find-Descendant $launchB.Win '配置加载失败，请检查设置'))
    Assert-True 'corrupt: NOT 安全测试模式' ($null -eq (Find-Descendant $launchB.Win '安全测试模式'))
    Assert-True 'corrupt: 版本文本仍正确' ($null -ne (Find-Descendant $launchB.Win $expectedVersion))
    # 恢复路径：顶栏「首页」→ 首页的「初始化安全配置」按钮（损坏配置不静默回退，需人工重建安全配置）
    $homeBtn = Find-DescendantLike $launchB.Win '首页' 'Button'
    Assert-True 'corrupt: 首页 button(恢复导航) 存在' ($null -ne $homeBtn)
    if ($homeBtn) {
        try {
            $homeBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            $recovered = $false
            $rdl = (Get-Date).AddSeconds(8)
            while ((Get-Date) -lt $rdl) {
                if (Find-Descendant $launchB.Win '初始化安全配置') { $recovered = $true; break }
                Start-Sleep -Milliseconds 400
            }
            Assert-True 'corrupt: 首页后出现 初始化安全配置(可恢复)' $recovered
        } catch {
            Assert-True 'corrupt: 首页可点击' $false $_.Exception.Message
        }
    }
} else {
    Assert-True 'corrupt window found' $false 'app 崩溃或无窗口'
}
Stop-ReleaseApp $launchB.Proc

# ======================================================================
# Session C : lifecycle rollback -> launchable (C7) ; uninstall/reinstall (C8)
# ======================================================================
Write-Host "`n-- C7 回滚后可启动 --"
$sb3 = New-SmokeSandbox 'c'
$dataC = Join-Path $sb3 'data'
$installC = Join-Path $sb3 'install'
$candC = Join-Path $sb3 'cand'
New-Item -ItemType Directory -Force -Path $dataC, $installC, $candC | Out-Null
$v1Name = 'AutoShutdown-v1.0.0-S13.4289bc1.exe'   # 前版槽位：释放包二进制副本（S13 EXE 早于数据根覆盖，不直接启动）
$v2Name = 'AutoShutdown-v2.0.0-PKG.fe54711.exe'
$releaseDir = Split-Path -Parent $releaseExe
# 安装槽/候选槽都放「完整发布目录」：单文件 EXE 需同目录原生 WPF 依赖（wpfgfx/PenImc/D3DCompiler 等），
# 与生命周期只替换版本化 EXE、不动基础文件的生产模型一致。
New-Item -ItemType Directory -Force -Path $installC, $candC | Out-Null
Get-ChildItem -LiteralPath $releaseDir -Force | Copy-Item -Destination $installC -Recurse -Force
Get-ChildItem -LiteralPath $releaseDir -Force | Copy-Item -Destination $candC -Recurse -Force
# 安装槽模拟「旧版已安装」：移除候选 EXE，放入 V1 槽位 EXE
Remove-Item -LiteralPath (Join-Path $installC $v2Name) -Force
[System.IO.File]::Copy($releaseExe, (Join-Path $installC $v1Name), $true)
# D1：安装槽声明所有权（所有权清单 = 槽内全部相对路径），非空目录门禁才放行替换/回滚/卸载。
Write-ASOwnerMarker -InstallDir $installC -AppFiles (Get-ASRelFileList -BaseDir $installC) -CandidateName $v2Name
$goodConfig  = @{ SchemaVersion = 1; TestMode = $true } | ConvertTo-Json
$goodTasks   = @{ SchemaVersion = 2; Tasks = @() } | ConvertTo-Json -Depth 4
$goodRuntime = @{ SchemaVersion = 2; Instances = @{} } | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $dataC 'config.json'), $goodConfig, [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $dataC 'tasks.json'), $goodTasks, [System.Text.UTF8Encoding]::new($true))
[System.IO.File]::WriteAllText((Join-Path $dataC 'runtime.json'), $goodRuntime, [System.Text.UTF8Encoding]::new($true))

Backup-ASDataRoot -Root $dataC -Tag 'smoke' | Out-Null
Replace-ASBinary -CandidateDir $candC -InstallDir $installC -Root $dataC -Tag 'smoke' | Out-Null
$sc = Test-ASSelfCheck -InstallDir $installC -Root $dataC -ExpectedVersion 'PKG.fe54711'
Assert-True 'C7: lifecycle 自检通过' $sc.Ok
Restore-ASRollback -InstallDir $installC -Root $dataC -Tag 'smoke' | Out-Null
Assert-True 'C7: 回滚恢复了 V1 槽位 EXE' (Test-Path -LiteralPath (Join-Path $installC $v1Name))
$rollbackHash = (Get-FileHash -LiteralPath (Join-Path $installC $v1Name) -Algorithm SHA256).Hash
$releaseHash  = (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash
Assert-True 'C7: 回滚后二进制与释放包一致' ($rollbackHash -eq $releaseHash)
$launchC = Start-ReleaseApp $dataC (Join-Path $installC $v1Name)
Assert-True 'C7: 回滚后 EXE 可启动' ($null -ne $launchC.Win)
if ($launchC.Win) {
    Assert-True 'C7: 回滚后版本文本正确' ($null -ne (Find-Descendant $launchC.Win $expectedVersion))
}
Stop-ReleaseApp $launchC.Proc

Write-Host "`n-- C8 卸载 Keep(保数据) + 重装后可启动 + 卸载 Remove --"
Invoke-ASUninstall -InstallDir $installC -Root $dataC -UserData 'Keep' | Out-Null
Assert-True 'C8: 卸载 Keep 移除安装文件' (-not (Test-Path -LiteralPath (Join-Path $installC $v1Name)))
Assert-True 'C8: 卸载 Keep 保留用户数据(config)' (Test-Path -LiteralPath (Join-Path $dataC 'config.json'))
Invoke-ASReinstall -CandidateDir $candC -InstallDir $installC -Root $dataC | Out-Null
Assert-True 'C8: 重装后候选 EXE 就位' (Test-Path -LiteralPath (Join-Path $installC $v2Name))
$launchD = Start-ReleaseApp $dataC (Join-Path $installC $v2Name)
Assert-True 'C8: 重装后 EXE 可启动' ($null -ne $launchD.Win)
if ($launchD.Win) {
    Assert-True 'C8: 重装后版本文本正确' ($null -ne (Find-Descendant $launchD.Win $expectedVersion))
}
Stop-ReleaseApp $launchD.Proc
Invoke-ASUninstall -InstallDir $installC -Root $dataC -UserData 'Remove' | Out-Null
Assert-True 'C8: 卸载 Remove 清理 spkg 备份' (-not (Test-Path -LiteralPath (Join-Path $dataC 'backups\spkg')))

# ======================================================================
# cleanup
# ======================================================================
foreach ($d in $smokeSandboxes) {
    Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item Env:AUTOSHUTDOWN_DATA_ROOT -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("RELEASE SMOKE: pass={0} fail={1} skip={2}" -f $pass, $fail, $skip)
if ($fail -gt 0) { exit 1 }
exit 0
