# Liar's Bar — 8 Player Mod

Raises Liar's Bar from 4 players to 8 (configurable 2–16).

Built with [BepInEx 6 (IL2CPP)](https://github.com/BepInEx/BepInEx) + Harmony. No game
files are modified on disk — everything is patched at runtime, and Steam's *Verify
integrity of game files* undoes the install completely.

**Everyone playing together must install this and run the same version.** A vanilla
client in a modded lobby will desync. The version you are running is drawn in the
bottom-left corner in game, and the host is warned when someone's build differs.

---

## What works

| | |
|---|---|
| Steam lobby of 8 | ✅ Steam's own API reports the raised member limit |
| Connections beyond the 4th and the 5th | ✅ Three separate caps, all confirmed raised at host time |
| Lobby podiums and name plates for 8 | ✅ Built and confirmed present; **not yet seen with 8 real people** |
| Turn order visiting every seat | ✅ All five wrap-around sites rewritten, confirmed in the log |
| Dealing to more than four | ✅ The deal's deck size is rewritten; 5 players get 25 cards, 8 get 40 |
| In-game seat ring and nameplates | ✅ Confirmed with real players |
| Liar's Dice, Chaos, Spin, Poker | ⚠️ The shared turn-order and cap fixes apply; not separately tested |

The caps and the patches are verified from a real launch. What has **not** happened yet
is a full round with eight people at the table — so treat the first eight-player game as
the test it is, and read `BepInEx/LogOutput.log` afterwards if something looks wrong.

`docs/PLAYER-LIMITS.md` maps every place the game assumes four players, and what was
done about each.

---

## Install

Download **`Install-LiarsBar8P.bat`** from the
[latest release](../../releases/latest) and run it.

It finds Liar's Bar through Steam automatically, removes any previous copy of the mod,
and installs the current one. It asks for administrator permission because the game
lives under `Program Files`. It is the way to update, too.

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

To confirm it worked, look at the bottom-left corner in game, or open
`BepInEx/LogOutput.log` and look for:

```
=== Liar's Bar 8P loaded ===
[cap] maxConnections 4 -> 8
[transport] server maxConnections 4 -> 8
[turn] GiveTurn: wraps at seat 3 -> 7
```

## Configure

`BepInEx/config/liarsbar.eightplayers.cfg`, created on first run. The installer replaces
it on every install, so a setting changed by hand does not survive an update — that is
deliberate: a stale setting once silently disabled a fix for a whole session.

| Setting | Default | Meaning |
|---|---|---|
| `MaxPlayers` | `8` | Lobby size. **Must match across all players.** |
| `VerboseDiagnostics` | `true` | Log seat, podium and deck counts. Leave on — it is what makes a bad round diagnosable. |
| `SelfTestAutoHostLobby` | `false` | Developer only. Leave off. |
| `SelfTestForceSoloStart` | `false` | Developer only. Leave off. |
| `SelfTestDeckSizePatch` | `false` | Developer only. Leave off. |

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
distributable zip and refuses to run if any file about to ship carries the build
account's name. `release.ps1` does the whole release in one command.

## Notes

- `docs/PLAYER-LIMITS.md` — every hard-coded four in the game, and what was done about it
- `CHANGELOG.md` — what changed in each version, and why
- `NOTES.md` — architecture and class map

No game assets or binaries are included in this repository.

## Licence

MIT, covering this mod's own source only. Liar's Bar is the property of Curve
Animation; BepInEx is redistributed under LGPL-2.1. You must own the game.
