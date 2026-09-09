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

    /// <summary>
    /// Seconds between routine shots, from the config. Zero — the default — turns the timer
    /// off, which is what it must be while somebody is actually playing: each shot is a few
    /// megabytes and a visible pause. Only a deliberate test run wants a picture record.
    /// </summary>
    private static float Every
    {
        get
        {
            // The harness asks out of band, for the same reason it asks for developer mode
            // that way: the config file is rewritten to its shipped defaults by an install,
            // and a run that quietly took no pictures looks exactly like one that did.
            if (_asked == null)
            {
                _asked = 0f;
                try
                {
                    string raw = Environment.GetEnvironmentVariable("LIARSBAR8P_SHOTSEVERY");
                    if (!string.IsNullOrEmpty(raw) &&
                        float.TryParse(raw, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out float v) &&
                        v > 0f)
                        _asked = v;
                }
                catch { }
            }

            if (_asked.Value > 0f) return _asked.Value;
            return Plugin.DevShotSeconds != null ? Plugin.DevShotSeconds.Value : 0f;
        }
    }

    private static float? _asked;

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

                // A folder per run, because the file names are a counter that restarts at 001
                // with the process. One fixed folder meant a second test run overwrote the
                // first run's pictures in place - destroying the record this exists to keep,
                // and doing it silently. The run is stamped under the harness variable too,
                // or several copies started together would collide there instead.
                root = System.IO.Path.Combine(
                    root,
                    $"{DateTime.Now:yyyyMMdd-HHmmss}-{System.Diagnostics.Process.GetCurrentProcess().Id}");

                System.IO.Directory.CreateDirectory(root);
                PruneOldRuns(System.IO.Directory.GetParent(root));
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

    /// <summary>
    /// Keep only the newest few runs. A folder per run stops one test overwriting another,
    /// but it also means they pile up, and at a few megabytes a shot that is hundreds of
    /// megabytes nothing ever removes. Only reached when screenshots are switched on at all,
    /// which is off by default.
    /// </summary>
    private const int KeepRuns = 5;

    /// <summary>Whether the joining copies of a loopback test should photograph themselves too.</summary>
    private static bool ClientShotsWanted
    {
        get
        {
            try
            {
                string v = Environment.GetEnvironmentVariable("LIARSBAR8P_SHOTS_CLIENTS");
                return !string.IsNullOrEmpty(v) && v != "0";
            }
            catch { return false; }
        }
    }

    private static void PruneOldRuns(System.IO.DirectoryInfo parent)
    {
        try
        {
            if (parent == null || !parent.Exists) return;

            // Only ever folders this class made, matched by the exact shape it names them.
            // The screenshot root can be pointed anywhere by LIARSBAR8P_SHOTS, and deleting
            // "the oldest directories next to it" would then recursively delete whatever else
            // happened to be in that folder. A recursive delete has to be incapable of
            // touching anything it did not create.
            var runs = new System.Collections.Generic.List<System.IO.DirectoryInfo>();
            foreach (var d in parent.GetDirectories())
                if (IsRunFolder(d.Name)) runs.Add(d);

            if (runs.Count <= KeepRuns) return;

            runs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = KeepRuns; i < runs.Count; i++)
                try { runs[i].Delete(true); } catch { }
        }
        catch { }
    }

    /// <summary>Exactly the shape <c>Folder</c> creates: yyyyMMdd-HHmmss-pid.</summary>
    private static bool IsRunFolder(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var parts = name.Split('-');
        if (parts.Length != 3) return false;
        if (parts[0].Length != 8 || parts[1].Length != 6) return false;
        foreach (var part in parts)
            foreach (char ch in part)
                if (!char.IsDigit(ch)) return false;
        return parts[2].Length > 0;
    }

    /// <summary>Take one now, tagged with what was happening.</summary>
    internal static void Take(string tag)
    {
        // Off unless a picture record was actually asked for. Developer mode is for playing
        // with the panel and the log, and it should not start writing megabytes to disk and
        // stuttering once a round just because it is on.
        if (!Dev.Enabled || Every <= 0f) return;

        // In a loopback test the joining copies are 640x400 windows nobody is watching, and
        // they all read the same config - so without this, five copies photograph themselves
        // at once. Only the host's view is normally worth keeping.
        //
        // Except when what is being tested is what a *client* sees, which is where the seating
        // bugs lived: the host's screen looked right for weeks while everyone else was looking
        // at a table laid out for four. Set LIARSBAR8P_SHOTS_CLIENTS=1 to photograph the
        // joining copies too - each writes to its own run folder, so they cannot collide.
        if (Loopback.Mine == Loopback.Role.Client && !ClientShotsWanted) return;

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
