using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Lets a player point the revolver at anyone at the table instead of at three fixed chairs.
///
/// When a chaos card lands - and in Liar's Poker, and in the Chaos mode - the round stops and
/// whoever is holding the gun chooses somebody to shoot. The game asks two questions to work
/// out who that is, and both were written for exactly four chairs:
///
/// <list type="number">
/// <item><b>How far the aim can move.</b> <c>LeftAim</c> and <c>RightAim</c> walk a single
/// number between -1 and 1 and refuse to go past either end. Three values, three targets -
/// which is every opponent when there are four players and nobody else.</item>
/// <item><b>Who that number means.</b> <c>GetAimTargetSlot</c> is a hand-written table of four
/// seats by three directions. Seats 0-3 are listed; anything else falls off the end and the
/// method returns nothing at all.</item>
/// </list>
///
/// So at eight players the aiming phase is broken in two directions at once. Seats four to
/// seven cannot aim at anybody - the table has no row for them, so the shot resolves against
/// nothing and the chaos card is spent for free. And seats zero to three can only ever pick
/// each other: half the table is not merely hard to hit, it is unreachable, because no value
/// the aim is allowed to hold refers to it. A chaos card at eight players was therefore never
/// the mechanic it looks like - it was a choice between three of the seven people it should
/// have been offering.
///
/// <b>What the shipped table actually encodes.</b> Read as arithmetic rather than as a list,
/// it is
///
///     target = (mySlot + (2 - aim)) mod 4
///
/// - aim 1 is the next seat round, aim 0 is straight across, aim -1 is the seat before. That
/// generalises on its own: with <c>n</c> at the table, the offsets that mean somebody other
/// than yourself are 1 to n-1, so
///
///     target = (mySlot + (n/2 - aim)) mod n,   aim from n/2-(n-1) up to n/2-1
///
/// At four players <c>n/2</c> is 2 and the range is -1 to 1: the same three answers, for the
/// same three inputs, as the shipped table. That is the point of writing it this way. The
/// replacement is not a new rule that happens to agree at four - it is the shipped rule with
/// the four taken out of it. Aim 0 still means the player opposite whatever the size, which is
/// what keeps the controls feeling like the ones people already know.
///
/// <b>What the aim still cannot do.</b> The pose is an animation, not a rotation: the game
/// plays one of three canned looking-left / ahead / right clips and never turns anyone to face
/// a particular chair. That is as true of the shipped game as of this, so past four chairs a
/// pose can only say roughly which way somebody is pointing. The aim is therefore reported to
/// the animator as one of those same three poses - chosen by which third of the table the
/// target sits in - so the clips asked for are always ones that exist. Which player is
/// actually being aimed at is said in words instead, by <see cref="AimHud"/>.
///
/// Below five players none of this does anything. The shipped code is already right for the
/// table it was written for, and the arithmetic above would only reproduce it.
/// </summary>
[HarmonyPatch]
internal static class AimRing
{
    // ------------------------------------------------------------------- the arithmetic

    /// <summary>
    /// How many chairs the ring has, from a number every machine agrees on.
    ///
    /// <c>StartPlayerCount</c> is a SyncVar, so a client reads what the host wrote.
    /// <c>Manager.Players</c> is the server's own roster and is empty on a client - reading it
    /// first would have every client compute a different ring from the host and draw the aim
    /// at somebody else entirely, which is exactly the mistake the turn order table made.
    /// </summary>
    internal static int Seats(Manager m)
    {
        try
        {
            if (m == null) return 0;
            if (m.StartPlayerCount > 0) return m.StartPlayerCount;
            return m.Players != null ? m.Players.Count : 0;
        }
        catch { return 0; }
    }

    /// <summary>Chairs round the table from me to the target: 1 is the next one, n-1 the previous.</summary>
    private static int Offset(int n, int aim) => n / 2 - aim;

    /// <summary>The aim that points this far round. The inverse of <see cref="Offset"/>.</summary>
    private static int AimFor(int n, int offset) => n / 2 - offset;

    /// <summary>Furthest right the aim goes: the next chair round.</summary>
    private static int Highest(int n) => n / 2 - 1;

    /// <summary>Furthest left the aim goes: the chair before mine.</summary>
    private static int Lowest(int n) => n / 2 - (n - 1);

    /// <summary>The seat an aim points at, or -1 when it points at nobody - or at me.</summary>
    private static int TargetSlot(int me, int aim, int n)
    {
        if (n < 2 || me < 0) return -1;
        int off = Offset(n, aim);
        if (off < 1 || off > n - 1) return -1;
        return (me + off) % n;
    }

