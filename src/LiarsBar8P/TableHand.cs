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

    /// <summary>
    /// The cards really still in the hand: its card objects that are switched on, each with its
    /// place in the hand. The list of values above is the deal and is never shortened by a throw,
    /// and Liar's Deck 2 sends it back out by those places when a round keeps the hands.
    /// </summary>
    internal System.Collections.Generic.List<(int Index, Card Card)> InHand()
    {
        var held = new System.Collections.Generic.List<(int Index, Card Card)>();
        try
        {
            var cards = _deck != null ? _deck.Cards : _chaos.Cards;
            if (cards == null) return held;
            for (int i = 0; i < cards.Count; i++)
            {
                var go = cards[i];
                if (go == null || !go.activeSelf) continue;
                var card = go.GetComponent<Card>();
                if (card != null) held.Add((i, card));
            }
        }
        catch { }
        return held;
    }

    /// <summary>A card taken out of the hand on every machine, the way the server does it when a throw leaves the hand.</summary>
    internal void Hide(int index)
    {
        var at = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(1);
        at[0] = index;
        if (_deck != null) _deck.ServerHideCards(at);
        else _chaos.ServerChaosHideCards(at);
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
    ///
    /// The seats are the server's roster where there is one, and otherwise the seats in the scene:
    /// the roster is the server's own and empty on a client for the whole match, so reading only
    /// that answered None on every machine but the host's.
    /// </summary>
    internal static Kind Playing()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null) { _kindFor = null; return Kind.None; }

            if (ReferenceEquals(_kindFor, m) && _kind != Kind.None) return _kind;

            // A client searches the scene, at most five times a second until a seat carries a mode.
            bool roster = m.Players != null && m.Players.Count > 0;
            float now = UnityEngine.Time.unscaledTime;
            if (!roster && ReferenceEquals(_lookedFor, m) && now < _nextLook) return Kind.None;
            _lookedFor = m;
            _nextLook = now + 0.2f;

            foreach (var p in Seats(m))
            {
                Kind found = KindOf(p);
                if (found == Kind.None) continue;

                _kindFor = m;
                _kind = found;
                return found;
            }
        }
        catch { }
        return Kind.None;
    }

    private static Kind KindOf(PlayerStats p)
    {
        if (p == null) return Kind.None;
        if (p.GetComponent<DeckGameplay>() != null) return Kind.Deck;
        if (p.GetComponent<ChaosDeckGameplay>() != null) return Kind.ChaosDeck;
        if (p.GetComponent<ChaosGamePlay>() != null) return Kind.Chaos;
        if (p.GetComponent<PokerGamePlay>() != null) return Kind.Poker;
        if (p.GetComponent<TexasGamePlay>() != null) return Kind.Texas;
        if (p.GetComponent<DiceGamePlay>() != null) return Kind.Dice;
        if (p.GetComponent<SpinGamePlay>() != null) return Kind.Spin;
        if (p.GetComponent<RouletteGamePlay>() != null) return Kind.Roulette;
        if (p.GetComponent<BlorfGamePlay>() != null) return Kind.Blorf;
        if (p.GetComponent<BlorfGamePlayMatchMaking>() != null) return Kind.BlorfMatchMaking;
        return Kind.None;
    }

    /// <summary>
    /// The seats at this table as this machine has them: the server's roster, or on a client,
    /// which has none, the seats in the scene.
    /// </summary>
    internal static System.Collections.Generic.List<PlayerStats> Seats(Manager m)
    {
        var found = new System.Collections.Generic.List<PlayerStats>();
        if (m == null) return found;

        if (m.Players != null && m.Players.Count > 0)
        {
            foreach (var p in m.Players) if (p != null) found.Add(p);
            return found;
        }

        var all = UnityEngine.Object.FindObjectsOfType<PlayerStats>();
        if (all != null)
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null) found.Add(all[i]);
        return found;
    }

    private static Manager _kindFor;
    private static Kind _kind = Kind.None;
    private static Manager _lookedFor;
    private static float _nextLook;

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
