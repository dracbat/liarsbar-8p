# Status

Target: 8 players, all modes. Priority: Liar's Deck, then Liar's Dice.

`docs/PLAYER-LIMITS.md` is the map of every place the game assumes four players.
`CHANGELOG.md` is what changed in each release, and why.

## Verified with eight real connections

Eight separate copies of the game, each with its own Mirror connection, played together on
one machine through the loopback harness. Seating measured on **every** machine, not just
the host:

| Players | Gap between seats | Furthest anyone sat from their own seat |
|---|---|---|
| 5 | 72.0° | 0.01 m |
| 6 | 60.0° | 0.01 m |
| 7 | 51.4° | 0.00 m |
| 8 | 45.0° | 0.00 m |

Zero errors from the mod on any peer, at any size.

## Played with real people, on real machines

**2026-09-08, v0.31.0: five people, five PCs, over Steam.** Not the loopback harness — five
separate machines in a Steam lobby, playing Liar's Deck Basic to a winner. Confirmed from the
host's log afterwards:

| | |
|---|---|
| All five seated on the ring | 5 of 5, gaps 72.0–72.1°, nobody more than 0.00 m from their own seat |
| All five dealt | five cards each, all holding them |
| Rounds played | 35 round resets across the session, matches run to a winner |
| Errors from the mod | **none** |
| Remote calls that threw | **none** |
| Deal routines patched | 7 of 7 |

That is the first time this has been true of anything but a single machine talking to itself.
**Eight people on eight machines is still the open question** — five proves the networking
path, not the table size.

> Logs from v2.0.0 and before record real Steam names. Do not paste one into an issue or a
> screenshot without reading it first. From the release after v2.0.0 the mod's own lines name
> seats and connections instead, and a player can save a cleaned bug report (below).

## Verified per mode, at every size

Each cell is a real table of that many separate copies of the game. "Dealt" means every seat
holding the cards it was dealt, read back from the server; "turn ring" means the turn was
handed round deliberately and reached every seat still in the game.

**v1.0.0: 65 matches over 8h35m** — every mode the lobby arrows can reach, at every size from
one to eight, in all four bars.

| Table | 8 | 7 | 6 | 5 | 4 | 3 | 2 | 1 | Its own mechanic |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|---|
| Liar's Deck — Basic | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby | — |
| Liar's Deck — Devil | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby | devil's deal fired at 8 |
| Chaos Deck | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby | chaos thrown, aimed and fired at 5–8 |
| Liar's Dice (both) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby | bids, liar calls and spot-on calls resolving |
| Liar's Texas | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby | raises and folds |
| Liar's Spin | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby | spins, claims and liar calls |

A table of one is a lobby: it forms, holds, reports nothing wrong, and no match begins. Whether
the game would allow a solo match is untested — the lobby never registers a player to ready, so
the game is never asked.

**The aim ring** — can every seat point the revolver at every other seat — was measured
separately in every Chaos deck match, on every machine rather than only the host: **all seats
reachable from all seats, at every size from two to eight.**

Liar's Poker and the standalone Chaos mode are not in this table. The lobby's mode arrows cycle
Liar's Deck, Texas, Dice and Spin and nothing else, so neither is reachable by a player; the
harness can force them, and testing what nobody can select would pad the matrix rather than
fill it.

> **Both of those Dice warnings turned out to be about the test rig, and a real fault was
> hiding behind them.** The turn was reaching few seats because the harness treated
> `TotalCount` — how many of one face showed at the last reveal — as the number of dice on
> the table, so the second bid of every round looked impossible and the table called liar
> three moves in. With that corrected the bidding runs properly, and what it runs into is an
> `IndexOutOfRangeException` when a liar call is resolved above four players: the reveal
> coroutine dies part way through and the round stops. That is a real eight-player defect in
> Liar's Dice, and it is what v1.0.0 had to deal with rather than sign off around.

**The fourth deck variant is Liar's Deck 2**, and that corrects what stood here before. The
lobby reports four and the game's arrow cycles three: `ChangeGameModeDeckRight` resets the value
to zero the moment it reaches 3. The fourth was never removed from the game, only from the
arrow - its deal of seven, its rounds that keep the hands, its card, rules page and sound are all
still there. Runs asking for it before the mod reached it were playing Basic a second time. From
the release after v2.0.0 the host's arrow steps onto it at a table where everyone has the mod.

> **That sign-off was too generous, and v0.31.0 says how.** It also claimed Liar's Dice at
> six and eight and Liar's Poker at seven, on the strength of the seating being right and no
> errors being logged. The seating *was* right. Nobody was dealt a hand: every mode except
> Liar's Deck threw on the first step of its deal, silently, and the only thing watching the
> deal read Liar's Deck's own component. Liar's Poker at five players did not merely fail to
> deal - it dropped every connection in the game. What "played in" is allowed to mean here is
> now "every seat holding the cards it was dealt, and the mode's own mechanic seen to fire".

How much memory a copy of the game holds was measured on 6 Nov (batch M2): one copy at a time on
the kit desktop, two only for a table of two people, `Get-Process` every 5 to 7 s for 2 minutes
once each state had settled, at Ultra quality unless another is named. It is the
game's own. At the main menu a copy with no mod at all (Doorstop's own switch off: no BepInEx, no
`coreclr.dll`, no log) held what a full and a bare copy held, and full and bare matched within
about 0.1 GB of committed private memory wherever both could run. In MB, private / working set:

| State | Full | Bare (`LIARSBAR8P_BARE`) | No mod |
|---|---|---|---|
| Main menu | 8,949 / 2,736 | 8,967 / 2,733 | 8,905 / 2,598 |
| Own lobby, one player | 10,150 / 4,129 | 10,150 / 4,178 | |
| Liar's Dice, two copies, the harness playing no seat (host; client) | 13,197 / 10,105; 13,200 / 10,776 | 13,214 to 13,248 / 10,256 to 10,345; 13,222 to 13,258 / 10,495 to 10,652 | |
| Liar's Deck, two copies (host; client) | 13,202 / 10,112; 13,199 / 10,741 | 13,174 / 10,187; 13,176 / 10,695 | |
| Liar's Dice, one copy and 3 bots: 4 seats | 13,234 / 7,853 | (no bots) | |
| one copy and 7 bots: 8 seats, 8 characters | 13,290 / 8,049 | | |
| 8 seats, every seat one character | 13,285 / 8,064 | | |
| 8 seats, the kit's screenshots every 8 s | 13,296 / 8,205 | | |
| 8 seats, after 20 minutes (two matches back to back) | 13,367 to 13,371 / 7,950 | | |
| 8 seats at High (quality 2) | 8,545 / 2,568 | | |
| 8 seats at Low (quality 0) | 7,025 / 1,708 | | |

Seats and characters barely count: 4 seats to 8 adds about 60 MB, and eight characters hold no
more than one. The quality does: Ultra holds 4.7 GB more than High at an eight-seat table. Over 21
minutes at eight seats private memory rose about 90 MB in the first match and not at all in the
second; it touched 15.3 GB for a moment while the second match loaded. With the harness playing
the seats of a two-copy table (`ModePlay`, which a bare copy does not have) the full client's
working set came out up to 0.6 GB above the bare one's; with it switched off (`seatplay off`) the
two were within 0.3 GB, either way, and private memory was lower in full. CPU, of one core at the
game's 60 frame cap: 64 to 71% at the menu (no mod 64%), 121 to 133% in the lobby, 113 to 156% at
a table. So the 13 GB the kit saw is the game at Ultra, and so is the test machine's cap of two copies. This
line once said about 3 GB at steady state; nothing measured at Ultra holds that little, though a
copy at the menu has 2.6 to 2.7 GB resident.

## Liar's Deck 2

| Item | Evidence |
|---|---|
| Arrows reach it, mouse and controller, both ways, host only | right from Chaos and left from Basic land on it; every other step is the game's own |
| Not reached at a table that does not count as modded | arrows wrap as the game ships; a lobby left on it goes back to Basic after 20 s |
| The lobby shows it | the game's DECK 2 plate and help button, on the host and on a client |
| Its rules page opens | the game had switched the page off; the mod switches it back on |
| Seven each at eight seats | deck 28 → 56, faces 16/16/16/8, every seat holding seven, host and clients |
| Hands carried over | 3 real connections: seven rounds, 7/7/7 down to 0/out/2, nothing dealt |
| Liar calls resolve, a player is eliminated, the match ends | the same 3-player run ended on its own and came back to the lobby |
| The turn watchdog waits while a kept-hands round shows its card | 3 real connections: rounds 2 and 3 had their first turn from the game 6.0 s after the reset, no watchdog; with the wait switched off by a test switch, round 5 (one seat out) had the watchdog move the turn on 4.5 s in, 1.5 s before the game's own first turn |
| The choice stays after a match | the lobby came back on Deck 2, on the host and on a client |
| Characters with five card objects | Gerk and Rasim hold five; at a Deck 2 table they get room for seven on every copy |

Not seen yet: the game's own fresh deal (when no more than one living player still holds cards)
in a running match. The 3-player match ended first; at eight seats with bots, every fresh deal
came from the test bots' restart of a call a bot could not answer. How the two added cards look
when a Gerk or Rasim player presses Look has no picture: the look is the player's own input.

## Two-Card Poker

The game's own mode 3, a two-card bluffing table that was never on a menu. Loopback with three
copies and no bots, over three sessions, every copy playing a different character: 4, 7 and 10,
past the end of the table's own character list in every bar.

