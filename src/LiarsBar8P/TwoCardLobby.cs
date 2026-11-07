using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// Two-Card Poker in the lobby: the Poker tab's own arrows, its name on the plate, and a rules page
/// behind the tab's help button, all made from the lobby's own pieces.
///
/// The arrows are a copy of the deck tab's, put round the Poker tab's plate at the plate's scale,
/// so the two tabs with a choice look alike; like the game's own deck arrows, only the host has
/// them, and they step between LIAR'S POKER and TWO-CARD POKER. Each table's name is written on a
/// copy of the game's plate laid over it, across the plate's black band: the plate itself is a
/// picture that reads TEXAS, the name the released Liar's Poker had before it came out. The
/// released game keeps its own help button and rules page. Two-Card Poker also covers the help
/// button with a copy that slides in a copy of the game's own Poker rules page. The page keeps the
/// game's frame, pattern, picture, slide, close button and sounds; its five boxes and its title get
/// this table's words, in boxes drawn over the game's own in their colours. The game's page has the
/// same layout in every language.
///
/// Built for each lobby screen, on every copy, because a client's lobby shows the table the host
/// chose (<see cref="TwoCardPoker.Shown"/>). The LIAR'S POKER plate only changes what this player
/// sees, so it is up at any table, modded or not. Anything that fails leaves the Poker tab as the
/// game has it, TEXAS plate and all, and the arrows off, so nobody can choose a table they could
/// not be shown.
/// </summary>
internal static class TwoCardLobby
{
    // ------------------------------------------------------------------ every word a player sees

    // The released game's name, for its plate. The game has it only inside its loading tips, not as
    // a text anything can read, so the table of words carries it, in the tips' own wording.
    private const string ReleasedName = "LIAR'S POKER";

    // Two-Card Poker's name is TwoCardPoker.Name. The rules, one for each box of the page, in the
    // order they are read. They come from the game's own code for the table, PokerGamePlay and
    // PokerGamePlayManager.
    private static readonly string[] Rules =
    {
        "Two cards each, from a deck of 21: two Aces, two Kings, two Queens, two Fives, four Fours, four Threes, four Twos and a Joker.",
        "Ace 12, King 7, Queen 6, the rest their number. Your hand is its two cards added up. A pair counts its value squared, so two Aces make 144, and the Joker pairs with any card.",
        "Your turn: E swaps a card for one from the deck, the old one face up. F passes, once a round. From the second lap, C challenges: show one card and stake your hand.",
        "The player before you answers. X calls it a lie: both hands show, the higher wins, the challenger on a tie. C challenges too. Once two have challenged, S shows all their hands: the highest wins.",
        "The winner aims the shotgun with A and D, and it fires when time runs out: three of its six shells are live. Two left, C faces off. Hold Space to see your cards, Tab for every hand's worth.",
    };

    // ------------------------------------------------------------------ the lobby's pieces

    private const string Panel = "GameMode Button/Poker";
    private const string Plate = "GameMode Button/Poker/Image";
    private const string HelpButton = "GameMode Button/Poker/HelpPoker";
    private const string DeckArrows = "GameMode Button/DeckGameModes/DeckModeButtons";
    private const string RightArrow = "DeckRightArrow";
    private const string LeftArrow = "Button (1)";
    private const string LeftArrowPicture = "DeckLeftArrow";
    private const string Label = "Alt/LobbyID (1)";
    private const string GamePage = "TutorialPoker";
    private const string PageClose = "Image";

    // The game's triggers on its rules pages' animators.
    private const string ShowTrigger = "Show";
    private const string HideTrigger = "NoShow";

    // The deck tab's arrows as the scene lays them out. The deck tab's own animation folds them
    // in behind its plate whenever that tab is not showing, so a copy is set out again here.
    private static readonly Vector2 RightArrowAt = new(106.4f, -1f);
    private static readonly Vector2 LeftArrowAt = new(0f, -1.2f);

    // The plate (180 x 46): the black band inside the frame, where the name goes, leaving some of
    // the band's pattern showing at each end.
    private const float NameWide = 132f;
    private const float NameHigh = 29f;
    private const float NameSize = 22f;

