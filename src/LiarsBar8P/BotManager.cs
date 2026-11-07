using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Bots that fill empty seats.
///
/// A bot is not a simulation of a player - it is a real player object as far as the game
/// is concerned. It is the game's own player prefab, spawned through Mirror, registered
/// in the same list, given a lobby podium by the same method, seated by the same code and
/// dealt to by the same deal. That is the whole point: a bot that took a private path
/// would prove nothing about whether a person can play.
///
/// The one thing a bot does not have is a connection, and that is what tells it apart: its
/// connection number is below zero, which no person's can be, and every machine can read it.
/// Its seat at the table goes on the network with no owner (<see cref="BotSeats"/>), the host
/// plays its turns (<see cref="BotBrain"/>) and gives it the poses its own machine would
/// (<see cref="BotPuppet"/>).
///
/// Bots are refused wherever they cannot play: anywhere but the host's lobby, at a table where
/// not everybody runs the mod, near ranked or in its matchmaking, in the Velvet Room, at a full
/// table, with <c>AllowBots</c> off, and at every table whose bot player is not built yet. Liar's
/// Dice, Liar's Deck (Basic, Devil and Deck 2) and the Chaos deck have one so far. A lobby that
/// changes to such a table sends its bots away. A bot gives its seat to a person who joins a full
/// table (<see cref="BotLobby"/>), and keeps a name nobody else in the room has.
/// </summary>
internal static class BotManager
{
    /// <summary>
    /// What bots are called, in the order they are given out. Plain words, at most eleven letters,
    /// where the game cuts a name on the table, and saying "Bot" so nobody takes one for a person.
    /// They are names, so they are the same in every language: every machine at the table shows
    /// the one name the host gave, and the dice reveal finds a seat by it.
    /// </summary>
    private static readonly string[] Names =
        { "Bot Ace", "Bot King", "Bot Queen", "Bot Jack", "Bot Joker", "Bot Deuce", "Bot Trey" };

    /// <summary>
    /// The made-up Steam ids bots carry: bot n is FirstId + n. The seating code tells players
    /// apart by id, so every bot has its own, clear of the ids the loopback harness gives its copies.
    /// </summary>
    internal const ulong FirstId = Loopback.FirstId + 100000UL;

    private static int _nextNumber = 1;
    private static readonly System.Random _pick = new();

    /// <summary>A bot spawned this frame, not yet seated. See <see cref="Tick"/>.</summary>
    private sealed class Pending
    {
        public PlayerObjectController Bot;
        public string Name;
        public int Ticks;
    }

    private static readonly List<Pending> _pending = new();

    /// <summary>Bots destroyed but perhaps still listed until the game lets go of them.</summary>
    private static readonly List<PlayerObjectController> _leaving = new();

    private static bool _saidBroken;

    // ------------------------------------------------------------------ who is a bot

    internal static bool IsBot(PlayerObjectController p)
    {
        try { return p != null && p.ConnectionID < 0; }
        catch { return false; }
    }

    /// <summary>
    /// A seat at the table. The host knows its bots' seats; anyone else matches the seat to the
    /// lobby by its Steam id, which the game copies from one to the other at the start.
    /// </summary>
    internal static bool IsBot(PlayerStats p)
    {
        if (p == null) return false;
        try
        {
            if (NetworkServer.active) return BotSeats.Is(p);

            ulong id = p.Player_Id;
            if (id == 0) return false;
            var nm = Net;
            if (nm == null || nm.GamePlayers == null) return false;
            foreach (var lobby in nm.GamePlayers)
                if (lobby != null && lobby.PlayerSteamID == id) return lobby.ConnectionID < 0;
        }
        catch { }
        return false;
    }

    internal static int Count
    {
        get
        {
            int n = Bots().Count;
            foreach (var p in _pending) if (p.Bot != null) n++;
            return n;
        }
    }

    private static CustomNetworkManager Net
    {
        get
        {
            try
            {
                var nm = NetworkManager.singleton;
                return nm != null ? nm.TryCast<CustomNetworkManager>() : null;
            }
            catch { return null; }
        }
    }