| Item | Evidence |
|---|---|
| The Poker tab's arrows, host only | the deck tab's arrows round the Poker tab's plate on the host, none on the clients; right, left and a controller's next each stepped between the released Liar's Poker and TWO-CARD POKER, written on the plate's black band (the released game showed the game's own TEXAS plate then; it now reads LIAR'S POKER, below) |
| The released game's plate reads LIAR'S POKER | loopback, a host at 3440x1440 (the test switch) with the arrows and a client at 1920x1080 without them: a copy of the game's plate over its TEXAS picture, LIAR'S POKER on the copy's black band in the plate's own font and colour, auto-sized to 15.5 of 22 (shots). The host stepping right showed TWO-CARD POKER and left LIAR'S POKER again on both copies within a second; the Deck tab and back, and the lobby after a played match, came back on LIAR'S POKER on both (shots). In Turkish LIAR'S POKER, in Russian ЛЖЕЦ-ПОКЕР, in Simplified Chinese 骗子扑克, each inside the band at both sizes, switched while the lobby was up (shots). The game has the mode's name as text only inside its loading tips (the Poker tab's title is an English picture in all four languages, and its string tables have no entry), so the words table carries it, in the tips' own wording; Turkish keeps the English name, as the game's Turkish tips do for Liar's Deck and Dice. The game's TEXAS picture, help button and rules page are untouched underneath |
| The rules page | the game's own Poker help page, its frame, picture and close, with five boxes of this table's rules and its name; it opens from the ? on the host and on a client and closes with its own X |
| The rules row | HOUSE RULES CLASSIC, "This table always plays CLASSIC" and no arrows, on the host and on a client; every match logged "match starts at TwoCard on CLASSIC" |
| Clients see the choice | both clients' Poker tabs showed TWO-CARD POKER within a second of the host's step, over the mod's data channel, and kept it while the bar loaded |
| Mode 3 only at the door | the lobby said Liar's Poker (5) throughout; the network manager said 3 only from the host's start until the copy left the bar, where it went back to 5 |
| Everyone sits down | the character list was lengthened to 11: from 1 in bars 0 and 3, from 7 in bars 1 and 2. Every seat was dealt and played as Scubby |
| Played to a winner in all four bars | bars 0, 3, 2 and 1: swaps, passes, liar calls, challenges, overall calls, face-offs at two left, the shotgun fired loaded and empty; a winner each time, and the table came back to the lobby each time |
| The cards read as the rules say | a Queen and a King in hand lit 13 on the game's table of hand values; a swapped card lay face up on the table |
| The choice stays after a match | the host's lobby came back on TWO-CARD POKER after all four matches; both clients' did too, the two times they were checked. Run again after the review fixes, when only a match that really started at this table brings the choice back: the game's own return still drops it and the mod still puts it back |
| A tab click ends the choice | after a Two-Card Poker match the host clicked the Poker tab: the released Liar's Poker on the host and on both clients. The next match played the released table, and the lobby came back on the released game's plate with nothing chosen (before the fix it came back on TWO-CARD POKER) |
| No hidden hand in the log | after the review fixes a face-off logs only the two challengers' hands, which the game turns over, and a showdown names the winning seat without its hand. A liar call won by the caller came up in that run |
| A fresh host starts on the released game | a new host after two matches: Poker tab on Liar's Poker, the released game's plate, nothing chosen |
| LastMode never 3 | the game's saved LastMode read 5 before, between and after every match, in the game and in the registry |
| No seat cameras | the host went out in three of the matches; the log said "none added: Two-Card Poker" and only the game's own 5 spectator cameras cycled. All 5 views were looked at: no card face in any of them |
| No bots | adding a bot with the lobby on this table was refused; a match with bots already seated plays the released Liar's Poker instead (by the code, not run) |
| The turn line | at this table it sits just right of the game's table of hand values, which it used to cover |
| Tab board and awards | neither at this table: the Tab key keeps the game's own table, and the awards logged nothing to award |

There are no seat cameras at this table.

**Seen along the way, not caused here:** the host logs four NullReferenceExceptions from the game
as the lobby loads after a match. The released Liar's Poker, played as a control in the same run,
logged the same four at its own return. In that control match both clients also logged one
ArgumentOutOfRangeException during a swap at three seats.

Not yet proven: a real keyboard (every move here was the test driver calling each key's own
methods), the host-migration restart (it needs two Steam accounts), and how the face-up cards and
reveals look at more than three seats: without bots the harness cannot seat more than three here.
Also only by reading, because the harness has no Steam lobby: the table is refused unless every
member's published version is this build's (as the house rules ask), and a client with no word
over the channel shows what the lobby key says. Until the version is raised at release, a copy on
the released 2.0.0 publishes the same version as this build and passes that check. At three seats
every hand an overall call turns over is a challenger's, so the log line that leaves a
non-challenger's hand out needs four players.

Built after the tests above, compiled and not yet run in the game: the table cannot be chosen
unless its own patches went on and the lobby built its whole plate, arrows and rules page (the
log's patches are apart, so a renamed log target costs only the log); a lobby piece that fails
half way is removed; and a host who quits a match here gets Liar's Poker back on the network
manager at the menu.

## Verified from a real run

Confirmed in `BepInEx/LogOutput.log` on a live host, current build:

| Item | Evidence |
|---|---|
| Steam lobby member limit 4 → 8 | `GetLobbyMemberLimit` returns 8 |
| `NetworkServer.Listen(4)` → 8 | Mirror's real connection check |
| Steam transport server cap 4 → 8 | `[transport] server maxConnections 4 -> 8` |
| Host manager's own field 4 → 8 | the hosting instance differs from the one patched at startup |
| Turn order wrap, all five sites | `[turn] … wraps at seat 3 -> 7` |
| Deck size constant rewritten | `[decksize] DealBasicOrDevil: deck 20 -> 25` |
| 8 lobby podiums, each with its own name plate | `[podium] lobby has 8 podiums for up to 8 players` |
| 8 podiums in two rows, all on screen | seen in a screenshot of an eight player lobby |
| 8 seated, all dealt five cards | `[dealcards] hands three seconds after the deal` lists all eight |
| Turn rotates through all eight seats | `active slot -> 4 … 5 … 6 … 7 … 0 … 1` |
| Whose turn it is, shown top-left | `Your turn` / `BOT-3's turn`, and the tabletop markings hidden |
| Clean boot | no patch failures, no exceptions from the mod |

## Verified with other people

| Item | Evidence |
|---|---|
| 5 players connect and stay | `connections=5`, `numPlayers=5`, no disconnects |
| No Mirror scene corruption | no `already spawned`, no duplicate NetworkManager |
| In-game seat ring and nameplates beyond 4 | seen in play |

## Finding a game

Run on real Steam with one account. One account cannot join its own lobby: it enters on Steam,
cannot connect, and when it leaves Steam takes the account out of the lobby, which ends the
hosting copy's lobby too.

| Item | Evidence |
|---|---|
| A tagged lobby is found by a modded search and not by a plain one | plain search returned 12 to 13 real lobbies, none of them ours |
| Rows | seen in screenshots |
| JOIN at exactly 4 of 8 | only with the row count forced by a test switch |
| A lobby stays listed past the game's old limit | still listed at 16 minutes |
| A lobby with a stopped heartbeat drops off | gone from a search within five minutes, owner still in it |
| Refusal for another mod version and another `MaxPlayers` | a 1.9.9 build, and a copy set to 6 seats |
| Quick Join opens a lobby and finds one | about 5 s alone; second copy started a join |
| Two lobbies noticing each other | the later one moved, about 9 s, boxes kept |
| Back while looking and while opening | lobby dropped, HOST GAME still works after |
| A real match closes the lobby and it lists again after | one bot, Liar's Dice, ended with `ForceEndGameWithWinner` |
| Start held while somebody arrives | only with a pretend arrival from a test switch |
| Old `SteamLobby` callbacks retired after the menu | 0 trampoline errors, each Steam answer once |
| A host who accepts an invite keeps looking after its own lobby | lobby id pointed at another lobby by a test switch: heartbeat kept moving, shut for a match, opened after; moved mid-match with the lobby saying `ingame`, it still said so back in the lobby, was listed again 10 s later and opened; on leaving, the mod left the lobby it hosted (the game left the other id) |
| The copy icon beside LOBBY ID copies | the clipboard, read back, holds the lobby's id and nothing else |
| JOIN WITH ID says what is wrong | empty, too short, a minus sign, too long, a player's own Steam id: each refused with a reason, nothing joined |
| Open lobbies on the main menu | the count's own search runs and is kept from the list; the line itself seen only with a test switch, since no other modded lobby was open |

**Needs a second account, never run:** a join connecting, a lobby listed with more than one real
member, two people ending at one table, turning away a player without the mod, another version
arriving by invite, an invite refused during a match, a crashed host's players leaving its lobby,
and a connection reaching a running match with no character.

After a host migration the game ends the table at the end of the match instead of returning to
the lobby, so a migrated lobby is never listed again. That is the game's behaviour, not a fault.

## Bug reports, names in the log, outfits in the lobby

Loopback with three copies given made-up names (an apostrophe, a Cyrillic name, a name shaped
like a log tag), then one copy at the menu on real Steam.

| Item | Evidence |
|---|---|
| The report's filter | a test command feeds 35 invented lines through it (names, a 17-digit name, Cyrillic, Chinese, emoji, zero-width and right-to-left marks, a newline, 300 characters, a name cut to 16, Windows, Proton, removable-card and network paths, a sign-in token, a 28-character id, a lobby id in an exception, the game's sign-in line, an online-services exception) plus 4 for the per-copy log: all pass |
| Reports with nothing personal in them | eight reports from a lobby, from a match (host and client, by the button and by the key's code path), from the menu and from real Steam: none holds the account's persona, Steam id, account name, Windows user name, computer name, profile path, the test names, a token or a long number. What matches a pattern is the project's issue address, the BepInEx version number and the bar's own scene name |
| The button | in the menu's settings and in a match's (Pause, Settings), copied from Back: no saved calls left on it, its localisation and menu switch removed, beside Restore Defaults in the menu, which still works as before |
| Where it went | said under the tag for 8 s, as a path inside the game folder |
| Five kept | six saves left the newest five |
| Sign-in in the per-copy logs | the game's access token and player id lines arrive masked in every copy's log; nothing token-shaped is left |
| Names in the mod's lines | in a 3-copy match no line the mod writes outside developer mode carried a test name. The developer bot driver's lines still did, and were changed to seats after that run: built, not run again |
| Outfit picker | a client's change seen on the host and the other client at 4 and at 8 seats, carried into the match (seen on the other client's copy, and in a picture), still on after the table came back to the lobby; Ejder's four outfits named as the wardrobe names them; hidden for Dubai |

**Not tested here:** F11 itself (no window may take focus, so the key's save was run by the test
driver instead), the D-pad (no controller), the report on Linux and a Steam Deck (the system line
reads Wine and Steam's own Deck flag), the ranked screen, where both are switched off by the same
check the rest of the ranked block uses, and other people's names arriving from Steam, which needs
a second account. Translations of the new words are for the translation pass.

## Tab board and end-of-match awards

Loopback, three copies, with and without bots, in all four bars. Over real Steam a client only
greets a host whose lobby says 3.0.0 or later, so until that release no client gets awards or the
board's card counts, and sees the game's own win screen.

| Item | Evidence |
|---|---|
| Same awards on every copy, at a natural end | the host's sent line and all three copies' shown lines identical: Deck (twice), Deck 2, Chaos, Dice, Slots |
| Panel seen in every bar | Deck in bar 0, Deck 2 in bar 1, Chaos in bar 2, Dice in bar 3, Slots in bar 2: left of the win screen, clear of the winner's name and medal |
| The whole build at eight, with bots | three copies and five bots, one launch, the table changed between matches by the test driver: Deck to its natural end (5 awards), then Deck 2, Chaos, Dice and Slots ended by the game's own forced end with a winner (4, 4, 1 and 1 awards). Each time the host's sent line and every copy's shown line were the same, and the countdown read 10.3 to 10.8 s four seconds into the win screen (Slots: the host's only). No FAILED to patch line on any copy |
| Why four of them were forced | the harness's bots cannot finish a match at eight, and nothing here is on a path a person plays. In Deck and Deck 2 a liar call a bot loses never resolves, so the harness restarts the round, again and again: Deck ended once the five bots were put out with the developer kill. In the Chaos deck the restarted round came back with nobody holding a card. In Dice the game's drink call for a bot that lost throws on every copy and the next turn never comes, the same on a fresh launch; Slots stopped after its first claim. Real bots will need each of these solved |
| Bots in the tally | in Deck and Deck 2 the harness's bots' throws never reached the tally, so only the copies' seats won there; in Chaos, Dice and Slots the bots' seats were counted |
| Errors on that run | none from the mod's own work. Its remote-call tracer logged the game's own calls throwing at a table with bots, 43 over the three copies: the dice shake (30) and the drink (3) in Dice, the round card (8) and the slot answer (2) in the deck tables. The harness's aim driver logged four bots' key walks stuck at aim 0 at one moment in the Chaos match |
| Win screen 15 s at a modded table | the lobby came back 15.0 s after the awards; the countdown read 10.3-11.0 four seconds in, on host and clients |
| A client without the channel | both clients muted by the test switch: the game's own win screen with the 15 s countdown, no panel, no error, no disconnect, while the host showed its awards |
| Calls counted as the game settled them | a three-player Deck match's four liar calls, read back from the bot driver's lines, give exactly the five awards sent, ties going to whoever got there first |
| Board at 4 and 8 seats, host and client | screenshots; a bot a client cannot see sitting is named from the lobby, with dashes |
| Cards in hand | the card models are no count (five after a throw, host and client alike), and nor is the server's own list of a seat's cards: it is the deal, which no throw shortens, so the board said five all the way down a hand of five, and R2's first board picture showed a seat that had just thrown at five. The earlier check here compared the board with that list, so it could not see this. The host now counts each hand itself, the deal less each throw it accepted (7 Nov, after R2's review). One copy, 7 bots, eight seats: in Liar's Deck the board and the host's count read 4, 4 and 2 for the seats that had thrown 1, 1 and 3 while the server's list stayed at 5, and each new round's deal put every seat back to 5; in the Chaos deck, 2 for each seat that had thrown its one card, 3 for the rest; in Liar's Deck 2, a seat that threw two read 5 against the server's 7, and still 5 after a round that kept the hands. Before the first deal every seat shows a dash. The channel that carries the counts to clients is unchanged, and a remote player's throw reaches the same server method the bots' do; neither was run again |
| No board in Liar's Slots | the key held: no board built |
| Liar's Poker | board right of centre. Three copies, no bots, CLASSIC, played to the end five times; four reached showdowns, and hands won, the best hand and a showdown shot lived through were counted and sent, the same line on all three copies each time, countdown 10.2 to 10.8 s. The last: CARD SHARK 2 hands won, BEST HAND a flush, LUCKIEST lived with 5 of 8 loaded, COWARD folded 3 times (seen on screen). 0 mod errors, no FAILED to patch |
| Names and numbers in Liar's Poker | at first only the titles showed: by the win screen the game has played its own showdown rows out of sight, and the copies the panel is made from kept that. The name and the number are now drawn fully, white as in the other bars (seen on screen) |
| A fold that is survived | the harness's folds now live and die at the game's own odds: seven folds on a stake of one over two matches all lived; each counted as a fold, and COWARD went to that seat |
| A fold that kills | a fold that kills is counted as a fold, nothing survived (four in the earlier run) |
| The chaos card is no lie | a seat whose only throw was the chaos card: one throw, one chaos, no lie; Agent of Chaos went to it and Best Liar did not |
| Key hint on a controller | a test switch standing in for a pad, host and client: the copied LB glyph is gone, Liar's Slots' hints-button glyph shows in place of the keycap, and the keycap comes back with the switch off |

