using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace LiarsBar8P;

/// <summary>
/// The middle finger, pointed at anyone at the table instead of in three fixed directions.
///
/// The emote panel at the bottom right of the screen puts three keys under the finger: 1, 2
/// and 3 (the D-pad's left, up and right). Each sends the game a direction - 1, 2 or 3 - and
/// every machine plays one of three canned clips: finger to the left, straight ahead, to the
/// right. Written for four chairs, those are the three other people at the table, and the
/// clips were made to point at exactly where they sit. With eight chairs there are seven
/// people and still three directions, so most of the table could never be singled out - the
/// clips land between people, or on whoever happens to sit at 45 degrees. A player asked for
/// it to reach everybody (issue #4).
///
/// <b>What a player gets.</b> At five or more, the row of keys under the finger grows to one
/// key per other player, numbered in plain order left to right as they sit round the table from
/// where you are: 1 is the person on your left, the last number the person on your right. The
/// game's other two emotes, 4 and 5 at four players, move up to the next two numbers so the
/// whole panel still reads in order - at six the finger is 1 to 5 and the others 6 and 7, at
/// eight the finger is 1 to 7 and the others 8 and 9 - and answer only to those. The D-pad is
/// left as the game has it: left, up and right are the neighbours and the person across, down
/// is the first of the other two - and the pad arrows the game draws on its keys 1 to 4 go
/// with those meanings, onto the keys that now do the same thing as each direction of the pad.
/// If the extra keys cannot be made, the panel and the number keys both stay the game's, so
/// the panel never shows a number that means something else. Where the game has a key picture for a number (1 to 5) its
/// own key is used, moved to its new place; past 5 the key is a copy of the game's key 3 - the
/// same cap, the same press animation - with the number changed, so the panel looks like the
/// game's own panel with more keys in it rather than something laid over it. Nine players is
/// as far as the number row goes (finger 1 to 8, the others 9 and 0); past that the panel and
/// keys stay the game's, with 1, 2 and 3 meaning left, across and right.
///
/// <b>The cooldown</b> between emotes is the game's own counter - 12 seconds, counted on the
/// player's own machine; the server passes emotes on without checking - run faster, so that it
/// fills in <c>EmoteCooldownSeconds</c> (10 unless set) at every table size, because that is a
/// change the mod's owner asked for rather than something the table size needs.
///
/// <b>How it travels.</b> The game sends the direction as a plain number to the server, which
/// hands it to everybody without looking at it. At five or more, the number sent is the target
/// seat instead (<see cref="Code"/> plus the seat), well clear of 1 to 3 so nothing can mistake
/// one for the other. Every machine works out from that seat which of the three clips is
/// nearest, plays it, and turns the body the rest of the way - never more than about 22 degrees
/// at eight - for the second and a half the gesture lasts, then turns it back. The person
/// gesturing is not turned on their own screen: their camera is inside that body, and a
/// sideways jolt to the view is not something they asked for. Everyone else sees the finger
/// land on the person it was meant for.
///
/// <b>Four players or fewer</b> nothing here does anything: the keys, the panel, what is sent
/// and what is played are all the game's own. And if any one of the patches below did not go
/// in, none of it is switched on - a target sent to a machine that cannot read it would play
/// the sound with no gesture at all, which is worse than three directions.
/// </summary>
[HarmonyPatch]
internal static class EmoteTargets
{
    /// <summary>A seat, as sent where the game sends 1, 2 or 3: this plus the seat number.</summary>
    private const int Code = 100;

    /// <summary>The highest seat a code can name. Anything above is not ours.</summary>
    private const int MaxSeat = 63;

    /// <summary>The animator triggers for the three shipped clips, by direction: left, ahead, right.</summary>
    private static readonly string[] Clip = { null, "Emote1", "Emote2", "Emote3" };

    /// <summary>Which way each clip points, in degrees to the right - where the neighbours sit at four.</summary>
    private static readonly float[] ClipBearing = { 0f, -45f, 0f, 45f };

    /// <summary>
    /// The largest table the number row covers: n-1 keys for the finger and two for the other
    /// emotes, out of 1 to 9 and 0.
    /// </summary>
    private const int MostSeats = 9;

    /// <summary>The game's own wait between emotes, in seconds - the number its counter runs up to.</summary>
    private const float ShippedCooldown = 12f;

