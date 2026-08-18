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
# 安全契约（与执行书一致 + D1 所有权边界）：
#  - 备份成功是任何替换前置；备份失败不得继续。
#  - 回滚包与目标发布一一对应（记录 source-commit / candidate 名），保留被替换前的
#    EXE 与配置（含 V1 EXE 恢复能力）。
#  - 损坏 JSON 只分类、只标记，绝不静默回退为可能触发任务的默认值。
#  - 本模块不触碰注册表、防火墙、Task Scheduler、自启或电源。系统集成在 B3 单独处理。
#  - 所有写路径都限定在数据根或安装目录之内（D2 统一校验：路径在根目录内 + 全链路无 ReparsePoint）。
#
# D1 安装所有权与破坏性路径加固：
#  - 新安装只允许进入「不存在的目录」或「空目录」；对已有非空目录的替换/回滚/卸载/重装
#    必须先通过 Test-ASInstallOwnership：目录内必须存在 AutoShutdown.owner.json，且其
#    schema/app/installDir 与该绝对路径绑定一致。未通过一律拒绝，不删除任何文件。
#  - 备份槽携带 backup.json 元数据，sourceInstallDir 与该绝对安装路径绑定；回滚前校验，
#    防止把其他目录/数据根的备份恢复到错误目标。
#  - 卸载/回滚/重装只删除所有权清单（appFiles）中的应用文件（并清理失败替换残留的候选
#    文件）；目录仅在为空时删除。绝不 Remove-Item <InstallDir> -Recurse 或枚举整目录全删。
#  - DataRoot 同样受保护：UserData=Remove 只删除经本应用标记的 S-PKG 备份
#    （backups\spkg\owner.json 绑定数据根），不因任意传入 DataRoot 删除其他数据。
#
# D2 junction/symlink/reparse-point 越界加固（最小返修，SPkg-Lifecycle 生命周期路径）：
#  - 统一校验：路径必须在根目录内（逐分量包含，非仅字符串/FullPath 前缀）且从根到目标的
#    完整链路逐分量无 ReparsePoint（junction/symlink）。仅字符串前缀与 `..` 过滤不足：
#    已拥有安装目录内的 NTFS junction 会在删除/复制/备份时被文件系统透明跟随，导致操作
#    作用于安装目录之外。
#  - 所有破坏性或递归路径先过该校验（fail-closed）：所有权门禁 Test-ASInstallOwnership
#    （安装目录自身 + 整树）、清单删除 Remove-ASOwnedFiles、空目录清理
#    Remove-ASEmptyDirsUnder、候选复制 Copy-ASDirContents / 重装复制、安装备份
#    Replace-ASBinary、回滚恢复 Restore-ASReplaceFailure / Restore-ASInstallBackup /
#    Restore-ASRollback、DataRoot 备份/恢复 Backup-ASDataRoot / Restore-ASRollback /
#    Invoke-ASUninstall（UserData=Remove）。
#  - 遇到安装目录、候选目录、备份目录或其子路径中的 junction/symlink/reparse point：
#    在任何复制、删除、写入前拒绝，且不触碰目录外内容。
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

# ---- D1：安装所有权与路径绑定元数据 ----
$script:OwnerMarkerFile = 'AutoShutdown.owner.json'
$script:InstallBackupMetaFile = 'backup.json'
$script:SpkgBackupsOwnerFile = 'owner.json'
$script:OwnerMarkerSchema = 1
$script:OwnerAppName = 'AutoShutdown V2'

