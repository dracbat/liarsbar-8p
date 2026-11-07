using System;
using Mirror;
using Steamworks;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Whether this copy sits at a table where everybody runs the mod: the one question every
/// gameplay, table and lobby feature asks before it does anything. Ask it here rather than in a
/// way of your own, for the same reason <c>TableHand.Playing()</c> is the only place that decides
/// the mode.
///
/// It is asked as "on only at a modded table" and never as "off in ranked". Asked the second
/// way, a new official queue or scene would inherit every feature. Asked this way, anything the
/// mod does not recognise gets the game as it ships, so every doubt answers no: the ranked screen
/// or a ranked match, no Steam lobby, Steam not running, a lobby without the pool's tag (a vanilla
/// lobby reached with <c>ShowUnmoddedLobbies</c>, say), a member whose mod version has not arrived
/// yet, a lobby entered on Steam while already seated at a table (see <see cref="Lobby"/>), and
/// anything that throws.
///
/// Bots are not Steam members, so they never count either way. In the loopback harness there is
/// no Steam lobby, and every copy there runs the mod, so that is a modded table.
///
/// Worked out once a second from <see cref="ModTicker"/>, because it asks Steam. A table can stop
/// counting in the middle of a match: the copy of the lobby id goes with a lost connection, and a
/// host migration loses it too. That is the safe direction, but a feature that must stay the same
/// for a whole round should read this when the round starts and keep the answer.
/// </summary>
internal static class Table
{
    private const float Every = 1f;

    private static float _next;
    private static bool? _said;

    // The network session this copy is in, told apart by the same two things ModNet watches, and
    // the Steam lobby that session belongs to, once known.
    private static bool _hosting;
    private static IntPtr _connection;
    private static ulong _lobby;
    private static int _session;

    /// <summary>True only at a table where everybody runs this mod.</summary>
    internal static bool Modded { get; private set; }

    /// <summary>
    /// A new number each time this copy starts hosting, joins, or leaves. A host's trip to a
    /// match and back to the lobby is one session; leaving, or hosting again, is the next.
    /// </summary>
    internal static int Session
    {
        get
        {
            Follow();
            return _session;
        }
    }

    /// <summary>Developer test switch: read the lobby as if it had no tag.</summary>
    internal static bool PretendUntagged { get; set; }

    /// <summary>Why <see cref="Modded"/> is what it is, for the log and the test driver.</summary>
    internal static string Why { get; private set; } = "not looked yet";

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        bool modded;
        string why;
        try
        {
            // Every tick, not once a second: the sooner a session's lobby is known, the less
            // room an invite has to change the id first.
            Follow();

            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + Every;

            modded = Decide(out why);
        }
        catch (Exception e) { modded = false; why = $"could not tell ({e.Message})"; }

        Modded = modded;
        Why = why;

