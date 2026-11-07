using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// Hold Tab during a match for a board of everybody at the table: name, still in or out, and
/// the two numbers that matter in that game. In Liar's Deck, cards in hand and shots survived;
/// in the Chaos deck, cards and the chamber; in Liar's Dice, lives and the bid each player made;
/// in Liar's Poker, bullets loaded and whether they folded or went all in. At eight seats there
/// is otherwise no way to see that without looking round the whole table.
///
/// Built from the game's own parts, so it looks like the game's: the name plates of the Liar's
/// Slots leaderboard, one per seat under a heading in the same font, copied with
/// <see cref="UiClone"/> and stripped of what drives them. It lives in the match's own canvas and
/// goes with the scene. (The frame of Liar's Poker's hands chart was tried as a backing and is
/// see-through without the chart in it.)
///
/// Nearly everything on it is a synced value every copy already holds, read from the seats this
/// copy can see for itself: the game's own player list is the host's, and is empty on a client
/// until something fills it. The one exception is how many cards each player holds. Only the host
/// knows that: a client holds its own hand, and the card models in the other players' hands are
/// not a count (measured: they stayed at five after a throw, on the host as on a client). Nor is
/// the host's own list of a seat's cards, which is the deal and is never shortened by a throw, so
/// the host counts each hand itself (<see cref="HandHooks"/>). It says, over <see cref="ModNet"/>,
/// whenever a count changes, and a copy without the channel shows a dash there. A count is no secret: everybody saw the throws. A seat whose player leaves
/// stays on the board, marked as gone. Never a card face, a die or anything else that is
/// somebody's secret.
///
/// Not in Liar's Slots, whose leaderboard is on screen all the time. In Liar's Poker, Tab also
/// opens the game's own hands chart on the left, so the board sits right of centre there. On a
/// controller it is joystick button 7, the one Liar's Slots shows its hints on, and LB in Liar's
/// Poker, where the game opens its hands chart with it. Only at a modded table
/// (<see cref="Table.Modded"/>), never near ranked, and only while the key is held.
/// </summary>
internal static class MatchBoard
{
    // Every word a player sees from this class: the key hint, the heading and columns of each
    // table, and what a seat can say instead of a number.
    private const string HintWord = "Players";
    private const string SeatName = "SEAT {0}";
    private const string OutWord = "OUT";
    private const string LeftWord = "LEFT";
    private const string FoldWord = "FOLD";
    private const string AllInWord = "ALL IN";
    private const string AimWord = "AIMING";
    private const string NoValue = "-";
    private static readonly Dictionary<Kind, (string Title, string A, string B)> Words = new()
    {
        [Kind.Deck]  = ("LIAR'S DECK", "CARDS", "SHOTS"),
        [Kind.Devil] = ("LIAR'S DECK: DEVIL", "CARDS", "SHOTS"),
        [Kind.Deck2] = ("LIAR'S DECK 2", "CARDS", "SHOTS"),
        [Kind.Chaos] = ("LIAR'S DECK: CHAOS", "CARDS", "CHAMBER"),
        [Kind.Dice]  = ("LIAR'S DICE", "LIVES", "BID"),
        [Kind.Poker] = ("LIAR'S POKER", "BULLETS", "PLAY"),
    };

    private enum Kind { None, Deck, Devil, Deck2, Chaos, Dice, Poker, Slots }

    // Sizes, in the canvas's 1920x1080 units and the copied parts' own.
    private const float BlockWide = 600f, BlockHigh = 760f;
    private const float MostOfHeight = 0.9f, Largest = 0.9f;
    private const float PokerAcross = 330f;
    private const float TitleY = 330f, HeaderY = 272f, FirstRowY = 218f, RowGap = 60f, RowsBottom = -380f;
    private const float RowSize = 1.5f;
    private const float TitleSize = 44f;

    // Inside a copied name plate: where its own centre sits against its backing, and the columns.
    private const float PlateCentre = 36f;
    private static readonly Vector2 SeatAt = new(-126f, 0f), SeatSize = new(30f, 31f);
    private static readonly Vector2 NameAt = new(-6f, 0f), NameSize = new(196f, 31f);
    private static readonly Vector2 AAt = new(128f, 0f), ASize = new(58f, 31f);
    private static readonly Vector2 BAt = new(186f, 0f), BSize = new(58f, 31f);

    private const string HintPath = "GamePlay/Tab_ENG";
    private static readonly Vector2 HintAt = new(-485f, -509f);

