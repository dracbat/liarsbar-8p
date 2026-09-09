using System;

namespace LiarsBar8P;

/// <summary>
/// Writes down how long every mode's per-seat lists are, next to how many people are at the
/// table.
///
/// Resolving a liar call in Liar's Dice at eight players throws
/// <c>IndexOutOfRangeException</c>, and an IL2CPP build gives no stack for it. The choice is
/// between guessing which collection is too short and measuring, and guessing has a poor
/// record here: the deck's deal was chased through four different candidates before the real
/// one turned up, and each wrong guess was plausible enough to write a fix for.
///
/// The deeper problem is that this shape of bug is invisible until somebody plays far enough
/// into a mode to hit it. A list that ships with four entries because the game had four
/// players is fine right up to the moment something indexes it by a fifth seat, and then it
/// takes out whatever coroutine it was in - quietly, because the round simply stops rather
/// than saying anything. Liar's Deck's lists were found that way, one at a time, over several
/// releases. Every other mode has its own set and none of them had ever been looked at.
///
/// So this asks all of them at once. Every list each mode manager keeps, its length, and the
/// size of the table, printed once when a match starts. Anything that is no longer than the
/// game's own four seats and is shorter than the table is called out as a candidate - not as
/// a fault, because plenty of these are per-card or per-symbol and four is simply their right
/// length. It says which are worth looking at; it does not pretend to know.
///
/// This measures. It changes nothing.
/// </summary>
internal static class SeatLists
{
    private static Manager _reported;

    internal static void Tick()
    {
        try
        {
            var m = Manager.Instance;
            if (m == null || !m.GameStarted) { _reported = null; return; }
            if (ReferenceEquals(_reported, m)) return;

            var kind = TableHand.Playing();
            if (kind == TableHand.Kind.None) return;      // nobody carries a mode component yet

            _reported = m;
            int seats = AimRing.Seats(m);
            if (seats < 2) return;

            var sb = new System.Text.StringBuilder();
            var suspect = new System.Collections.Generic.List<string>();

            switch (kind)
            {
                case TableHand.Kind.Deck:
                {
                    var d = m.DeckGamePlayManager;
                    if (d == null) break;
                    Note(sb, suspect, seats, "LastRound", Len(() => d.LastRound.Count));
                    Note(sb, suspect, seats, "LastRoundSpotOn", Len(() => d.LastRoundSpotOn.Count));
                    Note(sb, suspect, seats, "CardIcons", Len(() => d.CardIcons.Count));
                    Note(sb, suspect, seats, "OrderSprtes", Len(() => d.OrderSprtes.Count));
                    Note(sb, suspect, seats, "devilsDealEffects", Len(() => d.devilsDealEffects.Count));
                    break;
                }

                case TableHand.Kind.ChaosDeck:
                {
                    var c = m.ChaosDeckGame;
                    if (c == null) break;
                    Note(sb, suspect, seats, "LastRound", Len(() => c.LastRound.Count));
                    Note(sb, suspect, seats, "CardIcons", Len(() => c.CardIcons.Count));
                    Note(sb, suspect, seats, "OpenCards", Len(() => c.OpenCards.Count));
                    break;
                }

                case TableHand.Kind.Dice:
                {
                    var d = m.DiceGame;
                    if (d == null) break;
                    Note(sb, suspect, seats, "turnTexts", Len(() => d.turnTexts.Count));
                    Note(sb, suspect, seats, "DiceIcons", Len(() => d.DiceIcons.Count));

                    var p = d.dicePanel;
                    if (p != null)
                    {
                        Note(sb, suspect, seats, "panel.diceList", Len(() => p.diceList.Count));
                        Note(sb, suspect, seats, "panel.diceList2", Len(() => p.diceList2.Count));
                        Note(sb, suspect, seats, "panel.dices", Len(() => p.dices.Count));
                    }
                    break;
                }

                case TableHand.Kind.Texas:
                {
                    var t = m.TexasGame;
                    if (t == null) break;
                    Note(sb, suspect, seats, "WinStats", Len(() => t.WinStats.Count));
                    Note(sb, suspect, seats, "BulletUI", Len(() => t.BulletUI.Count));
                    Note(sb, suspect, seats, "switchAnimators", Len(() => t.switchAnimators.Count));
                    Note(sb, suspect, seats, "SwitchCardsImages", Len(() => t.SwitchCardsImages.Count));
                    Note(sb, suspect, seats, "PokerTableItems", Len(() => t.PokerTableItems.Count));
                    Note(sb, suspect, seats, "RoundAnim", Len(() => t.RoundAnim.Count));
                    Note(sb, suspect, seats, "OpenCards", Len(() => t.OpenCards.Count));
                    break;
                }

                case TableHand.Kind.Spin:
                {
                    var s = m.SpinGame;
                    if (s == null) break;
                    Note(sb, suspect, seats, "revealSprites", Len(() => s.revealSprites.Count));
                    break;
                }
            }

            if (sb.Length == 0) return;

            Plugin.Log.LogInfo($"[seatlists] {kind} with {seats} at the table:{sb}");

            if (suspect.Count > 0)
                Plugin.Log.LogWarning(
                    $"[seatlists] {kind} at {seats}: shorter than the table and short enough to " +
                    $"have been sized for four - {string.Join(", ", suspect)}");
        }
        catch (Exception e) { Plugin.Log.LogError($"[seatlists] could not measure: {e.Message}"); }
    }

    /// <summary>A list's length, or -1 when there is nothing there to ask.</summary>
    private static int Len(Func<int> read)
    {
        try { return read(); } catch { return -1; }
    }

    /// <summary>
    /// Record one list, and flag it if it looks like a four-player list at a bigger table.
    ///
    /// The test is deliberately narrow. A list of six is a die's faces, a list of fifty-two is
    /// a deck; only something no longer than the four seats the game shipped with is worth
    /// suspecting, and only when the table is bigger than it.
    /// </summary>
    private static void Note(System.Text.StringBuilder sb,
                             System.Collections.Generic.List<string> suspect,
                             int seats, string name, int n)
    {
        sb.Append($"  {name}={(n < 0 ? "-" : n.ToString())}");
        if (n >= 1 && n <= Limits.VanillaPlayers && n < seats) suspect.Add($"{name} ({n})");
    }
}