        if (_said == modded) return;
        _said = modded;
        Plugin.Log.LogInfo($"[table] modded table: {(modded ? "yes" : "no")} - {why}");
    }

    private static bool Decide(out string why)
    {
        // Ranked first: nothing below may touch Steam there.
        if (RankedGuard.Near) { why = "ranked"; return false; }

        if (PretendUntagged && Fair.TestsAllowed) { why = "pretending the lobby has no tag (test switch)"; return false; }

        if (Loopback.Active) { why = "loopback harness, every copy runs the mod"; return true; }

        if (!Lobby(out var lobby, out why)) return false;

        string tag = SteamMatchmaking.GetLobbyData(lobby, Matchmaking.VersionKey);
        if (string.IsNullOrEmpty(tag) || !tag.EndsWith(Matchmaking.PoolSuffix, StringComparison.Ordinal))
        {
            why = "the lobby has no mod tag";
            return false;
        }

        int members = SteamMatchmaking.GetNumLobbyMembers(lobby);
        if (members <= 0) { why = "Steam lists nobody in the lobby"; return false; }

        for (int i = 0; i < members; i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (!string.IsNullOrEmpty(SteamMatchmaking.GetLobbyMemberData(lobby, member, VersionCheck.Key))) continue;

            why = $"member {i + 1} of {members} has not shown the mod";
            return false;
        }

        why = $"tagged lobby, all {members} member(s) show the mod";
        return true;
    }

    /// <summary>
    /// The Steam lobby of the table this copy is playing at, for anything that reads that table's
    /// lobby. Ask it after the ranked check, because it asks Steam.
    ///
    /// <c>CurrentLobbyID</c> alone is not that. The game writes it on every Steam lobby it enters,
    /// whatever else is going on, and nothing stops a seated player from accepting an invite or a
    /// friend's Join Game. They enter the new lobby on Steam and stay at their table: their client
    /// is already running, so the game's start of a new one does nothing, and a host just carries
    /// on hosting. The id then names a lobby this copy is not playing in. So the lobby belongs to
    /// the running session: it is the one the id named once the session's own client was
    /// connected, and while the id names any other, there is no table lobby at all. A host's
    /// lobby must also be one it owns. The network address is no help: the game writes the new
    /// lobby's host into it just before the start that does nothing.
    /// </summary>
    internal static bool Lobby(out CSteamID lobby, out string why)
    {
        lobby = default;
        Follow();

        var sl = SteamLobby.Instance;
        ulong id = sl != null ? sl.CurrentLobbyID : 0;
        if (id == 0) { why = "not in a lobby"; return false; }
        if (!Matchmaking.SteamUp) { why = "Steam is not running"; return false; }

        if (_lobby == 0)
        {
            why = _hosting && _connection != IntPtr.Zero ? "somebody else owns the lobby this copy hosts" : "not at a table yet";
            return false;
        }
        if (id != _lobby) { why = "the lobby changed during this session"; return false; }
        if (_hosting && !Owns(id)) { why = "somebody else owns the lobby this copy hosts"; return false; }

        lobby = new CSteamID(id);
        why = "";
        return true;
    }

    /// <summary>
    /// The Steam lobby this copy hosts and owns, for the host's own upkeep. Unlike
    /// <see cref="Lobby"/> it ignores what <c>CurrentLobbyID</c> says now: a host who accepts an
    /// invite goes on hosting the table they had, and that lobby still needs its heartbeat, its
    /// door shut during a match, and players without the mod turned away. Not a reason to treat
    /// the table as modded: <see cref="Modded"/> still says no once the id has moved.
    /// </summary>
    internal static bool Hosted(out CSteamID lobby)
    {
        lobby = default;
        Follow();

        if (!_hosting || _lobby == 0 || RankedGuard.Near || Loopback.Active || !Owns(_lobby)) return false;

        lobby = new CSteamID(_lobby);
        return true;
    }

    /// <summary>
    /// Notice a new session, and learn its lobby once its own client is connected. The game
    /// writes the id just before it starts a client, so a client knows it at once. A host is
    /// started before the id of its new lobby is written, so for a moment the id can still name
    /// an earlier lobby; its own client only attaches when the lobby scene has loaded, seconds
    /// after the new id is in.
    /// </summary>
    private static void Follow()
    {
        bool hosting = NetworkServer.active;
        var connection = NetworkClient.connection;
        IntPtr id = connection is null ? IntPtr.Zero : connection.Pointer;
        if (hosting != _hosting || id != _connection)
        {
            _hosting = hosting;
            _connection = id;
            _lobby = 0;
            _session++;
        }

        // Nothing here may touch Steam near ranked, and the harness has no Steam lobby.
        if (_lobby != 0 || id == IntPtr.Zero || RankedGuard.Near || Loopback.Active) return;

        var sl = SteamLobby.Instance;
        ulong lobby = sl != null ? sl.CurrentLobbyID : 0;
        if (lobby != 0 && (!hosting || Owns(lobby))) _lobby = lobby;
    }

    private static bool Owns(ulong lobby) =>
        Matchmaking.SteamUp &&
        SteamMatchmaking.GetLobbyOwner(new CSteamID(lobby)).m_SteamID == SteamUser.GetSteamID().m_SteamID;
}
