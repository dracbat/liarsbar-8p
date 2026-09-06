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
    public const string Version = "2.0.1";

    public new static ManualLogSource Log;
    public static ConfigEntry<int> MaxPlayers;
    public static ConfigEntry<bool> Verbose;
    public static ConfigEntry<bool> DiagAutoHost;
    public static ConfigEntry<bool> DiagSoloStart;
    public static ConfigEntry<bool> DiagDeckPatchTest;
    public static ConfigEntry<bool> ScaleDeck;

    public override void Load()
    {
        Log = base.Log;

        MaxPlayers = Config.Bind("General", "MaxPlayers", 8,
            new ConfigDescription("Maximum players per lobby. Every player must run the same value.",
                new AcceptableValueRange<int>(2, 16)));

        Verbose = Config.Bind("Debug", "VerboseDiagnostics", true,
            "Dump runtime seat/prefab/slot counts to the log. Needed while the mod is still being built out.");

        ScaleDeck = Config.Bind("Gameplay", "ScaleDeck", true,
            "Scale the Liar's Deck proportionally with player count so everyone still gets five cards.");

        DiagAutoHost = Config.Bind("Debug", "SelfTestAutoHostLobby", false,
            "Development self test: auto-host a PRIVATE lobby on startup to capture lobby diagnostics.");

        DiagSoloStart = Config.Bind("Debug", "SelfTestForceSoloStart", false,
            "Development self test: force a solo match start to capture in-game seat diagnostics.");

        DiagDeckPatchTest = Config.Bind("Debug", "SelfTestDeckSizePatch", false,
            "Development self test: at startup, rewrite the deal's hardcoded deck size for a pretend five player table and report whether the write succeeded. Verifies the memory patch without needing other players.");

        Log.LogInfo($"=== Liar's Bar 8P loading (target={MaxPlayers.Value}) ===");

        var harmony = new Harmony(Guid);

        // Patch each area independently so one broken hook cannot disable the rest.
        Apply(harmony, typeof(CapPatches),     "player caps");
        Apply(harmony, typeof(JoinFix),        "join limits");
        Apply(harmony, typeof(CommandGuard),   "command guards");
        Apply(harmony, typeof(SeatAssign),     "seat assignment");
        Apply(harmony, typeof(LobbyExpansion), "lobby slot expansion");
        Apply(harmony, typeof(SeatExpansion),  "table seat expansion");
        Apply(harmony, typeof(SeatRing),       "seat spacing");
        Apply(harmony, typeof(DeckScaling),    "deck scaling");
        Apply(harmony, typeof(RosterFix),      "roster + seat indices");
        Apply(harmony, typeof(CardTypeFix),    "card type wrapping");
        Apply(harmony, typeof(DeckFix),        "deck top-up");
        Apply(harmony, typeof(JoinDiag),       "client join + roster");
        Apply(harmony, typeof(VersionCheck),   "version handshake");
        Apply(harmony, typeof(DeckDiag),       "deck diagnostics");
        Apply(harmony, typeof(DiagPatches),    "diagnostics");
        Apply(harmony, typeof(DiagAutoHost),   "self test: auto host");
        Apply(harmony, typeof(DiagSoloStart),  "self test: solo start");

        if (DiagDeckPatchTest.Value)
        {
            Log.LogInfo("  [selftest] exercising the deck size patch for 5 players...");
            DeckSizePatch.ApplyFor(5);
        }

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
            Log.LogInfo("  version HUD attached (bottom left)");
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














