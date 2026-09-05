using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Two roster faults break dealing above four players, and both are corrected just before
/// the round is set up.
///
/// StartPlayerCount lags. Manager.StartGame throws a NullReferenceException, so the count
/// stays at four while five players are seated. Anything looping over it deals to four
/// people and leaves the fifth empty handed - exactly the reported symptom.
///
/// Seat indices can exceed the roster. Seats are expanded to the configured maximum, so a
/// player can hold a seat index beyond the number of players present; anything indexing
/// the player list by seat then runs off the end, which is the most likely source of the
/// ArgumentOutOfRangeException that survived every collection being grown. Seats are
/// compacted to 0..n-1, preserving their relative order so the seating around the table
/// is unchanged.
/// </summary>
internal static class RosterFix
{
    private static int _lastLogged = -1;

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]   // must run before the deck and seat logic read these
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void Fix(bool first)
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Players == null) return;

            int count = m.Players.Count;
            if (count < 2) return;

            // --- 1. correct the player count ------------------------------------------
            if (m.StartPlayerCount != count)
            {
                Plugin.Log.LogWarning(
                    $"[roster] StartPlayerCount {m.StartPlayerCount} -> {count} " +
                    "(it lags because Manager.StartGame throws; dealing loops over it)");
                m.StartPlayerCount = count;
            }

            // --- 2. compact seat indices into 0..count-1 ------------------------------
            var order = new System.Collections.Generic.List<PlayerStats>();
            for (int i = 0; i < count; i++)
                if (m.Players[i] != null) order.Add(m.Players[i]);

            order.Sort((a, b) => a.Slot.CompareTo(b.Slot));

            bool changed = false;
            var before = new System.Text.StringBuilder();
            var after = new System.Text.StringBuilder();

            for (int i = 0; i < order.Count; i++)
            {
                before.Append($" {order[i].Slot}");
                if (order[i].Slot != i)
                {
                    order[i].Slot = i;
                    changed = true;
                }
                after.Append($" {i}");
            }

            if (changed)
            {
                Plugin.Log.LogWarning(
                    $"[roster] seat indices compacted:{before}  ->{after}  " +
                    "(a seat index past the roster breaks anything indexing players by seat)");
            }
            else if (count != _lastLogged)
            {
                _lastLogged = count;
                Plugin.Log.LogInfo($"[roster] {count} players, seats already contiguous:{after}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[roster] fix failed: {e.Message}");
        }
    }
}
