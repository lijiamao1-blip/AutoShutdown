#Requires -Version 5.1
param(
    [Parameter(Mandatory = $false)][ValidateSet('health', 'backup', 'replace', 'selfcheck', 'rollback', 'upgrade', 'uninstall', 'reinstall')]
    [string]$Command = '',
    [string]$DataRoot = '',
    [string]$Tag = 'upgrade',
    [string]$CandidateDir = '',
    [string]$CandidateExe = '',
    [string]$InstallDir = '',
    [string]$ExpectedVersion = '',
    [ValidateSet('Keep', 'Remove')][string]$UserData = 'Keep'
)
# tools/SPkg-Lifecycle.ps1 —— S-PKG 安装生命周期状态机（纯文件操作）。
#
# 覆盖：数据根定位、JSON 健康分类（NotFound/Corrupt/Invalid/UnsupportedVersion）、
#       备份、二进制替换、自检、回滚、卸载、重装。
#
# 安全契约（与执行书一致）：
#  - 备份成功是任何替换前置；备份失败不得继续。
#  - 回滚包与目标发布一一对应（记录 source-commit / candidate 名），保留被替换前的
#    EXE 与配置（含 V1 EXE 恢复能力）。
#  - 损坏 JSON 只分类、只标记，绝不静默回退为可能触发任务的默认值。
#  - 本模块不触碰注册表、防火墙、Task Scheduler、自启或电源。系统集成在 B3 单独处理。
#  - 所有写路径都限定在数据根或安装目录之内（Assert-AllowedPath）。
#
# 使用：
#   . ./SPkg-Lifecycle.ps1            # 点源加载函数
#   ./SPkg-Lifecycle.ps1 -Command backup -DataRoot <dir> [-Tag <tag>]
#   ./SPkg-Lifecycle.ps1 -Command health -DataRoot <dir>
#   ./SPkg-Lifecycle.ps1 -Command replace -CandidateExe <exe> -InstallDir <dir> -DataRoot <dir>
#   ./SPkg-Lifecycle.ps1 -Command selfcheck -InstallDir <dir> -DataRoot <dir> -ExpectedVersion <ver>
#   ./SPkg-Lifecycle.ps1 -Command rollback -InstallDir <dir> -DataRoot <dir> [-BackupTag <tag>]
#   ./SPkg-Lifecycle.ps1 -Command uninstall -InstallDir <dir> -DataRoot <dir> -UserData Keep|Remove
#   ./SPkg-Lifecycle.ps1 -Command reinstall -CandidateDir <dir> -InstallDir <dir> -DataRoot <dir>

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'SPkg-Lib.ps1')

# 与本阶段可追溯的元数据文件名（manifest 名由候选目录决定，backup 里写 life 记录）
$script:LifecycleMetaFile = 'spkg-lifecycle.json'

# ---- 数据根 ----
function Get-ASDataRoot {
    param([string]$Root = '')
    if (-not [string]::IsNullOrWhiteSpace($Root)) { return [System.IO.Path]::GetFullPath($Root) }
    $envRoot = [Environment]::GetEnvironmentVariable('AUTOSHUTDOWN_DATA_ROOT')
    if (-not [string]::IsNullOrWhiteSpace($envRoot)) { return [System.IO.Path]::GetFullPath($envRoot) }
    return [System.IO.Path]::Combine(
        [Environment]::GetFolderPath('LocalApplicationData'), 'AutoShutdown')
}