    /// <summary>
    /// Which of the three shipped poses to play for an aim.
    ///
    /// Nearer round to the right than straight across is the right-hand clip, the seat
    /// opposite is ahead, everything further round is the left-hand one. At four players this
    /// returns 1, 0 and -1 for the three aims that exist, so the animator is asked for exactly
    /// what it was always asked for.
    /// </summary>
    private static int Pose(int n, int aim)
    {
        int off = Offset(n, aim);
        int across = n / 2;
        if (off < across) return 1;
        if (off > across) return -1;
        return 0;
    }

    // --------------------------------------------------------------- asking about a seat

    /// <summary>
    /// The seat this player is in, the manager they belong to, and how big the ring is - or
    /// false, meaning leave the shipped code to it.
    ///
    /// The seat is taken off the component rather than out of a roster. The gameplay component
    /// lives on the player object, so its own <c>playerStats</c> is the one seat that is
    /// certainly right, on a client as much as on the host.
    /// </summary>
    private static bool Who(CharController self, out Manager m, out int me, out int n)
    {
        m = null; me = -1; n = 0;
        try
        {
            if (self == null) return false;

            var stats = self.playerStats;
            if (stats == null) return false;
            me = stats.Slot;

            m = self.manager;
            if (m == null) m = Manager.Instance;
            if (m == null) return false;

            n = Seats(m);
            return n > Limits.VanillaPlayers && me >= 0 && me < n;
        }
        catch { return false; }
    }

    /// <summary>The player an aim points at, or null - the same question the shipped code asks.</summary>
    private static PlayerStats Target(Manager m, int me, int aim, int n, bool skipOut)
    {
        int slot = TargetSlot(me, aim, n);
        if (slot < 0) return null;
        try { return m.GetTargetPlayer(slot, skipOut); }
        catch { return null; }
    }

