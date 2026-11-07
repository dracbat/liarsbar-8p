using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using Mirror;
using Steamworks;

namespace LiarsBar8P;

/// <summary>
/// The players this copy has blocked, by Steam id, and the two things a block does: a lobby this
/// copy hosts turns them away, and a lobby they host is not shown to this copy.
///
/// <b>Turned away before anything is sent.</b> When a blocked player connects to a lobby this copy
/// hosts, the connection is refused in <c>OnServerConnectInternal</c>, the one moment the host
/// knows who it is and has sent nothing yet. Any later and the game has already told them to load
/// the lobby, and a connection dropped during a load strands the player in an empty scene. Refused
/// there, they never leave the menu: the game says Failed To Connect, as it does for a lobby that
/// has gone, and Quick Join moves on to the next lobby. Nothing says why, on purpose. A host
/// migration is never refused, because that is a player already at the table finding the new
/// host, and ranked is never touched.
///
/// <b>Hidden.</b> Steam only says who hosts a lobby nobody here is in, so that is what is hidden:
/// the lobby list, the main menu's count and Quick Join all skip lobbies a blocked player hosts.
/// A lobby they are only sitting in cannot be told apart from outside, and an invite or a lobby
/// ID typed in is the player's own choice and goes through.
///
/// <b>Stored</b> as one Steam id a line in <c>blocked.txt</c> under local app data, next to the
/// mod's other files there and away from the BepInEx folder people zip for bug reports. Nothing
/// else is kept: no names, no dates. An id is never written to the log, only counts. A file that
/// cannot be read blocks nobody and says so once. Steam's own block list is not read.
/// </summary>
internal static class BlockList
{
    private const string Folder = "LiarsBar8P";
    private const string FileName = "blocked.txt";

    private static readonly HashSet<ulong> _ids = new();
    private static readonly List<NetworkConnectionToClient> _refused = new();

    // Players removed through the game's own kick in the lobby, and when the host closes each
    // connection itself if it is still open.
    private static readonly List<(NetworkConnectionToClient Conn, float At)> _kicked = new();
    private const float KickGrace = 1f;

    private static bool _loaded;
    private static string _path;
    private static bool _testFile;

    // The session block: ids a caught cheat has put on this host for as long as it keeps hosting,
    // never saved and never shown in the BLOCKED list. It is the sink every host-authority check
    // sends a clear cheat to, so one caught at the table cannot rejoin by invite or typed id. Held
    // apart from the manual list on purpose: the manual list is the player's, this is the host's
    // own enforcement, emptied the moment the host stops hosting so a false positive cannot outlive
    // the session. A bot or a connection with no Steam id (id 0) is never put on it.
    private static readonly HashSet<ulong> _session = new();
    private static bool _wasHosting;

    /// <summary>How many players are blocked.</summary>
    internal static int Count
    {
        get
        {
            Load();
            return _ids.Count;
        }
    }

    /// <summary>The blocked ids, for the BLOCKED list. Never for the log.</summary>
    internal static List<ulong> All()
    {
        Load();
        return new List<ulong>(_ids);
    }

    /// <summary>Is this Steam id blocked? Never yourself, outside a developer test file.</summary>
    internal static bool IsBlocked(ulong id)
    {
        if (id == 0) return false;
        Load();
        if (_ids.Count == 0 || !_ids.Contains(id)) return false;
        return _testFile || id != Own();
    }

    /// <summary>Block a player. Refused for nobody and for yourself.</summary>
    internal static bool Add(ulong id)
    {
        Load();
        if (id == 0 || id == Own() || !_ids.Add(id)) return false;
        Save();
        Plugin.Log.LogInfo($"[block] a player was blocked - {_ids.Count} on the list");
        return true;
    }

    internal static bool Remove(ulong id)
    {
        Load();
        if (!_ids.Remove(id)) return false;
        Save();
        Plugin.Log.LogInfo($"[block] a player was unblocked - {_ids.Count} on the list");
        return true;
    }

    // ------------------------------------------------------------------ the session block

    /// <summary>
    /// Refuse this id's rejoin for the rest of this hosting session. Called only by a host-authority
    /// check that has seen a state impossible under the mode's rules, never on anything lag or legal
    /// play could cause. Id 0 - a bot, a non-Steam or the host's own connection - is ignored, so a
    /// seat the host answers for can never be session-blocked. Never saved.
    /// </summary>
    internal static void BlockForSession(ulong id)
    {
        if (id == 0) return;
        if (_session.Add(id))
            Plugin.Log.LogInfo($"[block] a caught cheat was refused for the session - {_session.Count} held");
    }

