using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// The awards on the win screen, on every copy at the table: a short list down the left of the
/// screen, which the game's win screen leaves empty, built from the rows of the game's own
/// Liar's Poker showdown table so it looks like the game's. The host works them out
/// (<see cref="MatchStats"/>) and sends them over <see cref="ModNet"/> just before the win screen
/// opens; the host's own copy gets the same message the same way, so every screen shows the same.
///
/// The message names seats, not people. Each copy puts its own name to each seat, as plain text
/// in a field with rich text off, so a name can never become markup. The panel is the last child
/// of the game's own canvas: on top of everything the win screen shows, and gone with the scene
/// when the table goes back to the lobby, with nothing here to hide it.
///
/// Nothing arrives at a copy without the channel, which keeps the game's own win screen.
/// </summary>
internal static class AwardPanel
{
    // Every word a player sees from this class: the heading, then each award's title and its
    // line for one and for more ({0} is the number). Short on purpose: a row has room for few.
    private const string Heading = "AWARDS";
    private const string SeatName = "SEAT {0}";
    private static readonly Dictionary<MatchStats.Award, (string Title, string One, string Many)> Words = new()
    {
        [MatchStats.Award.BestLiar]        = ("BEST LIAR", "{0} lie never caught", "{0} lies never caught"),
        [MatchStats.Award.WorstBluffer]    = ("WORST BLUFFER", "caught lying once", "caught lying {0} times"),
        [MatchStats.Award.LieDetector]     = ("LIE DETECTOR", "{0} liar caught", "{0} liars caught"),
        [MatchStats.Award.CriedWolf]       = ("CRIED WOLF", "{0} wrong call", "{0} wrong calls"),
        [MatchStats.Award.IronNerves]      = ("NERVES OF STEEL", "{0} shot survived", "{0} shots survived"),
        [MatchStats.Award.Luckiest]        = ("LUCKIEST", "lived through a 1 in {0}", "lived through a 1 in {0}"),
        [MatchStats.Award.Honest]          = ("HONEST TO A FAULT", "{0} throw, not one lie", "{0} throws, not one lie"),
        [MatchStats.Award.DevilsFavourite] = ("DEVIL'S FAVOURITE", "spared by the devil once", "spared by the devil {0} times"),
        [MatchStats.Award.SoldOut]         = ("SOLD OUT", "{0} devil card played", "{0} devil cards played"),
        [MatchStats.Award.Sharpshooter]    = ("SHARPSHOOTER", "{0} kill", "{0} kills"),
        [MatchStats.Award.AgentOfChaos]    = ("AGENT OF CHAOS", "{0} chaos card", "{0} chaos cards"),
        [MatchStats.Award.SpotOn]          = ("SPOT ON", "{0} perfect call", "{0} perfect calls"),
        [MatchStats.Award.HighRoller]      = ("HIGH ROLLER", "bid {0} dice", "bid {0} dice"),
        [MatchStats.Award.CardShark]       = ("CARD SHARK", "{0} hand won", "{0} hands won"),
        [MatchStats.Award.AllIn]           = ("ALL-IN MANIAC", "all in once", "all in {0} times"),
        [MatchStats.Award.LuckyShot]       = ("LUCKIEST", "lived with {0} of 8 loaded", "lived with {0} of 8 loaded"),
        [MatchStats.Award.BestHand]        = ("BEST HAND", "{0}", "{0}"),
        [MatchStats.Award.Coward]          = ("COWARD", "folded once", "folded {0} times"),
        [MatchStats.Award.Swapper]         = ("SWAPPER", "{0} card swapped", "{0} cards swapped"),
        [MatchStats.Award.DeadSpin]        = ("DEAD SPIN SURVIVOR", "{0} dead spin lived", "{0} dead spins lived"),
        [MatchStats.Award.SlotJunkie]      = ("SLOT JUNKIE", "{0} spin", "{0} spins"),
        [MatchStats.Award.Executioner]     = ("EXECUTIONER", "{0} execution", "{0} executions"),
    };