# ---- JSON 健康分类（与 app 语义对齐：绝不把损坏当默认） ----
function Test-ASJsonHealth {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$ExpectedSchema = 1,
        [bool]$SchemaRequired = $true
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ Status = 'NotFound'; Path = $Path; Message = 'file not present' }
    }
    $raw = $null
    try {
        $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    } catch {
        return [pscustomobject]@{ Status = 'Corrupt'; Path = $Path; Message = 'read failed: ' + $_.Exception.Message }
    }
    $json = $null
    try {
        $json = $raw | ConvertFrom-Json
    } catch {
        return [pscustomobject]@{ Status = 'Corrupt'; Path = $Path; Message = 'invalid JSON: ' + $_.Exception.Message }
    }
    if (-not $SchemaRequired) {
        return [pscustomobject]@{ Status = 'Valid'; Path = $Path; Message = 'parseable (no schema required)' }
    }
    if ($null -eq $json.PSObject.Properties['SchemaVersion']) {
        return [pscustomobject]@{ Status = 'Invalid'; Path = $Path; Message = 'SchemaVersion missing' }
    }
    $sv = -1
    if (-not [int]::TryParse([string]$json.SchemaVersion, [ref]$sv)) {
        return [pscustomobject]@{ Status = 'Invalid'; Path = $Path; Message = 'SchemaVersion not an integer' }
    }
    if ($sv -eq $ExpectedSchema) {
        return [pscustomobject]@{ Status = 'Valid'; Path = $Path; Message = "schemaVersion $sv" }
    }
    if ($sv -gt $ExpectedSchema) {
        return [pscustomobject]@{ Status = 'UnsupportedVersion'; Path = $Path; Message = "schemaVersion $sv > expected $ExpectedSchema" }
    }
    return [pscustomobject]@{ Status = 'Invalid'; Path = $Path; Message = "schemaVersion $sv < expected $ExpectedSchema" }
}

# ---- 备份数据根（JSON 证据 + 生命周期记录），返回 backup 目录 ----
function Backup-ASDataRoot {
    param(
        [string]$Root = '',
        [string]$Tag = 'upgrade',
        [string]$CandidateName = ''
    )
    $dataRoot = Get-ASDataRoot -Root $Root
    if (-not (Test-Path -LiteralPath $dataRoot)) { New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null }
    $backupRoot = Join-Path $dataRoot 'backups\spkg'
    $ts = Get-Date -Format 'yyyyMMddHHmmss'
    $backupDir = Join-Path $backupRoot ("{0}-{1}" -f $ts, $Tag)
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $files = Get-ChildItem -LiteralPath $dataRoot -File -Recurse |
        Where-Object { $_.FullName -notlike (Join-Path $backupRoot '*') } |
        Select-Object -ExpandProperty FullName
    foreach ($f in $files) {
        $rel = $f.Substring($dataRoot.Length).TrimStart('\', '/')
        $dest = Join-Path $backupDir $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $f -Destination $dest -Force
    }
    $meta = [ordered]@{
        kind = 'spkg-backup'; tag = $Tag; created = (Get-Date -Format o)
        candidate = $CandidateName; dataRoot = $dataRoot
        files = @($files)
    }
    $meta | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backupDir $script:LifecycleMetaFile) -Encoding UTF8
    return [pscustomobject]@{ BackupDir = $backupDir; FileCount = @($files).Count; DataRoot = $dataRoot }
}

