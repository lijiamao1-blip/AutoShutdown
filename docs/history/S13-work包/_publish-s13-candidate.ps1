# S13 temp candidate EXE build script (T09, one-shot evidence; does NOT touch tools/Publish-SafeRelease.ps1)
# GATE-Q1 decision: {BUILD} = S13 source Git short hash (GATE-SCM scheme A, traceable).
# GATE-Q2 decision: candidate dir artifacts/release/v2.0.0/S13-{BUILD}/ (existing v1.0.0 layout untouched).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # project root = parent of this work-package folder
$build = '4289bc1'
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source   # SDK 8.0.130 on PATH
if (-not $dotnet) { Write-Host 'ERROR: no .NET SDK found.'; exit 10 }

$outDir = Join-Path $root "artifacts\release\v2.0.0\S13-$build"
$stage = Join-Path $outDir 'publish'
$manifestsDir = Join-Path $root 'artifacts\release\v2.0.0\manifests'
$asm = "AutoShutdown-v2.0.0-S13.$build"
$candidate = Join-Path $outDir "$asm.exe"

# Best-effort cleanup only: the directory may be momentarily held by AV scanning;
# publish overwrites files in place regardless.
try { if (Test-Path -LiteralPath $outDir) { Remove-Item -Recurse -Force -ErrorAction SilentlyContinue -LiteralPath $outDir } } catch { }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Single-file + self-contained: the EXE embeds assemblies and runs under any
# filename, so renaming it to the S-PKG-4 candidate name is safe.
& $dotnet publish (Join-Path $root 'src\AutoShutdown.App\AutoShutdown.App.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $outDir | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Rename-Item -LiteralPath (Join-Path $outDir 'AutoShutdown.App.exe') -NewName "$asm.exe"
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $candidate).Hash.ToLowerInvariant()

New-Item -ItemType Directory -Force -Path $manifestsDir | Out-Null
$manifest = Join-Path $manifestsDir "S13-$build-candidate.txt"
@"
candidate: AutoShutdown-v2.0.0-S13.$build.exe
source-commit: $build
sha256: $hash
publish: dotnet publish src/AutoShutdown.App/AutoShutdown.App.csproj -c Release -r win-x64 --self-contained true
sdk: $(& $dotnet --version)
"@ | Set-Content -Encoding ascii -LiteralPath $manifest

Write-Host "CANDIDATE=$candidate"
Write-Host "SHA256=$hash"
Write-Host "MANIFEST=$manifest"
