# Builds (optional) and deploys IronNestVR.dll + its Silk.NET deps into the game's BepInEx/plugins.
param([switch]$NoBuild)

$ErrorActionPreference = "Stop"
$src    = "$PSScriptRoot\bin\Release"
$dest   = "C:\Program Files (x86)\Steam\steamapps\common\IRON NEST Heavy Turret Simulator Demo\BepInEx\plugins\IronNestVR"

if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\IronNestVR.csproj" -c Release -v minimal
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Our plugin assembly.
Copy-Item "$src\IronNestVR.dll" $dest -Force

# Bundled third-party deps that are NOT already provided by BepInEx (e.g. Silk.NET). We copy any
# dll in the build output that isn't a reference-only game/loader assembly.
$skip = @("IronNestVR.dll")
Get-ChildItem "$src\*.dll" | Where-Object { $skip -notcontains $_.Name } | ForEach-Object {
    Copy-Item $_.FullName $dest -Force
    Write-Output "  + dep $($_.Name)"
}

Write-Output "Deployed to $dest"
Get-ChildItem $dest | Select-Object Name, Length | Format-Table -AutoSize
