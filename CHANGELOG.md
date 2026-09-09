# Changelog

Every player in a lobby must run the **same version**. The running version is shown in
the top-left corner in game, and every player is warned when someone's build differs.

**What v1.0.0 means here.** It was going to be reserved for the first release proven with
eight people in a Steam lobby. It is not that, and saying so plainly is better than quietly
moving the goalposts: eight people on eight machines has still never happened. What it does
mean is that every mode a player can choose has been played through at every table size from
one to eight, in every bar, by eight separate copies of the game holding eight real network
connections — and that the mechanics inside those modes, not merely the seating and the
dealing, were watched doing their job. Five real people on five real machines played this
build's predecessor over Steam. The eight-machine test comes next, with the people it needs.

Versions that were once numbered 1.x and 2.x were folded into the same 0.x line to make
room — `1.x.y` became `0.1x.y` and `2.x.y` became `0.2x.y`, so the order is unchanged: what
was v2.1.0 is now v0.21.0. Nothing else about those releases changed.

## v1.0.0 — two modes were broken in ways nothing had ever looked at

A player asked whether, in the Chaos deck at eight players, they would be able to choose any
of the other seven to shoot. The answer was no. Following that question found a second thing
that was worse.

### Half the table could not be shot at

When a chaos card lands the round stops and everyone picks somebody to shoot. Two pieces of
the game decide who that can be, and both were written for four chairs.

`GetAimTargetSlot` is a hand-written table of four seats by three directions: seats 0 to 3
have a row each, and anything above that falls off the end and returns nothing. `LeftAim` and
`RightAim` — the arrow keys — walk a single number between −1 and 1 and refuse to go past
either end. Three values, three targets.

So at eight players:

- **seats four to seven could not aim at anybody.** There was no row for them, the shot
  resolved against nothing, and the chaos card was spent for free;
- **seats zero to three could only ever pick each other.** The other half of the table was not
  hard to hit — it was unreachable, because no value the aim was permitted to hold referred to
  it.

Read as arithmetic rather than as a list, the shipped table is
`target = (mySlot + (2 - aim)) mod 4`, which generalises on its own:

```
target = (mySlot + (n/2 - aim)) mod n,   aim from n/2-(n-1) up to n/2-1
```

At four players that is the same three answers for the same three inputs — so this is the
shipped rule with the four taken out of it, not a new rule that happens to agree. Below five
players none of it runs at all. The aim also steps over chairs whose player is dead or gone,
which four seats could do without and seven cannot.

**The pose could not be fixed, so the mod says the name instead.** Aiming is shown by an
animation — look left, look ahead, look right — and the game never turns anyone to face a
particular chair, at four players or eight. With four players those three poses *are* the
three opponents. With eight they identify nobody, so the animator is given whichever pose
matches the third of the table the target is in, and the chosen player is named on screen
while you choose. Reachable but not choosable would have been a different bug, not a fixed one.

### Liar's Dice stopped dead on the first liar call

Above four players, resolving a liar call threw part way through the routine that shows
everyone's dice. The coroutine stopped where it threw, so no dice were shown, no loser was
chosen, nobody was given the turn, and the table sat there. The only way out was to quit.

**Liar's Dice has been unplayable above four players in every release of this mod**, and
nothing had noticed because nothing in the test rig had ever called liar in that mode — the
harness only knew how to play Liar's Deck.

The cause was the same trap as the seven deal routines: a four-player array and a four-seat
cursor built inside a coroutine. It is in a mode that deals no cards, which is why looking for
"the deal" never went near it.

Finding it needed measuring rather than reading. The decompiler gives up at that routine's
state-machine switch and emits no body at all, and this build logs exceptions without a stack,
so neither the method nor the line was available. What was available was the size at which it
broke — clean at four, broken at five, six and eight — which puts the array at exactly four
entries. Nothing reachable from outside the routine is four long. An array of four that
nothing outside can see is an array built inside, and that shape is one this mod already knows
how to rewrite.

### Why neither had been caught

Both were reachable only by playing further into a mode than any test had gone.

The aiming phase needs a chaos card to be dealt into somebody's hand and then thrown before
the round ends. Every run so far had opened that phase, let it time out, and logged
`chaos aim resolved` — which was true, and meant only that it had resolved the way it resolves
when nobody is playing. The liar call in Liar's Dice needed a harness that could play Liar's
Dice, and there wasn't one.

Three things changed so that this class of bug cannot hide again:

- **the harness plays every mode**, and takes the shot through the same methods a keypress
  reaches rather than by writing state;
- **the aim ring is measured directly** once a match, on every machine rather than only the
  host, so it does not depend on a chaos card turning up to be tested;
- **the shipped code was swept** for the six shapes a four-player assumption takes, instead of
  waiting for the next one to cost a session. What that found, and what it cannot see, is in
  `docs/PLAYER-LIMITS.md`.

### A test run that drove nobody

Developer mode — which is what makes the harness fill the seats and play them — is a setting in
the BepInEx config file, and installing a build rewrites that file with its shipped defaults. A
run with it off throws no cards, takes no turns, and finishes with a zero in every column,
which is indistinguishable from a mode that works perfectly. A whole run was collected that way
before the pattern of zeroes looked wrong. The harness sets it itself now, and refuses to
report a run in which it was not on.

The same run was also measuring about thirty-five seconds of play per cell while reporting four
minutes: it started its clock when the host pressed start, and eight copies of an HDRP game
take three and a half minutes to load the bar between them. Timing now starts when the table is
actually up.

### Also fixed

- **`Manager.GiveTurnSpinDeadSpinMoment`** — the handover after a dead spin kills somebody in
  Liar's Spin — walks a four-seat ring and had been shipping unpatched. Its `GiveTurnSpin`
  sibling was found long ago because it uses the plain `cmp` shape the scanner knew; this one
  writes the wrap as a bitmask, so the scanner walked past it. Above four seats it tries four
  chairs out of eight, and if those four are dead or gone it hands the turn to nobody.

- **Liar's Texas and Liar's Spin can be played by the harness at all**, so those modes are now
  tested past seating and dealing.

- **There is no fourth deck variant.** The lobby reports four and the arrow cycles three, so a
  test cell asking for a fourth had been quietly playing Basic a second time and filing the
  result under a table that does not exist.

### Left alone on purpose

The ceiling on a claim in Liar's Spin is a fixed number in the scene that nothing recomputes,
and the bid keys clamp against it. It looked like a four-player assumption worth raising. Read
at runtime it is **four** — not sixteen — so it was already far below an honest count of what
four players have on the table, and cannot have been derived from the seat count. Raising it
would have been changing what the mode is on a guess about a constant. It is measured, printed,
and left exactly as shipped.

### What was played this time

Every mode a player can choose from the lobby arrows, at every table size from one to eight, in
all four bars. **Sixty-five matches over eight and a half hours**, each a real table of that
many separate copies of the game with its own network connection, seating measured on *every*
machine rather than only the host's.

| Table | 8 | 7 | 6 | 5 | 4 | 3 | 2 | 1 |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| Liar's Deck — Basic | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby |
| Liar's Deck — Devil | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby |
| Chaos Deck | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby |
| Liar's Dice (both) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby |
| Liar's Texas | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby |
| Liar's Spin | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | lobby |

A table of one is a lobby: it forms, holds, reports nothing wrong, and no match begins.

Seats, measured on every machine: 44.9–45.1° apart at eight, 51.2–51.7° at seven, 59.7–60.3° at
six, 71.7–72.2° at five, with nobody more than 0.11 m from their own chair.

The aim ring was measured separately, on every machine, in every Chaos deck match: **every seat
could point at every other seat at every size from two to eight.**

### Still not true

**Eight people on eight machines has never happened.** Every table above is eight copies of the
game talking to each other on one PC. The largest real game so far is five people on five PCs.

