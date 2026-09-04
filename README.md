# Liar's Bar — 8 Player Mod

Raises Liar's Bar from 4 players to 8 (configurable 2–16).

> **Everyone in the lobby must install this mod and use the same `MaxPlayers` value.**
> A vanilla client joining a modded lobby will desync. This is for private games
> with friends who are all running it.

## Install

1. Install **BepInEx 6 (IL2CPP, win-x64)** into the Liar's Bar folder — the folder
   containing `Liar's Bar.exe`. When correct, `winhttp.dll` and a `BepInEx/` folder
   sit next to the .exe.
2. Launch the game once and wait for it to reach the main menu. The first launch is
   slow — BepInEx is generating interop assemblies. Then quit.
3. Copy `LiarsBar8P.dll` into `BepInEx/plugins/`.
4. Launch again.

Verify it loaded — `BepInEx/LogOutput.log` should contain:

```
=== Liar's Bar 8P loaded ===
[cap] maxConnections 4 -> 8
```

## Configure

`BepInEx/config/josh.liarsbar.eightplayers.cfg` (created on first run):

| Setting | Default | Meaning |
|---|---|---|
| `MaxPlayers` | `8` | Lobby size. **Must match across all players.** |
| `VerboseDiagnostics` | `true` | Log seat/slot/prefab counts. Useful while the mod is in development. |
| `SelfTestAutoHostLobby` | `false` | Dev only: auto-host a private lobby at startup to capture diagnostics. Leave off for normal play. |

## Uninstall

Delete `BepInEx/plugins/LiarsBar8P.dll`. To remove the loader entirely, delete
`winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`, and the
`BepInEx/` and `dotnet/` folders.

Nothing in the mod modifies game files on disk — all patching is at runtime — so
Steam's *Verify Integrity of Game Files* is always a clean escape hatch.

## Status

See `NOTES.md` for the full technical breakdown and `STATUS.md` for what works today.
