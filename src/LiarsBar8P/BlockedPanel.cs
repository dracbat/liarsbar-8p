using System;
using System.Collections.Generic;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LiarsBar8P;

/// <summary>
/// BLOCKED (n) in the lobby list's footer, beside QUICK JOIN, and the list of blocked players it
/// opens, each with UNBLOCK where JOIN would be.
///
/// No panel of its own: the lobby list's own rows are used, in the list itself, so it looks like
/// the rest of the screen. Pressing BLOCKED clears the list and puts in one row per blocked
/// player; pressing it again, Refresh, or any new search puts the lobbies back, because each of
/// those clears the list first. A row is the game's own row with everything that joins a lobby
/// taken out of it: its JOIN button's click, its Submit for a controller, and the words that the
/// language setting would write back. The count pill is hidden. The row is checked afterwards,
/// and dropped if anything that joins is left on it.
///
/// Only Steam ids are kept, so the name is Steam's: what it knows of the player now, which it
/// fetches for somebody who is not a friend and can take a moment. Until then the row says so.
/// Shown only while somebody is blocked, never near ranked.
/// </summary>
internal static class BlockedPanel
{
    // Every word a player sees from this class.
    private const string ButtonWords = "BLOCKED ({0})";
    private const string UnblockWord = "UNBLOCK";
    private const string NoName = "Unknown player";

    private const string ButtonName = "LB8P_Blocked";
    private const string RowName = "LB8P_BlockedRow";
    private const string Footer = "footer";
    private const string QuickJoin = "footer/QuickJoin";
    private const string Indicator = "txt_indicator";

    /// <summary>Where it stands: QUICK JOIN's place, one step further along the footer.</summary>
    private const float Step = 269f;
    private const float Wide = 260f;

    private static GameObject _button;
    private static TextMeshProUGUI _words;
    private static readonly Dictionary<ulong, UnityEngine.UI.Text> _rows = new();
    private static readonly List<TextMeshProUGUI> _unblocks = new();
    private static float _nextNames;

    // ------------------------------------------------------------------ from ModTicker

    /// <summary>Four times a second while the menu is up: the button, made once per menu, and the names on open rows.</summary>
    internal static void Tick()
    {
        var list = LobbyListManager.instance;
        if (list == null || RankedGuard.Near)
        {
            _rows.Clear();
            _unblocks.Clear();
            return;
        }

        if (_button == null) Make(list);
        if (_button == null) return;

        int n = BlockList.Count;
        bool show = n > 0;
        if (_button.activeSelf != show) _button.SetActive(show);
        if (show)
        {
            string words = Words.F(ButtonWords, n);
            if (_words.text != words) _words.text = words;
        }

        if (_rows.Count > 0 && Time.unscaledTime >= _nextNames) Names();
    }

    private static void Make(LobbyListManager list)
    {
        var panel = QuickMatch.ListPanel(list);
        var quick = panel != null ? panel.Find(QuickJoin) : null;
        var footer = panel != null ? panel.Find(Footer) : null;
        if (quick == null || footer == null) return;

        var copy = UiClone.Make(quick.gameObject, footer, ButtonName, Pressed);
        if (copy == null) return;

        var box = copy.GetComponent<RectTransform>();
        var from = quick.GetComponent<RectTransform>();
        box.anchoredPosition = from.anchoredPosition + new Vector2(Step, 0f);
        box.sizeDelta = new Vector2(Wide, from.sizeDelta.y);

        _words = copy.GetComponent<TextMeshProUGUI>();
        if (_words == null)
        {
            UnityEngine.Object.Destroy(copy);
            return;
        }
        _words.enableWordWrapping = false;
        _words.richText = false;

        copy.SetActive(false);
        _button = copy;
        Plugin.Log.LogInfo("[block] BLOCKED button made in the lobby list");
    }

    // ------------------------------------------------------------------ the list

