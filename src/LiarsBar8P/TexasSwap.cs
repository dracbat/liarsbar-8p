using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Liar's Texas card swaps at a table of five or more: a swap that never ends, a real face on
/// somebody else's card, and a deck one card short at eight.
///
/// The report was "after changing cards you can see the cards of the person next to you
/// instead of question marks; if you don't change cards it shows question marks". Every
/// machine hides the cards of seats it does not own the same way: <c>TexasCard.FixedUpdate</c>
/// puts the question-mark material (<c>BlurMaterial</c>) on them every physics tick - except
/// while that seat's <c>isSwitching</c> flag is up. Then it stops, and paints the card held up
/// during the swap with the face of the card being swapped out instead. That flag is only ever
/// lowered by the end of the swap animation or by the swapper's own "swap is over" message,
/// and nothing lowers it at the next deal. So a swap that never finishes leaves that seat's
/// cards unhidden for the rest of the match, on every machine. The host is where it shows: the
/// host paints every seat's real cards when it deals and when it swaps, and relies on the
/// hiding to cover them again. Above four players the seats beside the host are far enough
/// round that they are probably off its screen; if their animation is not played off screen,
/// the host's own end of the swap never runs and only the swapper's message can end it.
///
/// Three server-side guards make sure a swap always ends:
///   - a swap the game refuses (the option chosen is not one it offers - the betting round
///     moved on with the menu open, or the seat was dealt fewer options) is ended on the spot;
///     the game returns without a word and would otherwise leave the flag up for good;
///   - every deal lowers a flag left over from the last hand;
///   - a flag still up eight seconds after the swap went through, or after the seat's turn
///     ended, is lowered.
///
/// And every machine, once a frame and after everything else has painted, puts the question
/// mark back on every card of every seat it does not own. That closes the host's own flashes
/// (the new card painted on the swapper's card by the animation, the real faces painted at
/// each deal before the next physics tick hides them) and any swap that is stuck however it got
/// stuck. One thing is deliberately left alone: the card held up mid-swap still shows the card
/// being swapped out. That is the game's own rule, not a leak - the networked value it reads,
/// <c>CardValueForSwitch</c>, exists for nothing else, so the developers send the discarded
/// card to every machine on purpose. It happens at four players too, and the bigger table makes
/// it no easier to see: a neighbour at eight looks at the card more edge-on than at four (about
/// 67 degrees off its face rather than 45), only from closer. Hiding it above four would make
/// five-plus a different game from four. If testing shows that reveal is what players are
/// reporting, <see cref="ShowSwappedOutCard"/> is the switch.
///
/// The table cards in front of each seat are handled on the host only, because only the host
/// ever paints real cards on them before the showdown - at the deal and at the swap. For seats
/// the host does not own they are put back to whatever they showed before, which is exactly
/// what a joining player's machine shows. The showdown repaints them everywhere, host included.
///
/// Then the deck. Each seat is dealt two cards and four cards to switch to, and the table gets
/// five, all from one 52-card deck: 8 x 6 + 5 = 53. Every seat's options come out before the
/// table's, so it is the fifth table card that is drawn from an empty deck; that throws and
/// ends the deal without it, and the routine that turns the river over then asks for a fifth
/// card that is not there and throws too. Seven players need 47 and are fine. So when the deck
/// would run dry, cards to switch to are handed back: first from seats already out of the game,
/// who cannot swap, then one at a time from whoever holds the most. At eight that is one seat
/// offered three cards instead of four, in the first betting round only - the later rounds
/// offer three and two anyway. That seat's menu then moves between the three it has instead of
/// landing on a blank fourth.
///
/// Everything here does nothing at a table of four or fewer, which plays exactly as shipped.
/// </summary>
internal static class TexasSwap
{
    /// <summary>
    /// Keep the game's own swap reveal: the card held up mid-swap shows the card going back.
    /// See the class summary for why this stays on.
    /// </summary>
    private const bool ShowSwappedOutCard = true;

    // The deal's own numbers, read from watiforGiveCard: two cards each ("RandomCards(card1,
    // card2)"), four to switch to ("cmp r14d,4"), five for the table ("cmp r15d,5").
    private const int HoleCards = 2;
    private const int SwitchOptions = 4;
    private const int TableCards = 5;

    /// <summary>How long a finished swap may stay marked as in progress before it is ended.</summary>
    private const float StuckAfter = 8f;

    /// <summary>How often the cache is checked against the table: seats gone, seats added, whose they are.</summary>
    private const float RefreshEvery = 1f;

    /// <summary>
    /// How long to wait before looking again when the last look found no Liar's Texas here, or
    /// the table is known to be playing something else.
    /// </summary>
    private const float IdleEvery = 5f;

    /// <summary>
    /// How often the scene is searched again once the table has been found and nothing seems
    /// to have changed. Searching walks every loaded object, so it is not done every second -
    /// but a joining machine has no roster to say a seat is still on its way, so it is done.
    /// </summary>
    private const float SearchEvery = 10f;

    /// <summary>
    /// After the table is first found, or a seat comes or goes, it is searched every second
    /// for this long: players are still being spawned and their swap animations started.
    /// </summary>
    private const float SettleFor = 10f;

    /// <summary>The value the game uses for "no card is being swapped".</summary>
    private const int NoCard = -1;

    // ------------------------------------------------------------------ the table, cached

    /// <summary>One seat's cards, looked up when the scene is searched rather than once a frame.</summary>
    private sealed class Seat
    {
        internal TexasGamePlay Tg;
        internal PlayerStats Stats;
        internal int Slot = -1;

