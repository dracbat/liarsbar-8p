using System;
using HarmonyLib;
using Steamworks;

namespace LiarsBar8P;

/// <summary>
/// A sixth player was refused without the host ever seeing a connection attempt - no
/// "incoming connId=5" reached the server - so the block is client side, before Mirror.
/// Steam reports the lobby limit as 8, so it is the game's own join path refusing.
///
/// This instruments that path on the joining client and clears the game's own join lock
/// while the lobby genuinely has room.
/// </summary>
internal static class JoinDiag
{
    private static int Target => Plugin.MaxPlayers.Value;

    private static string LobbyState(ulong id)
    {
        try
        {
            if (id == 0) return "no lobby id";
            var cs = new CSteamID(id);
            return $"members={SteamMatchmaking.GetNumLobbyMembers(cs)}/" +
                   $"{SteamMatchmaking.GetLobbyMemberLimit(cs)}";
        }
        catch (Exception e) { return $"<{e.Message}>"; }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.JoinLobby))]
    private static void JoinLobby_Prefix(SteamLobby __instance, CSteamID lobbyID)
    {
        try
        {
            Plugin.Log.LogInfo(
                $"[joindiag] JoinLobby({lobbyID.m_SteamID}) {LobbyState(lobbyID.m_SteamID)} " +
                $"JoinLocked={__instance.JoinLocked}");

            if (__instance.JoinLocked)
            {
                __instance.JoinLocked = false;
                Plugin.Log.LogWarning("[joindiag] JoinLocked was set - cleared so the join can proceed");
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"[joindiag] {e.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyEntered))]
    private static void OnLobbyEntered_Postfix(SteamLobby __instance, LobbyEnter_t callback)
    {
        try
        {
            Plugin.Log.LogInfo(
                $"[joindiag] entered lobby {callback.m_ulSteamIDLobby} " +
                $"response={callback.m_EChatRoomEnterResponse} " +
                $"{LobbyState(callback.m_ulSteamIDLobby)}");
        }
        catch (Exception e) { Plugin.Log.LogError($"[joindiag] {e.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnJoinRequest))]
    private static void OnJoinRequest_Postfix(GameLobbyJoinRequested_t callback)
    {
        try
        {
            Plugin.Log.LogInfo(
                $"[joindiag] join requested for {callback.m_steamIDLobby.m_SteamID} " +
                $"{LobbyState(callback.m_steamIDLobby.m_SteamID)}");
        }
        catch { }
    }

    /// <summary>
    /// StartPlayerCount stays at 4 with five players because Manager.StartGame throws.
    /// Turn order then skips the extra player. Correct it once the roster is known.
    /// </summary>
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Manager), nameof(Manager.StartGame))]
    private static Exception StartGame_Finalizer(Exception __exception, Manager __instance)
    {
        try
        {
            int actual = __instance.Players != null ? __instance.Players.Count : 0;
            if (actual > 0 && __instance.StartPlayerCount != actual)
            {
                Plugin.Log.LogWarning(
                    $"[joindiag] StartPlayerCount {__instance.StartPlayerCount} -> {actual} " +
                    "(it lags because StartGame threw; turn order skips players otherwise)");
                __instance.StartPlayerCount = actual;
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"[joindiag] {e.Message}"); }
        return __exception;
    }
}
