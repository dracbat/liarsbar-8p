using HarmonyLib;
using Steamworks;

namespace LiarsBar8P;

/// <summary>
/// Development-only self test. Hosts a lobby automatically so the lobby-side
/// caps and slot counts can be observed without a second person present.
/// Forced private so the test lobby is never publicly joinable.
/// Off by default; only meaningful while building the mod out.
/// </summary>
internal static class DiagAutoHost
{
    private static int _frames;
    private static bool _fired;

    internal static bool Active => Plugin.DiagAutoHost != null && Plugin.DiagAutoHost.Value;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.Update))]
    private static void SteamLobby_Update_Postfix(SteamLobby __instance)
    {
        if (!Active || _fired) return;

        // Counting frames was not enough: the menu can be running before the Steam client
        // has answered, and CreateLobby then throws "Steamworks is not initialized". Wait
        // for the game's own flag, then a short settle so the lobby UI exists.
        bool steamReady;
        try { steamReady = SteamManager.Initialized; }
        catch { steamReady = false; }
        if (!steamReady) { _frames = 0; return; }
        if (++_frames < 120) return;

        _fired = true;
        try
        {
            Plugin.Log.LogInfo("[selftest] auto-hosting a PRIVATE lobby for diagnostics...");
            __instance.HostLobby();
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[selftest] HostLobby failed: {e}");
        }
    }

    /// <summary>
    /// While the self test is running, force the lobby private so an automated
    /// diagnostic run cannot put a joinable lobby in front of other players.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.CreateLobby))]
    private static void ForcePrivateDuringSelfTest(ref ELobbyType eLobbyType)
    {
        if (!Active) return;
        if (eLobbyType != ELobbyType.k_ELobbyTypePrivate)
        {
            Plugin.Log.LogInfo($"[selftest] forcing lobby type {eLobbyType} -> Private");
            eLobbyType = ELobbyType.k_ELobbyTypePrivate;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyCreated))]
    private static void OnLobbyCreated_Postfix(SteamLobby __instance, LobbyCreated_t callback)
    {
        try
        {
            Plugin.Log.LogInfo($"[lobby] created result={callback.m_eResult} id={callback.m_ulSteamIDLobby}");
            var id = new CSteamID(callback.m_ulSteamIDLobby);
            Plugin.Log.LogInfo($"[lobby] member limit reported by Steam = {SteamMatchmaking.GetLobbyMemberLimit(id)}");
            Plugin.Log.LogInfo($"[lobby] current members = {SteamMatchmaking.GetNumLobbyMembers(id)}");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[lobby] diag failed: {e.Message}");
        }
    }
}