    /// <summary>Whether the keys are numbered by this class at a table of <paramref name="n"/>.</summary>
    private static bool Renumbered(int n) => n > Limits.VanillaPlayers && n <= MostSeats;

    /// <summary>The number on the i-th key of the number row: 1 to 9, then 0.</summary>
    private static int Digit(int i) => i % 10;

    /// <summary>Errors reported so far. Some of this runs every frame; one fault must not fill the log.</summary>
    private static int _errors;
    private const int MaxErrors = 5;

    private static void Report(string what, Exception e)
    {
        if (++_errors <= MaxErrors) Plugin.Log.LogError($"[emote] {what}: {e.Message}");
    }

    // -------------------------------------------------------------- receiving a gesture
    //
    // First in the class on purpose: patches here go in in the order they are written, and the
    // first that fails stops the rest. If the receiving end cannot be patched, nothing that
    // sends a seat must go in either.

    /// <summary>
    /// Look at the number before the game reads it, without taking it: the game's own handler
    /// reads it next. The body of the remote call is compiled into Mirror's handler, so the
    /// handler is what is patched - a patch on the method with the body would never run.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharController), nameof(CharController.InvokeUserCode_PlayEmote1RPC__Int32))]
    private static void Peek(NetworkReader reader, ref int __state)
    {
        __state = -1;
        if (reader == null) return;
        int at = -1;
        try
        {
            at = reader.Position;
            int sent = reader.ReadInt();
            if (sent >= Code && sent <= Code + MaxSeat) __state = sent - Code;
        }
        catch (Exception e) { Report("could not read a gesture", e); }
        finally
        {
            try { if (at >= 0) reader.Position = at; }
            catch (Exception e) { Report("could not put a gesture back", e); }
        }
    }

    /// <summary>
    /// The game has played the sound and, not knowing the number, no clip. Play the clip
    /// nearest the seat and turn the body the rest of the way.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CharController), nameof(CharController.InvokeUserCode_PlayEmote1RPC__Int32))]
    private static void Played(NetworkBehaviour obj, int __state)
    {
        if (__state < 0) return;
        try { Show(obj, __state); }
        catch (Exception e) { Report("could not play a gesture", e); }
    }

    private static void Show(NetworkBehaviour obj, int target)
    {
        var self = obj != null ? obj.TryCast<CharController>() : null;
        if (self == null || !NetworkClient.active) return;

        // The game's own gate: a player who has switched emotes off sees nobody's.
        var m = self.manager;
        if (m == null) m = Manager.Instance;
        if (m == null || !m.Emotes) return;

        var stats = self.playerStats;
        var anim = self.animator;
        if (stats == null || anim == null) return;

        int me = stats.Slot;
        int n = AimRing.Seats(m);
        if (n < 2 || me < 0 || me >= n || target >= n || target == me) return;

        int clip = Nearest(m, me, target, n, out float turn);
        anim.SetTrigger(Clip[clip]);

        bool turned = !self.isOwned && StartTurn(self, anim, turn);
        Plugin.Log.LogInfo($"[emote] seat {me} gave seat {target} the finger at a table of {n}: " +
                           $"{Clip[clip]} clip, " + (turned ? $"turned {turn:+0;-0;0} deg" : "not turned"));
    }

    // ----------------------------------------------------------------- sending a gesture

    /// <summary>
    /// The game's own three finger directions at five or more - the D-pad's left, up and right,
    /// or the number keys 1 to 3 wherever the panel keeps the game's numbers (past nine
    /// players, or if its extra keys could not be made) - sent as the person on your left, the
    /// person across, or the person on your right. The game's own handling decides whether and
    /// when; this only changes what it sends.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharController), nameof(CharController.PlayEmote1Sfx))]
    private static void Remap(CharController __instance, ref int direction)
    {
        try
        {
            if (direction < 1 || direction > 3) return;   // already a seat
            if (!Ready() || !AimRing.Ring(__instance, out int me, out int n)) return;   // four or fewer

            int target = (me + OffsetFor(direction, n)) % n;
            Plugin.Log.LogInfo($"[emote] seat {me} gave direction {direction}: the finger at seat {target} of {n}");
            direction = Code + target;
        }
        catch (Exception e) { Report("could not aim the finger", e); }
    }

