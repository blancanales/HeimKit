using System;
using HeimKit.Core.World;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

// A witness retains the actual reciprocal pair and its expected landing point.
internal sealed class TameTravelSummonGate
{
    internal readonly Vector3 Source, Exit;
    internal readonly Quaternion Rotation;
    internal readonly Func<bool> Valid;
    internal int LocationHash { get; private set; }
    internal Teleport? Doorway { get; private set; }

    internal TameTravelSummonGate(Vector3 source, Vector3 exit, Quaternion rotation, Func<bool> valid)
    {
        Source = source; Exit = exit; Rotation = rotation; Valid = valid;
    }

    internal static TameTravelSummonGate? Dungeon(Teleport source, Vector3 player, float range, Func<bool>? retain = null, int locationHash = 0)
    {
        var target = source.m_targetPoint;
        var start = source.transform.position;
        if (!source.isActiveAndEnabled || target == null || (start - player).sqrMagnitude > range * range) return null;
        // Teleport.Interact uses this directed target for both entry and exit.
        // Hover text is display data and the destination may have its own onward link.
        var exit = target.transform.position + target.transform.forward - target.transform.up;
        if (Character.InInterior(player) != Character.InInterior(start) ||
            Character.InInterior(start) == Character.InInterior(exit)) return null;
        var rotation = target.transform.rotation;
        bool Valid() => (retain == null || retain()) && source != null && source.isActiveAndEnabled &&
            target != null && source.m_targetPoint == target && source.transform.position == start &&
            target.transform.position + target.transform.forward - target.transform.up == exit &&
            Quaternion.Angle(target.transform.rotation, rotation) < .01f;
        if (locationHash == 0)
        {
            var proxy = source.GetComponentInParent<LocationProxy>();
            locationHash = proxy?.GetComponent<ZNetView>()?.GetZDO()?.GetInt(ZDOVars.s_location) ?? 0;
            if (locationHash == 0 && source.GetComponentInParent<Location>() is { } location)
                locationHash = Utils.GetPrefabName(location.gameObject).GetStableHashCode();
        }
        return new(start, exit, rotation, Valid) { Doorway = source, LocationHash = locationHash };
    }

    internal static TameTravelSummonGate? Find(CandidateView nearby, ZDO actor, Vector3 point, Vector3 previous, TerrainContext context, float now, out bool loading)
    {
        loading = false;
        TameTravelSummonGate? found = null;
        var proxyPrefab = ZoneSystem.instance.m_locationProxyPrefab;
        foreach (var z in nearby)
        {
            var from = z.GetPosition();
            if ((from - point).sqrMagnitude <= 16 && TameTravelRules.Portal(z) is { } portal &&
                TameTravelRules.Crossed(z, actor, previous, point) &&
                TameTravelController.PortalPairRejection(z, out var target) == null && target != null)
            {
                if (found != null) return null;
                var sourceId = z.m_uid;
                var targetId = target.m_uid;
                var rotation = target.GetRotation();
                var end = target.GetPosition();
                var tag = z.GetString(ZDOVars.s_tag);
                bool Valid() => ReferenceEquals(ZDOMan.instance.GetZDO(sourceId), z) &&
                    TameTravelRules.Portal(z) != null && z.GetPosition() == from && z.GetString(ZDOVars.s_tag) == tag &&
                    TameTravelController.PortalPairRejection(z, out var destination) == null &&
                    destination != null && destination.m_uid == targetId && destination.GetPosition() == end &&
                    Quaternion.Angle(destination.GetRotation(), rotation) < .01f;
                found = new(from, end + rotation * Vector3.forward * portal.m_exitDistance + Vector3.up, rotation, Valid);
            }
            if (proxyPrefab == null || z.GetPrefab() != proxyPrefab.name.GetStableHashCode()) continue;
            if (!context.Include(z, now)) continue;
            var proxy = ZNetScene.instance.FindInstance(z);
            if (proxy == null) { loading = true; continue; }
            var gates = proxy.GetComponentsInChildren<Teleport>();
            if (gates.Length == 0 && proxy.GetComponentInChildren<Location>() == null) loading = true;
            bool Retain() => z.IsValid() && context.Include(z, Time.unscaledTime);
            foreach (var gate in gates)
            {
                var match = Dungeon(gate, point, 4, Retain, z.GetInt(ZDOVars.s_location));
                if (match == null) continue;
                if (found != null) return null;
                found = match;
            }
        }
        return found;
    }
}
