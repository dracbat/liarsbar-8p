using System;
using System.IO;
using System.Text;

namespace LiarsBar8P;

/// <summary>
/// Gives each running copy of the game its own log file.
///
/// BepInEx writes one <c>LogOutput.log</c> in the game folder, which is fine until two copies
/// of the game run at once: the second cannot open the file the first is holding, and
/// everything it has to say is simply lost. That was found the moment two instances were
/// first started together - one plugin load in the log, two processes on the machine.
///
/// Several copies at once is the whole point of the loopback testing this exists for, so each
/// writes beside the other under a name carrying its own process id and whether it is hosting
/// or joining. Nothing is taken away from the BepInEx log; this is written as well.
///
/// Lines are flushed as they are written. A log that is buffered when the process is killed
/// is a log that tells you nothing about why you killed it, and these processes get killed a
/// great deal.
///
/// Only when several copies are actually running. An ordinary player has one copy, one
/// BepInEx log and no problem, and writing them a second megabyte-and-a-half copy of it every
/// launch into a folder they were never told about is a cost with no matching benefit. See
/// <c>Wanted</c>; and the folder is now pruned, which it never used to be.
/// </summary>
internal static class InstanceLog
{
    private static StreamWriter _file;
    private static readonly object _lock = new();
    private static bool _tried;

    /// <summary>Where this process's log lives, once it has one.</summary>
    internal static string Path { get; private set; }

    /// <summary>
    /// Whether this copy needs a log file of its own.
    ///
    /// It only does when several copies of the game are running at once: BepInEx keeps one
    /// <c>LogOutput.log</c> in the game folder, the second copy cannot open it, and without
    /// this that copy would record nothing at all.
    ///
    /// A single ordinary player has no such problem, and this was running for them too -
    /// mirroring the whole BepInEx and Unity log into a new file, about a megabyte and a half
    /// per launch, into a folder they were never told about and which nothing ever tidies.
    /// Two hundred launches is a third of a gigabyte of duplicate text, and the uninstaller
    /// does not go near it.
    ///
    /// Read straight from the environment and the command line rather than from
    /// <c>Loopback.Active</c>, because Loopback has not been configured yet at this point -
    /// this deliberately runs before anything else can want to write a line.
    /// </summary>
    private static bool Wanted()
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIARSBAR8P_LOOPBACK"))) return true;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIARSBAR8P_ROLE"))) return true;

            var args = Environment.GetCommandLineArgs();
            if (args != null)
                foreach (var a in args)
                    if (string.Equals(a, "-lbhost", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "-lbclient", StringComparison.OrdinalIgnoreCase))
                        return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Keep the folder from growing without limit. Only the newest few runs are ever wanted;
    /// nothing here has ever deleted one before.
    /// </summary>
    private const int KeepRuns = 12;

    private static void Prune(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.log");
            if (files.Length <= KeepRuns) return;

            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = KeepRuns; i < files.Length; i++)
                try { files[i].Delete(); } catch { }
        }
        catch { }
    }

    internal static void Start(string role)
    {
        if (_tried) return;
        _tried = true;

        if (!Wanted()) return;

        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiarsBar8P", "logs");
            Directory.CreateDirectory(dir);
            Prune(dir);

            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            Path = System.IO.Path.Combine(dir, $"{role}-{pid}.log");

            _file = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                                     new UTF8Encoding(false)) { AutoFlush = true };

            Write($"=== Liar's Bar 8P {Plugin.Version} - process {pid}, role {role} ===");

            // Everything BepInEx logs, not only what this class is handed - otherwise every
            // line the mod already writes through Plugin.Log would have to be routed twice
            // by hand, and the ones that were forgotten would be exactly the ones needed.
            try { BepInEx.Logging.Logger.Listeners.Add(new Sink()); }
            catch (Exception e) { Write($"(could not tap the BepInEx log: {e.Message})"); }

            Plugin.Log.LogInfo($"[log] this instance also logs to {Path}");
        }
        catch (Exception e)
        {
            // A missing log is a nuisance; a mod that will not load because of one is worse.
            _file = null;
            try { Plugin.Log.LogWarning($"[log] no per-instance log: {e.Message}"); } catch { }
        }
    }

    internal static void Write(string line)
    {
        if (_file == null) return;
        try
        {
            lock (_lock) _file.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        }
        catch { _file = null; }
    }

    /// <summary>Copies everything BepInEx logs into this process's own file.</summary>
    private sealed class Sink : BepInEx.Logging.ILogListener
    {
        public BepInEx.Logging.LogLevel LogLevelFilter => BepInEx.Logging.LogLevel.All;

        public void LogEvent(object sender, BepInEx.Logging.LogEventArgs args)
        {
            if (args == null) return;
            Write($"[{args.Level}] {args.Source?.SourceName}: {args.Data}");
        }

        public void Dispose() { }
    }
}
