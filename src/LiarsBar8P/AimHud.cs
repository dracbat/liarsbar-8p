using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Says who you are about to shoot, in words, while you are choosing - where they sit, and
/// which keys move the aim.
///
/// The game never draws this because it never needed to. Aiming is shown by an animation
/// clip - look left, look ahead, look right - and with four chairs those three poses are the
/// three opponents, so the pose is the answer. <see cref="AimRing"/> opens the choice up to
/// everyone at the table, and the moment there are more than three people to choose between,
/// three poses stop identifying anybody: pointing "left" at a table of eight could mean any of
/// three players sitting one behind another from where you are.
///
/// So the pose keeps saying roughly which way the gun is turned, and this says exactly who is
/// at the end of it. Without it the extra targets would be reachable but not choosable, which
/// is a different bug rather than a fixed one. A name alone turned out not to be enough either:
/// at seven, people could see the name change and still not know where that person was sitting
/// or which key would bring the aim round to the one they wanted. So the line says where the
/// target sits counted from you ("2 seats to your right"), and a second line says which keys
/// move it. When the countdown runs out and the aim locks, it says so, and names the person the
/// shot will really go to.
///
/// "Right" here is the way the D key moves the aim: towards the next seat round, which is the
/// chair to your right as the mod lays the table out.
///
/// It appears only while a player is actually choosing, only for the player choosing, and only
/// above four players - below that the pose is unambiguous and this would be clutter.
/// </summary>
internal sealed class AimHud : MonoBehaviour
{
    public AimHud(IntPtr ptr) : base(ptr) { }

    /// <summary>Often enough to feel like it answers the keypress, cheap enough not to matter.</summary>
    private const float RefreshSeconds = 0.05f;

    /// <summary>How long the local player component is trusted before being looked up again.</summary>
    private const float LookupSeconds = 2f;

    /// <summary>The keys, by mode. The Chaos deck reads the pad as well; Liar's Poker only A and D.</summary>
    private const string DeckKeys = "A / D, LB / RB or the D-pad to change target";
    private const string PokerKeys = "A / D to change target";

    private static string _line;
    private static string _hint;

    private float _next;

    // The aiming component of the player at this computer, cast once when it is found rather
    // than twenty times a second.
    private static CharController _me;
    private static ChaosDeckGameplay _deck;
    private static ChaosGamePlay _chaos;
    private static PokerGamePlay _poker;
    private static float _meAt = -999f;

    private GUIStyle _style;
    private GUIStyle _hintStyle;
    private Texture2D _backdrop;

    // The two lines as they were last measured. OnGUI runs several times a frame - once to lay
    // out, once to paint, once per input event - and the text only changes on a keypress or a
    // lock, so each line is wrapped and measured once when it changes rather than on every
    // call. Drawing from the kept wrapper also spares copying the text into the game's own
    // kind of string on every call, which handing GUI.Label a plain string does.
    private string _sizedLine;
    private string _sizedHint;
    private GUIContent _lineContent;
    private GUIContent _hintContent;
    private Vector2 _lineSize;
    private Vector2 _hintSize;

    /// <summary>
    /// After every <c>Update</c> of the frame, not alongside them.
    ///
    /// On a client the server can send back an aim older than the one the keys just chose, and
    /// <see cref="AimKeys"/> puts the newer one back from inside the game's own update. Read in
    /// the same <c>Update</c> pass, the readout could catch the aim in between and show the seat
    /// the player had just moved away from for a refresh; read here, it never sees the gap.
    /// </summary>
    private void LateUpdate()
    {
        if (Time.time < _next) return;
        _next = Time.time + RefreshSeconds;

        try { Refresh(); }
        catch { _line = null; _hint = null; }
    }

    // What the line was last worked out from. The line is only rebuilt when one of these
    // changes, because answering "who is that" costs a walk of the roster - the game rebuilds
    // it from a scene scan on every lookup - and the aiming phase is the worst moment to
    // spend that twenty times a second for an answer that changes on a keypress.
    private static int _lastAim = int.MinValue;
    private static bool _lastLocked;
    private static PlayerStats _target;

    private static void Forget()
    {
        _line = null;
        _hint = null;
        _lastAim = int.MinValue;
        _target = null;
    }

    private static void Refresh()
    {
        if (!Local() || !Choosing()) { Forget(); return; }

        // Four players or fewer: the pose says it all, and the shipped keys reach everyone.
        if (!AimRing.Ring(_me, out int mySlot, out int n)) { Forget(); return; }

        int aim = Aim();
        bool locked = _deck != null && _deck.AimLocked;

        // The person at the end of the aim going out of the game changes the answer as much
        // as the aim moving does - another shooter can kill them while this one is choosing.
        bool gone = false;
        if (!ReferenceEquals(_target, null))
        {
            try { gone = _target == null || _target.Dead || _target.Fnished; }
            catch { gone = true; }
        }

        if (aim == _lastAim && locked == _lastLocked && !gone && _line != null) return;
        _lastAim = aim;
        _lastLocked = locked;

        _target = AimRing.AimingAt(_me);
        _hint = locked ? null : (_poker != null ? PokerKeys : DeckKeys);

        if (_target == null)
        {
            _line = locked ? "Aim locked on nobody" : "Aiming at nobody";
            return;
        }

        string name = _target.PlayerName;
        if (string.IsNullOrEmpty(name)) name = $"seat {_target.Slot + 1}";

        string where = Where((_target.Slot - mySlot + n) % n, n);
        string what = locked ? "Locked on" : "Aiming at";
        _line = where != null ? $"{what} {name} - {where}" : $"{what} {name}";
    }