**Not tested here:** the keys themselves (no window may take focus, so a test switch holds the
key), a real controller (a test switch stood in for one), a real host migration, and two people
over real Steam.

## House rules and the block list

Loopback with three copies (two for one check), at 3, 4 and 8 seats with bots, then three copies
on real Steam on one account: two hosting, one listing. Over real Steam a client only hears the
house rules over the channel from 3.0.0; until then it reads them from the lobby. A client's block
crosses wait for the channel too: they show once the host answers on it, so over real Steam not
before 3.0.0.

| Item | Evidence |
|---|---|
| Every rule took effect | Liar's Deck QUICK at 8: turns started at 15 s (clock 15.0 and 12.7 on the host, 15.0 and 14.1 on a client), every bullet in chamber 1 to 4, eight hands of three, and `(0\|4)` over the other seats on host and client. Chaos HARSH at 8: bullets in chambers 1 to 3, `(0\|3)` on both, 2 chaos cards in each pool of 12, clock 30. Dice HARSH at 8: 3 dice a seat and bids up to 24 on host and client (the client's own dice tray shows 3), everyone on one life, and the first lost call killed with the last-drink animation. Poker HARSH at 8: every stake started on 1 bullet, every seat's Coward's Fold spent on all copies. Slots QUICK at 8: every seat on 2 points, on the leaderboard and the machines' counters |
| Dead Man's Spin | at 3 seats two natural dead spins drew Spades and Clubs, both shots. Hearts never came up in the spins seen, so a test switch turned two drawn shots to Hearts by the same two writes the rule uses: both seats survived and play went on. That proves the writes decide the shot; the rule's own Hearts-to-Spades turn was not seen |
| CLASSIC as before | Deck at 8: clock 30.0 then 27.1, five cards, `(0\|6)`, no rule line beyond the match's own |
| Three Dice's cups (batch M2, 6 Nov) | loopback, one copy and three bots, QUICK, one whole match each, the same build with the fix switched off by the driver (`threecups off`) and on. Basic: about 29,300 errors from the game's per-frame copy of the dealt dice (58,562 `ArgumentOutOfRange` lines, two to an error), a 28.9 MB log and 139% of a core over 143 s, against 0 errors, 0.62 MB and 124% over 165 s; private memory 13,276 and 13,247 MB. Traditional threw too, which the first fix had left alone (its note said Traditional showed none): about 61,400 errors (122,890 lines), a 60.5 MB log and 130% over 289 s, against 0, 0.99 MB and 119% over 281 s once the fix covered both tables. The cup holds the 3 dice dealt (`botstate`: drawn 3 of 3, held 3 of 3; before, 3 of 5) and `[rules] Three Dice cups` is said once a match. CLASSIC Basic and CLASSIC Traditional: 0 errors and no such line, the cups 5 of 5. Every match was played to the end and won by a bot. Reveal shots before and after (`mmshots\m2-three-look-before`, `m2-three-look-late`): three dice on the table at each seat; the row of dice drawn over a seat as it shows has five, the last two showing 1, before the fix and after it. Measured on the host only; a client's copy runs the same patch and was not counted. That row was fixed in batch B2: it is each die's two face pictures, kept apart from the die, so a die put away now takes its pictures with it. Measured every frame through a HARSH match of eight, a host and a client with six bots, three dice each: a row of pictures up in 7,159 frames on the host and 7,161 on the client, a put-away die's picture drawn in 0 on both; a client's reveal shot shows three dice in the row (`c2-reveal1-10.5.png`, crop `b2-c2-three-reveal1-10.5-row.png`) |
| A bot's dice under its cup, renderers found once (batch M2) | `BotPuppet` now looks a cup's renderers up once and again only when its dice change, instead of about 140 new arrays a second at a full table. Dice watch on the host, every frame, one copy and three bots: CLASSIC Basic, 14,941 frames over 7 reveals, a bot's die drawn outside a reveal in 0; Traditional, CLASSIC and QUICK, 20,772 and 17,101 frames, drawn outside a reveal in 168 and 198, and a die rolled since the last reveal drawn in 0 of either. At the Traditional table a reveal's faces stay drawn for a moment after it, until the next roll: faces everyone has just seen. No pose or new-dice warnings |
| Refused | a test switch standing in for a member on another version: the lobby said CLASSIC with the reason under it, the match said it was refused and played CLASSIC (`(0\|6)`, clock 30) |
| The row | at the top left beside LEAVE, at 4 and 8 seats on host (with arrows) and client (without): clear of the tabs, the deck arrows, every character and plate, and the wardrobe. The words follow the table chosen, and the chat says the choice |
| Lobby list | a QUICK lobby's row has a second pill, QUICK; a CLASSIC lobby's row is unchanged. Quick Join's pick among two one-member lobbies, run without joining: the CLASSIC one, although the QUICK one had the lower id. A lobby Quick Join's own path opens says CLASSIC |
| Block, two clicks | first click: the cross turns gold and the chat says to click again; after 4 s it is red again and nobody is blocked; two clicks a second apart block |
| Sent away | blocking a seated player as host sent them to the menu from a shipped podium (2) and from an extra one (5) at 8 seats; the restored Kick did the same from podium 5 |
| Refused at the door | a blocked copy dialling again was refused before it was sent the lobby: it never left the menu, and the host had no player for it |
| A client's block | a client blocked the host: gold cross, one click unblocked it; nothing happened to the table |
| The host blocks by connection | with two clients' ids swapped on the host, as a host before the id fix leaves them, two clicks on podium 2 stored the id of that podium's own connection, not the one it carried, and sent it to the menu; the client on podium 3 stayed, unblocked |
| A client's crosses need the channel | with the host answering on the channel, each client had red crosses on the other two plates (seen on screen); with the host's channel muted, standing in for a host on an older build, the client gave up greeting it and had no cross on the host's plate (seen on screen), while the host still had one on the client's |
| Clickable at 5 to 8 | the same raycast a mouse uses, at each cross's screen point: the block cross and the Kick on podium 5 and 7 at 8 seats, and the crosses on shipped podiums, were hit first |
| Not at an unmodded table | with the lobby read as untagged, no crosses and no house rules row |
| Hidden | with the account's own id in a test block file, both lobbies left the list, the menu's count read 0, and Quick Join picked nothing; BLOCKED (1) listed one row with UNBLOCK, and UNBLOCK brought both lobbies and the count back |

**Seen along the way, not caused here:** a kicked copy logs one NullReferenceException as it
leaves, with the game's own Kick as well as with the mod's; and with bots at the table, clients
log a remote call that throws on each roll in Liar's Dice (the dice-shake call names a bot, which
has no network object) and one in the Chaos deck. Back in the lobby after a match with bots, Start
stays on WAITING until the bots are replaced, so the tests swapped the bots between matches.

**Not tested here:** a real joiner over Steam (the Steam id fix, the refusal of a real Steam
connection, and a client reading the rules from the lobby), a refused player's own screen over
Steam, a client seated by invite at a real host on an older mod version (a host with its channel
muted stood in for one), a host migration with rules on, the Poker fold key in the first round (no focus), the
ranked screen (switched off by the same check the rest of the ranked block uses), the fun of each
rule, and the 16:10 layout (worked out: the row ends about 20 units before the tabs).

## Seat cameras for players who are out

