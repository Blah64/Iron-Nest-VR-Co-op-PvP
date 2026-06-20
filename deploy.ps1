# Builds (optional) and deploys IronNestVR.dll + its Silk.NET deps into the game's BepInEx/plugins.
# Deploys to BOTH the primary Steam install AND the second "Client" copy used for same-machine co-op
# testing (the localhost loopback transport — Ctrl+F1/F2/F3; see LoopbackTransport.cs). The client copy is
# skipped with a warning if it isn't present, so this still works on a machine that only has the primary.
param([switch]$NoBuild, [switch]$PrimaryOnly)

$ErrorActionPreference = "Stop"
$src = "$PSScriptRoot\bin\Release"

# Plugin-folder destinations (one per game instance).
$dests = @(
    "C:\Program Files (x86)\Steam\steamapps\common\IRON NEST Heavy Turret Simulator Demo\BepInEx\plugins\IronNestVR"
)
if (-not $PrimaryOnly) {
    $dests += "F:\Games\IRON NEST Heavy Turret Simulator Demo Client\BepInEx\plugins\IronNestVR"
}

if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\IronNestVR.csproj" -c Release -v minimal
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

# Copy the plugin assembly + bundled third-party deps (anything in bin\Release that isn't a reference-only
# game/loader assembly, e.g. Silk.NET) into one plugin folder.
function Deploy-To([string]$dest) {
    # Only deploy where the game/BepInEx actually lives — the plugins parent must already exist. This avoids
    # silently creating a stray IronNestVR folder on a machine that lacks the second copy.
    $pluginsParent = Split-Path $dest -Parent
    if (-not (Test-Path $pluginsParent)) {
        Write-Warning "skipping (no BepInEx\plugins here): $dest"
        return
    }

    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item "$src\IronNestVR.dll" $dest -Force

    $skip = @("IronNestVR.dll")
    Get-ChildItem "$src\*.dll" | Where-Object { $skip -notcontains $_.Name } | ForEach-Object {
        Copy-Item $_.FullName $dest -Force
        Write-Output "  + dep $($_.Name)"
    }
    Write-Output "Deployed to $dest"
}

foreach ($d in $dests) { Deploy-To $d }

Write-Output ""
Write-Output "=== Deployed plugin (primary) ==="
Get-ChildItem $dests[0] | Select-Object Name, Length | Format-Table -AutoSize
