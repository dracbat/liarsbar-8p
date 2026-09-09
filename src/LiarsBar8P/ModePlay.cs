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
/// </summary>
internal static class ModePlay
{
    /// <summary>How long a seat appears to think. Short: these modes have their own timers.</summary>
    private const float MinThink = 0.8f;
    private const float MaxThink = 2.2f;

    private static readonly Dictionary<int, float> _actAt = new();
    private static readonly Dictionary<int, int> _actedOn = new();
    private static int _turnNo;
    private static int _lastActive = -1;
    private static int _moves;

    internal static void RoundStarting()
    {
        _actAt.Clear();
        _actedOn.Clear();
        _moves = 0;
    }

    internal static void Tick()
    {
        if (!Dev.Enabled || !Dev.IsServer) return;

        // Only when the harness is standing in for everybody. A real player's turn is theirs.
        if (!(DevAutoTest.Driving || Loopback.Mine == Loopback.Role.Host)) return;

        var m = Dev.Mgr;
        if (m == null || m.Players == null || !m.GameStarted) return;

        var kind = TableHand.Playing();
        var player = For(kind);
        if (player == null) return;                 // Liar's Deck and Chaos Deck: BotBehaviour's job

        // Count turns the same way BotBehaviour does, so "already acted this turn" survives the
        // active seat coming round again a lap later.
        int active = m.ActivePlayerSlot;
        if (active != _lastActive) { _lastActive = active; _turnNo++; }

        if (player.Resolving()) { _actAt.Clear(); return; }

        foreach (var p in m.Players)
        {
            if (p == null || p.Dead || !p.HaveTurn) { if (p != null) _actAt.Remove(p.Slot); continue; }
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
            catch (Exception e) { Dev.Warn("play", $"{p.PlayerName} could not act in {kind}: {e.Message}"); }
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

        void Act(PlayerStats p, int moveNo);
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

        public void Act(PlayerStats p, int moveNo)
        {
            var d = Mgr;
            var gp = p.GetComponent<DiceGamePlay>();
            if (d == null || gp == null) return;

            // The dice have to have been rolled before there is anything to bid about.
            try { if (gp.DiceValues == null || gp.DiceValues.Count == 0) return; } catch { return; }

            bool anyBid = false;
            int count = 0, face = 0, total = 0;
            try { anyBid = d.BetPlaced; count = d.LastCount; face = d.LastDice; total = d.TotalCount; } catch { }

            if (!anyBid || count <= 0)
            {
                int openFace = 2 + (p.Slot % 4);
                Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) opens the bidding: 1 x {openFace}");
                Bid(p, gp, 1, openFace);
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
                    Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) calls SPOT ON on {count} x {face}");
                    gp.UserCode_PlaySpotOnCMD();
                    gp.UserCode_CallSpotOn();
                }
                else
                {
                    Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) calls LIAR on {count} x {face}" +
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

            Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) raises to {nextCount} x {nextFace}");
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
    /// Liar's Texas: put a bullet in, or get out.
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
    /// </summary>
    private sealed class TexasPlayer : IModePlayer
    {
        private static TexasGamePlayManager Mgr
        {
            get { try { return Manager.Instance != null ? Manager.Instance.TexasGame : null; } catch { return null; } }
        }

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

        public void Act(PlayerStats p, int moveNo)
        {
            var t = Mgr;
            var gp = p.GetComponent<TexasGamePlay>();
            if (t == null || gp == null) return;

            try { if (gp.Folded || gp.Rised) return; }
            catch { return; }

            // Folding has to happen or the betting never ends, but a table that folds often
            // never reaches the later rounds where the switch and the all-in live. One seat in
            // five, and never the first two moves of a round.
            bool fold = moveNo > 2 && (moveNo % 5) == 0;

            if (fold)
            {
                Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) folds");

                // Folding spins the revolver, and whether that kills is the server's decision
                // to make and announce - not something to be worked out here and asserted.
                bool dies = false;
                // The server answers with two facts - whether the chamber was loaded and whether
                // the game intervened to save them. Only the first decides what to send next.
                try { dies = gp.ComputeRevolverOutcomeServer().Item1; }
                catch (Exception e) { Dev.Warn("play", $"could not spin the revolver: {e.Message}"); }

                try
                {
                    if (dies) gp.UserCode_CommandBeDead();
                    else gp.UserCode_CMDFolded();
                }
                catch (Exception e) { Dev.Warn("play", $"the fold did not go through: {e.Message}"); }
                return;
            }

            // All in is refused in the first betting round, so it is only ever offered later.
            bool allIn = false;
            try { allIn = t.TexasRound > 0 && (moveNo % 11) == 0; } catch { }

            Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) {(allIn ? "goes ALL IN" : "calls")}");

            gp.UserCode_AddBullet__Boolean(allIn);
            gp.UserCode_CMDRised__Boolean(allIn);
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
    /// </summary>
    private sealed class SpinPlayer : IModePlayer
    {
        private static LiarsSpinGameplayManager Mgr
        {
            get { try { return Manager.Instance != null ? Manager.Instance.SpinGame : null; } catch { return null; } }
        }

        public bool Resolving()
        {
            var s = Mgr;
            if (s == null) return false;
            try { return s.CalledLiar || s.isCallingLiar || !s.CanGiveBid; } catch { return false; }
        }

        public void Act(PlayerStats p, int moveNo)
        {
            var s = Mgr;
            var gp = p.GetComponent<SpinGamePlay>();
            if (s == null || gp == null) return;

            // Bidding without having spun is a move the game offers only when it says so, and
            // it is not the one being tested. Spin first, then bid on the next tick - the reels
            // take a moment and a claim made before they stop is a claim about nothing.
            try
            {
                if (!gp.hasSpinThisRound && !gp.CanBidWithoutSpin)
                {
                    Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) spins the machine");
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
                Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) calls LIAR on a claim of {last}");
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

            Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) claims {count}" +
                            (ceiling > 0 ? $" (the highest allowed is {ceiling})" : ""));

            try
            {
                gp.UserCode_RiseCmd__Int32(count);
                gp.UserCode_CmdGiveTurnSpin();
            }
            catch (Exception e) { Dev.Warn("play", $"the claim did not go through: {e.Message}"); }
        }
    }
}