Loopback with three copies and five bots at 8 seats, in the Queen's table bar. A copy was put out
with a developer kill (the death flag every mode's own death sets), the game turned its spectator
cameras on 11 seconds later, and the test driver stepped through every camera the way A and D do,
with a screenshot of each. Cards, dice, reels and hole cards were looked for in every picture.

| Item | Evidence |
|---|---|
| The cycle | a client out at seat 2 of 8 in Liar's Deck: 7 seat cameras after the game's own 4, in seat order from seat 3. Each step had exactly one camera on, and 11 steps came back to the game's first camera |
| Liar's Deck | every seat camera faces its player across the table, so a hand is seen from the back: card backs only, from all 7 |
| Liar's Deck 2 | seven cards a hand, a client out at seat 3 of 8 beside the host (seat 2) and the other client (seat 1): the game's first camera, then 7 seat cameras in seat order from seat 4, then the game's other 3, one camera on at each step and back to the first after 11. Every hand in sight showed its backs, from the cameras on seats 0, 1 and 2 and from the game's own; the bots' seats were empty chairs from this client. At eight the game's own second view looks over the backs of seats 1 and 2: in the shot their cards were out of sight behind their own bodies |
| Liar's Dice | the host out: cups down and no dice showing from any camera while bids were made. Each seat's row of dice showed only during the game's own reveal after a liar call, when every player is shown them, and was gone again once the next round was rolled |
| Liar's Poker | the host out: every hand lay face down on the table from all 7 cameras; the board was face down too. The host's own body is headless from the other seats' cameras, as it already is from the game's own spectator cameras |
| Liar's Slots | the host out: each camera sees the machines from the table side, which shows only the one reel the game makes public (a spade on the machine that had spun) and the points counter. The other three windows stayed dark from every camera |
| Winner framing at eight | each camera is that seat's winner camera: a close shot across the table at head height. At a ring of eight it takes in half of the neighbour on one side as well |
| Not at an unmodded table | a client with the test switch that reads its lobby as untagged, put out in Liar's Slots: spectating with the game's own 4 cameras and no seat cameras. Ranked is never a modded table, so they never appear there |

**Since then:** a bot's seat is a networked seat (next section), so a client's camera on a bot's
seat shows the bot, with its cup down until the game's reveal.

## Bots at the table: Liar's Dice

Loopback, three copies (the host and two clients) filled to eight with five bots, in the bar the
machine had saved, each person's copy given its own character first. Five matches over three
launches: Basic and Traditional under CLASSIC, then Traditional and Basic under HARSH (three dice,
one sip) on the build as it now stands, and one match without bots to compare. Bots are added with
the test switches only so far; the lobby buttons come in a later batch.

| Item | Evidence |
|---|---|
| A bot's seat on every screen | the host logged five `[bot] a bot's seat is on the network`, one per bot, and none of the game's `Player object is not a player`. Both clients: `laying this client's table out for 8 players (8 in the scene)` and `[seatcheck] 8 of 8 visible here`. The driver's seat list on all three copies: 8 bodies with the same network ids, 5 of them marked bot (a client matches them by Steam id) |
| Eight characters | characters 0 to 7 once each on every copy: the three people 3, 4 and 5, the bots 1, 6, 7, 0 and 2. A client's shot shows them round the table with their name plates (`c3-c3bid2.png`) |
| Bots play | bots opened, raised and called LIAR; the host logged each move by seat. A bot calling SPOT ON was not seen: it calls it only when the bid would be exactly right about a third of the time or more, which is rare with many dice on the table. The voice line of each bot bid went as a real command and was run on the host |
| The reveal shows the bots' dice | client and host shots mid-reveal: `Bot Ace Shows: 0x`, `Bot King Shows: 2x` with the bot's five dice in the row (`c2-reveal1-6.5.png`, `c2-c2table1.png`) |
| Drinking and dying | `Bot King POISONED!` on a client and the host; bots went to 1 life, then out (`[dev:dead] seat 2 is out; 7 still in`). A client that was out, watching a bot's seat camera, shows the bot lifting its poison bottle as it drinks (`c2-drink12-1.5.png`, Traditional). A client's seat camera and the host both show the fox bot at seat 0 slumped over the table once out (`c2-dead0.png`, `host-hostdead.png`), as a dead person's body is. The red panda sat up with its eyes shut once out, both times a bot played it. Batch B2 put a person on it: out after a lost call, the person's red panda sits upright with its eyes shut under the halo too, seen from the host (`host-b2-panda-person-out1.png`, crop `b2-panda-person-out1-crop.png`), so the pose is the character's own and nothing of the mod's |
| A bot wins | Basic: the last three in were bots, and `[bot] the match is won by a bot, at seat 7`; the win screen and awards on a client (`c3-c3win.png`). Traditional: won by a bot at seat 0. Traditional under HARSH (three dice, one sip), with the openings as they now ship: won by a bot at seat 3 |
| The last two seconds | with a test switch making every bot think 40 s, a bot given the turn at 16:30:25.5 moved at 16:30:53.8 (`seat 1's clock is nearly out - moving now`, a bid), and the turn went on: the turn clock is about 30 s |
| Errors | 0 of `Player object is not a player`, `un-spawned object`, `Attempted to serialize unspawned`, `ShakeTarget`, `DrinkRpc`, `[rpc] ... threw` and `Disconnecting` on every copy. The saved run with five bots from 28 October had one of the first for each bot, about 30 `ShakeTarget` lines and 40 refused dice in one reveal. The host logs a few `NullReferenceException` as each match unloads, 8 at eight seats with five bots and 4 in a match of three people with none, so they are not the bots' |
| What the host did for them | the end-of-match line, Basic: 46 commands run for bot seats (their voice lines), 14 target calls run (each drink and death), and 55 dropped, every one of them a bot's dice sent towards its seat, which no screen is meant to show |
| Seat cameras at bot seats, on a client | a client out at seat 4 of 8 stepped through 7 seat cameras after the game's own 4, twice: during a reveal, where the game's own rows show every seat's dice, and during bidding, where each bot's cup was down and no die showed (`c2-bcam8.png`: two bots and a third player, cups down) |
| The host's own screen during a round | bots' cups down and no die in sight while bids are made (`host-hostbid3.png`), the same as a person's seat; for what the host draws of a bot's dice, see No bot's dice drawn on the host, below |
| Seat cameras at bot seats, on the host | the host out at seat 1 of 8 stepped through its 7 seat cameras while only bots played on: a bot taking its turn with its cup down (`host-hcam6.png`) |
| No dice in the log | `HiddenDiceLog` keeps dice values out of every log: a later match's host log has none, and says once that they are kept out |
| No bot's dice drawn on the host | `KeepDiceHidden` draws none of a bot's dice on the host from each roll until a reveal starts. Measured on the host, five samples while bids were made: every bot seat 0 dice drawn; from the liar call on, the bots' dice drawn again for the reveal (`host-reveal1-6.5.png`). A review then found two gaps in that build, which looked four times a second and so could miss a roll. `HideOnRoll` now hides a bot's new dice inside the call that rolls them. Measured every frame on the host (`dicewatch`), loopback, a host and a client with five bots, CLASSIC, watch on before the match: 24791 frames over 6 rolls and 5 reveals, a bot's die drawn outside a reveal in 0 of them and during a reveal in 10904; `botstate` while bids were made: every bot seat 0 of 5 drawn. For people's seats, see Nobody's dice but your own, below |
| A bot whose move fails | nothing in the game ends an ownerless seat's turn (only the owner's copy bids for a person whose clock runs out, and the host's clock stops at nought), so a bot whose move failed held the table for good. `BotTurnGuard` now passes the turn on under one second left. Same run, with the driver's test faults: every bot's move made to throw, the first bot to try logged its failure and `bots are off until the game restarts` at 17:43:16.7, and its turn was passed on at 17:43:42.2 as its clock ran out; adding a bot was then refused (`no bot added: bots are off until the game restarts`); with the brain not running at all, a bot's turn that began then was passed on 29 s later; with the faults off, the seated bots bid again at once (four bids in 11 s) and the match went on. No guard or brain tick errors |
| A person takes a bot's character | in the lobby, a person switched to a bot's character 10: that bot moved to 8, one nobody played, and no other bot moved. An earlier build moved two bots for one clash; fixed |
| Refused everywhere else | in the lobby after a match, with five bots: switching to Liar's Deck sent them away in one line (`Liar's Deck plays without bots for now: 5 bot(s) left the lobby`); then adding was refused once each in Liar's Deck, the Chaos deck, Liar's Poker and Liar's Slots, two tries in Liar's Deck giving one line; back on Liar's Dice a bot joined at once (batch B1; since batch B2 Liar's Deck and the Chaos deck take bots) |

## Bots at the table: Liar's Deck, Deck 2 and the Chaos deck (batch B2, 6 Nov)

Loopback on the kit desktop, quality Low, HARSH house rules (15-second turns, a 3-chamber revolver,
two chaos cards in twelve) so a match reaches its winner in minutes, eight seats filled with bots:
one copy (the host and seven bots) or two (a host, a client and six), the next table chosen with the
driver's `nextmatch` on the same copies. The harness plays the people's seats; the brain plays only
bots. Bots are still added with the test switches only.

| Item | Evidence |
|---|---|
| Each table to a winner | Basic, one copy: won by a bot at seat 6 after 12 rounds, 36 bot throws and 12 liar calls, 7 out, the host's seat last of them. Devil, two copies: won by the client's seat; again with the bots calling every throw (`botcall on`): won by a bot at seat 5. Deck 2, two copies: won by a bot at seat 1, with two fresh deals among rounds that kept the hands. Chaos, two copies: won by the host's seat |
| A bot's throw is a person's | its cards come up (the look pose), it picks them and the game's own throw runs: the throw animation, then its two events taking the cards from the hand and laying them on the table, which the host plays as the bot's own copy (`throw events run as a bot's own copy 106` over two matches). Not once did a bot's cards fail to come up or a throw fail to go (`did not come up` 0, `throw did not go` 0) |
| Bots pull their own trigger | the first match had eight liar calls and not one pull (`fired 0` at every seat): Liar's Deck asks before each pull whether the seat has a connection, and a bot's has none. With a bot's seat answered for: the next match's first lost call killed the bot that made it (`seat 2 is out; 7 still in`), and so on to a winner |
| A bot's lost liar call fires on a client's screen | the client's own copy said `shots: seat 3 (bot) raises its revolver` and then `lowers it` for each pull (04:38:22, 04:39:05, 04:39:48, 04:41:16), and `seat 3 (bot) is out here` for the one that killed; the client's shots `c2-pull1-seat1.png` to `c2-pull4-seat6.png` |
| The devil card thrown and punished | a bot at seat 4 threw the devil card on its own, a bot at seat 5 called it, and the game's volley followed (`[mechanic] DEVIL'S DEAL started for seat 4`): every seat still in, five bots, raised its revolver at 04:41:57.5 on the host and on the client alike, and two died. Without the test switch bots threw the devil card three times in one match and nobody called it: a bot believes a single card as often as any |
| Chaos: bots aim | bots aim at somebody still in, never at themselves or a seat that is out, weighted to whoever has survived the most pulls: `seat 7 aims at seat 5 (2 pull(s) survived)`, `seat 1 aims at seat 7 (2 pull(s) survived)`; in the chaos card's volley six bots aimed at once. A bot aimed at the client's seat (`seat 7 aims at seat 6`, the client's) and the client's own copy fired at a bot's (`the server fires seat 6's aim 3 at seat 7`, an empty chamber) |
| Chaos: the gun put away | a seat's mark that it holds the gun is taken down by its own copy as its shot plays out; a bot's stayed up after the volley, and the first build's brain, which read any seat's mark as a call being settled, held the next bot still for good. The brain now reads the manager's own flags, and two seconds after the server fires a bot's gun the host takes the bot's mark down the server's way. A last match, one copy and seven bots, bots calling every throw: a volley (four bots aimed at once, each gun put away 2.2 s after the shots, `seat 6's gun put away after its shot`), a call that handed a bot the gun (`seat 3 aims at seat 6 (2 pull(s) survived)`, its gun down 2 s after), and three more, to the host's seat's win, with no stall |
| Only bots left | Devil: both people out by 04:40:42, the six bots played on to a bot's win at 04:47:44. Chaos: both people put out by the driver at 05:31:48, the bots played on to a bot's win at seat 5 at 05:35:31 |
| No stall, no rescue | across every match: `its turn was passed on` 0, `bots are off` 0, `a bot has held the turn for 20 s` 0, `RecoverStalledLiarCall` 0, `call was not taken` 0, `no rule for` 0. Two harness faults were found on the way and fixed: the harness threw a card for the host's seat in the moment the game holds every move to call liar itself for the last player with cards, and the table stopped; and in Deck 2 the harness threw from the list of dealt values instead of the cards in the hand, so its seats held seven cards on every screen for ever |
| Liar's Dice spot on, on a client | with the test switch `botspoton on`, a bot called SPOT ON (`seat 3 calls SPOT ON on 1 x 4`) and the client's copy said `moment 1: SPOT ON called, seen here`; bots called it again three times later in that match. A bot's voice line now goes on its own, so a line that fails no longer stops its move (none failed) |
| Nobody's dice but your own | from each roll until the reveal, no other seat's die is drawn. Measured every frame through a HARSH match of eight with two people, on the host and on a client: once the match's first roll was made, no other seat's die was drawn outside a reveal on either copy, apart from the reveal's own faces staying up into the seconds after it, until the next round. During reveals the dice show as the game shows them |
| Memory and CPU, the host, quality Low | 8 seats, one copy and seven bots, Basic: 6,986 MB private, 1,625 MB working set, 126% of a core over 30 s. 4 seats, the same copy after that match, three bots: 7,072 MB, 1,676 MB, 117% over 40 s (memory a match leaves behind is not given back, so this is not a clean 4-seat figure). Chaos, 8 seats, two copies: 7,028 MB, 4,229 MB, 155% over 40 s. Batch M2's 8-seat table at Low held 7.0 GB: the bots hold nothing a seat does not |
| A bare copy | `menupress` at the main menu: refused (`a bare copy presses the game's own buttons only in the lobby`) |
| A throw without the look, Chaos (review fix) | the server's throw takes no card from a hand; only the throwing animation's event does, and at the Chaos table the server plays one for the chaos card alone, so a card a bot threw without the look stayed in its hand, to be thrown again. It is now taken out through the server's own hiding. One copy, HARSH, seven bots, the driver's `botlook off` so no bot's cards came up: 13 throws without the look to a bot's win at seat 7, 5 of them the chaos card (5 `CHAOS thrown`, 5 aims resolved); every throw took its card from the hand (`3 in its hand, 2 now`), each seat's next throw started from what the last left, down to `1 in its hand, 0 now`, and 0 `[bot]` warnings |
| A throw without the look, Liar's Deck (review fix) | the same server call after the throwing animation set by hand, which may not start from a hand whose cards never came up. Basic, the same switch: 6 throws, all `cards did not come up`, of 1, 2 and 3 cards (`5 in its hand, 4 now`, `5 in its hand, 3 now`, `5 in its hand, 2 now`), each taking exactly the cards it named, and 0 `[bot]` warnings. Every round ended in a liar call within two throws, so no seat threw twice in one round here |
| When the game cannot be asked (review fix) | a game update that renames a flag the brain or the guard reads fails the whole method that names it as it is compiled, before any `try` inside it runs, so a renamed settling flag would have held a bot table for good and left bots on. The same Basic match, with the driver's new faults. `botfail tick` (the brain's whole tick throws): `bots are off until the game restarts: the bots' move tick failed` at once, and the guard passed seat 1's turn under a second as before. `botfail settle` (asking whether a call is being settled throws): no tick failed after the switch, no bot moved, and the guard passed seats 4 and 5 `by the clock alone`, 34.3 s and 34.1 s after each got the turn (the 15 s clock, then 20 s at nought). `botfail off`: seat 7 threw 6.5 s later. 0 `could not pass`, 0 `turn guard tick failed`, 0 `held the turn for 20 s` |