    /// <summary>Liar's Poker's hands, best last, as the game numbers them.</summary>
    private static readonly string[] Hands =
    {
        "HIGH CARD", "ONE PAIR", "TWO PAIR", "THREE OF A KIND", "STRAIGHT", "FLUSH", "FULL HOUSE",
        "FOUR OF A KIND", "STRAIGHT FLUSH", "ROYAL FLUSH",
    };

    // Where it goes, in the canvas's 1920x1080 units: the left of the screen, which the win screen
    // leaves empty, at about two thirds of the showdown table's size.
    private const float Size = 0.62f;
    private const float Across = -470f;
    private const float Down = 300f;
    private const float RowStep = 104f;
    private const float HeadingSize = 54f;
    private const float TextSize = 34f;

    // Each row's three columns, in the showdown row's own units: the name where the game puts
    // the name, then the title and the number where its cards, hand and WON stamp were.
    private static readonly Vector2 NameAt = new(-402f, 7f), NameSize = new(374f, 50f);
    private static readonly Vector2 TitleAt = new(-20f, 7f), TitleSize = new(380f, 50f);
    private static readonly Vector2 NumberAt = new(390f, 7f), NumberSize = new(420f, 50f);

    /// <summary>The cream the game writes scores in, for the titles.</summary>
    private static readonly Color TitleColour = new(250f / 255f, 230f / 255f, 162f / 255f, 1f);

    private static GameObject _shown;
    private static int _complaints;

    // Each text on the panel and how to say it, so a change of language while it is up says it
    // all again, and the language it was last said in.
    private static readonly List<(TextMeshProUGUI Text, Func<string> Say)> _texts = new();
    private static int _saidIn;

    /// <summary>At load, on every copy.</summary>
    internal static void Install() => ModNet.On(ModKind.Awards, Received);

    private static void Received(int from, ModReader body)
    {
        if (from != ModNet.FromHost || RankedGuard.Near) return;

        byte layout = body.U8();
        if (layout != MatchStats.Layout)
        {
            Complain($"awards in layout {layout} - this build reads {MatchStats.Layout}, so the game's own win screen stays");
            return;
        }

        var mode = (MatchStats.Mode)body.U8();
        int count = Math.Min((int)body.U8(), MatchStats.MostAwards);
        var list = new List<(MatchStats.Award Id, int Seat, int Value)>();
        for (int i = 0; i < count; i++)
        {
            var id = (MatchStats.Award)body.U8();
            int seat = body.U8();
            int value = body.I32();
            if (body.Failed) break;
            if (Words.ContainsKey(id)) list.Add((id, seat, value));   // one this build does not know is left out
        }

        if (body.Failed) { Complain("the awards arrived cut short - not shown"); return; }
        Show(mode, list);
    }

    /// <summary>Put the panel up on this copy, replacing any before it.</summary>
    internal static void Show(MatchStats.Mode mode, List<(MatchStats.Award Id, int Seat, int Value)> list)
    {
        try
        {
            if (_shown != null) { UnityEngine.Object.Destroy(_shown); _shown = null; }
            _texts.Clear();
            if (list.Count == 0) return;

            var m = Manager.Instance;
            var canvas = m != null && m.WinMenu != null ? m.WinMenu.transform.parent : null;
            var stats = m != null && m.TexasGame != null ? m.TexasGame.WinStats : null;
            var source = stats != null && stats.Count > 0 ? stats[0] : null;
            if (canvas == null || source == null)
            {
                Complain("no win screen or showdown row to build the awards from - not shown");
                return;
            }

            _shown = Build(canvas, source, list);
            if (_shown != null)
                Plugin.Log.LogInfo($"[awards] shown for {mode}, {MatchStats.Describe(list)}");
        }
        catch (Exception e) { Complain($"could not show the awards: {e.Message}"); }
    }

    private static GameObject Build(Transform canvas, TexasWinStat source, List<(MatchStats.Award Id, int Seat, int Value)> list)
    {
        var go = new GameObject("LB8P_Awards") { layer = canvas.gameObject.layer };
        go.SetActive(false);
        var box = go.AddComponent<RectTransform>();
        box.SetParent(canvas, false);
        box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 0.5f);
        box.sizeDelta = new Vector2(1259f, 100f);
        box.anchoredPosition = new Vector2(Across, Down);
        box.localScale = Vector3.one * Size;

