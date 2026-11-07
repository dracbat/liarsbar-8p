using System;
using HarmonyLib;
using Mirror;
using Steamworks;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Two-Card Poker: a whole table the game still carries under its own game mode 3 and never puts
/// on a menu. Two cards each from a deck of 21, a swap or a pass on your turn, then challenges,
/// liar calls and face-offs, and the best hand gets the shotgun and picks who it points at. Its
/// table, cards, sounds and rules are all the game's; it only lacks a way in. The lobby's Poker
/// tab gets one: arrows that step between the released Liar's Poker and this.
///
/// <b>What is never touched.</b> The lobby's game mode stays Liar's Poker (5) the whole time, so
/// the lobby list, its filters and Quick Join see nothing new, and nothing ever writes LastMode:
/// the game feeds that back the next time any lobby opens, with or without the mod, and a saved 3
/// leaves a lobby on no tab at all. The switch to 3 happens only at the door, in a prefix on the
/// network manager's StartGame, on the host, when the host chose this table, everyone at it runs
/// this very build of the mod (<see cref="Table.Modded"/>, then each member's version, as the
/// house rules ask) and ranked is nowhere near. An older build counts as modded but has never
/// heard of this table: its lobby would show the released game and its player would be carried
/// here unwarned. The bar reads the mode from there and tells every client itself. The lobby that
/// comes back after a match played here reads LastMode, which still says Liar's Poker, and the
/// host's choice is put back on top of it; after any other match, and in a fresh lobby, the Poker
/// tab starts on the released game.
///
/// <b>One character.</b> The table was built before the game had more than one model for it:
/// every bar lists the same Scubby, once or seven times, and a player whose character sits past
/// the end of that list would throw at the deal. The host's list gets the same Scubby repeated
/// until every character has an entry, so everybody plays as Scubby here.
///
/// <b>The host leaving.</b> The game restarts a round after a host migration for every table but
/// this one, although the table has its own restart. The new host starts it, as the game does for
/// the others.
///
/// <b>How the table hears.</b> As the house rules do: the Steam lobby carries <see cref="Key"/> for
/// anyone reading the lobby, and <see cref="ModNet"/> tells the copies at the table, so a client's
/// lobby shows the table the host chose. A client that hears nothing shows the released game.
/// </summary>
internal static class TwoCardPoker
{
    // ------------------------------------------------------------------ every word a player sees

    /// <summary>The table's name, on the plate under the Poker tab and on its rules page.</summary>
    internal const string Name = "TWO-CARD POKER";

    /// <summary>To the whole table's chat, so not translated: everyone there may read a different language.</summary>
    private const string RefusedToTheRoom = "Two-Card Poker is off this match: not everyone has this version of the mod";

    /// <summary>The same, for a table with bots at it.</summary>
    private const string NoBotsToTheRoom = "Two-Card Poker is off this match: it plays without bots";

    // ------------------------------------------------------------------ the table

    /// <summary>The Steam lobby key: "1" for this table, "0" or missing for the released Liar's Poker.</summary>
    internal const string Key = "lb8p_table";

    /// <summary>The ModNet body's layout. Raise it if the body ever means something else.</summary>
    private const byte Layout = 1;

    /// <summary>
    /// How long a lobby can sit on this table without counting as modded before it goes back to the
    /// released one. The same grace as Liar's Deck 2's, for the same reason: a player still arriving
    /// makes the table look unmodded for a moment.
    /// </summary>
    private const float NotModdedGrace = 20f;

    /// <summary>Every character the game has, at least: the host's list is padded to this many.</summary>
    private const int Characters = 11;

    /// <summary>Host: this lobby's choice. The released game whenever a lobby starts.</summary>
    internal static bool Chosen { get; private set; }

    /// <summary>
    /// Set by <see cref="Plugin"/>: this class's patches went on, the door among them. Without them
    /// the lobby could offer the table and the match would play the released game, so nobody can
    /// choose it. The log's own patches are apart (<see cref="MatchLog"/>), so a game update that
    /// renames one of those costs only the log.
    /// </summary>
    internal static bool Patched;

