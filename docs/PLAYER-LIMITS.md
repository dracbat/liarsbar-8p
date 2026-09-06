# Where four players is hard-coded

A map of every place the game (or this mod) assumes four players, made before changing
anything so the fixes could be aimed rather than guessed. Findings come from the game's
own compiled code: method names and call targets are resolved, so the notes below quote
real logic rather than inference.

The mod's own configurable cap lives in exactly one place — `Limits.Max`, backed by the
`MaxPlayers` setting. Nothing else in the mod defines a maximum.

---

## 1. Getting into the lobby

| Where | What it caps | Status |
|---|---|---|
| `SteamMatchmaking.CreateLobby(type, maxMembers)` | Steam lobby member limit. The game passes `NetworkManager.maxConnections`, so raising that raises the lobby. | Fixed since v0.1.0 (`CapPatches`) |
| `NetworkManager.maxConnections` | The serialized inspector field. Feeds both Steam's lobby size and the transport. | Fixed since v0.1.0 (`CapPatches`) |
| `NetworkServer.Listen(int maxConns)` | Mirror's *actual* connection check. The game always called `Listen(4)` regardless of the field. | Fixed since v0.2.0 (`JoinFix`) |
| **`Mirror.FizzySteam.NextServer` / `LegacyServer`** | **The Steam transport's own limit, below Mirror.** `OnConnectionStatusChanged` compares `steamConnections.Count` against the server's `maxConnections` and rejects with *"Incoming connection … would exceed max connection count"*. | **This is why the 6th player could not join** — fixed in v2.1.0 (`TransportCap`) |

The transport limit is the one that explains the exact symptom. The host does not open a
Steam connection to itself, so `steamConnections` holds only the remote players: a limit
of four meant four remote clients plus the host — **five players, and the sixth refused**.
The rejection happens inside the transport, before Mirror is involved, which is why the
host's log never showed an incoming connection for them.

## 2. Being shown in the lobby

`LobbyController.SpawnSlots` is a `List<LobbySlot>` of **four** podiums placed in the
lobby scene. `PlayerObjectController.CmdSetPlayer` — which runs on the host, called from
the `CmdSetPlayerName` command — does:

```
slot = SpawnSlots.Where(s => !s.Dolu).First()   <-- throws when all four are taken
slot.Dolu = true
slot.PlayerName = <name>
player.SlotName = slot.gameObject.name          <-- a SyncVar: every client sees it
player.transform.position = slot.transform.position
player.transform.rotation = slot.transform.rotation
```

So a fifth player throws `InvalidOperationException: Sequence contains no elements`, and
because that command is also what places their body, they end up with **no podium and no
lobby avatar** — exactly the reported symptom. In game they are fine, because the in-game
seat ring is a different list (`Manager.Slots`, plain transforms) that this mod already
extends.

`LobbySlot` is a `NetworkBehaviour` on a scene object, so more of them cannot simply be
spawned: Mirror identifies runtime-spawned networked objects by a build-time assetId,
while scene objects carry only a sceneId. Cloning one duplicates a scene identity and
disconnects everybody — that was the v0.3.0 incident.

`PlayerObjectController.Update` leans on the same list every frame until the player is
marked Loaded:

```
slot = SpawnSlots.First(s => s.gameObject.name == SlotName)
```

so a player with no `SlotName` never finishes joining the lobby at all.

What makes it solvable: a podium copy with a **fresh, never-spawned NetworkIdentity**.
Mirror ignores it (sceneId 0, netId 0, never spawned), but it is a real, working
`LobbySlot`, so both of those pieces of game code are satisfied. Every peer builds the
same copies, with the same names, from the same scene data.

The copied identity must be *cleared*, not removed: `NetworkIdentity.OnDestroy` runs
`NetworkClient.spawned.Remove(netId)`, and on a copy that still carries the original's
`netId` that would unregister the **original** podium.

Two things Mirror will not do for a copy:

- **SyncVars.** Occupied, name and ready never reach other machines. The host sets them
  through the game's own code; each client sets its own copies from what *is* synced —
  each player's `SlotName`, name and ready flag.
