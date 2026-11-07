using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Fair play at a table somebody else hosts: this copy, as a player, keeps an eye on the host.
///
/// <b>What it does.</b> The host's game decides everything at its table, so a player cannot check
/// any single outcome. What a player can check is a state the rules never allow at all, read from
/// what the host already sends every player: a seat still alive after more pulls than the cylinder
/// has chambers, a player who was out playing again, a player who was out named the winner, more
/// dice shown for one player than a cup holds, more bullets than the Liar's Poker revolver holds,
/// or awards for a match the host took over partway through. On any of these the player is told
/// why in plain words, leaves the table through the game's own Leave, and the host goes on their
/// BLOCKED list, so that host's tables are hidden and refused from then on. Nothing is sent to
/// anybody: every player running the mod watches for themselves.
///
/// <b>Only where it is fair.</b> It judges only a host running this very build of the mod, whose
/// own copy keeps each of these states impossible at its table whoever asks for them
/// (<see cref="HostCheckGuards"/>), so nothing another player does can make an honest host look
/// guilty. Never in ranked, never at the Velvet Room, never on the copy that hosts. Every state is
/// held for a few seconds before it counts, so a late or reordered update cannot trip it, and the
/// record starts over with each match, after any change of host, and whenever the check pauses.
/// The moments a rule legitimately bends are left out: the end of a match (the winner is stood back
/// up) and anything around a host migration (the new host restores each seat). After a migration
/// only the awards are judged: whether the new host's own copy is guarding its table is not
/// something a player can tell, and a doubt never counts against a host.
///
/// Bots are seats like any other here: the host answers for every seat at its table.
/// </summary>
internal static class HostCheck
{
    private const string Tag = Fair.Tag;

    // The hard limits. Each is one past the most the rules ever allow, whatever the house rules.
    /// <summary>A Liar's Deck cylinder has six chambers: no seat survives a sixth pull.</summary>
    internal const int Chambers = 6;
    /// <summary>A Liar's Dice cup holds five dice, and no rule adds one.</summary>
    internal const int MostDice = 5;
    /// <summary>The Liar's Poker revolver holds eight bullets.</summary>
    internal const int MostBullets = 8;

    // How long a state has to hold before it counts, in seconds. Far longer than any reordering
    // of updates between two machines; a real rule break holds for good.
    internal const float DeadFor = 2f;      // a seat counts as out once it has been out this long in a live match
    internal const float AliveFor = 4f;     // ...and as back in play once alive again this long, the match still live
    internal const float WinnerFor = 3f;    // a winner who was out, held this long
    internal const float HeldFor = 2f;      // pulls past the cylinder, bullets past the revolver
    internal const float OutTurnFor = 90f;  // a seat that is out holding the turn: noted in the log only
    private const float Settle = 6f;        // after (re)joining, before anything is judged
    private const float LookEvery = 0.25f;
    private const float ShowFor = 30f;

    // ------------------------------------------------------------------ what the player is told

    private const string SawRevolver = "A player at this table lived through {0} pulls of a six-chamber revolver.";
    private const string SawRevived = "A player who was out came back into this match.";
    private const string SawDeadWinner = "A player who was out was named the winner of this match.";
    private const string SawDice = "This host's table showed {0} dice for one player. A cup holds 5.";
    private const string SawBullets = "This host's table loaded {0} bullets into an eight-bullet revolver.";
    private const string SawAwards = "This host sent awards for a match they took over partway through.";
    private const string LeftLine = "The rules were broken at this host's table, so you left it. This host is now on your BLOCKED list.";

    // ------------------------------------------------------------------ install

    /// <summary>The watching itself, ticked on every copy; it stands down by itself on a host.</summary>
    [FairPatch("players watch the host's table for states the rules never allow")]
    [HarmonyPatch]
    internal static class Watch
    {
        private static void Register()
        {
            Fair.OnTick("host check", Tick);
            Fair.OnDrive("hostcheck", Drive);
        }
    }

    // ------------------------------------------------------------------ the session and the match

