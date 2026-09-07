using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// Gives players beyond the fourth a podium in the pre-game lobby.
///
/// The lobby has four podiums (<c>LobbyController.SpawnSlots</c>), and the host's code
/// leans on that list in two places:
///
///     CmdSetPlayer:  slot = SpawnSlots.Where(s =&gt; !s.Dolu).First()    // throws once all four are taken
///     Update:        slot = SpawnSlots.First(s =&gt; s.name == SlotName)  // every frame until the player is marked Loaded
///
/// A fifth player throws in the first, so they get no podium, no lobby avatar and no
/// <c>SlotName</c>; and because the second runs every frame until it succeeds, they never
/// become Loaded either. That is the reported "player 5 spawns in game but is missing from
/// the lobby".
///
/// A podium is a <c>LobbySlot</c> - a NetworkBehaviour on a scene object - and more of
/// them cannot be spawned through Mirror: runtime spawning needs a build-time assetId a
/// scene object does not have, and duplicating a spawned identity corrupts spawn handling
/// and disconnects everyone (that happened, and cost several sessions).
///
/// What works instead: a podium copy with a *fresh, never-spawned* identity. Mirror
/// ignores it completely (sceneId 0, netId 0), but it is a real, functioning LobbySlot,
/// so every piece of game code that walks <c>SpawnSlots</c> or does
/// <c>GameObject.Find(SlotName)</c> is satisfied. Every peer builds the same copies with
/// the same names at the same positions, computed from the same scene.
///
/// The one thing Mirror will not do for a copy is sync its SyncVars (occupied, name,
/// ready). The host sets them through the game's own code; clients set them themselves
/// from what *is* synced - each player's <c>SlotName</c>, name and ready flag.
/// </summary>
internal static class LobbyPodiums
{
    /// <summary>Podium names this mod creates. Deterministic, so every peer agrees.</summary>
    internal const string Prefix = "Slot8P_";

    private static readonly Dictionary<int, LobbySlot> _podiums = new();
    private static float _nextSweep;
    private static bool _geometryFailed;
    private static bool _reported;

    // --------------------------------------------------------------------- build

    /// <summary>Build every extra podium as soon as the lobby exists, on host and clients alike.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void OnLobbyStart(LobbyController __instance)
    {
        // A new lobby means a new scene: the previous copies are gone with it, and the
        // bar can be a different one with its podiums arranged differently. Nothing
        // learned about the last one should decide anything about this one.
        _podiums.Clear();
        _geometryFailed = false;
        _floorTest = 0;
        _cam = null;
        _camHolding = false;
        _camDone = false;
        _camTries = 0;
        _rigReported = false;
        _vcam = null;
        _pulledBack = false;
        _blendStarted = 0f;
        _plateHome.Clear();
        BuildAll(__instance);
    }