    /// <summary>
    /// Where a seat sits, counted from yours: round to the right for the nearer half of the
    /// table, round to the left for the other, straight across when there is a seat exactly
    /// opposite.
    /// </summary>
    internal static string Where(int offset, int n)
    {
        if (n < 2 || offset <= 0 || offset >= n) return null;
        if (offset * 2 == n) return "straight across";

        if (offset * 2 < n)
            return offset == 1 ? "next to you, on your right" : $"{offset} seats to your right";

        int left = n - offset;
        return left == 1 ? "next to you, on your left" : $"{left} seats to your left";
    }

    private static bool Choosing()
    {
        try
        {
            if (_deck != null) return _deck.TakingAim;
            if (_chaos != null) return _chaos.TakingAim;
            if (_poker != null) return _poker.TakingAim;
        }
        catch { }
        return false;
    }

    private static int Aim()
    {
        try
        {
            if (_deck != null) return _deck.Aim;
            if (_chaos != null) return _chaos.Aim;
            if (_poker != null) return _poker.Aim;
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Find the aiming component belonging to the player sitting at this computer.
    ///
    /// Ownership is the test rather than the name: the aiming code itself decides where to
    /// send a keypress by asking <c>isOwned</c> on this very component, so if it were not
    /// owned here the aim would not move in the first place and there would be nothing to
    /// report. Cached, because this is asked twenty times a second and a scene scan is not -
    /// and that includes finding nothing: out of a match there is no such component, and
    /// looking again on every refresh would scan the scene twenty times a second in the menus.
    ///
    /// The cached reference is re-checked rather than trusted: between rounds the player
    /// object is replaced, and a destroyed object is still a non-null C# reference while being
    /// null under Unity's own operator.
    /// </summary>
    private static bool Local()
    {
        if (Time.time - _meAt < LookupSeconds && (ReferenceEquals(_me, null) || _me != null))
            return !ReferenceEquals(_me, null);

        _meAt = Time.time;
        _me = null;
        _deck = null;
        _chaos = null;
        _poker = null;

        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<CharController>();
            if (all == null) return false;

            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null) continue;
                try { if (!c.isOwned) continue; }
                catch { continue; }
                if (c.playerStats == null) continue;

                _deck = c.TryCast<ChaosDeckGameplay>();
                if (_deck == null) _chaos = c.TryCast<ChaosGamePlay>();
                if (_deck == null && _chaos == null) _poker = c.TryCast<PokerGamePlay>();

                // A mode with no aiming at all is still remembered, so it is not looked for
                // again until the lookup runs out.
                _me = c;
                return true;
            }
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
                    fontSize = 26,
                    alignment = TextAnchor.MiddleCenter,
                    richText = false,
                };
                _style.normal.textColor = new Color(1f, 0.45f, 0.4f, 1f);

                _hintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 17,
                    alignment = TextAnchor.MiddleCenter,
                    richText = false,
                };
                _hintStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 1f);

                _backdrop = new Texture2D(1, 1);
                _backdrop.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
                _backdrop.Apply();
            }

            // Strings are replaced, never changed, so a different reference is a different line.
            string line = _line;
            string hint = _hint;
            if (!ReferenceEquals(line, _sizedLine) || _lineContent == null)
            {
                _sizedLine = line;
                _lineContent = new GUIContent(line);
                _lineSize = _style.CalcSize(_lineContent);
            }
            if (!ReferenceEquals(hint, _sizedHint))
            {
                _sizedHint = hint;
                _hintContent = string.IsNullOrEmpty(hint) ? null : new GUIContent(hint);
                _hintSize = _hintContent != null ? _hintStyle.CalcSize(_hintContent) : Vector2.zero;
            }

            // Just under the middle of the screen: where the player is already looking while
            // turning the gun, and clear of the cards along the bottom and the round text at
            // the top.
            Vector2 size = _lineSize;
            Vector2 hintSize = _hintContent != null ? _hintSize : Vector2.zero;

            float width = Mathf.Max(size.x, hintSize.x);
            float height = size.y + (hintSize.y > 0f ? hintSize.y + 2f : 0f);
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height * 0.62f;

            GUI.DrawTexture(new Rect(x - 16f, y - 6f, width + 32f, height + 12f), _backdrop);
            GUI.Label(new Rect(x, y, width, size.y), _lineContent, _style);
            if (hintSize.y > 0f)
                GUI.Label(new Rect(x, y + size.y + 2f, width, hintSize.y), _hintContent, _hintStyle);
        }
        catch { /* never let a HUD draw break the game */ }
    }
}
