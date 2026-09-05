using HarmonyLib;
using UnityEngine;

namespace LiarsBar8P;

/// <summary>
/// The lobby's character podiums (`LobbyController.SpawnSlots`) are four hand-placed
/// transforms sitting on an arc. Extra players need somewhere to stand, so the arc is
/// fitted and continued past its far end.
///
/// The original four are never moved: players 1-4 stand exactly where they do in
/// vanilla, and only the new slots are synthesised.
/// </summary>
internal static class LobbyExpansion
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LobbyController), nameof(LobbyController.Start))]
    private static void Expand(LobbyController __instance)
    {
        try
        {
            if (!Plugin.ExpandLobbySlots.Value) return;

            var slots = __instance.SpawnSlots;
            int want = Plugin.MaxPlayers.Value;
            if (slots == null || slots.Count == 0 || slots.Count >= want) return;

            int have = slots.Count;
            Plugin.Log.LogInfo($"[lobby] expanding spawn slots {have} -> {want}");

            var pts = new Vector2[have];
            var yaws = new float[have];
            float y = 0f;
            for (int i = 0; i < have; i++)
            {
                var t = slots[i].transform;
                pts[i] = new Vector2(t.position.x, t.position.z);
                yaws[i] = t.eulerAngles.y;
                y += t.position.y;
            }
            y /= have;

            FitCircle(pts, out Vector2 c, out float r);
            Plugin.Log.LogInfo($"[lobby] arc fit: centre=({c.x:F3}, {c.y:F3}) radius={r:F3}");

            // Order the seats along the arc. Atan2 returns -180..180, so a seat ring
            // that straddles that boundary sorts into nonsense if compared numerically.
            // Normalise to 0..360, then start the sequence after the largest angular
            // gap (the open side of the arc) and unwrap so angles rise monotonically.
            var idx = new int[have];
            var ang = new float[have];
            for (int i = 0; i < have; i++)
            {
                idx[i] = i;
                float a = Mathf.Atan2(pts[i].y - c.y, pts[i].x - c.x) * Mathf.Rad2Deg;
                ang[i] = (a % 360f + 360f) % 360f;
            }
            System.Array.Sort(idx, (a, b) => ang[a].CompareTo(ang[b]));

            int gapAt = 0;
            float widest = -1f;
            for (int i = 0; i < have; i++)
            {
                int j = (i + 1) % have;
                float gap = ang[idx[j]] - ang[idx[i]];
                if (gap < 0f) gap += 360f;
                if (gap > widest) { widest = gap; gapAt = j; }
            }

            var ring = new int[have];
            var unwrapped = new float[have];
            for (int i = 0; i < have; i++) ring[i] = idx[(gapAt + i) % have];
            unwrapped[0] = ang[ring[0]];
            for (int i = 1; i < have; i++)
            {
                float a = ang[ring[i]];
                while (a < unwrapped[i - 1]) a += 360f;
                unwrapped[i] = a;
            }

            float first = unwrapped[0];
            float last = unwrapped[have - 1];
            float step = (last - first) / (have - 1);
            if (Mathf.Abs(step) < 0.01f) step = 25f;

            Plugin.Log.LogInfo(
                $"[lobby] arc spans {first:F1}deg -> {last:F1}deg, step {step:F1}deg per seat");

            // Refuse to place seats if continuing the arc would wrap back onto the
            // existing ones; better to leave the lobby vanilla than stack players.
            float sweep = Mathf.Abs(step) * (want - 1);
            if (sweep >= 360f)
            {
                Plugin.Log.LogWarning(
                    $"[lobby] {want} seats at {step:F1}deg would wrap the circle ({sweep:F0}deg) - " +
                    "leaving lobby slots untouched");
                return;
            }

            float yawFirst = yaws[ring[0]];
            float yawLast = yaws[ring[have - 1]];
            float yawStep = Mathf.DeltaAngle(yawFirst, yawLast) / (have - 1);

            var template = slots[ring[have - 1]];

            for (int k = 1; k <= want - have; k++)
            {
                float a = last + step * k;
                float rad = a * Mathf.Deg2Rad;
                var pos = new Vector3(c.x + r * Mathf.Cos(rad), y, c.y + r * Mathf.Sin(rad));
                float yaw = yawLast + yawStep * k;

                // Instantiating an active object runs Awake immediately, which is
                // where Mirror registers the NetworkIdentity and complains that the
                // scene object "has already spawned". Cloning while inactive skips
                // Awake, so the networking components can be removed before the copy
                // is ever live.
                bool wasActive = template.gameObject.activeSelf;
                template.gameObject.SetActive(false);

                var clone = Object.Instantiate(template);
                StripNetworking(clone.gameObject);

                template.gameObject.SetActive(wasActive);
                clone.gameObject.SetActive(true);

                var ct = clone.transform;
                ct.position = pos;
                ct.rotation = Quaternion.Euler(0f, yaw, 0f);
                clone.gameObject.name = $"Slot{have + k}_8P";

                slots.Add(clone);
                Plugin.Log.LogInfo(
                    $"[lobby]   + {clone.gameObject.name} pos={pos.ToString("F3")} yaw={yaw:F1} (arc {a:F1}deg)");
            }

            Plugin.Log.LogInfo($"[lobby] spawn slots now {slots.Count}");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[lobby] slot expansion failed: {e}");
        }
    }

    /// <summary>
    /// Mirror rejects duplicated scene NetworkIdentities - "Slot4(Clone) has already
    /// spawned" - and the resulting bad spawn state cascades into clients being
    /// disconnected by the server. These extra podiums are decoration only, so every
    /// networking component is removed from the copy before it is ever used.
    /// </summary>
    private static void StripNetworking(GameObject go)
    {
        if (go == null) return;
        try
        {
            foreach (var nb in go.GetComponentsInChildren<Mirror.NetworkBehaviour>(true))
                if (nb != null) Object.DestroyImmediate(nb);

            foreach (var ni in go.GetComponentsInChildren<Mirror.NetworkIdentity>(true))
                if (ni != null) Object.DestroyImmediate(ni);
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[lobby] could not strip networking from clone: {e.Message}");
        }
    }

    /// <summary>
    /// Kasa least-squares circle fit. Falls back to the centroid with mean radius
    /// if the points are near-collinear and the system is degenerate.
    /// </summary>
    private static void FitCircle(Vector2[] p, out Vector2 centre, out float radius)
    {
        int n = p.Length;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var q in p)
        {
            double x = q.x, yy = q.y, z = x * x + yy * yy;
            sx += x; sy += yy; sxx += x * x; syy += yy * yy; sxy += x * yy;
            sxz += x * z; syz += yy * z; sz += z;
        }

        double a1 = 2 * (sx * sx - n * sxx);
        double b1 = 2 * (sx * sy - n * sxy);
        double c1 = sx * sz - n * sxz;
        double a2 = 2 * (sx * sy - n * sxy);
        double b2 = 2 * (sy * sy - n * syy);
        double c2 = sy * sz - n * syz;

        double det = a1 * b2 - a2 * b1;

        if (System.Math.Abs(det) < 1e-8)
        {
            centre = new Vector2((float)(sx / n), (float)(sy / n));
            float rr = 0f;
            foreach (var q in p) rr += Vector2.Distance(q, centre);
            radius = rr / n;
            Plugin.Log.LogWarning("[lobby] circle fit degenerate; using centroid fallback");
            return;
        }

        double cx = (c1 * b2 - c2 * b1) / det;
        double cy = (a1 * c2 - a2 * c1) / det;
        centre = new Vector2((float)cx, (float)cy);

        float acc = 0f;
        foreach (var q in p) acc += Vector2.Distance(q, centre);
        radius = acc / n;
    }
}
