using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Makes turn order visit every player instead of only the first four.
///
/// Two separate four-player assumptions, both in <c>Manager</c>.
///
/// <b>The wrap-around seat number.</b> <c>GiveTurn</c> advances like this:
///
///     slot = ActivePlayerSlot + 1
///     while (GetTargetPlayer(slot, checkdead: true) == null)
///         { slot++; if (slot &gt; 3) slot = 0; }
///     winner.HaveTurn = true; ActivePlayerSlot = slot
///
/// A full table happens to work, because the search only wraps when it finds an empty
/// seat. But the moment anyone in seats 0-3 is dead or gone - which in this game is most
/// of the time - the search steps past seat 3, fails <c>slot &gt; 3</c>, and jumps
/// straight back to seat 0. Seats 4 and up are never visited. That is the reported
/// "players play when the arrow isn't pointing at them": the turn skipped them.
/// <c>BackGiveTurn</c> and <c>findbackplayer</c> do the same in reverse, wrapping seat 0
/// back to a hardcoded seat 3.
///
/// The seat number is an immediate operand in compiled code - <c>cmp ebx,3</c> and
/// <c>mov ebp,3</c> - so no prefix or postfix can reach it. It is rewritten in memory,
/// exactly like the deck size. The value written is the highest configured seat, which is
/// safe at any player count: with fewer players the extra seats simply return nobody and
/// the search keeps going, ending at the same place it would have before.
///
/// These methods are deliberately never patched with Harmony. Harmony redirects a method
/// by writing a jump over its entry, which would move what this scan is reading.
///
/// <b>The neighbour table.</b> <c>GetTargetSlot(mySlot, direction)</c> is a hardcoded
/// four-by-four table of who is across from you, to your left and to your right; anything
/// outside seats 0-3 falls through to "seat 0". That drives aiming and
/// <c>CheckSlotFull</c>, so every extra player believed seat 0 lay in all three
/// directions. It is a pure function, so it is replaced outright with the arithmetic the
/// table encodes - and at four players it produces exactly the original table.
/// </summary>
internal static class TurnOrderFix
{
    private const int ScanBytes = 4096;
    private static bool _installed;

    // ---------------------------------------------------------------- neighbours

