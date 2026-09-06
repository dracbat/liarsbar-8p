using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Watches the coroutine that physically hands the cards out.
///
/// A round can reach a state where every player has five cards recorded against them and
/// not one card object on the table: the numbers say dealt, the game shows an untouched
/// deck and nobody able to play. The same routine also gives out the first turn, and that
/// never arrives either — one cause, two symptoms.
///
/// That routine is a coroutine, and a coroutine that throws part way through is silent.
/// Unity stops it and reports nothing, so the round simply stops progressing with no error
/// anywhere to explain it. This wraps its step function to say when it starts, when it
/// finishes, and above all what it threw if it stopped early.
/// </summary>
internal static class DealDiag
{
    /// <summary>
    /// The routines to watch, matched on the readable part of their name.
    ///
    /// A coroutine's real type is a compiler-generated nested one, and the interop layer
    /// rewrites the angle brackets, so the name cannot reliably be written out here.
    /// Searching the nested types for the method name inside it is what works.
    /// </summary>
    private static readonly string[] Routines =
    {
        // GiveCardsVisualRoutine is deliberately absent: its compiled code is rewritten by
        // DealArrayPatch, and a Harmony detour would move the bytes that scan reads.
        "ShowCardRound",
        "WaitforResetRound",
    };

    private static readonly Dictionary<string, int> _steps = new();

    // ------------------------------------------------------- how far the deal gets
    //
    // The routine that hands the cards out cannot be patched - its compiled code is
    // rewritten by DealArrayPatch and a detour would move those bytes. So progress is
    // observed from the outside, through two things it calls in order: first it tells each
    // player they are holding cards, then it gives out the first turn. Which of those
    // appear says exactly where it stopped.

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.SetHaveCards))]
    private static void ReachedHandOut(DeckGameplay __instance, bool value)
    {
        try
        {
            int objects = __instance != null && __instance.Cards != null ? __instance.Cards.Count : -1;
            int types = __instance != null && __instance.cardTypes != null ? __instance.cardTypes.Count : -1;
            Plugin.Log.LogInfo($"[dealdiag] the deal reached hand-out: haveCards={value}, " +
                               $"{objects} card objects, {types} card values");
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.GiveTurnAfterAllCardsDealt))]
    private static void ReachedFirstTurn()
    {
        Plugin.Log.LogInfo("[dealdiag] the deal reached the first turn - it ran to the end");
    }

    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> Targets()
    {
        var found = new List<MethodBase>();

        Type[] nested;
        try { nested = typeof(DeckGamePlayManager).GetNestedTypes(AccessTools.all); }
        catch (Exception e) { Plugin.Log.LogWarning($"[dealdiag] could not list the deal routines: {e.Message}"); return found; }

        // Every match, not the first: one routine's name is a prefix of another's, and
        // taking only the first left the one that actually starts the deal unwatched.
        foreach (var t in nested)
        {
            bool wanted = false;
            foreach (var want in Routines) if (t.Name.Contains(want)) { wanted = true; break; }
            if (!wanted) continue;

            var move = AccessTools.Method(t, "MoveNext");
            if (move == null) { Plugin.Log.LogInfo($"[dealdiag] {t.Name} has no MoveNext"); continue; }
            if (found.Contains(move)) continue;

            found.Add(move);
            Plugin.Log.LogInfo($"[dealdiag] watching {t.Name}.MoveNext");
        }

        if (found.Count == 0) Plugin.Log.LogWarning("[dealdiag] found no deal routines to watch");

        return found;
    }

    /// <summary>
    /// A finalizer, because the whole point is to see the exception. It passes it on
    /// rather than swallowing it: silencing it here would change how the game behaves,
    /// and the aim is to observe, not to alter.
    /// </summary>
    [HarmonyFinalizer]
    private static Exception Report(Exception __exception, object __instance, bool __result)
    {
        try
        {
            string name = __instance != null ? __instance.GetType().Name : "?";

            if (__exception != null)
            {
                _steps.TryGetValue(name, out int step);
                Plugin.Log.LogError(
                    $"[dealdiag] {name} THREW on step {step} - this is why no cards were handed " +
                    $"out and no first turn arrived: {__exception}");
                _steps[name] = 0;
                return __exception;
            }

            _steps.TryGetValue(name, out int n);
            _steps[name] = n + 1;

            // Only the boundaries: this runs once per frame while the deal animates.
            if (n == 0) Plugin.Log.LogInfo($"[dealdiag] {name} started");
            if (!__result) { Plugin.Log.LogInfo($"[dealdiag] {name} finished after {n + 1} steps"); _steps[name] = 0; }
        }
        catch { }

        return __exception;
    }
}
