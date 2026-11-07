using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Wide screens, 21:9 and 32:9, in the game's own display settings.
///
/// The game offers seven sizes, all 16:9 or 16:10, and puts its saved one back each time the menu
/// or a bar loads, so an ultrawide monitor shows a 16:9 picture with black bars down both sides.
/// Its 3D view would fill the width by itself: the cameras keep their height and show more of the
/// room at the sides, as turning the head would. Only the size is in the way.
///
/// <see cref="Sizes"/> adds the monitor's own shape after the game's seven, at the heights the game
/// already uses: on a 3440x1440 desktop that is 1720x720, 2580x1080 and 3440x1440, with any size the
/// monitor reports at 1.85 to 1 or wider. The game's own arrows and Apply then do the switching. The
/// first time the game runs full screen on such a monitor it picks, once, the desktop's shape at the
/// height the player already plays at (<see cref="Pick"/>): 1920x1080 becomes 2580x1080, as sharp
/// as before and nearly as cheap, with the bars filled. After that the player's own choice always
/// wins (<see cref="Choice"/>), and choosing a 16:9 size forgets the wide one.
///
/// The game saves only a place in its list, and a copy of the game without the mod must never find
/// a place past its seven: <see cref="Saved"/> writes a wide choice as the last of the game's own
/// sizes the player chose (its 1920x1080 when there is none), so a player who takes the mod out goes
/// back to that, and the wide size itself is kept in a file of the mod's own, outside the game folder
/// (<see cref="Memory"/>). The list is built the same way every time, the wide sizes narrowest first,
/// so a place always holds the same size; and a kept size the list cannot show this time, on a
/// smaller monitor, stays kept.
///
/// Nothing here is networked and nothing changes for anyone else at the table, so it works at any
/// table, modded or not. The canvases and pictures that need a hand at a wide size are
/// <see cref="UltrawideArt"/>'s.
///
/// For testing without a wide monitor, <c>LIARSBAR8P_UWTEST=3440x1440</c> with developer mode on
/// treats that size as the desktop's and selects it, in a window that stays where the test put it,
/// and writes neither the game's display settings nor the mod's (<see cref="TestWindow"/>).
/// </summary>
internal static class Ultrawide
{
    /// <summary>How many sizes the game's own list holds. Nothing at these places is ever touched.</summary>
    internal const int GameSizes = 7;

    /// <summary>The game's own 1920x1080, the place a wide choice is saved as when no other of the game's is remembered.</summary>
    private const int SavedAs = 4;

    /// <summary>The heights the game's own list already uses, so a wide size costs what the player is used to.</summary>
    private static readonly int[] Heights = { 720, 1080, 1440 };

    /// <summary>A size the monitor reports is offered from this shape on. 16:9 is 1.78 and 16:10 is 1.6.</summary>
    private const float ReportedShape = 1.85f;

    // The game's own keys for the two display settings a test copy must leave alone.
    private const string IndexKey = "resolutionIndex";
    private const string FullKey = "fullScreenIndex";

    private const string TestVariable = "LIARSBAR8P_UWTEST";

    /// <summary>
    /// Where the test harness put the window, "2000,40". A copy with it keeps its window there
    /// (<see cref="Spot"/>), developer mode or not, and a wide test does not run without it.
    /// </summary>
    private const string SpotVariable = "LIARSBAR8P_WINDOW_AT";

    /// <summary>
    /// For the test kit, which looks for these words in the plugin before it starts a copy: this
    /// build holds the window of every copy <see cref="SpotVariable"/> names a place for, a plain
    /// copy as well. A build without them lets a plain copy's window reach the main screen, so the
    /// kit refuses to start it.
    /// </summary>
    internal const string HoldsEveryCopy = "LIARSBAR8P_WINDOW_AT holds every copy";

