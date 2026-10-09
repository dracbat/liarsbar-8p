using System;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Lets a player point the revolver at anyone at the table instead of at three fixed chairs.
///
/// When a chaos card lands - and in Liar's Poker, and in the Chaos mode - the round stops and
/// whoever is holding the gun chooses somebody to shoot. The game asks two questions to work
/// out who that is, and both were written for exactly four chairs:
///
/// <list type="number">
/// <item><b>How far the aim can move.</b> The aiming keys walk a single number between -1 and
/// 1 and refuse to go past either end. Three values, three targets - which is every opponent
/// when there are four players and nobody else.</item>
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
/// <b>Where the keys are.</b> In Liar's Poker the keys call <c>LeftAim</c> and
/// <c>RightAim</c>, so patching those two is enough there. In the Chaos deck - the variant the
/// chaos card belongs to - it is not: the game's build compiled a copy of both methods
/// straight into <c>ChaosDeckGameplay.UpdateCall</c>, so the real methods are never called and
/// a patch on them never runs when a person presses a key. Version 1.1.1 patched only those
/// methods, which is why the arithmetic here was right and players at seven still could only
/// reach the three people furthest away. The Chaos deck's keys are handled by
/// <see cref="AimKeys"/>, which reads the same keys and steps with <see cref="Step"/> below.
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
    internal static int Offset(int n, int aim) => n / 2 - aim;

    /// <summary>The aim that points this far round. The inverse of <see cref="Offset"/>.</summary>
    private static int AimFor(int n, int offset) => n / 2 - offset;

    /// <summary>Furthest right the aim goes: the next chair round.</summary>
    internal static int Highest(int n) => n / 2 - 1;

    /// <summary>Furthest left the aim goes: the chair before mine.</summary>
    internal static int Lowest(int n) => n / 2 - (n - 1);

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
    internal static bool Who(CharController self, out Manager m, out int me, out int n)
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

    /// <summary>
    /// The same question for the readout: this player's seat and the size of the ring, or
    /// false at four players and below, where the shipped game needs no help.
    /// </summary>
    internal static bool Ring(CharController self, out int me, out int n) => Who(self, out _, out me, out n);

    /// <summary>The player an aim points at, or null - the same question the shipped code asks.</summary>
    internal static PlayerStats Target(Manager m, int me, int aim, int n, bool skipOut)
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
    /// is the difference between choosing a target and hunting for one. It also matters to
    /// the shot: the server refuses to kill somebody who is already dead, so a live chamber
    /// fired at an empty chair is a chaos card thrown away.
    ///
    /// <paramref name="wrap"/> carries the aim on round the table past either end - from the
    /// chair on your right straight to the chair on your left, the way the seats actually sit
    /// - instead of stopping there. The Chaos deck's keys ask for it: with seven people to
    /// choose from, two neighbours who are a single press apart at the table were five presses
    /// apart on a range with hard ends. Liar's Poker keeps the shipped hard stops.
    ///
    /// False means there was nowhere further to go, which is also what the shipped code does at
    /// either end of its range: it stays where it is.
    /// </summary>
    internal static bool Step(Manager m, int me, int n, int from, int direction, bool wrap, out int landed)
    {
        landed = from;
        int lo = Lowest(n), hi = Highest(n);
        int span = hi - lo + 1;                      // every aim that names somebody else
        if (span < 1 || direction == 0) return false;

        for (int i = 1; i <= span; i++)
        {
            int a = from + direction * i;
            if (wrap) a = lo + (((a - lo) % span) + span) % span;
            else if (a < lo || a > hi) break;
            if (a == from) break;                    // all the way round and back
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
    /// order exactly at four players, and extends it the only way it can. Every aim it can
    /// answer with is inside the range the keys walk, and names somebody still in the game.
    /// </summary>
    internal static int First(Manager m, int me, int n)
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
    internal static void ShowPose(CharController self, int n, int aim)
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

    // Nothing in the game calls these two - the keys are compiled into UpdateCall, see
    // AimKeys - so in normal play these prefixes never run. They stay so that the methods
    // still mean what the keys mean if a later build of the game starts calling them.

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "LeftAim")]
    private static bool DeckLeft(ChaosDeckGameplay __instance) => !DeckMove(__instance, -1);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), "RightAim")]
    private static bool DeckRight(ChaosDeckGameplay __instance) => !DeckMove(__instance, +1);

    /// <summary>A move asked for through <c>LeftAim</c> / <c>RightAim</c>. Returns whether it handled the call.</summary>
    private static bool DeckMove(ChaosDeckGameplay self, int direction)
    {
        if (!Who(self, out var m, out int me, out int n)) return false;

        try { if (self.AimLocked) return true; }             // the server will refuse it anyway
        catch { return false; }

        MoveDeck(self, m, me, n, direction, out _, out _);
        return true;
    }

    /// <summary>
    /// Move the aim one chair - one person still in the game - and tell the other machines.
    ///
    /// The shipped key code sets the value locally so the pose does not wait for the network,
    /// then sends it on - and both halves matter. Setting only the SyncVar would move nothing
    /// anywhere else, because a client writing a SyncVar writes it locally and no further;
    /// sending only the command would leave the local pose a round trip behind every keypress.
    ///
    /// The caller has already decided the aim may move; whether it is locked is not asked
    /// again here, because <see cref="AimKeys"/> holds the lock up while it calls this.
    /// Returns whether the aim moved.
    /// </summary>
    internal static bool MoveDeck(ChaosDeckGameplay self, Manager m, int me, int n, int direction,
                                  out int from, out int to)
    {
        from = to = 0;
        try { from = self.Aim; }
        catch { return false; }

        if (!Step(m, me, n, from, direction, true, out to)) { to = from; return false; }

        try
        {
            self.NetworkAim = to;
            ShowPose(self, n, to);
            SendAim(self, to);
            return true;
        }
        catch (Exception e)
        {
            Dev.Warn("aim", $"could not move the aim: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Make sure an aim that is just opening points at somebody the keys can reach and who is
    /// still in the game - and move it there if not.
    ///
    /// The server picks the opening aim with <see cref="First"/>, which only ever answers with
    /// a live seat inside the range the keys walk, so in practice this finds nothing to do. It
    /// is here because the cost of being wrong is a whole aiming phase: a player whose aim
    /// opened on an empty chair and who did not touch the keys would fire at nobody, and before
    /// this release an opening outside the range the shipped keys walked could not be moved
    /// at all. One look per aiming phase, on the machine of the person aiming.
    /// </summary>
    internal static bool FixOpening(ChaosDeckGameplay self, Manager m, int me, int n)
    {
        try
        {
            int aim = self.Aim;
            if (aim >= Lowest(n) && aim <= Highest(n) && Target(m, me, aim, n, true) != null) return false;

            int a = First(m, me, n);
            if (a == aim || Target(m, me, a, n, true) == null) return false;

            self.NetworkAim = a;
            ShowPose(self, n, a);
            SendAim(self, a);
            Plugin.Log.LogInfo($"[aimkeys] the aim opened on an empty or unreachable chair ({aim}) - moved to {a}");
            return true;
        }
        catch (Exception e)
        {
            Dev.Warn("aim", $"could not check the opening aim: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Push the new aim to the server the way the shipped code does.
    ///
    /// <c>SyncAimToServer</c> is the game's own: on the host it writes the SyncVar, on a client
    /// that owns the seat it sends <c>CmdSetAim</c>, which carries Mirror's authority check and
    /// its own serialisation with it - a hand-written stand-in would be a second copy of all of
    /// that to keep correct. It is private in the game, but the interop assembly exposes it, so
    /// it is called directly rather than by reflection.
    /// </summary>
    private static void SendAim(ChaosDeckGameplay self, int aim)
    {
        try { self.SyncAimToServer(aim); }
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

    // The same correction for the moment an aiming phase opens is the last patch in this
    // class, below Liar's Poker - see DeckAimOpened for why it is down there.

    // ------------------------------------------------------------------- the Chaos mode
    //
    // The standalone mode, which players cannot currently pick. Its keys are compiled into its
    // UpdateCall the same way the Chaos deck's are, so the two key prefixes below do not run
    // either; left as they were until the mode is reachable and its aim path can be checked.

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

    // Poker's UpdateCall really does call these two when A or D is pressed, so here the
    // patch is on the key path.

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
    /// best and would disconnect the sender at worst. The ends of the range stay hard stops,
    /// as they are in the shipped game.
    /// </summary>
    private static bool Move(CharController self, int direction, int from, Action<int> set)
    {
        if (!Who(self, out var m, out int me, out int n)) return false;
        if (!Step(m, me, n, from, direction, false, out int to)) return true;

        try
        {
            set(to);
            ShowPose(self, n, to);
        }
        catch (Exception e) { Dev.Warn("aim", $"could not move the aim: {e.Message}"); }
        return true;
    }

    // ------------------------------------------------- the Chaos deck, as an aiming phase opens

    /// <summary>
    /// The Chaos deck's pose correction (<see cref="DeckAimChanged"/>) for the moment an aiming
    /// phase opens.
    ///
    /// When the server starts somebody aiming it tells every machine with
    /// <c>RpcStartMasterProcesses</c>, and that, too, hands the raw aim to the animator. The
    /// hook on aim changes does not cover it: the call usually arrives before the new aim value
    /// does, and if the aim is the same as last time no change arrives at all, so nothing would
    /// come along afterwards to replace a raw 2 or -3 with a clip that exists.
    ///
    /// Patched on Mirror's handler for the call rather than on the method with the body in it,
    /// because the game's build compiled that body straight into the handler - a patch on the
    /// method itself would never run.
    ///
    /// Kept last in the class on purpose. Every patch here is applied in the order it is
    /// written, and the first one that fails stops the rest. This is the only one on a
    /// generated network handler rather than an ordinary method, and the only one whose loss
    /// costs nothing but a pose; written any higher, a failure here would quietly take the
    /// Liar's Poker and Chaos mode aim fixes down with it.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.InvokeUserCode_RpcStartMasterProcesses__Boolean))]
    private static void DeckAimOpened(NetworkBehaviour obj)
    {
        try
        {
            var deck = obj != null ? obj.TryCast<ChaosDeckGameplay>() : null;
            if (deck == null) return;
            if (!Who(deck, out _, out _, out int n)) return;
            ShowPose(deck, n, deck.Aim);
        }
        catch (Exception e) { Dev.Warn("aim", $"could not set the opening pose: {e.Message}"); }
    }
}