    /// <summary>
    /// The number keys at five to nine players: 1 up to n-1 for the finger, left to right as the
    /// other players sit, then n and n+1 for the game's other two emotes.
    ///
    /// The game reads its keys 1 to 5 inside this same method, with its two other emotes' sends
    /// compiled straight into it, so there is no smaller call to catch them at. Instead, whenever
    /// a number key is down at this size of table, this handles it and the game's own reading is
    /// skipped for that frame - so 4 and 5 can never fire their old emotes here. With no number
    /// key down the game's reading runs as shipped, and by then all it can answer is the D-pad.
    ///
    /// Only ever called by the game when an emote may be sent this frame - alive, emotes on, not
    /// cooling down - so all of that is already decided. A press sends exactly what the game
    /// sends and starts the same cooldown. Number keys are swallowed while typing in the chat or
    /// paused - the same rule the panel's keys follow for their press animation - because they
    /// are digits people type.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharController), nameof(CharController.ProcessEmoteInput))]
    private static bool Keys(CharController __instance)
    {
        try
        {
            int key = NumberKeyDown();
            if (key == 0) return true;
            if (!Ready() || !AimRing.Who(__instance, out var m, out int me, out int n)) return true;   // four or fewer
            if (!Renumbered(n)) return true;   // past the number row: the game's own keys
            if (_numberedFor != n) return true;   // the panel shows the game's numbers, so the keys keep them

            if (m.Chatting || m.GamePaused) return false;

            if (key <= n - 1)
            {
                int target = (me + n - key) % n;
                Plugin.Log.LogInfo($"[emote] seat {me} pressed {Digit(key)}: the finger at seat {target} of {n}");
                __instance.PlayEmote1Sfx(Code + target);
            }
            else if (key == n)
            {
                Plugin.Log.LogInfo($"[emote] seat {me} pressed {Digit(key)}: the game's key-4 emote");
                __instance.PlayEmote2Sfx();
            }
            else if (key == n + 1)
            {
                Plugin.Log.LogInfo($"[emote] seat {me} pressed {Digit(key)}: the game's key-5 emote");
                __instance.PlayEmote3Sfx();
            }
            else return true;   // a number nothing uses at this size; the game's reading, for the D-pad

            __instance.EmoteReadyy = false;
            return false;
        }
        catch (Exception e)
        {
            Report("could not read the emote keys", e);
            return true;
        }
    }

    /// <summary>Which key of the number row went down this frame, 1 to 10 (10 is the 0 key), or 0 for none.</summary>
    private static int NumberKeyDown()
    {
        for (int i = 1; i <= 10; i++)
            if (Input.GetKeyDown(KeyCode.Alpha0 + Digit(i))) return i;
        return 0;
    }

    // ---------------------------------------------------------------- who each key means

    /// <summary>Chairs round from me for directions 1, 2 and 3: the one before me, the one across, the next one.</summary>
    private static int OffsetFor(int direction, int n) =>
        direction == 1 ? n - 1 : direction == 2 ? n / 2 : 1;

    // ---------------------------------------------------------------------- the cooldown

    private static bool _cooldownReported;

    /// <summary>
    /// Run the game's emote cooldown faster, so that it fills in <c>EmoteCooldownSeconds</c>
    /// rather than 12.
    ///
    /// The wait is a counter on the player's own object: after any emote the game adds the
    /// frame's time to it every frame, draws the panel's fill from it, and makes emotes ready
    /// again when it reaches 12. The 12 is a constant shared with other code in the game's
    /// compiled program, so it is not changed; the counter is given the extra time it needs to
    /// reach 12 sooner, just before the game adds its own. The fill on the panel then runs at
    /// the new speed as well. The server does not check the wait at all - it passes every emote
    /// on as it comes - so nothing there needs to agree.
    ///
    /// This is every player object's every frame, so it stops at the first check on all of them
    /// but the one whose emotes are cooling down.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharController), nameof(CharController.Update))]
    private static void Cooldown(CharController __instance)
    {
        var setting = Plugin.EmoteCooldownSeconds;
        if (setting == null) return;
        float want = setting.Value;
        if (want <= 0f || Mathf.Approximately(want, ShippedCooldown)) return;

        try
        {
            if (__instance.EmoteReadyy || !__instance.isOwned) return;
            __instance.EmoteCooldown += Time.deltaTime * (ShippedCooldown / want - 1f);

            if (!_cooldownReported)
            {
                _cooldownReported = true;
                Plugin.Log.LogInfo($"[emote] emote cooldown {want:0.#} s (the game's own is {ShippedCooldown:0} s)");
            }
        }
        catch (Exception e) { Report("could not change the emote cooldown", e); }
    }

