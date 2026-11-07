using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using Mirror.RemoteCalls;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Marks a class of fair-play patches. <see cref="Fair.Install"/> applies every class carrying it,
/// each on its own, so one hook a game update breaks stays local and is named in the log.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class FairPatchAttribute : Attribute
{
    internal readonly string Label;
    internal readonly int Order;
    internal FairPatchAttribute(string label, int order = 100) { Label = label; Order = order; }
}

/// <summary>
/// Fair play: the shared core of the host's checks on the people at its table.
///
/// <b>What it does.</b> In this game the host is the server, and the host's word is what every
/// machine shows. So at a table it hosts, the host decides every outcome itself from its own
/// record, accepts only actions the rules allow, and removes a player whose game asks for
/// something an unchanged game never would.
///
/// <b>When it runs.</b> Only on a copy that is the server of a table it hosts and owns (or the
/// loopback test harness), never in ranked, never at the Velvet Room. That is decided from this
/// copy's own state, which nothing at the table can change mid-match (not <c>Table.Modded</c>,
/// which follows the lobby through the match and gates gameplay features instead). Every check
/// keeps the game's own sequence of remote calls, so a player whose copy has no mod sees exactly
/// what they always did; anything that changes what goes on the wire is gated apart, on
/// <see cref="HideHands"/>.
///
/// <b>Who is never a suspect.</b> A bot's seat is the host's own: its commands run on the host
/// with no sender. The host's own seat comes from the local connection. Neither is ever checked
/// as a cheat, only real remote connections are.
///
/// <b>What happens to a cheat.</b> Anything lag or honest play could cause is quietly refused
/// (<see cref="Refused"/>). Only something no honest copy could ever send is a clear cheat
/// (<see cref="Caught"/>): the player is removed through the game's own kick and refused at the
/// door until this copy stops hosting. Nothing is saved, so a mistake never outlives the session.
/// </summary>
[HarmonyPatch]
internal static class Fair
{
    internal const string Tag = "[fair]";

    /// <summary>Host setting: remove a player caught cheating. Off, the host still refuses the cheat itself.</summary>
    internal static ConfigEntry<bool> KickCheaters;

    // ------------------------------------------------------------------ install

    /// <summary>From <c>Plugin.Load</c>: one line there applies everything in the fair-play folder.</summary>
    internal static void Install(Harmony harmony, ConfigFile config)
    {
        KickCheaters = config.Bind("Fair play", "KickCheaters", true,
            "When you host: remove a player whose game sends a move no unchanged game could send " +
            "(cards it was never dealt, a bid or move the rules never allow), and refuse them at the door " +
            "until you stop hosting. Off, your game still settles every pull itself and refuses the move, " +
            "but lets the player stay. Only the host's setting matters.");

        var classes = new List<(Type Type, FairPatchAttribute Mark)>();
        foreach (var type in typeof(Fair).Assembly.GetTypes())
        {
            var mark = type.GetCustomAttribute<FairPatchAttribute>();
            if (mark != null) classes.Add((type, mark));
        }
        classes.Sort((a, b) => a.Mark.Order != b.Mark.Order
            ? a.Mark.Order.CompareTo(b.Mark.Order)
            : string.CompareOrdinal(a.Type.FullName, b.Type.FullName));

        foreach (var (type, mark) in classes)
        {
            try
            {
                harmony.PatchAll(type);
                Plugin.Log.LogInfo($"  patched: fair play: {mark.Label}");
            }
            catch (Exception e) { Plugin.Log.LogError($"  FAILED to patch fair play: {mark.Label}: {e.Message}"); }

            // A class may also want ticks or test commands; it says so in a static Register().
            try
            {
                var register = type.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (register != null && register.GetParameters().Length == 0) register.Invoke(null, null);
            }
            catch (Exception e) { Plugin.Log.LogError($"  FAILED to set up fair play: {mark.Label}: {e.InnerException?.Message ?? e.Message}"); }
        }
    }

    // ------------------------------------------------------------------ when the checks run

    private static int _frame = -1;
    private static bool _hosting;
    private static string _why = "not looked yet";