    /// <summary>The bots in the room, oldest first, leaving out any already on their way out.</summary>
    private static List<PlayerObjectController> Bots()
    {
        var found = new List<PlayerObjectController>();
        var nm = Net;
        if (nm == null || nm.GamePlayers == null) return found;

        foreach (var p in nm.GamePlayers)
            if (p != null && IsBot(p) && !Leaving(p)) found.Add(p);

        // Numbered as they came: -1, -2, -3. The newest has the lowest number.
        found.Sort((a, b) => b.ConnectionID.CompareTo(a.ConnectionID));
        return found;
    }

    private static bool Leaving(PlayerObjectController p)
    {
        foreach (var l in _leaving)
            if (l != null && l.Pointer == p.Pointer) return true;
        return false;
    }

    // ------------------------------------------------------------------ every tick

    /// <summary>
    /// From <see cref="ModTicker"/>, on the host. Finishes off bots spawned in earlier ticks,
    /// keeps each bot ready, keeps every bot's character its own, and sends the bots away from a
    /// table that does not take them.
    ///
    /// A freshly spawned object is not usable in the same frame: Unity has not run Start,
    /// so the player's own reference to the network manager is still null and the method
    /// that assigns a podium throws on it. Waiting also lets the game's own registration
    /// run, so the bot can be numbered by where it actually landed in the roster rather
    /// than by a guess made before it was in there.
    /// </summary>
    internal static void Tick()
    {
        if (!NetworkServer.active)
        {
            _pending.Clear();
            _leaving.Clear();
            return;
        }

        _leaving.RemoveAll(p => p == null || !Listed(p));
        HoldReady();
        Seat();

        var lobby = LobbyController.Instance;
        if (lobby == null) return;
        SendAwayIfUnwelcome(lobby);
        GiveWayToAll();
        KeepCharactersApart();
        KeepNamesApart();
    }

    private static bool Listed(PlayerObjectController p)
    {
        var nm = Net;
        if (nm == null || nm.GamePlayers == null) return false;
        foreach (var q in nm.GamePlayers)
            if (q != null && q.Pointer == p.Pointer) return true;
        return false;
    }

    private static void Seat()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (p.Bot == null) { _pending.RemoveAt(i); continue; }

            p.Ticks++;
            if (p.Ticks < 2) continue;          // give Start a frame to have happened

            var nm = Net;
            int index = -1;
            if (nm != null && nm.GamePlayers != null)
                for (int k = 0; k < nm.GamePlayers.Count; k++)
                    if (nm.GamePlayers[k] != null && nm.GamePlayers[k].Pointer == p.Bot.Pointer) { index = k; break; }

            if (index < 0)
            {
                if (p.Ticks > 120)
                {
                    Plugin.Log.LogWarning("[bot] a bot never appeared in the roster - giving up on it");
                    _pending.RemoveAt(i);
                }
                continue;
            }