    /// <summary>
    /// The session whose last match started at this table, for the trip back to the lobby, or -1.
    /// Set only at the door as the swap happens, and used up when the lobby comes back, so a match
    /// that played the released game never brings the choice back.
    /// </summary>
    private static int _keptFor = -1;

    private static int _session = -1;
    private static int _published = -1;
    private static int _sent = -1;
    private static float _notModdedSince = -1f;
    private static bool _sameBuild;
    private static string _whyNot = "not looked yet";
    private static float _nextCheck;
    private static bool _heard;
    private static bool _heardTable;
    private static float _nextRead;
    private static bool _swapped;
    private static IntPtr _paddedFor;

    /// <summary>
    /// Any copy: the lobby is on this table. On the host, its own choice; on a client, what the host
    /// said, over the channel or on the lobby key. Only ever while the lobby is on the Poker tab.
    /// </summary>
    internal static bool Shown
    {
        get
        {
            var room = LobbyController.Instance;
            if (room == null || room.Mode != CustomNetworkManager.GameMode.LiarsTexas || RankedGuard.Near) return false;
            return NetworkServer.active ? Chosen : _heardTable;
        }
    }

    /// <summary>Any copy: this match is played at this table. Asked of the people sitting down, as everything is.</summary>
    internal static bool Playing => TableHand.Playing() == TableHand.Kind.Poker;

    internal static void Install() => ModNet.On(ModKind.HiddenTable, Received);

    /// <summary>
    /// No bots at this table, as at Liar's Poker and Liar's Slots. A bot cannot take its shot
    /// here, so a bot that won a showdown would hold the table for ever. True while the lobby is on this table, for anything that adds bots.
    /// </summary>
    internal static bool RefusesBots => Shown;

    private static bool BotsSeated(CustomNetworkManager nm)
    {
        if (nm.GamePlayers == null) return false;
        foreach (var p in nm.GamePlayers)
            if (BotManager.IsBot(p)) return true;
        return false;
    }

    // ------------------------------------------------------------------ choosing

    /// <summary>
    /// A host at a table where everybody runs this very build, and nowhere near ranked. The build
    /// check is the one from once a second (<see cref="CheckBuilds"/>); the door asks it afresh.
    /// </summary>
    internal static bool Allowed(LobbyController room)
    {
        if (room == null || !room.isServer || RankedGuard.Near || !Table.Modded || !_sameBuild) return false;
        var nm = room.manager;
        return nm != null && !nm.Matchmaking && !nm.isVelvetRoom;
    }

    /// <summary>
    /// Every human at the table runs this very build, so every one of them knows this table. An
    /// older build of the mod counts as modded, but it reads none of what the host says about the
    /// table and shows the released Liar's Poker in its lobby. Bots are not Steam members and never
    /// count; in the loopback harness every copy is this build. Asks Steam, after the ranked check.
    /// </summary>
    private static bool SameBuild(out string why)
    {
        if (RankedGuard.Near) { why = "ranked"; return false; }
        if (!Table.Modded) { why = $"not a modded table ({Table.Why})"; return false; }
        if (Loopback.Active) { why = ""; return true; }

        if (!Table.Lobby(out var lobby, out why)) return false;

        int members = SteamMatchmaking.GetNumLobbyMembers(lobby);
        if (members <= 0) { why = "Steam lists nobody in the lobby"; return false; }
        for (int i = 0; i < members; i++)
        {
            var member = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (SteamMatchmaking.GetLobbyMemberData(lobby, member, VersionCheck.Key) == Plugin.Version) continue;

            why = $"member {i + 1} of {members} runs another version";
            return false;
        }

        why = "";
        return true;
    }

    /// <summary>Host, in the lobby: <see cref="SameBuild"/> once a second, as the table's own check is.</summary>
    private static void CheckBuilds()
    {
        float now = Time.unscaledTime;
        if (now < _nextCheck) return;
        _nextCheck = now + 1f;

        bool same = SameBuild(out string why);
        if (same != _sameBuild && Chosen)
            Plugin.Log.LogInfo($"[twocard] everyone at the table runs this build: {(same ? "yes" : $"no - {why}")}");
        _sameBuild = same;
        _whyNot = why;
    }

