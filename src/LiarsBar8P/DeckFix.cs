using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Il2CppGoList = Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject>;

namespace LiarsBar8P;

/// <summary>
/// Above four players the deal runs off the end of several collections:
///
///   ArgumentOutOfRangeException at DeckGamePlayManager.DealBasicOrDevil()
///
/// Two different shapes are involved. The card pools (MasaCards, ResetCards) hold the
/// deck itself; the rest hold one entry per player.
///
/// With more than four players the deck is simply **doubled** - two full decks. That
/// keeps the vanilla ratios exactly (6/6/6/2 becomes 12/12/12/4) and 40 cards covers
/// every size up to eight players at five cards each, so the composition never has to be
/// reasoned about per player count.
///
/// Spare objects from ExtraCards are used before anything is created. Cloning is the
/// fallback and only runs after confirming at runtime that the card carries no
/// NetworkIdentity - duplicating networked scene objects is what corrupted spawn
/// handling and disconnected everyone in earlier builds. A live run confirmed cards are
/// plain objects, unlike the lobby panels.
/// </summary>
internal static class DeckFix
{
    private const int CardsPerPlayer = 5;
    private static bool _networkedWarned;

    /// <summary>Sizes seen before any growth, so "double" always means double vanilla.</summary>
    private static readonly Dictionary<string, int> _vanilla = new();

    private static int Vanilla(string label, int current)
    {
        if (!_vanilla.TryGetValue(label, out int v)) { v = current; _vanilla[label] = v; }
        return v;
    }

