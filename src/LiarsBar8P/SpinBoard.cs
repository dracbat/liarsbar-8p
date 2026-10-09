using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Puts every player on the Liar's Spin leaderboard, not just the first four.
///
/// The leaderboard is the column of name plates at the top left of the screen in Liar's Spin -
/// a name and a score per player, sorted best first, with the crown on the leader and the
/// target mark on whoever is lowest. It is <c>LeaderboardSpin</c>, and its rows are a list of
/// <c>LeaderBoardDataSpin</c> objects laid out in the scene: four of them, one per player the
/// game was built for, with their four places on screen read once when it starts up.
///
/// When a match begins the server asks every copy of the game to build the board
/// (<c>CreateLeaderboard</c>), and each one walks the players handing out rows - but only for as
/// long as there are rows left, so at eight players the walk stops at four and seats five to
/// eight never get one. Nothing complains; they are simply not on it. Every later re-sort
/// (<c>Rpc_RefreshOrder</c>, after each spin and each death) only moves the rows that were
/// handed out, and its places run out at four as well: anyone ranked lower is parked on the
/// fourth place.
///
/// So just before the board is built, the row list is made as long as the table: copies of the
/// game's last row, placed below it at the game's own spacing, and the list of places is
/// extended to match. The game's own code then hands them out, fills them, sorts them and
/// moves them exactly as it does its own four. If a column of eight would run off the screen,
/// the whole column is shrunk to fit instead.
///
/// The rows are networked objects, but nothing about them needs the network: the board is
/// built by every copy of the game for itself from its own list of players. The copies are
/// given the original's network identity to answer "am I the server" with, because the row
/// reads its player through Mirror and that question has to have an answer - but they are never
/// in that identity's list, so Mirror never sends or reads anything for them. Because each copy
/// of the game fills its added rows from its own player list, a re-sort first makes sure the
/// added rows hold exactly the players the game's own four do not, so nobody can appear twice.
///
/// With four players or fewer this changes nothing: no rows are added, and if a bigger match
/// had added some, the game's own places and sizes are put back first.
/// </summary>
internal static class SpinBoard
{
    /// <summary>Never shrink a row below this much of its own size; past it, let it run long.</summary>
    private const float SmallestFit = 0.55f;

    /// <summary>The board the state below was taken from, by instance id.</summary>
    private static int _boardId;

    /// <summary>How many rows the game's own board has, and where it put them.</summary>
    private static int _vanillaRows;
    private static Vector3[] _vanillaSlots;
    private static Vector3[] _vanillaScales;

    /// <summary>The rows this added, by their game object's instance id.</summary>
    private static readonly HashSet<int> _added = new();

    /// <summary>Whether the board's places or sizes are currently ours rather than the game's.</summary>
    private static bool _changed;

    private static int _lastReported = -1;

    // ------------------------------------------------------------------------- patches

    /// <summary>Make the board long enough for the table before the game hands out rows.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LeaderboardSpin), nameof(LeaderboardSpin.UserCode_CreateLeaderboard))]
    private static void BeforeCreate(LeaderboardSpin __instance)
    {
        try { Widen(__instance); }
        catch (Exception e) { Plugin.Log.LogError($"[spinboard] could not widen the leaderboard: {e.Message}"); }
    }

    /// <summary>The added rows hold whoever the game's own rows do not.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LeaderboardSpin), nameof(LeaderboardSpin.UserCode_CreateLeaderboard))]
    private static void AfterCreate(LeaderboardSpin __instance)
    {
        try { Reconcile(__instance); }
        catch (Exception e) { Plugin.Log.LogError($"[spinboard] could not fill the added rows: {e.Message}"); }
    }

    /// <summary>
    /// Check the same just before every re-sort, which places rows by who they hold - so a row
    /// holding the wrong player would be put in the wrong place.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LeaderboardSpin), nameof(LeaderboardSpin.UserCode_Rpc_RefreshOrder))]
    private static void BeforeResort(LeaderboardSpin __instance)
    {
        try { Reconcile(__instance); }
        catch (Exception e) { Plugin.Log.LogError($"[spinboard] could not check the added rows: {e.Message}"); }
    }