    /// <summary>
    /// The next aim in a direction that points at somebody still in the game.
    ///
    /// Empty and dead chairs are stepped over rather than stopped on. With seven possible
    /// targets and a game that kills people steadily, an aim that could rest on a corpse would
    /// spend much of a match pointing at nobody - and the player would have no way to tell,
    /// because the pose is the same either way. Four seats could get away without this; seven
    /// is the difference between choosing a target and hunting for one.
    ///
    /// False means there was nowhere further to go, which is also what the shipped code does at
    /// either end of its range: it stays where it is.
    /// </summary>
    private static bool Step(Manager m, int me, int n, int from, int direction, out int landed)
    {
        landed = from;
        int lo = Lowest(n), hi = Highest(n);

        for (int a = from + direction; a >= lo && a <= hi; a += direction)
        {
            if (Target(m, me, a, n, true) == null) continue;
            landed = a;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Where the aim starts: straight across if that chair is occupied, then outwards either
    /// way until somebody is found.
    ///
    /// The shipped code tries 0, then 1, then -1. Working outwards from zero reproduces that
    /// order exactly at four players, and extends it the only way it can.
    /// </summary>
    private static int First(Manager m, int me, int n)
    {
        int lo = Lowest(n), hi = Highest(n);

        for (int d = 0; d <= n; d++)
        {
            for (int s = 0; s < 2; s++)
            {
                if (d == 0 && s == 1) continue;             // 0 and -0 are the same aim
                int a = s == 0 ? d : -d;
                if (a < lo || a > hi) continue;
                if (Target(m, me, a, n, true) != null) return a;
            }
        }
        return 0;
    }

    /// <summary>Tell the animator which of its three poses to play, if there is one to tell.</summary>
    private static void ShowPose(CharController self, int n, int aim)
    {
        try
        {
            var anim = self.animator;
            if (anim != null) anim.SetInteger("LookDirection", Pose(n, aim));
        }
        catch { }
    }

    // ------------------------------------------------------- what the readout wants to know

    /// <summary>
    /// The player a given seat is currently pointing at, for the on-screen readout.
    ///
    /// Answers for whichever of the three aiming modes this player is in, and null in any
    /// other - including at four players and below, where the shipped pose already says all
    /// there is to say.
    /// </summary>
    internal static PlayerStats AimingAt(CharController self)
    {
        if (!Who(self, out var m, out int me, out int n)) return null;

        try
        {
            int aim;
            var deck = self.TryCast<ChaosDeckGameplay>();
            if (deck != null) aim = deck.Aim;
            else
            {
                var chaos = self.TryCast<ChaosGamePlay>();
                if (chaos != null) aim = chaos.Aim;
                else
                {
                    var poker = self.TryCast<PokerGamePlay>();
                    if (poker == null) return null;
                    aim = poker.Aim;
                }
            }

            return Target(m, me, aim, n, true);
        }
        catch { return null; }
    }

    /// <summary>Where a seat's aim is pointing, as a number - cheap enough to ask every frame.</summary>
    internal static int CurrentAim(CharController self)
    {
        try
        {
            var deck = self.TryCast<ChaosDeckGameplay>();
            if (deck != null) return deck.Aim;

            var chaos = self.TryCast<ChaosGamePlay>();
            if (chaos != null) return chaos.Aim;

            var poker = self.TryCast<PokerGamePlay>();
            if (poker != null) return poker.Aim;
        }
        catch { }
        return 0;
    }

    /// <summary>Whether this seat is choosing a target right now, in any of the three modes.</summary>
    internal static bool Choosing(CharController self)
    {
        try
        {
            var deck = self.TryCast<ChaosDeckGameplay>();
            if (deck != null) return deck.TakingAim;

            var chaos = self.TryCast<ChaosGamePlay>();
            if (chaos != null) return chaos.TakingAim;

            var poker = self.TryCast<PokerGamePlay>();
            if (poker != null) return poker.TakingAim;
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ the Chaos deck
    //
    // The variant the chaos card belongs to, and the one this was reported against.

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "GetAim", new Type[] { typeof(int), typeof(bool) })]
    private static bool DeckGetAim2(ChaosDeckGameplay __instance, int aim, bool excludeFinished,
                                    ref PlayerStats __result)
    {
        if (!Who(__instance, out var m, out int me, out int n)) return true;
        __result = Target(m, me, aim, n, excludeFinished);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "GetAim", new Type[] { typeof(int) })]
    private static bool DeckGetAim1(ChaosDeckGameplay __instance, int aim, ref PlayerStats __result)
    {
        if (!Who(__instance, out var m, out int me, out int n)) return true;
        __result = Target(m, me, aim, n, true);
        return false;
    }

    /// <summary>
    /// Which aim points at a given seat - asked when the game forces somebody to shoot a
    /// particular player rather than letting them choose.
    ///
    /// The shipped version searches a three element list of aim values, so even with the seat
    /// table corrected it could still only ever answer with one of three directions, and the
    /// forced shot would land on whoever those happened to reach. It is the inverse of the
    /// arithmetic above, so it is computed as the inverse rather than searched for.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "GetAimDirectionForSlot")]
    private static bool DeckAimForSlot(ChaosDeckGameplay __instance, int targetSlot, ref int __result)
    {
        if (!Who(__instance, out var m, out int me, out int n)) return true;

        if (targetSlot >= 0 && targetSlot < n && targetSlot != me)
        {
            int aim = AimFor(n, (targetSlot - me + n) % n);
            if (aim >= Lowest(n) && aim <= Highest(n)) { __result = aim; return false; }
        }

        __result = First(m, me, n);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "GetFirstAim")]
    private static bool DeckFirstAim(ChaosDeckGameplay __instance, ref int __result)
    {
        if (!Who(__instance, out var m, out int me, out int n)) return true;
        __result = First(m, me, n);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "LeftAim")]
    private static bool DeckLeft(ChaosDeckGameplay __instance) => !DeckMove(__instance, -1);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "RightAim")]
    private static bool DeckRight(ChaosDeckGameplay __instance) => !DeckMove(__instance, +1);

    /// <summary>
    /// Move the aim one chair and tell the other machines about it.
    ///
    /// The shipped method sets the value locally so the pose does not wait for the network,
    /// then sends it on - and both halves matter. Setting only the SyncVar would move nothing
    /// anywhere else, because a client writing a SyncVar writes it locally and no further;
    /// sending only the command would leave the local pose a round trip behind every keypress.
    ///
    /// Returns whether it handled the call.
    /// </summary>
    private static bool DeckMove(ChaosDeckGameplay self, int direction)
    {
        if (!Who(self, out var m, out int me, out int n)) return false;

        int from;
        try
        {
            if (self.AimLocked) return true;                 // the server will refuse it anyway
            from = self.Aim;
        }
        catch { return false; }

        if (!Step(m, me, n, from, direction, out int to)) return true;

        try
        {
            self.NetworkAim = to;
            ShowPose(self, n, to);
            SendAim(self, to);
        }
        catch (Exception e) { Dev.Warn("aim", $"could not move the aim: {e.Message}"); }
        return true;
    }

    /// <summary>
    /// Push the new aim to the server the way the shipped code does.
    ///
    /// <c>SyncAimToServer</c> is private, so it is called by reflection rather than
    /// reimplemented. It picks between writing the SyncVar directly and sending
    /// <c>CmdSetAim</c> depending on which machine this is, and the command carries Mirror's
    /// authority check and its own serialisation with it - a hand-written stand-in would be a
    /// second copy of all of that to keep correct.
    /// </summary>
    private static System.Reflection.MethodInfo _sync;
    private static bool _syncLooked;

    private static void SendAim(ChaosDeckGameplay self, int aim)
    {
        if (!_syncLooked)
        {
            _syncLooked = true;
            _sync = AccessTools.Method(typeof(ChaosDeckGameplay), "SyncAimToServer",
                                       new Type[] { typeof(int) });
            if (_sync == null)
                Plugin.Log.LogWarning("[aim] SyncAimToServer is missing - aim changes will not leave this machine");
        }

        if (_sync == null) return;
        try { _sync.Invoke(self, new object[] { aim }); }
        catch (Exception e) { Dev.Warn("aim", $"could not send the aim: {e.Message}"); }
    }

    /// <summary>
    /// What the other machines play when somebody's aim changes.
    ///
    /// The shipped hook hands the raw aim straight to the animator. Over a range wider than
    /// -1 to 1 that asks for clips the controller does not have, so it is given the pose.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "OnAimChanged")]
    private static bool DeckAimChanged(ChaosDeckGameplay __instance, int newValue)
    {
        if (!Who(__instance, out _, out _, out int n)) return true;
        ShowPose(__instance, n, newValue);
        return false;
    }

    // ------------------------------------------------------------------- the Chaos mode

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosGamePlay), "GetAim")]
    private static bool ChaosGetAim(ChaosGamePlay __instance, int aim, ref PlayerStats __result)
    {
        if (!Who(__instance, out var m, out int me, out int n)) return true;
        __result = Target(m, me, aim, n, true);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosGamePlay), "GetFirstAim")]
    private static bool ChaosFirstAim(ChaosGamePlay __instance, ref int __result)
    {
        if (!Who(__instance, out var m, out int me, out int n)) return true;
        __result = First(m, me, n);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosGamePlay), "LeftAim")]
    private static bool ChaosLeft(ChaosGamePlay __instance)
        => !Move(__instance, -1, __instance.Aim, v => __instance.NetworkAim = v);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosGamePlay), "RightAim")]
    private static bool ChaosRight(ChaosGamePlay __instance)
        => !Move(__instance, +1, __instance.Aim, v => __instance.NetworkAim = v);

    // -------------------------------------------------------------------- Liar's Poker

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PokerGamePlay), "GetAim")]
    private static bool PokerGetAim(PokerGamePlay __instance, int aim, ref PlayerStats __result)
    {
        if (!Who(__instance, out var m, out int me, out int n)) return true;
        __result = Target(m, me, aim, n, true);
        return false;
    }

    /// <summary>Poker's version sets the aim rather than returning it, so this does the same.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PokerGamePlay), "GetFirstAim")]
    private static bool PokerFirstAim(PokerGamePlay __instance)
    {
        if (!Who(__instance, out var m, out int me, out int n)) return true;

        try
        {
            int a = First(m, me, n);
            __instance.NetworkAim = a;
            ShowPose(__instance, n, a);
        }
        catch (Exception e) { Dev.Warn("aim", $"could not set the opening aim: {e.Message}"); }
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PokerGamePlay), "LeftAim")]
    private static bool PokerLeft(PokerGamePlay __instance)
        => !Move(__instance, -1, __instance.Aim, v => __instance.NetworkAim = v);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PokerGamePlay), "RightAim")]
    private static bool PokerRight(PokerGamePlay __instance)
        => !Move(__instance, +1, __instance.Aim, v => __instance.NetworkAim = v);

    /// <summary>
    /// The same move, for the two modes whose aim is a plain SyncVar with nothing behind it.
    ///
    /// Chaos and Poker set <c>NetworkAim</c> and stop; neither has a <c>CmdSetAim</c>, so this
    /// does not invent one. Sending a message the game has no handler for would be dropped at
    /// best and would disconnect the sender at worst.
    /// </summary>
    private static bool Move(CharController self, int direction, int from, Action<int> set)
    {
        if (!Who(self, out var m, out int me, out int n)) return false;
        if (!Step(m, me, n, from, direction, out int to)) return true;

        try
        {
            set(to);
            ShowPose(self, n, to);
        }
        catch (Exception e) { Dev.Warn("aim", $"could not move the aim: {e.Message}"); }
        return true;
    }
}
