using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HeimKit.Core.Ownership;

// Targeted snapshots participate in native per-peer bookkeeping. In particular,
// a queued invalidation must not follow a snapshot that already moved this ID.
internal static class ZdoReplication
{
    // Keep type-initialization failures inside a nested holder so callers get
    // the actionable compatibility error rather than TypeInitializationException.
    private static class Accessors
    {
        // Prevent beforefieldinit from moving initialization outside the caller's try.
        static Accessors() { }
        internal static readonly Type PeerType = typeof(ZDOMan).GetNestedType("ZDOPeer", BindingFlags.NonPublic)!;
        internal static readonly Func<ZDOMan, long, object> GetPeer = NativeAccess.PeerGetter();
        internal static readonly Func<object, ZNetPeer> Peer = NativeAccess.ObjectField<ZNetPeer>(PeerType, "m_peer");
        internal static readonly Func<object, HashSet<ZDOID>> Invalid = NativeAccess.ObjectField<HashSet<ZDOID>>(PeerType, "m_invalidSector");
        internal static readonly Func<object, HashSet<ZDOID>> Forced = NativeAccess.ObjectField<HashSet<ZDOID>>(PeerType, "m_forceSend");
        internal static readonly Func<object, IDictionary> Known = NativeAccess.ObjectField<IDictionary>(PeerType, "m_zdos");
        internal static readonly Type InfoType = PeerType.GetNestedType("PeerZDOInfo", BindingFlags.Public | BindingFlags.NonPublic)!;
        internal static readonly Func<uint, ushort, float, object> Info = NativeAccess.PeerInfo(InfoType);
    }
    internal static void CheckCompatibility()
    {
        try { _ = Accessors.GetPeer; _ = Accessors.Info; }
        catch (Exception error) { throw new InvalidOperationException("Native animal replication peer bookkeeping is unavailable.", error); }
    }

    internal static void ResetClientCache(ZDOID id, Action<ZPackage> removeRemoteCache, Action sendSnapshot)
    {
        if (id.IsNone()) throw new ArgumentException("A client cache reset requires an original record ID.", nameof(id));
        var packet = new ZPackage(); packet.Write(1); packet.Write(id);
        removeRemoteCache(packet);
        sendSnapshot();
    }

    internal static void Send(ZNetPeer peer, ZDO record, uint? wireRevision = null)
    {
        var native = Accessors.GetPeer(ZDOMan.instance, peer.m_uid);
        if (native == null || !ReferenceEquals(Accessors.Peer(native), peer))
            throw new InvalidOperationException("Animal replication peer is no longer the joined connection.");
        uint revision = wireRevision ?? record.DataRevision;
        var packet = new ZPackage(); packet.Write(0); packet.Write(record.m_uid); packet.Write(record.OwnerRevision);
        packet.Write(revision); packet.Write(record.GetOwner()); packet.Write(record.GetPosition());
        var payload = new ZPackage(); record.Serialize(payload); packet.Write(payload); packet.Write(ZDOID.None);
        Transmit(native, record.m_uid, revision, record.OwnerRevision, Time.time, packet, p => peer.m_rpc.Invoke("ZDOData", p));
    }

    internal static void Transmit(object native, ZDOID id, uint data, ushort owner, float now, ZPackage packet, Action<ZPackage> send)
    {
        var invalid = (HashSet<ZDOID>)Accessors.Invalid(native);
        var forced = (HashSet<ZDOID>)Accessors.Forced(native);
        var known = (IDictionary)Accessors.Known(native);
        object info = Accessors.Info(data, owner, now);
        // No bookkeeping is consumed if the reliable send fails. The snapshot
        // itself replaces sector invalidation: native InternalSetPosition moves
        // the existing record from its old sector to its new one.
        send(packet);
        invalid.Remove(id); forced.Remove(id); known[id] = info;
    }
}
