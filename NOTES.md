# Liar's Bar — 8 Player Mod: Technical Notes

## Target build

| | |
|---|---|
| Steam AppID | `3097560` |
| Build ID | `24035789` (as of 2026-09-04) |
| Unity | 2022.3.27f1, HDRP |
| Scripting backend | **IL2CPP** |
| Networking | **Mirror** + FizzySteamworks (Steam P2P), kcp2k fallback |
| Voice | Dissonance (MirrorIgnorance integration) |
| Anti-cheat | none |

Vanilla hashes (backed up in `backup/vanilla/`):

- `GameAssembly.dll` — `542782A64107C13FF6FF12C5EDF018BBBE614BEA8BC03FA20093602E21D02D9C`
- `global-metadata.dat` — `119F7F0E9A8A882D8FCFE92BB281025137E1B81BB2D6C8302BF237BE3FACD084`

If these hashes change, the game updated and the mod must be re-verified.

## Modding approach

IL2CPP compiles C# to native code, so the game's methods cannot be edited on disk.
Everything is done with **BepInEx 6 (IL2CPP) + Harmony runtime patches**.

BepInEx regenerates readable "interop" assemblies into `BepInEx/interop/` on first
launch. Those carry the real type/method/field *names* but **not** the method bodies —
the bodies live as native code in `GameAssembly.dll`. Practical consequence:

- Names and signatures: fully available. Patch by name; survives most updates.
- Hardcoded literals inside methods: **not** readable without native decompilation.

That is why values are discovered by *instrumenting at runtime* rather than by
reading constants. Most caps in this game turn out to be serialized Unity inspector
fields anyway, which can simply be written at runtime.

## Architecture

Internal codename for the game is "Velvet". Devs are Turkish — expect Turkish
identifiers (`MasaCards` = table cards, `Sansur` = censor, `Sayilar` = numbers).

### Networking / lobby

- `CustomNetworkManager : Mirror.NetworkManager`
  - `maxConnections` — **vanilla 4** (inherited Mirror field)
  - `List<PlayerObjectController> GamePlayers` — dynamic, no fixed cap
  - `GameMode Mode`, `int DeckMode`, `int DiceMode`, `int LobbyType`
- `SteamLobby` — `HostLobby()` calls `SteamMatchmaking.CreateLobby(type, maxMembers)`
  with **maxMembers = 4** in vanilla
- `LobbyController` — `List<> SpawnSlots` = **4** podium slots (lobby UI)
- `PlayerObjectController.InGameSlot` (SyncVar) — seat assignment, index into `Manager.Slots`

### In-game

- `Manager` (central, `NetworkBehaviour`)
  - `List<Transform> Slots` — seat transforms
  - `List<> Players`, `List<> NameTexts`
  - Per-mode seated-character lists: `DicePlayerPrefabs`, `PokerPlayerPrefabs`,
    `TexasPlayerPrefabs`, `ChaosPlayerPrefabs`, `ChaosDeckPlayerPrefabs`,
    `RoulettePlayerPrefabs`, `LiarsSpinPrefabs`, `MatchMakingPlayerPrefabs`
  - `int ActivePlayerSlot`, `int StartPlayerCount`
- `PlayerStats` — `int Slot`, `Health`, `Dead`, `HaveTurn`, `Winner`
- `CharController : NetworkBehaviour` — base for every per-mode gameplay controller

### Game modes (`CustomNetworkManager.GameMode`)

| Value | Mode | Manager |
|---|---|---|
| 0 | LiarsDeck | `DeckGamePlayManager` |
| 1 | LiarsDice | `DiceGamePlayManager` |
| 2 | LiarsChaos | `ChaosGamePlayManager` |
| 3 | LiarsPoker | `PokerGamePlayManager` |
| 4 | VelvetRoom | `VelvetRoomManager` |
| 5 | LiarsTexas | `TexasGamePlayManager` |
| 6 | LiarsSpin | `LiarsSpinGameplayManager` |
| 7 | Arena | `ArenaGamePlayManager` |

### Deck construction (Liar's Deck)

Relevant members on `DeckGamePlayManager`:

- `void AddCards(List<int> types)` — builds the deck from a type list
- `int ToCardTypeBasic(int n)` / `int ToCardTypeDeck2(int n)` — index → card type mapping
  (**this is the deck composition function to patch for proportional scaling**)
- `void DealBasicOrDevil()`, `void DealDeck2()` — dealing entry points
- `IEnumerator GiveCardPlayer()` — per-player card handout
- `void ResetRound(bool first)` — round setup
- `List<> MasaCards` / `ResetCards` / `OpenCards` / `ExtraCards`

Also present and potentially useful: `bool EditorSoloDeckTest(Manager mgr)` — a
developer solo-test hook.

## Verified findings

| Cap | Vanilla | Patched | Verified by |
|---|---|---|---|
| `NetworkManager.maxConnections` | 4 | 8 | plugin log at runtime |
| Steam lobby `maxMembers` | 4 | 8 | **Steam API** `GetLobbyMemberLimit` = 8 |
| `LobbyController.SpawnSlots` | 4 | — | runtime count |

## Known constraints

1. **Every player must run the mod, with the same `MaxPlayers` value.** Mirror
   requires host and clients to agree on spawned objects and seat indices; a vanilla
   client in a modded lobby will desync.
2. **Game updates can break it.** Patches are name-based rather than offset-based,
   which survives most patches, but re-verify after every update (check the hashes above).
