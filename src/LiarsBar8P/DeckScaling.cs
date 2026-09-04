using System.Collections.Generic;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Liar's Deck deals five cards each from a deck that, at four players, is consumed
/// exactly. More players therefore need a bigger deck.
///
/// `DeckGamePlayManager.AddCards(List&lt;int&gt;)` receives the fully composed deck, so the
/// whole deck is rebuilt here rather than patching the index maths inside the game.
/// The composition is derived from whatever the game actually passed in, so the card
/// type encoding never has to be hardcoded and the vanilla ratios are preserved
/// exactly (at 8 players a 6/6/6/2 deck becomes 12/12/12/4).
/// </summary>
internal static class DeckScaling
{
    private const int CardsPerPlayer = 5;
    private const int VanillaPlayers = 4;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.AddCards))]
    private static void AddCards_Prefix(Il2CppSystem.Collections.Generic.List<int> types)
    {
        try
        {
            if (types == null) return;

            var counts = Tally(types, out int total);
            Plugin.Log.LogInfo($"[deck] AddCards received {total} cards :: {Describe(counts)}");

            if (!Plugin.ScaleDeck.Value) return;

            int players = CurrentPlayerCount();
            if (players <= VanillaPlayers)
            {
                Plugin.Log.LogInfo($"[deck] {players} players - leaving deck at vanilla size");
                return;
            }

            int target = players * CardsPerPlayer;
            if (total <= 0 || target <= total)
            {
                Plugin.Log.LogInfo($"[deck] no scaling needed (total={total}, target={target})");
                return;
            }

            var scaled = Scale(counts, total, target);
            types.Clear();
            foreach (var kv in scaled)
                for (int i = 0; i < kv.Value; i++)
                    types.Add(kv.Key);

            Plugin.Log.LogInfo(
                $"[deck] scaled {total} -> {types.Count} for {players} players :: {Describe(scaled)}");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[deck] scaling failed, deck left untouched: {e}");
        }
    }

    private static Dictionary<int, int> Tally(Il2CppSystem.Collections.Generic.List<int> types, out int total)
    {
        var counts = new Dictionary<int, int>();
        total = types.Count;
        for (int i = 0; i < types.Count; i++)
        {
            int t = types[i];
            counts.TryGetValue(t, out int c);
            counts[t] = c + 1;
        }
        return counts;
    }

    /// <summary>
    /// Scale each card type by target/total, then hand out any rounding remainder to
    /// the types with the largest fractional loss so the deck lands on exactly
    /// <paramref name="target"/> cards and the ratios stay as close as possible.
    /// </summary>
    private static Dictionary<int, int> Scale(Dictionary<int, int> counts, int total, int target)
    {
        var result = new Dictionary<int, int>();
        var remainder = new List<KeyValuePair<int, double>>();
        double factor = (double)target / total;
        int assigned = 0;

        foreach (var kv in counts)
        {
            double exact = kv.Value * factor;
            int whole = (int)exact;
            if (whole < 1) whole = 1;           // never let a card type vanish
            result[kv.Key] = whole;
            assigned += whole;
            remainder.Add(new KeyValuePair<int, double>(kv.Key, exact - whole));
        }

        remainder.Sort((a, b) => b.Value.CompareTo(a.Value));

        int idx = 0;
        while (assigned < target && remainder.Count > 0)
        {
            int key = remainder[idx % remainder.Count].Key;
            result[key]++;
            assigned++;
            idx++;
        }
        // if rounding overshot, trim from the largest stacks
        while (assigned > target)
        {
            int best = -1, bestCount = 0;
            foreach (var kv in result)
                if (kv.Value > bestCount) { bestCount = kv.Value; best = kv.Key; }
            if (best < 0 || result[best] <= 1) break;
            result[best]--;
            assigned--;
        }

        return result;
    }

    private static int CurrentPlayerCount()
    {
        try
        {
            var m = Manager.Instance;
            if (m != null)
            {
                if (m.StartPlayerCount > 0) return m.StartPlayerCount;
                if (m.Players != null && m.Players.Count > 0) return m.Players.Count;
            }
        }
        catch { /* fall through */ }
        return VanillaPlayers;
    }

    private static string Describe(Dictionary<int, int> counts)
    {
        var parts = new List<string>();
        var keys = new List<int>(counts.Keys);
        keys.Sort();
        foreach (var k in keys) parts.Add($"type{k}x{counts[k]}");
        return string.Join(" ", parts);
    }
}