    /// <summary>
    /// The host can step the table now: on the Poker tab, before the match is on its way, with the
    /// door patched and the lobby's plate, arrows and rules page built, so a controller's bumpers
    /// can never choose a table the lobby could not show.
    /// </summary>
    internal static bool CanStep(LobbyController room) =>
        Patched && TwoCardLobby.Built && Allowed(room) && room.Mode == CustomNetworkManager.GameMode.LiarsTexas &&
        Manager.Instance == null && !HouseRules.Locked(room);

    /// <summary>
    /// The host steps between the two tables, from the arrows or a controller's bumpers. There are
    /// two, so either way is the other one. False if nothing changed.
    /// </summary>
    internal static bool Step(string how)
    {
        var room = LobbyController.Instance;
        if (!CanStep(room)) return false;

        Chosen = !Chosen;
        _notModdedSince = -1f;

        // A host who picked a table chose this lobby: Quick Join's merge must not move it.
        QuickMatch.StopLooking();

        Plugin.Log.LogInfo($"[twocard] host chose {(Chosen ? "Two-Card Poker" : "the released Liar's Poker")} ({how})");
        Publish();
        return true;
    }

    /// <summary>A click on a game tab starts that tab afresh, as the deck tab goes back to Basic.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.ChangeGameMode))]
    private static void TabClicked(LobbyController __instance)
    {
        try
        {
            if (__instance == null || !__instance.isServer || !Chosen) return;
            Chosen = false;
            Plugin.Log.LogInfo("[twocard] the game mode was set - the Poker tab is on the released Liar's Poker");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not follow a tab change: {e.Message}"); }
    }

    // The controller's bumpers step the deck and dice variants; on the Poker tab they did nothing.
    // Liar's Deck 2's prefixes on the same two steps stand aside unless the lobby is on the deck tab.

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.ChangeNextDeckOrDiceMode))]
    private static bool PadNext(LobbyController __instance) => !Pad(__instance, "controller, next");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.ChangePrevDeckOrDiceMode))]
    private static bool PadPrevious(LobbyController __instance) => !Pad(__instance, "controller, previous");

    /// <summary>True if the press was the Poker tab's, taken whole.</summary>
    private static bool Pad(LobbyController room, string how)
    {
        try
        {
            if (room == null || room.Mode != CustomNetworkManager.GameMode.LiarsTexas) return false;
            if (Step(how)) TwoCardLobby.StepSound();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[twocard] could not step the Poker tab from a controller: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ the lobby

    /// <summary>
    /// The lobby as it comes back from a match. The game has just put back the mode LastMode
    /// remembers, which is Liar's Poker, through the same call a tab click makes, so the choice
    /// has just been dropped. It goes back on top only after a match that started at this table:
    /// the same host, the same session, and the lobby on the Poker tab. After any other match, and
    /// in a new session, the Poker tab starts afresh. The network manager's mode is the one to read
    /// here: the lobby copies it into its own synced one on its first frame, after this.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void BackFromMatch(LobbyController __instance)
    {
        try
        {
            _swapped = false;
            if (__instance == null || !__instance.isServer) return;

            bool kept = _keptFor == Table.Session;
            _keptFor = -1;
            if (!kept)
            {
                Chosen = false;
                return;
            }
            var nm = __instance.manager;
            if (!Allowed(__instance) || nm == null || nm.Mode != CustomNetworkManager.GameMode.LiarsTexas) return;

            Chosen = true;
            Plugin.Log.LogInfo("[twocard] back from a Two-Card Poker match - the lobby stays on it");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not keep Two-Card Poker: {e.Message}"); }
    }

    /// <summary>
    /// The lobby copies the network manager's mode into its own synced one every frame on the host.
    /// Between the switch at the door and the bar loading, that would show every lobby screen mode 3
    /// for a moment, a tab that does not exist; it keeps saying Liar's Poker instead.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), "Update")]
    private static void KeepLobbyMode(LobbyController __instance)
    {
        if (!_swapped) return;
        try
        {
            if (__instance != null && __instance.isServer && __instance.Mode == CustomNetworkManager.GameMode.LiarsPoker)
                __instance.NetworkMode = CustomNetworkManager.GameMode.LiarsTexas;
        }
        catch (Exception e)
        {
            _swapped = false;
            Plugin.Log.LogWarning($"[twocard] could not keep the lobby on Liar's Poker: {e.Message}");
        }
    }

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        var nm = Net();
        if (RankedGuard.Near || (nm != null && (nm.Matchmaking || nm.isVelvetRoom)))
        {
            Chosen = false;
            _keptFor = -1;
            _sameBuild = false;
            _heard = false;
            _heardTable = false;
            return;
        }

        // A new lobby, joining one or leaving one: the released game again, until told otherwise.
        int session = Table.Session;
        if (session != _session)
        {
            _session = session;
            _published = -1;
            _sent = -1;
            _heard = false;
            _heardTable = false;
            _notModdedSince = -1f;
            _sameBuild = false;
            _nextCheck = 0f;
            if (_keptFor != session && Manager.Instance == null) Chosen = false;
        }

        // The swap at the door holds until the bar is up: the lobby stays on screen while the bar
        // loads, and in that time it must go on saying Liar's Poker, to its own screen and to the
        // table, or the table would be told the released game for the length of the match.
        if (Manager.Instance != null) { _swapped = false; return; }

        if (NetworkServer.active)
        {
            CheckBuilds();
            FailClosed();
            Publish();
        }
        else if (NetworkClient.active) ClientTick();
        else AtTheMenu(nm);
    }

    /// <summary>
    /// No network and the menu on screen. A host who quits a match at this table, and a table that
    /// ends after a host migration, leave the bar by stopping the host, with no scene change for
    /// <see cref="LeavingTheBar"/> to catch, so the network manager could still say mode 3 here,
    /// which nothing on the menu knows. Only on the menu itself: a copy in the middle of a host
    /// migration is still in the bar, and the new host needs the 3 it was handed.
    /// </summary>
    private static void AtTheMenu(CustomNetworkManager nm)
    {
        if (nm == null || nm.Mode != CustomNetworkManager.GameMode.LiarsPoker) return;
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != Matchmaking.Menu) return;

        nm.Mode = CustomNetworkManager.GameMode.LiarsTexas;
        Plugin.Log.LogInfo("[twocard] back at the menu - the network manager says Liar's Poker again");
    }

    /// <summary>
    /// A lobby on this table that has stopped counting as modded, or where somebody runs another
    /// build, goes back to the released game, after the same grace Liar's Deck 2 gives, so a player
    /// arriving does not undo the choice.
    /// </summary>
    private static void FailClosed()
    {
        var room = LobbyController.Instance;
        if (!Chosen || room == null || Allowed(room))
        {
            _notModdedSince = -1f;
            return;
        }

        float now = Time.unscaledTime;
        if (_notModdedSince < 0f) { _notModdedSince = now; return; }
        if (now - _notModdedSince < NotModdedGrace) return;

        _notModdedSince = -1f;
        _keptFor = -1;
        Chosen = false;
        Plugin.Log.LogInfo($"[twocard] the table can no longer play it ({(_whyNot.Length > 0 ? _whyNot : Table.Why)}) - back to the released Liar's Poker");
    }

    /// <summary>The choice on the Steam lobby, for anyone reading it, and over the channel, for the table.</summary>
    private static void Publish()
    {
        int table = Shown ? 1 : 0;

        if (_published != table && Table.Hosted(out var lobby) &&
            SteamMatchmaking.SetLobbyData(lobby, Key, table == 1 ? "1" : "0"))
        {
            _published = table;
            Plugin.Log.LogInfo($"[twocard] the lobby says {(table == 1 ? "Two-Card Poker" : "the released Liar's Poker")}");
        }

        if (_sent != table && ModNet.Keep(ModKind.HiddenTable, new ModWriter().U8(Layout).U8((byte)table))) _sent = table;
    }

    /// <summary>
    /// A client with no word over the channel reads the lobby key once a second, as the house rules
    /// do, and shows what it says. Steam is asked only after the ranked check, and only about the
    /// table's own lobby. Once the channel speaks, it alone is listened to.
    /// </summary>
    private static void ClientTick()
    {
        if (_heard || Loopback.Active) return;

        float now = Time.unscaledTime;
        if (now < _nextRead) return;
        _nextRead = now + 1f;

        if (!Table.Modded || !Table.Lobby(out var lobby, out _)) { _heardTable = false; return; }
        _heardTable = SteamMatchmaking.GetLobbyData(lobby, Key) == "1";
    }

    private static void Received(int from, ModReader body)
    {
        if (from != ModNet.FromHost || NetworkServer.active || RankedGuard.Near) return;

        int layout = body.U8();
        int table = body.U8();
        if (body.Failed || layout != Layout) return;

        bool was = _heardTable;
        _heard = true;
        _heardTable = table == 1;
        if (was != _heardTable)
            Plugin.Log.LogInfo($"[twocard] the host says the table is {(_heardTable ? "Two-Card Poker" : "the released Liar's Poker")}");
    }

    // ------------------------------------------------------------------ the door

    /// <summary>
    /// The only place mode 3 is ever set: the network manager's StartGame, on the host, the moment
    /// the bar is asked for. Everything is checked again here, because the lobby could have
    /// changed under the choice: anything but an unranked table where everyone runs this build
    /// plays the released game. The bar's own way back to the lobby goes through StartGame too,
    /// and that is no door: nothing here happens while a bar is up.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.StartGame))]
    private static void AtTheDoor(CustomNetworkManager __instance)
    {
        try
        {
            if (__instance == null || !NetworkServer.active || Manager.Instance != null) return;

            // A start that was swapped and then never left the lobby: Liar's Poker again first,
            // so a host who has since stepped back to it gets it.
            if (_swapped && __instance.Mode == CustomNetworkManager.GameMode.LiarsPoker)
                __instance.Mode = CustomNetworkManager.GameMode.LiarsTexas;
            _swapped = false;

            // Only a match that really starts at this table brings the lobby back on it.
            _keptFor = -1;

            if (!Chosen || __instance.Mode != CustomNetworkManager.GameMode.LiarsTexas) return;
            if (__instance.Matchmaking || __instance.isVelvetRoom || RankedGuard.Near) return;

            if (!TwoCardLobby.Built)
            {
                Plugin.Log.LogWarning("[twocard] not this match, the table plays the released Liar's Poker: this lobby never showed it");
                return;
            }

            if (!SameBuild(out string why))
            {
                Matchmaking.TellTheRoom(RefusedToTheRoom);
                Plugin.Log.LogWarning($"[twocard] not this match, the table plays the released Liar's Poker: {why}");
                return;
            }

            if (BotsSeated(__instance))
            {
                Matchmaking.TellTheRoom(NoBotsToTheRoom);
                Plugin.Log.LogWarning("[twocard] not this match, the table plays the released Liar's Poker: there are bots at it");
                return;
            }

            __instance.Mode = CustomNetworkManager.GameMode.LiarsPoker;
            _swapped = true;
            _keptFor = Table.Session;
            Plugin.Log.LogInfo("[twocard] the match starts at Two-Card Poker (game mode 3); the lobby keeps saying Liar's Poker");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[twocard] could not start Two-Card Poker, the table plays the released Liar's Poker: {e.Message}");
            try { if (__instance != null && _swapped) __instance.Mode = CustomNetworkManager.GameMode.LiarsTexas; } catch { }
            _swapped = false;
        }
    }

    /// <summary>
    /// Any copy leaving a bar that played this table: the network manager says Liar's Poker again
    /// before the next scene loads. The host set 3 at the door, and a host that took over after a
    /// migration was handed it; the lobby and the menu that load next read the mode before the
    /// lobby sets its own, and 3 has no tab there, no list and no place in any search.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CustomNetworkManager), nameof(CustomNetworkManager.OnClientChangeScene))]
    private static void LeavingTheBar(CustomNetworkManager __instance)
    {
        try
        {
            if (__instance == null || Manager.Instance == null) return;
            if (__instance.Mode != CustomNetworkManager.GameMode.LiarsPoker) return;

            __instance.Mode = CustomNetworkManager.GameMode.LiarsTexas;
            Plugin.Log.LogInfo("[twocard] leaving the table - the network manager says Liar's Poker again");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not put Liar's Poker back on the way out: {e.Message}"); }
    }

    // ------------------------------------------------------------------ the bar

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Manager), "StartGame")]
    private static void BeforeSeating(Manager __instance) => Pad(__instance);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Manager), nameof(Manager.SpawnPlayerwithskin))]
    private static void BeforeSpawn(Manager __instance) => Pad(__instance);

    /// <summary>
    /// Host: the table's character list, long enough for every character. Each player sits down as
    /// the entry at their character's number, and this table's list is one Scubby in two of the
    /// bars and seven in the others, so a player past its end would throw at the start. The list
    /// is the bar's own and goes with it.
    /// </summary>
    private static void Pad(Manager m)
    {
        try
        {
            if (m == null || !NetworkServer.active || m.mode != CustomNetworkManager.GameMode.LiarsPoker) return;

            var list = m.PokerPlayerPrefabs;
            if (list == null || list.Count == 0) return;

            int want = Characters;
            var nm = Net();
            if (nm != null && nm.GamePlayers != null)
                foreach (var p in nm.GamePlayers)
                    if (p != null && p.PlayerSkin + 1 > want) want = p.PlayerSkin + 1;
            if (list.Count >= want) return;

            int had = list.Count;
            var scubby = list[0];
            while (list.Count < want) list.Add(scubby);

            if (_paddedFor != m.Pointer)
            {
                _paddedFor = m.Pointer;
                Plugin.Log.LogInfo($"[twocard] the table's character list had {had}, now {list.Count}: everyone sits down as Scubby");
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not lengthen the character list: {e.Message}"); }
    }

    /// <summary>
    /// The new host after a host migration. The game restarts the round here for every other table,
    /// from the new host's own player as it starts; this table's restart was written and never
    /// called. Mode 3 is only ever this table, so nothing else is caught.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerObjectController), "Start")]
    private static void TookOver(PlayerObjectController __instance)
    {
        try
        {
            if (__instance == null || !__instance.isServer || !__instance.isOwned) return;

            var hm = HostMigration.Instance;
            var nm = Net();
            if (hm == null || !hm.MigrationNedded || nm == null || nm.Matchmaking || RankedGuard.Near) return;
            if (nm.Mode != CustomNetworkManager.GameMode.LiarsPoker) return;

            var m = Manager.Instance;
            var game = m != null ? m.PokerGame : null;
            if (game == null) return;

            game.StartCoroutine(game.WaitforNextRoundMigration());
            Plugin.Log.LogInfo("[twocard] took over the table after the host left - the round starts again");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not restart the round after the host left: {e.Message}"); }
    }

    // ------------------------------------------------------------------ the match, for the log

    /// <summary>Server: deals this match, counted from one. Read by the test players to know a hand has ended.</summary>
    internal static int Round { get; private set; }

    /// <summary>
    /// Server: this deal's showdown is settled - a seat has taken the shotgun - and nothing more
    /// happens until the next deal. The seat that shot keeps its turn flag until then.
    /// </summary>
    internal static bool Settled => Round > 0 && _armedAt == Round;

    private static IntPtr _roundsOf;
    private static int _armedAt = -1;

    /// <summary>
    /// The match as the log tells it, on the server: deals, calls, showdowns and shots. Patched
    /// on its own, so a game update that renames one of these costs the log and the test players
    /// their way of knowing a hand has ended, and never the table.
    /// </summary>
    internal static class MatchLog
    {
        /// <summary>Every deal, on the server: the round number and who is still in. Seats, never names.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PokerGamePlayManager), "ResetRound")]
        private static void Dealt(PokerGamePlayManager __instance)
        {
            try
            {
                if (!NetworkServer.active || __instance == null) return;
                var m = Manager.Instance;
                IntPtr match = m != null ? m.Pointer : IntPtr.Zero;
                if (match != _roundsOf) { _roundsOf = match; Round = 0; _armedAt = -1; }
                Round++;
                Plugin.Log.LogInfo($"[twocard] deal {Round}: {Seats(false)}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not note a deal: {e.Message}"); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PokerGamePlayManager), nameof(PokerGamePlayManager.CallLiar))]
        private static void LiarCalled(PokerGamePlayManager __instance, GameObject callingplayer)
        {
            try
            {
                var caller = callingplayer != null ? callingplayer.GetComponent<PlayerStats>() : null;
                var challenger = __instance != null ? __instance.PokerChallanger : null;
                Plugin.Log.LogInfo($"[twocard] seat {SeatOf(caller)} calls liar on seat {SeatOf(challenger)}, who staked {__instance.PokerChallangerBet}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not note a liar call: {e.Message}"); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PokerGamePlayManager), nameof(PokerGamePlayManager.CallOverall))]
        private static void OverallCalled()
        {
            try { Plugin.Log.LogInfo($"[twocard] overall: every challenger shows - {Seats(true)}"); }
            catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not note an overall call: {e.Message}"); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.UserCode_EndTheVersusCMD))]
        private static void FaceOffEnds(PokerGamePlay __instance)
        {
            try { Plugin.Log.LogInfo($"[twocard] the face-off ends: {Seats(true)}"); }
            catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not note a face-off: {e.Message}"); }
        }

        /// <summary>
        /// The server handing a seat the gun, after a showdown. The table's code still names it a
        /// revolver, from before it had a model; the model every bar gives it is a shotgun.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.TakeAimCommand))]
        private static void Armed(PokerGamePlay __instance, bool con, bool overallaim)
        {
            try
            {
                if (!NetworkServer.active || !con) return;
                _armedAt = Round;
                // No hand here: the log shows only hands the game has turned over (see Seats), and
                // nothing here says whether this one was.
                Plugin.Log.LogInfo($"[twocard] seat {SeatOf(__instance)} wins the showdown and takes the shotgun" +
                                   (overallaim ? " (after an overall call or a face-off)" : ""));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not note a showdown: {e.Message}"); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.UserCode_HitTargetCmd))]
        private static void Shot(PokerGamePlay __instance)
        {
            try { Plugin.Log.LogInfo($"[twocard] seat {SeatOf(__instance)} fires a loaded chamber (aim {__instance.Aim})"); }
            catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not note a shot: {e.Message}"); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PokerGamePlay), nameof(PokerGamePlay.UserCode_MakeEmptyBullet__Int32))]
        private static void Empty(PokerGamePlay __instance)
        {
            try { Plugin.Log.LogInfo($"[twocard] seat {SeatOf(__instance)} fires an empty chamber"); }
            catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not note a shot: {e.Message}"); }
        }
    }

    private static int SeatOf(Component c)
    {
        var p = c != null ? c.GetComponent<PlayerStats>() : null;
        return p != null ? p.Slot : -1;
    }

    /// <summary>
    /// Who is in, and with <paramref name="hands"/> the hands the game turns over: a challenger's,
    /// at an overall call or a face-off, which show challengers and nobody else. Never another
    /// hand: the host is a player too, and the log is on the host's screen. "seat 0 out, seat 1
    /// in, seat 2 challenger 36".
    /// </summary>
    private static string Seats(bool hands)
    {
        var sb = new System.Text.StringBuilder();
        var m = Manager.Instance;
        if (m == null || m.Players == null) return "no table";
        foreach (var p in m.Players)
        {
            if (p == null) continue;
            var gp = p.GetComponent<PokerGamePlay>();
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"seat {p.Slot} ");
            if (p.Dead) { sb.Append("out"); continue; }
            if (gp == null) { sb.Append('?'); continue; }
            if (gp.Challenger) sb.Append("challenger ");
            sb.Append(hands && gp.Challenger ? gp.CardValue.ToString() : "in");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>What this copy thinks, for the log.</summary>
    internal static string Describe()
    {
        var room = LobbyController.Instance;
        var nm = Net();
        string heard = _heard ? (_heardTable ? "two-card" : "released") + " over the channel"
                     : _heardTable ? "two-card on the lobby key" : "nothing";
        return $"chosen {Chosen}, shown {Shown}, same build {(_sameBuild ? "yes" : $"no ({_whyNot})")}, heard {heard}, " +
               $"lobby mode {(room != null ? room.Mode.ToString() : "none")}, manager mode {(nm != null ? nm.Mode.ToString() : "none")}, " +
               $"kept for {_keptFor} (session {Table.Session}), playing here {Playing}";
    }

    private static CustomNetworkManager Net() =>
        NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
}
