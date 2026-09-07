using System;
using System.Text;
using HarmonyLib;
using Steamworks;

namespace LiarsBar8P;

/// <summary>
/// Version drift between players has broken several sessions: a client on an old build
/// duplicates Mirror scene objects and runs a four seat table while everyone else has
/// eight, corrupting shared state for the whole lobby. Finding it meant reading each
/// person's log by hand.
///
/// Each client publishes its mod version as Steam lobby member data, and *every* peer audits
/// every member - not just the host. That is deliberate: a client on the wrong build is the
/// one who most needs telling, and a host-only check would leave them waiting to be told over
/// voice chat. It also means each loopback copy reports independently, which is the only
/// per-instance version evidence those logs carry.
///
/// Steam member data rather than a Mirror message is deliberate: it works between mismatched
/// builds, which is exactly when it is needed. Clients on a build predating this check
/// publish nothing and are reported as unknown, which is the answer anyway.
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
            SteamMatchmaking.SetLobbyMemberData(
                new CSteamID(callback.m_ulSteamIDLobby), Key, Plugin.Version);
            Plugin.Log.LogInfo($"[version] published mod version {Plugin.Version}");
        }
        catch (Exception e) { Plugin.Log.LogError($"[version] publish failed: {e.Message}"); }
    }

    /// <summary>
    /// Drop the mismatch banner once the session it was raised for is over.
    ///
    /// The audit below only runs from <c>LobbyController.Update</c>, so the moment a match
    /// starts there is nothing left to clear it. A warning raised in the lobby - naming a
    /// player by name - then stayed burned across the top of every peer's screen for the
    /// whole match and was still there back at the main menu, naming somebody who had left.
    ///
    /// Deliberately not cleared merely because the lobby has gone: a version mismatch is at
    /// its most dangerous during the match, which is exactly when the lobby is not there.
    /// It goes when there is neither a lobby nor a match left to warn about.
    /// </summary>
    internal static void ForgetWhenSessionEnds()
    {
        if (string.IsNullOrEmpty(VersionHud.Mismatch)) return;
        try
        {
            if (Manager.Instance != null) return;                 // still playing
            if (LobbyController.Instance != null) return;         // still in a lobby

            var sl = SteamLobby.Instance;
            if (sl != null && sl.CurrentLobbyID != 0) return;

            VersionHud.Mismatch = null;
            Plugin.Log.LogInfo("[version] session over - the mismatch banner is cleared");
        }
        catch { }
    }

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
            if (members <= 1) { VersionHud.Mismatch = null; return; }

            int mismatched = 0;
            var report = new StringBuilder();

            for (int i = 0; i < members; i++)
            {
                var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                string name = SteamFriends.GetFriendPersonaName(member);
                string ver = SteamMatchmaking.GetLobbyMemberData(lobby, member, Key);
                if (string.IsNullOrEmpty(ver)) ver = "OLD or NO MOD";
                if (ver != Plugin.Version)
                {
                    mismatched++;
                    report.Append($"  {name}={ver}");
                }
            }

            if (mismatched > 0)
            {
                Plugin.Log.LogWarning(
                    $"[version] {mismatched} player(s) NOT on {Plugin.Version}:{report} " +
                    "- they must reinstall or the session will misbehave");
                VersionHud.Mismatch = $"VERSION MISMATCH -{report}";
            }
            else
            {
                VersionHud.Mismatch = null;
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"[version] audit failed: {e.Message}"); }
    }
}
