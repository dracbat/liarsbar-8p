using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Stops a big enough bid in Liar's Dice from knocking somebody out of the game.
///
/// When a bid is placed, the host tells every player's game to say it out loud:
/// <c>PlayBetVoiceRPC(count, dice)</c>, which plays <c>SayiClips[count - 1]</c> - one recorded
/// line per number - and then the line for the face. Those lines were recorded for a table of
/// four, where five dice each puts twenty on the table; at six there are thirty, and a bid
/// past the end of the list asks for a line that does not exist. (Where exactly the list ends
/// is read at run time below rather than assumed.)
///
/// A player's report showed exactly that. "30 x 2" at a table of six threw
/// <c>ArgumentOutOfRangeException</c> inside the voice call on every machine. Machines
/// running this mod kept going, because <see cref="RpcTrace"/> catches a remote call that
/// throws; the one player whose game was not running the mod was disconnected on the spot,
/// because Mirror drops a connection whose remote call throws.
///
/// So the host does not send the call when there is no line to play. The bid itself is
/// untouched - it is on the table and on everybody's screen as before - it is just not spoken.
/// Saying a smaller number instead was the alternative, and in a game about calling out lies
/// a voice announcing the wrong bid is worse than silence.
///
/// This has to be done where the call is sent rather than where it is played. The game's
/// compiler folded both ends into their callers - the send into <c>PlaceBet</c>, the playing
/// into Mirror's handler - so neither has a method of its own to patch; the one place left is
/// Mirror's <c>SendRPCInternal</c>, which every remote call goes through. Doing it on the host
/// also means it protects every player in the lobby, including anyone whose own copy of the
/// mod is older or missing.
/// </summary>
internal static class BetVoiceGuard
{
    /// <summary>How Mirror names the call, exactly as the game passes it.</summary>
    private const string BetVoice = "System.Void DiceGamePlay::PlayBetVoiceRPC(System.Int32,System.Int32)";

    /// <summary>Enough to see it happen, few enough not to fill the log in a long match.</summary>
    private const int MaxReports = 5;

    private static int _held;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mirror.NetworkBehaviour), "SendRPCInternal")]
    private static bool Send(Mirror.NetworkBehaviour __instance, string functionFullName)
    {
        try
        {
            if (!string.Equals(functionFullName, BetVoice, StringComparison.Ordinal)) return true;

            var dice = __instance.TryCast<DiceGamePlay>();
            var lines = dice != null ? dice.SayiClips : null;
            if (lines == null) return true;

            // PlaceBet stores the bid before it sends the call, so this is the number about to
            // be spoken.
            int count = dice.NetworkBetCount;
            if (count >= 1 && count <= lines.Count) return true;

            _held++;
            if (_held <= MaxReports)
                Plugin.Log.LogInfo(
                    $"[betvoice] a bid of {count} has no voice line (the game recorded 1-{lines.Count}) - " +
                    "the bid stands, it is just not spoken");
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[betvoice] {e.Message}");
            return true;
        }
    }
}
