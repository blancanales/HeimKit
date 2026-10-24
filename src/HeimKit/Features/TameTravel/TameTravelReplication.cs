using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HeimKit.Features.TameTravel;

// Targeted snapshots participate in native per-peer bookkeeping. In particular,
// a queued invalidation must not follow a snapshot that already moved this ID.
internal static class TameTravelReplication
{
    private static readonly Type PeerType = typeof(ZDOMan).GetNestedType("ZDOPeer", BindingFlags.NonPublic)!;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo GetPeer = typeof(ZDOMan).GetMethod("GetPeer", Members, null, new[] { typeof(long) }, null)!;
    private static readonly FieldInfo Peer = PeerType.GetField("m_peer", Members)!,
        Invalid = PeerType.GetField("m_invalidSector", Members)!,
        Forced = PeerType.GetField("m_forceSend", Members)!,
        Known = PeerType.GetField("m_zdos", Members)!;
    private static readonly Type InfoType = PeerType.GetNestedType("PeerZDOInfo", BindingFlags.Public | BindingFlags.NonPublic)!;
    private static readonly ConstructorInfo Info = InfoType.GetConstructor(new[] { typeof(uint), typeof(ushort), typeof(float) })!;

    internal static void CheckCompatibility()
    {
        if (GetPeer == null || GetPeer.ReturnType != PeerType || Peer?.FieldType != typeof(ZNetPeer) ||
            Invalid?.FieldType != typeof(HashSet<ZDOID>) || Forced?.FieldType != typeof(HashSet<ZDOID>) ||
            Known == null || !typeof(IDictionary).IsAssignableFrom(Known.FieldType) || Info == null)
            throw new MissingMemberException("Native animal replication peer bookkeeping is unavailable.");
    }

    internal static void Send(ZNetPeer peer, ZDO record, uint? wireRevision = null)
    {
        var native = GetPeer.Invoke(ZDOMan.instance, new object[] { peer.m_uid });
        if (native == null || !ReferenceEquals(Peer.GetValue(native), peer))
            throw new InvalidOperationException("Animal replication peer is no longer the joined connection.");
        uint revision = wireRevision ?? record.DataRevision;
        var packet = new ZPackage(); packet.Write(0); packet.Write(record.m_uid); packet.Write(record.OwnerRevision);
        packet.Write(revision); packet.Write(record.GetOwner()); packet.Write(record.GetPosition());
        var payload = new ZPackage(); record.Serialize(payload); packet.Write(payload); packet.Write(ZDOID.None);
        Transmit(native, record.m_uid, revision, record.OwnerRevision, Time.time, packet, p => peer.m_rpc.Invoke("ZDOData", p));
    }

    internal static void Transmit(object native, ZDOID id, uint data, ushort owner, float now, ZPackage packet, Action<ZPackage> send)
    {
        var invalid = (HashSet<ZDOID>)Invalid.GetValue(native);
        var forced = (HashSet<ZDOID>)Forced.GetValue(native);
        var known = (IDictionary)Known.GetValue(native);
        object info = Info.Invoke(new object[] { data, owner, now });
        // No bookkeeping is consumed if the reliable send fails. The snapshot
        // itself replaces sector invalidation: native InternalSetPosition moves
        // the existing record from its old sector to its new one.
        send(packet);
        invalid.Remove(id); forced.Remove(id); known[id] = info;
    }
}
