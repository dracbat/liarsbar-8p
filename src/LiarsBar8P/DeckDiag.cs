using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Dealing fails above four players:
///
///   ArgumentOutOfRangeException: Index was out of range
///     at List`1.get_Item(Int32)
///     at DeckGamePlayManager.DealBasicOrDevil()
///     at DeckGamePlayManager.ResetRound(Boolean)
///
/// AddCards never runs in this path, so scaling hooked there never applied. Rather than
/// guess which collection is short, every candidate is measured as the round is set up,
/// and the deal is guarded so a throw cannot wedge the round.
/// </summary>
internal static class DeckDiag
{
    private static string Count(string label, Func<int> f)
    {
        try { return $"{label}={f()}"; } catch { return $"{label}=<err>"; }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void ResetRound_Prefix(DeckGamePlayManager __instance, bool first)
    {
        try
        {
            int players = -1;
            try { var m = Manager.Instance; if (m != null) players = m.Players != null ? m.Players.Count : -1; } catch { }
            int startCount = -1;
            try { var m = Manager.Instance; if (m != null) startCount = m.StartPlayerCount; } catch { }

            Plugin.Log.LogInfo(
                $"[deckdiag] ResetRound(first={first}) players={players} startCount={startCount} " +
                Count("MasaCards", () => __instance.MasaCards.Count) + " " +
                Count("ResetCards", () => __instance.ResetCards.Count) + " " +
                Count("OpenCards", () => __instance.OpenCards.Count) + " " +
                Count("ExtraCards", () => __instance.ExtraCards.Count) + " " +
                Count("CardIcons", () => __instance.CardIcons.Count) + " " +
                Count("CardIcons", () => __instance.CardIcons.Count) + " " +
                Count("OrderSprtes", () => __instance.OrderSprtes.Count) + " " +
                Count("LastRound", () => __instance.LastRound.Count) + " " +
                Count("LastRoundSpotOn", () => __instance.LastRoundSpotOn.Count) + " " +
                Count("devilsDealEffects", () => __instance.devilsDealEffects.Count) + " " +
                Count("cardsOnTable", () => __instance.CardsOnTable));
        }
        catch (Exception e) { Plugin.Log.LogError($"[deckdiag] probe failed: {e.Message}"); }
    }

    /// <summary>Report the deal's failure without letting it wedge the round.</summary>
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.DealBasicOrDevil))]
    private static Exception Deal_Finalizer(Exception __exception, DeckGamePlayManager __instance)
    {
        if (__exception == null) return null;
        Plugin.Log.LogWarning($"[deckdiag] DealBasicOrDevil threw: {__exception.Message}");
        Plugin.Log.LogWarning("[deckdiag]   " +
            Count("MasaCards", () => __instance.MasaCards.Count) + " " +
            Count("ResetCards", () => __instance.ResetCards.Count) + " " +
            Count("OpenCards", () => __instance.OpenCards.Count) + " " +
            Count("ExtraCards", () => __instance.ExtraCards.Count));
        return null;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Manager), nameof(Manager.StartGame))]
    private static Exception StartGame_Finalizer(Exception __exception, Manager __instance)
    {
        if (__exception == null) return null;
        try
        {
            Plugin.Log.LogWarning(
                $"[deckdiag] Manager.StartGame threw: {__exception.Message} | " +
                Count("Players", () => __instance.Players.Count) + " " +
                Count("Slots", () => __instance.Slots.Count) + " " +
                Count("NameTexts", () => __instance.NameTexts.Count) + " " +
                Count("StartPlayerCount", () => __instance.StartPlayerCount));
        }
        catch { }
        return null;
    }
}
