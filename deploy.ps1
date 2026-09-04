# Build the plugin and copy it into the game's BepInEx plugins folder.
param([switch]$Launch)

$ErrorActionPreference = 'Stop'
$Game = "C:\Program Files (x86)\Steam\steamapps\common\Liar's Bar"
$Src  = "$PSScriptRoot\src\LiarsBar8P"
$Dest = "$Game\BepInEx\plugins"

if (Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue) {
    Write-Host "Game is running - close it first." -ForegroundColor Yellow
    exit 1
}

Push-Location $Src
dotnet build -c Release | Select-Object -Last 4
Pop-Location

New-Item -ItemType Directory -Force -Path $Dest | Out-Null
Copy-Item "$Src\bin\Release\net6.0\LiarsBar8P.dll" $Dest -Force
Write-Host "Deployed LiarsBar8P.dll -> $Dest" -ForegroundColor Green

if ($Launch) {
    Remove-Item "$Game\BepInEx\LogOutput.log" -ErrorAction SilentlyContinue
    Start-Process "steam://rungameid/3097560"
    Write-Host "Launched." -ForegroundColor Green
}
