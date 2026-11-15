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

    // --- The lobby's own idea of a full table ----------------------------------------
    // Since the game's 14 Nov 2026 update the lobby asks CustomNetworkManager.LobbyMaxPlayers
    // (4 at a table, 10 in the Arena) for its Steam member limit and, every frame, for whether
    // to show the host's START button: more players than that and the button goes away, so a
    // table of five or more could never start. Tables get the mod's limit; the Arena keeps its own.

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.LobbyMaxPlayers), MethodType.Getter)]
    private static void LobbyMaxPlayers_Postfix(CustomNetworkManager __instance, ref int __result)
    {
        try
        {
            if (__result < Target && __instance != null && __instance.Mode != CustomNetworkManager.GameMode.Arena)
                __result = Target;
        }
        catch { }
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
