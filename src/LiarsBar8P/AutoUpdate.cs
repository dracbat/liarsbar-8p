using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiarsBar8P;

/// <summary>
/// Keeps the mod up to date by itself: each time the game starts it asks this mod's GitHub
/// releases for the newest version, and if it is newer than the one running, downloads it and
/// puts it in place for the next start.
///
/// Every player at a table has to run the same version, and the slowest way to get a fix to
/// people was the mod site: a new upload waits days for its files to be checked, so players
/// there were always a release or more behind. With this, a copy from anywhere catches up on
/// its own.
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
/// <c>AutoUpdate</c> in the config turns it off.
/// </summary>
internal static class AutoUpdate
{
    private const string Repo = "dracbat/liarsbar-8p";
    private const string Latest = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    private const string ZipName = "LiarsBar-8P.zip";
    private const string PluginEntry = "BepInEx/plugins/LiarsBar8P.dll";

    /// <summary>Shown under the version line once an update is in place. Set from a worker thread.</summary>
    internal static volatile string Note;

    private static string _pending;   // new plugin waiting to be moved in when the game closes

    internal static void Start()
    {
        try
        {
            string here = typeof(Plugin).Assembly.Location;
            if (string.IsNullOrEmpty(here)) return;

            // Leftovers from an update that has now been loaded.
            TryDelete(here + ".old");

            if (Plugin.AutoUpdateEnabled == null || !Plugin.AutoUpdateEnabled.Value) return;
            if (Dev.Enabled || Loopback.Active) return;   // a test run must keep the build it was given

            Task.Run(() => Check(here));
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[update] could not start the update check: {e.Message}"); }
    }

    private static async Task Check(string here)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("LiarsBar8P-updater");

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

            Plugin.Log.LogInfo($"[update] v{newest} is out (running v{Plugin.Version}) - downloading");
            byte[] zip = await http.GetByteArrayAsync(url).ConfigureAwait(false);

            byte[] dll = null;
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                var entry = archive.GetEntry(PluginEntry);
                if (entry == null || entry.Length < 20_000 || entry.Length > 50_000_000)
                { Plugin.Log.LogWarning("[update] the release zip has no usable plugin - not updating"); return; }
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
            catch { TryDelete(fresh); Plugin.Log.LogWarning("[update] the downloaded plugin is not a .NET assembly - not updating"); return; }
            if (name.Name != "LiarsBar8P" || name.Version == null ||
                new Version(name.Version.Major, name.Version.Minor, name.Version.Build) != newest)
            {
                TryDelete(fresh);
                Plugin.Log.LogWarning($"[update] the downloaded plugin is {name.Name} {name.Version}, not LiarsBar8P {newest} - not updating");
                return;
            }

            try
            {
                TryDelete(here + ".old");
                File.Move(here, here + ".old");   // allowed while loaded; overwriting is not
                File.Move(fresh, here);
                Note = $"Liar's Bar 8P v{newest} is installed - restart the game to use it";
                Plugin.Log.LogInfo($"[update] v{newest} installed - it loads the next time the game starts");
            }
            catch (Exception e)
            {
                if (!File.Exists(here) && File.Exists(here + ".old")) File.Move(here + ".old", here);
                _pending = fresh;
                Note = $"Liar's Bar 8P v{newest} is ready - it installs when you close the game";
                Plugin.Log.LogInfo($"[update] v{newest} downloaded; the running copy could not be moved aside ({e.Message}) - it goes in when the game closes");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogInfo($"[update] could not check for an update this time ({e.GetType().Name}: {e.Message})");
        }
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