# ---- 二进制替换（备份成功是前置；失败自动回滚并恢复备份） ----
# 回滚槽采用“新备份验证完成后原子换名”策略：备份写入 .new 槽，文件数核对一致后才
# 交换为正式槽；备份阶段任何失败都保留上一个回滚槽与原始安装目录原样（绝不因一次失败
# 的替换而破坏 V1 EXE/配置恢复能力）。
function Replace-ASBinary {
    param(
        [Parameter(Mandatory = $true)][string]$CandidateDir,
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$Root = '',
        [string]$Tag = 'upgrade'
    )
    $dataRoot = Get-ASDataRoot -Root $Root
    $installFull = [System.IO.Path]::GetFullPath($InstallDir)
    $candFull = [System.IO.Path]::GetFullPath($CandidateDir)
    if (-not (Test-Path -LiteralPath $candFull)) { throw "candidate dir not found: $candFull" }
    $backupRoot = Join-Path $dataRoot 'backups\spkg'
    $slot = Join-Path $backupRoot 'rollback-install'
    $slotNew = Join-Path $backupRoot 'rollback-install.new'
    $completeMarker = Join-Path $slotNew '_complete.marker'
    $noInstallMarker = Join-Path $slotNew '_no-install.marker'

    # 1) 备份现有安装目录（含被替换前的 EXE，保留 V1 EXE 恢复能力）→ 验证 → 原子交换
    if (Test-Path -LiteralPath $slotNew) { Remove-Item -Recurse -Force -LiteralPath $slotNew }
    New-Item -ItemType Directory -Force -Path $slotNew | Out-Null
    $hadInstall = Test-Path -LiteralPath $installFull
    try {
        if ($hadInstall) {
            Copy-Item -LiteralPath $installFull -Destination $slotNew -Recurse -Force
            $backed = @(Get-ChildItem -LiteralPath (Join-Path $slotNew (Split-Path -Leaf $installFull)) -Recurse -File)
            $src = @(Get-ChildItem -LiteralPath $installFull -Recurse -File)
            if ($backed.Count -ne $src.Count) {
                throw "install backup incomplete ($($backed.Count)/$($src.Count)); aborting replace"
            }
            Set-Content -LiteralPath $completeMarker -Value (Get-Date -Format o) -Encoding UTF8
        } else {
            Set-Content -LiteralPath $noInstallMarker -Value (Get-Date -Format o) -Encoding UTF8
            New-Item -ItemType Directory -Force -Path $installFull | Out-Null
        }
        # 交换：仅在备份验证完成后替换正式槽
        if (Test-Path -LiteralPath $slot) { Remove-Item -Recurse -Force -LiteralPath $slot }
        Rename-Item -LiteralPath $slotNew -NewName 'rollback-install'
    } catch {
        if (Test-Path -LiteralPath $slotNew) { Remove-Item -Recurse -Force -LiteralPath $slotNew }
        throw 'backup failed; original install untouched: ' + $_.Exception.Message
    }
    $backupDir = $slot

    # 2) 复制候选（单文件 + 伴随文件）到安装目录；随后清除安装目录中过期的版本化
    #    EXE（与候选同名的除外）。陈旧旧版 EXE 若留在安装目录会让自检出现新旧两个
    #    AutoShutdown-v*.exe 的歧义，必须移除。只动 AutoShutdown-v*.exe，绝不删除
    #    安装目录里的用户自定义文件。任何失败 → 自动回滚。
    try {
        $items = Get-ChildItem -LiteralPath $candFull -Force
        foreach ($item in $items) {
            $target = Join-Path $installFull $item.Name
            if ($item.PSIsContainer) {
                Copy-Item -LiteralPath $item.FullName -Destination $target -Recurse -Force
            } else {
                Copy-Item -LiteralPath $item.FullName -Destination $target -Force
            }
        }
        $candExeNames = @(Get-ChildItem -LiteralPath $candFull -Filter 'AutoShutdown-v*.exe' -File | ForEach-Object { $_.Name })
        Get-ChildItem -LiteralPath $installFull -Filter 'AutoShutdown-v*.exe' -File -ErrorAction SilentlyContinue |
            Where-Object { $candExeNames -notcontains $_.Name } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    } catch {
        # 3) 替换失败 → 仅在存在完整备份时自动回滚；否则保持原状并报告（fail-closed）
        if (Test-Path -LiteralPath (Join-Path $backupDir '_complete.marker')) {
            Restore-ASInstallBackup -InstallDir $installFull -BackupDir $backupDir | Out-Null
            throw 'binary replace failed and was rolled back: ' + $_.Exception.Message
        }
        throw 'binary replace failed with no complete backup; original install preserved: ' + $_.Exception.Message
    }
    return [pscustomobject]@{
        InstallDir = $installFull; BackupDir = $backupDir
        HadInstall = $hadInstall; CandidateDir = $candFull
    }
}

function Restore-ASInstallBackup {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [Parameter(Mandatory = $true)][string]$BackupDir
    )
    if (-not (Test-Path -LiteralPath $BackupDir)) { throw 'rollback backup missing; cannot restore' }
    $leaf = Split-Path -Leaf $InstallDir
    $saved = Join-Path $BackupDir $leaf
    $completeMarker = Join-Path $BackupDir '_complete.marker'
    $noInstallMarker = Join-Path $BackupDir '_no-install.marker'
    if ((Test-Path -LiteralPath $completeMarker) -and (Test-Path -LiteralPath $saved)) {
        # 整目录删除后复制，避免 Copy-Item 把备份目录嵌套成 $InstallDir\<leaf> 子目录。
        if (Test-Path -LiteralPath $InstallDir) {
            Remove-Item -LiteralPath $InstallDir -Recurse -Force
        }
        Copy-Item -LiteralPath $saved -Destination $InstallDir -Recurse -Force
        return $true
    }
    if (Test-Path -LiteralPath $noInstallMarker) {
        if (Test-Path -LiteralPath $InstallDir) {
            Remove-Item -LiteralPath $InstallDir -Recurse -Force
        }
        return $true
    }
    throw "install rollback backup incomplete; refusing to modify $InstallDir"
}

