using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Takes screenshots from inside the game, for developer use.
///
/// Everything visual about this mod — where the podiums stand, whether the lobby camera can
/// see the back row, which player the table arrow points at, whether a hand of cards is
/// actually in somebody's hands — can only be judged by looking. Capturing the screen from
/// outside the game turned out not to be an option: the tooling that does it is
/// indistinguishable from screen-scraping malware and gets blocked.
///
/// Capturing from inside is better anyway. It photographs the game rather than the desktop,
/// it works whether or not the window has focus, and it can fire at a moment that means
/// something — a round starting, the turn moving — instead of on a wall clock and hoping.
///
/// Developer-only, and off unless <c>DeveloperMode</c> is set.
/// </summary>
internal static class DevShots
{
    /// <summary>Where the pictures go. Named per run so one session does not overwrite another.</summary>
    private static string _folder;
    private static int _count;
    private static float _next;
    private static string _pending;

    /// <summary>Seconds between routine shots. Zero turns the timer off.</summary>
    internal static float Every = 5f;

    private static string Folder
    {
        get
        {
            if (_folder != null) return _folder;
            try
            {
                string root = Environment.GetEnvironmentVariable("LIARSBAR8P_SHOTS");
                if (string.IsNullOrEmpty(root))
                    root = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LiarsBar8P", "shots");
                System.IO.Directory.CreateDirectory(root);
                _folder = root;
                Plugin.Log.LogInfo($"[shots] screenshots go to {root}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[shots] no folder to write to: {e.Message}");
                _folder = "";
            }
            return _folder;
        }
    }

    /// <summary>Take one now, tagged with what was happening.</summary>
    internal static void Take(string tag)
    {
        if (!Dev.Enabled) return;
        try
        {
            string dir = Folder;
            if (string.IsNullOrEmpty(dir)) return;

            _count++;
            string safe = Sanitise(tag);
            string path = System.IO.Path.Combine(dir, $"{_count:d3}_{safe}.png");

            // Unity writes this at the end of the frame, so the file appears a moment later.
            ScreenCapture.CaptureScreenshot(path);
            _pending = path;
            Dev.Log("shots", $"{_count:d3} {tag}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[shots] could not capture '{tag}': {e.Message}");
        }
    }

    /// <summary>A routine shot every few seconds, so a run has a record even between events.</summary>
    internal static void Tick()
    {
        if (!Dev.Enabled || Every <= 0f) return;
        if (Time.time < _next) return;
        _next = Time.time + Every;

        // Only while something is happening; the menu is not worth photographing.
        try
        {
            bool interesting = Manager.Instance != null || LobbyController.Instance != null;
            if (!interesting) return;
        }
        catch { return; }

        Take(Where());
    }

    /// <summary>A short word for what is on screen, so a file name is readable at a glance.</summary>
    private static string Where()
    {
        try
        {
            var m = Manager.Instance;
            if (m != null)
            {
                int players = m.Players != null ? m.Players.Count : 0;
                return $"table_{players}p_slot{m.ActivePlayerSlot}";
            }
            if (LobbyController.Instance != null)
            {
                var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
                int n = nm != null && nm.GamePlayers != null ? nm.GamePlayers.Count : 0;
                return $"lobby_{n}p";
            }
        }
        catch { }
        return "scene";
    }

    private static string Sanitise(string s)
    {
        if (string.IsNullOrEmpty(s)) return "shot";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.ToString();
    }
}
