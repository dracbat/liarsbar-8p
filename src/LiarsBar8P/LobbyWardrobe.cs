using System;
using HarmonyLib;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LiarsBar8P;

/// <summary>
/// A skin picker in the lobby: two arrows and the name of the set your character is wearing, so
/// an outfit can be changed without leaving the table for the main menu's wardrobe.
///
/// The real wardrobe cannot come here. It lives in the menu scene with its own camera, its own
/// copies of every character and the menu's state machine, and the lobby is a scene of its own
/// with none of them. It is not needed either. Every player's lobby object already carries one
/// <c>SkinSetter</c> per character, with all of that character's outfits, and each applies the
/// outfit its network value names on every machine, every frame. The picker writes the outfit
/// to the key the wardrobe itself saves it under, and asks the character's own setter to load
/// it again, which sends the new value to the host the way the game always does. Everyone sees
/// the change at once, at any table size, and the next match reads the same key. Your own
/// character on its podium is the preview.
///
/// Only at a table where everyone runs the mod (<see cref="Table.Modded"/>), never near ranked,
/// and not for Dubai and Rasim, who have no outfits to choose from. A controller's D-pad up and
/// down step it too: the game uses only left and right in the lobby, for the character.
/// </summary>
internal static class LobbyWardrobe
{
    // Every word a player sees from this class. The set names are the wardrobe's own.
    private const string Title = "WARDROBE";
    private static readonly string[] SetNames =
    {
        "ORIGINAL SET", "CULTURAL SET", "CYBERPUNK SET", "CHRISTMAS SET", "VISION SET", "MADHOUSE SET", "HALLOWEN SET",
    };

    /// <summary>Where the wardrobe keeps each character's outfit, by character number. Dubai and Rasim have none.</summary>
    private static readonly string[] Keys =
    {
        "ScubbySkin", "FoxySkin", "BristleSkin", "ToarSkin", "CupcakeSkin", "GerkSkin", "KudoSkin", "EjderSkin", "TuskSkin",
    };

    /// <summary>
    /// Ejder and Tusk have four outfits, not seven: theirs are the original and the last three
    /// sets, and the wardrobe saves them as 0 to 3. Their names are picked by this.
    /// </summary>
    private static readonly int[] ShortSet = { 0, 4, 5, 6 };

    // What the strip is made of, all from the lobby's own screen: the arrows of the bar picker,
    // which is shut while the lobby is up, and the LOBBY ID label along the bottom.
    private const string LeftArrow = "SelectBarPannel/LeftArrow";
    private const string RightArrow = "SelectBarPannel/RightArrow";
    private const string Label = "Alt/LobbyID (1)";
    private const string Under = "SelectBarButton";
    private const string LocalPlayer = "LocalGamePlayer";

    // Top right, under the Start or Ready button, in the canvas's 1080 units. Lower down the right
    // column it covered the legs of whoever stood on the fourth podium with four in the lobby, and
    // that can be the very character being dressed. Up here it is over the bar's back wall at
    // four, and clear of every podium and name plate at eight.
    private const float Wide = 300f;
    private const float High = 64f;
    private const float FromRight = 24f;
    private const float FromTop = 150f;
    private const float ArrowWide = 31f;
    private const float ArrowHigh = 35f;
    private const float TitleSize = 16f;
    private const float NameSize = 22f;

    private static RectTransform _strip;
    private static TextMeshProUGUI _name;
    private static TextMeshProUGUI _title;
    private static UnityEngine.UI.Button _left, _right;
    private static int _shownFor = -1;
    private static int _padWas;
    private static bool _padDead;

    /// <summary>The character this copy has just asked to leave, while the host has not answered yet, and when it asked.</summary>
    private static int _leaving = -1;
    private static float _askedAt;

    /// <summary>Longer than any answer from the host takes. A change the host never makes stops counting after this.</summary>
    private const float Answer = 2f;

    /// <summary>Whether the strip is on screen now, for the test driver.</summary>
    internal static bool Up => _strip != null && _strip.gameObject.activeInHierarchy;

    [HarmonyPatch]
    internal static class Build
    {
        /// <summary>Made once for each lobby screen, and left switched off until <see cref="Tick"/> says.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
        private static void Started(LobbyController __instance)
        {
            try { Make(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning($"[wardrobe] no skin picker in this lobby: {e.Message}"); }
        }
    }

