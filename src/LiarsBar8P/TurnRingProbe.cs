using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Hands the turn round the table on purpose, and reports which seats it reached.
///
/// The turn order fix rewrites the seat number five separate places wrap at, and three of
/// those - <c>GiveTurn</c>, <c>GiveTurnTexas</c>, <c>GiveTurnSpin</c> - belong to different
/// modes. Only the first has ever been watched doing its job, because watching it needs a
/// round to be played, and a round only gets played where there is something able to play
/// it. The loopback harness has the host act for every seat, and it only knows how to act in
/// Liar's Deck. So in every other mode the wrap was rewritten, logged as rewritten, and
/// never once seen to work.
///
/// This asks the question directly instead of waiting for a game to ask it: call the mode's
/// own turn-giving method a few more times than there are seats, and write down where the
/// turn actually went. If the wrap is right the turn visits every seat and comes back round;
/// if it still wraps at four, seats four and up never appear and the list repeats early -
/// which is the whole bug, visible in one line.
///
/// It is a test instrument and says so: nothing happens unless <c>LIARSBAR8P_TURNPROBE</c>
/// is set, which the harness sets and a player never does. It runs once per match, on the
/// server, and stops.
/// </summary>
internal static class TurnRingProbe
{
    /// <summary>Let the round get properly under way before interfering with it.</summary>
    private const float SettleSeconds = 12f;

    /// <summary>Slow enough that each handover is a separate, observable step.</summary>
    private const float StepSeconds = 1.2f;

    private static bool? _wanted;
    private static Manager _for;
    private static float _next;
    private static int _left;
    private static readonly List<int> _visited = new();

    private static bool Wanted
    {
        get
        {
            if (_wanted == null)
            {
                string raw = Environment.GetEnvironmentVariable("LIARSBAR8P_TURNPROBE");
                _wanted = !string.IsNullOrEmpty(raw) && raw != "0";
            }
            return _wanted.Value;
        }
    }

    internal static void Tick()
    {
        if (!Wanted) return;

        try
        {
            if (!Mirror.NetworkServer.active) { _for = null; return; }

            var m = Manager.Instance;
            if (m == null || m.Players == null || m.Players.Count < 2 || !m.GameStarted) { _for = null; return; }

            if (!ReferenceEquals(_for, m))
            {
                _for = m;
                _visited.Clear();

                // Twice round rather than once. Some tables move the turn on themselves - the
                // Chaos deck throws for a player who is slow - so the sequence this sees is
                // its own handovers interleaved with the game's, and a single lap's worth of
                // steps stopped short of a couple of seats and reported them as unreachable.
                _left = m.Players.Count * 2 + 4;
                _next = Time.time + SettleSeconds;
                return;
            }

            if (_left <= 0 || Time.time < _next) return;
            _next = Time.time + StepSeconds;

            string how = Hand(m);
            _left--;

            int slot = m.ActivePlayerSlot;
            if (_visited.Count == 0 || _visited[_visited.Count - 1] != slot) _visited.Add(slot);

            if (_left > 0) return;

            // Only the seats still in the game. The turn is supposed to skip a player who has
            // been shot, so counting them as missed reported a fault where the game was doing
            // exactly the right thing - a seven player round with one player already out came
            // back as "reached only 6 of 7".
            var wanted = new HashSet<int>();
            foreach (var p in m.Players)
                if (p != null && !p.Dead) wanted.Add(p.Slot);

            var seats = new HashSet<int>(_visited);
            var missed = new List<int>();
            foreach (int alive in wanted) if (!seats.Contains(alive)) missed.Add(alive);

            string order = string.Join(" -> ", _visited);
            if (missed.Count == 0)
                Plugin.Log.LogWarning(
                    $"[turnring] {how}: the turn reached all {wanted.Count} seats still in - {order}");
            else
                Plugin.Log.LogError(
                    $"[turnring] {how}: the turn reached only {wanted.Count - missed.Count} of " +
                    $"{wanted.Count} seats still in - never {string.Join(", ", missed)} - {order}");
        }
        catch (Exception e) { Plugin.Log.LogError($"[turnring] failed: {e.Message}"); }
    }

    /// <summary>
    /// Pass the turn on using the method this mode actually uses.
    ///
    /// Texas and Spin have their own, and their own wrap-around seat number with it. Calling
    /// the deck one for them would test the wrong constant and report a pass that means
    /// nothing.
    /// </summary>
    private static string Hand(Manager m)
    {
        // Which mode is running is decided in one place, from the component on the players.
        // Deciding it here from whether Texas's manager object was awake meant Texas's
        // handover was used in a game of Liar's Deck, where it throws - filling the log with
        // exceptions and testing the wrong constant.
        switch (TableHand.Playing())
        {
            case TableHand.Kind.Texas: m.GiveTurnTexas(); return "GiveTurnTexas";
            case TableHand.Kind.Spin:  m.GiveTurnSpin();  return "GiveTurnSpin";
            default:                   m.GiveTurn();      return "GiveTurn";
        }
    }
}
