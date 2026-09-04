using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Development self test only. The in-game seat ring, per-mode character objects and
/// nameplate list live in the gameplay scene, which normally needs a full lobby to
/// reach. The developers left a solo-test hook (`EditorSoloDeckTest`); forcing it lets
/// a match start alone so those runtime values can be observed.
///
/// Never enabled during normal play.
/// </summary>
internal static class DiagSoloStart
{
    private static bool Active => Plugin.DiagSoloStart != null && Plugin.DiagSoloStart.Value;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.EditorSoloDeckTest))]
    private static void ForceSoloDeckTest(ref bool __result)
    {
        if (!Active) return;
        if (!__result)
        {
            __result = true;
            Plugin.Log.LogInfo("[selftest] EditorSoloDeckTest forced true");
        }
    }

    private static int _frames;
    private static bool _fired;

    /// <summary>Kick the match off a few seconds after the lobby settles.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Update))]
    private static void TryStart(LobbyController __instance)
    {
        if (!Active || _fired) return;
        if (++_frames < 420) return;

        _fired = true;
        try
        {
            Plugin.Log.LogInfo("[selftest] attempting solo match start...");
            __instance.StartGame();
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[selftest] StartGame failed: {e.Message}");
        }
    }
}
