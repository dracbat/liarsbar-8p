using System;
using System.Globalization;
using HarmonyLib;
using Mirror;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiarsBar8P;

/// <summary>
/// One line on the main menu, beside ENTER THE BAR, saying how many lobbies with the mod are open
/// and how many players are waiting in them. Nothing at all at zero: with few players a nought
/// reads as "nobody plays this", which is the reason the lobby list never says it either.
///
/// The number comes from the game's own lobby search, sent with a note that the answer is this
/// class's, so it passes through every change the mod makes to that search. Steam's answer does
/// not say which search it answers, and if the game's list ever gets this answer or this class the
/// list's, both hold the same lobbies. The one thing that must never happen is this class keeping
/// an answer the game was waiting for, so the moment anything else starts a search the note is
/// torn up and the next answer goes to the game.
///
/// Only lobbies this copy could take a seat at are counted: sitting in the lobby, on this version,
/// at this seat count, with a seat free. Lobbies in a match are shut and never returned, so the
/// players are the ones waiting for a game, not everybody playing.
///
/// Not near ranked, and not with <c>ShowUnmoddedLobbies</c>: that player has asked for the game's
/// own list, and this number would be about the other one.
/// </summary>
internal static class LobbyCount
{
    // Every word a player sees from this class. {0} is a count, {1} the mod's name.
    private const string LobbyOne = "{0} {1} LOBBY OPEN";
    private const string LobbyMany = "{0} {1} LOBBIES OPEN";
    private const string PlayerOne = "{0} PLAYER WAITING";
    private const string PlayerMany = "{0} PLAYERS WAITING";
    private const char Dot = '·';

    /// <summary>After the menu appears, as long as the changelog waits, so the menu has settled.</summary>
    private const float Settle = 2.5f;

    /// <summary>How often to look while the menu stays up. Quick Join looks every five seconds and nothing minds.</summary>
    private const float Every = 60f;

    /// <summary>Steam gives up on a search at twenty seconds.</summary>
    private const float AnswerWait = 25f;

    /// <summary>A count older than this is not shown: it would be a number about some other minute.</summary>
    private const float FreshFor = 90f;

    private const float Gap = 24f;
    private const float Size = 22f;

    private const string MenuPath = "state_menu";
    private const string ButtonName = "btn_story";
    private const string StorePath = "Store";

    private static bool _mine, _inFlight;
    private static float _sentAt, _next, _menuSince = -1f, _countedAt = -1f;
    private static int _lobbies, _players;
    private static string _said;

    private static TextMeshProUGUI _line;
    private static IntPtr _lineOn;

    /// <summary>Developer test switch: the line shows this many lobbies, or the real count when below zero.</summary>
    internal static int PretendLobbies { get; set; } = -1;
    internal static int PretendPlayers { get; set; }