Not yet proven: a real joiner, host migration with bots, and a person playing against bots on a
second machine over Steam, which need a second account. The 4-seat figure above is not a fresh
copy's. One `launch-table -DeckMode 3` started a Chaos match; `nextmatch LiarsDeck 3 8` gave Deck 2
both times it was used. One game error without a stack (`NullReferenceException`) came two seconds
after a chaos volley in one Chaos match; the rest came as matches unloaded, as before.

## Bots for players: the lobby (batch B3, 6 Nov)

Loopback on the kit desktop, quality Low, a host and one client (three launches), and one copy on
real Steam. Bots are added with ADD BOT now; the test switches remain for the harness.

| Item | Evidence |
|---|---|
| A person at a full table takes a bot's seat | `LIARSBAR8P_FILL_EARLY=8`: the host filled to 8 with 7 bots before the client started (`filled the table to 8 with bots before the clients arrive`); when the client joined, `a bot left the lobby (6 bot(s) left)` and `the table was full: a bot gave its seat to connection 1`, both before the game made its player. Its plate list on both copies: podium 2 the client, 3 to 8 bots, no `no podium left` line |
| ADD BOT and REMOVE BOT | copies of INVITE above it, at 1833..1917 x 173..279 and 291..397 of 1920x1080, the countdown's empty ring with + and - (shots `host-b3-lobby-l0.png`, crops `b3-host-buttons-l0-crop.png`). Pressed through their own buttons: two ADD BOT to 8/8, a third refused (`the table is already full at 8/8`) with `The table is full` in the chat on the host's screen only (`b3-host-full-chat-crop.png`); REMOVE BOT took the newest. REMOVE BOT shows only with a bot in the room |
| Host only, four languages | host and client in English, Turkish, Russian and Chinese (`b3-buttons-sheet.png`: the host's column on top, the client's below with INVITE alone). Both labels share one size per language: 17.8 English, 21.3 Turkish, 16.9 Russian and Chinese, against INVITE's 25 (the words are kept over the button, where INVITE's run past the screen's edge; `b3-fit-sheet.png`) |
| Kick on a bot | the game's own Kick on podium 3 (`gamekick 3`) and the mod's on podium 5 (`kick 5`) each took the bot standing there away (`a bot left the lobby: the host's Kick`); the plates then read both empty |
| Refused tables | switching to the Poker tab sent 5 bots away (`Liar's Poker plays without bots for now: 5 bot(s) left the lobby`) with `Bots can't play this table yet, so they left` in the chat; ADD BOT then refused on Liar's Poker, Two-Card Poker and Liar's Slots, each saying `Bots can't play this table yet` (`b3-host-refused-chat-crop.png`). Velvet Room, ranked and matchmaking hide the buttons (by the code, not run) |
| A person named like a bot | the harness named the client `Bot Ace` while a bot had it: `bot 1 took another name: somebody in the room has the one it had`, in the lobby, twice (at 8 and at 5 seats); the plates showed eight different names (`host-b3-lobby-l0.png`) |
| The next host | with a bot first in the room, the game's start asked for a bot: `the next host is connection 1, not a bot`, and `newhost` on both copies read connection 1 TOLD, every bot not told |
| Real Steam, one copy, 3 bots | a public lobby, the Deck tab, three ADD BOT: the pool's own search from the lobby returned it, `its row reads 1/8`, state `lobby`, open to joiners, seatable. Up for 38 s and 41 s, nobody joined. The game's own `playercount` read 4: it writes that from its lobby every frame, and the lists read it only when Steam gives no member count |
| A bot's Devil's Deal | with `botdeal on`, `seat 4 makes the Devil's Deal (4 cards in the middle)` on the host; the client's screen showed the bot's claim (`c2-pull4-seat4.png`), then its revolver raised at 07:38:16.7 and lowered at 07:38:23.7 (lost, and it lived), and the next round was dealt at 07:38:26.5 |
| Nobody's dice at a table without the tag | the host with `notag 1` (Table.Modded false), Liar's Dice, a host, a client and three bots, 3 reveals: once the first roll was made, no other seat's die was drawn outside a reveal, the person's among them. Before this batch a person's cup was hidden only at a modded table |
| The table's mode on a client | `TableHand.Playing()` now reads the scene's seats where there is no roster. A client at Liar's Poker no longer draws the mod's turn line over the ladder (`c2-b3-texas-c2b.png`) |
| The Chaos deck's error two seconds after a volley | not seen again: a Chaos match of eight with a client, bots calling every throw, 10 aims resolved and 2 chaos cards thrown to a bot's win, 0 `NullReferenceException` during play on either copy. Two seconds after each shot both copies log the game's `Found no receiver for incoming ClientRpc [26549]`, 25 each: `RpcSyncRevolverUi` and `PlayHeartBeat` share that number (NOTES.md, colliding hashes). The mod's work at that moment, a bot's gun put away, runs inside a tagged try and was never named |
| The harness | the Chaos deck asked for by name (`-Mode LiarsChaos`) now sets the deck tab and variant 2 (it played Liar's Deck); each start checks the deck and dice variant once more, as it does the house rules |

Only real players can prove a Steam join into a room with bots, the joiner's Steam id fix
(`SteamIdFix`, which does nothing under loopback, where a connection is no Steam id), and a host
migration with bots.

## Translations

The mod's words come from a table inside the plugin (`words.tsv`), keyed on the English, in Turkish,
Russian and Simplified Chinese, machine translated. The language is the game's own setting, followed
within a quarter of a second. Checked on one copy on real Steam and a loopback table, switching the
game's language the way its settings do but without saving it (the saved setting read 0 before and
after).

