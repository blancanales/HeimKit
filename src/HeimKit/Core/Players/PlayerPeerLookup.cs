using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;

namespace HeimKit.Core.Players;

// Use the current census and physical connection identity. No cached peer can
// survive disconnect/replacement or require a separate invalidation protocol.
internal static class PlayerPeerLookup
{
    public static ZNetPeer? Ready(IReadOnlyList<ZNetPeer> peers, ZRpc rpc)
    {
        if (IncomingPackets.TryRosterPeer(rpc, peers, out var peer)) return peer;
        for (int i = 0; i < peers.Count; i++)
            if (ReferenceEquals(peers[i].m_rpc, rpc)) return peers[i].IsReady() ? peers[i] : null;
        return null;
    }
}
