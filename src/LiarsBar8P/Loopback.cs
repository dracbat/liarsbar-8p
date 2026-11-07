using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Runs several real copies of the game on one machine, connected to each other.
///
/// Everything about this mod has been tested with fake players made inside the host's own
/// process. They fill seats and play cards, but they have no Mirror connection at all, so
/// every connection-scoped path in the game - a message sent to one client, anything reading
/// <c>connectionToClient</c>, ownership, the client half of a round - is never exercised.
/// That is precisely where a real table breaks, and it is why "it worked with bots" has twice
/// meant nothing.
///
/// Two facts make this possible. Several copies of the game will run side by side on one
/// machine, which was simply tried and works. And the build already contains Telepathy and
/// kcp2k alongside the Steam transport, so Mirror can be pointed at 127.0.0.1 instead of
/// Steam - the developers' own <c>ArenaStressHarness</c> does exactly that, reading a
/// <c>-stresshost</c> flag and connecting to 127.0.0.1 on the active transport's port.
///
/// Steam cannot do this job: every copy signs in as the same account, so they cannot be
/// distinct members of one Steam lobby. Loopback sidesteps Steam entirely.
///
/// The role has to come from the command line or the environment, never the config file -
/// every instance reads the same config, so a role written there would make them all hosts.
/// </summary>
internal static class Loopback
{
    internal enum Role { Off, Host, Client }

    private static Role _role = Role.Off;
    private static int _port = 7777;
    private static bool _started;
    private static float _next;

    // Where a client dials: this machine, unless told otherwise. Told otherwise, a copy on one
    // machine can join a host on another over the network - which is how a Windows copy and a
    // Linux one under Proton get put at one table when both machines are signed into the same
    // Steam account, and so cannot meet in a Steam lobby.
    private static string _address = "127.0.0.1";

    /// <summary>
    /// A test role was asked for at launch. Only for the choices made while the plugin loads,
    /// before the harness has swapped its transport in; everything else asks <see cref="Active"/>.
    /// </summary>
    internal static bool Requested => _role != Role.Off;

    /// <summary>
    /// This copy is at a loopback test table: a role was asked for AND the harness's own
    /// transport is the one in use. A launch option alone is never enough, so a copy that leaves
    /// the harness for the menu is not treated as a test table anywhere it can reach.
    /// </summary>
    internal static bool Active
    {
        get
        {
            if (_role == Role.Off) return false;
            try
            {
                var t = Mirror.Transport.active;
                return t != null && t.TryCast<Mirror.TelepathyTransport>() != null;
            }
            catch { return false; }
        }
    }

    /// <summary>This copy's role at a loopback test table, or Off anywhere else.</summary>
    internal static Role Mine => Active ? _role : Role.Off;

    /// <summary>The made-up Steam ids the harness gives its copies start here: connection n is FirstId + n + 1.</summary>
    internal const ulong FirstId = 76500000000000000UL;

