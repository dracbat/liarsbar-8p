using System;
using System.Collections.Generic;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// One line per seat, in every mode, saying whether that seat was actually dealt in.
///
/// Liar's Deck has had a detailed account of its own deal for a long time - who was handed
/// what, three seconds after the cards went out - and that account is what caught the deal
/// silently stopping at seat four. It reads <c>DeckGameplay</c> directly, so it says nothing
/// at all about Liar's Poker, Texas, Chaos or the Blorf tables, and those were the modes
/// carrying the same bug with nothing watching for it. "The caps and the seat ring are
/// mode-independent" was true and beside the point: the deal is not.
///
/// Every card mode keeps the same three things under a differently named component, so this
/// asks each in turn and reports whichever it finds. Dice and Spin deal no cards; for those
/// the seat list itself is the point, and the card columns are simply absent.
///
/// Only changes are logged. The picture is sampled continuously but a repeat of the last
/// line is dropped, so a match produces a handful of blocks - one per deal, one per
/// elimination - rather than one per sample.
/// </summary>
internal static class RoundCensus
{
    /// <summary>Long enough for the deal's own animation to finish before judging it.</summary>
    private const float SettleSeconds = 4f;

    private static float _next;
    private static string _last = "";
    private static Manager _for;

    internal static void Tick()
    {
        if (Plugin.Verbose == null || !Plugin.Verbose.Value) return;
        if (Time.time < _next) return;
        _next = Time.time + 1f;

        try
        {
            // Server-side: Manager.Players is the server's roster and is empty on a client.
            if (!Mirror.NetworkServer.active) return;

            var m = Manager.Instance;
            if (m == null || m.Players == null || m.Players.Count == 0) { Forget(); return; }
            if (!m.GameStarted) { Forget(); return; }

            // A fresh match starts the account over, so the first deal of it is always news.
            if (!ReferenceEquals(_for, m))
            {
                _for = m;
                _last = "";
                _named = false;
                _next = Time.time + SettleSeconds;
                return;
            }

            if (!_named)
            {
                _named = true;
                Plugin.Log.LogInfo($"[census] the table running this match is {LiveTable(m)}");
            }

            var lines = new List<string>();
            int seats = 0, dealt = 0, holding = 0, empty = 0;
            bool anyCards = false;

            foreach (var p in m.Players)
            {
                if (p == null) continue;
                seats++;

                var hand = Read(p);
                string state = p.Dead ? "OUT" : "in ";

                if (hand == null)
                {
                    lines.Add($"  seat {p.Slot} {state} '{p.PlayerName}'");
                    continue;
                }

                anyCards = true;
                if (!p.Dead)
                {
                    if (hand.Dealt > 0) dealt++;
                    if (hand.Holding) holding++;
                    if (hand.Dealt > 0 && hand.Showing == 0) empty++;
                }

                // The card values, but only for a developer.
                //
                // They are the thing most likely to come out wrong at eight, because a bigger
                // table is dealt from a rewritten deck with rescaled face thresholds - a count
                // of five says nothing about whether those five are five jokers. They are also
                // every player's hand, written in plain text to a file on the host's machine,
                // in a game whose entire subject is not knowing what anybody else is holding.
                // A host who alt-tabbed to their own log would be able to read the table. The
                // counts are what makes a bad round diagnosable and they give nothing away, so
                // those stay on for everybody and the values go behind developer mode.
                string cards = Dev.Enabled ? $" [{hand.Values}]" : "";
                lines.Add($"  seat {p.Slot} {state} '{p.PlayerName}': {hand.Dealt} dealt{cards}, " +
                          $"{hand.Showing} of {hand.Objects} card objects out, holding={hand.Holding}");
            }

            if (seats == 0) return;

            // A deal takes a second or two, and during it seats legitimately look half done.
            // Warning on the first sample that looks wrong produced an alarming line in every
            // healthy round, immediately contradicted by the next one - so a complaint has to
            // survive a few seconds of the same picture before it is worth making.
            bool unhappy = empty > 0 || (anyCards && dealt > 0 && holding < dealt);
            if (!unhappy) { _unhappySince = 0f; _complained = false; }
            else if (_unhappySince == 0f) _unhappySince = Time.time;

            string report = string.Join("\n", lines);
            bool changed = report != _last;
            if (changed) { _last = report; Plugin.Log.LogInfo($"[census] {seats} at the table:\n{report}"); }

            if (!unhappy || _complained || Time.time - _unhappySince < SettleSeconds) return;
            _complained = true;

            // The failure this exists to catch: cards recorded against a seat that never
            // received any. It is silent in the game - a dead coroutine leaves no trace -
            // so it has to be said out loud or it is not noticed until somebody cannot play.
            if (empty > 0)
                Plugin.Log.LogWarning(
                    $"[census] {empty} seat(s) were dealt cards they are not holding - the deal " +
                    "did not reach them");
            else
                Plugin.Log.LogWarning(
                    $"[census] {dealt - holding} of {dealt} dealt seats are still not marked as " +
                    "holding their cards");
        }
        catch (Exception e) { Plugin.Log.LogError($"[census] failed: {e.Message}"); }
    }

