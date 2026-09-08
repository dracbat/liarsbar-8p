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
        // Four times a second is far more often than any of this changes, and cheap enough
        // that it does not matter.
        if (Time.time < _next) return;
        _next = Time.time + 0.25f;

        try { TableFill.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[tablefill] tick failed: {e.Message}"); }

        try { DealFallback.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[dealcards] tick failed: {e.Message}"); }

        try { TurnKickstart.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[turnstart] tick failed: {e.Message}"); }

        try { BotBehaviour.Tick(); }
        catch (Exception e) { Plugin.Log.LogError($"[bot] tick failed: {e.Message}"); }

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

        try { VersionCheck.ForgetWhenSessionEnds(); }
        catch (Exception e) { Plugin.Log.LogError($"[version] banner check failed: {e.Message}"); }

        try { DevLogging.PollClientState(); }
        catch (Exception e) { Plugin.Log.LogError($"[seen] client poll failed: {e.Message}"); }

        // The automatic test stops driving seats the moment its match is over.
        try { if (Manager.Instance == null) DevAutoTest.MatchEnded(); }
        catch (Exception e) { Plugin.Log.LogError($"[auto] match-end check failed: {e.Message}"); }
    }
}
