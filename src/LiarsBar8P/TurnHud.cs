using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Says whose turn it is, in words, in the top left corner (not in Liar's Texas, which says it itself).
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

    // The words, in one place for the word table. A name is never declined: other languages put it after a colon.
    private const string SeatTurn = "Seat {0}'s turn";
    private const string SeatWord = "seat {0}";
    private const string YourTurn = "Your turn";
    private const string NameTurn = "{0}'s turn";

    private GUIStyle _style;
    private GUIStyle _yours;
    private Texture2D _backdrop;

    private static ModCanvas.Line _row;
    private static bool _plain;

    /// <summary>The left edge, and the same height on any screen, because that block scales with the window.</summary>
    private static readonly Vector2 Corner = new(0f, 1f);
    private const float Inset = 6f;
    private const float Down = 224f;

    /// <summary>
    /// Two-Card Poker keeps a table of every hand's worth down the left edge, all match long, and
    /// this line sat across its top rows. There it starts just past the table instead.
    /// </summary>
    private const float BesideTheValues = 96f;

    /// <summary>
    /// Work out the line to show. Called four times a second from the ticker, because this
    /// walks the roster.
    /// </summary>
    internal static void Refresh()
    {
        Work();
        Publish();
    }

    /// <summary>For the self test.</summary>
    internal static void Pretend(string line) => _pretend = line;
    private static string _pretend;

    private static void Publish()
    {
        if (!ModCanvas.Ready) return;

        try
        {
            if (!string.IsNullOrEmpty(_pretend)) _line = _pretend;

            if (_row == null) _row = ModCanvas.NewLine("turn", Corner, 40f, 26f, ModCanvas.Cream, rule: true);
            _row.Box.anchoredPosition = new Vector2(TwoCardPoker.Playing ? BesideTheValues : Inset, -Down);

            _plain = !ModCanvas.Fits(_line);
            _row.Text.color = _isYou ? ModCanvas.Gold : ModCanvas.Cream;
            _row.Set(_plain ? null : _line);
        }
        catch (Exception e) { ModCanvas.Fail("the turn line", e); }
    }

    private static void Work()
    {
        try
        {
            // Nobody has a turn on the win screen, and the awards are written where this sits.
            var m = Manager.Instance;
            if (m == null || !m.GameStarted || m.GameEnd) { _line = null; return; }

            // Liar's Texas says whose turn it is itself, by name, in the top right - and
            // keeps a ranking ladder down the left edge, which this line was drawn straight
            // across. Two answers to the same question in two corners, one of them on top of
            // something else, is worse than the game's one.
            if (TableHand.Playing() == TableHand.Kind.Texas) { _line = null; return; }

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
                // A seat this machine has no player object for, such as one whose body has not
                // arrived yet. Name the seat rather than show nothing.
                _isYou = false;
                _line = slot >= 0 ? Words.F(SeatTurn, slot + 1) : null;
                return;
            }

            string name = up.PlayerName;
            if (string.IsNullOrEmpty(name)) name = Words.F(SeatWord, up.Slot + 1);

            _isYou = IsLocal(up);
            _line = _isYou ? Words.T(YourTurn) : Words.F(NameTurn, name);
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

    /// <summary>The plain version, for when <see cref="ModCanvas"/> is not up.</summary>
    private void OnGUI()
    {
        try
        {
            if ((ModCanvas.Ready && !_plain) || string.IsNullOrEmpty(_line)) return;

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
            float x = TwoCardPoker.Playing ? BesideTheValues * Screen.height / 1080f : 6f;
            Vector2 size = style.CalcSize(new GUIContent(_line));

            GUI.DrawTexture(new Rect(x, y - 3f, size.x + 14f, size.y + 8f), _backdrop);
            GUI.Label(new Rect(x + 7f, y, size.x + 10f, size.y + 4f), _line, style);
        }
        catch { /* never let a HUD draw break the game */ }
    }
}
