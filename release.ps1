<#
  One command to ship a release.

      .\release.ps1

  Builds the plugin, repackages the zip, commits anything outstanding, pushes,
  makes sure the repo is public (release assets follow repo visibility), then
  creates or updates the GitHub release with both assets attached.

  Safe to re-run: an existing release for the tag has its assets replaced rather
  than erroring out.

  First time only, you need to sign in once:
      gh auth login
#>
param(
    [string]$Tag,
    [string]$Notes,
    [switch]$KeepPrivate,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step ($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Good ($m) { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Bad  ($m) { Write-Host "  [FAIL] $m" -ForegroundColor Red }
function Warn ($m) { Write-Host "  [!]    $m" -ForegroundColor Yellow }
function Info ($m) { Write-Host "         $m" -ForegroundColor Gray }

$Repo = 'dracbat/liarsbar-8p'
$Zip  = "$PSScriptRoot\dist\LiarsBar-8P.zip"
$Bat  = "$PSScriptRoot\installer\Install-LiarsBar8P.bat"

# --------------------------------------------------------------- preflight
Step "Checking tools"

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    # winget puts it here but the PATH in this shell may be stale
    $guess = "$env:ProgramFiles\GitHub CLI\gh.exe"
    if (Test-Path $guess) { $gh = $guess } else {
        Bad "GitHub CLI (gh) not found."
        Info "Install it with:  winget install --id GitHub.cli -e"
        Info "then open a NEW terminal and run this script again."
        exit 1
    }
} else { $gh = $gh.Source }
Good "gh: $gh"

& $gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Bad "You are not signed in to GitHub."
    Info "Run this once, then re-run this script:"
    Info "    gh auth login"
    Info "(choose GitHub.com -> HTTPS -> login with a web browser)"
    exit 1
}
Good "signed in to GitHub"

# ------------------------------------------------------------ never publish
Step "Checking nothing sensitive would be published"
$bad = git ls-files | Select-String -Pattern 'GameAssembly|global-metadata|\.exe$|members\.txt|^backup/|^recon/'
if ($bad) {
    Bad "These must never be published:"
    $bad | ForEach-Object { Info $_ }
    exit 1
}
Good "no game binaries or decompilation dumps tracked"

# ------------------------------------------------------------------ version
if (-not $Tag) {
    $csproj = Get-Content "$PSScriptRoot\src\LiarsBar8P\LiarsBar8P.csproj" -Raw
    $m = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
    $Tag = if ($m.Success) { "v$($m.Groups[1].Value)" } else { 'v0.1.0' }
}
Info "release tag: $Tag"

# -------------------------------------------------------------------- build
if (-not $SkipBuild) {
    Step "Building plugin"
    if (Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue) {
        Bad "Liar's Bar is running - close it so the plugin can be rebuilt."
        exit 1
    }
    Push-Location "$PSScriptRoot\src\LiarsBar8P"
    dotnet build -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Pop-Location; Bad "build failed"; exit 1 }
    Pop-Location
    Good "plugin built"

    Step "Packaging zip"
    & "$PSScriptRoot\package.ps1" | Out-Null
    Good "packaged"
}

foreach ($f in @($Zip, $Bat)) {
    if (-not (Test-Path $f)) { Bad "missing asset: $f"; exit 1 }
}
Good "assets ready"
Info "$([IO.Path]::GetFileName($Zip))  $([math]::Round((Get-Item $Zip).Length/1MB,1)) MB"
Info "$([IO.Path]::GetFileName($Bat))  $([math]::Round((Get-Item $Bat).Length/1KB,1)) KB"

# ------------------------------------------------------------ commit + push
Step "Committing and pushing"
$dirty = git status --porcelain
if ($dirty) {
    Info "uncommitted changes:"
    $dirty | ForEach-Object { Info "  $_" }
    git add -A
    git commit -q -m "Release $Tag"
    Good "committed as 'Release $Tag'"
} else {
    Info "working tree already clean"
}

$ahead = (git log origin/main..HEAD --oneline | Measure-Object).Count
if ($ahead -gt 0) {
    git push origin main
    if ($LASTEXITCODE -ne 0) { Bad "push failed"; exit 1 }
    Good "pushed $ahead commit(s)"
} else {
    Info "nothing to push"
}

# -------------------------------------------------------------- visibility
if (-not $KeepPrivate) {
    Step "Making sure the repo is public"
    Info "release assets are only downloadable if the repo is public"
    $vis = (& $gh repo view $Repo --json visibility --jq .visibility) 2>$null
    if ($vis -eq 'public') {
        Good "already public"
    } else {
        & $gh repo edit $Repo --visibility public --accept-visibility-change-consequences
        if ($LASTEXITCODE -ne 0) { Bad "could not change visibility"; exit 1 }
        Good "repo is now public"
    }
} else {
    Warn "leaving visibility alone (-KeepPrivate)"
    Warn "the online installer will NOT be able to download while private"
}

# ----------------------------------------------------------------- release
Step "Publishing release $Tag"

if (-not $Notes) {
$Notes = @"
Raises Liar's Bar from 4 players to 8.

## Install

Download **Install-LiarsBar8P.bat** below and run it. It finds the game through
Steam automatically, downloads the mod and installs it.

Prefer to do it by hand? Download ``LiarsBar-8P.zip``, extract it, run ``install.bat``.

The first game launch after installing is slow (a few minutes) while BepInEx sets
up. That happens once.

## Status

Working and verified:
- 8 player Steam lobby (confirmed by Steam's own member limit)
- Mirror connection cap raised 4 -> 8

Implemented but **not yet tested in a real match**:
- Lobby podium slots for more than 4
- Proportional Liar's Deck scaling

Not done yet: in-game seats and nameplates beyond 4, Liar's Dice, other modes.
Getting 8 people into a lobby should work. Playing a full 8 player round probably
will not yet.

**Everyone playing together must install this and use the same MaxPlayers value.**
"@
}

$exists = $false
& $gh release view $Tag --repo $Repo 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) { $exists = $true }

if ($exists) {
    Info "release $Tag already exists - replacing its assets"
    & $gh release upload $Tag $Zip $Bat --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { Bad "asset upload failed"; exit 1 }
    Good "assets updated"
} else {
    & $gh release create $Tag $Zip $Bat --repo $Repo --title "Liar's Bar 8 Player Mod $Tag" --notes $Notes
    if ($LASTEXITCODE -ne 0) { Bad "release creation failed"; exit 1 }
    Good "release created"
}

# ------------------------------------------------------------------ verify
Step "Verifying it is actually downloadable"
try {
    $api = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ 'User-Agent' = 'release-script' }
    Good "public API sees release $($api.tag_name)"
    foreach ($a in $api.assets) { Info "$($a.name)  $([math]::Round($a.size/1MB,2)) MB" }
    if (-not ($api.assets | Where-Object { $_.name -like '*.zip' })) {
        Warn "no .zip asset visible - the online installer needs one"
    }
} catch {
    Warn "could not confirm anonymously: $($_.Exception.Message)"
    Warn "if the repo is private, the installer will not work for others"
}

Write-Host "`nDone." -ForegroundColor Green
Write-Host "  Release : https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
Write-Host "  Share   : the Install-LiarsBar8P.bat link from that page" -ForegroundColor Green