    // ------------------------------------------------------------ which clip, which turn

    /// <summary>
    /// The clip nearest the target - 1 left, 2 ahead, 3 right - and how far to turn the body
    /// from there, in degrees to the right. A target exactly between two clips takes the one
    /// straight ahead.
    /// </summary>
    private static int Nearest(Manager m, int me, int target, int n, out float turn)
    {
        float bearing = Bearing(m, me, target, n);
        int clip = bearing > 23f ? 3 : bearing < -23f ? 1 : 2;
        turn = Mathf.Clamp(bearing - ClipBearing[clip], -MaxTurn, MaxTurn);
        return clip;
    }

    private const float MaxTurn = 35f;

    /// <summary>
    /// Which way the target is from me, in degrees to the right of facing the middle of the
    /// table - from the seats as this machine has them laid out, and from the ring's own
    /// arithmetic if they cannot be read. The two agree whenever the table is laid out evenly.
    /// </summary>
    private static float Bearing(Manager m, int me, int target, int n)
    {
        try
        {
            var slots = m.Slots;
            if (slots != null && slots.Count >= n && n >= 3)
            {
                Vector3 middle = Vector3.zero;
                bool all = true;
                for (int i = 0; i < n && all; i++)
                {
                    var s = slots[i];
                    if (s == null) all = false;
                    else middle += s.position;
                }

                if (all)
                {
                    middle /= n;
                    Vector3 from = slots[me].position;
                    Vector3 ahead = middle - from;
                    Vector3 there = slots[target].position - from;
                    ahead.y = 0f;
                    there.y = 0f;
                    if (ahead.sqrMagnitude > 0.01f && there.sqrMagnitude > 0.01f)
                        return Vector3.SignedAngle(ahead, there, Vector3.up);
                }
            }
        }
        catch { }

        int off = ((target - me) % n + n) % n;
        return 90f - 180f * off / n;
    }

    // ------------------------------------------------------------------- the turn

    private sealed class Turn
    {
        public Transform Body;
        public IntPtr Key;
        public Quaternion Rest;
        public float Degrees;
        public float Start;
    }

    private static readonly List<Turn> _turns = new();

    // The finger clips run 1.2 to 1.4 seconds. Turn into it, hold, and turn back as it ends.
    private const float TurnIn = 0.3f;
    private const float TurnHold = 1.2f;
    private const float TurnDone = 1.6f;

    /// <summary>
    /// Turn the model, not the player. The player object's own rotation is synced from the
    /// server and is where the seat puts it; the model under it is not synced at all, and
    /// turning it turns the body on its chair and nothing else.
    /// </summary>
    private static bool StartTurn(CharController self, Animator anim, float degrees)
    {
        if (Mathf.Abs(degrees) < 2f) return false;

        var body = anim.transform;
        if (body == null || body.Pointer == self.transform.Pointer) return false;

        IntPtr key = body.Pointer;
        foreach (var t in _turns)
        {
            if (t.Key != key) continue;
            t.Degrees = degrees;
            t.Start = Time.time;
            return true;
        }

        _turns.Add(new Turn { Body = body, Key = key, Rest = body.localRotation, Degrees = degrees, Start = Time.time });
        return true;
    }

    /// <summary>Every frame, from <see cref="ModTicker"/>. Nothing to do on almost all of them.</summary>
    internal static void LateTick()
    {
        if (_turns.Count == 0) return;

        float now = Time.time;
        for (int i = _turns.Count - 1; i >= 0; i--)
        {
            var t = _turns[i];
            try
            {
                if (t.Body == null) { _turns.RemoveAt(i); continue; }   // gone with the round

                float age = now - t.Start;
                if (age >= TurnDone)
                {
                    t.Body.localRotation = t.Rest;
                    _turns.RemoveAt(i);
                    continue;
                }

                float w = age < TurnIn ? Mathf.SmoothStep(0f, 1f, age / TurnIn)
                        : age < TurnHold ? 1f
                        : Mathf.SmoothStep(1f, 0f, (age - TurnHold) / (TurnDone - TurnHold));
                t.Body.localRotation = Quaternion.Euler(0f, t.Degrees * w, 0f) * t.Rest;
            }
            catch (Exception e)
            {
                _turns.RemoveAt(i);
                Report("could not turn a body", e);
            }
        }
    }