        /// <summary>The network identity on the cards' root - whose they are, as the game's own hiding asks it.</summary>
        internal Mirror.NetworkIdentity Id;

        /// <summary>
        /// Whether this machine owns the seat, asked once a second rather than once a frame -
        /// asking is a call into the game, and every call boxes the answer. Once owned it stays
        /// owned: a seat is never taken back from its player. The cost of asking only once a
        /// second is a seat seen in the instant before it is handed to its player, which has its
        /// owner's own cards covered for up to a second - at spawn, long before anything is dealt.
        /// </summary>
        internal bool Owned;
        internal bool OwnedForGood;

        /// <summary>Its cards have been read at least once (<see cref="Describe"/>).</summary>
        internal bool CardsRead;

        /// <summary>Extra reads spent looking for a held card that has not turned up.</summary>
        internal int Rereads;

        /// <summary>Every card the seat holds in its hand, except the one held up mid-swap.</summary>
        internal Renderer[] Hand = Array.Empty<Renderer>();

        /// <summary>Where each of those sits in the seat's <c>Cards</c> list, or -1 (developer check).</summary>
        internal int[] HandIndex = Array.Empty<int>();

        /// <summary>The card held up during the swap animation.</summary>
        internal Renderer Held;

        /// <summary>The two cards lying on the table in front of the seat.</summary>
        internal Renderer[] Table = Array.Empty<Renderer>();

        internal bool Warned;

        // developer check only
        internal bool WasSwitching;
        internal float SwitchingSince;
        internal bool Described;
    }

    private static readonly Dictionary<IntPtr, Seat> _bySeat = new();
    private static readonly List<Seat> _seats = new();
    private static readonly List<IntPtr> _gone = new();

    private static Manager _for;
    private static Material _blur;
    private static Material[] _faces = Array.Empty<Material>();
    private static float _nextRefresh;

    // When the scene is next searched, how many seats the Manager reported at the last search,
    // and until when to keep searching every second.
    private static float _nextSearch, _settleUntil;
    private static int _countAtSearch = -1;

    /// <summary>A Texas table is running on this machine and the cache describes it.</summary>
    internal static bool Live { get; private set; }

    /// <summary>More than four seats at it.</summary>
    private static bool _bigger;

    /// <summary>Server only: when each seat was first seen marked mid-swap after it should have ended.</summary>
    private static readonly Dictionary<IntPtr, float> _stuckSince = new();

    /// <summary>Hands dealt this session, for the test harness to vary who swaps.</summary>
    internal static int HandsDealt { get; private set; }
    private static float _lastDealAt = -999f;
    private static int _deckReports;

    private static readonly System.Random _rng = new();

    // ------------------------------------------------------------------ how big a table

    /// <summary>
    /// How many are seated: the larger of the count the table started with and the server's
    /// roster. <c>StartPlayerCount</c> alone is not enough - it is known to stay at four with
    /// more seated (see <c>JoinDiag</c>), and nothing corrects it for Texas. The roster is
    /// empty on a joining machine, which is why the start count is asked as well; it is a
    /// SyncVar, so a joining machine reads what the host wrote.
    /// </summary>
    private static int Seats(Manager m)
    {
        try
        {
            if (m == null) return 0;
            int start = m.StartPlayerCount;
            var players = m.Players;
            int seated = players != null ? players.Count : 0;
            return Math.Max(start, seated);
        }
        catch { return 0; }
    }

    private static bool Bigger(Manager m) => Seats(m) > Limits.VanillaPlayers;

    /// <summary>
    /// Unity's "is this object still there", without Unity's <c>==</c>. That operator is a call
    /// into the game's code, and every such call boxes the bool it returns - harmless once a
    /// second, garbage once a frame per card. The native pointer Unity itself tests is a field,
    /// and a field is read straight from memory.
    /// </summary>
    private static bool Alive(UnityEngine.Object o) => (object)o != null && o.m_CachedPtr != IntPtr.Zero;

    private static int SlotOf(TexasGamePlay tg)
    {
        try
        {
            var p = tg != null ? tg.GetComponent<PlayerStats>() : null;
            return p != null ? p.Slot : -1;
        }
        catch { return -1; }
    }

    // ================================================================== server: swaps end

    /// <summary>
    /// A seat was dealt its hand: lower any swap flag left over from the last hand, and make
    /// sure the deck still holds what the rest of the deal will draw.
    ///
    /// <c>RandomCards</c> is called once per seat from inside the deal routine, after that
    /// seat's two cards and its cards to switch to have been drawn and before the next seat's
    /// are. It is an ordinary method rather than the routine itself, so it can be patched; the
    /// routine is a coroutine, and a coroutine's body must never be detoured.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.RandomCards))]
    private static void Dealt(TexasGamePlay __instance)
    {
        try
        {
            if (__instance == null || !Mirror.NetworkServer.active) return;

            // The whole table is dealt in one frame, so a gap means a new hand.
            if (Time.time - _lastDealAt > 1f) HandsDealt++;
            _lastDealAt = Time.time;

            // Counted from the roster the deal itself loops over, which is complete by now -
            // not from the start count alone, which can lag at four with eight seated and
            // would leave the fifth table card to throw again.
            var m = Manager.Instance;
            if (!Bigger(m)) return;

            ClearLeftover(__instance);
            KeepTableCardsInDeck(m, __instance);
        }
        catch (Exception e) { Plugin.Log.LogError($"[texasswap] deal check failed: {e.Message}"); }
    }