**Two faults belong to the game and are still there.** The Chaos deck makes every client log a
handful of `NullReferenceException`s handling two of its own remote calls, and Liar's Spin logs
a few per round. Both happen at four players as well, so neither is a seat-count fault and
neither is introduced here. The mod catches the first kind so Mirror does not drop the
connection, which is what used to end the session for everybody.

**Liar's Texas throws once as a round sets up** above four players. The round plays on; the
cause is not isolated. Its `WinStats` list was four entries long at an eight-seat table and has
been grown, which did not stop it - so that list was a latent fault worth fixing on its own
merits, and not this one.

## v0.31.0 — every mode except Liar's Deck was dealing to nobody

v0.30.0 signed off Liar's Dice at six and eight and Liar's Poker at seven. The seating was
right. **Nobody was dealt a hand.** Every mode except Liar's Deck stopped on the first step of
its deal, silently, and the only thing watching a deal read Liar's Deck's own component — so
there was nothing to notice. Liar's Poker at five players did worse than fail to deal: it
dropped every connection in the game.

### What was actually played this time

Each of these is a real table of that many separate copies of the game, each with its own
Mirror connection, on the build being released. "Dealt" means every seat holding the cards it
was dealt; "turn ring" means the turn was handed round on purpose and reached every seat still
in the game.

| Table | 5 | 6 | 7 | 8 |
|---|---|---|---|---|
| **Liar's Deck — Basic** | ✅ | ✅ | ✅ | ✅ |
| **Liar's Deck — Devil** | ✅ | ✅ | ✅ · devil's deal fired | ✅ |
| **Chaos Deck** | ✅ · chaos thrown | ✅ · chaos thrown | ✅ · chaos thrown | ✅ · chaos thrown |
| **Liar's Dice** | ✅ | ✅ | ⚠️ turn ring | ⚠️ turn ring |

Seats correct on **every machine**, not just the host's, at every size; nobody more than a
centimetre from the seat their own slot maps to. Liar's Texas at eight and Liar's Poker at
five were verified separately earlier in the same day's work.

Two things that table does not say:

- **Liar's Dice's turn ring is unresolved.** At seven and eight the probe reached five of
  seven and seven of eight seats. It hands the turn on with the generic method, and Dice may
  not use it — so this is as likely to be the wrong question as a wrong answer. It is written
  down rather than explained away.
- The lobby's deck arrow has **three** variants that can be reached reliably — Basic, Devil
  and Chaos Deck. A fourth position exists in the game's cycling code; runs asking for it
  landed on one of the other three, so whatever is there is untested and unnamed.

### One deal, seven copies of it, one of them patched

The routine that physically hands cards out builds a player array sized for four seats,
indexes it by seat number, and deals one seat at a time until a cursor reaches four. Both
numbers are constants compiled into machine code, so both are rewritten in memory. That has
been true since v0.20.0 — for Liar's Deck.

There are **seven copies of that routine**, one per mode family, each with its own four:

| Routine | Mode |
|---|---|
| `DeckGamePlayManager.GiveCardsVisualRoutine` | Liar's Deck |
| `ChaosDeckGamePlayManager.GiveCardsVisualRoutine` | the Chaos deck |
| `PokerGamePlayManager.GiveCardPlayer` | Liar's Poker |
| `TexasGamePlayManager.GiveCardPlayer` | Liar's Texas |
| `ChaosGamePlayManager.GiveCardPlayer` | Liar's Chaos |
| `BlorfGamePlayManager.GiveCardPlayer` | Blorf |
| `BlorfMatchMakingGamePlayManager.GiveCardPlayer` | Blorf, matchmade |

Only the first was ever patched. The second was named as a target and refused every launch —
one warning line in a log with hundreds. The other five were never targeted at all, because
"the caps, the turn order and the seat ring are mode-independent" was taken to cover the deal.
It does not. Above four players each threw `IndexOutOfRangeException` on the coroutine's first
step, Unity swallowed it without a word, and the round went quiet with the cards recorded as
dealt and nobody holding any.

The compiler emitted the two sites three different ways across the seven, which is why
matching bytes literally found one of them:

- the array length is followed straight by the call in four copies and separated from it by
  the element type load in the other three;
- the seat cursor's compare sits after its store in some and before it in others, and Texas
  puts two unrelated instructions between them;
- the loop jump goes **forwards**. A coroutine's "go round again" is a jump on to the next
  handout's setup, not a loop back to the top — and assuming it went backwards made the
  rewritten matcher find nothing at all on its first attempt, Liar's Deck included, where it
  had been working for ten releases.

Both matchers now describe the shape rather than the byte string, each site must still be the
only one of its shape inside the method, and the scan is bounded by the padding between
methods so "the only one" means something. **All fourteen sites now patch.**

### One missing animation clip ended the whole game

Five of the seven modes play a per-seat round-reset animation as part of dealing:

```
PlayResetAnim(player.Slot)          // a ClientRpc
if (slot >= clips.Length) throw     // four clips shipped
```

Seat four throws — and because it throws inside a remote call, Mirror's answer is to drop the
connection. So a five player table of Liar's Texas did not get a missing animation: it got
*"Disconnecting connection … caused an Exception"* on all five copies inside the same tenth of
a second, and the game was over. Liar's Poker did the same.

Liar's Deck and the Chaos deck are the only two modes that never call it, which is exactly why
this survived every eight-player test ever run here — the one mode under test is one of the
two that cannot reach it.

The fix has to go on the **receiving** end. Texas and Chaos call the sending method for real,
but Poker and both Blorf tables have it inlined into their deal, so there is no call site to
patch and a fix there would silently do nothing — the same trap `ToCardTypeBasic` falls into.
Seats now share the clips that exist, dividing the ring evenly, so at eight players each of the
four serves two neighbouring seats. At four or fewer nothing is translated and the game behaves
exactly as it shipped.

### The log now names the call that broke the game, and the game survives it

Mirror's account of the above is one line that does not say which call, on which component, in
which mode: it addresses remote calls by a two-byte hash and writes the message before turning
the hash back into a name. It will hand the name over if asked, so the mod asks — and prints
`ClientRpc PlayResetAnim__Int32 on TexasGamePlayManager threw`. That one line is the whole
distance between "the mod broke Texas" and a method to go and read, and it is what found this.

**It also keeps everybody connected.** Dropping eight people to the main menu because one seat
had no animation clip is not a graceful failure, so a remote call that throws is now logged and
survived rather than fatal. This is the one place in the mod that deliberately swallows an
error; it is paired with an unmissable line naming the call, and anything found this way still
gets fixed at its source.

It immediately earned that twice. The Chaos deck makes every client throw a
`NullReferenceException` handling `ChangeRoundCardMesh` and `ShowSlotOk` — and **the same
faults appear at four players**, a table size this mod changes nothing about. That is the
game's, not the mod's; Mirror also warns at startup that this build ships colliding two-byte
remote-call hashes. Without this class those faults end the session; with it they are noise in
a log.

### The lobby name plates

At five players and up the plates hung at the height the lobby laid them out for while the
camera moved back and up to fit everybody in — so the names landed around the players' knees,
several dropped off the bottom of the screen, and two of eight could not be seen at all.

They are now lifted above the head of the podium they belong to, turned to face the camera, and
scaled from one shared baseline so a name is the same size in the back row as the front. The
shot is framed to the plates rather than to the tops of people's heads, or the outermost name
hangs over the edge of the picture.

Three attempts, and the two failures are worth recording because each looked right:

- Aiming the group's pivot at head height wrote every name across its player's chest. The name
  is a child sitting at its own offset inside the group.
