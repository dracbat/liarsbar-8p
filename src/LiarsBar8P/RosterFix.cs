using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Brings the roster, the seat indices and the seat list into agreement before a round is
/// set up. Three faults, all stemming from the same mismatch.
///
/// 1. StartPlayerCount lags. Manager.StartGame throws, so the count stays at four while
///    five players are seated. Anything looping over it deals to four people and leaves
///    the fifth with nothing.
///
/// 2. Seat indices can exceed the roster. Seats are expanded to the configured maximum so
///    that a fifth player has somewhere to sit when they spawn, which means a player can
///    hold a seat index beyond the number of players present. Anything indexing the
///    player list by seat then runs off the end.
///
/// 3. The seat list stays at the maximum. This is the important one: the seats must exist
///    when players spawn, but leaving eight seats for five players means turn logic
///    walking the seat list lands on empty seats. The indicator points at an empty chair
///    while a real player acts - the reported "arrow points at nobody" - and dealing that
///    walks seats misses people.
///
/// So the list is trimmed back to exactly the players present once everyone has spawned.
/// The seats we added are only deactivated, never destroyed, so a later round with more
/// players can grow again.
/// </summary>
internal static class RosterFix
{
    private const string AddedSuffix = "_8P";
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

            CorrectPlayerCount(m, count);
            CompactSeatIndices(m, count);
            TrimSeatList(m, count);
            TrimNameplates(m, count);

            if (count != _lastLogged)
            {
                _lastLogged = count;
                Plugin.Log.LogInfo(
                    $"[roster] {count} players | StartPlayerCount={m.StartPlayerCount} " +
                    $"Slots={m.Slots?.Count} NameTexts={m.NameTexts?.Count}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[roster] fix failed: {e.Message}");
        }
    }

    private static void CorrectPlayerCount(Manager m, int count)
    {
        if (m.StartPlayerCount == count) return;
        Plugin.Log.LogWarning(
            $"[roster] StartPlayerCount {m.StartPlayerCount} -> {count} " +
            "(it lags because Manager.StartGame throws; dealing loops over it)");
        m.StartPlayerCount = count;
    }

    /// <summary>Renumber seats to 0..n-1, keeping the players' order around the table.</summary>
    private static void CompactSeatIndices(Manager m, int count)
    {
        var order = new System.Collections.Generic.List<PlayerStats>();
        for (int i = 0; i < count; i++)
            if (m.Players[i] != null) order.Add(m.Players[i]);

        order.Sort((a, b) => a.Slot.CompareTo(b.Slot));

        var before = new System.Text.StringBuilder();
        bool changed = false;

        for (int i = 0; i < order.Count; i++)
        {
            before.Append($" {order[i].Slot}");
            if (order[i].Slot != i) { order[i].Slot = i; changed = true; }
        }

        if (changed)
            Plugin.Log.LogWarning($"[roster] seat indices{before} -> 0..{order.Count - 1}");
    }

    /// <summary>
    /// Trim the seat list to the players present, so nothing walking it can land on an
    /// empty seat. Only seats this mod added are removed, and they are deactivated rather
    /// than destroyed so a bigger round can grow again.
    /// </summary>
    private static void TrimSeatList(Manager m, int count)
    {
        var slots = m.Slots;
        if (slots == null || count < 4 || slots.Count <= count) return;

        int removed = 0;
        while (slots.Count > count)
        {
            int last = slots.Count - 1;
            var t = slots[last];

            // never remove a seat the game shipped with
            if (t != null && !t.gameObject.name.EndsWith(AddedSuffix)) break;

            slots.RemoveAt(last);
            if (t != null) t.gameObject.SetActive(false);
            removed++;
        }

        if (removed > 0)
            Plugin.Log.LogWarning(
                $"[roster] trimmed {removed} unused seat(s); Slots now {slots.Count} for {count} players " +
                "(spare seats make turn order land on empty chairs)");
    }

    private static void TrimNameplates(Manager m, int count)
    {
        var texts = m.NameTexts;
        if (texts == null || count < 4 || texts.Count <= count) return;

        int removed = 0;
        while (texts.Count > count)
        {
            int last = texts.Count - 1;
            var t = texts[last];
            if (t != null && !t.gameObject.name.EndsWith(AddedSuffix)) break;

            texts.RemoveAt(last);
            if (t != null) t.gameObject.SetActive(false);
            removed++;
        }

        if (removed > 0)
            Plugin.Log.LogInfo($"[roster] hid {removed} unused nameplate(s); NameTexts now {texts.Count}");
    }
}
