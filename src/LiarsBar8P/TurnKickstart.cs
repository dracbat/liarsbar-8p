using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Keeps the turn moving when the game's own code stops moving it.
///
/// A round can reach the point where every player is seated, dealt and holding their
/// cards, and then simply stop: nobody is given the turn, so nobody can act, and the
/// table sits there. The game hands out the first turn from inside the deal's animation
/// routine, behind a filter that does not always produce anybody.
///
/// Rather than guess at that filter, this watches for the state that matters — a round is
/// under way, cards are dealt, and nobody holds the turn — and if that persists, asks the
/// game to give the turn through its own <c>GiveTurn</c>, which is the same method every
/// later turn goes through. It fires at most once per round and says so loudly, because a
/// round needing this means the game's own path did not run and that is worth seeing.
/// </summary>
internal static class TurnKickstart
{
    /// <summary>Long enough that a turn arriving normally is never pre-empted.</summary>
    private const float GraceSeconds = 4f;

    private static float _stalledSince;
    private static bool _startedThisRound;

    /// <summary>Called when a round begins, so each round gets one chance at this.</summary>
    internal static void RoundStarting()
    {
        _startedThisRound = false;
        _stalledSince = 0f;
    }

    internal static void Tick()
    {
        try
        {
            if (!Mirror.NetworkServer.active) return;

            var m = Manager.Instance;
            if (m == null || m.Players == null || m.Players.Count < 2 || !m.GameStarted)
            {
                _stalledSince = 0f;
                return;
            }

            bool anyoneHasTheTurn = false;
            bool everyoneIsHolding = true;
            int dealt = 0;
            int alive = 0;

            foreach (var p in m.Players)
            {
                if (p == null || p.Dead) { if (p != null && p.HaveTurn) anyoneHasTheTurn = true; continue; }
                alive++;
                if (p.HaveTurn) anyoneHasTheTurn = true;

                var gp = p.GetComponent<DeckGameplay>();
                if (gp == null || gp.cardTypes == null || gp.cardTypes.Count == 0)
                {
                    everyoneIsHolding = false;      // not dealt yet
                    continue;
                }

                dealt++;

                // Being dealt is not the same as holding. Card values arrive well before the
                // card objects reach anybody's hands, and starting the turn in that gap gives
                // a player their turn while their hand is still empty - which is exactly what
                // was reported: bots taking turns before the cards were dealt.
                if (!gp.HaveCards || !Holding(gp)) everyoneIsHolding = false;
            }

            // A turn is in progress, or the round has not finished dealing: nothing to do.
            if (anyoneHasTheTurn || dealt == 0 || !everyoneIsHolding || alive < 2)
            {
                _stalledSince = 0f;
                if (anyoneHasTheTurn) _startedThisRound = true;
                return;
            }

            if (_stalledSince == 0f) { _stalledSince = Time.time; return; }
            if (Time.time - _stalledSince < GraceSeconds) return;

            _stalledSince = 0f;

            // Not only the first turn. After a player throws, the game schedules the pass
            // to the next player, and that schedule does not always complete - a table
            // where everyone is dealt and nobody can act is stuck whether it is the first
            // turn or the fifth. Both are the same stall and get the same nudge.
            int wanted = m.ActivePlayerSlot;
            bool waiting = WaitingToPlay(m, wanted);

            Plugin.Log.LogWarning(
                $"[turnstart] all {alive} players are holding their cards but nobody has the turn " +
                $"after {GraceSeconds:F0}s ({(_startedThisRound ? "mid-round" : "start of round")}) - " +
                (waiting
                    ? $"seat {wanted} is due to play and has not been given it"
                    : "moving it on"));
            _startedThisRound = true;

            // The distinction matters, and getting it wrong skipped every other player. When
            // the game has already moved the active slot on and only failed to hand the turn
            // over, that seat must simply be *given* it. Advancing again from there steps
            // past a player who never got to act: seat 1, then 3, then 5.
            if (waiting) m.GiveTurnToActiveSlot();
            else m.GiveTurn();

            bool started = false;
            foreach (var p in m.Players) if (p != null && p.HaveTurn) started = true;

            if (started)
                Plugin.Log.LogInfo($"[turnstart] turn started; active slot is {m.ActivePlayerSlot}");
            else if (waiting)
            {
                // Giving it to the seat that was due did not take; fall back to advancing.
                Plugin.Log.LogWarning("[turnstart] seat " + wanted + " would not take the turn - advancing instead");
                m.GiveTurn();
                foreach (var p in m.Players) if (p != null && p.HaveTurn) started = true;
                if (!started)
                    Plugin.Log.LogError("[turnstart] the turn could not be given to anybody - the round cannot proceed");
            }
            else
                Plugin.Log.LogError(
                    "[turnstart] asked the game to give the turn and it still did not - the round " +
                    "cannot proceed");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[turnstart] failed: {e.Message}");
            _startedThisRound = true;      // do not retry a throwing path every quarter second
        }
    }

    /// <summary>
    /// Is there a living player on the active seat who has not yet been given the turn?
    ///
    /// This is the ordinary stall: the game moves the active slot on after somebody plays
    /// and then does not complete the handover, so a seat is due to play and holds nothing.
    /// Telling those two cases apart is what stops the turn skipping every other player.
    /// </summary>
    private static bool WaitingToPlay(Manager m, int slot)
    {
        try
        {
            if (slot < 0 || m.Players == null) return false;
            foreach (var p in m.Players)
                if (p != null && p.Slot == slot && !p.Dead && !p.HaveTurn) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Are this player's cards actually in their hands?
    ///
    /// <c>HaveCards</c> is a flag the game sets, and it sets it for everybody while players
    /// in the added seats hold nothing. The card objects being switched on is the physical
    /// fact. <c>activeSelf</c> rather than <c>activeInHierarchy</c>, because a whole hand is
    /// hidden while a player has their cards down and that says nothing about the deal.
    /// </summary>
    private static bool Holding(DeckGameplay gp)
    {
        try
        {
            if (gp.Cards == null || gp.cardTypes == null) return false;
            int on = 0;
            foreach (var c in gp.Cards)
                if (c != null && c.activeSelf) on++;
            return on >= gp.cardTypes.Count;
        }
        catch { return false; }
    }
}
