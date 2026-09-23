param([switch]$Build)

# ============================================================
#  UI 预览启动器（开发环境专用）
#  由 tools\启动UI预览.cmd（-Build）或 tools\仅打开现有UI.cmd 调用。
#  安全边界：
#   - 不修改任何 C#/XAML/项目文件；
#   - 不写注册表、不设置开机自启、不使用管理员权限；
#   - 不结束任何正在运行的进程；
#   - 不删除 bin/obj、不运行测试；
#   - 启动遵守现有单实例与 Named Pipe 机制（只会唤起已有窗口）；
#   - 不执行真实电源操作（软件仍为安全测试模式 FakePowerService）。
# ============================================================

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sdk = $env:AUTOSHUTDOWN_DOTNET
$exe = Join-Path $root 'src\AutoShutdown.App\bin\Release\net8.0-windows\AutoShutdown.App.exe'

if ($Build) {
    # ---------- 启动UI预览：先构建再启动 ----------

    $dotnet = $null
    if ($sdk -and (Test-Path -LiteralPath $sdk)) {
        $dotnet = $sdk
    }
    elseif (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $dotnet = 'dotnet'
    }

    if (-not $dotnet) {
        Write-Host '未找到可用的 .NET SDK。'
        Write-Host '系统 PATH 中也未找到 dotnet 命令。'
        Write-Host '请先安装 .NET SDK，或通过 AUTOSHUTDOWN_DOTNET 指定 SDK 路径。'
        Read-Host '按回车键退出'
        exit 1
    }

    Write-Host '正在构建（Release）...'
    Push-Location $root
    try {
        & $dotnet build AutoShutdown.sln -c Release --nologo
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet build failed'
        }
    }
    catch {
        Write-Host ''
        Write-Host '构建失败，未启动软件'
        Write-Host '请检查上方错误输出后重试。'
        Read-Host '按回车键退出'
        exit 1
    }
    finally {
        Pop-Location
    }
}
else {
    # ---------- 仅打开现有UI：不构建 ----------

    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Host '尚未找到可运行的 UI，请先运行“启动UI预览.cmd”完成构建。'
        Read-Host '按回车键退出'
        exit 1
    }

    Write-Host '打开现有 UI 预览...'
    Start-Process -FilePath $exe
    Write-Host '已打开（若已有实例，将唤起原窗口，不会启动第二个实例）。'
    exit 0
}

# ---------- 构建成功后的公共启动路径 ----------

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host '构建成功，但未找到目标程序：'
    Write-Host $exe
    Read-Host '按回车键退出'
    exit 1
}

Write-Host '启动 UI 预览...'
Start-Process -FilePath $exe
Write-Host '已启动（若已有实例，将唤起原窗口，不会启动第二个实例）。'
exit 0
