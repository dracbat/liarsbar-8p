using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Draws the running mod version in the top left corner.
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
    private Texture2D _backdrop;

    private void OnGUI()
    {
        try
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    alignment = TextAnchor.UpperLeft,
                    richText = false
                };
                _style.normal.textColor = new Color(1f, 1f, 1f, 0.75f);

                _warnStyle = new GUIStyle(_style) { fontSize = 15 };
                _warnStyle.normal.textColor = new Color(1f, 0.45f, 0.35f, 0.95f);
            }

            string label = $"Liar's Bar 8P  v{Plugin.Version}  (max {Plugin.MaxPlayers.Value})";

            // Top left. The bottom of the screen is where the game prints its own version,
            // and two version strings on one line are two illegible ones.
            float y = 8f;

            // A panel behind the text, because it has to stay readable over a bright menu
            // as well as a dark bar. A drop shadow alone was not enough.
            Vector2 size = _style.CalcSize(new GUIContent(label));
            if (_backdrop == null)
            {
                _backdrop = new Texture2D(1, 1);
                _backdrop.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
                _backdrop.Apply();
            }
            GUI.DrawTexture(new Rect(6f, y - 2f, size.x + 10f, size.y + 6f), _backdrop);
            GUI.Label(new Rect(11f, y, 900f, 22f), label, _style);

            if (!string.IsNullOrEmpty(Mismatch))
            {
                float wy = y + size.y + 8f;
                Vector2 wsize = _warnStyle.CalcSize(new GUIContent(Mismatch));
                GUI.DrawTexture(new Rect(6f, wy - 2f, wsize.x + 10f, wsize.y + 6f), _backdrop);
                GUI.Label(new Rect(11f, wy, 900f, 22f), Mismatch, _warnStyle);
            }
        }
        catch { /* never let a HUD draw break the game */ }
    }
}
