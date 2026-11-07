using System;
using System.IO;
using System.Runtime.InteropServices;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiarsBar8P;

/// <summary>
/// The one button the mod keeps when it is turned off: FULLY LOADED: ON or OFF, on the main menu,
/// under the game's own buttons and looking like them (a clean copy of one, <see cref="UiClone"/>).
///
/// It is the whole mod while FullyLoaded is off (<see cref="Plugin.Off"/>): no patch, no lobby key,
/// no update check, nothing at a table. The game then plays as the base game does, ranked included,
/// and a modded host turns the copy away like any copy without the mod.
///
/// Two clicks, as the block buttons do it: the first asks, the second within a few seconds writes
/// the setting and starts the game again, because a mod that is already loaded cannot take itself
/// back out. The restart is the updater's own way of doing it (<see cref="Restart"/>), and like it,
/// the old copy closes as soon as the new one has been started, so there is never a second copy.
/// </summary>
internal static class FullyLoadedSwitch
{
    // Every word a player sees from this class. {0} is the mod's name in capitals, which stays.
    private const string OnWords = "{0}: ON";
    private const string OffWords = "{0}: OFF";
    private const string AskOff = "TURN OFF? CLICK AGAIN";
    private const string AskOn = "TURN ON? CLICK AGAIN";
    private const string Restarting = "RESTARTING...";
    private const string ByHand = "RESTART THE GAME TO FINISH";

    private const string Scene = "SteamTest";
    private const string MenuPath = "state_menu";
    private const string LikeName = "btn_sins";     // a plain button: its own click goes with the cleaning
    private const string LastName = "btn_leave";    // the lowest of the game's buttons; this one goes under it
    private const string AboveName = "btn_sins";    // the one above that, for the gap between them
    internal const string Name = "LB8P_FullyLoaded";

    /// <summary>How long the first click waits for the second.</summary>
    private const float ArmSeconds = 4f;

    private static GameObject _button;
    private static TMP_Text _text;
    private static IntPtr _on;
    private static float _armedUntil = -1f;
    private static bool _restarting, _failed, _missing;

    /// <summary>From <see cref="ModTicker"/>, in both modes.</summary>
    internal static void Tick()
    {
        var menu = Menu();
        if (menu == null) return;

        if (_button == null || _on != menu.Pointer)
        {
            Build(menu);
            if (_button == null) return;
        }

        Place(menu);

        string words = Words();
        if (_text != null && _text.text != words) _text.text = words;
    }

    /// <summary>The main menu, while it shows, with no table and no connection.</summary>
    private static Transform Menu()
    {
        if (NetworkClient.active || NetworkServer.active) return null;
        if (SceneManager.GetActiveScene().name != Scene) return null;

        var list = LobbyListManager.instance;
        var box = list != null ? list.ConnectingScreen : null;
        var panel = box != null ? box.transform.parent : null;
        var canvas = panel != null ? panel.parent : null;
        var menu = canvas != null ? canvas.Find(MenuPath) : null;
        return menu != null && menu.gameObject.activeInHierarchy ? menu : null;
    }

    private static void Build(Transform menu)
    {
        _button = null;
        _text = null;

        var like = menu.Find(LikeName);
        if (like == null)
        {
            if (!_missing) Plugin.Log.LogWarning($"[switch] no {LikeName} on the main menu to copy - the switch is not shown");
            _missing = true;
            return;
        }

        // UISlide goes too: it moves a button about by itself, and this one sits where it is put.
        var copy = UiClone.Make(like.gameObject, menu, Name, Click, "UISlide");
        if (copy == null) return;

        var text = copy.GetComponent<TMP_Text>() ?? copy.GetComponentInChildren<TMP_Text>(true);
        if (text == null)
        {
            UnityEngine.Object.Destroy(copy);
            return;
        }

        // The game's own font and size; only made smaller when a language's words would not fit.
        text.richText = false;
        text.enableWordWrapping = false;
        text.fontSizeMax = text.fontSize;
        text.fontSizeMin = text.fontSize * 0.5f;
        text.enableAutoSizing = true;

        _button = copy;
        _text = text;
        _on = menu.Pointer;
        Plugin.Log.LogInfo($"[switch] the {Plugin.TagName} switch is on the main menu ({(Plugin.Off ? "off" : "on")})");
    }

