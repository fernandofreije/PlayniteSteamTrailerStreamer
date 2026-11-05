# Builds the extension and packs it into a .pext using Playnite's Toolbox.
param(
    [string]$Configuration = "Release",
    [string]$PlaynitePath = "$env:LOCALAPPDATA\Playnite"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$outDir = Join-Path $root "src\bin\$Configuration"
$distDir = Join-Path $root "dist"

dotnet build (Join-Path $root "src\SteamTrailerStreamer.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$toolbox = Join-Path $PlaynitePath "Toolbox.exe"
if (-not (Test-Path $toolbox)) { throw "Toolbox.exe not found in $PlaynitePath. Pass -PlaynitePath." }

New-Item -ItemType Directory -Force $distDir | Out-Null
& $toolbox pack $outDir $distDir
if ($LASTEXITCODE -ne 0) { throw "Packing failed" }

Get-ChildItem $distDir -Filter *.pext | Sort-Object LastWriteTime -Descending | Select-Object -First 1
