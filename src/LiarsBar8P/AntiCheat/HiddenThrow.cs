using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;

namespace LiarsBar8P;

/// <summary>
/// Fair play: thrown cards stay face down until the game's own reveal.
///
/// <b>What changes.</b> In Liar's Deck (Basic, Devil, Deck 2) and the Chaos deck, at a table this
/// copy hosts (<see cref="Fair.HideHands"/>), thrown cards stay face down on every machine until
/// the game's own reveal; the host keeps them and puts them in place as the reveal begins, so
/// everything from the reveal on (the cards turned over, the devil animation, who was lying) is
/// the game's own, from the real cards. The count of cards on the table and every count-driven
/// screen stay exactly as they were. A throw with the chaos card in it is turned face up by the
/// game at once, so it is never held back.
///
/// <b>Safe by construction.</b> Only the values the game writes during the throw change; the
/// thrower's own list is put back the moment the throw is done, so nothing after it sees a
/// difference. A real card is only ever written back over the plain card this record put there,
/// and only for a throw the game really recorded. If anything fails, the throw goes through with
/// its real cards (privacy lost for one throw, never a wrong outcome). Vanilla hosts, ranked and
/// tables this copy does not host are untouched.
/// </summary>
[FairPatch("thrown cards stay face down until the reveal")]
[HarmonyPatch]
internal static class HiddenThrow
{
    internal const int DecoyCard = HiddenDeal.DecoyCard;
    private const int ChaosCard = 3;

    internal enum Table { Deck, Chaos }

    /// <summary>The host's own record of the real cards behind the plain ones, by place in each list.</summary>
    internal sealed class Record
    {
        internal readonly Dictionary<int, int> Round = new();
        internal readonly Dictionary<int, int> SpotOn = new();
        internal int Hidden, Revealed;
        internal bool Pending => Round.Count > 0 || SpotOn.Count > 0;
    }

    private static readonly Record _deck = new(), _chaos = new();
    private static int _match = -1;

    internal static Record For(Table t) { Fresh(); return t == Table.Deck ? _deck : _chaos; }

    private static void Fresh()
    {
        if (Fair.Match == _match) return;
        _match = Fair.Match;
        foreach (var r in new[] { _deck, _chaos })
        {
            r.Round.Clear();
            r.SpotOn.Clear();
        }
    }

    /// <summary>What a throw in progress needs to put back afterwards.</summary>
    internal sealed class Throw
    {
        internal Il2CppSystem.Collections.Generic.List<int> Types;
        internal int[] Truth;
        internal int SpotOnBefore;
        internal bool Hidden, Added, Done;
    }

    // ------------------------------------------------------------------ the throw

