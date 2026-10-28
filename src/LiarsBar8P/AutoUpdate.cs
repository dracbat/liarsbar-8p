using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiarsBar8P;

/// <summary>What to do about a newer release of this mod: the <c>Updates</c> setting.</summary>
public enum UpdateMode
{
    /// <summary>A window in the game asks whether to install it.</summary>
    Ask,

    /// <summary>Install it without asking.</summary>
    Automatic,

    /// <summary>Never check.</summary>
    Off,
}

/// <summary>
/// Keeps the mod up to date: each time the game starts it asks this mod's GitHub releases for
/// the newest version, and if it is newer than the one running, offers it - or, if the player
/// has said so, installs it straight away - to load the next time the game starts.
///
/// Every player at a table has to run the same version, and the slowest way to get a fix to
/// people was the mod site: a new upload waits days for its files to be checked, so players
/// there were always a release or more behind. With this, a copy from anywhere catches up.
///
/// <b>Whose decision.</b> <c>Updates</c> in the config: <c>Ask</c> (the default) puts a window
/// up in the game - see <see cref="UpdatePrompt"/> - saying a newer version is out and asking
/// whether to install it: yes downloads it and restarts the game with it (<see cref="Restart"/>),
/// "not now" asks again next time the game starts. <c>Automatic</c>
/// installs without asking, and <c>Off</c> never checks. Two tick boxes in the window switch
/// to either, so nobody has to find the config file to stop being asked.
///
/// What it accepts, so a download can only ever be this mod's own release:
///   - the release has to be this repository's, and the file a <c>github.com/dracbat/liarsbar-8p/
///     releases/download/</c> link, fetched over HTTPS;
///   - only the plugin itself is taken from the release zip - BepInEx, its settings and every
///     other file are left alone;
///   - the file has to be a .NET assembly named LiarsBar8P whose own version is exactly the
///     release's, and newer than the one running.
///
/// The running copy cannot be overwritten - the game has it open - so it is renamed aside and
/// the new one written in its place, to load the next time the game starts; if the rename is
/// refused, the new one waits beside it and is moved in when the game closes. A line at the top
/// of the screen says an update is ready and to restart. Never during a test run of the mod,
/// and never when the version running is newer than the newest release (a build being tested).
/// </summary>
internal static class AutoUpdate
{
    private const string Repo = "dracbat/liarsbar-8p";
    private const string Latest = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    private const string ZipName = "LiarsBar-8P.zip";
    private const string PluginEntry = "BepInEx/plugins/LiarsBar8P.dll";

    /// <summary>The release zip carries the whole loader as well; anything far past that is not it.</summary>
    private const long MostZipBytes = 200L * 1024 * 1024;

    /// <summary>Shown under the version line once an update is in place. Set from a worker thread.</summary>
    internal static volatile string Note;

    /// <summary>How far an offered update has got, for the window.</summary>
    internal enum Stage { None, Asking, Installing, Installed, Failed }

    private static volatile Stage _stage;

    /// <summary>Where the offer stands. Written from a worker thread, read by the window.</summary>
    internal static Stage Current => _stage;

    /// <summary>The release on offer, once <see cref="Current"/> has left None.</summary>
    internal static Version Offered { get; private set; }

    /// <summary>How much of the download is in, 0 to 1, or below 0 while that is not known.</summary>
    internal static volatile float Progress = -1f;

    /// <summary>Why the last install did not go in, in a few words, for the window.</summary>
    internal static volatile string Problem;

    /// <summary>Whether the update just installed went straight into place, so a restart loads it.</summary>
    internal static volatile bool InPlace;

    /// <summary>Whether the player said yes in the window - only then does the game restart by itself.</summary>
    internal static volatile bool AcceptedByPlayer;

    private static string _offeredUrl;
    private static string _here;
    private static string _pending;   // new plugin waiting to be moved in when the game closes

