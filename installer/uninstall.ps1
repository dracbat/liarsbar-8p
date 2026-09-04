<#
  Removes the 8 Player mod and the BepInEx loader from Liar's Bar.
  Only removes files the installer added; game files are never touched.
#>

$ErrorActionPreference = 'Stop'
$AppId = '3097560'

function Say  ($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }
function Good ($m) { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Bad  ($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }

Say ""
Say "============================================" Cyan
Say "  Liar's Bar - 8 Player Mod  :  Uninstaller" Cyan
Say "============================================" Cyan
Say ""

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
    return $null
}

$steam = Get-SteamRoot
$libs  = New-Object System.Collections.Generic.List[string]
if ($steam) { $libs.Add($steam) }
$vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
if ($steam -and (Test-Path $vdf)) {
    foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
        $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
    }
}

$game = $null
foreach ($lib in $libs) {
    $man = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
    if (Test-Path $man) {
        $c = Get-Content $man -Raw
        if ($c -match '"installdir"\s+"([^"]+)"') {
            $d = Join-Path $lib "steamapps\common\$($Matches[1])"
            if (Test-Path (Join-Path $d "Liar's Bar.exe")) { $game = $d; break }
        }
    }
}
if (-not $game) {
    foreach ($lib in $libs) {
        $d = Join-Path $lib "steamapps\common\Liar's Bar"
        if (Test-Path (Join-Path $d "Liar's Bar.exe")) { $game = $d; break }
    }
}

if (-not $game) {
    $typed = Read-Host "  Could not find the game. Paste the Liar's Bar folder path (Enter to cancel)"
    if ([string]::IsNullOrWhiteSpace($typed)) { Bad "Cancelled."; exit 1 }
    $game = $typed.Trim('"').Trim()
    if (-not (Test-Path (Join-Path $game "Liar's Bar.exe"))) { Bad "Not a Liar's Bar folder."; exit 1 }
}
Good "Game: $game"

if (Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue) {
    Say ""
    Bad "Liar's Bar is running. Close it and run this again."
    exit 1
}

Say ""
Say "Removing mod files..."

# Only things the installer put there.
$targets = @('BepInEx', 'dotnet', 'winhttp.dll', 'doorstop_config.ini',
             '.doorstop_version', 'changelog.txt')

$removed = 0
foreach ($t in $targets) {
    $p = Join-Path $game $t
    if (Test-Path $p) {
        try { Remove-Item $p -Recurse -Force; Good "removed $t"; $removed++ }
        catch { Bad "could not remove $t : $($_.Exception.Message)" }
    }
}

Say ""
if ($removed -eq 0) {
    Say "Nothing to remove - the mod was not installed." Yellow
} else {
    Say "============================================" Green
    Say "  Uninstalled. The game is back to vanilla." Green
    Say "============================================" Green
    Say ""
    Say "  Game files themselves were never modified, so Steam's" Gray
    Say "  'Verify integrity of game files' is also always available." Gray
}
