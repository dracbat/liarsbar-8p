using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Plays a seat in the modes that are not Liar's Deck.
///
/// <c>BotBehaviour</c> knows one game. It waits for a seat's turn, throws a card or calls
/// liar, and everything it touches is <c>DeckGameplay</c> - so in Liar's Dice, Texas, Poker or
/// Spin every seat sat there doing nothing, and a test of those modes could only ever show
/// that people were seated and dealt. That is not the same as the mode working, and saying it
/// was is how "Liar's Dice at six and eight" got signed off on a build where nobody in Liar's
/// Dice had been dealt at all.
///
/// Each mode has its own idea of a legal move, so each gets its own small player here, driven
/// through the same entry points a person's keypress reaches - <c>PlaceBet</c>, <c>CallLier</c>
/// and so on - rather than by writing state directly. A move made any other way proves nothing
/// about whether a person could have made it.
///
/// This is a test instrument. It only runs with developer mode on, only on the server, and
/// only while the harness is driving every seat; in an ordinary game it does nothing at all.
/// It never plays a bot's seat, which is <see cref="BotBrain"/>'s - except under the harness's
/// own switch (<see cref="BotManager.TestBotsAnyTable"/>), at Liar's Poker and Liar's Slots,
/// where bots have no player of their own, so one copy can test those tables at eight seats.
/// </summary>
internal static class ModePlay
{
    /// <summary>How long a seat appears to think. Short: these modes have their own timers.</summary>
    private const float MinThink = 0.8f;
    private const float MaxThink = 2.2f;

    /// <summary>Under this many seconds on the turn's clock a seat held back for its cup moves anyway.</summary>
    private const float ClockNearlyOut = 3f;

    private static readonly Dictionary<int, float> _actAt = new();
    private static readonly Dictionary<int, int> _actedOn = new();
    private static int _turnNo;
    private static int _lastActive = -1;
    private static int _moves;
    private static bool _saidSettle;

    internal static void RoundStarting()
    {
        _actAt.Clear();
        _actedOn.Clear();
        _moves = 0;
    }

    /// <summary>
    /// The test driver's "seatplay off": the harness's seats are left to their clocks, as in a bare
    /// copy, which has none of this, so the two hold the same table when their memory is compared.
    /// </summary>
    internal static bool OffForTest { get; set; }

    /// <summary>
    /// The test driver's "seatplay leave N": the seat of connection N is left to its own copy, so a
    /// move its own game makes (the driver's "texasmove" on that copy, say) is the one the host
    /// receives, over the wire, instead of the harness moving the seat first from the host.
    /// "seatplay on" hands every seat back.
    /// </summary>
    private static readonly HashSet<int> _leftToOwnCopy = new();

    internal static void LeaveToOwnCopy(int connection) => _leftToOwnCopy.Add(connection);

    internal static void PlayEverySeat()
    {
        OffForTest = false;
        _leftToOwnCopy.Clear();
    }

    internal static string LeftToOwnCopy => _leftToOwnCopy.Count == 0 ? "none" : string.Join(", ", _leftToOwnCopy);

    private static bool LeftAlone(PlayerStats p)
    {
        if (_leftToOwnCopy.Count == 0) return false;
        try
        {
            var conn = p.connectionToClient;
            return conn != null && _leftToOwnCopy.Contains(conn.connectionId);
        }
        catch { return false; }
    }