    /// <summary>
    /// Resolve the Steam id behind a caught connection, refuse its rejoin for the session, and send
    /// it back through the game's own kick, the one the lobby's Kick button uses. The sink every
    /// host-authority check routes a clear cheat to, so the kick and the block are never apart.
    /// </summary>
    internal static void KickForCheat(NetworkConnectionToClient conn, string why)
    {
        if (conn == null) return;
        try
        {
            BlockForSession(IdOf(conn));

            var room = LobbyController.Instance;
            if (room != null && room.SpawnSlots != null && room.SpawnSlots.Count > 0 && room.SpawnSlots[0] != null)
            {
                room.SpawnSlots[0].KickRPC(conn);
                // The host closes the connection itself a moment later, once the game's own kick has
                // had time to reach the player, so the removal never rests on the player's copy.
                _kicked.Add((conn, UnityEngine.Time.unscaledTime + KickGrace));
                Plugin.Log.LogInfo($"[block] connection {conn.connectionId} {why} - kicked and refused for the session");
                return;
            }

            // No lobby podium to kick through (mid-scene): drop it on the next tick, as the door does.
            if (!_refused.Contains(conn)) _refused.Add(conn);
            Plugin.Log.LogInfo($"[block] connection {conn.connectionId} {why} - dropped and refused for the session");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[block] could not kick a caught cheat: {e.Message}"); }
    }

    /// <summary>How many ids the session block holds, for the test driver.</summary>
    internal static int SessionCount => _session.Count;

    /// <summary>Whether this id is refused for the session, for the test driver.</summary>
    internal static bool SessionHolds(ulong id) => id != 0 && _session.Contains(id);

    /// <summary>Empty the session block, for the test driver. The host restart clears it in play.</summary>
    internal static void ClearSessionForTest()
    {
        if (_session.Count == 0 || !Fair.TestsAllowed) return;
        _session.Clear();
        Plugin.Log.LogInfo("[block] the session block was emptied (test driver)");
    }

    /// <summary>
    /// The Steam id behind a connection on the host, from the transport's own record of who is
    /// on the other end. The loopback harness has no Steam ids, so in a developer run there each
    /// copy is numbered the way <see cref="Loopback"/> names it.
    /// </summary>
    internal static ulong IdOf(NetworkConnectionToClient conn)
    {
        if (conn == null) return 0;
        if (ulong.TryParse(conn.address, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0) return id;
        if (Loopback.Active && Dev.Enabled && conn.connectionId > 0) return Loopback.FirstId + (ulong)(conn.connectionId + 1);
        return 0;
    }

    /// <summary>Is this lobby hosted by somebody on the list? Read from the address the game publishes for joining it.</summary>
    internal static bool HostBlocked(CSteamID lobby)
    {
        if (Count == 0) return false;
        string host = SteamMatchmaking.GetLobbyData(lobby, "HostAddress");
        return ulong.TryParse(host, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && IsBlocked(id);
    }

    // ------------------------------------------------------------------ the refusal

    [HarmonyPatch]
    internal static class Refuse
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NetworkManager), "OnServerConnectInternal")]
        private static bool AtTheDoor(NetworkManager __instance, NetworkConnectionToClient conn)
        {
            try
            {
                if (__instance == null || conn == null || __instance.Matchmaking || RankedGuard.Near) return true;
                if (Count == 0 && _session.Count == 0) return true;

                var nm = __instance.TryCast<CustomNetworkManager>();
                if (nm != null && nm.isVelvetRoom) return true;

                ulong id = IdOf(conn);

                // A player this copy removed for cheating stays out until it stops hosting, through a
                // host migration too. The game lets everybody back in during one, and a caught cheat
                // must not ride back in with them. Only the session block: the manual list keeps its
                // migration exemption below, so a blocked friend someone invited is never stranded.
                if (id != 0 && _session.Contains(id))
                {
                    _refused.Add(conn);
                    Plugin.Log.LogInfo($"[block] connection {conn.connectionId} was removed for cheating this session - refused");
                    return false;
                }

                if (__instance.MigrationStarted || Count == 0) return true;

                // Only a lobby this copy hosts and owns, or the harness's stand-in for one.
                if (!Loopback.Active && !Table.Hosted(out _)) return true;

                if (!IsBlocked(id)) return true;

                // Left unauthenticated, nothing it sends is handled; it goes on the next tick,
                // outside the connect event.
                _refused.Add(conn);
                Plugin.Log.LogInfo($"[block] connection {conn.connectionId} is on the block list - refused before it was sent the lobby");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[block] could not check a connection: {e.Message}");
                return true;
            }
        }
    }

    /// <summary>From <see cref="ModTicker"/>: close the connections refused since the last tick.</summary>
    internal static void Tick()
    {
        // The session block lives only as long as this host keeps hosting. When hosting stops, it
        // is emptied, so a caught cheat is refused only for the session it was caught in and a false
        // positive never outlives it.
        bool hosting = NetworkServer.active;
        if (_wasHosting && !hosting)
        {
            _kicked.Clear();
            if (_session.Count > 0)
            {
                _session.Clear();
                Plugin.Log.LogInfo("[block] hosting stopped - the session block was emptied");
            }
        }
        _wasHosting = hosting;

        // A kicked player's grace is over: the connection, if still open, is closed below.
        if (_kicked.Count > 0)
        {
            float now = UnityEngine.Time.unscaledTime;
            for (int i = _kicked.Count - 1; i >= 0; i--)
            {
                var (conn, at) = _kicked[i];
                if (now < at) continue;
                _kicked.RemoveAt(i);
                if (conn == null || _refused.Contains(conn)) continue;
                bool open = false;
                try { open = hosting && NetworkServer.connections.TryGetValue(conn.connectionId, out var c) && c != null && c.Pointer == conn.Pointer; }
                catch { }
                if (open) _refused.Add(conn);
            }
        }

        if (_refused.Count == 0) return;

        var drop = new List<NetworkConnectionToClient>(_refused);
        _refused.Clear();
        foreach (var conn in drop)
        {
            try
            {
                if (conn == null) continue;
                conn.Disconnect();
                Plugin.Log.LogInfo($"[block] connection {conn.connectionId} closed");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[block] could not close a refused connection: {e.Message}"); }
        }
    }

    // ------------------------------------------------------------------ the file

    /// <summary>
    /// The list's file. A developer copy with a role reads a file of its own, so one account's
    /// copies can stand for different people in a test; only that file may hold your own id.
    /// </summary>
    private static string PathOf()
    {
        if (_path != null) return _path;

        string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Folder);
        string role = null;
        try { role = Environment.GetEnvironmentVariable("LIARSBAR8P_ROLE"); }
        catch { }

        _testFile = Dev.Enabled && !string.IsNullOrEmpty(role) && role.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
        _path = System.IO.Path.Combine(root, _testFile ? $"blocked-{role}.txt" : FileName);
        return _path;
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            string path = PathOf();
            if (!File.Exists(path)) return;

            foreach (string line in File.ReadAllLines(path))
            {
                string word = line.Trim();
                if (ulong.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0) _ids.Add(id);
            }
            Plugin.Log.LogInfo($"[block] {_ids.Count} blocked player(s){(_testFile ? " from this copy's test file" : "")}");
        }
        catch (Exception e)
        {
            _ids.Clear();
            Plugin.Log.LogWarning($"[block] could not read the block list - nobody is blocked: {e.Message}");
        }
    }

    /// <summary>Through a temporary file and a replace, so a crash halfway never leaves half a list.</summary>
    private static void Save()
    {
        try
        {
            string path = PathOf();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

            var lines = new List<string>();
            foreach (ulong id in _ids) lines.Add(id.ToString(CultureInfo.InvariantCulture));

            string temp = path + ".tmp";
            File.WriteAllLines(temp, lines);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[block] could not save the block list: {e.Message}"); }
    }

    /// <summary>This copy's own Steam id, or 0 where Steam cannot be asked.</summary>
    private static ulong Own()
    {
        try { return !RankedGuard.Near && Matchmaking.SteamUp ? SteamUser.GetSteamID().m_SteamID : 0; }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>Put this copy's own id in its developer test file, so one account can stand for a blocked host.</summary>
    internal static bool BlockSelfForTest()
    {
        PathOf();
        ulong own = Own();
        if (!_testFile || own == 0) return false;
        Load();
        if (!_ids.Add(own)) return true;
        Save();
        return true;
    }

    /// <summary>Empty this copy's developer test file.</summary>
    internal static void ClearForTest()
    {
        PathOf();
        if (!_testFile) return;
        Load();
        _ids.Clear();
        Save();
    }
}
