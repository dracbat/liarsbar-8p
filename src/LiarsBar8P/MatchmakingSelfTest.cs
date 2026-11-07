using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Mirror;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiarsBar8P;

/// <summary>
/// Developer only: one Steam account proving that a lobby can be found, with no second person.
///
/// Whether a tagged lobby is really returned by a real Steam search cannot be read out of the
/// game, and the loopback harness never creates a Steam lobby at all. But a search is only a
/// question to Steam, and it does not care which account hosted what, so two copies of the game
/// on one account can settle it, with the role coming from <c>LIARSBAR8P_MMTEST</c> because
/// every copy reads the same config file:
///
///   host       hosts a public tagged lobby by itself and reports what it published
///   list       runs the game's own search over and over and logs every lobby that comes back
///   listplain  the same search with this mod's changes left out, which is what a player without
///              the mod sends, and must never return a lobby the host above published
///   idle       none of the above, for a copy worked only through the commands below
///
/// A test copy can also be worked from outside the game, with no mouse and no window focus: lines
/// put in <c>LiarsBar8P\mmdrive\ROLE.txt</c> under local app data run one at a time and press the
/// game's real buttons. See <see cref="Run"/>.
///
/// <c>LIARSBAR8P_MMTEST_ROWCOUNT</c> is separate from those. It makes every row read that many
/// players, so the JOIN button can be seen at exactly four without four accounts: bots are not
/// Steam members, so they never move the count on a row.
///
/// Unlike <see cref="DiagAutoHost"/> this one is deliberately public, because a private lobby
/// proves nothing about a search. It does nothing unless developer mode is on as well.
///
/// Everything here is logged with the tag <c>[matchmaking] selftest</c> and uses real Steam.
/// </summary>
internal static class MatchmakingSelfTest
{
    private const string Variable = "LIARSBAR8P_MMTEST";
    private const string CountVariable = "LIARSBAR8P_MMTEST_ROWCOUNT";

    /// <summary>Long enough for the menu and the game's Steam setup to be finished.</summary>
    private const float Settle = 5f;

    /// <summary>Slow enough to leave Steam alone, quick enough to watch.</summary>
    private const float Every = 10f;

    private static bool _read;
    private static string _mode;
    private static bool _countRead;
    private static int _rowCount;
    private static float _readyAt;
    private static float _next;
    private static bool _hosted;
    private static bool _background;
    private static float _driveAt;
    private static float _backAt;
    private static string _role;
    private static string _shotWhen;
    private static string _shotName;
    private static string _backWhen;
    private static readonly Queue<string> _commands = new();

    private static string Mode
    {
        get
        {
            if (!_read)
            {
                _read = true;
                try
                {
                    string raw = Environment.GetEnvironmentVariable(Variable);
                    if (!string.IsNullOrEmpty(raw) && Dev.Enabled) _mode = raw.Trim().ToLowerInvariant();
                }
                catch { }
            }
            return _mode;
        }
    }

    /// <summary>This copy is standing in for a player without the mod.</summary>
    internal static bool PlainList => Mode == "listplain";

    /// <summary>Holds Start as if somebody were still arriving, which needs a second account to happen for real.</summary>
    internal static bool PretendArriving { get; private set; }

    /// <summary>Stops the heartbeat, to see a lobby expire with its owner still in it, as one left behind by a crash would.</summary>
    internal static bool NoHeartbeat { get; private set; }

    /// <summary>
    /// What every row is named, numbered down the list ("Player{0}'s lobby"), or null for the
    /// real names: for a picture of the list that names nobody.
    /// </summary>
    internal static string RowNames { get; private set; }

    /// <summary>What every row's count reads, or zero for the real count.</summary>
    internal static int RowCount
    {
        get
        {
            if (!_countRead)
            {
                _countRead = true;
                try
                {
                    string raw = Environment.GetEnvironmentVariable(CountVariable);
                    if (Dev.Enabled && int.TryParse(raw, out int count) && count > 0) _rowCount = count;
                }
                catch { }
            }
            return _rowCount;
        }
    }

    /// <summary>Hosts the next lobby as Private, so a test lobby is never listed for strangers.</summary>
    internal static bool HostingPrivate { get; private set; }

    /// <summary>
    /// What a bare copy (<see cref="Plugin.Bare"/>) refuses: hosting a public lobby and joining
    /// one. It has none of the mod's lobby parts, so a lobby it opened would carry no modded tag
    /// and the game's own list would show it to players without the mod, and a lobby it joined
    /// could be anybody's. Its one Steam lobby is the driver's 'hostprivate', which
    /// <see cref="DiagAutoHost"/> keeps private there as in a full copy.
    /// </summary>
    private static readonly HashSet<string> BareRefuses = new(StringComparer.Ordinal) { "host", "hostgame", "quick", "join", "idjoin" };

    /// <summary>
    /// The commands that work outside the loopback test harness: screenshots and read-outs, the
    /// lobby list, hosting, searching and leaving a lobby, and this copy's own screens and
    /// settings. Each does no more than this copy's player could with a click or a key. Every
    /// other command, and any added later, changes a table or shows something hidden, so it works
    /// only where <see cref="Fair.TestsAllowed"/> holds.
    /// </summary>
    private static readonly HashSet<string> RealSteamCommands = new(StringComparer.Ordinal)
    {
        "wait", "shot", "fps", "shotwhen", "backwhen", "state", "printdeck", "open", "rownames", "quick", "back",
        "refresh", "box", "idjoin", "join", "name", "host", "hostgame", "hostprivate", "leave", "stophost",
        "note", "warn", "fakeoffer", "fakedone", "fakeclear", "fonts", "turnline", "aimline",
        "newpos", "newhit", "newclick", "rankedbutton", "gate", "watchnear", "emotetime", "upkeep",
        "tab", "deckright", "deckleft", "padnext", "padprev", "deckstate", "deckhelp", "pageclose",
        "copyid", "clipkeep", "clipback", "clipboard", "idcheck", "ownrow", "countnow", "countshow",
        "tagspot", "scrubtest", "report", "pressreport", "reportbutton", "pause", "unpause", "settings",
        "skin", "skinstate", "skinrace", "clonetest", "board", "boardstate", "boardpad", "awards",
        "endshot", "awardstate", "rules", "rulesstate", "quickrank", "block", "blockstate", "blockgeom",
        "blockhit", "blockedlist", "unblockrow", "blockedstate", "blockself", "blockclear", "lang",
        "speccam", "twocard", "twostep", "twohelp", "uidump", "press", "prefs", "widepick", "widelist",
        "widekeep", "widestep", "wideapply", "widereset", "wideart", "switchto", "settingspage",
        "menupress", "menudump", "buttons", "rect", "animinfo", "partshot",
    };

    private const string BareRefusal = "a bare copy hosts no public lobby and joins none (it has no modded tag, so players without the mod would see it); 'hostprivate' is its one lobby";

    /// <summary>From <see cref="ModTicker"/>. Nothing unless the variable is set.</summary>
    internal static void Tick()
    {
        string mode = Mode;
        if (mode == null) return;

        // In the loopback harness only the commands run: there is no Steam lobby to host or list.
        if (Loopback.Active)
        {
            try { Drive(Time.unscaledTime); }
            catch (Exception e) { Plugin.Log.LogError($"[matchmaking] drive: {e.Message}"); }
            return;
        }

        try
        {
            float now = Time.unscaledTime;

            // Copies on one PC cannot all have focus, and a paused copy tests nothing.
            if (!_background)
            {
                _background = true;
                Plugin.Log.LogInfo($"[matchmaking] selftest: runInBackground was {Application.runInBackground}");
                Application.runInBackground = true;
            }

            Drive(now);

            if (mode == "idle" || now < _next) return;

            if (mode == "host" && _hosted)
            {
                _next = now + Every;
                ReportOwnLobby();
                return;
            }

            if (!SteamManager.Initialized) return;

            var sl = SteamLobby.Instance;
            if (sl == null || LobbyListManager.instance == null) return;
            if (SceneManager.GetActiveScene().name != Matchmaking.Menu) return;

            if (_readyAt <= 0f)
            {
                _readyAt = now + Settle;
                Plugin.Log.LogWarning($"[matchmaking] selftest: this copy is '{mode}'");
                return;
            }
            if (now < _readyAt) return;

            if (mode == "host" && Plugin.Bare)
            {
                Plugin.Log.LogWarning($"[matchmaking] selftest: 'host' refused: {BareRefusal}");
                _mode = null;
                return;
            }

            if (mode == "host")
            {
                _hosted = true;
                _next = now + Every;
                if (DiagAutoHost.Active)
                    Plugin.Log.LogWarning("[matchmaking] selftest: SelfTestAutoHostLobby is on and forces lobbies " +
                                          "private, which proves nothing here - turn it off");
                Plugin.Log.LogWarning("[matchmaking] selftest: hosting a PUBLIC tagged lobby");
                QuickMatch.HostPublic(sl);
                return;
            }

            if (mode == "list" || mode == "listplain")
            {
                _next = now + Every;
                Plugin.Log.LogInfo($"[matchmaking] selftest: searching (our pool: {Matchmaking.Active})");
                sl.GetLobbiesList();
                return;
            }

            Plugin.Log.LogWarning($"[matchmaking] selftest: '{mode}' is not a role - use host, list, listplain or idle");
            _mode = null;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[matchmaking] selftest: {e.Message}");
            _next = Time.unscaledTime + Every;
        }
    }

    // ------------------------------------------------------------ working the menus

    private static string Role => _role ??= Environment.GetEnvironmentVariable("LIARSBAR8P_ROLE") ?? "game";