        var nameSource = source.NameText;
        if (nameSource == null) { UnityEngine.Object.Destroy(go); Complain("the showdown row has no name to copy"); return null; }

        var heading = Text(nameSource.gameObject, box, "heading", new Vector2(0f, 96f), new Vector2(1259f, 70f), HeadingSize);
        if (heading == null) { UnityEngine.Object.Destroy(go); return null; }
        Say(heading, () => LiarsBar8P.Words.T(Heading));
        heading.color = TitleColour;
        heading.alignment = TextAlignmentOptions.Center;

        for (int i = 0; i < list.Count; i++)
        {
            var (id, seat, value) = list[i];
            var row = UiClone.Make(source.gameObject, box, $"award{i}", null, "TexasWinStat");
            if (row == null) { UnityEngine.Object.Destroy(go); Complain("could not copy a showdown row"); return null; }

            Quiet(row);
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, -i * RowStep);
            rt.localScale = Vector3.one;

            // The cards, the hand and the stamp go; the name and the line under it stay.
            Off(Twin(row.transform, source.transform, source.Card1Image));
            Off(Twin(row.transform, source.transform, source.Card2Image));
            Off(Twin(row.transform, source.transform, source.RankImage));
            Off(Twin(row.transform, source.transform, source.WinnerImage));

            var name = Twin(row.transform, source.transform, nameSource)?.GetComponent<TextMeshProUGUI>();
            if (name == null) continue;
            Place(name, NameAt, NameSize, TextSize, TextAlignmentOptions.Left);
            Opaque(name);
            Say(name, () => NameOf(seat));