    // ------------------------------------------------------------------------- widening

    private static void Widen(LeaderboardSpin board)
    {
        if (board == null) return;
        var rows = board.leaderboardsObjects;
        if (rows == null || rows.Count == 0) return;

        var m = board.manager;
        if (m == null) m = Manager.Instance;
        int players = (m != null && m.Players != null) ? m.Players.Count : 0;

        Remember(board, rows);
        if (_vanillaRows < 1) return;

        if (players <= _vanillaRows)
        {
            // The game's own board fits. Put back anything a bigger match on this board changed.
            Restore(board, rows);
            return;
        }

        // Sixteen is the most a lobby can be configured for; anything above it is not a table.
        int need = Math.Min(players, 16);
        var template = rows[_vanillaRows - 1];
        if (template == null) return;

        int before = rows.Count;
        while (rows.Count < need)
        {
            var row = CloneRow(board, template, rows.Count - _vanillaRows + 1, rows[rows.Count - 1]);
            if (row == null) break;
            rows.Add(row);
        }

        // Added rows past this table's size are from a bigger match on the same board.
        for (int i = need; i < rows.Count; i++)
            if (IsAdded(rows[i])) rows[i].gameObject.SetActive(false);

        // The rows are already added, so the places have to keep up whatever happens: the game
        // indexes its list of places by row with a hard bounds check, and one place short would
        // end the whole board build at the first added row.
        float fit = 1f;
        try { fit = Lay(board, rows, Math.Min(need, rows.Count)); }
        finally { PadPlaces(board, rows.Count); }

        if (need != _lastReported)
        {
            _lastReported = need;
            int v = _vanillaRows;
            Vector3 step = v > 1 ? (_vanillaSlots[v - 1] - _vanillaSlots[0]) / (v - 1) : Vector3.zero;
            Plugin.Log.LogInfo(
                $"[spinboard] Liar's Spin leaderboard has {v} rows for {players} players - " +
                $"{(rows.Count > before ? $"added {rows.Count - before}" : "using the rows added before")}, " +
                $"{rows.Count} now; places start at {_vanillaSlots[0].ToString("F0")} " +
                $"every {step.ToString("F0")}" +
                (fit < 0.999f ? $", column shrunk to {fit:F2} to stay on screen" : ""));
        }
    }

    /// <summary>Take the game's own board as it is, the first time this board is seen.</summary>
    private static void Remember(LeaderboardSpin board, Il2CppSystem.Collections.Generic.List<LeaderBoardDataSpin> rows)
    {
        int id = board.GetInstanceID();
        if (id == _boardId && _vanillaSlots != null) return;

        _boardId = id;
        _added.Clear();
        _changed = false;
        _lastReported = -1;
        _vanillaRows = rows.Count;
        _vanillaSlots = new Vector3[_vanillaRows];
        _vanillaScales = new Vector3[_vanillaRows];

        var slots = board.slotPositions;
        for (int i = 0; i < _vanillaRows; i++)
        {
            var r = rows[i];
            _vanillaScales[i] = r != null ? r.transform.localScale : Vector3.one;
            _vanillaSlots[i] = slots != null && i < slots.Length ? slots[i]
                             : r != null ? r.transform.localPosition : Vector3.zero;
        }
    }

    /// <summary>The game's own places and sizes, and none of the added rows showing.</summary>
    /// <summary>
    /// Make sure there is a place for every row - the last place repeated if nothing better was
    /// worked out - so rows and places can never disagree.
    /// </summary>
    private static void PadPlaces(LeaderboardSpin board, int rowCount)
    {
        try
        {
            var places = board.slotPositions;
            int have = places != null ? places.Length : 0;
            if (have >= rowCount || have == 0) return;

            var padded = new Il2CppStructArray<Vector3>(rowCount);
            for (int i = 0; i < rowCount; i++) padded[i] = places[Math.Min(i, have - 1)];
            board.slotPositions = padded;
            Plugin.Log.LogWarning($"[spinboard] {rowCount - have} leaderboard place(s) were missing - repeated the last one");
        }
        catch { }
    }

