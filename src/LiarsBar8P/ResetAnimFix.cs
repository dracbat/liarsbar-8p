using System;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Stops the round-reset animation throwing at seat four and ending the session.
///
/// Five of the seven modes play a per-seat reset animation as part of dealing, and the
/// manager carries one clip per seat the game shipped with:
///
///     PlayResetAnim(player.Slot)              // a ClientRpc
///     ...
///     if (slot &gt;= clips.Length) throw        // four clips
///     ResetAnim.clip = clips[slot];
///
/// Above four players that throws, and it throws inside a remote call - which is much worse
/// than it sounds. Mirror's response to an exception while handling an Rpc is to drop the
/// connection, so one seat with no clip does not produce a missing animation: it produces
/// "Disconnecting connection ... caused an Exception" on every peer in the same tenth of a
/// second and the game is over. A five player table of Liar's Texas got as far as dealing
/// everybody in and then collapsed, all four clients gone at once. Liar's Poker did the same.
///
/// Liar's Deck and the Chaos deck variant never call it, which is exactly why this went
/// unnoticed for as long as eight player testing has existed: the one mode being tested is
/// one of the two that cannot hit it.
///
/// <para>
/// The fix goes on the <em>receiving</em> end, and that is not a preference. Liar's Texas and
/// Liar's Chaos call the sending method for real, but Poker and both Blorf tables have it
/// inlined into their deal - the call site is gone, so a patch on it has nothing to attach to
/// and would silently do nothing, the same trap <c>ToCardTypeBasic</c> fell into. Every peer
/// runs the receiving end, the host included, so one patch there covers all five modes and
/// every machine.
/// </para>
///
/// <para>
/// The seat is mapped onto the clips that exist: the ring is divided evenly, so at eight
/// players each of the four clips serves two neighbouring seats and the order round the table
/// is kept. At four players or fewer nothing is translated and the game behaves exactly as it
/// shipped. What the animation depicts has not been established, so it is not claimed that
/// this looks right - only that it is consistent, and that a seat without a clip of its own
/// must not take the table down.
/// </para>
/// </summary>
internal static class ResetAnimFix
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TexasGamePlayManager), nameof(TexasGamePlayManager.InvokeUserCode_PlayResetAnim__Int32))]
    private static bool Texas(NetworkBehaviour obj, NetworkReader reader) =>
        Deliver(obj, reader, "Texas");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosGamePlayManager), nameof(ChaosGamePlayManager.InvokeUserCode_PlayResetAnim__Int32))]
    private static bool Chaos(NetworkBehaviour obj, NetworkReader reader) =>
        Deliver(obj, reader, "Chaos");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PokerGamePlayManager), nameof(PokerGamePlayManager.InvokeUserCode_PlayResetAnim__Int32))]
    private static bool Poker(NetworkBehaviour obj, NetworkReader reader) =>
        Deliver(obj, reader, "Poker");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlorfGamePlayManager), nameof(BlorfGamePlayManager.InvokeUserCode_PlayResetAnim__Int32))]
    private static bool Blorf(NetworkBehaviour obj, NetworkReader reader) =>
        Deliver(obj, reader, "Blorf");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlorfMatchMakingGamePlayManager),
                  nameof(BlorfMatchMakingGamePlayManager.InvokeUserCode_PlayResetAnim__Int32))]
    private static bool BlorfMatchMaking(NetworkBehaviour obj, NetworkReader reader) =>
        Deliver(obj, reader, "Blorf (matchmaking)");

    /// <summary>
    /// Read the seat off the wire, pick a clip that exists, and play it.
    ///
    /// Returns false so the game's own version does not also run: the number has already been
    /// taken out of the reader by then, and reading it twice would take the next message's
    /// bytes instead. Everything the original did is done here, including its complaint about
    /// arriving on a server.
    /// </summary>
    private static bool Deliver(NetworkBehaviour obj, NetworkReader reader, string mode)
    {
        try
        {
            if (!NetworkClient.active)
            {
                Plugin.Log.LogWarning($"[resetanim] {mode}: a reset animation arrived on the server - ignored, " +
                                      "which is what the game does with it too");
                return false;
            }

            int slot = reader.ReadInt();
            Play(obj, slot, mode);
            return false;
        }
        catch (Exception e)
        {
            // Never let this be the thing that breaks the round. Falling through to the
            // game's own version is not an option - the reader has moved on - so the
            // animation is simply skipped.
            Plugin.Log.LogError($"[resetanim] {mode}: could not play the reset animation: {e.Message}");
            return false;
        }
    }

    private static void Play(NetworkBehaviour obj, int slot, string mode)
    {
        // Each mode's receiver is its own method on its own type, so the cast decides which.
        if (obj == null) return;

        var texas = obj.TryCast<TexasGamePlayManager>();
        if (texas != null) { texas.UserCode_PlayResetAnim__Int32(Map(slot, Length(texas.clips), mode)); return; }

        var chaos = obj.TryCast<ChaosGamePlayManager>();
        if (chaos != null) { chaos.UserCode_PlayResetAnim__Int32(Map(slot, Length(chaos.clips), mode)); return; }

        var poker = obj.TryCast<PokerGamePlayManager>();
        if (poker != null) { poker.UserCode_PlayResetAnim__Int32(Map(slot, Length(poker.clips), mode)); return; }

        var blorf = obj.TryCast<BlorfGamePlayManager>();
        if (blorf != null) { blorf.UserCode_PlayResetAnim__Int32(Map(slot, Length(blorf.clips), mode)); return; }

        var blorfMm = obj.TryCast<BlorfMatchMakingGamePlayManager>();
        if (blorfMm != null) { blorfMm.UserCode_PlayResetAnim__Int32(Map(slot, Length(blorfMm.clips), mode)); return; }

        Plugin.Log.LogWarning($"[resetanim] {mode}: the reset animation arrived for something unrecognised - skipped");
    }

    private static int Length<T>(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T> clips)
        where T : Il2CppSystem.Object
    {
        try { return clips == null ? 0 : (int)clips.Length; }
        catch { return 0; }
    }

    /// <summary>Which of the clips that exist belongs to this seat.</summary>
    private static int Map(int slot, int clips, string mode)
    {
        try
        {
            if (clips <= 0) return 0;
            if (slot < 0) return 0;

            int seats = Seats();

            // A table no bigger than the clip list is the shipped case and is left alone.
            if (seats <= clips) return Mathf.Min(slot, clips - 1);

            // Otherwise every seat is translated, not just the ones that would have thrown.
            // Translating only the overflow left the ring inconsistent with itself - seat
            // three of eight kept clip three while seat six, half a turn further round, got
            // the same one. Dividing the ring evenly gives each clip an equal share and keeps
            // the order round the table, which is as much as can be said for it without
            // knowing what the animation depicts.
            int mapped = Mathf.Clamp((int)(slot * (long)clips / seats), 0, clips - 1);

            if (mapped != slot && Plugin.Verbose != null && Plugin.Verbose.Value && Report(slot))
                Plugin.Log.LogInfo(
                    $"[resetanim] {mode}: seat {slot} of {seats} has no reset animation of its own " +
                    $"({clips} shipped) - using the one for seat {mapped}, rather than throwing and " +
                    "taking every connection down with it");

            return mapped;
        }
        catch { return 0; }
    }

    /// <summary>Once per seat per session: this fires on every deal, and the news is the seat.</summary>
    private static bool Report(int slot)
    {
        if (slot < 0 || slot > 63) return true;
        ulong bit = 1UL << slot;
        if ((_said & bit) != 0) return false;
        _said |= bit;
        return true;
    }

    private static ulong _said;

    /// <summary>
    /// How many are at the table, asked in a way a client can answer.
    ///
    /// <c>Manager.Players</c> is a plain Mirror list, which means it is the server's and is
    /// EMPTY on every client - a rule this project has written down three times and this
    /// broke anyway. The mapping happens on the receiving end, so each machine works it out
    /// for itself: with the server-only list, the host divided the ring by the real number
    /// and every client divided it by the fallback of eight, and the same seat got a
    /// different animation on different screens. At four players it was worse than that -
    /// the host passed the seat straight through, as shipped, while the clients remapped it.
    ///
    /// <c>StartPlayerCount</c> is a SyncVar, so it is the same number everywhere.
    /// </summary>
    private static int Seats()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) return Limits.Max;

            if (m.StartPlayerCount > 0) return m.StartPlayerCount;

            int n = m.Players != null ? m.Players.Count : 0;   // server's own view, when there is one
            return n > 0 ? n : Limits.Max;
        }
        catch { return Limits.Max; }
    }
}