- Correcting that with a world-space offset sent the names wandering off across the bar: the
  offset depends on which way the group is facing, and the group was being turned immediately
  afterwards, so each frame corrected for the way it had pointed the frame before.
- Correcting with the rect's own middle barely moved them. A text box is far wider than a short
  name, so a left-aligned "Player1" sits well off to one side of its own box.

What works is the bounds of the glyphs the text component actually drew, which it will report
if asked. Two rewrites went by on guesswork before the numbers were printed instead.

### The deck arrow picks a game, not a ruleset

`DeckMode 2` is dealt by an entirely separate manager with its own copy of everything, so
"tested Liar's Deck" left part of that menu untried — and untestable: everything that drove a
seat went through `DeckGameplay` directly, and in the Chaos deck that component is not there,
so every seat quietly did nothing. Seats are now driven through whichever component the mode
uses, and each run records the variant it actually ran on.

Two things about those variants that only turned up by playing them:

- **The Chaos deck plays itself**, throwing for anyone too slow — well inside the one to three
  seconds a test seat took to decide, so every turn was taken by the game's timer and a four
  minute round produced three deliberate plays and not one chaos card.
- **The devil card is `-1`.** The first attempt at making a seat lead with its special card
  picked the highest value in hand, on the reasoning that a deck is built by mapping numbers
  onto faces in order. That did not merely fail to help: it actively avoided the one card the
  test existed to play. Seats now lead with whatever the table has fewest of, which needs no
  guess about what the special card is.

The Chaos deck also never got a start-of-round reset, because all of that hung off Liar's
Deck's own round start. Who threw last carried over between rounds, so a seat could be called a
liar for a claim made before the cards were re-dealt.

### One mistake, made four times

Every mode's manager object is awake in the scene whatever is being played, so "is this
manager active" identifies nothing at all. That was found once, fixed in the census — and then
written again in three more places, each looking perfectly reasonable: the turn probe handed
the turn on with Texas's method during a Liar's Deck round and filled the log with exceptions,
the bots decided in half a second at tables that do not take the turn away, and the liar-call
check consulted the wrong manager. One place now decides which mode is running, from the
component bolted onto the players, and everything else asks it.

### Caught by reviewing this release before shipping it

- **The reset-animation fix sized the ring from a server-only list.** `Manager.Players` is
  empty on every client — a rule this project has written down three times and this broke
  anyway. The mapping happens on each machine, so the host divided the ring by the real
  number and every client divided it by the fallback of eight, and the same seat played a
  different animation on different screens. At four players it was worse: the host passed the
  seat through as shipped while the clients remapped it. It now reads the SyncVar every peer
  agrees on.
- **Everyone's exact cards were being written to the host's log,** on by default, in a game
  whose entire subject is not knowing what anybody else is holding. A host who opened their
  own log mid-game could read the table. The counts stay — they are what makes a bad round
  diagnosable and they give nothing away — and the values are now developer-only.
- **The seat cursor could be raised when the array had not grown.** The two writes were
  independent, so a future game update that moved one pattern and not the other would leave
  the deal walking eight seats into an array holding four — the exact failure this release
  removes, reintroduced at every table size including four. Both, or neither.
- **The turn watchdog could reach into other modes.** Its record of who has been dealt is
  cleared from the deck managers' round reset, which no other mode calls, so playing Liar's
  Deck and then switching to Poker or Texas in the same launch left it populated with the old
  seats — and it could decide a healthy round had stalled and hand somebody the turn in the
  middle of it.
- `deploy.ps1` **copied the plugin even when the build had failed**, and said "Deployed". That
  is how a version bump got tested against the build before it.

### Also

- A **mode-agnostic census** reports one line per seat in any mode: what it was dealt, the
  card values, how much of it reached the player, and whether the game thinks they are holding
  it. The old account was Liar's Deck only, which is precisely why the broken modes had nothing
  watching them.
- A **turn-ring probe** hands the turn round on purpose and writes down which seats it reached,
  so `GiveTurnTexas` and `GiveTurnSpin` can be checked without anyone at a keyboard.
- The harness reaches the deck and dice **variants** and the **bar** — there are four bars,
  chosen by a host setting remembered in PlayerPrefs, and every test this project had ever run
  happened in whichever one the machine last played in.
- Runs wait for the log line that says a copy is ready instead of a fixed forty-five seconds
  plus thirty a joiner, and for the host's roster rather than the connection — a lobby of eight
  was once photographed with seven people in it and the mod blamed for the missing plate.
- `[dealcards] all 0 hands arrived on their own`, printed directly under five lines saying five
  hands had arrived, counted only the hands that also had their flag set.
- The seat ring's list of live tables was missing Liar's Spin and Roulette and now has them. No
  claim that this fixed anything — the same list is satisfied by the deck manager, which is
  awake in every mode, so the gate was probably already open.

## v0.30.0 — eight players, and a table that stays a table

**Eight players have now sat down together and played.** Eight real connections, every one
of them a separate copy of the game with its own Mirror connection — not bots. All eight
seated, dealt from a 56 card deck in vanilla proportions (16 Aces, 16 Kings, 16 Queens,
8 Jokers), taking turns in order, with no errors from the mod on any of the eight machines.
That is the thing this project exists for and it had never been done.

Verified at **five, six, seven and eight**, on every machine in the game rather than just
the host's: gaps of 72.0°, 60.0°, 51.4° and 45.0°, everybody within a centimetre of the
seat their own slot maps to.

### The seat ring, rebuilt

Seats had been landing under the map and, after that was fixed, scattered metres across
the bar. Both were the same underlying mistake, and two rules now make it impossible
rather than merely avoided:

**Every seat stays on the table's own circle.** Empty ones included. An empty seat is only
empty until the player count this works from turns out to be wrong, and then somebody is
standing wherever it was put — four metres down, or outside the ring. There was never
anything to gain by moving one: the only thing hanging off a seat is a name plate, and
those are now switched off for seats nobody is in. (They were not, on any machine but the
host, which meant the rewrite briefly parked live name plates on the table between
players.)

**The table is measured once and never measured again.** The circle used to be re-fitted
from the live seat transforms on every pass — which is fine until one of the seats it reads
is one this code has already moved. Then each pass fits a bigger circle than the last: the
table inflated by exactly the parking multiplier per round, 1.33 m to 3.19 to 7.66 to
18.38, with the players strewn across the level and the check cheerfully reporting them
"evenly spaced".

Three more faults came out of testing each table size rather than reasoning about them:

- **The ring was sized from a number the machines did not agree on.** It took the highest
  seat index this machine could see, which is the server's roster on the host and a scene
  scan on a client. One peer divided the table by six while another used seven.
- **Clients lost a tug of war they were not aware of.** The host lays out at the round
  reset, before the game seats anybody; a client has no such hook and lays out afterwards,
  so the game put everyone straight back. The same four players were moved from the same
  wrong places on every attempt. Clients now hold the arrangement for a few seconds, which
  is long enough, and then check that it stayed.
- **The check could never pass on a client.** It judged the table by the gaps between the
  bodies it could see, and a client cannot see a bot at all — so a perfectly correct eight
  player table measured as two players 45° apart and was called wrong. It now asks a
  question that needs no agreement about population: is each player on the seat their own
  slot maps to.

### It is not only Liar's Deck any more

The seat ring was a Liar's Deck feature by accident: its trigger was a patch on the deck
manager's round reset, so Liar's Dice, Poker and the rest kept the shipped eight-seat
arrangement and bunched five, six or seven players along one side. Every mode now counts,
and both the host and the clients drive it from the same place, so the trigger no longer
depends on which game is being played. Verified in Liar's Dice at six and eight and Liar's
Poker at seven.

### Things that would have hurt a real player

