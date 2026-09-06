<#
  One command to ship a release.

      .\release.ps1

  Builds the plugin, repackages the zip, commits anything outstanding, pushes,
  makes sure the repo is public (release assets follow repo visibility), then
  creates or updates the GitHub release with both assets attached.

  Safe to re-run: an existing release for the tag has its assets replaced.

  First time only, sign in once:
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

# Native tools write to stderr routinely. Under $ErrorActionPreference = 'Stop',
# PowerShell 5.1 turns that into a terminating error, so every gh call goes through
# this helper: output is captured as plain text and success is judged by exit code.
function Invoke-Gh {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GhArgs)
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = & $script:GhExe @GhArgs 2>&1 | ForEach-Object { $_.ToString() }
        [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($lines -join "`n") }
    }
    finally { $ErrorActionPreference = $old }
}

# git warns on stderr for harmless things (line endings, for one). Same hazard as
# above, so mutating git calls run through here and are judged by exit code.
function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = & git @GitArgs 2>&1 | ForEach-Object { $_.ToString() }
        [pscustomobject]@{ Code = $LASTEXITCODE; Out = ($lines -join "`n") }
    }
    finally { $ErrorActionPreference = $old }
}

# ------------------------------------------------------------------ preflight
Step "Checking tools"

$GhExe = $null
$cmd = Get-Command gh -ErrorAction SilentlyContinue
if ($cmd) { $GhExe = $cmd.Source }
else {
    foreach ($guess in @("$env:ProgramFiles\GitHub CLI\gh.exe",
                         "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe",
                         "$env:LOCALAPPDATA\Microsoft\WinGet\Links\gh.exe")) {
        if (Test-Path $guess) { $GhExe = $guess; break }
    }
}
if (-not $GhExe) {
    Bad "GitHub CLI (gh) not found."
    Info "Install it:  winget install --id GitHub.cli -e"
    Info "Then open a NEW terminal and run this again."
    exit 1
}
Good "gh: $GhExe"

$auth = Invoke-Gh auth status
if ($auth.Code -ne 0) {
    Bad "Not signed in to GitHub."
    Info ""
    Info "Run this once (a browser will open):"
    Info "    `"$GhExe`" auth login"
    Info ""
    Info "Choose:  GitHub.com  ->  HTTPS  ->  Login with a web browser"
    Info "Then run this script again."
    exit 1
}
Good "signed in"
$who = Invoke-Gh api user --jq .login
if ($who.Code -eq 0) { Info "account: $($who.Out)" }

# ------------------------------------------------------------- never publish
Step "Checking nothing sensitive would be published"
$bad = git ls-files | Select-String -Pattern 'GameAssembly|global-metadata|\.exe$|members\.txt|^backup/|^recon/'
if ($bad) {
    Bad "These must never be published:"
    $bad | ForEach-Object { Info $_ }
    exit 1
}
Good "no game binaries or decompilation dumps tracked"

# --------------------------------------------------------------------- version
if (-not $Tag) {
    $csproj = Get-Content "$PSScriptRoot\src\LiarsBar8P\LiarsBar8P.csproj" -Raw
    $m = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
    $Tag = if ($m.Success) { "v$($m.Groups[1].Value)" } else { 'v0.1.0' }
}
Info "release tag: $Tag"

# ----------------------------------------------------------------------- build
if (-not $SkipBuild) {
    Step "Building plugin"
    if (Get-Process -Name "Liar's Bar" -ErrorAction SilentlyContinue) {
        Bad "Liar's Bar is running - close it so the plugin can be rebuilt."
        exit 1
    }
    Push-Location "$PSScriptRoot\src\LiarsBar8P"
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    dotnet build -c Release --nologo -v quiet | Out-Null
    $rc = $LASTEXITCODE
    $ErrorActionPreference = $old
    Pop-Location
    if ($rc -ne 0) { Bad "build failed"; exit 1 }
    Good "plugin built"

    Step "Packaging zip"
    & "$PSScriptRoot\package.ps1" | Out-Null
    Good "packaged"
}

foreach ($a in @($Zip, $Bat)) {
    if (-not (Test-Path $a)) { Bad "missing asset: $a"; exit 1 }
}
Good "assets ready"
Info "$([IO.Path]::GetFileName($Zip))  $([math]::Round((Get-Item $Zip).Length/1MB,1)) MB"
Info "$([IO.Path]::GetFileName($Bat))  $([math]::Round((Get-Item $Bat).Length/1KB,1)) KB"

# ------------------------------------------------------------- commit + push
Step "Committing and pushing"
$dirty = git status --porcelain
if ($dirty) {
    Info "uncommitted changes:"
    $dirty | ForEach-Object { Info "  $_" }
    $r = Invoke-Git add -A
    if ($r.Code -ne 0) { Bad "git add failed"; Info $r.Out; exit 1 }
    $r = Invoke-Git commit -q -m "Release $Tag"
    if ($r.Code -ne 0) { Bad "git commit failed"; Info $r.Out; exit 1 }
    Good "committed as 'Release $Tag'"
} else {
    Info "working tree already clean"
}

$ahead = (git log origin/main..HEAD --oneline | Measure-Object).Count
if ($ahead -gt 0) {
    $r = Invoke-Git push origin main
    if ($r.Code -ne 0) { Bad "push failed"; Info $r.Out; exit 1 }
    if ($r.Out) { $r.Out -split "`n" | ForEach-Object { Info $_ } }
    Good "pushed $ahead commit(s)"
} else {
    Info "nothing to push"
}