    internal static void Tick()
    {
        if (!Dev.Enabled || !Dev.IsServer || OffForTest) return;

        // Only when the harness is standing in for everybody. A real player's turn is theirs.
        if (!(DevAutoTest.Driving || Loopback.Mine == Loopback.Role.Host)) return;

        var m = Dev.Mgr;
        if (m == null || m.Players == null || !m.GameStarted) return;

        var kind = TableHand.Playing();
        var player = For(kind);
        if (player == null) return;                 // Liar's Deck and Chaos Deck: BotBehaviour's job

        // A Texas swap spans several ticks: the menu held open, the choice, the animation.
        if (kind == TableHand.Kind.Texas) _texas.Continue();

        // Count turns the same way BotBehaviour does, so "already acted this turn" survives the
        // active seat coming round again a lap later.
        int active = m.ActivePlayerSlot;
        if (active != _lastActive) { _lastActive = active; _turnNo++; }

        // Under the harness's own switch, bots sit at the two tables BotBrain has no player for,
        // and their seats are played from here; BotBrain then only passes a turn left to run out.
        bool botsHere = BotManager.TestBotsAnyTable && (kind == TableHand.Kind.Texas || kind == TableHand.Kind.Spin);

        // Nobody takes a turn while a call is settled, but a step of the settling that only the
        // seat's own copy takes is still taken here for a bot, which has no copy: Liar's Slots'
        // dead spin waits for the loser's copy, and waited for good when the loser was a bot.
        if (botsHere)
        {
            try { player.Settle(m); }
            catch (Exception e)
            {
                if (!_saidSettle) Dev.Warn("play", $"a bot's step of the settling in {kind} failed: {e.Message}");
                _saidSettle = true;
            }
        }

        if (player.Resolving()) { _actAt.Clear(); return; }

        foreach (var p in m.Players)
        {
            // A bot's seat is BotBrain's: played from here as well, it would move twice.
            if (p != null && BotManager.IsBot(p) && !botsHere) continue;
            // Outside the loopback test harness, the host's own seat and nobody else's.
            if (p != null && !Fair.TestsAllowed && !p.isLocalPlayer) continue;
            if (p == null || p.Dead || !p.HaveTurn) { if (p != null) _actAt.Remove(p.Slot); continue; }
            // Left to its own copy by the driver, or waiting for a fair-play test the host is about to
            // send for it (FairTexas's "fair texas wire"), which needs the seat's turn untouched.
            if (LeftAlone(p) || FairTexas.WireArmedFor(p)) { _actAt.Remove(p.Slot); continue; }
            // Not this seat's moment yet (a Liar's Slots seat before its reveal, a Liar's Dice seat
            // whose cup is still being shaken): asked again next tick.
            if (!player.Ready(p)) { _actAt.Remove(p.Slot); continue; }
            if (_actedOn.TryGetValue(p.Slot, out int on) && on == _turnNo) continue;

            if (!_actAt.TryGetValue(p.Slot, out float due))
            {
                due = Time.time + MinThink + ((p.Slot * 7 % 5) / 4f) * (MaxThink - MinThink);
                _actAt[p.Slot] = due;
                continue;
            }
            if (Time.time < due) continue;

            _actAt.Remove(p.Slot);
            _actedOn[p.Slot] = _turnNo;
            _moves++;

            try { player.Act(p, _moves); }
            catch (Exception e) { Dev.Warn("play", $"seat {p.Slot} could not act in {kind}: {e.Message}"); }
        }
    }

    private static IModePlayer For(TableHand.Kind kind)
    {
        switch (kind)
        {
            case TableHand.Kind.Dice:  return _dice;
            case TableHand.Kind.Texas: return _texas;
            case TableHand.Kind.Spin:  return _spin;
            default: return null;
        }
    }

    private static readonly DicePlayer _dice = new();
    private static readonly TexasPlayer _texas = new();
    private static readonly SpinPlayer _spin = new();

    // --------------------------------------------------------------------------- the modes

    private interface IModePlayer
    {
        /// <summary>True while the round is settling something and nobody should act.</summary>
        bool Resolving();

        /// <summary>Whether this seat, holding the turn, may move now; false asks again on the next tick.</summary>
        bool Ready(PlayerStats p);

        void Act(PlayerStats p, int moveNo);

        /// <summary>
        /// Every tick, before <see cref="Resolving"/> is asked, and only under the harness's own bot
        /// switch: a step of the settling that only a seat's own copy takes, taken here for a bot's
        /// seat, which has no copy to take it.
        /// </summary>
        void Settle(Manager m);
    }

    /// <summary>
    /// Liar's Dice: raise the bid, or challenge it.
    ///
    /// The table's standing bid is "<c>LastCount</c> dice showing <c>LastDice</c>". A legal
    /// raise is more dice, or the same number of a higher face - so a seat can always raise
    /// until the count runs past the dice that exist, which is what makes calling necessary
    /// rather than optional.
    /// </summary>
    private sealed class DicePlayer : IModePlayer
    {
        /// <summary>
        /// How many dice the first bid of a round names: one, unless <c>LIARSBAR8P_DICE_OPEN</c>
        /// says otherwise.
        ///
        /// Steady raises from one never get anywhere near twenty, and twenty is where the game's
        /// spoken bids run out - a bid past it is what disconnected a player in a real game of
        /// six. Opening high is how a harness run reaches those bids on purpose.
        /// </summary>
        private static readonly int OpenCount = ReadOpenCount();