    private static void ClearLeftover(TexasGamePlay tg)
    {
        bool marked = tg.isSwitching;
        if (marked) tg.NetworkisSwitching = false;

        // Lowered quietly when it is the only thing left: the end of the swap animation clears
        // the flag but not the card, so a stale card on its own is the normal case.
        if (tg.CardValueForSwitch != NoCard) tg.NetworkCardValueForSwitch = NoCard;
        _stuckSince.Remove(tg.Pointer);

        if (marked)
            Plugin.Log.LogInfo($"[texasswap] seat {SlotOf(tg)} was still marked mid-swap when the next hand " +
                               "was dealt - cleared, so its cards are hidden again");
    }

    /// <summary>
    /// The swap itself, on the server: end it if the game refused it, and keep the table cards
    /// on the host showing what they showed before.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_SwitchCmd__Int32__Int32))]
    private static void BeforeSwitch(TexasGamePlay __instance, out Material[] __state)
    {
        __state = null;
        try { __state = TableFacesToKeep(__instance); }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_SwitchCmd__Int32__Int32))]
    private static void AfterSwitch(TexasGamePlay __instance, int card, int selectedcard, Material[] __state)
    {
        try
        {
            if (__instance == null || !Mirror.NetworkServer.active) return;
            if (__state != null) PutTableFacesBack(__instance, __state);

            if (!Bigger(Manager.Instance)) return;
            if (__instance.Switched || !__instance.isSwitching) return;

            // Switched is set only once every check has passed, so up-but-not-switched means
            // the game turned the swap down - and nothing will ever end it.
            int dealt = 0;
            try { var sc = __instance.SwitchCards; dealt = sc != null ? sc.Count : 0; } catch { }
            End(__instance, $"its swap was refused (hand card {card}, option {selectedcard}; " +
                            $"{Offered(__instance)} offered this round, {dealt} dealt to it)");
        }
        catch (Exception e) { Plugin.Log.LogError($"[texasswap] swap check failed: {e.Message}"); }
    }

    /// <summary>The number of options the game offers this round, as its own range check works it out.</summary>
    private static int Offered(TexasGamePlay tg)
    {
        try
        {
            if (tg.Switched) return 0;
            int round = Manager.Instance.TexasGame.TexasRound;
            return round == 0 ? 4 : round == 1 ? 3 : 2;
        }
        catch { return -1; }
    }

    /// <summary>
    /// End a swap the way the swapper's own "swap is over" message does: flag down, no card.
    /// </summary>
    private static void End(TexasGamePlay tg, string why)
    {
        try { tg.UserCode_EndSwitchCmd(); }
        catch (Exception e) { Plugin.Log.LogWarning($"[texasswap] the game's own end of swap failed: {e.Message}"); }

        // That body reaches through the seat's animator for an object it does not use, and
        // throws before clearing anything if the path is broken. Make sure either way.
        try
        {
            if (tg.isSwitching) tg.NetworkisSwitching = false;
            if (tg.CardValueForSwitch != NoCard) tg.NetworkCardValueForSwitch = NoCard;
        }
        catch { }

        _stuckSince.Remove(tg.Pointer);
        Plugin.Log.LogInfo($"[texasswap] seat {SlotOf(tg)}: {why} - ended the swap so its cards are hidden again");
    }

    /// <summary>
    /// Server watchdog. A swap that went through finishes its animation in a second or two; a
    /// turn that ended cannot have a menu open. Either way, eight seconds later it is over.
    ///
    /// Not keyed on "swapped and still marked": a refused swap is marked without ever being
    /// swapped, and that is the case that sticks.
    /// </summary>
    private static void Watch()
    {
        float now = Time.time;
        for (int i = 0; i < _seats.Count; i++)
        {
            var s = _seats[i];
            var tg = s.Tg;
            if (!Alive(tg)) continue;

            IntPtr key = tg.Pointer;
            if (!tg.isSwitching) { _stuckSince.Remove(key); continue; }

            bool swapped = tg.Switched;
            bool turnOver = s.Stats != null && !s.Stats.HaveTurn;
            if (!swapped && !turnOver) { _stuckSince.Remove(key); continue; }   // menu open on its turn

            if (!_stuckSince.TryGetValue(key, out float since)) { _stuckSince[key] = now; continue; }
            if (now - since < StuckAfter) continue;

            End(tg, swapped
                ? $"still marked mid-swap {StuckAfter:0} s after the swap went through"
                : $"still marked mid-swap {StuckAfter:0} s after its turn ended");
        }
    }

    // ================================================================ server: the deck at eight

    /// <summary>
    /// Hand back cards to switch to until the deck holds what the deal draws next: the next
    /// seat's six, or after the last seat the table's five.
    ///
    /// Only seats already dealt this pass can give anything back. The ones still to come hold
    /// last hand's cards, which are back in the freshly shuffled deck, so taking from them would
    /// put a card on the table twice.
    /// </summary>
    private static void KeepTableCardsInDeck(Manager m, TexasGamePlay dealt)
    {
        var t = m.TexasGame;
        var deck = t != null ? t.Deste : null;
        var players = m.Players;
        if (deck == null || players == null) return;

        // The most the deal ever draws next is one seat's six or the table's five; a deck that
        // covers both needs nothing worked out.
        if (deck.Count >= Math.Max(HoleCards + SwitchOptions, TableCards)) return;

        int index = -1;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p == null) continue;
            var tg = p.GetComponent<TexasGamePlay>();
            if (tg != null && tg.Pointer == dealt.Pointer) { index = i; break; }
        }
        if (index < 0) return;

        bool last = index == players.Count - 1;
        int next = last ? TableCards : HoleCards + SwitchOptions;
        int shortBy = next - deck.Count;
        if (shortBy <= 0) return;

        var from = new List<(TexasGamePlay Tg, int Slot, bool Out)>();
        for (int i = 0; i <= index; i++)
        {
            var p = players[i];
            if (p == null) continue;
            var tg = p.GetComponent<TexasGamePlay>();
            if (tg != null) from.Add((tg, p.Slot, p.Dead));
        }

        var gaveBy = new SortedDictionary<int, int>();
        int given = 0;
        while (given < shortBy)
        {
            int best = -1, bestCount = 0, ties = 0;
            bool bestOut = false;
            for (int k = 0; k < from.Count; k++)
            {
                var sc = from[k].Tg.SwitchCards;
                int c = sc != null ? sc.Count : 0;
                if (c == 0) continue;

                // Out of the game first - they cannot swap at all. Then whoever holds the most,
                // so a shortfall is spread across seats rather than piled on one; ties are drawn
                // at random so it is not always the same seat.
                bool o = from[k].Out;
                bool better = best < 0 || (o && !bestOut) || (o == bestOut && c > bestCount);
                if (better) { best = k; bestCount = c; bestOut = o; ties = 1; }
                else if (o == bestOut && c == bestCount && _rng.Next(++ties) == 0) best = k;
            }
            if (best < 0) break;

            var take = from[best].Tg.SwitchCards;
            int at = take.Count - 1;                 // the last option: only the first betting round offers it
            int card = take[at];
            take.RemoveAt(at);
            deck.Add(card);
            given++;

            int slot = from[best].Slot;
            gaveBy[slot] = gaveBy.TryGetValue(slot, out int n) ? n + 1 : 1;
        }

        if (given > 0 && (_deckReports++ < 3 || (Plugin.Verbose != null && Plugin.Verbose.Value)))
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in gaveBy)
            {
                if (sb.Length > 0) sb.Append(", ");
                int left = 0;
                foreach (var f in from) if (f.Slot == kv.Key) { left = f.Tg.SwitchCards.Count; break; }
                sb.Append($"seat {kv.Key} gave {kv.Value} back and now has {left} to switch to");
            }
            Plugin.Log.LogInfo($"[texasdeck] {players.Count} seats need {players.Count * (HoleCards + SwitchOptions) + TableCards} " +
                               $"cards from a deck of 52 - {sb}, so the deal can finish" +
                               (last ? " with all five table cards" : ""));
        }

        if (given < shortBy)
            Plugin.Log.LogWarning($"[texasdeck] the deck is still {shortBy - given} short with nothing left to hand " +
                                  "back - the deal will stop before the table is complete");
    }

    /// <summary>
    /// The options the switch menu moves between, cut to what the seat actually holds.
    ///
    /// The menu's left and right keys wrap round this count, and the game works it out from
    /// the betting round alone - four, three, two. A seat that handed one back at the deal
    /// would otherwise be able to land on an empty fourth slot, and choosing it is refused by
    /// the server. Runs on the swapper's own machine every frame its menu is open; at four
    /// players, and whenever nothing was handed back, it changes nothing.
    ///
    /// Every frame, so the table size is the cached one (<see cref="Refresh"/>) and nothing is
    /// asked of the game until the table is known to be bigger than four - a four-player table
    /// pays one check of a cached flag.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.GetAvailableSwitchCount))]
    private static void OptionsOffered(TexasGamePlay __instance, ref int __result)
    {
        try
        {
            if (__result <= 1 || !_bigger || (object)__instance == null) return;
            var sc = __instance.SwitchCards;
            int held = (object)sc != null ? sc.Count : 0;
            if (held > 0 && held < __result) __result = held;
        }
        catch { }
    }

    // ================================================================= host: the table cards

    /// <summary>
    /// The host paints the real cards on the table cards in front of every seat when it deals
    /// and when a seat swaps. Every other machine never learns those cards until the showdown,
    /// when they are painted everywhere at once. So on the host, for seats it does not own, the
    /// table cards are put back to what they were - which is what everybody else is looking at.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_SetCardsCmd))]
    private static void BeforeDeal(TexasGamePlay __instance, out Material[] __state)
    {
        __state = null;
        try { __state = TableFacesToKeep(__instance); }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TexasGamePlay), nameof(TexasGamePlay.UserCode_SetCardsCmd))]
    private static void AfterDeal(TexasGamePlay __instance, Material[] __state)
    {
        try { if (__state != null) PutTableFacesBack(__instance, __state); }
        catch (Exception e) { Plugin.Log.LogError($"[texasswap] table card check failed: {e.Message}"); }
    }

    private static Material[] TableFacesToKeep(TexasGamePlay tg)
    {
        if (tg == null || !Mirror.NetworkServer.active || tg.isOwned) return null;
        if (!Bigger(Manager.Instance)) return null;

        var masa = tg.CardsMasa;
        if (masa == null || masa.Count == 0) return null;

        var kept = new Material[masa.Count];
        for (int i = 0; i < masa.Count; i++)
        {
            var go = masa[i];
            var r = go != null ? go.GetComponent<Renderer>() : null;
            kept[i] = r != null ? r.sharedMaterial : null;
        }
        return kept;
    }

    private static void PutTableFacesBack(TexasGamePlay tg, Material[] kept)
    {
        var masa = tg.CardsMasa;
        if (masa == null) return;
        for (int i = 0; i < masa.Count && i < kept.Length; i++)
        {
            if (kept[i] == null) continue;
            var go = masa[i];
            var r = go != null ? go.GetComponent<Renderer>() : null;
            if (r != null) r.sharedMaterial = kept[i];
        }
    }

    // ============================================================ every machine: the cache

    /// <summary>Four times a second, from <see cref="ModTicker"/>.</summary>
    internal static void Tick()
    {
        Spawn();

        if (Time.time >= _nextRefresh)
        {
            _nextRefresh = Time.time + RefreshEvery;
            try { Refresh(); }
            catch (Exception e) { Live = false; Failed("reading the table", e); }
        }

        if (Live && _bigger && Mirror.NetworkServer.active)
        {
            // Caught here rather than by the ticker, which would log a lasting fault four times
            // a second. Failed stops after a handful of lines.
            try { Watch(); }
            catch (Exception e) { Failed("the swap watchdog", e); }
        }
    }

    private static bool _spawned;

    /// <summary>
    /// The per-frame part needs a LateUpdate of its own: the mod's ticker runs four times a
    /// second in Update, before the animation events and the late network messages that paint
    /// the faces this has to cover.
    /// </summary>
    private static void Spawn()
    {
        if (_spawned) return;
        _spawned = true;
        try
        {
            Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<TexasCardGuard>();
            var go = new GameObject("LiarsBar8P_TexasCardGuard");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<TexasCardGuard>();
        }
        catch (Exception e) { Plugin.Log.LogError($"[texasswap] card guard not attached: {e.Message}"); }
    }

    private static void Forget()
    {
        Drop();
        _for = null;
        _blur = null;
        _faces = Array.Empty<Material>();
        _nextSearch = 0f;
        _settleUntil = 0f;
        _countAtSearch = -1;
        ForgetChecks();
    }

    /// <summary>No Texas table here (any more): nothing to hide, watch or trim.</summary>
    private static void Drop()
    {
        Live = false;
        _bigger = false;
        _bySeat.Clear();
        _seats.Clear();
        _stuckSince.Clear();
    }

    /// <summary>
    /// Keep the cache true to the table, once a second, as cheaply as that can be done.
    ///
    /// The Texas players are found by searching the scene rather than the server's roster,
    /// which is empty on a joining player's machine; a seat's own component is what carries a
    /// gameplay component in Texas and nothing else. Searching walks every loaded object, so it
    /// is done only when there is reason to: a table known to be playing another mode is never
    /// searched, one where nothing was found is searched every few seconds, and a table already
    /// found is searched again when a seat has gone, the seat count has changed, while it is
    /// still settling, or every ten seconds as a backstop.
    ///
    /// Each seat's cards are read only where something uses them - above four seats, where they
    /// are hidden, or in developer mode, where they are checked. Whose each seat is, which the
    /// hiding needs every frame, is asked here rather than there.
    /// </summary>
    private static void Refresh()
    {
        Manager m = null;
        try { m = Manager.Instance; } catch { }
        if (m == null) { if ((object)_for != null) Forget(); return; }
        if (!ReferenceEquals(m, _for)) { Forget(); _for = m; }

        float now = Time.time;

        // One Manager plays one mode for as long as it lives, and the mode is read off the
        // seats. Asked of the server's roster, so on a joining machine it says nothing and the
        // search below decides.
        var kind = TableHand.Playing();
        if (kind != TableHand.Kind.None && kind != TableHand.Kind.Texas)
        {
            if (_seats.Count > 0 || Live) Drop();
            _nextRefresh = now + IdleEvery;
            return;
        }

        var t = m.TexasGame;
        if (t == null) { Drop(); return; }

        _blur = t.BlurMaterial;
        if (_faces.Length == 0 && t.CardMaterials != null && t.CardMaterials.Count > 0)
        {
            var faces = new Material[t.CardMaterials.Count];
            for (int i = 0; i < faces.Length; i++) faces[i] = t.CardMaterials[i];
            _faces = faces;
        }

        int count = Seats(m);
        bool search;
        if (_seats.Count == 0) search = kind == TableHand.Kind.Texas || now >= _nextSearch;
        else search = now >= _nextSearch || now < _settleUntil || count != _countAtSearch || AnyGone();

        if (search)
        {
            var found = UnityEngine.Object.FindObjectsOfType<TexasGamePlay>();
            _countAtSearch = count;
            if (found == null || found.Length == 0)
            {
                Drop();
                _nextSearch = now + IdleEvery;
                return;
            }
            _nextSearch = now + SearchEvery;
            if (Rebuild(found)) _settleUntil = now + SettleFor;
        }

        // The seats in the scene are a seated count of their own, needing no SyncVar.
        _bigger = Math.Max(count, _seats.Count) > Limits.VanillaPlayers;

        bool cards = _bigger || Dev.Enabled;
        for (int i = 0; i < _seats.Count; i++)
        {
            var s = _seats[i];
            ReadOwner(s);
            if (Alive(s.Stats)) s.Slot = s.Stats.Slot;

            // Read on every search, the first time they are needed, and - for a seat whose
            // held card has not turned up yet - every second for a while, since the swap
            // animation's component only names it once it has started.
            if (!cards) continue;
            bool again = !s.CardsRead || search || (!Alive(s.Held) && s.Rereads < MaxRereads);
            if (!again) continue;
            if (s.CardsRead && !search) s.Rereads++;
            Describe(s);
        }

        Live = Alive(_blur) && _seats.Count > 0;
    }

    /// <summary>How many extra once-a-second reads a seat gets while its held card is missing.</summary>
    private const int MaxRereads = 30;

    private static bool AnyGone()
    {
        for (int i = 0; i < _seats.Count; i++)
            if (!Alive(_seats[i].Tg)) return true;
        return false;
    }

    /// <summary>
    /// Bring the seat list in line with a search. Returns true when a seat came or went.
    /// </summary>
    private static bool Rebuild(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TexasGamePlay> found)
    {
        _gone.Clear();
        foreach (var key in _bySeat.Keys) _gone.Add(key);

        bool changed = false;
        _seats.Clear();
        for (int i = 0; i < found.Length; i++)
        {
            var tg = found[i];
            if (!Alive(tg)) continue;
            IntPtr key = tg.Pointer;
            _gone.Remove(key);
            if (!_bySeat.TryGetValue(key, out var s))
            {
                s = new Seat();
                _bySeat[key] = s;
                Identify(s, tg);
                changed = true;
            }
            _seats.Add(s);
        }

        foreach (var key in _gone)
        {
            _bySeat.Remove(key);
            _stuckSince.Remove(key);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Whose the seat is. Sticky once owned; an answer that throws counts as owned for now,
    /// leaving the seat to the game's own hiding until the next look.
    /// </summary>
    private static void ReadOwner(Seat s)
    {
        if (s.OwnedForGood) { s.Owned = true; return; }
        try
        {
            bool owned = (object)s.Id != null ? s.Id.isOwned : Alive(s.Tg) && s.Tg.isOwned;
            s.Owned = owned;
            s.OwnedForGood = owned;
        }
        catch { s.Owned = true; }   // unsure: leave it to the game
    }

    /// <summary>The seat itself: its player, its slot, and the network identity its cards hang under.</summary>
    private static void Identify(Seat s, TexasGamePlay tg)
    {
        s.Tg = tg;
        s.Stats = tg.GetComponent<PlayerStats>();
        s.Slot = s.Stats != null ? s.Stats.Slot : -1;

        // The same test the game's own hiding uses: the network identity on the card's root.
        var root = tg.transform.root;
        var id = root != null ? root.GetComponent<Mirror.NetworkIdentity>() : null;
        s.Id = id;
        if (id == null && !s.Warned)
        {
            s.Warned = true;
            Plugin.Log.LogWarning($"[texasswap] seat {s.Slot}'s cards are not under a networked object - " +
                                  "the game's own hiding cannot tell whose they are");
        }
    }

    /// <summary>The seat's cards: in hand, held up mid-swap, and on the table in front of it.</summary>
    private static void Describe(Seat s)
    {
        var tg = s.Tg;
        if (!Alive(tg)) return;
        Identify(s, tg);
        s.CardsRead = true;

        var table = new List<Renderer>();
        var onTable = new HashSet<IntPtr>();
        var masa = tg.CardsMasa;
        if (masa != null)
            for (int i = 0; i < masa.Count; i++)
            {
                var go = masa[i];
                var r = go != null ? go.GetComponent<Renderer>() : null;
                table.Add(r);
                if (go != null) onTable.Add(go.Pointer);
            }

        // The held card, asked of the swap animation's own component, which is what the game
        // paints it through. Marked as the held card only once that component has started, so
        // the flag on the card is the fallback rather than the test.
        Renderer held = null;
        try
        {
            var events = tg._bulletevents;
            if (events == null && tg.animator != null) events = tg.animator.GetComponent<BulletEvents>();
            if (events != null && events.HandCard != null) held = events.HandCard;
        }
        catch { }

        var inHand = tg.Cards;
        var hand = new List<Renderer>();
        var handIndex = new List<int>();
        foreach (var c in tg.GetComponentsInChildren<TexasCard>(true))
        {
            if (c == null) continue;
            var r = c.GetComponent<Renderer>();
            if (r == null) continue;
            if (held != null && r.Pointer == held.Pointer) continue;
            if (c.isHandCard) { if (held == null) held = r; continue; }

            // The table cards are revealed at the showdown by painting them; never cover those.
            if (onTable.Contains(c.gameObject.Pointer)) continue;

            int index = -1;
            if (inHand != null)
                for (int i = 0; i < inHand.Count; i++)
                    if (inHand[i] != null && inHand[i].Pointer == c.Pointer) { index = i; break; }

            hand.Add(r);
            handIndex.Add(index);
        }

        s.Hand = hand.ToArray();
        s.HandIndex = handIndex.ToArray();
        s.Held = held;
        s.Table = table.ToArray();
    }

    // =========================================================== every machine: hide them

    /// <summary>
    /// Once a frame, after every animation event, coroutine and network message of the frame
    /// has had its turn: the question mark on every card of every seat this machine does not
    /// own, except the game's own swap reveal on the held card.
    ///
    /// Nothing here may allocate, at up to two dozen cards a frame. Whose a seat is was asked
    /// once a second (<see cref="Refresh"/>); whether an object is still there is read off its
    /// native pointer rather than asked with Unity's <c>==</c>, which boxes its answer; and the
    /// fields read here - the swap flag, the card going back - are read straight from memory.
    /// Each card's material is read before it is written and written only when it is wrong:
    /// the wrapper for a material already seen is reused rather than made again, and writing
    /// the material a card already has would still mark it changed for the renderer every frame.
    /// </summary>
    internal static void Enforce()
    {
        if (!_bigger) return;
        var blur = _blur;
        if (!Alive(blur)) return;
        IntPtr blurAt = blur.Pointer;

        for (int s = 0; s < _seats.Count; s++)
        {
            var seat = _seats[s];
            if (seat.Owned) continue;
            var tg = seat.Tg;
            if (!Alive(tg)) continue;

            var hand = seat.Hand;
            for (int i = 0; i < hand.Length; i++) Paint(hand[i], blur, blurAt);

            var held = seat.Held;
            if (!Alive(held)) continue;

            int going = tg.CardValueForSwitch;
            Material face = ShowSwappedOutCard && tg.isSwitching && going >= 1 && going <= _faces.Length
                ? _faces[going - 1] : null;
            if (Alive(face)) Paint(held, face, face.Pointer);
            else Paint(held, blur, blurAt);
        }
    }

    /// <summary>Put <paramref name="want"/> on a card unless it is already there.</summary>
    private static void Paint(Renderer r, Material want, IntPtr wantAt)
    {
        if (!Alive(r)) return;
        var now = r.sharedMaterial;
        if ((object)now != null && now.Pointer == wantAt) return;
        r.sharedMaterial = want;
    }

    private static int _failures;

    internal static void Failed(string what, Exception e)
    {
        if (_failures++ < 5) Plugin.Log.LogError($"[texasswap] {what} failed: {e.Message}");
    }

    // ===================================================== developer: watch for real faces

    // Which face each card object was last reported showing, so each offence is told once.
    private static readonly Dictionary<long, int> _showing = new();
    private static readonly Dictionary<IntPtr, int> _kind = new();
    private static readonly Dictionary<string, int> _faceByName = new();
    private static string _blurName;
    private static float _nextScan, _hotUntil;
    private static int _reports;
    private static bool _toldOnce;
    private const int MaxReports = 150;

    private static void ForgetChecks()
    {
        _showing.Clear();
        _kind.Clear();
        _faceByName.Clear();
        _blurName = null;
        _reports = 0;
        _toldOnce = false;
    }

    /// <summary>
    /// Developer mode only, on every machine: report any card of a seat this machine does not
    /// own that has a card face on it while it is switched on, outside the showdown.
    ///
    /// Judged the way the verification of this bug said it has to be. Materials are compared by
    /// name with " (Instance)" taken off, because the swap animation copies materials through
    /// the getter that clones them. "Visible" is never <c>Renderer.isVisible</c>, which is true
    /// for a shadow pass: each report says whether the card is inside the local camera's view
    /// and which way it faces it, and takes a screenshot if those are switched on. Runs after
    /// the hiding above, so it reports what is actually drawn.
    ///
    /// Also says when each seat's swap flag goes up and down, so how long a swap stays marked -
    /// on the host against on a joining machine - can be read straight off the log.
    /// </summary>
    internal static void Watchful()
    {
        if (!Dev.Enabled || !Live) return;

        float now = Time.time;
        bool any = false;
        for (int i = 0; i < _seats.Count; i++)
        {
            var s = _seats[i];
            var tg = s.Tg;
            if (!Alive(tg)) continue;

            bool on = tg.isSwitching;
            any |= on;
            if (on == s.WasSwitching) continue;
            s.WasSwitching = on;

            if (on)
            {
                s.SwitchingSince = now;
                Leak($"seat {s.Slot} swap flag UP (card going back {tg.CardValueForSwitch}, " +
                     $"{(s.Owned ? "mine" : "not mine")}) [{Role()}]");
            }
            else
                Leak($"seat {s.Slot} swap flag down after {now - s.SwitchingSince:0.00} s " +
                     $"(swapped={tg.Switched}) [{Role()}]");
        }

        if (any) _hotUntil = now + 2f;
        if (now < _hotUntil || now >= _nextScan)
        {
            _nextScan = now + 0.25f;
            Scan();
        }
    }

    private static string Role() => Mirror.NetworkServer.active ? "host" : "client";

    /// <summary>Tagged on its own so one grep finds every line of the check.</summary>
    private static void Leak(string message, bool warn = false)
    {
        if (!Dev.Enabled) return;
        if (warn) Plugin.Log.LogWarning("[texasleak] " + message);
        else Plugin.Log.LogInfo("[texasleak] " + message);
    }

    private static void Scan()
    {
        var mgr = Manager.Instance != null ? Manager.Instance.TexasGame : null;
        if (mgr == null) return;
        TellOnce();
        if (!_toldOnce) return;

        bool showdown = false;
        try { showdown = mgr.ShowdownActive; } catch { }

        for (int i = 0; i < _seats.Count; i++)
        {
            var s = _seats[i];
            if (s.Owned) continue;
            var tg = s.Tg;
            if (!Alive(tg)) continue;

            if (!s.Described)
            {
                s.Described = true;
                try
                {
                    // Culling decides whether the swap animation - and the end of the swap it
                    // triggers - plays at all for a seat that is off this machine's screen.
                    string cull = tg.animator != null ? tg.animator.cullingMode.ToString() : "no animator";
                    Leak($"seat {s.Slot}: {s.Hand.Length} hand card(s), held card " +
                         $"{(s.Held != null ? "found" : "MISSING")}, {s.Table.Length} table card(s), " +
                         $"animator culling {cull} [{Role()}]");
                }
                catch { }
            }

            bool reveal = showdown;
            try { reveal |= tg.Reveal; } catch { }
            if (reveal) continue;   // the showdown is when faces are meant to be seen

            for (int k = 0; k < s.Hand.Length; k++) Check(s, tg, 0, k, s.Hand[k], s.HandIndex[k]);
            Check(s, tg, 1, 0, s.Held, -1);
            for (int k = 0; k < s.Table.Length; k++) Check(s, tg, 2, k, s.Table[k], k);
        }
    }

    private static readonly string[] KindNames = { "hand card", "hand-held swap card", "table card" };

    private static void Check(Seat s, TexasGamePlay tg, int kind, int which, Renderer r, int cardIndex)
    {
        if (!Alive(r)) return;
        long key = ((long)(s.Slot & 0xFF) << 16) | ((long)kind << 8) | (long)(which & 0xFF);

        bool active = r.enabled && r.gameObject.activeInHierarchy;
        int face = active ? Classify(r.sharedMaterial) : 0;
        if (face <= 0) { _showing.Remove(key); return; }
        if (_showing.TryGetValue(key, out int was) && was == face) return;
        _showing[key] = face;

        if (_reports >= MaxReports) return;
        if (++_reports == MaxReports)
        {
            Leak($"{MaxReports} reports this match - no more until the next one", true);
            return;
        }

        bool switching = tg.isSwitching;
        int going = tg.CardValueForSwitch;
        string whose = WhoseFace(tg, kind, cardIndex, face, going, switching);

        Leak($"seat {s.Slot} {KindNames[kind]} {which} shows card {face}{whose} - isSwitching={switching}, " +
             $"CardValueForSwitch={going}, swapped={tg.Switched}; {Sight(r)} [{Role()}]", true);

        DevShots.Take($"texasleak-seat{s.Slot}-{(kind == 0 ? "hand" : kind == 1 ? "held" : "table")}");
    }

    /// <summary>Whether a face is the seat's real card, where this machine can know.</summary>
    private static string WhoseFace(TexasGamePlay tg, int kind, int cardIndex, int face, int going, bool switching)
    {
        if (kind == 1)
            return face == going && switching ? " (the card being swapped out - the game's own swap reveal)" : "";

        // Only the host knows another seat's cards; a joining machine is never told them.
        if (!Mirror.NetworkServer.active) return " (a joining machine cannot tell whether it is the real card)";
        try
        {
            var types = tg.CardTypes;
            if (types == null || cardIndex < 0 || cardIndex >= types.Count) return "";
            return types[cardIndex] == face ? " - its REAL card this hand" : $" (not its card this hand, which is {types[cardIndex]})";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Is the card inside the local camera's view, how far away, and which way is it turned?
    /// Which local axis a card's face points along is not known from the dumps, so both
    /// candidates are given: positive means that axis points at the camera.
    /// </summary>
    private static string Sight(Renderer r)
    {
        try
        {
            var cam = Camera.main;
            if (cam == null)
                foreach (var c in Camera.allCameras)
                    if (c != null && c.isActiveAndEnabled && c.targetTexture == null) { cam = c; break; }
            if (cam == null) return "no camera to judge by";

            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            bool inView = GeometryUtility.TestPlanesAABB(planes, r.bounds);

            var t = r.transform;
            Vector3 toCam = cam.transform.position - t.position;
            float dist = toCam.magnitude;
            if (dist > 0f) toCam /= dist;

            return $"{(inView ? "IN VIEW" : "out of view")} of camera '{cam.name}' at {dist:0.00} m, " +
                   $"forward.cam={Vector3.Dot(t.forward, toCam):0.00} up.cam={Vector3.Dot(t.up, toCam):0.00}";
        }
        catch (Exception e) { return $"view unknown ({e.Message})"; }
    }

    /// <summary>A card face's number (1-52), -1 for the question mark, 0 for anything else.</summary>
    private static int Classify(Material mat)
    {
        if (mat == null) return 0;
        IntPtr key = mat.Pointer;
        if (_kind.TryGetValue(key, out int known)) return known;

        string name = Bare(mat.name);
        int result = 0;
        if (_blurName != null && name == _blurName) result = -1;
        else if (_faceByName.TryGetValue(name, out int face)) result = face;

        _kind[key] = result;
        return result;
    }

    private static string Bare(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        const string clone = " (Instance)";
        while (name.EndsWith(clone, StringComparison.Ordinal)) name = name.Substring(0, name.Length - clone.Length);
        return name;
    }

    /// <summary>
    /// Learn the names of the question mark and the 52 faces, once a match, and say what they
    /// are - confirming the question mark really is <c>BlurMaterial</c> was an open question.
    /// </summary>
    private static void TellOnce()
    {
        if (_toldOnce || _blur == null || _faces.Length == 0) return;
        _toldOnce = true;
        _kind.Clear();
        try
        {
            _blurName = Bare(_blur.name);
            for (int i = 0; i < _faces.Length; i++)
                if (_faces[i] != null) _faceByName[Bare(_faces[i].name)] = i + 1;

            string tex = "-";
            try { tex = _blur.mainTexture != null ? _blur.mainTexture.name : "-"; } catch { }
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            Leak($"question mark material '{_blurName}' (texture '{tex}'), {_faceByName.Count} card faces, " +
                 $"scene '{scene}', table of {Seats(Manager.Instance)}, hiding above four " +
                 $"{(_bigger ? "ON" : "off")} [{Role()}]");
        }
        catch (Exception e) { Leak($"could not describe the card materials: {e.Message}", true); }
    }
}

/// <summary>
/// The once-a-frame half of <see cref="TexasSwap"/>. A component of its own because nothing the
/// game runs comes late enough in the frame: the faces it covers are painted by animation events,
/// coroutines and messages from other machines, all of which have run by the time LateUpdate does.
/// </summary>
internal sealed class TexasCardGuard : MonoBehaviour
{
    public TexasCardGuard(IntPtr ptr) : base(ptr) { }

    private void LateUpdate()
    {
        if (!TexasSwap.Live) return;

        try { TexasSwap.Enforce(); }
        catch (Exception e) { TexasSwap.Failed("hiding other seats' cards", e); }

        if (!Dev.Enabled) return;
        try { TexasSwap.Watchful(); }
        catch (Exception e) { TexasSwap.Failed("the developer card check", e); }
    }
}