    private static void Restore(LeaderboardSpin board, Il2CppSystem.Collections.Generic.List<LeaderBoardDataSpin> rows)
    {
        for (int i = 0; i < rows.Count; i++)
            if (IsAdded(rows[i]) && rows[i].gameObject.activeSelf) rows[i].gameObject.SetActive(false);

        if (!_changed) return;
        _changed = false;
        _lastReported = -1;   // a later widening of this board says so again

        var slots = new Il2CppStructArray<Vector3>(_vanillaRows);
        for (int i = 0; i < _vanillaRows; i++) slots[i] = _vanillaSlots[i];
        board.slotPositions = slots;

        for (int i = 0; i < _vanillaRows && i < rows.Count; i++)
            if (rows[i] != null) rows[i].transform.localScale = _vanillaScales[i];

        Plugin.Log.LogInfo($"[spinboard] {_vanillaRows} or fewer at the table - the leaderboard is the game's own again");
    }

    /// <summary>
    /// Copy one of the game's rows into a new, empty row next to it.
    ///
    /// Copied while the original is switched off, so nothing on the copy wakes up until the
    /// game itself switches the row on to use it. If the row carries a network identity of
    /// its own, the copy's is cleared the way <see cref="Cloning.CloneAsUnspawned"/> clears
    /// a podium's, or Mirror would destroy it as a duplicate.
    /// </summary>
    private static LeaderBoardDataSpin CloneRow(LeaderboardSpin board, LeaderBoardDataSpin template, int k,
                                                LeaderBoardDataSpin after)
    {
        GameObject go = null;
        try
        {
            var src = template.gameObject;
            var parent = src.transform.parent;
            bool was = src.activeSelf;

            src.SetActive(false);
            try { go = UnityEngine.Object.Instantiate(src, parent, false); }
            finally { src.SetActive(was); }

            go.name = $"{src.name}_8P{k}";
            go.SetActive(false);

            bool ownIdentity = false;
            foreach (var ni in go.GetComponentsInChildren<Mirror.NetworkIdentity>(true))
            {
                if (ni == null) continue;
                ownIdentity = true;
                ni.netId = 0;
                ni.sceneId = 0;
                ni.hasSpawned = false;
                ni.isServer = false;
                ni.isClient = false;
                ni.isLocalPlayer = false;
            }

            var row = go.GetComponent<LeaderBoardDataSpin>();
            if (row == null)
            {
                UnityEngine.Object.Destroy(go);
                Plugin.Log.LogWarning("[spinboard] a copied leaderboard row has no row component - not adding rows");
                return null;
            }

            // The row reads its player through Mirror, which asks the row's network identity
            // whether this is the server. A copy is not in any identity's list, so it has to be
            // told which one to ask.
            if (row.netIdentity == null) row.netIdentity = template.netIdentity;

            // Start empty. Who it shows, whether it holds the turn and whether it is the target
            // are all the game's to decide.
            row.player = null;
            if (row.TargetIcon != null) row.TargetIcon.SetActive(false);
            if (row.TurnObject != null) row.TurnObject.SetActive(false);
            if (row.deadIcon != null) row.deadIcon.SetActive(false);
            DropCrownCopy(board, src.transform, go.transform);

            if (after != null && after.transform.parent == parent)
                go.transform.SetSiblingIndex(after.transform.GetSiblingIndex() + 1);

            _added.Add(go.GetInstanceID());

            if (k == 1)
                Plugin.Log.LogInfo($"[spinboard] leaderboard rows are copied from '{src.name}' " +
                                   (ownIdentity ? "(each row has its own network identity, cleared on the copy)"
                                                : "(rows share the board's network identity)"));
            return row;
        }
        catch (Exception e)
        {
            if (go != null) UnityEngine.Object.Destroy(go);
            Plugin.Log.LogWarning($"[spinboard] could not copy a leaderboard row: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The board has one crown, which it switches on and off by itself. If it hangs off the
    /// copied row, the copy would carry a second one nobody ever switches off.
    /// </summary>
    private static void DropCrownCopy(LeaderboardSpin board, Transform src, Transform copy)
    {
        var crown = board.crown;
        if (crown == null || !crown.transform.IsChildOf(src) || crown.transform == src) return;

        var path = new List<string>();
        for (var t = crown.transform; t != null && t != src; t = t.parent) path.Insert(0, t.name);

        // Switched off rather than destroyed: a destroy only happens at the end of the frame,
        // and the row finds its parts by child position when it wakes, so a crown copy still
        // counted among the children could shift them. The game only ever toggles its own
        // crown, so this one simply stays hidden.
        var twin = copy.Find(string.Join("/", path));
        if (twin != null) twin.gameObject.SetActive(false);
    }

    /// <summary>
    /// Give the board a place for every row, and put the added rows in theirs.
    ///
    /// The game's four places are kept exactly and the column carries on below them at the
    /// game's own spacing. Only if that would leave the screen is the whole column - the game's
    /// rows too, so it stays one even column - drawn closer together and smaller.
    /// Returns how much it was shrunk: one for not at all.
    /// </summary>
    private static float Lay(LeaderboardSpin board, Il2CppSystem.Collections.Generic.List<LeaderBoardDataSpin> rows, int need)
    {
        int v = _vanillaRows;
        Vector3 first = _vanillaSlots[0];
        Vector3 step = v > 1 ? (_vanillaSlots[v - 1] - first) / (v - 1) : Vector3.zero;

        if (step.sqrMagnitude < 0.01f)
        {
            // The game's places are all one place: something else lays these rows out, and
            // there is no spacing to carry on. Leave the copies where their original is.
            Plugin.Log.LogWarning("[spinboard] the leaderboard's places are not spaced apart - " +
                                  "added rows are left on the game's last place");
        }

        // Measured at the game's own size, whatever an earlier match on this board left it at.
        if (rows[0] != null) rows[0].transform.localScale = _vanillaScales[0];

        Vector3 last = need <= v ? _vanillaSlots[need - 1] : _vanillaSlots[v - 1] + step * (need - v);
        float fit = step.sqrMagnitude < 0.01f ? 1f : Fit(rows[0], first, last);

        int count = Math.Max(rows.Count, v);
        var slots = new Il2CppStructArray<Vector3>(count);
        for (int i = 0; i < count; i++)
        {
            if (fit >= 0.999f)
                slots[i] = i < v ? _vanillaSlots[i] : _vanillaSlots[v - 1] + step * (i - v + 1);
            else
                slots[i] = first + step * (fit * i);
        }
        board.slotPositions = slots;
        _changed = true;

        Vector3 baseScale = _vanillaScales[v - 1];
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r == null) continue;
            var scale = i < v ? _vanillaScales[i] : baseScale;
            r.transform.localScale = fit >= 0.999f ? scale : scale * fit;
            if (i >= v) r.transform.localPosition = slots[i];
        }

        return fit;
    }

    /// <summary>
    /// How much the column has to shrink to stay on screen from its first place to its last,
    /// measured along the direction it grows. One if it fits as it is, or if it cannot be told.
    /// </summary>
    private static float Fit(LeaderBoardDataSpin sample, Vector3 first, Vector3 last)
    {
        try
        {
            var rt = sample != null ? sample.transform.TryCast<RectTransform>() : null;
            var parent = rt != null ? rt.parent : null;
            var canvas = rt != null ? rt.GetComponentInParent<Canvas>() : null;
            if (rt == null || parent == null || canvas == null) return 1f;

            var root = canvas.rootCanvas;
            Camera cam = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;

            var corners = new Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(corners);
            Vector3 here = rt.localPosition;

            Rect At(Vector3 local)
            {
                Vector3 shift = parent.TransformVector(local - here);
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                for (int c = 0; c < 4; c++)
                {
                    Vector2 s = RectTransformUtility.WorldToScreenPoint(cam, corners[c] + shift);
                    x0 = Mathf.Min(x0, s.x); x1 = Mathf.Max(x1, s.x);
                    y0 = Mathf.Min(y0, s.y); y1 = Mathf.Max(y1, s.y);
                }
                return Rect.MinMaxRect(x0, y0, x1, y1);
            }

            Rect top = At(first);
            Rect end = At(last);

            float w = Screen.width, h = Screen.height;
            if (w < 1f || h < 1f) return 1f;
            float margin = 0.01f * h;

            Vector2 grow = end.center - top.center;
            float room, span;
            if (Mathf.Abs(grow.y) >= Mathf.Abs(grow.x))
            {
                if (grow.y < 0f) { room = top.yMax - margin; span = top.yMax - end.yMin; }
                else             { room = h - margin - top.yMin; span = end.yMax - top.yMin; }
            }
            else
            {
                if (grow.x < 0f) { room = top.xMax - margin; span = top.xMax - end.xMin; }
                else             { room = w - margin - top.xMin; span = end.xMax - top.xMin; }
            }

            if (span <= 0f || room >= span) return 1f;
            return Mathf.Clamp(room / span, SmallestFit, 1f);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[spinboard] could not measure the leaderboard on screen: {e.Message}");
            return 1f;
        }
    }

    // ------------------------------------------------------------------------- who is shown

    /// <summary>
    /// Make the added rows hold exactly the players the game's own rows do not.
    ///
    /// Every copy of the game fills its rows from its own player list, and the game's own rows
    /// are then overwritten by what the server put in them. If a copy's list is in a different
    /// order from the server's, its added rows could end up holding someone a game row already
    /// shows - one player twice, another missing. So the added rows are given whoever is left,
    /// in the order the players are listed. On the host, and wherever the lists agree, this
    /// changes nothing.
    /// </summary>
    private static void Reconcile(LeaderboardSpin board)
    {
        if (board == null || _added.Count == 0 || board.GetInstanceID() != _boardId) return;

        var active = board.ActivatedleaderboardsObjects;
        if (active == null || active.Count <= _vanillaRows) return;

        var m = board.manager;
        if (m == null) m = Manager.Instance;
        if (m == null || m.Players == null) return;

        var shown = new HashSet<int>();
        var extra = new List<LeaderBoardDataSpin>();
        foreach (var row in active)
        {
            if (row == null) continue;
            if (IsAdded(row)) { extra.Add(row); continue; }
            var p = row.Networkplayer;
            if (p != null) shown.Add(p.GetInstanceID());
        }
        if (extra.Count == 0) return;

        // Everyone the game's rows leave out, in the order the players are listed.
        var left = new List<GameObject>();
        var leftIds = new HashSet<int>();
        foreach (var p in m.Players)
        {
            if (p == null) continue;
            var go = p.gameObject;
            if (go == null || !shown.Add(go.GetInstanceID())) continue;
            left.Add(go);
            leftIds.Add(go.GetInstanceID());
        }

        // An added row already holding one of them keeps it; the rest are handed out in order.
        var claimed = new HashSet<int>();
        var wrong = new List<LeaderBoardDataSpin>();
        foreach (var row in extra)
        {
            var now = row.Networkplayer;
            int id = now != null ? now.GetInstanceID() : 0;
            if (now != null && leftIds.Contains(id) && claimed.Add(id)) continue;
            wrong.Add(row);
        }

        int moved = 0, next = 0;
        foreach (var row in wrong)
        {
            while (next < left.Count && claimed.Contains(left[next].GetInstanceID())) next++;

            if (next < left.Count)
            {
                claimed.Add(left[next].GetInstanceID());
                row.Networkplayer = left[next];
                moved++;
            }
            else if (row.Networkplayer != null)
            {
                // Nobody left for it - it was showing someone already on the board. An empty
                // row hides itself, which is right: one player, one row.
                row.Networkplayer = null;
                row.player = null;
                moved++;
            }
        }

        if (moved > 0)
            Dev.Log("spinboard", $"{moved} added leaderboard row(s) given to players the game's own rows were not showing");
    }

    private static bool IsAdded(LeaderBoardDataSpin row)
    {
        try { return row != null && _added.Contains(row.gameObject.GetInstanceID()); }
        catch { return false; }
    }
}
