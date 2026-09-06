using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Bots that fill empty seats, so an eight player round can be exercised without eight
/// people.
///
/// A bot is not a simulation of a player — it is a real player object as far as the game
/// is concerned. It is the game's own player prefab, spawned through Mirror, registered
/// in the same list, given a lobby podium by the same method, seated by the same code and
/// dealt to by the same deal. That is the whole point: a bot that took a private path
/// would prove nothing about whether a person can play.
///
/// The one thing a bot does not have is a connection. Mirror does not need one to spawn
/// and own an object server-side — <c>NetworkServer.Spawn</c> without an owner gives a
/// networked object every client can see, driven entirely by the host. What that costs is
/// anything the game sends to a specific connection; see <see cref="BotBehaviour"/>.
/// </summary>
internal static class BotManager
{
    /// <summary>Names are the giveaway in a log, so they are obvious and stable.</summary>
    internal const string NamePrefix = "BOT-";

    private static readonly List<PlayerObjectController> _bots = new();
    private static int _nextNumber = 1;

    /// <summary>A bot spawned this frame, not yet seated. See <see cref="Tick"/>.</summary>
    private sealed class Pending
    {
        public PlayerObjectController Bot;
        public string Name;
        public int Frames;
    }

    private static readonly List<Pending> _pending = new();