    private static void Forget()
    {
        _for = null;
        _last = "";
        _named = false;
        _unhappySince = 0f;
        _complained = false;
    }

    /// <summary>
    /// Which mode's table is actually running, rather than which one the lobby said.
    ///
    /// The two are not the same question. The lobby's game mode and its deck or dice variant
    /// together decide which manager wakes up, and the mapping is the game's business, not
    /// this mod's - a run labelled "Liar's Deck" can be played on the Chaos Deck table. When
    /// a mode is being signed off as working at eight players, what it was actually played on
    /// is the part worth recording.
    /// </summary>
    private static string LiveTable(Manager m)
    {
        try
        {
            var kind = TableHand.Playing();
            if (kind == TableHand.Kind.None) return "not identified - nobody at the table carries a mode component";

            // The deck variant too, because it is the whole difference between three of the
            // four games hiding behind "Liar's Deck" on the lobby arrows.
            if (kind == TableHand.Kind.Deck)
            {
                var deck = m.DeckGamePlayManager;
                return deck != null ? $"Liar's Deck, {deck.DeckMode} variant" : "Liar's Deck";
            }

            if (kind == TableHand.Kind.ChaosDeck) return "Chaos Deck";
            if (kind == TableHand.Kind.BlorfMatchMaking) return "Blorf (matchmaking)";
            return kind.ToString();
        }
        catch { }
        return "not identified - nobody at the table carries a mode component";
    }

    private static bool _named;
    private static float _unhappySince;
    private static bool _complained;

    private sealed class Hand
    {
        internal int Dealt;      // how many card values the server dealt this seat
        internal string Values;  // and what they were
        internal int Objects;    // card objects the seat owns
        internal int Showing;    // how many of them are switched on
        internal bool Holding;   // the flag the game gates acting on
    }

    /// <summary>The card values in a hand, in the order they were dealt.</summary>
    private static string Spell(Il2CppSystem.Collections.Generic.List<int> types)
    {
        try
        {
            if (types == null || types.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < types.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(types[i]);
            }
            return sb.ToString();
        }
        catch { return "?"; }
    }

    /// <summary>
    /// The seat's hand, whichever mode's component is carrying it.
    ///
    /// The seven card modes each declare their own <c>Cards</c>, <c>CardTypes</c> and
    /// <c>HaveCards</c> rather than inheriting them, so there is nothing to ask generically
    /// and they are asked one at a time. Null means this mode deals no cards, which is not a
    /// fault - Dice, Spin and Roulette are played without any.
    /// </summary>
    private static Hand Read(PlayerStats p)
    {
        try
        {
            var deck = p.GetComponent<DeckGameplay>();
            if (deck != null) return Build(deck.cardTypes, deck.Cards, deck.HaveCards);

            var chaosDeck = p.GetComponent<ChaosDeckGameplay>();
            if (chaosDeck != null) return Build(chaosDeck.CardTypes, chaosDeck.Cards, chaosDeck.HaveCards);

            var chaos = p.GetComponent<ChaosGamePlay>();
            if (chaos != null) return Build(chaos.CardTypes, chaos.Cards, chaos.HaveCards);

            var poker = p.GetComponent<PokerGamePlay>();
            if (poker != null) return Build(poker.CardTypes, poker.Cards, poker.HaveCards);

            // Texas holds its cards as its own component type rather than as bare objects.
            var texas = p.GetComponent<TexasGamePlay>();
            if (texas != null) return BuildTexas(texas);

            var blorf = p.GetComponent<BlorfGamePlay>();
            if (blorf != null) return Build(blorf.CardTypes, blorf.Cards, blorf.HaveCards);

            var blorfMm = p.GetComponent<BlorfGamePlayMatchMaking>();
            if (blorfMm != null) return Build(blorfMm.CardTypes, blorfMm.Cards, blorfMm.HaveCards);
        }
        catch { }
        return null;
    }

    private static Hand BuildTexas(TexasGamePlay texas)
    {
        var hand = new Hand
        {
            Dealt = texas.CardTypes != null ? texas.CardTypes.Count : 0,
            Values = Spell(texas.CardTypes),
            Holding = texas.HaveCards,
        };
        try
        {
            var cards = texas.Cards;
            if (cards == null) return hand;
            hand.Objects = cards.Count;
            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (card != null && card.gameObject != null && card.gameObject.activeSelf) hand.Showing++;
            }
        }
        catch { }
        return hand;
    }

    private static Hand Build(Il2CppSystem.Collections.Generic.List<int> types,
                              Il2CppSystem.Collections.Generic.List<GameObject> cards, bool holding)
    {
        var hand = new Hand { Dealt = types != null ? types.Count : 0, Values = Spell(types), Holding = holding };
        try
        {
            if (cards == null) return hand;
            hand.Objects = cards.Count;
            for (int i = 0; i < cards.Count; i++)
            {
                var go = cards[i];
                if (go != null && go.activeSelf) hand.Showing++;
            }
        }
        catch { }
        return hand;
    }
}
