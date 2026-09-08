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
/// handed out, and the first turn - given from further down the same routine - never
/// arrives either. One constant, both symptoms, and nothing in any log to say so.
///
/// The four is an immediate operand handed to the array allocator, so no prefix or postfix
/// can reach it; it is rewritten in memory, exactly like the deck size and the turn order
/// wrap. The routine must therefore never be patched with Harmony as well - a detour would
/// move the very bytes this reads.
///
/// <para>
/// Every mode has its own copy of this routine, and each one has its own four. That is not
/// a detail: for a long time only Liar's Deck was patched here, and the other modes were
/// described as covered by "shared fixes, not separately tested" on the strength of the
/// caps and the seat ring being mode-independent. The deal is not mode-independent. Liar's
/// Poker, Texas, Chaos, Chaos Deck and both Blorf tables each build their own
/// <c>PlayerStats[4]</c> and each walk their own four seats, so above four players every
/// one of them stopped in exactly the way Liar's Deck used to - silently, with the cards
/// recorded as dealt and no hand in anybody's hands.
/// </para>
///
/// <para>
/// The compiler did not emit the two sites identically in every copy, which is why matching
/// bytes literally found only one of the seven. The array length is followed by the call in
/// some copies and separated from it by the element type load in others; the seat cursor's
/// compare sits after its store in some and before it in others. Both matchers now describe
/// the shape rather than the byte string, and each site must still be the only one of its
/// shape inside the method or nothing is written.
/// </para>
/// </summary>
internal static class DealArrayPatch
{
    private const int ScanBytes = 8192;
    private static bool _installed;

