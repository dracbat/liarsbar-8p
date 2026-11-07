using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Keeps dice values out of every log, at every table, so a log stays safe to paste into a bug
/// report or a public issue. Nothing else is touched, and the roll itself is untouched.
/// </summary>
[HarmonyPatch]
internal static class HiddenDiceLog
{
    /// <summary>What this looks for.</summary>
    private const string RollLine = "] Zarlar: ";

    private static bool _said, _seen;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UnityEngine.Debug), nameof(UnityEngine.Debug.Log), new[] { typeof(Il2CppSystem.Object) })]
    private static bool Log(Il2CppSystem.Object message)
    {
        try
        {
            if (!_seen)
            {
                _seen = true;
                Dev.Log("dicelog", "the game's log lines pass through here");
            }

            // The game logs something most frames, so a line that is not the roll goes on its way
            // with no more than this look. The text is what Unity itself would make of it.
            string line = message != null ? message.ToString() : null;
            if (line == null || line.IndexOf(RollLine, StringComparison.Ordinal) < 0) return true;

            if (!_said)
            {
                _said = true;
                Plugin.Log.LogInfo("[dicelog] every seat's dice are kept out of the log");
            }
            return false;
        }
        catch { return true; }
    }
}
