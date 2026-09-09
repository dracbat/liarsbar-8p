using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Says how long Liar's Dice's per-seat lists are, and how many seats there are.
///
/// Resolving a liar call at eight players throws <c>IndexOutOfRangeException</c>. Nothing in
/// an IL2CPP build gives a stack for that, so the choice is between guessing which collection
/// is too short and measuring. Liar's Dice keeps several things one-per-player - the turn
/// labels above each seat, the dice icons, the slots the reveal panel drops dice into - and
/// each of them ships sized for the table the game was built for.
///
/// Guessing here has a poor record. The deck's deal was chased through four different
/// candidate collections before the real one turned up, and each wrong guess looked plausible
/// enough to write a fix for. So this writes the numbers down instead: every list the mode
/// keeps per seat, its length, and the number of people at the table, printed once when a
/// round of Liar's Dice starts. Whichever is shorter than the table is the one to grow.
///
/// It is a measurement, not a fix. Nothing here changes anything.
/// </summary>
internal static class DiceLists
{
    private static DiceGamePlayManager _reported;

    internal static void Tick()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || !m.GameStarted) { _reported = null; return; }

            // From the component on the players, not from whether the manager object exists -
            // every mode's manager is awake in every match.
            if (TableHand.Playing() != TableHand.Kind.Dice) return;

            var d = m.DiceGame;
            if (d == null || ReferenceEquals(_reported, d)) return;
            _reported = d;

            int seats = AimRing.Seats(m);
            var sb = new System.Text.StringBuilder();

            Add(sb, "turnTexts", Count(() => d.turnTexts != null ? d.turnTexts.Count : -1));
            Add(sb, "DiceIcons", Count(() => d.DiceIcons != null ? d.DiceIcons.Count : -1));

            var panel = d.dicePanel;
            if (panel != null)
            {
                Add(sb, "panel.diceList", Count(() => panel.diceList != null ? panel.diceList.Count : -1));
                Add(sb, "panel.diceList2", Count(() => panel.diceList2 != null ? panel.diceList2.Count : -1));
                Add(sb, "panel.dices", Count(() => panel.dices != null ? panel.dices.Count : -1));
                Add(sb, "panel.spawnedDice", Count(() => panel.spawnedDice != null ? panel.spawnedDice.Count : -1));
            }
            else sb.Append("  dicePanel=none");

            Plugin.Log.LogWarning($"[dicelists] {seats} at the table;{sb}");
        }
        catch (Exception e) { Plugin.Log.LogError($"[dicelists] could not measure: {e.Message}"); }
    }

    private static int Count(Func<int> read)
    {
        try { return read(); } catch { return -2; }
    }

    private static void Add(System.Text.StringBuilder sb, string name, int n)
    {
        sb.Append($"  {name}={(n < 0 ? "?" : n.ToString())}");
    }
}
