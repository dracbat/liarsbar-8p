using System;

namespace LiarsBar8P;

/// <summary>
/// Reads the ceiling on a claim in Liar's Spin, and says what it is.
///
/// This was going to raise it. A claim in Liar's Spin is a number, and the number is clamped
/// against <c>LiarsSpinGameplayManager.MaxCount</c> - a plain value set in the scene that
/// nothing in the game ever recomputes. Each player has four reels, so the reasoning was that
/// a ceiling set for four players would leave eight players unable to claim more than half of
/// what is honestly on the table, and the mode would quietly stop being a game without
/// anything in a log complaining.
///
/// Read at runtime, the shipped ceiling is <b>four</b> - not sixteen. Four reels each and four
/// players is sixteen symbols on the table, so the ceiling was already far below an honest
/// count before this mod touched anything. Whatever four means here, it is not four players:
/// a number that was derived from the seat count would have been sixteen.
///
/// So it is left exactly as shipped. Raising it would not be fixing a four-player assumption,
/// it would be changing what the mode is, on a guess about a constant whose meaning is not
/// established - and a mod that quietly rewrites a game's rules because a number looked small
/// is worse than one that leaves them alone. The value is printed instead, with the table size
/// beside it, so the fact is on the record and a later run that shows the mode misbehaving at
/// eight has somewhere to start.
///
/// The bid keys clamp and wrap against this number, so <see cref="ModePlay"/> keeps its claims
/// inside it too. A test that made claims a player cannot make would not be testing the mode.
/// </summary>
internal static class SpinBidCap
{
    /// <summary>Reels in front of each player.</summary>
    internal const int ReelsPerSeat = 4;

    private static LiarsSpinGameplayManager _reported;

    /// <summary>The highest claim the game allows, or zero if it cannot be read.</summary>
    internal static int Ceiling()
    {
        try
        {
            var m = Manager.Instance;
            var spin = m != null ? m.SpinGame : null;
            return spin != null ? spin.MaxCount : 0;
        }
        catch { return 0; }
    }

    internal static void Tick()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || !m.GameStarted) { _reported = null; return; }

            // Which mode is running is decided from the component on the players, never from
            // whether a manager object exists. Every mode's manager is awake in every match,
            // so "the Spin manager is here" is true during a game of Texas - and this first
            // reported the Spin bid ceiling in the middle of a Texas round.
            if (TableHand.Playing() != TableHand.Kind.Spin) return;

            var spin = m.SpinGame;
            if (spin == null || ReferenceEquals(_reported, spin)) return;
            _reported = spin;

            int n = AimRing.Seats(m);
            int ceiling = spin.MaxCount;

            Plugin.Log.LogWarning(
                $"[spin] the highest claim allowed is {ceiling}; {n} players have " +
                $"{ReelsPerSeat * n} reels between them. Left as shipped - the ceiling is not " +
                "worked out from the number of players, so raising it would change the mode " +
                "rather than fix it");
        }
        catch (Exception e) { Plugin.Log.LogError($"[spin] could not read the bid ceiling: {e.Message}"); }
    }
}
