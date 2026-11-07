using System;
using System.Collections.Generic;
using System.Globalization;
using Steamworks;

namespace LiarsBar8P;

/// <summary>
/// Everyone at a table of the mod's pool runs exactly the same build of the mod. Always on: there
/// is no setting and no switch.
///
/// <b>The host.</b> Start stays unlit while any player seated at the table this copy hosts shows
/// a mod version that is not this build's. A seated player is one with a connection to this
/// host, the connection the host's kick reaches; bots never count. It is one more hold reason in
/// <see cref="Matchmaking"/>'s lobby upkeep, the same one that already waits for somebody
/// proving the mod, so Start lights again by itself the moment the last of them matches or
/// leaves. The host's lobby label says how many it is waiting for, and the room is told once, in
/// the game's own chat, each time the count goes up.
///
/// Somebody who shows no version at all is the no-mod rule's to hold and send back. Somebody in
/// the Steam lobby who is not seated yet is the arriving wait's; once seated, they count here
/// until they match or leave.
///
/// <b>A joiner.</b> A copy entering a mod table whose host shows another version leaves it at
/// once, with a line on screen (see <c>Matchmaking.LeaveOtherBuilds</c>). The host's version is
/// the lobby data it writes when it creates the lobby, <see cref="VersionCheck.Key"/>, and if
/// that is missing, the host's own member data under the same key.
///
/// Nothing here touches ranked, the game's own lobbies, bots, or the updater. The
/// loopback harness has no Steam lobby and every copy there runs this build, so it counts as all
/// the same; a developer test there can pretend one connection shows another version.
/// </summary>
internal static class SameVersion
{
    /// <summary>To the whole room, in the game's own chat. English, like the no-mod line beside it: it reaches every copy in the room, whatever their language.</summary>
    internal const string ToTheRoom = "Everyone needs the same version of " + Plugin.DisplayName + " to play";

    /// <summary>On a joiner's screen as it leaves.</summary>
    internal const string OtherTable = "This table runs a different version of " + Plugin.DisplayName;

    // On the host's lobby label while Start waits: how many. Short, to fit the label in every language.
    private const string Held = "{0} on another mod version";

    private static int _told;
    private static bool _registered;
    private static readonly HashSet<ulong> _counted = new();

    /// <summary>Connections a developer test pretends show another version. Honoured only in the loopback harness.</summary>
    private static readonly HashSet<int> _pretend = new();

    /// <summary>How many the host's last count found on another version.</summary>
    internal static int Behind { get; private set; }

    /// <summary>How many times the room has been told. For the test.</summary>
    private static int _tellings;

    private static bool Pretending => Loopback.Active && Dev.Enabled && _pretend.Count > 0;

    // ------------------------------------------------------------------ the host

