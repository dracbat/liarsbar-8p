using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;

namespace LiarsBar8P;

/// <summary>
/// Rewrites the hardcoded deck size inside the compiled deal methods.
///
/// This is the root cause of dealing failing above four players. Decompiling the game
/// shows the deck is built from a constant, not from anything reachable by a normal
/// patch:
///
///     DealBasicOrDevil : Enumerable.Range(1, 20).ToList()     20 cards, 5 each
///     DealDeck2        : Enumerable.Range(1, 28).ToList()     28 cards, 7 each
///
/// Adding card objects never changed that list, so the deal always produced twenty
/// cards: four full hands, nothing for a fifth player, and an index past the end -
/// the ArgumentOutOfRangeException seen every round.
///
/// The constant is an immediate operand in native code, so it cannot be reached with a
/// prefix or postfix. It is patched directly in memory instead, scaled by the same
/// cards-per-player the game itself uses (deck / 4), so five players get 25 and 35, and
/// eight get 40 and 56. The instruction sequence is verified before anything is written
/// and the original protection is restored afterwards.
/// </summary>
internal static class DeckSizePatch
{
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const int VanillaPlayers = 4;
    private const int ScanBytes = 4096;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    private sealed class Target
    {
        public string Method;
        public int VanillaDeck;      // the constant the game shipped with
        public int Current;          // what is written there now
        public IntPtr Site = IntPtr.Zero;   // address of the 32-bit operand
    }

    private static readonly Target[] _targets =
    {
        new Target { Method = "DealBasicOrDevil", VanillaDeck = 20 },
        new Target { Method = "DealDeck2",        VanillaDeck = 28 },
    };

    /// <summary>Native code address of a game method, via its IL2CPP MethodInfo.</summary>
    private static IntPtr CodePointer(string method)
    {
        try
        {
            var mi = AccessTools.Method(typeof(DeckGamePlayManager), method);
            if (mi == null) return IntPtr.Zero;

            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(mi);
            if (field == null) return IntPtr.Zero;

            var methodInfo = (IntPtr)field.GetValue(null);
            if (methodInfo == IntPtr.Zero) return IntPtr.Zero;

            // Il2CppMethodInfo begins with the pointer to the compiled code
            return Marshal.ReadIntPtr(methodInfo);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[decksize] could not resolve {method}: {e.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Find "mov edx, imm32" followed by "mov ecx, 1" - the two arguments to
    /// Enumerable.Range(1, deckSize). Requiring both makes the match specific enough that
    /// it cannot land on an unrelated constant.
    /// </summary>
    private static IntPtr FindOperand(IntPtr code, int expected)
    {
        if (code == IntPtr.Zero) return IntPtr.Zero;

        for (int i = 0; i < ScanBytes; i++)
        {
            try
            {
                if (Marshal.ReadByte(code, i) != 0xBA) continue;              // mov edx, imm32
                if (Marshal.ReadInt32(code, i + 1) != expected) continue;
                if (Marshal.ReadByte(code, i + 5) != 0xB9) continue;          // mov ecx, imm32
                if (Marshal.ReadInt32(code, i + 6) != 1) continue;            // ...specifically 1
                return code + i + 1;
            }
            catch { return IntPtr.Zero; }
        }
        return IntPtr.Zero;
    }

    private static bool Write(IntPtr site, int value)
    {
        try
        {
            if (!VirtualProtect(site, (UIntPtr)4, PAGE_EXECUTE_READWRITE, out uint old)) return false;
            Marshal.WriteInt32(site, value);
            VirtualProtect(site, (UIntPtr)4, old, out _);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[decksize] write failed: {e.Message}");
            return false;
        }
    }

    /// <summary>Size both decks for the given player count. Safe to call every round.</summary>
    internal static void ApplyFor(int players)
    {
        if (players < 2) return;

        foreach (var t in _targets)
        {
            int perPlayer = t.VanillaDeck / VanillaPlayers;      // 5 for basic, 7 for deck2
            int wanted = perPlayer * players;
            if (t.Current == wanted) continue;

            if (t.Site == IntPtr.Zero)
            {
                var code = CodePointer(t.Method);
                t.Site = FindOperand(code, t.Current == 0 ? t.VanillaDeck : t.Current);
                if (t.Site == IntPtr.Zero)
                {
                    Plugin.Log.LogWarning(
                        $"[decksize] could not locate the deck size inside {t.Method} - " +
                        "the game may have updated; dealing is unchanged");
                    t.Current = -1;   // stop retrying every round
                    continue;
                }
            }

            if (t.Current == -1) continue;

            if (Write(t.Site, wanted))
            {
                Plugin.Log.LogInfo(
                    $"[decksize] {t.Method}: deck {(t.Current == 0 ? t.VanillaDeck : t.Current)} -> {wanted} " +
                    $"({perPlayer} cards each for {players} players)");
                t.Current = wanted;
                if (t.Method == "DealBasicOrDevil") CardTypeFix.SetDeckSize(wanted);
            }
            else
            {
                Plugin.Log.LogError($"[decksize] could not write the new deck size for {t.Method}");
                t.Current = -1;
            }
        }
    }
}