    // Inside the hint: the keycap, and on it the controller glyph the game shows instead while a
    // pad is in use. The copy's own glyph is Liar's Poker's LB, so the one put there is Liar's
    // Slots' hints button, joystick button 7, which is the board's button outside Liar's Poker.
    private const string KeycapPath = "Key_Tab", GlyphPath = "Key_Tab/Image";
    private const string PadGlyphPath = "GamePlay/F1_ENG/Key_Tab/Image";

    private sealed class Row
    {
        internal RectTransform Box;
        internal TextMeshProUGUI Seat, Name, A, B;
        internal GameObject Turn, Dead;
        internal Graphic Back;
    }

    private static Manager _for;
    private static Kind _kind;
    private static GameObject _root, _hint;
    private static readonly List<Row> _rows = new();
    private static TextMeshProUGUI _title;
    private static Row _header;
    private static Color _normal = Color.white, _dead = Color.gray, _yours = Color.white, _mine = Color.white, _back = Color.white;

    /// <summary>The cream the game writes Liar's Slots scores in, for the heading and the column names.</summary>
    private static readonly Color Cream = new(250f / 255f, 230f / 255f, 162f / 255f, 1f);
    private static bool _failed, _hintFailed;
    private static float _next;

    // The hint's keycap and glyph, which this class sets now that the game's switcher is off them.
    private static Image _keycap, _glyph;
    private static Sprite _padGlyph;
    private static GamepadManager _pads;
    private static bool _padsLooked;
    private static int _padShown = -1;
    private static float _padNext;

    /// <summary>The language the hint's word was last written in, as <see cref="LiarsBar8P.Words.Generation"/> counts them.</summary>
    private static int _hintWords = -1;

    /// <summary>The seats ever seen this match, so a player who leaves stays on the board.</summary>
    private static readonly Dictionary<int, string> _seen = new();

    /// <summary>Developer test switch: behave as if the key were held.</summary>
    internal static bool Pretend { get; set; }

    /// <summary>Developer test switch: behave as if the game said a controller is in use.</summary>
    internal static bool PretendPad { get; set; }

    /// <summary>Whether the board is on screen now.</summary>
    internal static bool Up => _root != null && _root.activeInHierarchy;

    // ------------------------------------------------------------------ every frame

    /// <summary>From <see cref="ModTicker"/>, every frame: the key, and whether to show.</summary>
    internal static void Frame()
    {
        var m = Manager.Instance;
        if (m == null)
        {
            if (_for != null) Forget();
            return;
        }
        if (!ReferenceEquals(_for, m)) { Forget(); _for = m; }

        var kind = Allowed(m, out bool playing);
        var table = playing ? KindOf(m) : Kind.None;
        Hint(m, table == Kind.Deck || table == Kind.Devil || table == Kind.Deck2 || table == Kind.Chaos || table == Kind.Dice);
        SayHands(table);
        if (_failed) return;

        bool open = kind != Kind.None && kind != Kind.Slots && Held(kind);

        if (open && _root == null && !Make(m)) return;
        if (_root != null && _root.activeSelf != open)
        {
            if (open)
            {
                _root.transform.SetAsLastSibling();
                _next = 0f;
                Place(kind);
            }
            _root.SetActive(open);
        }
        if (open && Time.unscaledTime >= _next)
        {
            _next = Time.unscaledTime + 0.25f;
            Fill(m, kind);
        }
    }

    private static void Forget()
    {
        _for = null;
        _kind = Kind.None;
        _root = null;
        _hint = null;
        _title = null;
        _header = null;
        _rows.Clear();
        _seen.Clear();
        _wasHere.Clear();
        _hands.Clear();
        _handsSaid.Clear();
        _held.Clear();
        _seated.Clear();
        _seatedAt = -999f;
        _failed = false;
        _hintFailed = false;
        _keycap = _glyph = null;
        _padGlyph = null;
        _pads = null;
        _padsLooked = false;
        _padShown = -1;
    }

    /// <summary>
    /// The table being played, if the board may show at it now; None otherwise.
    /// <paramref name="playing"/>: a match of a modded table is running, chat and pause aside.
    /// </summary>
    private static Kind Allowed(Manager m, out bool playing)
    {
        var nm = NetworkManager.singleton;
        playing = m.GameStarted && !m.GameEnd && Table.Modded && !RankedGuard.Near && nm != null && !nm.Matchmaking;
        if (!playing || m.Chatting || m.GamePaused) return Kind.None;
        return KindOf(m);
    }