    // --------------------------------------------------------------------- the panel

    private static GameObject _panel;
    private static IntPtr _panelKey;

    /// <summary>The game's own keys 1 to 5 (index 1 to 5) and where each one sits in the panel.</summary>
    private static readonly RectTransform[] _own = new RectTransform[6];
    private static readonly Vector2[] _ownAt = new Vector2[6];
    private static bool _found;

    /// <summary>The keys made here, by number: 6 to 9 and 0.</summary>
    private static readonly Dictionary<int, RectTransform> _extra = new();
    private static int _laidFor;
    private static int _announcedFor;
    private static bool _noKeysReported;

    /// <summary>
    /// The table size the panel is numbered for, or 0 while it shows the game's own numbers.
    /// The number keys follow it, so a key never means something other than what it shows.
    /// </summary>
    private static int _numberedFor;

    // The game's GamepadButtonType values for the four directions of the pad.
    private const int PadRight = 8, PadLeft = 9, PadDown = 10, PadUp = 11;

    /// <summary>
    /// The D-pad arrows the game draws on its keys 1 to 4, by the way they point, each with the
    /// key it came on. At five or more they are moved onto the keys that do what that direction
    /// of the pad does, and put back where they came from at four or fewer.
    /// </summary>
    private static readonly Dictionary<int, (Transform Arrow, RectTransform Home)> _arrows = new();

    /// <summary>
    /// Four times a second, from <see cref="ModTicker"/>: number the panel's keys for the size
    /// of table, once per match. Does nothing once that is done.
    /// </summary>
    internal static void Tick()
    {
        Manager m;
        try { m = Manager.Instance; }
        catch { m = null; }
        if (m == null) { _panel = null; return; }

        var panel = m.Taunts;
        if (panel == null) return;

        if (_panel == null || panel.Pointer != _panelKey) Adopt(panel);
        if (!_found) return;

        int n = AimRing.Seats(m);
        int want = Renumbered(n) ? n : 0;
        if (want == _laidFor) return;
        if (want > 0 && !Ready()) return;

        Lay(want);
        _laidFor = want;
    }

    /// <summary>A new table, a new panel: find the game's five keys and remember where they sit.</summary>
    private static void Adopt(GameObject panel)
    {
        _panel = panel;
        _panelKey = panel.Pointer;
        _extra.Clear();
        _arrows.Clear();
        _laidFor = 0;
        _announcedFor = 0;
        _numberedFor = 0;
        _found = false;
        _font = null;
        _fontLooked = false;

        var t = panel.transform;
        for (int d = 1; d <= 5; d++)
        {
            _own[d] = Key(t, d.ToString());
            if (_own[d] == null) return;
            _ownAt[d] = _own[d].anchoredPosition;
        }
        _found = true;

        // The arrows are only pictures: a panel without them still gets its numbers.
        try
        {
            for (int d = 1; d <= 4; d++)
            {
                var arrow = ArrowOn(_own[d], out int way);
                if (arrow != null && !_arrows.ContainsKey(way)) _arrows[way] = (arrow, _own[d]);
            }
        }
        catch (Exception e) { Report("could not find the pad arrows on the panel", e); }
    }

    /// <summary>The D-pad arrow drawn on a key and the way it points, or null if the key has none.</summary>
    private static Transform ArrowOn(RectTransform key, out int way)
    {
        way = -1;
        for (int i = 0; i < key.childCount; i++)
        {
            var child = key.GetChild(i);
            var effect = child != null ? child.GetComponent<GamepadButtonEffect>() : null;
            if (effect == null) continue;
            way = (int)effect.buttonType;
            return child;
        }
        return null;
    }

