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

    internal static bool Active => _role != Role.Off;
    internal static Role Mine => _role;

    /// <summary>
    /// Pick the game mode for a harness run, from <c>LIARSBAR8P_MODE</c>.
    ///
    /// The mod's fixes are meant to be mode-independent - the caps, the turn order wrap and the
    /// seat ring are all shared - but "meant to be" is not evidence, and every one of them has
    /// only ever been watched in Liar's Deck. Naming a mode here is what lets the others be
    /// played rather than reasoned about.
    ///
    /// Accepts a name or a number: LiarsDeck, LiarsDice, LiarsChaos, LiarsPoker, VelvetRoom,
    /// LiarsTexas, LiarsSpin, Arena. Unset leaves whatever the lobby already had.
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
            if (!string.IsNullOrEmpty(want) && int.TryParse(want, out int n) && n > 1) _expect = n;

            string p = System.Environment.GetEnvironmentVariable("LIARSBAR8P_PORT");
            if (!string.IsNullOrEmpty(p) && int.TryParse(p, out int parsed) && parsed > 1024 && parsed < 65535)
                _port = parsed;

            Plugin.Log.LogWarning(
                $"[loopback] this copy is a {_role} on 127.0.0.1:{_port} - Steam is not used for " +
                "networking in this mode");
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

        if (_started) { NameTheCopies(); StartWhenEverybodyIsHere(); return; }
        if (Time.time < _next) return;
        _next = Time.time + 1f;

        try
        {
            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null) return;

            var tp = Swap(nm);
            if (tp == null) { _started = true; return; }

            nm.maxConnections = Limits.Max;
            nm.networkAddress = "127.0.0.1";

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
                Plugin.Log.LogWarning($"[loopback] joining 127.0.0.1:{_port}");
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
        if (_role != Role.Host) return;

        try
        {
            if (!Mirror.NetworkServer.active) return;
            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null || nm.GamePlayers == null) return;

            foreach (var p in nm.GamePlayers)
            {
                if (p == null) continue;

                // Everyone is numbered after their connection, which is unique and already
                // means something in the log - the host included. The host used to keep its
                // Steam persona, which is the one piece of real identity that ends up printed
                // over somebody's head in every screenshot taken of a test. A harness has no
                // use for it, and screenshots of a harness get shared.
                //
                // Bots are skipped, and not only for tidiness: BotManager recognises a bot at
                // the table by its name, so renaming one makes it stop being a bot as far as
                // the rest of the mod is concerned - it would no longer have its revolver
                // loaded or its "holding cards" flag kept up. Bots also carry negative
                // connection ids, which would collide with the numbering.
                if (p.ConnectionID < 0 || BotManager.IsBot(p)) continue;

                string want = $"Player{p.ConnectionID + 1}";
                if (p.PlayerName == want) continue;

                p.NetworkPlayerName = want;
                p.NetworkPlayerSteamID = 76500000000000000UL + (ulong)(p.ConnectionID + 1);
                Plugin.Log.LogInfo($"[loopback] connection {p.ConnectionID} is now '{want}'");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[loopback] could not name the copies: {e.Message}");
        }
    }

    /// <summary>
    /// Once everyone expected has arrived, ready them all and start the match.
    ///
    /// There is nobody at the other five keyboards to press ready, and sending keystrokes to
    /// a window that does not have focus does not work. The host owns the ready flag, so it
    /// sets it, and then starts the match through the game's own button handler - the same
    /// path a person clicking START takes.
    ///
    /// How many to wait for comes from the environment, so one launcher can ask for a table
    /// of five or a table of eight without rebuilding anything.
    /// </summary>
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
            if (nm.GamePlayers.Count < _expect) return;

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

            // Everyone ready, including the copies with nobody at the keyboard.
            int waiting = 0;
            foreach (var p in nm.GamePlayers)
            {
                if (p == null) continue;
                if (!p.Ready) { p.NetworkReady = true; waiting++; }
            }

            if (waiting > 0)
            {
                Plugin.Log.LogInfo($"[loopback] readied {waiting} player(s); waiting a moment before starting");
                _readyAt = Time.time;
                return;
            }

            // A breath after the last ready reaches everybody, then start.
            if (_readyAt == 0f) { _readyAt = Time.time; return; }
            if (Time.time - _readyAt < 3f) return;

            _matchStarted = true;

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

    private static int _expect;
    private static bool _matchStarted;
    private static bool _modeChosen;
    private static float _readyAt;

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
