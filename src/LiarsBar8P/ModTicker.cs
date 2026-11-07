using System;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// A once-per-frame heartbeat for the parts of this mod that cannot be driven by a patch.
///
/// Several things the game does only finish on a later frame — a spawned object joins the
/// roster next frame, a fresh player object has no manager reference until Start has run.
/// Work that depends on those cannot be done from inside the call that started them, and
/// there is no game method that runs at the right moment to patch. So this does: attached
/// to the same persistent object as the version readout, alive in every scene.
///
/// Kept deliberately thin. Anything that throws here would throw every frame.
/// </summary>
internal sealed class ModTicker : MonoBehaviour
{
    public ModTicker(IntPtr ptr) : base(ptr) { }

    private float _next;

    private void Update()
    {
        if (Plugin.Bare) { BareUpdate(); return; }

        try { WhatsNew.Frame(); }
        catch (Exception e) { Plugin.Log.LogError($"[update] changelog frame failed: {e.Message}"); }

        try { BugReport.Frame(); }
        catch (Exception e) { Plugin.Log.LogError($"[bugreport] key failed: {e.Message}"); }

        try { LobbyWardrobe.Frame(); }
        catch (Exception e) { Plugin.Log.LogError($"[wardrobe] pad failed: {e.Message}"); }

        try { MatchBoard.Frame(); }
        catch (Exception e) { Plugin.Log.LogError($"[board] frame failed: {e.Message}"); }

        // A wide test copy's window, held where the harness put it. Nothing outside a test.
        try { Ultrawide.Frame(); }
        catch (Exception e) { Plugin.Log.LogError($"[wide] test window frame failed: {e.Message}"); }

        // Four times a second is far more often than any of this changes, and cheap enough
        // that it does not matter.
        if (Time.time < _next) return;
        _next = Time.time + 0.25f;

        // First, so everything after them this tick sees the same answers.
        try { RankedGuard.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[ranked] watchdog failed: {e.Message}"); }

        try { Table.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[table] tick failed: {e.Message}"); }

        try { ModNet.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[modnet] tick failed: {e.Message}"); }

        try { ModNetProof.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[netproof] tick failed: {e.Message}"); }

        try { TableFill.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[tablefill] tick failed: {e.Message}"); }

        try { DealFallback.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[dealcards] tick failed: {e.Message}"); }

        try { RoundCensus.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[census] tick failed: {e.Message}"); }

        try { RpcTrace.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[rpc] tick failed: {e.Message}"); }

        try { TurnRingProbe.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[turnring] tick failed: {e.Message}"); }

        try { TurnKickstart.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[turnstart] tick failed: {e.Message}"); }

        // Bots: the seats first, so a match that has ended takes its seats with it before anything
        // else asks; then the lobby side, the poses, the moves, and last the guard on their turns,
        // so a bot that moved this tick is never passed on as well.
        try { BotSeats.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[bot] seat tick failed: {e.Message}"); }

        try { BotManager.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[bot] lobby tick failed: {e.Message}"); }

        try { BotButtons.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[bot] lobby buttons tick failed: {e.Message}"); }

        try { BotPuppet.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[bot] pose tick failed: {e.Message}"); }

        // Either of these failing turns bots off too. A game update that renamed something one of
        // them reads fails it here, as it is compiled, before any try inside it can run.
        try { BotBrain.Tick(); }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[bot] move tick failed: {e.Message}");
            BotTurnGuard.Trip($"the bots' move tick failed ({e.Message})");
        }

        try { BotTurnGuard.Tick(); }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[bot] turn guard tick failed: {e.Message}");
            BotTurnGuard.Trip($"the turn guard's tick failed ({e.Message})");
        }

        try { BotBehaviour.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[bot] tick failed: {e.Message}"); }

        try { ModePlay.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[play] tick failed: {e.Message}"); }

        try { AimDrive.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[aim] tick failed: {e.Message}"); }

        try { SpinBidCap.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[spin] tick failed: {e.Message}"); }

        try { DiceRevealGuard.Tick(); }
        catch (Exception e) { Plugin.Log.LogError("[dicefix] tick failed: " + e.Message); }

        try { HiddenDice.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[hiddendice] tick failed: {e.Message}"); }

        try { SeatLists.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[seatlists] tick failed: {e.Message}"); }

        try { SeatRing.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[seatring] tick failed: {e.Message}"); }

        try { DevShots.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[shots] tick failed: {e.Message}"); }

        try { Loopback.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[loopback] tick failed: {e.Message}"); }

        try { TurnHud.Refresh(); }
        catch (Exception e) { Plugin.Log.LogError($"[turnhud] refresh failed: {e.Message}"); }

        try { TurnPointer.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[arrow] tick failed: {e.Message}"); }

        try { TexasSwap.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[texasswap] tick failed: {e.Message}"); }

        try { EmoteTargets.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[emote] tick failed: {e.Message}"); }

        try { VersionCheck.ForgetWhenSessionEnds(); }
        catch (Exception e) { Plugin.Log.LogError($"[version] banner check failed: {e.Message}"); }

        try { Matchmaking.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] tick failed: {e.Message}"); }

        try { QuickMatch.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] quick join tick failed: {e.Message}"); }

        try { LobbyCount.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[lobbycount] tick failed: {e.Message}"); }

        try { LobbyId.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[lobbyid] tick failed: {e.Message}"); }

        try { HiddenTables.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[deck2] tick failed: {e.Message}"); }

        try { TwoCardPoker.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[twocard] tick failed: {e.Message}"); }

        try { TwoCardLobby.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[twocard] lobby tick failed: {e.Message}"); }

        try { LobbyWardrobe.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[wardrobe] tick failed: {e.Message}"); }

        try { MatchStats.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[awards] tick failed: {e.Message}"); }

        try { AwardPanel.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[awards] panel tick failed: {e.Message}"); }

        try { HouseRules.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[rules] tick failed: {e.Message}"); }

        try { RuleRow.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[rules] row tick failed: {e.Message}"); }

        try { BlockList.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[block] refusal tick failed: {e.Message}"); }

        try { Fair.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[fair] tick failed: {e.Message}"); }

        try { BlockButtons.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[block] plate tick failed: {e.Message}"); }

        try { BlockedPanel.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[block] list tick failed: {e.Message}"); }

        try { BugReport.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[bugreport] tick failed: {e.Message}"); }

        try { SeatCams.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[seatcam] tick failed: {e.Message}"); }

        try { Words.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[words] tick failed: {e.Message}"); }

        try { UltrawideArt.FitCanvases(); }
        catch (Exception e) { Plugin.Log.LogError($"[wide] canvas tick failed: {e.Message}"); }

        try { UltrawideArt.FixArt(); }
        catch (Exception e) { Plugin.Log.LogError($"[wide] picture tick failed: {e.Message}"); }

        try { UltrawideArt.FixPanels(); }
        catch (Exception e) { Plugin.Log.LogError($"[wide] panel tick failed: {e.Message}"); }

        try { VersionTag.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[version] tag tick failed: {e.Message}"); }

        try { WhatsNew.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[update] changelog tick failed: {e.Message}"); }

        try { MatchmakingSelfTest.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] self test tick failed: {e.Message}"); }

        try { DevLogging.PollClientState(); }
        catch (Exception e) { Plugin.Log.LogError($"[seen] client poll failed: {e.Message}"); }

        // The automatic test stops driving seats the moment its match is over.
        try { if (Manager.Instance == null) { DevAutoTest.MatchEnded(); MechanicsTrace.MatchOver(); } }
        catch (Exception e) { Plugin.Log.LogError($"[auto] match-end check failed: {e.Message}"); }
    }

    /// <summary>A bare test copy (<see cref="Plugin.Bare"/>): the window hold, the ranked block, the harness and the driver, and nothing else.</summary>
    private void BareUpdate()
    {
        try { Ultrawide.Frame(); }
        catch (Exception e) { Plugin.Log.LogError($"[wide] test window frame failed: {e.Message}"); }

        if (Time.time < _next) return;
        _next = Time.time + 0.25f;

        try { RankedGuard.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[ranked] watchdog failed: {e.Message}"); }

        try { Loopback.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[loopback] tick failed: {e.Message}"); }

        try { MatchmakingSelfTest.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] self test tick failed: {e.Message}"); }
    }

    /// <summary>
    /// Every frame, after the game's animations have been applied. A few things the game
    /// animates have to be corrected after the animator writes them, or the animator simply
    /// writes over the correction - the Liar's Dice centre panel's angle is one. Update is too
    /// early for that; LateUpdate is the first moment the animator's work is done.
    ///
    /// Whatever runs here runs every frame, so it does its own throttling and never throws.
    /// </summary>
    // A downloaded update the game was holding open goes in once the game has closed.
    private void OnApplicationQuit()
    {
        if (Plugin.Bare) return;
        try { AutoUpdate.OnQuit(); } catch { }
    }

    private void LateUpdate()
    {
        if (Plugin.Bare) return;

        try { DiceTable.LateTick(); }
        catch (Exception e) { Plugin.Log.LogError($"[dicetable] late tick failed: {e.Message}"); }

        try { EmoteTargets.LateTick(); }
        catch (Exception e) { Plugin.Log.LogError($"[emote] late tick failed: {e.Message}"); }

        // A test count, off unless the driver switches it on.
        try { MatchmakingSelfTest.LateFrame(); }
        catch (Exception e) { Plugin.Log.LogError($"[matchmaking] late frame failed: {e.Message}"); }
    }
}
