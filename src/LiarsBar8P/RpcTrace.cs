using System;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Names the networked call that threw, when one throws.
///
/// Mirror's own account of this is a single line: "Disconnecting connection: connection(0)
/// because handling a message of type Mirror.RpcMessage caused an Exception ... Reason:
/// System.IndexOutOfRangeException". Every peer in the game prints it at the same instant,
/// every peer is dropped, and the session is over - and nothing in it says which call, on
/// which component, in which mode. Mirror addresses remote calls by a two-byte hash, and the
/// message is written before the hash is turned back into a name.
///
/// It knows the name, though: <c>GetFunctionMethodName</c> will hand it over. So the call is
/// noted on the way in and cleared on the way out, and a call that never came out is the one
/// that threw. That works whether or not the exception itself can be caught from here, which
/// matters: the game is IL2CPP, and an exception raised inside its compiled code does not
/// reliably arrive at a managed handler.
///
/// This is the difference between "the mod broke Texas" and one line naming the method to
/// look at. It is left on in ordinary play for the same reason: when somebody's eight player
/// game falls over, the log should say what fell over.
///
/// <para>
/// <b>It also keeps everybody connected.</b> Mirror's answer to an exception in a remote call
/// is to drop the connection, and in a game of eight that is not a graceful degradation - it
/// is eight people back at the main menu because one seat had no animation clip. The two runs
/// that found the Texas fault said so plainly: without this class, five copies printed
/// "Disconnecting connection" in the same tenth of a second and the session was over; with
/// it, the same fault was logged five times and the table played on for another thirty-nine
/// turns.
/// </para>
///
/// <para>
/// The finalizer below is what makes that deliberate. It was true before the finalizer
/// existed - a detour around a method compiled by IL2CPP does not reliably let an exception
/// raised inside it back out to the caller's handler - and behaviour that important should
/// not rest on a side effect nobody chose. Swallowing an error is normally the wrong answer,
/// and it is the exact habit that let the broken deal hide for so long, so it is paired with
/// an unmissable line naming the call. A fault found this way still gets fixed at its source;
/// this only decides what happens to the people in the game in the meantime.
/// </para>
/// </summary>
[HarmonyPatch]
internal static class RpcTrace
{
    private static Mirror.NetworkBehaviour _inFlight;
    private static ushort _inFlightHash;
    private static Mirror.RemoteCalls.RemoteCallType _inFlightKind;
    private static bool _haveInFlight;
    private static int _depth;
    private static int _reported;

    /// <summary>Enough repeats to see a pattern, few enough not to fill the log.</summary>
    private const int MaxReports = 20;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Mirror.RemoteCalls.RemoteProcedureCalls),
                  nameof(Mirror.RemoteCalls.RemoteProcedureCalls.Invoke))]
    private static void Before(ushort functionHash, Mirror.RemoteCalls.RemoteCallType remoteCallType,
                               Mirror.NetworkBehaviour component)
    {
        try
        {
            // A call left over from last time means the last one never returned.
            ReportUnfinished();

            _depth++;

            // Noted, not described. This runs on every remote call in the game - several a
            // second at a table of eight - and turning a hash back into a name and reading a
            // component's type name are both string work. The description is built only for
            // the one call in a session that does not come back.
            _inFlight = component;
            _inFlightHash = functionHash;
            _inFlightKind = remoteCallType;
            _haveInFlight = true;
        }
        catch { }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Mirror.RemoteCalls.RemoteProcedureCalls),
                  nameof(Mirror.RemoteCalls.RemoteProcedureCalls.Invoke))]
    private static void After()
    {
        _depth--;
        if (_depth <= 0) { _depth = 0; _inFlight = null; _haveInFlight = false; }
    }

    /// <summary>
    /// Keep the connection when a remote call throws, and say so.
    ///
    /// Returning null discards the exception, which is deliberate and is the whole point:
    /// Mirror's own handling of one is to disconnect, and one seat's missing animation clip
    /// should not put eight people back at the main menu. The call is named first, loudly, so
    /// this never becomes the silent swallow that let the broken deal hide for a month.
    ///
    /// Whether the exception reaches here at all depends on the game: raised inside code
    /// compiled by IL2CPP, it may never cross back into managed hands. The prefix and postfix
    /// above name it either way; this makes the outcome intentional in the cases where it can
    /// be reached.
    /// </summary>
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Mirror.RemoteCalls.RemoteProcedureCalls),
                  nameof(Mirror.RemoteCalls.RemoteProcedureCalls.Invoke))]
    private static Exception Instead(Exception __exception)
    {
        if (__exception == null) return null;

        try
        {
            ReportUnfinished();
            if (_reported <= MaxReports)
                Plugin.Log.LogError($"[rpc] ...it threw {__exception.GetType().Name}: {__exception.Message}");
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Say what did not come back, once per occurrence.
    ///
    /// Called from the next invocation rather than from a finalizer, because the throw
    /// happens inside the game's compiled code and a finalizer there is not dependable.
    /// Also called from the ticker, so a call that throws and is never followed by another
    /// one is still named.
    /// </summary>
    internal static void ReportUnfinished()
    {
        if (!_haveInFlight) return;

        string what = Describe(_inFlightHash, _inFlightKind, _inFlight);
        _inFlight = null;
        _haveInFlight = false;
        _depth = 0;

        if (_reported >= MaxReports) return;
        _reported++;

        Plugin.Log.LogError(
            $"[rpc] {what} threw - Mirror drops the connection when a remote call throws, so " +
            "this is what ends the session. Everything after it is a consequence.");

        if (_reported == MaxReports)
            Plugin.Log.LogWarning("[rpc] that is enough of those - no more will be reported this session");
    }

    internal static void Tick()
    {
        // One frame's grace: an invocation in progress right now is not a failed one.
        if (!_haveInFlight) { _idle = 0; return; }
        if (++_idle < 8) return;
        _idle = 0;
        ReportUnfinished();
    }

    private static int _idle;

    private static string Describe(ushort hash, Mirror.RemoteCalls.RemoteCallType kind,
                                   Mirror.NetworkBehaviour component)
    {
        string name = null;
        try { Mirror.RemoteCalls.RemoteProcedureCalls.GetFunctionMethodName(hash, out name); }
        catch { }

        if (string.IsNullOrEmpty(name)) name = $"remote call #{hash}";

        string on = "";
        try { if (component != null) on = $" on {component.GetIl2CppType().Name}"; }
        catch { }

        return $"{kind} {name}{on}";
    }
}
