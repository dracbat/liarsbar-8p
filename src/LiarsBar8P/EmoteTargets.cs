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
/// key per other player, laid out left to right in the order they sit round the table from
/// where you are. 1, 2 and 3 keep the meaning they always had - the person on your left, the
/// person across, the person on your right - so nothing anybody already knows changes, and the
/// D-pad still reaches those three. The people in between get 6, 7, 8 and 9 (and 0 at nine
/// players). At eight the row reads 1 6 7 2 8 9 3: press the key under where somebody sits.
/// The new keys are copies of the game's own key 3 - the same key cap, the same press
/// animation, the same place in the panel - with the number changed, so the panel looks like
/// the game's own panel with more keys in it rather than something laid over it.
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

    /// <summary>The keys for the people between your neighbours and across, in the order they are handed out.</summary>
    private static readonly int[] ExtraDigits = { 6, 7, 8, 9, 0 };

    private static readonly KeyCode[] ExtraKeys =
        { KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0 };

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
    /// 1, 2 or 3 - pressed, or the D-pad - at five or more: send the person on your left, the
    /// person across, or the person on your right, as a seat. The game's own key handling is
    /// left to decide whether and when; this only changes what it sends.
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
            Plugin.Log.LogInfo($"[emote] seat {me} pressed {direction}: the finger at seat {target} of {n}");
            direction = Code + target;
        }
        catch (Exception e) { Report("could not aim the finger", e); }
    }

    /// <summary>
    /// The keys the game does not have: 6, 7, 8, 9 and 0, for the people between. Only ever
    /// called by the game when an emote may be sent this frame - alive, emotes on, not cooling
    /// down - so all of that is already decided. A press is sent the way the game sends 1 to 3
    /// and starts the same cooldown; nothing pressed, and the game's own handling runs as
    /// shipped. Not while typing in the chat or paused - the same rule the game's own keys on
    /// the panel follow for their press animation - because these are digits people type.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharController), nameof(CharController.ProcessEmoteInput))]
    private static bool Extra(CharController __instance)
    {
        try
        {
            int digit = ExtraKeyDown();
            if (digit < 0) return true;
            if (!Ready() || !AimRing.Who(__instance, out var m, out int me, out int n)) return true;
            if (m.Chatting || m.GamePaused) return true;

            int off = OffsetForDigit(digit, n);
            if (off < 1) return true;   // no seat has that key at this size of table

            int target = (me + off) % n;
            Plugin.Log.LogInfo($"[emote] seat {me} pressed {digit}: the finger at seat {target} of {n}");
            __instance.PlayEmote1Sfx(Code + target);
            __instance.EmoteReadyy = false;
            return false;
        }
        catch (Exception e)
        {
            Report("could not read the extra keys", e);
            return true;
        }
    }

    private static int ExtraKeyDown()
    {
        for (int i = 0; i < ExtraKeys.Length; i++)
            if (Input.GetKeyDown(ExtraKeys[i])) return ExtraDigits[i];
        return -1;
    }

    // ---------------------------------------------------------------- who each key means

    /// <summary>Chairs round from me for 1, 2 and 3: the one before me, the one across, the next one.</summary>
    private static int OffsetFor(int direction, int n) =>
        direction == 1 ? n - 1 : direction == 2 ? n / 2 : 1;

    /// <summary>
    /// The key for each chair round from me (index 1 to n-1), or -1 for none. 1 is the chair on
    /// the left, 3 the chair on the right, 2 the one across - the seat the revolver's aim calls
    /// straight ahead - and the rest are handed 6, 7, 8, 9, 0 from left to right.
    /// </summary>
    private static int[] KeysFor(int n)
    {
        if (_keysFor == n && _keys != null) return _keys;

        var keys = new int[n];
        for (int i = 0; i < n; i++) keys[i] = -1;
        keys[n - 1] = 1;
        keys[n / 2] = 2;
        keys[1] = 3;

        int next = 0;
        for (int off = n - 1; off >= 1; off--)
        {
            if (keys[off] != -1) continue;
            if (next < ExtraDigits.Length) keys[off] = ExtraDigits[next++];
        }

        _keys = keys;
        _keysFor = n;
        return keys;
    }

    private static int[] _keys;
    private static int _keysFor;

    private static int OffsetForDigit(int digit, int n)
    {
        var keys = KeysFor(n);
        for (int off = 1; off < n; off++)
            if (keys[off] == digit) return off;
        return -1;
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
    private static RectTransform _one, _two, _three;
    private static Vector2 _onePos, _twoPos, _threePos;
    private static bool _found;
    private static readonly Dictionary<int, RectTransform> _extra = new();
    private static int _laidFor;
    private static int _announcedFor;
    private static bool _noKeysReported;

    /// <summary>
    /// Four times a second, from <see cref="ModTicker"/>: lay the row of keys out for the size
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
        int want = n > Limits.VanillaPlayers ? n : 0;
        if (want == _laidFor) return;
        if (want > 0 && !Ready()) return;

        Lay(want);
        _laidFor = want;
    }

    /// <summary>A new table, a new panel: find the game's three keys and remember where they sit.</summary>
    private static void Adopt(GameObject panel)
    {
        _panel = panel;
        _panelKey = panel.Pointer;
        _extra.Clear();
        _laidFor = 0;
        _announcedFor = 0;
        _found = false;
        _font = null;
        _fontLooked = false;

        var t = panel.transform;
        _one = Key(t, "1");
        _two = Key(t, "2");
        _three = Key(t, "3");
        if (_one == null || _two == null || _three == null) return;

        _onePos = _one.anchoredPosition;
        _twoPos = _two.anchoredPosition;
        _threePos = _three.anchoredPosition;
        _found = true;
    }

    private static RectTransform Key(Transform panel, string name)
    {
        var t = panel.Find(name);
        return t != null ? t.GetComponent<RectTransform>() : null;
    }

    /// <summary>
    /// One key per other player, left to right as they sit, ending where key 3 always was -
    /// tucked under the finger - and growing to the left at the game's own spacing. At four or
    /// fewer (<paramref name="n"/> zero) the game's three keys go back where they came from.
    /// </summary>
    private static void Lay(int n)
    {
        if (n == 0)
        {
            _one.anchoredPosition = _onePos;
            _two.anchoredPosition = _twoPos;
            _three.anchoredPosition = _threePos;
            foreach (var kv in _extra)
                if (kv.Value != null) kv.Value.gameObject.SetActive(false);
            return;
        }

        var keys = KeysFor(n);

        // Every key the row needs, before anything moves: half a row is worse than the game's.
        for (int off = 1; off < n; off++)
        {
            int d = keys[off];
            if (d < 0 || (d >= 1 && d <= 3)) continue;   // none, or one of the game's own three
            if (_extra.TryGetValue(d, out var have) && have != null) continue;

            var made = MakeKey(d);
            if (made == null)
            {
                if (!_noKeysReported)
                {
                    _noKeysReported = true;
                    Plugin.Log.LogWarning("[emote] could not make the extra keys for the panel - " +
                                          "6 to 9 still work, the row shows the game's three");
                }
                return;
            }
            _extra[d] = made;
        }

        float step = (_threePos.x - _onePos.x) / 2f;
        var used = new HashSet<int>();
        var order = new System.Text.StringBuilder();

        int slot = 0, slots = 0;
        for (int off = n - 1; off >= 1; off--) if (keys[off] >= 0) slots++;

        for (int off = n - 1; off >= 1; off--)
        {
            int d = keys[off];
            if (d < 0) continue;

            var rt = d == 1 ? _one : d == 2 ? _two : d == 3 ? _three : _extra[d];
            rt.anchoredPosition = new Vector2(_threePos.x - (slots - 1 - slot) * step, _threePos.y);
            if (!rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
            used.Add(d);
            order.Append(order.Length == 0 ? "" : " ").Append(d);
            slot++;
        }

        foreach (var kv in _extra)
            if (!used.Contains(kv.Key) && kv.Value != null) kv.Value.gameObject.SetActive(false);

        if (_announcedFor != n)
        {
            _announcedFor = n;
            Plugin.Log.LogInfo($"[emote] middle finger at a table of {n}: keys {order} under the finger, " +
                               "left to right as the other players sit");
        }
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
    /// A copy of the game's key 3 - its cap, its press animation, its place in the panel - with
    /// another number on it. The game draws its numbers into each key's picture and has none
    /// past 5, so the copy gets a blank cap made from key 3's own picture, and the number is
    /// written on in the panel's own font and the key's own yellow. The D-pad arrow that comes
    /// with key 3 is left off: no pad button reaches these seats.
    /// </summary>
    private static RectTransform MakeKey(int digit)
    {
        try
        {
            if (_blank == null && !_blankTried)
            {
                _blankTried = true;
                var image = _three.GetComponent<Image>();
                _blank = image != null && image.sprite != null ? BlankCap(image.sprite) : null;
            }
            if (_blank == null) return null;

            if (!_fontLooked)
            {
                _fontLooked = true;
                _font = PanelFont(_three);
            }

            var go = UnityEngine.Object.Instantiate(_three.gameObject, _three.parent, false);
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
