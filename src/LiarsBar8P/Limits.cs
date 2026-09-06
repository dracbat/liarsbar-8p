namespace LiarsBar8P;

/// <summary>
/// The one place the mod decides how many players a table holds.
///
/// Two numbers, and they mean different things:
///
///   <see cref="Max"/>            what this install is configured to allow. Everything
///                                that raises a cap reads this and nothing else.
///   <see cref="VanillaPlayers"/> what the game shipped with. This is a fact about the
///                                game, not a setting, and is used to scale things in
///                                proportion - cards per player, the seat circle. Tying
///                                it to the configured maximum would silently change the
///                                deck's composition.
/// </summary>
internal static class Limits
{
    /// <summary>Seats the game was built around. Never changes.</summary>
    public const int VanillaPlayers = 4;

    /// <summary>Configured maximum. Falls back to eight if settings are not loaded yet.</summary>
    public static int Max => Plugin.MaxPlayers != null ? Plugin.MaxPlayers.Value : 8;

    /// <summary>Highest seat index at the configured maximum.</summary>
    public static int MaxSlot => Max - 1;
}