    /// <summary>A screen size, written the way the game's list writes it: 3440x1440.</summary>
    internal readonly record struct Size(int W, int H)
    {
        public override string ToString() =>
            W.ToString(CultureInfo.InvariantCulture) + "x" + H.ToString(CultureInfo.InvariantCulture);

        internal static bool TryParse(string words, out Size size)
        {
            size = default;
            if (string.IsNullOrWhiteSpace(words)) return false;
            string[] parts = words.Trim().ToLowerInvariant().Split('x');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h) ||
                w < 320 || h < 200 || w > 16384 || h > 16384) return false;
            size = new Size(w, h);
            return true;
        }
    }

    // ------------------------------------------------------------------ the rules, with nothing of the game's in them

    /// <summary>Wider than 16:9 by more than a hair. 16:9 itself and the Steam Deck's 16:10 are not.</summary>
    internal static bool IsWide(int w, int h) => w > 0 && h > 0 && (float)w / h > 16f / 9f * 1.01f;

    internal static bool IsWide(Size size) => IsWide(size.W, size.H);

    /// <summary>The screen as it is now, for the canvases and the pictures.</summary>
    internal static bool ScreenWide => IsWide(Screen.width, Screen.height);

    /// <summary>The desktop's shape at a height, the width rounded to an even number: 3440x1440 at 1080 is 2580x1080.</summary>
    internal static Size ShapeAt(Size desktop, int height) =>
        new(2 * (int)Math.Round(desktop.W * (double)height / desktop.H / 2.0, MidpointRounding.AwayFromZero), height);

    /// <summary>
    /// The sizes that go after the game's seven, narrowest first: the desktop, its shape at each of
    /// the game's heights that is not taller than the desktop, every size the monitor reports at 1.85
    /// to 1 or wider, and the wide size kept from before (or about to be). None at all unless the
    /// desktop is wide. Always in this order, so the place of each is the same in every list built.
    /// </summary>
    internal static List<Size> Extras(Size desktop, IEnumerable<Size> reported, Size? kept)
    {
        var all = new List<Size>();
        if (!IsWide(desktop)) return all;

        void Add(Size size)
        {
            if (size.W > 0 && size.H > 0 && !all.Contains(size)) all.Add(size);
        }

        Add(desktop);
        foreach (int height in Heights)
            if (height <= desktop.H) Add(ShapeAt(desktop, height));
        if (reported != null)
            foreach (var size in reported)
                if (size.H > 0 && (float)size.W / size.H >= ReportedShape) Add(size);
        if (kept is Size k && IsWide(k) && k.H <= desktop.H) Add(k);

        Sorted(all);
        return all;
    }

    private static void Sorted(List<Size> all) => all.Sort((a, b) => a.W != b.W ? a.W.CompareTo(b.W) : a.H.CompareTo(b.H));

    /// <summary>
    /// The one-time pick: the desktop's shape at the height the player already plays at, so the
    /// picture fills the screen at the same sharpness and nearly the same cost. A height taller than
    /// the desktop gives the desktop itself. Nothing on a desktop that is not wide, or for a player
    /// already on a wide size.
    /// </summary>
    internal static Size? Pick(Size desktop, Size now)
    {
        if (!IsWide(desktop) || now.H <= 0 || IsWide(now)) return null;
        return now.H >= desktop.H ? desktop : ShapeAt(desktop, now.H);
    }

    // ------------------------------------------------------------------ the test switch

    private static bool _testRead;
    private static Size? _test;
    private static readonly List<Size> _testMore = new();

    /// <summary>
    /// The size <c>LIARSBAR8P_UWTEST</c> asks for, read once, and only with developer mode on and the
    /// window's place named as well. A window wider than the screen it sits on gets moved by the game
    /// onto whichever screen it fits, and a test window must never sit over somebody's game. Sizes
    /// after a comma, "5120x1440,1280x800", go on the list as well, to be stepped to in one run.
    /// </summary>
    internal static Size? TestSize
    {
        get
        {
            if (!_testRead)
            {
                _testRead = true;
                try
                {
                    string[] raw = (Environment.GetEnvironmentVariable(TestVariable) ?? "").Split(',');
                    if (Dev.Enabled && Size.TryParse(raw[0], out var size) && Held)
                    {
                        _test = size;
                        for (int i = 1; i < raw.Length; i++)
                            if (Size.TryParse(raw[i], out var more) && !_testMore.Contains(more)) _testMore.Add(more);
                    }
                    else if (raw[0].Length > 0)
                        Plugin.Log.LogWarning($"[wide] {TestVariable} is ignored: it needs developer mode, a size such as 3440x1440 " +
                                              $"and {SpotVariable} with the window's place, such as 2000,40");
                }
                catch { }
            }
            return _test;
        }
    }

    /// <summary>Set by the plugin: whether the guard on the game's saved settings went on.</summary>
    internal static bool SavesGuarded { get; set; }

    /// <summary>Set by the plugin: whether the test's window patch went on.</summary>
    internal static bool TestReady { get; set; }

    /// <summary>Set by the plugin: whether a placed copy's guard against full screen went on.</summary>
    internal static bool WindowGuarded { get; set; }

    /// <summary>A test run, only with every guard on, so a test copy can neither go full screen nor write the game's settings.</summary>
    internal static bool Testing => TestSize != null && SavesGuarded && TestReady && WindowGuarded;

    // ------------------------------------------------------------------ what is kept

    private static Size? _testKept;
    private static bool _testKeptSet;

    /// <summary>The wide size the player chose, or null for the game's own list. A test keeps it in memory only.</summary>
    internal static Size? Kept
    {
        get
        {
            if (Testing)
            {
                if (!_testKeptSet) { _testKeptSet = true; _testKept = TestSize; }
                return _testKept;
            }
            return Memory.Wide;
        }
    }

    private static int? _testPlain;

    /// <summary>
    /// The last place among the game's seven the player chose, or had before the pick: what a wide
    /// choice is saved as. Null when none is remembered. A test keeps it in memory only.
    /// </summary>
    internal static int? Plain => Testing ? _testPlain : Memory.Plain;

    /// <summary>The place a wide choice is saved as in the game's own settings.</summary>
    private static int SavedPlace => Plain ?? SavedAs;

    private static void KeepPlain(int index)
    {
        if (index < 0 || index >= GameSizes) return;
        if (Testing) _testPlain = index;
        else Memory.SavePlain(index);
    }

    /// <summary>A wide size kept, by the pick or by hand, also spends the pick: it never runs over a choice made in the list.</summary>
    private static void Keep(Size? size)
    {
        if (Testing)
        {
            _testKeptSet = true;
            if (_testKept == size) return;
            _testKept = size;
        }
        else if (!Memory.Save(size, size != null || Memory.Picked, Memory.Plain)) return;
        Plugin.Log.LogInfo(size != null ? $"[wide] {size} is kept as the display size{(Testing ? " (test, in memory)" : "")}"
                                        : "[wide] a size from the game's own list was chosen: no wide size is kept now");
    }

    /// <summary>
    /// The kept size, the mark that the one-time pick is spent, and the last place among the game's
    /// own sizes the player used, in <c>display.txt</c> under local app data, next to the block list.
    /// Not in the mod's config: both installers start that file afresh on every install, on purpose,
    /// and these are the player's own choices. Lost there, a reinstall would forget a 3440x1440 the
    /// game's own save cannot hold, and run the pick again over a 16:9 size the player went back to.
    /// A developer copy with a role keeps a file of its own, so a test never writes the player's. A
    /// file that cannot be read keeps no size, lets nothing be picked, and says so once.
    /// </summary>
    private static class Memory
    {
        private const string Folder = "LiarsBar8P";
        private const string FileName = "display.txt";
        private const string WideKey = "wide", PickedKey = "picked", PlainKey = "plain", Yes = "yes", No = "no";

        private static bool _loaded, _picked, _ownFile;
        private static Size? _wide;
        private static int? _plain;
        private static string _path;

        internal static Size? Wide
        {
            get
            {
                Load();
                return _wide;
            }
        }

        internal static bool Picked
        {
            get
            {
                Load();
                return _picked;
            }
        }

        internal static int? Plain
        {
            get
            {
                Load();
                return _plain;
            }
        }

        internal static void SavePlain(int index)
        {
            if (Save(Wide, Picked, index))
                Plugin.Log.LogInfo($"[wide] place {index} of the game's own sizes is what a wide choice is saved as now");
        }

        /// <summary>The file's name alone, for the log. Its folder holds the user's name.</summary>
        internal static string Name => System.IO.Path.GetFileName(PathOf());

        /// <summary>Whether this copy keeps a developer file of its own rather than the player's.</summary>
        internal static bool OwnFile
        {
            get
            {
                PathOf();
                return _ownFile;
            }
        }

        /// <summary>All three, through a temporary file and a replace, as the block list is saved. False when none changed.</summary>
        internal static bool Save(Size? wide, bool picked, int? plain)
        {
            Load();
            if (_wide == wide && _picked == picked && _plain == plain) return false;
            _wide = wide;
            _picked = picked;
            _plain = plain;
            try
            {
                string path = PathOf();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllLines(temp, new[] { $"{WideKey}={wide?.ToString() ?? ""}", $"{PickedKey}={(picked ? Yes : No)}",
                                                 $"{PlainKey}={plain?.ToString(CultureInfo.InvariantCulture) ?? ""}" });
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] could not save the display choice: {e.Message}"); }
            return true;
        }

        /// <summary>Read again, for the test driver: as a new session after a reinstall would find it.</summary>
        internal static void Reload()
        {
            _loaded = false;
            _wide = null;
            _picked = false;
            _plain = null;
            Load();
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                string path = PathOf();
                if (!File.Exists(path)) return;

                foreach (string line in File.ReadAllLines(path))
                {
                    int at = line.IndexOf('=');
                    if (at <= 0) continue;
                    string key = line.Substring(0, at).Trim(), value = line.Substring(at + 1).Trim();
                    if (key == WideKey) _wide = Size.TryParse(value, out var size) && IsWide(size) ? size : null;
                    else if (key == PickedKey) _picked = value == Yes;
                    else if (key == PlainKey)
                        _plain = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int place) && place < GameSizes
                            ? place : null;
                }
                Plugin.Log.LogInfo($"[wide] {Name}: kept {(_wide?.ToString() ?? "none")}, pick spent {_picked}, " +
                                   $"saved as place {(_plain?.ToString(CultureInfo.InvariantCulture) ?? "none")}");
            }
            catch (Exception e)
            {
                _wide = null;
                _picked = true;
                _plain = null;
                Plugin.Log.LogWarning($"[wide] could not read the display choice - no wide size is kept and none is picked: {e.Message}");
            }
        }

        /// <summary>The player's file, or with developer mode and a role a file of the copy's own, as the block list does it.</summary>
        private static string PathOf()
        {
            if (_path != null) return _path;

            string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Folder);
            string role = null;
            try { role = Environment.GetEnvironmentVariable("LIARSBAR8P_ROLE"); }
            catch { }

            _ownFile = Dev.Enabled && !string.IsNullOrEmpty(role) && role.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
            _path = System.IO.Path.Combine(root, _ownFile ? $"display-{role}.txt" : FileName);
            return _path;
        }
    }

    /// <summary>The desktop's size: the monitor's own, or the one the test names.</summary>
    internal static Size Desktop()
    {
        if (Testing) return TestSize.Value;
        var desk = Screen.currentResolution;
        return new Size(desk.width, desk.height);
    }

    private static List<Size> Reported()
    {
        var found = new List<Size>();
        var all = Screen.resolutions;
        if (all != null)
            foreach (var mode in all) found.Add(new Size(mode.width, mode.height));
        return found;
    }

    // ------------------------------------------------------------------ the list

    /// <summary>Settings screens whose list this lengthened, by instance. Only these are ever read back.</summary>
    private static readonly HashSet<int> _lengthened = new();
    private static int _resetFrame = -1;
    private static string _saidList;

    [HarmonyPatch]
    internal static class Sizes
    {
        /// <summary>
        /// Before the saved choice is read. The game has just built its list of seven, here and
        /// after an F7 reset, so this is the one moment to add to it.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChangeSettings), nameof(ChangeSettings.LoadPrefs))]
        private static void Lengthen(ChangeSettings __instance)
        {
            try { Extend(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] the display list stays the game's own: {e.Message}"); }
        }

        /// <summary>After it: the kept wide size goes back on the selector, before the game applies it.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChangeSettings), nameof(ChangeSettings.LoadPrefs))]
        private static void Chosen(ChangeSettings __instance)
        {
            try { Select(__instance); }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] the display size stays the game's choice: {e.Message}"); }
        }

        /// <summary>
        /// F7 in the menu: the game chooses the size its own way (it reloads the one it last loaded,
        /// which is the one on show), and the kept size is not put on top of its choice.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChangeSettings), nameof(ChangeSettings.ResetSettings))]
        private static void Resetting() => _resetFrame = Time.frameCount;
    }

    /// <summary>The game's list of seven, and its selector's seven names, with the wide sizes after them.</summary>
    private static void Extend(ChangeSettings settings)
    {
        if (settings == null) return;
        var sizes = settings.resolutions;
        var selector = settings.resolutionSelector;
        var names = selector != null ? selector.data : null;
        if (sizes == null || names == null) return;
        if (sizes.Length != GameSizes || names.Count != GameSizes) return;   // lengthened already, or not the list this knows
        _lengthened.Remove(settings.GetInstanceID());
        int at = Rebuild(settings, Kept);

        // A wide place from earlier in the session, in a list that no longer holds the kept size
        // (the desktop changed: another monitor, or one plugged back in). The selector would land
        // on the list's last entry, another wide size, and that would be taken as the player's
        // choice. The game's own size the wide one is saved as is loaded instead, and the wide
        // size stays kept for a monitor that has it.
        var prefs = settings.playerPreferences;
        if (prefs != null && prefs.ResolutionIndex >= GameSizes && at < 0)
        {
            Plugin.Log.LogInfo($"[wide] place {prefs.ResolutionIndex} is not {(Kept is Size k ? k.ToString() : "a kept size")} in this list " +
                               $"(desktop {Desktop()}): place {SavedPlace} is loaded instead");
            prefs.ResolutionIndex = SavedPlace;
        }
    }

    /// <summary>
    /// The game's seven, then the wide sizes with <paramref name="with"/> among them, narrowest first,
    /// over whatever came after the seven before. Every list is built this one way, so a size has the
    /// same place in each whatever was kept when it was made, and a place the game reloads (an F7, the
    /// next scene) is the same size it was. The index of <paramref name="with"/>, or -1.
    /// </summary>
    private static int Rebuild(ChangeSettings settings, Size? with)
    {
        var sizes = settings.resolutions;
        var names = settings.resolutionSelector.data;
        if (sizes == null || names == null || sizes.Length < GameSizes || names.Count < GameSizes || sizes.Length != names.Count) return -1;

        var desktop = Desktop();
        var extra = Extras(desktop, Reported(), with);
        if (Testing)
        {
            var asked = new List<Size> { TestSize.Value };
            asked.AddRange(_testMore);
            foreach (var size in asked)
                if (IndexIn(sizes, GameSizes, size) < 0 && !extra.Contains(size)) extra.Add(size);
            Sorted(extra);
        }
        if (extra.Count == 0) return -1;

        var longer = new Il2CppStructArray<Resolution>(GameSizes + extra.Count);
        for (int i = 0; i < GameSizes; i++) longer[i] = sizes[i];
        for (int i = 0; i < extra.Count; i++) longer[GameSizes + i] = Make(extra[i]);
        settings.resolutions = longer;
        while (names.Count > GameSizes) names.RemoveAt(names.Count - 1);
        foreach (var size in extra) names.Add(size.ToString());
        _lengthened.Add(settings.GetInstanceID());

        string said = string.Join(", ", extra);
        if (said != _saidList)
        {
            _saidList = said;
            Plugin.Log.LogInfo($"[wide] display sizes after the game's seven: {said} (desktop {desktop}{(Testing ? ", from the test" : "")})");
        }
        return with is Size w ? IndexOf(longer, w) : -1;
    }

    /// <summary>The kept size on the selector, or the one-time pick, before the game's Start applies it.</summary>
    private static void Select(ChangeSettings settings)
    {
        if (settings == null || (!_lengthened.Contains(settings.GetInstanceID()) && !Testing)) return;
        if (Time.frameCount == _resetFrame)
        {
            Plugin.Log.LogInfo("[wide] settings reset: the game's own size stands");
            return;
        }

        var sizes = settings.resolutions;
        var selector = settings.resolutionSelector;
        if (sizes == null || selector == null) return;

        var kept = Kept;
        if (kept != null)
        {
            // A wide choice is saved as the place remembered for it, so any other of the game's
            // sizes was saved after it by a copy without the mod: that choice stands, and the wide one goes.
            if (!Testing && selector.index >= 0 && selector.index < GameSizes && selector.index != SavedPlace)
            {
                Plugin.Log.LogInfo($"[wide] {SizeAt(sizes, selector.index)} was saved without the mod after {kept} was kept: it stands");
                KeepPlain(selector.index);
                Keep(null);
                return;
            }
            // Chosen by its size, never by a place: on a smaller monitor this time the list may not
            // hold it, and then the game's own choice is shown and the wide size stays kept.
            int at = IndexOf(sizes, kept.Value);
            if (at >= 0 && at != selector.index) Choose(settings, at);
            return;
        }

        if (!MayPick(settings)) return;
        var now = SizeAt(sizes, selector.index);
        var pick = Pick(Desktop(), now);
        if (pick == null) return;

        // Into the list at its own place among the wide sizes, as every later list will have it,
        // even at a height the list does not have yet (a 1366x768 player gets 1834x768).
        int index = IndexOf(sizes, pick.Value);
        if (index < GameSizes) index = Rebuild(settings, pick.Value);
        if (index < GameSizes) return;

        KeepPlain(selector.index);   // what the wide size is saved as: the player's own size before it
        Choose(settings, index);
        Keep(pick);   // which spends the pick
        Plugin.Log.LogInfo($"[wide] the first time full screen on a wide monitor: {now} becomes {pick}, this once");
    }

    /// <summary>
    /// Only ever once, and only for a player: full screen on a wide desktop, never in a window, a test
    /// or developer mode, where the "desktop" is whatever monitor the test window happens to be on.
    /// </summary>
    private static bool MayPick(ChangeSettings settings) =>
        !Testing && !Dev.Enabled && !Memory.Picked && Screen.fullScreen && settings.FullScreenIndex == 1;

    /// <summary>
    /// The place on the selector, and in the two places the game reads it from before its Apply. Not
    /// in the preferences object the selector was just loaded from: an F7 reloads from that, and an F7
    /// must come back to the game's own size, not the wide one.
    /// </summary>
    private static void Choose(ChangeSettings settings, int index)
    {
        settings.resolutionSelector.index = index;
        settings.ResolutionIndex = index;
        var manager = SettingsManager.Instance;
        if (manager != null) manager.ResolutionIndex = index;
    }

    private static Resolution Make(Size size)
    {
        var made = new Resolution();
        made.width = size.W;
        made.height = size.H;
        return made;
    }

    /// <summary>The size at a place in the list, read the way the game reads it: an empty entry is 1920x1080.</summary>
    private static Size SizeAt(Il2CppStructArray<Resolution> sizes, int index)
    {
        if (sizes == null || index < 0 || index >= sizes.Length) return new Size(1920, 1080);
        var entry = sizes[index];
        return entry.width > 0 && entry.height > 0 ? new Size(entry.width, entry.height) : new Size(1920, 1080);
    }

    private static int IndexOf(Il2CppStructArray<Resolution> sizes, Size size) => IndexIn(sizes, sizes != null ? sizes.Length : 0, size);

    /// <summary>The place of a size among the first <paramref name="count"/> of the list, or -1.</summary>
    private static int IndexIn(Il2CppStructArray<Resolution> sizes, int count, Size size)
    {
        if (sizes == null) return -1;
        for (int i = 0; i < count && i < sizes.Length; i++)
            if (sizes[i].width == size.W && sizes[i].height == size.H) return i;
        return -1;
    }

    // ------------------------------------------------------------------ remembering the choice

    [HarmonyPatch]
    internal static class Choice
    {
        /// <summary>The settings' Apply, which also runs as every menu and bar loads: what is on the selector now.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChangeSettings), nameof(ChangeSettings.ApplyDisplay))]
        private static void Applied(ChangeSettings __instance)
        {
            try
            {
                if (__instance != null && __instance.resolutionSelector != null) Note(__instance, __instance.resolutionSelector.index);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] could not note the display size: {e.Message}"); }
        }

        /// <summary>A size set straight from its place in the list.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChangeSettings), nameof(ChangeSettings.SetResolution))]
        private static void Set(ChangeSettings __instance, int __0)
        {
            try { if (__instance != null) Note(__instance, __0); }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] could not note the display size: {e.Message}"); }
        }
    }

    /// <summary>
    /// A wide place keeps its size. A place among the game's seven is remembered as what a wide
    /// choice is saved as, and forgets the wide size only when that size was in this list to choose:
    /// a 3440x1440 player on a 2560x1080 monitor for once has no 3440x1440 to pick, so the 16:9 size
    /// the game shows there is no choice against it. Only for a list this lengthened: on any other
    /// a missing wide size means something failed, not a choice.
    /// </summary>
    private static void Note(ChangeSettings settings, int index)
    {
        if (!_lengthened.Contains(settings.GetInstanceID()) && !Testing) return;
        var sizes = settings.resolutions;
        if (sizes == null || index < 0 || index >= sizes.Length) return;
        if (index >= GameSizes)
        {
            Keep(SizeAt(sizes, index));
            return;
        }

        KeepPlain(index);
        var kept = Kept;
        if (kept == null) return;
        if (IndexOf(sizes, kept.Value) >= 0) Keep(null);
        else if (_saidAbsent != kept)
        {
            _saidAbsent = kept;
            Plugin.Log.LogInfo($"[wide] {kept} is not in this list (desktop {Desktop()}): it stays kept for a monitor that has it");
        }
    }

    private static Size? _saidAbsent;

    // ------------------------------------------------------------------ what the game saves

    /// <summary>
    /// Every save of the game's settings goes through <c>SaveToPrefs</c>: the settings' Apply, the
    /// settings closing and the game quitting. A wide place is written as the last of the game's own
    /// sizes the player used (<see cref="Plain"/>, else its 1920x1080) and put back in memory straight
    /// after, so a copy without the mod, or after an uninstall, only ever finds a size it knows, and
    /// the one that player had.
    ///
    /// A test copy saves nothing at all. Its display settings are not the machine's, and an F7 there
    /// would write the defaults of every setting, sound and graphics included, over the machine's own.
    /// </summary>
    [HarmonyPatch]
    internal static class Saved
    {
        private static bool _held, _saidTest;
        private static int _index;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SettingsManager), nameof(SettingsManager.SaveToPrefs))]
        private static bool Before(SettingsManager __instance)
        {
            _held = false;
            try
            {
                if (__instance == null) return true;
                if (TestSize != null)
                {
                    if (!_saidTest) Plugin.Log.LogInfo("[wide] test: this copy never saves the game's settings");
                    _saidTest = true;
                    return false;
                }

                int index = __instance.ResolutionIndex;
                if (index < GameSizes) return true;
                _index = index;
                _held = true;
                __instance.ResolutionIndex = SavedPlace;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] the settings were saved as they are: {e.Message}"); }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SettingsManager), nameof(SettingsManager.SaveToPrefs))]
        private static void After(SettingsManager __instance)
        {
            if (!_held) return;
            _held = false;
            try { __instance.ResolutionIndex = _index; }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] could not put the display size back after a save: {e.Message}"); }
        }
    }

    // ------------------------------------------------------------------ a test copy

    /// <summary>
    /// Any copy the test harness placed, developer mode or not. The game asks for full screen when
    /// its saved setting says so, and a test copy must never cover a screen or take focus: it asks
    /// for a window instead. The saved setting itself is untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class HeldWindow
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChangeSettings), nameof(ChangeSettings.LoadPrefs))]
        private static void Windowed(ChangeSettings __instance)
        {
            try
            {
                var prefs = __instance != null ? __instance.playerPreferences : null;
                if (prefs != null) prefs.FullScreenIndex = 0;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] test: could not ask for a window: {e.Message}"); }
        }
    }

    /// <summary>
    /// Only with <c>LIARSBAR8P_UWTEST</c> and developer mode: the size on the selector, in a window,
    /// every time the game sets a size.
    /// </summary>
    [HarmonyPatch]
    internal static class TestWindow
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChangeSettings), nameof(ChangeSettings.ApplyDisplay))]
        private static void Applied(ChangeSettings __instance)
        {
            try
            {
                if (__instance != null && __instance.resolutionSelector != null) InAWindow(__instance, __instance.resolutionSelector.index);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] test: could not keep the window: {e.Message}"); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChangeSettings), nameof(ChangeSettings.SetResolution))]
        private static void Set(ChangeSettings __instance, int __0)
        {
            try { if (__instance != null) InAWindow(__instance, __0); }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] test: could not keep the window: {e.Message}"); }
        }
    }

    private static void InAWindow(ChangeSettings settings, int index)
    {
        if (TestSize == null) return;
        var sizes = settings.resolutions;
        if (sizes == null || index < 0 || index >= sizes.Length) return;

        var size = SizeAt(sizes, index);
        Screen.SetResolution(size.W, size.H, false);
        Plugin.Log.LogInfo($"[wide] test: {size} in a window");
    }

    private static bool _heldRead, _held;

    /// <summary>
    /// A copy the test harness started with its window's place named, developer mode or not. Its
    /// plain copy stands in for a player, and at the first menu the game resizes every copy's window
    /// to the saved size, wider than the test screen, and centres it onto the main one. No player
    /// has the variable.
    /// </summary>
    internal static bool Held
    {
        get
        {
            if (!_heldRead)
            {
                _heldRead = true;
                try { _held = Spot.Parse(Environment.GetEnvironmentVariable(SpotVariable)); }
                catch { }
                if (_held) Plugin.Log.LogInfo($"[wide] test: this copy's window is held at {Spot.Place}");
            }
            return _held;
        }
    }

    /// <summary>From <see cref="ModTicker"/>, every frame. Only a copy the harness placed does anything.</summary>
    internal static void Frame()
    {
        if (Held) Spot.Hold();
    }

    /// <summary>
    /// A test copy's game window, kept at the place the harness named from the first frame on. Two
    /// things put it over the main screen otherwise. The process's first window is BepInEx's console,
    /// so a harness that places "the main window" places that, and the game's own window stays
    /// minimised where the game made it, on the main screen, until something restores it there. And
    /// the game re-centres a window it resizes on whichever screen it fits. So the game's window is
    /// found by its class, and its place is set to the named one as soon as it exists, minimised or
    /// not, and again whenever it changes: checked every frame, and never activating it.
    ///
    /// A check each frame still comes after the move, so for a frame the window could be drawn
    /// where the game sent it: a 3440x1440 test window that does not fit the screen it sits on is
    /// sent to the one it fits, which is the main screen. So the window's own procedure is wrapped
    /// as well (<see cref="Wrapped"/>): every move Windows is about to make to it, other than to
    /// the minimised parking place, is turned into a move to the named place before it happens.
    /// Its size still changes as asked.
    /// </summary>
    private static class Spot
    {
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Box { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Placement { public int Length, Flags, Show; public Point Min, Max; public Box Normal; }
        [StructLayout(LayoutKind.Sequential)] private struct Moving { public IntPtr Window, After; public int X, Y, W, H; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct ScreenInfo { public int Size; public Box Whole, Work; public int Flags; }

        private delegate bool EachWindow(IntPtr window, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr Procedure(IntPtr window, uint message, IntPtr w, IntPtr l);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EachWindow each, IntPtr data);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int most);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Box box);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Box box, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr screen, ref ScreenInfo info);

        private const string GameClass = "UnityWndClass";
        private const int ShowNoActivate = 4, ShowMinimisedNoActivate = 7;
        private const int ProcedureSlot = -4;             // GWLP_WNDPROC
        private const uint PlaceChanging = 0x0046;        // WM_WINDOWPOSCHANGING
        private const uint DoNotMove = 0x0002;            // SWP_NOMOVE
        private const uint DoNotActivate = 0x0010;        // SWP_NOACTIVATE
        private static readonly IntPtr NoActivation = (IntPtr)3;   // MA_NOACTIVATE
        private const int Parked = -30000;                // a minimised window is moved to -32000
        private const uint Nearest = 2;                   // MONITOR_DEFAULTTONEAREST
        private const int MainScreen = 1;                 // MONITORINFOF_PRIMARY

        private static readonly EachWindow _each = Look;   // held here, so it outlives every call
        private static readonly Procedure _procedure = Wrapped;
        private static uint _process;
        private static IntPtr _window, _found, _wrapped, _previous;
        private static float _lookAgain;
        private static int _x, _y, _moves, _turned;

        internal static string Place => $"{_x},{_y}";

        /// <summary>"2000,40".</summary>
        internal static bool Parse(string at)
        {
            string[] parts = (at ?? "").Split(',');
            return parts.Length == 2 &&
                   int.TryParse(parts[0].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _x) &&
                   int.TryParse(parts[1].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _y);
        }

        private static bool Look(IntPtr window, IntPtr data)
        {
            GetWindowThreadProcessId(window, out uint process);
            if (process != _process) return true;
            var name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() != GameClass) return true;
            _found = window;
            return false;
        }

        /// <summary>This process's game window, looked for again if the game made a new one.</summary>
        private static IntPtr Window()
        {
            if (_window != IntPtr.Zero && IsWindow(_window)) return _window;
            _window = IntPtr.Zero;
            if (Time.unscaledTime < _lookAgain) return IntPtr.Zero;
            _lookAgain = Time.unscaledTime + 0.25f;
            if (_process == 0) _process = (uint)Environment.ProcessId;
            _found = IntPtr.Zero;
            EnumWindows(_each, IntPtr.Zero);
            _window = _found;
            return _window;
        }

        private static Placement Read(IntPtr window, out bool ok)
        {
            var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            ok = GetWindowPlacement(window, ref placement);
            return placement;
        }

        /// <summary>The game's window procedure, with every move it is about to get sent to the named place instead.</summary>
        private static void Wrap(IntPtr window)
        {
            if (window == _wrapped || _wrapFailed) return;
            _previous = SetWindowLongPtr(window, ProcedureSlot, Marshal.GetFunctionPointerForDelegate(_procedure));
            if (_previous != IntPtr.Zero) _wrapped = window;
            else _wrapFailed = true;   // tried once: the check each frame still holds it
            Plugin.Log.LogInfo(_previous != IntPtr.Zero ? $"[wide] test: every move of the game's window now lands at {_x},{_y}"
                                                        : "[wide] test: the game's window could not be wrapped - only the check each frame holds it");
        }

        private static bool _wrapFailed;

        /// <summary>
        /// Before Windows moves the window: a move anywhere but the named place goes there instead,
        /// the restore from minimised included, which is the game's own first show of its window,
        /// centred on its screen. Only a move to the minimised parking place is left as it is, so
        /// the guard's minimise still works. Every move, show and minimise also goes without
        /// activation, and a click on the window activates nothing: a test copy must never take
        /// the foreground from the owner's game (<see cref="HeldFocus"/>, which also hears every
        /// activation message, and hands back a foreground the game takes anyway). Never throws
        /// back into Windows.
        /// </summary>
        private static IntPtr Wrapped(IntPtr window, uint message, IntPtr w, IntPtr l)
        {
            if (message == PlaceChanging && l != IntPtr.Zero)
            {
                try
                {
                    var move = Marshal.PtrToStructure<Moving>(l);
                    bool changed = false;
                    if ((move.Flags & DoNotMove) == 0 && (move.X != _x || move.Y != _y) && move.X > Parked && move.Y > Parked)
                    {
                        move.X = _x;
                        move.Y = _y;
                        changed = true;
                        _turned++;
                    }
                    if ((move.Flags & DoNotActivate) == 0)
                    {
                        HeldFocus.Quieted(move.Flags);
                        move.Flags |= DoNotActivate;
                        changed = true;
                    }
                    if (changed) Marshal.StructureToPtr(move, l, false);
                }
                catch { }
            }
            else if (message == HeldFocus.MouseMessage)
            {
                HeldFocus.Seen(message, w, l);
                return NoActivation;
            }
            else if (message == HeldFocus.AppMessage || message == HeldFocus.ActivateMessage || message == HeldFocus.FrameMessage)
                HeldFocus.Seen(message, w, l);
            return CallWindowProc(_previous, window, message, w, l);
        }

        internal static void Hold()
        {
            var window = Window();
            if (window == IntPtr.Zero) return;
            Wrap(window);
            var placement = Read(window, out bool ok);
            if (!ok) { _window = IntPtr.Zero; return; }
            if (window != _described)
            {
                _described = window;
                Plugin.Log.LogInfo($"[wide] test: the game's window as first found, before any move: {Where(window, placement)}");
            }
            if (placement.Normal.Left == _x && placement.Normal.Top == _y) return;

            int w = placement.Normal.Right - placement.Normal.Left, h = placement.Normal.Bottom - placement.Normal.Top;
            int fromX = placement.Normal.Left, fromY = placement.Normal.Top;
            bool minimised = IsIconic(window);
            placement.Normal = new Box { Left = _x, Top = _y, Right = _x + w, Bottom = _y + h };
            placement.Show = minimised ? ShowMinimisedNoActivate : ShowNoActivate;
            placement.Flags = 0;
            SetWindowPlacement(window, ref placement);
            if (++_moves <= 5 || _moves % 50 == 0)
                Plugin.Log.LogInfo($"[wide] test: the game's window ({(minimised ? "minimised" : "on show")}, {w}x{h}) had its place at " +
                                   $"{fromX},{fromY}: now {_x},{_y} (move {_moves})");
        }

        /// <summary>
        /// Where the window is and which screen that is, the main one named: the window itself
        /// when on show, its restored place when minimised.
        /// </summary>
        private static string Where(IntPtr window, Placement placement)
        {
            bool minimised = IsIconic(window);
            var box = placement.Normal;
            string text = $"{(minimised ? "minimised" : "on show")}, its restored place {box.Left},{box.Top} {box.Right - box.Left}x{box.Bottom - box.Top}";
            if (!minimised && GetWindowRect(window, out var shown))
            {
                box = shown;
                text += $", on screen at {box.Left},{box.Top} {box.Right - box.Left}x{box.Bottom - box.Top}";
            }
            var screen = new ScreenInfo { Size = Marshal.SizeOf<ScreenInfo>() };
            if (!GetMonitorInfo(MonitorFromRect(ref box, Nearest), ref screen)) return text + ", on a screen that cannot be read";
            var whole = screen.Whole;
            return text + $", on the screen at {whole.Left},{whole.Top} {whole.Right - whole.Left}x{whole.Bottom - whole.Top}" +
                   ((screen.Flags & MainScreen) != 0 ? " - THE MAIN SCREEN" : ", not the main screen");
        }

        private static IntPtr _described;

        internal static string Describe()
        {
            var window = Window();
            if (window == IntPtr.Zero) return "no game window found";
            var placement = Read(window, out bool ok);
            if (!ok) return "the game window cannot be read";
            return $"game window {Where(window, placement)}, held at {_x},{_y}, set {_moves} times, " +
                   $"{(_wrapped != IntPtr.Zero ? $"{_turned} moves turned back as they happened" : "not wrapped")}; {HeldFocus.Describe()}";
        }
    }

    // ------------------------------------------------------------------ for the test driver

    /// <summary>The settings screen on show, else the first one in a loaded scene.</summary>
    private static ChangeSettings Settings()
    {
        ChangeSettings first = null;
        foreach (var found in Resources.FindObjectsOfTypeAll(Il2CppType.Of<ChangeSettings>()))
        {
            var settings = found.TryCast<ChangeSettings>();
            if (settings == null || !settings.gameObject.scene.IsValid()) continue;
            if (settings.gameObject.activeInHierarchy) return settings;
            first ??= settings;
        }
        return first;
    }

    /// <summary>The screen, the desktop, the list as the selector has it, and what is kept.</summary>
    internal static string Describe()
    {
        var text = new StringBuilder();
        text.Append($"screen {Screen.width}x{Screen.height} ({(ScreenWide ? "wide" : "not wide")}), full screen {Screen.fullScreen}, " +
                    $"desktop {Desktop()}{(Testing ? " (test)" : "")}, monitor {Screen.currentResolution.width}x{Screen.currentResolution.height}; ");

        var settings = Settings();
        var sizes = settings != null ? settings.resolutions : null;
        var selector = settings != null ? settings.resolutionSelector : null;
        if (sizes == null || selector == null || selector.data == null) text.Append("no display list here; ");
        else
        {
            text.Append($"list of {sizes.Length} ({selector.data.Count} names{(_lengthened.Contains(settings.GetInstanceID()) ? ", lengthened" : "")}):");
            for (int i = 0; i < selector.data.Count; i++)
                text.Append($" {(i == selector.index ? "[" : "")}{selector.data[i]}{(i == selector.index ? "]" : "")}");
            text.Append($"; selector shows {selector.value}, {(selector.gameObject.activeInHierarchy ? "on screen" : "not on screen")} at ");
            var names = new List<string>();
            for (var up = selector.transform; up != null; up = up.parent) names.Insert(0, up.name);
            text.Append(string.Join("/", names)).Append("; ");
        }

        var manager = SettingsManager.Instance;
        text.Append($"kept {(Kept?.ToString() ?? "none")} ({(Testing ? "test, in memory" : Memory.Name)}), pick spent {Memory.Picked}, " +
                    $"saved as place {SavedPlace}{(Plain == null ? " (none remembered)" : "")}, " +
                    $"in memory {(manager != null ? manager.ResolutionIndex : -1)}, saved {PlayerPrefs.GetInt(IndexKey, -1)}, " +
                    $"saved full screen {PlayerPrefs.GetInt(FullKey, -1)}; {Spot.Describe()}");
        return text.ToString();
    }

    /// <summary>The selector's own arrow, as a click on it.</summary>
    internal static string Step(int by)
    {
        var selector = Settings()?.resolutionSelector;
        if (selector == null) return "no display list here";
        if (by < 0) selector.OnLeftClicked();
        else selector.OnRightClicked();
        return Describe();
    }

    /// <summary>The settings' own Apply for the display.</summary>
    internal static string Apply()
    {
        var settings = Settings();
        if (settings == null) return "no settings here";
        settings.ApplyDisplay();
        return Describe();
    }

    /// <summary>What F7 does in the menu.</summary>
    internal static string Reset()
    {
        var settings = Settings();
        if (settings == null) return "no settings here";
        settings.ResetSettings();
        return Describe();
    }

    /// <summary>
    /// "2580x1080" or "none": kept as a choice in the list keeps it, and "read": the file read again,
    /// as the next session finds it. Only in a developer copy with a file of its own, never the player's.
    /// </summary>
    internal static string KeepForTest(string words)
    {
        if (Testing) return "a wide test keeps its choice in memory, not in a file";
        if (!Memory.OwnFile) return "only a developer copy with a role has a file of its own to write";

        string word = (words ?? "").Trim();
        if (word == "read") Memory.Reload();
        else if (word == "none") Keep(null);
        else if (Size.TryParse(word, out var size) && IsWide(size)) Keep(size);
        else return "give a wide size such as 2580x1080, none, or read";
        return Describe();
    }

    /// <summary>"3440x1440 1920x1080": what the one-time pick would choose on that desktop, playing at that size.</summary>
    internal static string WouldPick(string words)
    {
        string[] parts = (words ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Size.TryParse(parts[0], out var desktop) || !Size.TryParse(parts[1], out var now))
            return "give a desktop and a current size, such as 3440x1440 1920x1080";
        var pick = Pick(desktop, now);
        var list = Extras(desktop, null, pick);
        return $"desktop {desktop} playing at {now}: picks {(pick?.ToString() ?? "nothing")}; " +
               $"the list would add {string.Join(", ", list)}" +
               (pick is Size p ? $", the pick at place {GameSizes + list.IndexOf(p)}, as every list built after it has it" : "");
    }
}
