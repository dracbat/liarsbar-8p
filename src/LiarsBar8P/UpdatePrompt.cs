using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LiarsBar8P;

/// <summary>
/// The window that asks whether to install a newer version of the mod, when <c>Updates</c> is
/// <c>Ask</c> (see <see cref="AutoUpdate"/>), and that restarts the game once a yes is in.
///
/// It only ever goes up at the main menu - no table, not hosting, not connected to anybody -
/// so it can never get in the way of a lobby or a match: if the check finishes, or the download
/// does, while the player is somewhere else, it waits until they are back. After a yes it goes
/// away at once; the download runs in the background with its progress in the line at the top
/// of the screen, and the window comes back only to count down to the restart.
///
/// While it is up, the game's own menus are switched off underneath it, so a click on one of
/// its buttons cannot also press whatever button of the game's is behind it; the moment it goes
/// they are back. It can be answered with the mouse, the keyboard (Y or Enter for yes, N for
/// no) or a controller (A for yes, B for no) - the game's own menu navigation, which a
/// controller would otherwise use, is one of the things switched off.
///
/// If drawing it ever fails, it is put away for good and the menus switched back on: a window
/// nobody can close, over a menu nobody can click, would be far worse than no window.
/// </summary>
internal static class UpdatePrompt
{
    private static bool _automatic, _off;   // the two tick boxes
    private static bool _broken;
    private static readonly List<EventSystem> _paused = new();

    // After a yes, the countdown to the restart: long enough to read the window and pick a button.
    private const float RestartDelay = 10f;
    private static float _countFrom = -1f;
    private static bool _restartTried;

    private static int _polled = -1, _answer;
    private static bool _sceneLogged;

    private static GUIStyle _title, _text, _small;
    private static Texture2D _shade, _panel;

    /// <summary>Whether the window was up this frame, so the HUD can put it in front of everything.</summary>
    internal static bool Showing { get; private set; }

    /// <summary>From <see cref="VersionHud"/>'s OnGUI, for every GUI event. Never throws.</summary>
    internal static void Draw()
    {
        try
        {
            var stage = AutoUpdate.Current;
            bool countdown = stage == AutoUpdate.Stage.Installed && AutoUpdate.AcceptedByPlayer &&
                             AutoUpdate.InPlace && !_restartTried;
            bool show = !_broken && (stage == AutoUpdate.Stage.Asking || countdown) && AtMenu();

            Hold(show);
            Showing = show;
            if (!show)
            {
                _countFrom = -1f;   // away from the menu: the countdown starts again on the way back
                return;
            }

            if (!_sceneLogged)
            {
                _sceneLogged = true;
                Plugin.Log.LogInfo($"[update] update window up (scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}')");
            }
            DrawWindow(stage);
        }
        catch (Exception e)
        {
            _broken = true;
            Showing = false;
            try { Hold(false); } catch { }
            Plugin.Log.LogWarning($"[update] the update window failed and has been put away ({e.Message}) - " +
                                  "set Updates in the config to Automatic or Off instead");
        }
    }

    /// <summary>The main menu: no table, not hosting, not connected. The only place the window goes up.</summary>
    private static bool AtMenu()
    {
        try
        {
            if (NetworkServer.active || NetworkClient.active) return false;
            return Manager.Instance == null;
        }
        catch { return false; }
    }

    /// <summary>While the window is up, the game's menus are off; back on the moment it goes.</summary>
    private static void Hold(bool on)
    {
        if (on)
        {
            // Checked every time: a new scene brings a new set of menus with it.
            var events = EventSystem.current;
            if (events != null && events.enabled)
            {
                events.enabled = false;
                _paused.Add(events);
            }
            return;
        }

        if (_paused.Count > 0)
        {
            foreach (var events in _paused)
                if (events != null) events.enabled = true;
            _paused.Clear();
        }
    }

