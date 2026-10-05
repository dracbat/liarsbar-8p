<#
  Rewrites every published release's notes from CHANGELOG.md.

  Releases were originally all published with the same generic text, so a release page
  never said what was actually in that version - and the text went stale besides. This
  reads the section for each tag and replaces the notes with it, followed by the same
  install footer release.ps1 uses.

  Run with -WhatIf first to see what would change.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Repo = 'dracbat/liarsbar-8p'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent

function Info ($m) { Write-Host "         $m" -ForegroundColor Gray }
function Good ($m) { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Warn ($m) { Write-Host "  [!]    $m" -ForegroundColor Yellow }

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) {
    foreach ($guess in @("$env:ProgramFiles\GitHub CLI\gh.exe",
                         "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe",
                         "$env:LOCALAPPDATA\Microsoft\WinGet\Links\gh.exe")) {
        if (Test-Path $guess) { $gh = $guess; break }
    }
}
if (-not $gh) { throw "GitHub CLI (gh) not found" }

# Native tools write to stderr routinely; under 'Stop' that would terminate the script.
function Invoke-Gh {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GhArgs)
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = & $gh @GhArgs 2>&1 | ForEach-Object { $_.ToString() }
        [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($lines -join "`n") }
    }
    finally { $ErrorActionPreference = $old }
}

# Two footers, because a release page should only describe what is attached to it. Releases
# from before Linux support carry no Install-LiarsBar8P.sh and their zips no install.sh, so
# they keep the Windows-only text they were published with. $footer must stay the same as
# $InstallFooter in release.ps1.
$footer = @'
## Install

**Windows:** download **Install-LiarsBar8P.bat** below and run it. It finds the game through
Steam automatically, downloads the mod and installs it. It removes any previous copy first,
so it is also the way to update. Updating from 1.0.0? Download it again from here rather
than re-running your old copy, which also deletes other mods with "LiarsBar" in their name.

**Linux and Steam Deck:** download **Install-LiarsBar8P.sh** below, open a terminal, and run
it from where it downloaded to - usually:

    bash ~/Downloads/Install-LiarsBar8P.sh

Then, in Steam, right-click Liar's Bar > **Properties > General > Launch Options** and paste:

    WINEDLLOVERRIDES="winhttp=n,b" %command%

Without that, Proton never starts the mod and the game runs as normal. On a Steam Deck, run
the installer in Desktop Mode (the launch option can be set in either mode), and set the
game's graphics to High or lower: on Ultra the Deck runs out of graphics memory loading the
table.

Prefer to do it by hand? Download the zip, extract it, and run install.bat (Windows) or
`bash install.sh` (Linux).

The first game launch after installing is slow (a few minutes) while the loader sets
itself up. That happens once.

**Everyone playing together must install this, and must be on the same version.** The
version you are running is shown in the top-left corner in game. Linux support is tested on
a Steam Deck. Windows and Linux players run the very same mod, so they should be able to
share a lobby - but that part has not yet been confirmed.
'@

$windowsFooter = @'
## Install

Download **Install-LiarsBar8P.bat** below and run it. It finds the game through Steam
automatically, downloads the mod and installs it. It removes any previous copy first, so
it is also the way to update.

Prefer to do it by hand? Download the zip, extract it, run install.bat.

The first game launch after installing is slow (a few minutes) while the loader sets
itself up. That happens once.

**Everyone playing together must install this, and must be on the same version.** The
version you are running is shown in the top-left corner in game.
'@

# Windows PowerShell reads a BOM-less file as the system codepage, which turns every em
# dash into mojibake on the release page. Read as UTF-8, and write back without a BOM.
$changelog = [IO.File]::ReadAllText((Join-Path $Root "CHANGELOG.md"), [Text.Encoding]::UTF8)

$tags = (Invoke-Gh api "repos/$Repo/releases" --paginate --jq '.[].tag_name')
if ($tags.Code -ne 0) { throw "could not list releases: $($tags.Out)" }

$missing = @()
foreach ($tag in ($tags.Out -split "`n" | Where-Object { $_.Trim() })) {
    $tag = $tag.Trim()
    $pattern = '(?ms)^## ' + [regex]::Escape($tag) + '\b.*?(?=^## |\z)'
    $m = [regex]::Match($changelog, $pattern)
    if (-not $m.Success) {
        Warn "$tag - no CHANGELOG section, left alone"
        $missing += $tag
        continue
    }

    $assets = Invoke-Gh release view $tag --repo $Repo --json assets --jq '.assets[].name'
    if ($assets.Code -ne 0) { Warn "$tag - could not list its files, left alone"; continue }
    $tagFooter = if ($assets.Out -split "`n" -contains 'Install-LiarsBar8P.sh') { $footer } else { $windowsFooter }
    $notes = $m.Value.Trim() + "`n`n" + $tagFooter
    if (-not $PSCmdlet.ShouldProcess($tag, "rewrite release notes")) {
        Info "$tag would get $($notes.Length) chars"
        continue
    }

    $file = Join-Path $env:TEMP "lb8p-notes-$($tag -replace '[^\w.]','').md"
    [IO.File]::WriteAllText($file, $notes, (New-Object Text.UTF8Encoding $false))
    $r = Invoke-Gh release edit $tag --repo $Repo --notes-file $file
    Remove-Item $file -Force -ErrorAction SilentlyContinue
    if ($r.Code -ne 0) { Warn "$tag - update failed: $($r.Out)" }
    else { Good "$tag" }
}

if ($missing) {
    Write-Host ""
    Warn "no changelog entry for: $($missing -join ', ')"
    exit 1
}
