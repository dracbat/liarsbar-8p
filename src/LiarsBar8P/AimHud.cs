using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Says who you are about to shoot, in words, while you are choosing.
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
/// is a different bug rather than a fixed one.
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

    private static string _line;

    private float _next;
    private static CharController _me;
    private static float _meAt = -999f;

    private GUIStyle _style;
    private Texture2D _backdrop;

    private void Update()
    {
        if (Time.time < _next) return;
        _next = Time.time + RefreshSeconds;

        try { _line = Line(); }
        catch { _line = null; }
    }

    private static int _lastAim = int.MinValue;
    private static string _lastLine;

    private static string Line()
    {
        var me = Local();
        if (me == null) { _lastAim = int.MinValue; return null; }
        if (!AimRing.Choosing(me)) { _lastAim = int.MinValue; return null; }

        // Only work out who that is when the aim has actually moved. Answering the question
        // costs a walk of the roster - the game rebuilds it from a scene scan on every
        // lookup - and asking twenty times a second for an answer that changes on a keypress
        // would spend that during the aiming phase, which is the worst moment to spend it.
        int aim = AimRing.CurrentAim(me);
        if (aim == _lastAim) return _lastLine;
        _lastAim = aim;

        var target = AimRing.AimingAt(me);
        if (target == null) { _lastLine = "Aiming at nobody"; return _lastLine; }

        string name = target.PlayerName;
        if (string.IsNullOrEmpty(name)) name = $"seat {target.Slot + 1}";
        _lastLine = $"Aiming at {name}";
        return _lastLine;
    }

    /// <summary>
    /// The aiming component belonging to the player sitting at this computer.
    ///
    /// Ownership is the test rather than the name: the aiming code itself decides where to
    /// send a keypress by asking <c>isOwned</c> on this very component, so if it were not
    /// owned here the aim would not move in the first place and there would be nothing to
    /// report. Cached, because this is asked twenty times a second and a scene scan is not.
    ///
    /// The cached reference is re-checked rather than trusted: between rounds the player
    /// object is replaced, and a destroyed object is still a non-null C# reference while being
    /// null under Unity's own operator.
    /// </summary>
    private static CharController Local()
    {
        if (_me != null && Time.time - _meAt < LookupSeconds) return _me;

        _meAt = Time.time;
        _me = null;

        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<CharController>();
            if (all == null) return null;

            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null) continue;
                try { if (!c.isOwned) continue; }
                catch { continue; }
                if (c.playerStats == null) continue;
                _me = c;
                return _me;
            }
        }
        catch { }

        return null;
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

                _backdrop = new Texture2D(1, 1);
                _backdrop.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
                _backdrop.Apply();
            }

            // Just under the middle of the screen: where the player is already looking while
            // turning the gun, and clear of the cards along the bottom and the round text at
            // the top.
            Vector2 size = _style.CalcSize(new GUIContent(_line));
            float x = (Screen.width - size.x) * 0.5f;
            float y = Screen.height * 0.62f;

            GUI.DrawTexture(new Rect(x - 16f, y - 6f, size.x + 32f, size.y + 12f), _backdrop);
            GUI.Label(new Rect(x, y, size.x, size.y), _line, _style);
        }
        catch { /* never let a HUD draw break the game */ }
    }
}