        private static int ReadOpenCount()
        {
            try
            {
                if (!Fair.TestsAllowed) return 1;
                string raw = Environment.GetEnvironmentVariable("LIARSBAR8P_DICE_OPEN");
                if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int n) && n >= 1 && n <= 40)
                {
                    Plugin.Log.LogWarning($"[play] Liar's Dice rounds open at {n} dice for this run");
                    return n;
                }
            }
            catch { }
            return 1;
        }

        private static DiceGamePlayManager Mgr
        {
            get { try { return Manager.Instance != null ? Manager.Instance.DiceGame : null; } catch { return null; } }
        }

        public bool Resolving()
        {
            var d = Mgr;
            if (d == null) return false;
            try { return d.CalledLiar || d.CalledSpotOn || d.DrinkPhaseActive; } catch { return false; }
        }

        /// <summary>
        /// Not while the seat's own cup is still being shaken. A person's keys send no bid or call
        /// then: the game's input waits for <c>isShakeMoment</c>, which the shake's own animation
        /// raises at its start and lowers at its end, just after it shows the dice again (it hides
        /// them while the cup moves). A move made sooner from here cut the shake short on every
        /// screen before that, the dice stayed hidden all round, and at the reveal the seat's dice
        /// were missing and its label threw as it opened. Asked of the host's copy of the seat;
        /// with the clock nearly out the seat moves anyway, rather than let its turn run out.
        /// </summary>
        public bool Ready(PlayerStats p)
        {
            var gp = p.GetComponent<DiceGamePlay>();
            bool shaking = false;
            try { shaking = gp != null && gp.isShakeMoment; } catch { }
            if (!shaking) return true;

            float clock = 0f;
            try { clock = Manager.Instance.CountDown; } catch { }
            return clock > 0f && clock < ClockNearlyOut;
        }

        public void Settle(Manager m) { }

        public void Act(PlayerStats p, int moveNo)
        {
            var d = Mgr;
            var gp = p.GetComponent<DiceGamePlay>();
            if (d == null || gp == null) return;

            // The dice have to have been rolled before there is anything to bid about.
            try { if (gp.diceValues == null || gp.diceValues.Count == 0) return; } catch { return; }

            bool anyBid = false;
            int count = 0, face = 0, total = 0;

            // MaxCount, not TotalCount. MaxCount is every die on the table - the game adds
            // five to it for each player as they are seated - and is what makes a bid
            // impossible to satisfy. TotalCount is how many of one face were showing when a
            // round was last revealed, and at the start of a round it is small or zero: read
            // as a ceiling it made the second bid of every round look absurd, so at eight
            // players the table called liar three moves in, every round, and only four of the
            // eight seats ever got a turn.
            try { anyBid = d.BetPlaced; count = d.LastCount; face = d.LastDice; total = d.MaxCount; } catch { }

            if (!anyBid || count <= 0)
            {
                int openFace = 2 + (p.Slot % 4);
                Dev.Log("play", $"seat {p.Slot} opens the bidding: {OpenCount} x {openFace}");
                Bid(p, gp, OpenCount, openFace);
                return;
            }

            // Challenging is the only move that ends a round, so it has to happen - but not so
            // often that the bidding never gets going. Every fourth move, and always once the
            // bid has run past the dice that physically exist on the table.
            bool absurd = total > 0 && count > total;
            if (absurd || (moveNo % 4) == 0)
            {
                bool spotOn = false;
                try { spotOn = d.CanSpotOn && (moveNo % 8) == 0; } catch { }

                if (spotOn)
                {
                    Dev.Log("play", $"seat {p.Slot} calls SPOT ON on {count} x {face}");
                    gp.UserCode_PlaySpotOnCMD();
                    gp.UserCode_CallSpotOn();
                }
                else
                {
                    Dev.Log("play", $"seat {p.Slot} calls LIAR on {count} x {face}" +
                                    (absurd ? " - the bid is past the dice on the table" : ""));
                    gp.UserCode_PlayLiarCMD();
                    gp.UserCode_CallLier();
                }

                // Deliberately no handover. A challenge ends the round, and the reveal
                // coroutines give the turn out again themselves; reaching in here would be
                // reaching into the middle of that.
                return;
            }

            int nextCount = count, nextFace = face + 1;
            if (nextFace > 6) { nextFace = 2; nextCount = count + 1; }

            Dev.Log("play", $"seat {p.Slot} raises to {nextCount} x {nextFace}");
            Bid(p, gp, nextCount, nextFace);
        }

        /// <summary>
        /// A bid, the way the game makes one - all three steps of it.
        ///
        /// This is where the first attempt at Liar's Dice was wrong twice over, and both
        /// mistakes were silent.
        ///
        /// <c>PlaceBet</c>, <c>CallLier</c> and <c>CallSpotOn</c> are raw Mirror
        /// <c>[Command]</c>s whose entire body is SendCommandInternal. Called on the host for a
        /// seat the host does not own, they fail Mirror's authority check and do nothing at
        /// all - no error, no bid, a table that simply never moves. The server-side body a
        /// Command dispatches into is <c>UserCode_*</c>, and that is literally the code a
        /// person's keypress runs. Liar's Deck hid this: it has <c>Request*</c> wrappers that
        /// call the server side directly, and Dice has none.
        ///
        /// And a bid is not one call. The game's own input path is bet, then clear my turn,
        /// then hand the turn on - and nothing in the dice manager advances the turn after a
        /// bet, the acting client does. Bid alone and the table deadlocks on the first seat.
        /// </summary>
        private static void Bid(PlayerStats p, DiceGamePlay gp, int count, int face)
        {
            gp.UserCode_PlaceBet__Int32__Int32(count, face);
            gp.UserCode_ResetTurn();

            var cc = p.GetComponent<CharController>();
            if (cc != null) cc.UserCode_GiveTurn();
            else Manager.Instance.GiveTurn();
        }
    }

    /// <summary>
    /// Liar's Texas: swap a card, put a bullet in, or get out.
    ///
    /// A betting round rather than a bidding one. A seat either matches what is on the table -
    /// which the game calls rising, and which costs a bullet - or folds, which is free and
    /// takes a turn of the revolver. Going all in is a third option and is only legal once the
    /// first round of betting is over.
    ///
    /// Every move here is two calls, and the pairing is not decoration: <c>AddBullet</c> moves
    /// the stake and <c>CMDRised</c> is what tells the table the seat has acted and hands the
    /// turn on. Sending the second without the first bets nothing; sending the first without
    /// the second leaves the table waiting on a player who has already moved.
    ///
    /// Before betting in the first round, every other seat swaps a card, the way a person does
    /// it: open the switch on one of its two cards (<c>SetSwitch</c>, which tells every machine
    /// which card is going back), hold the menu open for three seconds as a person reading the
    /// options would, choose one (<c>SwitchCmd</c>), let the animation finish, then bet. The
    /// report this tests was "after changing cards you can see the next player's cards; if you
    /// don't change, question marks" - so which seats swap alternates hand by hand, and every
    /// hand has seats that swapped sitting next to seats that did not. The end of the swap is
    /// deliberately left to the game, which is half of what is being tested;
    /// <see cref="TexasSwap"/> ends it if the game does not.
    /// </summary>
    private sealed class TexasPlayer : IModePlayer
    {
        /// <summary>How long the switch menu stays open before a choice is made.</summary>
        private const float SwapHold = 3f;

        /// <summary>How long to wait for the swap animation to end before betting anyway.</summary>
        private const float SwapSettle = 3f;

        private static TexasGamePlayManager Mgr
        {
            get { try { return Manager.Instance != null ? Manager.Instance.TexasGame : null; } catch { return null; } }
        }

        // The swap in progress. There is only one turn at a time, so only ever one of these.
        private PlayerStats _swapper;
        private TexasGamePlay _swapGp;
        private int _swapSlot, _swapIdx, _swapOld, _swapMove;
        private bool _swapOwned;
        private float _openedAt, _chosenAt;

        /// <summary>
        /// Whether the host's answer to the choice has been read and said. A choice made through the
        /// seat's own menu is the seat's own command, which the host runs a frame or two later, so it
        /// is read then; read at once it was reported as refused while the swap went through.
        /// </summary>
        private bool _choiceJudged;

        /// <summary>
        /// The all-in question is answered by everyone at once rather than in turn, so it is
        /// not a turn to take - it is a state where taking one would be wrong.
        /// </summary>
        public bool Resolving()
        {
            var t = Mgr;
            if (t == null) return false;
            try { return t.AllInMode; } catch { return false; }
        }

        public bool Ready(PlayerStats p) => true;

        public void Settle(Manager m) { }

        public void Act(PlayerStats p, int moveNo)
        {
            var t = Mgr;
            var gp = p.GetComponent<TexasGamePlay>();
            if (t == null || gp == null) return;

            try { if (gp.Folded || gp.Rised) return; }
            catch { return; }

            if (StartSwap(p, gp, t, moveNo)) return;    // the bet follows once the swap is over
            Bet(p, gp, t, moveNo);
        }

        private static void Bet(PlayerStats p, TexasGamePlay gp, TexasGamePlayManager t, int moveNo)
        {
            try { if (gp.Folded || gp.Rised) return; }
            catch { return; }

            // Folding has to happen or the betting never ends, but a table that folds often
            // never reaches the later rounds where the switch and the all-in live. A fold fires
            // the seat's own stake, and folds made on a grown stake killed every time, so a fold
            // that is survived was never seen and the table thinned out before any showdown.
            // So the third move of a hand folds on the one bullet every seat is dealt, which the
            // revolver punishes one time in eight, and after that one seat in five folds, on a
            // stake of one at most.
            int stake = 0;
            try { stake = gp.Bullets; } catch { }
            bool fold = (moveNo == 3 || (moveNo > 2 && (moveNo % 5) == 0)) && stake <= 1;

            if (fold)
            {
                // Folding spins the revolver. Since the game's 14 Nov 2026 update the host rolls the
                // pull itself (TexasGamePlay.ServerResolveShot), so the seat asks for its outcome
                // the way its own game does, then ends the fold.
                Dev.Log("play", $"seat {p.Slot} folds on a stake of {stake}; the host rolls the pull");
                try
                {
                    gp.UserCode_CmdRequestRevolverOutcome();
                    gp.UserCode_CmdFolded();
                }
                catch (Exception e) { Dev.Warn("play", $"the fold did not go through: {e.Message}"); }
                return;
            }

            // All in is refused in the first betting round, so it is only ever offered later, and
            // not between the last two in a hand: it stakes all eight chambers, so it ended the
            // match on the spot instead of letting the hand go to a showdown.
            bool allIn = false;
            try { allIn = t.TexasRound > 0 && (moveNo % 11) == 0 && InHand() > 2; } catch { }

            Dev.Log("play", $"seat {p.Slot} {(allIn ? "goes ALL IN" : "calls")}");

            gp.UserCode_AddBullet__Boolean(allIn);
            gp.UserCode_CmdRised__Boolean(allIn);
        }

        /// <summary>Seats still in this hand: alive and not folded.</summary>
        private static int InHand()
        {
            int n = 0;
            foreach (var p in Manager.Instance.Players)
            {
                if (p == null || p.Dead) continue;
                var gp = p.GetComponent<TexasGamePlay>();
                if (gp != null && !gp.Folded) n++;
            }
            return n;
        }

        /// <summary>
        /// Open the switch, if this seat is one that swaps this hand and could swap now.
        ///
        /// The card going back is the server's own record of the seat's hand. The card objects'
        /// numbers are only filled in once the owner's machine answers the deal, which a seat
        /// with no machine behind it never does.
        /// </summary>
        private bool StartSwap(PlayerStats p, TexasGamePlay gp, TexasGamePlayManager t, int moveNo)
        {
            if (_swapper != null) return false;
            try
            {
                if (t.TexasRound != 0 || t.AllInMode) return false;
                if (!gp.HaveCards || gp.Switched || gp.isSwitching) return false;

                int hand = TexasSwap.HandsDealt;
                if (((p.Slot + hand) & 1) != 0) return false;

                var options = gp.SwitchCards;
                var types = gp.CardTypes;
                if (options == null || options.Count == 0 || types == null || types.Count < 2) return false;

                int idx = ((p.Slot >> 1) + hand) & 1;
                int old = types[idx];
                if (old <= 0) return false;

                // The seat this machine owns goes through the player's own path: the switch menu
                // is what raises the card in the character's hand, and that raised card is what the
                // neighbours see - the server-side calls alone never show it. Seats owned by another
                // copy, and bots, can only be driven from the server.
                _swapOwned = false;
                if (gp.isOwned)
                {
                    try
                    {
                        gp.switchcard = idx;
                        gp.SetSwitch(idx, old);
                        gp.OpenSwitchMenu();
                        _swapOwned = true;
                    }
                    catch (Exception e) { Dev.Warn("play", $"seat {p.Slot}: the player's own switch path failed ({e.Message}) - using the server's"); }
                }
                if (!_swapOwned)
                {
                    // The game's own key path: SetSwitch, then the menu opening plays the sound.
                    gp.UserCode_SetSwitch__Int32__Int32(idx, old);
                    try { gp.UserCode_SwitchStartSfxCmd(); } catch { }
                }

                _swapper = p;
                _swapGp = gp;
                _swapSlot = p.Slot;
                _swapIdx = idx;
                _swapOld = old;
                _swapMove = moveNo;
                _openedAt = Time.time;
                _chosenAt = 0f;

                Dev.Log("play", $"seat {p.Slot} opens the switch on card {idx} ({old}), " +
                                $"{options.Count} to choose from - choosing in {SwapHold:0} s");
                return true;
            }
            catch (Exception e)
            {
                Dev.Warn("play", $"seat {p.Slot} could not open the switch: {e.Message}");
                return false;
            }
        }

        /// <summary>Move a swap in progress on, from <see cref="ModePlay.Tick"/>.</summary>
        internal void Continue()
        {
            if (_swapper == null) return;
            try { Step(); }
            catch (Exception e)
            {
                Dev.Warn("play", $"seat {_swapSlot}'s swap failed: {e.Message}");
                Forget();
            }
        }

        private void Step()
        {
            var p = _swapper;
            var gp = _swapGp;
            var t = Mgr;

            string cut = null;
            if (p == null || gp == null || t == null) cut = "the table went away";
            else if (p.Dead) cut = "the seat is out";
            else if (!p.HaveTurn) cut = "the turn ended";
            else if (t.AllInMode) cut = "the table went all in";

            if (cut != null)
            {
                Dev.Warn("play", $"seat {_swapSlot}'s swap was cut short - {cut}");

                // A menu that was never chosen from is closed rather than left for the watchdog.
                if (_chosenAt == 0f && gp != null && gp.isSwitching) gp.UserCode_EndSwitchCmd();
                Forget();
                return;
            }

            float now = Time.time;
            if (_chosenAt == 0f)
            {
                if (now - _openedAt < SwapHold) return;

                var options = gp.SwitchCards;
                int offered = t.TexasRound == 0 ? 4 : t.TexasRound == 1 ? 3 : 2;
                int choices = Math.Min(offered, options != null ? options.Count : 0);
                if (choices <= 0)
                {
                    Dev.Warn("play", $"seat {_swapSlot} has nothing to switch to - closing the switch and betting");
                    if (gp.isSwitching) gp.UserCode_EndSwitchCmd();
                    BetNow(p, gp, t);
                    return;
                }

                int opt = (_swapSlot + TexasSwap.HandsDealt) % choices;
                int incoming = options[opt];
                bool viaMenu = false;
                if (_swapOwned)
                {
                    // What confirming in the menu does: send the choice and lower the raised card.
                    try { gp.selectedcard = opt; gp.Switch(_swapIdx, opt); viaMenu = true; }
                    catch (Exception e) { Dev.Warn("play", $"seat {_swapSlot}: confirming through the menu failed ({e.Message})"); }
                }
                if (!viaMenu) gp.UserCode_SwitchCmd__Int32__Int32(_swapIdx, opt);
                _chosenAt = now;

                // The server's own handler has run by now; the seat's own menu has only sent its
                // command, which the host takes on a later frame.
                _choiceJudged = !viaMenu;
                Dev.Log("play", $"seat {_swapSlot} swaps card {_swapIdx} ({_swapOld}) for option " +
                                $"{opt} of {choices} ({incoming}) - " +
                                (viaMenu ? "sent through its own menu" : gp.Switched ? "accepted" : "NOT accepted"));
                return;
            }

            float waited = now - _chosenAt;

            // A choice sent through the seat's own menu is judged once the host has run it, or once
            // it has had a second to arrive.
            if (!_choiceJudged && (gp.Switched || waited >= 1f))
            {
                _choiceJudged = true;
                Dev.Log("play", $"seat {_swapSlot}'s swap through its own menu was " +
                                $"{(gp.Switched ? "accepted" : "NOT accepted")} by the host, {waited:0.00} s after it was sent");
            }

            // Bet once the animation has ended the swap, as a person would - or, if it has not,
            // after a few seconds, leaving the stuck flag for the watchdog to find.
            if (gp.isSwitching && waited < SwapSettle) return;

            if (gp.isSwitching)
                Dev.Warn("play", $"seat {_swapSlot} is still marked mid-swap {waited:0.0} s after choosing - betting anyway");
            else
                Dev.Log("play", $"seat {_swapSlot}'s swap ended within {waited:0.0} s of choosing");

            BetNow(p, gp, t);
        }

        private void BetNow(PlayerStats p, TexasGamePlay gp, TexasGamePlayManager t)
        {
            int move = _swapMove;
            Forget();
            Bet(p, gp, t, move);
        }

        private void Forget()
        {
            _swapper = null;
            _swapGp = null;
            _chosenAt = 0f;
        }
    }

    /// <summary>
    /// Liar's Spin: spin the reels, then claim how many of something are showing.
    ///
    /// Two moves make a turn here rather than one. A seat spins its own machine - which is
    /// what gives it something to lie about - and then either raises the standing claim or
    /// calls the last claim a lie. Raising is the ordinary move and challenging is what ends
    /// the round, so as in Liar's Dice it has to happen often enough to matter and rarely
    /// enough to let a claim get somewhere first.
    ///
    /// The turn is passed by the player, not by the table. <c>Manager.GiveTurnSpin</c> is
    /// reached only through <c>CmdGiveTurnSpin</c>, which the seat that bid sends itself, so a
    /// bid without that second call leaves the table stopped on the seat that made it.
    ///
    /// The loser of a liar call then takes the dead spin, and the host waits for it
    /// (<c>CoDoSpinRoulette</c> holds on <c>isButtonClicked</c>): the loser's own copy plays the
    /// dead-spin animation and, at its <c>DeadSpin</c> event, sends <c>ButtonClicked</c>, which
    /// only the seat's own copy may send. A bot's seat has no copy, so for a bot that step is
    /// taken here (<see cref="Settle"/>), or the table stops for good at the first call a bot loses.
    /// </summary>
    private sealed class SpinPlayer : IModePlayer
    {
        private static LiarsSpinGameplayManager Mgr
        {
            get { try { return Manager.Instance != null ? Manager.Instance.SpinGame : null; } catch { return null; } }
        }

        /// <summary>The seat whose dead spin was taken here, until its dead-spin moment is over; -1 for none.</summary>
        private int _deadSpinTaken = -1;

        /// <summary>
        /// A bot that lost a liar call takes its dead spin. The host marks the loser's moment
        /// (<c>isDeadSpinMoment</c>) about 1.3 s after it starts the dead-spin animation, and a
        /// person's copy sends its <c>ButtonClicked</c> from that animation 0.8 to 1.4 s in, so the
        /// bot's is sent once its moment is seen: the server's own handler, as the command runs on
        /// arrival. Once per dead spin. A seat already out is passed over, in case its moment is
        /// still marked from the spin that put it out.
        /// </summary>
        public void Settle(Manager m)
        {
            var s = Mgr;
            if (s == null || m == null || m.Players == null) return;

            PlayerStats loser = null;
            SpinGamePlay loserGp = null;
            bool anyMoment = false;
            foreach (var p in m.Players)
            {
                if (p == null || p.Dead) continue;
                var gp = p.GetComponent<SpinGamePlay>();
                bool moment = false;
                try { moment = gp != null && gp.isDeadSpinMoment; } catch { }
                if (!moment) continue;
                anyMoment = true;
                if (p.Slot == _deadSpinTaken) continue;
                loser = p;
                loserGp = gp;
                break;
            }

            if (!anyMoment) { _deadSpinTaken = -1; return; }
            if (loser == null || !BotManager.IsBot(loser)) return;

            bool clicked;
            try { clicked = s.isButtonClicked; } catch { return; }
            if (clicked) return;

            _deadSpinTaken = loser.Slot;
            Dev.Log("play", $"seat {loser.Slot} takes its dead spin: a bot has no copy to send it, so the host runs it");
            try { loserGp.UserCode_ButtonClicked(); }
            catch (Exception e) { Dev.Warn("play", $"seat {loser.Slot}'s dead spin did not go through: {e.Message}"); }
        }

        /// <summary>A liar call or the penalty spin is being settled: nobody moves.</summary>
        public bool Resolving()
        {
            var s = Mgr;
            if (s == null) return false;
            try { return s.CalledLiar || s.isCallingLiar || s.isDeadSpin; } catch { return false; }
        }

        /// <summary>
        /// A person's seat claims only once the game lets it bid, which it does after that seat's own
        /// copy has played the reveal of its spin. A bot's seat (the harness's own switch) has no copy
        /// to play one, so the game never lets it bid that way, and waiting for it left every bot's
        /// turn to run out on its clock: 38 seconds a turn, eleven turns in six minutes. The claim
        /// itself (the server's RiseCmd) does not ask, so a bot moves as soon as it holds the turn.
        /// </summary>
        public bool Ready(PlayerStats p)
        {
            if (BotManager.IsBot(p)) return true;
            var s = Mgr;
            try { return s != null && s.CanGiveBid; } catch { return false; }
        }

        public void Act(PlayerStats p, int moveNo)
        {
            var s = Mgr;
            var gp = p.GetComponent<SpinGamePlay>();
            if (s == null || gp == null) return;
            bool bot = BotManager.IsBot(p);

            // Bidding without having spun is a move the game offers only when it says so, and
            // it is not the one being tested. Spin first, then bid on the next tick - the reels
            // take a moment and a claim made before they stop is a claim about nothing.
            // A bot's seat spins with its claim instead (below): only the seat's own copy marks a
            // spin as seen, which a bot has none of, so a bot that spun here would spin every turn
            // and never claim.
            try
            {
                if (!bot && !gp.hasSpinThisRound && !gp.CanBidWithoutSpin)
                {
                    Dev.Log("play", $"seat {p.Slot} spins the machine");
                    gp.UserCode_Cmd_RequestSecretSpin();
                    return;                                  // the bid comes next time round
                }
            }
            catch { }

            int last = 0;
            bool anyBid = false;
            try { last = s.LastShowsCount; anyBid = s.isBetGivenBefore; } catch { }

            // Challenge every fourth move once there is something to challenge. The count can
            // always be raised, so nothing else would ever end a round.
            if (anyBid && last > 0 && (moveNo % 4) == 0)
            {
                Dev.Log("play", $"seat {p.Slot} calls LIAR on a claim of {last}");
                try
                {
                    gp.UserCode_PlayLiarCMD();
                    gp.UserCode_CallLiar();
                }
                catch (Exception e) { Dev.Warn("play", $"the liar call did not go through: {e.Message}"); }

                // No handover on purpose: a challenge ends the round and the reveal gives the
                // turn out again itself.
                return;
            }

            // The bid keys clamp against the mode's own ceiling and wrap round at the top, so
            // this does the same. Sending a number a player could not have dialled in would be
            // testing something nobody can do.
            int ceiling = SpinBidCap.Ceiling();
            int count = last + 1;
            if (ceiling > 0 && count > ceiling) count = 0;

            Dev.Log("play", $"seat {p.Slot} claims {count}" +
                            (ceiling > 0 ? $" (the highest allowed is {ceiling})" : "") +
                            (bot ? ", its reels spun on the host first" : ""));

            try
            {
                // The server's half of a spin, as any seat's spin starts: its four reels are rolled on
                // the host, which is where a call on the claim is judged, so a call on a bot's claim
                // is judged on reels it really spun. Its copy's half, the reveal, it has none of.
                if (bot) gp.UserCode_Cmd_RequestSecretSpin();
                gp.UserCode_RiseCmd__Int32(count);
                gp.UserCode_CmdGiveTurnSpin();
            }
            catch (Exception e) { Dev.Warn("play", $"the claim did not go through: {e.Message}"); }
        }
    }
}
