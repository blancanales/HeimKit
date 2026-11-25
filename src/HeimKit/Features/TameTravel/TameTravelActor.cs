using HeimKit.Core.Players;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

internal readonly struct TameTravelActor
{
    internal ZNetPeer? Remote { get; }
    internal Player? Local { get; }
    private readonly WorldActor _host;

    private TameTravelActor(ZNetPeer peer)
    {
        Remote = peer;
        Local = null;
        _host = default;
    }

    private TameTravelActor(Player player, WorldActor host)
    {
        Remote = null;
        Local = player;
        _host = host;
    }

    internal bool IsLocal => Remote == null && !ReferenceEquals(Local, null);
    internal long Owner => Remote?.m_uid ?? _host.Owner;
    internal ZDOID Character => Remote?.m_characterID ?? _host.Character;
    internal string Name => Remote?.m_playerName ?? (Local != null ? Local.GetPlayerName() : "");
    internal Vector3 Position(ZDO record) => Local != null ? Local.transform.position : record.GetPosition();
    internal bool Matches(TameTravelActor other) =>
        ReferenceEquals(Remote, other.Remote) && ReferenceEquals(Local, other.Local) && Owner == other.Owner;

    internal ZDO? ResolveLocal() =>
        Local != null && ReferenceEquals(Local, Player.m_localPlayer) && Local.GetZDOID() == Character &&
        WorldActors.TryLocal(out var current) && current.Owner == Owner ? WorldActors.Resolve(_host) : null;

    internal static bool TryLocal(Player? player, out TameTravelActor actor)
    {
        actor = default;
        if (player == null || !ReferenceEquals(player, Player.m_localPlayer) || !WorldActors.TryLocal(out var host))
            return false;
        actor = new TameTravelActor(player, host);
        return true;
    }

    public static implicit operator TameTravelActor(ZNetPeer peer) => new(peer);
}