    /// <summary>Put the arrow for one direction of the pad on a key, keeping its place on the cap.</summary>
    private static void MoveArrow(int way, RectTransform onto)
    {
        if (onto == null || !_arrows.TryGetValue(way, out var a) || a.Arrow == null) return;
        if (a.Arrow.parent != onto) a.Arrow.SetParent(onto, false);
    }

    private static RectTransform Key(Transform panel, string name)
    {
        var t = panel.Find(name);
        return t != null ? t.GetComponent<RectTransform>() : null;
    }

    /// <summary>The key showing a number: the game's own for 1 to 5, one made here for the rest.</summary>
    private static RectTransform KeyShowing(int digit) =>
        digit >= 1 && digit <= 5 ? _own[digit] : _extra.TryGetValue(digit, out var k) ? k : null;

    /// <summary>
    /// Number the panel for a table of <paramref name="n"/>. The finger's keys 1 to n-1 go in a
    /// row left to right as the other players sit, ending where key 3 always was - tucked under
    /// the finger - and growing to the left at the game's own spacing. The other two emotes'
    /// keys n and n+1 go exactly where 4 and 5 were, and the pad's arrows onto the keys that do
    /// what each direction of the pad does. At four or fewer (<paramref name="n"/> zero) the
    /// game's five keys and their arrows go back where they came from.
    /// </summary>
    private static void Lay(int n)
    {
        if (n == 0)
        {
            for (int d = 1; d <= 5; d++)
                if (_own[d] != null) _own[d].anchoredPosition = _ownAt[d];
            foreach (var kv in _extra)
                if (kv.Value != null) kv.Value.gameObject.SetActive(false);
            foreach (var kv in _arrows) MoveArrow(kv.Key, kv.Value.Home);
            _numberedFor = 0;
            return;
        }

        // Every key the panel needs, before anything moves: half a panel is worse than the game's.
        for (int i = 1; i <= n + 1; i++)
        {
            int d = Digit(i);
            if (KeyShowing(d) != null) continue;

            var made = MakeKey(d);
            if (made == null)
            {
                if (!_noKeysReported)
                {
                    _noKeysReported = true;
                    Plugin.Log.LogWarning("[emote] could not make the extra keys for the panel - the panel and " +
                                          "the number keys stay as the game has them (1, 2, 3 left, across, right)");
                }
                Lay(0);
                return;
            }
            _extra[d] = made;
        }

        float step = (_ownAt[3].x - _ownAt[1].x) / 2f;
        var used = new HashSet<int>();
        var finger = new System.Text.StringBuilder();

        for (int i = 1; i <= n - 1; i++)
        {
            Place(Digit(i), new Vector2(_ownAt[3].x - (n - 1 - i) * step, _ownAt[3].y), used);
            finger.Append(i == 1 ? "" : " ").Append(Digit(i));
        }
        Place(Digit(n), _ownAt[4], used);
        Place(Digit(n + 1), _ownAt[5], used);

        foreach (var kv in _extra)
            if (!used.Contains(kv.Key) && kv.Value != null) kv.Value.gameObject.SetActive(false);

        // The pad does what it always did (OffsetFor): left and right are the neighbours, up the
        // person across, down the first of the other two emotes. Key k points n-k chairs round.
        int up = n - n / 2;
        MoveArrow(PadLeft, KeyShowing(Digit(1)));
        MoveArrow(PadUp, KeyShowing(Digit(up)));
        MoveArrow(PadRight, KeyShowing(Digit(n - 1)));
        MoveArrow(PadDown, KeyShowing(Digit(n)));
        _numberedFor = n;

        if (_announcedFor != n)
        {
            _announcedFor = n;
            Plugin.Log.LogInfo($"[emote] emote keys at a table of {n}: middle finger {finger} (left to right " +
                               $"as the other players sit), the other two emotes {Digit(n)} and {Digit(n + 1)}; " +
                               $"pad arrows ({_arrows.Count} found) left on {Digit(1)}, up on {Digit(up)}, " +
                               $"right on {Digit(n - 1)}, down on {Digit(n)}");
        }
    }

