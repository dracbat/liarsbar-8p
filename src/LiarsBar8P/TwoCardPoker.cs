namespace LiarsBar8P;

/// <summary>
/// Two-Card Poker is coming back soon. Liar's Bar's Season 6 update (14 Nov 2026) took the table
/// out of the game, its code and its art both, so the mod has nothing left to switch on. Until the
/// mod has a table of its own, nobody can choose it: everything here answers as if the released
/// Liar's Poker were chosen, which is what every lobby gets.
/// </summary>
internal static class TwoCardPoker
{
    /// <summary>The table's name, for the words that say it is coming back.</summary>
    internal const string Name = "TWO-CARD POKER";

    internal static bool Shown => false;
    internal static bool Chosen => false;
    internal static bool Playing => false;
    internal static bool Settled => false;
    internal static bool RefusesBots => false;
    internal static int Round => 0;

    internal static string Describe() => "coming back soon: the game's 14 Nov 2026 update removed the table";
}