# ---- 路径规范化 ----
function Resolve-ASPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($full)
    if ($full -ne $root) { $full = $full.TrimEnd('\', '/') }
    return $full
}

# ---- D1：危险路径硬守卫（文件系统根/用户主目录/Windows 系统根/工作区根/artifacts） ----
# 这些位置即使出现所有权标记也一律拒绝，是所有权机制之外的最后防线。
function Test-ASForbiddenPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = Resolve-ASPath $Path
    if ($full -eq ([System.IO.Path]::GetPathRoot($full))) { return $true }
    $userHome = Resolve-ASPath ([Environment]::GetFolderPath('UserProfile'))
    if ($full -eq $userHome) { return $true }
    $sysRoot = Resolve-ASPath $env:SystemRoot
    if ($full -eq $sysRoot) { return $true }
    $repoRoot = Resolve-ASPath (Split-Path -Parent $PSScriptRoot)
    if ($full -eq $repoRoot) { return $true }
    $artifacts = Resolve-ASPath (Join-Path $repoRoot 'artifacts')
    if ($full -eq $artifacts) { return $true }
    if ($full.StartsWith($artifacts + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    return $false
}

# ---- D2：统一的「路径在根目录内 + 全链路无 ReparsePoint」校验 ----
# 仅字符串/FullPath 前缀与 `..` 过滤不足：已拥有根目录内的 NTFS junction/symlink 会被
# 文件系统透明跟随，使删除/复制/备份作用于根目录之外。这里先做逐分量包含校验（非仅前缀），
# 再从根逐分量探测 ReparsePoint（junction/symlink），命中即返回失败（调用方 fail-closed）。
# 返回 [pscustomobject]@{ Ok; Reason; Message; ReparsePath }。
function Test-ASPathWithinRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $rootFull = Resolve-ASPath $Root
    $full = [System.IO.Path]::GetFullPath($Path)
    # 1) 逐分量包含校验（GetFullPath 已解析 ..；此处杜绝 C:\data vs C:\dataevil 一类前缀误判）
    $fullDrive = [System.IO.Path]::GetPathRoot($full)
    $rootDrive = [System.IO.Path]::GetPathRoot($rootFull)
    if (-not $fullDrive.Equals($rootDrive, [System.StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'not-within-root'; Message = "path '$full' not on same drive as root '$rootFull'"; ReparsePath = $null }
    }
    $rootParts = @(($rootFull.TrimEnd('\', '/')) -split '[\\/]' | Where-Object { $_ })
    $fullParts = @(($full.TrimEnd('\', '/')) -split '[\\/]' | Where-Object { $_ })
    if ($fullParts.Count -lt $rootParts.Count) {
        return [pscustomobject]@{ Ok = $false; Reason = 'not-within-root'; Message = "path '$full' is above root '$rootFull'"; ReparsePath = $null }
    }
    for ($i = 0; $i -lt $rootParts.Count; $i++) {
        if (-not $fullParts[$i].Equals($rootParts[$i], [System.StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{ Ok = $false; Reason = 'not-within-root'; Message = "path '$full' outside root '$rootFull' (segment '$($fullParts[$i])')"; ReparsePath = $null }
        }
    }
    # 2) 逐分量（根→目标）ReparsePoint 探测；目标缺失视为安全（其后无内容可跟随）
    $probe = $rootFull
    $item = Get-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    if ($null -ne $item -and ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'reparse-point'; Message = "reparse point in chain: $probe"; ReparsePath = $probe }
    }
    for ($i = $rootParts.Count; $i -lt $fullParts.Count; $i++) {
        $probe = Join-Path $probe $fullParts[$i]
        $item = Get-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) { break }
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            return [pscustomobject]@{ Ok = $false; Reason = 'reparse-point'; Message = "reparse point in chain: $probe"; ReparsePath = $probe }
        }
    }
    return [pscustomobject]@{ Ok = $true; Reason = 'ok'; Message = "path within root with no reparse point: $full"; ReparsePath = $null }
}

# ---- D2：相对路径（base→leaf）逐分量 ReparsePoint 探测 ----
# 返回 $null = 安全；'TRAVERSAL' = 含 .. 或绝对路径；字符串 = 第一个 reparse point 的完整路径。
# 不校验 BaseDir 自身（由调用方按其语义单独校验）。
function Get-ASRelPathReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$BaseDir,
        [Parameter(Mandatory = $false)][string]$RelativePath = ''
    )
    $base = Resolve-ASPath $BaseDir
    $rel = [string]$RelativePath
    if ([string]::IsNullOrWhiteSpace($rel)) { return $null }
    if ([System.IO.Path]::IsPathRooted($rel)) { return 'TRAVERSAL' }
    $clean = @()
    foreach ($p in @($rel -split '[\\/]')) {
        if ([string]::IsNullOrWhiteSpace($p) -or $p -eq '.') { continue }
        if ($p -eq '..') { return 'TRAVERSAL' }
        $clean += $p
    }
    if ($clean.Count -eq 0) { return $null }
    $probe = $base
    foreach ($p in $clean) {
        $probe = Join-Path $probe $p
        $item = Get-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) { return $null }
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { return $probe }
    }
    return $null
}

# ---- D2：相对路径链路断言（命中 reparse/越界即抛错 fail-closed） ----
function Assert-ASRelPathSafe {
    param(
        [Parameter(Mandatory = $true)][string]$BaseDir,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [string]$Action = 'operate'
    )
    $rp = Get-ASRelPathReparsePoint -BaseDir $BaseDir -RelativePath $RelativePath
    if ($rp -eq 'TRAVERSAL') { throw "refusing to $Action outside '$BaseDir' (path traversal in rel '$RelativePath')" }
    if ($rp) { throw "refusing to $Action through reparse point at '$rp' (rel '$RelativePath')" }
}

# ---- D2：不跟随 junction/symlink 的安全递归枚举 ----
# 逐层枚举（绝不下钻 reparse 点）；遇到 ReparsePoint 立即返回失败路径，调用方必须 fail-closed。
# 返回 [pscustomobject]@{ Ok; ReparsePath; Files=@(绝对路径); Dirs=@(绝对路径) }。
function Get-ASDirTreeSafe {
    param([Parameter(Mandatory = $true)][string]$BaseDir)
    $base = Resolve-ASPath $BaseDir
    $files = [System.Collections.Generic.List[string]]::new()
    $dirs = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $base)) {
        return [pscustomobject]@{ Ok = $true; ReparsePath = $null; Files = @($files); Dirs = @($dirs) }
    }
    $item = Get-Item -LiteralPath $base -Force
    if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        return [pscustomobject]@{ Ok = $false; ReparsePath = $base; Files = @($files); Dirs = @($dirs) }
    }
    $stack = [System.Collections.Generic.Stack[string]]::new()
    $stack.Push($base)
    while ($stack.Count -gt 0) {
        $dir = $stack.Pop()
        $children = @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)
        foreach ($c in $children) {
            if ($c.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                return [pscustomobject]@{ Ok = $false; ReparsePath = $c.FullName; Files = @($files); Dirs = @($dirs) }
            }
            if ($c.PSIsContainer) { $dirs.Add($c.FullName); $stack.Push($c.FullName) }
            else { $files.Add($c.FullName) }
        }
    }
    return [pscustomobject]@{ Ok = $true; ReparsePath = $null; Files = @($files); Dirs = @($dirs) }
}