- **The host was playing everyone's cards for them.** Running one bot test and then hosting
  a lobby for friends in the same session meant every friend's turn was played for them a
  second or two after it arrived — about every third of those forced moves calling them a
  liar in their own name. "The test option is switched on" stays true for a whole launch;
  it now tracks whether a test is actually driving a match.
- **Nobody but the host could see whose turn it was.** The readout walked the server's
  roster, so it was blank on every other machine — while the tabletop markings it replaced
  are hidden on all of them. Other players had no indicator at all.
- **Who is across from me** disagreed between machines for any seat above the third.
- **The online installer removed the mod before checking it could download a replacement.**
  Update with the connection down and the game still launches, looks perfectly normal, and
  is silently vanilla — then desyncs in a friend's lobby.
- **The uninstaller deleted the whole of BepInEx**, taking every other mod with it, while
  its own header promised it only removed what it had added.
- **Both deal methods were Harmony-patched *and* natively rewritten** — the one thing this
  project's notes say never to do, because a detour overwrites the bytes the scan reads.
- A release could publish a stale plugin under a new tag, and the "nothing sensitive"
  check only looked at files already tracked by git, moments before committing everything
  untracked and making the repository public.

### Also

- Screenshots go in a folder per run instead of overwriting the previous one, and the
  pruner can no longer delete anything it did not create.
- Ordinary players are no longer written a megabyte and a half of duplicate log per launch
  into a folder nothing ever tidied.
- One match produced 18,966 lines of elimination spam, because the game re-asserts the dead
  flag constantly and every write was being logged.
- The harness can pick a game mode (`LIARSBAR8P_MODE`), photograph the joining copies
  (`LIARSBAR8P_SHOTS_CLIENTS`) and hide the developer overlay for clean captures
  (`LIARSBAR8P_CLEAN_SHOTS`). It also names every copy Player1–PlayerN, the host included,
  so no Steam persona ends up in a screenshot.

## v0.29.0 — a round that can actually end

Bots could deal, sit down and take turns, but they could never *finish* a game, and three
separate faults were hiding behind that.

**Bots can call liar.** They only ever threw cards, so every round ended the one way: with
everybody's hand empty. Nobody was ever shot, nobody was ever eliminated, and a winner was
unreachable no matter how long a test was left running. They now call liar on roughly every
third move, and always when they have nothing left to throw.

Which player to challenge is tracked by the mod rather than read from the game's own
`LastBetPlayer`. That field is filled in by the announcing half of a claim, which never runs
for a player with no connection, so at a table of bots it stays empty and every call was
declined as "nothing to challenge".

**The turn stopped dead at the end of the first lap.** A bot remembered which turn it had
already played on by the seat number that was active at the time. Seat numbers come round
again: once play had gone all the way round the table and arrived back at seat 0, that bot
found it had "already played on turn 0", declined to act, and the table sat there
permanently. Turns are counted now, so the same seat a lap later is a different turn.

**A bot's throw did not move the turn on.** The game passes the turn from a scheduled step
that runs on the throwing player's own machine, and a bot has none. Clearing the turn flag
was not enough — that left the active slot still pointing at the bot that had just played,
so the watchdog handed the turn straight back to it. The turn is now actually moved on, and
only if the game has not done it itself two and a half seconds later, so a real player's
turn is untouched.

**The round started before the cards were dealt.** The turn watchdog waits for every player
to be holding cards, and this mod puts cards straight into a bot's hands itself because a
bot has no connection to be dealt over — so "everyone is holding" became true while the game
was still dealing. The watchdog started the round early, the bots played a full lap, and
then the real deal finished and handed the first turn back to seat 0, throwing that lap
away. The watchdog now stands aside while a deal is running.

### Everyone except the host was looking at the old table

Five players were reported as bunched on one side. Measuring the host's table said the
opposite — gaps of 72.0° where even is 72.0°, every player 1.33 m from the middle — and
that turned out to be the whole problem: **only the host had an evenly spaced table.**

The seats are re-spaced from `ResetRound`, which is server-side code. A client never runs
it, so nothing on a client's machine ever laid its table out: every player except the host
was still looking at the four seats the game ships with, which is exactly what "they are
all on one side" looks like from a player's seat. It could not be seen from the host's
screen, which is the screen it was being judged on.

Every machine now lays out its own table. That is safe because the answer does not depend
on the machine: the ring is fitted from seats that are identical in every copy of the game
and divided by a player count everyone agrees on, so each peer arrives at the same
positions and a client is only moving a body to where the host already put it.

That last clause was not true when it was first written, and an audit of the change caught
it. The count came from `Manager.Players` — the server's roster, empty on a client — falling
back to `StartPlayerCount`, which the mod was writing to the **SyncVar's backing field** and
therefore never sending. A client would have sized the ring for four at a table of eight and
parked the seats the extra players were sitting in four metres under the floor, taking their
nameplates and their own cameras with them. The count is now written through the syncing
property, read from it in preference to the roster, and the parking loop refuses to move a
seat anyone occupies whatever the count says. Three separate reviewers found this one.

A client also laid out only once, when the player count changed — which on a client happens
before the other players have spawned. It laid the table out for one body and left everyone
who arrived a few frames later at the shipped positions, on that screen, for the whole match.
It now re-lays out when the bodies change too, and forgets what it knew when a new match
starts.

The seat ring also reports what it actually produced now, a few seconds *after* the round
is under way rather than at the moment it places anything — where everyone was put and
where they ended up are different questions, and only the second one is worth anything.

The chair, it turns out, is part of the character rather than part of the room, so it
travels with them — there was no furniture left behind.

### Found by auditing the above

Everything in this release was then read back over, and these came out of it. Several are
faults in the fixes themselves; two would have shipped a broken release to everybody.

- **Nobody but the host could see whose turn it was.** The turn readout walked the server's
  roster, so it was permanently blank on every client — and the tabletop markings it replaced
  are switched off on *every* machine. Other players had no indicator at all. It now reads
  the synced active slot and finds players in the scene, and names the seat when it cannot
  find a player for it.
- **Aiming at a neighbour disagreed between machines.** "Who is across from me" also keyed
  off the server's roster, so a client fell through to the shipped four-seat table and
  answered seat 0 for every seat above the third, while the host answered correctly.
- **Both deal methods were Harmony-patched *and* natively rewritten.** The mod's own rule is
  never to do both to one method, because a detour overwrites the bytes the scan reads. Two
  logging patches were quietly breaking it; they are gone, and the safety net that was on the
  deal has moved to `ResetRound`, which nothing scans.
- **The turn watchdog switched itself off the moment anyone emptied their hand** — for the
  rest of the round, at exactly the point in a round when stalls are likeliest. It now tells
  "played out" apart from "not dealt yet", and stands aside during a liar resolution rather
  than handing somebody the turn in the middle of one.
- **A release could publish a stale plugin.** Nothing checked that the zip's plugin matched
  the tag. The zip on disk really did contain 0.28.0 while the tag would have been v0.29.0 —
  and the installer tells every downloader to check exactly that number on screen, so a
  correct install would have looked broken to everyone. Now verified inside the zip, and the
  release aborts naming both numbers.
- **The "nothing sensitive would be published" gate only looked at tracked files**, and the
  very next step commits untracked ones and makes the repo public. A stray decompilation
  dump or a log carrying local paths would have passed. It now checks everything that would
  actually be committed.
- **The installer crashed instead of asking.** With no Steam in the registry it died on a raw
  PowerShell error rather than reaching the "type the folder in yourself" prompt, and its
  fallback guess for the standard Steam folder expanded to `C:\Program Files(x86)\Steam` —
  no space, a path that can never exist.
- **Ordinary players were being written a log they never asked for**: about a megabyte and a
  half per launch, mirroring BepInEx's own log into a folder nothing ever tidied and the
  uninstaller does not touch. It is now only written when several copies run at once, which
  is the case it exists for, and old ones are pruned.