    private static void BuildAll(LobbyController lobby)
    {
        try
        {
            if (lobby == null || lobby.SpawnSlots == null) return;
            int built = 0;
            for (int i = Limits.VanillaPlayers; i < Limits.Max; i++)
                if (Ensure(i, lobby) != null) built++;

            if (!_reported && built > 0)
            {
                _reported = true;
                Plugin.Log.LogInfo($"[podium] lobby has {lobby.SpawnSlots.Count} podiums for up to {Limits.Max} players");
            }

            // The shot has to take in two rows of podiums now. The camera may not exist on
            // the frame the lobby starts, so this is tried again from the sweep until it
            // either settles or gives up.
            FrameLobby(lobby);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] build failed: {e.Message}");
        }
    }

    /// <summary>The podium for this index, created and registered on first use. Null if it cannot be built.</summary>
    private static LobbySlot Ensure(int index, LobbyController lobby)
    {
        if (_podiums.TryGetValue(index, out var existing) && existing != null) return existing;
        if (_geometryFailed || lobby == null) return null;

        try
        {
            var slots = lobby.SpawnSlots;
            if (slots == null || slots.Count < 3) { _geometryFailed = true; return null; }

            // A copy this mod made earlier may already be in the list (the scene was kept but
            // the dictionary was not - host migration, for one). Adopt it rather than add twins.
            string name = Prefix + index;
            foreach (var s in slots)
                if (s != null && s.gameObject != null && s.gameObject.name == name)
                {
                    _podiums[index] = s;
                    return s;
                }

            var vanilla = new List<LobbySlot>();
            foreach (var s in slots)
                if (s != null && s.transform != null && !s.gameObject.name.StartsWith(Prefix)) vanilla.Add(s);
            if (vanilla.Count < 3) { _geometryFailed = true; return null; }

            if (!RowSeat(vanilla, index, out Vector3 pos, out Quaternion rot))
            {
                _geometryFailed = true;
                Plugin.Log.LogWarning("[podium] could not work out where extra podiums go - " +
                                      "players beyond four will only be seated once the match starts");
                return null;
            }

            var clone = Cloning.CloneAsUnspawned(vanilla[vanilla.Count - 1].transform, name);
            if (clone == null) return null;

            clone.position = pos;
            clone.rotation = rot;

            var slot = clone.GetComponent<LobbySlot>();
            if (slot == null)
            {
                Plugin.Log.LogError("[podium] the copied podium lost its LobbySlot component");
                UnityEngine.Object.Destroy(clone.gameObject);
                return null;
            }

            // The template may have been occupied; the copy starts empty.
            slot.NetworkDolu = false;
            slot.NetworkPlayerName = "";
            slot.NetworkReady = false;

            GiveOwnPanel(slot, vanilla, index);

            slots.Add(slot);
            _podiums[index] = slot;
            Plugin.Log.LogInfo($"[podium] built {name} at {pos}");
            return slot;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] could not build podium {index}: {e.Message}");
            _geometryFailed = true;
            return null;
        }
    }

    // ----------------------------------------------------------------- lobby UI

    /// <summary>
    /// Give a copied podium its own name plate.
    ///
    /// A podium has no children: its name text, ready icon, character panel and kick
    /// button live in the shared lobby canvas, one group per podium, and the LobbySlot
    /// only holds references to them. Copying the podium copies those references, so
    /// without this every copy would drive the *same* group as the podium it was copied
    /// from - four slots fighting over one name plate.
    ///
    /// So the canvas group is copied too, placed by continuing the spacing the existing
    /// groups use, and the copy's references are re-pointed into it. If the layout cannot
    /// be worked out, the references are cleared instead: an extra player with no name
    /// plate is a blemish, four players overwriting someone else's is a bug.
    /// </summary>
    private static void GiveOwnPanel(LobbySlot slot, List<LobbySlot> vanilla, int index)
    {
        try
        {
            var template = vanilla[vanilla.Count - 1];
            var templatePanel = template.NameText != null ? template.NameText.transform.parent : null;
            if (templatePanel == null) { ClearUi(slot); return; }

            var panel = UnityEngine.Object.Instantiate(templatePanel.gameObject, templatePanel.parent);
            panel.name = $"Panel8P_{index}";

            if (!PlacePanel(panel.transform, templatePanel, template.transform, slot.transform, vanilla, index))
            {
                UnityEngine.Object.Destroy(panel);
                ClearUi(slot);
                return;
            }

            slot.NameText     = Bind<TMPro.TextMeshProUGUI>(panel.transform, templatePanel, template.NameText != null ? template.NameText.transform : null);
            slot.CharNameText = Bind<TMPro.TextMeshProUGUI>(panel.transform, templatePanel, template.CharNameText != null ? template.CharNameText.transform : null);
            slot.SelectChar   = Find(panel.transform, templatePanel, template.SelectChar != null ? template.SelectChar.transform : null);
            slot.ReadyIcon    = Find(panel.transform, templatePanel, template.ReadyIcon != null ? template.ReadyIcon.transform : null);

            // The kick button's click handler still points at the podium it was copied
            // from, so pressing it would kick the wrong player. It is removed rather than
            // rewired - kicking from the original four panels still works.
            var kick = Find(panel.transform, templatePanel, template.KickB != null ? template.KickB.transform : null);
            if (kick != null) kick.SetActive(false);
            slot.KickB = null;

            Plugin.Log.LogInfo($"[podium] {slot.gameObject.name} has its own name plate ({panel.name})");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] could not give podium {index} its own name plate: {e.Message}");
            ClearUi(slot);
        }
    }

    /// <summary>
    /// Put a copied name plate where the new podium is.
    ///
    /// The lobby's plates hang in the world next to the podium they belong to, so the
    /// right placement is the template plate's offset *from its own podium*, re-applied
    /// to the new one - which follows the curve of the podium arc instead of running off
    /// in a straight line. If the plates turn out to be flat screen overlays instead, the
    /// spacing between the existing ones is continued, which is the only thing that means
    /// anything there.
    /// </summary>
    private static bool PlacePanel(Transform panel, Transform templatePanel,
                                   Transform templatePodium, Transform newPodium,
                                   List<LobbySlot> vanilla, int index)
    {
        var canvas = panel.GetComponentInParent<Canvas>();
        bool world = canvas == null || canvas.renderMode == RenderMode.WorldSpace;

        if (world && templatePodium != null && newPodium != null)
        {
            var local = templatePodium.InverseTransformPoint(templatePanel.position);
            panel.position = newPodium.TransformPoint(local);
            panel.rotation = newPodium.rotation *
                             (Quaternion.Inverse(templatePodium.rotation) * templatePanel.rotation);
            Plugin.Log.LogInfo($"[podium] {panel.name} placed in the world at {panel.position}");
            return true;
        }

        var rects = new List<RectTransform>();
        foreach (var v in vanilla)
        {
            var p = v.NameText != null ? v.NameText.transform.parent : null;
            var rt = p != null ? p.TryCast<RectTransform>() : null;
            if (rt != null && rt.parent == templatePanel.parent) rects.Add(rt);
        }
        if (rects.Count < 2) return false;
        rects.Sort((a, b) => a.GetSiblingIndex().CompareTo(b.GetSiblingIndex()));

        Vector2 step = (rects[rects.Count - 1].anchoredPosition - rects[0].anchoredPosition) / (rects.Count - 1);
        if (step.sqrMagnitude < 0.01f) return false;

        var rect = panel.TryCast<RectTransform>();
        if (rect == null) return false;
        rect.anchoredPosition = rects[rects.Count - 1].anchoredPosition + step * (index - Limits.VanillaPlayers + 1);
        panel.SetSiblingIndex(rects[rects.Count - 1].GetSiblingIndex() + 1 + (index - Limits.VanillaPlayers));
        Plugin.Log.LogInfo($"[podium] {panel.name} placed on screen at {rect.anchoredPosition}");
        return true;
    }

    /// <summary>Leave a copy driving nothing rather than driving somebody else's plate.</summary>
    private static void ClearUi(LobbySlot slot)
    {
        try
        {
            slot.NameText = null;
            slot.CharNameText = null;
            slot.SelectChar = null;
            slot.ReadyIcon = null;
            slot.KickB = null;
            Plugin.Log.LogWarning($"[podium] {slot.gameObject.name} gets no name plate - " +
                                  "the lobby canvas is not laid out the way this expects");
        }
        catch { }
    }

    /// <summary>The matching object inside a copied group, found by its path in the original.</summary>
    private static GameObject Find(Transform copy, Transform originalRoot, Transform target)
    {
        if (copy == null || originalRoot == null || target == null) return null;
        var path = new List<string>();
        var cur = target;
        int guard = 0;
        while (cur != null && cur != originalRoot && guard++ < 32) { path.Insert(0, cur.name); cur = cur.parent; }
        if (cur != originalRoot) return null;                    // not inside the group
        if (path.Count == 0) return copy.gameObject;
        var found = copy.Find(string.Join("/", path));
        return found != null ? found.gameObject : null;
    }

    private static T Bind<T>(Transform copy, Transform originalRoot, Transform target) where T : Component
    {
        var go = Find(copy, originalRoot, target);
        return go != null ? go.GetComponent<T>() : null;
    }

    // ------------------------------------------------------------------ fallback

    /// <summary>
    /// Only reached if the game finds no free podium at all - which, with the copies in
    /// the list, means more players than the configured maximum. Rather than let the
    /// original throw and strand the player, give them a name and a place anyway.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(PlayerObjectController), nameof(PlayerObjectController.CmdSetPlayer))]
    private static bool Assign(PlayerObjectController __instance)
    {
        try
        {
            var lobby = LobbyController.Instance;
            if (lobby == null || lobby.SpawnSlots == null || __instance == null) return true;

            foreach (var s in lobby.SpawnSlots)
                if (s != null && !s.Dolu) return true;      // the game can seat them

            Plugin.Log.LogWarning(
                $"[podium] no podium left for '{__instance.PlayerName}' - {lobby.SpawnSlots.Count} podiums, " +
                $"maximum {Limits.Max}; placing them at the last one");

            if (__instance.isOwned) __instance.NetworkReady = true;
            var last = lobby.SpawnSlots[lobby.SpawnSlots.Count - 1];
            if (last != null)
            {
                __instance.NetworkSlotName = last.gameObject.name;
                Place(__instance.transform, last.transform);
            }
            return false;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] fallback failed: {e.Message}");
            return true;
        }
    }

    // -------------------------------------------------------------------- sweep

    /// <summary>
    /// Keeps the copies' state right on machines Mirror does not update.
    ///
    /// On the host the game's own code marks a copy occupied and names it, exactly as
    /// it does the originals. Those values are SyncVars, and a copy is never synced, so
    /// on every client the copy would stay blank. Each client therefore fills its copies
    /// in from what *is* synced on each player - their SlotName, name and ready flag -
    /// and clears any copy whose player has gone.
    ///
    /// Positions are covered too: the host places a body on its podium when it assigns
    /// it, but only a client with the podium at the same spot can show that. Bodies are
    /// nudged onto the local copy only when visibly away from it, so this never fights
    /// the game's own movement sync.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Update))]
    private static void Sweep(LobbyController __instance)
    {
        try
        {
            if (Time.time < _nextSweep) return;
            _nextSweep = Time.time + 0.5f;
            if (__instance == null) return;

            if (_podiums.Count == 0) BuildAll(__instance);
            if (!_camDone) FrameLobby(__instance);
            HoldCamera();
            FaceThePlates(__instance, _pulledBack);

            if (Dev.Enabled && Time.time >= _nextVisReport)
            {
                _nextVisReport = Time.time + 6f;
                ReportWhoIsVisible(__instance);
                ReportCameraRig();
            }
            if (_podiums.Count == 0) return;

            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null || nm.GamePlayers == null) return;

            bool isHost;
            try { isHost = Mirror.NetworkServer.active; }
            catch { isHost = false; }

            // The game writes "<sprite=N>Name" on the originals when skins are shown.
            // Copy whichever form the originals are using right now.
            bool spriteNames = false;
            foreach (var s in __instance.SpawnSlots)
            {
                if (s == null || s.gameObject.name.StartsWith(Prefix)) continue;
                var n = s.PlayerName;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("<sprite=")) { spriteNames = true; break; }
            }

            foreach (var kv in _podiums)
            {
                var slot = kv.Value;
                if (slot == null) continue;
                string name = Prefix + kv.Key;

                PlayerObjectController occupant = null;
                foreach (var p in nm.GamePlayers)
                    if (p != null && p.SlotName == name) { occupant = p; break; }

                if (occupant == null)
                {
                    // The host clears its own copies through the game's leave handling.
                    if (!isHost && slot.Dolu)
                    {
                        slot.NetworkDolu = false;
                        slot.NetworkPlayerName = "";
                        slot.NetworkReady = false;
                    }
                    continue;
                }

                if (!isHost)
                {
                    string shown = spriteNames ? $"<sprite={occupant.PlayerSkin}>{occupant.PlayerName}" : occupant.PlayerName;
                    if (!slot.Dolu) slot.NetworkDolu = true;
                    if (slot.PlayerName != shown) slot.NetworkPlayerName = shown;
                    if (slot.Ready != occupant.Ready) slot.NetworkReady = occupant.Ready;
                }

                var body = occupant.transform;
                if (body != null && slot.transform != null &&
                    Vector3.Distance(body.position, slot.transform.position) > 0.5f)
                    Place(body, slot.transform);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] sweep failed: {e.Message}");
            _nextSweep = Time.time + 30f;   // a broken frame must not be repeated twice a second
        }
    }

    private static void Place(Transform body, Transform podium)
    {
        if (body == null || podium == null) return;
        body.position = podium.position;
        body.rotation = podium.rotation;
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>The shipped podium row, measured: where it runs, how it bows, which way it faces.</summary>
    private sealed class Row
    {
        internal Vector2 Centre;
        internal float Height;
        internal Vector2 Dir;        // along the row
        internal Vector2 Back;       // across it, pointing away from the camera
        internal float[] Along;      // each shipped podium's place along the row, in order
        internal float Step;         // the spacing between them
        internal float UA, UB;       // how far the row bows off a straight line
        internal float YawA, YawB;   // which way a podium faces, as it turns along the row
        internal bool CheckFloor;

        /// <summary>A spot on the row, optionally set back from it.</summary>
        internal Vector3 At(float along, float depth)
        {
            Vector2 flat = Centre + Dir * along + Back * (UA + UB * along + depth);
            return new Vector3(flat.x, Height, flat.y);
        }

        internal Quaternion Facing(float along) => Quaternion.Euler(0f, YawA + YawB * along, 0f);
    }

    /// <summary>
    /// Put the extra podiums in a second row behind the shipped four.
    ///
    /// The four stand in a row, not a ring, and the first attempt at this simply continued
    /// that row past its ends. Eight podiums then occupied more than twice the width the
    /// lobby camera frames, and players five to eight were pushed off the side of the shot.
    ///
    /// So the row is doubled rather than lengthened: each extra podium stands behind the
    /// shipped one in the same place along the row, half a space to the side so a back-row
    /// character is seen in the gap between two front ones rather than hidden behind one.
    /// The row's own measurements carry over - its slight sideways bow, and the way a
    /// podium's facing turns steadily along it (about fifteen degrees per unit) - so the
    /// second row is the first one repeated, not a straight line pasted behind a curve.
    ///
    /// If there is no floor behind the row - a wall, a bar, the edge of the room - the old
    /// behaviour is used for that podium instead, since a character standing off the side
    /// of the shot is still better than one standing inside a wall.
    /// </summary>
    private static bool RowSeat(List<LobbySlot> vanilla, int index, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;
        if (vanilla.Count < 2) return false;
        if (!Measure(vanilla, out Row row)) return false;

        // Beyond one extra row, keep going back a row at a time. Clamping instead would put
        // every podium past the eighth on the same spot as the eighth - three characters
        // standing inside each other with their name plates on top of one another. Eight
        // players never reach that, but the maximum is a setting and it accepts more.
        int extra = index - Limits.VanillaPlayers;
        int wide = row.Along.Length;
        int rank = extra / wide;                       // 0 is the row straight behind the shipped one
        float along = row.Along[extra % wide] + row.Step * 0.5f;

        // Far enough back to stand clear of the row in front, close enough to stay in shot.
        float depth = Mathf.Clamp(Mathf.Abs(row.Step) * 1.15f, 1.2f, 3f) * (rank + 1);

        var behind = row.At(along, depth);
        if (!row.CheckFloor || HasFloor(behind))
        {
            pos = behind;
            rot = row.Facing(along);
            return true;
        }

        Plugin.Log.LogInfo($"[podium] no floor behind the row at {behind.ToString("F2")} - " +
                           "putting this podium at the end of the row instead");
        return AlongsideRow(row, extra, out pos, out rot);
    }

    /// <summary>
    /// The old placement, kept as a fallback: continue the row past its ends, alternating
    /// so the group stays centred rather than growing off one side.
    /// </summary>
    private static bool AlongsideRow(Row row, int extra, out Vector3 pos, out Quaternion rot)
    {
        pos = Vector3.zero;
        rot = Quaternion.identity;

        float first = row.Along[0];
        float last = row.Along[row.Along.Length - 1];
        int outward = extra / 2 + 1;
        bool after = (extra % 2) == 0;

        foreach (float candidate in new[]
                 {
                     after ? last + row.Step * outward : first - row.Step * outward,
                     after ? first - row.Step * outward : last + row.Step * outward,
                 })
        {
            var at = row.At(candidate, 0f);
            if (row.CheckFloor && !HasFloor(at)) continue;
            pos = at;
            rot = row.Facing(candidate);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Measure the shipped row: the line through it, how it bows, and how its podiums face.
    ///
    /// The four are projected onto their own principal axis, giving each a position along
    /// the row; two straight-line fits against that position carry the rest. Across the row
    /// is taken to point away from the lobby camera, so "behind" means behind as seen by
    /// somebody sitting in the lobby.
    /// </summary>
    private static bool Measure(List<LobbySlot> vanilla, out Row row)
    {
        row = null;

        Vector2 centre = Vector2.zero;
        float height = 0f;
        foreach (var s in vanilla)
        {
            var p = s.transform.position;
            centre += new Vector2(p.x, p.z);
            height += p.y;
        }
        centre /= vanilla.Count;
        height /= vanilla.Count;

        // Principal axis of the row, from the 2x2 covariance of the points.
        float sxx = 0f, szz = 0f, sxz = 0f;
        foreach (var s in vanilla)
        {
            var p = s.transform.position;
            float dx = p.x - centre.x, dz = p.z - centre.y;
            sxx += dx * dx; szz += dz * dz; sxz += dx * dz;
        }
        float theta = 0.5f * Mathf.Atan2(2f * sxz, sxx - szz);
        Vector2 dir = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));
        if (dir.sqrMagnitude < 1e-6f) return false;
        dir.Normalize();
        Vector2 back = new Vector2(-dir.y, dir.x);

        // Point it away from whoever is watching, so a second row goes behind the first.
        var cam = LobbyCamera();
        if (cam != null)
        {
            var c = cam.transform.position;
            if (Vector2.Dot(back, centre - new Vector2(c.x, c.z)) < 0f) back = -back;
        }
        else
        {
            // No camera to ask: the podiums themselves are turned towards the room, so
            // behind is the side they have their backs to.
            Vector2 facing = Vector2.zero;
            foreach (var s in vanilla)
            {
                var f = s.transform.forward;
                facing += new Vector2(f.x, f.z);
            }
            if (Vector2.Dot(back, facing) > 0f) back = -back;
        }

        int n = vanilla.Count;
        var t = new float[n];
        var u = new float[n];
        var yaw = new float[n];
        for (int i = 0; i < n; i++)
        {
            var p = vanilla[i].transform.position;
            Vector2 d = new Vector2(p.x - centre.x, p.z - centre.y);
            t[i] = Vector2.Dot(d, dir);
            u[i] = Vector2.Dot(d, back);
            yaw[i] = vanilla[i].transform.eulerAngles.y;
        }

        // Facing wraps at 360; unwrap along the row so a straight-line fit is meaningful.
        var order = new List<int>();
        for (int i = 0; i < n; i++) order.Add(i);
        order.Sort((a, b) => t[a].CompareTo(t[b]));
        for (int k = 1; k < order.Count; k++)
        {
            float prev = yaw[order[k - 1]], cur = yaw[order[k]];
            while (cur - prev > 180f) cur -= 360f;
            while (prev - cur > 180f) cur += 360f;
            yaw[order[k]] = cur;
        }

        if (!Fit(t, u, out float uA, out float uB)) return false;
        if (!Fit(t, yaw, out float yA, out float yB)) return false;

        var along = new float[n];
        for (int k = 0; k < n; k++) along[k] = t[order[k]];

        float step = (along[n - 1] - along[0]) / (n - 1);
        if (Mathf.Abs(step) < 0.05f) return false;

        row = new Row
        {
            Centre = centre,
            Height = height,
            Dir = dir,
            Back = back,
            Along = along,
            Step = step,
            UA = uA,
            UB = uB,
            YawA = yA,
            YawB = yB,
            CheckFloor = FloorTestWorks(PointsOf(vanilla)),
        };
        return true;
    }

    // -------------------------------------------------------------------- camera

    /// <summary>How high above a podium a character's head is, near enough for framing.</summary>
    private const float HeadHeight = 1.8f;

    private static Camera _cam;
    private static Vector3 _camHome, _camWanted;
    private static Quaternion _camHomeRot, _camWantedRot;
    private static float _camHomeFov, _camWantedFov;
    private static bool _camHolding, _camDone;
    private static float _nextVisReport;

    /// <summary>The virtual camera that owns the lobby shot, switched off while we hold it.</summary>
    private static GameObject _vcam;

    /// <summary>
    /// Take the lobby shot away from Cinemachine.
    ///
    /// The lobby camera carries a <c>CinemachineBrain</c>, and a brain re-poses its camera
    /// every frame in LateUpdate from whichever virtual camera is live — long after anything
    /// here runs. That is why raising the camera changed the numbers in the log and nothing
    /// on the screen: the move was real, and then it was overwritten before the frame was
    /// drawn. Moving the camera under a live brain is not possible.
    ///
    /// With the virtual camera switched off the brain has nothing to drive and stops writing
    /// to the transform, so the raised shot simply holds. The lobby shot is a fixed one, so
    /// nothing is lost by taking it over; it is handed straight back when the framing is
    /// released.
    /// </summary>
    private static void SilenceRig()
    {
        if (_vcam != null) return;
        try
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || t.gameObject == null) continue;
                if (!t.gameObject.scene.IsValid() || !t.gameObject.activeInHierarchy) continue;

                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    if (comp.GetIl2CppType().Name != "CinemachineVirtualCamera") continue;
                    _vcam = t.gameObject;
                    break;
                }
                if (_vcam != null) break;
            }

            if (_vcam == null) return;

            _vcam.SetActive(false);
            Plugin.Log.LogInfo($"[podium] '{_vcam.name}' drives the lobby camera through a Cinemachine " +
                               "brain - switched off so the raised shot holds");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[podium] could not quieten the camera rig: {e.Message}");
            _vcam = null;
        }
    }

    /// <summary>Give the shot back to whatever was driving it.</summary>
    private static void RestoreRig()
    {
        try { if (_vcam != null) _vcam.SetActive(true); }
        catch { }
        _vcam = null;
    }
    private static int _camTries;

    private static Camera LobbyCamera()
    {
        try
        {
            var cam = Camera.main;
            if (cam != null) return cam;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
                if (c != null && c.isActiveAndEnabled && c.targetTexture == null) return c;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Raise the lobby camera until every podium is in shot.
    ///
    /// Two rows of characters need more of the room in frame than one, and the back row
    /// stands behind the front one, so the shot has to look down at them a little. The
    /// camera is lifted a step at a time and re-aimed at the middle of the group, and only
    /// widened if lifting alone is not enough; the first setting that holds every podium -
    /// feet and head - is kept. If the shot already covers them nothing is touched, and if
    /// no amount of lifting works the camera is left exactly as the game had it, because a
    /// lobby framed oddly is better than one pointing at the ceiling.
    /// </summary>
    private static void FrameLobby(LobbyController lobby)
    {
        if (_camDone || lobby == null || lobby.SpawnSlots == null) return;

        try
        {
            var cam = LobbyCamera();
            if (cam == null)
            {
                // The lobby camera may not exist yet on the frame the lobby starts.
                if (++_camTries > 20) { _camDone = true; Plugin.Log.LogInfo("[podium] no lobby camera to raise"); }
                return;
            }

            var marks = new List<Vector3>();
            var front = new List<Vector3>();
            var back = new List<Vector3>();
            foreach (var s in lobby.SpawnSlots)
            {
                if (s == null || s.transform == null) continue;
                var at = s.transform.position;
                marks.Add(at + Vector3.up * 0.1f);
                marks.Add(at + Vector3.up * HeadHeight);
                if (s.gameObject.name.StartsWith(Prefix)) back.Add(at); else front.Add(at);
            }
            if (marks.Count == 0) return;

            var t = cam.transform;
            _cam = cam;
            _camHome = t.position;
            _camHomeRot = t.rotation;
            _camHomeFov = cam.fieldOfView;
            _camDone = true;

            Plugin.Log.LogInfo($"[podium] lobby camera '{cam.name}' at {_camHome.ToString("F2")} " +
                               $"fov={_camHomeFov:F1} ortho={cam.orthographic}");

            if (cam.orthographic)
            {
                Plugin.Log.LogInfo("[podium] the lobby camera is orthographic - left alone");
                return;
            }

            if (InShot(cam, marks) && Unblocked(cam, back, front))
            {
                Plugin.Log.LogInfo($"[podium] all {lobby.SpawnSlots.Count} podiums are already in shot");
                return;
            }

            Vector3 focus = Vector3.zero;
            foreach (var s in lobby.SpawnSlots)
                if (s != null && s.transform != null) focus += s.transform.position + Vector3.up * (HeadHeight * 0.5f);
            focus /= lobby.SpawnSlots.Count;

            // Lifting is what was asked for, so try every lift before widening at all.
            bool haveFallback = false;
            Vector3 fbPos = Vector3.zero;
            Quaternion fbRot = Quaternion.identity;
            float fbFov = 0f, fbLift = 0f, fbWider = 0f;

            for (float wider = 0f; wider <= 12.01f; wider += 4f)
            {
                for (float lift = 0.25f; lift <= 6.01f; lift += 0.25f)
                {
                    t.position = _camHome + Vector3.up * lift;
                    t.rotation = Quaternion.LookRotation(focus - t.position, Vector3.up);
                    cam.fieldOfView = _camHomeFov + wider;
                    if (!InShot(cam, marks)) continue;

                    // Everyone in frame is worth keeping even if the back row is still
                    // partly hidden, so the first of those is remembered as a second best.
                    if (!haveFallback)
                    {
                        haveFallback = true;
                        fbPos = t.position; fbRot = t.rotation; fbFov = cam.fieldOfView;
                        fbLift = lift; fbWider = wider;
                    }

                    if (!Unblocked(cam, back, front)) continue;

                    _camWanted = t.position;
                    _camWantedRot = t.rotation;
                    _camWantedFov = cam.fieldOfView;
                    _camHolding = true;
                    SilenceRig();
                    Plugin.Log.LogInfo(
                        $"[podium] lobby camera raised {lift:F2} units" +
                        (wider > 0f ? $" and widened {wider:F0} degrees" : "") +
                        $" - all {lobby.SpawnSlots.Count} podiums in shot and the back row clear " +
                        "of the front");
                    return;
                }
            }

            if (haveFallback)
            {
                t.position = fbPos; t.rotation = fbRot; cam.fieldOfView = fbFov;
                _camWanted = fbPos; _camWantedRot = fbRot; _camWantedFov = fbFov;
                _camHolding = true;
                SilenceRig();
                Plugin.Log.LogInfo(
                    $"[podium] lobby camera raised {fbLift:F2} units" +
                    (fbWider > 0f ? $" and widened {fbWider:F0} degrees" : "") +
                    " - everyone is in shot, though the back row is not fully clear of the front");
                return;
            }

            RestoreCamera();
            Plugin.Log.LogWarning("[podium] could not frame every podium - the lobby camera is left as the game had it");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[podium] raising the lobby camera failed: {e.Message}");
            RestoreCamera();
            _camDone = true;
        }
    }

    private static void RestoreCamera()
    {
        try
        {
            _camHolding = false;
            RestoreRig();
            if (_cam == null) return;
            _cam.transform.position = _camHome;
            _cam.transform.rotation = _camHomeRot;
            _cam.fieldOfView = _camHomeFov;
        }
        catch { }
    }

    /// <summary>
    /// Keep the raised shot if the game puts the camera back where it started.
    ///
    /// Only a camera still standing at its lobby position is moved. If the game has taken
    /// it somewhere else - a character close-up, a menu - that is a shot of its own and is
    /// left alone.
    /// </summary>
    private static void HoldCamera()
    {
        if (!_camHolding || _cam == null) return;
        try
        {
            var t = _cam.transform;

            // The wider shot is only for a table the game was not built for. At four or
            // fewer the lobby looks as it always has, and the camera is handed back.
            bool wanted = Crowded();
            if (wanted != _pulledBack)
            {
                _pulledBack = wanted;
                _blendFrom = t.position;
                _blendFromRot = t.rotation;
                _blendFromFov = _cam.fieldOfView;
                _blendStarted = Time.time;
                if (wanted) SilenceRig();
                Plugin.Log.LogInfo(wanted
                    ? "[podium] a fifth player has joined - easing the lobby camera back to show everyone"
                    : "[podium] back to four - easing the lobby camera home");
            }

            var goalPos = _pulledBack ? _camWanted : _camHome;
            var goalRot = _pulledBack ? _camWantedRot : _camHomeRot;
            var goalFov = _pulledBack ? _camWantedFov : _camHomeFov;

            // A cut is jarring in a lobby people are standing around in; a second and a bit
            // of eased movement reads as the camera stepping back to fit everyone in.
            float k = _blendStarted <= 0f ? 1f
                    : Mathf.Clamp01((Time.time - _blendStarted) / BlendSeconds);
            k = k * k * (3f - 2f * k);          // smoothstep: no jolt at either end

            t.position = Vector3.Lerp(_blendFrom, goalPos, k);
            t.rotation = Quaternion.Slerp(_blendFromRot, goalRot, k);
            _cam.fieldOfView = Mathf.Lerp(_blendFromFov, goalFov, k);

            // Once home again, the shot belongs to the game.
            if (!_pulledBack && k >= 1f) RestoreRig();
        }
        catch { _camHolding = false; }
    }

    /// <summary>How long the camera takes to move between the two shots.</summary>
    private const float BlendSeconds = 1.4f;

    private static bool _pulledBack;
    private static float _blendStarted;
    private static Vector3 _blendFrom;
    private static Quaternion _blendFromRot;
    private static float _blendFromFov;

    /// <summary>Are there more people here than the lobby was built to show?</summary>
    private static bool Crowded()
    {
        try
        {
            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            int n = nm != null && nm.GamePlayers != null ? nm.GamePlayers.Count : 0;
            return n > Limits.VanillaPlayers;
        }
        catch { return false; }
    }

    /// <summary>
    /// Can the camera see the back row over the heads of the front one?
    ///
    /// Being inside the picture is not the same as being visible: a character standing
    /// directly behind another is in frame and entirely hidden. So for each podium in the
    /// back row, the line from the camera to where that character's chest will be is checked
    /// against every front-row character standing near it - treating each as a person's
    /// width and height - and has to pass over their head.
    /// </summary>
    private static bool Unblocked(Camera cam, List<Vector3> back, List<Vector3> front)
    {
        // Generous on both counts. The cast in this game are not all the same size - the
        // boar is roughly twice the width of the rabbit - and a model that assumes an
        // average build leaves the widest of them hiding somebody.
        const float shoulders = 0.8f;    // half a character's width
        const float stature = 2.15f;     // the top of a character's head

        var eye = cam.transform.position;
        var eyeFlat = new Vector2(eye.x, eye.z);

        foreach (var b in back)
        {
            var chest = b + Vector3.up * 1.2f;
            var chestFlat = new Vector2(chest.x, chest.z);
            var run = chestFlat - eyeFlat;
            float length = run.magnitude;
            if (length < 0.01f) continue;
            var along = run / length;

            foreach (var f in front)
            {
                var head = new Vector2(f.x, f.z);
                float u = Vector2.Dot(head - eyeFlat, along) / length;
                if (u <= 0.05f || u >= 0.98f) continue;                 // not between the two
                if ((eyeFlat + run * u - head).magnitude > shoulders) continue;   // standing aside

                float height = eye.y + (chest.y - eye.y) * u;
                if (height < f.y + stature) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Put every name plate over the head of the player it names, facing the camera.
    ///
    /// The shipped plates hang in the world at positions chosen for the shot the lobby
    /// normally uses. Move the camera up and back and they no longer read: they sit at odd
    /// angles, some behind their own character, and with two rows it stops being clear whose
    /// name is whose - which is what "all over the place" means.
    ///
    /// So while the wider shot is being held, each plate is placed directly above its own
    /// podium and turned to face the camera. Above the head there is nothing to overlap, the
    /// association with the character underneath is unambiguous, and facing the camera is
    /// what makes the text legible from a position the plates were never laid out for. The
    /// original pose of each is kept, so handing the shot back puts them where they were.
    /// </summary>
    private static void FaceThePlates(LobbyController lobby, bool pulledBack)
    {
        if (_cam == null || lobby == null || lobby.SpawnSlots == null) return;

        try
        {
            foreach (var s in lobby.SpawnSlots)
            {
                if (s == null || s.NameText == null) continue;

                var plate = s.NameText.transform.parent;
                if (plate == null) continue;

                int id = plate.GetInstanceID();

                if (!_plateHome.TryGetValue(id, out var home))
                {
                    home = (plate.position, plate.rotation);
                    _plateHome[id] = home;
                }

                if (!pulledBack)
                {
                    plate.position = home.Pos;
                    plate.rotation = home.Rot;
                    continue;
                }

                var over = s.transform.position + Vector3.up * PlateHeight;
                var toCam = _cam.transform.position - over;
                if (toCam.sqrMagnitude < 0.01f) continue;

                plate.position = over;
                plate.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
            }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[podium] could not place the name plates: {e.Message}"); }
    }

    /// <summary>How far above a podium a name plate hangs when the shot is pulled back.</summary>
    private const float PlateHeight = 2.35f;

    private static readonly Dictionary<int, (Vector3 Pos, Quaternion Rot)> _plateHome = new();

    /// <summary>
    /// What is actually driving the lobby camera.
    ///
    /// The camera was moved, the log agreed it had moved, and the picture did not change —
    /// which means something puts it back after this does. The likely culprit is a rig
    /// rather than the camera itself: this game has objects named "..._Camera/cm", and a
    /// virtual-camera brain re-poses the real camera every frame in LateUpdate, long after
    /// any of this runs. Moving the camera under a brain is futile; the brain has to be
    /// moved, or stopped.
    ///
    /// So this names every camera in the scene and every component on it, and anything
    /// nearby that looks like a rig, once, so the right thing can be moved.
    /// </summary>
    private static void ReportCameraRig()
    {
        if (!Dev.Enabled || _rigReported) return;
        _rigReported = true;

        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("cameras in the lobby:");

            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (c == null || c.gameObject == null) continue;
                if (!c.gameObject.scene.IsValid()) continue;

                sb.AppendLine($"  '{Trail(c.transform)}' enabled={c.enabled} active={c.gameObject.activeInHierarchy} " +
                              $"depth={c.depth} target={(c.targetTexture != null ? "texture" : "screen")} " +
                              $"at {c.transform.position.ToString("F2")} fov={c.fieldOfView:F1}");

                foreach (var comp in c.GetComponents<Component>())
                    if (comp != null)
                        sb.AppendLine($"       component {comp.GetIl2CppType().Name}");
            }

            // Anything that looks like a virtual camera or a brain, wherever it lives.
            int rigs = 0;
            foreach (var go in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (go == null || go.gameObject == null || !go.gameObject.scene.IsValid()) continue;
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    string n = comp.GetIl2CppType().Name;
                    if (n.IndexOf("Cinemachine", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("Brain", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (++rigs > 24) break;
                    sb.AppendLine($"  rig '{Trail(go)}' has {n} " +
                                  $"active={go.gameObject.activeInHierarchy} at {go.position.ToString("F2")}");
                }
                if (rigs > 24) break;
            }
            if (rigs == 0) sb.AppendLine("  no virtual-camera rig found - the camera is posed by ordinary code");

            Dev.Log("podium", sb.ToString().TrimEnd());
        }
        catch (Exception e) { Dev.Warn("podium", $"could not read the camera rig: {e.Message}"); }
    }

    private static bool _rigReported;

    private static string Trail(Transform t)
    {
        var parts = new List<string>();
        var cur = t;
        int guard = 0;
        while (cur != null && guard++ < 8) { parts.Insert(0, cur.name); cur = cur.parent; }
        return string.Join("/", parts);
    }

    /// <summary>
    /// Say where every player in the lobby actually appears on screen.
    ///
    /// "All eight podiums are in shot" is not the same as "eight characters can be seen": a
    /// player whose body was never moved onto their podium, or who is standing behind
    /// somebody broad, is in frame and invisible. This reports each player's place in the
    /// picture as a fraction of the screen, so the ones that cannot be seen can be named
    /// rather than counted off a screenshot.
    /// </summary>
    private static void ReportWhoIsVisible(LobbyController lobby)
    {
        if (!Dev.Enabled || _cam == null) return;

        try
        {
            var nm = UnityEngine.Object.FindObjectOfType<CustomNetworkManager>();
            if (nm == null || nm.GamePlayers == null) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"where each player appears, camera at {_cam.transform.position.ToString("F2")}:");

            foreach (var p in nm.GamePlayers)
            {
                if (p == null) continue;

                LobbySlot podium = null;
                foreach (var s in lobby.SpawnSlots)
                    if (s != null && s.gameObject.name == p.SlotName) { podium = s; break; }

                var head = p.transform.position + Vector3.up * HeadHeight;
                var v = _cam.WorldToViewportPoint(head);
                bool onScreen = v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;

                float adrift = podium != null
                    ? Vector3.Distance(p.transform.position, podium.transform.position)
                    : -1f;

                sb.AppendLine(
                    $"  '{p.PlayerName}' podium={p.SlotName} " +
                    $"screen=({v.x:F2},{v.y:F2}) depth={v.z:F1} " +
                    (onScreen ? "on screen" : "OFF SCREEN") +
                    (adrift > 0.5f ? $"  !! standing {adrift:F2} from their podium" : ""));
            }

            Dev.Log("podium", sb.ToString().TrimEnd());
        }
        catch (Exception e) { Dev.Warn("podium", $"could not report who is visible: {e.Message}"); }
    }

    /// <summary>Is every one of these points inside the picture, with a little room to spare?</summary>
    private static bool InShot(Camera cam, List<Vector3> points)
    {
        const float margin = 0.03f;
        foreach (var p in points)
        {
            var v = cam.WorldToViewportPoint(p);
            if (v.z <= 0f) return false;
            if (v.x < margin || v.x > 1f - margin) return false;
            if (v.y < margin || v.y > 1f - margin) return false;
        }
        return true;
    }

    /// <summary>Least squares y = a + b*x. False if every x is the same.</summary>
    private static bool Fit(float[] x, float[] y, out float a, out float b)
    {
        a = 0f; b = 0f;
        int n = x.Length;
        if (n < 2) return false;

        float sx = 0f, sy = 0f, sxx = 0f, sxy = 0f;
        for (int i = 0; i < n; i++)
        {
            sx += x[i]; sy += y[i];
            sxx += x[i] * x[i]; sxy += x[i] * y[i];
        }

        float den = n * sxx - sx * sx;
        if (Mathf.Abs(den) < 1e-6f) return false;

        b = (n * sxy - sx * sy) / den;
        a = (sy - b * sx) / n;
        return true;
    }

    private static List<Vector3> PointsOf(List<LobbySlot> slots)
    {
        var pts = new List<Vector3>();
        foreach (var s in slots) if (s != null && s.transform != null) pts.Add(s.transform.position);
        return pts;
    }

    private static int _floorTest;   // 0 unknown, 1 usable, -1 not

    /// <summary>Would the floor check pass for podiums that are definitely standing on floor?</summary>
    private static bool FloorTestWorks(List<Vector3> known)
    {
        if (_floorTest != 0) return _floorTest > 0;

        int ok = 0;
        foreach (var p in known) if (HasFloor(p)) ok++;
        _floorTest = ok == known.Count ? 1 : -1;
        if (_floorTest < 0)
            Plugin.Log.LogInfo($"[podium] floor check reaches only {ok}/{known.Count} of the shipped " +
                               "podiums - the lobby has nothing to test against, so it is not used");
        return _floorTest > 0;
    }

    /// <summary>Is there ground directly under this spot, at about the podium's own height?</summary>
    private static bool HasFloor(Vector3 pos)
    {
        try
        {
            if (!Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 6f)) return false;
            return Mathf.Abs(hit.point.y - pos.y) < 1.5f;
        }
        catch { return false; }
    }
}