    /// <summary>
    /// Pick the game mode for a harness run, from <c>LIARSBAR8P_MODE</c>.
    ///
    /// The mod's fixes are meant to be mode-independent - the caps, the turn order wrap and the
    /// seat ring are all shared - but "meant to be" is not evidence, and every one of them has
    /// only ever been watched in Liar's Deck. Naming a mode here is what lets the others be
    /// played rather than reasoned about.
    ///
    /// Accepts a name or a number: LiarsDeck, LiarsDice, LiarsChaos, LiarsTexas, LiarsSpin. Unset
    /// leaves whatever the lobby already had. LiarsPoker, VelvetRoom and Arena are refused.
    /// </summary>
    private static void ChooseMode(LobbyController lobby)
    {
        try
        {
            string want = Environment.GetEnvironmentVariable("LIARSBAR8P_MODE");
            if (string.IsNullOrEmpty(want)) return;

            var names = new[] { "LiarsDeck", "LiarsDice", "LiarsChaos", "LiarsPoker",
                                "VelvetRoom", "LiarsTexas", "LiarsSpin", "Arena" };

            int index = -1;
            if (!int.TryParse(want, out index))
                for (int i = 0; i < names.Length; i++)
                    if (string.Equals(names[i], want, StringComparison.OrdinalIgnoreCase)) index = i;

            if (index < 0 || index >= names.Length)
            {
                Plugin.Log.LogWarning(
                    $"[loopback] '{want}' is not a game mode - leaving the lobby on the one it has. " +
                    $"Try one of: {string.Join(", ", names)}");
                return;
            }

            // Never the three the lobby has no tab for. The mode button saves what it is given as
            // the game's last mode, and the next lobby on this machine, with or without the mod,
            // opens on it: on no tab at all, and for Arena with nobody able to join.
            if (index == 3 || index == 4 || index == 7)
            {
                Plugin.Log.LogWarning(
                    $"[loopback] {names[index]} is not set through the lobby's mode button, which would save it as the " +
                    "game's last mode - leaving the lobby on the one it has. Two-Card Poker is LIARSBAR8P_MODE=LiarsTexas " +
                    "with LIARSBAR8P_TWOCARD=1");
                return;
            }

            // The Chaos deck has no tab: it is the deck tab's third table, and the mode button given
            // LiarsChaos leaves the lobby on Liar's Deck, which a run asking for it then played. So
            // it is asked for as the deck tab with its variant, unless the run names a variant itself.
            if (index == 2)
            {
                index = 0;
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIARSBAR8P_DECKMODE")))
                    Environment.SetEnvironmentVariable("LIARSBAR8P_DECKMODE", "2");
            }

            // Through the game's own mode button rather than by writing the SyncVar. Writing
            // NetworkMode directly looked like it worked - the field changed and the change
            // synced - and the match still started Liar's Deck, because choosing a mode also
            // sets up the sub-mode lists and whatever else the button does. Press the button.
            lobby.ChangeGameMode(index);

            // Read back rather than assumed - but the read is deliberately not treated as a
            // failure. The button does more than assign the field, and the field is not
            // necessarily the button's first act, so asking a line later can still show the
            // mode the lobby was on before. This once printed "game mode set to LiarsTexas
            // (lobby now reports LiarsChaos)" on a run that went on to play Texas perfectly,
            // which is a log accusing the game of something the game did not do. The mode a
            // match actually ran is settled afterwards, by asking the players what component
            // they are carrying - see the census - and that is what the harness checks.
            Plugin.Log.LogWarning(
                $"[loopback] game mode set to {names[index]} for this run (the lobby reads back " +
                $"as {lobby.Mode}; what the table actually plays is reported by the census)");

            ChooseSubMode(lobby);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[loopback] could not set the game mode: {e.Message}"); }
    }

    /// <summary>
    /// Pick the deck or dice variant, from <c>LIARSBAR8P_DECKMODE</c> / <c>LIARSBAR8P_DICEMODE</c>.
    ///
    /// The lobby's left and right arrows are not a decoration on top of the game mode: they
    /// choose between genuinely different tables. Liar's Deck has four of them and Liar's Dice
    /// two, and they are not the same game - one of the deck variants is dealt by an entirely
    /// separate manager with its own copy of the deal. Testing "Liar's Deck" and stopping there
    /// leaves most of what a player can actually sit down to untested.
    ///
    /// Set through the game's own arrow, pressed until the variant comes up, for the same
    /// reason the mode is: the button does more than assign the number.
    /// </summary>
    private static void ChooseSubMode(LobbyController lobby)
    {
        Step("LIARSBAR8P_DECKMODE", "deck", 4, () => lobby.DeckMode, () => lobby.ChangeGameModeDeckRight());
        Step("LIARSBAR8P_DICEMODE", "dice", 2, () => lobby.DiceMode, () => lobby.ChangeGameModeDiceRight());

        // The bar, from LIARSBAR8P_MAP. There are four of them and they are four different
        // rooms with four different tables - and every test this mod has ever run happened in
        // whichever one the machine last played in, because the choice is remembered in
        // PlayerPrefs rather than defaulting. The seat ring measures the table it finds rather
        // than assuming one, so it ought not to care; "ought not to" is the reason to check.
        Step("LIARSBAR8P_MAP", "bar", 4, () => lobby.CurrentMap, () => lobby.ChangeMap());

        // The house rules, from LIARSBAR8P_RULES (0 CLASSIC, 1 QUICK, 2 HARSH), through the row's
        // own arrow when it is up, as a host's click would.
        Step("LIARSBAR8P_RULES", "house rules", 3, () => HouseRules.Picked,
             () => { if (!RuleRow.Press(1)) HouseRules.Pick(HouseRules.Picked + 1); });

        // The Poker tab's table, from LIARSBAR8P_TWOCARD (0 the released Liar's Poker, 1 Two-Card
        // Poker), through the tab's own arrow when it is up, as a host's click would. Never through
        // the game mode: that would write mode 3 into what the game remembers.
        Step("LIARSBAR8P_TWOCARD", "Poker tab", 2, () => TwoCardPoker.Chosen ? 1 : 0,
             () => { if (!TwoCardLobby.Press(1)) TwoCardPoker.Step("test driver"); });
    }

    private static void Step(string variable, string what, int count, Func<int> read, Action next)
    {
        try
        {
            string want = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(want)) return;
            if (!int.TryParse(want, out int target) || target < 0 || target >= count)
            {
                Plugin.Log.LogWarning($"[loopback] '{want}' is not a {what} variant - there are {count}, numbered from 0");
                return;
            }

            // Pressing the arrow rather than writing the number, and never more times than
            // there are variants, so a variant that will not take cannot spin forever.
            for (int i = 0; i < count && read() != target; i++) next();

            if (read() == target) Plugin.Log.LogWarning($"[loopback] {what} variant set to {target} for this run");
            else Plugin.Log.LogWarning($"[loopback] the {what} variant would not move to {target} - it is on {read()}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[loopback] could not set the {what} variant: {e.Message}"); }
    }

