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
    /// <summary>When the deal's animation chain last started. Zero means it is not running.</summary>
    private static float _dealStartedAt;

    /// <summary>
    /// Whether the deal is still handing out cards.
    ///
    /// The turn watchdog needs this. It starts a turn when every player is holding cards
    /// and nobody has the turn, and for a table of bots that test passes far too early:
    /// this mod puts the cards straight into a bot's hands itself, because a bot has no
    /// connection to be dealt over, so everyone is "holding" while the game is still
    /// dealing. The watchdog then started the round early, the bots played a full lap, and
    /// the real deal finished afterwards and handed the first turn back to seat 0 - so a
    /// lap of play was thrown away and the table went round twice from the same seat.
    ///
    /// The timeout is a backstop: if a deal ever fails to reach its end this must not
    /// disable the watchdog for the rest of the match, which would trade a rare stall for
    /// a permanent one.
    /// </summary>
    internal static bool Dealing =>
        _dealStartedAt > 0f && UnityEngine.Time.time - _dealStartedAt < 45f;

    /// <summary>
    /// The window opens when the round is reset, not when the dealing animation starts.
    ///
    /// There is a gap of ten seconds or so between the two, and the card *values* are
    /// handed out at the start of it - which is enough for this mod to fill a bot's hands
    /// and for everyone to look ready. The watchdog fired in that gap, four seconds in,
    /// long before there was any dealing animation to notice.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ResetRound))]
    private static void RoundReset(bool first)
    {
        _dealStartedAt = UnityEngine.Time.time;
        Plugin.Log.LogInfo($"[dealtrace] round reset (first={first}) - dealing, turn watchdog stands aside");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.ShowCardRound))]
    private static void ShowCardRoundCalled(bool first)
    {
        _dealStartedAt = UnityEngine.Time.time;
        Plugin.Log.LogInfo($"[dealtrace] ShowCardRound(first={first}) was created");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.GiveCardsVisualRoutine))]
    private static void GiveCardsCalled()
    {
        _dealStartedAt = UnityEngine.Time.time;
        Plugin.Log.LogInfo("[dealtrace] GiveCardsVisualRoutine was created");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.GiveTurnAfterAllCardsDealt))]
    private static void FirstTurnReached()
    {
        _dealStartedAt = 0f;
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
