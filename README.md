# Liar's Bar — 8 Player Mod

Raises Liar's Bar from 4 players to 8 (configurable 2–16).

Built with [BepInEx 6 (IL2CPP)](https://github.com/BepInEx/BepInEx) + Harmony. No game
files are modified on disk — everything is patched at runtime.

---

## ⚠️ Read this first

**This is early. The lobby side works; the in-game side is not proven yet.**

| | Status |
|---|---|
| 8-player Steam lobby | ✅ Verified — Steam's own API reports the raised member limit |
| Mirror connection cap 4 → 8 | ✅ Verified at runtime |
| Lobby podium slots for 8 | ⚠️ Implemented, not yet tested with a full lobby |
| Liar's Deck proportional deck scaling | ⚠️ Implemented, not yet tested in a real match |
| In-game seats / nameplates for >4 | ❌ Not done |
| Liar's Dice and other modes | ❌ Not done |

Seats, dealing and nameplates only run once a match starts, which needs real connected
clients. Until that has been tested, **expect an 8-player match to break.** Getting 8
people into a lobby should work; playing a full round with 8 probably will not, yet.

**Everyone in the lobby must install this mod and use the same `MaxPlayers` value.** A
vanilla client joining a modded lobby will desync. This is for private games with
friends who are all running it.

---

## Install

Download **`Install-LiarsBar8P.bat`** from the
[latest release](../../releases/latest) and run it.

It finds Liar's Bar through Steam automatically, downloads the mod, and installs it.
It asks for administrator permission because the game lives under `Program Files`.

It is a plain text file — open it in Notepad first if you want to see what it does.

<details>
<summary>Manual install instead</summary>

1. Download `LiarsBar-8P.zip` from the [latest release](../../releases/latest).
2. Extract it somewhere.
3. Run `install.bat` from inside the extracted folder.

Or fully by hand: copy the zip's contents into your Liar's Bar folder so that
`winhttp.dll` and `BepInEx/` sit next to `Liar's Bar.exe`.
</details>

**The first launch after installing is slow** — up to a few minutes while BepInEx
prepares the game. This happens once. Let it reach the main menu.

To confirm it worked, open `BepInEx/LogOutput.log` in the game folder and look for:

```
=== Liar's Bar 8P loaded ===
[cap] maxConnections 4 -> 8
```

## Configure

`BepInEx/config/liarsbar.eightplayers.cfg`, created on first run.

| Setting | Default | Meaning |
|---|---|---|
| `MaxPlayers` | `8` | Lobby size. **Must match across all players.** |
| `ScaleDeck` | `true` | Scale the Liar's Deck with player count so everyone still gets 5 cards. |
| `VerboseDiagnostics` | `true` | Log seat/slot counts. Useful while the mod is unfinished. |
| `SelfTestAutoHostLobby` | `false` | Developer only. Leave off. |
| `SelfTestForceSoloStart` | `false` | Developer only. Leave off. |

## Uninstall

Run `uninstall.bat` from the zip, or delete `winhttp.dll`, `doorstop_config.ini`,
`.doorstop_version`, `changelog.txt`, and the `BepInEx/` and `dotnet/` folders.

Since no game files are ever modified, Steam's **Verify integrity of game files**
also restores everything.

## Building from source

Requires the .NET SDK and a local install of the game with BepInEx already set up
(the project references the interop assemblies BepInEx generates on first launch).

```
cd src/LiarsBar8P
dotnet build -c Release
```

`deploy.ps1` builds and copies the plugin into the game. `package.ps1` produces the
distributable zip.

## Notes

- `NOTES.md` — technical breakdown: architecture, class map, how the caps were found
- `STATUS.md` — what is proven versus outstanding

No game assets or binaries are included in this repository.

## Licence

MIT, covering this mod's own source only. Liar's Bar is the property of Curve
Animation; BepInEx is redistributed under LGPL-2.1. You must own the game.
