using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace LiarsBar8P;

/// <summary>
/// What may leave this machine in a line of log text, and how it is cleaned first.
///
/// Two users. <see cref="Secrets"/> is for any log the mod writes itself: the game prints the
/// player's own Unity sign-in to its log as it starts - an access token and a player id - and
/// every listener BepInEx has hears it, including the per-copy log. <see cref="Line"/> is the bug
/// report's much stricter gate, which takes almost nothing and cleans what it takes.
///
/// The rule for the rest of the mod is simpler: a log line names a player by seat or by
/// connection, never by name. Names come from everywhere and are whatever their owner typed, and a
/// log is exactly what people paste into a public issue. Everything below is the second net.
///
/// Every step is pure string work, with no Unity call: a log listener is called on any thread.
/// </summary>
internal static class LogScrub
{
    // ------------------------------------------------------------------ secrets, for any log

    private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_-]{10,}(?:\.[A-Za-z0-9_-]+){0,2}", RegexOptions.Compiled);
    private static readonly Regex Labelled = new(@"(?i)\b(access[ _]?token|player[ _]?id|session[ _]?token|id[ _]?token|bearer)\b(\s*[:=]\s*|\s+)\S+",
                                                 RegexOptions.Compiled);

    /// <summary>
    /// The line with anything that is a sign-in taken out: a JSON web token anywhere, and the value
    /// after a label that announces one. Everything else is left exactly as it was, because the test
    /// tools read these logs line by line.
    /// </summary>
    internal static string Secrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Cheap enough to run on every line the game prints: most lines have none of these.
        if (text.IndexOf("eyJ", StringComparison.Ordinal) < 0 &&
            text.IndexOf("token", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("player", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("bearer", StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        text = Jwt.Replace(text, "<token>");
        return Labelled.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "<hidden>");
    }

    // ------------------------------------------------------------------ the bug report's gate

    /// <summary>
    /// Tags whose lines may go into a bug report. None of them prints a name, a path or anything
    /// typed by a player; the ones that once named players name seats now. A tag not on this list is
    /// left out, so a new tag stays out of reports until somebody has read what it prints.
    /// </summary>
    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal)
    {
        "matchmaking", "update", "join", "joindiag", "cap", "transport", "turn", "turnstart", "turnring",
        "dealarray", "decksize", "cardmix", "cardtype", "deckfix", "deckdiag", "dicetable", "dicerows",
        "speccam", "texasswap", "texasdeck", "spin", "spinboard", "seatlists", "seats", "seatcheck",
        "resetanim", "autoready", "betvoice", "skinclick", "emote", "roster", "rpc", "native", "clone",
        "seatmark", "arrow", "turnhud", "aimkeys", "version", "census", "mechanic", "table", "ranked",
        "modnet", "lobbyid", "lobbycount", "deck2", "aim", "aimring", "tablefill", "seatring",
        "dealcards", "dealtrace", "seatassign", "podium", "guard", "dicefix", "bugreport", "wardrobe",
        "awards", "board", "bot",
    };

    /// <summary>Lines under an allowed tag that are a developer's, or that can hold typed text.</summary>
    private static readonly string[] Refused =
    {
        "[matchmaking] drive", "[matchmaking] selftest", "[matchmaking] state", "[bugreport] scrubtest",
    };

    /// <summary>The loader's own lines from the mod's start, which carry no tag.</summary>
    private static readonly string[] Untagged =
    {
        "  patched: ", "  FAILED to patch", "=== ", "  DEVELOPER MODE", "  developer mode off",
    };

    /// <summary>The longest line a report keeps.</summary>
    internal const int Widest = 240;

    /// <summary>
    /// One log event as a bug report may keep it, or null to leave it out. In order: only the mod's
    /// own lines and the game's exceptions; only allowed tags; the first line only; known names
    /// replaced; masks; and anything that is not plain ASCII left out, since all of the mod's own
    /// words are, and so whatever else is in the line can only be somebody's text.
    /// </summary>
    internal static string Line(string text, LogLevel level, bool mine, string source, Names names)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = FirstLine(text);

        if (mine)
        {
            if (!Wanted(text)) return null;
        }
        else if (source == "Unity" && (level & (LogLevel.Error | LogLevel.Fatal)) != 0)
        {
            if (!IsException(text)) return null;
        }
        else return null;

        if (text.IndexOf("arguments", StringComparison.OrdinalIgnoreCase) >= 0 && text.StartsWith("[update]", StringComparison.Ordinal))
            return null;     // a launch option line: whatever the player typed in Steam

        string tag = Tag(text);
        string body = text.Substring(tag.Length);
        if (names != null) body = names.Replace(body);
        body = Masks(body);

        foreach (char c in body)
            if (c < ' ' || c > '~') return (tag.Length > 0 ? tag : "[?]") + " (line left out)";

        string clean = tag + body;
        return clean.Length > Widest ? clean.Substring(0, Widest) + "..." : clean;
    }

    /// <summary>A kept line once more, with every name known by now: some are learnt after the line they were in.</summary>
    internal static string Again(string line, Names names)
    {
        if (string.IsNullOrEmpty(line) || names == null) return line;
        string tag = Tag(line);
        return tag + names.Replace(line.Substring(tag.Length));
    }

    private static string FirstLine(string text)
    {
        int cut = text.IndexOfAny(new[] { '\r', '\n' });
        return cut < 0 ? text : text.Substring(0, cut);
    }

    private static bool Wanted(string text)
    {
        foreach (string refused in Refused)
            if (text.StartsWith(refused, StringComparison.Ordinal)) return false;

        string tag = Tag(text);
        if (tag.Length > 2) return Tags.Contains(tag.Substring(1, tag.Length - 3));

        foreach (string start in Untagged)
            if (text.StartsWith(start, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The tag at the start of a line with the space after it, "[aim] ", or "" when it has none.</summary>
    private static string Tag(string text)
    {
        if (text.Length < 3 || text[0] != '[') return "";
        int close = text.IndexOf(']');
        if (close < 2 || close > 24 || close + 1 >= text.Length || text[close + 1] != ' ') return "";
        for (int i = 1; i < close; i++)
        {
            char c = text[i];
            if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != ':') return "";
        }
        return text.Substring(0, close + 2);
    }

    private static readonly Regex Thrown = new(@"^[A-Za-z_][\w.]*Exception:", RegexOptions.Compiled);

    /// <summary>The game's own exceptions, which a maintainer needs, except the online services' own.</summary>
    private static bool IsException(string text) =>
        Thrown.IsMatch(text) && !text.StartsWith("Unity.Services.", StringComparison.Ordinal);

    // The masks, in the order they run. A path runs on to the next quote or the end of the line,
    // because Windows user names can have spaces in them.
    private static readonly (Regex Find, string Put)[] MaskList =
    {
        (new Regex(@"(?i)\b(?:https?|ftp)://\S+", RegexOptions.Compiled), "<url>"),
        (new Regex(@"(?i)\bfile:/[^'""]*", RegexOptions.Compiled), "<path>"),
        (new Regex(@"\\\\[^'""]*", RegexOptions.Compiled), "<path>"),
        (new Regex(@"(?<![A-Za-z])[A-Za-z]:[\\/][^'""]*", RegexOptions.Compiled), "<path>"),
        (new Regex(@"(?i)/(?:home|users|run/media|mnt|media|var/home|tmp)/[^'""]*", RegexOptions.Compiled), "<path>"),
        (new Regex(@"eyJ[A-Za-z0-9_-]{10,}(?:\.[A-Za-z0-9_-]+){0,2}", RegexOptions.Compiled), "<token>"),
        (new Regex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled), "<email>"),
        (new Regex(@"\b\d{15,20}\b", RegexOptions.Compiled), "<id>"),
        (new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled), "<ip>"),
    };

    private static readonly Regex Run = new(@"(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/=_-]{24,}(?![A-Za-z0-9+/=_-])", RegexOptions.Compiled);

    // Greedy on purpose: from the first quote to the last, so a name with an apostrophe in it
    // cannot leave half of itself outside.
    private static readonly Regex Quoted = new(@"'.*'", RegexOptions.Compiled);

    /// <summary>Paths, ids, tokens, addresses and anything quoted, each replaced by what it was.</summary>
    internal static string Masks(string text)
    {
        foreach (var (find, put) in MaskList) text = find.Replace(text, put);

        // A long run is a key or an id when it has a digit in it, or when it is longer than any
        // name in the game's code; a run of letters alone is usually the name of a class.
        text = Run.Replace(text, m =>
        {
            if (m.Length >= 28) return "<token>";
            foreach (char c in m.Value) if (c >= '0' && c <= '9') return "<token>";
            return m.Value;
        });

        text = Labelled.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "<hidden>");
        return Quoted.Replace(text, "'<text>'");
    }

    // ------------------------------------------------------------------ names

    /// <summary>
    /// Every name this copy has seen this session, each put back as Player1, Player2 and on in the
    /// order they were first seen, so a report can still tell two people apart. Exact matches only,
    /// longest first, and only where the name stands on its own: a name inside a longer word is
    /// left alone. Each name is also kept cut to the lengths the mod once cut names to before
    /// logging them. Safe to call from any thread.
    /// </summary>
    internal sealed class Names
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, string> _as = new(StringComparer.Ordinal);
        private List<string> _longestFirst = new();
        private int _people;

        internal int Count { get { lock (_lock) return _people; } }

        internal void Add(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            name = name.Trim();
            if (name.Length < 2) return;

            lock (_lock)
            {
                if (_as.ContainsKey(name)) return;
                string label = $"Player{++_people}";
                _as[name] = label;
                foreach (int cut in new[] { 16, 24 })
                    if (name.Length > cut && !_as.ContainsKey(name.Substring(0, cut))) _as[name.Substring(0, cut)] = label;

                var all = new List<string>(_as.Keys);
                all.Sort((a, b) => b.Length.CompareTo(a.Length));
                _longestFirst = all;
            }
        }

        internal string Replace(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            List<string> all;
            lock (_lock) all = _longestFirst;
            if (all.Count == 0) return text;

            var sb = new StringBuilder(text);
            foreach (string name in all)
            {
                string label;
                lock (_lock) label = _as[name];

                int from = 0;
                while (true)
                {
                    string now = sb.ToString();
                    int at = now.IndexOf(name, from, StringComparison.Ordinal);
                    if (at < 0) break;

                    bool alone = (at == 0 || !char.IsLetterOrDigit(now[at - 1])) &&
                                 (at + name.Length >= now.Length || !char.IsLetterOrDigit(now[at + name.Length]));
                    if (!alone) { from = at + 1; continue; }

                    sb.Remove(at, name.Length).Insert(at, label);
                    from = at + label.Length;
                }
            }
            return sb.ToString();
        }
    }
}
