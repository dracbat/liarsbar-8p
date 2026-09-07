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
    public const string Version = "0.26.0";

    public new static ManualLogSource Log;
    public static ConfigEntry<int> MaxPlayers;
    public static ConfigEntry<bool> Verbose;
    public static ConfigEntry<bool> DiagAutoHost;
    public static ConfigEntry<bool> DiagSoloStart;
    public static ConfigEntry<bool> DiagDeckPatchTest;
    public static ConfigEntry<bool> DeveloperMode;
    public static ConfigEntry<bool> DevAutoTest;

    public override void Load()
    {
        Log = base.Log;

        MaxPlayers = Config.Bind("General", "MaxPlayers", 8,
            new ConfigDescription("Maximum players per lobby. Every player must run the same value.",
                new AcceptableValueRange<int>(2, 16)));

        Verbose = Config.Bind("Debug", "VerboseDiagnostics", true,
            "Dump runtime seat/prefab/slot counts to the log. Needed while the mod is still being built out.");

        DiagAutoHost = Config.Bind("Debug", "SelfTestAutoHostLobby", false,
            "Development self test: auto-host a PRIVATE lobby on startup to capture lobby diagnostics.");

        DiagSoloStart = Config.Bind("Debug", "SelfTestForceSoloStart", false,
            "Development self test: force a solo match start to capture in-game seat diagnostics.");

        DiagDeckPatchTest = Config.Bind("Debug", "SelfTestDeckSizePatch", false,
            "Development self test: at startup, rewrite the deal's hardcoded deck size for a pretend five player table and report whether the write succeeded. Verifies the memory patch without needing other players.");

        DeveloperMode = Config.Bind("Developer", "DeveloperMode", false,
            "Developer testing tools: bots that fill empty seats, an on-screen panel (F8) and a " +
            "running account of registration, seating, dealing, turns and eliminations. An eight " +
            "player round cannot be tested with eight people to hand; this is how it gets tested. " +
            "Leave it off for normal play.");

        DevAutoTest = Config.Bind("Developer", "AutoTestFullTable", false,
            "With developer mode on: once a lobby is hosted, fill it with bots and start the " +
            "match automatically, printing the state at each step. One launch, one whole round, " +
            "no keyboard.");

        Log.LogInfo($"=== Liar's Bar 8P loading (target={MaxPlayers.Value}) ===");

        var harmony = new Harmony(Guid);

        // Patch each area independently so one broken hook cannot disable the rest.
        Apply(harmony, typeof(CapPatches),     "player caps");
        Apply(harmony, typeof(JoinFix),        "join limits");
        Apply(harmony, typeof(CommandGuard),   "command guards");
        Apply(harmony, typeof(SeatAssign),     "seat assignment");
        Apply(harmony, typeof(TransportCap),   "steam transport cap");
        Apply(harmony, typeof(LobbyPodiums),   "lobby podiums beyond four");
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

        if (DiagDeckPatchTest.Value)
        {
            Log.LogInfo("  [selftest] exercising the deck size patch for 5 players...");
            DeckSizePatch.ApplyFor(5);
        }

        // The turn-order constants live in compiled code, so they are rewritten rather than
        // patched. Done here so the log says plainly whether it worked, and retried at the
        // start of a round if the game was not ready yet.
        TurnOrderFix.Install();
        DealArrayPatch.Install();

        SpawnHud();

        Log.LogInfo("=== Liar's Bar 8P loaded ===");
    }

    /// <summary>
    /// Inject the HUD type into IL2CPP and attach it to a persistent object so the
    /// version is visible on every scene.
    /// </summary>
    private static void SpawnHud()
    {
        try
        {
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<VersionHud>();
            var go = new UnityEngine.GameObject("LiarsBar8P_VersionHud");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            go.AddComponent<VersionHud>();

            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<ModTicker>();
            go.AddComponent<ModTicker>();
            Log.LogInfo("  version HUD attached (top left)");

            if (DeveloperMode.Value)
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

    private static void Apply(Harmony harmony, System.Type patchClass, string label)
    {
        try
        {
            harmony.PatchAll(patchClass);
            Log.LogInfo($"  patched: {label}");
        }
        catch (System.Exception e)
        {
            Log.LogError($"  FAILED to patch {label}: {e.Message}");
        }
    }
}




















