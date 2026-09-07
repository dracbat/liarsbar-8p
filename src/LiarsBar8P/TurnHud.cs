using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Says whose turn it is, in words, in the top left corner.
///
/// The game's own answer to that question is the markings on the table, and at eight players
/// they are worse than nothing: there are four of them for eight seats, so the one nearest
/// the player who is up usually belongs to their neighbour. They have been switched off (see
/// <see cref="TurnPointer"/>), and this replaces them.
///
/// Words rather than a pointer, because a pointer has to be read against a ring of eight
/// people seen edge-on from your own chair, and a name does not. It sits under the round
/// card and the claim, which is where a player is already looking to know what is going on.
///
/// This is not a developer tool. It is on for everybody, and it is the only thing in the mod
/// that tells a player whose turn it is.
/// </summary>
internal sealed class TurnHud : MonoBehaviour
{
    public TurnHud(IntPtr ptr) : base(ptr) { }

    /// <summary>What to draw, worked out on the mod's tick rather than while drawing.</summary>
    private static string _line;
    private static bool _isYou;

    private GUIStyle _style;
    private GUIStyle _yours;
    private Texture2D _backdrop;

    /// <summary>
    /// Work out the line to show. Called four times a second from the ticker, because
    /// <c>OnGUI</c> runs several times per frame and this walks the roster.
    /// </summary>
    internal static void Refresh()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Players == null || !m.GameStarted) { _line = null; return; }

            PlayerStats up = null;
            foreach (var p in m.Players)
                if (p != null && p.HaveTurn && !p.Dead) { up = p; break; }

            if (up == null)
            {
                // Between turns. Saying nothing is better than saying something stale: a
                // name left on screen after that player has acted is worse than a blank.
                _line = null;
                return;
            }

            string name = up.PlayerName;
            if (string.IsNullOrEmpty(name)) name = $"seat {up.Slot + 1}";

            _isYou = IsLocal(up);
            _line = _isYou ? "Your turn" : $"{name}'s turn";
        }
        catch
        {
            _line = null;
        }
    }

    /// <summary>Is this the player sitting at this computer?</summary>
    private static bool IsLocal(PlayerStats p)
    {
        try
        {
            // The seated character is not the object that carries ownership; the lobby-side
            // player object is, and the two are matched by name.
            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null || nm.GamePlayers == null) return false;

            foreach (var g in nm.GamePlayers)
                if (g != null && g.isOwned)
                    return !string.IsNullOrEmpty(g.PlayerName) && g.PlayerName == p.PlayerName;
        }
        catch { }
        return false;
    }

    private void OnGUI()
    {
        try
        {
            if (string.IsNullOrEmpty(_line)) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 20,
                    alignment = TextAnchor.UpperLeft,
                    richText = false,
                };
                _style.normal.textColor = new Color(1f, 0.94f, 0.78f, 0.96f);

                _yours = new GUIStyle(_style) { fontSize = 22, fontStyle = FontStyle.Bold };
                _yours.normal.textColor = new Color(0.55f, 1f, 0.55f, 1f);

                _backdrop = new Texture2D(1, 1);
                _backdrop.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
                _backdrop.Apply();
            }

            var style = _isYou ? _yours : _style;

            // Under the round card and the claim, which the game draws in this corner. A
            // fraction of the height rather than a pixel count, because that block is laid
            // out by a canvas that scales with the window.
            float y = Screen.height * 0.21f;
            Vector2 size = style.CalcSize(new GUIContent(_line));

            GUI.DrawTexture(new Rect(6f, y - 3f, size.x + 14f, size.y + 8f), _backdrop);
            GUI.Label(new Rect(13f, y, size.x + 10f, size.y + 4f), _line, style);
        }
        catch { /* never let a HUD draw break the game */ }
    }
}