# ---- 替换失败的内部回滚（仅在本函数刚创建的 rollback-install 槽上调用） ----
# 有完整的 backup.json 绑定（sourceInstallDir==本路径）作证据；只删除所有权清单文件 +
# 候选残留 + 标记文件，再按 complete/no-install 标记恢复或清理，绝不整目录删除。
function Restore-ASReplaceFailure {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [Parameter(Mandatory = $true)][string]$BackupDir,
        [Parameter(Mandatory = $true)][string]$CandidateDir
    )
    $installFull = Resolve-ASPath $InstallDir
    $binding = Test-ASBackupBinding -BackupDir $BackupDir -InstallDir $installFull
    if (-not $binding.Ok) { throw "refusing to roll back ${installFull}: $($binding.Message)" }
    $complete = Test-Path -LiteralPath (Join-Path $BackupDir '_complete.marker')
    $noInstall = Test-Path -LiteralPath (Join-Path $BackupDir '_no-install.marker')
    $deleteRel = @()
    $marker = Read-ASOwnerMarker -InstallDir $installFull
    if ($null -ne $marker -and $null -ne $marker.PSObject.Properties['appFiles']) { $deleteRel = @($marker.appFiles) }
    if ($null -ne $binding.Meta.PSObject.Properties['candidateFiles']) { $deleteRel += @($binding.Meta.candidateFiles) }
    $deleteRel = @($deleteRel | Where-Object { $_ -and ($_ -ne $script:OwnerMarkerFile) } | Sort-Object -Unique)
    Remove-ASOwnedFiles -InstallDir $installFull -AppFiles $deleteRel
    $markerPath = Join-Path $installFull $script:OwnerMarkerFile
    if (Test-Path -LiteralPath $markerPath) {
        Assert-ASRelPathSafe -BaseDir $installFull -RelativePath $script:OwnerMarkerFile -Action 'remove ownership marker'
        Remove-Item -LiteralPath $markerPath -Force
    }
    $markerTmp = Join-Path $installFull ($script:OwnerMarkerFile + '.tmp')
    if (Test-Path -LiteralPath $markerTmp) {
        Assert-ASRelPathSafe -BaseDir $installFull -RelativePath ($script:OwnerMarkerFile + '.tmp') -Action 'remove ownership marker tmp'
        Remove-Item -LiteralPath $markerTmp -Force
    }
    if ($complete) {
        $saved = Join-Path $BackupDir (Split-Path -Leaf $installFull)
        if (Test-Path -LiteralPath $saved) {
            # 逐子项、逐级合并复制备份内容到安装目录（目标目录存在时避免嵌套 <install>\<install> 或同名子目录）。
            Copy-ASDirContents -SourceDir $saved -DestinationDir $installFull
            return 'restored'
        }
    }
    if ($noInstall) {
        Remove-ASEmptyDirsUnder -InstallDir $installFull
        if ((Test-Path -LiteralPath $installFull) -and -not (Get-ChildItem -LiteralPath $installFull -Force -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $installFull -Force
        }
        return 'cleaned'
    }
    throw "install rollback backup incomplete; refusing to modify $installFull"
}

# ---- 目录内文件相对路径清单（只读枚举，用于所有权清单与候选清单） ----
function Get-ASRelFileList {
    param([Parameter(Mandatory = $true)][string]$BaseDir)
    $base = Resolve-ASPath $BaseDir
    $rel = @()
    if (Test-Path -LiteralPath $base) {
        # D2：不跟随 junction 的安全枚举；命中 reparse 即抛错 fail-closed。
        $tree = Get-ASDirTreeSafe -BaseDir $base
        if (-not $tree.Ok) { throw "refusing to enumerate '$base': reparse point at '$($tree.ReparsePath)'" }
        $rel = @($tree.Files | ForEach-Object { $_.Substring($base.Length).TrimStart('\', '/') })
    }
    return $rel
}

# ---- 安装所有权标记读取（解析失败/缺失一律 $null） ----
function Read-ASOwnerMarker {
    param([Parameter(Mandatory = $true)][string]$InstallDir)
    $markerPath = Join-Path $InstallDir $script:OwnerMarkerFile
    if (-not (Test-Path -LiteralPath $markerPath)) { return $null }
    try {
        return (Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json)
    } catch {
        return $null
    }
}

# ---- 安装所有权标记写入（原子：临时文件后换名；appFiles 为相对路径清单） ----
function Write-ASOwnerMarker {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string[]]$AppFiles,
        [string]$CandidateName = ''
    )
    $full = Resolve-ASPath $InstallDir
    $marker = [ordered]@{
        schema = $script:OwnerMarkerSchema
        app = $script:OwnerAppName
        installDir = $full
        appFiles = @($AppFiles | Where-Object { $_ } | Sort-Object -Unique)
        candidate = $CandidateName
        created = (Get-Date -Format o)
    }
    $tmp = Join-Path $full ($script:OwnerMarkerFile + '.tmp')
    $final = Join-Path $full $script:OwnerMarkerFile
    $marker | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $final -Force
}

# ---- D1 核心：安装目录所有权验证 ----
# 返回 { Ok, Reason, Message, Marker }：
#   forbidden / no-marker / bad-marker / path-mismatch -> Ok=$false（拒绝，不删除任何文件）
#   new（目录不存在）/ empty（空目录）-> Ok=$true（允许作为新安装目标）
#   owned（标记有效且绑定本路径）-> Ok=$true
function Test-ASInstallOwnership {
    param([Parameter(Mandatory = $true)][string]$InstallDir)
    $full = Resolve-ASPath $InstallDir
    if (Test-ASForbiddenPath $full) {
        return [pscustomobject]@{ Ok = $false; Reason = 'forbidden'; Message = "protected location (filesystem root / user home / workspace / artifacts): $full"; Marker = $null }
    }
    if (-not (Test-Path -LiteralPath $full)) {
        return [pscustomobject]@{ Ok = $true; Reason = 'new'; Message = "install dir does not exist: $full"; Marker = $null }
    }
    # D2：安装目录自身不得是 junction/symlink/reparse point（否则全部后续写入/删除会被透明跟随）。
    $dirItem = Get-Item -LiteralPath $full -Force
    if ($dirItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        return [pscustomobject]@{ Ok = $false; Reason = 'reparse-point'; Message = "install dir itself is a reparse point: $full"; Marker = $null }
    }
    $entries = @(Get-ChildItem -LiteralPath $full -Force -ErrorAction SilentlyContinue)
    if ($entries.Count -eq 0) {
        return [pscustomobject]@{ Ok = $true; Reason = 'empty'; Message = "install dir is empty: $full"; Marker = $null }
    }
    $marker = Read-ASOwnerMarker -InstallDir $full
    if ($null -eq $marker) {
        return [pscustomobject]@{ Ok = $false; Reason = 'no-marker'; Message = "install dir not owned by $($script:OwnerAppName) (no $script:OwnerMarkerFile): $full"; Marker = $null }
    }
    $schemaOk = $false; $appOk = $false
    if ($null -ne $marker.PSObject.Properties['schema']) { $schemaOk = ([int]$marker.schema -eq $script:OwnerMarkerSchema) }
    if ($null -ne $marker.PSObject.Properties['app']) { $appOk = ([string]$marker.app -eq $script:OwnerAppName) }
    if (-not ($schemaOk -and $appOk)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'bad-marker'; Message = "install ownership marker invalid (schema/app) at $full"; Marker = $marker }
    }
    if ([string]$marker.installDir -ne $full) {
        return [pscustomobject]@{ Ok = $false; Reason = 'path-mismatch'; Message = "install ownership marker bound to '$($marker.installDir)' but target is '$full'"; Marker = $marker }
    }
    # D2：所有权有效仅当整树无 junction/symlink/reparse point（不跟随的安全枚举；命中即拒绝）。
    $tree = Get-ASDirTreeSafe -BaseDir $full
    if (-not $tree.Ok) {
        return [pscustomobject]@{ Ok = $false; Reason = 'reparse-point'; Message = "reparse point under install dir: $($tree.ReparsePath)"; Marker = $marker }
    }
    return [pscustomobject]@{ Ok = $true; Reason = 'owned'; Message = "install dir owned: $full"; Marker = $marker }
}

