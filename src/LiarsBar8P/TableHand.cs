using System;

namespace LiarsBar8P;

/// <summary>
/// One seat's cards, whichever deck game is being played.
///
/// Liar's Deck and the Chaos deck variant are two different games with two different
/// components bolted to the players - <c>DeckGameplay</c> and <c>ChaosDeckGameplay</c> - and
/// they do not share a base class, so nothing can be asked of both. They do, however, offer
/// the same four things under the same names: the cards in hand, whether the player is
/// holding them, throwing, and calling liar.
///
/// Everything that drives a seat went through <c>DeckGameplay</c> directly, which is why the
/// test harness could only ever play Liar's Deck: in the Chaos deck the component is not
/// there, the lookup returns null, and every seat quietly does nothing. Three of the deck
/// variants a player can pick from the lobby arrows are the first game and one is the
/// second, so "tested Liar's Deck" left a quarter of that menu untried.
/// </summary>
internal sealed class TableHand
{
    private readonly DeckGameplay _deck;
    private readonly ChaosDeckGameplay _chaos;

    private TableHand(DeckGameplay deck, ChaosDeckGameplay chaos)
    {
        _deck = deck;
        _chaos = chaos;
    }

    /// <summary>The seat's hand, or null if this player is not playing a deck game at all.</summary>
    internal static TableHand For(PlayerStats p)
    {
        try
        {
            if (p == null) return null;

            var deck = p.GetComponent<DeckGameplay>();
            if (deck != null) return new TableHand(deck, null);

            var chaos = p.GetComponent<ChaosDeckGameplay>();
            if (chaos != null) return new TableHand(null, chaos);
        }
        catch { }
        return null;
    }

    internal bool IsChaosDeck => _chaos != null;

    /// <summary>The card values still in hand. Never null once a hand has been dealt.</summary>
    internal Il2CppSystem.Collections.Generic.List<int> Cards
    {
        get
        {
            try { return _deck != null ? _deck.cardTypes : _chaos.CardTypes; }
            catch { return null; }
        }
    }

    internal int Count
    {
        get { var c = Cards; return c != null ? c.Count : 0; }
    }

    internal bool Holding
    {
        get { try { return _deck != null ? _deck.HaveCards : _chaos.HaveCards; } catch { return false; } }
    }

    internal void Throw(Il2CppSystem.Collections.Generic.List<int> types, bool emptied)
    {
        if (_deck != null) _deck.RequestThrowCards(types, emptied);
        else _chaos.RequestThrowCards(types, emptied);
    }

    internal void CallLiar()
    {
        if (_deck != null) _deck.RequestCallLiar();
        else _chaos.RequestCallLiar();
    }

    /// <summary>The Liar's Deck component, for the few things only it has.</summary>
    internal DeckGameplay Deck => _deck;

    /// <summary>
    /// Whether this seat is playing the Devil variant - and could take the deal offered when
    /// a liar call resolves against them.
    /// </summary>
    internal bool IsDevilsDeal
    {
        get { try { return _deck != null && _deck.IsDevilsDealMode(); } catch { return false; } }
    }

    // ------------------------------------------------------- which game is being played

    internal enum Kind { None, Deck, ChaosDeck, Chaos, Poker, Texas, Dice, Spin, Roulette, Blorf, BlorfMatchMaking }

    /// <summary>
    /// Which mode's table is actually running.
    ///
    /// Asked of the people sitting down, never of the managers. Every mode's manager object is
    /// awake in the scene whatever is being played, so "is this manager active" identifies
    /// nothing at all - and answering it that way has now gone wrong three separate times, in
    /// three different classes, each looking perfectly reasonable: a run of Texas that filed
    /// itself as Liar's Deck, a Liar's Deck round where the turn was handed on with Texas's
    /// method and threw, and seats deciding in half a second because the Chaos deck's manager
    /// looked live in a game that was not the Chaos deck.
    ///
    /// Exactly one gameplay component is bolted onto a player, and it is the one for the mode
    /// being played. That is the discriminator, and this is the only place that decides it.
    /// </summary>
    internal static Kind Playing()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || m.Players == null) { _kindFor = null; return Kind.None; }

            if (ReferenceEquals(_kindFor, m) && _kind != Kind.None) return _kind;

            foreach (var p in m.Players)
            {
                if (p == null) continue;
                Kind found = Kind.None;

                if (p.GetComponent<DeckGameplay>() != null) found = Kind.Deck;
                else if (p.GetComponent<ChaosDeckGameplay>() != null) found = Kind.ChaosDeck;
                else if (p.GetComponent<ChaosGamePlay>() != null) found = Kind.Chaos;
                else if (p.GetComponent<PokerGamePlay>() != null) found = Kind.Poker;
                else if (p.GetComponent<TexasGamePlay>() != null) found = Kind.Texas;
                else if (p.GetComponent<DiceGamePlay>() != null) found = Kind.Dice;
                else if (p.GetComponent<SpinGamePlay>() != null) found = Kind.Spin;
                else if (p.GetComponent<RouletteGamePlay>() != null) found = Kind.Roulette;
                else if (p.GetComponent<BlorfGamePlay>() != null) found = Kind.Blorf;
                else if (p.GetComponent<BlorfGamePlayMatchMaking>() != null) found = Kind.BlorfMatchMaking;

                if (found == Kind.None) continue;

                _kindFor = m;
                _kind = found;
                return found;
            }
        }
        catch { }
        return Kind.None;
    }

    private static Manager _kindFor;
    private static Kind _kind = Kind.None;

    /// <summary>Whether a liar call is being resolved, asked of the manager actually running.</summary>
    internal static bool LiarCallInFlight()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) return false;

            switch (Playing())
            {
                case Kind.Deck:      return m.DeckGamePlayManager != null && m.DeckGamePlayManager.LiarCalled;
                case Kind.ChaosDeck: return m.ChaosDeckGame != null && m.ChaosDeckGame.LiarCalled;
            }
        }
        catch { }
        return false;
    }

    /// <summary>How many cards are face down on the table, for the log line before a call.</summary>
    internal static int CardsOnTable()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) return -1;

            switch (Playing())
            {
                case Kind.Deck:      return m.DeckGamePlayManager != null ? m.DeckGamePlayManager.CardsOnTable : -1;
                case Kind.ChaosDeck: return m.ChaosDeckGame != null ? m.ChaosDeckGame.CardsOnTable : -1;
            }
        }
        catch { }
        return -1;
    }
}
