using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// What each house rule does to the game, one class per rule, each patched on its own: a game
/// update that renames one method loses that one rule, and <see cref="HouseRules"/> then neither
/// switches it on nor names it in the lobby.
///
/// Every effect happens on the host and reaches the other copies through the game's own network
/// values and calls, so nothing here needs a client to have the rule. Each one starts by asking
/// <see cref="HouseRules.Host"/>, which is false on every copy that is not the table's server and
/// at every CLASSIC table, so a CLASSIC table runs exactly the game's own code. The only two that
/// act on a client are cosmetic: the chamber count shown over the other seats, and the drink that
/// kills looking like one.
///
/// None of them rewrites compiled code or the game's data, so there is nothing to put back: the
/// rules end when <see cref="HouseRules"/> says the match is over. The methods the turn order, the
/// deck size and the deal arrays read the bytes of are not patched by anything here.
/// </summary>
internal static class RuleEffects
{
    private const float GameClock = 30f;
    private const float FastClockSeconds = 15f;
    private const int HandSize = 3;
    private const int DevilCard = -1;
    private const int Dice = 3;
    private const int HeadStartPoints = 2;

    // The twelve raw values the chaos deck draws from: 1-5 are Kings, 6-10 Queens, 11 the chaos
    // card and 12 the joker. One King made a chaos card gives four, five, two and one.
    private const int KingCard = 1;
    private const int ChaosCard = 11;

    private static readonly HashSet<string> _said = new();

    /// <summary>A match has started or ended: every rule may say once again what it did.</summary>
    internal static void NewMatch() => _said.Clear();

    /// <summary>One line per rule per match, so a player's log shows the rule worked and no more.</summary>
    private static void Once(string rule, string what)
    {
        if (_said.Add(rule)) Plugin.Log.LogInfo($"[rules] {rule}: {what}");
    }

    /// <summary>Every time in developer mode, which is what the tests read; once otherwise.</summary>
    private static void Each(string rule, string what)
    {
        if (Dev.Enabled) Plugin.Log.LogInfo($"[rules] {rule}: {what}");
        else Once(rule, what);
    }

    private static void Failed(string rule, Exception e) =>
        Plugin.Log.LogWarning($"[rules] {rule} could not act: {e.Message}");

    // ------------------------------------------------------------------ 1. Fast Clock

