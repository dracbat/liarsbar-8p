using System;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// The last word on a bot's turn: a bot still holding the turn with its clock nearly out has had
/// its move fail, so the turn is passed on, the way a person's own copy ends a turn.
///
/// A bot's seat has no owner, and in the game only the owner's copy does anything when a turn's
/// clock runs out: a person who sits still has a bid made or a card thrown for them by their own
/// copy. The host's clock only counts down while the turn is held and stops at nought, and nothing
/// else in the game moves the turn on, so a bot whose move threw, or was not taken, would hold
/// every player at the table for good. The brain always moves with two seconds left; this steps in
/// under one.
///
/// It reads nothing of the brain, only who holds the turn and the clock, so it works when the brain
/// cannot run at all, as after a game update that renamed something a move uses. The first time it
/// is needed, or a move or a whole tick of the brain or of this throws, bots are off until the game
/// restarts: no new one is added, and those already seated have their turns passed on like this
/// until the match ends.
///
/// Only at a table whose calls it knows. A caller keeps the turn, and its clock keeps running,
/// while the call is settled, and a turn moved on in the middle of that would break the round. So
/// it never acts while a call is being settled: in Liar's Dice the liar or spot-on call and the
/// drink after it; in Liar's Deck the liar call, the Devil's Deal and the shots that settle them;
/// in the Chaos deck the liar call, the chaos card, and anybody holding the gun; and at either deck
/// table while the cards are dealt, or while the game holds every player's moves, as it does when
/// it makes the call itself for the last player still holding cards.
///
/// If the game cannot be asked whether a call is being settled, as after an update that renamed a
/// flag it is asked by, bots are off as well, no bot moves, and only the clock is left to go by: a
/// bot's turn is passed on once its clock has sat under a second for longer than a call takes to
/// settle. Each caller asks inside a try of its own, because a renamed flag fails the call that
/// names it as that call is compiled, before any try inside it can run.
/// </summary>
internal static class BotTurnGuard
{
    /// <summary>Under this many seconds a bot still holding the turn has it passed on.</summary>
    private const float PassUnder = 1f;

    /// <summary>
    /// With no way to tell a call being settled from a move that failed: how long a bot's clock sits
    /// under a second before its turn is passed on. A caller holds the turn while its call is settled,
    /// its clock running down meanwhile, and the brain says so as a flag left up if that lasts this long.
    /// </summary>
    private const float ClockAloneAfter = 20f;

    /// <summary>Set the first time a bot could not move. No bot is added again this session.</summary>
    internal static bool Tripped { get; private set; }

    /// <summary>From <see cref="ModTicker"/>, after the brain, on the host.</summary>
    internal static void Tick()
    {
        if (!BotSeats.Any || !NetworkServer.active) return;

        var m = Manager.Instance;
        if (m == null || !m.GameStarted) return;

        var table = TableHand.Playing();
        if (table != TableHand.Kind.Dice && table != TableHand.Kind.Deck && table != TableHand.Kind.ChaosDeck) return;

        // A call of its own, inside this try: a renamed flag fails the call that names it.
        bool settling = false, clockAlone = false;
        try { settling = Settling(m, table); }
        catch (Exception e)
        {
            clockAlone = true;
            Trip($"the game could not be asked whether a call is being settled ({e.Message})");
        }

        float clock = m.CountDown;
        float now = UnityEngine.Time.time;
        foreach (var seat in BotSeats.Seated())
        {
            var p = seat.Player;
            bool holds = !p.Dead && p.HaveTurn;
            if (!holds || clock >= PassUnder) seat.LowSince = -1f;
            else if (seat.LowSince < 0f) seat.LowSince = now;

            if (clockAlone)
            {
                if (seat.LowSince < 0f || now - seat.LowSince < ClockAloneAfter) continue;
            }
            else
            {
                if (settling || !holds) { seat.ClockRan = false; continue; }

                // A clock never seen running this turn may be the last turn's, not yet set again.
                if (clock >= PassUnder) { seat.ClockRan = true; continue; }
                if (!seat.ClockRan) continue;
            }

            seat.ClockRan = false;
            seat.LowSince = -1f;
            Trip($"seat {p.Slot} still held the turn with its clock nearly out");
            try
            {
                p.NetworkHaveTurn = false;
                m.GiveTurn();
                Plugin.Log.LogWarning($"[bot] seat {p.Slot}'s move did not happen: its turn was passed on" +
                                      (clockAlone ? ", by the clock alone" : ""));
            }
            catch (Exception e) { Plugin.Log.LogError($"[bot] could not pass seat {p.Slot}'s turn: {e.Message}"); }
            return;
        }
    }

