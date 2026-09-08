<#
  Removes the 8 Player mod from Liar's Bar, and the BepInEx loader with it if no other
  mod is using it. Game files are never touched, and neither is anybody else's plugin.
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
# Join-Path throws on a null Path, and $steam is null when Steam is not in the registry -
# so this died with a raw PowerShell error instead of reaching the "type the folder in
# yourself" prompt. The same bug was fixed in both installers; it was left here.
$vdf = if ($steam) { Join-Path $steam 'steamapps\libraryfolders.vdf' } else { $null }
if ($vdf -and (Test-Path $vdf)) {
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

# This mod's own files first.
$removed = 0
foreach ($f in @('BepInEx\plugins\LiarsBar8P.dll',
                 'BepInEx\config\liarsbar.eightplayers.cfg')) {
    $p = Join-Path $game $f
    if (Test-Path $p) {
        try { Remove-Item $p -Force; Good "removed $(Split-Path $f -Leaf)"; $removed++ }
        catch { Bad "could not remove $(Split-Path $f -Leaf) : $($_.Exception.Message)" }
    }
}

# BepInEx itself is shared, so it only goes if nothing else is using it.
#
# This used to delete the whole BepInEx and dotnet trees unconditionally, while the header
# promised it "only removes files the installer added". BepInEx is a loader other mods sit
# in: uninstalling this one silently took every other mod in the plugins folder with it. If
# somebody else's plugin is there, the loader stays and only this mod's own files go.
$plugDir = Join-Path $game 'BepInEx\plugins'
$others = @()
if (Test-Path $plugDir) {
    $others = @(Get-ChildItem $plugDir -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -eq '.dll' })
}

if ($others.Count -gt 0) {
    Say ""
    Say "Leaving BepInEx in place - $($others.Count) other plugin(s) are using it:" Yellow
    $others | ForEach-Object { Say "         $($_.Name)" Gray }
} else {
    foreach ($t in @('BepInEx', 'dotnet', 'winhttp.dll', 'doorstop_config.ini',
                     '.doorstop_version', 'changelog.txt')) {
        $p = Join-Path $game $t
        if (Test-Path $p) {
            try { Remove-Item $p -Recurse -Force; Good "removed $t"; $removed++ }
            catch { Bad "could not remove $t : $($_.Exception.Message)" }
        }
    }
}

Say ""
if ($removed -eq 0) {
    Say "Nothing to remove - the mod was not installed." Yellow
} else {
    Say "============================================" Green
    if ($others.Count -gt 0) {
        Say "  8 Player mod removed. BepInEx and your other" Green
        Say "  mods were left alone." Green
    } else {
        Say "  Uninstalled. The game is back to vanilla." Green
    }
    Say "============================================" Green
    Say ""
    Say "  Game files themselves were never modified, so Steam's" Gray
    Say "  'Verify integrity of game files' is also always available." Gray
}