    /// <summary>
    /// Finishes off bots spawned in earlier frames.
    ///
    /// A freshly spawned object is not usable in the same frame: Unity has not run Start,
    /// so the player's own reference to the network manager is still null and the method
    /// that assigns a podium throws on it. Waiting also lets the game's own registration
    /// run, so the bot can be numbered by where it actually landed in the roster rather
    /// than by a guess made before it was in there.
    /// </summary>
    internal static void Tick()
    {
        HoldReady();
        if (_pending.Count == 0) return;

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (p.Bot == null) { _pending.RemoveAt(i); continue; }

            p.Frames++;
            if (p.Frames < 2) continue;          // give Start a frame to have happened

            var nm = Dev.Net;
            bool registered = false;
            int index = -1;
            if (nm != null && nm.GamePlayers != null)
                for (int k = 0; k < nm.GamePlayers.Count; k++)
                    if (nm.GamePlayers[k] == p.Bot) { registered = true; index = k; break; }

            if (!registered)
            {
                if (p.Frames > 300)
                {
                    Dev.Warn("bot", $"{p.Name} never appeared in the roster - giving up on it");
                    _pending.RemoveAt(i);
                }
                continue;
            }

            try
            {
                p.Bot.NetworkPlayerIdNumber = index + 1;
                p.Bot.CmdSetPlayer();
                p.Bot.NetworkReady = true;
                p.Bot.NetworkLoaded = true;
                Dev.Log("bot", $"seated {p.Name} -> {Dev.Describe(p.Bot)}");
                _pending.RemoveAt(i);
            }
            catch (Exception e)
            {
                if (p.Frames > 300)
                {
                    Dev.Warn("bot", $"{p.Name} could not be seated: {e.Message}");
                    _pending.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// A bot is always ready. Setting it once is not enough — the lobby drives the ready
    /// flag from the owning client every frame, and a bot has no client to hold the button
    /// down, so the host holds it instead.
    /// </summary>
    private static void HoldReady()
    {
        try
        {
            if (!NetworkServer.active) return;
            foreach (var p in Dev.LobbyPlayers())
            {
                if (p == null || !IsBot(p)) continue;
                if (!p.Ready) p.NetworkReady = true;
                if (!p.Loaded) p.NetworkLoaded = true;
            }
        }
        catch { }
    }

    internal static int Count
    {
        get { Prune(); return _bots.Count; }
    }

    internal static bool IsBot(PlayerObjectController p)
    {
        if (p == null) return false;
        try
        {
            if (_bots.Contains(p)) return true;
            // After a scene change the list may be stale, but the name survives.
            return !string.IsNullOrEmpty(p.PlayerName) && p.PlayerName.StartsWith(NamePrefix);
        }
        catch { return false; }
    }

    internal static bool IsBot(PlayerStats p)
    {
        try { return p != null && !string.IsNullOrEmpty(p.PlayerName) && p.PlayerName.StartsWith(NamePrefix); }
        catch { return false; }
    }

    private static void Prune()
    {
        for (int i = _bots.Count - 1; i >= 0; i--)
            if (_bots[i] == null) _bots.RemoveAt(i);
    }

    // --------------------------------------------------------------------- add

    internal static void AddBot()
    {
        try
        {
            if (!Dev.Enabled) return;

            if (!NetworkServer.active)
            {
                Dev.Warn("bot", "only the host can add bots");
                return;
            }

            var nm = Dev.Net;
            if (nm == null || nm.GamePlayers == null)
            {
                Dev.Warn("bot", "no network manager - host a lobby first");
                return;
            }

            int present = nm.GamePlayers.Count;
            if (present >= Limits.Max)
            {
                Dev.Warn("bot", $"the table is already full at {present}/{Limits.Max}");
                return;
            }

            var prefab = nm.GamePlayerPrefab;
            if (prefab == null)
            {
                Dev.Warn("bot", "the game's player prefab is not available");
                return;
            }

            var go = UnityEngine.Object.Instantiate(prefab.gameObject);
            var bot = go.GetComponent<PlayerObjectController>();
            if (bot == null)
            {
                Dev.Warn("bot", "the copied player prefab has no PlayerObjectController");
                UnityEngine.Object.Destroy(go);
                return;
            }

            string name = NamePrefix + _nextNumber++;
            go.name = name;

            // Set before spawning so the values travel in the object's initial state
            // rather than as a follow-up nobody may be listening for yet.
            bot.NetworkConnectionID = -_nextNumber;      // no real connection; kept distinct
            // A distinct fake id: the seating code matches players to their table selves by
            // it, and every bot sharing zero makes them all look like the same person.
            bot.NetworkPlayerSteamID = (ulong)(76500000000000000L + _nextNumber);
            bot.NetworkPlayerName = name;
            bot.NetworkPlayerSkin = _nextNumber % 10;

            NetworkServer.Spawn(go);
            _bots.Add(bot);

            // Everything else has to wait. Unity runs Start on the next frame, and until it
            // has, the object's own reference to the network manager is null - which is
            // exactly what CmdSetPlayer dereferences. Calling it here throws for every bot
            // and leaves them with no podium.
            _pending.Add(new Pending { Bot = bot, Name = name });

            Dev.Log("bot", $"spawned {name}; waiting a frame before seating it");
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"add failed: {e}");
        }
    }

    // ------------------------------------------------------------------ remove

    internal static void RemoveBot()
    {
        try
        {
            if (!Dev.Enabled) return;
            if (!NetworkServer.active) { Dev.Warn("bot", "only the host can remove bots"); return; }

            Prune();
            if (_bots.Count == 0)
            {
                // Adopt any bot this session did not create (after a scene change).
                foreach (var p in Dev.LobbyPlayers()) if (IsBot(p)) _bots.Add(p);
                Prune();
            }
            if (_bots.Count == 0) { Dev.Warn("bot", "there are no bots to remove"); return; }

            var bot = _bots[_bots.Count - 1];
            _bots.RemoveAt(_bots.Count - 1);
            string name = bot.PlayerName;

            // Destroying the spawned object is exactly what happens when a person leaves,
            // so the game frees their podium and roster entry through its own code.
            NetworkServer.Destroy(bot.gameObject);
            Dev.Log("bot", $"removed {name} ({Dev.LobbyPlayers().Count - 1} left)");
        }
        catch (Exception e)
        {
            Dev.Warn("bot", $"remove failed: {e}");
        }
    }

    internal static void FillToMax()
    {
        try
        {
            if (!Dev.Enabled) return;
            var nm = Dev.Net;
            if (nm == null || nm.GamePlayers == null) { Dev.Warn("bot", "host a lobby first"); return; }

            int want = Limits.Max - nm.GamePlayers.Count;
            if (want <= 0) { Dev.Warn("bot", $"already at {nm.GamePlayers.Count}/{Limits.Max}"); return; }

            Dev.Log("bot", $"filling {nm.GamePlayers.Count} -> {Limits.Max}");
            for (int i = 0; i < want; i++) AddBot();
        }
        catch (Exception e) { Dev.Warn("bot", $"fill failed: {e}"); }
    }
}