    /// <summary>Read the role once, from the environment or the command line.</summary>
    internal static void Configure()
    {
        try
        {
            string raw = System.Environment.GetEnvironmentVariable("LIARSBAR8P_LOOPBACK");

            if (string.IsNullOrEmpty(raw))
                foreach (var arg in System.Environment.GetCommandLineArgs())
                {
                    if (string.Equals(arg, "-lbhost", StringComparison.OrdinalIgnoreCase)) raw = "host";
                    else if (string.Equals(arg, "-lbclient", StringComparison.OrdinalIgnoreCase)) raw = "client";
                }

            if (string.IsNullOrEmpty(raw)) return;

            if (raw.Equals("host", StringComparison.OrdinalIgnoreCase)) _role = Role.Host;
            else if (raw.Equals("client", StringComparison.OrdinalIgnoreCase)) _role = Role.Client;
            else return;

            string want = System.Environment.GetEnvironmentVariable("LIARSBAR8P_EXPECT");
            string address = System.Environment.GetEnvironmentVariable("LIARSBAR8P_ADDRESS");

            // The same two as command line options, for a copy started by Steam - on a Steam
            // Deck, say - where the environment cannot be set but extra options can be passed:
            // steam -applaunch 3097560 -lbhost -lbexpect 2
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "-lbexpect", StringComparison.OrdinalIgnoreCase)) want = args[i + 1];
                else if (string.Equals(args[i], "-lbaddress", StringComparison.OrdinalIgnoreCase)) address = args[i + 1];
            }

            // One is the host alone with its bots, so a single copy can play a whole table.
            if (!string.IsNullOrEmpty(want) && int.TryParse(want, out int n) && n >= 1) _expect = n;

            if (!string.IsNullOrWhiteSpace(address) && address.IndexOfAny(new[] { ' ', '/', ':' }) < 0)
                _address = address.Trim();

            string p = System.Environment.GetEnvironmentVariable("LIARSBAR8P_PORT");
            if (!string.IsNullOrEmpty(p) && int.TryParse(p, out int parsed) && parsed > 1024 && parsed < 65535)
                _port = parsed;

            Plugin.Log.LogWarning(
                $"[loopback] this copy is a {_role}" +
                (_role == Role.Client ? $", joining {_address}:{_port}" : $" on port {_port}") +
                (_expect > 0 ? $", waiting for {_expect} players" : "") +
                " - Steam is not used for networking in this mode");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[loopback] could not read the role: {e.Message}");
            _role = Role.Off;
        }
    }

    /// <summary>
    /// Once the network manager exists, point it at loopback and start.
    ///
    /// Driven from the ticker rather than a patch because the manager is created during
    /// scene load and is not ready on the frame the mod loads.
    /// </summary>
    internal static void Tick()
    {
        if (_role == Role.Off) return;

        if (_started) { NameTheCopies(); StartWhenEverybodyIsHere(); ReportTheMatch(); return; }
        if (Time.time < _next) return;
        _next = Time.time + 1f;

        try
        {
            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null) return;

            var tp = Swap(nm);
            if (tp == null) { _started = true; return; }

            nm.maxConnections = Limits.Max;
            nm.networkAddress = _address;

            // Extra copies are here to be a connection, not to be watched: no sound, and a
            // modest frame rate so eight of them do not fight over the machine. The game's
            // own stress harness does the same.
            if (_role == Role.Client)
            {
                Application.runInBackground = true;
                Application.targetFrameRate = 30;
                AudioListener.pause = true;
                AudioListener.volume = 0f;
            }

            _started = true;

            if (_role == Role.Host)
            {
                // Says what it actually does. Setting networkAddress only tells a CLIENT
                // where to dial; Telepathy's listener takes a port and no address, so the
                // socket is open on every interface, not just loopback. Printing
                // "hosting on 127.0.0.1" was a reassurance the code could not back up, and
                // this mode has no Steam and no authentication behind it - so anyone who can
                // reach the port while a test is running can join as a client.
                Plugin.Log.LogWarning(
                    $"[loopback] hosting on port {_port} - the listener accepts connections on " +
                    "every network interface, not only 127.0.0.1, and this mode has no Steam " +
                    "and no authentication. Only run it on a machine you trust the network of.");
                nm.StartHost();
            }
            else
            {
                Plugin.Log.LogWarning($"[loopback] joining {_address}:{_port}");
                nm.StartClient();
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[loopback] could not start as {_role}: {e.Message}");
            _started = true;
        }
    }

    /// <summary>
    /// Test driver: a client sent back to the menu dials the host again, as a player pressing
    /// Join would. The menu's network manager is a fresh one, still on Steam, so it is pointed at
    /// loopback first. A new connection gets a new number, and so a new made-up id.
    /// </summary>
    internal static bool Redial()
    {
        if (_role != Role.Client || Mirror.NetworkClient.active) return false;

        var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
        if (nm == null || Swap(nm) == null) return false;

        nm.networkAddress = _address;
        nm.StartClient();
        return true;
    }

    /// <summary>
    /// Give each connected copy a name and an id of its own.
    ///
    /// Every copy signs into the same Steam account, so without this they all arrive called
    /// the same thing with the same Steam id - and a table of eight identical players is not
    /// a simulation of anything. Worse, the mod's own guard against one person being seated
    /// twice matches on exactly those two fields, so it would quietly drop seven of them.
    ///
    /// The host owns these values, so it hands them out: they are SyncVars, set through the
    /// Network properties so every client is told. Only in loopback mode; a real Steam game
    /// is never touched.
    /// </summary>
    private static void NameTheCopies()
    {
        if (_role != Role.Host || !Active) return;

        try
        {
            if (!Mirror.NetworkServer.active) return;
            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null || nm.GamePlayers == null) return;

            foreach (var p in nm.GamePlayers)
            {
                if (p == null) continue;

                // Everyone is numbered after their connection, which is unique and already
                // means something in the log - the host included. That way no Steam
                // persona appears in a screenshot of a test.
                //
                // Bots are skipped: they have their own names and ids, and their negative
                // connection numbers would collide with the numbering.
                if (BotManager.IsBot(p)) continue;

                string want = TestName(p.ConnectionID) ?? $"Player{p.ConnectionID + 1}";
                if (p.PlayerName == want) continue;

                p.NetworkPlayerName = want;
                p.NetworkPlayerSteamID = FirstId + (ulong)(p.ConnectionID + 1);
                Plugin.Log.LogInfo($"[loopback] connection {p.ConnectionID} is renamed for the test");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[loopback] could not name the copies: {e.Message}");
        }
    }

    private static string[] _testNames;

    /// <summary>
    /// Made-up names from <c>LIARSBAR8P_TESTNAMES</c>, split on '|', one for each connection in
    /// turn, for a test that the log and the bug report keep names out: an apostrophe, another
    /// script, a name that looks like a tag. Nothing when it is not set.
    /// </summary>
    private static string TestName(int connection)
    {
        if (_testNames == null)
        {
            string raw = null;
            try { raw = Environment.GetEnvironmentVariable("LIARSBAR8P_TESTNAMES"); }
            catch { }
            _testNames = string.IsNullOrEmpty(raw) ? Array.Empty<string>() : raw.Split('|');
        }
        return connection >= 0 && connection < _testNames.Length && _testNames[connection].Length > 0
            ? _testNames[connection] : null;
    }

    private static string[] _botNames;

    /// <summary>
    /// Made-up names for bots from <c>LIARSBAR8P_BOTNAMES</c>, split on '|', given out before the
    /// bots' own, for a picture of a full table whose plates all read like the copies' numbered
    /// names. Only inside the loopback test harness, and nothing when it is not set.
    /// </summary>
    internal static string[] BotTestNames()
    {
        if (_botNames == null)
        {
            string raw = null;
            try { if (Dev.Enabled) raw = Environment.GetEnvironmentVariable("LIARSBAR8P_BOTNAMES"); }
            catch { }
            _botNames = string.IsNullOrEmpty(raw) ? Array.Empty<string>() : raw.Split('|');
        }
        // Real players read bots' names, so these are for the loopback test harness only.
        return Fair.TestsAllowed ? _botNames : Array.Empty<string>();
    }

    /// <summary>For the test driver: other made-up bot names from now on, or none, as above.</summary>
    internal static void SetBotTestNames(string raw) =>
        _botNames = string.IsNullOrEmpty(raw) || !Dev.Enabled ? Array.Empty<string>() : raw.Split('|');

    /// <summary>
    /// Once everyone expected has arrived, ready them all and start the match.
    ///
    /// There is nobody at the other five keyboards to press ready, and sending keystrokes to
    /// a window that does not have focus does not work. The host owns the ready flag, so it
    /// sets it, and then starts the match through the game's own button handler - the same
    /// path a person clicking START takes.
    ///
    /// How many to wait for comes from the environment, so one launcher can ask for a table
    /// of five or a table of eight without rebuilding anything. One means the host by itself,
    /// with the bots in <c>LIARSBAR8P_FILL</c> making up the table. The game starts no match
    /// for one player, so a lone host needs at least one bot.
    /// </summary>
    /// <summary>
    /// Test driver switch: keep everybody in the lobby when they have all arrived, for a test of
    /// the lobby itself. Clearing it lets the start below go ahead as usual.
    /// </summary>
    internal static bool HoldStart { get; set; }

    private static void StartWhenEverybodyIsHere()
    {
        if (_role != Role.Host || _expect <= 0 || _matchStarted) return;

        try
        {
            if (!Mirror.NetworkServer.active) return;

            var lobby = LobbyController.Instance;
            if (lobby == null) return;

            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null || nm.GamePlayers == null) return;

            // Bots first, from LIARSBAR8P_FILL_EARLY, before any client has arrived, on the table
            // the run asks for: each copy that joins a full table then takes a bot's seat, as a
            // person does. Held in the lobby or not.
            if (FillEarly(lobby, nm) || HoldStart) return;

            // Not everybody asked for may come: a launcher that runs short of memory stops
            // starting copies rather than drive the machine into the page file. Once nobody
            // new has arrived for a couple of minutes, go with whoever is here - the bots in
            // LIARSBAR8P_FILL make up the rest of the table. Copies are counted, not bots.
            int here = 0;
            foreach (var p in nm.GamePlayers) if (p != null && !BotManager.IsBot(p)) here++;
            if (here != _lastHere) { _lastHere = here; _lastArrival = Time.time; }
            bool givenUp = here >= 2 && Time.time - _lastArrival > 150f;
            if (here < _expect && !givenUp) return;
            if (here < _expect && !_announcedShort)
            {
                _announcedShort = true;
                Plugin.Log.LogWarning($"[loopback] {here} of {_expect} copies arrived and nobody new for a while - going ahead");
            }

            // The mode first, before anybody is marked ready. Choosing it last looked right
            // and did nothing: the call went through and the lobby still reported Liar's Deck,
            // because by then everyone was ready and the lobby will not change mode under a
            // ready table. It also needs a moment to reach the other copies before the match
            // is started on top of it.
            if (!_modeChosen)
            {
                _modeChosen = true;
                ChooseMode(lobby);
                _readyAt = 0f;
                return;
            }

            // Bots to make up the table, from LIARSBAR8P_FILL. Eight copies of the game want
            // about six gigabytes each, which a machine does not always have spare; four copies
            // and four bots still put eight characters round the table, and four of them can
            // be looked through. Added once, in the lobby, where bots have to be added.
            if (!_filled)
            {
                _filled = true;
                string fill = Environment.GetEnvironmentVariable("LIARSBAR8P_FILL");
                if (!string.IsNullOrEmpty(fill) && int.TryParse(fill, out int table) && table > nm.GamePlayers.Count)
                {
                    table = Math.Min(table, Limits.Max);

                    // What came of it, not what was asked. A bot added here is still on its way and
                    // is not in the lobby's list until a later frame, so that list's count said
                    // "filled to 2" at a table about to hold seven. And a bare copy has no bots at all.
                    if (Plugin.Bare)
                        Plugin.Log.LogWarning(
                            $"[loopback] a bare copy has no bots - LIARSBAR8P_FILL={table} is ignored, " +
                            $"and the table stays at {nm.GamePlayers.Count}");
                    else
                    {
                        BotManager.FillTo(table);
                        int people = 0;
                        foreach (var p in nm.GamePlayers) if (p != null && !BotManager.IsBot(p)) people++;
                        Plugin.Log.LogWarning(
                            $"[loopback] asked for bots to fill the table to {table}: {people} player(s) " +
                            $"and {BotManager.Count} bot(s) now");
                    }
                    _readyAt = 0f;
                    return;
                }
            }

            // Everyone ready, including the copies with nobody at the keyboard.
            // Bots are readied but not waited for: BotManager holds them ready, and a bot that
            // arrived a tick ago may not be on its podium yet.
            int waiting = 0;
            foreach (var p in nm.GamePlayers)
            {
                if (p == null) continue;
                if (BotManager.IsBot(p)) { if (!p.Ready) p.NetworkReady = true; continue; }
                if (!p.Ready) { p.NetworkReady = true; waiting++; }
            }

            if (waiting > 0)
            {
                Plugin.Log.LogInfo($"[loopback] readied {waiting} player(s); waiting a moment before starting");
                _readyAt = Time.time;
                return;
            }

            // The house rules and the variant asked for, picked again if the lobby has dropped
            // them: the breath below gives the choice its check and its publish before the start.
            if (KeepRules()) { _readyAt = Time.time; return; }
            if (KeepVariant(lobby)) { _readyAt = Time.time; return; }

            // A breath after the last ready reaches everybody, then start.
            if (_readyAt == 0f) { _readyAt = Time.time; return; }
            if (Time.time - _readyAt < 3f) return;

            // The game's own gate, asked rather than assumed. Start lights only for two players or
            // more, all of them ready, and on the host the game ignores a start while it is dark.
            // Claiming a start then had a lone copy with no bots log one, and a test measure its
            // lobby as if it were a table. Said once for each reason, and tried again every tick,
            // so a copy that arrives late still gets its match.
            int seated = 0;
            foreach (var p in nm.GamePlayers) if (p != null) seated++;
            if (lobby.StartButtonActive == null || !lobby.StartButtonActive.activeSelf)
            {
                string more = Plugin.Bare ? "a bare copy has no bots, so it needs LIARSBAR8P_EXPECT 2 or more"
                                          : "LIARSBAR8P_FILL adds bots, LIARSBAR8P_EXPECT more copies";
                string why = seated < 2
                    ? $"{seated} player(s) at the table, and the game starts no match for fewer than 2 - " +
                      $"not starting ({more})"
                    : $"the lobby's Start is not lit with {seated} at the table - waiting for it";
                if (why != _darkSaid) { _darkSaid = why; Plugin.Log.LogWarning($"[loopback] {why}"); }
                return;
            }

            _matchStarted = true;
            _darkSaid = null;

            Plugin.Log.LogWarning(
                $"[loopback] all {nm.GamePlayers.Count} copies are here and ready - starting " +
                $"a {lobby.Mode} match");
            lobby.StartGameOrReady();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[loopback] could not start the match: {e.Message}");
            _matchStarted = true;
        }
    }

    /// <summary>
    /// Once a match is under way, say so, with the seats it has. The start line above only says
    /// a start was asked for; the game decides, and a test that waits for a table waits for this
    /// line and checks the count against what it asked for. It reads nothing but the game's own
    /// match manager, so a bare copy says it too. Once a match, after the count has held for two
    /// seconds, because the seats join the manager's list one by one as they load.
    /// </summary>
    private static void ReportTheMatch()
    {
        if (_role != Role.Host) return;

        try
        {
            var m = Manager.Instance;
            if (m == null) { _matchSaid = false; _matchSeats = -1; return; }
            if (_matchSaid || !m.GameStarted || m.Players == null) return;

            int seats = 0;
            foreach (var p in m.Players) if (p != null) seats++;
            if (seats != _matchSeats) { _matchSeats = seats; _matchSeatsSince = Time.time; return; }
            if (Time.time - _matchSeatsSince < 2f) return;

            _matchSaid = true;
            Plugin.Log.LogWarning($"[loopback] the match is up with {seats} seats");
        }
        catch (Exception e)
        {
            _matchSaid = true;
            Plugin.Log.LogWarning($"[loopback] could not read the match: {e.Message}");
        }
    }

    /// <summary>
    /// The run's LIARSBAR8P_RULES once more, on the settled lobby, and once a run. A lone host
    /// (LIARSBAR8P_EXPECT=1) makes its first pick in the tick its hosting begins, and the new
    /// session <see cref="HouseRules"/> sees later in that same tick puts every lobby back on
    /// CLASSIC: the first QUICK run of batch M2 picked QUICK and played CLASSIC. True when it
    /// picked, so the start waits.
    /// </summary>
    private static bool KeepRules()
    {
        if (_rulesKept || Plugin.Bare) return false;
        string want = Environment.GetEnvironmentVariable("LIARSBAR8P_RULES");
        if (string.IsNullOrEmpty(want) || !int.TryParse(want, out int target)) return false;
        if (target < 0 || target >= HouseRules.Names.Length || HouseRules.Picked == target) return false;

        _rulesKept = true;
        Plugin.Log.LogWarning($"[loopback] the house rules are back on {HouseRules.Names[HouseRules.Picked]}: " +
                              $"picking {HouseRules.Names[target]} again before the start");
        for (int i = 0; i < HouseRules.Names.Length && HouseRules.Picked != target; i++)
            if (!RuleRow.Press(1)) HouseRules.Pick(HouseRules.Picked + 1);
        return true;
    }

    private static bool _rulesKept;

    /// <summary>
    /// The run's deck or dice variant once more before each start, as <see cref="KeepRules"/> does
    /// for the house rules. A lobby that has just opened can put back the variant the game remembers
    /// after the harness picked another: one launch that asked for Deck 2 played the Chaos deck,
    /// while the same choice made from a settled lobby held every time. True when it stepped, so
    /// the start waits.
    /// </summary>
    private static bool KeepVariant(LobbyController lobby)
    {
        if (_variantKept || Plugin.Bare) return false;
        _variantKept = true;

        bool deck = lobby.Mode == CustomNetworkManager.GameMode.LiarsDeck || lobby.Mode == CustomNetworkManager.GameMode.LiarsChaos;
        bool dice = lobby.Mode == CustomNetworkManager.GameMode.LiarsDice;
        bool stepped = false;
        if (deck && Wanted("LIARSBAR8P_DECKMODE", 4, out int d) && lobby.DeckMode != d)
        {
            Plugin.Log.LogWarning($"[loopback] the deck variant is back on {lobby.DeckMode}: picking {d} again before the start");
            Step("LIARSBAR8P_DECKMODE", "deck", 4, () => lobby.DeckMode, () => lobby.ChangeGameModeDeckRight());
            stepped = true;
        }
        if (dice && Wanted("LIARSBAR8P_DICEMODE", 2, out int v) && lobby.DiceMode != v)
        {
            Plugin.Log.LogWarning($"[loopback] the dice variant is back on {lobby.DiceMode}: picking {v} again before the start");
            Step("LIARSBAR8P_DICEMODE", "dice", 2, () => lobby.DiceMode, () => lobby.ChangeGameModeDiceRight());
            stepped = true;
        }
        return stepped;
    }

    private static bool Wanted(string variable, int count, out int target)
    {
        target = -1;
        string want = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrEmpty(want) && int.TryParse(want, out target) && target >= 0 && target < count;
    }

    private static bool _variantKept;

    /// <summary>
    /// Test switch <c>LIARSBAR8P_FILL_EARLY</c>: fill the table with bots as soon as the host's
    /// lobby is up, before any client has arrived, so each copy that joins arrives at a full table
    /// and a bot gives it its seat. The mode is chosen first: bots are refused at a table they do
    /// not play. True while it is doing that, so nothing else runs this tick.
    /// </summary>
    private static bool FillEarly(LobbyController lobby, CustomNetworkManager nm)
    {
        if (_filledEarly || Plugin.Bare) return false;
        string raw = Environment.GetEnvironmentVariable("LIARSBAR8P_FILL_EARLY");
        if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out int table) || table < 2)
        {
            _filledEarly = true;
            return false;
        }

        if (!_modeChosen)
        {
            _modeChosen = true;
            ChooseMode(lobby);
            return true;
        }

        _filledEarly = true;
        BotManager.FillTo(Math.Min(table, Limits.Max));
        Plugin.Log.LogWarning($"[loopback] filled the table to {Math.Min(table, Limits.Max)} with bots before the clients arrive " +
                              $"({BotManager.Count} bot(s) on their way)");
        return true;
    }

    private static bool _filledEarly;

    /// <summary>
    /// Test driver: back in the lobby after a match, start the next one the way the first was
    /// started, everyone readied in the same tick as the start. The mode and the bots are left as
    /// the lobby has them now.
    /// </summary>
    internal static void StartAgain()
    {
        _modeChosen = true;
        _filled = true;
        _readyAt = 0f;
        _matchStarted = false;
        _darkSaid = null;
        _variantKept = false;
        HoldStart = false;
    }

    /// <summary>
    /// Test driver: back in the lobby after a match, the next one at another table, "LiarsDeck 3"
    /// or "LiarsDice 1" with the variant after the mode, chosen and filled the way the first was.
    /// A third word fills to that many instead, "LiarsTexas - 0" for no bots and no variant.
    /// For Liar's Poker the variant is the Poker tab's table: "LiarsTexas 1 0" is Two-Card Poker.
    /// A fourth word is the bar, 0 to 3: "LiarsTexas 1 0 2".
    /// The bots go first: the lobby will not start with the ones that sat through the last match.
    /// </summary>
    internal static string NextMatch(string table)
    {
        string[] parts = table.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "no table named";

        bool dice = parts[0].IndexOf("Dice", StringComparison.OrdinalIgnoreCase) >= 0;
        bool poker = parts[0].IndexOf("Texas", StringComparison.OrdinalIgnoreCase) >= 0 || parts[0] == "5";
        string variant = parts.Length > 1 && parts[1] != "-" ? parts[1] : null;
        Environment.SetEnvironmentVariable("LIARSBAR8P_MODE", parts[0]);
        Environment.SetEnvironmentVariable("LIARSBAR8P_DECKMODE", dice || poker ? null : variant);
        Environment.SetEnvironmentVariable("LIARSBAR8P_DICEMODE", dice ? variant : null);
        Environment.SetEnvironmentVariable("LIARSBAR8P_TWOCARD", poker ? variant : null);
        if (parts.Length > 2) Environment.SetEnvironmentVariable("LIARSBAR8P_FILL", parts[2]);
        if (parts.Length > 3) Environment.SetEnvironmentVariable("LIARSBAR8P_MAP", parts[3]);

        while (BotManager.Count > 0) BotManager.RemoveBot();
        StartAgain();
        _modeChosen = false;
        _filled = false;
        return $"{parts[0]}{(variant != null ? $", variant {variant}" : "")}";
    }

    private static int _expect;
    private static bool _matchStarted;
    private static bool _modeChosen;
    private static bool _filled;
    private static int _lastHere = -1;
    private static float _lastArrival;
    private static bool _announcedShort;
    private static float _readyAt;
    private static string _darkSaid;
    private static bool _matchSaid;
    private static int _matchSeats = -1;
    private static float _matchSeatsSince;

    /// <summary>
    /// Put a plain TCP transport in place of the Steam one.
    ///
    /// The Steam transport addresses peers by Steam id, which is no use when every copy is
    /// the same account. Telepathy takes an address and a port, which is exactly what is
    /// wanted. Mirror looks the active transport up in two places - the manager's own field
    /// and the static - and both have to agree or the client and server halves disagree
    /// about who is carrying the packets.
    /// </summary>
    private static Mirror.TelepathyTransport Swap(CustomNetworkManager nm)
    {
        try
        {
            var existing = nm.GetComponent<Mirror.TelepathyTransport>();
            if (existing == null) existing = nm.gameObject.AddComponent<Mirror.TelepathyTransport>();
            if (existing == null)
            {
                Plugin.Log.LogError("[loopback] this build has no Telepathy transport to use");
                return null;
            }

            existing.Port = (ushort)_port;
            existing.enabled = true;

            // The Steam transport must stop listening, or it and Telepathy both try to run.
            var steam = nm.GetComponent<Mirror.FizzySteam.FizzySteamworks>();
            if (steam != null) steam.enabled = false;

            nm.transport = existing;
            Mirror.Transport.active = existing;

            Plugin.Log.LogInfo($"[loopback] transport swapped to Telepathy on port {_port}");
            return existing;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[loopback] could not swap the transport: {e.Message}");
            return null;
        }
    }
}
