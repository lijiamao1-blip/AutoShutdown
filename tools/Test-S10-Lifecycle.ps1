<#
AutoShutdown S10 双实例生命周期验收脚本。
只验证单实例和 ACTIVATE/OK，不发送任务或电源命令，不修改注册表。
#>
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $projectRoot 'src\AutoShutdown.App\bin\Release\net8.0-windows\AutoShutdown.App.exe'
$pipeName = 'AutoShutdown.Desktop.Activation.v1'
$main = $null
$second = $null

function Send-ActivateProbe {
    param([int]$TimeoutMilliseconds = 1500)

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.',
        $pipeName,
        [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect($TimeoutMilliseconds)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes("ACTIVATE`n")
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        $buffer = New-Object byte[] 64
        $read = $pipe.Read($buffer, 0, $buffer.Length)
        return [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read).Trim()
    }
    finally {
        $pipe.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Release 程序不存在：$exePath"
}

try {
    $main = Start-Process -FilePath $exePath -PassThru
    Start-Sleep -Milliseconds 1500
    $main.Refresh()
    if ($main.HasExited) {
        throw "主实例提前退出，退出码：$($main.ExitCode)"
    }

    $second = Start-Process -FilePath $exePath -PassThru
    if (-not $second.WaitForExit(5000)) {
        throw '第二实例未在 5 秒内退出。'
    }

    $ack = Send-ActivateProbe
    $main.Refresh()
    if ($main.HasExited) {
        throw '激活后主实例意外退出。'
    }
    if ($ack -ne 'OK') {
        throw "Pipe 响应不是 OK：$ack"
    }

    [pscustomobject]@{
        MainPid = $main.Id
        MainAlive = -not $main.HasExited
        SecondaryPid = $second.Id
        SecondaryExited = $second.HasExited
        SecondaryExitCode = $second.ExitCode
        PipeAck = $ack
    } | Format-List
}
finally {
    if ($second -and -not $second.HasExited) {
        Stop-Process -Id $second.Id -ErrorAction SilentlyContinue
    }

    if ($main) {
        $main.Refresh()
        if (-not $main.HasExited) {
            Stop-Process -Id $main.Id -ErrorAction SilentlyContinue
        }
    }
}