    // ------------------------------------------------------------------ the search

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.GetLobbiesList))]
    private static void AnotherSearch()
    {
        if (!_mine) _inFlight = false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.GetLobbiesListMatchmaking))]
    private static void QuickSearch() => _inFlight = false;

    /// <summary>
    /// Every lobby search's answer. Only the one this class sent is kept, and the game's handler is
    /// skipped for it: left alone it would ask for every lobby's data and fill a list nobody has
    /// open. Every other answer goes on to the game as before.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.OnGetLobbyList))]
    private static bool Answer(LobbyMatchList_t result)
    {
        if (!_inFlight) return true;
        _inFlight = false;

        try
        {
            int returned = (int)Math.Min(result.m_nLobbiesMatching, 50u);
            if (_ownRow != 0)
            {
                Plugin.Log.LogInfo($"[lobbycount] the pool's search from this lobby: {OwnRow(returned)}");
                _ownRow = 0;
                return false;
            }

            int lobbies = 0, players = 0;
            for (int i = 0; i < returned; i++)
            {
                var id = SteamMatchmaking.GetLobbyByIndex(i);
                if (id.m_SteamID == 0 || !Matchmaking.Seatable(id)) continue;
                lobbies++;
                players += Members(id);
            }
            Record(lobbies, players, $"{returned} returned");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[lobbycount] could not read the search: {e.Message}"); }
        return false;
    }

    /// <summary>The lobby list has just been filled, which is a fresh count with no search of its own.</summary>
    internal static void Saw(Il2CppSystem.Collections.Generic.List<CSteamID> found)
    {
        try
        {
            if (!Matchmaking.Active || found == null) return;

            int lobbies = 0, players = 0;
            for (int i = 0; i < found.Count; i++)
            {
                if (!Matchmaking.Seatable(found[i])) continue;
                lobbies++;
                players += Members(found[i]);
            }
            Record(lobbies, players, "from the lobby list");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[lobbycount] could not count the list: {e.Message}"); }
    }

    /// <summary>Steam's own count for a lobby this copy is not in, or the host's own figure if Steam has none.</summary>
    private static int Members(CSteamID id)
    {
        int members = SteamMatchmaking.GetNumLobbyMembers(id);
        if (members <= 0 && int.TryParse(SteamMatchmaking.GetLobbyData(id, "playercount"), NumberStyles.None,
                CultureInfo.InvariantCulture, out int said))
            members = said;
        return Math.Max(members, 0);
    }

    private static void Record(int lobbies, int players, string how)
    {
        _lobbies = lobbies;
        _players = players;
        _countedAt = Time.unscaledTime;

        string said = $"{lobbies}/{players}";
        if (said == _said) return;
        _said = said;
        Plugin.Log.LogInfo($"[lobbycount] {lobbies} lobbies open with {players} players waiting ({how})");
    }

    private static ulong _ownRow;

    /// <summary>
    /// For the test driver, on a copy hosting a lobby: the pool's own search, as the list sends it
    /// but without the list's screen, answered here with whether this lobby came back and what its
    /// row would read.
    /// </summary>
    internal static string SearchForOwnRow()
    {
        if (!Table.Hosted(out var own)) return "not hosting a lobby of its own";
        string tag = SteamMatchmaking.GetLobbyData(own, Matchmaking.VersionKey);
        if (string.IsNullOrEmpty(tag)) return "the lobby has no tag";

        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        SteamMatchmaking.AddRequestLobbyListStringFilter(Matchmaking.VersionKey, tag, ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);
        SteamMatchmaking.RequestLobbyList();
        _ownRow = own.m_SteamID;
        _inFlight = true;
        _sentAt = Time.unscaledTime;
        return "searching";
    }

    private static string OwnRow(int returned)
    {
        var own = new CSteamID(_ownRow);
        bool listed = false;
        for (int i = 0; i < returned && !listed; i++) listed = SteamMatchmaking.GetLobbyByIndex(i).m_SteamID == _ownRow;

        return $"{returned} returned, this lobby {(listed ? "among them" : "NOT among them")}; its row reads " +
               $"{SteamMatchmaking.GetNumLobbyMembers(own)}/{SteamMatchmaking.GetLobbyMemberLimit(own)}, " +
               $"its playercount says {Matchmaking.Clean(SteamMatchmaking.GetLobbyData(own, "playercount"), 4)}, " +
               $"state '{Matchmaking.Clean(SteamMatchmaking.GetLobbyData(own, Matchmaking.StateKey), 12)}', " +
               $"seatable {Matchmaking.Seatable(own)}";
    }

    /// <summary>Look now, for the test driver. The usual guards still apply.</summary>
    internal static void LookNow()
    {
        _next = 0f;
        _said = null;
    }

    // ------------------------------------------------------------------ the line

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (_inFlight && now - _sentAt > AnswerWait) _inFlight = false;

        Transform canvas = null;
        var story = Allowed() ? ButtonAtMenu(out canvas) : null;
        if (story == null)
        {
            Hide();
            _menuSince = -1f;
            return;
        }

        if (_menuSince < 0f)
        {
            _menuSince = now;
            _next = Math.Max(_next, now + Settle);
        }

        if (now >= _next && CanSearch(canvas))
        {
            _next = now + Every;
            Search(now);
        }

        Show(story, now);
    }

    /// <summary>The main menu of a copy in the modded pool, with Steam up and no lobby under way.</summary>
    private static bool Allowed() =>
        Matchmaking.Active && !RankedGuard.Near && Matchmaking.SteamUp &&
        !NetworkClient.active && !NetworkServer.active &&
        SceneManager.GetActiveScene().name == Matchmaking.Menu;

    /// <summary>ENTER THE BAR, while the menu it is on is showing.</summary>
    private static Transform ButtonAtMenu(out Transform canvas)
    {
        canvas = null;
        var list = LobbyListManager.instance;
        var panel = list != null ? QuickMatch.ListPanel(list) : null;
        canvas = panel != null ? panel.parent : null;

        var menu = canvas != null ? canvas.Find(MenuPath) : null;
        if (menu == null || !menu.gameObject.activeInHierarchy) return null;

        var story = menu.Find(ButtonName);
        return story != null && story.gameObject.activeInHierarchy ? story : null;
    }

    /// <summary>Nobody is reading rows, joining, or looking at anything laid over the menu.</summary>
    private static bool CanSearch(Transform canvas)
    {
        if (_inFlight || QuickMatch.Busy || WhatsNew.Showing || UpdatePrompt.Showing) return false;

        var sl = SteamLobby.Instance;
        if (sl == null || sl.JoinLocked) return false;

        var store = canvas.Find(StorePath);
        return store == null || !store.gameObject.activeInHierarchy;
    }

    private static void Search(float now)
    {
        _mine = true;
        try
        {
            SteamLobby.Instance.GetLobbiesList();
            _inFlight = true;
            _sentAt = now;
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[lobbycount] could not search: {e.Message}"); }
        finally { _mine = false; }
    }

    private static void Show(Transform story, float now)
    {
        bool pretend = PretendLobbies >= 0 && Dev.Enabled;
        int lobbies = pretend ? PretendLobbies : _lobbies;
        int players = pretend ? PretendPlayers : _players;
        bool fresh = pretend || (_countedAt >= 0f && now - _countedAt < FreshFor);

        if (!fresh || lobbies <= 0)
        {
            Hide();
            return;
        }

        var line = Line(story);
        if (line == null) return;

        string words = Words(line, lobbies, players);
        if (line.text != words) line.text = words;

        // Beside the words of the button, wherever its language makes them end.
        var button = story.GetComponent<TextMeshProUGUI>();
        float after = button != null ? button.preferredWidth : 0f;
        var box = line.rectTransform;
        box.anchoredPosition = new Vector2(after + Gap, 0f);
        box.sizeDelta = new Vector2(line.GetPreferredValues(words).x + 4f, Size * 2f);

        if (!line.gameObject.activeSelf) line.gameObject.SetActive(true);
    }

    private static string Words(TMP_Text line, int lobbies, int players)
    {
        string open = LiarsBar8P.Words.N(LobbyOne, LobbyMany, lobbies, Plugin.TagName);
        if (players <= 0) return open;

        string waiting = LiarsBar8P.Words.N(PlayerOne, PlayerMany, players);
        string between = line.font != null && line.font.HasCharacter(Dot, true, true) ? $"  {Dot}  " : "  -  ";
        return open + between + waiting;
    }

    /// <summary>
    /// The line, hung from ENTER THE BAR itself so it slides and fades with the button, in the
    /// button's own font, in the game's gold. It takes no clicks and draws no markup.
    /// </summary>
    private static TextMeshProUGUI Line(Transform story)
    {
        if (_line != null && _lineOn == story.Pointer) return _line;

        var like = story.GetComponent<TextMeshProUGUI>();
        if (like == null) return null;

        var go = new GameObject("LB8P_LobbyCount") { layer = story.gameObject.layer };
        var box = go.AddComponent<RectTransform>();
        box.SetParent(story, false);
        box.anchorMin = box.anchorMax = box.pivot = new Vector2(0f, 0.5f);

        var text = go.AddComponent<TextMeshProUGUI>();
        text.font = like.font;
        text.fontSize = Size;
        text.color = ModCanvas.Gold;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.enableWordWrapping = false;
        text.richText = false;
        text.raycastTarget = false;

        go.SetActive(false);
        _line = text;
        _lineOn = story.Pointer;
        return text;
    }

    private static void Hide()
    {
        if (_line != null && _line.gameObject.activeSelf) _line.gameObject.SetActive(false);
    }
}
