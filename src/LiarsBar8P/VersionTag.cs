using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The mod's tag at the top of the screen: which version is running, and under it the updater's
/// line and the warning when somebody's build differs. <see cref="VersionHud"/> draws the same
/// lines plainly whenever <see cref="ModCanvas"/> is not up.
/// </summary>
internal static class VersionTag
{
    private const float RowHeight = 28f;
    private const float RowGap = 4f;
    private const float TagSize = 18f;
    private const float LineSize = 18f;

    private static readonly Vector2 TopMiddle = new(0.5f, 1f);

    /// <summary>
    /// The round help button of each game in the lobby, under the bar of game tabs. Two of them,
    /// Liar's Poker's and Liar's Dice's, sit in the strip the tag is drawn in, one either side of
    /// the middle, so no one place for the tag clears both: it steps aside from whichever is up.
    /// </summary>
    private const string TabBar = "GameMode Button";
    internal const string MaxWords = "MAX {0}";
    private static readonly string[] HelpButtons =
    {
        "Dice/HelpDiceBasic", "Dice/HelpDiceTraditanial", "Poker/HelpPoker", "Slots/HelpSlot",
        "DeckGameModes/HelpDeckBasic", "DeckGameModes/HelpDeckDevil",
        "DeckGameModes/HelpDeck2", "DeckGameModes/HelpDeckChaos",
    };

    /// <summary>Clear space kept between the tag and a help button's own box, in screen pixels at 1080.</summary>
    private const float HelpGap = 12f;

    private static ModCanvas.Line _tag, _note, _warning, _report;
    private static IntPtr _helpsFor;
    private static RectTransform[] _helps;
    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> _corners = new(4);

    /// <summary>How far the tag stands aside in the lobby, in the tag canvas's own units. For the test driver.</summary>
    internal static float Shift { get; private set; }

    /// <summary>The warning, when it has a name in it the game's fonts cannot draw.</summary>
    internal static string PlainWarning { get; private set; }

    /// <summary>From <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        try
        {
            if (!ModCanvas.Build()) return;

            if (_tag == null)
            {
                _tag = ModCanvas.NewLine("tag", TopMiddle, RowHeight, TagSize, ModCanvas.Cream, rule: true, rich: true);
                _note = ModCanvas.NewLine("note", TopMiddle, RowHeight, LineSize, ModCanvas.Cream);
                _warning = ModCanvas.NewLine("warning", TopMiddle, RowHeight, LineSize, ModCanvas.Warning);
                _report = ModCanvas.NewLine("report", TopMiddle, RowHeight, LineSize, ModCanvas.Cream);
            }

            _tag.Set($"<color={ModCanvas.GoldTag}>{Plugin.TagName}</color>  v{Plugin.Version}  " +
                     $"<color={ModCanvas.FaintTag}>{Words.F(MaxWords, Plugin.MaxPlayers.Value)}</color>");

            float x = 0f;
            try { x = Aside(); }
            catch (Exception e)
            {
                _helps = null;
                Plugin.Log.LogWarning($"[version] could not keep the tag clear of the lobby's help button: {e.Message}");
            }
            Shift = x;
            _tag.Box.anchoredPosition = new Vector2(x, 0f);

            float y = RowHeight + RowGap;
            y = Place(_note, AutoUpdate.Note, x, y);

            string warning = VersionHud.Mismatch;
            bool fits = ModCanvas.Fits(warning);
            PlainWarning = fits ? null : warning;
            y = Place(_warning, fits ? warning : null, x, y);

            // Where a bug report went, for a few seconds after it is saved.
            Place(_report, BugReport.Shown, x, y);
        }
        catch (Exception e) { ModCanvas.Fail("the tag at the top", e); }
    }

    /// <summary>One more line under the tag, if it has anything to say. Returns where the next would go.</summary>
    private static float Place(ModCanvas.Line line, string words, float x, float y)
    {
        line.Set(words);
        if (string.IsNullOrEmpty(words)) return y;

        line.Box.anchoredPosition = new Vector2(x, -y);
        return y + RowHeight + RowGap;
    }

    /// <summary>
    /// In the lobby, how far to move the tag sideways so it does not touch the help button that is
    /// up, and nothing anywhere else. Worked in screen pixels, because the game's canvas and the
    /// tag's scale with the screen in different ways, then turned into the tag's own units.
    /// </summary>
    private static float Aside()
    {
        var room = LobbyController.Instance;
        if (room == null || Manager.Instance != null || !_tag.Box.gameObject.activeSelf) return 0f;

        var helps = Helps(room);
        if (helps == null) return 0f;

        float scale = _tag.Box.lossyScale.x;
        if (scale <= 0f) return 0f;

        float middle = Screen.width / 2f;
        float half = _tag.Box.rect.width * scale / 2f;
        float bottom = Screen.height - RowHeight * scale;
        float gap = HelpGap * Screen.height / 1080f;

        foreach (var help in helps)
        {
            if (help == null || !help.gameObject.activeInHierarchy) continue;

            help.GetWorldCorners(_corners);
            float left = _corners[0].x, right = _corners[2].x, low = _corners[0].y;
            if (low >= Screen.height || _corners[2].y <= bottom) continue;           // not in the tag's strip
            if (right + gap <= middle - half || left - gap >= middle + half) continue;  // already clear

            float move = (left + right) / 2f >= middle
                ? (left - gap) - (middle + half)     // the button is to the right: step left
                : (right + gap) - (middle - half);   // to the left: step right
            return move / scale;
        }
        return 0f;
    }

    /// <summary>The help buttons of this lobby, found once per lobby.</summary>
    private static RectTransform[] Helps(LobbyController room)
    {
        if (_helps != null && _helpsFor == room.Pointer) return _helps;

        var label = room.LoobyIdText;
        var canvas = label != null && label.canvas != null ? label.canvas.rootCanvas : null;
        var bar = canvas != null ? canvas.transform.Find(TabBar) : null;
        if (bar == null) return null;

        var found = new RectTransform[HelpButtons.Length];
        for (int i = 0; i < HelpButtons.Length; i++)
        {
            var t = bar.Find(HelpButtons[i]);
            found[i] = t != null ? t.GetComponent<RectTransform>() : null;
        }

        _helps = found;
        _helpsFor = room.Pointer;
        return found;
    }
}