- The table markings could come back permanently: one transient exception latched the whole
  thing off for the rest of the process, so later matches showed the misleading chevrons
  again with nothing in the log to say why. It now retries and gives up per match, loudly.
- The red version-mismatch banner was never cleared once the lobby ended, so it stayed across
  the screen for the whole match and back at the main menu, naming somebody who had left.
- Screenshots overwrote the previous run's in place; each run now gets its own folder, and
  old ones are capped.
- A lobby that failed to arrange podiums pushed a 30-second back-off that the *next* lobby
  inherited, so it spent its first half minute doing nothing.
- Developer logging was silent on clients — the hooks are on SyncVar setters, which Mirror
  only calls on the server. Clients now poll, tagged `seen` to be honest that a poll
  edge-samples.
- Documentation corrected where it asserted things the code does not do: the version audit
  runs on every peer rather than the host, `CardTypeFix` has never fired (the thresholds are
  rewritten inside the deals instead), the turn watchdog does not fire once per round, and
  the bots no longer clear `HaveTurn` themselves.

### Also

- A liar call that never resolves now says so in the log and restarts the round, instead of
  freezing the table silently. The trigger pull runs on the losing player's own machine, and
  a bot has none, so this is a step that can genuinely stop. It also checks that the match is
  not simply *over*: it fired at the exact moment a match was won and dealt a fresh hand to a
  table with one player left, stepping on the victory screen.
- `AutoTestPlayers` sets how many sit down in an automatic test. A partly full table has to
  re-space its seats and a full one does not, so they are different cases — and testing the
  five player case by lowering `MaxPlayers` would change what is being tested.


## v0.28.0 — tested with real players at last

Every test until now used fake players made inside the host's own process. They fill seats
and play cards, but they have no network connection at all, so nothing that happens *between*
machines was ever exercised — which is exactly where a real table breaks. Twice now, "it
worked with bots" has meant nothing.

**Several real copies of the game can now be run on one machine and played against each
other.** Two facts made it possible: copies of the game will run side by side, and the build
already contains a plain TCP transport (Telepathy) beside the Steam one, so Mirror can be
pointed at 127.0.0.1 instead of Steam. Steam itself cannot do this — every copy signs into
the same account, so they cannot be distinct members of one lobby.

A five player game has now been played this way, with five genuine connections: all five
seated on their own seats, a deck of 25 dealt in the right proportions, every player holding
their cards, and the turn passing from one to the next. No errors from the mod on any of the
five.

What that immediately found and fixed:

- **The deal fallback was re-dealing to everybody, every round.** With real clients the cards
  arrive correctly but the flag saying a player is holding them lags behind. The fallback saw
  "not holding" and handed out five fresh hands over the network each round. It now tells the
  two cases apart: cards present but unflagged is just a flag to set, and only a genuinely
  empty hand is dealt again.
- **Two players with the same name were treated as the same person.** The guard against one
  player being seated twice fell back to matching on display name, so two friends who happen
  to share a Steam name would have had one of them silently dropped from the table. It now
  only falls back to names when there is no Steam id to go on at all.
- **Every copy of the game now writes its own log.** BepInEx writes one log file in the game
  folder, so a second copy could not open it and everything it had to say was lost.

Developer-only and off unless asked for: the harness is driven by environment variables, so a
normal installation is untouched.

## v0.27.0 — the turn is written down, and the table is clean

- **Whose turn it is now appears in words**, top left, under the round card and the claim.
  "BOT-7's turn", or "Your turn" in green when it is you. This is the thing the markings on
  the table were being read as, and it says it plainly instead.

- **The markings on the table are gone.** They were never a turn indicator: at four players
  all four are switched on at once on every turn, nothing drawn on the tabletop changes
  angle when the turn moves, and nothing in the game aims anything at a player. They are
  four static seat markings, so with eight seats they sit in front of every *other* seat and
  the one nearest the player up is usually their neighbour's. They are switched off and held
  off, because the game turns them back on as each round begins.

- **The lobby camera steps back rather than climbing.** It was rising four metres and looking
  down at the tops of everyone's heads - which also put each name plate, and the
  character-select panel in the same group, on top of the character's own body, so nobody
  could find where to click to change character. It now prefers stepping back, rises only as
  much as it must, and chooses the framing that leaves the fewest people hidden behind
  somebody rather than the first one that merely fits everybody in.

- **The name plates are turned to face the camera and otherwise left alone.** Moving them was
  the mistake above; the game already puts each one beside the podium it belongs to.

- **The camera move is smooth.** The blend was being stepped on the half-second sweep that
  does the podium bookkeeping, so a move lasting a second and a bit was sampled about three
  times. It runs every frame now; the bookkeeping still does not.

Four defects found by an audit of the whole mod and fixed:

- **The seat ring sank a metre every round at two or three players.** The ring is fitted from
  the first four seats, and unused seats are parked four metres down - below four players
  those two sets overlap, so each round fitted the ring partly from a seat the previous round
  had dropped. The table, and everyone at it, sank and drifted until the game was unplayable.
- **Renumbered seats never reached the other players.** Compacting seat indices wrote the
  SyncVar's backing field instead of the synced property, so the host renumbered everybody
  and told nobody: the seat a player is drawn at, dealt to and given the turn at could
  disagree between machines.
- **Clearing the table markings switched off card furniture with them.** Fixed by touching
  only the marking group's children and, for the discs beside it, only what they draw.

## v0.26.1 — developer mode stops taking photographs

- **Screenshots are off unless asked for.** Developer mode had begun taking a picture every
  five seconds, which is right for an unattended test run and wrong for anybody actually
  playing with the panel open: a few megabytes a shot and a visible pause each time. It is
  now its own setting, `ScreenshotEverySeconds` under `[Developer]`, and it defaults to zero.
  Set it to 5 when a run needs a picture record.

Nothing else changed. Developer mode itself is still off by default, so a normal install is
unaffected either way.

## v0.26.0 — every seat is dealt, and dealt a fair deck

The night's work. Two constants found by reading the compiled deal account for most of what
was wrong with a round of more than four players.

- **The deal only ever visited four seats.** It does not deal everyone in one pass: it deals
  one seat, steps a cursor on, and re-launches itself for the next — and the cursor stops at
  four. So with the player array already widened, all eight players were placed into it
  correctly and then only the first four were ever visited. Seats five to eight were never
  told the round had begun and never told they were holding cards: dealt on paper, empty in
  the hand. The deal then declared itself finished and handed out the first turn regardless,
  which is why a turn could arrive before anybody was holding anything. One constant, and it
  explains the empty corner seats, the missing hands and the early turn together.

- **More than half the deck was Jokers.** Which face a card gets is decided by three
  thresholds on its index — under 6 an Ace, under 12 a King, under 18 a Queen, otherwise a
  Joker — and those are the numbers for a twenty card deck. Doubling the deck without
  touching them made every card from nineteen up a Joker: twenty-two of forty, against two
  in twenty as shipped. Not a crash, and worse for it — the game ran and the bluffing was
  ruined. The mod had patched the method that does this arithmetic, but the compiler had
  inlined that method into both deals, so the patch had no call site and had never once run.
  Eight players now get **12 Aces, 12 Kings, 12 Queens and 4 Jokers** — exactly two vanilla
  decks, which is the composition this was always meant to have.

- **The turn no longer skips every other player.** When the safety net stepped in it advanced
  from a seat the game had already moved on, so seat 1 was followed by seat 3, then 5. It now
  distinguishes a seat that is merely waiting to be handed the turn from a table with nobody
  due, and gives rather than advances.

- **Turns no longer begin before the cards arrive.** The watchdog was watching for card
  *values* to exist, which happens well before the cards reach anyone's hands.

