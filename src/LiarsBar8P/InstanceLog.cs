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
/// </summary>
internal static class InstanceLog
{
    private static StreamWriter _file;
    private static readonly object _lock = new();
    private static bool _tried;

    /// <summary>Where this process's log lives, once it has one.</summary>
    internal static string Path { get; private set; }

    internal static void Start(string role)
    {
        if (_tried) return;
        _tried = true;

        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiarsBar8P", "logs");
            Directory.CreateDirectory(dir);

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