    // The game's rules page (1162 x 658), in its own units from its top left: five boxes down the
    // left, and the table's title at the bottom right under the picture.
    private const float BoxLeft = 67f;
    private const float BoxWide = 665f;
    private const float BoxHigh = 98f;
    private static readonly float[] BoxTops = { 51f, 163f, 274.5f, 385f, 498f };
    private const float BoxEdge = 6f;
    private const float RulesSize = 21f;
    private const float RulesMin = 13f;
    private static readonly Rect TitleAt = new(828f, 490f, 240f, 116f);
    private const float TitleSize = 44f;

    // The game's own colours, read off its plate and its rules page.
    private static readonly Color Band = new(0.01f, 0.01f, 0.02f, 1f);
    private static readonly Color PlateWords = new(1f, 0.945f, 0.588f, 1f);
    private static readonly Color BoxFill = new(0.341f, 0.306f, 0.737f, 1f);
    private static readonly Color BoxLine = new(0.02f, 0.02f, 0.05f, 1f);
    private static readonly Color PageWords = new(0.98f, 0.97f, 0.94f, 1f);
    private static readonly Color TitleYellow = new(1f, 1f, 0.518f, 1f);

    private static RectTransform _arrows, _plate, _released, _help, _page;
    private static Button _left, _right, _open, _close;
    private static TextMeshProUGUI _name, _releasedName, _title;
    private static readonly TextMeshProUGUI[] _rules = new TextMeshProUGUI[5];
    private static Animator _slide;
    private static AudioSource _openSound, _closeSound;
    private static bool _isOpen;
    private static int _wordsFor = -1;
    private static string _said;

    /// <summary>Whether the host's arrows are on screen now, for the test driver.</summary>
    internal static bool ArrowsUp => _arrows != null && _arrows.gameObject.activeInHierarchy;

    /// <summary>This lobby has the whole table to show: arrows, both plates, help button and rules page.</summary>
    internal static bool Built => _arrows != null && _plate != null && _released != null && _help != null && _page != null;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void Started(LobbyController __instance)
    {
        try { Make(__instance); }
        catch (Exception e)
        {
            Drop();
            Plugin.Log.LogWarning($"[twocard] no Two-Card Poker on this lobby's Poker tab: {e.Message}");
        }
    }

    /// <summary>
    /// Everything built so far goes. Each piece is kept in its field the moment it is copied, so a
    /// piece that failed half way is taken away too, and is hidden at once rather than at the end
    /// of the frame.
    /// </summary>
    private static void Drop()
    {
        foreach (var t in new[] { _arrows, _plate, _released, _help, _page })
        {
            if (t == null) continue;
            t.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(t.gameObject);
        }
        _arrows = _plate = _released = _help = _page = null;
    }

    /// <summary>A copy's box, or the copy destroyed and null if it has none.</summary>
    private static RectTransform BoxOf(GameObject copy)
    {
        var box = copy.GetComponent<RectTransform>();
        if (box == null) UnityEngine.Object.Destroy(copy);
        return box;
    }

    private static void Make(LobbyController room)
    {
        _arrows = _plate = _released = _help = _page = null;
        _said = null;
        _wordsFor = -1;
        _isOpen = false;
        if (room == null || RankedGuard.Near) return;

        var label = room.LoobyIdText;
        var canvas = label != null && label.canvas != null ? label.canvas.rootCanvas : null;
        var root = canvas != null ? canvas.transform : null;
        var panel = root != null ? root.Find(Panel) : null;
        var plate = root != null ? root.Find(Plate) : null;
        var help = root != null ? root.Find(HelpButton) : null;
        var arrows = root != null ? root.Find(DeckArrows) : null;
        var words = root != null ? root.Find(Label) : null;
        var page = root != null ? root.Find(GamePage) : null;
        if (panel == null || plate == null || help == null || arrows == null || words == null || page == null)
        {
            Plugin.Log.LogWarning("[twocard] the lobby's Poker tab is not laid out as expected - no Two-Card Poker here");
            return;
        }

        if (!MakeArrows(arrows, plate, panel) || !MakePlates(plate, panel, words) || !MakePage(page, root, words) ||
            !MakeHelp(help, panel))
        {
            Drop();
            return;
        }

        _openSound = SoundOf(help.GetComponent<Button>());
        var close = page.Find(PageClose);
        _closeSound = SoundOf(close != null ? close.GetComponent<Button>() : null);
    }

