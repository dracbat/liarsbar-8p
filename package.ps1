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
# The old zip goes now, so a run that stops at any check below leaves no zip at all rather
# than the previous one looking like this one's output.
if (Test-Path $Zip) { Remove-Item $Zip -Force }

# 1. loader (as downloaded, unmodified)
Copy-Item "$Root\tools\bepinex-staging\*" $Staging -Recurse -Force

# ...except one file. BepInEx's dotnet\ folder carries Microsoft.DiaSymReader.Native, which is
# under the Microsoft .NET Library License rather than MIT, and that licence's conditions for
# passing it on cannot be met by including a text file. It only adds line numbers to .NET
# stack traces: BepInEx and the mod were run without it and loaded every patch. So it stays
# out, and the check on the finished zip below makes sure it never comes back.
$Excluded = @('dotnet/Microsoft.DiaSymReader.Native.amd64.dll')
foreach ($x in $Excluded) {
    $path = Join-Path $Staging ($x -replace '/', '\')
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

# 2. plugin
$plug = "$Staging\BepInEx\plugins"
New-Item -ItemType Directory -Force -Path $plug | Out-Null
Copy-Item "$Root\src\LiarsBar8P\bin\Release\net6.0\LiarsBar8P.dll" $plug -Force

# 3. pre-set config so every copy agrees
$cfgDir = "$Staging\BepInEx\config"
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
@"
## Settings file was created by plugin Liar's Bar 8 Players
## Plugin GUID: liarsbar.eightplayers

[General]

## Maximum players per lobby. Every player must use the same value.
# Setting type: Int32
# Default value: 8
MaxPlayers = $MaxPlayers

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
"@ | Set-Content "$cfgDir\liarsbar.eightplayers.cfg" -Encoding utf8

# 4. one-click installer / uninstaller
Copy-Item "$Root\installer\install.bat"     $Staging -Force
Copy-Item "$Root\installer\install.ps1"     $Staging -Force
Copy-Item "$Root\installer\uninstall.bat"   $Staging -Force
Copy-Item "$Root\installer\uninstall.ps1"   $Staging -Force

# ...and the same for Linux and the Steam Deck. A shell script with a carriage return at
# the end of its lines does not run ("bash\r: No such file or directory"), and one with a
# byte order mark in front of its #! line runs under the wrong shell. Git on this machine
# checks files out with Windows line endings unless .gitattributes says otherwise, and a
# Windows editor can add either at any time, so refuse to ship a broken one.
$ShellScripts = @('install.sh', 'uninstall.sh')
foreach ($sh in $ShellScripts) {
    $bytes = [IO.File]::ReadAllBytes("$Root\installer\$sh")
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "installer\$sh starts with a byte order mark - save it as UTF-8 without one"
    }
    if ([Array]::IndexOf($bytes, [byte]13) -ge 0) {
        throw "installer\$sh has Windows line endings - it would not run on Linux"
    }
    Copy-Item "$Root\installer\$sh" $Staging -Force
}

Copy-Item "$Root\README.md" $Staging -Force

# Licences. Everything in BepInEx\ and dotnet\ is somebody else's work, and their licences
# require their notices - for the LGPL and Apache parts, the whole licence text - to travel
# with the files. v1.0.0 shipped without them; THIRD-PARTY-NOTICES.txt says which file is
# under which licence, and licenses\ holds the texts it points at.
Copy-Item "$Root\LICENSE" $Staging -Force
Copy-Item "$Root\THIRD-PARTY-NOTICES.txt" $Staging -Force
New-Item -ItemType Directory -Force -Path "$Staging\licenses" | Out-Null
Copy-Item "$Root\licenses\*.txt" "$Staging\licenses" -Force

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

LINUX AND STEAM DECK
  Liar's Bar runs on Linux through Proton, and this is the same mod - the same files,
  the same version - so Linux and Windows players should be able to share a lobby,
  though that has not been tested yet. See README.md for what has been confirmed.

  1. Extract this whole zip into a folder of its own.
  2. Open a terminal in that folder (in the file manager, right click an empty
     space -> Open Terminal Here, or Open in Terminal) and run:
         bash install.sh
     It finds Liar's Bar through Steam automatically, SD cards included.
  3. In Steam: right click Liar's Bar -> Properties -> General -> Launch Options,
     and paste in exactly:
         WINEDLLOVERRIDES="winhttp=n,b" %command%
     Without this, Proton never starts the mod and the game runs as normal.
  4. Launch the game. The FIRST launch is slow, as above.

  Steam Deck: do steps 1 and 2 in Desktop Mode (Steam button -> Power ->
  Switch to Desktop). Step 3 also works in Game Mode: select Liar's Bar,
  press the cog -> Properties. And set the game's graphics to High or lower
  (Settings -> Graphics): on Ultra the Deck runs out of graphics memory
  loading the table, and the game hangs on its loading screen.

  Uninstall: bash uninstall.sh

CHECK IT WORKED
  The mod's version is drawn in the top left corner in game. For more detail, open
  BepInEx\LogOutput.log  in the game folder (BepInEx/LogOutput.log on Linux) and look for:
     === Liar's Bar 8P loaded ===
     [cap] maxConnections 4 -> 8

SETTINGS
  BepInEx\config\liarsbar.eightplayers.cfg
  MaxPlayers must be the SAME for everyone playing together.

UNINSTALL
  Double click  uninstall.bat  (on Linux: bash uninstall.sh)
  Nothing in the game itself is modified. Steam's "Verify integrity of game
  files" does NOT remove the mod - it only checks the game's own files.
"@ | Set-Content "$Staging\INSTALL.txt" -Encoding utf8

# Anything that lands in the staging folder is downloaded by every player, and a compiler
# or tool can quietly stamp an absolute build path into a file. So refuse to package
# if any file carries one.
#
# One listing serves both this check and the zip, hidden files included, so nothing can be
# zipped without having been looked at here first.
$StagedItems = @(Get-ChildItem $Staging -Recurse -Force)
$leak = @()
foreach ($f in ($StagedItems | Where-Object { -not $_.PSIsContainer })) {
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    foreach ($enc in @([Text.Encoding]::ASCII, [Text.Encoding]::Unicode)) {
        if ($enc.GetString($bytes) -like "*$env:USERNAME*") {
            $leak += $f.FullName.Substring($Staging.Length + 1)
            break
        }
    }
}
if ($leak) {
    Write-Host "Refusing to package - these carry the build account name:" -ForegroundColor Red
    $leak | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "build account name found in the package"
}
Write-Host "No build-account identifiers in the package" -ForegroundColor Green

# 5. the zip
#
# Not Compress-Archive. The one in Windows PowerShell 5.1 writes every path with backslashes
# ("BepInEx\core\0Harmony.dll"), and v1.0.0 shipped that way: 225 of its 235 entries. Windows
# does not mind, and neither does Info-ZIP's unzip, which repairs them with a warning. But the
# zip format says the separator is a forward slash, and some Linux tools - Python's zipfile
# among them - take it at its word and unpack a single folder of files with backslashes in
# their names. Forward slashes work everywhere, Windows included, so every entry is written
# by hand.
#
# Built under a temporary name and only moved into place once it has passed every check
# below. A zip left half written - by Ctrl+C, a full disk, a check that failed - is still a
# readable zip, holding whatever had been written so far, and release.ps1 -SkipBuild would
# publish it.
function Set-ZipUnixExecutable([string]$Path, [string[]]$Names) {
    # The shell scripts should run as ./install.sh as well as bash install.sh, which needs the
    # execute bit. A zip can only carry Unix permissions on an entry marked as made on Unix,
    # and .NET marks every entry as made on Windows, so the central directory record of each
    # script is patched: "version made by" system byte -> 3 (Unix), and the upper half of the
    # external attributes -> 0100755 (a regular file, rwxr-xr-x). Everything else keeps the
    # defaults an unzip tool gives a file from Windows.
    $b = [IO.File]::ReadAllBytes($Path)
    $eocd = -1
    for ($i = $b.Length - 22; $i -ge [Math]::Max(0, $b.Length - 65557); $i--) {
        if ($b[$i] -eq 0x50 -and $b[$i + 1] -eq 0x4B -and $b[$i + 2] -eq 0x05 -and $b[$i + 3] -eq 0x06) {
            $eocd = $i; break
        }
    }
    if ($eocd -lt 0) { throw "no end of central directory record in $Path" }
    $count = [BitConverter]::ToUInt16($b, $eocd + 10)
    $p     = [long][BitConverter]::ToUInt32($b, $eocd + 16)
    $hit   = @()
    for ($n = 0; $n -lt $count; $n++) {
        if ([BitConverter]::ToUInt32($b, $p) -ne 0x02014b50) {
            throw "central directory entry $n is not where the zip says it is"
        }
        $nameLen    = [BitConverter]::ToUInt16($b, $p + 28)
        $extraLen   = [BitConverter]::ToUInt16($b, $p + 30)
        $commentLen = [BitConverter]::ToUInt16($b, $p + 32)
        $entry = [Text.Encoding]::UTF8.GetString($b, $p + 46, $nameLen)
        if ($Names -contains $entry) {
            $b[$p + 5]  = 3
            $b[$p + 38] = 0; $b[$p + 39] = 0; $b[$p + 40] = 0xED; $b[$p + 41] = 0x81
            $hit += $entry
        }
        $p += 46 + $nameLen + $extraLen + $commentLen
    }
    $missing = @($Names | Where-Object { $hit -notcontains $_ })
    if ($missing.Count) { throw "not in the zip to mark executable: $($missing -join ', ')" }
    [IO.File]::WriteAllBytes($Path, $b)
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$Partial = "$Zip.partial"
if (Test-Path $Partial) { Remove-Item $Partial -Force }
try {
    $stream  = [IO.File]::Open($Partial, [IO.FileMode]::CreateNew)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $StagedItems) {
            $name = $item.FullName.Substring($Staging.Length + 1).Replace('\', '/')
            if ($item.PSIsContainer) {
                # A folder only needs an entry of its own when it is empty - every other one is
                # implied by the paths of the files inside it. BepInEx expects patchers/ to exist.
                if (-not (Get-ChildItem $item.FullName -Force | Select-Object -First 1)) {
                    [void]$archive.CreateEntry("$name/")
                }
                continue
            }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $item.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
        $stream.Dispose()
    }

    Set-ZipUnixExecutable $Partial $ShellScripts

    # Read it back the way the online installer will, and check what Linux will see.
    $check = [IO.Compression.ZipFile]::OpenRead($Partial)
    try {
        $files = @($StagedItems | Where-Object { -not $_.PSIsContainer }).Count
        $inZip = @($check.Entries | Where-Object { -not $_.FullName.EndsWith('/') }).Count
        if ($inZip -ne $files) { throw "zip holds $inZip files, staging has $files" }
        foreach ($x in $Excluded) {
            if ($check.Entries | Where-Object { $_.FullName -eq $x }) { throw "zip contains $x, which must stay out" }
        }
        $backslashed = @($check.Entries | Where-Object { $_.FullName.Contains('\') })
        if ($backslashed.Count) { throw "zip entry with a backslash path: $($backslashed[0].FullName)" }
        foreach ($want in @('winhttp.dll', 'BepInEx/plugins/LiarsBar8P.dll', 'install.sh', 'install.bat',
                            'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'licenses/LGPL-2.1.txt', 'licenses/LGPL-3.0.txt',
                            'licenses/GPL-3.0.txt', 'licenses/Apache-2.0.txt', 'licenses/MIT-components.txt',
                            'licenses/dotnet-runtime.txt')) {
            if (-not ($check.Entries | Where-Object { $_.FullName -eq $want })) { throw "zip is missing $want" }
        }
        Write-Host "Zip paths use forward slashes ($($check.Entries.Count) entries)" -ForegroundColor Green
    } finally { $check.Dispose() }

    Move-Item $Partial $Zip -Force
} catch {
    Remove-Item $Partial -Force -ErrorAction SilentlyContinue
    throw
}
Write-Host "Packaged -> $Zip" -ForegroundColor Green
Get-Item $Zip | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}