- **The lobby camera pulls back only when a fifth player joins**, and eases there over about
  a second rather than cutting; it returns when the table drops back to four. It also had to
  be taken off the Cinemachine brain that owns the lobby shot — the brain re-poses the camera
  every frame after everything else runs, so the raised shot was being applied and then
  overwritten before the frame was drawn.

- **Lobby name plates** hang above the player they name and turn to face the camera while the
  wider shot is held, so it is clear whose name is whose.

- **Table name plates for the added seats** follow the ring of seats. They had been placed by
  taking one seat's offset in world space and reusing it for all of them, which left all
  three at the same angle and outside the ring.

- **A player dropping mid-round** no longer strands the turn: the ring walked to find the next
  player who can act was four seats wide.

- **The seat markings on the table.** The group the game calls `TurnArrows` is not a turn
  indicator, despite the name — at four players all four are switched on at once on every
  turn, and nothing drawn on the table changes angle when the turn moves. They are markings,
  one per shipped seat. With eight players they land on every *other* seat, so the marking
  nearest whoever is playing usually belongs to their neighbour, which is what "the arrow is
  pointing at the person to the right" is. Every seat now has one, copied from the game's own
  and turned to that seat's bearing.

## v0.25.1 — the version it says it is

v0.25.0 called itself v0.24.0. **Install this over it.**

- **The version was written down twice and the two disagreed.** The number in the corner of
  the screen, the name the loader logs, and the handshake that compares builds between
  players all come from a constant in the source; the release number comes from the project
  file. Only the second was raised. So v0.25.0 shipped announcing itself as v0.24.0 — the
  wrong number on screen, and, far more serious, a handshake that would have told somebody
  on a genuine v0.24.0 that their build matched. Everyone in a lobby has to be on the same
  build, and that check is what enforces it.
- **The build now refuses to run when those two numbers disagree**, with a message saying
  which file to change. This was a shipping defect rather than a typo, so it is stopped at
  the point where it is made rather than noticed afterwards.
- A podium fix that only shows above eight: podiums past the eighth were all placed on the
  same spot as the eighth instead of continuing into a further row. Eight never reaches it,
  but the maximum is a setting that accepts more.

Everything in v0.25.0 below is in this build too.

## v0.25.0 — everyone is one player again

**If you have v0.24.0, update.** With five real players it seated every one of them twice.

- **The mod was seating people before the game had started the match.** A five player game
  showed it: the match scene loads, the lobby already holds five, and the table roster is
  still empty because `StartGame` has not been called yet. Seeing five in the lobby and
  nobody at the table, this decided the game had missed everyone and seated all five itself
  — and then `StartGame` ran and seated them properly on top. Ten characters, ten roster
  entries, eight seats, and every player standing inside another one, with the camera
  looking out of somebody else's face.

  It now waits for the game to seat people and for the table to stop changing before it
  believes a seat is empty. Two further guards sit behind that: one person can no longer
  enter the roster twice whatever else goes wrong, and a duplicate entry is dropped rather
  than moved to a seat of its own, which is what pulled the copies apart across the table.

Three more things reported from an eight player table, each with a definite cause.

- **Players in the seats this mod adds were not dealt a hand.** The safety net that hands
  out cards when the game's own animation does not asked the game a simple question — is
  this player holding cards? — and the game answered yes for all eight while the added
  seats' hands were empty. That false yes is why the net never caught them. It now counts
  the card objects actually switched on in a hand and compares that with the number of cards
  dealt, so an empty hand is dealt regardless of what the flag claims. It still does nothing
  when the hands arrive by themselves.

- **The arrow on the table pointed one seat past the player whose turn it was.** The table
  carries four arrows, standing at its centre and turned to face the four seats the game
  shipped with — ninety degrees apart. Eight players sit forty-five degrees apart, so those
  four arrows land on every *other* seat: showing arrow number three for the player in seat
  three points at seat six. Nothing in the game aims an arrow at anybody, so one is now aimed
  straight at the seat in play, and the rest are kept switched off so only one ever shows.
  At four players the shipped arrows already line up and are left alone.

- **Players five to eight in the lobby stood off the side of the screen.** The four shipped
  podiums are a row, and the extras were continuing it sideways past both ends — twice the
  width the lobby camera frames. The row is now doubled instead of lengthened: each extra
  podium stands behind the shipped one in the same place along the row, half a space to the
  side so a character at the back is seen in the gap between two at the front rather than
  hidden behind one. The camera lifts and tilts down until nobody at the back is standing
  behind somebody at the front, and is left exactly as the game had it if no amount of
  lifting achieves that.

## v0.24.0 — everyone is actually dealt a hand

The deal happens in two halves. The first works: every player is given five card values,
and that half is plain server code. The second — the animation that puts the card objects
into people's hands, and then gives out the first turn — is a coroutine, and above four
players it does not run to completion. The table ends up dealt on paper and empty in the
hands, with nobody able to act and nothing in any log to say why.

- **The cards are now handed over directly when that animation does not deliver them.**
  Each waiting player has their hand applied and shown, and the game's own message is sent
  afterwards so a player on another computer receives theirs the usual way. It waits three
  seconds first and does nothing at all if the hands arrive on their own, so a table where
  the game manages by itself is untouched.
- Sending the message alone is not enough on the machine it is sent from — the hand has to
  be applied there as well. That distinction cost a test run to find and is the difference
  between a full hand and an empty one.

Verified at eight players with none of the test tooling driving it: a real player is dealt
a visible hand where before they got nothing.

## v0.23.3 — the diagnostic was breaking the deal

- **A watcher added to find out why cards were not handed out was itself stopping them.**
  Harmony could not patch the coroutine it was watching, and a half-applied patch on a
  `MoveNext` leaves the coroutine unusable — so the routine that hands out the cards and
  gives the first turn stopped running. The diagnostic was causing the fault it was meant
  to explain. It found the four-element player array first, which is a real fix and stays.
- What replaces it patches only the ordinary methods that *create* those coroutines, never
  their `MoveNext`, which is safe and answers the same question.
- Bots are shown their hand rather than only recorded as holding one: applying the card
  state records the cards, showing the hand is a separate step, and it is the one that puts
  the meshes in a player's hands.

## v0.23.2 — bots hold cards and carry a loaded gun

- Everything a player physically receives arrives over their own connection: the card
  objects, the flag saying they are holding cards, the revolver being loaded. A bot has no
  connection, so none of it reached them — dealt a hand that existed only as numbers, sat
  there empty handed with no gun, and the round waited for everyone to be holding cards
  before giving out the first turn. The host now runs the receiving end on their behalf,
  calling the same methods a real client runs on the message rather than sending one.
  All eight now hold five cards and a loaded revolver.

## v0.23.1 — the cards reach every seat

- **The deal built a four-element array and indexed it by seat number.** The routine that
  physically hands the cards out starts with `new PlayerStats[4]` and then does
  `array[player.Slot] = player`, so a player in seat four or beyond was off the end of it.
  It threw on the routine's first step — and because that routine is a coroutine, the
  exception was swallowed silently. No card objects were handed out, the first turn was
  never given, and nothing in any log said why: every card was recorded as dealt while the
  deck sat untouched on the table. The four is an immediate operand handed to the array
  allocator, so it is rewritten in memory like the deck size and the turn wrap.
- Bots can no longer be added once a match has started. One added mid-match has no podium,
  so the game's own seating throws for it, and the resulting errors read like a dealing
  fault when they were nothing of the kind.
- Developer mode reports how far the deal gets, because a coroutine that dies part way
  through is otherwise completely silent. That is what found the above.

Still outstanding: a bot receives no card objects and does not load its revolver. Both
arrive over a player's own connection and a bot has not got one — a limit of the test
harness, not of eight players. A real player in seat five is dealt normally.