            try
            {
                p.Bot.NetworkPlayerIdNumber = index + 1;
                p.Bot.CmdSetPlayer();
                p.Bot.NetworkReady = true;
                Dev.Log("bot", $"seated a bot -> {Dev.Describe(p.Bot)}");
                _pending.RemoveAt(i);
            }
            catch (Exception e)
            {
                if (p.Ticks > 120)
                {
                    Plugin.Log.LogWarning($"[bot] a bot could not be given a podium: {e.Message}");
                    _pending.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// A bot is always ready, and loaded once a match is under way, since it has nobody to press
    /// the button or a scene to load.
    ///
    /// Only Ready is held in the lobby. Loaded there means "just back from a match", and the game
    /// answers it by putting the player back on their podium and clearing both flags; held on
    /// every tick, that whole return ran over and over. In the match a bot is marked loaded the
    /// way a person's own copy marks itself once the scene is up, which is what the game waits
    /// for before it deals.
    /// </summary>
    private static void HoldReady()
    {
        try
        {
            bool inLobby = LobbyController.Instance != null;
            bool inMatch = !inLobby && Manager.Instance != null;
            if (!inLobby && !inMatch) return;

            foreach (var p in Bots())
            {
                if (inLobby)
                {
                    if (!p.Loaded && !p.Ready && !string.IsNullOrEmpty(p.SlotName)) p.NetworkReady = true;
                }
                else if (!p.Loaded)
                {
                    p.NetworkReady = false;
                    p.NetworkLoaded = true;
                }
            }
        }
        catch (Exception e) { Dev.Warn("bot", $"could not hold a bot ready: {e.Message}"); }
    }

    /// <summary>
    /// Every bot plays a character nobody else at the table has. People keep theirs: when somebody
    /// picks a bot's character, the bot moves to one nobody is playing, through the same call the
    /// game uses to set a character, which keeps it to the ones that exist.
    /// </summary>
    private static void KeepCharactersApart()
    {
        if (SameCharacterForTest) return;
        var nm = Net;
        if (nm == null || nm.GamePlayers == null) return;

        // Who keeps a character: people first, then bots in the order they came. A bot that has to
        // move takes one nobody in the room plays, so no other bot has to move after it.
        var taken = new HashSet<int>();
        var played = new HashSet<int>();
        foreach (var p in nm.GamePlayers)
        {
            if (p == null) continue;
            played.Add(p.PlayerSkin);
            if (!IsBot(p)) taken.Add(p.PlayerSkin);
        }

        foreach (var bot in Bots())
        {
            if (taken.Contains(bot.PlayerSkin))
            {
                int was = bot.PlayerSkin;
                int pick = FreeCharacter(bot, played);
                if (pick >= 0) played.Add(pick);
                if (pick < 0) { taken.Add(was); continue; }
                bot.UserCode_CmdSetSkinIndex__Int32(pick);
                Dev.Log("bot", $"a bot moved from character {was} to {bot.PlayerSkin}: somebody else plays {was}");
            }
            taken.Add(bot.PlayerSkin);
        }
    }

    /// <summary>
    /// Every bot has a name nobody else in the room has. A bot's name is free when it sits down, but
    /// a person who arrives later can carry the same one, and the dice reveal finds a seat by its
    /// name: two seats with one name would each be shown both hands. So the bot takes another free
    /// name, here in the lobby, before the match can start, on its podium's plate too. People keep
    /// theirs. Never a name in the log: the one given up is a person's.
    /// </summary>
    private static void KeepNamesApart()
    {
        var nm = Net;
        if (nm == null || nm.GamePlayers == null) return;

        var people = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in nm.GamePlayers)
            if (p != null && !IsBot(p) && !string.IsNullOrEmpty(p.PlayerName)) people.Add(p.PlayerName);
        if (people.Count == 0) return;

        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bot in Bots())
        {
            string had = bot.PlayerName;
            if (!string.IsNullOrEmpty(had) && !people.Contains(had) && kept.Add(had)) continue;

            string name = FreeName(nm);
            bot.NetworkPlayerName = name;
            kept.Add(name);
            RenamePlate(bot, name);
            Plugin.Log.LogInfo($"[bot] bot {-bot.ConnectionID} took another name: somebody in the room has the one it had");
        }
    }

    /// <summary>The bot's podium shows its new name, in the form the game writes there.</summary>
    private static void RenamePlate(PlayerObjectController bot, string name)
    {
        var room = LobbyController.Instance;
        if (room == null || room.SpawnSlots == null || string.IsNullOrEmpty(bot.SlotName)) return;

        foreach (var slot in room.SpawnSlots)
        {
            if (slot == null || slot.gameObject.name != bot.SlotName) continue;
            string shown = slot.PlayerName ?? "";
            int close = shown.IndexOf('>');
            slot.NetworkPlayerName = shown.StartsWith("<sprite=", StringComparison.Ordinal) && close > 0
                ? shown.Substring(0, close + 1) + name
                : name;
            return;
        }
    }

