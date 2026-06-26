# package-public.ps1 — build a PUBLIC (plugin-only) zip.
# Users install BepInEx 6 (IL2CPP) themselves; this ships only the mod + openxr_loader.dll.
#
#   .\package-public.ps1            build + deploy the latest plugin (via deploy.ps1), then zip
#   .\package-public.ps1 -NoBuild   skip the build/deploy, just re-zip what's currently deployed
#
# Output: build\IRON-NEST-VR-Mod-public.zip — README + a "game-files" overlay the user
# merges into their game folder AFTER installing BepInEx:
#     game-files\openxr_loader.dll              (VR native loader, game root)
#     game-files\BepInEx\plugins\IronNestVR\*   (the mod)
# Does NOT bundle BepInEx, the doorstop loader, or the dotnet runtime.
# (For the all-in-one tester build that DOES bundle BepInEx, use package.ps1.)
param([switch]$NoBuild)

$ErrorActionPreference = "Stop"

$root      = $PSScriptRoot
$game      = "C:\Program Files (x86)\Steam\steamapps\common\IRON NEST Heavy Turret Simulator Demo"
$readme    = "$root\packaging\README-public.md"
$outDir    = "$root\build"
$zip       = "$outDir\IRON-NEST-VR-Mod-public.zip"
$pluginSrc = "$game\BepInEx\plugins\IronNestVR"

# 1. Build + deploy the latest plugin into the game (deploy.ps1 throws on build failure).
#    PublicBuild=true ⇒ PUBLIC_BUILD ⇒ crash heartbeat OFF by default in this public zip.
if (-not $NoBuild) { & "$root\deploy.ps1" -BuildProps "-p:PublicBuild=true" }

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

# The plugin itself (DLL + Silk.NET deps + hands.bundle) -> BepInEx\plugins\IronNestVR.
if (-not (Test-Path $pluginSrc)) { throw "plugin not deployed: $pluginSrc (build, or run with -NoBuild after a deploy)" }
robocopy $pluginSrc $pluginDst /E | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy plugin failed ($LASTEXITCODE)" }
$global:LASTEXITCODE = 0   # robocopy success codes (1-7) aren't errors

# 3. Zip it.
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $pkg -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

$z = Get-Item $zip
Write-Output ("Built {0}  ({1:N1} MB)" -f $z.FullName, ($z.Length / 1MB))