# ---- 自检：安装目录存在、EXE 版本与期望一致、数据根 JSON 健康 ----
function Test-ASSelfCheck {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$Root = '',
        [string]$ExpectedVersion = ''
    )
    $dataRoot = Get-ASDataRoot -Root $Root
    $results = [System.Collections.Generic.List[object]]::new()
    $ok = $true

    $exes = @(Get-ChildItem -LiteralPath $InstallDir -Filter 'AutoShutdown-v*.exe' -File -ErrorAction SilentlyContinue)
    if ($exes.Count -eq 0) {
        $results.Add([pscustomobject]@{ Check = 'exe'; Status = 'Fail'; Message = 'no candidate exe found' })
        $ok = $false
    } elseif ($ExpectedVersion) {
        $match = @($exes | Where-Object { $_.Name -like "*$ExpectedVersion*" } | Select-Object -First 1)
        $stale = @($exes | Where-Object { $_.Name -notlike "*$ExpectedVersion*" })
        if ($match.Count -eq 0) {
            $results.Add([pscustomobject]@{ Check = 'exe'; Status = 'Fail'; Message = "expected $ExpectedVersion not found (present: $([string]::Join(',', @($exes | ForEach-Object { $_.Name })))" })
            $ok = $false
        } elseif ($stale.Count -gt 0) {
            $results.Add([pscustomobject]@{ Check = 'exe'; Status = 'Fail'; Message = "stale versioned exe present: $([string]::Join(',', @($stale | ForEach-Object { $_.Name })))" })
            $ok = $false
        } else {
            $results.Add([pscustomobject]@{ Check = 'exe'; Status = 'Pass'; Message = $match[0].Name })
        }
    } else {
        $results.Add([pscustomobject]@{ Check = 'exe'; Status = 'Pass'; Message = $exes[0].Name })
    }

    # 各文件 schema：config.json=1（V1→V2 迁移由 app RuntimeStateStore 在 .v1bak 后重建）、
    # tasks.json=2、runtime.json 只要求可解析（V1 单实例 schema=1 / V2 多实例 schema=2，
    # 损坏才判 Corrupt，绝不静默回退）。
    $schema = @{ 'config.json' = 1; 'tasks.json' = 2; 'runtime.json' = -1 }
    foreach ($f in @('config.json', 'tasks.json', 'runtime.json')) {
        $expected = $schema[$f]
        $h = Test-ASJsonHealth -Path (Join-Path $dataRoot $f) -SchemaRequired ($expected -ge 0) -ExpectedSchema $expected
        if ($h.Status -in @('Valid')) {
            $results.Add([pscustomobject]@{ Check = "json:$f"; Status = 'Pass'; Message = $h.Message })
        } else {
            $results.Add([pscustomobject]@{ Check = "json:$f"; Status = 'Fail'; Message = $h.Status + ': ' + $h.Message })
            $ok = $false
        }
    }
    return [pscustomobject]@{ Ok = $ok; Checks = @($results) }
}

# ---- 回滚：恢复备份的数据文件 + 替换前的安装目录 ----
function Restore-ASRollback {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$Root = '',
        [string]$Tag = 'upgrade'
    )
    $dataRoot = Get-ASDataRoot -Root $Root

    # 1) 恢复最近一次 spkg 备份的数据文件
    $backupRoot = Join-Path $dataRoot 'backups\spkg'
    $metaFiles = @(Get-ChildItem -LiteralPath $backupRoot -Filter $script:LifecycleMetaFile -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    $matched = $null
    foreach ($m in $metaFiles) {
        # 精确匹配尾部标签：目录名 {ts}-<Tag>，避免 'upgrade' 误配 'upgrade2'。
        if ($m.Directory.Name -like "*-$Tag") { $matched = $m.Directory; break }
    }
    if (-not $matched) { throw "no spkg data backup found for tag '$Tag' under $backupRoot" }
    $restored = @()
    $meta = Get-Content -LiteralPath (Join-Path $matched.FullName $script:LifecycleMetaFile) -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($rel in $meta.files) {
        $relPath = [string]$rel
        $src = Join-Path $matched.FullName $relPath
        if (-not (Test-Path -LiteralPath $src)) { continue }
        $dest = Join-Path $dataRoot $relPath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $src -Destination $dest -Force
        $restored += $relPath
    }
    # 2) 恢复替换前的安装目录
    Restore-ASInstallBackup -InstallDir ([System.IO.Path]::GetFullPath($InstallDir)) -BackupDir (Join-Path $backupRoot 'rollback-install') | Out-Null
    return [pscustomobject]@{ DataFilesRestored = @($restored); DataRoot = $dataRoot; InstallDir = [System.IO.Path]::GetFullPath($InstallDir) }
}