| Item | Evidence |
|---|---|
| Following the game | the log said Tr, Ru, Zh and En in turn, each a quarter of a second after the switch, and the game's own menus changed with it |
| The tag | `MAKS 8`, `МАКС 8` in Cyrillic, `最多 8` beside the Latin name and version |
| Lobby count | `3 FULLY LOADED LOBİSİ AÇIK · 7 OYUNCU BEKLİYOR`; Russian `1 ИГРОК ЖДЁТ`, `22 ИГРОКА ЖДУТ`, `7 ИГРОКОВ ЖДУТ` (the three plural forms); Chinese `3 个 FULLY LOADED 大厅开放中 · 7 名玩家等待中`, all in the game's face for the language |
| Update window | all three fit the window: Turkish in Oswald Bold, Russian in Oswald Regular, Chinese in Noto |
| Ranked notice | the box over the lobby list in all three |
| Block list | the footer button `ENGELLENENLER (1)`, `Заблокированы (1)`, `已屏蔽 (1)` beside the game's own buttons |
| Lobby | on a loopback host: the rules line `Обычные правила игры`, `游戏原版规则`, `Oyunun kendi kuralları`, and the first click on a block cross said `Нажми на крестик ещё раз, чтобы заблокировать: Player2`, `再次点击叉号以屏蔽 Player2`, `Engellemek için çarpıya tekrar tıkla: Player2` in the chat. The HOUSE RULES and WARDROBE titles kept the language they were built in on that run; they now follow a change from each row's tick (built, not run again) |
| Tab board and turn line | Liar's Deck: Chaos at 8: `KART HAZNE` with `Sıra: BOT-3`, `КАРТЫ ПАТРОН` with `Ход: BOT-3`, `手牌 弹膛` with `轮到 BOT-3` |
| Awards | the made-up panel in each language, with the Russian counts in their forms: `4 обмана не раскрыты`, `3 лжеца поймано`, `выжил после 5 выстрелов`, `6 ходов, ни одной лжи` |
| Aim line | Chaos at 8, in the match. A real aim on the host in Russian: `Цель зафиксирована: Player3 - рядом с тобой, справа`. Then the line a real aim builds, on each copy in its own language, for two seats right, three left once locked, straight across and next to you: `Цель: Player2 - на 2 места правее тебя`, `Цель зафиксирована: Player2 - на 3 места левее тебя`, `Цель: Player2 - прямо напротив тебя`; `Nişan: Player2 - 2 koltuk sağında`, `Nişan kilitlendi: Player2 - 3 koltuk solunda`, `Nişan: Player2 - tam karşında`; `瞄准: Player2 - 你右边第 2 个座位`, `已锁定: Player2 - 你左边第 3 个座位`, `瞄准: Player2 - 在你正对面`. The keys line under each, in the same language |
| Following a change while up | mid-match on a client the board's key hint went from `Players` to `Игроки`; the awards panel, made in English, read `ÖDÜLLER` with every title and number in Turkish after a switch on its win screen, and a client already in Russian showed `НАГРАДЫ`, `3 обмана не раскрыты`, `4 лжеца поймано`. Save Bug Report in the menu's settings, made in English, followed each switch within a second with the settings open (`Hata Raporu Kaydet`, `Сохранить отчёт об ошибке`, `保存错误报告` beside the game's own `返回`, in a picture), and one made in Russian as the menu came back went to English when the game put its saved language back a third of a second later |
| Built, not run | Save Bug Report in a match's settings (Pause, Settings) after a switch, which is the menu's code, UNBLOCK on rows already open in the BLOCKED list (written again with the names each second), the What's New window's title and CONTINUE (in the language's face), `OLD or NO MOD` in the version warning, and MAX in the plain version line drawn before the game's font is up. The row for the lobby's "The house rules just changed" had lost its tab and never matched; it does now |

A switch made with the test driver is not saved, and the game puts its saved language back when a
match loads, so a test switches after the table is up.

**Not translated, on purpose:** logs and the bug report file, the mode names, the mod's name, the
presets CLASSIC, QUICK and HARSH, and lines sent to the whole table's chat.

## Wide screens, 21:9 and 32:9

`Ultrawide.cs` puts the monitor's own shape in the game's display list, makes the one-time pick
and keeps a copy without the mod from ever finding a size it does not know. `UltrawideArt.cs` makes
every screen-space canvas keep its height while the screen is wider than 16:9, crops the two
videos, keeps the DRAW and GOD SAVED lettering in shape and puts black at the sides of the two fixed
pages. Run in the game on a second screen through the test switch `LIARSBAR8P_UWTEST=WxH`
(developer mode only), which takes its size as the desktop's, selects it, keeps the copy in a window
and saves none of the game's settings. One wide copy at a time; a second loopback copy at 1920x1080
for the modes bots cannot play and as the regression copy.

| Item | Evidence |
|---|---|
| The list | desktop 3440x1440: the game's seven untouched, then 1720x720, 2580x1080, 3440x1440 and every size the monitor reported at 1.85 to 1 or wider (on one run 1680x720, 1920x820, 2100x990, 2310x990, 2560x1080, 3024x1296; on another none), narrowest first, in the game's own selector on the Display page (shot) |
| Choosing | the arrows move the selector and Apply applies, as the game does. 3440x1440, 2580x1080, 2560x1080, 2310x990, 2100x990, 1920x820 and 1720x720 were each applied and kept; 1920x1080 applied, the kept size forgotten, and every canvas and picture put back in the same moment; then 3440x1440 kept again |
| F7 | the list comes back with the wide sizes. The size stays the one on show: the game's own F7 reloads the selector from what it last loaded, its own sizes included |
| The one-time pick | the decision alone, through the driver: a 3440x1440 desktop playing at 1920x1080 picks 2580x1080, at 1366x768 1834x768, at 3840x2160 the desktop itself; 2560x1080 at 1920x1080 picks 2560x1080; 5120x1440 at 1920x1080 picks 3840x1080; a 1920x1080 desktop picks nothing. It never runs in developer mode, so it has not run for real |
| What is saved | a test copy saves none of the game's settings (logged once per copy); the registry's resolutionIndex and fullScreenIndex read the same before and after. The player's path, a wide place written as the game's 1920x1080, has not run: it needs a copy outside developer mode |
| Kept between sessions | the kept size and the spent one-time pick live in `display.txt` under `%LOCALAPPDATA%\LiarsBar8P`, not in the mod's config, which both installers delete on every install (a reinstall used to lose a 3440x1440 and run the pick again over a 16:9 choice). Driven in one developer copy with a role, which keeps `display-ROLE.txt` (its desktop was the second screen, so its list was never lengthened): no file read as nothing kept and the pick not spent; keeping 2580x1080 wrote `wide=2580x1080` and `picked=yes`; choosing from the game's own list then wrote `wide=` and kept `picked=yes`; a file written from outside, as a reinstall finds it, read back as 3440x1440 kept and the pick spent. The player's own `display.txt` was never created |
| Main menu, 3440x1440 | the background video cropped, its lanterns round; the logo whole; buttons, news, radio, player card and version tag in place (shot). Also seen at 2560x1080, 2310x990, 2100x990, 1920x820 and 1720x720 |
| Display page | at 3440x1440, 2560x1080 and 1920x1080: the list and its values in place, Apply at the bottom right (shots) |
| Table Stories | the 16:9 page between black sides; nothing of the menu shows through, and the sides take the clicks the page's own art takes (shot) |
| Lobby list | at 3440x1440 and 1720x720: header, filters and footer in place. **No rows to look at**: no FULLY LOADED lobby was open, and a tagged lobby hosted from a second copy on the same account did not come back in the list |
| Lobby at 8 seats, 3440x1440 | 2 copies and 6 bots: podiums and name plates, LEAVE, the HOUSE RULES row, the tabs, the Poker tab's plate (then the game's own TEXAS; it reads LIAR'S POKER since) with the host's arrows, START, SWITCH BAR, INVITE, chat and the bottom bar (shot). On the Poker tab stepping right chose TWO-CARD POKER and left went back, through the arrows' own clicks |
| Wardrobe strip | not seen: both copies played characters with no outfits, for whom the strip is never built, at 1920x1080 as well |
| Match HUD, Devil deck, 3440x1440 | turn line, round card and claim block top left, board hint, chat, emote and key strips and the Look hint in place; the Tab board centred and sized to the height (shots). The Devil card strip is switched on and sits 83 units above the bottom of a canvas that is now 1080 units tall, but the game had it faded out at every shot |
| A size change mid-match | Pause, Settings, Display in the bar: 2580x1080 applied and back to 3440x1440; the HUD, pause and mod canvases re-fitted each time (shot at 2580x1080) |
| Turn line and aim line | the aim line centred under the middle of the table, clear of the HUD; the turn line under the game's claim block (shots) |
| Chaos deck, 3440x1440 | round header, claim block, centre call, emotes and keys in place (shot) |
| Win screen, 3440x1440 | the winner camera with name and medal, RETURNING TO LOBBY with its countdown at the top right, 10.6 s left four seconds in (the 15 s of a modded table). No awards: the match was ended a minute in, before anything to award |
| Liar's Dice, 3440x1440 | the bid block top left with the turn line under it, the dice count top centre, the table's call in the middle (shot) |
| Liar's Poker, 3440x1440 | two copies, no bots: the key hints Tab, Q and F1 along the bottom, 31 units above the bottom of the canvas (they were below a width-only canvas), the hand values at the left edge, the actions at the top right, the timer top centre (shot) |
| Liar's Poker's hand rankings, docked | the rankings hang from the middle of the HUD at the 16:9 left edge's distance, so a wider canvas showed 460 of their 712 units all match at 21:9. Kept that far from the left edge by their anchors (0.372 at 3440x1440, 0.25 at 3840x1080): idle, only the 16:9 strip of 128 units shows; Tab open (the game's own `On` switch on their animator), all 712 units from the left edge, x 0..949 of 3440 and 0..712 of 3840, as at 1920x1080 (0..712) (shots at both sizes). Their animator plays only their place (clips `tabmenutexason` and `tabmenutexasoff`, switch `On`); the anchors read the same after it ran |
| Liar's Poker's showdown rows, framed | the four rows also hang from the middle, parked just past the 16:9 left edge, and slide in to a banner centred under the shared cards. Docked to the left edge as the review asked, the shown banner moved 330 units left of the shared cards at 21:9 (960 at 32:9; shot of the first build), so instead the rows' holder is clipped to the middle 16:9 while wide: the rows keep the game's own anchors and animation, a parked row (x -334..926 of 3840) is out of sight, and a showdown shows the banner centred under the shared cards at 3440x1440, as at 1920x1080 (shots) |
| Pause fade, loading and credits | `Pause/Group` is a fixed box, so the fade, the loading panel and the credits covered only its 1920x1080. While wide they are the whole canvas's size (2584x1084 at 3440x1440, 3844x1084 at 3840x1080) and back to their own at 1920x1080 (read with the driver after each change). Leave from Pause at 3440x1440, through its own LEAVE and the "Do you want to leave the game?" confirmation: the loading frame is evenly dark across the width (mean brightness 13 at the left strip, the middle and the right strip), then the menu (shots) |
| The host change's picture | left as it is: forced on to look at it, the picture is see-through but for its small "SWITCHING HOST..." box in the middle, at 1920x1080 and at 3440x1440 alike, and the game's own shading behind it stretches across the width, so nothing leaves a strip (shots). A black backing, as first built from the review, blacked out the whole bar at 3840x1080 (shot) and was taken out |
| Back at 16:9 | stepping the host from 3840x1080 to 1920x1080 in the bar put the rankings' anchors back to 0.5, the three pause panels back to their own stretch at 1920x1080, and took the clip away; back to 3440x1440 put all of it on again |
| The list's order, and the pick | every list is now built one way, the wide sizes narrowest first with the kept or picked size in its own place: the test copy's list read 1720x720, 2580x1080, 3440x1440, 3840x1080 after the game's seven, and the driver's pick for a 3440x1440 desktop at 1366x768 is 1834x768 at place 8, where every later list has it (it used to be added last, so the next list moved it). The wide sizes only go on with both the save guard and the choice patched (the plugin's log shows all three) |
| Liar's Spin, 3440x1440 | two copies, no bots: the bid line and the leaderboard rows top left with the turn line under them, the reveal in the middle, the reels at the bottom, the F1 hint (shot) |
| Bar canvases | in every bar scene: the mod's canvas (already 1), the pause canvas (0.87) and the match HUD (0, the width alone) follow the height, and DRAW and GOD SAVED keep their shape |
| The Devil card strip, measured | its rectangle on the screen, read in the match: x 1121..1439, y 69..97 from the bottom at 2560x1080, centred and where a 16:9 screen has it (a width-only canvas puts it at y -69..-41, off the screen); x 534..746, y 86..105 at 1280x800, where nothing is changed |
| 2560x1080 | the lobby at 8 seats and the Devil and Dice HUDs, the size set mid-match from the pause menu and kept back in the lobby and the menu; every canvas following the height (shots) |
| 1920x1080, the regression copy | every scene logged "not wider than 16:9, no canvas changed" and "no picture changed"; its menu, lobby and Devil HUD look as the same screens did in batch H's shots |
| 1280x800, the regression | the menu, the lobby at 8 seats and the Devil HUD: every scene logged "not wider than 16:9, no canvas changed" and "no picture changed", the game's own 16:10 layout (shots). No earlier shots at this size exist to set beside them |
| 32:9 | a 5120x1440 window was held by Windows to 4520x1440, the width of the whole desktop, so 32:9 itself ran at 3840x1080. At both: the lobby at 8 seats with the edge buttons out at the edges and the tabs, podiums and bottom bar in the middle; the Devil HUD with the strip centred at the 16:9 height (x 1761..2079, y 69..97 at 3840x1080); Dice; the menu with its video cropped (shots) |
| Neighbours' hands at 32:9 | from the host's seat at 4520x1440 with every seat told to raise its cards: the neighbours' faces and hands fill the lower corners and no card is in sight, nor anywhere else at the table (shot, corners at full size) |
| Not built | the optional pinning of split pairs: the one pair that drifts apart in the shots is the menu's player card at 32:9 (the title medal stays near the middle, the rank card goes to the edge), and it still reads as two parts |
| Test windows | the game's own window was found opening on the main screen for a few seconds at each kit launch (the launcher's minimised start reached only the BepInEx console), and twice staying there. The kit now writes each copy's place into the game's saved window position before starting it, and a copy with `LIARSBAR8P_WINDOW_AT`, plain or developer, holds its game window there every frame without activating it and never asks for full screen. In the last two runs a watcher reading every game window ten times a second saw none on the main screen, launch included |
| Test windows, the guard | the kit's window guard minimises any window of a copy it started that lands on the main screen, without focus, and kills one that lands five times in a minute. Tried first with its box on the second screen: it caught a copy's console and window there, minimised without focus, and then failed every later check (a variable that named both the box's right edge and a window's rectangle), now fixed; with a stand-in window that kept coming back it minimised it five times, two seconds apart, and killed it. Copies were found taking the foreground: the minimised start activates the console, and restoring the game window from minimised let the game take the foreground once the desktop had been idle past Windows' foreground lock. Copies now start with SW_SHOWMINNOACTIVE and the kit never restores the game window (it renders and takes screenshots minimised); the game shows it itself, and the developer copy's hold now also turns every move of it, the game's own centring included, into a move to its spot from `Plugin.Load` on. Over the sessions after that, eight copies, the four hosts at 3440x1440 and one of them at 3840x1080 as well: the guard logged nothing on the main screen and the watcher saw no copy take the foreground |
| Test windows, a plain copy | the hold needed developer mode, so a plain copy, and any build without the hold, would come up on the main screen at its first menu, where the game resizes its window to the saved size and every size in its list is wider than screen 2. It now needs only the kit's variable, and the kit starts no build without the hold (it looks for the hold's marker in the plugin, and refuses an installed 2.0.0). One plain copy, developer mode off: held at its spot from the plugin's load, its first show turned from 3551,30 to 3450,30, the menu at 1920x1080, its window minimised with its restored place on the second screen; the guard and the watcher logged nothing, and no display file of the player's was written |
| Test windows, a guard that waits for nothing | ShowWindow and SetWindowPlacement wait for the copy's own thread, so a copy that was not answering held the guard there, with nothing logged and no other copy guarded. The minimise is now posted, the restored place is set on a thread of its own, and the guard writes a heartbeat the launchers require. With its box on a part of screen 2 and stand-in windows: one that stopped answering was killed 1021 ms after it was first seen, and within that second the guard minimised another in 77 ms; that one, coming back every 2 s, went in 62 to 92 ms each time and was killed at the fifth; one already not answering when first seen was killed at once; the heartbeat was never older than 549 ms and the launchers' check never failed |