    private static string Under(string folder, string file)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiarsBar8P", folder);
        Directory.CreateDirectory(root);
        return Path.Combine(root, file);
    }

    private static void Drive(float now)
    {
        Watch();
        Clocks(now);

        if (_commands.Count == 0)
        {
            string file = Under("mmdrive", Role + ".txt");
            if (File.Exists(file))
            {
                foreach (string line in File.ReadAllLines(file))
                    if (!string.IsNullOrWhiteSpace(line)) _commands.Enqueue(line.Trim());
                File.Delete(file);
            }
        }

        if (now < _driveAt || _commands.Count == 0) return;

        string command = _commands.Dequeue();
        Plugin.Log.LogInfo($"[matchmaking] drive: {command}");
        try { Run(command, now); }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] drive: '{command}' failed: {e.Message}"); }
    }

    /// <summary>
    /// A shot, or Back, at the moment some words come on screen. These lines can last under a
    /// second, so nothing sent from outside could hit them.
    /// </summary>
    private static string _partShotPath, _partShotName;
    private static float _partShotAt, _partShotNext;

    /// <summary>The part named by "partshot" on the screen: a shot a moment later, once.</summary>
    private static void WatchPart()
    {
        if (_partShotPath == null) return;
        float now = Time.unscaledTime;
        if (_partShotAt > 0f)
        {
            if (now < _partShotAt) return;
            Plugin.Log.LogInfo($"[matchmaking] drive: partshot: {_partShotPath} is on the screen, shot {_partShotName}");
            ScreenCapture.CaptureScreenshot(Under("mmshots", $"{Role}-{_partShotName}.png"));
            _partShotPath = null;
            return;
        }
        if (now < _partShotNext) return;
        _partShotNext = now + 0.25f;
        var rt = Part(_partShotPath)?.TryCast<RectTransform>();
        if (rt == null || !rt.gameObject.activeInHierarchy) return;
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        rt.GetWorldCorners(corners);
        if (corners[2].x > 0f && corners[0].x < Screen.width && corners[2].y > 0f && corners[0].y < Screen.height) _partShotAt = now + 1.5f;
    }

    private static void Watch()
    {
        WatchEnd();
        WatchAim();
        WatchReveals();
        WatchMoments();
        WatchShots();
        WatchPart();
        if (_shotWhen == null && _backWhen == null && _backAt <= 0f) return;

        var list = LobbyListManager.instance;
        var room = LobbyController.Instance;
        string box = list != null && list.ConnectingScreen != null && list.ConnectingScreen.activeInHierarchy &&
                     list.ConnectingScreenInfo != null ? list.ConnectingScreenInfo.text : "";
        string label = room != null && room.lobbyvisible != null ? room.lobbyvisible.text : "";

        if (_shotWhen != null && (box.Contains(_shotWhen) || label.Contains(_shotWhen)))
        {
            _shotWhen = null;
            Plugin.Log.LogInfo($"[matchmaking] drive: saw it, shot {_shotName}");
            ScreenCapture.CaptureScreenshot(Under("mmshots", $"{Role}-{_shotName}.png"));
        }

        // A second later, as a person would: in the same frame the panel is still opening.
        if (_backWhen != null && box.Contains(_backWhen))
        {
            _backWhen = null;
            _backAt = Time.unscaledTime + 1f;
        }

        if (_backAt > 0f && Time.unscaledTime >= _backAt)
        {
            _backAt = 0f;
            Plugin.Log.LogInfo("[matchmaking] drive: saw it, pressing Back");
            Press(QuickMatch.ListPanel(list), "btn_return");
        }
    }

    /// <summary>One line: a word, and whatever it needs after it.</summary>
    private static int _fpsFrame;
    private static float _fpsAt;

    /// <summary>
    /// The frame rate since the last <c>fps</c> line, from the frame count: a copy on the kit's own
    /// desktop is never on screen, so nothing else shows how fast it draws.
    /// </summary>
    private static void LogFps()
    {
        int frame = Time.frameCount;
        float at = Time.realtimeSinceStartup;
        string rate = _fpsAt > 0f && at > _fpsAt
            ? $"{(frame - _fpsFrame) / (at - _fpsAt):0.0} frames a second over the last {at - _fpsAt:0.0} s"
            : "the first count";
        Plugin.Log.LogInfo($"[matchmaking] drive: fps: frame {frame} at {at:0.0} s, {rate}; this frame {Time.unscaledDeltaTime * 1000f:0.0} ms, " +
                           $"target {Application.targetFrameRate}, vsync {QualitySettings.vSyncCount}");
        _fpsFrame = frame;
        _fpsAt = at;
    }

    private static void Run(string line, float now)
    {
        int space = line.IndexOf(' ');
        string word = space < 0 ? line : line.Substring(0, space);
        string rest = space < 0 ? "" : line.Substring(space + 1).Trim();

        if (Plugin.Bare && BareRefuses.Contains(word))
        {
            Plugin.Log.LogWarning($"[matchmaking] drive: '{word}' refused: {BareRefusal}");
            return;
        }

        if (!Fair.TestsAllowed && !RealSteamCommands.Contains(word))
        {
            Plugin.Log.LogWarning($"[matchmaking] drive: '{word}' refused: it works only in the loopback test harness");
            return;
        }

        var sl = SteamLobby.Instance;
        var list = LobbyListManager.instance;
        var panel = list != null ? QuickMatch.ListPanel(list) : null;

        switch (word)
        {
            case "wait":
                _driveAt = now + float.Parse(rest, CultureInfo.InvariantCulture);
                break;
            case "shot":
                ScreenCapture.CaptureScreenshot(Under("mmshots", $"{Role}-{rest}.png"));
                break;
            case "fps":
                LogFps();
                break;
            case "shotwhen":
                _shotName = rest.Substring(0, rest.IndexOf(' '));
                _shotWhen = rest.Substring(rest.IndexOf(' ') + 1);
                break;
            case "backwhen":
                _backWhen = rest;
                break;
            case "state":
                LogState();
                break;
            case "printdeck":
                // The developer panel's F6: card counts, and the values only inside the loopback test harness.
                DevCommands.PrintDeck();
                break;
            case "open":
                Plugin.Log.LogInfo($"[matchmaking] drive: the list opened: {QuickMatch.OpenList(list)}");
                break;
            case "rownames":
                // "rownames Player{0}'s lobby": rows built from now on are named that, numbered down the list; "rownames off" puts the real names back.
                RowNames = rest == "off" || rest.Length == 0 ? null : rest;
                Plugin.Log.LogInfo($"[matchmaking] drive: rows {(RowNames != null ? "get made-up names" : "keep their own names")}");
                break;
            case "quick":
                Press(panel, "footer/QuickJoin");
                break;
            case "back":
                Press(panel, "btn_return");
                break;
            case "refresh":
                Press(panel, "footer/RefreshButton");
                break;
            case "box":
                Box(list, rest);
                break;
            case "idjoin":
                Press(panel, "footer/JoinWithId");
                list.LobbyIdInput.text = rest;
                Press(panel, "JoinWithId/Button");
                break;
            case "join":
                JoinFirstRow(list);
                break;
            case "name":
                sl.lobbyname.text = rest;
                break;
            case "host":
                QuickMatch.HostPublic(sl);
                break;
            case "hostgame":
                sl.HostLobby();
                break;
            case "leave":
                LobbyController.Instance.ReturnMenu();
                break;
            case "bots":
                for (int i = int.Parse(rest); i > 0; i--) BotManager.AddBot();
                break;
            case "nobots":
                while (BotManager.Count > 0) BotManager.RemoveBot();
                break;
            case "botarrival":
                // "botarrival off": as if the part that hands a bot's seat to a joiner had not gone on; "botarrival on" puts it back.
                BotLobby.ArrivalOn = rest != "off";
                Plugin.Log.LogInfo($"[matchmaking] drive: a bot gives its seat to a joiner: {BotLobby.ArrivalOn}");
                break;
            case "botcrowd":
                // "botcrowd 2": the host counts two more people in the room than are here, as two joiners in one frame; "botcrowd 0".
                BotManager.CrowdForTest = int.Parse(rest, CultureInfo.InvariantCulture);
                Plugin.Log.LogInfo($"[matchmaking] drive: {BotManager.CrowdForTest} pretend arrival(s), {BotManager.Count} bot(s) here");
                break;
            case "botskin":
                // "botskin same": bots added from now on play the host's character; "botskin off" undoes it.
                BotManager.SameCharacterForTest = rest == "same";
                Plugin.Log.LogInfo($"[matchmaking] drive: bots {(BotManager.SameCharacterForTest ? "play the host's character" : "each play one nobody has")}");
                break;
            case "botnames":
                // "botnames Player2|Player3": bots added from now on take these names first; "botnames off" gives them their own.
                Loopback.SetBotTestNames(rest == "off" ? null : rest);
                Plugin.Log.LogInfo($"[matchmaking] drive: {Loopback.BotTestNames().Length} made-up bot name(s)");
                break;
            case "arrive":
                PretendArriving = rest == "1";
                break;
            case "noheartbeat":
                NoHeartbeat = true;
                break;
            case "startinfo":
                StartInfo();
                break;
            case "startmatch":
                DevCommands.StartMatch();
                break;
            case "endmatch":
                Manager.Instance.ForceEndGameWithWinner(UnityEngine.Object.FindObjectOfType<PlayerStats>());
                break;
            case "ranked":
                UnityEngine.Object.FindObjectOfType<LoadingInGame>().LoadMatchmakingScene();
                break;
            case "note":
                AutoUpdate.Note = rest.Length > 0 ? rest : null;
                break;
            case "warn":
                VersionHud.Mismatch = rest.Length > 0 ? rest : null;
                break;
            case "fakeoffer":
                AutoUpdate.Pretend(AutoUpdate.Stage.Asking);
                break;
            case "fakedone":
                AutoUpdate.InPlace = true;
                AutoUpdate.Pretend(AutoUpdate.Stage.Installed);
                break;
            case "fakeclear":
                AutoUpdate.InPlace = false;
                AutoUpdate.RestartLater();
                AutoUpdate.Decline(null);
                break;
            case "fonts":
                foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Font>()))
                    Plugin.Log.LogInfo($"[matchmaking] drive: font '{found.name}'");
                foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMPro.TMP_FontAsset>()))
                {
                    var asset = found.TryCast<TMPro.TMP_FontAsset>();
                    Plugin.Log.LogInfo($"[matchmaking] drive: TMP font '{asset.name}', source '{(asset.sourceFontFile != null ? asset.sourceFontFile.name : "none")}'");
                }
                break;
            case "turnline":
                TurnHud.Pretend(rest);
                break;
            case "aimline":
                // "seats 2 8 locked": the line a real aim builds for a seat two round at a table of eight.
                var aimArgs = rest.Split(' ');
                if (aimArgs.Length >= 3 && aimArgs[0] == "seats")
                    AimHud.Pretend(AimHud.Sentence("Player2", int.Parse(aimArgs[1], CultureInfo.InvariantCulture),
                                                   int.Parse(aimArgs[2], CultureInfo.InvariantCulture), aimArgs.Length > 3 && aimArgs[3] == "locked"));
                else AimHud.Pretend(rest);
                break;
            case "aimshot":
                _aimShot = rest;
                _aimSeenAt = 0f;
                break;
            case "nextmatch":
                Plugin.Log.LogInfo($"[matchmaking] drive: next match at {Loopback.NextMatch(rest)}");
                break;
            case "newpos":
                WhatsNew.ScrollTo(float.Parse(rest, CultureInfo.InvariantCulture));
                break;
            case "newhit":
                NewHit();
                break;
            case "newclick":
                WhatsNew.ContinueButton.onClick.Invoke();
                break;
            case "unranked":
                UnityEngine.Object.FindObjectOfType<MatchmakerTicketer>().ReturnMainMenu();
                break;
            case "rankedbutton":
                PressRanked(panel);
                break;
            case "rankedskip":
                RankedGuard.SkipButtonForTest = rest == "1";
                break;
            case "gate":
                LogGate();
                break;
            case "watchnear":
                _nearUntil = now + float.Parse(rest, CultureInfo.InvariantCulture);
                _nearNext = 0f;
                break;
            case "notag":
                Table.PretendUntagged = rest == "1";
                break;
            case "strayid":
                StrayId(sl, rest == "1");
                break;
            case "emotetime":
                StartEmoteClock();
                break;
            case "hostprivate":
                sl.lobyhosted = false;
                HostingPrivate = true;
                try { sl.HostLobby(); }
                finally { HostingPrivate = false; }
                break;
            case "upkeep":
                LogUpkeep(sl);
                break;
            case "ingame":
                // What the game's own start writes on the table's lobby, which a bot at the table
                // then overwrites with "off". Written on the table's own lobby whatever the id names.
                if (Table.Hosted(out var table)) SteamMatchmaking.SetLobbyData(table, Matchmaking.StateKey, rest);
                Plugin.Log.LogInfo($"[matchmaking] drive: the table's own lobby says InGame '{Matchmaking.Clean(rest, 12)}' (hosted: {table.m_SteamID != 0})");
                break;
            case "keptwindow":
                DealTrace.IgnoreKeptHands = rest == "off";
                Plugin.Log.LogInfo($"[matchmaking] drive: a round that keeps the hands {(DealTrace.IgnoreKeptHands ? "is left unwatched, as before" : "opens the deal window")}");
                break;
            case "tab":
            {
                int tab = int.Parse(rest, CultureInfo.InvariantCulture);
                // Outside the loopback harness only the modes the lobby has a tab for.
                if (!Fair.TestsAllowed && tab != 0 && tab != 1 && tab != 2 && tab != 5 && tab != 6)
                {
                    Plugin.Log.LogWarning($"[matchmaking] drive: 'tab {tab}' refused: that mode has no tab in the lobby");
                    break;
                }
                LobbyController.Instance.ChangeGameMode(tab);
                break;
            }
            case "deckright":
                LobbyController.Instance.ChangeGameModeDeckRight();
                LogDeck();
                break;
            case "deckleft":
                LobbyController.Instance.ChangeGameModeDeckLeft();
                LogDeck();
                break;
            case "padnext":
                LobbyController.Instance.ChangeNextDeckOrDiceMode();
                LogDeck();
                break;
            case "padprev":
                LobbyController.Instance.ChangePrevDeckOrDiceMode();
                LogDeck();
                break;
            case "deckstate":
                LogDeck();
                break;
            case "deckhelp":
                PressHelp();
                break;
            case "pageclose":
                PressPageClose();
                break;
            case "forcelook":
            case "handpos":
                foreach (var own in UnityEngine.Object.FindObjectsOfType<DeckGameplay>())
                {
                    if (own == null || !own.isOwned) continue;
                    if (word == "forcelook") { own.ForceLookForLiarAnim(); Plugin.Log.LogInfo("[matchmaking] drive: this copy's player looks at its cards"); break; }
                    var where = new System.Text.StringBuilder();
                    for (int i = 0; own.Cards != null && i < own.Cards.Count; i++)
                    {
                        var c = own.Cards[i];
                        if (c == null) continue;
                        var at = c.transform.localPosition;
                        where.Append($" [{i} {(c.activeInHierarchy ? "on" : "off")} p{at.x:0.000},{at.y:0.000},{at.z:0.000} r{c.transform.localEulerAngles.z:0}]");
                    }
                    Plugin.Log.LogInfo($"[matchmaking] drive: this copy's hand:{where}");
                    break;
                }
                break;
            case "look":
                // Host only: a seat raises its cards, the way its player holding the look key would.
                foreach (var seat in Dev.TablePlayers())
                {
                    var hand = seat.GetComponent<DeckGameplay>();
                    if (hand == null || (rest != "all" && seat.Slot.ToString(CultureInfo.InvariantCulture) != rest)) continue;
                    hand.NetworkLooking = true;
                    Plugin.Log.LogInfo($"[matchmaking] drive: seat {seat.Slot} raises its cards");
                }
                break;
            case "skinfor":
                string[] pair = rest.Split(' ');
                _skinFor[int.Parse(pair[0], CultureInfo.InvariantCulture)] = int.Parse(pair[1], CultureInfo.InvariantCulture);
                break;
            case "copyid":
                LobbyController.Instance.CopyText();
                break;
            case "clipkeep":
                _clipWas = GUIUtility.systemCopyBuffer;
                Plugin.Log.LogInfo($"[matchmaking] drive: clipboard kept ({(_clipWas ?? "").Length} characters, not shown)");
                break;
            case "clipback":
                if (!string.IsNullOrEmpty(_clipWas)) GUIUtility.systemCopyBuffer = _clipWas;
                Plugin.Log.LogInfo($"[matchmaking] drive: clipboard put back ({(string.IsNullOrEmpty(_clipWas) ? "it held no text" : "text")})");
                _clipWas = null;
                break;
            case "clipboard":
                LogClipboard();
                break;
            case "idcheck":
                Plugin.Log.LogInfo($"[matchmaking] drive: the ID box would say '{(LobbyId.WhyNot(rest) ?? "nothing - it would join").Replace('\n', ' ')}' to '{rest}'");
                break;
            case "ownrow":
                // Host on Steam: the pool's search from the lobby, saying whether this lobby is listed and what its row reads.
                Plugin.Log.LogInfo($"[matchmaking] drive: own row: {LobbyCount.SearchForOwnRow()}");
                break;
            case "countnow":
                LobbyCount.LookNow();
                break;
            case "countshow":
                if (rest == "off") LobbyCount.PretendLobbies = -1;
                else
                {
                    string[] two = rest.Split(' ');
                    LobbyCount.PretendLobbies = int.Parse(two[0], CultureInfo.InvariantCulture);
                    LobbyCount.PretendPlayers = two.Length > 1 ? int.Parse(two[1], CultureInfo.InvariantCulture) : 0;
                }
                break;
            case "tagspot":
                Plugin.Log.LogInfo($"[matchmaking] drive: the tag stands {VersionTag.Shift:0.0} aside (screen {Screen.width}x{Screen.height})");
                break;
            case "stophost":
                NetworkManager.singleton.StopHost();
                break;
            case "holdstart":
                Loopback.HoldStart = rest == "1";
                Plugin.Log.LogInfo($"[matchmaking] drive: the harness {(Loopback.HoldStart ? "holds everyone in the lobby" : "may start the match")}");
                break;
            case "scrubtest":
                ScrubTest();
                break;
            case "report":
                Plugin.Log.LogInfo($"[matchmaking] drive: report saved as {BugReport.Save() ?? "nothing"}");
                break;
            case "pressreport":
                PressReport();
                break;
            case "reportbutton":
                LogReportButton();
                break;
            case "pause":
                Manager.Instance.Pause();
                break;
            case "unpause":
                Manager.Instance.UnPause();
                break;
            case "settings":
                OpenSettings();
                break;
            case "skin":
                Plugin.Log.LogInfo($"[matchmaking] drive: wardrobe arrow {rest} pressed: {LobbyWardrobe.Press(rest.StartsWith("-") ? -1 : 1)}");
                break;
            case "skinstate":
                LogSkins();
                break;
            case "skinrace":
                SkinRace();
                break;
            case "clonetest":
                CloneTest();
                break;
            case "board":
                MatchBoard.Pretend = rest != "off";
                Plugin.Log.LogInfo($"[matchmaking] drive: board key {(MatchBoard.Pretend ? "held" : "let go")}");
                break;
            case "boardstate":
                Plugin.Log.LogInfo($"[matchmaking] drive: board: {MatchBoard.Describe()}");
                break;
            case "boardpad":
                MatchBoard.PretendPad = rest != "off";
                Plugin.Log.LogInfo($"[matchmaking] drive: board hint {(MatchBoard.PretendPad ? "as on a controller" : "as on a keyboard")}");
                break;
            case "awards":
                if (rest == "off") AwardPanel.Show(MatchStats.Mode.None, new List<(MatchStats.Award, int, int)>());
                else AwardPanel.ShowMadeUp();
                Plugin.Log.LogInfo($"[matchmaking] drive: {AwardPanel.Describe()}");
                break;
            case "endshot":
                _endShot = rest;
                _endSeenAt = 0f;
                break;
            case "awardstate":
                Plugin.Log.LogInfo($"[matchmaking] drive: tally {MatchStats.Tally()}; {AwardPanel.Describe()}");
                break;
            case "taint":
                MatchStats.Taint();
                break;
            case "rules":
                // Through the row's own arrow, as the host's click would; straight to the choice where there is no row.
                int want = int.Parse(rest, CultureInfo.InvariantCulture);
                for (int i = 0; i < HouseRules.Names.Length && HouseRules.Picked != want; i++)
                    if (!RuleRow.Press(1)) HouseRules.Pick(HouseRules.Picked + 1);
                Plugin.Log.LogInfo($"[matchmaking] drive: rules {want}: {HouseRules.Describe()}");
                break;
            case "quickrank":
                // Quick Join's choice from the last search, without joining it: one account cannot join its own lobby.
                int members = QuickMatch.Best(sl, list, out var pick, out string pickRules);
                // Each lobby found by its place in id order, never its id: whether the pick is just the lower id.
                var order = new List<ulong>();
                if (sl.lobbiesIDs != null) foreach (var id in sl.lobbiesIDs) order.Add(id.m_SteamID);
                order.Sort();
                var each = new System.Text.StringBuilder();
                foreach (ulong id in order)
                {
                    var lobby = new CSteamID(id);
                    each.Append($" [id order {order.IndexOf(id) + 1}: {SteamMatchmaking.GetNumLobbyMembers(lobby)} in it, " +
                                $"{HouseRules.RowName(SteamMatchmaking.GetLobbyData(lobby, HouseRules.Key)) ?? "CLASSIC"}, " +
                                $"{(Matchmaking.Seatable(lobby) ? "seatable" : "not seatable")}{(id == pick.m_SteamID ? ", PICKED" : "")}]");
                }
                Plugin.Log.LogInfo($"[matchmaking] drive: Quick Join would pick " +
                                   (members < 0 ? "nothing" : $"a lobby with {members} in it, {pickRules ?? "CLASSIC"}") +
                                   $", of {order.Count} found:{each}");
                break;
            case "deadspin":
                RuleEffects.DeadMansSpin.SpareForTest = rest == "spare";
                Plugin.Log.LogInfo($"[matchmaking] drive: dead spins {(RuleEffects.DeadMansSpin.SpareForTest ? "that draw a shot are turned to Hearts" : "as the rule has them")}");
                break;
            case "seatplay":
                // "seatplay off": the harness plays nobody's seat, as in a bare copy; "seatplay on" undoes it.
                ModePlay.OffForTest = rest == "off";
                Plugin.Log.LogInfo($"[matchmaking] drive: the harness {(ModePlay.OffForTest ? "plays no seat" : "plays the seats it drives")}");
                break;
            case "threecups":
                // "threecups off": Three Dice leaves each cup's five dice as the game has them, for the count before the fix.
                // "threecups norow": the spare dice put away, their face pictures left in the reveal row as before batch B2.
                RuleEffects.ThreeDiceCups.OffForTest = rest == "off";
                RuleEffects.ThreeDiceCups.RowOffForTest = rest == "norow";
                Plugin.Log.LogInfo($"[matchmaking] drive: Three Dice cups {(RuleEffects.ThreeDiceCups.OffForTest ? "left as the game has them" : "kept to the dice dealt")}" +
                                   $"{(RuleEffects.ThreeDiceCups.RowOffForTest ? ", the spare dice's pictures left as they were" : "")}");
                break;
            case "dicerow":
                // Every seat's dice as this copy has them: in the cup or put away, and each one's two face pictures. Counts and faces only.
                Plugin.Log.LogInfo($"[matchmaking] drive: dice rows: {DiceRows()}");
                break;
            case "dicerowat":
                // "dicerowat 3": the same, four and a half seconds into each of the next 3 reveals, when the rows are up.
                _rowsAtReveals = int.Parse(rest, CultureInfo.InvariantCulture);
                Plugin.Log.LogInfo($"[matchmaking] drive: dice rows at the next {_rowsAtReveals} reveal(s)");
                break;
            case "harnessstart":
                Loopback.StartAgain();
                Plugin.Log.LogInfo("[matchmaking] drive: the harness starts the next match");
                break;
            case "ruleseen":
                Plugin.Log.LogInfo($"[matchmaking] drive: table: {RuleSeen()}");
                break;
            case "gamekick":
                // Host: the game's own Kick on one of its four podiums, by its own method.
                var kickSlot = LobbyController.Instance.SpawnSlots[int.Parse(rest, CultureInfo.InvariantCulture) - 1];
                kickSlot.Kick();
                Plugin.Log.LogInfo($"[matchmaking] drive: the game's own kick pressed on podium {rest}");
                break;
            case "readyall":
                // Host only: everyone at the table ready, as the harness does before its own start.
                int readied = 0;
                foreach (var p in Dev.LobbyPlayers())
                    if (p != null && !p.Ready) { p.NetworkReady = true; readied++; }
                Plugin.Log.LogInfo($"[matchmaking] drive: readied {readied} player(s)");
                break;
            case "rulesstate":
                Plugin.Log.LogInfo($"[matchmaking] drive: rules: {HouseRules.Describe()}; {RuleRow.Describe()}");
                break;
            case "rulesrefuse":
                HouseRules.RefuseForTest = rest == "1";
                Plugin.Log.LogInfo($"[matchmaking] drive: house rules {(HouseRules.RefuseForTest ? "refused as if a version differed" : "allowed again")}");
                break;
            case "block":
            case "kick":
                Plugin.Log.LogInfo($"[matchmaking] drive: {word} on podium {rest}: " +
                                   BlockButtons.Press(int.Parse(rest, CultureInfo.InvariantCulture), word == "kick"));
                break;
            case "blockstate":
                Plugin.Log.LogInfo($"[matchmaking] drive: plates:{BlockButtons.Describe()}");
                break;
            case "swapids":
                Plugin.Log.LogInfo($"[matchmaking] drive: {SwapIds(rest)}");
                break;
            case "blockgeom":
                Plugin.Log.LogInfo($"[matchmaking] drive: plate {rest}:{BlockButtons.Geometry(int.Parse(rest, CultureInfo.InvariantCulture))}");
                break;
            case "blockhit":
                Plugin.Log.LogInfo($"[matchmaking] drive: click test:{BlockButtons.HitTest()}");
                break;
            case "blockedlist":
                Plugin.Log.LogInfo($"[matchmaking] drive: BLOCKED: {BlockedPanel.Press()}");
                break;
            case "unblockrow":
                Plugin.Log.LogInfo($"[matchmaking] drive: {BlockedPanel.PressFirstRow()}");
                break;
            case "blockedstate":
                Plugin.Log.LogInfo($"[matchmaking] drive: blocked list: {BlockedPanel.Describe()}");
                break;
            case "blockself":
                Plugin.Log.LogInfo($"[matchmaking] drive: own id in this copy's test block file: {BlockList.BlockSelfForTest()}");
                break;
            case "blockclear":
                BlockList.ClearForTest();
                Plugin.Log.LogInfo($"[matchmaking] drive: this copy's test block file emptied ({BlockList.Count} left)");
                break;
            case "rejoin":
                // Loopback only: dial the harness's host again, as a player pressing Join would.
                Plugin.Log.LogInfo($"[matchmaking] drive: rejoin {(Loopback.Redial() ? "dialled" : "not dialled (loopback clients only, and not while connected)")}");
                break;
            case "seat":
                Plugin.Log.LogInfo($"[matchmaking] drive: {SpectateState()}");
                break;
            case "lang":
                // The game's own language for this run, the way its settings switch it, without its save to the prefs.
                var locales = UnityEngine.Localization.Settings.LocalizationSettings.AvailableLocales.Locales;
                var locale = locales[int.Parse(rest, CultureInfo.InvariantCulture)];
                UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale = locale;
                Plugin.Log.LogInfo($"[matchmaking] drive: the game's language set to {locale.LocaleName} for this run");
                break;
            case "kill":
                // Host only: a seat out of the match, by the flag every mode's own death sets. "me" is the host's seat.
                int victim = -1;
                foreach (var ps in UnityEngine.Object.FindObjectsOfType<PlayerStats>())
                {
                    if (ps == null) continue;
                    if (rest == "me" ? ps.isOwned : ps.Slot.ToString(CultureInfo.InvariantCulture) == rest)
                    {
                        ps.NetworkHealth = 0;
                        ps.NetworkDead = true;
                        victim = ps.Slot;
                    }
                }
                Plugin.Log.LogInfo($"[matchmaking] drive: kill {rest}: {(victim >= 0 ? $"seat {victim} is out" : "no such seat here")}");
                break;
            case "speccam":
                // The game's own A and D, done the way it does them: this camera off, the next on, wrapping.
                var specMe = DiceTable.Local;
                var specParent = Manager.Instance != null ? Manager.Instance.SpectatorCameraParrent : null;
                if (specMe == null || specParent == null || !specMe.spectatormode)
                {
                    Plugin.Log.LogInfo($"[matchmaking] drive: speccam: not spectating ({SpectateState()})");
                    break;
                }
                var specT = specParent.transform;
                specT.GetChild(specMe.speccamindex).gameObject.SetActive(false);
                specMe.speccamindex = (specMe.speccamindex + (rest == "prev" ? -1 : 1) + specT.childCount) % specT.childCount;
                specT.GetChild(specMe.speccamindex).gameObject.SetActive(true);
                Plugin.Log.LogInfo($"[matchmaking] drive: speccam: {SpectateState()}");
                break;
            case "twocard":
                Plugin.Log.LogInfo($"[matchmaking] drive: two-card: {TwoCardPoker.Describe()}; {TwoCardLobby.Describe()}");
                break;
            case "twostep":
                // Through the arrow's own Button, as a click would; "pad" steps as a controller's bumper does.
                if (rest == "pad") LobbyController.Instance.ChangeNextDeckOrDiceMode();
                else Plugin.Log.LogInfo($"[matchmaking] drive: two-card arrow {(TwoCardLobby.Press(rest == "left" ? -1 : 1) ? "pressed" : "not up")}");
                Plugin.Log.LogInfo($"[matchmaking] drive: two-card: {TwoCardPoker.Describe()}");
                break;
            case "twohelp":
                Plugin.Log.LogInfo($"[matchmaking] drive: two-card rules page {(TwoCardLobby.PressHelp(rest != "close") ? (rest == "close" ? "closed" : "opened") : "button not up")}");
                break;
            case "uidump":
                DumpUi(rest);
                break;
            case "press":
                PressPath(rest);
                break;
            case "botstate":
                Plugin.Log.LogInfo($"[matchmaking] drive: bots: {BotState()}");
                break;
            case "revealbook":
                // Host only: each bot's dice as a liar call starts, kept here until "reveals" writes them.
                _revealBook = rest != "off";
                if (_revealBook) _reveals.Clear();
                Plugin.Log.LogInfo($"[matchmaking] drive: reveal book {(_revealBook ? "open" : "closed")}");
                break;
            case "botthink":
                // Every bot thinks this long, past its clock, so its last-moment move shows. 0 is off.
                BotBrain.TestThink = float.Parse(rest, CultureInfo.InvariantCulture);
                Plugin.Log.LogInfo($"[matchmaking] drive: bots think {BotBrain.TestThink}s (0 is their own pace)");
                break;
            case "shotwatch":
                // "shotwatch 4": every revolver raised and every seat out is said here, and the first 4 raises are shot. "shotwatch 0" stops it.
                _shotWatch = int.Parse(rest, CultureInfo.InvariantCulture);
                _shotsSeen = 0;
                _raised.Clear();
                _wasOut.Clear();
                Plugin.Log.LogInfo($"[matchmaking] drive: shot watch {(_shotWatch > 0 ? $"on, {_shotWatch} picture(s)" : "off")}");
                break;
            case "botcall":
                // "botcall on": at Liar's Deck and the Chaos deck every bot calls liar on every throw it can; "botcall off".
                BotBrain.TestCallAll = rest == "on";
                Plugin.Log.LogInfo($"[matchmaking] drive: bots call liar {(BotBrain.TestCallAll ? "on every throw they can" : "by their own odds")}");
                break;
            case "botdeal":
                // "botdeal on": at the Devil table every bot makes the Devil's Deal whenever the game lets it; "botdeal off".
                BotBrain.TestDeal = rest == "on";
                Plugin.Log.LogInfo($"[matchmaking] drive: bots make the Devil's Deal {(BotBrain.TestDeal ? "whenever they can" : "by their own odds")}");
                break;
            case "newhost":
                // Host: who the game has told to get ready to take over, by connection, never a name.
                var hostNm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
                var told = new System.Text.StringBuilder();
                if (hostNm != null && hostNm.GamePlayers != null)
                    foreach (var p in hostNm.GamePlayers)
                        if (p != null) told.Append($" connection {p.ConnectionID}{(BotManager.IsBot(p) ? " (bot)" : "")} {(p.isnewHost ? "TOLD" : "not told")};");
                Plugin.Log.LogInfo($"[matchmaking] drive: next host:{told}");
                break;
            case "botbutton":
                // "botbutton add" or "botbutton remove": the lobby's button pressed as a click would; "botbutton" says where they are.
                if (rest == "add" || rest == "remove")
                    Plugin.Log.LogInfo($"[matchmaking] drive: {(rest == "add" ? "ADD BOT" : "REMOVE BOT")} {BotButtons.Press(rest == "add")}");
                Plugin.Log.LogInfo($"[matchmaking] drive: bot buttons: {BotButtons.Describe()}");
                break;
            case "botspoton":
                // "botspoton on": in Liar's Dice every bot calls spot on whenever the game lets it; "botspoton off".
                BotBrain.TestSpotOn = rest == "on";
                Plugin.Log.LogInfo($"[matchmaking] drive: bots call spot on {(BotBrain.TestSpotOn ? "whenever they can" : "by their own odds")}");
                break;
            case "botfail":
                // "botfail move": every bot's move throws; "botfail brain": the brain does not run;
                // "botfail settle": the game cannot be asked whether a call is being settled;
                // "botfail tick": the brain's whole tick throws; "botfail off".
                BotBrain.TestFault = rest switch
                {
                    "move" => BotBrain.Fault.Move,
                    "brain" => BotBrain.Fault.Brain,
                    "settle" => BotBrain.Fault.Settle,
                    "tick" => BotBrain.Fault.Tick,
                    _ => BotBrain.Fault.None,
                };
                Plugin.Log.LogInfo($"[matchmaking] drive: bot fault {BotBrain.TestFault}, guard tripped {BotTurnGuard.Tripped}");
                break;
            case "botlook":
                // "botlook off": at the deck tables no bot raises its cards, so every throw is made without the look; "botlook on".
                BotBrain.TestNoLook = rest == "off";
                Plugin.Log.LogInfo($"[matchmaking] drive: bots {(BotBrain.TestNoLook ? "throw without the look" : "raise their cards to throw")}");
                break;
            case "dicewatch":
                // Any copy: every frame, whether another seat's die is drawn here. "dicewatch on" starts the count, "dicewatch" says it.
                if (rest == "on")
                {
                    _diceWatch = true;
                    _watchFrames = _watchReveals = _litOutside = _litInReveal = _litRolled = _botLitOutside = _personLitOutside = 0;
                    _litSaid = _rowFrames = _awayFrames = 0;
                    _litSeat.Clear();
                    _revealEndedAt = 0f;
                }
                else if (rest == "off") _diceWatch = false;
                Plugin.Log.LogInfo($"[matchmaking] drive: dice watch {(_diceWatch ? "on" : "off")}, {_watchFrames} frame(s) over " +
                                   $"{_watchReveals} reveal(s): another seat's die drawn here outside a reveal in {_litOutside} " +
                                   $"(a bot's in {_botLitOutside}, a person's in {_personLitOutside}), during one in {_litInReveal}, " +
                                   $"put out of sight since the last roll or shake in {_litRolled}; a row of face pictures up in {_rowFrames}, " +
                                   $"a put-away die's picture drawn in {_awayFrames}; {HiddenDice.Describe()}");
                break;
            case "momentshots":
                // Liar's Dice: shots of this copy's screen just after each of the next N calls, and through the drinking.
                // "momentshots 8 loser": and, once this copy is out, through the loser's seat camera.
                // "momentshots 8 late": and later in each reveal too, when the seats after this copy's show.
                string[] ms = rest.Split(' ');
                _momentShots = ms[0] == "off" ? 0 : int.Parse(ms[0], CultureInfo.InvariantCulture);
                _watchLoser = ms.Length > 1 && ms[1] == "loser";
                _lateShots = ms.Length > 1 && ms[1] == "late";
                Plugin.Log.LogInfo($"[matchmaking] drive: shots at the next {_momentShots} call(s)");
                break;
            case "reveals":
                File.WriteAllLines(Under("mmdrive", Role + "-reveals.txt"), _reveals);
                Plugin.Log.LogInfo($"[matchmaking] drive: {_reveals.Count} bot hand(s) from the reveals written beside the commands (not here)");
                break;
            case "prefs":
                Plugin.Log.LogInfo($"[matchmaking] drive: saved LastMode {PlayerPrefs.GetInt("LastMode", -1)}, DeckMode {PlayerPrefs.GetInt("DeckMode", -1)}, " +
                                   $"map {PlayerPrefs.GetInt("CurrentMap", -1)}");
                break;
            case "widepick":
                // "widepick 3440x1440 1920x1080": the one-time pick for that desktop and size, with no full-screen copy.
                Plugin.Log.LogInfo($"[matchmaking] drive: wide: {Ultrawide.WouldPick(rest)}");
                break;
            case "widelist":
                Plugin.Log.LogInfo($"[matchmaking] drive: wide: {Ultrawide.Describe()}");
                break;
            case "widekeep":
                // "widekeep 2580x1080", "widekeep none", "widekeep read": the kept choice in this copy's own file.
                Plugin.Log.LogInfo($"[matchmaking] drive: wide: keep {rest}: {Ultrawide.KeepForTest(rest)}");
                break;
            case "widestep":
                Plugin.Log.LogInfo($"[matchmaking] drive: wide: arrow {(rest == "left" ? "left" : "right")}: {Ultrawide.Step(rest == "left" ? -1 : 1)}");
                break;
            case "wideapply":
                Plugin.Log.LogInfo($"[matchmaking] drive: wide: applied: {Ultrawide.Apply()}");
                break;
            case "widereset":
                Plugin.Log.LogInfo($"[matchmaking] drive: wide: reset as F7 does: {Ultrawide.Reset()}");
                break;
            case "wideart":
                Plugin.Log.LogInfo($"[matchmaking] drive: wide: {UltrawideArt.Describe()}");
                break;
            case "switchto":
                SwitchTo(rest);
                break;
            case "settingspage":
                var pages = UnityEngine.Object.FindObjectOfType<SettingsUiManager>();
                if (pages == null) { Plugin.Log.LogWarning("[matchmaking] drive: no settings pages on screen"); break; }
                pages.OpenButton(int.Parse(rest, CultureInfo.InvariantCulture));
                Plugin.Log.LogInfo($"[matchmaking] drive: settings page {rest} opened");
                break;
            case "menupress":
                // A bare copy presses nothing outside the lobby: on the menu that could be the game's own Host or Find Match.
                if (Plugin.Bare && LobbyController.Instance == null)
                {
                    Plugin.Log.LogWarning($"[matchmaking] drive: 'menupress' refused: a bare copy presses the game's own buttons only in the lobby. {BareRefusal}");
                    break;
                }
                MenuPress(rest);
                break;
            case "menudump":
                MenuDump(rest);
                break;
            case "buttons":
                Buttons(rest);
                break;
            case "rect":
                // "rect Canvas/TexasUI/TabMenu": a part's anchors, pivot, place and size, and where it falls on the screen.
                PartRect(rest);
                break;
            case "partshow":
                // "partshow Canvas/HostMigration on": a part of the game's switched on or off, to look at it. Tests only.
                PartShow(rest);
                break;
            case "animinfo":
                // "animinfo Canvas/TexasUI/TabMenu": the part's animator, its clips and its parameters.
                AnimInfo(rest);
                break;
            case "partshot":
                // "partshot Canvas/TexasUI/VersusMenu/Player1 showdown": one shot a second and a half after that part first
                // comes onto the screen, such as a showdown row sliding in. Watched four times a second.
                int lastSpace = rest.LastIndexOf(' ');
                _partShotPath = lastSpace > 0 ? rest.Substring(0, lastSpace) : null;
                _partShotName = lastSpace > 0 ? rest.Substring(lastSpace + 1) : null;
                _partShotAt = 0f;
                Plugin.Log.LogInfo($"[matchmaking] drive: partshot waits for {_partShotPath ?? "nothing"}");
                break;
            case "animplay":
                // "animplay Canvas/TexasUI/TabMenu On": that state played on the part's own animator, from its start.
                AnimPlay(rest);
                break;
            case "animbool":
                // "animbool Canvas/TexasUI/TabMenu On true": one of the part's animator's own switches, as the game's code sets it.
                AnimBool(rest);
                break;
            case "fair":
                // Fair play tests, all inside the host: "fair <test> ..." (AntiCheat/Fair.cs).
                Fair.Drive(rest);
                break;
            default:
                Plugin.Log.LogWarning($"[matchmaking] drive: no such command '{word}'");
                break;
        }
    }

    // ------------------------------------------------------------ ranked and the table gate

    private static float _nearUntil, _nearNext;
    private static float _emoteFrom = -1f;
    private static CharController _emoteOf;
    private static ulong _ownId;
    private static string _clipWas;

    // ------------------------------------------------------------ batch F: seat cameras

    /// <summary>This copy's seat, whether it is out, and which spectator camera is showing. Seats, never names.</summary>
    /// <summary>
    /// Every seat this copy has a body for: seat, character, bot or not, out or not. In the lobby,
    /// the room's characters instead. No names.
    /// </summary>
    private static string BotState()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{BotManager.Count} in the room, {BotSeats.Seated().Count} seat(s) registered here, works {BotSeats.Works}, " +
                  $"throw events run as a bot's own copy {BotSeats.EventsRun}, Liar's Deck pulls let {BotSeats.PullsLet};");
        if (Manager.Instance == null)
        {
            var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
            if (nm != null && nm.GamePlayers != null)
                foreach (var p in nm.GamePlayers)
                    if (p != null) sb.Append($" [{(BotManager.IsBot(p) ? "bot" : "person")} character {p.PlayerSkin} ready {p.Ready}]");
            return sb.ToString();
        }

        var bodies = UnityEngine.Object.FindObjectsOfType<PlayerStats>();
        var room = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        sb.Append($" {bodies.Count} bodies:");
        foreach (var ps in bodies)
        {
            if (ps == null) continue;
            // The character the lobby gave the seat's player: what the game seats them as. The body's
            // outfit setter is named too, but the two characters without outfits carry another's.
            int chosen = -1;
            if (room != null && room.GamePlayers != null)
                foreach (var lp in room.GamePlayers)
                    if (lp != null && lp.PlayerSteamID == ps.Player_Id) chosen = lp.PlayerSkin;
            var looks = new System.Text.StringBuilder();
            foreach (var s in ps.GetComponentsInChildren<SkinSetter>(true))
                if (s != null) looks.Append(looks.Length > 0 ? "/" : "").Append(s.character.ToString(CultureInfo.InvariantCulture));
            sb.Append($" [seat {ps.Slot} {(BotManager.IsBot(ps) ? "bot" : "person")} character {chosen} (setter {looks}) " +
                      $"{(ps.Dead ? "out" : "in")}{(ps.Winner ? " WON" : "")} net {ps.netId}{Pose(ps)}{DiceShown(ps)}{CardsShown(ps)}]");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Liar's Deck and the Chaos deck: how many cards the seat holds as this copy draws its hand, and
    /// its pose. A count only, never which cards.
    /// </summary>
    private static string CardsShown(PlayerStats ps)
    {
        var deck = ps.GetComponent<DeckGameplay>();
        var chaos = deck == null ? ps.GetComponent<ChaosDeckGameplay>() : null;
        var cards = deck != null ? deck.Cards : chaos != null ? chaos.Cards : null;
        var anim = deck != null ? deck.animator : chaos != null ? chaos.animator : null;
        if (cards == null || anim == null) return "";
        int held = 0;
        foreach (var go in cards) if (go != null && go.activeSelf) held++;
        return $", holds {held} card(s), pose HaveCard {anim.GetBool("HaveCard")} Look {anim.GetBool("Look")} Dead {anim.GetBool("Dead")}" +
               (chaos != null ? $" TakeAim {anim.GetBool("TakeAim")}" : "");
    }

    /// <summary>Liar's Dice: the seat's animator as this copy runs it, the values the owner's copy sets.</summary>
    private static string Pose(PlayerStats ps)
    {
        var cc = ps.GetComponent<DiceGamePlay>();
        var anim = cc != null ? cc.animator : null;
        if (anim == null) return "";
        var state = anim.GetCurrentAnimatorStateInfo(0);
        return $", pose Dead {anim.GetBool("Dead")} Drink {anim.GetBool("Drink")} Show {anim.GetBool("Show")} " +
               $"in Die {state.IsName("Die")}";
    }

    /// <summary>
    /// On the host, for a bot's seat: how many of its dice this copy draws, and whether the faces it
    /// draws are the dice the bot holds now, which only the game's reveal may show. Counts only.
    /// </summary>
    private static string DiceShown(PlayerStats ps)
    {
        // Every seat but the host's own: a person's seat is the game as it ships, for comparison.
        if (!NetworkServer.active || ps.isOwned) return "";
        var gp = ps.GetComponent<DiceGamePlay>();
        var held = gp != null ? gp.DiceValues : null;
        var drawn = gp != null ? gp.dicerenders : null;
        if (held == null || drawn == null) return "";

        int lit = 0, same = 0;
        for (int i = 0; i < drawn.Count; i++)
        {
            var die = drawn[i];
            if (die == null) continue;
            bool on = false;
            if (die.gameObject.activeInHierarchy)
                foreach (var r in die.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r.enabled) { on = true; break; }
            if (on) lit++;
            if (i < held.Count && die.Face == held[i]) same++;
        }
        bool reveal = Manager.Instance.DiceGame != null && (Manager.Instance.DiceGame.CalledLiar || Manager.Instance.DiceGame.CalledSpotOn);
        return $", dice drawn here {lit} of {drawn.Count}, faces as held {same} of {held.Count}{(reveal ? " (reveal)" : "")}";
    }

    private static bool _diceWatch, _watchCalled;
    private static int _watchFrames, _watchReveals, _litOutside, _litInReveal, _litRolled, _botLitOutside, _personLitOutside;
    private static readonly List<DiceGamePlay> _watchSeats = new();
    private static readonly List<Dice> _awayDice = new();
    private static readonly HashSet<int> _litSeat = new();
    private static int _watchSeatsAt, _litSaid, _rowFrames, _awayFrames;
    private static bool _watchCalled2;
    private static float _revealEndedAt;

    /// <summary>
    /// Test only, on any copy, every frame from <see cref="ModTicker"/>'s LateUpdate, the last moment
    /// before the frame is drawn: whether any die of another seat still in the match is drawn here,
    /// outside a reveal and during one, split by bot and person, and whether one is drawn that
    /// <see cref="HiddenDice"/> has put out of sight since the last roll or shake, which must never
    /// be. Counts only. A die counts as drawn when it is switched on, whether or not its cup covers
    /// it, since a tipped cup shows what is under it. At the Traditional table a reveal's faces stay
    /// drawn for a moment after it, until the next roll: those are faces everyone has just seen, and
    /// only the last count says whether a new roll showed.
    /// </summary>
    internal static void LateFrame()
    {
        if (!_diceWatch) return;
        try
        {
            var m = Manager.Instance;
            var d = m != null ? m.DiceGame : null;
            if (d == null || !m.GameStarted) { _watchSeats.Clear(); return; }

            bool called = d.CalledLiar || d.CalledSpotOn;
            if (called && !_watchCalled) _watchReveals++;
            _watchCalled = called;
            _watchFrames++;

            if (!called && _watchCalled2) _revealEndedAt = Time.time;
            _watchCalled2 = called;

            // The dice seats in the scene, found again once a second: the manager's roster is the
            // server's, and a client's is empty. With them, each seat's dice that are not in its cup.
            if (_watchSeats.Count == 0 || Time.frameCount >= _watchSeatsAt)
            {
                _watchSeats.Clear();
                _awayDice.Clear();
                foreach (var h in UnityEngine.Object.FindObjectsOfType<DiceGamePlay>())
                {
                    if (h == null) continue;
                    _watchSeats.Add(h);
                    var cup = h.dicerenders;
                    foreach (var die in h.GetComponentsInChildren<Dice>(true))
                    {
                        bool inCup = false;
                        for (int i = 0; cup != null && i < cup.Count && !inCup; i++)
                            if (cup[i] != null && cup[i].Pointer == die.Pointer) inCup = true;
                        if (!inCup) _awayDice.Add(die);
                    }
                }
                _watchSeatsAt = Time.frameCount + 60;
            }

            bool anyLit = false, rolledLit = false, botLit = false, personLit = false, rowUp = false, awayUp = false;
            foreach (var hand in _watchSeats)
            {
                var ps = hand != null ? hand.GetComponent<PlayerStats>() : null;
                if (ps == null) continue;
                var drawn = hand.dicerenders;
                if (drawn == null) continue;

                // The row of face pictures over a seat as it shows, any seat's own included.
                for (int i = 0; i < drawn.Count && !rowUp; i++)
                    if (drawn[i] != null && (Drawn(drawn[i].dice) || Drawn(drawn[i].dice2))) rowUp = true;

                if (ps.isOwned || ps.Dead) continue;
                bool lit = false;
                for (int i = 0; i < drawn.Count && !lit; i++)
                {
                    var die = drawn[i];
                    if (die == null) continue;
                    if (die.gameObject.activeInHierarchy)
                        foreach (var r in die.GetComponentsInChildren<Renderer>(true))
                            if (r != null && r.enabled) { lit = true; break; }
                    if (!lit) lit = Drawn(die.dice) || Drawn(die.dice2);
                }
                bool wasLit = _litSeat.Contains(ps.Slot);
                if (lit) _litSeat.Add(ps.Slot); else _litSeat.Remove(ps.Slot);
                if (!lit) continue;
                anyLit = true;
                if (!called)
                {
                    if (BotManager.IsBot(ps)) botLit = true; else personLit = true;
                    // The first few moments another seat's dice come into sight outside a reveal, with when.
                    if (!wasLit && _litSaid < 12)
                    {
                        _litSaid++;
                        Plugin.Log.LogInfo($"[matchmaking] drive: dice watch: seat {ps.Slot} ({(BotManager.IsBot(ps) ? "bot" : "person")}) drawn outside a reveal, " +
                                           $"{(_revealEndedAt > 0f ? $"{Time.time - _revealEndedAt:0.0} s after a reveal ended" : "no reveal yet")}, " +
                                           $"{(HiddenDice.LastHideAt > 0f ? $"{Time.time - HiddenDice.LastHideAt:0.0} s after the last hiding" : "nothing hidden yet")}, " +
                                           $"drinking {d.DrinkPhaseActive}, hidden {HiddenDice.IsHidden(hand)}");
                    }
                }
                // Put out of sight at the last roll or shake, and no reveal on: the reveal clears it.
                if (HiddenDice.IsHidden(hand) && !called) rolledLit = true;
            }
            foreach (var die in _awayDice)
                if (die != null && (Drawn(die.dice) || Drawn(die.dice2))) { awayUp = true; break; }

            if (anyLit) { if (called) _litInReveal++; else _litOutside++; }
            if (botLit) _botLitOutside++;
            if (personLit) _personLitOutside++;
            if (rolledLit) _litRolled++;
            if (rowUp) _rowFrames++;
            if (awayUp) _awayFrames++;
        }
        catch (Exception e)
        {
            // Every frame would say it again: once, and the watch stops.
            _diceWatch = false;
            Plugin.Log.LogError($"[matchmaking] drive: dice watch stopped: {e.Message}");
        }
    }

    private static bool Drawn(Renderer r) => r != null && r.enabled && r.gameObject.activeInHierarchy;

    /// <summary>
    /// Each seat's dice as this copy has them: how many it holds and has in its cup, then every die
    /// the seat carries, in its cup or put away, with whether it and its two face pictures are drawn.
    /// The pictures' faces only while a reveal shows them to everybody; seats and counts otherwise.
    /// </summary>
    private static string DiceRows()
    {
        var m = Manager.Instance;
        var d = m != null ? m.DiceGame : null;
        if (d == null) return "not at a table";
        bool reveal = d.CalledLiar || d.CalledSpotOn;

        var sb = new System.Text.StringBuilder(reveal ? "(reveal)" : "(no reveal)");
        foreach (var hand in UnityEngine.Object.FindObjectsOfType<DiceGamePlay>())
        {
            var ps = hand != null ? hand.GetComponent<PlayerStats>() : null;
            if (ps == null) continue;
            var cup = hand.dicerenders;
            sb.Append($" [seat {ps.Slot}{(ps.isOwned ? " mine" : "")}{(ps.Dead ? " out" : "")} holds {(hand.DiceValues != null ? hand.DiceValues.Count : -1)}, " +
                      $"cup {(cup != null ? cup.Count : -1)}:");
            foreach (var die in hand.GetComponentsInChildren<Dice>(true))
            {
                if (die == null) continue;
                bool inCup = false;
                for (int i = 0; cup != null && i < cup.Count && !inCup; i++)
                    if (cup[i] != null && cup[i].Pointer == die.Pointer) inCup = true;
                sb.Append($" {(inCup ? "in" : "away")}/{(die.gameObject.activeInHierarchy ? "on" : "off")}");
                sb.Append(Picture(die, die.dice, reveal)).Append(Picture(die, die.dice2, reveal));
            }
            sb.Append(']');
        }
        return sb.ToString();
    }

    private static string Picture(Dice die, SpriteRenderer r, bool reveal)
    {
        if (r == null) return " -";
        string where = r.transform.IsChildOf(die.transform) ? "under" : "apart";
        string face = reveal && r.sprite != null ? $" {r.sprite.name}" : "";
        return $" ({where} {(Drawn(r) ? "drawn" : "not drawn")}{face})";
    }

    private static bool _revealBook, _wasCalled;
    private static readonly List<string> _reveals = new();

    /// <summary>
    /// Test only, on the host: each bot's dice at the moment a liar or spot-on call starts the reveal
    /// that shows them to everybody, kept in memory. Written out only by "reveals", into the driver's
    /// own folder, so the logs can be searched for them afterwards. Nothing is written to a log.
    /// </summary>
    private static void WatchReveals()
    {
        if (!_revealBook || !NetworkServer.active) return;
        var d = Manager.Instance != null ? Manager.Instance.DiceGame : null;
        bool called = d != null && (d.CalledLiar || d.CalledSpotOn);
        if (called && !_wasCalled)
            foreach (var seat in BotSeats.Seated())
            {
                var own = seat.Dice != null ? seat.Dice.DiceValues : null;
                if (own == null || seat.Player.Dead) continue;
                var v = new List<string>();
                foreach (int x in own) v.Add(x.ToString(CultureInfo.InvariantCulture));
                _reveals.Add($"{DateTime.Now:HH:mm:ss.fff} seat {seat.Player.Slot}: {string.Join(" ", v)}");
            }
        _wasCalled = called;
    }

    private static int _momentShots, _moment;
    private static bool _wasCalledHere, _wasDrinking, _watchLoser, _lateShots;
    private static readonly float[] RevealShots = { 2.5f, 4.5f, 6.5f };
    private static readonly float[] LateRevealShots = { 2.5f, 4.5f, 6.5f, 8.5f, 10.5f, 12.5f };

    /// <summary>
    /// On a copy that is out: switch to the seat camera of the seat whose turn the drink gave it (the
    /// game hands the turn to the loser as the drink starts), the way A and D switch. The seat, for
    /// the shot's name, or nothing.
    /// </summary>
    private static string LookAtLoser()
    {
        var me = DiceTable.Local;
        var m = Manager.Instance;
        var parent = m != null ? m.SpectatorCameraParrent : null;
        if (me == null || parent == null || !me.spectatormode) return "";

        int loser = m.ActivePlayerSlot;
        var t = parent.transform;
        for (int i = 0; i < t.childCount; i++)
        {
            if (t.GetChild(i).name != SeatCams.Prefix + loser) continue;
            t.GetChild(me.speccamindex).gameObject.SetActive(false);
            me.speccamindex = i;
            t.GetChild(i).gameObject.SetActive(true);
            return $"-seat{loser}";
        }
        return "";
    }
    private static readonly List<(float At, string Name)> _shotQueue = new();

    /// <summary>
    /// Shots at the moments of a Liar's Dice round that are over in seconds: the reveal after a
    /// call, and the drink that follows it. Read from what the host syncs to every copy.
    /// </summary>
    private static int _rowsAtReveals;
    private static bool _rowCalled;
    private static float _rowAt = -1f;

    /// <summary>"dicerowat N": the dice rows written out four and a half seconds into each of the next N reveals.</summary>
    private static void WatchRows()
    {
        if (_rowsAtReveals <= 0 && _rowAt < 0f) return;
        var d = Manager.Instance != null ? Manager.Instance.DiceGame : null;
        if (d == null) return;
        bool called = d.CalledLiar || d.CalledSpotOn;
        float now = Time.unscaledTime;
        if (called && !_rowCalled && _rowsAtReveals > 0)
        {
            _rowsAtReveals--;
            _rowAt = now + 4.5f;
        }
        _rowCalled = called;
        if (_rowAt >= 0f && now >= _rowAt)
        {
            _rowAt = -1f;
            Plugin.Log.LogInfo($"[matchmaking] drive: dice rows: {DiceRows()}");
        }
    }

    private static void WatchMoments()
    {
        WatchRows();
        if (_momentShots <= 0 && _shotQueue.Count == 0) return;
        float now = Time.unscaledTime;

        var d = Manager.Instance != null ? Manager.Instance.DiceGame : null;
        if (d != null)
        {
            bool called = d.CalledLiar || d.CalledSpotOn;
            bool drinking = d.DrinkPhaseActive;
            if (called && !_wasCalledHere && _momentShots > 0)
            {
                _momentShots--;
                _moment++;
                Plugin.Log.LogInfo($"[matchmaking] drive: moment {_moment}: {(d.CalledSpotOn ? "SPOT ON" : "LIAR")} called, seen here");
                foreach (float t in _lateShots ? LateRevealShots : RevealShots)
                    _shotQueue.Add((now + t, $"reveal{_moment}-{t.ToString("0.0", CultureInfo.InvariantCulture)}"));
            }
            if (drinking && !_wasDrinking && _moment > 0)
            {
                // A copy that is out looks through the loser's own seat camera, the way A and D get there.
                string at = _watchLoser ? LookAtLoser() : "";
                foreach (float t in new[] { 0.25f, 1f, 2f, 3f, 4.5f })
                    _shotQueue.Add((now + t, $"drink{_moment}{at}-{t.ToString("0.0", CultureInfo.InvariantCulture)}"));
            }
            _wasCalledHere = called;
            _wasDrinking = drinking;
        }

        for (int i = _shotQueue.Count - 1; i >= 0; i--)
        {
            if (now < _shotQueue[i].At) continue;
            ScreenCapture.CaptureScreenshot(Under("mmshots", $"{Role}-{_shotQueue[i].Name}.png"));
            _shotQueue.RemoveAt(i);
        }
    }

    private static int _shotWatch, _shotsSeen;
    private static readonly Dictionary<int, bool> _raised = new(), _wasOut = new();

    /// <summary>
    /// Liar's Deck and the Chaos deck, on any copy: each seat raising its revolver and putting it down
    /// again, and going out, as this copy's own animator for that seat shows it, said with the seat
    /// and whether it is a bot. The first few raises are also shot a second and a half later, so a
    /// client's picture of a bot's trigger pull can be looked at. Seats and counts only.
    /// </summary>
    private static void WatchShots()
    {
        if (_shotWatch <= 0) return;
        var m = Manager.Instance;
        if (m == null || !m.GameStarted) { _raised.Clear(); _wasOut.Clear(); return; }

        // The seats in the scene: the manager's roster is the server's, and a client's is empty.
        foreach (var ps in UnityEngine.Object.FindObjectsOfType<PlayerStats>())
        {
            if (ps == null) continue;
            CharController body = ps.GetComponent<DeckGameplay>();
            if (body == null) body = ps.GetComponent<ChaosDeckGameplay>();
            var anim = body != null ? body.animator : null;
            if (anim == null) continue;

            bool raised = anim.GetBool("Roulet");
            _raised.TryGetValue(ps.Slot, out bool was);
            if (raised != was)
            {
                _raised[ps.Slot] = raised;
                string who = $"seat {ps.Slot} ({(BotManager.IsBot(ps) ? "bot" : ps.isOwned ? "this copy" : "person")})";
                Plugin.Log.LogInfo($"[matchmaking] drive: shots: {who} {(raised ? "raises its revolver" : "lowers it")}");
                if (raised && _shotsSeen < _shotWatch)
                {
                    _shotsSeen++;
                    _shotQueue.Add((Time.unscaledTime + 1.5f, $"pull{_shotsSeen}-seat{ps.Slot}"));
                }
            }
            _wasOut.TryGetValue(ps.Slot, out bool out0);
            if (ps.Dead && !out0)
                Plugin.Log.LogInfo($"[matchmaking] drive: shots: seat {ps.Slot} ({(BotManager.IsBot(ps) ? "bot" : "person")}) is out here");
            _wasOut[ps.Slot] = ps.Dead;
        }
    }

    private static string SpectateState()
    {
        var me = DiceTable.Local;
        if (me == null || me.playerStats == null) return "no seat here";
        var parent = Manager.Instance != null ? Manager.Instance.SpectatorCameraParrent : null;
        string cams = "no spectator cameras";
        if (parent != null)
        {
            var t = parent.transform;
            int at = me.speccamindex;
            int live = 0;
            for (int i = 0; i < t.childCount; i++)
                if (t.GetChild(i).gameObject.activeSelf) live++;
            string name = at >= 0 && at < t.childCount ? t.GetChild(at).name : "?";
            cams = $"camera {at + 1} of {t.childCount} '{name}', {live} switched on, seat cameras added {SeatCams.Added}";
        }
        return $"seat {me.playerStats.Slot}, out {me.playerStats.Dead}, spectating {me.spectatormode}, {cams}, modded {Table.Modded}" +
               (TexasSwap.Live ? $", Texas hides others' cards {TexasSwap.Hiding}" : "");
    }

    // ------------------------------------------------------------ batch E: house rules

    /// <summary>What each seat shows of the rules, on this copy: the clock, cards, chambers, dice, bullets, points. Seats, never names.</summary>
    private static string RuleSeen()
    {
        var m = Manager.Instance;
        if (m == null) return "no match";

        // The manager's list of seats is the host's; a client finds the seats in the scene.
        var seats = new List<PlayerStats>();
        if (m.Players != null) foreach (var p in m.Players) if (p != null) seats.Add(p);
        if (seats.Count == 0) foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerStats>()) if (p != null) seats.Add(p);
        seats.Sort((a, b) => a.Slot.CompareTo(b.Slot));

        var sb = new System.Text.StringBuilder($"clock {m.CountDown:0.0}, rules {HouseRules.Names[HouseRules.Live]};");
        foreach (var p in seats)
        {
            if (p == null) continue;
            sb.Append($" seat {p.Slot}{(p.isOwned ? " (this copy)" : "")}:");

            var deck = p.GetComponent<DeckGameplay>();
            if (deck != null)
            {
                int cards = 0;
                if (deck.Cards != null) foreach (var c in deck.Cards) if (c != null && c.activeInHierarchy) cards++;
                sb.Append($" cards {cards}, says '{(deck.RoundText != null ? deck.RoundText.text : "")}'");
                if (NetworkServer.active) sb.Append(Fair.HideHands ? $", fired {deck.CurrentRevolver}" : $", bullet in {deck.NetworkrevolverBullet + 1}, fired {deck.CurrentRevolver}");
            }

            var chaos = p.GetComponent<ChaosDeckGameplay>();
            if (chaos != null)
            {
                sb.Append($" says '{(chaos.RoundText != null ? chaos.RoundText.text : "")}'");
                if (NetworkServer.active) sb.Append(Fair.HideHands ? $", fired {chaos.Networkcurrentrevoler}" : $", bullet in {chaos.Networkrevolverbulllet + 1}, fired {chaos.Networkcurrentrevoler}");
            }

            var dice = p.GetComponent<DiceGamePlay>();
            if (dice != null) sb.Append($" dice {dice.TotalDices}, drinks {dice.drinkCount}, lives {p.Health}");

            var texas = p.GetComponent<TexasGamePlay>();
            if (texas != null) sb.Append($" bullets {texas.Bullets}, coward folds {texas.CowardFoldCount}");

            if (p.GetComponent<SpinGamePlay>() != null) sb.Append($" points {p.spinPoint}");
            sb.Append(p.Dead ? ", dead;" : ";");
        }

        var dm = UnityEngine.Object.FindObjectOfType<DiceGamePlayManager>();
        if (dm != null) sb.Append($" highest bid {dm.MaxCount}");
        return sb.ToString();
    }

    // ------------------------------------------------------------ batch E: block list

    /// <summary>
    /// Host: the players on two podiums swap Steam ids, as a host on a build before the id fix
    /// leaves them when two people arrive out of order. Podiums count from 1. The answer has
    /// podium numbers only, never a name or an id.
    /// </summary>
    private static string SwapIds(string rest)
    {
        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        var room = LobbyController.Instance;
        if (!NetworkServer.active || nm == null || nm.GamePlayers == null || room == null || room.SpawnSlots == null)
            return "swapids: the host, in a lobby, only";

        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 2 ||
            !int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out int a) ||
            !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out int b) ||
            a < 1 || b < 1 || a == b || a > room.SpawnSlots.Count || b > room.SpawnSlots.Count)
            return "swapids: give two different podiums";

        PlayerObjectController first = null, second = null;
        foreach (var p in nm.GamePlayers)
        {
            if (p == null) continue;
            if (p.SlotName == room.SpawnSlots[a - 1].gameObject.name) first = p;
            if (p.SlotName == room.SpawnSlots[b - 1].gameObject.name) second = p;
        }
        if (first == null || second == null) return "swapids: somebody must stand on both";

        ulong was = first.PlayerSteamID;
        first.NetworkPlayerSteamID = second.PlayerSteamID;
        second.NetworkPlayerSteamID = was;
        return $"podiums {a} and {b} now carry each other's Steam id";
    }

    // ------------------------------------------------------------ batch C: bug report, wardrobe

    /// <summary>
    /// Made-up lines through the bug report's gate, each with what must not come out of it. Every
    /// name, id, path and token here is invented. A failure logs what came out, which is made up too.
    /// </summary>
    private static void ScrubTest()
    {
        const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJmYWtlIn0.ZmFrZXNpZ25hdHVyZXRleHQ";
        var names = new LogScrub.Names();
        foreach (string n in new[] { "O'Neil", "[update]", "12345678", "76561190000000099", "\u0418\u0432\u0430\u043d",
                                     "\u5f20\u4f1f", "ExampleLongPlayerName_X", "Zed" })
            names.Add(n);

        var M = BepInEx.Logging.LogLevel.Message;
        var I = BepInEx.Logging.LogLevel.Info;
        var E = BepInEx.Logging.LogLevel.Error;
        const string Out = "(dropped)";
        const string Id28 = "AbCdEfGhIjKlMnOpQrStUvWxYz12";
        string longLine = "[census] " + string.Concat(System.Linq.Enumerable.Repeat("abc ", 75));

        // name, line, level, the mod's own, source, what must not be in the result, what must be (or Out)
        var cases = new (string, string, BepInEx.Logging.LogLevel, bool, string, string[], string)[]
        {
            ("apostrophe", "[dealcards] 'O'Neil' handed 5 cards (5 card objects)", I, true, null, new[] { "O'Neil", "Neil" }, "[dealcards] "),
            ("unquoted name", "[aim] Zed aims at seat 5 - aim 2 of a 8 seat ring - and fires", I, true, null, new[] { "Zed" }, "Player"),
            ("name like a tag", "[version] [update] is not on 2.0.0", I, true, null, new[] { "[update]" }, "[version] "),
            ("digits name", "[tablefill] 12345678 has nowhere to sit", I, true, null, new[] { "12345678" }, "[tablefill] "),
            ("17 digit name", "[guard]   player : 76561190000000099 joined", I, true, null, new[] { "76561190000000099" }, "[guard] "),
            ("cyrillic", "[aim] \u0418\u0432\u0430\u043d \u041f\u0435\u0442\u0440\u043e\u0432 aims at seat 2", I, true, null, new[] { "\u0418" }, "(line left out)"),
            ("known cjk name", "[aim] \u5f20\u4f1f aims at seat 2", I, true, null, new[] { "\u5f20" }, "[aim] Player"),
            ("unknown cjk name", "[aim] \u674e\u5a1c aims at seat 2", I, true, null, new[] { "\u674e" }, "(line left out)"),
            ("emoji", "[aim] Kay\U0001F600 aims at seat 2", I, true, null, new[] { "Kay" }, "(line left out)"),
            ("zero width", "[aim] K\u200Bay aims at seat 2", I, true, null, new[] { "ay aims" }, "(line left out)"),
            ("right to left", "[aim] \u202Edeppilf aims at seat 2", I, true, null, new[] { "deppilf" }, "(line left out)"),
            ("newline", "[aim] seat 3 fires\nSomebody Else was here", I, true, null, new[] { "Somebody" }, "[aim] seat 3 fires"),
            ("300 characters", longLine, I, true, null, new[] { longLine.Substring(0, LogScrub.Widest + 1) }, "..."),
            ("name cut to 16", "[seatring] ExampleLongPlaye holds slot 9", I, true, null, new[] { "ExampleLong" }, "[seatring] "),
            ("path with a space", @"[update] could not write C:\Users\Example User\AppData\Local\Temp\x.zip", I, true, null, new[] { "Example", "Users" }, "<path>"),
            ("proton path", "IOException: Sharing violation on path Z:/home/deck/.local/share/Steam/x", E, false, "Unity", new[] { "deck" }, "<path>"),
            ("removable card", "[update] cannot read /run/media/deck/EXAMPLECARD/steamapps/x", I, true, null, new[] { "EXAMPLECARD", "deck" }, "<path>"),
            ("other drive", @"[native] D:\Users\ExampleUser\SteamLibrary\x.dll", I, true, null, new[] { "ExampleUser" }, "<path>"),
            ("network path", @"[update] \\FAKESERVER\share\x", I, true, null, new[] { "FAKESERVER" }, "<path>"),
            ("token", "[modnet] header " + Jwt, I, true, null, new[] { "eyJ", "ZmFr" }, "<token>"),
            ("28 character id", $"[table] id {Id28} seen", I, true, null, new[] { "AbCdEf" }, "<token>"),
            ("lobby id in an exception", "InvalidOperationException: lobby 109775240000000123 is gone", E, false, "Unity", new[] { "109775240000000123" }, "<id>"),
            ("address", "[join] 192.168.1.20 connected", I, true, null, new[] { "192.168" }, "<ip>"),
            ("email", "[update] mail fake.person@example.com", I, true, null, new[] { "fake.person" }, "<email>"),
            ("web address", "[update] got https://example.com/u/fakeuser?t=abc", I, true, null, new[] { "fakeuser" }, "<url>"),
            ("sign-in from the game", "Access Token: " + Jwt, M, false, "Unity", new[] { "eyJ" }, Out),
            ("sign-in as an error", "Access Token: " + Jwt, E, false, "Unity", new[] { "eyJ" }, Out),
            ("online services exception", "Unity.Services.Core.ServicesInitializationException: failed for AbC123", E, false, "Unity", new[] { "AbC123" }, Out),
            ("untagged", "some words with Zed in them", I, true, null, new[] { "Zed" }, Out),
            ("developer line", "[dev:bot] Zed has the turn", I, true, null, new[] { "Zed" }, Out),
            ("self test line", "[matchmaking] selftest: hosting 1 name='Zed's lobby'", I, true, null, new[] { "Zed" }, Out),
            ("harness line", "[loopback] connection 1 is now 'Zed'", I, true, null, new[] { "Zed" }, Out),
            ("launch options", "[update] restarting the game (arguments: -fakeoption)", I, true, null, new[] { "fakeoption" }, Out),
            ("another mod's line", "[aim] Zed aims", I, false, "OtherMod", new[] { "Zed" }, Out),
            ("patched line", "  patched: player caps", I, true, null, new string[0], "  patched: player caps"),
        };

        int pass = 0;
        foreach (var (name, line, level, mine, source, never, want) in cases)
        {
            string got = LogScrub.Line(line, level, mine, source, names);
            bool ok = want == Out ? got == null : got != null && got.Contains(want);
            if (got != null) foreach (string bad in never) if (got.Contains(bad)) ok = false;
            if (ok) { pass++; continue; }
            Plugin.Log.LogWarning($"[bugreport] scrubtest FAIL '{name}': got '{got ?? "(dropped)"}'");
        }

        // The per-copy log keeps every line, so only the sign-in itself is taken out of it.
        var secrets = new (string, string, string)[]
        {
            ("token line", "Access Token: " + Jwt, "eyJ"),
            ("player id line", "PlayerID: " + Id28, "AbCdEf"),
            ("token in a sentence", "signed in with " + Jwt + " just now", "eyJ"),
        };
        foreach (var (name, line, bad) in secrets)
        {
            string got = LogScrub.Secrets(line);
            if (!got.Contains(bad)) { pass++; continue; }
            Plugin.Log.LogWarning($"[bugreport] scrubtest FAIL '{name}': the per-copy log kept it");
        }

        // Ordinary lines must reach the per-copy log exactly as they were: the test tools read
        // them. The developer lines among them carry seat, connection and player numbers.
        var plain = new List<string>
        {
            "[aim] seat 3 aims at seat 5 - aim 2 of a 8 seat ring - and fires",
            "[dev:join] registered player #3 conn=2 seat=2 podium=- ready=False",
            "[modnet] connection 2 has the channel, level 1, v2.0.0",
            "[awards] sent 3: BestLiar seat 2 = 4, LieDetector seat 0 = 2, IronNerves seat 1 = 3",
        };
        foreach (var lobbyPlayer in Dev.LobbyPlayers()) plain.Add($"[dev:join] registered {Dev.Describe(lobbyPlayer)}");
        int plainPass = 0;
        foreach (string line in plain)
        {
            if (LogScrub.Secrets(line) == line) { plainPass++; continue; }
            Plugin.Log.LogWarning($"[bugreport] scrubtest FAIL 'plain line': the per-copy log changed '{LogScrub.Secrets(line)}'");
        }
        if (plainPass == plain.Count) pass++;

        int all = cases.Length + secrets.Length + 1;
        Plugin.Log.LogInfo($"[bugreport] scrubtest: {pass} of {all} cases pass{(pass == all ? "" : " - FAILED")}");
    }

    // ------------------------------------------------------------ batch D: the win screen, the picker's race, wider clone check

    private static string _endShot;
    private static float _endSeenAt;

    /// <summary>
    /// A shot a few seconds into the win screen, whenever the match ends. The end comes on its own
    /// and the screen lasts seconds, so nothing sent from outside could time it.
    /// </summary>
    private static void WatchEnd()
    {
        if (_endShot == null) return;
        var m = Manager.Instance;
        if (m == null || !m.GameEnd) { _endSeenAt = 0f; return; }

        if (_endSeenAt <= 0f) { _endSeenAt = Time.unscaledTime; return; }
        if (Time.unscaledTime - _endSeenAt < 4f) return;

        Plugin.Log.LogInfo($"[matchmaking] drive: win screen shot {_endShot}, lobby in {m.CountDownLobby:0.0} s, {AwardPanel.Describe()}");
        ScreenCapture.CaptureScreenshot(Under("mmshots", $"{Role}-{_endShot}.png"));
        _endShot = null;
        _endSeenAt = 0f;
    }

    private static string _aimShot;
    private static float _aimSeenAt;

    /// <summary>
    /// A shot of the aim line a moment after this copy's own seat starts choosing a target. The
    /// choice comes when the cards bring it and lasts seconds. The line itself names a player, so
    /// only its language is logged.
    /// </summary>
    private static void WatchAim()
    {
        if (_aimShot == null) return;
        if (!AimHud.Showing) { _aimSeenAt = 0f; return; }

        if (_aimSeenAt <= 0f) { _aimSeenAt = Time.unscaledTime; return; }
        if (Time.unscaledTime - _aimSeenAt < 0.8f) return;

        Plugin.Log.LogInfo($"[matchmaking] drive: aim line shot {_aimShot}, in {Words.Now}");
        ScreenCapture.CaptureScreenshot(Under("mmshots", $"{Role}-{_aimShot}.png"));
        _aimShot = null;
        _aimSeenAt = 0f;
    }

    /// <summary>
    /// Ask for the next character and, in the same frame, try to change the outfit: the moment the
    /// picker used to dress the character being left. Both must refuse until the host answers.
    /// </summary>
    private static void SkinRace()
    {
        var room = LobbyController.Instance;
        var me = room != null ? room.LocalPlayerController : null;
        if (me == null) { Plugin.Log.LogWarning("[matchmaking] drive: no lobby player of our own"); return; }

        int before = me.PlayerSkin;
        me.SetSkin();
        bool stepped = LobbyWardrobe.Step(1);
        Plugin.Log.LogInfo($"[matchmaking] drive: asked to leave character {before}; the picker in the same frame changed an outfit: " +
                           $"{stepped} (must be False), picker still up: {LobbyWardrobe.Up}");
    }

    /// <summary>
    /// A control that is not a button or a toggle (a slider, an input box, a scroll bar): a copy of
    /// one holding saved calls must be refused. The first such control in the scene is tried.
    /// </summary>
    private static void CloneTest()
    {
        int seen = 0;
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.UI.Selectable>()))
        {
            var control = found.TryCast<UnityEngine.UI.Selectable>();
            if (control == null || control.TryCast<UnityEngine.UI.Button>() != null || control.TryCast<UnityEngine.UI.Toggle>() != null) continue;
            if (!control.gameObject.scene.IsValid() || control.transform.parent == null) continue;
            seen++;

            int saved = UiClone.Saved(control.gameObject);
            if (saved == 0) continue;
            var copy = UiClone.Make(control.gameObject, control.transform.parent, "LB8P_CloneTest");
            Plugin.Log.LogInfo($"[matchmaking] drive: clone test on a {control.GetIl2CppType().Name} with {saved} saved call(s): " +
                               $"{(copy == null ? "refused" : "COPIED")} (must be refused)");
            if (copy != null) UnityEngine.Object.Destroy(copy);
            return;
        }
        Plugin.Log.LogInfo($"[matchmaking] drive: clone test: {seen} other control(s) in the scene, none holding saved calls");
    }

    private static void PressReport()
    {
        var button = BugReport.SettingsButton.Made;
        if (button == null) { Plugin.Log.LogWarning("[matchmaking] drive: no report button made yet"); return; }

        Plugin.Log.LogInfo($"[matchmaking] drive: pressing the report button (on screen: {button.gameObject.activeInHierarchy})");
        button.onClick.Invoke();
    }

    /// <summary>The report button: where it is, what it says, and that nothing saved came along with the copy.</summary>
    private static void LogReportButton()
    {
        var button = BugReport.SettingsButton.Made;
        if (button == null) { Plugin.Log.LogWarning("[matchmaking] drive: no report button made yet"); return; }

        var box = button.GetComponent<RectTransform>();
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        box.GetWorldCorners(corners);
        var text = button.GetComponent<TMPro.TextMeshProUGUI>();
        Plugin.Log.LogInfo($"[matchmaking] drive: report button on screen: {button.gameObject.activeInHierarchy}, " +
                           $"x {corners[0].x:0}..{corners[2].x:0} y {corners[0].y:0}..{corners[2].y:0} of {Screen.width}x{Screen.height}, " +
                           $"says '{(text != null ? text.text : "?")}' at {(text != null ? text.fontSize : 0f):0.0}, " +
                           $"saved calls left {UiClone.Saved(button.gameObject)}");

        foreach (var part in button.GetComponents<Component>())
            Plugin.Log.LogInfo($"[matchmaking] drive: report button has <{part.GetIl2CppType().Name}>");
    }

    /// <summary>
    /// Opens the settings the way their own button would: through the visible switcher that leads
    /// to a settings screen. In a match, <c>pause</c> first. Every switcher on screen is named, to
    /// find the way when it is not the expected one.
    /// </summary>
    private static void OpenSettings()
    {
        StateSwitcher pick = null;
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<StateSwitcher>()))
        {
            var sw = found.TryCast<StateSwitcher>();
            if (sw == null || !sw.gameObject.activeInHierarchy) continue;

            string to = sw.ChangeCanvasTo != null ? sw.ChangeCanvasTo.name : "";
            Plugin.Log.LogInfo($"[matchmaking] drive: switcher '{sw.gameObject.name}' goes to '{to}'");
            if (pick == null && to.IndexOf("setting", StringComparison.OrdinalIgnoreCase) >= 0) pick = sw;
        }

        if (pick == null) { Plugin.Log.LogWarning("[matchmaking] drive: no switcher to the settings on screen"); return; }
        Plugin.Log.LogInfo($"[matchmaking] drive: opening the settings through '{pick.gameObject.name}'");
        pick.OnButtonClicked();
    }

    /// <summary>
    /// Every outfit this copy can see: each lobby player's character and the outfit its setter
    /// shows, and in a match every setter in the scene with the seat it belongs to. Then the keys
    /// the wardrobe keeps, which every copy on one PC shares.
    /// </summary>
    private static void LogSkins()
    {
        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        if (nm != null && nm.GamePlayers != null && Manager.Instance == null)
            foreach (var p in nm.GamePlayers)
            {
                if (p == null) continue;
                string outfit = "no setter";
                foreach (var s in p.GetComponents<SkinSetter>())
                    if (s != null && s.character == p.PlayerSkin) { outfit = s.CurrentSkin.ToString(CultureInfo.InvariantCulture); break; }
                Plugin.Log.LogInfo($"[matchmaking] drive: lobby connection {p.ConnectionID}{(p.isOwned ? " (this copy)" : "")} " +
                                   $"character {p.PlayerSkin} outfit {outfit}");
            }

        if (Manager.Instance != null)
            foreach (var s in UnityEngine.Object.FindObjectsOfType<SkinSetter>())
            {
                if (s == null) continue;
                var seat = s.GetComponentInParent<PlayerStats>();
                Plugin.Log.LogInfo($"[matchmaking] drive: match setter character {s.character} outfit {s.CurrentSkin} " +
                                   $"seat {(seat != null ? seat.Slot.ToString(CultureInfo.InvariantCulture) : "-")}{(s.isOwned ? " (this copy)" : "")}");
            }

        var keys = new System.Text.StringBuilder();
        foreach (string key in new[] { "ScubbySkin", "FoxySkin", "BristleSkin", "ToarSkin", "CupcakeSkin", "GerkSkin", "KudoSkin", "EjderSkin", "TuskSkin" })
            keys.Append($" {key}={PlayerPrefs.GetInt(key, 0)}");
        Plugin.Log.LogInfo($"[matchmaking] drive: keys{keys}");
        Plugin.Log.LogInfo($"[matchmaking] drive: wardrobe: {LobbyWardrobe.Describe()}");
    }

    // ------------------------------------------------------------ batch B: lobby ID, Deck 2, upkeep

    /// <summary>
    /// Which lobby the host's upkeep looks after, against what <c>CurrentLobbyID</c> and the lobby
    /// screen say now. After <c>strayid 1</c>, also whether the last lobby this copy left was the
    /// table's own: Steam still names this copy the owner of a lobby it has left, so that is no test.
    /// </summary>
    private static void LogUpkeep(SteamLobby sl)
    {
        ulong kept = Matchmaking.Upkept;
        ulong named = sl != null ? sl.CurrentLobbyID : 0;
        bool owned = Table.Hosted(out var hosted);
        string alive = kept != 0 ? SteamMatchmaking.GetLobbyData(new CSteamID(kept), Matchmaking.AliveKey) : "";
        string state = kept != 0 ? SteamMatchmaking.GetLobbyData(new CSteamID(kept), Matchmaking.StateKey) : "";
        var room = LobbyController.Instance;
        ulong screen = room != null ? room.CurrentLobbyID : 0;
        ulong own = _ownId != 0 ? _ownId : owned ? hosted.m_SteamID : 0;
        ulong left = Matchmaking.LastLeft;
        string still = _ownId == 0 ? "" :
            $"; the last lobby left: {(left == 0 ? "none" : left == _ownId ? "the table's own" : "another")}";

        Plugin.Log.LogInfo($"[matchmaking] drive: upkeep looks after {(kept == 0 ? "no lobby" : kept == hosted.m_SteamID ? "the lobby this copy hosts" : "ANOTHER lobby")}; " +
                           $"CurrentLobbyID names {(named == 0 ? "nothing" : owned && named == hosted.m_SteamID ? "that lobby" : "another lobby")}; " +
                           $"the lobby screen names {(room == null ? "no screen" : screen == 0 ? "nothing" : screen == own ? "the table's own" : "another lobby")}; " +
                           $"InGame '{Matchmaking.Clean(state, 12)}'; " +
                           $"heartbeat minute {Matchmaking.Clean(alive, 12)} (now {Matchmaking.AliveMinute}); " +
                           $"open to joiners: {(Matchmaking.Open.HasValue ? Matchmaking.Open.Value.ToString() : "not said yet")}{still}");
    }

    /// <summary>The deck variant as the lobby, the manager and the lobby's own pictures have it.</summary>
    private static void LogDeck()
    {
        var room = LobbyController.Instance;
        if (room == null) { Plugin.Log.LogWarning("[matchmaking] drive: no lobby here"); return; }

        var nm = room.manager;
        var art = room.DeckModeImage != null && room.DeckModeImage.sprite != null ? room.DeckModeImage.sprite.name : "none";
        Plugin.Log.LogInfo($"[matchmaking] drive: deck variant {room.DeckMode}, lobby mode {room.Mode}, " +
                           $"manager {(nm != null ? $"{nm.Mode}/{nm.DeckMode}" : "none")}, picture '{art}', " +
                           $"help up: {ShownHelp(room) ?? "none"}, table modded {Table.Modded}");
    }

    /// <summary>The name of whichever deck help button is showing.</summary>
    private static string ShownHelp(LobbyController room)
    {
        var bar = HelpBar(room);
        if (bar == null) return null;
        foreach (string name in new[] { "HelpDeckBasic", "HelpDeckDevil", "HelpDeck2", "HelpDeckChaos" })
        {
            var t = bar.Find(name);
            if (t != null && t.gameObject.activeInHierarchy) return name;
        }
        return null;
    }

    private static Transform HelpBar(LobbyController room)
    {
        var label = room.LoobyIdText;
        var canvas = label != null && label.canvas != null ? label.canvas.rootCanvas : null;
        return canvas != null ? canvas.transform.Find("GameMode Button/DeckGameModes") : null;
    }

    /// <summary>Press the deck help button that is up, as a click would.</summary>
    private static void PressHelp()
    {
        var room = LobbyController.Instance;
        string name = room != null ? ShownHelp(room) : null;
        var t = name != null ? HelpBar(room).Find(name) : null;
        var button = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
        if (button == null) { Plugin.Log.LogWarning("[matchmaking] drive: no deck help button up"); return; }

        Plugin.Log.LogInfo($"[matchmaking] drive: pressing {name}");
        button.onClick.Invoke();
    }

    /// <summary>The Deck 2 rules page's own close, as a click on it would.</summary>
    private static void PressPageClose()
    {
        var room = LobbyController.Instance;
        var label = room != null ? room.LoobyIdText : null;
        var canvas = label != null && label.canvas != null ? label.canvas.rootCanvas : null;
        var t = canvas != null ? canvas.transform.Find("TutorialDeck2/Image") : null;
        var button = t != null ? t.GetComponent<UnityEngine.UI.Button>() : null;
        if (button == null) { Plugin.Log.LogWarning("[matchmaking] drive: no close on the Deck 2 page"); return; }

        Plugin.Log.LogInfo("[matchmaking] drive: closing the Deck 2 page");
        button.onClick.Invoke();
    }

    /// <summary>Loopback host only: the character a connection plays, put on it as soon as it is in the lobby.</summary>
    private static readonly Dictionary<int, int> _skinFor = new();

    private static void GiveSkins()
    {
        if (_skinFor.Count == 0 || !NetworkServer.active || LobbyController.Instance == null) return;

        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        if (nm == null || nm.GamePlayers == null) return;

        foreach (var p in nm.GamePlayers)
        {
            if (p == null || !_skinFor.TryGetValue(p.ConnectionID, out int skin) || p.PlayerSkin == skin) continue;
            p.NetworkPlayerSkin = skin;
            Plugin.Log.LogInfo($"[matchmaking] drive: connection {p.ConnectionID} now plays character {skin}");
        }
    }

    /// <summary>The lobby screen's root, where every path the two commands below take starts.</summary>
    private static Transform LobbyRoot()
    {
        var room = LobbyController.Instance;
        var label = room != null ? room.LoobyIdText : null;
        var canvas = label != null && label.canvas != null ? label.canvas.rootCanvas : null;
        return canvas != null ? canvas.transform : null;
    }

    /// <summary>
    /// One lobby object and everything under it: whether it is on, where, its picture and its words.
    /// Only for the game's own fixed panels: a name plate's text is a player's name and stays out.
    /// </summary>
    private static void DumpUi(string path)
    {
        var root = LobbyRoot();
        var at = root != null ? root.Find(path) : null;
        if (at == null) { Plugin.Log.LogWarning($"[matchmaking] drive: uidump: nothing at '{path}'"); return; }
        DumpTree(at, 40);
    }

    /// <summary>
    /// The same for any screen's canvas: "menudump Canvas (1)/state_settings", the canvas named first.
    /// Several canvases can share a name, so the one where that part is on screen comes first.
    /// </summary>
    private static void MenuDump(string path)
    {
        int cut = path.IndexOf('/');
        string canvasName = cut > 0 ? path.Substring(0, cut) : path, rest = cut > 0 ? path.Substring(cut + 1) : "";
        Transform first = null;
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Canvas>()))
        {
            var canvas = found.TryCast<Canvas>();
            if (canvas == null || !canvas.gameObject.scene.IsValid() || canvas.name != canvasName) continue;
            var at = rest.Length > 0 ? canvas.transform.Find(rest) : canvas.transform;
            if (at == null) continue;
            if (at.gameObject.activeInHierarchy) { DumpTree(at, 80); return; }
            first ??= at;
        }
        if (first != null) { DumpTree(first, 80); return; }
        Plugin.Log.LogWarning($"[matchmaking] drive: menudump: nothing at '{path}'");
    }

    /// <summary>"buttons Canvas (2)": every button on screen under canvases of that name, by its object's name and place only.</summary>
    private static void Buttons(string canvasName)
    {
        var names = new List<string>();
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Canvas>()))
        {
            var canvas = found.TryCast<Canvas>();
            if (canvas == null || !canvas.gameObject.scene.IsValid() || canvas.name != canvasName || !canvas.gameObject.activeInHierarchy) continue;
            foreach (var button in canvas.GetComponentsInChildren<UnityEngine.UI.Button>(false))
            {
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                var up = button.transform.parent;
                names.Add($"{(up != null ? up.name + "/" : "")}{button.name}");
            }
        }
        Plugin.Log.LogInfo($"[matchmaking] drive: buttons on screen under {canvasName}: {(names.Count > 0 ? string.Join(", ", names) : "none")}");
    }

    private static void DumpTree(Transform at, int most)
    {
        int n = 0;
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        foreach (var t in at.GetComponentsInChildren<Transform>(true))
        {
            // Outside the loopback test harness only what is on screen, which the player sees anyway.
            if (!Fair.TestsAllowed && !t.gameObject.activeInHierarchy) continue;
            if (++n > most) break;
            var rt = t.TryCast<RectTransform>();
            // Where it falls on the screen, for a part on a screen-space canvas: its pixels, bottom up.
            if (rt != null && n == 1)
            {
                rt.GetWorldCorners(corners);
                Plugin.Log.LogInfo($"[matchmaking] drive: uidump {t.name} covers x {corners[0].x:0}..{corners[2].x:0}, " +
                                   $"y {corners[0].y:0}..{corners[2].y:0} of {Screen.width}x{Screen.height}");
            }
            var image = t.GetComponent<UnityEngine.UI.Image>();
            var text = t.GetComponent<TMPro.TMP_Text>();
            string words = text != null ? text.text ?? "" : null;
            if (words != null && words.Length > 40) words = words.Substring(0, 40);
            Plugin.Log.LogInfo($"[matchmaking] drive: uidump {t.name} {(t.gameObject.activeInHierarchy ? "on" : "off")}" +
                               (rt != null ? $" pos {rt.anchoredPosition.x:0.0},{rt.anchoredPosition.y:0.0} size {rt.sizeDelta.x:0.0}x{rt.sizeDelta.y:0.0} scale {rt.localScale.x:0.00}" : "") +
                               (image != null ? $" image '{(image.sprite != null ? image.sprite.name : "none")}' {image.type} colour {image.color.r:0.00},{image.color.g:0.00},{image.color.b:0.00},{image.color.a:0.00}" : "") +
                               (text != null ? $" text '{words.Replace('\n', '/')}' font '{(text.font != null ? text.font.name : "none")}' size {text.fontSize:0}" : ""));
        }
    }

    /// <summary>
    /// A menu screen opened the way its own button opens it, through the switcher that leads to it:
    /// "switchto stories". Every switcher on screen is named, to find the way when it is not that one.
    /// </summary>
    private static void SwitchTo(string name)
    {
        StateSwitcher pick = null;
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<StateSwitcher>()))
        {
            var sw = found.TryCast<StateSwitcher>();
            if (sw == null || !sw.gameObject.activeInHierarchy) continue;

            string to = sw.ChangeCanvasTo != null ? sw.ChangeCanvasTo.name : "";
            Plugin.Log.LogInfo($"[matchmaking] drive: switcher '{sw.gameObject.name}' goes to '{to}'");
            if (pick == null && name.Length > 0 && to.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) pick = sw;
        }

        // A sub-page hides its screen's buttons; the screen's own switcher still does what its click does.
        if (pick == null && name.Length > 0)
            foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<StateSwitcher>()))
            {
                var sw = found.TryCast<StateSwitcher>();
                if (sw == null || !sw.gameObject.scene.IsValid() || sw.ChangeCanvasTo == null) continue;
                if (string.Equals(sw.ChangeCanvasTo.name, name, StringComparison.OrdinalIgnoreCase)) { pick = sw; break; }
            }

        if (pick == null) { Plugin.Log.LogWarning($"[matchmaking] drive: no switcher to '{name}' on screen"); return; }
        Plugin.Log.LogInfo($"[matchmaking] drive: switching to '{pick.ChangeCanvasTo.name}' through '{pick.gameObject.name}'" +
                           (pick.gameObject.activeInHierarchy ? "" : " (not on screen)"));
        pick.OnButtonClicked();
    }

    /// <summary>A part of any screen's canvas, "Canvas (2)/Pause/Group/Fade", the canvas named first; null if none has it.</summary>
    private static Transform Part(string path)
    {
        int cut = path.IndexOf('/');
        if (cut <= 0) return null;
        string canvasName = path.Substring(0, cut), rest = path.Substring(cut + 1);
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Canvas>()))
        {
            var canvas = found.TryCast<Canvas>();
            if (canvas == null || !canvas.gameObject.scene.IsValid() || canvas.name != canvasName) continue;
            var at = canvas.transform.Find(rest);
            if (at != null) return at;
        }
        return null;
    }

    private static void PartRect(string path)
    {
        var rt = Part(path)?.TryCast<RectTransform>();
        if (rt == null) { Plugin.Log.LogWarning($"[matchmaking] drive: rect: nothing at '{path}'"); return; }
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        rt.GetWorldCorners(corners);
        Plugin.Log.LogInfo($"[matchmaking] drive: rect {rt.name} {(rt.gameObject.activeInHierarchy ? "on" : "off")} anchors " +
                           $"{rt.anchorMin.x:0.000},{rt.anchorMin.y:0.000}..{rt.anchorMax.x:0.000},{rt.anchorMax.y:0.000} pivot {rt.pivot.x:0.00},{rt.pivot.y:0.00} " +
                           $"pos {rt.anchoredPosition.x:0.0},{rt.anchoredPosition.y:0.0} size {rt.rect.width:0}x{rt.rect.height:0}; " +
                           $"on the screen x {corners[0].x:0}..{corners[2].x:0}, y {corners[0].y:0}..{corners[2].y:0} of {Screen.width}x{Screen.height}");
    }

    private static void PartShow(string words)
    {
        int space = words.LastIndexOf(' ');
        var at = space > 0 ? Part(words.Substring(0, space)) : null;
        if (at == null) { Plugin.Log.LogWarning($"[matchmaking] drive: partshow: nothing at '{words}'"); return; }
        bool on = words.Substring(space + 1) == "on";
        at.gameObject.SetActive(on);
        Plugin.Log.LogInfo($"[matchmaking] drive: partshow {at.name} {(on ? "on" : "off")}, on screen now: {at.gameObject.activeInHierarchy}");
    }

    private static void AnimInfo(string path)
    {
        var animator = Part(path)?.GetComponent<Animator>();
        if (animator == null) { Plugin.Log.LogWarning($"[matchmaking] drive: animinfo: no animator at '{path}'"); return; }
        var clips = new List<string>();
        var controller = animator.runtimeAnimatorController;
        if (controller != null && controller.animationClips != null)
            foreach (var clip in controller.animationClips) if (clip != null) clips.Add($"{clip.name} {clip.length:0.00}s");
        var parameters = new List<string>();
        if (animator.parameters != null)
            foreach (var p in animator.parameters) parameters.Add($"{p.name} {p.type}");
        var state = animator.GetCurrentAnimatorStateInfo(0);
        Plugin.Log.LogInfo($"[matchmaking] drive: animinfo {animator.name}: enabled {animator.enabled}, clips {string.Join(", ", clips)}; " +
                           $"parameters {string.Join(", ", parameters)}; state hash {state.shortNameHash} at {state.normalizedTime:0.00}");
    }

    private static void AnimBool(string words)
    {
        string[] parts = words.Split(' ');
        if (parts.Length < 3) { Plugin.Log.LogWarning("[matchmaking] drive: animbool: give a path, a switch and true or false"); return; }
        string path = string.Join(" ", parts, 0, parts.Length - 2);
        var animator = Part(path)?.GetComponent<Animator>();
        if (animator == null) { Plugin.Log.LogWarning($"[matchmaking] drive: animbool: no animator at '{path}'"); return; }
        bool on = parts[parts.Length - 1] == "true";
        animator.SetBool(parts[parts.Length - 2], on);
        Plugin.Log.LogInfo($"[matchmaking] drive: animbool {animator.name} {parts[parts.Length - 2]} {on}");
    }

    private static void AnimPlay(string words)
    {
        int space = words.LastIndexOf(' ');
        var animator = space > 0 ? Part(words.Substring(0, space))?.GetComponent<Animator>() : null;
        if (animator == null) { Plugin.Log.LogWarning($"[matchmaking] drive: animplay: no animator at '{words}'"); return; }
        string state = words.Substring(space + 1);
        animator.Play(state, 0, 0f);
        Plugin.Log.LogInfo($"[matchmaking] drive: animplay {animator.name} {state}");
    }

    /// <summary>
    /// A button on any screen's canvas, pressed through its own click: "menupress Canvas (1)/path/to/button",
    /// the canvas named first. Only the game's own buttons, by their fixed paths.
    /// </summary>
    private static void MenuPress(string path)
    {
        int cut = path.IndexOf('/');
        if (cut <= 0) { Plugin.Log.LogWarning("[matchmaking] drive: menupress: name the canvas, then the path"); return; }
        string canvasName = path.Substring(0, cut), rest = path.Substring(cut + 1);

        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Canvas>()))
        {
            var canvas = found.TryCast<Canvas>();
            if (canvas == null || !canvas.gameObject.scene.IsValid() || canvas.name != canvasName) continue;
            var at = canvas.transform.Find(rest);
            if (at == null)
            {
                // Only a name, "Canvas (1)/btn_table_stories": the first part by that name, wherever it is.
                string last = rest.Substring(rest.LastIndexOf('/') + 1);
                foreach (var part in canvas.GetComponentsInChildren<Transform>(true))
                    if (part.name == last) { at = part; break; }
            }
            var button = at != null ? at.GetComponent<UnityEngine.UI.Button>() : null;
            if (button == null) continue;
            if (!Pressable(button, path)) return;
            Plugin.Log.LogInfo($"[matchmaking] drive: pressing {path} ({(at.gameObject.activeInHierarchy ? "on screen" : "not on screen")})");
            button.onClick.Invoke();
            return;
        }
        Plugin.Log.LogWarning($"[matchmaking] drive: menupress: no button at '{path}'");
    }

    /// <summary>A button anywhere on the lobby screen, pressed through its own click.</summary>
    private static void PressPath(string path)
    {
        var root = LobbyRoot();
        var at = root != null ? root.Find(path) : null;
        var button = at != null ? at.GetComponent<UnityEngine.UI.Button>() : null;
        if (button == null) { Plugin.Log.LogWarning($"[matchmaking] drive: press: no button at '{path}'"); return; }
        if (!Pressable(button, path)) return;
        Plugin.Log.LogInfo($"[matchmaking] drive: pressing {path}");
        button.onClick.Invoke();
    }

    /// <summary>
    /// Outside the loopback test harness a press goes only to a button a player could click right
    /// now: on screen and enabled. Inside it, a test may press anything.
    /// </summary>
    private static bool Pressable(UnityEngine.UI.Button button, string path)
    {
        if (Fair.TestsAllowed) return true;
        if (button.gameObject.activeInHierarchy && button.interactable) return true;
        Plugin.Log.LogWarning($"[matchmaking] drive: '{path}' refused: not a button a player could click right now");
        return false;
    }

    /// <summary>Whether the clipboard holds this lobby's id. The clipboard itself is never written to the log.</summary>
    private static void LogClipboard()
    {
        var room = LobbyController.Instance;
        string want = room != null && room.CurrentLobbyID != 0 ? room.CurrentLobbyID.ToString(CultureInfo.InvariantCulture) : null;
        string held = GUIUtility.systemCopyBuffer ?? "";
        bool digits = held.Length > 0;
        foreach (char c in held) if (c < '0' || c > '9') digits = false;

        Plugin.Log.LogInfo($"[matchmaking] drive: clipboard holds {held.Length} characters, digits only: {digits}, " +
                           $"the lobby id: {(want == null ? "no lobby id here" : (held == want).ToString())}, " +
                           $"label shows the same id: {(room != null && room.LoobyIdText != null && want != null && room.LoobyIdText.text.Contains(want))}");
    }

    /// <summary>
    /// What an invite accepted while seated does to this copy, without a second account: the game
    /// writes the other lobby's id over <c>CurrentLobbyID</c> and the player stays at this table.
    /// One more than the real id stands in for the other lobby, and 0 puts the real id back. Steam
    /// refuses any write to a lobby this copy is not in, so the stand-in changes nothing there.
    /// </summary>
    private static void StrayId(SteamLobby sl, bool on)
    {
        if (on && _ownId == 0) _ownId = sl.CurrentLobbyID;
        if (_ownId == 0) return;

        sl.CurrentLobbyID = on ? _ownId + 1 : _ownId;
        if (!on) _ownId = 0;
        Plugin.Log.LogInfo($"[matchmaking] drive: the lobby id now names {(on ? "another lobby" : "this table's own")}");
    }

    /// <summary>The ranked button on the ENTER THE BAR panel, pressed through its own Button, from wherever the menu is.</summary>
    private static void PressRanked(Transform panel)
    {
        const string path = "state_story/btn_continue (1)";
        for (var t = panel; t != null; t = t.parent)
        {
            var found = t.Find(path);
            var button = found != null ? found.GetComponent<UnityEngine.UI.Button>() : null;
            if (button == null) continue;

            Plugin.Log.LogInfo($"[matchmaking] drive: pressing the ranked button (on screen: {found.gameObject.activeInHierarchy})");
            button.onClick.Invoke();
            return;
        }
        Plugin.Log.LogWarning($"[matchmaking] drive: no button at {path}");
    }

    /// <summary>Everything the ranked block and the table gate decide on, as this copy sees it.</summary>
    private static void LogGate()
    {
        var nm = NetworkManager.singleton;
        Plugin.Log.LogInfo($"[matchmaking] drive: gate scene={SceneManager.GetActiveScene().name} near={RankedGuard.Near} " +
                           $"rankedFlag={(nm != null ? nm.Matchmaking.ToString() : "no manager")} " +
                           $"rankedScreen={UnityEngine.Object.FindObjectOfType<MatchmakerTicketer>() != null} " +
                           $"allowed={RankedGuard.Allowed} table={Table.Modded} ({Table.Why}) " +
                           $"modnet {ModNet.DescribeGate()}");
    }

    private static void StartEmoteClock()
    {
        _emoteOf = null;
        foreach (var c in UnityEngine.Object.FindObjectsOfType<CharController>())
            if (c != null && c.isOwned) { _emoteOf = c; break; }

        if (_emoteOf == null)
        {
            Plugin.Log.LogWarning("[matchmaking] drive: no player of this copy's own to time");
            return;
        }

        // What an emote does to the counter: not ready, and counting from nothing.
        _emoteOf.EmoteReadyy = false;
        _emoteOf.EmoteCooldown = 0f;
        _emoteFrom = Time.unscaledTime;
        Plugin.Log.LogInfo($"[matchmaking] drive: emote clock started (table modded={Table.Modded})");
    }

    /// <summary>The emote stopwatch and the once-a-second ranked readout, both from <see cref="Drive"/>.</summary>
    private static void Clocks(float now)
    {
        GiveSkins();

        if (_emoteFrom >= 0f)
        {
            if (_emoteOf == null)
            {
                _emoteFrom = -1f;
                Plugin.Log.LogWarning("[matchmaking] drive: the timed player went away");
            }
            else if (_emoteOf.EmoteReadyy)
            {
                Plugin.Log.LogInfo($"[matchmaking] drive: emotes ready again after {now - _emoteFrom:0.00} s " +
                                   $"(table modded={Table.Modded})");
                _emoteFrom = -1f;
            }
            else if (now - _emoteFrom > 30f)
            {
                _emoteFrom = -1f;
                Plugin.Log.LogWarning("[matchmaking] drive: emotes still not ready after 30 s");
            }
        }

        if (now < _nearUntil && now >= _nearNext)
        {
            _nearNext = now + 1f;
            var nm = NetworkManager.singleton;
            Plugin.Log.LogInfo($"[matchmaking] drive: near={RankedGuard.Near} " +
                               $"rankedFlag={(nm != null ? nm.Matchmaking.ToString() : "no manager")} " +
                               $"scene={SceneManager.GetActiveScene().name} table={Table.Modded}");
        }
    }

    private static void Press(Transform panel, string path)
    {
        var found = panel != null ? panel.Find(path) : null;
        var button = found != null ? found.GetComponent<UnityEngine.UI.Button>() : null;
        if (button == null)
        {
            Plugin.Log.LogWarning($"[matchmaking] drive: no button at {path}");
            return;
        }
        button.onClick.Invoke();
    }

    private static void Box(LobbyListManager list, string rest)
    {
        string[] parts = rest.Split(' ');
        var box = parts[0] switch
        {
            "deck" => list.DeckFilter,
            "dice" => list.DiceFilter,
            "poker" => list.PokerFilter,
            _ => list.SpinFilter,
        };
        box.isOn = parts.Length > 1 && parts[1] == "1";
    }

    /// <summary>What a mouse would land on: CONTINUE on the button, and the shade on the game's menu behind it.</summary>
    private static void NewHit()
    {
        var button = WhatsNew.ContinueButton.GetComponent<RectTransform>();
        Hit("CONTINUE", button.TransformPoint(button.rect.center));
        Hit("ENTER THE BAR", new Vector2(0.17f * Screen.width, 0.52f * Screen.height));
    }

    private static void Hit(string what, Vector2 at)
    {
        var data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current) { position = at };
        var hits = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
        UnityEngine.EventSystems.EventSystem.current.RaycastAll(data, hits);
        Plugin.Log.LogInfo($"[matchmaking] drive: a click on {what} lands on '{(hits.Count > 0 ? hits[0].gameObject.name : "nothing")}'");
    }

    /// <summary>What comes along when Start is switched on and off every frame during a hold.</summary>
    private static void StartInfo()
    {
        var start = LobbyController.Instance != null ? LobbyController.Instance.StartButtonActive : null;
        if (start == null)
        {
            Plugin.Log.LogWarning("[matchmaking] drive: no Start button here");
            return;
        }

        foreach (var part in start.GetComponentsInChildren<Component>(true))
            Plugin.Log.LogInfo($"[matchmaking] drive: Start has {part.gameObject.name} <{part.GetIl2CppType().Name}>");

        foreach (var sound in start.GetComponentsInChildren<AudioSource>(true))
            Plugin.Log.LogInfo($"[matchmaking] drive: sound on {sound.gameObject.name}, playOnAwake={sound.playOnAwake}");
    }

    private static void JoinFirstRow(LobbyListManager list)
    {
        if (list.listOfLobbies.Count == 0)
        {
            Plugin.Log.LogWarning("[matchmaking] drive: no rows to join");
            return;
        }

        var row = list.listOfLobbies[0].GetComponent<LobbyDataEntry>();
        var button = row.joinButton.GetComponent<UnityEngine.UI.Button>();
        Plugin.Log.LogInfo($"[matchmaking] drive: JOIN on '{row.LobbyName}', showing: {row.joinButton.activeInHierarchy}");
        if (button != null) button.onClick.Invoke();
        else row.JoinLobby();
    }

    /// <summary>What a person would see, as text.</summary>
    private static void LogState()
    {
        var sl = SteamLobby.Instance;
        var list = LobbyListManager.instance;
        var room = LobbyController.Instance;

        Plugin.Log.LogInfo($"[matchmaking] state: scene={SceneManager.GetActiveScene().name} " +
                           $"server={NetworkServer.active} client={NetworkClient.active} connected={NetworkClient.isConnected} " +
                           $"lobby={(sl != null ? sl.CurrentLobbyID : 0)} joinLocked={(sl != null && sl.JoinLocked)} " +
                           $"quickJoining={QuickMatch.Joining} steamLobby={(sl != null ? sl.GetInstanceID() : 0)} " +
                           $"hosted={(sl != null && sl.lobyhosted)} changelog={WhatsNew.Showing}");

        if (list != null)
        {
            var box = list.ConnectingScreen;
            var info = list.ConnectingScreenInfo;
            var panel = QuickMatch.ListPanel(list);
            Plugin.Log.LogInfo($"[matchmaking] state: list open={panel != null && panel.gameObject.activeInHierarchy} " +
                               $"box={box != null && box.activeInHierarchy} text='{(info != null ? info.text : "")}' " +
                               $"backdrop={list.ConnectionBG != null && list.ConnectionBG.activeInHierarchy} " +
                               $"boxes D{list.DeckFilter.isOn} L{list.DiceFilter.isOn} P{list.PokerFilter.isOn} S{list.SpinFilter.isOn}");

            for (int i = 0; i < list.listOfLobbies.Count; i++)
            {
                var row = list.listOfLobbies[i].GetComponent<LobbyDataEntry>();
                var words = new List<string>();
                foreach (var t in row.GetComponentsInChildren<UnityEngine.UI.Text>(true)) words.Add(t.text);
                Plugin.Log.LogInfo($"[matchmaking] state: row {i}: {string.Join(" | ", words)} " +
                                   $"join={(row.joinButton != null && row.joinButton.activeInHierarchy)}");
            }
        }

        if (room != null)
            Plugin.Log.LogInfo($"[matchmaking] state: lobby screen label='{(room.lobbyvisible != null ? room.lobbyvisible.text : "")}' " +
                               $"on screen={(room.lobbyvisible != null && room.lobbyvisible.gameObject.activeInHierarchy)} " +
                               $"name='{(room.LobbyNameText != null ? room.LobbyNameText.text : "")}' " +
                               $"startLit={(room.StartButtonActive != null && room.StartButtonActive.activeInHierarchy)}");
    }

    private static void ReportOwnLobby()
    {
        var sl = SteamLobby.Instance;
        if (sl == null || sl.CurrentLobbyID == 0) return;
        Plugin.Log.LogInfo($"[matchmaking] selftest: hosting {Describe(new CSteamID(sl.CurrentLobbyID))}");
    }

    /// <summary>Every lobby a search brought back, with its data, as this copy can read it.</summary>
    internal static void Report(Il2CppSystem.Collections.Generic.List<CSteamID> lobbies)
    {
        if (Mode == null) return;

        try
        {
            int count = lobbies != null ? lobbies.Count : 0;
            Plugin.Log.LogInfo($"[matchmaking] selftest: search returned {count} lobbies");
            for (int i = 0; i < count; i++)
                Plugin.Log.LogInfo($"[matchmaking] selftest:   {Describe(lobbies[i])}");
        }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] selftest: could not read the list: {e.Message}"); }
    }

    private static string Describe(CSteamID id)
    {
        string why = Matchmaking.WhyNot(id);
        return $"{id.m_SteamID} name='{Read(id, "name")}' {Matchmaking.VersionKey}={Read(id, Matchmaking.VersionKey)} " +
               $"{VersionCheck.Key}={Read(id, VersionCheck.Key)} {Matchmaking.StateKey}={Read(id, Matchmaking.StateKey)} " +
               $"{Matchmaking.ModeKey}={Read(id, Matchmaking.ModeKey)} playercount={Read(id, "playercount")} " +
               $"{Matchmaking.AliveKey}={Read(id, Matchmaking.AliveKey)} " +
               $"members={SteamMatchmaking.GetNumLobbyMembers(id)}/{SteamMatchmaking.GetLobbyMemberLimit(id)} " +
               $"joinable={(why == null ? "yes" : "no: " + why.Replace('\n', ' '))}";
    }

    private static string Read(CSteamID id, string key) =>
        Matchmaking.Clean(SteamMatchmaking.GetLobbyData(id, key), 40);
}