    /// <summary>
    /// The character on your podium is a network value only the host writes. Changing it sends the
    /// host a request, and your own copy goes on reading the old character until the answer comes
    /// back. A press of the picker in that moment wrote the outfit of the character being left and
    /// sent it as yours. So a character change you ask for is noted here, and the picker waits for
    /// the character you chose before it shows or changes anything again.
    /// </summary>
    [HarmonyPatch]
    internal static class Follow
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.SetSkin))]
        private static void Next(PlayerObjectController __instance) => Asked(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.SetSkinBack))]
        private static void Back(PlayerObjectController __instance) => Asked(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.CmdSetSkinIndex))]
        private static void Index(PlayerObjectController __instance) => Asked(__instance);
    }

    private static void Asked(PlayerObjectController p)
    {
        try
        {
            if (p == null || !p.isOwned) return;
            _leaving = p.PlayerSkin;
            _askedAt = Time.unscaledTime;
        }
        catch { }
    }

    /// <summary>True while a character change asked for here has not come back from the host.</summary>
    private static bool Changing(PlayerObjectController me)
    {
        if (_leaving < 0) return false;
        if (me == null || me.PlayerSkin != _leaving || Time.unscaledTime - _askedAt > Answer)
        {
            _leaving = -1;
            return false;
        }
        return true;
    }

    private static void Make(LobbyController room)
    {
        _strip = null;
        _name = null;
        _shownFor = -1;
        if (room == null || RankedGuard.Near) return;

        var label = room.LoobyIdText;
        var canvas = label != null && label.canvas != null ? label.canvas.rootCanvas : null;
        var root = canvas != null ? canvas.transform : null;
        var left = root != null ? root.Find(LeftArrow) : null;
        var right = root != null ? root.Find(RightArrow) : null;
        var words = root != null ? root.Find(Label) : null;
        if (left == null || right == null || words == null)
        {
            Plugin.Log.LogWarning("[wardrobe] the lobby screen is not laid out as expected - no skin picker");
            return;
        }

        var go = new GameObject("LB8P_Wardrobe") { layer = 5 };
        var strip = go.AddComponent<RectTransform>();
        strip.SetParent(root, false);
        strip.anchorMin = strip.anchorMax = strip.pivot = new Vector2(1f, 1f);
        strip.sizeDelta = new Vector2(Wide, High);
        strip.anchoredPosition = new Vector2(-FromRight, -FromTop);

        // Under everything the game draws after the bar picker's button, its loading screen among them.
        var under = root.Find(Under);
        if (under != null) strip.SetSiblingIndex(under.GetSiblingIndex() + 1);

        var title = Text(words.gameObject, strip, "title", TitleSize);
        var name = Text(words.gameObject, strip, "set", NameSize);
        _left = Arrow(left.gameObject, strip, "left", 0f, () => Pressed(-1));
        _right = Arrow(right.gameObject, strip, "right", 1f, () => Pressed(1));
        if (title == null || name == null || _left == null || _right == null)
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
        title.text = LiarsBar8P.Words.T(Title);
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

        go.SetActive(false);
        _strip = strip;
        _name = name;
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

    /// <summary>Four times a second: on or off, and the set your character is wearing.</summary>
    internal static void Tick()
    {
        if (_strip == null) return;

        // The title follows a change of language made in the settings while the lobby is up.
        string heading = LiarsBar8P.Words.T(Title);
        if (_title != null && _title.text != heading) _title.text = heading;

        // Left as it is until the character asked for arrives: switching it off for that moment
        // would only make it blink.
        if (Changing(Own())) return;

        SkinSetter setter = null;
        bool show = !RankedGuard.Near && Table.Modded && Manager.Instance == null && !Velvet() && Mine(out setter);
        if (_strip.gameObject.activeSelf != show) _strip.gameObject.SetActive(show);
        if (!show)
        {
            _shownFor = -1;
            return;
        }

        string words = Words(setter);
        if (_name.text != words) _name.text = words;

        if (_shownFor != setter.character)
        {
            _shownFor = setter.character;
            Plugin.Log.LogInfo($"[wardrobe] skin picker up for character {setter.character} ({setter.Skins.Count} outfits)");
        }
    }

    /// <summary>
    /// Every frame: the D-pad's up and down. An edge, so holding it steps once. Not while typing in
    /// the chat, and never again this session if the build has no such axis.
    /// </summary>
    internal static void Frame()
    {
        if (_padDead || !Up) return;

        // A controller is read even by a game that is not the one in front: not ours to answer then.
        if (!Application.isFocused) { _padWas = 0; return; }

        float y;
        try { y = Input.GetAxisRaw("DPadY"); }
        catch { _padDead = true; return; }

        int dir = y > 0.5f ? 1 : y < -0.5f ? -1 : 0;
        if (dir != 0 && dir != _padWas && !Typing()) Pressed(dir);
        _padWas = dir;
    }

    private static bool Typing()
    {
        var events = EventSystem.current;
        var picked = events != null ? events.currentSelectedGameObject : null;
        if (picked == null) return false;

        var tmp = picked.GetComponent<TMP_InputField>();
        if (tmp != null && tmp.isFocused) return true;
        var plain = picked.GetComponent<UnityEngine.UI.InputField>();
        return plain != null && plain.isFocused;
    }

    // ------------------------------------------------------------------ changing it

    private static void Pressed(int by)
    {
        try
        {
            if (!Step(by)) return;

            // The sound the game makes when a character changes on its podium.
            var sound = UnityEngine.Object.FindObjectOfType<SkinChangerLobby>();
            if (sound != null && sound.audioSource != null) sound.audioSource.Play();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[wardrobe] could not change outfit: {e.Message}"); }
    }

    /// <summary>
    /// The next or previous outfit for your own character. The key is the one truth: it is what the
    /// game's own setter reads back and sends on, and what the next match will read.
    /// </summary>
    internal static bool Step(int by)
    {
        if (RankedGuard.Near || !Table.Modded || Manager.Instance != null || !Mine(out var setter)) return false;

        int n = setter.Skins.Count;
        string key = Keys[setter.character];
        int now = Wrap(PlayerPrefs.GetInt(key, 0), n);
        int next = Wrap(now + by, n);

        PlayerPrefs.SetInt(key, next);
        PlayerPrefs.Save();
        setter.GetSkinRPC();

        Plugin.Log.LogInfo($"[wardrobe] character {setter.character} outfit {now} -> {next} of {n}");
        return true;
    }

    private static int Wrap(int value, int n) => ((value % n) + n) % n;

    /// <summary>Your own lobby player, or null.</summary>
    private static PlayerObjectController Own()
    {
        var room = LobbyController.Instance;
        var me = room != null ? room.LocalPlayerController : null;
        if (me == null)
        {
            var go = GameObject.Find(LocalPlayer);
            me = go != null ? go.GetComponent<PlayerObjectController>() : null;
        }
        return me != null && me.isOwned ? me : null;
    }

    /// <summary>
    /// The setter of your own character, if it has outfits to choose from. None while a character
    /// change you asked for is on its way, because until then the character read here is the old one.
    /// </summary>
    private static bool Mine(out SkinSetter setter)
    {
        setter = null;

        var me = Own();
        if (me == null || Changing(me)) return false;

        int character = me.PlayerSkin;
        if (character < 0 || character >= Keys.Length) return false;

        foreach (var s in me.GetComponents<SkinSetter>())
        {
            if (s == null || s.character != character || s.Skins == null || s.Skins.Count == 0) continue;
            setter = s;
            return true;
        }
        return false;
    }

    /// <summary>The set's name as the wardrobe shows it, between its gold bars. The mod's own words, so rich text.</summary>
    private static string Words(SkinSetter setter)
    {
        int n = setter.Skins.Count;
        int at = Wrap(setter.CurrentSkin, n);
        int set = n == ShortSet.Length ? ShortSet[at] : at;
        string name = set < SetNames.Length ? SetNames[set] : $"SET {at + 1}";
        return $"<color=#FFEF83>I</color>  {name}  <color=#FFEF83>I</color>";
    }

    private static bool Velvet()
    {
        var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
        return nm != null && nm.isVelvetRoom;
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>Press one of the strip's own arrows, through its button, as a click would.</summary>
    internal static bool Press(int by)
    {
        var button = by < 0 ? _left : _right;
        if (button == null || !Up) return false;
        button.onClick.Invoke();
        return true;
    }

    /// <summary>Where the strip is on screen, in pixels, and what it says.</summary>
    internal static string Describe()
    {
        if (_strip == null) return "no strip in this lobby";
        if (!Up) return "strip built, not shown";

        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        _strip.GetWorldCorners(corners);
        return $"strip shown at x {corners[0].x:0}..{corners[2].x:0}, y {corners[0].y:0}..{corners[2].y:0} " +
               $"of {Screen.width}x{Screen.height}, saying '{_name.text}'";
    }
}
