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

# Finding the game uses -LiteralPath too, as the removal below always has. A plain path is
# read as a wildcard pattern, in which [ and ] mean "any one of these characters" - so a
# Steam library in a folder like "D:\Games [SSD]" matched nothing, and the game was never
# found there, nor accepted when its folder was pasted in by hand.
function Get-SteamRoot {
    foreach ($k in @('HKCU:\Software\Valve\Steam',
                     'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
                     'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $p = Get-ItemProperty $k -ErrorAction Stop
            foreach ($v in @($p.SteamPath, $p.InstallPath)) {
                if ($v -and (Test-Path -LiteralPath $v)) { return $v }
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
if ($vdf -and (Test-Path -LiteralPath $vdf)) {
    foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
        $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
    }
}

$game = $null
foreach ($lib in $libs) {
    $man = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
    if (Test-Path -LiteralPath $man) {
        $c = Get-Content -LiteralPath $man -Raw
        if ($c -match '"installdir"\s+"([^"]+)"') {
            $d = Join-Path $lib "steamapps\common\$($Matches[1])"
            if (Test-Path -LiteralPath (Join-Path $d "Liar's Bar.exe")) { $game = $d; break }
        }
    }
}
if (-not $game) {
    foreach ($lib in $libs) {
        $d = Join-Path $lib "steamapps\common\Liar's Bar"
        if (Test-Path -LiteralPath (Join-Path $d "Liar's Bar.exe")) { $game = $d; break }
    }
}

if (-not $game) {
    $typed = Read-Host "  Could not find the game. Paste the Liar's Bar folder path (Enter to cancel)"
    if ([string]::IsNullOrWhiteSpace($typed)) { Bad "Cancelled."; exit 1 }
    $game = $typed.Trim('"').Trim()
    if (-not (Test-Path -LiteralPath (Join-Path $game "Liar's Bar.exe"))) { Bad "Not a Liar's Bar folder."; exit 1 }
}
Good "Game: $game"

if (Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue) {
    Say ""
    Bad "Liar's Bar is running. Close it and run this again."
    exit 1
}

Say ""
Say "Removing mod files..."

# This mod's own files first: the plugin, its settings, and any older copy of either -
# the same set the installer clears, so a leftover copy of this mod cannot be mistaken
# below for somebody else's plugin and keep the loader installed.
$removed = 0
$plugDir = Join-Path $game 'BepInEx\plugins'
$cfgDir  = Join-Path $game 'BepInEx\config'
$mine = @()
if (Test-Path -LiteralPath $plugDir) {
    $mine += @(Get-ChildItem -LiteralPath $plugDir -Filter '*LiarsBar8P*' -File -Force -ErrorAction SilentlyContinue)
}
if (Test-Path -LiteralPath $cfgDir) {
    $mine += @(Get-ChildItem -LiteralPath $cfgDir -Filter '*liarsbar.eightplayers.cfg' -File -Force -ErrorAction SilentlyContinue)
}
foreach ($f in $mine) {
    try { Remove-Item -LiteralPath $f.FullName -Force; Good "removed $($f.Name)"; $removed++ }
    catch { Bad "could not remove $($f.Name) : $($_.Exception.Message)" }
}

# BepInEx itself is shared, so it only goes if nothing else is using it.
#
# This used to delete the whole BepInEx and dotnet trees unconditionally, while the header
# promised it "only removes files the installer added". BepInEx is a loader other mods sit
# in: uninstalling this one silently took every other mod in the plugins folder with it.
#
# Then "nothing else" meant no .dll left in plugins, which was not enough either: a mod
# switched off by renaming it to .dll.disabled or .dll.old, or a preloader patcher in
# patchers, is not a .dll in plugins - so the loader was deleted from under it, and with it
# every other mod's settings in BepInEx\config. With this mod's own files gone, anything
# still in plugins\ or patchers\ is somebody else's - a DLL, one switched off by renaming
# it, a folder of them, a link to one kept elsewhere - and keeps the loader installed.
$others = @()
foreach ($d in @('plugins', 'patchers')) {
    $dir = Join-Path $game "BepInEx\$d"
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    $root = (Get-Item -LiteralPath $dir -Force).FullName.TrimEnd('\')
    $others += @(Get-ChildItem -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue |
                 Where-Object { -not $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
                 ForEach-Object { "$d\" + $_.FullName.Substring($root.Length + 1) })
}

$kept = @()
if ($others.Count -gt 0) {
    Say ""
    Say "Leaving BepInEx in place - $($others.Count) other mod file(s) are using it:" Yellow
    $others | ForEach-Object { Say "         $_" Gray }
} else {
    foreach ($t in @('dotnet', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')) {
        $p = Join-Path $game $t
        if (Test-Path -LiteralPath $p) {
            try { Remove-Item -LiteralPath $p -Recurse -Force; Good "removed $t"; $removed++ }
            catch { Bad "could not remove $t : $($_.Exception.Message)" }
        }
    }
    # The rest of BepInEx is the loader's own - its core, the interop it generated, its
    # cache, its log, its own settings - except for other mods' settings files, which
    # outlive their mods and are kept in case the mod comes back.
    if (Test-Path -LiteralPath $cfgDir) {
        $kept = @(Get-ChildItem -LiteralPath $cfgDir -Force -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -ne 'BepInEx.cfg' })
    }
    $bep = Join-Path $game 'BepInEx'
    if (Test-Path -LiteralPath $bep) {
        try {
            if ($kept.Count -eq 0) {
                Remove-Item -LiteralPath $bep -Recurse -Force
                Good "removed BepInEx"
            } else {
                Get-ChildItem -LiteralPath $bep -Force | Where-Object { $_.Name -ne 'config' } |
                    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
                $bepCfg = Join-Path $cfgDir 'BepInEx.cfg'
                if (Test-Path -LiteralPath $bepCfg) { Remove-Item -LiteralPath $bepCfg -Force }
                Good "removed BepInEx, keeping other mods' settings:"
                $kept | ForEach-Object { Say "         config\$($_.Name)" Gray }
            }
            $removed++
        } catch { Bad "could not remove all of BepInEx : $($_.Exception.Message)" }
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
    } elseif ($kept.Count -gt 0) {
        Say "  Uninstalled. The game is back to vanilla; other" Green
        Say "  mods' settings were kept in BepInEx\config." Green
    } else {
        Say "  Uninstalled. The game is back to vanilla." Green
    }
    Say "============================================" Green
    Say ""
    Say "  The game's own files were never modified." Gray
}