    /// <summary>
    /// A yes (1) or a no (-1) from the keyboard or a controller this frame, or 0. Read once a
    /// frame: the GUI is drawn several times a frame, and each must see the same answer.
    /// </summary>
    private static int Answer()
    {
        if (_polled != Time.frameCount)
        {
            _polled = Time.frameCount;
            _answer = Input.GetKeyDown(KeyCode.Y) || Input.GetKeyDown(KeyCode.Return) ||
                      Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.JoystickButton0) ? 1
                    : Input.GetKeyDown(KeyCode.N) || Input.GetKeyDown(KeyCode.JoystickButton1) ? -1
                    : 0;
        }
        return _answer;
    }

    private static void DrawWindow(AutoUpdate.Stage stage)
    {
        Styles();

        // Laid out for a 1080-line screen and scaled to the real one, so that it reads the same
        // on a Steam Deck as on a 4K monitor. The scaling applies to the clicks as well.
        float scale = Mathf.Clamp(Screen.height / 1080f, 0.7f, 3f);
        var keep = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        try
        {
            float sw = Screen.width / scale, sh = Screen.height / scale;
            GUI.DrawTexture(new Rect(0f, 0f, sw, sh), _shade);

            bool asking = stage == AutoUpdate.Stage.Asking;
            const float w = 560f, pad = 22f;
            float h = asking ? 300f : 200f;
            float x = Mathf.Round((sw - w) / 2f), y = Mathf.Round((sh - h) / 2f);
            GUI.DrawTexture(new Rect(x, y, w, h), _panel);

            float cx = x + pad, cw = w - 2f * pad, cy = y + pad;
            var left = new Rect(cx + cw / 2f - 200f, 0f, 190f, 32f);
            var right = new Rect(cx + cw / 2f + 10f, 0f, 190f, 32f);
            var offered = AutoUpdate.Offered;

            if (asking)
            {
                GUI.Label(new Rect(cx, cy, cw, 26f), "Liar's Bar 8 Player Mod - update available", _title);
                cy += 40f;
                GUI.Label(new Rect(cx, cy, cw, 72f),
                    $"v{offered} is out. You have v{Plugin.Version}.\n" +
                    "Everyone at a table needs the same version.\n" +
                    "Install it? The game restarts by itself to load it.", _text);
                cy += 80f;

                left.y = right.y = cy;
                int key = Answer();
                if (GUI.Button(left, "Yes, install   (Y / A)") || key > 0) AutoUpdate.Accept(Choice());
                else if (GUI.Button(right, "Not now   (N / B)") || key < 0) AutoUpdate.Decline(Choice());
                cy += 48f;

                // Either, or neither: ticking one clears the other.
                bool automatic = GUI.Toggle(new Rect(cx, cy, cw, 22f), _automatic, " Install updates automatically from now on");
                if (automatic != _automatic) { _automatic = automatic; if (automatic) _off = false; }
                cy += 26f;
                bool off = GUI.Toggle(new Rect(cx, cy, cw, 22f), _off, " Don't check for updates");
                if (off != _off) { _off = off; if (off) _automatic = false; }
                if (_off)
                    GUI.Label(new Rect(cx + 18f, cy + 22f, cw - 18f, 20f),
                              "Updates in the mod's config file turns checks back on, and so does reinstalling the mod.", _small);
                return;
            }

            // The countdown after a yes: a few seconds to read what is happening, then the restart.
            GUI.Label(new Rect(cx, cy, cw, 26f), "Liar's Bar 8 Player Mod - update installed", _title);
            cy += 40f;
            if (_countFrom < 0f) _countFrom = Time.realtimeSinceStartup;
            float remaining = RestartDelay - (Time.realtimeSinceStartup - _countFrom);
            GUI.Label(new Rect(cx, cy, cw, 70f),
                $"v{offered} is installed. Restarting the game to load it in {Mathf.Max(1, Mathf.CeilToInt(remaining))}..." + Future(),
                _text);

            left.y = right.y = y + h - pad - 32f;
            int answer = Answer();
            bool now = GUI.Button(left, "Restart now   (Y / A)") || answer > 0 ||
                       (remaining <= 0f && Event.current.type == EventType.Repaint);
            bool later = !now && (GUI.Button(right, "Restart later   (N / B)") || answer < 0);

            if (now)
            {
                _restartTried = true;
                if (!AutoUpdate.Restart()) AutoUpdate.RestartFailed();
            }
            else if (later)
            {
                _restartTried = true;
                AutoUpdate.RestartLater();
            }
        }
        finally { GUI.matrix = keep; }
    }

    private static UpdateMode? Choice() => _automatic ? UpdateMode.Automatic : _off ? UpdateMode.Off : null;

    private static UpdateMode Mode() => Plugin.Updates != null ? Plugin.Updates.Value : UpdateMode.Ask;

    /// <summary>A line for what the tick boxes changed, if they changed anything.</summary>
    private static string Future() => Mode() switch
    {
        UpdateMode.Automatic => "\nFuture updates will install by themselves.",
        UpdateMode.Off => "\nThe mod will not check for updates again unless it is reinstalled or Updates is changed in its config.",
        _ => "",
    };

    private static void Styles()
    {
        if (_title == null)
        {
            _title = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold, wordWrap = false };
            _title.normal.textColor = new Color(0.98f, 0.82f, 0.33f, 1f);   // the emote panel's yellow
            _text = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            _text.normal.textColor = new Color(1f, 1f, 1f, 0.92f);
            _small = new GUIStyle(_text) { fontSize = 12, wordWrap = false };
            _small.normal.textColor = new Color(1f, 1f, 1f, 0.6f);
        }

        // Made again if a scene change has thrown them away.
        if (_shade == null) _shade = Solid(new Color(0f, 0f, 0f, 0.45f));
        if (_panel == null) _panel = Solid(new Color(0.07f, 0.06f, 0.05f, 0.94f));
    }

    private static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }
}
