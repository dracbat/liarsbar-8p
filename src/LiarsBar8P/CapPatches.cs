using HarmonyLib;
using Steamworks;

namespace LiarsBar8P;

/// <summary>
/// Raises the two hard player caps: the Steam lobby member limit and Mirror's
/// connection limit. These are the only caps that must be lifted before more
/// than four people can occupy a lobby at all.
/// </summary>
internal static class CapPatches
{
    private static int Target => Plugin.MaxPlayers.Value;

    // --- Steam lobby member limit -------------------------------------------------
    // SteamMatchmaking.CreateLobby(ELobbyType, int cMaxMembers)

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.CreateLobby))]
    private static void CreateLobby_Prefix(ref int cMaxMembers)
    {
        if (cMaxMembers < Target)
        {
            Plugin.Log.LogInfo($"[cap] CreateLobby maxMembers {cMaxMembers} -> {Target}");
            cMaxMembers = Target;
        }
    }

    // SteamMatchmaking.SetLobbyMemberLimit(CSteamID, int cMaxMembers)
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.SetLobbyMemberLimit))]
    private static void SetLobbyMemberLimit_Prefix(ref int cMaxMembers)
    {
        if (cMaxMembers < Target)
        {
            Plugin.Log.LogInfo($"[cap] SetLobbyMemberLimit {cMaxMembers} -> {Target}");
            cMaxMembers = Target;
        }
    }

    // --- Mirror connection limit --------------------------------------------------
    // CustomNetworkManager inherits NetworkManager.maxConnections. It is a serialized
    // inspector field, so it is re-applied every time the manager starts.

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.Start))]
    private static void NetworkManager_Start_Postfix(CustomNetworkManager __instance)
    {
        if (__instance == null) return;
        var before = __instance.maxConnections;
        if (before < Target)
        {
            __instance.maxConnections = Target;
            Plugin.Log.LogInfo($"[cap] maxConnections {before} -> {__instance.maxConnections}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.HostLobbyServer))]
    private static void HostLobbyServer_Postfix(CustomNetworkManager __instance)
    {
        if (__instance == null) return;
        if (__instance.maxConnections < Target)
        {
            __instance.maxConnections = Target;
            Plugin.Log.LogInfo($"[cap] maxConnections re-applied at host time -> {Target}");
        }
    }
}