    /// <summary>
    /// Test driver only: every bot plays the host's own character, so the memory a table of one
    /// character holds can be set against a table of eight.
    /// </summary>
    internal static bool SameCharacterForTest { get; set; }

    /// <summary>The character the first person in the room plays, or -1.</summary>
    private static int PersonCharacter(CustomNetworkManager nm)
    {
        foreach (var p in nm.GamePlayers) if (p != null && !IsBot(p)) return p.PlayerSkin;
        return -1;
    }

    /// <summary>A character nobody in <paramref name="taken"/> plays, picked at random, or -1.</summary>
    private static int FreeCharacter(PlayerObjectController bot, HashSet<int> taken)
    {
        int count = 0;
        try { count = bot.Skins != null ? bot.Skins.Count : 0; } catch { }

        var free = new List<int>();
        for (int i = 0; i < count; i++) if (!taken.Contains(i)) free.Add(i);
        return free.Count > 0 ? free[_pick.Next(free.Count)] : -1;
    }

    /// <summary>A name from the list nobody in the room has yet.</summary>
    private static string FreeName(CustomNetworkManager nm)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in nm.GamePlayers) if (p != null && p.PlayerName != null) used.Add(p.PlayerName);
        foreach (var p in _pending) if (p.Name != null) used.Add(p.Name);

        foreach (var n in Loopback.BotTestNames()) if (n.Length > 0 && !used.Contains(n)) return n;
        foreach (var n in Names) if (!used.Contains(n)) return n;
        for (int i = 1; ; i++) if (!used.Contains($"Bot {i}")) return $"Bot {i}";
    }

    // ------------------------------------------------------------------ where bots may sit

    /// <summary>
    /// Why a bot cannot sit at the table the lobby is set to, or null when it can. A table is
    /// listed here until a bot can play every turn of it.
    /// </summary>
    internal static string Unwelcome(LobbyController lobby)
    {
        if (lobby == null) return "bots can only be added from the lobby";
        if (TwoCardPoker.RefusesBots) return "Two-Card Poker plays without bots";

        switch (lobby.Mode)
        {
            // Only with a bot's new dice kept out of the host's sight, which takes a patch of its own.
            case CustomNetworkManager.GameMode.LiarsDice:
                return HiddenDice.On ? null : "Liar's Dice plays without bots in this version of the game";
            // Basic, Devil, Deck 2 and the Chaos deck: only with a bot's throw reaching the table,
            // which takes a patch of its own, and a second for each table's gun: at Liar's Deck a bot
            // pulling its own trigger, at the Chaos deck a bot putting the gun down after its shot.
            // The deck arrow's Chaos table puts the lobby on a mode of its own, LiarsChaos, and the
            // lobby can say LiarsDeck with the Chaos deck chosen too.
            case CustomNetworkManager.GameMode.LiarsDeck:
            case CustomNetworkManager.GameMode.LiarsChaos:
                bool chaos = lobby.Mode == CustomNetworkManager.GameMode.LiarsChaos || lobby.DeckMode == 2;
                if (BotSeats.ThrowsOn && (chaos ? BotSeats.GunDownOn : BotSeats.TriggerOn)) return null;
                return chaos ? "the Chaos deck plays without bots in this version of the game"
                             : "Liar's Deck plays without bots in this version of the game";
            case CustomNetworkManager.GameMode.LiarsTexas: return "Liar's Poker plays without bots for now";
            case CustomNetworkManager.GameMode.LiarsSpin: return "Liar's Slots plays without bots for now";
            default: return $"{lobby.Mode} plays without bots";
        }
    }

    /// <summary>Why ADD BOT seated nobody, for the words the host is shown.</summary>
    internal enum Refusal { None, Table, Full, Other }

    /// <summary>Whether the host has bots switched on in the config. A copy that has not read it yet counts as on.</summary>
    internal static bool Allowed => Plugin.AllowBots == null || Plugin.AllowBots.Value;

    /// <summary>
    /// Whether this copy hosts a lobby bots could ever sit in: the host, at a table where everybody
    /// runs the mod, away from ranked, matchmaking and the Velvet Room, with bots switched on and
    /// working. Where bots are welcome at the moment is <see cref="Unwelcome"/>'s and the count's.
    /// </summary>
    internal static bool Offered
    {
        get
        {
            var nm = Net;
            return NetworkServer.active && Allowed && BotSeats.Works && !BotTurnGuard.Tripped && !RankedGuard.Near &&
                   Table.Modded && LobbyController.Instance != null && Manager.Instance == null &&
                   nm != null && !nm.Matchmaking && !nm.isVelvetRoom;
        }
    }

    /// <summary>Why no bot can be added now, or null when one can.</summary>
    private static string WhyNot(CustomNetworkManager nm) => WhyNot(nm, out _);

    private static string WhyNot(CustomNetworkManager nm, out Refusal kind)
    {
        kind = Refusal.Other;
        if (!NetworkServer.active) return "only the host can add bots";
        if (!Allowed) return "bots are switched off (AllowBots in the config)";
        if (!BotSeats.Works) return "bots are off: a part they need did not go on in this version of the game";
        if (BotTurnGuard.Tripped) return "bots are off until the game restarts: a bot could not make its move";
        if (RankedGuard.Near) return "no bots near ranked";
        if (nm == null || nm.GamePlayers == null) return "no lobby to add a bot to";
        if (nm.Matchmaking) return "no bots in a matchmaking match";
        if (nm.isVelvetRoom) return "no bots in the Velvet Room";

        // Only from the lobby. A bot added once a match is under way has no podium to take, so the
        // game's own seating throws for it, and it arrives at a table that has already been dealt.
        var lobby = LobbyController.Instance;
        if (lobby == null) return "bots can only be added from the lobby, not during a match";
        if (!Table.Modded) return "bots sit only at a table where everybody runs the mod";

        string unwelcome = Unwelcome(lobby);
        if (unwelcome != null) { kind = Refusal.Table; return unwelcome; }

        int present = Present(nm);
        if (present >= Limits.Max) { kind = Refusal.Full; return $"the table is already full at {present}/{Limits.Max}"; }

        // Without a bot giving its seat to a person who joins, the last seat stays free for one.
        if (!BotLobby.ArrivalOn && present >= Limits.Max - 1)
        {
            kind = Refusal.Full;
            return "the last seat is kept free for a person: a bot cannot give its seat up in this version of the game";
        }

        kind = Refusal.None;
        return null;
    }

    /// <summary>
    /// Everybody in the room: the roster, less bots on their way out, bots not on it yet, and
    /// people whose player is made but not on the roster yet (two can arrive in one frame).
    /// </summary>
    private static int Present(CustomNetworkManager nm)
    {
        int n = 0;
        foreach (var p in nm.GamePlayers) if (p != null && !Leaving(p)) n++;
        foreach (var p in _pending) if (p.Bot != null && !Listed(p.Bot)) n++;
        foreach (var pair in NetworkServer.connections)
        {
            var identity = pair.Value != null ? pair.Value.identity : null;
            var person = identity != null ? identity.GetComponent<PlayerObjectController>() : null;
            if (person != null && !Listed(person)) n++;
        }
        if (CrowdForTest > 0 && Loopback.Active && Dev.Enabled) n += CrowdForTest;
        return n;
    }

    /// <summary>Developer test, in the loopback harness only: this many more people in the room than are here, as if they had all just arrived.</summary>
    internal static int CrowdForTest { get; set; }

    /// <summary>
    /// More people than seats, from two joiners in one frame or ADD BOT in the frame a person
    /// arrived: the newest bots give their seats up until everybody has one.
    /// </summary>
    private static void GiveWayToAll()
    {
        var nm = Net;
        if (nm == null || nm.GamePlayers == null) return;
        for (int i = 0; i < Limits.Max && Count > 0 && Present(nm) > Limits.Max; i++)
        {
            if (!RemoveOne()) return;
            Plugin.Log.LogInfo("[bot] more people than seats: a bot gave its seat up");
        }
    }

    /// <summary>
    /// Bots leave a lobby that has changed to a table they cannot play, said once. Whoever adds
    /// them adds them again on a table that takes them.
    /// </summary>
    private static void SendAwayIfUnwelcome(LobbyController lobby)
    {
        if (Bots().Count == 0 && _pending.Count == 0) return;
        string why = Unwelcome(lobby);
        if (why == null) return;

        int n = 0;
        while (Count > 0 && RemoveOne()) n++;
        SentAway++;
        Plugin.Log.LogWarning($"[bot] {why}: {n} bot(s) left the lobby");
    }

    /// <summary>One more each time bots leave a lobby that changed to a table they cannot play, for the host's line.</summary>
    internal static int SentAway { get; private set; }

    /// <summary>
    /// A person is joining a table with no seat left: the newest bot gives up its seat first, the
    /// way it leaves at REMOVE BOT, so the game has a podium to give them. Only in the lobby: in a
    /// match the lobby is shut, and a bot is never swapped for a person in the middle of one.
    /// </summary>
    internal static bool GiveWay(CustomNetworkManager nm)
    {
        if (!NetworkServer.active || nm == null || nm.GamePlayers == null || LobbyController.Instance == null) return false;
        if (Count == 0 || Present(nm) < Limits.Max) return false;
        return RemoveOne();
    }

    // --------------------------------------------------------------------- add

    /// <summary>One bot, or one line saying why not.</summary>
    internal static void AddBot() => TryAdd();

    /// <summary>The lobby's ADD BOT: one bot, or why not, for the words the host is shown.</summary>
    internal static Refusal AddFromLobby()
    {
        var nm = Net;
        string why = WhyNot(nm, out var kind);
        if (why != null)
        {
            Refused(why);
            return kind;
        }

        // A host who adds a bot chose this lobby: Quick Join's merge, which counts only people, must not move it.
        QuickMatch.StopLooking();
        return Spawn(nm) ? Refusal.None : Refusal.Other;
    }

    private static bool TryAdd()
    {
        var nm = Net;
        string why = WhyNot(nm);
        if (why != null)
        {
            Refused(why);
            return false;
        }
        return Spawn(nm);
    }

    private static string _lastRefusal;
    private static float _lastRefusalAt = -999f;

    /// <summary>
    /// The reason, once. A missing part cannot change, so it is said once a session; any other
    /// reason is not said again while it is still the answer a moment later.
    /// </summary>
    private static void Refused(string why)
    {
        float now = Time.unscaledTime;
        if (!BotSeats.Works || BotTurnGuard.Tripped)
        {
            if (_saidBroken) return;
            _saidBroken = true;
        }
        else if (why == _lastRefusal && now - _lastRefusalAt < 5f)
        {
            _lastRefusalAt = now;
            return;
        }
        _lastRefusal = why;
        _lastRefusalAt = now;
        Plugin.Log.LogWarning($"[bot] no bot added: {why}");
    }

    private static bool Spawn(CustomNetworkManager nm)
    {
        try
        {
            var prefab = nm.GamePlayerPrefab;
            if (prefab == null) { Plugin.Log.LogWarning("[bot] no bot added: the game's player prefab is not available"); return false; }

            var go = UnityEngine.Object.Instantiate(prefab.gameObject);
            var bot = go.GetComponent<PlayerObjectController>();
            if (bot == null)
            {
                Plugin.Log.LogWarning("[bot] no bot added: the copied player prefab has no PlayerObjectController");
                UnityEngine.Object.Destroy(go);
                return false;
            }

            int number = _nextNumber++;
            string name = FreeName(nm);
            go.name = $"LB8P_Bot{number}";

            // Set before spawning so the values travel in the object's initial state rather than
            // as a follow-up nobody may be listening for yet. No connection, numbered below zero.
            bot.NetworkConnectionID = -number;
            bot.NetworkPlayerSteamID = FirstId + (ulong)number;
            bot.NetworkPlayerName = name;

            var taken = new HashSet<int>();
            foreach (var p in nm.GamePlayers) if (p != null) taken.Add(p.PlayerSkin);
            foreach (var p in _pending) if (p.Bot != null) taken.Add(p.Bot.PlayerSkin);
            int character = SameCharacterForTest ? PersonCharacter(nm) : FreeCharacter(bot, taken);
            bot.UserCode_CmdSetSkinIndex__Int32(character >= 0 ? character : 0);

            NetworkServer.Spawn(go);

            // Everything else has to wait. Unity runs Start on the next frame, and until it has,
            // the object's own reference to the network manager is null - which is exactly what
            // CmdSetPlayer dereferences. Calling it here throws for every bot and leaves them
            // with no podium.
            _pending.Add(new Pending { Bot = bot, Name = name });
            Plugin.Log.LogInfo($"[bot] a bot joined the lobby, character {bot.PlayerSkin} ({Count} bot(s))");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[bot] no bot added: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ remove

    internal static void RemoveBot()
    {
        if (!NetworkServer.active) { Plugin.Log.LogWarning("[bot] only the host can remove bots"); return; }
        if (!RemoveOne()) Plugin.Log.LogWarning("[bot] there are no bots to remove");
    }

    /// <summary>The newest bot leaves, through the game's own leaving: it frees the podium and the roster entry.</summary>
    private static bool RemoveOne()
    {
        try
        {
            var bots = Bots();
            PlayerObjectController bot = bots.Count > 0 ? bots[bots.Count - 1] : null;
            if (bot == null)
            {
                for (int i = _pending.Count - 1; i >= 0 && bot == null; i--)
                {
                    bot = _pending[i].Bot;
                    _pending.RemoveAt(i);
                }
            }
            if (bot == null) return false;

            _leaving.Add(bot);
            NetworkServer.Destroy(bot.gameObject);
            Plugin.Log.LogInfo($"[bot] a bot left the lobby ({Count} bot(s) left)");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[bot] could not remove a bot: {e.Message}");
            return false;
        }
    }

    /// <summary>The bot standing on this podium, on the host, or null: a person's podium, or an empty one.</summary>
    internal static PlayerObjectController On(string slotName)
    {
        if (!NetworkServer.active || string.IsNullOrEmpty(slotName)) return null;
        foreach (var bot in Bots())
            if (bot.SlotName == slotName) return bot;
        return null;
    }

    /// <summary>This bot leaves, the way the newest does at REMOVE BOT. Only in the lobby.</summary>
    internal static bool Remove(PlayerObjectController bot, string how)
    {
        if (!NetworkServer.active || LobbyController.Instance == null || !IsBot(bot) || Leaving(bot)) return false;
        try
        {
            _pending.RemoveAll(p => p.Bot == null || p.Bot.Pointer == bot.Pointer);
            _leaving.Add(bot);
            NetworkServer.Destroy(bot.gameObject);
            Plugin.Log.LogInfo($"[bot] a bot left the lobby: {how} ({Count} bot(s) left)");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[bot] could not remove a bot: {e.Message}");
            return false;
        }
    }

    internal static void FillToMax() => FillTo(Limits.Max);

    /// <summary>
    /// Add bots until the lobby holds <paramref name="target"/> players, the host included. A
    /// refusal is said once, not once for every seat.
    /// </summary>
    internal static void FillTo(int target)
    {
        var nm = Net;
        if (target > Limits.Max) target = Limits.Max;

        string why = WhyNot(nm);
        if (why != null) { Refused(why); return; }

        int present = Present(nm);
        int want = target - present;
        if (want <= 0) { Dev.Warn("bot", $"already at {present}/{target}"); return; }

        Dev.Log("bot", $"filling {present} -> {target}");
        for (int i = 0; i < want; i++) if (!TryAdd()) break;
    }
}
