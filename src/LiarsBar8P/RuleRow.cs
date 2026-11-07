using System;
using HarmonyLib;
using Mirror;
using TMPro;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The house rules in the lobby: HOUSE RULES, the preset between two arrows, and a line saying
/// what it does at the table chosen. Everyone at a modded table sees which rules the table plays;
/// only the host gets the arrows, the way the game shows its own deck arrows only to the host.
///
/// Made from the lobby's own pieces, the same ones the wardrobe strip is made of, so the two look
/// alike: the bar picker's arrows, which are shut while the lobby is up, and the LOBBY ID label.
/// It stands in the top band beside LEAVE, before the game tabs, where the lobby draws nothing
/// else: under LEAVE it touched the tallest characters' hats at four players. Up here it is clear
/// of every character, podium and name plate at any table size, and of the tabs at 16:10 too.
///
/// Built for each lobby screen, switched on only at a table where everyone runs the mod
/// (<see cref="Table.Modded"/>), never near ranked or in the Velvet Room. Anything that fails
/// leaves no row and the rules on CLASSIC. A controller's D-pad up and down belong to the
/// wardrobe, so this one is for the mouse, which a Steam Deck's trackpad also drives.
/// </summary>
internal static class RuleRow
{
    // Every word a player sees from this class. The presets and what they do are HouseRules' own.
    private const string Title = "HOUSE RULES";

    private const string LeftArrow = "SelectBarPannel/LeftArrow";
    private const string RightArrow = "SelectBarPannel/RightArrow";
    private const string Label = "Alt/LobbyID (1)";
    private const string Under = "SelectBarButton";

    // Top left, right of LEAVE, in the canvas's 1080 units. The tabs start about 390 in at 16:10.
    private const float Wide = 260f;
    private const float High = 64f;
    private const float FromLeft = 112f;
    private const float FromTop = 22f;
    private const float ArrowWide = 31f;
    private const float ArrowHigh = 35f;
    private const float TitleSize = 16f;
    private const float NameSize = 22f;
    private const float LineSize = 15f;
    private const float LineHigh = 42f;

    private static RectTransform _strip;
    private static TextMeshProUGUI _name, _line, _title;
    private static UnityEngine.UI.Button _left, _right;
    private static string _said;

    /// <summary>Whether the row is on screen now, for the test driver.</summary>
    internal static bool Up => _strip != null && _strip.gameObject.activeInHierarchy;