# ---- 只删除所有权清单中的应用文件（逐文件、路径越界防御；标记文件最后单独删） ----
function Remove-ASOwnedFiles {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string[]]$AppFiles
    )
    $base = Resolve-ASPath $InstallDir
    $prefix = $base + [System.IO.Path]::DirectorySeparatorChar
    $markerPath = Join-Path $base $script:OwnerMarkerFile
    # D2：根自身不得是 reparse point（防御纵深；调用方门禁已校验整树）。
    if (Test-Path -LiteralPath $base) {
        $baseItem = Get-Item -LiteralPath $base -Force
        if ($baseItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "refusing to remove files: install dir is a reparse point: $base"
        }
    }
    foreach ($rel in $AppFiles) {
        if ([string]::IsNullOrWhiteSpace($rel)) { continue }
        if ($rel.IndexOf('..', [System.StringComparison]::Ordinal) -ge 0) { continue }
        $full = [System.IO.Path]::GetFullPath((Join-Path $base $rel))
        if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($full -eq $markerPath) { continue }
        # D2：统一「根内 + 全链路无 ReparsePoint」校验；命中 reparse 即 fail-closed（不触碰目录外）。
        $within = Test-ASPathWithinRoot -Root $base -Path $full
        if (-not $within.Ok) {
            if ($within.Reason -eq 'reparse-point') {
                throw "refusing to remove '$rel': reparse point at '$($within.ReparsePath)' under install dir '$base'"
            }
            continue
        }
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Force }
    }
}

# ---- 仅删除安装目录下已为空的子目录（目录仅在为空时删除） ----
function Remove-ASEmptyDirsUnder {
    param([Parameter(Mandatory = $true)][string]$InstallDir)
    $base = Resolve-ASPath $InstallDir
    if (-not (Test-Path -LiteralPath $base)) { return }
    # D2：不跟随 junction 的安全枚举；命中 reparse 即 fail-closed（绝不 Remove-Item 到 junction 目录）。
    $tree = Get-ASDirTreeSafe -BaseDir $base
    if (-not $tree.Ok) { throw "refusing to remove empty dirs: reparse point under '$base' at '$($tree.ReparsePath)'" }
    $allDirs = @($tree.Dirs)
    if ($allDirs.Count -eq 0) { return }
    $allDirs | Sort-Object { $_.Length } -Descending | ForEach-Object {
        $d = $_
        if (-not (Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue)) {
            # 二次链路校验（防御纵深：枚举后至删除前可能被替换为 junction）。
            $rel = $d.Substring($base.Length).TrimStart('\', '/')
            Assert-ASRelPathSafe -BaseDir $base -RelativePath $rel -Action 'remove empty dir'
            Remove-Item -LiteralPath $d -Force
        }
    }
}

# ---- 目录内容合并复制（逐文件、逐级）----
# 目标目录已存在时逐子项复制并建立相对路径，绝不 Copy-Item <源目录> -Destination <已存在目录>
# 造成 <目标>\<同名>\… 嵌套（D1：候选/备份中的子目录在安装槽已有同名目录时同样适用）。
# 只复制文件并建立其父目录；空目录无内容，不保留。
function Copy-ASDirContents {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$DestinationDir
    )
    $src = Resolve-ASPath $SourceDir
    $dst = Resolve-ASPath $DestinationDir
    # D2：源树必须无 reparse point（不跟随的安全枚举；命中即 fail-closed，不读取目录外内容）。
    $tree = Get-ASDirTreeSafe -BaseDir $src
    if (-not $tree.Ok) { throw "refusing to copy from '$src': reparse point at '$($tree.ReparsePath)'" }
    # D2：目标自身不得已是 junction/symlink（存在时校验）；不存在时新建。
    if (Test-Path -LiteralPath $dst) {
        $dstItem = Get-Item -LiteralPath $dst -Force
        if ($dstItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "refusing to copy into '$dst': destination is a reparse point"
        }
    } else {
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
    }
    foreach ($f in $tree.Files) {
        $rel = $f.Substring($src.Length).TrimStart('\', '/')
        $dest = Join-Path $dst $rel
        # D2：目标链路逐分量校验（已存在子目录可能是 junction，写入前拒绝）。
        Assert-ASRelPathSafe -BaseDir $dst -RelativePath $rel -Action 'copy into'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $f -Destination $dest -Force
    }
}

# ---- 备份元数据与该绝对安装路径绑定校验（D1） ----
function Test-ASBackupBinding {
    param(
        [Parameter(Mandatory = $true)][string]$BackupDir,
        [Parameter(Mandatory = $true)][string]$InstallDir
    )
    $metaPath = Join-Path $BackupDir $script:InstallBackupMetaFile
    if (-not (Test-Path -LiteralPath $metaPath)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'no-backup-meta'; Message = "install rollback backup metadata ($script:InstallBackupMetaFile) missing in $BackupDir"; Meta = $null }
    }
    $meta = $null
    try {
        $meta = Get-Content -LiteralPath $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        return [pscustomobject]@{ Ok = $false; Reason = 'bad-backup-meta'; Message = "install rollback backup metadata corrupt in $BackupDir"; Meta = $null }
    }
    if ([string]$meta.sourceInstallDir -ne (Resolve-ASPath $InstallDir)) {
        return [pscustomobject]@{ Ok = $false; Reason = 'backup-path-mismatch'; Message = "rollback backup bound to '$($meta.sourceInstallDir)' but target is '$(Resolve-ASPath $InstallDir)'"; Meta = $meta }
    }
    if ([int]$meta.schema -ne $script:OwnerMarkerSchema -or [string]$meta.app -ne $script:OwnerAppName) {
        return [pscustomobject]@{ Ok = $false; Reason = 'bad-backup-meta'; Message = "install rollback backup metadata invalid in $BackupDir"; Meta = $meta }
    }
    return [pscustomobject]@{ Ok = $true; Reason = 'bound'; Message = 'backup bound to install dir'; Meta = $meta }
}