    /// <summary>
    /// Runs last of everything that looks at a throw first (the match record, the host's own
    /// checks), so they all see the real cards; and not at all when one of them refused it.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ServerThrowCards))]
    private static void DeckThrowing(Il2CppSystem.Collections.Generic.List<int> types, bool __runOriginal, out Throw __state)
    {
        __state = null;
        try
        {
            if (!__runOriginal || !NetworkServer.active) return;
            var m = Manager.Instance != null ? Manager.Instance.DeckGamePlayManager : null;
            if (m == null) return;
            // Every throw replaces the last round's cards, so what the record held for it is stale now.
            For(Table.Deck).Round.Clear();
            if (!Fair.HideHands) return;
            __state = Hide(types, m.LastRoundSpotOn, chaos: false);
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} hidden throw: {e.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ServerThrowCards))]
    private static void DeckThrown(Throw __state, bool __runOriginal) => Done(__state, Table.Deck, __runOriginal);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.ServerThrowCards))]
    private static Exception DeckThrowEnded(Exception __exception, Throw __state)
    {
        Done(__state, Table.Deck, ran: true);
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerThrowCards))]
    private static void ChaosThrowing(Il2CppSystem.Collections.Generic.List<int> types, bool __runOriginal, out Throw __state)
    {
        __state = null;
        try
        {
            if (!__runOriginal || !NetworkServer.active) return;
            var m = Manager.Instance != null ? Manager.Instance.ChaosDeckGame : null;
            if (m == null) return;
            For(Table.Chaos).Round.Clear();
            if (!Fair.HideHands) return;
            __state = Hide(types, null, chaos: true);
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} hidden throw: {e.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerThrowCards))]
    private static void ChaosThrown(Throw __state, bool __runOriginal) => Done(__state, Table.Chaos, __runOriginal);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(ChaosDeckGameplay), nameof(ChaosDeckGameplay.ServerThrowCards))]
    private static Exception ChaosThrowEnded(Exception __exception, Throw __state)
    {
        Done(__state, Table.Chaos, ran: true);
        return __exception;
    }

    /// <summary>Swap the throw's values for plain cards while the game records it.</summary>
    private static Throw Hide(Il2CppSystem.Collections.Generic.List<int> types, SyncList<int> spotOn, bool chaos)
    {
        if (types == null || types.Count == 0) return null;
        var truth = new int[types.Count];
        for (int i = 0; i < truth.Length; i++)
        {
            truth[i] = types[i];
            if (chaos && truth[i] == ChaosCard) return null;   // turned face up by the game at once
        }

        var t = new Throw { Types = types, Truth = truth, SpotOnBefore = spotOn != null ? spotOn.Count : 0 };
        for (int i = 0; i < truth.Length; i++) types[i] = DecoyCard;
        t.Hidden = true;
        _current = t;
        return t;
    }

    // The throw being recorded right now, so the game's own recording of it can be seen to happen.
    private static Throw _current;

    /// <summary>The game recorded the throw on the table: what this record writes back belongs there.</summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.AddCards))]
    private static void DeckAdding(Il2CppSystem.Collections.Generic.List<int> types) => Adding(types);

    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(ChaosDeckGamePlayManager), nameof(ChaosDeckGamePlayManager.AddCards))]
    private static void ChaosAdding(Il2CppSystem.Collections.Generic.List<int> types) => Adding(types);

    private static void Adding(Il2CppSystem.Collections.Generic.List<int> types)
    {
        try
        {
            var t = _current;
            if (t != null && !t.Done && types != null && types.Pointer == t.Types.Pointer) t.Added = true;
        }
        catch { }
    }

    /// <summary>
    /// The throw is over: put the thrower's list back, and remember what went where - but only
    /// when the game really recorded this throw (it ran, and put it on the table), so a throw the
    /// game or a check refused can never leave real cards to be written over anything.
    /// </summary>
    private static void Done(Throw t, Table table, bool ran)
    {
        if (t == null || t.Done) return;
        t.Done = true;
        if (_current == t) _current = null;
        try
        {
            if (t.Hidden)
                for (int i = 0; i < t.Truth.Length && i < t.Types.Count; i++) t.Types[i] = t.Truth[i];
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"{Fair.Tag} hidden throw could not put the thrower's list back: {e.Message}");
        }

        try
        {
            var rec = For(table);
            if (!ran || !t.Hidden || !Lists(table, out var round, out var spotOn)) return;

            // Exactly the plain cards this throw put there, or nothing is recorded and the
            // record stays empty: a reveal then shows whatever the list holds.
            int n = t.Truth.Length;
            if (t.Added && round != null && round.Count == n && AllPlain(round, 0, n))
                for (int i = 0; i < n; i++) rec.Round[i] = t.Truth[i];

            // A throw adds to the end of the spot-on list. A place recorded at or past that end
            // belongs to a list the game has since emptied for a new round: forget it.
            if (spotOn != null)
            {
                var stale = new List<int>();
                foreach (var k in rec.SpotOn.Keys) if (k >= t.SpotOnBefore) stale.Add(k);
                foreach (var k in stale) rec.SpotOn.Remove(k);
            }

            if (spotOn != null && spotOn.Count == t.SpotOnBefore + n && AllPlain(spotOn, t.SpotOnBefore, n))
                for (int i = 0; i < n; i++) rec.SpotOn[t.SpotOnBefore + i] = t.Truth[i];

            rec.Hidden++;
            if (Dev.Enabled) HandsTests.CheckThrow(table);
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} hidden throw record: {e.Message}"); }
    }

    private static bool AllPlain(SyncList<int> list, int from, int n)
    {
        for (int i = from; i < from + n; i++) if (list[i] != DecoyCard) return false;
        return true;
    }

    internal static bool Lists(Table table, out SyncList<int> round, out SyncList<int> spotOn)
    {
        round = spotOn = null;
        var m = Manager.Instance;
        if (m == null) return false;
        if (table == Table.Deck)
        {
            var d = m.DeckGamePlayManager;
            if (d == null) return false;
            round = d.LastRound;
            spotOn = d.LastRoundSpotOn;
        }
        else
        {
            var c = m.ChaosDeckGame;
            if (c == null) return false;
            round = c.LastRound;
        }
        return true;
    }

    // ------------------------------------------------------------------ the reveal

    /// <summary>
    /// The real cards go back into the lists, each only over the plain card this record put there.
    /// Runs on the host whenever a reveal starts, whatever started it; a second call does nothing.
    /// </summary>
    internal static void Reveal(Table table, string why)
    {
        try
        {
            if (!NetworkServer.active) return;
            var rec = For(table);
            if (!rec.Pending) return;
            if (!Lists(table, out var round, out var spotOn)) return;

            var roundTruth = new Dictionary<int, int>(rec.Round);
            var spotOnTruth = new Dictionary<int, int>(rec.SpotOn);
            int wrote = Put(round, rec.Round) + Put(spotOn, rec.SpotOn);
            rec.Round.Clear();
            rec.SpotOn.Clear();
            rec.Revealed++;
            if (Dev.Enabled)
            {
                Plugin.Log.LogInfo($"{Fair.Tag} thrown cards turned over for the reveal ({table}, {why}): {wrote} places");
                HandsTests.CheckReveal(table, roundTruth, spotOnTruth);
            }
        }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} hidden throw reveal ({table}): {e.Message}"); }
    }

    private static int Put(SyncList<int> list, Dictionary<int, int> truth)
    {
        if (list == null) return 0;
        int wrote = 0;
        foreach (var kv in truth)
        {
            // Set in place: one change per place, carrying only the real card.
            if (kv.Key < list.Count && list[kv.Key] == DecoyCard && kv.Value != DecoyCard)
            {
                list[kv.Key] = kv.Value;
                wrote++;
            }
        }
        return wrote;
    }

    // Each reveal coroutine opens with a pause; its first step is the earliest moment a reveal
    // has begun, whatever began it (a player's call, the automatic call after the last throw).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager._ResolveLiarRoutine_d__190), nameof(DeckGamePlayManager._ResolveLiarRoutine_d__190.MoveNext))]
    private static void DeckLiarStep() => Reveal(Table.Deck, "liar");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(DeckGamePlayManager._ResolveSpotOnRoutine_d__196), nameof(DeckGamePlayManager._ResolveSpotOnRoutine_d__196.MoveNext))]
    private static void DeckSpotOnStep() => Reveal(Table.Deck, "spot on");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChaosDeckGamePlayManager._ResolveLiarRoutine_d__235), nameof(ChaosDeckGamePlayManager._ResolveLiarRoutine_d__235.MoveNext))]
    private static void ChaosLiarStep() => Reveal(Table.Chaos, "liar");
}

