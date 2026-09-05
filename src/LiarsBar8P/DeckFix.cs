using System;
using HarmonyLib;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic.List<UnityEngine.GameObject>;

namespace LiarsBar8P;

/// <summary>
/// Dealing runs off the end of several collections above four players:
///
///   ArgumentOutOfRangeException at DeckGamePlayManager.DealBasicOrDevil()
///
/// Measured live with five players:
///   MasaCards=20  ResetCards=20  OpenCards=4  ExtraCards=2
///
/// Two different shapes are short. The card pools (MasaCards, ResetCards) are sized for
/// the deck - four players times five cards - and need players*5. OpenCards holds one
/// entry per player and needs one per seat.
///
/// Cards are grown from ExtraCards first, since those are spare objects the game already
/// owns. Cloning is the fallback and only runs after confirming at runtime that the card
/// carries no NetworkIdentity: duplicating networked scene objects is what corrupted
/// spawn handling and disconnected everyone earlier. A live run confirmed cards are
/// plain objects, so the clone path is safe here - unlike the lobby panels.
/// </summary>
internal static class DeckFix
{
    private const int CardsPerPlayer = 5;
    private static bool _networkedWarned;

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

    /// <summary>
    /// Grows one list to <paramref name="need"/>, spares first and cloning second.
    /// Returns how many entries were added.
    /// </summary>
    private static int Grow(Il2CppList list, Il2CppList spares, int need, string label)
    {
        if (list == null) { Plugin.Log.LogWarning($"[deckfix] {label} is null"); return 0; }
        if (list.Count >= need) return 0;

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
            var sample = list.Count > 0 ? list[0] : null;
            if (!IsSafeToClone(sample))
            {
                if (!_networkedWarned)
                {
                    _networkedWarned = true;
                    Plugin.Log.LogError(
                        $"[deckfix] {label} carries a NetworkIdentity - refusing to duplicate it");
                }
                return list.Count - start;
            }

            int guard = 0;
            while (list.Count < need && guard++ < 64)
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

        int added = list.Count - start;
        if (added > 0) Plugin.Log.LogInfo($"[deckfix]   {label}: {start} -> {list.Count}");
        return added;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void TopUp(DeckGamePlayManager __instance, bool first)
    {
        try
        {
            int players = PlayerCount();
            if (players <= 4) return;

            int need = players * CardsPerPlayer;
            var spares = __instance.ExtraCards;

            Plugin.Log.LogInfo(
                $"[deckfix] players={players} need={need} " +
                $"MasaCards={(__instance.MasaCards == null ? -1 : __instance.MasaCards.Count)} " +
                $"ResetCards={(__instance.ResetCards == null ? -1 : __instance.ResetCards.Count)} " +
                $"OpenCards={(__instance.OpenCards == null ? -1 : __instance.OpenCards.Count)} " +
                $"ExtraCards={(spares == null ? -1 : spares.Count)}");

            // card pools: one card per dealt card
            Grow(__instance.ResetCards, spares, need, "ResetCards");
            Grow(__instance.MasaCards, spares, need, "MasaCards");

            // per-player: one entry per seat
            Grow(__instance.OpenCards, spares, players, "OpenCards");

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