# ---- S-PKG 备份根的所有权标记（DataRoot 保护） ----
function Ensure-ASSpkgBackupsRoot {
    param([Parameter(Mandatory = $true)][string]$DataRoot)
    $dataRootFull = Resolve-ASPath $DataRoot
    $backupRoot = Join-Path $dataRootFull 'backups\spkg'
    # D2：backups\spkg 链路不得含 junction/symlink（否则 New-Item/写入会透明跟随到目录外）。
    Assert-ASRelPathSafe -BaseDir $dataRootFull -RelativePath 'backups\spkg' -Action 'create backups root'
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $ownerPath = Join-Path $backupRoot $script:SpkgBackupsOwnerFile
    if (-not (Test-Path -LiteralPath $ownerPath)) {
        $owner = [ordered]@{
            schema = $script:OwnerMarkerSchema; app = $script:OwnerAppName
            kind = 'spkg-backups-root'; dataRoot = $dataRootFull; created = (Get-Date -Format o)
        }
        $owner | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ownerPath -Encoding UTF8
    }
    return $backupRoot
}

# ---- 校验 backups\spkg 是本应用标记的 S-PKG 备份（UserData=Remove 前必查） ----
function Test-ASSpkgBackupsOwned {
    param([Parameter(Mandatory = $true)][string]$DataRoot)
    $dataRootFull = Resolve-ASPath $DataRoot
    if (Test-ASForbiddenPath $dataRootFull) { return $false }
    # D2：备份根链路含 junction/symlink 时视为非本应用标记备份（fail-closed，拒绝删除）。
    $rp = Get-ASRelPathReparsePoint -BaseDir $dataRootFull -RelativePath ('backups\spkg\' + $script:SpkgBackupsOwnerFile)
    if ($rp) { return $false }
    $ownerPath = Join-Path $dataRootFull ("backups\spkg\" + $script:SpkgBackupsOwnerFile)
    if (-not (Test-Path -LiteralPath $ownerPath)) { return $false }
    $owner = $null
    try {
        $owner = Get-Content -LiteralPath $ownerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        return $false
    }
    if ([string]$owner.app -ne $script:OwnerAppName) { return $false }
    if ([int]$owner.schema -ne $script:OwnerMarkerSchema) { return $false }
    if ([string]$owner.dataRoot -ne $dataRootFull) { return $false }
    return $true
}

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
    $dataRootFull = Resolve-ASPath $dataRoot
    if (Test-ASForbiddenPath $dataRootFull) { throw "refusing to back up into a protected data root: $dataRootFull" }
    if (-not (Test-Path -LiteralPath $dataRootFull)) { New-Item -ItemType Directory -Force -Path $dataRootFull | Out-Null }
    # D2：DataRoot 备份前做不跟随 junction 的安全枚举；命中 reparse 即 fail-closed，不写入目录外。
    $tree = Get-ASDirTreeSafe -BaseDir $dataRootFull
    if (-not $tree.Ok) { throw "refusing to back up data root: reparse point at '$($tree.ReparsePath)'" }
    $backupRoot = Ensure-ASSpkgBackupsRoot -DataRoot $dataRootFull
    $ts = Get-Date -Format 'yyyyMMddHHmmss'
    $backupDir = Join-Path $backupRoot ("{0}-{1}" -f $ts, $Tag)
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $backupRootPrefix = $backupRoot + [System.IO.Path]::DirectorySeparatorChar
    $files = @()
    foreach ($f in $tree.Files) {
        # 排除备份根自身（含上次备份）与备份目录，避免自我复制。
        if ($f.StartsWith($backupRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $rel = $f.Substring($dataRootFull.Length).TrimStart('\', '/')
        $dest = Join-Path $backupDir $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        Copy-Item -LiteralPath $f -Destination $dest -Force
        $files += $rel
    }
    $meta = [ordered]@{
        kind = 'spkg-backup'; tag = $Tag; created = (Get-Date -Format o)
        candidate = $CandidateName; dataRoot = $dataRootFull
        files = @($files)
    }
    $meta | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backupDir $script:LifecycleMetaFile) -Encoding UTF8
    return [pscustomobject]@{ BackupDir = $backupDir; FileCount = @($files).Count; DataRoot = $dataRootFull }
}

# ---- 二进制替换（备份成功是前置；失败自动回滚并恢复备份） ----
# 回滚槽采用“新备份验证完成后原子换名”策略：备份写入 .new 槽，文件数核对一致后才
# 交换为正式槽；备份阶段任何失败都保留上一个回滚槽与原始安装目录原样（绝不因一次失败
# 的替换而破坏 V1 EXE/配置恢复能力）。
# D1：替换前必须通过 Test-ASInstallOwnership；备份槽写入 backup.json 绑定 sourceInstallDir；
# 替换后刷新所有权清单（候选文件 + 仍存在的既有应用文件）。
function Replace-ASBinary {
    param(
        [Parameter(Mandatory = $true)][string]$CandidateDir,
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$Root = '',
        [string]$Tag = 'upgrade'
    )
    $dataRoot = Get-ASDataRoot -Root $Root
    $dataRootFull = Resolve-ASPath $dataRoot
    if (Test-ASForbiddenPath $dataRootFull) { throw "refusing to use a protected data root: $dataRootFull" }
    $installFull = Resolve-ASPath $InstallDir
    $candFull = Resolve-ASPath $CandidateDir
    if (-not (Test-Path -LiteralPath $candFull)) { throw "candidate dir not found: $candFull" }
    # D2：候选目录自身不得是 junction/symlink/reparse point（其子路径由复制/枚举路径守卫）。
    $candItem = Get-Item -LiteralPath $candFull -Force
    if ($candItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "refusing to use candidate dir: reparse point at $candFull"
    }

    # 0) 所有权门禁（fail-closed）：非空既有目录必须有绑定本绝对路径的有效所有权标记；
    #    危险路径（文件系统根/用户主目录/工作区/artifacts）一律拒绝。
    $ownership = Test-ASInstallOwnership -InstallDir $installFull
    if (-not $ownership.Ok) {
        throw "refusing to modify install dir: $($ownership.Message)"
    }
    $hadInstall = Test-Path -LiteralPath $installFull

    $backupRoot = Join-Path $dataRootFull 'backups\spkg'
    $slot = Join-Path $backupRoot 'rollback-install'
    $slotNew = Join-Path $backupRoot 'rollback-install.new'
    $completeMarker = Join-Path $slotNew '_complete.marker'
    $noInstallMarker = Join-Path $slotNew '_no-install.marker'

    # 1) 备份现有安装目录（含被替换前的 EXE，保留 V1 EXE 恢复能力）→ 验证 → 原子交换
    if (Test-Path -LiteralPath $slotNew) { Remove-Item -Recurse -Force -LiteralPath $slotNew }
    New-Item -ItemType Directory -Force -Path $slotNew | Out-Null
    try {
        if ($hadInstall) {
            # D2：安装备份改用不跟随 junction 的安全枚举 + 逐文件复制；命中 reparse 即
            # fail-closed（原安装目录不受影响），绝不 Copy-Item -Recurse 沿 junction 复制目录外内容。
            $tree = Get-ASDirTreeSafe -BaseDir $installFull
            if (-not $tree.Ok) { throw "refusing to back up install dir: reparse point at '$($tree.ReparsePath)'" }
            $savedDir = Join-Path $slotNew (Split-Path -Leaf $installFull)
            New-Item -ItemType Directory -Force -Path $savedDir | Out-Null
            foreach ($f in $tree.Files) {
                $rel = $f.Substring($installFull.Length).TrimStart('\', '/')
                $dest = Join-Path $savedDir $rel
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
                Copy-Item -LiteralPath $f -Destination $dest -Force
            }
            $backed = @(Get-ChildItem -LiteralPath $savedDir -Recurse -File)
            $srcCount = @($tree.Files).Count
            if ($backed.Count -ne $srcCount) {
                throw "install backup incomplete ($($backed.Count)/$($srcCount)); aborting replace"
            }
            Set-Content -LiteralPath $completeMarker -Value (Get-Date -Format o) -Encoding UTF8
        } else {
            Set-Content -LiteralPath $noInstallMarker -Value (Get-Date -Format o) -Encoding UTF8
            New-Item -ItemType Directory -Force -Path $installFull | Out-Null
        }
        # 备份元数据与该绝对安装路径绑定（D1）：回滚时校验，防止跨目录/跨数据根误恢复。
        $backupMeta = [ordered]@{
            schema = $script:OwnerMarkerSchema; app = $script:OwnerAppName; kind = 'install-rollback'
            sourceInstallDir = $installFull; hadInstall = $hadInstall
            candidateFiles = @(Get-ASRelFileList -BaseDir $candFull)
            created = (Get-Date -Format o)
        }
        $backupMeta | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $slotNew $script:InstallBackupMetaFile) -Encoding UTF8
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
                # 逐子项、逐级合并复制：目标已有同名目录时不产生 <目标>\<同名>\… 嵌套。
                Copy-ASDirContents -SourceDir $item.FullName -DestinationDir $target
            } else {
                Copy-Item -LiteralPath $item.FullName -Destination $target -Force
            }
        }
        $candExeNames = @(Get-ChildItem -LiteralPath $candFull -Filter 'AutoShutdown-v*.exe' -File | ForEach-Object { $_.Name })
        Get-ChildItem -LiteralPath $installFull -Filter 'AutoShutdown-v*.exe' -File -ErrorAction SilentlyContinue |
            Where-Object { $candExeNames -notcontains $_.Name } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        # 3) 刷新所有权清单：候选文件 + 仍存在的既有应用文件（排除标记文件本身）
        $oldOwned = @()
        if ($null -ne $ownership.Marker -and $null -ne $ownership.Marker.PSObject.Properties['appFiles']) {
            $oldOwned = @($ownership.Marker.appFiles)
        }
        $candRel = @(Get-ASRelFileList -BaseDir $candFull)
        $newOwned = @()
        foreach ($rel in $oldOwned) {
            if ($rel -and (Test-Path -LiteralPath (Join-Path $installFull $rel))) { $newOwned += $rel }
        }
        foreach ($rel in $candRel) { $newOwned += $rel }
        $newOwned = @($newOwned | Where-Object { $_ -and ($_ -ne $script:OwnerMarkerFile) } | Sort-Object -Unique)
        Write-ASOwnerMarker -InstallDir $installFull -AppFiles $newOwned -CandidateName ($candExeNames | Select-Object -First 1)
    } catch {
        # 4) 替换失败 → 自动回滚（备份绑定本路径为证据；只删除清单文件+候选残留，绝不整目录删除）；
        #    无论原目录是「已拥有/空/新建」，都恢复为其替换前状态。
        $rolledBack = Restore-ASReplaceFailure -InstallDir $installFull -BackupDir $backupDir -CandidateDir $candFull
        throw "binary replace failed and was rolled back ($rolledBack): " + $_.Exception.Message
    }
    return [pscustomobject]@{
        InstallDir = $installFull; BackupDir = $backupDir
        HadInstall = $hadInstall; CandidateDir = $candFull
    }
}

# ---- 回滚：恢复替换前的安装目录 ----
# D1：只删除所有权清单中的应用文件 + 失败替换残留的候选文件，然后从绑定本绝对路径的
# 完整备份复制恢复；绝不整目录递归删除。
function Restore-ASInstallBackup {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [Parameter(Mandatory = $true)][string]$BackupDir
    )
    if (-not (Test-Path -LiteralPath $BackupDir)) { throw 'rollback backup missing; cannot restore' }
    $installFull = Resolve-ASPath $InstallDir
    $leaf = Split-Path -Leaf $installFull
    $saved = Join-Path $BackupDir $leaf
    $completeMarker = Join-Path $BackupDir '_complete.marker'
    $noInstallMarker = Join-Path $BackupDir '_no-install.marker'

    # 备份元数据必须存在且与该绝对安装路径绑定；否则拒绝，不删除任何文件。
    $binding = Test-ASBackupBinding -BackupDir $BackupDir -InstallDir $installFull
    if (-not $binding.Ok) { throw "refusing to modify ${installFull}: $($binding.Message)" }

    # 当前安装目录必须是本应用拥有（或不存在/空）；非拥有且非空 → 拒绝。
    $ownership = Test-ASInstallOwnership -InstallDir $installFull
    if (-not $ownership.Ok) { throw "refusing to modify ${installFull}: $($ownership.Message)" }

    # 只删除所有权清单中的应用文件 + 失败替换残留的候选文件（逐文件、越界防御）。
    $deleteRel = @()
    if ($ownership.Reason -eq 'owned' -and $null -ne $ownership.Marker.PSObject.Properties['appFiles']) {
        $deleteRel = @($ownership.Marker.appFiles)
    }
    $candResidue = @()
    if ($null -ne $binding.Meta.PSObject.Properties['candidateFiles']) { $candResidue = @($binding.Meta.candidateFiles) }
    $deleteRel = @($deleteRel + $candResidue | Where-Object { $_ -and ($_ -ne $script:OwnerMarkerFile) } | Sort-Object -Unique)
    Remove-ASOwnedFiles -InstallDir $installFull -AppFiles $deleteRel
    $markerPath = Join-Path $installFull $script:OwnerMarkerFile
    if (Test-Path -LiteralPath $markerPath) {
        Assert-ASRelPathSafe -BaseDir $installFull -RelativePath $script:OwnerMarkerFile -Action 'remove ownership marker'
        Remove-Item -LiteralPath $markerPath -Force
    }

    if ((Test-Path -LiteralPath $completeMarker) -and (Test-Path -LiteralPath $saved)) {
        # 只复制备份目录的「内容」到安装目录（绝不整目录删除；目标目录存在时逐子项、逐级
        # 合并复制，避免 Copy-Item 把源文件夹嵌套成 <install>\<install>\… 或同名子目录）。
        Copy-ASDirContents -SourceDir $saved -DestinationDir $installFull
        return $true
    }
    if (Test-Path -LiteralPath $noInstallMarker) {
        if (Test-Path -LiteralPath $installFull) {
            Remove-ASEmptyDirsUnder -InstallDir $installFull
            if (-not (Get-ChildItem -LiteralPath $installFull -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $installFull -Force
            }
        }
        return $true
    }
    throw "install rollback backup incomplete; refusing to modify $installFull"
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
    $dataRootFull = Resolve-ASPath $dataRoot
    if (Test-ASForbiddenPath $dataRootFull) { throw "refusing to use a protected data root: $dataRootFull" }
    $installFull = Resolve-ASPath $InstallDir

    # 1) 恢复最近一次 spkg 备份的数据文件
    $backupRoot = Join-Path $dataRootFull 'backups\spkg'
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
    # 数据备份与该数据根绑定：跨数据根恢复会污染目标，一律拒绝。
    if ([string]$meta.dataRoot -ne $dataRootFull) {
        throw "spkg data backup bound to '$($meta.dataRoot)' but current data root is '$dataRootFull'; refusing to restore"
    }
    $dataPrefix = $dataRootFull + [System.IO.Path]::DirectorySeparatorChar
    # D2：备份槽整条链路（dataRoot→matched）不得含 junction/symlink，否则读取会透明跟随。
    $matchedRel = $matched.FullName.Substring($dataRootFull.Length).TrimStart('\', '/')
    $rpMatched = Get-ASRelPathReparsePoint -BaseDir $dataRootFull -RelativePath $matchedRel
    if ($rpMatched) { throw "refusing to restore: reparse point at '$rpMatched' in backup path" }
    foreach ($rel in $meta.files) {
        $relPath = [string]$rel
        # 兼容旧备份：若记录为绝对路径则换算为相对路径；同时校验不得越出数据根。
        if ($relPath.StartsWith($dataPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relPath = $relPath.Substring($dataRootFull.Length).TrimStart('\', '/')
        }
        if ([string]::IsNullOrWhiteSpace($relPath)) { continue }
        if ($relPath.IndexOf('..', [System.StringComparison]::Ordinal) -ge 0) { continue }
        $destFull = [System.IO.Path]::GetFullPath((Join-Path $dataRootFull $relPath))
        if (-not $destFull.StartsWith($dataPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        # D2：恢复目标链路与备份源链路均无 reparse；命中即 fail-closed（不写目录外）。
        $destRel = $destFull.Substring($dataRootFull.Length).TrimStart('\', '/')
        $rpDest = Get-ASRelPathReparsePoint -BaseDir $dataRootFull -RelativePath $destRel
        if ($rpDest) {
            if ($rpDest -eq 'TRAVERSAL') { throw "refusing to restore outside data root (path traversal): $relPath" }
            throw "refusing to restore through reparse point at '$rpDest' (rel '$relPath')"
        }
        $src = Join-Path $matched.FullName $relPath
        if (-not (Test-Path -LiteralPath $src)) { continue }
        $rpSrc = Get-ASRelPathReparsePoint -BaseDir $matched.FullName -RelativePath $relPath
        if ($rpSrc) { throw "refusing to restore from reparse point in backup at '$rpSrc' (rel '$relPath')" }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destFull) | Out-Null
        Copy-Item -LiteralPath $src -Destination $destFull -Force
        $restored += $relPath
    }
    # 2) 恢复替换前的安装目录
    Restore-ASInstallBackup -InstallDir $installFull -BackupDir (Join-Path $backupRoot 'rollback-install') | Out-Null
    return [pscustomobject]@{ DataFilesRestored = @($restored); DataRoot = $dataRootFull; InstallDir = [System.IO.Path]::GetFullPath($InstallDir) }
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

    # 2) 替换二进制（内部备份安装目录 → 所有权门禁 → 原子换槽；复制失败自动回滚并抛出）。
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
# D1：所有权门禁——非空目录必须拥有绑定本路径的有效标记，否则拒绝；只删除所有权清单
# 中的应用文件，目录仅在为空时删除；UserData=Remove 只删除经本应用标记的 S-PKG 备份。
function Invoke-ASUninstall {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$Root = '',
        [ValidateSet('Keep', 'Remove')][string]$UserData = 'Keep'
    )
    $dataRoot = Get-ASDataRoot -Root $Root
    $dataRootFull = Resolve-ASPath $dataRoot
    if (Test-ASForbiddenPath $dataRootFull) { throw "refusing to use a protected data root: $dataRootFull" }
    $installFull = Resolve-ASPath $InstallDir
    $removed = @()

    $ownership = Test-ASInstallOwnership -InstallDir $installFull
    if (-not $ownership.Ok) { throw "refusing to modify ${installFull}: $($ownership.Message)" }
    if ($ownership.Reason -eq 'owned') {
        $appFiles = @()
        if ($null -ne $ownership.Marker.PSObject.Properties['appFiles']) { $appFiles = @($ownership.Marker.appFiles) }
        Remove-ASOwnedFiles -InstallDir $installFull -AppFiles $appFiles
        $markerPath = Join-Path $installFull $script:OwnerMarkerFile
        if (Test-Path -LiteralPath $markerPath) { Remove-Item -LiteralPath $markerPath -Force; $removed += $markerPath }
        Remove-ASEmptyDirsUnder -InstallDir $installFull
        if (-not (Get-ChildItem -LiteralPath $installFull -Force -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $installFull -Force
            $removed += $installFull
        }
    }

    # 备份是应用拥有的可清理证据；但只有经本应用标记的 S-PKG 备份才会被 Remove 删除。
    if ($UserData -eq 'Remove') {
        $backupRoot = Join-Path $dataRootFull 'backups\spkg'
        if (Test-Path -LiteralPath $backupRoot) {
            if (-not (Test-ASSpkgBackupsOwned -DataRoot $dataRootFull)) {
                throw "refusing to remove user data: spkg backups at $dataRootFull\backups\spkg are not app-owned"
            }
            # D2：备份根链路与整树不得含 junction/symlink，否则 Remove-Item -Recurse 会透明跟随。
            Assert-ASRelPathSafe -BaseDir $dataRootFull -RelativePath 'backups\spkg' -Action 'remove user data backups'
            $bkpTree = Get-ASDirTreeSafe -BaseDir $backupRoot
            if (-not $bkpTree.Ok) { throw "refusing to remove user data: reparse point at '$($bkpTree.ReparsePath)'" }
            Remove-Item -LiteralPath $backupRoot -Recurse -Force
            $removed += $backupRoot
        }
        $backupsDir = Join-Path $dataRootFull 'backups'
        if ((Test-Path -LiteralPath $backupsDir) -and -not (Get-ChildItem -LiteralPath $backupsDir -Force -ErrorAction SilentlyContinue)) {
            Assert-ASRelPathSafe -BaseDir $dataRootFull -RelativePath 'backups' -Action 'remove empty backups dir'
            Remove-Item -LiteralPath $backupsDir -Force
            $removed += $backupsDir
        }
    }

    return [pscustomobject]@{ Removed = @($removed); UserData = $UserData; DataRoot = $dataRootFull; InstallDir = $installFull }
}

# ---- 重装：全新安装目录 + 候选副本 ----
# D1：新安装只允许进入不存在/空目录；已有安装需通过所有权验证，只删除所有权清单中的
# 应用文件后重装，绝不整目录递归删除。
function Invoke-ASReinstall {
    param(
        [Parameter(Mandatory = $true)][string]$CandidateDir,
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string]$Root = ''
    )
    $dataRoot = Get-ASDataRoot -Root $Root
    $dataRootFull = Resolve-ASPath $dataRoot
    if (Test-ASForbiddenPath $dataRootFull) { throw "refusing to use a protected data root: $dataRootFull" }
    $installFull = Resolve-ASPath $InstallDir
    $candFull = Resolve-ASPath $CandidateDir
    if (-not (Test-Path -LiteralPath $candFull)) { throw "candidate dir not found: $candFull" }
    # D2：候选目录自身不得是 junction/symlink/reparse point（其子路径由复制/枚举路径守卫）。
    $candItem = Get-Item -LiteralPath $candFull -Force
    if ($candItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "refusing to use candidate dir: reparse point at $candFull"
    }

    $ownership = Test-ASInstallOwnership -InstallDir $installFull
    if (-not $ownership.Ok) { throw "refusing to modify install dir: $($ownership.Message)" }
    if ($ownership.Reason -eq 'owned') {
        $appFiles = @()
        if ($null -ne $ownership.Marker.PSObject.Properties['appFiles']) { $appFiles = @($ownership.Marker.appFiles) }
        Remove-ASOwnedFiles -InstallDir $installFull -AppFiles $appFiles
        $markerPath = Join-Path $installFull $script:OwnerMarkerFile
        if (Test-Path -LiteralPath $markerPath) {
            Assert-ASRelPathSafe -BaseDir $installFull -RelativePath $script:OwnerMarkerFile -Action 'remove ownership marker'
            Remove-Item -LiteralPath $markerPath -Force
        }
        Remove-ASEmptyDirsUnder -InstallDir $installFull
    } elseif (-not (Test-Path -LiteralPath $installFull)) {
        New-Item -ItemType Directory -Force -Path $installFull | Out-Null
    }
    $copied = @()
    foreach ($item in (Get-ChildItem -LiteralPath $candFull -Force)) {
        $target = Join-Path $installFull $item.Name
        if ($item.PSIsContainer) { Copy-ASDirContents -SourceDir $item.FullName -DestinationDir $target }
        else { Copy-Item -LiteralPath $item.FullName -Destination $target -Force }
        $copied += $item.Name
    }
    $candExe = @(Get-ChildItem -LiteralPath $candFull -Filter 'AutoShutdown-v*.exe' -File -ErrorAction SilentlyContinue | Select-Object -First 1)
    Write-ASOwnerMarker -InstallDir $installFull -AppFiles @(Get-ASRelFileList -BaseDir $candFull) -CandidateName ($candExe | ForEach-Object { $_.Name })
    return [pscustomobject]@{ InstallDir = $installFull; Copied = @($copied); DataRoot = $dataRootFull }
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
