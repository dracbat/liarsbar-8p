using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace LiarsBar8P;

[BepInPlugin(Guid, "Liar's Bar 8 Players", "0.1.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "josh.liarsbar.eightplayers";

    public new static ManualLogSource Log;
    public static ConfigEntry<int> MaxPlayers;
    public static ConfigEntry<bool> Verbose;
    public static ConfigEntry<bool> DiagAutoHost;
    public static ConfigEntry<bool> DiagSoloStart;
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

        Log.LogInfo($"=== Liar's Bar 8P loading (target={MaxPlayers.Value}) ===");

        var harmony = new Harmony(Guid);

        // Patch each area independently so one broken hook cannot disable the rest.
        Apply(harmony, typeof(CapPatches),     "player caps");
        Apply(harmony, typeof(LobbyExpansion), "lobby slot expansion");
        Apply(harmony, typeof(DeckScaling),    "deck scaling");
        Apply(harmony, typeof(DiagPatches),    "diagnostics");
        Apply(harmony, typeof(DiagAutoHost),   "self test: auto host");
        Apply(harmony, typeof(DiagSoloStart),  "self test: solo start");

        Log.LogInfo("=== Liar's Bar 8P loaded ===");
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
