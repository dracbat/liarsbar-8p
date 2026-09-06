using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Says whether the deal's animation chain is even started.
///
/// The routine that shows the cards and gives out the first turn is a coroutine, and a
/// coroutine that never starts looks exactly like one that dies immediately: no cards, no
/// turn, nothing in the log either way.
///
/// This patches the ordinary methods that *create* those coroutines, never their MoveNext.
/// Patching a MoveNext was tried and Harmony could not do it - the attempt failed and left
/// the coroutine unusable, which stopped the deal outright and looked for all the world
/// like a dealing bug. The outer methods are plain and safe to patch, and being called is
/// exactly the fact needed.
/// </summary>
internal static class DealTrace
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ShowCardRound))]
    private static void ShowCardRoundCalled(bool first)
    {
        Plugin.Log.LogInfo($"[dealtrace] ShowCardRound(first={first}) was created");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.GiveCardsVisualRoutine))]
    private static void GiveCardsCalled()
    {
        Plugin.Log.LogInfo("[dealtrace] GiveCardsVisualRoutine was created");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.GiveTurnAfterAllCardsDealt))]
    private static void FirstTurnReached()
    {
        Plugin.Log.LogInfo("[dealtrace] the deal reached the end and gave out the first turn");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.SetHaveCards))]
    private static void HandShown(DeckGameplay __instance, bool value)
    {
        try
        {
            var stats = __instance.GetComponent<PlayerStats>();
            string who = stats != null ? stats.PlayerName : "?";
            int objects = __instance.Cards != null ? __instance.Cards.Count : -1;
            Plugin.Log.LogInfo($"[dealtrace] '{who}' hand shown={value} ({objects} card objects)");
        }
        catch { }
    }
}
