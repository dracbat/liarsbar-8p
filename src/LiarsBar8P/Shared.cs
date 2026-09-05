using UnityEngine;

namespace LiarsBar8P;

internal static class Geometry
{
    /// <summary>
    /// Kasa least-squares circle fit. Falls back to the centroid with mean radius if the
    /// points are near-collinear and the system is degenerate.
    /// </summary>
    internal static void FitCircle(Vector2[] p, out Vector2 centre, out float radius)
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
            return;
        }

        centre = new Vector2((float)((c1 * b2 - c2 * b1) / det), (float)((a1 * c2 - a2 * c1) / det));

        float acc = 0f;
        foreach (var q in p) acc += Vector2.Distance(q, centre);
        radius = acc / n;
    }
}

internal static class Cloning
{
    /// <summary>
    /// Copies a scene object without letting Mirror see a duplicate NetworkIdentity.
    ///
    /// Instantiating an active object runs Awake immediately, which is where Mirror
    /// registers the identity and rejects it with "has already spawned" - that corrupted
    /// spawn handling and disconnected every client. Cloning while the template is
    /// inactive skips Awake, so the networking components can be destroyed before the
    /// copy is ever live.
    /// </summary>
    internal static Transform SafeClone(Transform template, string name)
    {
        if (template == null) return null;
        bool wasActive = template.gameObject.activeSelf;
        try
        {
            template.gameObject.SetActive(false);
            var clone = Object.Instantiate(template);
            StripNetworking(clone.gameObject);
            clone.gameObject.name = name;
            clone.gameObject.SetActive(true);
            return clone;
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[clone] failed for {name}: {e.Message}");
            return null;
        }
        finally
        {
            template.gameObject.SetActive(wasActive);
        }
    }

    /// <summary>Remove every networking component so the copy is inert to Mirror.</summary>
    internal static void StripNetworking(GameObject go)
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
            Plugin.Log.LogError($"[clone] could not strip networking: {e.Message}");
        }
    }
}

internal static class Describe
{
    private static bool _done;

    /// <summary>
    /// One-shot dump of what a scene slot actually carries. Cloning strategy depends on
    /// whether these are bare positional markers or real networked objects.
    /// </summary>
    internal static void Components(GameObject go, string label)
    {
        if (_done || go == null) return;
        _done = true;
        try
        {
            Plugin.Log.LogInfo($"[inspect] {label} '{go.gameObject.name}' components:");
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) { Plugin.Log.LogInfo("    <null component>"); continue; }
                Plugin.Log.LogInfo($"    {comp.GetIl2CppType().FullName}");
            }
            Plugin.Log.LogInfo($"[inspect] children={go.transform.childCount} active={go.activeSelf}");
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogError($"[inspect] failed: {e.Message}");
        }
    }
}

internal static class Markers
{
    /// <summary>
    /// Creates a fresh, empty marker object matching a reference transform's parent and
    /// scale.
    ///
    /// The seat and podium entries are bare positional markers - no components, no
    /// children - so a new GameObject serves the same purpose as a copy. Crucially it is
    /// also inert: earlier attempts cloned the originals, which carried Mirror scene
    /// identities, and duplicating those corrupted spawn handling and disconnected every
    /// client. Nothing here can touch networking.
    /// </summary>
    internal static Transform Create(Transform reference, string name, Vector3 pos, float yaw)
    {
        var go = new GameObject(name);
        var t = go.transform;
        if (reference != null)
        {
            if (reference.parent != null) t.SetParent(reference.parent, true);
            t.localScale = reference.localScale;
        }
        t.position = pos;
        t.rotation = Quaternion.Euler(0f, yaw, 0f);
        return t;
    }
}
