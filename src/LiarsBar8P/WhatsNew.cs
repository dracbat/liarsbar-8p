using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// After an update, what changed: the changelog of the new version, once, at the main menu.
///
/// It has to come from the new version, because the one that did the download has no idea what
/// is in it. So the plugin carries <c>CHANGELOG.md</c> inside itself, which also means this
/// needs no network and cannot show the notes of some other version.
///
/// <b>Knowing there was an update.</b> The version this was last shown for is kept in the config.
/// That does not exist yet the first time this code runs, so two other signs count too: the
/// updater starts the game again with the old version in <c>LIARSBAR8P_RESTARTED</c>, which every
/// updater so far has done, and it leaves the old plugin beside the new one until the next
/// start, which covers an update that loaded later. With none of those it is a new install,
/// where everything is new, and nothing is shown. Having skipped versions shows each of them.
///
/// <b>How it looks.</b> Built on the menu's own canvas with the font of the game's own text, like
/// everything else this mod adds to the menus, not drawn on top of it. The news panel on the
/// menu would be the natural place, but that is a picture. A full-screen shade takes the clicks
/// while it is up, and the game's menus are left running: the window's own button has to work,
/// and the list behind it cannot be reached anyway.
///
/// If building it ever fails it is put away and counted as shown: a changelog is not worth a
/// player's menu.
/// </summary>
internal static class WhatsNew
{
    private const string Resource = "CHANGELOG.md";

    /// <summary>A player many versions behind wants the newest of it, not a book.</summary>
    private const int MostVersions = 5;

    /// <summary>Long enough for the menu's own fade to finish.</summary>
    private const float Settle = 2.5f;

    private const float PanelWidth = 1240f;
    private const float PanelSide = 64f;
    private const float TopBand = 136f;
    private const float BottomBand = 112f;
    private const float BarRoom = 26f;
    private const float KeyScrollSpeed = 900f;

    private const string Gold = "#FAD154";
    private static readonly Color Cream = new(0.95f, 0.92f, 0.82f, 1f);
    private static readonly Color GoldColour = new(0.98f, 0.82f, 0.33f, 1f);
    private static readonly Color Backing = new(0.055f, 0.045f, 0.04f, 1f);   // anything less, and the menu shows through
    private static readonly Color Shade = new(0f, 0f, 0f, 0.72f);

    private static string _notes;
    private static bool _open;
    private static bool _noAxis;
    private static float _readyAt;
    private static int _closedFrame = -10;
    private static GameObject _root;
    private static GameObject _selectedBefore;
    private static ScrollRect _scroll;
    private static RectTransform _viewport, _content, _thumb;

    internal static bool Showing => _open;

    /// <summary>
    /// While the window is up, and for the frame after it closes: the key that put it away must
    /// not go on to answer the update window underneath.
    /// </summary>
    internal static bool Holding => _open || Time.frameCount <= _closedFrame + 1;

    /// <summary>For the self test.</summary>
    internal static Button ContinueButton { get; private set; }

    /// <summary>For the self test: 1 is the top of the text, 0 the bottom.</summary>
    internal static void ScrollTo(float at)
    {
        if (_scroll != null) _scroll.verticalNormalizedPosition = at;
    }

