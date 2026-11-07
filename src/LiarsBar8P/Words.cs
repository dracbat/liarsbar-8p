using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LiarsBar8P;

/// <summary>The four languages the game ships. There are no others.</summary>
internal enum Tongue { En, Tr, Ru, Zh }

/// <summary>
/// The mod's words in the language the game is set to: English, Turkish, Russian or Simplified
/// Chinese, from a table that travels inside the plugin (<c>words.tsv</c>), the same way the
/// changelog does, because an update replaces only the plugin.
///
/// Every string is looked up by its English text, written where it is used, so English can never
/// go missing: it is the argument, not something found in the table. No table, no row, a blank
/// cell or a language the mod does not know all give back the English that is already in the
/// code. A translation that breaks <see cref="string.Format(string, object[])"/> formats the
/// English instead.
///
/// The language is the game's own choice in its settings, read from Unity's selected locale four
/// times a second once the main menu is up (an earlier read can stall while the locale loads), so
/// a change in the settings shows within a quarter of a second. There is no setting of the mod's
/// own. Logs and the bug report file stay English, so nothing written there goes through here.
///
/// Nothing here touches Unity except <see cref="Tick"/>, so the updater's worker thread can call
/// <see cref="T"/> and <see cref="F"/> too. The Russian, Turkish and Chinese are machine
/// translated: corrections from native speakers are welcome.
/// </summary>
internal static class Words
{
    private const string Resource = "words.tsv";
    private const float Every = 0.25f;

    private static Dictionary<string, string[]> _rows;
    private static bool _ready;
    private static bool _failed;
    private static float _next;

    /// <summary>The game's language, English until the game says otherwise.</summary>
    internal static Tongue Now { get; private set; }

    /// <summary>One more each time the language changes, for words drawn once and kept.</summary>
    internal static int Generation { get; private set; }

    /// <summary>From <c>Plugin.Load</c>, before anything that shows words. A failure leaves everything English.</summary>
    internal static void Load()
    {
        try
        {
            using var stream = typeof(Words).Assembly.GetManifestResourceStream(Resource);
            if (stream == null) { Plugin.Log.LogWarning("[words] no word table in the plugin - English only"); return; }

            var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
            using var reader = new StreamReader(stream);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] cells = line.Split('\t');
                if (cells.Length < 2 || cells[0].Length == 0) continue;

                var row = new string[3];
                for (int i = 0; i < 3; i++)
                    row[i] = i + 1 < cells.Length && cells[i + 1].Length > 0 ? Unescape(cells[i + 1]) : null;
                rows[Unescape(cells[0])] = row;
            }
            _rows = rows;
            Plugin.Log.LogInfo($"[words] {rows.Count} lines in tr, ru and zh");
        }
        catch (Exception e)
        {
            _rows = null;
            Plugin.Log.LogWarning($"[words] could not read the word table, English only: {e.Message}");
        }
    }

    private static string Unescape(string cell) => cell.Replace("\\n", "\n");

    /// <summary>These words in the game's language.</summary>
    internal static string T(string english)
    {
        if (english == null || Now == Tongue.En || _rows == null) return english;
        return _rows.TryGetValue(english, out var row) && row[(int)Now - 1] is string said ? said : english;
    }

    /// <summary>These words in the game's language, with the blanks filled in. Numbers in the invariant culture.</summary>
    internal static string F(string english, params object[] args) => Fill(T(english), english, args);

    /// <summary>
    /// A count in words: English picks <paramref name="one"/> or <paramref name="many"/>, and the
    /// table is keyed on <paramref name="many"/>. A Russian cell holds three forms split by '|'
    /// (1 and 21, 2 to 4 and 22 to 24, the rest), because Russian changes the noun with the number.
    /// Turkish and Chinese use one form for every count.
    /// </summary>
    internal static string N(string one, string many, int n, params object[] more)
    {
        var args = new object[1 + more.Length];
        args[0] = n;
        Array.Copy(more, 0, args, 1, more.Length);

        string english = n == 1 ? one : many;
        if (Now == Tongue.En || _rows == null || !_rows.TryGetValue(many, out var row) || row[(int)Now - 1] == null)
            return Fill(english, english, args);

        string said = row[(int)Now - 1];
        string[] forms = said.Split('|');
        if (forms.Length == 3) said = forms[RussianForm(n)];
        else if (forms.Length > 1) said = forms[0];
        return Fill(said, english, args);
    }

    private static string Fill(string said, string english, object[] args)
    {
        try { return string.Format(CultureInfo.InvariantCulture, said, args); }
        catch (FormatException)
        {
            try { return string.Format(CultureInfo.InvariantCulture, english, args); }
            catch (FormatException) { return english; }
        }
    }

    private static int RussianForm(int n)
    {
        n = Math.Abs(n);
        if (n % 10 == 1 && n % 100 != 11) return 0;
        if (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14)) return 1;
        return 2;
    }

    /// <summary>From <see cref="ModTicker"/>. Follows the game's language setting.</summary>
    internal static void Tick()
    {
        if (_failed) return;
        float now = UnityEngine.Time.unscaledTime;
        if (now < _next) return;
        _next = now + Every;

        // The first read waits for the menu: by then the locale has loaded and reading it cannot stall.
        if (!_ready)
        {
            if (LobbyListManager.instance == null) return;
            _ready = true;
        }

        try
        {
            var locale = UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale;
            string name = locale != null ? locale.LocaleName : null;
            if (name == null) return;

            Tongue tongue = name.Contains("(tr)") ? Tongue.Tr
                          : name.Contains("(ru)") ? Tongue.Ru
                          : name.Contains("(zh") ? Tongue.Zh
                          : Tongue.En;
            if (tongue == Now) return;

            Now = tongue;
            Generation++;
            Plugin.Log.LogInfo($"[words] the game's language is now {tongue}");
            ModCanvas.OnLanguage();
        }
        catch (Exception e)
        {
            _failed = true;
            Plugin.Log.LogWarning($"[words] could not read the game's language, staying in {Now}: {e.Message}");
        }
    }
}