    /// <summary>
    /// Held, polled every frame rather than caught as a press and a release: a release lost to
    /// alt-tab would leave it stuck open. Shift+Tab is Steam's overlay, so not with Shift held.
    /// </summary>
    private static bool Held(Kind kind)
    {
        if (Pretend && Dev.Enabled) return true;
        if (!Application.isFocused) return false;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return false;
        if (Input.GetKey(KeyCode.Tab)) return true;
        return Input.GetKey(kind == Kind.Poker ? KeyCode.JoystickButton4 : KeyCode.JoystickButton7);
    }

    /// <summary>
    /// Which game, from the seats this copy can see: exactly one gameplay part is fitted to every
    /// player, the one for the game being played. The same test as <see cref="TableHand.Playing"/>,
    /// asked of this copy's own list.
    /// </summary>
    private static Kind KindOf(Manager m)
    {
        if (_kind != Kind.None) return _kind;
        foreach (var p in Seated())
        {
            if (p == null) continue;
            if (p.GetComponent<DeckGameplay>() != null)
            {
                var d = m.DeckGamePlayManager;
                _kind = d == null ? Kind.Deck
                      : d.DeckMode == DeckGamePlayManager.deckmode.Devil ? Kind.Devil
                      : d.DeckMode == DeckGamePlayManager.deckmode.Deck2 ? Kind.Deck2 : Kind.Deck;
            }
            else if (p.GetComponent<ChaosDeckGameplay>() != null) _kind = Kind.Chaos;
            else if (p.GetComponent<DiceGamePlay>() != null) _kind = Kind.Dice;
            else if (p.GetComponent<TexasGamePlay>() != null) _kind = Kind.Poker;
            else if (p.GetComponent<SpinGamePlay>() != null) _kind = Kind.Slots;
            if (_kind != Kind.None) break;
        }
        return _kind;
    }

    // ------------------------------------------------------------------ seats

    private static readonly List<PlayerStats> _seated = new();
    private static float _seatedAt = -999f;

