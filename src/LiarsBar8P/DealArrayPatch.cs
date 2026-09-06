using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Rewrites the four-element array the card deal builds and indexes by seat number.
///
/// This is what stopped a round of more than four ever being playable. The routine that
/// physically hands out the cards starts by building an array of players:
///
///     new PlayerStats[4]          // mov edx,4 ; call SzArrayNew
///     ...
///     array[player.Slot] = player // bounds-checked against a length of four
///
/// It is indexed by the player's seat, so the moment a player sits in seat four or beyond
/// it throws IndexOutOfRangeException. That happens on the routine's very first step, and
/// because the routine is a coroutine the exception is swallowed by Unity without a word:
/// the round simply stops. Every card is recorded as dealt while not one card object is
/// handed out, and the first turn — given from further down the same routine — never
/// arrives either. One constant, both symptoms, and nothing in any log to say so.
///
/// The four is an immediate operand handed to the array allocator, so no prefix or postfix
/// can reach it; it is rewritten in memory, exactly like the deck size and the turn order
/// wrap. The routine must therefore never be patched with Harmony as well - a detour would
/// move the very bytes this reads.
/// </summary>
internal static class DealArrayPatch
{
    private const int ScanBytes = 4096;
    private static bool _installed;

    /// <summary>The deal routines that build a player array sized for four.</summary>
    private static readonly (Type Owner, string Routine)[] Targets =
    {
        (typeof(DeckGamePlayManager), "GiveCardsVisualRoutine"),
        (typeof(ChaosDeckGamePlayManager), "GiveCardsVisualRoutine"),
    };

    internal static void Install()
    {
        if (_installed) return;

        int want = Limits.Max;
        if (want < Limits.VanillaPlayers || want > 127)
        {
            Plugin.Log.LogWarning($"[dealarray] player maximum {want} out of range - the deal is left as shipped");
            return;
        }

        int done = 0;
        foreach (var t in Targets) done += Patch(t.Owner, t.Routine, want);

        // Nothing resolved usually means the types are not loaded yet rather than that the
        // pattern is wrong, so allow a later attempt.
        _installed = done > 0;
    }

    private static int Patch(Type owner, string routine, int want)
    {
        try
        {
            // The routine's code lives on the compiler-generated state machine, not on the
            // method that returns it. Its name is mangled, so it is found by search.
            Type state = null;
            foreach (var nested in owner.GetNestedTypes(AccessTools.all))
                if (nested.Name.Contains(routine)) { state = nested; break; }

            if (state == null)
            {
                Plugin.Log.LogInfo($"[dealarray] {owner.Name}.{routine} not found - skipped");
                return 0;
            }

            var code = NativeCode.CodePointer(state, "MoveNext");
            if (code == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"[dealarray] could not read {state.Name} - the deal is left as shipped");
                return 0;
            }

            // mov edx, 4 immediately followed by a call: the length handed to the array
            // allocator. Requiring the call rules out an unrelated constant four.
            IntPtr site = IntPtr.Zero;
            int hits = 0;
            for (int i = 0; i < ScanBytes; i++)
            {
                if (!NativeCode.TryReadByte(code, i, out byte mov) || mov != 0xBA) continue;
                if (!NativeCode.TryReadInt32(code, i + 1, out int imm) || imm != Limits.VanillaPlayers) continue;
                if (!NativeCode.TryReadByte(code, i + 5, out byte call) || call != 0xE8) continue;
                hits++;
                if (hits == 1) site = code + i + 1;
            }

            if (hits != 1)
            {
                Plugin.Log.LogWarning(
                    $"[dealarray] {state.Name}: expected one player array size, found {hits} - " +
                    "left as shipped rather than writing on a guess");
                return 1;      // resolved; retrying will not help
            }

            if (!NativeCode.WriteInt32(site, want))
            {
                Plugin.Log.LogError($"[dealarray] {state.Name}: could not write the new array size");
                return 1;
            }

            Plugin.Log.LogInfo(
                $"[dealarray] {owner.Name}.{routine}: the deal's player array grows " +
                $"{Limits.VanillaPlayers} -> {want}, so seats {Limits.VanillaPlayers}+ can be dealt to");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[dealarray] {owner.Name}.{routine}: {e.Message}");
            return 0;
        }
    }
}
