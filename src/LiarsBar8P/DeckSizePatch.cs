using System;
using System.Collections.Generic;
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
    private const int ScanBytes = 4096;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    private sealed class Target
    {
        public string Method;
        public int VanillaDeck;      // the constant the game shipped with
        public int Current;          // what is written there now
        public IntPtr Site = IntPtr.Zero;   // address of the 32-bit operand

        /// <summary>The shipped card-face thresholds inside this deal, in order.</summary>
        public int T1, T2, T3;
        public IntPtr[] Mix;                // start of each threshold block
        public int MixFor;                  // deck size the thresholds are currently written for
    }

    private static readonly Target[] _targets =
    {
        new Target { Method = "DealBasicOrDevil", VanillaDeck = 20, T1 = 6, T2 = 12, T3 = 18 },
        new Target { Method = "DealDeck2",        VanillaDeck = 28, T1 = 8, T2 = 16, T3 = 24 },
    };

    /// <summary>What the basic deal will actually produce right now, for diagnostics.</summary>
    internal static int CurrentSize
    {
        get
        {
            foreach (var t in _targets)
                if (t.Method == "DealBasicOrDevil")
                    return t.Current > 0 ? t.Current : t.VanillaDeck;
            return -1;
        }
    }

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
            int perPlayer = t.VanillaDeck / Limits.VanillaPlayers;      // 5 for basic, 7 for deck2
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
                ScaleTheMix(t, wanted, players);
            }
            else
            {
                Plugin.Log.LogError($"[decksize] could not write the new deck size for {t.Method}");
                t.Current = -1;
            }
        }
    }

    // ------------------------------------------------------------- the card mix

    /// <summary>
    /// Keep the deck's *composition* in proportion when its size changes.
    ///
    /// Making the deck bigger is only half of it. Which face each card gets is decided by
    /// three thresholds on the card's index — under 6 an Ace, under 12 a King, under 18 a
    /// Queen, anything else a Joker — and those are the numbers for a twenty card deck.
    /// Grow the deck to forty and leave them alone and every card from nineteen upwards is a
    /// Joker: twenty-two of forty, more than half the deck, against two in twenty as shipped.
    /// It is not a crash, it is worse — the game still runs and the bluffing is ruined. It
    /// was visible in the hands and nobody had spotted why: <c>[J J J J A]</c>, <c>[K K J J J]</c>.
    ///
    /// The game has a method for this arithmetic, and this mod already patched it — but the
    /// compiler inlined that method into both deals, so the patch has no call site and has
    /// never once run. The thresholds have to be rewritten where they actually are: three
    /// <c>imm8</c> operands in the deal's own code.
    ///
    /// The block is matched by its shape rather than its numbers, so it is still found after
    /// it has been written once, with the three thresholds wildcarded and only sanity-checked
    /// (ascending, positive, inside a byte). Exactly one match is required.
    /// </summary>
    private static void ScaleTheMix(Target t, int deck, int players)
    {
        try
        {
            if (t.MixFor == deck) return;         // already written for this size
            if (t.MixFor == -1) return;           // gave up on this method

            int a = Scale(t.T1, t.VanillaDeck, deck);
            int b = Scale(t.T2, t.VanillaDeck, deck);
            int c = Scale(t.T3, t.VanillaDeck, deck);

            if (!(0 < a && a < b && b < c && c <= 127 && c < deck))
            {
                Plugin.Log.LogWarning(
                    $"[cardmix] {t.Method}: {a}/{b}/{c} for a deck of {deck} is not a sane split - " +
                    "the card faces are left as shipped");
                t.MixFor = -1;
                return;
            }

            if (t.Mix == null)
            {
                t.Mix = FindMix(CodePointer(t.Method));
                if (t.Mix == null || t.Mix.Length == 0)
                {
                    Plugin.Log.LogWarning(
                        $"[cardmix] could not find the card faces inside {t.Method} - every card past " +
                        $"{t.T3} will be a Joker at this deck size");
                    t.MixFor = -1;
                    return;
                }
            }

            // The basic deal carries the same arithmetic twice - once for an ordinary round
            // and once for a devil round - so every block found is written, not just the
            // first. Leaving one behind would deal a normal round properly and a devil round
            // as a fistful of Jokers.
            foreach (var site in t.Mix)
                if (!NativeCode.WriteByte(site + 2, (byte)a) ||
                    !NativeCode.WriteByte(site + 14, (byte)b) ||
                    !NativeCode.WriteByte(site + 28, (byte)c))
                {
                    Plugin.Log.LogError($"[cardmix] {t.Method}: could not write the card faces");
                    t.MixFor = -1;
                    return;
                }

            t.MixFor = deck;
            Plugin.Log.LogInfo(
                $"[cardmix] {t.Method}: card faces {t.T1}/{t.T2}/{t.T3} -> {a}/{b}/{c} in " +
                $"{t.Mix.Length} place(s) for a deck of {deck} - {a} Aces, {b - a} Kings, " +
                $"{c - b} Queens, {deck - c} Jokers at {players} players");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[cardmix] {t.Method}: {e.Message}");
            t.MixFor = -1;
        }
    }

    private static int Scale(int threshold, int vanillaDeck, int deck) =>
        (int)Math.Round(threshold * (double)deck / vanillaDeck, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The three thresholds, found by the shape of the compare chain rather than its values.
    ///
    ///     cmp reg, t1 / jg +7 / mov edx,1 / jmp +0x17
    ///     cmp reg, t2 / jg +7 / mov edx,2 / jmp +0x0B
    ///     xor edx,edx / cmp reg, t3 / setg dl / add edx,3
    ///
    /// Every byte but the three thresholds is fixed, and the same register must be compared
    /// all three times, which is what makes this unmistakable — a bare compare against six
    /// would not be.
    /// </summary>
    private static IntPtr[] FindMix(IntPtr code)
    {
        if (code == IntPtr.Zero) return null;

        var found = new List<IntPtr>();

        for (int i = 0; i < 4096; i++)
        {
            if (!NativeCode.TryReadByte(code, i, out byte b0) || b0 != 0x83) continue;
            if (!NativeCode.TryReadByte(code, i + 1, out byte reg) || reg < 0xF8) continue;  // cmp r32, imm8

            if (!Shape(code, i, reg)) continue;

            if (!NativeCode.TryReadByte(code, i + 2, out byte t1)) continue;
            if (!NativeCode.TryReadByte(code, i + 14, out byte t2)) continue;
            if (!NativeCode.TryReadByte(code, i + 28, out byte t3)) continue;
            if (!(0 < t1 && t1 < t2 && t2 < t3 && t3 <= 127)) continue;

            found.Add(code + i);
        }

        // Thirty-five bytes of fixed shape, the same register compared three times and three
        // ascending thresholds is not something a stray constant satisfies. More than a
        // handful would mean the shape is wrong, though, so that is still refused.
        if (found.Count == 0 || found.Count > 4)
        {
            Plugin.Log.LogWarning($"[cardmix] found {found.Count} card-face blocks - not writing on that");
            return null;
        }

        return found.ToArray();
    }

    private static bool Shape(IntPtr code, int i, byte reg)
    {
        // offset, expected byte. The thresholds at 2, 14 and 28 are deliberately absent.
        var want = new (int At, byte Byte)[]
        {
            (3, 0x7F), (4, 0x07), (5, 0xBA), (6, 0x01), (7, 0x00), (8, 0x00), (9, 0x00),
            (10, 0xEB), (11, 0x17), (12, 0x83), (13, reg), (15, 0x7F), (16, 0x07),
            (17, 0xBA), (18, 0x02), (19, 0x00), (20, 0x00), (21, 0x00), (22, 0xEB), (23, 0x0B),
            (25, 0xD2), (26, 0x83), (27, reg), (29, 0x0F), (30, 0x9F), (31, 0xC2),
            (32, 0x83), (33, 0xC2), (34, 0x03),
        };

        foreach (var w in want)
            if (!NativeCode.TryReadByte(code, i + w.At, out byte got) || got != w.Byte) return false;

        // xor edx,edx has two encodings; accept either.
        if (!NativeCode.TryReadByte(code, i + 24, out byte x) || (x != 0x33 && x != 0x31)) return false;
        return true;
    }
}
