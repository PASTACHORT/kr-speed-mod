# Kingdom Rush Speed Slider - installer / uninstaller
#   powershell -ExecutionPolicy Bypass -File install.ps1             # install into every game found
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall  # remove it again
# The mod is one file (director.lua) copied into each game's save folder in %APPDATA%.
# No game files are changed. Kingdom Rush Vengeance uses a different engine: see vengeance/KRVSpeed.
param([switch]$Uninstall)

$src = Join-Path $PSScriptRoot "src\director.lua"
$games = [ordered]@{
    "kingdom_rush"           = "Kingdom Rush"
    "kingdom_rush_frontiers" = "Kingdom Rush Frontiers"
    "kingdom_rush_origins"   = "Kingdom Rush Origins"
    "kingdom_rush_alliance"  = "Kingdom Rush 5: Alliance"
    "kingdom_rush_genesis"   = "Kingdom Rush 6: Genesis"
}

foreach ($id in $games.Keys) {
    $dir = Join-Path $env:APPDATA $id
    $name = $games[$id]
    if (-not (Test-Path $dir)) { Write-Host "skip      $name (no save folder - run the game once first)"; continue }
    $dst = Join-Path $dir "director.lua"
    if ($Uninstall) {
        foreach ($f in "director.lua", "speedmod_settings.txt", "speedmod_log.txt") {
            $p = Join-Path $dir $f
            if (Test-Path $p) { Remove-Item $p -Confirm:$false }
        }
        Write-Host "removed   $name"
    } else {
        Copy-Item $src $dst -Force
        Write-Host "installed $name -> $dst"
    }
}