/// <summary>
/// The same reveal, reached from the calls themselves: a second way in, so the cards are turned
/// over even if a game update renames the reveal coroutines above. Only when the call was
/// accepted: a refused spot-on must not turn anything over.
/// </summary>
[FairPatch("thrown cards turned over at an accepted call")]
[HarmonyPatch]
internal static class HiddenThrowCalls
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.CallLiar))]
    private static void DeckLiar(DeckGamePlayManager __instance)
    {
        try { if (__instance != null && __instance.LiarCalled) HiddenThrow.Reveal(HiddenThrow.Table.Deck, "liar call"); }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} hidden throw: {e.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DeckGamePlayManager), nameof(DeckGamePlayManager.CallSpotOn))]
    private static void DeckSpotOn(DeckGamePlayManager __instance)
    {
        try { if (__instance != null && __instance.spotOnResolveActive) HiddenThrow.Reveal(HiddenThrow.Table.Deck, "spot-on call"); }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} hidden throw: {e.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ChaosDeckGamePlayManager), nameof(ChaosDeckGamePlayManager.CallLiar))]
    private static void ChaosLiar(ChaosDeckGamePlayManager __instance)
    {
        try { if (__instance != null && __instance.LiarCalled) HiddenThrow.Reveal(HiddenThrow.Table.Chaos, "liar call"); }
        catch (Exception e) { Plugin.Log.LogError($"{Fair.Tag} hidden throw: {e.Message}"); }
    }
}