    /// <summary>True on a copy that is the server of a non-ranked table it owns. Read again each frame.</summary>
    internal static bool Hosting
    {
        get
        {
            int frame = Time.frameCount;
            if (frame != _frame)
            {
                _frame = frame;
                bool was = _hosting;
                try { _hosting = Decide(out _why); }
                catch (Exception e) { _hosting = false; _why = $"could not tell ({e.Message})"; }
                if (was != _hosting) Plugin.Log.LogInfo($"{Tag} host checks {(_hosting ? "on" : "off")} - {_why}");
            }
            return _hosting;
        }
    }

    /// <summary>Why <see cref="Hosting"/> is what it is, for the log and the test driver.</summary>
    internal static string Why => _why;

    private static bool Decide(out string why)
    {
        if (Plugin.Bare) { why = "bare test copy"; return false; }
        if (!NetworkServer.active) { why = "not hosting"; return false; }
        if (RankedGuard.Near) { why = "ranked"; return false; }

        var nm = Net;
        if (nm == null) { why = "no network manager"; return false; }
        if (nm.Matchmaking) { why = "a ranked server"; return false; }
        if (nm.isVelvetRoom) { why = "the Velvet Room"; return false; }

        if (Loopback.Active) { why = "loopback harness"; return true; }
        if (Table.Hosted(out _)) { why = "this copy hosts and owns the table"; return true; }

        // A host migration: this copy took over the server of the table it was playing at. Steam
        // picks the lobby's new owner itself, and it need not be this copy, so the ownership test
        // above can fail on the very copy that now decides every outcome. The game's own record
        // that this copy is the new host counts as hosting too, and once seen it holds for as long
        // as this copy keeps serving, because the game clears that record once the takeover settles.
        if (nm.MigrationStarted || NewHostAfterMigration()) TookOver = true;
        if (TookOver) { why = "this copy took over the table in a host migration"; return true; }

        why = "not a lobby this copy owns";
        return false;
    }

    /// <summary>This copy became the server through a host migration, and is still serving.</summary>
    internal static bool TookOver
    {
        get => _tookOver && NetworkServer.active;
        private set => _tookOver = value;
    }

    private static bool _tookOver;