## v0.23.0 — eight players actually play

The core loop, end to end at eight: everyone registers, everyone is seated, everyone is
dealt, and the turn goes all the way around the table and wraps back to the first seat.

- **The fifth player could not be seated, and that was one missing component.** When a
  match starts, the game does not number a player by the order it seats them — it reads
  the number off the seat itself: `Slots[n].GetComponent<Slot>().SlotID`. The seats this
  mod adds were bare markers with no such component, so that returned null and the whole
  seating sweep threw a NullReferenceException on the first added seat. It stopped after
  four because four is exactly how many seats the game shipped with — everyone after that
  was left in the lobby roster, never at the table. Added seats now carry a real `Slot`
  numbered to match their position, with their own seat camera copied across so a person
  sitting there sees the table from their own chair.
- **Seat numbers are checked rather than assumed.** Every seat must carry its own list
  position and no two may share one; a duplicate puts two players on one number and one of
  them never gets a turn. Anything wrong is reported, not silently patched over.
- **The first turn was never handed out**, so a fully dealt table simply sat there. The
  game gives it from inside the deal's animation, behind a filter that does not always
  produce anybody. A round that is dealt, holding cards and has nobody able to act is now
  recognised as the stall it is and moved on through the game's own `GiveTurn`.
- With eight at the table the deal is right: forty cards, forty card objects, five each.

Developer mode gained enough to prove all of the above by itself — bots take a turn,
throw one card and hand the turn on, and the host's own seat is played too while an
automatic test is running, so a whole round runs unattended.

Lobby podium placement was also corrected: the shipped podiums are a row, not a ring, and
extending them as a ring put the extra four *behind* the original four. Wider table
layout, seat spacing and camera framing are deliberately still outstanding — playable
first.

## v0.22.0 — a way to test eight players without eight people

Everything here is off unless `DeveloperMode` is switched on, and none of it changes an
ordinary game. It exists because every remaining fault needed eight people in a room to
see, and that is not a test anybody can run twice.

- **Bots that fill the empty seats.** A bot is the game's own player object, spawned
  through the networking layer, registered in the same roster, given a lobby podium by the
  same method and dealt to by the same deal — not a simulation. Eight players now assemble
  in a lobby on demand, and the four extra podiums added in v0.21.0 have been seen in use
  for the first time. On its turn a bot waits a second or three and throws one card, which
  is always a legal move; it is not meant to play well.
- **A debug panel on F8**, with the same commands on function keys: add or remove a bot,
  fill the table, print the player list, the seat assignments, the lobby podiums, the deck
  state or whose turn it is, skip a turn, start the match. It also shows a live count of
  players, seats, the active slot and the deck size.
- **A full account in the log** of registration, lobby creation, seat assignment, dealing,
  turn changes, eliminations, round boundaries and deck size — each tagged so one concern
  can be read back on its own. It calls out the things that have gone wrong before:
  a player dealt nothing, two players on one seat, the turn indicator disagreeing with
  whose turn it is.
- **`AutoTestFullTable`** runs the whole thing by itself: host, fill, start, report. One
  launch, one round, no keyboard.

What it found in its first hour, all of it previously invisible:

- **`Manager.StartGame` throws part way through seating.** It seats about four players and
  then stops, so at eight only five reach the table and the rest are simply lost — in the
  lobby, in the roster, never at the table. This is the largest remaining limit, and it is
  now visible in one line rather than costing a session to notice.
- The game's own `SpawnPlayerwithskin` seats a player without adding them to the roster,
  so seating anyone outside that sweep does not count them.
- `OrderSprtes`, a per-player list, ships with **three** entries and was only grown once a
  round was already being set up — after seating had needed it. It is now grown first.
- A player with no connection is never told they are holding cards, and the round waits
  for everyone to be holding cards before the first turn.

## v0.21.2 — the installer is reusable, and says so

- The installer always asked GitHub for the newest release, so re-running the same file
  has always been enough to update - but nothing said so, and it told people to check the
  bottom left for their version, which is no longer where it is. It now says both.

## v0.21.1 — the version moves to the top left

- The version was drawn along the bottom of the screen, on the same line as the game's
  own version string. Two of them overlapped into one unreadable smear, which defeats
  the point of showing it at all. It now sits in the top left, on a dark panel so it
  stays legible over a bright menu as well as a dark bar.

## v0.21.0 — the sixth player, and turn order that skipped seats

Everything here came from mapping the game's compiled code for hard-coded fours before
changing anything. That map is now `docs/PLAYER-LIMITS.md`.

- **The sixth player was refused by the Steam transport, underneath the game.** There is a
  third connection limit nobody had found: the Steam transport's own server checks its
  connection count before the game or its networking layer ever see the attempt, and
  rejects with *"would exceed max connection count"*. The host does not connect to itself
  over Steam, so that count holds only the other players — a limit of four meant four
  guests plus the host, exactly the five that worked, and the sixth bounced with nothing
  in anyone's log. Raised wherever that server is built.
- **Turn order silently skipped seats five to eight.** Advancing a turn searches for the
  next living player and, on finding an empty seat, checks `seat > 3` before wrapping to
  seat zero. A full table happened to work; the moment anyone in the first four seats died
  — which is most of a round in this game — the search stepped past seat 3 and jumped
  straight back to the start, never visiting the later seats. That is the reported "it
  makes players play when the arrow isn't pointing at them". The same hard-coded seat
  three governed going backwards. All five places are now rewritten in memory, the same
  way the deck size is.
- **Who is on your left, right and across was a fixed four-by-four table.** Anything
  outside the first four seats fell through to "seat zero", so every extra player thought
  seat zero lay in all three directions. It is now worked out from the number of players
  present, and at four players produces exactly the table the game shipped with.
- **Players beyond the fourth now appear in the lobby.** The lobby has four podiums and
  the host picks a free one for each arriving player; the fifth threw, and because that
  same code is what places their body, they had no podium and no avatar until the match
  started. Extra podiums are now built from the existing ones — with a fresh, never-spawned
  identity, so the networking layer ignores them entirely — placed by continuing the arc
  the originals stand on, and each is given its own name plate rather than sharing one.
- **A seat given to an extra player never reached anybody else.** The fallback that seats
  a player the game could not place was writing the plain field instead of the synced one,
  so only the host knew about it. Every other player kept seeing them in seat zero.
- **Removed a deck patch that was attached to the wrong method.** It was scaling the list
  of cards handed to `AddCards`, which is not the deal — it is a player *playing* cards.
  Above four players it would have replaced a two-card play with a whole deck's worth. The
  deal itself has been handled properly since v0.20.0.
- One configurable maximum. Everything that raises a cap now reads a single value, and the
  count the game shipped with is kept separate from it, since scaling the deck in
  proportion needs the original and must not follow the setting.
- The plugin no longer waits a fixed number of frames before its optional self-test; it
  waits for Steam, which was not ready and made the test fail every time.

## v0.20.2

- **The plugin file itself carried the build machine's folder path.** The compiler records
  where an assembly was built and stamps that into the file, so every download contained
  an absolute path from the machine that produced it. The build no longer emits a symbol
  file or records source paths, and packaging now refuses to run if any file about to be
  shipped still contains the build account name.
- The old settings file is matched by its ending rather than its full name, so a copy left
  behind by an earlier version is cleared on install even though nothing names it.
- Every release page now describes what actually changed in it. They had all carried the
  same text, and that text still claimed seats and dealing were untested.

## v0.20.1

- Removed personal identifiers from the project. The plugin id changed from a name-based
  one to `liarsbar.eightplayers`, so the settings file is now
  `BepInEx/config/liarsbar.eightplayers.cfg`. The installer wipes the old file, so nothing
  needs doing by hand.