    internal static void Start()
    {
        try
        {
            string here = typeof(Plugin).Assembly.Location;
            if (string.IsNullOrEmpty(here)) return;
            _here = here;

            // Leftovers from an update that has now been loaded.
            TryDelete(here + ".old");

            string before = Environment.GetEnvironmentVariable("LIARSBAR8P_RESTARTED");
            if (!string.IsNullOrEmpty(before))
                Plugin.Log.LogInfo($"[update] started again by the update from v{before} - running v{Plugin.Version} now");

            var mode = Plugin.Updates != null ? Plugin.Updates.Value : UpdateMode.Off;
            if (mode == UpdateMode.Off)
            {
                Plugin.Log.LogInfo("[update] not checking for updates (Updates = Off in the config)");
                return;
            }
            if (Dev.Enabled || Loopback.Active) return;   // a test run must keep the build it was given

            Task.Run(() => Check(here, mode));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[update] could not start the update check: {e.Message}"); }
    }

    private static async Task Check(string here, UpdateMode mode)
    {
        try
        {
            using var http = Client(TimeSpan.FromSeconds(20));
            string json = await http.GetStringAsync(Latest).ConfigureAwait(false);
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?(\\d+\\.\\d+\\.\\d+)\"");
            if (!tag.Success) { Plugin.Log.LogWarning("[update] the latest release has no version tag - not updating"); return; }

            var newest = Version.Parse(tag.Groups[1].Value);
            var running = Version.Parse(Plugin.Version);
            if (newest <= running)
            {
                Plugin.Log.LogInfo($"[update] up to date (v{Plugin.Version}; newest release v{newest})");
                return;
            }

            string url = null;
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string u = m.Groups[1].Value;
                if (u.StartsWith(DownloadPrefix, StringComparison.Ordinal) &&
                    u.EndsWith("/" + ZipName, StringComparison.Ordinal)) { url = u; break; }
            }
            if (url == null) { Plugin.Log.LogWarning($"[update] v{newest} has no {ZipName} from this mod's releases - not updating"); return; }

            if (mode == UpdateMode.Automatic)
            {
                Plugin.Log.LogInfo($"[update] v{newest} is out (running v{Plugin.Version}) - installing it (Updates = Automatic)");
                await Install(here, newest, url).ConfigureAwait(false);
                return;
            }

            _offeredUrl = url;
            Offered = newest;
            _stage = Stage.Asking;   // last: the window reads the offer once it sees this
            Plugin.Log.LogInfo($"[update] v{newest} is out (running v{Plugin.Version}) - asking whether to install it");
        }
        catch (Exception e)
        {
            Plugin.Log.LogInfo($"[update] could not check for an update this time ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>
    /// "Yes" in the window: install the release on offer. <paramref name="fromNow"/> is what the
    /// tick boxes asked for in future, or null to leave <c>Updates</c> as it is.
    /// </summary>
    internal static void Accept(UpdateMode? fromNow)
    {
        if (_stage != Stage.Asking) return;
        Remember(fromNow);

        var newest = Offered;
        string url = _offeredUrl, here = _here;
        Problem = null;
        Progress = -1f;
        AcceptedByPlayer = true;
        _stage = Stage.Installing;
        Plugin.Log.LogInfo($"[update] v{newest}: the player said yes - downloading");

        Task.Run(async () =>
        {
            bool ok = await Install(here, newest, url).ConfigureAwait(false);
            _stage = ok ? Stage.Installed : Stage.Failed;
        });
    }

    /// <summary>"Restart later" after a yes: the update stays installed and loads at the next start.</summary>
    internal static void RestartLater()
    {
        if (_stage != Stage.Installed) return;
        _stage = Stage.None;
        Plugin.Log.LogInfo($"[update] v{Offered}: restart put off - it loads the next time the game starts");
    }

    /// <summary>The game could not start itself again: say so where the player will see it.</summary>
    internal static void RestartFailed()
    {
        _stage = Stage.None;
        Note = $"Liar's Bar 8P v{Offered} is installed, but the game could not restart by itself - restart it to use the new version";
    }

    /// <summary>"Not now": put the window away. Nothing is remembered, so it asks again next time.</summary>
    internal static void Decline(UpdateMode? fromNow)
    {
        if (_stage != Stage.Asking) return;
        Remember(fromNow);
        _stage = Stage.None;
        Plugin.Log.LogInfo($"[update] v{Offered}: the player said not now - it is offered again next time the game starts");
    }

    private static void Remember(UpdateMode? fromNow)
    {
        if (fromNow == null || Plugin.Updates == null) return;
        try
        {
            Plugin.Updates.Value = fromNow.Value;   // written to the config file straight away
            Plugin.Log.LogInfo($"[update] Updates is now {fromNow.Value}, chosen in the update window");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[update] could not save the update setting: {e.Message}"); }
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiarsBar8P-updater");
        return http;
    }

    /// <summary>
    /// Download the release, take the plugin out of it, check it is this mod at exactly that
    /// version, and put it in place for the next start. True if it went in, or is waiting to
    /// go in when the game closes.
    /// </summary>
    private static async Task<bool> Install(string here, Version newest, string url)
    {
        try
        {
            // The zip carries the loader and its runtime as well as the plugin - tens of
            // megabytes - so the download gets far longer than the twenty seconds the version
            // check does. Twenty seconds was all a slow connection got for the whole of it.
            byte[] zip;
            using (var http = Client(TimeSpan.FromMinutes(10)))
            using (var answer = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                answer.EnsureSuccessStatusCode();
                long total = answer.Content.Headers.ContentLength ?? -1;
                if (total > MostZipBytes) return Fail($"the release zip is {total / (1024 * 1024)} MB, far bigger than it should be");

                using var body = await answer.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int read, shown = -1;
                Note = $"Downloading Liar's Bar 8P v{newest}...";
                while ((read = await body.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > MostZipBytes) return Fail("the release zip is far bigger than it should be");

                    // The line at the top of the screen, which never blocks anything, shows how far it has got.
                    int now = total > 0 ? (int)(100 * ms.Length / total) : (int)(ms.Length >> 20);
                    if (now != shown)
                    {
                        shown = now;
                        if (total > 0) Progress = (float)ms.Length / total;
                        Note = total > 0 ? $"Downloading Liar's Bar 8P v{newest}... {now}%"
                                         : $"Downloading Liar's Bar 8P v{newest}... {now} MB";
                    }
                }
                zip = ms.ToArray();
            }

            byte[] dll;
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry(PluginEntry);
                if (entry == null || entry.Length < 20_000 || entry.Length > 50_000_000)
                    return Fail("the release zip has no usable plugin");
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                dll = ms.ToArray();
            }

            string fresh = here + ".new";
            File.WriteAllBytes(fresh, dll);

            // It has to be this mod, at exactly the version the release says.
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(fresh); }
            catch { TryDelete(fresh); return Fail("the downloaded plugin is not a .NET assembly"); }
            if (name.Name != "LiarsBar8P" || name.Version == null ||
                new Version(name.Version.Major, name.Version.Minor, name.Version.Build) != newest)
            {
                TryDelete(fresh);
                return Fail($"the downloaded plugin is {name.Name} {name.Version}, not LiarsBar8P {newest}");
            }

            try
            {
                TryDelete(here + ".old");
                File.Move(here, here + ".old");   // allowed while loaded; overwriting is not
                File.Move(fresh, here);
                InPlace = true;
                Note = $"Liar's Bar 8P v{newest} is installed - restart the game to use it";
                Plugin.Log.LogInfo($"[update] v{newest} installed - it loads the next time the game starts");
            }
            catch (Exception e)
            {
                if (!File.Exists(here) && File.Exists(here + ".old")) File.Move(here + ".old", here);
                InPlace = false;
                _pending = fresh;
                Note = $"Liar's Bar 8P v{newest} is ready - it installs when you close the game";
                Plugin.Log.LogInfo($"[update] v{newest} downloaded; the running copy could not be moved aside ({e.Message}) - it goes in when the game closes");
            }
            return true;
        }
        catch (Exception e)
        {
            return Fail($"{e.GetType().Name}: {e.Message}");
        }

        bool Fail(string why)
        {
            why = why.TrimEnd('.');
            Problem = why;
            Plugin.Log.LogWarning($"[update] not updating: {why}");

            string shortWhy = why.Length > 90 ? why.Substring(0, 90) + "..." : why;
            var mode = Plugin.Updates != null ? Plugin.Updates.Value : UpdateMode.Ask;
            Note = $"Liar's Bar 8P v{newest} could not be installed ({shortWhy})" +
                   (mode == UpdateMode.Automatic ? " - it is tried again the next time the game starts"
                    : mode == UpdateMode.Ask ? " - you will be asked again the next time the game starts" : "");
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetCommandLineW();

    /// <summary>
    /// Start the game again with the update in it - a fresh copy of the game with exactly the
    /// command line and environment this one was started with - and close this one. False if the
    /// new copy could not be started, or if the update is not in place yet (one waiting for the
    /// game to close would not be loaded by it).
    ///
    /// The new copy is started from here rather than through Steam's "run game" link, because
    /// Steam takes a moment to notice a game has closed and ignores the link until it has. It
    /// inherits everything this one was given: the launch options from Steam, the app id Steam
    /// set, and on Linux the WINEDLLOVERRIDES the mod needs to load at all.
    /// </summary>
    internal static bool Restart()
    {
        if (!InPlace) return false;
        try
        {
            string exe = BepInEx.Paths.ExecutablePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                Plugin.Log.LogWarning("[update] cannot restart the game: its exe was not found - restart it by hand");
                return false;
            }

            string args = Arguments(Marshal.PtrToStringUni(GetCommandLineW()) ?? "");
            var start = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };

            // The loader that starts the mod (Doorstop) marks the process it has set up, in its
            // environment, so that a program the game starts is not set up a second time - and a
            // process started from this one inherits the mark. Left in, the new copy of the game
            // started without the mod at all: no loader, no version line, an unmodded client.
            // Everything of the loader's is left out, so it sets the new copy up afresh.
            var keys = new System.Collections.Generic.List<string>();
            foreach (string key in start.Environment.Keys)
                if (key.StartsWith("DOORSTOP_", StringComparison.OrdinalIgnoreCase)) keys.Add(key);
            foreach (string key in keys) start.Environment.Remove(key);
            start.Environment["LIARSBAR8P_RESTARTED"] = Plugin.Version;

            System.Diagnostics.Process.Start(start);
            Plugin.Log.LogInfo($"[update] restarting the game to load the update (arguments: {args}; " +
                               $"left out of its environment: {(keys.Count > 0 ? string.Join(", ", keys) : "nothing")})");
            UnityEngine.Application.Quit();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[update] could not restart the game ({e.Message}) - restart it by hand");
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

    /// <summary>
    /// When the game closes, move a waiting update in. The file is still held open at this point,
    /// so a short-lived command does it a moment after the game has gone.
    /// </summary>
    internal static void OnQuit()
    {
        try
        {
            string fresh = _pending;
            if (string.IsNullOrEmpty(fresh) || !File.Exists(fresh)) return;
            string here = fresh.Substring(0, fresh.Length - ".new".Length);
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c ping -n 4 127.0.0.1 >nul & move /y \"{fresh}\" \"{here}\"")
            { CreateNoWindow = true, UseShellExecute = false };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