- **UI.** A podium has no children: its name text, ready icon, character panel and kick
  button live in a shared lobby canvas (`Canvas/Player1`, `Canvas/Playe2`, …), one group
  per podium, referenced by the `LobbySlot`. Copying a podium copies those references, so
  every copy would drive the *same* name plate. The canvas group is therefore copied too
  and positioned by the template plate's offset from its own podium, which follows the
  curve of the arc. The kick button on a copy is disabled — its click handler still points
  at the podium it was copied from, so it would kick the wrong player.

New podium spots are floor-checked before use, with the check calibrated on the shipped
podiums so a lobby without colliders disables it rather than failing everything.
(`LobbyPodiums`, v2.1.0.)

## 3. Turn order

Three separate four-player assumptions, all in `Manager`:

**`GiveTurn()`** — advance to the next player:

```
slot = ActivePlayerSlot + 1
loop:
    p = GetTargetPlayer(slot, checkdead: true)
    if (p == null) { slot++; if (slot <= 3) goto loop; slot = 0; goto loop }
    p.HaveTurn = true; ActivePlayerSlot = slot
```

`slot <= 3` is compiled as `cmp ebx,3`. A full table happens to work — the search only
wraps when it finds an empty slot. But the moment anybody in seats 0–3 is dead or gone,
the search steps past 3, fails the test, and **jumps straight back to seat 0, skipping
seats 4–7 entirely**. That is the "it makes players play when the arrow isn't pointing at
them" report: the turn silently skips the extra players.

**`BackGiveTurn()` / `findbackplayer()`** — the same loop backwards, with the wrap value
`mov ebp,3` — seat 0 steps back to seat 3, so seats 4–7 are never reached.

**`GetTargetSlot(mySlot, direction)`** — a hard-coded four-by-four table of who is to
your left, right and across:

| my slot | across | previous | next |
|---|---|---|---|
| 0 | 2 | 3 | 1 |
| 1 | 3 | 0 | 2 |
| 2 | 0 | 1 | 3 |
| 3 | 1 | 2 | 0 |

Anything outside 0–3 falls through to `return 0`, so every extra player thinks seat 0 is
in all three directions. This drives aiming and `CheckSlotFull`.

`GiveTurnSpin()` and `GiveTurnTexas()` carry the same `cmp ebx,3` wrap for their modes.

## 4. Dealing

`DeckGamePlayManager.DealBasicOrDevil` and `DealDeck2` build their deck from a constant
compiled into the game — `Enumerable.Range(1, 20)` and `Enumerable.Range(1, 28)`. No
amount of adding card objects changes it. Fixed in v2.0.0 (`DeckSizePatch`), with card
faces rescaled to match (`CardTypeFix`), since `ToCardTypeBasic` is arithmetic over the
card index with thresholds sized for twenty cards.

`Manager.StartPlayerCount` lagging at four also dealt to only four people; corrected since
v1.2.0 (`RosterFix`).

`DeckGamePlayManager.AddCards(List<int>)` is **not** part of dealing — its only callers are
`DeckGameplay.ServerThrowCards` and the pending-table-cards paths, i.e. a player putting
cards down. A patch that scaled that list to the player count shipped from v0.1.0 to
v2.0.2; above four players it would have turned a two-card play into a whole deck. Removed
in v2.1.0.

## 5. In-game seats

`Manager.Slots` (List<Transform>) and `Manager.NameTexts` ship with four entries. Both are
plain lists and are extended by this mod (`SeatExpansion`), spaced for the players actually
present (`SeatRing`) and trimmed back to the roster so nothing walks an empty chair
(`RosterFix`). Character prefabs are not a constraint — the game ships eleven.

## 6. Where a four still legitimately appears

Not every four is a limit. These describe **what the game shipped with**, and are needed to
scale anything proportionally:

- `Limits.VanillaPlayers = 4` — the seat count and per-player divisor the game was built
  around. Cards per player is `vanillaDeck / VanillaPlayers`; the seat circle is fitted
  from the four original seats so the table never drifts.
- `CardTypeFix` thresholds `6/12/18` of 20 and `8/16/24` of 28 — the vanilla deck
  composition, scaled from rather than replaced.

These are deliberately *not* driven by `Limits.Max`: they are facts about the game, and
tying them to a setting would silently change the deck's composition.
