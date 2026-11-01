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

    private static ModCanvas.Line _tag, _note, _warning;

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
            }

            _tag.Set($"<color={ModCanvas.GoldTag}>8 PLAYER MOD</color>  v{Plugin.Version}  " +
                     $"<color={ModCanvas.FaintTag}>MAX {Plugin.MaxPlayers.Value}</color>");

            float y = RowHeight + RowGap;
            y = Place(_note, AutoUpdate.Note, y);

            string warning = VersionHud.Mismatch;
            bool fits = ModCanvas.Fits(warning);
            PlainWarning = fits ? null : warning;
            Place(_warning, fits ? warning : null, y);
        }
        catch (Exception e) { ModCanvas.Fail("the tag at the top", e); }
    }

    /// <summary>One more line under the tag, if it has anything to say. Returns where the next would go.</summary>
    private static float Place(ModCanvas.Line line, string words, float y)
    {
        line.Set(words);
        if (string.IsNullOrEmpty(words)) return y;

        line.Box.anchoredPosition = new Vector2(0f, -y);
        return y + RowHeight + RowGap;
    }
}