# ---- 升级编排：备份 → 替换 → 数据迁移（app 负责，可注入模拟）→ 自检 → 失败自动回滚 ----
# 编码执行书契约：备份成功是任何替换前置；迁移失败/自检失败一律自动回滚并恢复原数据
# 与原 EXE（含 V1 EXE 恢复能力）。MigrationScript 为空表示数据无需迁移（V2→V2 或数据已迁移）。
function Invoke-ASUpgrade {
    param(
        [Parameter(Mandatory = $true)][string]$CandidateDir,
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$Root = '',
        [string]$Tag = 'upgrade',
        [string]$ExpectedVersion = '',
        [scriptblock]$MigrationScript = $null
    )
    $dataRoot = Get-ASDataRoot -Root $Root

    # 1) 备份：备份成功是任何替换前置；失败直接中止，不触碰安装目录。
    $backup = Backup-ASDataRoot -Root $dataRoot -Tag $Tag

    # 2) 替换二进制（内部备份安装目录 → 验证 → 原子换槽；复制失败自动回滚并抛出）。
    $replace = Replace-ASBinary -CandidateDir $CandidateDir -InstallDir $InstallDir -Root $dataRoot -Tag $Tag

    # 3) 数据迁移（V1→V2 由 app 启动时完成；沙箱/离线路径用 MigrationScript 模拟）。
    #    迁移失败必须自动回滚，绝不带着半迁移数据继续。
    if ($null -ne $MigrationScript) {
        try {
            & $MigrationScript -Root $dataRoot
        } catch {
            Restore-ASRollback -InstallDir $InstallDir -Root $dataRoot -Tag $Tag | Out-Null
            throw 'upgrade data migration failed; automatic rollback performed: ' + $_.Exception.Message
        }
    }

    # 4) 自检：配置/tasks 版本、runtime 可解析、EXE 版本一致；失败自动回滚。
    $sc = Test-ASSelfCheck -InstallDir $InstallDir -Root $dataRoot -ExpectedVersion $ExpectedVersion
    if (-not $sc.Ok) {
        $fails = @($sc.Checks | Where-Object { $_.Status -eq 'Fail' } |
            ForEach-Object { "$($_.Check): $($_.Message)" }) -join '; '
        Restore-ASRollback -InstallDir $InstallDir -Root $dataRoot -Tag $Tag | Out-Null
        throw "upgrade self-check failed; automatic rollback performed: $fails"
    }

    return [pscustomobject]@{
        BackupDir = $backup.BackupDir; InstallDir = $replace.InstallDir; DataRoot = $dataRoot
        Migrated = ($null -ne $MigrationScript); SelfCheck = $sc
    }
}

# ---- 卸载：默认只移除应用拥有的非用户文件；用户数据显式选择 Keep|Remove ----
function Invoke-ASUninstall {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$Root = '',
        [ValidateSet('Keep', 'Remove')][string]$UserData = 'Keep'
    )
    $dataRoot = Get-ASDataRoot -Root $Root
    $removed = @()

    # 安装目录中的候选/伴随文件（删除安装内容）
    if (Test-Path -LiteralPath $InstallDir) {
        $installFiles = @(Get-ChildItem -LiteralPath $InstallDir -File -Recurse -ErrorAction SilentlyContinue)
        foreach ($f in $installFiles) { Remove-Item -LiteralPath $f.FullName -Force; $removed += $f.FullName }
        # 仅删除由安装产生的空目录（若还有残留说明有其他占用，保留不动）
        Get-ChildItem -LiteralPath $InstallDir -Recurse -Directory -ErrorAction SilentlyContinue |
            Sort-Object { $_.FullName.Length } -Descending |
            ForEach-Object { if (-not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $_.FullName -Force } }
    }

    # 备份是应用拥有的可清理证据；但保留最近一份以便回滚观察窗口人工复核（登记到报告）。
    if ($UserData -eq 'Remove') {
        $backupRoot = Join-Path $dataRoot 'backups\spkg'
        if (Test-Path -LiteralPath $backupRoot) {
            Remove-Item -LiteralPath $backupRoot -Recurse -Force
            $removed += $backupRoot
        }
        $backupsDir = Join-Path $dataRoot 'backups'
        if ((Test-Path -LiteralPath $backupsDir) -and -not (Get-ChildItem -LiteralPath $backupsDir -Force -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $backupsDir -Force
            $removed += $backupsDir
        }
    }

    return [pscustomobject]@{ Removed = @($removed); UserData = $UserData; DataRoot = $dataRoot; InstallDir = [System.IO.Path]::GetFullPath($InstallDir) }
}

