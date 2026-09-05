using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Draws the running mod version in the bottom left corner.
///
/// Version drift between players has repeatedly broken sessions, and spotting it meant
/// reading each person's log by hand. Putting the version on screen means anyone can
/// check their own at a glance, and a screenshot settles it.
///
/// When the host's audit finds players on other builds, their names are shown here too,
/// so the problem is visible to the person who can actually do something about it.
/// </summary>
internal sealed class VersionHud : MonoBehaviour
{
    public VersionHud(IntPtr ptr) : base(ptr) { }

    /// <summary>Set by VersionCheck when the lobby contains mismatched builds.</summary>
    internal static string Mismatch = null;

    private GUIStyle _style;
    private GUIStyle _warnStyle;

    private void OnGUI()
    {
        try
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    alignment = TextAnchor.LowerLeft,
                    richText = false
                };
                _style.normal.textColor = new Color(1f, 1f, 1f, 0.75f);

                _warnStyle = new GUIStyle(_style) { fontSize = 15 };
                _warnStyle.normal.textColor = new Color(1f, 0.45f, 0.35f, 0.95f);
            }

            string label = $"Liar's Bar 8P  v{Plugin.Version}  (max {Plugin.MaxPlayers.Value})";

            float y = Screen.height - 26f;

            // shadow first so the text stays readable over a light background
            var shadow = new GUIStyle(_style);
            shadow.normal.textColor = new Color(0f, 0f, 0f, 0.65f);
            GUI.Label(new Rect(11f, y + 1f, 900f, 22f), label, shadow);
            GUI.Label(new Rect(10f, y, 900f, 22f), label, _style);

            if (!string.IsNullOrEmpty(Mismatch))
            {
                float wy = y - 20f;
                var wshadow = new GUIStyle(_warnStyle);
                wshadow.normal.textColor = new Color(0f, 0f, 0f, 0.65f);
                GUI.Label(new Rect(11f, wy + 1f, 900f, 22f), Mismatch, wshadow);
                GUI.Label(new Rect(10f, wy, 900f, 22f), Mismatch, _warnStyle);
            }
        }
        catch { /* never let a HUD draw break the game */ }
    }
}
