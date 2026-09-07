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
                Plugin.Log.LogWarning($"[loopback] hosting on 127.0.0.1:{_port}");
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

                // The host keeps its own Steam name; the joiners are numbered after their
                // connection, which is unique and already means something in the log.
                if (p.ConnectionID == 0) continue;

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
            Plugin.Log.LogWarning($"[loopback] all {nm.GamePlayers.Count} copies are here and ready - starting the match");
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