    [HarmonyPatch]
    internal static class Build
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
        private static void Started(LobbyController __instance)
        {
            try { Make(__instance); }
            catch (Exception e)
            {
                if (_strip != null) UnityEngine.Object.Destroy(_strip.gameObject);
                _strip = null;
                Plugin.Log.LogWarning($"[rules] no house rules row in this lobby: {e.Message}");
            }
        }
    }

    private static void Make(LobbyController room)
    {
        _strip = null;
        _name = _line = null;
        _said = null;
        if (room == null || RankedGuard.Near) return;

        var label = room.LoobyIdText;
        var canvas = label != null && label.canvas != null ? label.canvas.rootCanvas : null;
        var root = canvas != null ? canvas.transform : null;
        var left = root != null ? root.Find(LeftArrow) : null;
        var right = root != null ? root.Find(RightArrow) : null;
        var words = root != null ? root.Find(Label) : null;
        if (left == null || right == null || words == null)
        {
            Plugin.Log.LogWarning("[rules] the lobby screen is not laid out as expected - no house rules row");
            return;
        }

        var go = new GameObject("LB8P_Rules") { layer = 5 };
        var strip = go.AddComponent<RectTransform>();
        strip.SetParent(root, false);
        strip.anchorMin = strip.anchorMax = strip.pivot = new Vector2(0f, 1f);
        strip.sizeDelta = new Vector2(Wide, High);
        strip.anchoredPosition = new Vector2(FromLeft, -FromTop);

        // Under everything the game draws after the bar picker's button, its loading screen among them.
        var under = root.Find(Under);
        if (under != null) strip.SetSiblingIndex(under.GetSiblingIndex() + 1);

        var title = Text(words.gameObject, strip, "title", TitleSize);
        var name = Text(words.gameObject, strip, "preset", NameSize);
        var line = Text(words.gameObject, strip, "summary", LineSize);
        _left = Arrow(left.gameObject, strip, "left", 0f, () => Pressed(-1));
        _right = Arrow(right.gameObject, strip, "right", 1f, () => Pressed(1));
        if (title == null || name == null || line == null || _left == null || _right == null)
        {
            UnityEngine.Object.Destroy(go);
            return;
        }

        var top = title.rectTransform;
        top.anchorMin = new Vector2(0f, 1f);
        top.anchorMax = new Vector2(1f, 1f);
        top.pivot = new Vector2(0.5f, 1f);
        top.sizeDelta = new Vector2(0f, 22f);
        top.anchoredPosition = Vector2.zero;
        title.text = Words.T(Title);
        _title = title;

        var middle = name.rectTransform;
        middle.anchorMin = new Vector2(0f, 0f);
        middle.anchorMax = new Vector2(1f, 0f);
        middle.pivot = new Vector2(0.5f, 0f);
        middle.sizeDelta = new Vector2(-2f * (ArrowWide + 6f), 40f);
        middle.anchoredPosition = Vector2.zero;
        name.richText = true;
        name.enableAutoSizing = true;
        name.fontSizeMax = NameSize;
        name.fontSizeMin = 14f;

        // What the preset does at this table, under the row, two lines at most.
        var below = line.rectTransform;
        below.anchorMin = new Vector2(0f, 0f);
        below.anchorMax = new Vector2(1f, 0f);
        below.pivot = new Vector2(0.5f, 1f);
        below.sizeDelta = new Vector2(0f, LineHigh);
        below.anchoredPosition = new Vector2(0f, -2f);
        line.alignment = TextAlignmentOptions.Top;
        line.enableWordWrapping = true;
        line.richText = false;
        line.color = ModCanvas.Cream;

        go.SetActive(false);
        _strip = strip;
        _name = name;
        _line = line;
    }

    private static TextMeshProUGUI Text(GameObject like, Transform parent, string name, float size)
    {
        var copy = UiClone.Make(like, parent, name);
        var text = copy != null ? copy.GetComponent<TextMeshProUGUI>() : null;
        if (text == null) return null;

        text.fontSize = size;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
        return text;
    }

    private static UnityEngine.UI.Button Arrow(GameObject like, Transform parent, string name, float side, Action click)
    {
        var copy = UiClone.Make(like, parent, name, click);
        var button = copy != null ? copy.GetComponent<UnityEngine.UI.Button>() : null;
        if (button == null) return null;

        var box = copy.GetComponent<RectTransform>();
        box.anchorMin = box.anchorMax = box.pivot = new Vector2(side, 0f);
        box.sizeDelta = new Vector2(ArrowWide, ArrowHigh);
        box.anchoredPosition = new Vector2(0f, 2f);
        return button;
    }

    // ------------------------------------------------------------------ from ModTicker

    /// <summary>Four times a second: on or off, the arrows for the host, and the words.</summary>
    internal static void Tick()
    {
        if (_strip == null) return;

        var room = LobbyController.Instance;
        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        bool show = room != null && !RankedGuard.Near && Table.Modded && Manager.Instance == null &&
                    nm != null && !nm.Matchmaking && !nm.isVelvetRoom;
        if (_strip.gameObject.activeSelf != show) _strip.gameObject.SetActive(show);
        if (!show) return;

        bool arrows = HouseRules.CanPick(room);
        if (_left.gameObject.activeSelf != arrows) _left.gameObject.SetActive(arrows);
        if (_right.gameObject.activeSelf != arrows) _right.gameObject.SetActive(arrows);

        // The title follows a change of language made in the settings while the lobby is up.
        string heading = Words.T(Title);
        if (_title != null && _title.text != heading) _title.text = heading;

        int shown = HouseRules.Shown;
        string words = $"<color=#FFEF83>I</color>  {HouseRules.Names[shown]}  <color=#FFEF83>I</color>";
        if (_name.text != words) _name.text = words;

        var game = HouseRules.GameOf((int)room.Mode, room.DeckMode);
        string line = HouseRules.Waiting ?? HouseRules.Summary(shown, game);
        if (_line.text != line) _line.text = line;

        string said = $"{HouseRules.Names[shown]} at {game}{(arrows ? ", host" : "")}";
        if (said != _said)
        {
            _said = said;
            Plugin.Log.LogInfo($"[rules] lobby row shows {said}");
        }
    }

    private static void Pressed(int by)
    {
        try
        {
            if (!HouseRules.Pick(HouseRules.Picked + by)) return;

            // The sound the game makes when the bar picker moves.
            var sound = UnityEngine.Object.FindObjectOfType<SkinChangerLobby>();
            if (sound != null && sound.audioSource != null) sound.audioSource.Play();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[rules] could not change the house rules: {e.Message}"); }
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>Press one of the row's own arrows, through its button, as a click would.</summary>
    internal static bool Press(int by)
    {
        var button = by < 0 ? _left : _right;
        if (button == null || !Up || !button.gameObject.activeInHierarchy) return false;
        button.onClick.Invoke();
        return true;
    }

    /// <summary>Where the row is on screen, in pixels, and what it says.</summary>
    internal static string Describe()
    {
        if (_strip == null) return "no row in this lobby";
        if (!Up) return "row built, not shown";

        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        _strip.GetWorldCorners(corners);
        var below = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        _line.rectTransform.GetWorldCorners(below);
        return $"row at x {corners[0].x:0}..{corners[2].x:0}, y {below[0].y:0}..{corners[2].y:0} of {Screen.width}x{Screen.height}, " +
               $"arrows {(_left.gameObject.activeSelf ? "on" : "off")}, saying '{HouseRules.Names[HouseRules.Shown]}' / '{_line.text}'";
    }
}
