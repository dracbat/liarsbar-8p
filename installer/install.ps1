<#
  Liar's Bar - 8 Player Mod installer.
  Finds the game through Steam's own registry keys and library config, then copies
  the loader and plugin in. Nothing in the game folder is modified or deleted.
#>

$ErrorActionPreference = 'Stop'
$AppId = '3097560'
$Here  = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say  ($m, $c = 'Gray')  { Write-Host $m -ForegroundColor $c }
function Good ($m) { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Bad  ($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }
function Warn ($m) { Write-Host "  [!]    $m" -ForegroundColor Yellow }

Say ""
Say "==========================================" Cyan
Say "  Liar's Bar - 8 Player Mod  :  Installer" Cyan
Say "==========================================" Cyan
Say ""

# ---------------------------------------------------------------- find Steam
function Get-SteamRoot {
    foreach ($k in @('HKCU:\Software\Valve\Steam',
                     'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
                     'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $p = Get-ItemProperty $k -ErrorAction Stop
            foreach ($v in @($p.SteamPath, $p.InstallPath)) {
                if ($v -and (Test-Path $v)) { return $v }
            }
        } catch { }
    }
    foreach ($g in @("$env:ProgramFiles(x86)\Steam", "$env:ProgramFiles\Steam", "C:\Steam")) {
        if ($g -and (Test-Path $g)) { return $g }
    }
    return $null
}

# --------------------------------------------------------------- find game
function Get-GameDir {
    param($SteamRoot)

    $libs = New-Object System.Collections.Generic.List[string]
    if ($SteamRoot) { $libs.Add($SteamRoot) }

    $vdf = Join-Path $SteamRoot 'steamapps\libraryfolders.vdf'
    if ($SteamRoot -and (Test-Path $vdf)) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
        }
    }

    # a library that actually declares the app wins
    foreach ($lib in $libs) {
        $man = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (Test-Path $man) {
            $c = Get-Content $man -Raw
            if ($c -match '"installdir"\s+"([^"]+)"') {
                $d = Join-Path $lib "steamapps\common\$($Matches[1])"
                if (Test-Path (Join-Path $d "Liar's Bar.exe")) { return $d }
            }
        }
    }
    # fall back to the conventional folder name
    foreach ($lib in $libs) {
        $d = Join-Path $lib "steamapps\common\Liar's Bar"
        if (Test-Path (Join-Path $d "Liar's Bar.exe")) { return $d }
    }
    return $null
}

Say "Looking for Liar's Bar..."
$steam = Get-SteamRoot
if ($steam) { Good "Steam: $steam" } else { Warn "Steam not found in the registry" }

$game = Get-GameDir -SteamRoot $steam

# last resort: let the user point at it
if (-not $game) {
    Bad "Could not find Liar's Bar automatically."
    Say ""
    Say "  In Steam: right click Liar's Bar -> Manage -> Browse local files," Gray
    Say "  then copy the folder path from the address bar and paste it below." Gray
    Say ""
    $typed = Read-Host "  Paste the Liar's Bar folder path (or press Enter to cancel)"
    if ([string]::IsNullOrWhiteSpace($typed)) { Say ""; Bad "Cancelled."; exit 1 }
    $typed = $typed.Trim('"').Trim()
    if (-not (Test-Path (Join-Path $typed "Liar's Bar.exe"))) {
        Bad "No `"Liar's Bar.exe`" in that folder. Nothing was changed."
        exit 1
    }
    $game = $typed
}
Good "Game: $game"

# ------------------------------------------------------------ safety checks
if (Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue) {
    Say ""
    Bad "Liar's Bar is running. Close the game and run this again."
    exit 1
}

$payload = @('BepInEx', 'dotnet', 'winhttp.dll', 'doorstop_config.ini',
             '.doorstop_version', 'changelog.txt')
$missing = $payload | Where-Object { -not (Test-Path (Join-Path $Here $_)) }
if ($missing -contains 'BepInEx' -or $missing -contains 'winhttp.dll') {
    Say ""
    Bad "This installer is missing its files (BepInEx / winhttp.dll)."
    Say "  Extract the whole zip first, then run install.bat from inside it." Gray
    exit 1
}

# ------------------------------------------------------------------ install
Say ""
Say "Installing..."
$existing = Test-Path (Join-Path $game 'BepInEx\plugins\LiarsBar8P.dll')
if ($existing) { Warn "An existing install was found - it will be updated." }

# keep the user's settings across an update
$cfgPath = Join-Path $game 'BepInEx\config\josh.liarsbar.eightplayers.cfg'
$savedCfg = $null
if (Test-Path $cfgPath) { $savedCfg = Get-Content $cfgPath -Raw }

try {
    foreach ($item in $payload) {
        $src = Join-Path $Here $item
        if (-not (Test-Path $src)) { continue }
        Copy-Item $src -Destination $game -Recurse -Force
    }
    Good "Files copied"
} catch {
    Say ""
    Bad "Copy failed: $($_.Exception.Message)"
    Say "  Try right clicking install.bat and choosing 'Run as administrator'." Gray
    exit 1
}

if ($savedCfg) {
    Set-Content $cfgPath $savedCfg -Encoding utf8
    Good "Kept your existing settings"
}

# ------------------------------------------------------------------- verify
Say ""
Say "Verifying..."
$checks = @{
    'winhttp.dll (loader)'   = Join-Path $game 'winhttp.dll'
    'BepInEx core'           = Join-Path $game 'BepInEx\core\BepInEx.Core.dll'
    '8 Player plugin'        = Join-Path $game 'BepInEx\plugins\LiarsBar8P.dll'
}
$fail = $false
foreach ($k in $checks.Keys | Sort-Object) {
    if (Test-Path $checks[$k]) { Good $k } else { Bad $k; $fail = $true }
}

Say ""
if ($fail) {
    Say "Install INCOMPLETE - see the failures above." Red
    exit 1
}

$mp = 8
if (Test-Path $cfgPath) {
    $m = [regex]::Match((Get-Content $cfgPath -Raw), 'MaxPlayers\s*=\s*(\d+)')
    if ($m.Success) { $mp = $m.Groups[1].Value }
}

Say "==========================================" Green
Say "  Installed successfully - max $mp players" Green
Say "==========================================" Green
Say ""
Say "NEXT:" Cyan
Say "  1. Launch Liar's Bar."
Say "  2. The FIRST launch is slow (a few minutes) while it sets up."
Say "     This happens once. Let it reach the main menu."
Say ""
Say "IMPORTANT:" Yellow
Say "  Everyone you play with needs this same installer," Yellow
Say "  and everyone must use the same MaxPlayers value." Yellow
Say ""
Say "  Settings: BepInEx\config\josh.liarsbar.eightplayers.cfg" Gray
Say "  To remove: run uninstall.bat" Gray
