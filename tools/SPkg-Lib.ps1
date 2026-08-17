# SPkg-Lib.ps1 — S-PKG 阶段共享工具函数（仅供 S-PKG 发布工具 dot-source）
# 只读 / 纯计算：不做任何注册表、防火墙、任务计划、电源或破坏性写操作。
# 本文件由 tools/Publish-ReleaseCandidate.ps1、tools/Invoke-Upgrade.ps1 等 dot-source。

function Get-ASDotNet {
    # 项目已知 SDK 优先，回退 PATH。返回 dotnet.exe 全路径字符串；找不到返回 $null。
    $knownSdk = 'C:\Users\李佳茂\Documents\Codex\2026-08-10\new-chat-5\work\.dotnet-sdk\dotnet.exe'
    if (Test-Path -LiteralPath $knownSdk) {
        return $knownSdk
    }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Assert-AllowedPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )
    # 只允许在 $Root 之下操作；越界即退出（与既有 S12.4 脚本一致）。
    $full = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($Root)
    $rootPrefix = if ($rootFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $rootFull } else { $rootFull + [System.IO.Path]::DirectorySeparatorChar }
    if (-not $full.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "路径超出允许范围：$full（根：$rootFull）"
    }
    return $full
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-GitHeadInfo {
    # 返回 @{ Short; Full; Date; Subject }；非 git 仓库或失败时返回 $null。
    $root = Split-Path -Parent $PSScriptRoot
    Push-Location $root
    try {
        $full = (& git rev-parse HEAD 2>$null)
        if (-not $full) { return $null }
        $short = (& git rev-parse --short HEAD 2>$null)
        $date = (& git show -s --format=%cI HEAD 2>$null)
        $subject = (& git show -s --format=%s HEAD 2>$null)
        return @{ Short = $short; Full = $full; Date = $date; Subject = $subject }
    }
    finally { Pop-Location }
}

function Test-ForbiddenStageFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Label
    )
    # 发布包禁止携带调试/测试/诊断/中间文件。
    $hits = Get-ChildItem -LiteralPath $Stage -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
        $n = $_.Name
        $n -like '*.pdb' -or $n -like '*.trx' -or $n -like '*.dmp' -or $n -like '*.diag' -or
        $n -like '*testhost*' -or $n -like '*xunit*' -or $n -like '*TestResults*' -or
        $n -like 's12-*' -or $n -like 's13-*' -or $n -like '*.log' -or $n -like '*.out'
    }
    if ($hits) {
        Write-Error ("禁止文件命中 [$Label]：`n" + (($hits | ForEach-Object { $_.FullName }) -join "`n"))
        return $null
    }
    return 0
}

function Test-TextLeak {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Label
    )
    # 只检查文本类文件；绝不读取二进制 DLL。
    $badText = @('D:\电脑定时关机重建完整版', 'C:\Users\李佳茂', 'new-chat-5', '\.dotnet-sdk\', 'Codex')
    $files = Get-ChildItem -LiteralPath $Stage -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Extension -in @('.json', '.config', '.txt', '.md', '.cmd', '.ps1')
    }
    foreach ($f in $files) {
        $content = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $content) { continue }
        foreach ($b in $badText) {
            if ($content.IndexOf($b, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                Write-Error ("文本泄露 [$b] 于 $($f.FullName) [$Label]")
                return $false
            }
        }
    }
    return $true
}
