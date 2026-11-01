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

    /// <summary>From <see cref="ModTicker"/>. Nothing unless the variable is set.</summary>
    internal static void Tick()
    {
        string mode = Mode;
        if (mode == null || Loopback.Active) return;

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
    private static void Watch()
    {
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
    private static void Run(string line, float now)
    {
        int space = line.IndexOf(' ');
        string word = space < 0 ? line : line.Substring(0, space);
        string rest = space < 0 ? "" : line.Substring(space + 1).Trim();

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
            case "open":
                Plugin.Log.LogInfo($"[matchmaking] drive: the list opened: {QuickMatch.OpenList(list)}");
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
                AimHud.Pretend(rest);
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
            case "stophost":
                NetworkManager.singleton.StopHost();
                break;
            default:
                Plugin.Log.LogWarning($"[matchmaking] drive: no such command '{word}'");
                break;
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