Only on a real ultrawide, by its owner: full screen on the main monitor at 3440x1440 with no bars,
the one-time pick on a real launch (it never runs in developer mode), the saved index after
quitting (the registry's resolutionIndex should then read the last of the game's own sizes used,
4 when none was, and `display.txt` the wide size and that place), a kept 3440x1440 still kept after
a session on a smaller monitor, the
kept size and the spent pick after a reinstall with either installer, a 16:9 size saved by the game
without the mod standing over the kept wide size when the mod comes back (that check only runs on a
wide desktop outside developer mode), and the frame rate at the native size.

## Test copies: focus, the second screen and the test lock (kit only, no player's game changes)

| What | State |
|---|---|
| The test lock | proven without the game on 5 Nov: every guarded kit step refuses without it (launch, deploy, settings save, lower and restore, the installed game's restore, stop), two takes at the same instant gave one winner in each of three rounds, an ended session's lock was taken over and logged, a live other session's lock was refused, and release refused while the game's plugin differed from the one recorded at the take |
| A held copy never activating its window, and handing back a foreground it gets (`HeldFocus`) | ran once (one copy, 5 Nov 20:16): the hold placed the window on the second screen and the copy's mark for the other held copies was visible from outside. The copy still took the foreground from the window in front 13 s after it started and kept it 69 s until it was stopped: Unity took it as its window opened, about 4 s before the plugin loaded, so the thread had seen no window to give it back to. The launcher now passes the window that was in front at the start, so the thread can hand it back on its first check. That is built and not yet run, and a copy still holds the foreground for those first seconds. Never handing it to another held copy, and the cap of six hand-backs in ten seconds, are built and need two copies and a real steal to show. Since batch K2 every test copy runs on the kit's own desktop, where nothing is ever in front and the hand-back is off, so all of this now matters only for a copy started with `-Shown -OwnerDesktop` |
| Every test copy on the kit's own desktop (`LiarsBar8P-kit`, batch K2) | proven on 5 Nov. A probe first: `charmap.exe` started there hidden had its 13 windows on that desktop and none on the main desktop, and the desktop was gone once it stopped. Then three launches, every copy there: a full copy from the menu through the lobby to a Liar's Dice table of 4 with 3 bots, played to a bot's win in 4 minutes with the awards shown; two bare copies, host and client, from the menu through the lobby to a 2-seat table; and two full copies, host and client, at a table of 4 with 2 bots. The copies' own screenshots are real pictures, the host's and the client's (1920x1080, 1160 to 1320 colours in 1600 samples, mean brightness 35 to 59), and the table shot was looked at. They draw at the game's own cap of 60 frames a second (59.6 to 60.0 over 10 s, host and client, full and bare, from the driver's new `fps` command), at 1.3 to 1.6 cores each. Not one activation message or foreground reached any copy (0 WM_ACTIVATEAPP, 0 FOREGROUND lines), and the guard and the watcher logged none of ON THE MAIN SCREEN, KILLED, GUARD ERROR, SEEN, FOCUS, UNREGISTERED or ON THE OWNER'S DESKTOP. `stop-test.ps1` stopped every copy each time, with no crash handler left and the desktop gone. Not compared with a copy on the main desktop: no earlier log holds a frame rate or a CPU figure, and this batch ran no copy there. The ON THE OWNER'S DESKTOP line itself has not fired yet, as it must not |
| Unity's `-monitor 2` | tried once and dropped: the copy opened in the second screen's corner with its frame's invisible border over the main screen, and the guard killed it; the kit keeps writing the remembered monitor |

## Release 3.0.0: the last fixes (batch R1, 6 Nov)