    /// <summary>
    /// Every turn clock that starts at 30 starts at 15. All of them go through one network value
    /// on the manager. The game writes it every frame: 30 over and over while it waits for the
    /// turn to begin, then less each frame as it runs down. The running value is never exactly 30,
    /// so only the waiting value is changed, and the turn then runs down from 15. Said once a
    /// match: it happens every frame. Liar's Slots has no such rule: its 40 and 30 are read by each
    /// player's copy to time the spin.
    /// </summary>
    [HarmonyPatch]
    internal static class FastClock
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Manager), "set_NetworkCountDown")]
        private static void Shorter(ref float value)
        {
            try
            {
                if (value != GameClock || !HouseRules.Host(HouseRules.Rule.FastClock)) return;
                value = FastClockSeconds;
                Once("Fast Clock", $"the {GameClock:0} s turn clock starts at {FastClockSeconds:0} s");
            }
            catch (Exception e) { Failed("Fast Clock", e); }
        }
    }

    // ------------------------------------------------------------------ 2. Short Cylinder

    /// <summary>
    /// The bullet goes in one of four or three chambers instead of six. Drawn from the chamber
    /// under the hammer upward, never from zero, because the same network value is written again
    /// on a reload and after a host migration, and a bullet drawn behind the hammer would never fire.
    ///
    /// Each copy also writes the count over the other seats as the rule has it. A copy without the
    /// rule still says six there, which is the only thing it gets wrong.
    /// </summary>
    [HarmonyPatch]
    internal static class ShortCylinder
    {
        private const HouseRules.Rule Either = HouseRules.Rule.Cylinder4 | HouseRules.Rule.Cylinder3;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), "set_NetworkrevolverBullet")]
        private static void Deck(DeckGameplay __instance, ref int value)
        {
            try
            {
                if (HouseRules.Host(Either)) Draw(ref value, __instance.CurrentRevolver, "deck");
            }
            catch (Exception e) { Failed("Short Cylinder", e); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), "set_Networkrevolverbulllet")]
        private static void Chaos(ChaosDeckGameplay __instance, ref int value)
        {
            try
            {
                if (HouseRules.Host(Either)) Draw(ref value, __instance.Networkcurrentrevoler, "chaos");
            }
            catch (Exception e) { Failed("Short Cylinder", e); }
        }

        private static void Draw(ref int value, int fired, string where)
        {
            int n = HouseRules.Chambers;
            int left = n - fired;
            value = left <= 1 ? Math.Max(fired, 0) : fired + UnityEngine.Random.Range(0, left);
            // Never which chamber: the log would say when each seat's revolver fires.
            Each("Short Cylinder", $"{where} bullet drawn among the {Math.Max(left, 1)} chamber(s) left of {n} ({fired} fired)");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DeckGameplay), "UpdateCall")]
        private static void DeckText(DeckGameplay __instance)
        {
            try
            {
                if (!HouseRules.On(Either) || __instance.isOwned) return;
                Show(__instance.RoundText, __instance.CurrentRevolver);
            }
            catch (Exception e) { Failed("Short Cylinder", e); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaosDeckGameplay), "UpdateCall")]
        private static void ChaosText(ChaosDeckGameplay __instance)
        {
            try
            {
                if (!HouseRules.On(Either) || __instance.isOwned) return;
                Show(__instance.RoundText, __instance.Networkcurrentrevoler);
            }
            catch (Exception e) { Failed("Short Cylinder", e); }
        }

        // Every frame for every seat, so the words are made once and kept.
        private static readonly string[,] _texts = new string[8, 8];

        private static void Show(TMPro.TextMeshPro text, int fired)
        {
            int n = HouseRules.Chambers;
            if (text == null || fired < 0 || fired >= 8 || n >= 8) return;
            string want = _texts[fired, n] ??= $"({fired}|{n})";
            if (text.text != want) text.text = want;
        }
    }

    // ------------------------------------------------------------------ 3. Short Hand

    /// <summary>
    /// Liar's Deck Basic and Devil deal three cards, not five. The host's list of a hand's cards is
    /// cut before the hand is sent, and a hand already cut short is something the game handles:
    /// the spare card slots stay empty and selection skips them. The devil card is never the one
    /// cut. Deck 2 deals sevens that carry over, and is left alone.
    /// </summary>
    [HarmonyPatch]
    internal static class ShortHand
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DeckGameplay), nameof(DeckGameplay.RandomCards))]
        private static void Trim(Il2CppSystem.Collections.Generic.List<int> types)
        {
            try
            {
                if (types == null || types.Count <= HandSize || !HouseRules.Host(HouseRules.Rule.ShortHand)) return;

                var deck = Manager.Instance != null ? Manager.Instance.DeckGamePlayManager : null;
                if (deck != null && deck.DeckMode == DeckGamePlayManager.deckmode.Deck2) return;

                bool devil = false;
                for (int i = HandSize; i < types.Count; i++)
                    if (types[i] == DevilCard) devil = true;

                while (types.Count > HandSize) types.RemoveAt(types.Count - 1);
                if (devil) types[HandSize - 1] = DevilCard;

                Each("Short Hand", $"a hand of {HandSize} dealt" + (devil ? ", the devil card kept in it" : ""));
            }
            catch (Exception e) { Failed("Short Hand", e); }
        }
    }

    // ------------------------------------------------------------------ 4. Chaos Storm

    /// <summary>
    /// Two chaos cards in every twelve the chaos deck draws from, instead of one. The pool is
    /// built again each time it runs out, and each build gets one more.
    /// </summary>
    [HarmonyPatch]
    internal static class ChaosStorm
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ChaosDeckGamePlayManager), "BuildDeckPool")]
        private static void MoreChaos(ChaosDeckGamePlayManager __instance)
        {
            try
            {
                if (!HouseRules.Host(HouseRules.Rule.ChaosStorm)) return;
                var pool = __instance.serverDeckPool;
                if (pool == null) return;

                int at = pool.IndexOf(KingCard);
                if (at < 0) return;
                pool[at] = ChaosCard;

                int chaos = 0;
                for (int i = 0; i < pool.Count; i++)
                    if (pool[i] == ChaosCard) chaos++;
                Each("Chaos Storm", $"{chaos} chaos cards in a pool of {pool.Count}");
            }
            catch (Exception e) { Failed("Chaos Storm", e); }
        }
    }

    // ------------------------------------------------------------------ 5 and 6. Three Dice, One Sip

    /// <summary>
    /// Before each roll, on the host: three dice a player (the one place that reads the number is
    /// this roll, so the plain field is enough, and every copy hides the dice a player does not
    /// have by itself), and the highest bid follows. Worked out again every round, which also puts
    /// it right after somebody leaves and after a host migration.
    ///
    /// One Sip is in the same place: nobody has more than one life, so the first lost call is the
    /// last drink.
    /// </summary>
    [HarmonyPatch]
    internal static class DiceRolls
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceGamePlayManager), nameof(DiceGamePlayManager.AssignAllDiceValues))]
        private static void BeforeRoll(DiceGamePlayManager __instance)
        {
            try
            {
                bool three = HouseRules.Host(HouseRules.Rule.ThreeDice);
                bool sip = HouseRules.Host(HouseRules.Rule.OneSip);
                if (!three && !sip) return;

                var m = Manager.Instance;
                if (m == null || m.Players == null) return;

                int seats = 0, cut = 0;
                foreach (var p in m.Players)
                {
                    if (p == null) continue;
                    seats++;

                    if (three)
                    {
                        var hand = p.GetComponent<DiceGamePlay>();
                        if (hand != null) hand.TotalDices = Dice;
                    }

                    if (sip && p.Health > 1)
                    {
                        p.NetworkHealth = 1;
                        cut++;
                    }
                }

                if (three)
                {
                    __instance.NetworkMaxCount = Dice * seats;
                    Each("Three Dice", $"{Dice} dice each at {seats} seats, bids up to {Dice * seats}");
                }
                if (sip && cut > 0) Each("One Sip", $"{cut} seat(s) down to one life");
            }
            catch (Exception e) { Failed("Three Dice / One Sip", e); }
        }
    }

    /// <summary>
    /// Three Dice, at both dice tables, on every copy. Each seat's cup holds the game's five dice,
    /// and every frame the game copies the seat's dealt values onto them, one die after another,
    /// reading a value for each die in the cup. Dealt three, the fourth read is past the end, so it
    /// threw for every seat, every frame, on every copy, and whatever the game does after that copy
    /// never ran for the seat. So before that copy, a seat with more dice in its cup than it was
    /// dealt puts the spare ones away the way the game's own ReduceDice does: hidden, and taken off
    /// the seat's list of dice. A cup with no more dice than were dealt is left alone. Basic and
    /// Traditional deal alike (Traditional only counts ones as wild), so both need it: measured in
    /// batch M2 at four seats, one match each, Basic threw about 29,000 times and Traditional about
    /// 61,000 (the first design here said Traditional showed none).
    ///
    /// A die put away takes the two pictures of its face with it. The row of dice drawn over a seat
    /// as it shows them at a reveal is those pictures, kept apart from the die itself, so hiding the
    /// die left its pictures in the row: five dice in it, the last two the 1 they start on.
    /// </summary>
    [HarmonyPatch]
    internal static class ThreeDiceCups
    {
        /// <summary>The test driver's "threecups off": the game's own cups, for the count before this fix.</summary>
        internal static bool OffForTest { get; set; }

        /// <summary>The test driver's "threecups norow": the spare dice's pictures left where they are, as before.</summary>
        internal static bool RowOffForTest { get; set; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceGamePlay), "UpdateCall")]
        private static void Before(DiceGamePlay __instance)
        {
            if (OffForTest || !HouseRules.On(HouseRules.Rule.ThreeDice)) return;
            try
            {
                var dealt = __instance.DiceValues;
                var cup = __instance.dicerenders;
                if (dealt == null || cup == null) return;
                int have = dealt.Count;
                if (have <= 0 || cup.Count <= have) return;

                int spare = cup.Count - have;
                while (cup.Count > have)
                {
                    var die = cup[cup.Count - 1];
                    if (die != null)
                    {
                        die.gameObject.SetActive(false);
                        if (!RowOffForTest)
                        {
                            if (die.dice != null) die.dice.enabled = false;
                            if (die.dice2 != null) die.dice2.enabled = false;
                        }
                    }
                    cup.RemoveAt(cup.Count - 1);
                }
                Once("Three Dice cups", $"each cup keeps the {have} dice dealt, {spare} spare put away with their pictures");
            }
            // Every frame for every seat, so said once a match.
            catch (Exception e) { if (_said.Add("Three Dice cups failed")) Failed("Three Dice", e); }
        }
    }

    /// <summary>
    /// The drink that kills plays as the last drink, not the first. The game picks the animation
    /// from the count of drinks. Never set before a loss: one drink also gives the poisoned look.
    /// </summary>
    [HarmonyPatch]
    internal static class OneSipLook
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DiceGamePlayManager),
                      nameof(DiceGamePlayManager.InvokeUserCode_ReduceDice__NetworkConnectionToClient__PlayerStats))]
        private static void LastDrink()
        {
            try
            {
                if (!HouseRules.On(HouseRules.Rule.OneSip)) return;

                foreach (var hand in UnityEngine.Object.FindObjectsOfType<DiceGamePlay>())
                {
                    if (hand == null || !hand.isOwned || hand.drinkCount != 0) continue;
                    hand.NetworkdrinkCount = 1;
                    Each("One Sip", "the first drink plays as the last");
                    break;
                }
            }
            catch (Exception e) { Failed("One Sip", e); }
        }
    }

    // ------------------------------------------------------------------ 7. Hot Table

    /// <summary>
    /// Liar's Poker's stake is bullets, and every hand starts on one instead of none. So even a
    /// Coward's Fold has a chamber in eight that kills.
    /// </summary>
    [HarmonyPatch]
    internal static class HotTable
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TexasGamePlay), "set_NetworkBullets")]
        private static void OneBullet(ref int value)
        {
            try
            {
                if (value != 0 || !HouseRules.Host(HouseRules.Rule.HotTable)) return;
                value = 1;
                Each("Hot Table", "a stake of none starts on 1 bullet");
            }
            catch (Exception e) { Failed("Hot Table", e); }
        }
    }

    // ------------------------------------------------------------------ 8. No Coward's Fold

    /// <summary>
    /// Nobody folds in the first betting round. Each seat's one Coward's Fold is spent as the match
    /// begins; every copy then refuses the fold there by itself, and dims its button.
    /// </summary>
    [HarmonyPatch]
    internal static class NoCowardFold
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TexasGamePlayManager), nameof(TexasGamePlayManager.ResetRound))]
        private static void Spent(bool first)
        {
            try
            {
                if (!first || !HouseRules.Host(HouseRules.Rule.NoCowardFold)) return;
                var m = Manager.Instance;
                if (m == null || m.Players == null) return;

                int n = 0;
                foreach (var p in m.Players)
                {
                    var hand = p != null ? p.GetComponent<TexasGamePlay>() : null;
                    if (hand == null || hand.CowardFoldCount == 0) continue;
                    hand.NetworkCowardFoldCount = 0;
                    n++;
                }
                Each("No Coward's Fold", $"{n} seat(s) start the match without one");
            }
            catch (Exception e) { Failed("No Coward's Fold", e); }
        }
    }

    // ------------------------------------------------------------------ 9. Head Start

    /// <summary>
    /// Liar's Slots: every living seat starts each round on two points, set the way the game adds
    /// points, so the leaderboard and each player's own count agree.
    /// </summary>
    [HarmonyPatch]
    internal static class HeadStart
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LiarsSpinGameplayManager), nameof(LiarsSpinGameplayManager.SetFirstTurnRandomly))]
        private static void First() => Give("the first round");

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LiarsSpinGameplayManager), nameof(LiarsSpinGameplayManager.ResetRound))]
        private static void Next() => Give("a new round");

        private static void Give(string when)
        {
            try
            {
                if (!HouseRules.Host(HouseRules.Rule.HeadStart)) return;
                var m = Manager.Instance;
                if (m == null || m.Players == null) return;

                int n = 0;
                foreach (var p in m.Players)
                {
                    if (p == null || p.Dead) continue;
                    var hand = p.GetComponent<SpinGamePlay>();
                    if (hand == null) continue;
                    p.NetworkspinPoint = HeadStartPoints;
                    hand.SetPoint(HeadStartPoints);
                    n++;
                }
                Each("Head Start", $"{n} seat(s) on {HeadStartPoints} points for {when}");
            }
            catch (Exception e) { Failed("Head Start", e); }
        }
    }

    // ------------------------------------------------------------------ 10. Dead Man's Spin

    /// <summary>
    /// The dead spin draws a suit from seven and only Hearts lives, two chances in seven. Under
    /// this rule half the Hearts are turned to Spades, about one in seven. The suit array is in the
    /// game's data and stays as it is: the drawn suit is changed where the spin shows it, and in
    /// the spin's own record of it, which is what decides the shot afterwards. Only while that spin
    /// is running, so the call is never changed anywhere else.
    /// </summary>
    [HarmonyPatch]
    internal static class DeadMansSpin
    {
        private static LiarsSpinGameplayManager._CoDoSpinRoulette_d__93 _running;

        /// <summary>
        /// Developer test switch: every spin that drew a shot is turned to Hearts the same way, so
        /// a run shows on every spin, not one in seven, that the turned suit is what decides.
        /// </summary>
        internal static bool SpareForTest { get; set; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(LiarsSpinGameplayManager._CoDoSpinRoulette_d__93),
                      nameof(LiarsSpinGameplayManager._CoDoSpinRoulette_d__93.MoveNext))]
        private static void Enter(LiarsSpinGameplayManager._CoDoSpinRoulette_d__93 __instance) => _running = __instance;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LiarsSpinGameplayManager._CoDoSpinRoulette_d__93),
                      nameof(LiarsSpinGameplayManager._CoDoSpinRoulette_d__93.MoveNext))]
        private static void Leave() => _running = null;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpinGamePlay), nameof(SpinGamePlay.SetDeadSpin))]
        private static void Harder(ref LiarsSpinGameplayManager.TableCriterion table)
        {
            try
            {
                var spin = _running;
                if (spin == null || !HouseRules.Host(HouseRules.Rule.DeadMansSpin)) return;

                if (table != LiarsSpinGameplayManager.TableCriterion.Hearts)
                {
                    if (SpareForTest && Fair.TestsAllowed)
                    {
                        Each("Dead Man's Spin", $"the spin drew {table} and the test switch turned it to Hearts, so a survival");
                        table = LiarsSpinGameplayManager.TableCriterion.Hearts;
                        spin._shotCriterion_5__2 = table;
                        return;
                    }
                    Each("Dead Man's Spin", $"the spin drew {table}, a shot");
                    return;
                }
                if (UnityEngine.Random.value >= 0.5f)
                {
                    Each("Dead Man's Spin", "the spin drew Hearts and it stood, a survival");
                    return;
                }

                table = LiarsSpinGameplayManager.TableCriterion.Spades;
                spin._shotCriterion_5__2 = table;
                Each("Dead Man's Spin", "the spin drew Hearts and it was turned to Spades, a shot");
            }
            catch (Exception e) { Failed("Dead Man's Spin", e); }
        }
    }
}