    /// <summary>The deck tab's arrows, round the Poker tab's plate as the deck's are round its own.</summary>
    private static bool MakeArrows(Transform like, Transform plate, Transform panel)
    {
        var copy = UiClone.Make(like.gameObject, panel, "LB8P_TwoCardArrows");
        if (copy == null) return false;

        var box = BoxOf(copy);
        if (box == null) return false;
        _arrows = box;
        var at = plate.GetComponent<RectTransform>();
        box.anchorMin = at.anchorMin;
        box.anchorMax = at.anchorMax;
        box.pivot = at.pivot;
        box.anchoredPosition = at.anchoredPosition;
        box.localScale = at.localScale;
        box.SetSiblingIndex(plate.GetSiblingIndex() + 1);

        var right = copy.transform.Find(RightArrow);
        var left = copy.transform.Find(LeftArrow);
        var leftPicture = left != null ? left.Find(LeftArrowPicture) : null;
        _right = right != null ? right.GetComponent<Button>() : null;
        _left = left != null ? left.GetComponent<Button>() : null;
        if (_right == null || _left == null || leftPicture == null) return false;

        right.GetComponent<RectTransform>().anchoredPosition = RightArrowAt;
        leftPicture.GetComponent<RectTransform>().anchoredPosition = LeftArrowAt;

        _right.onClick.AddListener((Action)(() => Pressed("right arrow")));
        _left.onClick.AddListener((Action)(() => Pressed("left arrow")));

        copy.SetActive(false);
        return true;
    }

    /// <summary>
    /// A copy of the plate on top of it for each table, its band blacked out under that table's
    /// name: the released game's while it is the one shown, Two-Card Poker's while that one is.
    /// </summary>
    private static bool MakePlates(Transform plate, Transform panel, Transform words)
    {
        _released = MakePlate(plate, panel, words, "LB8P_LiarsPokerPlate", out _releasedName);
        if (_released == null) return false;
        _plate = MakePlate(plate, panel, words, "LB8P_TwoCardPlate", out _name);
        return _plate != null;
    }

    private static RectTransform MakePlate(Transform plate, Transform panel, Transform words, string name, out TextMeshProUGUI text)
    {
        text = null;
        var copy = UiClone.Make(plate.gameObject, panel, name);
        if (copy == null) return null;

        var box = BoxOf(copy);
        if (box == null) return null;
        copy.SetActive(false);
        Same(box, plate.GetComponent<RectTransform>());
        box.SetSiblingIndex(plate.GetSiblingIndex() + 1);
        foreach (var part in copy.GetComponentsInChildren<Graphic>(true)) part.raycastTarget = false;

        var size = box.sizeDelta;
        var band = Square(box, "band", (size.x - NameWide) / 2f, (size.y - NameHigh) / 2f, NameWide, NameHigh, Band);
        text = Text(words, band, "name", NameSize);
        if (text == null)
        {
            UnityEngine.Object.Destroy(copy);
            return null;
        }
        Fill(text.rectTransform, 3f);
        text.color = PlateWords;
        text.fontStyle = FontStyles.Bold;
        text.enableAutoSizing = true;
        text.fontSizeMax = NameSize;
        text.fontSizeMin = 10f;
        return box;
    }