    /// <summary>The deal routines that build a player array sized for four.</summary>
    private static readonly (Type Owner, string Routine)[] Targets =
    {
        (typeof(DeckGamePlayManager), "GiveCardsVisualRoutine"),
        (typeof(ChaosDeckGamePlayManager), "GiveCardsVisualRoutine"),
        (typeof(PokerGamePlayManager), "GiveCardPlayer"),
        (typeof(TexasGamePlayManager), "GiveCardPlayer"),
        (typeof(ChaosGamePlayManager), "GiveCardPlayer"),
        (typeof(BlorfGamePlayManager), "GiveCardPlayer"),
        (typeof(BlorfMatchMakingGamePlayManager), "GiveCardPlayer"),
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

            int length = MethodLength(code);

            // The cursor is only raised if the array actually grew. Raising it on its own is
            // worse than doing nothing at all: the deal would walk eight seats into an array
            // that still holds four and throw at seat four - which is the very failure this
            // class exists to remove, reintroduced at every table size including the four the
            // game shipped for. Either both, or neither.
            if (PatchArraySize(owner, routine, code, length, want))
                PatchSeatCursor(owner, routine, code, length, want);
            else
                Plugin.Log.LogWarning(
                    $"[dealarray] {owner.Name}.{routine}: the seat cursor is left at " +
                    $"{Limits.VanillaPlayers} too, because walking more seats than the array holds " +
                    "would break the deal rather than fix it");

            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[dealarray] {owner.Name}.{routine}: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// How far the method runs, so a scan cannot wander into the next one.
    ///
    /// Both matchers below insist on being the only match, and that is only a real guard if
    /// "only" means inside this method. The compiler pads methods apart with int3, so the
    /// first run of it is the end. Four in a row rather than one: a single 0xCC turns up
    /// inside ordinary immediates.
    /// </summary>
    private static int MethodLength(IntPtr code)
    {
        for (int i = 0; i < ScanBytes; i++)
        {
            bool pad = true;
            for (int k = 0; k < 4 && pad; k++)
                pad = NativeCode.TryReadByte(code, i + k, out byte b) && b == 0xCC;
            if (pad) return i;
        }
        return ScanBytes;
    }

    /// <summary>
    /// Grow the player array the deal indexes by seat.
    ///
    /// <c>mov edx, 4</c> handed to the array allocator. In four of the seven copies the call
    /// follows immediately; in the other three the element type is loaded into rcx in
    /// between (<c>mov rcx, [rip+disp32]</c>, seven bytes). Requiring the call either way is
    /// what rules out an unrelated constant four - matching only the adjacent form is what
    /// made this silently skip Chaos Deck and the rest.
    /// </summary>
    private static bool PatchArraySize(Type owner, string routine, IntPtr code, int length, int want)
    {
        IntPtr site = IntPtr.Zero;
        int hits = 0;

        for (int i = 0; i < length; i++)
        {
            if (!NativeCode.TryReadByte(code, i, out byte mov) || mov != 0xBA) continue;
            if (!NativeCode.TryReadInt32(code, i + 1, out int imm) || imm != Limits.VanillaPlayers) continue;

            int at = i + 5;

            // Optional: mov rcx, [rip+disp32] - the array's element type.
            if (NativeCode.TryReadByte(code, at, out byte rex) && rex == 0x48 &&
                NativeCode.TryReadByte(code, at + 1, out byte load) && load == 0x8B &&
                NativeCode.TryReadByte(code, at + 2, out byte modrm) && modrm == 0x0D)
                at += 7;

            if (!NativeCode.TryReadByte(code, at, out byte call) || call != 0xE8) continue;

            hits++;
            if (hits == 1) site = code + i + 1;
        }

        if (hits != 1)
        {
            Plugin.Log.LogWarning(
                $"[dealarray] {owner.Name}.{routine}: expected one player array size, found {hits} - " +
                "left as shipped rather than writing on a guess");
            return false;
        }

        if (!NativeCode.WriteInt32(site, want))
        {
            Plugin.Log.LogError($"[dealarray] {owner.Name}.{routine}: could not write the new array size");
            return false;
        }

        Plugin.Log.LogInfo(
            $"[dealarray] {owner.Name}.{routine}: the deal's player array grows " +
            $"{Limits.VanillaPlayers} -> {want}, so seats {Limits.VanillaPlayers}+ can be dealt to");
        return true;
    }

    /// <summary>
    /// Raise the number of seats the deal actually walks.
    ///
    /// This is the one that mattered most, and it hid behind the array. The routine does not
    /// deal everybody in one pass: it deals ONE seat, then increments a cursor on the manager
    /// and re-launches itself for the next. The cursor is compared against four:
    ///
    ///     mov eax,[rsi+258h]      ; the seat cursor, zeroed in ResetRound
    ///     inc eax
    ///     mov [rsi+258h],eax
    ///     cmp eax,4               ; the one that matters
    ///     jl  (deal the next seat)
    ///
    /// Below four it goes round again; at four it stops re-launching and moves on to give out
    /// the first turn. So with the array grown, all eight players are put into it correctly -
    /// and then only the first four are visited. Seats four and beyond are never told the game
    /// has started and never told they are holding cards, which is exactly what was reported:
    /// the corner seats dealt on paper and empty in the hand. The turn is then handed out
    /// regardless, which is why it could arrive before anybody was holding anything.
    ///
    /// The seven copies of this routine order those five instructions three different ways -
    /// the store lands after the compare in some and before it in others, and Texas puts two
    /// unrelated instructions in between - so what is matched is the relationship rather than
    /// the sequence: a <c>cmp eax,4</c> with an <c>inc eax</c> just behind it and a
    /// backward-branching <c>jl</c> just ahead. Insisting on one exact byte string is what
    /// left five of the seven modes dealing to four seats.
    ///
    /// Fewer players than the maximum is safe: an unvisited slot in the array is null, the
    /// routine's own null check sends it round to the next seat, and the cursor simply steps
    /// past at the cost of one short wait each.
    /// </summary>
    private static void PatchSeatCursor(Type owner, string routine, IntPtr code, int length, int want)
    {
        if (want > 127)
        {
            Plugin.Log.LogWarning("[dealarray] the seat cursor is a byte-sized compare - " +
                                  $"{want} will not fit, so the deal is left walking four seats");
            return;
        }

        IntPtr site = IntPtr.Zero;
        int hits = 0;

        for (int i = 0; i < length; i++)
        {
            // cmp eax, imm8
            if (!NativeCode.TryReadByte(code, i, out byte cmp) || cmp != 0x83) continue;
            if (!NativeCode.TryReadByte(code, i + 1, out byte reg) || reg != 0xF8) continue;
            if (!NativeCode.TryReadByte(code, i + 2, out byte imm) || imm != Limits.VanillaPlayers) continue;

            if (!HasIncEaxBehind(code, i)) continue;
            if (!HasJumpLessAhead(code, i + 3)) continue;

            hits++;
            if (hits == 1) site = code + i + 2;
        }

        if (hits != 1)
        {
            Plugin.Log.LogWarning(
                $"[dealarray] {owner.Name}.{routine}: expected one seat cursor limit, found {hits} - " +
                "left as shipped, so the deal will still stop after four seats");
            return;
        }

        if (!NativeCode.WriteByte(site, (byte)want))
        {
            Plugin.Log.LogError($"[dealarray] {owner.Name}.{routine}: could not raise the seat cursor limit");
            return;
        }

        Plugin.Log.LogInfo(
            $"[dealarray] {owner.Name}.{routine}: the deal now walks {want} seats rather than " +
            $"{Limits.VanillaPlayers}, so every seat is dealt and told the round has begun");
    }

    /// <summary>An <c>inc eax</c> within the handful of bytes before the compare.</summary>
    private static bool HasIncEaxBehind(IntPtr code, int cmpAt)
    {
        for (int back = 2; back <= 24; back++)
        {
            int at = cmpAt - back;
            if (at < 0) break;
            if (NativeCode.TryReadByte(code, at, out byte b0) && b0 == 0xFF &&
                NativeCode.TryReadByte(code, at + 1, out byte b1) && b1 == 0xC0) return true;
        }
        return false;
    }

    /// <summary>
    /// A <c>jl</c> shortly after the compare: go round for the next seat.
    ///
    /// The jump is forwards, not backwards, which is worth saying because assuming otherwise
    /// made this match nothing at all in any of the seven. A coroutine is a state machine, so
    /// "go round again" is not a loop back to the top - it is a jump on to the block that
    /// sets up the next handout, which the compiler lays out after the test.
    /// </summary>
    private static bool HasJumpLessAhead(IntPtr code, int after)
    {
        for (int ahead = 0; ahead <= 12; ahead++)
        {
            int at = after + ahead;

            if (!NativeCode.TryReadByte(code, at, out byte b0)) return false;
            if (b0 == 0x7C) return true;                                     // jl rel8
            if (b0 == 0x0F &&
                NativeCode.TryReadByte(code, at + 1, out byte b1) && b1 == 0x8C) return true;   // jl rel32
        }
        return false;
    }
}
