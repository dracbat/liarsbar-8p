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
            if (m == null || !m.GameStarted) { _line = null; return; }

            // Not Manager.Players: that is the server's roster and is empty on a client, so
            // reading it left this readout permanently blank on every screen but the host's -
            // and since the tabletop markings this replaced are switched off on every peer,
            // those players had no way at all to tell whose turn it was.
            var seated = Seated();

            PlayerStats up = null;
            for (int i = 0; i < seated.Count; i++)
            {
                var p = seated[i];
                if (p != null && p.HaveTurn && !p.Dead) { up = p; break; }
            }

            // Nobody is claiming the turn. The active slot is a SyncVar and is right on every
            // machine, so it still answers the question - and it tracks the game's own notion
            // of whose turn it is, which moves on when the turn does rather than lagging.
            int slot = m.ActivePlayerSlot;
            if (up == null)
            {
                for (int i = 0; i < seated.Count; i++)
                {
                    var p = seated[i];
                    if (p != null && p.Slot == slot && !p.Dead) { up = p; break; }
                }
            }

            if (up == null)
            {
                // A seat this machine has no player object for - a bot, whose object is never
                // network-spawned and so cannot be seen from a client. Name the seat rather
                // than show nothing.
                _isYou = false;
                _line = slot >= 0 ? $"Seat {slot + 1}'s turn" : null;
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

    /// <summary>
    /// The players at the table, found in the scene and cached.
    ///
    /// A scene scan four times a second would be wasteful for something that changes once a
    /// match, so the answer is kept for a couple of seconds. Entries are still null-checked
    /// by callers: a player destroyed inside that window is null under Unity's operator
    /// while the array still holds the reference.
    /// </summary>
    private static readonly System.Collections.Generic.List<PlayerStats> _seated = new();
    private static float _seatedAt = -999f;

    private static System.Collections.Generic.List<PlayerStats> Seated()
    {
        if (Time.time - _seatedAt < 2f) return _seated;
        _seatedAt = Time.time;
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