    /// <summary>
    /// Whether a call is being settled at the table being played, asked of that table's manager.
    /// This and the three below throw when the game cannot say; every caller asks inside a try.
    /// </summary>
    private static bool Settling(Manager m, TableHand.Kind table) => table switch
    {
        TableHand.Kind.Dice => Settling(m.DiceGame),
        TableHand.Kind.Deck => Settling(m.DeckGamePlayManager),
        _ => Settling(m, m.ChaosDeckGame),
    };

    /// <summary>Liar's Dice: a liar or spot-on call, or the drink that settles it.</summary>
    internal static bool Settling(DiceGamePlayManager d)
    {
        TestCannotAsk();
        if (d == null) return true;
        return d.CalledLiar || d.CalledSpotOn || d.DrinkPhaseActive;
    }

    /// <summary>
    /// Liar's Deck, Basic, Devil and Deck 2: a liar call or the Devil's Deal and its shots, the deal,
    /// or the game holding everybody's moves, as it does while it makes the call itself for the last
    /// player with cards.
    /// </summary>
    internal static bool Settling(DeckGamePlayManager dm)
    {
        TestCannotAsk();
        if (dm == null) return true;
        return dm.LiarCalled || dm.spotOnResolveActive || dm.liarResolveRoutineActive || dm.BlocksBidInput ||
               DealTrace.Dealing;
    }

    /// <summary>
    /// The Chaos deck: a liar call, the chaos card and its gun, the gun handed to a liar's catcher,
    /// the deal, or the game holding every move. Asked of the manager's own flags, never of a seat
    /// still marked as aiming: a seat's mark is cleared by its own copy after its shot, so a bot's
    /// stayed up after the chaos card's volley, and a first build that read it held every bot still
    /// for good at the next turn.
    /// </summary>
    internal static bool Settling(Manager m, ChaosDeckGamePlayManager cm)
    {
        TestCannotAsk();
        if (cm == null) return true;
        return cm.LiarCalled || cm.ChaosStarted || cm.MasterStarted || cm.chaosThrowResolving || cm.BlocksBidInput ||
               DealTrace.Dealing;
    }

    /// <summary>
    /// Developer test switch, through <see cref="BotBrain.TestFault"/>: the asking fails, as it would
    /// for a flag a game update renamed. It does nothing outside developer mode.
    /// </summary>
    private static void TestCannotAsk()
    {
        if (BotBrain.TestFault == BotBrain.Fault.Settle && Fair.TestsAllowed)
            throw new MissingFieldException("test: the game cannot be asked whether a call is being settled");
    }

    private static float _heldSince = -1f;
    private static bool _saidHeld;

    /// <summary>
    /// From the deck brains: a bot holding the turn while the game says a call is being settled.
    /// Neither the bot nor this guard moves then, which is right while the call plays out; held a
    /// long time it is a flag left up, and it is said once a session, with the flags, so the next
    /// one is found at once.
    /// </summary>
    internal static void HeldWhileSettling(bool held, Func<string> flags)
    {
        if (!held) { _heldSince = -1f; return; }
        float now = UnityEngine.Time.time;
        if (_heldSince < 0f) _heldSince = now;
        if (_saidHeld || now - _heldSince < 20f) return;
        _saidHeld = true;
        string up;
        try { up = flags(); }
        catch (Exception e) { up = e.Message; }
        Plugin.Log.LogWarning($"[bot] a bot has held the turn for 20 s while a call is being settled ({up}) - nothing moves");
    }

    /// <summary>Whether any bot still in the match holds the turn.</summary>
    internal static bool ABotHolds()
    {
        foreach (var seat in BotSeats.Seated())
            if (!seat.Player.Dead && seat.Player.HaveTurn) return true;
        return false;
    }

    /// <summary>The settling flags that are up, for that one line.</summary>
    internal static string Flags(DeckGamePlayManager dm) =>
        dm == null ? "no manager" :
        $"liar called {dm.LiarCalled}, devil's deal {dm.spotOnResolveActive}, resolving {dm.liarResolveRoutineActive}, " +
        $"input held {dm.BlocksBidInput}, dealing {DealTrace.Dealing}";

    internal static string Flags(ChaosDeckGamePlayManager cm) =>
        cm == null ? "no manager" :
        $"liar called {cm.LiarCalled}, chaos {cm.ChaosStarted}, gun {cm.MasterStarted}, chaos throw {cm.chaosThrowResolving}, " +
        $"input held {cm.BlocksBidInput}, dealing {DealTrace.Dealing}";

    /// <summary>A bot could not move: said once, and from now on no bot is added this session.</summary>
    internal static void Trip(string why)
    {
        if (Tripped) return;
        Tripped = true;
        Plugin.Log.LogWarning($"[bot] bots are off until the game restarts: {why}");
    }
}
