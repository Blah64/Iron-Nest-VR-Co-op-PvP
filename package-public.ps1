# package-public.ps1 - build a PUBLIC (plugin-only) zip.
# Users install BepInEx 6 (IL2CPP) themselves; this ships only the mod + openxr_loader.dll.
#
#   .\package-public.ps1            build directly (no deploy), then zip
#   .\package-public.ps1 -NoBuild   skip the build; stage from existing bin\Release + BundleOutput
#   .\package-public.ps1 -GameDir "D:\..." override the game path (used for build refs + openxr_loader.dll)
#
# Output: build\IRON-NEST-VR-Mod-public.zip - README + a "game-files" overlay the user
# merges into their game folder AFTER installing BepInEx:
#     game-files\openxr_loader.dll              (VR native loader, game root)
#     game-files\BepInEx\plugins\IronNestVR\*   (the mod)
# Does NOT bundle BepInEx, the doorstop loader, or the dotnet runtime.
# (For the all-in-one tester build that DOES bundle BepInEx, use package.ps1.)
param([switch]$NoBuild, [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\IRON NEST Heavy Turret Simulator Demo")

$ErrorActionPreference = "Stop"
$game = $GameDir

$root      = $PSScriptRoot
$readme    = "$root\packaging\README-public.md"
$outDir    = "$root\build"
$zip       = "$outDir\IRON-NEST-VR-Mod-public.zip"

# 1. Build directly (never via deploy.ps1) so staging never depends on the deployed plugin folder.
#    PublicBuild=true - PUBLIC_BUILD - crash heartbeat OFF by default in this public zip.
#    GameDir is passed to the build so the csproj can resolve BepInEx/interop assembly references.
if (-not $NoBuild) {
    dotnet build "$root\IronNestVR.csproj" -c Release -v minimal -p:PublicBuild=true -p:GameDir="$GameDir"
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

# 2. Stage a plugin-only overlay.
$stage     = Join-Path $env:TEMP ("invr_pub_" + [System.Guid]::NewGuid().ToString("N"))
$pkg       = "$stage\IRON-NEST-VR-Mod"
$gf        = "$pkg\game-files"
$pluginDst = "$gf\BepInEx\plugins\IronNestVR"
New-Item -ItemType Directory -Force -Path $pluginDst | Out-Null

Copy-Item $readme "$pkg\README.md" -Force

# openxr_loader.dll -> game root (VR native dependency; not part of the base demo or BepInEx).
$oxr = Join-Path $game "openxr_loader.dll"
if (-not (Test-Path $oxr)) { throw "required game-root file missing: $oxr" }
Copy-Item $oxr $gf -Force

# The plugin itself -> BepInEx\plugins\IronNestVR.
# Stage ONLY known canonical files - never robocopy the deployed folder, which is mutable
# runtime/deploy state and could leak stale or experimental non-DLL files into the public zip.

# Copy top-level build DLLs from bin\Release (runtimes\ subfolder is intentionally excluded).
$binRelease = "$root\bin\Release"
if (-not (Test-Path "$binRelease\IronNestVR.dll")) {
    throw "required build output missing: $binRelease\IronNestVR.dll - build first, or run without -NoBuild"
}
Get-ChildItem "$binRelease\*.dll" | ForEach-Object { Copy-Item $_.FullName $pluginDst -Force }

# Copy the canonical asset bundle from its in-repo BundleOutput (not the deployed copy).
$handsSrc = "$root\IronNestHands\IronNestHands\BundleOutput\hands.bundle"
if (-not (Test-Path $handsSrc)) { throw "required asset missing: $handsSrc" }
Copy-Item $handsSrc $pluginDst -Force

# Verify both required files made it into the staging folder.
if (-not (Test-Path "$pluginDst\IronNestVR.dll"))  { throw "required file missing from staged plugin: IronNestVR.dll" }
if (-not (Test-Path "$pluginDst\hands.bundle"))     { throw "required file missing from staged plugin: hands.bundle" }

# 3. Zip it.
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $pkg -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

$z = Get-Item $zip
Write-Output ("Built {0}  ({1:N1} MB)" -f $z.FullName, ($z.Length / 1MB))