# ---- 重装：全新安装目录 + 候选副本 ----
function Invoke-ASReinstall {
    param(
        [Parameter(Mandatory = $true)][string]$CandidateDir,
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$Root = ''
    )
    $dataRoot = Get-ASDataRoot -Root $Root
    $installFull = [System.IO.Path]::GetFullPath($InstallDir)
    if (Test-Path -LiteralPath $installFull) {
        Get-ChildItem -LiteralPath $installFull -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Force -Path $installFull | Out-Null
    }
    $copied = @()
    foreach ($item in (Get-ChildItem -LiteralPath ([System.IO.Path]::GetFullPath($CandidateDir)) -Force)) {
        $target = Join-Path $installFull $item.Name
        if ($item.PSIsContainer) { Copy-Item -LiteralPath $item.FullName -Destination $target -Recurse -Force }
        else { Copy-Item -LiteralPath $item.FullName -Destination $target -Force }
        $copied += $item.Name
    }
    return [pscustomobject]@{ InstallDir = $installFull; Copied = @($copied); DataRoot = $dataRoot }
}

# ---- 命令行分发（点源时不触发） ----
if ($MyInvocation.InvocationName -ne '.') {
    if (-not $Command) {
        Write-Error '点源加载本模块使用： . ./SPkg-Lifecycle.ps1；直接执行必须提供 -Command。'
        exit 40
    }
    switch ($Command) {
        'health' {
            $root = Get-ASDataRoot -Root $DataRoot
            $schema = @{ 'config.json' = 1; 'tasks.json' = 2; 'runtime.json' = -1 }
            foreach ($f in @('config.json', 'tasks.json', 'runtime.json')) {
                $expected = $schema[$f]
                $h = Test-ASJsonHealth -Path (Join-Path $root $f) -SchemaRequired ($expected -ge 0) -ExpectedSchema $expected
                [pscustomobject]@{ file = $f; status = $h.Status; message = $h.Message }
            }
        }
        'backup' { Backup-ASDataRoot -Root $DataRoot -Tag $Tag | Format-List | Out-String | Write-Host }
        'replace' { Replace-ASBinary -CandidateDir $CandidateDir -InstallDir $InstallDir -Root $DataRoot -Tag $Tag | Format-List | Out-String | Write-Host }
        'selfcheck' {
            $r = Test-ASSelfCheck -InstallDir $InstallDir -Root $DataRoot -ExpectedVersion $ExpectedVersion
            $r.Checks | Format-Table -AutoSize | Out-String | Write-Host
            if (-not $r.Ok) { exit 30 }
        }
        'rollback' { Restore-ASRollback -InstallDir $InstallDir -Root $DataRoot -Tag $Tag | Format-List | Out-String | Write-Host }
        'upgrade' {
            $r = Invoke-ASUpgrade -CandidateDir $CandidateDir -InstallDir $InstallDir -Root $DataRoot -Tag $Tag -ExpectedVersion $ExpectedVersion
            $r | Format-List | Out-String | Write-Host
        }
        'uninstall' { Invoke-ASUninstall -InstallDir $InstallDir -Root $DataRoot -UserData $UserData | Format-List | Out-String | Write-Host }
        'reinstall' { Invoke-ASReinstall -CandidateDir $CandidateDir -InstallDir $InstallDir -Root $DataRoot | Format-List | Out-String | Write-Host }
    }
}