# --------------------------------------------------------------- visibility
if (-not $KeepPrivate) {
    Step "Making sure the repo is public"
    Info "release assets are only downloadable if the repo is public"
    $vis = Invoke-Gh repo view $Repo --json visibility --jq .visibility
    if ($vis.Code -eq 0 -and $vis.Out.Trim() -eq 'public') {
        Good "already public"
    } else {
        $r = Invoke-Gh repo edit $Repo --visibility public --accept-visibility-change-consequences
        if ($r.Code -ne 0) { Bad "could not change visibility"; Info $r.Out; exit 1 }
        Good "repo is now public"
    }
} else {
    Warn "leaving visibility alone (-KeepPrivate)"
    Warn "the online installer cannot download while the repo is private"
}

# ------------------------------------------------------------------ release
Step "Publishing release $Tag"

# Prefer the changelog entry for this tag: notes written twice drift apart.
if (-not $Notes) {
    $clPath = Join-Path $PSScriptRoot 'CHANGELOG.md'
    if (Test-Path $clPath) {
        $cl = Get-Content $clPath -Raw
        $pattern = '(?ms)^## ' + [regex]::Escape($Tag) + '\b.*?(?=^## |\z)'
        $m = [regex]::Match($cl, $pattern)
        if ($m.Success) {
            $Notes = $m.Value.Trim()
            Info "release notes taken from CHANGELOG.md"
        } else {
            Warn "no CHANGELOG.md section for $Tag - using the generic notes"
        }
    }
}

if (-not $Notes) {
$Notes = @"
Raises Liar's Bar from 4 players to 8.

## Install

Download **Install-LiarsBar8P.bat** below and run it. It finds the game through
Steam automatically, downloads the mod and installs it.

Prefer to do it by hand? Download the zip, extract it, run ``install.bat``.

The first game launch after installing is slow (a few minutes) while BepInEx sets
up. That happens once.

## Status

Verified working:
- 8 player Steam lobby, confirmed by Steam's own reported member limit
- Mirror connection cap raised 4 -> 8

Implemented but **not yet tested in a real match**:
- Lobby podium slots beyond 4
- Proportional Liar's Deck scaling

Not done yet: in-game seats and nameplates beyond 4, Liar's Dice, other modes.
Getting 8 people into a lobby should work. Playing a full 8 player round probably
will not yet.

**Everyone playing together must install this and use the same MaxPlayers value.**
"@
}

$exists = (Invoke-Gh release view $Tag --repo $Repo).Code -eq 0
if ($exists) {
    Info "release $Tag exists - replacing its assets"
    $r = Invoke-Gh release upload $Tag $Zip $Bat --repo $Repo --clobber
    if ($r.Code -ne 0) { Bad "asset upload failed"; Info $r.Out; exit 1 }
    Good "assets updated"
} else {
    $notesFile = Join-Path $env:TEMP "lb8p-notes-$([guid]::NewGuid().ToString('N').Substring(0,6)).md"
    Set-Content $notesFile $Notes -Encoding utf8
    $r = Invoke-Gh release create $Tag $Zip $Bat --repo $Repo --title "Liar's Bar 8 Player Mod $Tag" --notes-file $notesFile
    if ($r.Code -ne 0) { Bad "release creation failed"; Info $r.Out; exit 1 }
    Good "release created"
}

# ------------------------------------------------------------------- verify
# a release created as a draft is invisible to releases/latest, which is what the
# installer fetches - publish it before claiming success
$vw = Invoke-Gh release view $Tag --repo $Repo --json isDraft --jq .isDraft
if ($vw.Code -eq 0 -and $vw.Out.Trim() -eq "true") {
    Warn "release $Tag is a DRAFT - publishing it"
    $pub = Invoke-Gh release edit $Tag --repo $Repo --draft=false
    if ($pub.Code -ne 0) { Bad "could not publish the draft"; Info $pub.Out; exit 1 }
    Good "draft published"
    Start-Sleep -Seconds 4
}

Step "Verifying it is actually downloadable by other people"
try {
    $api = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ 'User-Agent' = 'release-script' }
    if ($api.tag_name -ne $Tag) {
        Bad "releases/latest is $($api.tag_name), not $Tag - the installer would fetch the wrong build"
        exit 1
    }
    Good "anonymous API sees release $($api.tag_name)"
    foreach ($a in $api.assets) { Info "$($a.name)  $([math]::Round($a.size/1MB,2)) MB" }
    if (-not ($api.assets | Where-Object { $_.name -like '*.zip' })) {
        Warn "no .zip asset visible - the online installer needs one"
    }
} catch {
    Warn "could not confirm anonymously: $($_.Exception.Message)"
    Warn "if the repo is still private, the installer will not work for anyone else"
}

Write-Host "`nDone." -ForegroundColor Green
Write-Host "  Release : https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Green
Write-Host "  Share   : the Install-LiarsBar8P.bat link on that page" -ForegroundColor Green