    /// <summary>
    /// How many players seated at the table this copy hosts show a mod version that is not this
    /// build's, each found by the Steam id their connection holds; this copy's own seat and bots
    /// are skipped. With no Steam lobby (the loopback harness) only the test's pretend list can
    /// count.
    /// </summary>
    internal static int Count(CSteamID lobby, CustomNetworkManager nm)
    {
        bool steam = lobby.m_SteamID != 0 && !Loopback.Active;
        int behind = 0;
        _counted.Clear();

        var players = nm != null ? nm.GamePlayers : null;
        if (players != null)
        {
            foreach (var p in players)
            {
                if (p == null || p.isOwned || BotManager.IsBot(p)) continue;

                var conn = p.connectionToClient;
                if (conn == null) continue;

                if (Pretending && _pretend.Contains(conn.connectionId)) { behind++; continue; }
                if (!steam) continue;

                if (!ulong.TryParse(conn.address, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0) continue;
                if (_counted.Add(id) && Other(lobby, new CSteamID(id))) behind++;
            }
        }

        return behind;
    }

    /// <summary>This member shows a mod version, and it is not this build's.</summary>
    private static bool Other(CSteamID lobby, CSteamID member)
    {
        string theirs = SteamMatchmaking.GetLobbyMemberData(lobby, member, VersionCheck.Key);
        return !string.IsNullOrEmpty(theirs) && !string.Equals(theirs, Plugin.Version, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hold reason for this count, or null when nobody is on another version. Tells the room
    /// once each time the count goes up, and logs each change as a count.
    /// </summary>
    internal static string Hold(int behind)
    {
        if (behind != Behind)
        {
            Plugin.Log.LogInfo(behind > 0
                ? $"[matchmaking] {behind} player(s) in the lobby show another mod version - Start waits"
                : "[matchmaking] everyone in the lobby shows this mod version - Start no longer waits for it");
        }

        if (behind > _told)
        {
            Matchmaking.TellTheRoom(ToTheRoom);
            _tellings++;
        }

        _told = behind;
        Behind = behind;
        return behind > 0 ? Words.F(Held, behind) : null;
    }

    /// <summary>The hosted lobby is gone.</summary>
    internal static void Forget()
    {
        _told = 0;
        Behind = 0;
    }

    // ------------------------------------------------------------------ a joiner

    /// <summary>
    /// Is this a mod table, hosted by somebody else, whose host does not show this build? A mod
    /// table is a lobby of the mod's pool, or a lobby whose host shows a mod version in its own
    /// member data: releases from before the pool publish that, and the game never does. Any
    /// other lobby is the game's, and the answer there is no. <paramref name="theirs"/> is what
    /// the host shows, or null if it shows nothing.
    /// </summary>
    internal static bool HostRunsAnother(CSteamID lobby, out string theirs)
    {
        theirs = null;

        ulong owner = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;
        if (owner == SteamUser.GetSteamID().m_SteamID) return false;

        string tag = SteamMatchmaking.GetLobbyData(lobby, Matchmaking.VersionKey);
        bool pool = !string.IsNullOrEmpty(tag) && tag.EndsWith(Matchmaking.PoolSuffix, StringComparison.Ordinal);

        if (pool) theirs = SteamMatchmaking.GetLobbyData(lobby, VersionCheck.Key);
        if (string.IsNullOrEmpty(theirs) && owner != 0)
            theirs = SteamMatchmaking.GetLobbyMemberData(lobby, new CSteamID(owner), VersionCheck.Key);
        if (string.IsNullOrEmpty(theirs)) theirs = null;

        if (!pool && theirs == null) return false;
        return !string.Equals(theirs, Plugin.Version, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ developer test

    /// <summary>From <see cref="Matchmaking.Tick"/>, on every copy. Registers the test once.</summary>
    internal static void Register()
    {
        if (_registered) return;
        _registered = true;
        Fair.OnDrive("version", Test);
    }

    /// <summary>
    /// Developer tests, inside the loopback harness only (<see cref="Fair.TestsAllowed"/>):
    /// "fair version" reports the host's count; "fair version other [connection]" pretends that
    /// connection shows another version and checks the hold and the one line to the room; "fair
    /// version same" takes the pretence away and checks the hold has gone. Host side only:
    /// nothing is sent to anybody but the chat line the rule itself sends.
    /// </summary>
    private static void Test(string args)
    {
        const string Name = "version";
        if (!Fair.Hosting) { Fair.Result(Name, false, $"not hosting ({Fair.Why})"); return; }

        var nm = Fair.Net;
        CSteamID lobby = default;
        if (!Loopback.Active) Table.Hosted(out lobby);

        string[] parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string what = parts.Length > 0 ? parts[0] : "";

        int bots = 0;
        if (nm != null && nm.GamePlayers != null)
            foreach (var p in nm.GamePlayers)
                if (p != null && BotManager.IsBot(p)) bots++;

        if (what == "other" || what == "same")
        {
            if (!Loopback.Active) { Fair.Result(Name, false, "the pretend switch only works in the loopback harness"); return; }

            if (what == "other")
            {
                var conn = Fair.TestConnection(parts.Length > 1 ? parts[1] : "");
                if (conn == null) { Fair.Result(Name, false, "no remote connection to pretend with"); return; }
                _pretend.Add(conn.connectionId);

                int was = Behind, before = _tellings;
                string first = Hold(Count(lobby, nm));
                string second = Hold(Count(lobby, nm));
                int told = _tellings - before;
                bool pass = first != null && second != null && Behind == _pretend.Count && told == (Behind > was ? 1 : 0);
                Fair.Result(Name, pass, $"pretending connection {conn.connectionId}: behind={Behind} " +
                                        $"room told {told} time(s) over two passes, bots={bots} (never counted)");
                return;
            }

            _pretend.Clear();
            string hold = Hold(Count(lobby, nm));
            Fair.Result(Name, hold == null && Behind == 0, $"pretence cleared: behind={Behind}, bots={bots} (never counted)");
            return;
        }

        int behind = Count(lobby, nm);
        Fair.Result(Name, true, $"behind={behind} (last upkeep {Behind}), bots={bots} (never counted), " +
                                $"steam lobby={(lobby.m_SteamID != 0 ? "yes" : "no")}");
    }
}
