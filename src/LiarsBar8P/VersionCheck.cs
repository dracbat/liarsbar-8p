using System;
using HarmonyLib;
using Steamworks;

namespace LiarsBar8P;

/// <summary>
/// Version drift between players has broken several sessions: a client still running an
/// old build duplicates Mirror scene objects and runs a four seat table while everyone
/// else has eight, corrupting shared state for the whole lobby. Spotting it meant
/// reading each person's log by hand.
///
/// Each client now publishes its mod version as Steam lobby member data, and the host
/// reads every member's entry. No Mirror messages are involved, so this works even
/// between mismatched builds - which is exactly when it is needed. Players on a build
/// predating this check publish nothing, and are reported as unknown, which is itself
/// the answer.
/// </summary>
internal static class VersionCheck
{
    private const string Key = "lb8p_ver";
    private static float _nextReport;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnLobbyEntered))]
    private static void Publish(LobbyEnter_t callback)
    {
        try
        {
            var lobby = new CSteamID(callback.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyMemberData(lobby, Key, Plugin.Version);
            Plugin.Log.LogInfo($"[version] published mod version {Plugin.Version} to the lobby");
        }
        catch (Exception e) { Plugin.Log.LogError($"[version] publish failed: {e.Message}"); }
    }

    /// <summary>Host-side audit: name anyone not on this exact build.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Update))]
    private static void Audit(LobbyController __instance)
    {
        try
        {
            if (UnityEngine.Time.realtimeSinceStartup < _nextReport) return;
            _nextReport = UnityEngine.Time.realtimeSinceStartup + 10f;

            var sl = SteamLobby.Instance;
            if (sl == null || sl.CurrentLobbyID == 0) return;

            var lobby = new CSteamID(sl.CurrentLobbyID);
            int members = SteamMatchmaking.GetNumLobbyMembers(lobby);
            if (members <= 1) return;

            int mismatched = 0;
            var report = new System.Text.StringBuilder();

            for (int i = 0; i < members; i++)
            {
                var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                string name = SteamFriends.GetFriendPersonaName(member);
                string ver = SteamMatchmaking.GetLobbyMemberData(lobby, member, Key);
                if (string.IsNullOrEmpty(ver)) ver = "OLD or NO MOD";
                if (ver != Plugin.Version) { mismatched++; report.Append($" | {name}={ver}"); }
            }

            if (mismatched > 0)
            {
                Plugin.Log.LogWarning(
                    $"[version] {mismatched} player(s) NOT on {Plugin.Version}{report} " +
                    "- they must reinstall or the session will misbehave");
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"[version] audit failed: {e.Message}"); }
    }
}