    /// <summary>
    /// From <see cref="AutoUpdate.Start"/>, while the plugin the update replaced can still be seen.
    /// </summary>
    internal static void Decide(string restartedFrom, bool oldPluginLeft)
    {
        try
        {
            if (Loopback.Active || Plugin.ChangelogShownFor == null) return;

            // A test run must not use up the player's one look at it.
            bool restarted = !string.IsNullOrEmpty(restartedFrom);
            if (Dev.Enabled && !restarted) return;

            string shown = Plugin.ChangelogShownFor.Value;
            if (shown == Plugin.Version) return;

            string from = restarted ? restartedFrom : shown;
            if (string.IsNullOrEmpty(from) && !oldPluginLeft)
            {
                Plugin.ChangelogShownFor.Value = Plugin.Version;
                return;
            }

            _notes = Notes(from);
            if (_notes == null)
            {
                Plugin.Log.LogWarning($"[update] no changelog for v{Plugin.Version} in this build");
                Plugin.ChangelogShownFor.Value = Plugin.Version;
                return;
            }
            Plugin.Log.LogInfo($"[update] updated from {(string.IsNullOrEmpty(from) ? "an earlier version" : "v" + from)} - " +
                               "showing what changed at the main menu");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[update] could not read the changelog: {e.Message}"); }
    }

    // ------------------------------------------------------------------ the text

    /// <summary>The sections of the changelog newer than <paramref name="from"/>, or just this version's if that is not known.</summary>
    private static string Notes(string from)
    {
        using var stream = typeof(WhatsNew).Assembly.GetManifestResourceStream(Resource);
        if (stream == null) return null;

        string all;
        using (var reader = new StreamReader(stream, Encoding.UTF8)) all = reader.ReadToEnd();

        var running = Version.Parse(Plugin.Version);
        Version.TryParse(from, out Version before);

        var text = new StringBuilder();
        int versions = 0;
        foreach (Match m in Regex.Matches(all, @"(?ms)^## v(\d+\.\d+\.\d+)([^\r\n]*)\r?\n(.*?)(?=^## |\z)"))
        {
            var version = Version.Parse(m.Groups[1].Value);
            if (version > running || (before != null ? version <= before : version != running)) continue;
            if (++versions > MostVersions) break;

            if (text.Length > 0) text.Append('\n');
            text.Append($"<size=125%><color={Gold}>{Inline("v" + m.Groups[1].Value + m.Groups[2].Value.TrimEnd())}</color></size>\n");
            Section(text, m.Groups[3].Value);
        }
        return text.Length > 0 ? text.ToString() : null;
    }

    /// <summary>One version's text. The changelog is wrapped by hand, so its lines are joined back into paragraphs.</summary>
    private static void Section(StringBuilder text, string section)
    {
        var paragraph = new StringBuilder();
        int depth = 0;

        void End()
        {
            if (paragraph.Length == 0) return;

            string words = Inline(paragraph.ToString());
            text.Append(depth switch
            {
                0 => words,
                1 => $"•<indent=1.2em>{words}</indent>",
                _ => $"<indent=1.2em>•</indent><indent=2.4em>{words}</indent>",
            });
            text.Append('\n');
            paragraph.Clear();
        }

        foreach (string raw in section.Split('\n'))
        {
            string line = raw.TrimEnd();
            string words = line.TrimStart();
            if (words.Length == 0) { End(); depth = 0; continue; }

            if (words.StartsWith("- ", StringComparison.Ordinal))
            {
                End();
                depth = line.Length - words.Length >= 2 ? 2 : 1;
                paragraph.Append(words, 2, words.Length - 2);
                continue;
            }

            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(words);
        }
        End();
    }

    private static string Inline(string text)
    {
        text = text.Replace("<", "<noparse><</noparse>");
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", $"<color={Gold}>$1</color>");
        text = Regex.Replace(text, @"\*(.+?)\*", "<i>$1</i>");
        return text.Replace("`", "");
    }

    // --------------------------------------------------------------- the window

    /// <summary>From <see cref="ModTicker"/>. Nothing to do once it has been seen, which is nearly always.</summary>
    internal static void Tick()
    {
        if (_notes == null || _open) return;

        if (!AtMenu())
        {
            _readyAt = 0f;
            return;
        }

        float now = Time.unscaledTime;
        if (_readyAt <= 0f) { _readyAt = now + Settle; return; }
        if (now < _readyAt) return;

        try { Show(); }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[update] the changelog could not be shown: {e.Message}");
            Close();
        }
    }

    private static bool AtMenu() =>
        !UpdatePrompt.Showing && LobbyListManager.instance != null &&
        !NetworkClient.active && !NetworkServer.active &&
        SceneManager.GetActiveScene().name == Matchmaking.Menu;

    private static void Show()
    {
        var list = LobbyListManager.instance;
        var like = list.ConnectingScreenInfo;
        var panelBeside = QuickMatch.ListPanel(list);
        var canvas = panelBeside != null ? panelBeside.parent : null;
        if (like == null || canvas == null) throw new InvalidOperationException("the menu is not where it was");

        var root = Box("LB8P_WhatsNew", canvas);
        _root = root.gameObject;
        _open = true;
        root.SetAsLastSibling();
        Stretch(root, 0f, 0f, 0f, 0f);
        root.gameObject.AddComponent<Image>().color = Shade;

        var panel = Box("panel", root);
        panel.anchorMin = new Vector2(0.5f, 0.06f);
        panel.anchorMax = new Vector2(0.5f, 0.94f);
        panel.sizeDelta = new Vector2(PanelWidth, 0f);
        panel.gameObject.AddComponent<Image>().color = Backing;

        Rule(panel, 1f);
        Rule(panel, 0f);

        var title = Words("title", panel, like, 54f);
        var top = title.rectTransform;
        top.anchorMin = new Vector2(0f, 1f);
        top.anchorMax = Vector2.one;
        top.pivot = new Vector2(0.5f, 1f);
        top.sizeDelta = new Vector2(0f, 110f);
        top.anchoredPosition = new Vector2(0f, -14f);
        title.alignment = TextAlignmentOptions.Center;
        title.text = $"<size=50%><color={Gold}>LIAR'S BAR 8 PLAYER MOD UPDATE</color></size>\nWHAT'S NEW";

        var area = Box("scroll", panel);
        Stretch(area, PanelSide, TopBand, PanelSide, BottomBand);
        area.gameObject.AddComponent<Image>().color = Color.clear;   // somewhere for the wheel to land

        _viewport = Box("viewport", area);
        Stretch(_viewport, 0f, 0f, BarRoom, 0f);
        _viewport.gameObject.AddComponent<RectMask2D>();

        var body = Words("content", _viewport, like, 24f);
        _content = body.rectTransform;
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.pivot = new Vector2(0.5f, 1f);
        _content.sizeDelta = Vector2.zero;
        body.paragraphSpacing = 12f;
        body.text = _notes;

        Canvas.ForceUpdateCanvases();
        float wide = _viewport.rect.width;
        _content.sizeDelta = new Vector2(0f, body.GetPreferredValues(_notes, wide, 0f).y + 8f);

        _scroll = area.gameObject.AddComponent<ScrollRect>();
        _scroll.viewport = _viewport;
        _scroll.content = _content;
        _scroll.horizontal = false;
        _scroll.vertical = true;
        _scroll.movementType = ScrollRect.MovementType.Clamped;
        _scroll.scrollSensitivity = 60f;
        _scroll.verticalNormalizedPosition = 1f;

        _thumb = Box("thumb", area);
        _thumb.anchorMin = _thumb.anchorMax = _thumb.pivot = new Vector2(1f, 1f);
        _thumb.sizeDelta = new Vector2(5f, 0f);
        _thumb.gameObject.AddComponent<Image>().color = new Color(Cream.r, Cream.g, Cream.b, 0.65f);

        var label = Words("continue", panel, like, 38f);
        var box = label.rectTransform;
        box.anchorMin = box.anchorMax = new Vector2(0.5f, 0f);
        box.pivot = new Vector2(0.5f, 0f);
        box.sizeDelta = new Vector2(320f, 70f);
        box.anchoredPosition = new Vector2(0f, 20f);
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = true;
        label.text = "CONTINUE";

        var button = label.gameObject.AddComponent<Button>();
        button.targetGraphic = label;
        var tint = button.colors;
        tint.normalColor = Cream;
        tint.highlightedColor = GoldColour;
        tint.selectedColor = GoldColour;
        tint.pressedColor = GoldColour;
        button.colors = tint;
        var nav = button.navigation;
        nav.mode = Navigation.Mode.None;
        button.navigation = nav;
        button.onClick.AddListener(new Action(Close));
        ContinueButton = button;

        // Enter and a controller's A then press this and not whatever the menu had picked.
        var events = EventSystem.current;
        if (events != null)
        {
            _selectedBefore = events.currentSelectedGameObject;
            events.SetSelectedGameObject(label.gameObject);
        }

        Plugin.Log.LogInfo("[update] changelog up");
    }