    private static int PlayerCount()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) return 0;
            if (m.Players != null && m.Players.Count > 0) return m.Players.Count;
            if (m.StartPlayerCount > 0) return m.StartPlayerCount;
        }
        catch { }
        return 0;
    }

    private static bool IsSafeToClone(GameObject go)
    {
        try
        {
            if (go == null) return false;
            var ids = go.GetComponentsInChildren<Mirror.NetworkIdentity>(true);
            return ids == null || ids.Length == 0;
        }
        catch { return false; }
    }

    private static void Grow(Il2CppGoList list, Il2CppGoList spares, int need, string label)
    {
        try
        {
            if (list == null || list.Count >= need) return;
            int start = list.Count;

            while (list.Count < need && spares != null && spares.Count > 0)
            {
                var spare = spares[0];
                spares.RemoveAt(0);
                if (spare == null) continue;
                spare.SetActive(true);
                list.Add(spare);
            }

            if (list.Count < need)
            {
                if (!IsSafeToClone(list.Count > 0 ? list[0] : null))
                {
                    if (!_networkedWarned)
                    {
                        _networkedWarned = true;
                        Plugin.Log.LogError($"[deckfix] {label} is networked - refusing to duplicate");
                    }
                    return;
                }

                int guard = 0;
                while (list.Count < need && guard++ < 128)
                {
                    var src = list[list.Count % Mathf.Max(1, start)];
                    if (src == null) break;
                    bool wasActive = src.activeSelf;
                    src.SetActive(false);
                    var clone = UnityEngine.Object.Instantiate(src, src.transform.parent);
                    src.SetActive(wasActive);
                    clone.name = $"{src.name}_8P";
                    clone.SetActive(wasActive);
                    list.Add(clone);
                }
            }

            if (list.Count != start) Plugin.Log.LogInfo($"[deckfix]   {label}: {start} -> {list.Count}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[deckfix] {label} skipped: {e.Message}"); }
    }

    private static void GrowSprites(Il2CppSystem.Collections.Generic.List<Sprite> list, int need, string label)
    {
        try
        {
            if (list == null || list.Count == 0 || list.Count >= need) return;
            int start = list.Count;
            while (list.Count < need) list.Add(list[list.Count % start]);
            Plugin.Log.LogInfo($"[deckfix]   {label}: {start} -> {list.Count}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[deckfix] {label} skipped: {e.Message}"); }
    }

    private static void GrowSyncInts(Mirror.SyncList<int> list, int need, string label)
    {
        try
        {
            if (list == null || list.Count >= need) return;
            int start = list.Count;
            while (list.Count < need) list.Add(0);
            Plugin.Log.LogInfo($"[deckfix]   {label}: {start} -> {list.Count}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[deckfix] {label} skipped: {e.Message}"); }
    }

    /// <summary>
    /// Every collection on DeckGamePlayManager is now grown and the deal still indexes out
    /// of range, so the bad index is into something else. The likeliest candidate is a
    /// player holding a seat seat index beyond the roster: seats are expanded to eight
    /// while Players only holds the people present.
    /// </summary>
    private static void DumpSeats()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Players == null) return;

            var sb = new System.Text.StringBuilder();
            int worst = -1;
            for (int i = 0; i < m.Players.Count; i++)
            {
                var p = m.Players[i];
                if (p == null) { sb.Append($"  [{i}]=null"); continue; }
                sb.Append($"  [{i}] '{p.PlayerName}' slot={p.Slot} dead={p.Dead}");
                if (p.Slot > worst) worst = p.Slot;
            }

            Plugin.Log.LogInfo($"[deckfix] roster ({m.Players.Count} players, highest slot {worst}):{sb}");

            if (worst >= m.Players.Count)
                Plugin.Log.LogWarning(
                    $"[deckfix] a player holds slot {worst} but only {m.Players.Count} players exist - " +
                    "anything indexing Players by slot will run off the end");
        }
        catch (Exception e) { Plugin.Log.LogError($"[deckfix] roster dump failed: {e.Message}"); }
    }

    /// <summary>
    /// Shrink a per player list back if the table got smaller, so nothing walks an entry
    /// with no player behind it. Objects are deactivated, never destroyed.
    /// </summary>
    private static void TrimToPlayers(Il2CppGoList list, int players, string label)
    {
        try
        {
            if (list == null || players < 4 || list.Count <= players) return;
            int removed = 0;
            while (list.Count > players)
            {
                int last = list.Count - 1;
                var go = list[last];
                list.RemoveAt(last);
                if (go != null) go.SetActive(false);
                removed++;
            }
            Plugin.Log.LogInfo($"[deckfix]   {label} trimmed by {removed} -> {list.Count}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[deckfix] {label} trim skipped: {e.Message}"); }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void TopUp(DeckGamePlayManager __instance, bool first)
    {
        try
        {
            int players = PlayerCount();
            if (players <= 4) return;

            var spares = __instance.ExtraCards;

            int masaV = Vanilla("MasaCards", __instance.MasaCards?.Count ?? 0);
            int resetV = Vanilla("ResetCards", __instance.ResetCards?.Count ?? 0);

            // Vanilla holds exactly players * 5 cards - 20 for four players - and the deal
            // consumes the whole deck. Keeping that invariant matters more than a round
            // number: 40 cards for five players would leave 15 undealt, which the deal may
            // not expect. At eight players this is 40, exactly the two decks intended.
            int vanillaDeck = Mathf.Max(masaV, resetV);
            int deckTarget = Mathf.Max(vanillaDeck, players * CardsPerPlayer);
            CardTypeFix.NoteVanillaDeck(vanillaDeck);

            Plugin.Log.LogInfo(
                $"[deckfix] {players} players -> deck of {deckTarget} cards " +
                $"({CardsPerPlayer} each, vanilla was {vanillaDeck})");

            Grow(__instance.ResetCards, spares, deckTarget, "ResetCards");
            Grow(__instance.MasaCards, spares, deckTarget, "MasaCards");

            // Per player collections are grown to the configured maximum rather than the
            // current headcount. The deal still indexed out of range with every list
            // sized to the exact player count, so the offending index is chosen by some
            // scheme not yet identified - most likely a seat index, which can exceed the
            // roster because seats are expanded to the maximum. Over provisioning makes
            // any index in 0..max-1 valid whichever scheme it turns out to be; the cost
            // is a few unused entries.
            // Exactly one entry per player. Over provisioning was tried and is wrong for the
            // same reason spare seats are wrong: anything walking these lists would visit
            // entries with no player behind them. Seat indices are compacted to 0..n-1
            // before this runs, so no index can exceed the roster.
            int perPlayer = players;

            Grow(__instance.OpenCards, spares, perPlayer, "OpenCards");
            GrowSprites(__instance.OrderSprtes, perPlayer, "OrderSprtes");
            GrowSprites(__instance.CardIcons, perPlayer, "CardIcons");
            GrowSyncInts(__instance.LastRound, perPlayer, "LastRound");
            GrowSyncInts(__instance.LastRoundSpotOn, perPlayer, "LastRoundSpotOn");

            TrimToPlayers(__instance.OpenCards, players, "OpenCards");

            Plugin.Log.LogInfo(
                $"[deckfix] after: MasaCards={__instance.MasaCards.Count} " +
                $"ResetCards={__instance.ResetCards.Count} OpenCards={__instance.OpenCards.Count}");

            DumpSeats();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[deckfix] failed, lists left untouched: {e}");
        }
    }
}