    private static void Place(int digit, Vector2 at, HashSet<int> used)
    {
        var rt = KeyShowing(digit);
        if (rt == null) return;
        rt.anchoredPosition = at;
        if (!rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
        used.Add(digit);
    }

    // ----------------------------------------------------------- making a key the game has not got

    // The blank cap is made once and kept for every table after; the font belongs to the
    // scene it came from, so it is looked up again for each new panel.
    private static Sprite _blank;
    private static bool _blankTried;
    private static Color _digitColour = new(0.98f, 0.82f, 0.33f, 1f);
    private static TMPro.TMP_FontAsset _font;
    private static bool _fontLooked;

    /// <summary>
    /// A copy of the game's key 3 - its cap, its press animation - with another number on it.
    /// The game draws its numbers into each key's picture and has none past 5, so the copy gets
    /// a blank cap made from key 3's own picture, and the number is written on in the panel's
    /// own font and the key's own yellow. Any D-pad arrow that comes with the copy is left off:
    /// it would be a second one, and the game's own arrows are moved onto whichever keys match
    /// the pad (see <see cref="Lay"/>).
    /// </summary>
    private static RectTransform MakeKey(int digit)
    {
        try
        {
            if (_blank == null && !_blankTried)
            {
                _blankTried = true;
                var image = _own[3].GetComponent<Image>();
                _blank = image != null && image.sprite != null ? BlankCap(image.sprite) : null;
            }
            if (_blank == null) return null;

            if (!_fontLooked)
            {
                _fontLooked = true;
                _font = PanelFont(_own[3]);
            }

            var go = UnityEngine.Object.Instantiate(_own[3].gameObject, _own[3].parent, false);
            go.name = digit.ToString();

            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                var child = go.transform.GetChild(i);
                if (child != null && child.GetComponent<GamepadButtonEffect>() != null)
                    UnityEngine.Object.Destroy(child.gameObject);
            }

            var img = go.GetComponent<Image>();
            if (img != null) img.sprite = _blank;

            var press = go.GetComponent<KeyAnim>();
            if (press != null) press.Key = KeyCode.Alpha0 + digit;

            WriteNumber(go, digit);
            return go.GetComponent<RectTransform>();
        }
        catch (Exception e)
        {
            Report($"could not make key {digit}", e);
            return null;
        }
    }

    /// <summary>
    /// The number, where the game's own numbers sit on a key: up and to the left of centre, in
    /// a slanted bold face about a third of the key's height.
    /// </summary>
    private static void WriteNumber(GameObject key, int digit)
    {
        var holder = key.GetComponent<RectTransform>();
        float size = holder != null && holder.rect.height > 1f ? holder.rect.height : 30f;
        float u = size / 30f;

        var go = new GameObject("number");
        go.layer = key.layer;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(key.transform, false);

        var text = go.AddComponent<TMPro.TextMeshProUGUI>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(20f * u, 18f * u);
        rt.anchoredPosition = new Vector2(-3f * u, 5f * u);

        if (_font != null) text.font = _font;
        text.text = digit.ToString();
        text.fontSize = 14f * u;
        text.fontStyle = TMPro.FontStyles.Italic;
        text.color = _digitColour;
        text.alignment = TMPro.TextAlignmentOptions.Center;
        text.enableWordWrapping = false;
        text.raycastTarget = false;
    }

