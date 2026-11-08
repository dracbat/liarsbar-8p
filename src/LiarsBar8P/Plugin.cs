using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace LiarsBar8P;

[BepInPlugin(Guid, "Liar's Bar 8 Players", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "liarsbar.eightplayers";
    public const string Version = "3.0.2";

    /// <summary>
    /// The mod's name as players see it, and the one place it is written. Only words on screen
    /// use it. The plugin id, the file names, the lobby keys and the name in the attribute above
    /// keep the old ones on purpose: installs, settings, the updater and the lobby pool all find
    /// the mod by those, and nobody sees them.
    /// </summary>
    public const string DisplayName = "Fully Loaded";

    /// <summary>With the game's name in front, for headings that have to stand alone.</summary>
    public const string FullName = "Liar's Bar: " + DisplayName;

    /// <summary>The name in capitals, the way the tag and the lobby rows show it.</summary>
    internal static readonly string TagName = DisplayName.ToUpperInvariant();

    /// <summary>The full name in capitals, for the headings of the mod's windows.</summary>
    internal static readonly string TitleName = FullName.ToUpperInvariant();

    public new static ManualLogSource Log;
    public static ConfigEntry<int> MaxPlayers;
    public static ConfigEntry<bool> AutoReadyJoiners;
    public static ConfigEntry<UpdateMode> Updates;
    public static ConfigEntry<float> EmoteCooldownSeconds;
    public static ConfigEntry<bool> ShowUnmoddedLobbies;
    public static ConfigEntry<bool> AllowBots;
    public static ConfigEntry<bool> FullyLoaded;
    public static ConfigEntry<string> ChangelogShownFor;
    public static ConfigEntry<bool> Verbose;
    public static ConfigEntry<bool> DiagAutoHost;
    public static ConfigEntry<bool> DiagSoloStart;
    public static ConfigEntry<bool> DiagDeckPatchTest;
    public static ConfigEntry<bool> DeveloperMode;
    public static ConfigEntry<bool> DevAutoTest;
    public static ConfigEntry<int> DevTestPlayers;
    public static ConfigEntry<float> DevShotSeconds;

    /// <summary>
    /// A bare test copy: only the window hold, the test kit's driver and the loopback harness go
    /// on (and the ranked block, see <see cref="LoadBare"/>), so the kit can measure what the game
    /// holds without the mod in it. Read from the
    /// harness's own environment, and only with the harness's developer switch beside it, never
    /// the config file's, so nothing a player can reach turns it on.
    /// </summary>
    internal static bool Bare
    {
        get
        {
            if (_bare == null)
            {
                string raw = null;
                try { raw = System.Environment.GetEnvironmentVariable("LIARSBAR8P_BARE"); }
                catch { }
                _bare = raw == "1" && Dev.FromHarness;
            }
            return _bare.Value;
        }
    }

    private static bool? _bare;

    /// <summary>
    /// FullyLoaded is off: the mod is the switch on the main menu and nothing else
    /// (<see cref="FullyLoadedSwitch"/>). Never in a bare test copy, which has its own short load.
    /// </summary>
    internal static bool Off { get; private set; }

    public override void Load()
    {
        Log = base.Log;

        // First, because off, the mod is the switch on the main menu and nothing else.
        FullyLoaded = Config.Bind("General", "FullyLoaded", true,
            "On: everything this mod adds. Off: the game plays exactly like the base game, ranked " +
            "included, and the only thing left of the mod is the FULLY LOADED switch on the main menu, " +
            "which turns it back on. The switch restarts the game for you; a change made here loads the " +
            "next time the game starts. The installer writes this file afresh, so this is on again after reinstalling.");
        Off = !FullyLoaded.Value && !Bare;

        // The bug report's memory of this session, first, so it keeps the start of it too.
        if (!Bare)
        {
            if (!Off) BugReport.Listen();
            Words.Load();    // the mod's words in the game's language, before anything shows any
        }

        // Its own log file per process, before anything else can want to write to one.
        InstanceLog.Start(System.Environment.GetEnvironmentVariable("LIARSBAR8P_ROLE") ?? "game");
        Loopback.Configure();

        MaxPlayers = Config.Bind("General", "MaxPlayers", 8,
            new ConfigDescription("Maximum players per lobby. Every player must run the same value.",
                new AcceptableValueRange<int>(2, 16)));
        AutoReadyJoiners = Config.Bind("General", "AutoReadyJoiners", true,
            "When you host: mark each player ready once as they arrive in your lobby - when they join, and " +
            "again when everyone comes back to the lobby after a match - so you only have to press Start. " +
            "They can still un-ready with the game's own button, and being ready locks nothing in the lobby. " +
            "Never applies to you, the host. Only the host's setting matters.");
        Updates = Config.Bind("General", "Updates", UpdateMode.Ask,
            "What to do when a newer version of this mod is out, checked each time the game starts. Ask: a " +
            "window in the game asks whether to install it. Automatic: install it without asking. Off: never " +
            "check. Either way it loads the next time the game starts. Everyone at a table must run the same version.");
        EmoteCooldownSeconds = Config.Bind("General", "EmoteCooldownSeconds", 10f,
            new ConfigDescription("Seconds between your emotes - the game's own wait is 12. It is counted on " +
                "your own machine, as the game counts it, and applies at every table size, but only at a " +
                "table where everyone has the mod. Anywhere else your emotes keep the game's own wait.",
                new AcceptableValueRange<float>(1f, 60f)));
        ShowUnmoddedLobbies = Config.Bind("General", "ShowUnmoddedLobbies", false,
            "The lobby list and Quick Join only look at lobbies hosted with this mod, which is where " +
            "eight-player tables are. Turn this on to put the game's normal list and Quick Join back: " +
            "you then see lobbies without the mod, and not the ones with it. Lobbies you host stay " +
            "modded either way, and friend invites and the lobby ID box work the same either way.");
        AllowBots = Config.Bind("General", "AllowBots", true,
            "When you host: ADD BOT and REMOVE BOT in the lobby, at the tables bots can play. A bot takes " +
            "an empty seat and gives it up to anyone who joins a full table. Turn this off to host without " +
            "them. Only the host's setting matters. The installer writes this file afresh, so this is on " +
            "again after reinstalling.");
        // A wide display size is not a setting here: the installers start this file afresh on every
        // install, so the player's choice is kept where they cannot reach it (Ultrawide.Memory).

        ChangelogShownFor = Config.Bind("Debug", "ChangelogShownFor", "",
            "The version whose changes were last shown after an update. The mod keeps this itself.");

        Verbose = Config.Bind("Debug", "VerboseDiagnostics", true,
            "Dump runtime seat/prefab/slot counts to the log. Needed while the mod is still being built out.");

        DeveloperMode = Config.Bind("Developer", "DeveloperMode", false,
            "Developer testing tools: bots that fill empty seats, an on-screen panel (F8) and a " +
            "running account of registration, seating, dealing, turns and eliminations. An eight " +
            "player round cannot be tested with eight people to hand; this is how it gets tested. " +
            "Leave it off for normal play.");

        // The self-test switches exist only with developer mode on, so a player's settings file
        // never holds them.
        if (Dev.Enabled)
        {
            DiagAutoHost = Config.Bind("Debug", "SelfTestAutoHostLobby", false,
                "Development self test: auto-host a PRIVATE lobby on startup to capture lobby diagnostics.");

            DiagSoloStart = Config.Bind("Debug", "SelfTestForceSoloStart", false,
                "Development self test: force a solo match start to capture in-game seat diagnostics.");

            DiagDeckPatchTest = Config.Bind("Debug", "SelfTestDeckSizePatch", false,
                "Development self test: at startup, rewrite the deal's hardcoded deck size for a pretend five player table and report whether the write succeeded. Verifies the memory patch without needing other players.");
        }

        // Like the self tests, only with developer mode on, so a player's settings file never holds them.
        if (Dev.Enabled)
        {
            DevAutoTest = Config.Bind("Developer", "AutoTestFullTable", false,
                "With developer mode on: once a lobby is hosted, fill it with bots and start the " +
                "match automatically, printing the state at each step. One launch, one whole round, " +
                "no keyboard.");

            DevTestPlayers = Config.Bind("Developer", "AutoTestPlayers", 0,
                "With AutoTestFullTable on: how many players to sit at the table, counting the " +
                "host. Zero means fill every seat. Set it to 5 or 6 to test a partly full table, " +
                "where the seats have to be re-spaced - that is a different case from a full ring " +
                "and has to be tested without changing MaxPlayers, which would change what is " +
                "being tested.");
        }

        DevShotSeconds = Config.Bind("Developer", "ScreenshotEverySeconds", 0f,
            "With developer mode on: take a screenshot this often, into " +
            "%LOCALAPPDATA%\\LiarsBar8P\\shots. Zero turns it off, which is what you want while " +
            "actually playing - it writes a few megabytes a shot and pauses briefly each time. " +
            "Set it to 5 when you want a picture record of a test run.");

        Log.LogInfo($"=== Liar's Bar 8P loading (target={MaxPlayers.Value}) ===");

        // Said plainly and early, because a test run with this off drives nobody and reports
        // zeroes that look exactly like a mode with nothing wrong with it.
        if (Dev.Enabled)
        {
            Log.LogWarning("  DEVELOPER MODE IS ON" +
                           (Dev.FromHarness ? " (LIARSBAR8P_DEV is set - this is a test run)"
                                            : " (DeveloperMode in the config file)"));
            AskForStackTraces();
        }
        else
            Log.LogInfo("  developer mode off - normal play");

        if (Bare) { LoadBare(); return; }
        if (Off) { LoadOff(); return; }

        var harmony = new Harmony(Guid);

        // First, on every copy, loopback included: ranked is off while the mod is installed. The
        // button and the search are applied apart, so a game update that renames one still leaves
        // the other, and the watchdog in ModTicker needs no patch at all.
        Apply(harmony, typeof(RankedGuard.ButtonLine), "ranked: the button says ranked is off");
        Apply(harmony, typeof(RankedGuard.SearchLine), "ranked: no search from a modded copy");

        // Patch each area independently so one broken hook cannot disable the rest.
        Apply(harmony, typeof(CapPatches),     "player caps");
        Apply(harmony, typeof(JoinFix),        "join limits");
        Apply(harmony, typeof(CommandGuard),   "command guards");
        Apply(harmony, typeof(SeatAssign),     "seat assignment");
        Apply(harmony, typeof(TransportCap),   "steam transport cap");
        Apply(harmony, typeof(LobbyPodiums),   "lobby podiums beyond four");
        Apply(harmony, typeof(LobbySkinClick), "clicking your character in a lobby of five or more");
        Apply(harmony, typeof(AutoReady),      "readying players as they arrive in the lobby");
        Apply(harmony, typeof(SpinBoard),      "every player on the Liar's Spin leaderboard");
        Apply(harmony, typeof(TurnOrderFix),   "turn order");
        Apply(harmony, typeof(SeatExpansion),  "table seat expansion");
        Apply(harmony, typeof(SeatRing),       "seat spacing");
        Apply(harmony, typeof(RosterFix),      "roster + seat indices");
        Apply(harmony, typeof(CardTypeFix),    "card type wrapping");
        Apply(harmony, typeof(DeckFix),        "deck top-up");
        Apply(harmony, typeof(JoinDiag),       "client join + roster");
        Apply(harmony, typeof(VersionCheck),   "version handshake");
        Apply(harmony, typeof(DeckDiag),       "deck diagnostics");
        Apply(harmony, typeof(DiagPatches),    "diagnostics");
        Apply(harmony, typeof(DiagAutoHost),   "self test: auto host");
        Apply(harmony, typeof(DiagSoloStart),  "self test: solo start");
        Apply(harmony, typeof(TableFill),      "seat everyone StartGame missed");
        Apply(harmony, typeof(DealTrace),      "deal trace");
        Apply(harmony, typeof(DevLogging),     "developer logging");
        Apply(harmony, typeof(RpcTrace),       "naming a remote call that throws");
        Apply(harmony, typeof(BetVoiceGuard),  "dice bids beyond the recorded voice lines");
        Apply(harmony, typeof(ResetAnimFix),   "reset animation beyond four seats");
        Apply(harmony, typeof(AimRing),        "aiming at every seat, not three");
        Apply(harmony, typeof(AimKeys),        "aim keys reaching every seat in the Chaos deck");
        Apply(harmony, typeof(EmoteTargets),   "middle finger at every seat, not three directions");
        Apply(harmony, typeof(TexasSwap),      "Liar's Texas card swaps and the deck at eight");
        Apply(harmony, typeof(DiceRevealRows), "Liar's Dice reveals of more than twenty dice");
        Apply(harmony, typeof(DiceRowOrder),   "Liar's Dice reveal rows lit in reading order");
        Apply(harmony, typeof(MechanicsTrace),  "devil and chaos mechanics trace");
        Apply(harmony, typeof(HiddenTables),   "Liar's Deck 2 on the deck arrows");
        TwoCardPoker.Patched =
            Apply(harmony, typeof(TwoCardPoker), "Two-Card Poker: the table behind the Poker tab");
        Apply(harmony, typeof(TwoCardPoker.MatchLog), "Two-Card Poker: the match, for the log");
        Apply(harmony, typeof(TwoCardLobby),   "Two-Card Poker: its arrows, name and rules page in the lobby");
        Apply(harmony, typeof(LobbyId.Copy),   "lobby ID: the copy icon copies and says so");
        Apply(harmony, typeof(BugReport.SettingsButton), "bug report: a button in the settings");

        // Wide screens, each part on its own. The save guard first: a test copy is only allowed
        // its window with the guard on, so it can never write the game's display settings. The
        // wide sizes go on the list only with both of the others: without the guard a wide place
        // would reach the game's own save, and without the choice a kept size could never be cleared.
        Ultrawide.SavesGuarded =
            Apply(harmony, typeof(Ultrawide.Saved),  "wide screens: a wide size is saved as one of the game's own");
        bool wideRemembered =
            Apply(harmony, typeof(Ultrawide.Choice), "wide screens: a wide choice is remembered");
        if (Ultrawide.SavesGuarded && wideRemembered)
            Apply(harmony, typeof(Ultrawide.Sizes),  "wide screens: the monitor's shape in the display settings");
        else
            Log.LogWarning("  skipped: wide screens: the display list stays the game's own, a part it needs did not go on");
        if (Ultrawide.Held)
        {
            Ultrawide.WindowGuarded =
                Apply(harmony, typeof(Ultrawide.HeldWindow), "test copies: a window, never full screen");
            // Now, not on the first frame: the game shows its window, centred on its screen, as
            // soon as the plugins have loaded, and a wide one centred there covers the main screen.
            try { Ultrawide.Frame(); }
            catch (System.Exception e) { Log.LogWarning($"[wide] test: the window is held from the first frame on: {e.Message}"); }
            // And a foreground the game takes anyway goes straight back to the window that had it.
            try { HeldFocus.Start(); }
            catch (System.Exception e) { Log.LogWarning($"[wide] test: the foreground is not watched: {e.Message}"); }
        }
        if (Ultrawide.TestSize != null)
            Ultrawide.TestReady =
                Apply(harmony, typeof(Ultrawide.TestWindow), "wide screens: the test size, in a window");
        Apply(harmony, typeof(LobbyWardrobe.Build), "a skin picker in the lobby");
        Apply(harmony, typeof(LobbyWardrobe.Follow), "the skin picker waits for a character change");
        Apply(harmony, typeof(MatchStats.CoreHooks),   "awards: the start and end of a match");
        Apply(harmony, typeof(MatchStats.DeckHooks),   "awards: Liar's Deck");
        Apply(harmony, typeof(MatchStats.ChaosHooks),  "awards: the Chaos deck");
        Apply(harmony, typeof(MatchStats.DiceHooks),   "awards: Liar's Dice");
        Apply(harmony, typeof(MatchStats.PokerHooks),  "awards: Liar's Poker");
        Apply(harmony, typeof(MatchStats.SlotsHooks),  "awards: Liar's Slots");
        Apply(harmony, typeof(MatchBoard.HandHooks),   "the Tab board: the cards left in each hand");

        // House rules: the choice and its row, then each rule on its own, so a game update that
        // renames one method costs that one rule, which is then never switched on or named.
        Apply(harmony, typeof(HouseRules.Freeze), "house rules: settled as a match starts");
        Apply(harmony, typeof(RuleRow.Build),     "house rules: the row in the lobby");
        HouseRules.Works(HouseRules.Rule.FastClock,
            Apply(harmony, typeof(RuleEffects.FastClock),     "house rules: Fast Clock"));
        HouseRules.Works(HouseRules.Rule.Cylinder4 | HouseRules.Rule.Cylinder3,
            Apply(harmony, typeof(RuleEffects.ShortCylinder), "house rules: Short Cylinder"));
        HouseRules.Works(HouseRules.Rule.ShortHand,
            Apply(harmony, typeof(RuleEffects.ShortHand),     "house rules: Short Hand"));
        HouseRules.Works(HouseRules.Rule.ChaosStorm,
            Apply(harmony, typeof(RuleEffects.ChaosStorm),    "house rules: Chaos Storm"));
        bool diceRolls =
            Apply(harmony, typeof(RuleEffects.DiceRolls),     "house rules: Three Dice and One Sip");
        HouseRules.Works(HouseRules.Rule.OneSip, diceRolls);
        // Three Dice also needs each cup kept to the dice dealt, or both dice tables throw every frame.
        HouseRules.Works(HouseRules.Rule.ThreeDice, diceRolls &&
            Apply(harmony, typeof(RuleEffects.ThreeDiceCups), "house rules: Three Dice, each cup keeps the dice dealt"));
        Apply(harmony, typeof(RuleEffects.OneSipLook),        "house rules: One Sip's last drink");
        HouseRules.Works(HouseRules.Rule.HotTable,
            Apply(harmony, typeof(RuleEffects.HotTable),      "house rules: Hot Table"));
        HouseRules.Works(HouseRules.Rule.NoCowardFold,
            Apply(harmony, typeof(RuleEffects.NoCowardFold),  "house rules: No Coward's Fold"));
        HouseRules.Works(HouseRules.Rule.HeadStart,
            Apply(harmony, typeof(RuleEffects.HeadStart),     "house rules: Head Start"));
        HouseRules.Works(HouseRules.Rule.DeadMansSpin,
            Apply(harmony, typeof(RuleEffects.DeadMansSpin),  "house rules: Dead Man's Spin"));

        // Blocking: the refusal at the door applies under loopback too, so the harness can test it.
        Apply(harmony, typeof(BlockList.Refuse),     "block list: blocked players refused at the door");
        Apply(harmony, typeof(BlockButtons.Follow),  "block list: the buttons on the name plates");
        Apply(harmony, typeof(SteamIdFix),           "each joiner's Steam id from their connection");

        // Fair play: the host's checks on the people at its table (the AntiCheat folder).
        Fair.Install(harmony, Config);

        // Bots: a seat on the network that nobody owns, then the host answering for it. Without
        // both, no bot is added at all, and nothing else changes.
        BotSeats.SpawnOn =
            Apply(harmony, typeof(BotSeats.Spawn), "bots: a bot's seat on the network, owned by nobody") &&
            BotSeats.SpawnPatched();
        BotSeats.CallsOn =
            Apply(harmony, typeof(BotSeats.Calls), "bots: the host answers for a bot's seat");
        if (!BotSeats.Works) Log.LogWarning("  bots are off: a part they need did not go on");
        BotSeats.ThrowsOn =
            Apply(harmony, typeof(BotSeats.Events), "bots: a bot's thrown cards leave its hand and reach the table");
        BotSeats.TriggerOn =
            Apply(harmony, typeof(BotSeats.Trigger), "bots: a bot pulls its own trigger in Liar's Deck");
        BotSeats.GunDownOn =
            Apply(harmony, typeof(BotSeats.GunDown), "bots: a bot puts the Chaos deck's gun down after its shot");

        // Bots and people in the lobby, each apart, so one that fails costs only itself.
        Apply(harmony, typeof(BotButtons.Build), "bots: ADD BOT and REMOVE BOT in the lobby");
        Apply(harmony, typeof(BotLobby.Kick),    "bots: the game's own Kick takes a bot away");
        BotLobby.ArrivalOn =
            Apply(harmony, typeof(BotLobby.Arrival), "bots: a person joining a full table takes the newest bot's seat");
        Apply(harmony, typeof(BotLobby.NextHost), "bots: never chosen to take over as host");
        HiddenDice.On =
            Apply(harmony, typeof(HiddenDice.Roll), "Liar's Dice: nobody's dice but yours drawn from each roll until the reveal");
        Apply(harmony, typeof(HiddenDice.Shake), "Liar's Dice: nobody's dice but yours drawn from each shake of the cups");
        Apply(harmony, typeof(HiddenDiceLog), "Liar's Dice: every seat's dice kept out of the log");

        // Steam is stubbed in the loopback harness, and there is no lobby there to find.
        if (!Loopback.Requested)
        {
            Apply(harmony, typeof(Matchmaking), "matchmaking: the modded lobby pool");
            Apply(harmony, typeof(LobbyRows),   "matchmaking: rows in the lobby list");
            Apply(harmony, typeof(QuickMatch),  "matchmaking: Quick Join");
            Apply(harmony, typeof(LobbyCount),  "matchmaking: open lobbies on the main menu");
            Apply(harmony, typeof(LobbyId.Check), "lobby ID: the JOIN WITH ID box says what is wrong");
        }

        // Only does anything when the loopback harness is running.
        if (Loopback.Requested) Apply(harmony, typeof(LoopbackSteamStub), "loopback steam stub");
        if (Loopback.Requested) Apply(harmony, typeof(LoopbackNoSteamLobbies), "loopback: a test copy never opens or joins a Steam lobby");

        if (DiagDeckPatchTest != null && DiagDeckPatchTest.Value && Dev.Enabled)
        {
            Log.LogInfo("  [selftest] exercising the deck size patch for 5 players...");
            DeckSizePatch.ApplyFor(5);
        }

        // The turn-order constants live in compiled code, so they are rewritten rather than
        // patched. Done here so the log says plainly whether it worked, and retried at the
        // start of a round if the game was not ready yet.
        TurnOrderFix.Install();
        DealArrayPatch.Install();

        // Watches the log for the fault that kills a Liar's Dice reveal above four players.
        // Not a developer tool: the bug reaches players, so the recovery has to as well.
        DiceRevealGuard.Listen();

        // The data channel between copies that both run the mod. Registered this early on
        // purpose: see ModNet.Install. Not a patch, so not in the list above.
        ModNet.Install();
        ModNetProof.Install();   // nothing unless developer mode, the loopback harness and LIARSBAR8P_NETPROOF
        AwardPanel.Install();    // what to do with the host's awards when they arrive
        MatchBoard.Install();    // and with its count of the cards in each hand
        // SeatCams patches nothing: the game's own spectator keys walk the cameras it adds, from ModTicker.
        HouseRules.Install();    // and with the house rules the host has picked
        TwoCardPoker.Install();  // and with the table the host has chosen on the Poker tab

        SpawnHud();

        // In the background: the game never waits on the network to start.
        LiarsBar8P.AutoUpdate.Start();

        Log.LogInfo("=== Liar's Bar 8P loaded ===");
    }

    /// <summary>
    /// The whole load with FullyLoaded off (<see cref="Off"/>): the switch on the main menu, which
    /// needs no patch, only the ticker. No other patch, no lobby key, no update check. A copy the
    /// test harness started keeps its window held as well, which no player's copy has.
    /// </summary>
    private static void LoadOff()
    {
        Log.LogInfo($"  {DisplayName} is off: only the switch on the main menu (FullyLoaded = false in the config)");
        if (Ultrawide.Held) HoldTestWindow(new Harmony(Guid));
        SpawnHud();
        Log.LogInfo("=== Liar's Bar 8P loaded (Fully Loaded off) ===");
    }

    /// <summary>A test copy's window, held where the harness put it (<see cref="Ultrawide.Held"/>).</summary>
    private static void HoldTestWindow(Harmony harmony)
    {
        Ultrawide.SavesGuarded =
            Apply(harmony, typeof(Ultrawide.Saved), "wide screens: a wide size is saved as one of the game's own");
        Ultrawide.WindowGuarded =
            Apply(harmony, typeof(Ultrawide.HeldWindow), "test copies: a window, never full screen");
        try { Ultrawide.Frame(); }
        catch (System.Exception e) { Log.LogWarning($"[wide] test: the window is held from the first frame on: {e.Message}"); }
        try { HeldFocus.Start(); }
        catch (System.Exception e) { Log.LogWarning($"[wide] test: the foreground is not watched: {e.Message}"); }
    }

    /// <summary>
    /// A bare test copy's whole load (<see cref="Bare"/>). The ranked block stays on too: a copy
    /// with the mod in it never reaches ranked, bare or not, and it holds next to nothing.
    /// </summary>
    private static void LoadBare()
    {
        Log.LogWarning("  BARE TEST COPY (LIARSBAR8P_BARE): only the window hold, the ranked block, the private test lobby, the test driver and the loopback harness are on");
        var harmony = new Harmony(Guid);

        Apply(harmony, typeof(RankedGuard.ButtonLine), "ranked: the button says ranked is off");
        Apply(harmony, typeof(RankedGuard.SearchLine), "ranked: no search from a modded copy");
        // The driver's 'hostprivate' lobby is forced private here, the one Steam lobby a bare copy
        // may open (MatchmakingSelfTest.BareRefuses says why it opens and joins no other).
        Apply(harmony, typeof(DiagAutoHost), "self test: auto host");

        Ultrawide.SavesGuarded =
            Apply(harmony, typeof(Ultrawide.Saved), "wide screens: a wide size is saved as one of the game's own");
        if (Ultrawide.Held)
        {
            Ultrawide.WindowGuarded =
                Apply(harmony, typeof(Ultrawide.HeldWindow), "test copies: a window, never full screen");
            try { Ultrawide.Frame(); }
            catch (System.Exception e) { Log.LogWarning($"[wide] test: the window is held from the first frame on: {e.Message}"); }
            try { HeldFocus.Start(); }
            catch (System.Exception e) { Log.LogWarning($"[wide] test: the foreground is not watched: {e.Message}"); }
        }

        if (Loopback.Requested) Apply(harmony, typeof(LoopbackSteamStub), "loopback steam stub");
        if (Loopback.Requested) Apply(harmony, typeof(LoopbackNoSteamLobbies), "loopback: a test copy never opens or joins a Steam lobby");

        SpawnHud();
        Log.LogInfo("=== Liar's Bar 8P loaded (bare) ===");
    }

    /// <summary>
    /// Ask Unity to say where an exception came from.
    ///
    /// This build logs exceptions as a single line - "IndexOutOfRangeException: Index was
    /// outside the bounds of the array" - and nothing else. Which array, in which method, in
    /// which of eight copies, is left to be guessed at, and guessing is how a day gets spent
    /// on the wrong candidate: the one that stopped Liar's Dice from resolving a liar call
    /// above four players was hunted through six collections before this was tried.
    ///
    /// Stack traces are a Unity setting, not a build-time one, so they can simply be asked
    /// for. Only in developer mode: they cost something to collect, and a player has no use
    /// for them.
    /// </summary>
    private static void AskForStackTraces()
    {
        try
        {
            UnityEngine.Application.SetStackTraceLogType(
                UnityEngine.LogType.Exception, UnityEngine.StackTraceLogType.ScriptOnly);
            UnityEngine.Application.SetStackTraceLogType(
                UnityEngine.LogType.Error, UnityEngine.StackTraceLogType.ScriptOnly);
            Log.LogInfo("  exceptions will be logged with a stack trace");
        }
        catch (System.Exception e)
        {
            Log.LogWarning($"  could not turn stack traces on: {e.Message}");
        }
    }

    /// <summary>
    /// Inject the HUD type into IL2CPP and attach it to a persistent object so the
    /// version is visible on every scene.
    /// </summary>
    private static void SpawnHud()
    {
        try
        {
            var go = new UnityEngine.GameObject("LiarsBar8P_VersionHud");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = UnityEngine.HideFlags.HideAndDontSave;

            // A bare test copy has the ticker alone, which then runs only the kit's parts.
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<ModTicker>();
            if (Bare || Off) { go.AddComponent<ModTicker>(); return; }

            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<VersionHud>();
            go.AddComponent<VersionHud>();
            go.AddComponent<ModTicker>();

            // Whose turn it is, for every player - not a developer tool.
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TurnHud>();
            go.AddComponent<TurnHud>();

            // Who you are pointing the revolver at. Three aiming poses cannot identify one of
            // seven people, so the choice is named as well as posed.
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<AimHud>();
            go.AddComponent<AimHud>();
            Log.LogInfo("  version HUD attached (top centre)");

            if (Dev.Enabled)
            {
                Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<DevHud>();
                go.AddComponent<DevHud>();
                Log.LogInfo("  DEVELOPER MODE on - press F8 in game for the debug panel");
            }
        }
        catch (System.Exception e)
        {
            Log.LogError($"  version HUD failed: {e.Message}");
        }
    }

    /// <summary>Patch one class. True if it went on, for a feature that must not claim what failed.</summary>
    private static bool Apply(Harmony harmony, System.Type patchClass, string label)
    {
        try
        {
            harmony.PatchAll(patchClass);
            Log.LogInfo($"  patched: {label}");
            return true;
        }
        catch (System.Exception e)
        {
            Log.LogError($"  FAILED to patch {label}: {e.Message}");
            return false;
        }
    }
}




















