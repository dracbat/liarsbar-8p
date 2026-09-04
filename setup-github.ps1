<#
  One-time GitHub setup for this repo.

    .\setup-github.ps1 -User yourusername

  Creates the first commit under a GitHub noreply address (so your real email
  never enters the history), wires up the remote and pushes.

  On the first push, Git Credential Manager opens a browser for you to sign in.
  Nothing here ever handles your password or a token directly.

  The repo must already exist on GitHub and be PRIVATE:
    https://github.com/new  ->  name it, tick Private, create it EMPTY
    (no README, no .gitignore, no license - this repo already has them)
#>
param(
    [Parameter(Mandatory = $true)][string]$User,
    [string]$Repo = 'liarsbar-8p'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Say ($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }

Say ""
Say "Configuring repo identity..." Cyan
git config user.name  $User
git config user.email "$User@users.noreply.github.com"
Say "  user.name  = $User"
Say "  user.email = $User@users.noreply.github.com"

# safety net: never let game binaries or dumps get committed
$bad = git ls-files | Select-String -Pattern 'GameAssembly|global-metadata|\.exe$|members\.txt|^backup/|^recon/'
if ($bad) {
    Say ""
    Say "ABORTED - these should never be published:" Red
    $bad | ForEach-Object { Say "  $_" Red }
    exit 1
}

if (-not (git log -1 2>$null)) {
    Say ""
    Say "Creating first commit..." Cyan
    git add -A
    git commit -q -m @'
Liar's Bar 8 player mod: verified lobby caps, installer, recon

BepInEx 6 (IL2CPP) + Harmony mod raising Liar's Bar from 4 players to 8.

Verified working:
  - NetworkManager.maxConnections 4 -> 8
  - Steam lobby member limit 4 -> 8, confirmed by Steam's own
    GetLobbyMemberLimit rather than by self-reporting

Implemented, pending a real multiplayer test:
  - Lobby podium slot expansion, fitted to the existing seat arc
  - Proportional Liar's Deck scaling, derived from the deck the game
    supplies so card type encoding is never hardcoded

Includes a self-elevating one-click installer with Steam auto-detection,
a matching uninstaller, and the IL2CPP recon tooling.

No game binaries are included; see NOTES.md for the technical breakdown
and STATUS.md for what is proven versus outstanding.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
'@
    Say "  committed"
} else {
    Say "  commit already exists - skipping" Yellow
}

git branch -M main

$url = "https://github.com/$User/$Repo.git"
if (git remote 2>$null) { git remote set-url origin $url } else { git remote add origin $url }
Say ""
Say "Remote: $url" Cyan

Say ""
Say "Pushing (a browser may open for you to sign in)..." Cyan
git push -u origin main

Say ""
Say "Done: https://github.com/$User/$Repo" Green
