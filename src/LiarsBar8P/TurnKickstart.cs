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
            bool anyoneHasCards = false;
            int alive = 0;

            foreach (var p in m.Players)
            {
                if (p == null) continue;
                if (!p.Dead) alive++;
                if (p.HaveTurn) anyoneHasTheTurn = true;

                var gp = p.GetComponent<DeckGameplay>();
                if (gp != null && gp.cardTypes != null && gp.cardTypes.Count > 0) anyoneHasCards = true;
            }

            // A turn is in progress, or the round has not dealt yet: nothing to do.
            if (anyoneHasTheTurn || !anyoneHasCards || alive < 2)
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
            Plugin.Log.LogWarning(
                $"[turnstart] {alive} players are dealt and holding cards but nobody has the turn " +
                $"after {GraceSeconds:F0}s ({(_startedThisRound ? "mid-round" : "start of round")}) - moving it on");
            _startedThisRound = true;

            m.GiveTurn();

            bool started = false;
            foreach (var p in m.Players) if (p != null && p.HaveTurn) started = true;

            if (started)
                Plugin.Log.LogInfo($"[turnstart] turn started; active slot is {m.ActivePlayerSlot}");
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
}
