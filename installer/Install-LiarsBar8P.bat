@echo off
setlocal
title Liar's Bar - 8 Player Mod (online installer)

rem ---------------------------------------------------------------------------
rem  One-file installer. Removes any previous install of this mod, then downloads
rem  and installs the latest release from GitHub.
rem
rem  This is a batch script with a PowerShell script embedded at the end of the
rem  file. cmd exits before reaching it, so you can open this in Notepad and read
rem  exactly what it will do before you run it.
rem ---------------------------------------------------------------------------

rem The game lives under Program Files, so writing to it needs administrator rights.
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$c=[IO.File]::ReadAllText('%~f0'); Invoke-Expression $c.Substring($c.LastIndexOf('#PS_BEGIN')+9)"

echo.
pause
exit /b

#PS_BEGIN
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

$Repo  = 'dracbat/liarsbar-8p'
$AppId = '3097560'

function Say  ($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }
function Good ($m) { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Bad  ($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }
function Warn ($m) { Write-Host "  [!]    $m" -ForegroundColor Yellow }

Say ""
Say "=================================================" Cyan
Say "  Liar's Bar - 8 Player Mod  :  Online Installer" Cyan
Say "=================================================" Cyan
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
    foreach ($g in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) {
        if ($g -and (Test-Path $g)) { return $g }
    }
    return $null
}

function Get-GameDir ($SteamRoot) {
    $libs = New-Object System.Collections.Generic.List[string]
    if ($SteamRoot) { $libs.Add($SteamRoot) }
    $vdf = Join-Path $SteamRoot 'steamapps\libraryfolders.vdf'
    if ($SteamRoot -and (Test-Path $vdf)) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
        }
    }
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
    foreach ($lib in $libs) {
        $d = Join-Path $lib "steamapps\common\Liar's Bar"
        if (Test-Path (Join-Path $d "Liar's Bar.exe")) { return $d }
    }
    return $null
}

Say "Looking for Liar's Bar..."
$game = Get-GameDir (Get-SteamRoot)
if (-not $game) {
    Bad "Could not find Liar's Bar automatically."
    Say ""
    Say "  In Steam: right click Liar's Bar -> Manage -> Browse local files," Gray
    Say "  then copy the folder path from the address bar and paste it below." Gray
    Say ""
    $typed = Read-Host "  Paste the Liar's Bar folder path (Enter to cancel)"
    if ([string]::IsNullOrWhiteSpace($typed)) { Bad "Cancelled."; exit 1 }
    $game = $typed.Trim('"').Trim()
    if (-not (Test-Path (Join-Path $game "Liar's Bar.exe"))) {
        Bad "That folder does not contain the game executable. Nothing was changed."
        exit 1
    }
}
Good "Game: $game"

if (Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue) {
    Say ""
    Bad "Liar's Bar is running. Close the game and run this again."
    exit 1
}

# A stale plugin or leftover config has broken sessions before: an old setting
# survived an update and silently disabled a fix. Every install now starts from a
# clean slate for this mod's own files. BepInEx's generated interop folder is left
# alone on purpose - it is expensive to rebuild and is not ours.
Say ""
Say "Removing any previous install of this mod..."
$gone = 0
$targets = @(
    (Join-Path $game 'BepInEx\plugins\LiarsBar8P.dll'),
    (Join-Path $game 'BepInEx\config\josh.liarsbar.eightplayers.cfg')
)
$plugDir = Join-Path $game 'BepInEx\plugins'
if (Test-Path $plugDir) {
    foreach ($stray in Get-ChildItem $plugDir -Filter '*LiarsBar*' -File -ErrorAction SilentlyContinue) {
        $targets += $stray.FullName
    }
}
foreach ($old in ($targets | Select-Object -Unique)) {
    if (Test-Path $old) {
        try { [IO.File]::Delete($old); Good "removed $(Split-Path $old -Leaf)"; $gone++ }
        catch { Warn "could not remove $(Split-Path $old -Leaf): $($_.Exception.Message)" }
    }
}
if ($gone -eq 0) { Say "         nothing previous found - clean machine" Gray }

Say ""
Say "Checking for the latest release..."
try {
    $rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ 'User-Agent' = 'LiarsBar8P-Installer' }
} catch {
    $code = $null
    try { $code = $_.Exception.Response.StatusCode.value__ } catch { }
    if ($code -eq 404) {
        Bad "No published release found for $Repo."
        Say "  Either no release has been published yet, or the repository" Gray
        Say "  is private. Ask whoever sent you this installer to publish one." Gray
    } else {
        Bad "Could not reach GitHub: $($_.Exception.Message)"
        Say "  Check your internet connection and try again." Gray
    }
    exit 1
}

$asset = $rel.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1
if (-not $asset) { Bad "That release has no .zip attached to it."; exit 1 }
Good "Version $($rel.tag_name)  ($([math]::Round($asset.size/1MB,1)) MB)"

$tmp = Join-Path $env:TEMP "LiarsBar8P_$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$zip = Join-Path $tmp 'mod.zip'

try {
    Say ""
    Say "Downloading..."
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
    Good "Downloaded $([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB"

    Say ""
    Say "Extracting..."
    $extract = Join-Path $tmp 'x'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $extract)
    Good "Extracted"

    $srcRoot = $extract
    if (-not (Test-Path (Join-Path $srcRoot 'BepInEx'))) {
        $inner = Get-ChildItem $extract -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'BepInEx') } | Select-Object -First 1
        if ($inner) { $srcRoot = $inner.FullName }
    }
    if (-not (Test-Path (Join-Path $srcRoot 'BepInEx'))) {
        Bad "The downloaded package does not look right (no BepInEx folder)."
        exit 1
    }

    Say ""
    Say "Installing fresh..."
    foreach ($item in @('BepInEx','dotnet','winhttp.dll','doorstop_config.ini','.doorstop_version','changelog.txt')) {
        $src = Join-Path $srcRoot $item
        if (Test-Path $src) { Copy-Item $src -Destination $game -Recurse -Force }
    }
    Good "Files copied"
}
catch {
    Say ""
    Bad "Install failed: $($_.Exception.Message)"
    exit 1
}
finally {
    try { [IO.Directory]::Delete($tmp, $true) } catch { }
}

Say ""
Say "Verifying..."
$checks = @{
    'winhttp.dll (loader)' = Join-Path $game 'winhttp.dll'
    'BepInEx core'         = Join-Path $game 'BepInEx\core\BepInEx.Core.dll'
    '8 Player plugin'      = Join-Path $game 'BepInEx\plugins\LiarsBar8P.dll'
    'fresh config'         = Join-Path $game 'BepInEx\config\josh.liarsbar.eightplayers.cfg'
}
$fail = $false
foreach ($k in ($checks.Keys | Sort-Object)) {
    if (Test-Path $checks[$k]) { Good $k } else { Bad $k; $fail = $true }
}

Say ""
if ($fail) { Say "Install INCOMPLETE - see the failures above." Red; exit 1 }

Say "=================================================" Green
Say "  Installed $($rel.tag_name) - clean install" Green
Say "=================================================" Green
Say ""
Say "NEXT:" Cyan
Say "  1. Launch Liar's Bar."
Say "  2. Check the BOTTOM LEFT of the screen - it must read $($rel.tag_name)."
Say "     Everyone playing together must show the same version."
Say ""
Say "  The first launch after installing can take a few minutes." Gray