    private static void Pressed()
    {
        try
        {
            var list = LobbyListManager.instance;
            if (list == null || RankedGuard.Near) return;

            // A second press puts the lobbies back, the same way Refresh does.
            if (Showing(list))
            {
                Back(list);
                return;
            }

            list.ClearLobbies();
            _rows.Clear();
            _unblocks.Clear();

            int made = 0;
            foreach (ulong id in BlockList.All())
                if (Row(list, id)) made++;
            Names();
            Plugin.Log.LogInfo($"[block] the list shows {made} blocked player(s)");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[block] could not show the blocked players: {e.Message}"); }
    }

    private static bool Showing(LobbyListManager list)
    {
        if (list.listOfLobbies == null) return false;
        foreach (var go in list.listOfLobbies)
            if (go != null && go.name == RowName) return true;
        return false;
    }

    private static void Back(LobbyListManager list)
    {
        _rows.Clear();
        _unblocks.Clear();
        list.ClearLobbies();
        var sl = SteamLobby.Instance;
        if (sl != null) sl.GetLobbiesList();
    }

    /// <summary>One blocked player's row: the game's row with every way of joining taken off it.</summary>
    private static bool Row(LobbyListManager list, ulong id)
    {
        var hold = new GameObject("LB8P_Hold");
        hold.SetActive(false);
        GameObject go = null;
        try
        {
            go = UnityEngine.Object.Instantiate(list.lobbyDataPrefab, hold.transform, false);
            go.name = RowName;

            foreach (var part in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (part != null && part.GetIl2CppType().Name == "LocalizeStringEvent") UnityEngine.Object.DestroyImmediate(part);

            foreach (var button in go.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();

            // The row's Submit joins the lobby. Its hover and select entries only light it up.
            foreach (var trigger in go.GetComponentsInChildren<EventTrigger>(true))
            {
                if (trigger.triggers == null) continue;
                for (int i = trigger.triggers.Count - 1; i >= 0; i--)
                {
                    var entry = trigger.triggers[i];
                    if (entry != null && (entry.eventID == EventTriggerType.Submit || Joins(entry.callback)))
                        trigger.triggers.RemoveAt(i);
                }
            }

            var row = go.GetComponent<LobbyDataEntry>();
            if (row == null || row.joinButton == null || row.lobbynameText == null || Joins(go))
            {
                Plugin.Log.LogWarning("[block] a blocked player's row still had a way to join on it - not shown");
                UnityEngine.Object.Destroy(go);
                return false;
            }

            row.lobbynameText.supportRichText = false;
            row.lobbynameText.text = Words.T(NoName);

            var count = row.playerCountext != null ? row.playerCountext.transform.parent : null;
            var pill = count != null ? count.parent : null;
            if (pill != null) pill.gameObject.SetActive(false);

            var unblock = row.joinButton.GetComponent<UnityEngine.UI.Button>();
            if (unblock == null) { UnityEngine.Object.Destroy(go); return false; }
            unblock.onClick.AddListener((Action)(() => Unblock(id)));
            foreach (var text in row.joinButton.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (text.gameObject.name == Indicator)
                {
                    text.text = Words.T(UnblockWord);
                    _unblocks.Add(text);
                }
            row.joinButton.SetActive(true);

            go.transform.SetParent(list.lobbylistContent.transform, false);
            go.transform.localScale = Vector3.one;
            list.listOfLobbies.Add(go);
            _rows[id] = row.lobbynameText;

            // Steam fetches the name of somebody who is not a friend on request.
            try { SteamFriends.RequestUserInformation(new CSteamID(id), true); }
            catch { }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[block] could not make a blocked player's row: {e.Message}");
            if (go != null) UnityEngine.Object.Destroy(go);
            return false;
        }
        finally
        {
            UnityEngine.Object.Destroy(hold);
        }
    }

    /// <summary>Anything left on the row that would join a lobby: a saved call to the row's join.</summary>
    private static bool Joins(GameObject go)
    {
        foreach (var button in go.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            if (Joins(button.onClick)) return true;
        foreach (var trigger in go.GetComponentsInChildren<EventTrigger>(true))
        {
            if (trigger.triggers == null) continue;
            foreach (var entry in trigger.triggers)
                if (entry != null && Joins(entry.callback)) return true;
        }
        return false;
    }

    private static bool Joins(UnityEngine.Events.UnityEventBase saved)
    {
        var group = saved != null ? saved.m_PersistentCalls : null;
        var calls = group != null ? group.m_Calls : null;
        if (calls == null) return false;
        foreach (var call in calls)
            if (call != null && call.m_MethodName == "JoinLobby") return true;
        return false;
    }

    private static void Unblock(ulong id)
    {
        try
        {
            if (!BlockList.Remove(id)) return;
            var list = LobbyListManager.instance;
            if (list == null) return;

            if (_rows.TryGetValue(id, out var text) && text != null)
            {
                var go = text.GetComponentInParent<LobbyDataEntry>().gameObject;
                list.listOfLobbies.Remove(go);
                UnityEngine.Object.Destroy(go);
            }
            _rows.Remove(id);

            // Nobody left on it: back to the lobbies.
            if (BlockList.Count == 0) Back(list);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[block] could not unblock from the list: {e.Message}"); }
    }

    /// <summary>The names Steam knows by now, as plain text, and UNBLOCK in the language the game is in now.</summary>
    private static void Names()
    {
        _nextNames = Time.unscaledTime + 1f;

        string unblock = Words.T(UnblockWord);
        foreach (var text in _unblocks)
            if (text != null && text.text != unblock) text.text = unblock;

        foreach (var pair in _rows)
        {
            if (pair.Value == null) continue;
            string name = "";
            try { name = SteamFriends.GetFriendPersonaName(new CSteamID(pair.Key)); }
            catch { }
            name = Matchmaking.Clean(name, LobbyRows.NameLength);
            string want = name.Length == 0 || name == "[unknown]" ? Words.T(NoName) : name;
            if (pair.Value.text != want) pair.Value.text = want;
        }
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>Press BLOCKED, as the mouse would.</summary>
    internal static string Press()
    {
        if (_button == null || !_button.activeInHierarchy) return "no BLOCKED button showing";
        _button.GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
        return "pressed";
    }

    /// <summary>Press the first row's UNBLOCK.</summary>
    internal static string PressFirstRow()
    {
        var list = LobbyListManager.instance;
        if (list == null || list.listOfLobbies == null) return "no list";
        foreach (var go in list.listOfLobbies)
        {
            if (go == null || go.name != RowName) continue;
            var row = go.GetComponent<LobbyDataEntry>();
            row.joinButton.GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
            return "pressed UNBLOCK";
        }
        return "no blocked rows showing";
    }

    /// <summary>The button and the rows, counted, never named.</summary>
    internal static string Describe()
    {
        var list = LobbyListManager.instance;
        int rows = 0, lobbies = 0;
        if (list != null && list.listOfLobbies != null)
            foreach (var go in list.listOfLobbies)
            {
                if (go == null) continue;
                if (go.name == RowName) rows++; else lobbies++;
            }
        return $"button {(_button != null && _button.activeInHierarchy ? $"'{_words.text}'" : "hidden")}, " +
               $"{rows} blocked row(s), {lobbies} lobby row(s), {BlockList.Count} on the list";
    }
}
