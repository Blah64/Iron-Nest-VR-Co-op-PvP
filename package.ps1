# package.ps1 — build a tester zip from the live game install.
#
#   .\package.ps1            build + deploy the latest plugin (via deploy.ps1), then zip
#   .\package.ps1 -NoBuild   skip the build/deploy, just re-zip what's currently deployed
#
# Output: build\IRON-NEST-VR-Mod.zip  (README + a "game-files" overlay the tester
# drops into their game folder: doorstop loader + dotnet runtime + BepInEx + plugin).
# Pulls from the live game install so the zip always matches what you've tested.
param([switch]$NoBuild)

$ErrorActionPreference = "Stop"

$root   = $PSScriptRoot
$game   = "C:\Program Files (x86)\Steam\steamapps\common\IRON NEST Heavy Turret Simulator Demo"
$readme = "$root\packaging\README.md"
$outDir = "$root\build"
$zip    = "$outDir\IRON-NEST-VR-Mod.zip"

# 1. Build + deploy the latest plugin into the game (deploy.ps1 throws on build failure).
if (-not $NoBuild) { & "$root\deploy.ps1" }

# 2. Stage the overlay the tester drops into their game folder.
$stage = Join-Path $env:TEMP ("invr_pkg_" + [System.Guid]::NewGuid().ToString("N"))
$pkg   = "$stage\IRON-NEST-VR-Mod"
$gf    = "$pkg\game-files"
New-Item -ItemType Directory -Force -Path $gf | Out-Null

Copy-Item $readme "$pkg\README.md" -Force

# Game-root files the tester needs:
#  - doorstop loader (winhttp/doorstop_config/.doorstop_version)
#  - openxr_loader.dll: the native OpenXR loader the VR layer P/Invokes via Silk.NET.
#    It is NOT part of the base demo and must sit in the game root or VR won't init.
$rootFiles = "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "openxr_loader.dll"
foreach ($f in $rootFiles) {
    $p = Join-Path $game $f
    if (-not (Test-Path $p)) { throw "required game-root file missing: $p" }
    Copy-Item $p $gf -Force
}

# BepInEx tree minus the auto-generated interop/cache (regenerated on the tester's
# first launch) and log files. dotnet = the CoreCLR runtime BepInEx loads the plugin with.
robocopy "$game\BepInEx" "$gf\BepInEx" /E /XD "$game\BepInEx\interop" "$game\BepInEx\cache" /XF *.log | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy BepInEx failed ($LASTEXITCODE)" }
robocopy "$game\dotnet" "$gf\dotnet" /E | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy dotnet failed ($LASTEXITCODE)" }
$global:LASTEXITCODE = 0   # robocopy success codes (1-7) aren't errors

# 3. Zip it.
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $pkg -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

$z = Get-Item $zip
Write-Output ("Built {0}  ({1:N1} MB)" -f $z.FullName, ($z.Length / 1MB))
