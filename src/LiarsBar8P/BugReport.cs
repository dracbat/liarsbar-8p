using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiarsBar8P;

/// <summary>
/// A bug report a player can save with one press and attach to an issue, with nothing personal
/// in it.
///
/// Asking players for <c>LogOutput.log</c> asked them to read a file of a thousand lines and
/// judge what in it was private, and the game's own <c>Player.log</c> is worse: it holds the
/// player's Unity sign-in, a live access token among it. So this reads no log file at all. It
/// listens to the mod's own log as it is written and keeps the last few hundred lines in memory,
/// through <see cref="LogScrub.Line"/>: the mod's own lines under tags known to print nothing
/// personal, and the game's exceptions, with names, ids, paths and anything shaped like a key
/// taken out. Over that goes a header of plain facts about this copy - versions, the mode, the
/// table's size, the system, the language - each a value the mod reads, never free text.
///
/// The file goes to <c>BepInEx/bug-reports</c> in the game folder, which a player already finds
/// through Steam's Browse local files, and which is the same place on Windows and on a Steam Deck.
/// The newest five are kept. Nothing is sent anywhere: there is no network code in this class.
///
/// Saved from a button beside Back in the settings, in the menu and in a match, or with F11
/// anywhere but ranked. Where it went is said under the tag at the top of the screen for a few
/// seconds, as a path inside the game folder, so a screenshot of it shows no user name.
/// </summary>
internal static class BugReport
{
    // Every word a player sees from this class. The button is in the footer's own title case,
    // beside Back and Restore Defaults.
    private const string ButtonWords = "Save Bug Report";
    private const string SavedWords = "Bug report saved: ";
    private const string FailedWords = "Could not save the bug report";

    private const string Folder = "bug-reports";
    private const int KeepLines = 300;
    private const int KeepFiles = 5;
    private const float ShowSeconds = 8f;
    private const float NamesEvery = 1f;

    private sealed class Entry
    {
        internal double At;
        internal string Text;
        internal int Repeats;
    }

    private static readonly LogScrub.Names _names = new();
    private static readonly object _lock = new();
    private static readonly LinkedList<Entry> _ring = new();
    private static readonly Stopwatch _clock = new();
    private static int _errors, _failedPatches;
    private static float _namesAt;
    private static string _shown;
    private static float _shownUntil;

    /// <summary>The settings buttons' words, kept to be written again when the language changes.</summary>
    private static readonly List<TextMeshProUGUI> _labels = new();

    /// <summary>The language those words were last written in, as <see cref="LiarsBar8P.Words.Generation"/> counts them.</summary>
    private static int _labelsIn;

    /// <summary>What the line under the tag says about the last save, while it lasts.</summary>
    internal static string Shown => _shown;

    /// <summary>The settings button's own words, for the test driver.</summary>
    internal static string Words => ButtonWords;

    /// <summary>
    /// From <c>Plugin.Load</c>, before anything else logs, so the start of the session is kept too.
    /// </summary>
    internal static void Listen()
    {
        try
        {
            _clock.Start();
            BepInEx.Logging.Logger.Listeners.Add(new Listener());
        }
        catch (Exception e)
        {
            try { Plugin.Log.LogWarning($"[bugreport] no bug report this session: {e.Message}"); } catch { }
        }
    }