    private static RectTransform Box(string name, Transform parent)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        var box = go.AddComponent<RectTransform>();
        box.SetParent(parent, false);
        return box;
    }

    private static void Stretch(RectTransform box, float left, float top, float right, float bottom)
    {
        box.anchorMin = Vector2.zero;
        box.anchorMax = Vector2.one;
        box.offsetMin = new Vector2(left, bottom);
        box.offsetMax = new Vector2(-right, -top);
    }

    /// <summary>The fine line the game draws above and below its own boxes.</summary>
    private static void Rule(RectTransform panel, float at)
    {
        var line = Box("rule", panel);
        line.anchorMin = new Vector2(0f, at);
        line.anchorMax = new Vector2(1f, at);
        line.sizeDelta = new Vector2(0f, 2f);
        line.gameObject.AddComponent<Image>().color = Cream;
    }

    /// <summary>Text in the font of the game's own text, whatever language has made that.</summary>
    private static TextMeshProUGUI Words(string name, Transform parent, TMP_Text like, float size)
    {
        var box = Box(name, parent);
        var text = box.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = like.font;
        text.fontSize = size;
        text.color = Cream;
        text.enableWordWrapping = true;
        text.raycastTarget = false;
        return text;
    }

    /// <summary>From <see cref="ModTicker"/>, every frame, so that a key press is not missed.</summary>
    internal static void Frame()
    {
        if (!_open) return;

        if (_root == null)
        {
            Close();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
            Input.GetKeyDown(KeyCode.JoystickButton0))
        {
            Close();
            return;
        }

        if (_scroll == null || _thumb == null) return;

        float seen = _viewport.rect.height, all = _content.rect.height, spare = all - seen;
        if (spare <= 1f)
        {
            _thumb.gameObject.SetActive(false);
            return;
        }

        float move = Steer();
        if (move != 0f)
            _scroll.verticalNormalizedPosition =
                Mathf.Clamp01(_scroll.verticalNormalizedPosition + move * KeyScrollSpeed * Time.unscaledDeltaTime / spare);

        float bar = Mathf.Max(40f, seen * seen / all);
        _thumb.sizeDelta = new Vector2(_thumb.sizeDelta.x, bar);
        _thumb.anchoredPosition = new Vector2(0f, -(1f - _scroll.verticalNormalizedPosition) * (seen - bar));
    }

    /// <summary>The arrow keys and a left stick, by the game's input axis, if it kept that.</summary>
    private static float Steer()
    {
        if (_noAxis) return 0f;
        try { return Input.GetAxisRaw("Vertical"); }
        catch
        {
            _noAxis = true;
            return 0f;
        }
    }

    internal static void Close()
    {
        _open = false;
        _notes = null;
        _closedFrame = Time.frameCount;

        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null;

        var events = EventSystem.current;
        if (events != null && _selectedBefore != null) events.SetSelectedGameObject(_selectedBefore);
        _selectedBefore = null;

        if (Plugin.ChangelogShownFor != null) Plugin.ChangelogShownFor.Value = Plugin.Version;
        Plugin.Log.LogInfo("[update] changelog put away");
    }
}