    private static bool NewHostAfterMigration()
    {
        try
        {
            var hm = HostMigration.Instance;
            return hm != null && hm.isNewHost;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ hidden information

    private static int _hideFor = -1;
    private static bool _hide;
    private static bool _hasNext, _next, _nextTagged;

    /// <summary>
    /// The host keeps hidden things hidden: each seat's cards, chambers and the like go only to
    /// the seat they belong to until the game's own reveal. That changes what goes on the wire, so
    /// it runs only where every player at the table runs this same build, decided once per match at the
    /// moment the host starts it (<see cref="LatchHideHands"/>), when the lobby has just turned
    /// away anyone without the mod, and held for the whole match. Anything uncertain fails toward
    /// the game's own behaviour for that match, never toward a player missing their own hand.
    /// </summary>
    internal static bool HideHands
    {
        get
        {
            if (!Hosting) return false;
            if (_hideFor != Match)
            {
                // A match this copy did not start (it took over in a host migration): decide now.
                _hideFor = Match;
                _hide = EveryoneShowsTheMod(out string why, out bool tagged);
                Plugin.Log.LogInfo($"{Tag} hidden hands for this match: {(_hide ? "on" : "off")} - {why} (decided late)");
                if (!_hide && tagged) HiddenOffNote.Show();
            }
            return _hide;
        }
    }

    /// <summary>
    /// The host presses Start: decide for the whole match whether every player runs this mod.
    /// Fresh from Steam, not the once-a-second cache, so a player seated in the last second counts.
    /// </summary>
    [FairPatch("hidden hands decided as the host starts a match", order: 1)]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.StartGame))]
    internal static class LatchHideHands
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Starting()
        {
            try
            {
                if (!NetworkServer.active) return;
                // Held until Manager.StartGame opens the match in the next scene (MatchStart), so
                // nothing read while the table loads can replace it.
                _next = EveryoneShowsTheMod(out string why, out _nextTagged);
                _hasNext = true;
                Plugin.Log.LogInfo($"{Tag} hidden hands for the next match: {(_next ? "on" : "off")} - {why}");
                if (!_next && _nextTagged) HiddenOffNote.Show();
            }
            catch (Exception e) { _next = false; _nextTagged = false; _hasNext = true; Plugin.Log.LogWarning($"{Tag} could not decide hidden hands: {e.Message}"); }
        }
    }

    /// <summary>
    /// Whether every player shows this build. <paramref name="tagged"/>: the table is a lobby of the
    /// mod's pool, where the host is told when hidden hands are off.
    /// </summary>
    private static bool EveryoneShowsTheMod(out string why, out bool tagged)
    {
        tagged = false;
        if (!Hosting) { why = "not hosting"; return false; }
        if (Loopback.Active) { why = "loopback harness, every copy runs the mod"; return true; }
        if (!Table.Lobby(out var lobby, out why))
        {
            // A copy that took over in a host migration may not own the Steam lobby, and then the
            // table's lobby is the one its own client was in.
            var sl = SteamLobby.Instance;
            ulong id = sl != null ? sl.CurrentLobbyID : 0;
            if (!TookOver || id == 0 || !Matchmaking.SteamUp) { why = $"no table lobby ({why})"; return false; }
            lobby = new Steamworks.CSteamID(id);
        }

        string tag = Steamworks.SteamMatchmaking.GetLobbyData(lobby, Matchmaking.VersionKey);
        if (string.IsNullOrEmpty(tag) || !tag.EndsWith(Matchmaking.PoolSuffix, StringComparison.Ordinal))
        {
            why = "the lobby has no mod tag";
            return false;
        }
        tagged = true;

        var nm = Net;
        if (nm == null || nm.GamePlayers == null) { why = "no players"; return false; }
        foreach (var p in nm.GamePlayers)
        {
            if (p == null || p.isOwned || BotManager.IsBot(p)) continue;
            var conn = p.connectionToClient;
            if (conn == null) continue;
            if (!ulong.TryParse(conn.address, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0)
            {
                why = $"connection {conn.connectionId} has no Steam id";
                return false;
            }
            if (!SameBuild(lobby, new Steamworks.CSteamID(id)))
            {
                why = $"connection {conn.connectionId} has not shown this build of the mod";
                return false;
            }
        }

        // Everyone in the lobby too, seated or not yet: any of them may become the next host,
        // and the next host must be this same build to carry on a hidden-hands match.
        int members = Steamworks.SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (int i = 0; i < members; i++)
        {
            var member = Steamworks.SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (!SameBuild(lobby, member))
            {
                why = "a lobby member has not shown this build of the mod";
                return false;
            }
        }
        why = "every player shows this build of the mod";
        return true;
    }

    /// <summary>The member publishes exactly this copy's mod version.</summary>
    private static bool SameBuild(Steamworks.CSteamID lobby, Steamworks.CSteamID member) =>
        string.Equals(Steamworks.SteamMatchmaking.GetLobbyMemberData(lobby, member, VersionCheck.Key),
                      Plugin.Version, StringComparison.Ordinal);

    /// <summary>
    /// A short note on the host's own screen when hidden hands are off for a match at a lobby of
    /// the mod's pool, in the modes that keep cards and bullets hidden (Liar's Deck and the Chaos
    /// deck): shown as the host starts the match and again as the table opens. Only this copy
    /// draws it, on the mod's own canvas, which lasts through the change of scene; nothing is
    /// sent to anybody.
    /// </summary>
    internal static class HiddenOffNote
    {
        internal const string Line = "Cards and bullets are not kept hidden this match.";
        private const float ShowFor = 12f;
        private static ModCanvas.Line _line;
        private static float _until;

        /// <summary>The match is one that keeps cards and bullets hidden: at the table, by its seats; before that, by the mode the host chose.</summary>
        private static bool KeepsThingsHidden()
        {
            switch (TableHand.Playing())
            {
                case TableHand.Kind.Deck:
                case TableHand.Kind.ChaosDeck: return true;
                case TableHand.Kind.None: break;
                default: return false;
            }
            var nm = Net;
            return nm != null && (nm.Mode == CustomNetworkManager.GameMode.LiarsDeck || nm.Mode == CustomNetworkManager.GameMode.LiarsChaos);
        }

        internal static void Show(bool test = false)
        {
            try
            {
                if (!test && !KeepsThingsHidden()) return;
                if (!ModCanvas.Ready && !ModCanvas.Build()) return;
                if (_line == null)
                {
                    _line = ModCanvas.NewLine("fair-hidden-off", new Vector2(0.5f, 1f), 38f, 24f, ModCanvas.Cream, rule: true);
                    _line.Box.anchoredPosition = new Vector2(0f, -150f);
                }
                string said = Words.T(Line);
                if (!ModCanvas.Fits(said)) said = Line;
                _line.Set(said);
                _until = Time.unscaledTime + ShowFor;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Tag} the hidden hands note could not be shown: {e.Message}"); }
        }

        internal static void Tick()
        {
            if (_until <= 0f || Time.unscaledTime < _until) return;
            _until = 0f;
            try { _line?.Set(null); }
            catch { }
        }
    }

    internal static CustomNetworkManager Net
    {
        get
        {
            try
            {
                var nm = NetworkManager.singleton;
                return nm == null ? null : nm.TryCast<CustomNetworkManager>();
            }
            catch { return null; }
        }
    }

    // ------------------------------------------------------------------ who sent the command

    private static NetworkConnectionToClient _sender;
    private static ushort _hash;

    /// <summary>
    /// The connection whose Command is being handled right now on the host, or null: a bot's seat,
    /// the host's own code, or no command at all. Set at Mirror's one door for every remote call.
    /// </summary>
    internal static NetworkConnectionToClient Sender => _sender;

    /// <summary>The function hash of the Command being handled, 0 when none.</summary>
    internal static ushort Hash => _hash;

    /// <summary>A real player's copy on the other end, not the host's own seat and not a bot.</summary>
    internal static bool IsRemote(NetworkConnectionToClient c)
    {
        if (c == null) return false;
        try
        {
            if (c.connectionId == 0) return false;   // Mirror's local connection, the host's own seat
            var local = NetworkServer.localConnection;
            if (local != null && local.connectionId == c.connectionId) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// True when the Command being handled was sent by a remote player for an object that player
    /// owns: the normal case, since Mirror refuses a Command on an object its sender doesn't own.
    /// False for bots and the host's own code, and for game code that calls one seat's handler
    /// while handling another seat's Command, so a check never blames the wrong player.
    /// </summary>
    internal static bool SentBy(NetworkBehaviour part, out NetworkConnectionToClient conn)
    {
        conn = null;
        var s = _sender;
        if (!IsRemote(s) || part == null) return false;
        try
        {
            var owner = part.connectionToClient;
            if (owner == null || owner.connectionId != s.connectionId) return false;
        }
        catch { return false; }
        conn = s;
        return true;
    }

    /// <summary>Mirror's one door for every Command and Rpc: remember who sent it, for the checks below it.</summary>
    [FairPatch("the host knows who sent each command", order: 0)]
    [HarmonyPatch(typeof(RemoteProcedureCalls), nameof(RemoteProcedureCalls.Invoke))]
    internal static class Door
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Before(ushort functionHash, RemoteCallType remoteCallType,
                                   NetworkConnectionToClient senderConnection,
                                   ref bool __result, out (NetworkConnectionToClient, ushort) __state)
        {
            __state = (_sender, _hash);
            if (remoteCallType != RemoteCallType.Command || !NetworkServer.active)
            {
                _sender = null;
                _hash = 0;
                return true;
            }

            _sender = senderConnection;
            _hash = functionHash;

            // A flood: more commands in a second than any honest copy sends in a minute. The excess
            // is dropped before it reaches the game, so it can't stall the host or spam the table.
            if (IsRemote(senderConnection) && Hosting && Flooding(senderConnection.connectionId))
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyFinalizer]
        private static Exception After(Exception __exception, (NetworkConnectionToClient, ushort) __state)
        {
            _sender = __state.Item1;
            _hash = __state.Item2;
            return __exception;
        }
    }

    // ------------------------------------------------------------------ floods

    // Far above anything honest: the game's busiest legitimate traffic is a few commands a second.
    private const int FloodPerSecond = 400;
    private static readonly Dictionary<int, (float Second, int Count, int Dropped)> _rate = new();
    private static float _rateSaid;

    private static bool Flooding(int connectionId)
    {
        float second = Mathf.Floor(Time.unscaledTime);
        _rate.TryGetValue(connectionId, out var r);
        if (r.Second != second) r = (second, 0, r.Dropped);
        r.Count++;
        bool over = r.Count > FloodPerSecond;
        if (over) r.Dropped++;
        _rate[connectionId] = r;

        if (over && Time.unscaledTime >= _rateSaid)
        {
            _rateSaid = Time.unscaledTime + 5f;
            Plugin.Log.LogWarning($"{Tag} connection {connectionId} is flooding the host with commands - " +
                                  $"{r.Dropped} dropped so far");
        }
        return over;
    }

    // ------------------------------------------------------------------ a cheat, and a refusal

    private static readonly HashSet<int> _caught = new();
    private static readonly Dictionary<string, float> _refusedSaid = new(StringComparer.Ordinal);

    /// <summary>The words the table sees in chat when the host removes a player for cheating.</summary>
    internal const string RemovedLine = "The host's anti-cheat removed a player.";

    /// <summary>
    /// A clear cheat: something no unchanged copy could send, whatever the lag. The player is
    /// removed through the game's own kick and refused at the door until this copy stops hosting.
    /// The caller has already refused the command itself; this never runs for a bot or the host.
    /// </summary>
    internal static void Caught(NetworkConnectionToClient conn, string what)
    {
        if (!IsRemote(conn)) return;
        int id = conn.connectionId;

        if (KickCheaters != null && !KickCheaters.Value)
        {
            Refused($"connection {id}: {what} (KickCheaters is off, so not removed)");
            return;
        }
        if (!_caught.Add(id)) return;   // already on its way out

        Plugin.Log.LogWarning($"{Tag} connection {id} removed for cheating: {what}");
        BlockList.KickForCheat(conn, what);
        // The whole table's chat, where every player may read a different language: in English,
        // like every other line the mod sends there.
        Matchmaking.TellTheRoom(RemovedLine);
        CaughtCount++;
    }

    /// <summary>How many players this copy has removed while hosting, for the test driver.</summary>
    internal static int CaughtCount { get; private set; }

    /// <summary>
    /// Refused without blame: out of turn, too late, a value lag could explain. The command is
    /// dropped by the caller; this only logs, at most once every few seconds for the same reason.
    /// </summary>
    internal static void Refused(string what)
    {
        RefusedCount++;
        float now = Time.unscaledTime;
        if (_refusedSaid.TryGetValue(what, out float until) && now < until) return;
        _refusedSaid[what] = now + 5f;
        Plugin.Log.LogInfo($"{Tag} refused: {what}");
    }

    /// <summary>How many commands this copy has refused while hosting, for the test driver.</summary>
    internal static int RefusedCount { get; private set; }

    // ------------------------------------------------------------------ the match

    /// <summary>Counts up at the start of every match the host starts, so per-match records know when to start over.</summary>
    internal static int Match { get; private set; }

    [FairPatch("a fresh record at the start of each match", order: 1)]
    [HarmonyPatch(typeof(Manager), nameof(Manager.StartGame))]
    internal static class MatchStart
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Started()
        {
            try
            {
                if (!NetworkServer.active) return;
                Match++;
                if (_hasNext)
                {
                    _hide = _next;
                    _hideFor = Match;
                    _hasNext = false;
                    // Said again as the match opens, so the host sees it at the table too.
                    if (!_hide && _nextTagged && Hosting) HiddenOffNote.Show();
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ ticks

    private static readonly List<(string Name, Action Run)> _ticks = new();
    private static bool _wasHosting;

    /// <summary>A feature's per-frame upkeep, run from <see cref="Tick"/>, each on its own.</summary>
    internal static void OnTick(string name, Action run) => _ticks.Add((name, run));

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        bool hosting = NetworkServer.active;
        if (!_wasHosting && hosting)
        {
            // This copy starts serving, fresh or by taking a table over: every per-match record
            // starts again, and hidden hands are decided again for whatever match it serves.
            Match++;
            _hideFor = -1;
        }
        if (_wasHosting && !hosting)
        {
            _tookOver = false;
            _hasNext = false;
            _caught.Clear();
            _rate.Clear();
            _refusedSaid.Clear();
        }
        _wasHosting = hosting;
        HiddenOffNote.Tick();

        foreach (var (name, run) in _ticks)
        {
            try { run(); }
            catch (Exception e) { Plugin.Log.LogError($"{Tag} {name} tick failed: {e.Message}"); }
        }
    }

    // ------------------------------------------------------------------ developer tests

    private static readonly Dictionary<string, Action<string>> _drive = new(StringComparer.Ordinal);

    /// <summary>A developer test command, reached through the test driver's "fair &lt;word&gt; ..." line.</summary>
    internal static void OnDrive(string word, Action<string> run) => _drive[word] = run;

    /// <summary>
    /// Whether the developer tests may run: developer mode and the loopback test harness, both.
    /// Some tests act on a connection or on the host's own table, so none of them ever runs at a
    /// real table.
    /// </summary>
    internal static bool TestsAllowed => Dev.Enabled && Loopback.Active;

    /// <summary>
    /// From the test driver. Only with developer mode on inside the loopback harness
    /// (<see cref="TestsAllowed"/>); anywhere else every "fair" command is refused. Every test runs
    /// inside the host; nothing here changes what any client sends.
    /// </summary>
    internal static void Drive(string line)
    {
        if (!TestsAllowed)
        {
            Plugin.Log.LogWarning($"{Tag} test: fair tests run only with developer mode on, inside the loopback harness");
            return;
        }

        int space = line.IndexOf(' ');
        string word = space < 0 ? line : line.Substring(0, space);
        string rest = space < 0 ? "" : line.Substring(space + 1).Trim();

        if (word == "state")
        {
            Plugin.Log.LogInfo($"{Tag} test: state hosting={Hosting} ({Why}) caught={CaughtCount} refused={RefusedCount} " +
                               $"session={BlockList.SessionCount} match={Match} kick={(KickCheaters == null || KickCheaters.Value)}");
            return;
        }
        if (word == "hiddennote")
        {
            // The host's note for a match without hidden hands, on this screen only.
            HiddenOffNote.Show(test: true);
            Result("hiddennote", true, "the note is on the host's screen for a few seconds");
            return;
        }
        if (!_drive.TryGetValue(word, out var run))
        {
            Plugin.Log.LogWarning($"{Tag} test: no such command '{word}' ({string.Join(", ", _drive.Keys)})");
            return;
        }
        try { run(rest); }
        catch (Exception e) { Plugin.Log.LogError($"{Tag} test: '{word}' failed: {e.Message}"); }
    }

    /// <summary>A test result, in one greppable form.</summary>
    internal static void Result(string test, bool pass, string detail) =>
        Plugin.Log.LogInfo($"[fair-test] {(pass ? "PASS" : "FAIL")} {test}: {detail}");

    /// <summary>The first remote player's connection, or the one with this connection id, for a test.</summary>
    internal static NetworkConnectionToClient TestConnection(string which)
    {
        if (!TestsAllowed) return null;
        int.TryParse(which, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted);
        foreach (var kv in NetworkServer.connections)
        {
            var c = kv.Value;
            if (!IsRemote(c)) continue;
            if (wanted > 0 && c.connectionId != wanted) continue;
            return c;
        }
        return null;
    }

    /// <summary>
    /// Run the next part of the host's own handling with this sender, inside the host. With no
    /// connection it is the host's own call (a settle, a deadline). With one it is a developer test,
    /// and that runs only where <see cref="TestsAllowed"/>; anywhere else nothing runs.
    /// </summary>
    internal static void AsSentBy(NetworkConnectionToClient conn, Action body)
    {
        if (conn != null && !TestsAllowed)
        {
            Plugin.Log.LogWarning($"{Tag} test: a test sender is used only inside the loopback harness - nothing ran");
            return;
        }
        var (was, wasHash) = (_sender, _hash);
        _sender = conn;
        _hash = 0;
        try { body(); }
        finally { _sender = was; _hash = wasHash; }
    }
}