    /// <summary>The panel's own font: the HUD's bold face if it can be found, any of its fonts if not.</summary>
    private static TMPro.TMP_FontAsset PanelFont(Transform near)
    {
        try
        {
            var canvas = near.GetComponentInParent<Canvas>();
            if (canvas == null) return null;

            TMPro.TMP_FontAsset any = null;
            foreach (var t in canvas.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
            {
                var f = t != null ? t.font : null;
                if (f == null) continue;
                if (f.name != null && f.name.Contains("Oswald Bold")) return f;
                if (any == null) any = f;
            }
            return any;
        }
        catch { return null; }
    }

    /// <summary>
    /// Key 3's picture with its number taken off.
    ///
    /// The game's key pictures cannot be read from code as they are, so the picture is drawn
    /// into a scratch texture and read back from there. The number sits in the upper left of
    /// the cap and the right half is plain, so the band the number crosses is filled from the
    /// plain cap beside it and the left half is then mirrored from the right. The key's yellow
    /// is taken from the brightest point of the old number on the way.
    /// </summary>
    private static Sprite BlankCap(Sprite from)
    {
        var source = from.texture;
        var r = from.textureRect;
        int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
        if (source == null || w < 16 || h < 16) return null;

        var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var scratch = RenderTexture.GetTemporary(source.width, source.height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var was = RenderTexture.active;
        try
        {
            Graphics.Blit(source, scratch);
            RenderTexture.active = scratch;
            copy.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0, false);
        }
        finally
        {
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(scratch);
        }

        var px = copy.GetPixels32();
        var p = new Color32[w * h];
        for (int i = 0; i < p.Length; i++) p[i] = px[i];

        // The number is in the top half of the cap. Found in the bottom half, the read came
        // back upside down - put it the right way up first.
        int top = Bright(p, w, Box(w, 7), Box(w, 16), Box(h, 15), Box(h, 25));
        int low = Bright(p, w, Box(w, 7), Box(w, 16), h - 1 - Box(h, 25), h - 1 - Box(h, 15));
        if (low > top) FlipRows(p, w, h);

        // The key's yellow: the brightest point of its number.
        int best = -1;
        for (int y = Box(h, 13); y <= Box(h, 26); y++)
            for (int x = Box(w, 5); x <= Box(w, 17); x++)
            {
                var c = p[y * w + x];
                int v = c.r + c.g;
                if (c.a > 200 && v > best) { best = v; _digitColour = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f); }
            }

        int half = w / 2, plain = Box(w, 5);
        for (int y = Box(h, 12); y < Box(h, 27); y++)
            for (int x = half; x < Box(w, 18); x++)
                p[y * w + x] = p[y * w + x + plain];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < half; x++)
                p[y * w + x] = p[y * w + (w - 1 - x)];

        for (int i = 0; i < p.Length; i++) px[i] = p[i];
        copy.SetPixels32(px);
        copy.filterMode = source.filterMode;
        copy.wrapMode = TextureWrapMode.Clamp;
        copy.Apply(false, false);
        copy.name = "LiarsBar8P blank key";
        copy.hideFlags = HideFlags.DontUnloadUnusedAsset;

        var sprite = Sprite.Create(copy, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f),
            from.pixelsPerUnit, 0, SpriteMeshType.FullRect);
        sprite.name = copy.name;
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sprite;
    }

    /// <summary>A position on the game's 30 pixel key, at this key's size.</summary>
    private static int Box(int size, int at30) => Mathf.Clamp(Mathf.RoundToInt(at30 * size / 30f), 0, size - 1);

    private static int Bright(Color32[] p, int w, int x0, int x1, int y0, int y1)
    {
        int count = 0;
        for (int y = Math.Max(0, y0); y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var c = p[y * w + x];
                if (c.a > 120 && c.r + c.g + c.b > 420) count++;
            }
        return count;
    }

    private static void FlipRows(Color32[] p, int w, int h)
    {
        for (int y = 0; y < h / 2; y++)
            for (int x = 0; x < w; x++)
            {
                int a = y * w + x, b = (h - 1 - y) * w + x;
                (p[a], p[b]) = (p[b], p[a]);
            }
    }

    // --------------------------------------------------------------- all or nothing

    private static bool? _ready;

    /// <summary>
    /// Whether every patch here went in. Sending a seat to a machine that cannot read one plays
    /// the sound and no gesture, so it is all of them or none of them.
    /// </summary>
    private static bool Ready()
    {
        if (_ready.HasValue) return _ready.Value;

        bool ok = Has(nameof(CharController.InvokeUserCode_PlayEmote1RPC__Int32), true)
               && Has(nameof(CharController.PlayEmote1Sfx), false)
               && Has(nameof(CharController.ProcessEmoteInput), false);
        _ready = ok;

        if (!ok)
            Plugin.Log.LogWarning("[emote] not every emote patch went in - leaving the middle finger " +
                                  "to the game's three directions");
        return ok;
    }

    private static bool Has(string method, bool postfix)
    {
        try
        {
            var original = AccessTools.Method(typeof(CharController), method);
            var info = original != null ? Harmony.GetPatchInfo(original) : null;
            if (info == null) return false;

            foreach (var p in postfix ? info.Postfixes : info.Prefixes)
                if (p.owner == Plugin.Guid && p.PatchMethod.DeclaringType == typeof(EmoteTargets)) return true;
        }
        catch { }
        return false;
    }
}
