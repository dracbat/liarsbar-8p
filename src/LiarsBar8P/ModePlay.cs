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
            case TableHand.Kind.Dice: return _dice;
            default: return null;
        }
    }

    private static readonly DicePlayer _dice = new();

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
            try { return d.CalledLiar || d.CalledSpotOn; } catch { return false; }
        }

        public void Act(PlayerStats p, int moveNo)
        {
            var d = Mgr;
            var gp = p.GetComponent<DiceGamePlay>();
            if (d == null || gp == null) return;

            bool anyBid = false;
            int count = 0, face = 0, total = 0;
            try { anyBid = d.BetPlaced; count = d.LastCount; face = d.LastDice; total = d.TotalCount; } catch { }

            // Nothing on the table yet: open modestly so there is room to raise.
            if (!anyBid || count <= 0)
            {
                int openFace = 2 + (p.Slot % 4);
                Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) opens the bidding: 1 x {openFace}");
                gp.PlaceBet(1, openFace);
                return;
            }

            // Challenging is the only move that can end a round, so it has to happen - but not
            // so often that the bidding never gets going. Every fourth move, and always once
            // the bid has run past the dice that physically exist.
            bool absurd = total > 0 && count >= total;
            if (absurd || (moveNo % 4) == 0)
            {
                bool spotOn = false;
                try { spotOn = d.CanSpotOn && (moveNo % 8) == 0; } catch { }

                if (spotOn)
                {
                    Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) calls SPOT ON on {count} x {face}");
                    gp.CallSpotOn();
                }
                else
                {
                    Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) calls LIAR on {count} x {face}" +
                                    (absurd ? " - the bid is past the dice on the table" : ""));
                    gp.CallLier();
                }
                return;
            }

            // Raise: a higher face at the same count where there is room, otherwise one more die.
            int nextCount = count, nextFace = face + 1;
            if (nextFace > 6) { nextFace = 2; nextCount = count + 1; }

            Dev.Log("play", $"{p.PlayerName} (seat {p.Slot}) raises to {nextCount} x {nextFace}");
            gp.PlaceBet(nextCount, nextFace);
        }
    }
}