    private static ulong _lobby;              // the Steam lobby this copy is in, 0 when none
    private static float _connectedSince = -1f;
    private static ulong _host;               // the host this copy is connected to now, 0 when not known
    private static bool _matchOver = true;    // no match is running as far as this copy has seen
    private static ulong _matchHost;          // the host when this copy first saw the current match
    private static bool _hostChanged;         // the host changed during the current match
    private static bool _sawMigration;        // the game moved this copy to a new host during the current match
    private static IntPtr _manager;           // the match manager the seat records belong to
    private static readonly Dictionary<uint, Seat> _seats = new();
    private static float _nextLook;
    private static string _why = "not looked yet";
    private static bool _armed;

    /// <summary>The host changed during the match this copy is in or has just finished.</summary>
    internal static bool HostChangedThisMatch => _hostChanged;

    /// <summary>The game moved this copy to another host, or tried to, during the current match.</summary>
    internal static bool MigrationThisMatch => _sawMigration || _hostChanged;

    private static void Tick()
    {
        if (_leaveAt > 0f && Time.unscaledTime >= _leaveAt) { _leaveAt = 0f; Leave(); }
        if (_shownUntil > 0f && Time.unscaledTime >= _shownUntil) { _shownUntil = 0f; Show(null); }

        float now = Time.unscaledTime;
        if (now < _nextLook) return;
        _nextLook = now + LookEvery;

        Follow(now);
        bool armed = Armed(now, out _why);
        if (armed != _armed)
        {
            _armed = armed;
            Plugin.Log.LogInfo($"{Tag} host check {(armed ? "watching" : "paused")} - {_why}");
        }
        if (!armed) { _seats.Clear(); return; }

        try { Look(now); }
        catch (Exception e)
        {
            // Never judge on a read that failed: start the record over, and say so now and then.
            _seats.Clear();
            if (now >= _complainAt)
            {
                _complainAt = now + 30f;
                Plugin.Log.LogWarning($"{Tag} host check: could not read the table: {e.Message}");
            }
        }
    }

    private static float _complainAt;

    /// <summary>Follow the lobby, the connection, the host and the match this copy is in.</summary>
    private static void Follow(float now)
    {
        ulong lobby = 0;
        try { var sl = SteamLobby.Instance; lobby = sl != null ? sl.CurrentLobbyID : 0; } catch { }
        if (lobby != _lobby)
        {
            _lobby = lobby;
            Forget("a new lobby");
        }

        if (NetworkServer.active || !NetworkClient.active)
        {
            _connectedSince = -1f;
            _host = 0;
            if (NetworkServer.active) { Forget(null); return; }   // this copy hosts: nothing to watch
            if (!Migrating()) Forget(null);                     // left the table, not moving to a new host
            return;
        }

        bool connected = false;
        try { connected = NetworkClient.isConnected; } catch { }
        if (!connected) { _connectedSince = -1f; _host = 0; return; }
        if (_connectedSince < 0f) _connectedSince = now;

        _host = HostId();

        var m = Manager.Instance;
        bool migrating = Migrating();
        if (!_matchOver && migrating) _sawMigration = true;

        if (m == null)
        {
            // Back in the lobby scene, not on the way to a new host: the match is over.
            if (!migrating) _matchOver = true;
            _manager = IntPtr.Zero;
            _seats.Clear();
            return;
        }

        if (m.Pointer != _manager)
        {
            _manager = m.Pointer;
            _seats.Clear();
        }

        bool live = Live(m);
        if (m.GameEnd) _matchOver = true;

        if (live && _matchOver && !migrating && _host != 0)
        {
            // A new match: everything recorded for the last one goes.
            _matchOver = false;
            _matchHost = _host;
            _hostChanged = false;
            _sawMigration = false;
            _seats.Clear();
        }
        else if (!_matchOver && _matchHost != 0 && _host != 0 && _host != _matchHost && !_hostChanged)
        {
            _hostChanged = true;
            _seats.Clear();
            Plugin.Log.LogInfo($"{Tag} host check: the host changed during this match - from now on only the awards are judged");
        }
    }