    /// <summary>
    /// A copy of the game's Poker rules page: its picture, its slide and its close. Its boxes and
    /// title are drawn over with this table's. The copy's language swap is left behind, or it would
    /// put the released rules back the moment the language is read.
    /// </summary>
    private static bool MakePage(Transform page, Transform root, Transform words)
    {
        var copy = UiClone.Make(page.gameObject, root, "LB8P_TwoCardRules", null, "LocalizeSpriteEvent");
        if (copy == null) return false;

        var box = BoxOf(copy);
        if (box == null) return false;
        _page = box;
        Same(box, page.GetComponent<RectTransform>());
        box.SetSiblingIndex(page.GetSiblingIndex() + 1);
        _slide = copy.GetComponent<Animator>();

        var close = copy.transform.Find(PageClose);
        _close = close != null ? close.GetComponent<Button>() : null;
        if (_slide == null || _close == null) return false;
        _close.onClick.AddListener((Action)(() => Slide(false)));

        for (int i = 0; i < _rules.Length; i++)
        {
            _rules[i] = Boxed(words, box, $"rule{i + 1}", BoxLeft, BoxTops[i], BoxWide, BoxHigh, RulesSize);
            if (_rules[i] == null) return false;
            _rules[i].color = PageWords;
            _rules[i].fontSizeMin = RulesMin;
        }

        _title = Boxed(words, box, "title", TitleAt.x, TitleAt.y, TitleAt.width, TitleAt.height, TitleSize);
        if (_title == null) return false;
        _title.color = TitleYellow;
        _title.fontStyle = FontStyles.Bold;
        _title.fontSizeMin = 18f;

        // Drawn last, so nothing of this table's ever sits over the way out.
        close.SetAsLastSibling();
        return true;
    }

    /// <summary>One of the page's boxes: a black line round the game's blue, and words in the middle.</summary>
    private static TextMeshProUGUI Boxed(Transform words, RectTransform page, string name, float x, float y, float w, float h, float size)
    {
        var line = Square(page, name, x, y, w, h, BoxLine);
        var fill = Square(line, "fill", BoxEdge, BoxEdge, w - 2f * BoxEdge, h - 2f * BoxEdge, BoxFill);
        var text = Text(words, fill, "words", size);
        if (text == null) return null;

        Fill(text.rectTransform, 10f);
        text.enableWordWrapping = true;
        text.enableAutoSizing = true;
        text.fontSizeMax = size;
        return text;
    }