Version 3.0.0 in the csproj and `Plugin.Version` (the plugin's file version reads 3.0.0.0), the
round-2 fair play follow-ups, the three bot-lobby minors from batch B3 and the two ultrawide minors
from batch P. Loopback, every copy shown on a second screen at quality Low, under the
test lock, with the window guard and the watcher running.

| Item | Evidence |
|---|---|
| The pause screen's panels at a wide size (P's minor) | one copy at `LIARSBAR8P_UWTEST=1032x432`, the bar loading for a Liar's Deck match: the canvases followed the height at 21:56:22.370 (`3 canvases follow the height`), the panel pass in that same tick covered `3 pause panels across the screen at 2488x1044` (the old scale, the size the review found), and the pass run again two frames later, at 22.580, covered them `at 2584x1084`, the whole canvas (1032 / 0.4 + 4 by 1080 + 4). Before, the next look came 3 s later |
| A kept wide size the list no longer holds (P's minor) | from the code only: when a list is built without the kept size (another desktop) while the size place in memory is a wide one, that place is put back to the one the wide size is saved as before the game loads the selector, so the selector never lands on another wide size that would be taken as the player's choice. Not run: a test copy's desktop is its test size for the whole run, and a copy that loads a 16:9 place in a test window would size the window past screen 2. Needs the owner's monitor change |
| A Steam member who is not seated, on another version | from the code: `SameVersion.Count` reads only the seated players, each by the Steam id its connection holds; the walk over the Steam lobby's members is gone. Such a member holds Start only through the arriving wait, which ends after 15 s (`ArrivingLimit`), as it does for a member showing no version |
| ADD BOT stops Quick Join's lone-host merge | from the code: `BotManager.AddFromLobby` calls `QuickMatch.StopLooking()` before the bot is made, as the house rules and the Poker tab's table do. Quick Join is not on in the loopback harness, so the merge itself cannot run there |
| Liar's Deck to a winner at 3.0.0 | one copy and 3 bots, Basic, shown at 1032x432: up at 21:56:24 with hidden hands on, played to a winner at 22:05:54 (`awards shown for Deck, 5`, nobody with more than two). 0 `[fair]` removals, refusals or errors; the only `[fair]` lines were the thrown cards turned over at each reveal. One Unity `NullReferenceException` as the last seat went out and five as the lobby loaded after, none from the mod's code (R2 compares them with v2.0.0) |
| The version on screen | `FULLY LOADED v3.0.0 MAX 8` at the top of a Liar's Dice match (`r1dice-r1-dice-tag.png`, crop `r1dice-r1-dice-tag-top.png`); the copy's own log opens `Liar's Bar 8P 3.0.0` |
| Liar's Dice to a winner at 3.0.0 | one copy and 3 bots, Basic, at 1920x1080: up at 22:07:23, the host out at 22:08:40, a bot's win at 22:11:23 (`awards shown for Dice, 4`, nobody with more than two). 0 `[fair]` removals, refusals or errors. Four Unity `NullReferenceException`s as the lobby loaded after the match, the four R2 compares with v2.0.0 |
| A bot leaves the last seat free when the part that gives a seat to a joiner is off (B3's minor) | the result of applying `BotLobby.Arrival` is kept (`BotLobby.ArrivalOn`). With `botarrival off`, seven ADD BOTs from the host alone stopped at six bots, a table of 7 of 8: `no bot added: the last seat is kept free for a person`. With it back on, one more filled the table to 8 |
| More people than seats (B3's minor) | the host also counts a connection whose player is made but not yet on the roster, and every lobby tick lets the newest bots give way while more are present than seats. At a full table (the host and 7 bots), `botcrowd 2` (two pretend arrivals, as two joiners in one server frame would be) at 22:12:24.005: in the next tick, 22:12:24.261, `more people than seats: a bot gave its seat up` twice, 5 bots left, the table at 8 with the two pretend; nothing more left after `botcrowd 0`. Two real joiners in one frame need real players |
| The Texas owed pull on a copy that took the table over (round 2's R4a) | the gate on a match hosted from its start is gone from `HostDecidesTexas.Owe`. The loopback stand-in for a takeover: `fair texaspull skip 1 takeover` makes the host count as a copy that took the table over in a host migration until the next match (`watched the whole match: False`), then drops that seat's next ask. A host and a client, Liar's Poker, match up at 22:13:54, armed at 22:14:04: the client's ask was dropped at 22:16:04 (`its deadline rolls the pull`), and at 22:16:15 `seat 0's owed pull rolled by the host (it does not fire)`, `PASS texas pull`. Before the change this copy would have kept no owed pull and let the ask through. A real host migration needs real players. No pull is rolled twice across one: a copy that starts serving starts every per-match record afresh, and the game's own restore deals the Texas round again (`WaitforNextRoundMigration`) |
| Liar's Poker to a winner at 3.0.0 | a host and a client (Liar's Poker refuses bots), at 1920x1080: up at 22:13:54 with hidden hands on, calls, folds and the owed-pull test above, the client out at 22:18:51 and `awards shown for Poker, 3` at 22:18:56. On the host 0 `[fair]` removals, refusals or errors (`caught=0 refused=0`); on the client `host check watching - the host runs this build` and nothing else from fair play. The client's screen shows the host's test name and the v3.0.0 tag (`r1c2-r1-c2-texas.png`) |
| Strict versions: a seated player on another version (round 2's R1, R2 and R6) | the same table back in the lobby after the match: `fair version` read behind=0; `fair version other 1` (the seated client pretended onto another version) gave `1 player(s) in the lobby show another mod version - Start waits`, with the room told once over two passes; `fair version same` gave `Start no longer waits for it`, behind=0. Pretended onto another version again (behind=1), the client then left (`connection 1 left`, 22:19:59), and the host's count read behind=0 at 22:20:09: the hold ends when they match and when they leave. Loopback has no Steam lobby, so Start's own light and the lobby label were not looked at; they need a real Steam table, as before |
| Guard and watcher | four launches (one Deck copy, one Dice copy, then a host and a client for Liar's Poker), every one `-OwnerDesktop -Shown`: guard.log has no ON THE MAIN SCREEN, KILLED, GUARD ERROR, SEEN or UNREGISTERED line, and watch.log has seven FOCUS lines, each a copy opening (21:55:48, 22:06:47, 22:13:05 to 22:13:18), which the shown tests accept. The installed plugin and config were put back and checked byte for byte, the display settings restored, and the test lock released |

Only real players can prove a real host migration in Liar's Poker, two people joining one lobby in
the same server frame, Start's light and the lobby label at a real Steam table with somebody on
another version (in Turkish and Russian too, for the label's fit), a joiner leaving a v1.x host's
untagged lobby, and a kept wide size surviving a real monitor change.

## Release 3.0.0: pictures and every mode to a winner (batch R2, 7 Nov)

The 3.0.0 build with R1's fixes. Loopback, every copy shown on a second screen at the
PC's own quality, under the test lock, with the window guard and the watcher running: one copy
and bots at the tables bots play, a host and a client at the three that refuse them. The README's
pictures came from these same matches.

| Item | Evidence |
|---|---|
| Liar's Deck, Basic, at eight | one copy and 7 bots, CLASSIC: up at 01:15:04, a winner at 01:34:31 (`awards shown for Deck, 5`, nobody with more than two). 25 reveals for the 7 seats that went out |
| Liar's Deck 2 at eight | the same copy, `nextmatch` to the fourth deck table, 7 bots: up at 01:36:35 (`the table running this match is Liar's Deck, Deck2 variant`), a winner at 01:56:57 (5 awards, nobody with more than two) |
| Liar's Dice, Basic, at eight | 7 bots with their own names: 01:57:50 to a winner at 02:09:55 (5 awards, nobody with more than two) |
| Liar's Dice, Traditional, at four | variant 1, 3 bots: 02:10:38 to 02:15:06 (4 awards) |
| Liar's Deck, Devil, at four | 3 bots: 02:15:57 (`Devil variant`) to 02:26:31 (5 awards, nobody with more than two) |
| The Chaos deck at eight, at 21:9 | a new copy at a 2580x1080 test size (`display sizes after the game's seven: 1720x720, 2580x1080`), 7 bots: 02:28:49 (`Chaos Deck`) to 02:39:40 (5 awards, nobody with more than two). The HUD spans the whole width, at its usual height (`ultrawide-21-9.jpg`) |
| Two-Card Poker | a host and a client: `everyone sits down as Scubby`, up at 02:43:16, seat 0 out in the third deal at 02:44:37 and seat 1 the winner, back in the lobby at 02:44:57. No awards on this table's win screen |
| Liar's Poker | the same two, `nextmatch` to the released table: up at 02:54:23, a winner at 02:57:49 (`awards shown for Poker, 2`) |
| Liar's Spin | the same two: up at 03:04:59 (`the table running this match is Spin`), the win screen at 03:08:26 |
| Fair play | in all nine matches, 0 removals, 0 refusals and 0 errors from fair play on the host. The client's only fair play lines were `host check watching - the host runs this build` at each match and `paused - not in a match` after it |
| Exceptions, all the game's | Unity's own log, kept for the five-match copy, the Chaos copy and the client, has the stack of each one, and none has a method of the mod on it; the host of the last three tables logged the same lines at the same moments. As each match went back to the lobby, one `NullReferenceException` per player there from the game's lobby player update looking up a Velvet Room slot (`PlayerObjectController.Update`, inside `PlayerObjectControllerVelvet`): 8 at eight seats, 4 at four, 2 at two. Now and then one more there from `LobbyController.OnStartClient`, and in Liar's Deck and the Chaos deck one from the game's `DeckAnimEvent.DeadCloseEyes` as the last seat went out. Per match on the host: Deck 9, Deck 2 9, Dice at eight 8, Traditional 5, Devil 5, Chaos 10, Two-Card 3, Liar's Poker 2, Spin 3; none during play otherwise. The Chaos deck also logged the game's `Found no receiver for incoming ClientRpc [26549]` 4 times, its colliding remote-call hash. The client logged one `ArgumentOutOfRangeException` in Liar's Poker, at 02:55:36, from the game's `TexasCard.SetMesh` in its card-switch animation (`BulletEvents.KartSwitch`), just after a swap the game did not take; the host logged none. R1's Poker client logged one too, and batch H's control on the released Liar's Poker one per client at three seats |
| Against v2.0.0 | no figure for either exception was ever recorded for v2.0.0: STATUS, NOTES and the batch notes hold them only for builds after it, and the logs kept from before matchmaking are 1.1.2 and 1.1.3 runs with no return to the lobby. v2.0.0 cannot run in the kit either, which refuses a build without the window hold. So the comparison is by the stack: each is raised in the game's own code, and the lobby's count follows the number of players (batch B1: 8 at eight seats, 4 with three people). Not run: the swap at three seats, which needs a third copy, and with two copies at a two-seat lobby holding 10.7 and 10.8 GB, 26.1 GB was free, so a third would have left under the 16 GB the kit keeps free |
| What's New after an update | with the updater's restart marker set to 1.1.3, a copy at the main menu showed the 3.0.0 and 2.0.0 notes: 26,389 characters given and 23,634 laid out in one text, every visible one of them. The top, the middle and the bottom were looked at (`wn113-before-*.png`), and the bottom ends on 2.0.0's last line. Set to 2.0.0, the 3.0.0 notes, 17,190 laid out, ending on its last line (`wn200-*.png`). Unity logged no mesh or vertex error. The text is not cut short, so WhatsNew was not changed; its `changelog up` line now gives both counts |
| The pictures | 11 for the README, in `docs/images/` (ten at 1920x1080, one at 2580x1080), each looked at whole: the names are Player1 to Player8 or the bots' own, there is no developer overlay or F8 hint, the lobby ID reads 0, and no lobby name shows but the list's stand-in. At eight seats, eight different characters; Two-Card Poker is the one table of one character. Each was decoded and its pixels copied into a new image before encoding, and each JPG holds only the JFIF marker: no EXIF, XMP, ICC or comment. `tab-board.jpg` was taken again after the card-count fix (7 Nov, one copy and 7 bots, 1920x1080, the same name and clean-shot switches, encoded the same way and checked the same): a seat at 2 cards just after a throw of three, the three on the table at the liar call, one seat out, two at 1/6, and the board's counts the host's own |
| Test switches for the pictures | developer mode only: `LIARSBAR8P_BOTNAMES` (and the driver's `botnames`) gives bots made-up names before their own, so the eight-seat pictures read Player1 to Player8; the driver's `rownames` names every row of the lobby list, so the list picture names nobody (a real-Steam host and a list copy, one row, `Player1's lobby 1/8 FULLY LOADED QUICK`) |
| INSTALL.txt in the zip | retitled `Liar's Bar: Fully Loaded`, and now says what the README says: everyone at a table on the same version, ranked off, install, updating (1.1.3 and later update themselves, 1.1.2 and older install by hand), and Save Bug Report. `package.ps1` rebuilt the zip: the same 244 entries in the same order as the list recorded with the updater harness, and only `INSTALL.txt`, `README.md` and the DLL differ in size. Nothing on the updater's must-stay-fixed list moved |
| Memory, one copy at the PC's own quality | 9,011 MB private at the menu's lobby list, against batch M's 8,949 at the main menu; 10,220 MB in its own lobby with one player, against 10,150; 13,313 to 13,349 MB private and 7,834 to 8,151 MB working set at an eight-seat Liar's Dice table with 7 bots (six samples over 42 s), against 13,290 / 8,049 |
| Guard and watcher | nine copies, every one `-OwnerDesktop -Shown`: guard.log and watch.log have no ON THE MAIN SCREEN, KILLED, GUARD ERROR, SEEN or UNREGISTERED line. watch.log has 13 FOCUS lines, each as a copy opened. Three copies took the foreground a second time after Windows had shown a ghost window for them while they loaded, and kept it until something else took it: 119 s, 152 s, and about 30 minutes for the Two-Card client, until it was stopped |

Only real players can prove eight people's own screens at one table, the Texas swap at three or
more real seats, several real lobbies in the list at once, and a real update from 1.1.3 or 2.0.0
through the game (the offline updater harness covers the code).

## Release 3.0.0: developer tools locked to the test harness (7 Nov)

Developer mode is a setting any player can turn on, so on its own it no longer lets a copy change a
real table or show anything a table keeps hidden. One gate, `Fair.TestsAllowed` (developer mode and
the loopback test harness together), covers: the test driver's commands outside an allow-list of
read-outs, screenshots and lobby commands (a new command is gated until it is added there); the
developer panel's keys that change a table (F1 to F3, F9, F11); card values in the panel's deck
read-out and the round census (counts everywhere else); the ranked test switch; the Liar's Poker
test switches; test bot names; the Chaos card types in the mechanics trace; the test switches the
driver sets, where they are read; and the self-test that starts a match. The automatic full-table
test plays only the host's own seat outside the harness. The self-test switches are only written to
the settings file with developer mode on, and the shipped file no longer lists them.

| Check | Result |
|---|---|
| Build | `dotnet build -c Release`: 0 errors, 0 warnings |
| Loopback, a host and a client, shown, quality Low | a Liar's Poker match played to a winner with hidden hands on (awards at the end); the driver's `deadspin`, `state`, `shot` and `printdeck` ran, and `fair revolver rules` passed every case; no error from the mod on either copy |
| One real-Steam copy, developer mode and the driver on, no lobby hosted | `kill`, `endmatch`, `deadspin`, `ranked`, `bots`, `botcall`, `readyall` and `fair` were each refused with one line (`'kill' refused: it works only in the loopback test harness`); `printdeck` and `state` ran |
| Guard and watcher | four copies, every one `-OwnerDesktop -Shown`: no ON THE MAIN SCREEN, KILLED, GUARD ERROR, SEEN or UNREGISTERED line; four FOCUS lines, one or two as each copy opened |

Not proven in the game: the panel's deck read-out printing counts only at a real Liar's Deck table
(that needs a real match outside the harness; the read-out and the census use the same gate as the
refusals above), and the F-keys themselves (the kit sends no keys).

## Not yet proven

- **What the "arrow" on the table actually is.** The group named `TurnArrows` holds four
  objects 90° apart, one per shipped seat — and at four players *all four are switched on at
  once, on every turn*. Nothing drawn on the tabletop changes angle when the turn moves, in
  a four player round or an eight player one. So they are static seat markings, not a turn
  indicator: a player reads the marking nearest the person playing, and at eight players
  there is a marking in front of only every other seat, which is exactly what "the arrow is
  pointing at the person to the right" looks like. What actually shows whose turn it is has
  not been identified; `PlayerStats.SetEmissionByTurn` changes a *material* on the player,
  which no transform-and-active-state scan would ever have caught.
- **Whether turns pass by themselves when a person presses the key.** Since batch B2 a bot's
  seat is on the network and throws through the game's own throw, which ends the turn and
  passes it on by itself, and `AdvanceIfStuck` is gone: every Deck and Chaos match of that
  batch passed every turn with no help. The harness still drives the people's seats from the
  host, server-side, rather than each copy playing for itself, so a person at a keyboard
  pressing the key is still what settles it for a person's seat.
- **Velvet Room and Arena.** Neither is a table in a private lobby - the lobby's mode arrows
  reach four games (Liar's Deck, Texas, Dice, Spin) and neither of these is among them. They
  are listed in the mode enum and have no seated-player table of their own, so there is
  nothing here for the seat ring or the deal to be right or wrong about. Not tested, and not
  currently believed to be in scope.
- **The other three bars.** The venue is a host setting remembered in PlayerPrefs, and there
  are four of them with four different rooms. Every test this project has ever run happened
  in whichever one this machine last played in. The seat ring measures the table it finds
  rather than assuming one, so it ought not to care; that is reasoning, not evidence.
- **A Windows player and a Linux player at the same table.** Linux itself is proven on a Steam
  Deck (SteamOS 3.7, Proton 11 — see the CHANGELOG): same game files byte for byte, the mod's
  61 start-up lines identical to Windows, an eight-seat bot table dealt and played. What has
  not happened is a mixed lobby. Checks to make when it does: no version warning for the Linux
  player, and the same `[seatring]` seat count and `[dealarray]`/`[turn]` lines in both logs.
  Deck players must use High graphics or lower — on Ultra the table never loads (out of
  graphics memory, at four players as well as eight).
- **A real eight-person table.** Eight *connections* have been proven, all from one machine.
  Eight people on eight machines, over Steam rather than loopback, is still untested — v1.0.0
  shipped without it, and it remains the open question.

## Known structural limits

- Lobby podiums cannot be spawned through Mirror — a scene object has no build-time
  assetId. The mod uses copies with a fresh, never-spawned identity, which the networking
  layer ignores; their state is therefore filled in per machine rather than synced.
- Every player must run the same version. The build is drawn at the top of the screen in game, and every
  peer — not just the host — warns about mismatches, because mixed versions have corrupted
  whole sessions.
