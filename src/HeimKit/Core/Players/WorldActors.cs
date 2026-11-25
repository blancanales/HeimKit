using System.Collections.Generic;
using HeimKit.Core.World;
using UnityEngine;

namespace HeimKit.Core.Players;

// An actor belongs to the world. Only remote players also have a network peer.
internal readonly struct WorldActor
{
    internal WorldActor(long owner, ZDOID character)
    {
        Owner = owner;
        Character = character;
    }

    internal long Owner { get; }
    internal ZDOID Character { get; }
}

internal static class WorldActors
{
    internal static void ReadSimulationOwners(IPlayerPresence presence,
        Dictionary<long, (Vector3 Point, int Radius, bool Classic)> owners)
    {
        owners.Clear();
        var peers = presence.Peers;
        for (int i = 0; i < peers.Count; i++)
        {
            var peer = peers[i];
            if (!peer.IsReady() || peer.m_characterID.IsNone()) continue;
            var distance = peer.m_simulationDistance;
            owners[peer.m_uid] = (peer.m_refPos, distance.NearSimulationDistance, distance.IsClassic);
        }
        var net = ZNet.instance;
        if (net == null || !WorldSession.IsPlayerHosted(WorldSession.Identify(net)) ||
            Player.m_localPlayer == null || ZDOMan.instance == null) return;

        long owner = ZDOMan.GetSessionID();
        var actor = ZDOMan.instance.GetZDO(Player.m_localPlayer.GetZDOID());
        if (actor == null || !actor.IsValid() || actor.GetOwner() != owner) return;
        var point = net.GetReferencePosition();
        if (!PlayerWorldScope.Finite(point)) return;
        var localDistance = net.GetSyncedSimulationDistance();
        owners[owner] = (point, localDistance.NearSimulationDistance, localDistance.IsClassic);
    }

    internal static void Read(IPlayerPresence presence, List<WorldActor> actors)
    {
        actors.Clear();
        if (TryLocal(out var host)) actors.Add(host);

        var peers = presence.Peers;
        for (int index = 0; index < peers.Count; index++)
        {
            var peer = peers[index];
            if (peer.IsReady()) actors.Add(new WorldActor(peer.m_uid, peer.m_characterID));
        }
    }

    internal static bool TryLocal(out WorldActor actor)
    {
        actor = default;
        var player = Player.m_localPlayer;
        if (player == null || ZDOMan.instance == null ||
            !WorldSession.IsPlayerHosted(WorldSession.Identify(ZNet.instance))) return false;
        var candidate = new WorldActor(ZDOMan.GetSessionID(), player.GetZDOID());
        if (Resolve(candidate) == null) return false;
        actor = candidate;
        return true;
    }

    internal static bool IsCurrent(IPlayerPresence presence, WorldActor actor)
    {
        if (actor.Owner == ZDOMan.GetSessionID())
            return TryLocal(out var host) && host.Character == actor.Character;
        var peers = presence.Peers;
        for (int i = 0; i < peers.Count; i++)
            if (peers[i].m_uid == actor.Owner)
                return peers[i].IsReady() && peers[i].m_characterID == actor.Character;
        return false;
    }

    internal static ZDO? Resolve(WorldActor actor)
    {
        if (actor.Character.IsNone()) return null;
        var record = ZDOMan.instance?.GetZDO(actor.Character);
        return record != null && record.IsValid() && record.GetOwner() == actor.Owner ? record : null;
    }
}