    /// <summary>A plain rectangle of one colour, placed from its parent's top left.</summary>
    private static RectTransform Square(Transform parent, string name, float x, float y, float w, float h, Color colour)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        var box = go.AddComponent<RectTransform>();
        box.SetParent(parent, false);
        box.anchorMin = box.anchorMax = box.pivot = new Vector2(0f, 1f);
        box.sizeDelta = new Vector2(w, h);
        box.anchoredPosition = new Vector2(x, -y);
        var image = go.AddComponent<Image>();
        image.color = colour;
        image.raycastTarget = false;
        return box;
    }

    private static void Fill(RectTransform box, float inset)
    {
        box.anchorMin = Vector2.zero;
        box.anchorMax = Vector2.one;
        box.pivot = new Vector2(0.5f, 0.5f);
        box.sizeDelta = new Vector2(-2f * inset, -2f * inset);
        box.anchoredPosition = Vector2.zero;
    }

    /// <summary>A copy of the help button on top of the game's, opening this table's page instead.</summary>
    private static bool MakeHelp(Transform help, Transform panel)
    {
        var copy = UiClone.Make(help.gameObject, panel, "LB8P_TwoCardHelp", () => Slide(true));
        if (copy == null) return false;

        var box = BoxOf(copy);
        if (box == null) return false;
        _help = box;
        Same(box, help.GetComponent<RectTransform>());
        box.SetSiblingIndex(help.GetSiblingIndex() + 1);
        _open = copy.GetComponent<Button>();
        if (_open == null) return false;

        copy.SetActive(false);
        return true;
    }

    private static void Same(RectTransform box, RectTransform like)
    {
        box.anchorMin = like.anchorMin;
        box.anchorMax = like.anchorMax;
        box.pivot = like.pivot;
        box.sizeDelta = like.sizeDelta;
        box.anchoredPosition = like.anchoredPosition;
        box.localScale = like.localScale;
        box.localRotation = like.localRotation;
    }

    private static TextMeshProUGUI Text(Transform like, Transform parent, string name, float size)
    {
        var copy = UiClone.Make(like.gameObject, parent, name);
        var text = copy != null ? copy.GetComponent<TextMeshProUGUI>() : null;
        if (text == null) return null;

        text.fontSize = size;
        text.alignment = TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.richText = false;
        text.raycastTarget = false;
        return text;
    }

    /// <summary>The sound a game button plays, read from its own saved call, so the copies sound the same.</summary>
    private static AudioSource SoundOf(Button button)
    {
        try
        {
            var calls = button != null && button.onClick != null && button.onClick.m_PersistentCalls != null
                ? button.onClick.m_PersistentCalls.m_Calls : null;
            if (calls == null) return null;
            foreach (var call in calls)
            {
                var source = call != null && call.m_Target != null ? call.m_Target.TryCast<AudioSource>() : null;
                if (source != null) return source;
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not find a button's sound: {e.Message}"); }
        return null;
    }

    // ------------------------------------------------------------------ from ModTicker

    /// <summary>Four times a second: the table shown, the arrows for the host, and the words.</summary>
    internal static void Tick()
    {
        if (_arrows == null) return;

        var room = LobbyController.Instance;
        bool lobby = room != null && Manager.Instance == null && !RankedGuard.Near;
        bool shown = lobby && TwoCardPoker.Shown;
        bool arrows = lobby && TwoCardPoker.CanStep(room);

        Show(_arrows, arrows);
        Show(_released, lobby && !shown);
        Show(_plate, shown);
        Show(_help, shown);
        if (!shown && _isOpen) Slide(false);

        // The words follow a change of language made in the settings while the lobby is up.
        if (_wordsFor != Words.Generation)
        {
            _wordsFor = Words.Generation;
            _releasedName.text = Words.T(ReleasedName);
            _name.text = Words.T(TwoCardPoker.Name);
            _title.text = Words.T(TwoCardPoker.Name);
            for (int i = 0; i < _rules.Length; i++) _rules[i].text = Words.T(Rules[i]);
        }

        string said = $"{(shown ? "Two-Card Poker" : "Liar's Poker")}{(arrows ? ", host arrows" : "")}";
        if (said != _said && room != null && room.Mode == CustomNetworkManager.GameMode.LiarsTexas)
        {
            _said = said;
            Plugin.Log.LogInfo($"[twocard] Poker tab shows {said}");
        }
    }

    private static void Show(RectTransform box, bool on)
    {
        if (box != null && box.gameObject.activeSelf != on) box.gameObject.SetActive(on);
    }

    /// <summary>The page in or out, the way the game's help button and close do it, with their sounds.</summary>
    private static void Slide(bool open)
    {
        try
        {
            if (_slide == null) return;
            _slide.SetTrigger(open ? ShowTrigger : HideTrigger);
            _isOpen = open;
            var sound = open ? _openSound : _closeSound;
            if (sound != null) sound.Play();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not move the rules page: {e.Message}"); }
    }

    private static void Pressed(string how)
    {
        try { if (TwoCardPoker.Step(how)) StepSound(); }
        catch (Exception e) { Plugin.Log.LogWarning($"[twocard] could not step the Poker tab: {e.Message}"); }
    }

    /// <summary>The sound the game makes when the bar picker moves, as the house rules arrows make.</summary>
    internal static void StepSound()
    {
        var sound = UnityEngine.Object.FindObjectOfType<SkinChangerLobby>();
        if (sound != null && sound.audioSource != null) sound.audioSource.Play();
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>Press one of the arrows through its own Button, as a click would.</summary>
    internal static bool Press(int by)
    {
        var button = by < 0 ? _left : _right;
        if (button == null || !ArrowsUp) return false;
        button.onClick.Invoke();
        return true;
    }

    /// <summary>Open the page through its help button, or close it through its own close.</summary>
    internal static bool PressHelp(bool open)
    {
        var button = open ? _open : _close;
        if (button == null || (open && !_help.gameObject.activeInHierarchy)) return false;
        button.onClick.Invoke();
        return true;
    }

    internal static string Describe()
    {
        if (_arrows == null) return "nothing built on this lobby";
        return $"arrows {(ArrowsUp ? "up" : "off")}, plate {(_plate.gameObject.activeInHierarchy ? "up" : "off")}, " +
               $"released plate {(_released.gameObject.activeInHierarchy ? "up" : "off")} at size {_releasedName.fontSize:0.0} " +
               $"('{_releasedName.text}' in {(_releasedName.font != null ? _releasedName.font.name : "no font")}), " +
               $"help {(_help.gameObject.activeInHierarchy ? "up" : "off")}, page {(_isOpen ? "open" : "closed")} at y {_page.anchoredPosition.y:0}";
    }
}