    private static void Forget(string why)
    {
        if (why != null && (!_matchOver || _matchHost != 0)) Plugin.Log.LogInfo($"{Tag} host check: starting over ({why})");
        _matchOver = true;
        _matchHost = 0;
        _hostChanged = false;
        _sawMigration = false;
        _manager = IntPtr.Zero;
        _seats.Clear();
    }

    /// <summary>A match is being played: started, not ended, not being wound up.</summary>
    private static bool Live(Manager m)
    {
        try { return m != null && m.GameStarted && !m.GameEnd && !m.endgamechecked; }
        catch { return false; }
    }

    /// <summary>The game is moving this copy to another host, or has just started to.</summary>
    private static bool Migrating()
    {
        try
        {
            var nm = Fair.Net;
            if (nm != null && nm.MigrationStarted) return true;
            var hm = HostMigration.Instance;
            return hm != null && hm.MigrationNedded;
        }
        catch { return true; }   // can't tell: treat it as a migration, which only ever stops a check
    }

    /// <summary>
    /// The Steam id of the host this copy is connected to: the address the game connected to,
    /// which it takes from the lobby when joining and from the game's own choice of new host after
    /// a migration. The loopback harness has none; there the host is numbered as the harness does.
    /// </summary>
    private static ulong HostId()
    {
        if (Loopback.Active) return Dev.Enabled ? Loopback.FirstId + 1UL : 0;
        try
        {
            var nm = Fair.Net;
            if (nm == null) return 0;
            return ulong.TryParse(nm.networkAddress, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0;
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ when it judges

    private static float _versionAt = -1f;
    private static ulong _versionFor;
    private static bool _sameBuild;
    private static string _versionWhy = "";

    private static bool Armed(float now, out string why)
    {
        if (Plugin.Bare) { why = "bare test copy"; return false; }
        if (NetworkServer.active) { why = "this copy hosts"; return false; }
        if (!NetworkClient.active || _connectedSince < 0f) { why = "not at a table"; return false; }
        if (RankedGuard.Near) { why = "ranked"; return false; }

        var nm = Fair.Net;
        if (nm == null) { why = "no network manager"; return false; }
        if (nm.Matchmaking) { why = "a ranked server"; return false; }
        if (nm.isVelvetRoom) { why = "the Velvet Room"; return false; }
        if (Migrating()) { why = "moving to a new host"; return false; }
        if (now - _connectedSince < Settle) { why = "just joined"; return false; }
        if (_host == 0) { why = "the host is not known"; return false; }
        if (_tripped.Contains(_host)) { why = "already left this host"; return false; }
        if (Manager.Instance == null) { why = "not in a match"; return false; }

        if (!SameBuild(now, out why)) return false;
        why = _hostChanged ? "the host changed this match: awards only" : "the host runs this build";
        return true;
    }

    /// <summary>The host runs exactly this build, so its own copy keeps these states impossible.</summary>
    private static bool SameBuild(float now, out string why)
    {
        if (Loopback.Active) { why = Dev.Enabled ? "loopback harness" : "loopback without developer mode"; return Dev.Enabled; }
        if (_versionFor == _host && now < _versionAt + 2f) { why = _versionWhy; return _sameBuild; }
        _versionFor = _host;
        _versionAt = now;

        _sameBuild = false;
        if (!Table.Lobby(out var lobby, out string lw)) _versionWhy = $"no lobby ({lw})";
        else
        {
            string ver = SteamMatchmaking.GetLobbyMemberData(lobby, new CSteamID(_host), VersionCheck.Key);
            if (string.IsNullOrEmpty(ver)) _versionWhy = "the host shows no mod";
            else if (ver != Plugin.Version) _versionWhy = "the host runs another build of the mod";
            else { _sameBuild = true; _versionWhy = "the host runs this build"; }
        }
        why = _versionWhy;
        return _sameBuild;
    }

    /// <summary>
    /// The host made this table and still owns it, and has not changed this match: the only host
    /// whose own guards a player can be sure of. A host who took a table over in a migration is
    /// judged on the awards alone.
    /// </summary>
    private static bool FirstHost()
    {
        if (_hostChanged) return false;
        if (Loopback.Active) return Dev.Enabled;
        try
        {
            if (!Table.Lobby(out var lobby, out _)) return false;
            string made = SteamMatchmaking.GetLobbyData(lobby, "HostAddress");
            if (!ulong.TryParse(made, NumberStyles.None, CultureInfo.InvariantCulture, out ulong maker) || maker != _host) return false;
            return SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID == _host;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ the seats

    /// <summary>One seat's record for the current match. Pure: fed the seat's state and the time.</summary>
    internal sealed class Seat
    {
        private float _deadSince = -1f;
        private bool _outThisStretch;       // out for long enough in the current live stretch
        private float _aliveSince = -1f;
        private bool _outEver;              // out for long enough in a live match, and out ever since
        private bool _notWinnerSinceOut;
        private float _deadWinnerSince = -1f;
        private float _pastSince = -1f;
        private float _bulletsSince = -1f;
        private float _outTurnSince = -1f;
        internal bool OutTurnSaid;

        internal enum Saw { Nothing, Revived, DeadWinner }

        /// <summary>
        /// Life and death: a seat out for good in a live match that plays on alive, or is named
        /// the winner while still out. <paramref name="judge"/> false keeps the record without
        /// ever answering (a host whose table is judged on the awards only).
        /// </summary>
        internal Saw Life(float now, bool live, bool dead, bool winner, bool judge)
        {
            if (!live)
            {
                // The match is ending or not yet under way: the game stands the winner back up
                // then, so only an outage that started and was seen inside a live stretch counts.
                _deadSince = -1f;
                _outThisStretch = false;
                _aliveSince = -1f;
            }
            else if (dead)
            {
                _aliveSince = -1f;
                if (_deadSince < 0f) _deadSince = now;
                if (now - _deadSince >= DeadFor) { _outThisStretch = true; _outEver = true; }
            }
            else
            {
                _deadSince = -1f;
                if (_outThisStretch)
                {
                    if (_aliveSince < 0f) _aliveSince = now;
                    if (judge && now - _aliveSince >= AliveFor) return Saw.Revived;
                }
            }

            if (!dead)
            {
                _outEver = false;
                _notWinnerSinceOut = false;
                _deadWinnerSince = -1f;
            }
            else if (_outEver)
            {
                if (!winner) { _notWinnerSinceOut = true; _deadWinnerSince = -1f; }
                else if (_notWinnerSinceOut)
                {
                    if (_deadWinnerSince < 0f) _deadWinnerSince = now;
                    if (judge && now - _deadWinnerSince >= WinnerFor) return Saw.DeadWinner;
                }
            }
            return Saw.Nothing;
        }

        /// <summary>A Liar's Deck seat alive with at least six pulls made, held.</summary>
        internal bool PastTheCylinder(float now, bool dead, int pulls)
        {
            if (dead || pulls < Chambers) { _pastSince = -1f; return false; }
            if (_pastSince < 0f) _pastSince = now;
            return now - _pastSince >= HeldFor;
        }

        /// <summary>A Liar's Poker seat with more bullets than the revolver holds, held.</summary>
        internal bool TooManyBullets(float now, int bullets)
        {
            if (bullets <= MostBullets) { _bulletsSince = -1f; return false; }
            if (_bulletsSince < 0f) _bulletsSince = now;
            return now - _bulletsSince >= HeldFor;
        }

        /// <summary>A seat that is out holding the turn in a live match, for a long time. Noted only.</summary>
        internal bool OutHoldingTurn(float now, bool live, bool dead, bool turn)
        {
            if (!live || !dead || !turn) { _outTurnSince = -1f; return false; }
            if (_outTurnSince < 0f) _outTurnSince = now;
            return now - _outTurnSince >= OutTurnFor;
        }
    }

    private static void Look(float now)
    {
        var m = Manager.Instance;
        if (m == null) return;

        bool live = Live(m);
        bool judge = FirstHost();
        var kind = TableHand.Playing();

        foreach (var p in TableHand.Seats(m))
        {
            if (p == null) continue;
            bool mine;
            try { mine = p.isOwned; } catch { continue; }
            if (mine) continue;   // this copy's own seat: nothing to learn about the host from it

            uint key = p.netId;
            if (!_seats.TryGetValue(key, out var seat)) _seats[key] = seat = new Seat();

            bool dead = p.Dead;
            switch (seat.Life(now, live, dead, p.Winner, judge))
            {
                case Seat.Saw.Revived:
                    Trip("a player who was out played on in a live match", SawRevived);
                    return;
                case Seat.Saw.DeadWinner:
                    Trip("a player who was out was named the winner", SawDeadWinner);
                    return;
            }

            if (judge && kind == TableHand.Kind.Deck)
            {
                var deck = p.GetComponent<DeckGameplay>();
                if (deck != null)
                {
                    int pulls = deck.NetworkcurrentRevolver;
                    if (seat.PastTheCylinder(now, dead, pulls))
                    {
                        Trip($"a seat alive after {pulls} pulls of a six-chamber cylinder", SawRevolver, pulls);
                        return;
                    }
                }
            }

            if (judge && kind == TableHand.Kind.Texas)
            {
                var texas = p.GetComponent<TexasGamePlay>();
                if (texas != null)
                {
                    int bullets = texas.NetworkBullets;
                    if (seat.TooManyBullets(now, bullets))
                    {
                        Trip($"a seat loaded with {bullets} bullets", SawBullets, bullets);
                        return;
                    }
                }
            }

            bool turn;
            try { turn = p.HaveTurn; } catch { turn = false; }
            if (!seat.OutTurnSaid && seat.OutHoldingTurn(now, live, dead, turn))
            {
                // Not judged: a stalled round can leave the turn with a seat that is out, and that
                // is the game's own trouble, not proof of anything. Noted for the bug report.
                seat.OutTurnSaid = true;
                Plugin.Log.LogInfo($"{Tag} host check: seat {SafeSlot(p)} is out and has held the turn for {OutTurnFor:0} s (noted only)");
            }
        }
    }

    private static int SafeSlot(PlayerStats p)
    {
        try { return p.Slot; } catch { return -1; }
    }

    // ------------------------------------------------------------------ the dice shown at a reveal

    /// <summary>
    /// The dice the host shows for one player at a reveal, on a client, before the game draws them.
    /// The game's own handler runs either way: this only counts them.
    /// </summary>
    [FairPatch("players count the dice the host shows at a reveal")]
    [HarmonyPatch]
    internal static class Reveal
    {
        /// <summary>
        /// The game's handlers for the remote calls that carry a seat's dice. Their names hold
        /// characters the interop layer can't keep, so they are found by shape: the reveal is told
        /// apart from the dice dealt to their owner by what it carries (<see cref="DiceIn"/>).
        /// </summary>
        [HarmonyTargetMethods]
        private static IEnumerable<System.Reflection.MethodBase> Targets()
        {
            var found = new List<System.Reflection.MethodBase>();
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(DiceGamePlay)))
            {
                if (!m.IsStatic) continue;
                var ps = m.GetParameters();
                if (ps.Length != 3 || ps[0].ParameterType != typeof(NetworkBehaviour) || ps[1].ParameterType != typeof(NetworkReader)
                    || ps[2].ParameterType != typeof(NetworkConnectionToClient)) continue;
                if (m.Name.StartsWith("InvokeUserCode_RpcSetAllDice", StringComparison.Ordinal)
                    || m.Name.StartsWith("InvokeUserCode_TargetSetDiceValues", StringComparison.Ordinal)
                    || m.Name.StartsWith("Method_", StringComparison.Ordinal))
                    found.Add(m);
            }
            if (found.Count == 0) throw new MissingMethodException("DiceGamePlay", "the dice remote calls");
            return found;
        }

        [HarmonyPrefix]
        private static void Before(NetworkReader __1)
        {
            try
            {
                if (!_armed || _hostChanged || NetworkServer.active || __1 == null) return;
                int n = DiceIn(__1);
                if (n <= MostDice || !FirstHost()) return;
                Trip($"{n} dice shown for one player", SawDice, n);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"{Tag} host check: could not count a reveal: {e.Message}"); }
        }
    }

    /// <summary>
    /// How many dice a reveal carries, read without moving the reader: the player's name, then
    /// the list of dice. -1 unless the message is exactly that and nothing else, so a format this
    /// build does not expect is never judged. The dice dealt to their owner (a list alone, at most
    /// five) can never read as more than three this way, so only a reveal can count past five.
    /// </summary>
    private static int DiceIn(NetworkReader reader)
    {
        int start = reader.Position;
        try
        {
            NetworkReaderExtensions.ReadString(reader);
            int n = NetworkReaderExtensions.ReadInt(reader);
            if (n < 0 || n > 4096) return -1;
            return reader.Remaining == n * 4 ? n : -1;
        }
        catch { return -1; }
        finally { reader.Position = start; }
    }

    // ------------------------------------------------------------------ awards

    /// <summary>
    /// The host's awards, before they go up. A host that took the table over partway through a
    /// match never has the whole match to give awards for, so this build's own host never sends
    /// them then. Arriving anyway from a host running this build, they are not shown, and the
    /// player leaves. After a migration this copy could not follow, they are just not shown.
    /// </summary>
    [FairPatch("no awards from a host who took the match over")]
    [HarmonyPatch(typeof(AwardPanel), "Received")]
    internal static class Awards
    {
        [HarmonyPrefix]
        private static bool Before(int from)
        {
            try
            {
                if (from != ModNet.FromHost || NetworkServer.active) return true;
                if (_hostChanged && _armed)
                {
                    Trip("awards for a match the host took over partway through", SawAwards);
                    return false;
                }
                if (MigrationThisMatch)
                {
                    Plugin.Log.LogInfo($"{Tag} host check: awards after a host migration are not shown");
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} host check: could not look at the awards: {e.Message}");
                return true;
            }
        }
    }

    // ------------------------------------------------------------------ leaving

    private static readonly HashSet<ulong> _tripped = new();
    private static ulong _lastTripped;
    private static float _leaveAt;
    private static float _shownUntil;
    private static ModCanvas.Line _reasonLine, _leftLine;

    /// <summary>Developer switch: say and log a trip, but stay at the table and block nobody.</summary>
    internal static bool DryForTest { get; set; }

    /// <summary>How many times this copy has left a host, for the test driver.</summary>
    internal static int Trips { get; private set; }

    /// <summary>
    /// A rule the host's table broke. <paramref name="line"/> is the English for the player, with
    /// <paramref name="args"/> for its numbers; <paramref name="what"/> is for the log.
    /// </summary>
    private static void Trip(string what, string line, params object[] args)
    {
        ulong host = _host;
        if (host == 0 || !_tripped.Add(host)) return;
        _lastTripped = host;
        Trips++;

        bool dry = DryForTest && Fair.TestsAllowed;
        Plugin.Log.LogWarning($"{Tag} host check: {what} - {(dry ? "noted only (test switch)" : "leaving the table and blocking this host")}");
        Show(Say(line, args), Say(LeftLine));
        if (dry) return;

        if (Loopback.Active) Plugin.Log.LogInfo($"{Tag} host check: loopback harness, nobody is blocked");
        else if (!BlockList.Add(host)) Plugin.Log.LogInfo($"{Tag} host check: the host was already blocked");

        _leaveAt = Time.unscaledTime + 0.5f;   // out of whatever game code is running now
    }

    /// <summary>Leave through the game's own Leave: the match's, or the lobby's.</summary>
    private static void Leave()
    {
        try
        {
            if (NetworkServer.active) return;   // this copy became the host in the meantime: nothing to leave
            var m = Manager.Instance;
            if (m != null) { m.ReturnMenu(); return; }
            var room = LobbyController.Instance;
            if (room != null) { room.ReturnMenu(); return; }
            if (NetworkClient.active) Fair.Net?.StopClient();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"{Tag} host check: could not leave through the game's own path: {e.Message}");
            try { if (NetworkClient.active) Fair.Net?.StopClient(); } catch { }
        }
    }

    /// <summary>A line in the game's language, or in English where the font can't draw it.</summary>
    private static string Say(string english, params object[] args)
    {
        string said = Words.T(english);
        if (!ModCanvas.Fits(said)) said = english;
        try { return args.Length == 0 ? said : string.Format(CultureInfo.InvariantCulture, said, args); }
        catch { return string.Format(CultureInfo.InvariantCulture, english, args); }
    }

    private static void Show(string reason, string left = null)
    {
        try
        {
            if (!ModCanvas.Ready && !ModCanvas.Build()) return;
            if (_reasonLine == null)
            {
                var top = new Vector2(0.5f, 1f);
                _reasonLine = ModCanvas.NewLine("hostcheck", top, 44f, 28f, ModCanvas.Warning, rule: true);
                _reasonLine.Box.anchoredPosition = new Vector2(0f, -150f);
                _leftLine = ModCanvas.NewLine("hostcheck-left", top, 34f, 22f, ModCanvas.Cream);
                _leftLine.Box.anchoredPosition = new Vector2(0f, -196f);
            }

            _reasonLine.Set(reason);
            _leftLine.Set(reason == null || (DryForTest && Fair.TestsAllowed) ? null : left);
            _shownUntil = reason == null ? 0f : Time.unscaledTime + ShowFor;
        }
        catch (Exception e) { ModCanvas.Fail("the host check line", e); }
    }

    // ------------------------------------------------------------------ developer tests

    /// <summary>
    /// "fair hostcheck state | self | dry on|off | forgive". Everything here runs inside this copy:
    /// "self" feeds the judging made-up seats on a made-up clock and checks each answer.
    /// </summary>
    private static void Drive(string rest)
    {
        string[] words = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string what = words.Length > 0 ? words[0] : "state";
        switch (what)
        {
            case "state":
                Plugin.Log.LogInfo($"{Tag} test: host check {(_armed ? "watching" : "paused")} ({_why}); first host={FirstHost()} " +
                                   $"host changed={_hostChanged} migration={_sawMigration} seats={_seats.Count} trips={Trips} dry={DryForTest}");
                return;
            case "self":
                SelfTest();
                return;
            case "dry":
                DryForTest = words.Length < 2 || words[1] != "off";
                Plugin.Log.LogInfo($"{Tag} test: host check trips are {(DryForTest ? "noted only" : "acted on")}");
                return;
            case "forgive":
                if (_lastTripped != 0)
                {
                    if (!Loopback.Active) BlockList.Remove(_lastTripped);
                    _tripped.Remove(_lastTripped);
                    _lastTripped = 0;
                }
                _tripped.Clear();
                Plugin.Log.LogInfo($"{Tag} test: the last host left is forgiven");
                return;
            default:
                Plugin.Log.LogWarning($"{Tag} test: hostcheck state | self | dry on|off | forgive");
                return;
        }
    }

    /// <summary>The judging, fed made-up seats on a made-up clock. Nothing on the table changes.</summary>
    internal static void SelfTest()
    {
        int failed = 0;
        void Check(string test, bool pass, string detail)
        {
            Fair.Result("hostcheck " + test, pass, detail);
            if (!pass) failed++;
        }

        // Pulls: five and alive is the longest honest streak; six alive is past the cylinder.
        {
            var s = new Seat();
            bool tripped = false;
            for (float t = 0f; t < 10f; t += 0.25f) tripped |= s.PastTheCylinder(t, false, 5);
            Check("revolver legal", !tripped, "alive at 5 pulls for 10 s: no trip");

            s = new Seat();
            tripped = false;
            for (float t = 0f; t < 10f; t += 0.25f) tripped |= s.PastTheCylinder(t, true, 6);
            Check("revolver dead", !tripped, "out at 6 pulls: no trip");

            s = new Seat();
            bool early = s.PastTheCylinder(0f, false, 7) || s.PastTheCylinder(1f, false, 7);
            bool late = s.PastTheCylinder(2.25f, false, 7);
            Check("revolver past", !early && late, $"alive at 7 pulls: trips after {HeldFor} s held, not before");

            s = new Seat();
            tripped = false;
            for (float t = 0f; t < 10f; t += 0.5f) tripped |= s.PastTheCylinder(t, false, (int)t % 2 == 0 ? 6 : 3);
            Check("revolver flicker", !tripped, "a value that never holds: no trip");
        }

        // Bullets: eight is the most there is.
        {
            var s = new Seat();
            bool tripped = false;
            for (float t = 0f; t < 10f; t += 0.25f) tripped |= s.TooManyBullets(t, MostBullets);
            Check("bullets legal", !tripped, "8 bullets held 10 s: no trip");
            s = new Seat();
            tripped = false;
            for (float t = 0f; t < 3f; t += 0.25f) tripped |= s.TooManyBullets(t, 9);
            Check("bullets past", tripped, "9 bullets held: trips");
        }

        // Back from the dead.
        Check("revive", Run(new[] { (0f, true, true, false), (3f, true, false, false), (8f, true, false, false) }, true) == Seat.Saw.Revived,
              "out 3 s in a live match, then alive 5 s: trips");
        Check("revive end of match", Run(new[] { (0f, true, true, false), (3f, false, false, true), (20f, false, false, true) }, true) == Seat.Saw.Nothing,
              "out, then stood back up as the winner as the match ends: no trip");
        Check("revive end late", Run(new[] { (0f, true, true, false), (3f, true, false, true), (4f, false, false, true), (20f, false, false, true) }, true) == Seat.Saw.Nothing,
              "the winner stood up a second before the end shows: no trip");
        Check("revive blink", Run(new[] { (0f, true, true, false), (1f, true, false, false), (8f, true, false, false) }, true) == Seat.Saw.Nothing,
              "out for under 2 s, then alive: no trip");
        Check("revive judged off", Run(new[] { (0f, true, true, false), (3f, true, false, false), (8f, true, false, false) }, false) == Seat.Saw.Nothing,
              "the same revival at a host judged on awards only: no trip");
        Check("revive after pause", Run(new[] { (0f, true, true, false), (3f, false, true, false), (4f, true, false, false), (12f, true, false, false) }, true) == Seat.Saw.Nothing,
              "out, a pause in the match, then alive: no trip (only one live stretch counts)");

        // A winner who was out.
        Check("dead winner", Run(new[] { (0f, true, true, false), (3f, false, true, false), (4f, false, true, true), (8f, false, true, true) }, true) == Seat.Saw.DeadWinner,
              "out, then named winner while still out: trips");
        Check("winner who died after", Run(new[] { (0f, true, false, false), (2f, false, false, true), (3f, false, true, true), (10f, false, true, true) }, true) == Seat.Saw.Nothing,
              "named winner alive, out afterwards: no trip");
        Check("winner stood up", Run(new[] { (0f, true, true, false), (3f, false, true, false), (4f, false, false, true), (10f, false, false, true) }, true) == Seat.Saw.Nothing,
              "out, then named winner and stood back up together: no trip");
        Check("old winner flag", Run(new[] { (0f, true, true, true), (5f, true, true, true), (10f, false, true, true) }, true) == Seat.Saw.Nothing,
              "a winner flag already up before the seat went out: no trip");

        // Out holding the turn is only ever noted.
        {
            var s = new Seat();
            bool early = s.OutHoldingTurn(0f, true, true, true) || s.OutHoldingTurn(60f, true, true, true);
            bool late = s.OutHoldingTurn(OutTurnFor + 1f, true, true, true);
            Check("out holding turn", !early && late, $"noted after {OutTurnFor:0} s, not before");
        }

        // The dice count reads the reveal as the game writes it, and nothing else.
        Check("dice limit", MostDice == 5, "a cup holds 5");

        Plugin.Log.LogInfo($"{Tag} test: host check self-test done, {failed} failed");
    }

    /// <summary>Feed one seat a timeline of (time, live, dead, winner), stepping a quarter second.</summary>
    private static Seat.Saw Run((float T, bool Live, bool Dead, bool Winner)[] steps, bool judge)
    {
        var s = new Seat();
        for (int i = 0; i < steps.Length; i++)
        {
            float end = i + 1 < steps.Length ? steps[i + 1].T : steps[i].T + 0.01f;
            for (float t = steps[i].T; t < end; t += 0.25f)
            {
                var saw = s.Life(t, steps[i].Live, steps[i].Dead, steps[i].Winner, judge);
                if (saw != Seat.Saw.Nothing) return saw;
            }
        }
        return Seat.Saw.Nothing;
    }
}
