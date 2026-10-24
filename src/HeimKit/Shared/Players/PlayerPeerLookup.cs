using System;
using System.Collections.Generic;

namespace HeimKit.Shared.Players;

// Use the current census and physical connection identity. No cached peer can
// survive disconnect/replacement or require a separate invalidation protocol.
internal static class PlayerPeerLookup
{
    public static ZNetPeer? Ready(IReadOnlyList<ZNetPeer> peers, ZRpc rpc)
    {
        for (int i = 0; i < peers.Count; i++)
            if (ReferenceEquals(peers[i].m_rpc, rpc)) return peers[i].IsReady() ? peers[i] : null;
        return null;
    }
}
