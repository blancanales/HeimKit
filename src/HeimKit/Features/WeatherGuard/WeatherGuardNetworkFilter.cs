using System;
using System.Collections.Generic;
using System.IO;

namespace HeimKit.Features.WeatherGuard;

// Keep unprotected records opaque. Never infer a damage cause from a health delta.
public static class WeatherGuardNetworkFilter
{
    public const int MaximumPacketBytes = 2 * 1024 * 1024;
    public const int MaximumRecords = 32768;
    internal static ZPackage ReadPackage(ZPackage packet)
    {
        int length = packet.ReadInt();
        if (length < 0 || length > MaximumPacketBytes || length > packet.Size() - packet.GetPos())
            throw new InvalidDataException("Invalid embedded package length.");
        // Native ReadPackage trusts the declared length before allocating its byte buffer.
        return new ZPackage(packet.ReadByteArray(length));
    }
    public static ZRoutedRpc.RoutedRPCData ReadRouted(ZPackage packet)
    {
        int cursor = packet.GetPos();
        try
        {
            if (packet.Size() > MaximumPacketBytes) throw new InvalidDataException("Oversized routed packet.");
            var data = new ZRoutedRpc.RoutedRPCData
            {
                m_msgID = packet.ReadLong(), m_senderPeerID = packet.ReadLong(), m_targetPeerID = packet.ReadLong(),
                m_targetZDO = packet.ReadZDOID(), m_methodHash = packet.ReadInt(), m_parameters = ReadPackage(packet)
            };
            if (packet.GetPos() != packet.Size()) throw new InvalidDataException("Trailing routed data.");
            return data;
        }
        finally { packet.SetPos(cursor); }
    }
    public static bool RelevantRouted(ZPackage packet, Func<ZDOID, bool> protectedId)
    {
        int cursor = packet.GetPos();
        try
        {
            packet.ReadLong(); packet.ReadLong(); packet.ReadLong();
            var id = packet.ReadZDOID(); int method = packet.ReadInt();
            return protectedId(id) || id.IsNone() && method == "DestroyZDO".GetStableHashCode();
        }
        finally { packet.SetPos(cursor); }
    }
    public static ZPackage FilterData(ZPackage original, Func<ZDOID, bool> protectedId, out int blocked,
        Action<ZDOID, uint, ushort>? rejected = null)
    {
        int cursor = original.GetPos();
        blocked = 0;
        try
        {
            if (original.Size() > MaximumPacketBytes) throw new InvalidDataException("Oversized ZDO packet.");
            var invalid = new List<ZDOID>();
            var rejectedRecords = new List<(ZDOID Id, uint Data, ushort Owner)>();
            int count = original.ReadInt();
            if (count < 0 || count > MaximumRecords) throw new InvalidDataException("Invalid sector count.");
            for (int i = 0; i < count; i++)
            {
                var id = original.ReadZDOID();
                if (protectedId(id)) blocked++; else invalid.Add(id);
            }
            var output = new ZPackage();
            output.Write(invalid.Count);
            foreach (var id in invalid) output.Write(id);
            int records = 0;
            while (true)
            {
                var id = original.ReadZDOID();
                if (id.IsNone()) break;
                if (++records > MaximumRecords) throw new InvalidDataException("Too many ZDO records.");
                ushort ownerRevision = original.ReadUShort();
                uint revision = original.ReadUInt();
                long owner = original.ReadLong();
                var position = original.ReadVector3();
                var data = ReadPackage(original);
                if (protectedId(id)) { blocked++; rejectedRecords.Add((id, revision, ownerRevision)); continue; }
                output.Write(id); output.Write(ownerRevision); output.Write(revision);
                output.Write(owner); output.Write(position); output.Write(data);
            }
            if (original.GetPos() != original.Size()) throw new InvalidDataException("Trailing ZDO data.");
            output.Write(ZDOID.None); output.SetPos(0);
            // Do not change authority until the complete packet has validated.
            if (rejected != null) foreach (var record in rejectedRecords) rejected(record.Id, record.Data, record.Owner);
            return output;
        }
        finally { original.SetPos(cursor); }
    }

    public static ZPackage FilterDestroyed(ZPackage original, Func<ZDOID, bool> protectedId, out int blocked, Action<ZDOID>? rejected = null)
    {
        int cursor = original.GetPos(); blocked = 0;
        try
        {
            int count = original.ReadInt();
            if (count < 0 || count > MaximumRecords || original.Size() > MaximumPacketBytes)
                throw new InvalidDataException("Invalid destroyed-object list.");
            var kept = new List<ZDOID>();
            var excluded = new List<ZDOID>();
            for (int i = 0; i < count; i++)
            {
                var id = original.ReadZDOID();
                if (protectedId(id)) { blocked++; excluded.Add(id); } else kept.Add(id);
            }
            if (original.GetPos() != original.Size()) throw new InvalidDataException("Trailing destroyed-object data.");
            var output = new ZPackage(); output.Write(kept.Count);
            foreach (var id in kept) output.Write(id);
            output.SetPos(0);
            if (rejected != null) foreach (var id in excluded) rejected(id);
            return output;
        }
        finally { original.SetPos(cursor); }
    }
}
