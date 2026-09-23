# SPkg-Lib.ps1 — S-PKG 阶段共享工具函数（仅供 S-PKG 发布工具 dot-source）
# 只读 / 纯计算：不做任何注册表、防火墙、任务计划、电源或破坏性写操作。
# 本文件由 tools/Publish-ReleaseCandidate.ps1、tools/Invoke-Upgrade.ps1 等 dot-source。

function Get-ASDotNet {
    # 返回一个「确实能为本仓库解析出 SDK」的 dotnet.exe 全路径；找不到返回 $null。
    #
    # 修复（S-PKG-D1）：原实现把一条机器专用的硬编码 SDK 路径放在最前，只要该路径存在
    # 就无条件返回，从不验证它能否满足仓库的 global.json —— 注释写着「回退 PATH」，
    # 但代码只在路径不存在时才回退，正好漏掉「路径还在、SDK 却不合用」这一种情况。
    # 仓库在 6c80c8c 加入 global.json（限定 8.0.1xx 特征带）后，那个目录里只剩 8.0.423
    # （4xx 带），于是脚本里每一句 dotnet 调用都以
    # "A compatible .NET SDK was not found" 失败，构建在第 3 步即 exit 20 退出。
    # 该硬编码路径同时出现在本文件 Test-TextLeak 的禁止泄露清单里（'\.dotnet-sdk\'、
    # 'Codex'、用户目录），说明它本就不该固化进仓库，因此这里一并移除。
    #
    # 新策略：逐个候选在仓库根目录实际执行 `dotnet --version`，退出码为 0 才采用。
    # 这样无论 global.json 将来怎么写都能自适应，也不再绑定任何一台机器的特定目录。
    # 需要指定某个 SDK 时，用环境变量 AUTOSHUTDOWN_DOTNET 覆盖，而不是改脚本。
    $candidates = @()
    if ($env:AUTOSHUTDOWN_DOTNET) { $candidates += $env:AUTOSHUTDOWN_DOTNET }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += 'C:\Program Files\dotnet\dotnet.exe'

    $repoRoot = Split-Path -Parent $PSScriptRoot
    foreach ($candidate in ($candidates | Where-Object { $_ } | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $usable = $false
        Push-Location $repoRoot
        try {
            $global:LASTEXITCODE = 0
            $null = & $candidate --version 2>$null
            $usable = ($LASTEXITCODE -eq 0)
        }
        catch {
            $usable = $false
        }
        finally {
            Pop-Location
        }
        if ($usable) { return $candidate }
    }

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
    $badText = @('D:\电脑定时关机重建完整版', 'C:\Users\', 'new-chat-5', '\.dotnet-sdk\', 'Codex')
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