    /// <summary>Under the game's last button, one of its own gaps further down. Each tick, so it follows them.</summary>
    private static void Place(Transform menu)
    {
        var last = menu.Find(LastName)?.GetComponent<RectTransform>();
        var above = menu.Find(AboveName)?.GetComponent<RectTransform>();
        var mine = _button.GetComponent<RectTransform>();
        if (last == null || mine == null) return;

        float gap = above != null ? above.anchoredPosition.y - last.anchoredPosition.y : 70f;
        if (gap < 20f || gap > 200f) gap = 70f;
        var at = new Vector2(last.anchoredPosition.x, last.anchoredPosition.y - gap);
        if (mine.anchoredPosition != at) mine.anchoredPosition = at;
    }

    private static string Words()
    {
        if (_failed) return LiarsBar8P.Words.T(ByHand);
        if (_restarting) return LiarsBar8P.Words.T(Restarting);
        if (Time.unscaledTime < _armedUntil) return LiarsBar8P.Words.T(Plugin.Off ? AskOn : AskOff);
        return LiarsBar8P.Words.F(Plugin.Off ? OffWords : OnWords, Plugin.TagName);
    }

    private static void Click()
    {
        try
        {
            if (_restarting || Plugin.FullyLoaded == null) return;

            if (Time.unscaledTime >= _armedUntil)
            {
                _armedUntil = Time.unscaledTime + ArmSeconds;
                return;
            }
            _armedUntil = -1f;

            bool on = Plugin.Off;   // what it becomes: the other of what is running now
            Plugin.FullyLoaded.Value = on;
            Plugin.Log.LogInfo($"[switch] {Plugin.DisplayName} turned {(on ? "on" : "off")} from the main menu - restarting the game");

            _restarting = true;
            if (!Restart()) _failed = true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[switch] the click failed: {e.Message}");
            _failed = _restarting;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetCommandLineW();

    /// <summary>
    /// The same restart the updater does (AutoUpdate.Restart): the game's own exe with this copy's
    /// command line and environment, without the loader's marks, and then this copy closes. It
    /// does not say an update happened: the updater's mark is taken out, not put in, so the next
    /// start shows no changelog and checks for updates as on any other start.
    /// </summary>
    private static bool Restart()
    {
        try
        {
            string exe = BepInEx.Paths.ExecutablePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                Plugin.Log.LogWarning("[switch] cannot restart the game: its exe was not found - restart it by hand");
                return false;
            }

            string args = Arguments(Marshal.PtrToStringUni(GetCommandLineW()) ?? "");
            var start = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };

            // Left in, the loader's marks start the new copy without the mod at all (see AutoUpdate.Restart).
            var keys = new System.Collections.Generic.List<string>();
            foreach (string key in start.Environment.Keys)
                if (key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase)) keys.Add(key);
            foreach (string key in keys) start.Environment.Remove(key);
            start.Environment.Remove("LIARSBAR8P_RESTARTED");

            System.Diagnostics.Process.Start(start);

            int words = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            Plugin.Log.LogInfo($"[switch] restarting the game ({words} launch word(s) passed on; " +
                               $"left out of its environment: {(keys.Count > 0 ? string.Join(", ", keys) : "nothing")})");
            UnityEngine.Application.Quit();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[switch] could not restart the game ({e.Message}) - restart it by hand");
            return false;
        }
    }

    /// <summary>A command line without its first word - the program's own path, quoted or not.</summary>
    private static string Arguments(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        if (i < line.Length && line[i] == '"')
        {
            int close = line.IndexOf('"', i + 1);
            i = close < 0 ? line.Length : close + 1;
        }
        else
        {
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        }
        return line.Substring(i).Trim();
    }
}