            var title = Text(name.gameObject, row.transform, "title", TitleAt, TitleSize, TextSize);
            var number = Text(name.gameObject, row.transform, "number", NumberAt, NumberSize, TextSize * 0.8f);
            if (title != null) { Say(title, () => LiarsBar8P.Words.T(Words[id].Title)); title.color = TitleColour; }
            if (number != null) { Opaque(number); Say(number, () => Number(id, value)); }
        }

        foreach (var g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
        box.SetAsLastSibling();
        go.SetActive(true);
        _saidIn = LiarsBar8P.Words.Generation;
        return go;
    }

    private static void Say(TextMeshProUGUI text, Func<string> say)
    {
        text.text = say();
        _texts.Add((text, say));
    }

    /// <summary>From <see cref="ModTicker"/>: the panel says it all again when the game's language changes while it is up.</summary>
    internal static void Tick()
    {
        if (_shown == null) { _texts.Clear(); return; }
        if (_saidIn == LiarsBar8P.Words.Generation) return;
        _saidIn = LiarsBar8P.Words.Generation;
        foreach (var (text, say) in _texts)
            if (text != null) text.text = say();
    }

    private static string Number(MatchStats.Award id, int value)
    {
        var (_, one, many) = Words[id];
        if (id == MatchStats.Award.BestHand)
            return value >= 0 && value < Hands.Length ? LiarsBar8P.Words.T(Hands[value]) : "";
        return LiarsBar8P.Words.N(one, many, value);
    }

    /// <summary>A seat's name as this copy knows it, plain, or the seat number when it knows none.</summary>
    internal static string NameOf(int seat)
    {
        try
        {
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerStats>())
                if (p != null && p.Slot == seat && !string.IsNullOrEmpty(p.PlayerName)) return ModReader.Plain(p.PlayerName, 24);

            // A bot has no seat object on a client, but its lobby player came along.
            var nm = Mirror.NetworkManager.singleton != null ? Mirror.NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
            if (nm != null && nm.GamePlayers != null)
                foreach (var g in nm.GamePlayers)
                    if (g != null && g.InGameSlot == seat && !string.IsNullOrEmpty(g.PlayerName)) return ModReader.Plain(g.PlayerName, 24);
        }
        catch { }
        return LiarsBar8P.Words.F(SeatName, seat + 1);
    }

    // ------------------------------------------------------------------ parts

    /// <summary>A copy of one of the game's texts, plain, in the same font, at a place of its own.</summary>
    internal static TextMeshProUGUI Text(GameObject like, Transform parent, string name, Vector2 at, Vector2 size, float fontSize)
    {
        var copy = UiClone.Make(like, parent, name);
        var text = copy != null ? copy.GetComponent<TextMeshProUGUI>() : null;
        if (text == null) return null;
        Place(text, at, size, fontSize, TextAlignmentOptions.Left);
        return text;
    }

    private static void Place(TextMeshProUGUI text, Vector2 at, Vector2 size, float fontSize, TextAlignmentOptions align)
    {
        var rt = text.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = at;
        rt.localScale = Vector3.one;
        text.richText = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.enableAutoSizing = true;
        text.fontSizeMax = fontSize;
        text.fontSizeMin = fontSize * 0.5f;
        text.alignment = align;
        text.raycastTarget = false;
    }

    /// <summary>
    /// Fully drawn. By the end of a Liar's Poker match the game has played its showdown rows out of
    /// sight, and a copy made then kept that: the titles, whose colour is set here, showed, and the
    /// names and numbers did not.
    /// </summary>
    private static void Opaque(TextMeshProUGUI text)
    {
        var c = text.color;
        text.color = new Color(c.r, c.g, c.b, 1f);
        text.canvasRenderer.SetAlpha(1f);
    }

    /// <summary>The showdown row's own animation and its WON sound stay behind.</summary>
    internal static void Quiet(GameObject go)
    {
        foreach (var a in go.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(a);
        foreach (var a in go.GetComponentsInChildren<Animation>(true)) UnityEngine.Object.DestroyImmediate(a);
        foreach (var a in go.GetComponentsInChildren<AudioSource>(true)) UnityEngine.Object.DestroyImmediate(a);
    }

    /// <summary>The part of a copy that sits where <paramref name="part"/> sits in <paramref name="original"/>.</summary>
    internal static Transform Twin(Transform copy, Transform original, Component part)
    {
        if (copy == null || original == null || part == null) return null;
        var path = new List<int>();
        for (var t = part.transform; t != null && t != original; t = t.parent) path.Insert(0, t.GetSiblingIndex());

        var at = copy;
        foreach (int index in path)
        {
            if (index >= at.childCount) return null;
            at = at.GetChild(index);
        }
        return at;
    }

    private static void Off(Transform t)
    {
        if (t != null) t.gameObject.SetActive(false);
    }

    private static void Complain(string what)
    {
        if (_complaints >= 10) return;
        _complaints++;
        Plugin.Log.LogWarning($"[awards] {what}");
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>A made-up set of awards for whoever is seated here, to see the panel without playing a match out.</summary>
    internal static void ShowMadeUp()
    {
        var seats = new List<int>();
        foreach (var p in UnityEngine.Object.FindObjectsOfType<PlayerStats>())
            if (p != null) seats.Add(p.Slot);
        if (seats.Count == 0) seats.Add(0);
        seats.Sort();

        int S(int i) => seats[i % seats.Count];
        Show(MatchStats.Mode.Deck, new List<(MatchStats.Award, int, int)>
        {
            (MatchStats.Award.BestLiar, S(0), 4),
            (MatchStats.Award.LieDetector, S(1), 3),
            (MatchStats.Award.IronNerves, S(2), 5),
            (MatchStats.Award.Luckiest, S(1), 2),
            (MatchStats.Award.Honest, S(0), 6),
        });
    }

    /// <summary>Where the panel is on screen, in pixels.</summary>
    internal static string Describe()
    {
        if (_shown == null) return "no awards on screen";
        var rt = _shown.GetComponent<RectTransform>();
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        rt.GetWorldCorners(corners);
        return $"awards up, {_shown.transform.childCount} parts, row box at x {corners[0].x:0}..{corners[2].x:0} " +
               $"y {corners[0].y:0}..{corners[2].y:0} of {Screen.width}x{Screen.height}, last child: {_shown.transform.GetSiblingIndex() == _shown.transform.parent.childCount - 1}";
    }
}
