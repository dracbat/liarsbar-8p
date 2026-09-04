<#
  Builds a distributable zip for other players.

  Everyone in the lobby must run the same mod and the same MaxPlayers value, so this
  bundles the loader, the plugin, a pre-set config and a one-click installer together
  rather than expecting each person to configure it themselves.
#>
param([int]$MaxPlayers = 8)

$ErrorActionPreference = 'Stop'
$Root    = $PSScriptRoot
$Staging = "$Root\dist\LiarsBar-8P"
$Zip     = "$Root\dist\LiarsBar-8P.zip"

if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Staging | Out-Null

# 1. loader (as downloaded, unmodified)
Copy-Item "$Root\tools\bepinex-staging\*" $Staging -Recurse -Force

# 2. plugin
$plug = "$Staging\BepInEx\plugins"
New-Item -ItemType Directory -Force -Path $plug | Out-Null
Copy-Item "$Root\src\LiarsBar8P\bin\Release\net6.0\LiarsBar8P.dll" $plug -Force

# 3. pre-set config so every copy agrees
$cfgDir = "$Staging\BepInEx\config"
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
@"
## Settings file was created by plugin Liar's Bar 8 Players
## Plugin GUID: josh.liarsbar.eightplayers

[General]

## Maximum players per lobby. Every player must use the same value.
# Setting type: Int32
# Default value: 8
MaxPlayers = $MaxPlayers

[Gameplay]

## Scale the Liar's Deck proportionally with player count so everyone still gets five cards.
# Setting type: Boolean
# Default value: true
ScaleDeck = true

[Debug]

## Dump runtime seat/slot/prefab counts to the log.
# Setting type: Boolean
# Default value: true
VerboseDiagnostics = true

## Development self test: auto-host a PRIVATE lobby on startup.
# Setting type: Boolean
# Default value: false
SelfTestAutoHostLobby = false

## Development self test: force a solo match start.
# Setting type: Boolean
# Default value: false
SelfTestForceSoloStart = false
"@ | Set-Content "$cfgDir\josh.liarsbar.eightplayers.cfg" -Encoding utf8

# 4. one-click installer / uninstaller
Copy-Item "$Root\installer\install.bat"     $Staging -Force
Copy-Item "$Root\installer\install.ps1"     $Staging -Force
Copy-Item "$Root\installer\uninstall.bat"   $Staging -Force
Copy-Item "$Root\installer\uninstall.ps1"   $Staging -Force

Copy-Item "$Root\README.md" $Staging -Force

@"
Liar's Bar - $MaxPlayers Player Mod
===================================

EVERY player must install this, and everyone must use the same MaxPlayers value.
A vanilla client joining a modded lobby will desync.

INSTALL
  1. Extract this whole zip somewhere (Desktop is fine).
  2. Double click  install.bat
     It will ask for administrator permission - that is needed because the game
     lives in Program Files. It finds Liar's Bar through Steam automatically.
  3. Launch the game. The FIRST launch is slow (a few minutes) while it sets up.
     This happens once. Let it reach the main menu.

If install.bat cannot find the game, it will ask you to paste the folder path.
You can get that from Steam:
  right click Liar's Bar -> Manage -> Browse local files -> copy the address bar.

CHECK IT WORKED
  Open  BepInEx\LogOutput.log  in the game folder and look for:
     === Liar's Bar 8P loaded ===
     [cap] maxConnections 4 -> 8

SETTINGS
  BepInEx\config\josh.liarsbar.eightplayers.cfg
  MaxPlayers must be the SAME for everyone playing together.

UNINSTALL
  Double click  uninstall.bat
  Nothing in the game itself is modified, so Steam's
  "Verify integrity of game files" also restores everything.
"@ | Set-Content "$Staging\INSTALL.txt" -Encoding utf8

if (Test-Path $Zip) { Remove-Item $Zip -Force }
Compress-Archive -Path "$Staging\*" -DestinationPath $Zip
Write-Host "Packaged -> $Zip" -ForegroundColor Green
Get-Item $Zip | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