    /// <summary>The players at the table that this copy can see, found in the scene once a second.</summary>
    private static List<PlayerStats> Seated()
    {
        if (Time.unscaledTime - _seatedAt < 1f) return _seated;
        _seatedAt = Time.unscaledTime;
        _seated.Clear();
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<PlayerStats>();
            if (all != null)
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null) _seated.Add(all[i]);
        }
        catch { }
        return _seated;
    }

    /// <summary>This copy's own seat: the seated player it owns, else its lobby player's seat.</summary>
    private static int MySeat()
    {
        foreach (var p in Seated())
            if (p != null && p.isOwned) return p.Slot;
        try
        {
            var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
            if (nm != null && nm.GamePlayers != null)
                foreach (var g in nm.GamePlayers)
                    if (g != null && g.isOwned) return g.InGameSlot;
        }
        catch { }
        return 0;
    }

    // ------------------------------------------------------------------ building

    private static bool Make(Manager m)
    {
        try
        {
            var canvas = m.WinMenu != null ? m.WinMenu.transform.parent : null;
            var board = m.SpinGame != null ? m.SpinGame.SpinLeaderboard : null;
            var plates = board != null ? board.leaderboardsObjects : null;
            var plate = plates != null && plates.Count > 1 ? plates[1] : null;
            if (canvas == null || plate == null || plate.playerNameText == null)
                return Fail("the game's leaderboard is not where it was");

            var go = new GameObject("LB8P_Board") { layer = canvas.gameObject.layer };
            go.SetActive(false);
            var root = go.AddComponent<RectTransform>();
            root.SetParent(canvas, false);
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(BlockWide, BlockHigh);
            _root = go;

            _title = AwardPanel.Text(plate.playerNameText.gameObject, root, "title", new Vector2(0f, TitleY),
                                     new Vector2(BlockWide, 60f), TitleSize);
            if (_title == null) return Fail("could not copy a name for the heading");
            _title.alignment = TextAlignmentOptions.Center;
            _title.color = Cream;

            _normal = plate.normalColor;
            _dead = plate.deadColor;
            _yours = plate.ourColor;
            _mine = plate._bgColorOwned;
            _back = plate._bgColor;

            _header = Plate(plate, root, "header");
            if (_header == null) return Fail("could not copy a leaderboard row");
            _header.Box.anchoredPosition = new Vector2(-PlateCentre * RowSize, HeaderY);
            if (_header.Back != null) _header.Back.enabled = false;
            _header.Seat.text = "";
            _header.Name.text = "";
            foreach (var t in new[] { _header.A, _header.B }) t.color = Cream;

            int seats = Math.Max(Limits.Max, Limits.VanillaPlayers);
            float gap = Math.Min(RowGap, (FirstRowY - RowsBottom) / Math.Max(1, seats - 1));
            for (int i = 0; i < seats; i++)
            {
                var row = Plate(plate, root, $"row{i}");
                if (row == null) return Fail("could not copy a leaderboard row");
                row.Box.anchoredPosition = new Vector2(-PlateCentre * RowSize, FirstRowY - i * gap);
                if (gap < RowGap) row.Box.localScale = Vector3.one * (RowSize * gap / RowGap);
                _rows.Add(row);
            }

            foreach (var g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            Plugin.Log.LogInfo($"[board] built for this match: {seats} rows");
            return true;
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    /// <summary>One of the Liar's Slots name plates, copied, with only its looks left.</summary>
    private static Row Plate(LeaderBoardDataSpin plate, Transform parent, string name)
    {
        var go = UiClone.Make(plate.gameObject, parent, name, null, "LeaderBoardDataSpin", "TextUpdate");
        if (go == null) return null;
        AwardPanel.Quiet(go);

        // The plate's texts by its own fields where it has filled them in, else by their place: some
        // are only filled in when the game first uses the plate, and this one may never have been.
        var src = plate.transform;
        var row = new Row
        {
            Box = go.GetComponent<RectTransform>(),
            Name = Find<TextMeshProUGUI>(go, src, plate.playerNameText) ?? Child<TextMeshProUGUI>(go, "Text (TMP) (1)"),
            B = Find<TextMeshProUGUI>(go, src, plate.playerPointText) ?? Child<TextMeshProUGUI>(go, "Text (TMP) (2)"),
            A = Find<TextMeshProUGUI>(go, src, plate._deltaText) ?? Child<TextMeshProUGUI>(go, "Text (TMP) (3)"),
            Seat = Child<TextMeshProUGUI>(go, "Text (TMP)"),
            Turn = AwardPanel.Twin(go.transform, src, plate.TurnObject != null ? plate.TurnObject.transform : null)?.gameObject,
            Dead = AwardPanel.Twin(go.transform, src, plate.deadIcon != null ? plate.deadIcon.transform : null)?.gameObject,
            Back = Find<Graphic>(go, src, plate.bg) ?? go.GetComponentInChildren<Image>(true),
        };

        // The rank number's picture goes; the number itself becomes the seat.
        if (row.Seat != null)
            foreach (var picture in row.Seat.GetComponentsInChildren<Image>(true)) picture.gameObject.SetActive(false);

        var target = AwardPanel.Twin(go.transform, src, plate.TargetIcon != null ? plate.TargetIcon.transform : null);
        if (target != null) target.gameObject.SetActive(false);

        if (row.Name == null || row.A == null || row.B == null || row.Seat == null)
        {
            Plugin.Log.LogWarning($"[board] a copied name plate has no {(row.Name == null ? "name " : "")}{(row.A == null ? "change " : "")}" +
                                  $"{(row.B == null ? "points " : "")}{(row.Seat == null ? "rank " : "")}text");
            UnityEngine.Object.Destroy(go);
            return null;
        }

        row.Box.anchorMin = row.Box.anchorMax = new Vector2(0.5f, 0.5f);
        row.Box.localScale = Vector3.one * RowSize;

        // The change text was left by its animation at whatever size and colour it ended on; all
        // four are set the same as the name, which the game draws at rest.
        float size = row.Name.fontSize;
        Column(row.Seat, SeatAt, SeatSize, TextAlignmentOptions.Center, size);
        Column(row.Name, NameAt, NameSize, TextAlignmentOptions.Left, size);
        Column(row.A, AAt, ASize, TextAlignmentOptions.Center, size);
        Column(row.B, BAt, BSize, TextAlignmentOptions.Center, size);
        if (row.Turn != null) row.Turn.SetActive(false);
        if (row.Dead != null) row.Dead.SetActive(false);
        return row;
    }

    private static T Find<T>(GameObject copy, Transform original, Component part) where T : Component
    {
        var at = AwardPanel.Twin(copy.transform, original, part);
        return at != null ? at.GetComponent<T>() : null;
    }

    private static T Child<T>(GameObject copy, string name) where T : Component
    {
        var at = copy.transform.Find(name);
        return at != null ? at.GetComponent<T>() : null;
    }

    private static void Column(TextMeshProUGUI text, Vector2 at, Vector2 size, TextAlignmentOptions align, float fontSize)
    {
        var rt = text.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = at;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        text.enabled = true;
        text.alpha = 1f;
        text.enableAutoSizing = true;
        text.fontSizeMax = fontSize;
        text.fontSizeMin = fontSize * 0.6f;
        text.richText = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.alignment = align;
        text.raycastTarget = false;
        text.gameObject.SetActive(true);
    }

    /// <summary>Centred, or right of centre in Liar's Poker, and as large as fits the screen.</summary>
    private static void Place(Kind kind)
    {
        var root = _root.GetComponent<RectTransform>();
        var canvas = root.parent != null ? root.parent.GetComponent<RectTransform>() : null;
        float high = canvas != null ? canvas.rect.height : 1080f;
        float size = Mathf.Min(Largest, high * MostOfHeight / BlockHigh);
        root.localScale = Vector3.one * size;
        root.anchoredPosition = new Vector2(kind == Kind.Poker ? PokerAcross : 0f, 0f);
    }

    // ------------------------------------------------------------------ filling

    private struct Line
    {
        internal int Seat;
        internal string Name, A, B;
        internal bool Here, Out, Turn, Mine;
    }

    private static void Fill(Manager m, Kind kind)
    {
        if (_title == null) return;
        var words = Words[kind];
        _title.text = LiarsBar8P.Words.T(words.Title);
        _header.A.text = LiarsBar8P.Words.T(words.A);
        _header.B.text = LiarsBar8P.Words.T(words.B);

        var lines = Lines(m, kind);
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool used = i < lines.Count;
            if (row.Box.gameObject.activeSelf != used) row.Box.gameObject.SetActive(used);
            if (!used) continue;

            var line = lines[i];
            row.Seat.text = (line.Seat + 1).ToString();
            row.Name.text = line.Name;
            row.A.text = line.A;
            row.B.text = line.B;

            var colour = line.Out ? _dead : _normal;
            row.A.color = row.B.color = row.Seat.color = colour;
            row.Name.color = line.Mine && !line.Out ? _yours : colour;
            if (row.Back != null) row.Back.color = line.Mine ? _mine : _back;
            if (row.Turn != null && row.Turn.activeSelf != line.Turn) row.Turn.SetActive(line.Turn);
            if (row.Dead != null && row.Dead.activeSelf != line.Out) row.Dead.SetActive(line.Out);
        }
    }

    /// <summary>One line per seat, in seat order from your own, everyone this match has seen.</summary>
    private static List<Line> Lines(Manager m, Kind kind)
    {
        var bySeat = new Dictionary<int, PlayerStats>();
        foreach (var p in Seated())
        {
            if (p == null) continue;
            bySeat[p.Slot] = p;
            _seen[p.Slot] = Plain(p.PlayerName, p.Slot);
        }

        // Seats with nobody this copy can see sitting in them, named from the lobby: a bot on a client.
        try
        {
            var nm = NetworkManager.singleton != null ? NetworkManager.singleton.TryCast<CustomNetworkManager>() : null;
            if (nm != null && nm.GamePlayers != null)
                foreach (var g in nm.GamePlayers)
                    if (g != null && g.InGameSlot >= 0 && g.InGameSlot < 64 && !bySeat.ContainsKey(g.InGameSlot))
                        _seen[g.InGameSlot] = Plain(g.PlayerName, g.InGameSlot);
        }
        catch { }

        var order = new List<int>(_seen.Keys);
        int mine = MySeat();
        order.Sort((a, b) => ((a - mine + 64) % 64).CompareTo((b - mine + 64) % 64));

        var lines = new List<Line>(order.Count);
        foreach (int seat in order)
        {
            var line = new Line { Seat = seat, Name = _seen[seat], A = NoValue, B = NoValue, Mine = seat == mine };
            if (bySeat.TryGetValue(seat, out var p))
            {
                line.Here = true;
                line.Out = p.Dead;
                line.Turn = p.HaveTurn && !p.Dead;
                Values(p, kind, ref line);
                // The word one column in: the plate's own skull sits on the last column and covered it.
                if (p.Dead) { line.A = LiarsBar8P.Words.T(OutWord); line.B = ""; }
            }
            else if (WasHere(seat)) { line.A = LiarsBar8P.Words.T(LeftWord); line.B = ""; line.Out = true; }
            else if (kind != Kind.Dice && kind != Kind.Poker) line.A = CardsOf(seat);   // a bot, which a client cannot see sitting
            lines.Add(line);
        }
        return lines;
    }

    private static readonly HashSet<int> _wasHere = new();

    /// <summary>A seat this copy once saw a seated player in, who is gone now.</summary>
    private static bool WasHere(int seat) => _wasHere.Contains(seat);

    /// <summary>The two numbers for one seat, from values the game keeps in step on every copy.</summary>
    private static void Values(PlayerStats p, Kind kind, ref Line line)
    {
        _wasHere.Add(p.Slot);
        switch (kind)
        {
            case Kind.Deck:
            case Kind.Devil:
            case Kind.Deck2:
                var deck = p.GetComponent<DeckGameplay>();
                if (deck == null) return;
                line.A = CardsOf(p.Slot);
                line.B = $"{deck.CurrentRevolver}/6";
                break;
            case Kind.Chaos:
                var chaos = p.GetComponent<ChaosDeckGameplay>();
                if (chaos == null) return;
                line.A = chaos.TakingAim ? LiarsBar8P.Words.T(AimWord) : CardsOf(p.Slot);
                line.B = $"{chaos.CurrentRevoler}/6";
                break;
            case Kind.Dice:
                var dice = p.GetComponent<DiceGamePlay>();
                line.A = p.Health.ToString();
                line.B = dice != null && dice.BetCount > 0 ? $"{dice.BetCount} x {dice.BetDice}" : NoValue;
                break;
            case Kind.Poker:
                var poker = p.GetComponent<TexasGamePlay>();
                if (poker == null) return;
                line.A = $"{poker.Bullets}/8";
                line.B = poker.Folded || p.isFolded ? LiarsBar8P.Words.T(FoldWord) : poker.AllIn ? LiarsBar8P.Words.T(AllInWord) : NoValue;
                break;
        }
    }

    private static string Plain(string name, int seat)
    {
        string clean = ModReader.Plain(name, 24);
        return string.IsNullOrWhiteSpace(clean) ? LiarsBar8P.Words.F(SeatName, seat + 1) : clean;
    }

    // ------------------------------------------------------------------ hands, from the host

    /// <summary>Cards held, by seat, as the host last said.</summary>
    private static readonly Dictionary<int, int> _hands = new();

    /// <summary>Host: the counts last said, so only a change is sent.</summary>
    private static readonly Dictionary<int, int> _handsSaid = new();
    private static float _handsNext;

    /// <summary>Host: the cards still in each hand, by the seat's netId. Set by each hand put in a seat, less each throw accepted.</summary>
    private static readonly Dictionary<uint, int> _held = new();

    /// <summary>
    /// The host's count of each hand, kept from the game's own deals and throws. The game's list
    /// of a seat's cards is the deal: only a deal writes it, so it read five all the way down a
    /// hand of five. A count only, never which cards.
    /// </summary>
    [HarmonyPatch]
    internal static class HandHooks
    {
        /// <summary>
        /// Liar's Deck: every hand put in a seat on the host ends here: the deal (after the Short
        /// Hand rule has cut it), a Deck 2 round that keeps the hands, a hand handed over again, a
        /// migration's. A card is in the hand where the game shows it: every place, or only the
        /// places named, and none when the seat is said to have no cards.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ApplyCardState))]
        private static void DeckDealt(DeckGameplay __instance, Il2CppStructArray<int> syncedTypes,
                                      Il2CppStructArray<int> activeIndexes, bool haveCards)
        {
            try
            {
                if (!NetworkServer.active || !Table.Modded || __instance == null || syncedTypes == null) return;
                int cards = haveCards && activeIndexes == null ? syncedTypes.Length : 0;
                if (haveCards && activeIndexes != null)
                {
                    var on = new HashSet<int>();
                    for (int i = 0; i < activeIndexes.Length; i++) on.Add(activeIndexes[i]);
                    for (int i = 0; i < syncedTypes.Length; i++) if (on.Contains(i)) cards++;
                }
                _held[__instance.netId] = cards;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[board] a Liar's Deck hand not counted: {e.Message}"); }
        }

        /// <summary>The Chaos deck: the host's one call that deals a seat its hand.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.RpcDealHand))]
        private static void ChaosDealt(ChaosDeckGameplay __instance, Il2CppStructArray<int> handTypes)
        {
            try
            {
                if (!NetworkServer.active || !Table.Modded || __instance == null || handTypes == null) return;
                _held[__instance.netId] = handTypes.Length;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[board] a Chaos deck hand not counted: {e.Message}"); }
        }

        // Every throw reaches these on the host, a person's, the host's own and a bot's. One that
        // another patch refused before the game played it has left nobody's hand.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ServerThrowCards))]
        private static void DeckThrew(DeckGameplay __instance, Il2CppSystem.Collections.Generic.List<int> types, bool __runOriginal)
            => Thrown(__instance, types, __runOriginal);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerThrowCards))]
        private static void ChaosThrew(ChaosDeckGameplay __instance, Il2CppSystem.Collections.Generic.List<int> types, bool __runOriginal)
            => Thrown(__instance, types, __runOriginal);
    }

    private static void Thrown(NetworkBehaviour part, Il2CppSystem.Collections.Generic.List<int> types, bool ran)
    {
        try
        {
            if (!ran || !NetworkServer.active || part == null || types == null) return;
            if (_held.TryGetValue(part.netId, out int cards)) _held[part.netId] = Math.Max(0, cards - types.Count);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[board] a throw not counted: {e.Message}"); }
    }

    /// <summary>The host's count for a seat's hand, or -1 when it has none.</summary>
    private static int HeldBy(PlayerStats p)
    {
        NetworkBehaviour part = p.GetComponent<DeckGameplay>();
        if (part == null) part = p.GetComponent<ChaosDeckGameplay>();
        return part != null && _held.TryGetValue(part.netId, out int cards) ? cards : -1;
    }

    /// <summary>At load, on every copy.</summary>
    internal static void Install() => ModNet.On(ModKind.Hands, HandsArrived);

    private static void HandsArrived(int from, ModReader body)
    {
        if (from != ModNet.FromHost) return;
        int count = Math.Min((int)body.U8(), 64);
        for (int i = 0; i < count; i++)
        {
            int seat = body.U8(), cards = body.U8();
            if (body.Failed) return;
            _hands[seat] = cards;
        }
    }

    /// <summary>Host, in the two deck games: every count that changed, twice a second at most.</summary>
    private static void SayHands(Kind kind)
    {
        if (kind != Kind.Deck && kind != Kind.Devil && kind != Kind.Deck2 && kind != Kind.Chaos) return;
        if (!NetworkServer.active || !ModNet.Open || Time.unscaledTime < _handsNext) return;
        _handsNext = Time.unscaledTime + 0.5f;

        var changed = new List<(int Seat, int Cards)>();
        foreach (var p in Seated())
        {
            if (p == null || p.Slot < 0 || p.Slot > byte.MaxValue) continue;
            int held = HeldBy(p);
            if (held < 0) continue;

            int cards = Math.Min(held, byte.MaxValue);
            if (_handsSaid.TryGetValue(p.Slot, out int was) && was == cards) continue;
            _handsSaid[p.Slot] = cards;
            changed.Add((p.Slot, cards));
        }
        if (changed.Count == 0) return;

        var body = new ModWriter().U8((byte)changed.Count);
        foreach (var (seat, cards) in changed) body.U8((byte)seat).U8((byte)cards);
        ModNet.SendToAll(ModKind.Hands, body);
    }

    private static string CardsOf(int seat) => _hands.TryGetValue(seat, out int cards) ? cards.ToString() : NoValue;

    // ------------------------------------------------------------------ the key hint

    /// <summary>
    /// The game's own "Tab" keycap from Liar's Poker, copied to the same spot in the games that had
    /// no use for the key, so players can find the board.
    ///
    /// Not its controller glyph. The game's switcher on the keycap shows LB while a pad is in use,
    /// which opens the hands chart in Liar's Poker but does nothing for the board, and pulses when
    /// LB is pressed. It comes off with the copy, and <see cref="PadKey"/> sets the two parts here.
    /// </summary>
    private static void Hint(Manager m, bool wanted)
    {
        if (!wanted || _hintFailed)
        {
            if (_hint != null && _hint.activeSelf) _hint.SetActive(false);
            return;
        }

        if (_hint == null)
        {
            var chart = m.TexasGame != null && m.TexasGame.PokerTableAnimator != null ? m.TexasGame.PokerTableAnimator.transform : null;
            var source = chart != null && chart.parent != null ? chart.parent.Find(HintPath) : null;
            var canvas = m.WinMenu != null ? m.WinMenu.transform.parent : null;
            _hint = source != null && canvas != null
                  ? UiClone.Make(source.gameObject, canvas, "LB8P_BoardKey", null, "LanguageHolder", "GamepadButtonEffect") : null;
            if (_hint == null)
            {
                _hintFailed = true;
                Plugin.Log.LogWarning("[board] no Tab hint this match: the game's keycap could not be copied");
                return;
            }

            var rt = _hint.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = HintAt;
            _hintWords = -1;
            foreach (var g in _hint.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;

            _keycap = ImageAt(_hint.transform, KeycapPath);
            _glyph = ImageAt(_hint.transform, GlyphPath);
            var pad = ImageAt(chart.parent, PadGlyphPath);
            _padGlyph = pad != null ? pad.sprite : null;
            _padShown = -1;
            _padNext = 0f;
            Plugin.Log.LogInfo($"[board] Tab hint made: on a controller it shows '{(_padGlyph != null ? _padGlyph.name : "the keycap, no glyph found")}', " +
                               $"not the copied '{(_glyph != null && _glyph.sprite != null ? _glyph.sprite.name : "?")}'");
        }

        // Written again when the language is changed in the settings during the match.
        if (_hintWords != LiarsBar8P.Words.Generation)
        {
            _hintWords = LiarsBar8P.Words.Generation;
            foreach (var t in _hint.GetComponentsInChildren<TextMeshProUGUI>(true)) t.text = LiarsBar8P.Words.T(HintWord);
        }
        PadKey();
        if (!_hint.activeSelf) _hint.SetActive(true);
    }

    private static Image ImageAt(Transform under, string path)
    {
        var t = under != null ? under.Find(path) : null;
        return t != null ? t.GetComponent<Image>() : null;
    }

    /// <summary>
    /// The keycap, or while the game says a controller is in use, Liar's Slots' hints button in its
    /// place, as the game's own hints switch. Set on every change, never left as copied: the copy
    /// holds whatever the original last showed. With no glyph found, the keycap always.
    /// </summary>
    private static void PadKey()
    {
        if (Time.unscaledTime < _padNext) return;
        _padNext = Time.unscaledTime + 0.5f;

        int pad = _padGlyph != null && _glyph != null && PadInUse() ? 1 : 0;
        if (pad == _padShown) return;
        _padShown = pad;

        if (_glyph != null)
        {
            if (pad == 1) _glyph.sprite = _padGlyph;
            _glyph.enabled = pad == 1;
        }
        if (_keycap != null)
        {
            _keycap.enabled = pad == 0;
            var c = _keycap.color;
            c.a = 1f;   // the game's switcher clears it while a pad is in use
            _keycap.color = c;
        }
    }

    /// <summary>The game's own test, the one its key hints use: a pad connected, chosen in its settings, and used.</summary>
    private static bool PadInUse()
    {
        if (PretendPad && Dev.Enabled) return true;
        try
        {
            if (!_padsLooked)
            {
                _padsLooked = true;
                _pads = UnityEngine.Object.FindObjectOfType<GamepadManager>();
            }
            return _pads != null && _pads.IsGamepadConnected();
        }
        catch { return false; }
    }

    private static bool Fail(string why)
    {
        _failed = true;
        Plugin.Log.LogWarning($"[board] no board this match: {why}");
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null;
        _rows.Clear();
        return false;
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>What the board shows, by seat, without names.</summary>
    internal static string Describe()
    {
        var m = Manager.Instance;
        if (m == null) return "no match";
        var kind = KindOf(m);
        if (kind == Kind.None) return "no table yet";
        if (kind == Kind.Slots) return "Liar's Slots: no board, its leaderboard is always on screen";

        string key = (_keycap != null && _keycap.enabled ? "keycap" : "no keycap") + ", "
                   + (_glyph != null && _glyph.enabled ? $"glyph '{(_glyph.sprite != null ? _glyph.sprite.name : "?")}'" : "no glyph");
        var sb = new System.Text.StringBuilder($"{kind}, allowed={Allowed(m, out _) != Kind.None}, up={Up}, " +
                                               $"hint={(_hint != null && _hint.activeInHierarchy)} ({key}):");
        foreach (var line in Lines(m, kind))
            sb.Append($" [seat {line.Seat}{(line.Mine ? " you" : "")}{(line.Turn ? " turn" : "")}{(line.Here ? "" : " unseen")} {line.A}|{line.B}]");

        // The host alone holds every hand: what the board says against the server's deal and the
        // host's own count of what is left of it.
        if (NetworkServer.active && kind != Kind.Dice && kind != Kind.Poker)
        {
            sb.Append(" server hands, dealt/held:");
            foreach (var p in Seated())
            {
                var deck = p != null ? p.GetComponent<DeckGameplay>() : null;
                var chaos = p != null ? p.GetComponent<ChaosDeckGameplay>() : null;
                var dealt = deck != null ? deck.cardTypes : chaos != null ? chaos.CardTypes : null;
                if (deck == null && chaos == null) continue;
                int held = HeldBy(p);
                sb.Append($" {p.Slot}={(dealt != null ? dealt.Count : -1)}/{(held >= 0 ? held.ToString() : "?")}");
            }
        }
        return sb.ToString();
    }
}
