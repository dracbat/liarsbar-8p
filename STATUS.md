# Status

Target: 8 players (configurable 2–16). Modes prioritised: Liar's Deck, then Liar's Dice,
then the rest.

## Verified working

| Item | Evidence |
|---|---|
| BepInEx 6 IL2CPP loads | plugin logs on every boot, host and clients |
| Steam lobby member limit 4 → 8 | Steam API `GetLobbyMemberLimit` returns 8 |
| Clients reach the lobby | 5th player's log showed the host's exact lobby ID |
| **`NetworkServer.Listen(4)` → 8** | **the real fix — see below** |
| Lobby podium slots 4 → 8, arc-fitted | 21.8°/seat, positions match an independent simulation |

### The 5-player bug (fixed in 0.2.0)

Mirror keeps **two** connection limits:

- `NetworkManager.maxConnections` — a serialized inspector field
- `NetworkServer.maxConnections` — a static, installed by `NetworkServer.Listen(int)`,
  and the one actually checked when a client connects

Raising only the first is not enough. The game calls `Listen` with **4** regardless, so
Steam admitted a 5th player to the lobby and Mirror dropped them straight back to the
menu. Confirmed live:

```
[join] NetworkServer.Listen(4) -> 8
[join] incoming connId=0 | server.max=8 manager.max=4
```

Note `manager.max=4` at connect time: the manager that hosts is a *different instance*
from the one patched at startup, which is why patching only the manager silently failed.
Intercepting `Listen` fixes the value Mirror enforces, whichever instance is hosting.

## Implemented, not yet proven in a real match

| Item | Why unproven |
|---|---|
| 5+ players actually staying connected | needs a retest with real clients |
| Proportional Liar's Deck scaling | only runs once a match starts |

## Not yet started

- In-game seat ring (`Manager.Slots`) expansion beyond 4
- Per-mode seated character objects (8 separate lists)
- Nameplate UI (`Manager.NameTexts`)
- Turn order / win condition / revolver assignment review for >4
- Liar's Dice scaling
- Remaining modes: Chaos, Poker, Texas, Spin, Arena, Velvet

## Known gaps in diagnostics

Join and disconnect events are now logged (`[join]`). Before 0.2.0 they were not, which
is why the original 5-player failure produced a completely silent host log.
