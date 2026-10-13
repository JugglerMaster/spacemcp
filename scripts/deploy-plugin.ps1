<#
.SYNOPSIS
    Builds SpaceMcp.Plugin and stages / deploys it for Space Engineers.

.DESCRIPTION
    The Space Engineers client plugin loader (the community "Plugin Loader" /
    Pulsar) discovers plugins in:

        <SpaceEngineers>\Bin64\Plugins\Local\

    This script:
      1. Resolves the Space Engineers Bin64 folder.
      2. Builds SpaceMcp.Plugin (Release by default).
      3. Works out the minimal runtime dependency set: our own assemblies plus
         any dependency whose version differs from (or is missing in) the game's
         Bin64 folder. Everything else is already provided by the game.
      4. Stages those files into dist\plugin and (unless -StageOnly) copies them
         into <Bin64>\Plugins\Local.
      5. Writes a default SpaceMcp.json in the target folder if one is missing.

    NOTE: The plugin currently references System.Text.Json 8.0.0.5 while the
    game ships 8.0.0.4 and provides no binding redirect for it, so the script
    copies System.Text.Json.dll alongside the plugin. If the game is ever
    updated to ship >= 8.0.0.5, the script will stop copying it automatically.

.PARAMETER GameDir
    SpaceEngineers Bin64 folder (or the game root, or the folder containing it).
    Defaults to the SE_GAME_DIR environment variable, then the Steam uninstall
    registry key for app 244850.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER NoBuild
    Skip the dotnet build and use the existing output.

.PARAMETER Port
    Value written to SpaceMcp.json when the file does not exist yet. Default: 4627.

.PARAMETER OutDir
    Staging folder. Default: <repo>\dist\plugin.

.PARAMETER StageOnly
    Only populate OutDir; do not touch the Space Engineers install.

.PARAMETER Force
    Overwrite an existing SpaceMcp.json in the target folder.

.EXAMPLE
    .\scripts\deploy-plugin.ps1
    Build, stage into dist\plugin and copy into the game's Plugins\Local folder.

.EXAMPLE
    .\scripts\deploy-plugin.ps1 -StageOnly -NoBuild
    Just re-stage the current build into dist\plugin (safe, no game writes).
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [int]$Port = 4627,
    [string]$OutDir,
    [switch]$StageOnly,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'dist\plugin' }

function Resolve-Bin64 {
    param([string]$Candidate)

    if ($Candidate) {
        $full = [System.IO.Path]::GetFullPath($Candidate)
        if (Test-Path (Join-Path $full 'SpaceEngineers.exe')) { return $full }
        $sub = Join-Path $full 'Bin64'
        if (Test-Path (Join-Path $sub 'SpaceEngineers.exe')) { return $sub }
        throw "Could not find SpaceEngineers.exe under '$Candidate'."
    }

    if ($env:SE_GAME_DIR -and (Test-Path (Join-Path $env:SE_GAME_DIR 'SpaceEngineers.exe'))) {
        return [System.IO.Path]::GetFullPath($env:SE_GAME_DIR)
    }

    foreach ($view in @('Registry64', 'Registry32')) {
        $key = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 244850"
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($item.InstallLocation) {
                $sub = Join-Path $item.InstallLocation 'Bin64'
                if (Test-Path (Join-Path $sub 'SpaceEngineers.exe')) { return $sub }
            }
        } catch { }
    }

    throw "Space Engineers not found. Pass -GameDir, or set the SE_GAME_DIR environment variable."
}

$bin64 = Resolve-Bin64 -Candidate $GameDir
Write-Host "Space Engineers Bin64 : $bin64"

# Directory.Build.props reads SE_GAME_DIR, so make sure it is set for the build.
$env:SE_GAME_DIR = $bin64

$pluginProject = Join-Path $repoRoot 'src\SpaceMcp.Plugin\SpaceMcp.Plugin.csproj'
$pluginOut     = Join-Path $repoRoot "src\SpaceMcp.Plugin\bin\$Configuration\net48"

if (-not $NoBuild) {
    Write-Host "Building SpaceMcp.Plugin ($Configuration)..."
    & dotnet build $pluginProject -c $Configuration -v:minimal -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path (Join-Path $pluginOut 'SpaceMcp.Plugin.dll'))) {
    throw "Plugin output not found at '$pluginOut'. Run without -NoBuild first."
}

# --- Work out the minimal file set -------------------------------------------------
$always = @('SpaceMcp.Plugin.dll', 'SpaceMcp.Protocol.dll')
$files  = New-Object System.Collections.Generic.List[string]
foreach ($name in $always) { $files.Add($name) }

foreach ($dll in Get-ChildItem $pluginOut -Filter *.dll) {
    if ($files.Contains($dll.Name)) { continue }

    $gamePath = Join-Path $bin64 $dll.Name
    if (-not (Test-Path $gamePath)) {
        # Dependency the game does not ship at all -> must travel with the plugin.
        $files.Add($dll.Name)
        continue
    }

    try {
        $mine = [System.Reflection.AssemblyName]::GetAssemblyName($dll.FullName).Version
        $game = [System.Reflection.AssemblyName]::GetAssemblyName($gamePath).Version
        if ($mine -ne $game) { $files.Add($dll.Name) }
    } catch {
        # Native / unreadable as a managed assembly -> already provided by the game.
    }
}

# --- Stage -------------------------------------------------------------------------
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

foreach ($name in $files) {
    Copy-Item (Join-Path $pluginOut $name) (Join-Path $OutDir $name) -Force
}

$configPath = Join-Path $OutDir 'SpaceMcp.json'
$configText = "{`n  `"port`": $Port`n}`n"
if ($Force -or -not (Test-Path $configPath)) {
    Set-Content -Path $configPath -Value $configText -Encoding UTF8
}

Write-Host ""
Write-Host "Staged $($files.Count) assemblies + SpaceMcp.json into:"
Write-Host "  $OutDir"
foreach ($name in $files) { Write-Host "    + $name" }

# --- Install -----------------------------------------------------------------------
if ($StageOnly) {
    Write-Host ""
    Write-Host "StageOnly specified; Space Engineers was not modified."
    Write-Host "Deploy for real with:  .\scripts\deploy-plugin.ps1 -NoBuild"
    return
}

$target = Join-Path $bin64 'Plugins\Local'
if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }

if (Get-Process -Name 'SpaceEngineers' -ErrorAction SilentlyContinue) {
    Write-Warning "SpaceEngineers is running; the DLL copy will fail until the game is closed."
}

foreach ($name in $files) {
    $dest = Join-Path $target $name
    $copied = $false
    for ($i = 0; $i -lt 10 -and -not $copied; $i++) {
        try {
            Copy-Item (Join-Path $OutDir $name) $dest -Force
            $copied = $true
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $copied) { throw "Could not copy '$name' to '$target' (file locked?)." }
}

if ($Force -or -not (Test-Path (Join-Path $target 'SpaceMcp.json'))) {
    Set-Content -Path (Join-Path $target 'SpaceMcp.json') -Value $configText -Encoding UTF8
}

Write-Host ""
Write-Host "Deployed to:"
Write-Host "  $target"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Start Space Engineers and enable the plugin in the Plugin Loader menu."
Write-Host "  2. Load a world that has a programmable block."
Write-Host "  3. Register SpaceMcp.McpServer in your AI client (see docs\DEPLOY.md)."