- Added this changelog. Release notes now come from it rather than being written twice.

## v0.20.0 — the deck was hardcoded

The real cause of cards never working above four players.

- **The deal's deck size is a constant compiled into the game.** Decompiling showed
  `DealBasicOrDevil` builds `Enumerable.Range(1, 20)` and `DealDeck2` builds
  `Range(1, 28)`. Adding card objects never changed it, so every round produced twenty
  cards no matter how many people sat down: four full hands, nothing for the fifth player,
  and an index past the end. That single constant explains the crash, the missing cards,
  and why the deck never appeared to double.
- The constant is now rewritten in memory, scaled by the cards-per-player the game itself
  uses. Five players get 25 and 35; eight get 40 and 56.
- **Card faces are computed, not looked up.** `n<=6` Ace, `n<=12` King, `n<=18` Queen, else
  Joker. Nothing threw on a bigger deck — everything past the last threshold simply became
  a Joker, so a 25 card deck would have dealt six Aces, six Kings, six Queens and *seven*
  Jokers. Thresholds now scale with the deck, giving exactly 12/12/12/4 at eight players:
  two vanilla decks.

## v0.15.1 — installers always start clean

- Both installers delete this mod's plugin and settings before installing, including any
  renamed or duplicated copies. Keeping settings across an update had let a stale option
  silently disable a fix, and a leftover plugin had put players on different builds in the
  same lobby. Both cost whole test sessions.
- BepInEx's generated `interop` folder is deliberately left alone; rebuilding it is slow
  and it does not belong to this mod.

## v0.15.0 — fixes to the mod's own patching

- **Patches were cancelling each other out.** Two of this mod's handlers sat on the same
  game method with no ordering between them. Once one absorbed the error the next saw
  nothing, so the code that seats an extra player may never have run at all. The handler
  that acts now runs first and passes the error on, so the one that reports it still does.
- **Trimming seats was a one-way door.** Spare seats were removed so turn order could not
  land on an empty chair, but they were discarded — if someone joined and the table grew,
  there was no seat for them. Trimmed seats and nameplates are now kept and restored.

## v0.14.1 — simpler card index wrapping

- Removed a "learn the deck size" branch that the following bounds check already covered.
  It only added state that could mislead. An ordinary four player round is unaffected.

## v0.14.0 — lists match the players present

- **Seats and per-player lists were expanded to the maximum and left there**, so anything
  walking them visited empty chairs. The turn indicator pointed at nobody while a real
  player acted, and dealing that walks seats skipped people. Both symptoms were one cause,
  not two. Everything is now sized to the players actually present, and seat indices are
  compacted so no index can exceed the roster.
- Seats this mod added are deactivated rather than destroyed, and seats the game shipped
  with are never removed, so a later round can grow again.
- The card index is wrapped against the vanilla deck so a second deck is an exact copy of
  the first, keeping 6/6/6/2 rather than inventing a distribution. (Superseded by v0.20.0,
  which found the deck size itself was fixed in the game's own code.)

## v0.12.0 — best-effort fixes for dealing above four

- `StartPlayerCount` is corrected before the round is set up. It lagged at four while five
  players were seated, so anything looping over it dealt to four people and left the fifth
  with nothing — exactly the reported symptom.
- Seat indices are compacted into a contiguous range, so a player cannot hold a seat index
  beyond the number of players present and send something indexing off the end.

## v0.11.1 — players move with their seats

- Re-spacing the ring moved the seats, but bodies were placed when they spawned and stayed
  put. The turn arrow follows the seat, so it pointed at empty space while the player acted
  from where they had originally spawned. Players are now moved onto their seats, on the
  host only, so a client cannot fight the position sync.
- The release script had created a version as a draft, which is invisible to the "latest"
  link the online installer fetches — anyone installing "the latest version" silently got
  the previous one, and the script reported success anyway. It now publishes drafts and
  fails outright if the latest release is not the one it just built.

## v0.11.0 — the ring fits the players present

- With five players in an eight seat ring, the occupied seats covered only half the table:
  players bunched on one side, gaps opposite, and the turn indicator pointing into a gap.
  Seats are now spaced evenly for the number of players actually present, and unused seats
  are parked out of the way.
- A player the game could not register now receives the lowest unclaimed seat instead of
  remaining a ghost with no body, no cards and an arrow pointing at their empty chair.

## v0.10.0 — version visibility, and a doubled deck

- The running version is drawn in the top-left corner, and the host names anyone whose
  build differs. Three different builds had been in one lobby at once, and that mismatch
  corrupts shared state for everybody.
- Above four players the deck is doubled outright rather than sized per player, keeping the
  vanilla ratios exactly.
- Seats are laid out in index order around the table. New seats had been appended to the
  end of the list but placed physically between the original four, so list order and
  seating order disagreed and the indicator pointed at one seat while someone elsewhere
  acted.

## v0.9.0 — mismatched builds report themselves

- Each client publishes its version through Steam's lobby data and the host checks every
  member, naming anyone who does not match. Steam data rather than an in-game message on
  purpose: it works between mismatched builds, which is exactly when it is needed. Two
  sessions had been spent chasing symptoms that turned out to be one player who had not
  reinstalled.
- Three more per-player collections are grown so the deal can complete, and every remaining
  collection is logged at round setup so a further surprise names itself in one round.

## v0.8.0 — dealing to five, and skipped turns

- More than one collection is involved in a deal; only one was being topped up. The card
  pools and the per-seat list are now all grown.
- Turn order skipped the extra player because the roster count stayed at four. It is now
  corrected after the round starts, even though the game's own start method throws.

## v0.7.0 — the deck is topped up

- With five players seated the deal failed outright: five players need 25 cards from a deck
  built for 20. Spare card objects the game already owns are used first, and cards are only
  duplicated after confirming at runtime that they carry no networking component.

## v0.6.0 — in-game seats instead of lobby panels

- Lobby panels are networked scene objects and **cannot** be added by a mod. Three
  approaches were tried and the first cost several test sessions; the mod now reports the
  limit once instead of attempting it.
- The in-game seat ring is plain markers, so that *is* extended. New seats are placed on
  the circle fitted from the seats the game shipped with, so the original four never move.
  Nameplates are extended too.

## v0.5.0 — a stale setting was disabling the fix

- The previous version had shipped the lobby expansion switched off, and the installer
  preserved existing settings, so that "off" survived the update and the fix silently never
  ran. The option was removed entirely rather than defaulted on.
- The plugin reports its real version instead of a hardcoded one, which had made it
  impossible to tell from a log which build someone was running.

## v0.4.0 — the fifth player can be seated

- Four lobby podiums is a hard ceiling of four players: the game picks a free one from a
  list and fails when none are left. Expanding that list turned out to be required, not
  cosmetic as v0.3.0 had assumed. (v0.6.0 later established this cannot be done safely at
  all, and took a different route.)

## v0.3.0 — clients no longer disconnect

- The mod had been duplicating networked scene objects to add lobby podiums. That corrupted
  spawn handling and disconnected everyone in the lobby. An error inside the game's own
  code, blamed earlier on the game, was a downstream symptom of this.

## v0.2.0 — the fifth player can join

- **The networking layer keeps two connection limits.** Only the visible one was being
  raised; the one actually checked when a client connects is installed separately, and the
  game always set it to four. Steam admitted a fifth player and they were dropped straight
  back to the menu.
- Fixed the lobby seat arc: angles were sorted by a raw value that wraps at 180 degrees, so
  a ring straddling that boundary produced a 113 degree step instead of 22 and scattered
  the podiums, stacking some on top of each other.

## v0.1.0 — first release

- Raises the Steam lobby limit and the connection cap from 4 to 8.
- One-click installer that finds the game through Steam automatically, plus a matching
  uninstaller.
