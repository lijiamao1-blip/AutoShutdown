[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$BuildOutputRoot
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = if ($RepositoryRoot) { [IO.Path]::GetFullPath($RepositoryRoot) } else { [IO.Path]::GetFullPath((Join-Path $scriptRoot '..')) }
$logRoot = Join-Path $root '.build-tmp\S-UI3-logs'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$logPath = Join-Path $logRoot ('start-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date))

function Write-Status([string]$text) {
    Write-Host $text
    Add-Content -LiteralPath $logPath -Value ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $text) -Encoding UTF8
}

function Fail([string]$message, [int]$code) {
    Write-Status ('启动失败：' + $message)
    Write-Status ('日志路径：' + $logPath)
    exit $code
}

try {
    Write-Status '正在准备 AutoShutdown 实时 UI 安全测试界面……'
    if (-not (Test-Path -LiteralPath (Join-Path $root 'AutoShutdown.sln'))) { Fail '无法确认工作区根目录。' 10 }
    $head = (& git -C $root rev-parse HEAD 2>&1).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { Fail '无法读取真实 Git HEAD。' 11 }
    Write-Status ('工作区：' + $root)
    Write-Status ('Git HEAD：' + $head)

    $existing = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'AutoShutdown*' })
    if ($existing.Count -gt 0) { Fail '检测到已有 AutoShutdown 实例。请先从托盘正常退出；本入口不会自动关闭它。' 12 }

    $sandbox = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AutoShutdown\UiTestSandbox'))
    $formal = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AutoShutdown'))
    if ($sandbox -eq $formal -or $sandbox.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { Fail '测试数据目录隔离检查失败。' 13 }
    if (Test-Path -LiteralPath $sandbox) {
        if ((Get-Item -LiteralPath $sandbox -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { Fail '测试数据目录是 reparse point。' 14 }
    } else {
        New-Item -ItemType Directory -Path $sandbox | Out-Null
    }

    $configPath = Join-Path $sandbox 'config.json'
    if (-not (Test-Path -LiteralPath $configPath)) {
        $safe = [ordered]@{
            SchemaVersion = 1; TestMode = $true; RealPowerEnabled = $false; DefaultWarningSeconds = 60
            DefaultSnoozeSeconds = 300; StartWithWindows = $false
            AllowedActions = @('Shutdown','Restart','Sleep','Hibernate'); MinimizeToTrayOnClose = $true
            Logging = @{ Level = 'Information'; RetentionDays = 14 }
            CloseApps = @{ GracefulTimeoutSeconds = 30; Targets = @() }
            RunCommands = @{ DefaultTimeoutSeconds = 30; Whitelist = @{ Allow = @() }; Commands = @() }
        }
        $safe | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding UTF8
        Write-Status '已创建首次使用的安全测试配置。'
    }

    try { $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -ErrorAction Stop } catch { Fail '配置文件损坏，拒绝启动。' 15 }
    if ($config.TestMode -ne $true -or $config.RealPowerEnabled -ne $false -or $config.StartWithWindows -ne $false) { Fail '配置未保持 TestMode=true 和真实电源关闭。' 16 }
    if (@($config.RunCommands.Commands).Count -ne 0 -or @($config.RunCommands.Whitelist.Allow).Count -ne 0) { Fail 'RunCommands 不是空配置。' 17 }
    if (@($config.CloseApps.Targets).Count -ne 0) { Fail 'CloseApps 目标或强制关闭授权不为空。' 18 }
    foreach ($name in @('unattended.json','remote-devices.json','remote-pairing-lock.json','remote-server-cert.dpapi','remote-imported-cert-password.dpapi')) {
        if (Test-Path -LiteralPath (Join-Path $sandbox $name)) { Fail ('检测到禁止的授权、证书或配对数据：' + $name) 19 }
    }
    foreach ($name in @('task-sync.json','remote-settings.json')) {
        $path = Join-Path $sandbox $name
        if (Test-Path -LiteralPath $path) {
            try { $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -ErrorAction Stop } catch { Fail ($name + ' 损坏。') 20 }
            if ($doc.Enabled -eq $true) { Fail ($name + ' 已启用。') 21 }
        }
    }

    $out = if ($BuildOutputRoot) { [IO.Path]::GetFullPath($BuildOutputRoot) } else { Join-Path $root ('.build-tmp\S-UI3-' + $head.Substring(0,7)) }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Write-Status '正在构建当前源码的 Release 版本，请稍候……'
    & dotnet build (Join-Path $root 'src\AutoShutdown.App\AutoShutdown.App.csproj') -c Release --nologo -p:OutputPath=$out 2>&1 | Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) { Fail 'Release 构建失败，不会启动任何旧程序。' 30 }
    $exe = Join-Path $out 'AutoShutdown.App.exe'
    if (-not (Test-Path -LiteralPath $exe)) { Fail '当前构建未产生预期程序，不会回退到旧版本。' 31 }

    $env:AUTOSHUTDOWN_DATA_ROOT = $sandbox
    $env:AUTOSHUTDOWN_UI_TEST = '1'
    $env:AUTOSHUTDOWN_BUILD_COMMIT = $head
    Write-Status '构建成功，正在打开安全测试模式界面……'
    Write-Status '安全测试模式——不会执行真实系统电源操作'
    Start-Process -FilePath $exe -WorkingDirectory $out
    exit 0
} catch {
    Fail $_.Exception.Message 99
}
