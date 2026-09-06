using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace LiarsBar8P;

/// <summary>
/// Raises the Steam transport's own connection limit.
///
/// This is the cap that stopped a sixth player, and it sits *below* Mirror. The
/// FizzySteamworks server keeps its own <c>maxConnections</c>, taken from
/// <c>NetworkManager.maxConnections</c> when the server starts, and rejects anything
/// beyond it inside <c>OnConnectionStatusChanged</c>:
///
///     if (steamConnections.Count &gt;= maxConnections)
///         "Incoming connection {0} would exceed max connection count. Rejecting."
///
/// The host does not open a Steam connection to itself, so that dictionary holds only
/// the remote players. A limit of four therefore meant four remote clients plus the
/// host - five players, with the sixth refused. And because the refusal happens in the
/// transport, Mirror never sees the attempt, which is why the host's log showed no
/// incoming connection at all and the failure looked like it was on the joining side.
///
/// The value is raised where the server is constructed, so it applies to whichever
/// transport the game picks (the game ships both the "next" SteamNetworkingSockets
/// server and the legacy P2P one).
/// </summary>
[HarmonyPatch]
internal static class TransportCap
{
    private static readonly string[] TypeNames =
    {
        "Mirror.FizzySteam.NextServer",
        "Mirror.FizzySteam.LegacyServer",
    };

    /// <summary>
    /// Every constructor or factory on either transport server that takes the limit.
    /// Selected by parameter name rather than by signature so a differently shaped
    /// overload cannot be patched by accident.
    /// </summary>
    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> Targets()
    {
        var found = new List<MethodBase>();

        // The interop assembly is only loaded on demand, and at plugin load nothing has
        // touched the transport yet - so the type is invisible unless it is asked for by
        // name first.
        try { System.Reflection.Assembly.Load("FizzySteamworks"); }
        catch (System.Exception e) { Plugin.Log.LogWarning($"[transport] could not load the transport assembly: {e.Message}"); }

        foreach (var name in TypeNames)
        {
            var type = AccessTools.TypeByName(name);
            if (type == null)
            {
                Plugin.Log.LogInfo($"[transport] {name} not present - skipped");
                continue;
            }

            var methods = type.GetConstructors(AccessTools.all)
                .Cast<MethodBase>()
                .Concat(type.GetMethods(AccessTools.all).Where(m => m.Name == "CreateServer"));

            foreach (var m in methods)
            {
                if (!m.GetParameters().Any(p => p.Name == "maxConnections")) continue;
                found.Add(m);
                Plugin.Log.LogInfo($"[transport] will raise the limit in {type.Name}.{m.Name}");
            }
        }

        if (found.Count == 0)
            Plugin.Log.LogWarning(
                "[transport] no Steam transport server found to raise - more than five " +
                "players may still be refused before Mirror sees them");

        return found;
    }

    [HarmonyPrefix]
    private static void Raise(ref int maxConnections)
    {
        int target = Limits.Max;
        if (maxConnections >= target) return;

        Plugin.Log.LogInfo($"[transport] server maxConnections {maxConnections} -> {target}");
        maxConnections = target;
    }
}
