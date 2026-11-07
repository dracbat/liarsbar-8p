using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LiarsBar8P;

/// <summary>
/// A test copy never keeps the foreground. Only in a copy the test kit placed
/// (<see cref="Ultrawide.Held"/>, which needs <c>LIARSBAR8P_WINDOW_AT</c>): no player's game runs
/// any of this.
///
/// The kit starts each copy minimised and without activation, and the main screen stays free
/// for whoever is using the machine. On 5 Nov a copy still held the foreground window twice, once during the menu
/// load and once as the lobby loaded, with the desktop idle for minutes. Windows lets any process
/// take the foreground once the user has given no input for the foreground lock time, so a copy that asks for it then gets it. Two parts stop that. The window's own
/// procedure (<c>Ultrawide.Spot</c>) turns every move or show into one without activation and
/// answers a click with no activation, so the game's own SetWindowPos and ShowWindow calls cannot
/// do it. Unity also calls SetForegroundWindow, which no message can turn down. So this is the
/// backstop: a thread of its own notes, every 25 ms, the last foreground window outside this
/// process, and the moment the foreground is this process's, it hands it straight back to exactly
/// that window. Only the foreground process may call SetForegroundWindow, so that moment is the
/// one time it can. It never activates any other window, does nothing when the window that had it
/// is gone, and tries at most three times per steal and six times in any ten seconds.
///
/// Another held copy's window is never the one noted. Two copies may run, and a match start loads
/// both at once: had each noted the other's window, each would hand the foreground to the other,
/// the other would count a fresh steal with fresh tries and hand it straight back, and the window
/// that was in front would not get it again. So each held copy makes a named mark (<see cref="Mark"/>) that
/// the others look for, and only a window without one is handed the foreground. A game the kit did not
/// start makes no mark, so it can still be given it back. The cap over ten seconds is the backstop
/// for any other pair that would keep resetting the tries.
///
/// It runs on a thread of its own because the game's thread stops for seconds while a scene loads,
/// and a foreground taken then would wait for it. The procedure wakes it the moment it sees
/// WM_ACTIVATEAPP. Every activation message the procedure sees is written to the copy's log with
/// its time, so a test shows when the game tried, even when Windows refused it.
///
/// Since batch K2 the kit starts every copy on a desktop of its own and says so in
/// <c>LIARSBAR8P_DESKTOP</c>. No window there can be in front, so the hand-back is off: the window it
/// would hand the foreground to is on the main desktop, and the best it could do there is
/// nothing. The thread still runs and writes down any foreground it sees, which should be none.
/// </summary>
internal static class HeldFocus
{
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public int Size; public uint Time; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int most);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();

    // The messages the window procedure hands on, by the numbers Windows gives them.
    internal const uint ActivateMessage = 0x0006;      // WM_ACTIVATE
    internal const uint AppMessage = 0x001C;           // WM_ACTIVATEAPP
    internal const uint MouseMessage = 0x0021;         // WM_MOUSEACTIVATE
    internal const uint FrameMessage = 0x0086;         // WM_NCACTIVATE

    private const int PollMs = 25;
    private const int Tries = 3;
    private const int TryGapMs = 100;
    /// <summary>At most this many hand-backs in any <see cref="BudgetMs"/>, whatever the steals: past it the next one waits.</summary>
    private const int Budget = 6;
    private const int BudgetMs = 10000;
    /// <summary>Every event is written up to here, then one in fifty, so a copy that keeps asking cannot flood its log.</summary>
    private const int Written = 40;

    /// <summary>Set by the kit to its own desktop's name when this copy runs there.</summary>
    private const string DesktopVariable = "LIARSBAR8P_DESKTOP";

    private static readonly AutoResetEvent _wake = new(false);
    private static readonly object _gate = new();
    private static Thread _thread;
    private static uint _process;
    /// <summary>False on the kit's own desktop: nothing is ever handed back from there.</summary>
    private static bool _handBack = true;
    /// <summary>This copy's mark for the other held copies. Kept so it lives as long as the process.</summary>
    private static Mutex _mark;

    // Kept by the thread alone.
    private static IntPtr _outside, _copy;
    private static long _since, _lastTry;
    private static int _tried, _writes, _slot;
    private static bool _waited;
    /// <summary>When the last <see cref="Budget"/> hand-backs were made, oldest at <see cref="_slot"/>.</summary>
    private static readonly long[] _recent = new long[Budget];

    // Counts for the driver's widelist, written by both threads.
    private static int _apps, _windows, _frames, _mice, _moves, _taken, _handed, _refused, _gone, _others, _lines;

    /// <summary>Once, from <see cref="Plugin"/> beside the window hold. Nothing unless the kit placed this copy.</summary>
    internal static void Start()
    {
        lock (_gate)
        {
            if (_thread != null || !Ultrawide.Held) return;
            _process = (uint)Environment.ProcessId;
            _handBack = string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DesktopVariable));
            try { _mark = new Mutex(false, Mark(_process)); }
            catch (Exception e) { Plugin.Log.LogWarning($"[wide] test: no mark for the other held copies ({e.GetType().Name}): they may hand this copy the foreground"); }
            // The window the kit saw in front as it started this copy. Unity can take the foreground
            // as its window opens, before any plugin runs (5 Nov 20:16: four seconds before this
            // thread began), and a thread that had seen no window could not give it back.
            if (_handBack && long.TryParse(Environment.GetEnvironmentVariable("LIARSBAR8P_FOREGROUND"), out long seen) && seen != 0)
                _outside = new IntPtr(seen);
            _thread = new Thread(Run) { IsBackground = true, Name = "LiarsBar8P held focus" };
            _thread.Start();
        }
        Plugin.Log.LogInfo(_handBack
            ? $"[wide] test: the foreground is handed straight back whenever this copy gets it " +
              $"(checked every {PollMs} ms on a thread of its own); the desktop idle {Idle()} s"
            : $"[wide] test: on the kit's own desktop, where no window is ever in front: the hand-back is off, " +
              $"and any foreground this copy gets is only written down (checked every {PollMs} ms); the desktop idle {Idle()} s");
    }

    /// <summary>
    /// From the wrapped window procedure, on the game's thread, for each activation message. Writes
    /// it with what is done about it. Never throws back into Windows.
    /// </summary>
    internal static void Seen(uint message, IntPtr w, IntPtr l)
    {
        try
        {
            long on = w.ToInt64();
            switch (message)
            {
                case AppMessage:
                    Interlocked.Increment(ref _apps);
                    if (on != 0) _wake.Set();
                    Line(on != 0 ? $"WM_ACTIVATEAPP on: this copy is becoming the foreground app (from thread {l.ToInt64()}); the hand-back is woken, the desktop idle {Idle()} s"
                                 : $"WM_ACTIVATEAPP off: the foreground app is now thread {l.ToInt64()}'s");
                    break;
                case ActivateMessage:
                    Interlocked.Increment(ref _windows);
                    int state = (int)(on & 0xFFFF);
                    Line($"WM_ACTIVATE {(state == 0 ? "inactive" : state == 2 ? "active by a click" : "active")}" +
                         $"{(((on >> 16) & 0xFFFF) != 0 ? ", minimised" : "")}" +
                         $"{(state != 0 ? $", the desktop idle {Idle()} s: active in its own thread, which alone takes no foreground; the hand-back watches that" : "")}");
                    break;
                case FrameMessage:
                    Interlocked.Increment(ref _frames);
                    Line($"WM_NCACTIVATE {(on != 0 ? "on" : "off")}");
                    break;
                case MouseMessage:
                    Interlocked.Increment(ref _mice);
                    Line("WM_MOUSEACTIVATE: answered with no activation");
                    break;
            }
        }
        catch { }
    }

    /// <summary>From the wrapped procedure: a move or show that would have activated the window, now made without.</summary>
    internal static void Quieted(uint flags)
    {
        try
        {
            Interlocked.Increment(ref _moves);
            Line($"a move or show that would activate the window (flags 0x{flags:X4}) now goes without activation");
        }
        catch { }
    }

    internal static string Describe() =>
        _thread == null ? "no foreground watch" :
        $"{(_handBack ? "" : "on the kit desktop, the hand-back off; ")}activation messages seen: {_apps} WM_ACTIVATEAPP, {_windows} WM_ACTIVATE, {_frames} WM_NCACTIVATE, {_mice} WM_MOUSEACTIVATE; " +
        $"{_moves} activating moves made without; the foreground taken {_taken} times, handed back {_handed} " +
        $"({_refused} refused, {_gone} with the window gone); another held copy's window passed over {_others} times";

    /// <summary>The name a held copy's mark goes by. Local: every copy runs in the user's own logon session.</summary>
    private static string Mark(uint process) => $"Local\\LiarsBar8P-held-{process}";

    /// <summary>Whether a process is another copy the kit placed: each makes its mark as it starts.</summary>
    private static bool IsHeldCopy(uint process)
    {
        if (!Mutex.TryOpenExisting(Mark(process), out Mutex found)) return false;
        found.Dispose();
        return true;
    }

    private static void Run()
    {
        while (true)
        {
            _wake.WaitOne(PollMs);
            try { Check(); }
            catch { }
        }
    }

    private static void Check()
    {
        IntPtr top = GetForegroundWindow();
        if (top == IntPtr.Zero) return;
        GetWindowThreadProcessId(top, out uint owner);
        long now = Stopwatch.GetTimestamp();

        if (owner != _process)
        {
            if (_since != 0)
            {
                Write($"the foreground left this copy after {Ms(now - _since)} ms: now {Name(top)}" +
                      $"{(top == _outside ? ", the window it had come from" : ", NOT the window it had come from")}");
                _since = 0;
            }
            // Looked up once per window: the same one stays in front for many polls.
            if (top != _outside && top != _copy)
            {
                if (IsHeldCopy(owner))
                {
                    _copy = top;
                    Interlocked.Increment(ref _others);
                    Write($"the foreground is another held copy's ({Name(top)}): never a window to hand it back to");
                }
                else _outside = top;
            }
            return;
        }

        if (_since == 0)
        {
            _since = now;
            _tried = 0;
            _waited = false;
            Interlocked.Increment(ref _taken);
            Write($"FOREGROUND: this copy has it ({Name(top)}), the desktop idle {Idle()} s; it came from {(_outside != IntPtr.Zero ? Name(_outside) : "no window seen")}" +
                  $"{(_handBack ? "" : "; on the kit desktop nothing is handed back")}");
        }
        if (!_handBack) return;
        if (_tried >= Tries || (_tried > 0 && Ms(now - _lastTry) < TryGapMs)) return;
        long oldest = _recent[_slot];
        if (oldest != 0 && Ms(now - oldest) < BudgetMs)
        {
            if (!_waited) Write($"{Budget} hand-backs within {BudgetMs / 1000} s already: the next waits until the first of them is {BudgetMs / 1000} s old");
            _waited = true;
            return;
        }
        _tried++;
        _lastTry = now;

        // Asked again here: a copy noted in the moment before its mark was made is still another copy.
        uint target = 0;
        if (_outside != IntPtr.Zero && IsWindow(_outside)) GetWindowThreadProcessId(_outside, out target);
        if (target == 0 || IsHeldCopy(target))
        {
            _tried = Tries;
            if (target == 0) Interlocked.Increment(ref _gone);
            else Interlocked.Increment(ref _others);
            Write(target == 0 ? "the window that had the foreground is gone: nothing is activated, the foreground stays here"
                              : "the window noted is another held copy's: nothing is activated, the foreground stays here");
            return;
        }
        _recent[_slot] = now;
        _slot = (_slot + 1) % Budget;
        bool done = SetForegroundWindow(_outside);
        if (done) Interlocked.Increment(ref _handed);
        else Interlocked.Increment(ref _refused);
        Write($"the foreground handed back to {Name(_outside)} {Ms(Stopwatch.GetTimestamp() - _since)} ms after this copy got it " +
              $"(try {_tried}): {(done ? "done" : "refused")}");
    }

    /// <summary>The game's thread: through the plugin's log, as every other line.</summary>
    private static void Line(string words)
    {
        int n = Interlocked.Increment(ref _lines);
        if (n <= Written || n % 50 == 0)
            Plugin.Log.LogInfo($"[wide] test: {words}{(n == Written ? " (from here on one line in fifty)" : "")}");
    }

    /// <summary>This thread: straight into the copy's own log, which takes lines from any thread. A steal is rare: more of these are kept.</summary>
    private static void Write(string words)
    {
        int n = ++_writes;
        if (n <= Written * 5 || n % 50 == 0)
            InstanceLog.Write($"[wide] test (focus thread): {words}{(n == Written * 5 ? " (from here on one line in fifty)" : "")}");
    }

    private static long Ms(long ticks) => ticks * 1000 / Stopwatch.Frequency;

    /// <summary>A window's class and process, never its title: a title can hold a name.</summary>
    private static string Name(IntPtr window)
    {
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        GetWindowThreadProcessId(window, out uint owner);
        return $"{name} (pid {owner})";
    }

    /// <summary>Seconds since the last input anywhere in the session: past the foreground lock time, Windows lets any process take the foreground.</summary>
    private static long Idle()
    {
        var input = new LastInput { Size = Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) ? (GetTickCount() - input.Time) / 1000 : -1;
    }
}