    // Direction values, read off the shipped table:
    //   1 -> across, 2 -> the previous seat, 3 -> the next seat.
    private const int Across = 1;
    private const int Previous = 2;
    private const int Next = 3;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Manager), nameof(Manager.GetTargetSlot))]
    private static bool Neighbour(Manager __instance, int myslot, CharController.Targets targetdirection, ref int __result)
    {
        int n;
        try { n = __instance?.Players?.Count ?? 0; }
        catch { return true; }

        // Below five players the shipped table is already correct; leave it alone.
        if (n <= Limits.VanillaPlayers) return true;
        if (myslot < 0 || myslot >= n) { __result = 0; return false; }

        __result = (int)targetdirection switch
        {
            Across   => (myslot + n / 2) % n,
            Previous => (myslot + n - 1) % n,
            Next     => (myslot + 1) % n,
            _        => 0,          // the original returns 0 for anything else
        };
        return false;
    }

    /// <summary>
    /// The same neighbour table again, in the overload that returns the player.
    ///
    /// <c>GetTargetPlayer(mySlot, direction)</c> does not call <c>GetTargetSlot</c>: the
    /// compiler inlined a second, independent copy of the four-by-four table into it, so
    /// fixing the first one left this one still answering for a four seat table. Everything
    /// that asks "who is across from me" or "who is to my left" through this overload gets
    /// the player in seat zero for any seat above the third.
    ///
    /// It is a nest of compares rather than a single constant, so there is nothing to
    /// rewrite; it is answered here instead, from the same arithmetic as the other table.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Manager), nameof(Manager.GetTargetPlayer),
                  new Type[] { typeof(int), typeof(CharController.Targets) })]
    private static bool NeighbourPlayer(Manager __instance, int myslot,
                                        CharController.Targets targetdirection,
                                        ref PlayerStats __result)
    {
        int n;
        try { n = __instance?.Players?.Count ?? 0; }
        catch { return true; }

        if (n <= Limits.VanillaPlayers) return true;      // the shipped table is right
        if (myslot < 0 || myslot >= n) { __result = null; return false; }

        int want = (int)targetdirection switch
        {
            Across   => (myslot + n / 2) % n,
            Previous => (myslot + n - 1) % n,
            Next     => (myslot + 1) % n,
            _        => -1,
        };

        __result = null;
        if (want < 0) return false;

        try
        {
            foreach (var p in __instance.Players)
                if (p != null && p.Slot == want) { __result = p; break; }
        }
        catch { }

        return false;
    }

    // ------------------------------------------------------------------- the wrap

    /// <summary>
    /// Installed from the start of a match rather than from the turn methods themselves,
    /// which must stay unpatched for the scan to read the real code.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Manager), nameof(Manager.StartGame))]
    private static void OnStartGame() => Install();

    internal static void Install()
    {
        if (_installed) return;

        int maxSlot = Limits.MaxSlot;
        if (maxSlot < Limits.VanillaPlayers - 1 || maxSlot > 127)
        {
            Plugin.Log.LogWarning($"[turn] seat ceiling {maxSlot} out of range - turn order left as shipped");
            return;
        }

        // Forwards: cmp r32,3 followed by jle. Backwards: mov r32,3 followed by jns.
        int done = 0;
        done += PatchCompare("GiveTurn", maxSlot);
        done += PatchCompare("GiveTurnSpin", maxSlot);
        done += PatchCompare("GiveTurnTexas", maxSlot);
        done += PatchMoveImm("BackGiveTurn", maxSlot);
        done += PatchMoveImm("findbackplayer", maxSlot);
        done += PatchLeaverRing();

        // Nothing resolved means the game was not ready yet rather than that the patterns
        // are wrong, so leave the door open for the next round to try again.
        _installed = done > 0;
    }

    /// <summary>
    /// Hand the turn on correctly when the player whose turn it was disconnects.
    ///
    /// <c>DeckGamePlayManager.GiveTurnSkippingLeaver</c> walks the ring looking for the next
    /// player who can act, and it walks exactly four seats. A leaver in seat five hands the
    /// turn to seat two — a chair with nothing to do with them — and seats four upwards can
    /// never be reached at all. Once four of eight are out, every seat it can reach is empty
    /// and the round stops dead. With people playing over the internet somebody dropping
    /// mid-round is ordinary, so this is not a corner case.
    ///
    /// The wrap is a power-of-two trick rather than a division:
    ///
    ///     and ecx, 0x80000003    ; keep the low bits (and the sign)
    ///     jge +7 / dec ecx / or ecx, 0xFFFFFFFC / inc ecx    ; fix up a negative
    ///     ...
    ///     cmp r14d, 4            ; how many seats to try
    ///
    /// which means the modulus has to *stay* a power of two: writing five here would compute
    /// "and 5", which is not a modulo at all and would give nonsense seats. So the next power
    /// of two at or above the maximum is used — eight for any table of five to eight. Probing
    /// eight residues at a smaller table is harmless: the empty ones find nobody and it keeps
    /// looking.
    ///
    /// The three immediates are two halves of one modulus and its probe count. They are
    /// written together or not at all.
    /// </summary>
    private static int PatchLeaverRing()
    {
        try
        {
            int p = 1;
            while (p < Limits.Max) p <<= 1;              // next power of two at or above the max
            if (p < 4 || p > 128)
            {
                Plugin.Log.LogWarning($"[turn] a ring of {p} seats is out of range - the leaver handover is left as shipped");
                return 0;
            }
            if (p == Limits.VanillaPlayers) return 1;    // nothing to change

            var code = NativeCode.CodePointer(typeof(DeckGamePlayManager), "GiveTurnSkippingLeaver");
            if (code == IntPtr.Zero) return 0;

            // The wrap: add / and imm32 / jge / dec / or imm8 / inc / store.
            IntPtr ring = IntPtr.Zero;
            int ringHits = 0;
            IntPtr bound = IntPtr.Zero;
            int boundHits = 0;

            for (int i = 0; i < 4096; i++)
            {
                if (Match(code, i, 0x41, 0x03, 0xCE, 0x81, 0xE1) &&
                    NativeCode.TryReadInt32(code, i + 5, out int mask) && mask == unchecked((int)0x80000003) &&
                    Match(code, i + 9, 0x7D, 0x07, 0xFF, 0xC9, 0x83, 0xC9, 0xFC, 0xFF, 0xC1, 0x89, 0x4E, 0x10))
                {
                    ringHits++;
                    if (ringHits == 1) ring = code + i;
                }

                // The probe count: inc r14d / cmp r14d, 4 / jle back.
                if (Match(code, i, 0x41, 0xFF, 0xC6, 0x41, 0x83, 0xFE) &&
                    NativeCode.TryReadByte(code, i + 6, out byte n) && n == Limits.VanillaPlayers &&
                    NativeCode.TryReadByte(code, i + 7, out byte jle) && jle == 0x0F)
                {
                    boundHits++;
                    if (boundHits == 1) bound = code + i + 6;
                }
            }

            if (ringHits != 1 || boundHits != 1)
            {
                Plugin.Log.LogWarning(
                    $"[turn] GiveTurnSkippingLeaver: expected one ring and one probe count, found " +
                    $"{ringHits} and {boundHits} - left as shipped, so a player dropping mid-round " +
                    "may still strand the turn");
                return 1;      // resolved; retrying will not help
            }

            // All three, or none: a mask that does not match its fix-up gives wrong seats.
            if (!NativeCode.WriteInt32(ring + 5, unchecked((int)(0x80000000u | (uint)(p - 1)))))
            {
                Plugin.Log.LogError("[turn] GiveTurnSkippingLeaver: could not widen the ring");
                return 1;
            }
            if (!NativeCode.WriteByte(ring + 15, (byte)(0x100 - p)) ||
                !NativeCode.WriteByte(bound, (byte)p))
            {
                // Put the mask back rather than leave a half-applied modulus.
                NativeCode.WriteInt32(ring + 5, unchecked((int)0x80000003));
                Plugin.Log.LogError("[turn] GiveTurnSkippingLeaver: could not finish widening the ring - put back as shipped");
                return 1;
            }

            Plugin.Log.LogInfo(
                $"[turn] GiveTurnSkippingLeaver: the ring walked when somebody drops grows " +
                $"{Limits.VanillaPlayers} -> {p} seats");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[turn] GiveTurnSkippingLeaver: {e.Message}");
            return 0;
        }
    }

    /// <summary>Do these exact bytes sit at this offset?</summary>
    private static bool Match(IntPtr code, int offset, params byte[] want)
    {
        for (int k = 0; k < want.Length; k++)
            if (!NativeCode.TryReadByte(code, offset + k, out byte got) || got != want[k]) return false;
        return true;
    }

    /// <summary>
    /// Rewrite "cmp r32, 3" where the next instruction is a short jump-if-less-or-equal.
    /// The register form is required: the same constant also appears compared against a
    /// field, and that encoding is a different one, so it cannot be hit by accident.
    /// </summary>
    private static int PatchCompare(string method, int maxSlot)
    {
        return Scan(method, (code, i) =>
        {
            // inc r32 ; cmp r32,3 ; jle short ; xor r32,r32 - the whole loop tail, on one
            // register throughout. Matching the shape rather than the constant alone means
            // an unrelated comparison against three cannot be mistaken for it.
            if (!NativeCode.TryReadByte(code, i, out byte inc) || inc != 0xFF) return -1;
            if (!NativeCode.TryReadByte(code, i + 1, out byte incReg) || incReg < 0xC0 || incReg > 0xC7) return -1;
            if (!NativeCode.TryReadByte(code, i + 2, out byte cmp) || cmp != 0x83) return -1;
            if (!NativeCode.TryReadByte(code, i + 3, out byte cmpReg) || cmpReg < 0xF8) return -1;
            if ((cmpReg - 0xF8) != (incReg - 0xC0)) return -1;
            if (!NativeCode.TryReadByte(code, i + 4, out byte imm) || imm != Limits.VanillaPlayers - 1) return -1;
            if (!NativeCode.TryReadByte(code, i + 5, out byte jle) || jle != 0x7E) return -1;
            if (!NativeCode.TryReadByte(code, i + 7, out byte xor) || (xor != 0x31 && xor != 0x33)) return -1;
            return i + 4;
        }, (site) => NativeCode.WriteByte(site, (byte)maxSlot), maxSlot);
    }

    /// <summary>Rewrite "mov r32, 3" where a jump-if-not-negative follows shortly after.</summary>
    private static int PatchMoveImm(string method, int maxSlot)
    {
        return Scan(method, (code, i) =>
        {
            // mov r32,3 ; test r32,r32 ; cmovs r32,r32 - "step back, and if that went below
            // zero take the last seat instead". The conditional move is what makes this
            // specific; there is no branch to look for.
            if (!NativeCode.TryReadByte(code, i, out byte mov) || mov < 0xB8 || mov > 0xBF) return -1;
            if (!NativeCode.TryReadInt32(code, i + 1, out int imm) || imm != Limits.VanillaPlayers - 1) return -1;
            if (!NativeCode.TryReadByte(code, i + 5, out byte test) || test != 0x85) return -1;
            if (!NativeCode.TryReadByte(code, i + 7, out byte esc) || esc != 0x0F) return -1;
            if (!NativeCode.TryReadByte(code, i + 8, out byte cmovs) || cmovs != 0x48) return -1;
            return i + 1;
        }, (site) => NativeCode.WriteInt32(site, maxSlot), maxSlot);
    }

    /// <summary>
    /// Find the one place in a method matching the pattern and rewrite it.
    ///
    /// Exactly one match is required. If a game update ever produces two, the right one
    /// cannot be told from the wrong one, and writing to executable code on a guess is
    /// not a risk worth taking - the mod reports it and leaves turn order as shipped.
    /// </summary>
    /// <summary>Four int3 bytes in a row: the gap the compiler leaves between methods.</summary>
    private static bool IsPadding(IntPtr code, int offset)
    {
        for (int k = 0; k < 4; k++)
            if (!NativeCode.TryReadByte(code, offset + k, out byte b) || b != 0xCC) return false;
        return true;
    }

    private static int Scan(string method, Func<IntPtr, int, int> match, Func<IntPtr, bool> write, int maxSlot)
    {
        try
        {
            var code = NativeCode.CodePointer(typeof(Manager), method);
            if (code == IntPtr.Zero)
            {
                Plugin.Log.LogWarning($"[turn] {method} not found - left as shipped");
                return 0;
            }

            IntPtr site = IntPtr.Zero;
            int hits = 0;
            for (int i = 0; i < ScanBytes; i++)
            {
                // Methods are padded apart with int3. Stopping at the first run of it keeps
                // the scan inside this method instead of finding the next one's loop.
                if (IsPadding(code, i)) break;

                int at = match(code, i);
                if (at < 0) continue;
                hits++;
                if (hits == 1) site = code + at;
            }

            if (hits != 1)
            {
                Plugin.Log.LogWarning(
                    $"[turn] {method}: expected one wrap-around seat number, found {hits} - " +
                    "left as shipped so nothing is written on a guess");
                return 1;   // resolved, just not patched - no point retrying
            }

            if (write(site))
                Plugin.Log.LogInfo($"[turn] {method}: wraps at seat {Limits.VanillaPlayers - 1} -> {maxSlot}");
            else
                Plugin.Log.LogError($"[turn] {method}: could not write the new wrap-around seat");
            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[turn] {method}: {e.Message}");
            return 0;
        }
    }
}
