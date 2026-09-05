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

            // more than four players: two full decks
            int deckTarget = Mathf.Max(masaV, resetV) * 2;

            Plugin.Log.LogInfo(
                $"[deckfix] {players} players -> doubling the deck to {deckTarget} cards " +
                $"(vanilla {Mathf.Max(masaV, resetV)})");

            Grow(__instance.ResetCards, spares, deckTarget, "ResetCards");
            Grow(__instance.MasaCards, spares, deckTarget, "MasaCards");

            // one entry per player
            Grow(__instance.OpenCards, spares, players, "OpenCards");
            GrowSprites(__instance.OrderSprtes, players, "OrderSprtes");
            GrowSyncInts(__instance.LastRound, players, "LastRound");
            GrowSyncInts(__instance.LastRoundSpotOn, players, "LastRoundSpotOn");

            Plugin.Log.LogInfo(
                $"[deckfix] after: MasaCards={__instance.MasaCards.Count} " +
                $"ResetCards={__instance.ResetCards.Count} OpenCards={__instance.OpenCards.Count}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[deckfix] failed, lists left untouched: {e}");
        }
    }
}