    /// <summary>
    /// Called on whatever thread logged: string work and one lock, no Unity call. The game's own
    /// lines are nearly all of what arrives, and all but its errors stop at the first test.
    /// </summary>
    private sealed class Listener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs args)
        {
            try
            {
                if (args == null) return;
                bool mine = ReferenceEquals(args.Source, Plugin.Log);
                bool error = (args.Level & (LogLevel.Error | LogLevel.Fatal)) != 0;
                if (!mine && !error) return;

                string text = args.Data?.ToString();
                if (string.IsNullOrEmpty(text)) return;

                string source = args.Source?.SourceName;
                if (error && (mine || source == "Unity")) Interlocked.Increment(ref _errors);
                if (mine && text.StartsWith("  FAILED to patch", StringComparison.Ordinal))
                    Interlocked.Increment(ref _failedPatches);

                string line = LogScrub.Line(text, args.Level, mine, source, _names);
                if (line != null) Keep(line);
            }
            catch { }
        }

        public void Dispose() { }
    }

    private static void Keep(string line)
    {
        double at = _clock.Elapsed.TotalSeconds;
        lock (_lock)
        {
            var last = _ring.Last;
            if (last != null && last.Value.Text == line)
            {
                last.Value.Repeats++;
                return;
            }

            _ring.AddLast(new Entry { At = at, Text = line });
            while (_ring.Count > KeepLines) _ring.RemoveFirst();
        }
    }

    // ------------------------------------------------------------------ from ModTicker

    /// <summary>Four times a second: the line under the tag, the button's words, and the names this copy can see.</summary>
    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (_shown != null && now >= _shownUntil) _shown = null;
        Relabel();

        if (now < _namesAt) return;
        _namesAt = now + NamesEvery;
        LearnNames();
    }

    /// <summary>
    /// The button's words again when the language changes. The menu's settings are made before the
    /// game's language is first read, and a change made in the settings rewrites only the game's own
    /// buttons.
    /// </summary>
    private static void Relabel()
    {
        if (_labelsIn == LiarsBar8P.Words.Generation) return;
        _labelsIn = LiarsBar8P.Words.Generation;

        string said = LiarsBar8P.Words.T(ButtonWords);
        foreach (var label in _labels)
            if (label != null) label.text = said;
    }

    /// <summary>Every frame: F11 saves a report anywhere but ranked.</summary>
    internal static void Frame()
    {
        if (!Input.GetKeyDown(KeyCode.F11)) return;

        // The developer panel's own F11 starts a match, and wins while the panel is open.
        if (DevHud.PanelOpen || RankedGuard.Near) return;
        Save();
    }

    /// <summary>
    /// Names this copy has seen, so a line that still carries one can have it replaced: everyone
    /// in the lobby and at the table, and on Steam this player, the lobby's other members, and the
    /// lobby's own name, which is the host's name unless they changed it.
    /// </summary>
    private static void LearnNames()
    {
        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        if (nm != null && nm.GamePlayers != null)
            foreach (var p in nm.GamePlayers)
                if (p != null && !BotManager.IsBot(p)) _names.Add(p.PlayerName);

        var m = Manager.Instance;
        if (m != null && m.Players != null)
            foreach (var p in m.Players)
                if (p != null && !BotManager.IsBot(p)) _names.Add(p.PlayerName);

        // Steam is shut down on purpose in ranked, and nothing may wake it there.
        if (RankedGuard.Near || !Matchmaking.SteamUp) return;

        _names.Add(SteamFriends.GetPersonaName());

        var sl = SteamLobby.Instance;
        ulong id = sl != null ? sl.CurrentLobbyID : 0;
        if (id == 0) return;

        var lobby = new CSteamID(id);
        _names.Add(SteamMatchmaking.GetLobbyData(lobby, "name"));
        int members = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (int i = 0; i < members && i < Limits.Max * 2; i++)
            _names.Add(SteamFriends.GetFriendPersonaName(SteamMatchmaking.GetLobbyMemberByIndex(lobby, i)));
    }

    // ------------------------------------------------------------------ saving

    /// <summary>
    /// Write a report and say where it went. Returns the path inside the game folder, or null if
    /// nothing was saved. Main thread only.
    /// </summary>
    internal static string Save()
    {
        if (RankedGuard.Near) return null;

        try
        {
            try { LearnNames(); } catch { }

            string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, Folder);
            Directory.CreateDirectory(dir);

            string stem = $"bug-report-{DateTime.Now:yyyy-MM-dd-HHmmss}";
            string file = stem + ".txt";
            for (int n = 2; File.Exists(Path.Combine(dir, file)); n++) file = $"{stem}-{n}.txt";

            string text = Compose(out int lines);
            File.WriteAllText(Path.Combine(dir, file), text, new UTF8Encoding(false));
            Prune(dir);

            string shown = Path.Combine("BepInEx", Folder, file);
            Show(LiarsBar8P.Words.T(SavedWords) + shown);
            Plugin.Log.LogInfo($"[bugreport] a report was saved, with {lines} log line(s)");
            return shown;
        }
        catch (Exception e)
        {
            // The type only: the message of a file error holds the path, and the path the user name.
            Show(LiarsBar8P.Words.T(FailedWords));
            Plugin.Log.LogWarning($"[bugreport] could not save a report ({e.GetType().Name})");
            return null;
        }
    }

    private static void Show(string words)
    {
        _shown = words;
        _shownUntil = Time.unscaledTime + ShowSeconds;
    }

    private static void Prune(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("bug-report-*.txt");
            if (files.Length <= KeepFiles) return;

            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = KeepFiles; i < files.Length; i++)
                try { files[i].Delete(); } catch { }
        }
        catch { }
    }

    /// <summary>The whole file. English on purpose: it is read by whoever fixes the bug.</summary>
    private static string Compose(out int lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bug report for {Plugin.FullName}. Nothing has been sent anywhere.");
        sb.AppendLine("Names, Steam and lobby IDs, paths and anything that looks like a key have been taken out");
        sb.AppendLine("as far as the mod can tell. Read it through before you post it.");
        sb.AppendLine("Attach it to an issue at https://github.com/dracbat/liarsbar-8p/issues");
        sb.AppendLine();
        sb.AppendLine("What looked wrong:");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("-- this game --");
        Header(sb);
        sb.AppendLine("-- recent log, oldest first, seconds since the game started --");

        Entry[] kept;
        lock (_lock)
        {
            kept = new Entry[_ring.Count];
            _ring.CopyTo(kept, 0);
        }

        foreach (var e in kept)
        {
            // Again with every name known by now: some arrive after the lines they were in.
            string text = LogScrub.Again(e.Text, _names);
            sb.Append(e.At.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).PadLeft(8));
            sb.Append("  ").Append(text);
            if (e.Repeats > 0) sb.Append($" (repeated {e.Repeats + 1} times)");
            sb.AppendLine();
        }

        lines = kept.Length;
        return sb.ToString();
    }

    /// <summary>Plain facts about this copy, each read from the game or the mod, one guarded read at a time.</summary>
    private static void Header(StringBuilder sb)
    {
        sb.AppendLine($"mod version: {Plugin.Version}    max players: {Read(() => Plugin.MaxPlayers.Value.ToString())}");
        sb.AppendLine($"game: {Read(() => Application.version)}    build: {Read(Build)}    " +
                      $"unity: {Read(() => Application.unityVersion)}    " +
                      $"bepinex: {Read(() => typeof(BepInEx.Paths).Assembly.GetName().Version.ToString())}");
        sb.AppendLine($"system: {Read(Platform)}");
        sb.AppendLine($"language: {Read(Language)}");
        sb.AppendLine($"where: {Read(Where)}    scene: {Read(() => SceneManager.GetActiveScene().name)}");
        sb.AppendLine($"mode: {Read(Mode)}    players: {Read(Players)}    role: {Read(Role)}    " +
                      $"modded table: {(Table.Modded ? "yes" : "no")}");
        sb.AppendLine($"updates: {Read(() => Plugin.Updates.Value.ToString())}    " +
                      $"show unmodded lobbies: {Read(() => Plugin.ShowUnmoddedLobbies.Value.ToString())}    " +
                      $"auto ready: {Read(() => Plugin.AutoReadyJoiners.Value.ToString())}    " +
                      $"developer mode: {(Dev.Enabled ? "on" : "off")}");
        sb.AppendLine($"patches failed: {_failedPatches}    errors this session: {_errors}");
    }

    private static string Read(Func<string> read)
    {
        try
        {
            string value = read();
            return string.IsNullOrEmpty(value) ? "unknown" : value;
        }
        catch { return "unknown"; }
    }

    private static string Build()
    {
        if (RankedGuard.Near || !Matchmaking.SteamUp) return null;
        return SteamApps.GetAppBuildId().ToString();
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    /// <summary>Windows, or Linux through Proton (Wine answers a question Windows does not), and whether that is a Steam Deck.</summary>
    private static string Platform()
    {
        bool wine = false;
        try
        {
            var nt = GetModuleHandleW("ntdll.dll");
            wine = nt != IntPtr.Zero && GetProcAddress(nt, "wine_get_version") != IntPtr.Zero;
        }
        catch { }

        bool deck = false;
        try { deck = !RankedGuard.Near && Matchmaking.SteamUp && SteamUtils.IsSteamRunningOnSteamDeck(); }
        catch { }

        return deck ? "Steam Deck" : wine ? "Linux (Proton)" : "Windows";
    }

    private static string Language()
    {
        var locale = UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale;
        return locale != null ? locale.Identifier.Code : null;
    }

    private static string Where()
    {
        if (RankedGuard.Near) return "ranked";
        if (Manager.Instance != null) return "match";
        if (LobbyController.Instance != null) return "lobby";
        return SceneManager.GetActiveScene().name == Matchmaking.Menu ? "menu" : "elsewhere";
    }

    private static readonly string[] DeckVariants = { "Basic", "Devil", "Chaos", "Deck 2" };
    private static readonly string[] DiceVariants = { "Basic", "Traditional" };

    private static string Mode()
    {
        // In a match, the one place that decides what is being played: from the seats, on the
        // host and on a client alike. Unknown only for a moment, before anybody is seated.
        if (Manager.Instance != null)
        {
            var kind = TableHand.Playing();
            return kind == TableHand.Kind.None ? "a match, not known on this copy" : $"playing {kind}";
        }

        // In a lobby, the lobby's own values: a client is sent those, and never the network
        // manager's, which on a client still says whatever it said at the menu.
        var room = LobbyController.Instance;
        return room != null ? Named(room.Mode.ToString(), room.DeckMode, room.DiceMode) : "none";
    }

    private static string Named(string mode, int deck, int dice) =>
        mode == "LiarsDeck" ? $"{mode} ({Variant(DeckVariants, deck)})"
        : mode == "LiarsDice" ? $"{mode} ({Variant(DiceVariants, dice)})"
        : mode;

    private static string Variant(string[] names, int value) =>
        value >= 0 && value < names.Length ? names[value] : $"variant {value}";

    private static string Players()
    {
        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        int all = nm != null && nm.GamePlayers != null ? nm.GamePlayers.Count : 0;
        return $"{all} (bots {BotManager.Count})";
    }

    private static string Role() =>
        NetworkServer.active ? "host" : NetworkClient.active ? "client" : "none";

    // ------------------------------------------------------------------ the button in the settings

    [HarmonyPatch]
    internal static class SettingsButton
    {
        /// <summary>
        /// Back, never <c>btn_back (1)</c> beside it: that one is Restore Defaults, and a copy of it
        /// that kept a saved click would reset a player's settings. Back exists in the menu's
        /// settings and in a match's, and its own click only plays a sound.
        /// </summary>
        private const string MenuBack = "state_settings/footer/btn_back";
        private const string MatchBack = "Pause/Group/state_settings/footer/btn_back";
        internal const string Name = "LB8P_SaveReport";

        /// <summary>Left of Restore Defaults in the menu, and in the same spot in a match, which has only Back.</summary>
        private static readonly Vector2 Spot = new(-460f, 20f);

        /// <summary>The button, once it is up, for the test driver.</summary>
        internal static UnityEngine.UI.Button Made { get; private set; }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChangeSettings), nameof(ChangeSettings.Start))]
        private static void Added(ChangeSettings __instance)
        {
            try
            {
                if (__instance == null || RankedGuard.Near) return;

                bool menu = true;
                var back = __instance.transform.Find(MenuBack);
                if (back == null) { back = __instance.transform.Find(MatchBack); menu = false; }
                if (back == null)
                {
                    Plugin.Log.LogWarning("[bugreport] no Back button in these settings - no report button here");
                    return;
                }
                if (back.parent.Find(Name) != null) return;

                var copy = UiClone.Make(back.gameObject, back.parent, Name, new Action(Pressed));
                if (copy == null) return;

                var text = copy.GetComponent<TextMeshProUGUI>();
                if (text != null)
                {
                    float size = text.fontSize;
                    text.text = LiarsBar8P.Words.T(ButtonWords);
                    text.enableAutoSizing = true;
                    text.fontSizeMax = size;
                    text.fontSizeMin = 18f;

                    // Those of a scene gone are dropped here, so the list holds only buttons still up.
                    _labels.RemoveAll(l => l == null);
                    _labels.Add(text);
                }

                var box = copy.GetComponent<RectTransform>();
                box.anchorMin = box.anchorMax = new Vector2(1f, 0.5f);
                box.anchoredPosition = Spot;

                Made = copy.GetComponent<UnityEngine.UI.Button>();
                Plugin.Log.LogInfo($"[bugreport] the report button is in the {(menu ? "menu's" : "match's")} settings");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[bugreport] no report button in the settings: {e.Message}");
            }
        }

        private static void Pressed()
        {
            try { Save(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[bugreport] the button failed: {e.Message}"); }
        }
    }
}
