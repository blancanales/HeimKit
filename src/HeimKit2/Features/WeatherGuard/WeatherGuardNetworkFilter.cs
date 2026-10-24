using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
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
            // Keep opaque payloads in the source buffer. Allocate a replacement only
            // after the whole packet validates and actually contains protected IDs.
            List<(int Start, int Length)>? removed = null;
            List<(ZDOID Id, uint Data, ushort Owner)>? rejectedRecords = null;
            int count = original.ReadInt();
            if (count < 0 || count > MaximumRecords) throw new InvalidDataException("Invalid sector count.");
            int invalidBlocked = 0;
            for (int i = 0; i < count; i++)
            {
                int start = original.GetPos();
                var id = original.ReadZDOID();
                if (!protectedId(id)) continue;
                blocked++; invalidBlocked++;
                (removed ??= new()).Add((start, original.GetPos() - start));
            }
            int records = 0;
            while (true)
            {
                int start = original.GetPos();
                var id = original.ReadZDOID();
                if (id.IsNone()) break;
                if (++records > MaximumRecords) throw new InvalidDataException("Too many ZDO records.");
                ushort ownerRevision = original.ReadUShort();
                uint revision = original.ReadUInt();
                original.ReadLong();
                original.ReadVector3();
                int length = original.ReadInt();
                if (length < 0 || length > MaximumPacketBytes || length > original.Size() - original.GetPos())
                    throw new InvalidDataException("Invalid embedded package length.");
                original.SetPos(original.GetPos() + length);
                if (!protectedId(id)) continue;
                blocked++;
                (removed ??= new()).Add((start, original.GetPos() - start));
                (rejectedRecords ??= new()).Add((id, revision, ownerRevision));
            }
            if (original.GetPos() != original.Size()) throw new InvalidDataException("Trailing ZDO data.");
            if (blocked == 0) return original;
            byte[] source = original.GetArray();
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(count - invalidBlocked);
            int offset = cursor + sizeof(int);
            foreach (var range in removed!)
            {
                writer.Write(source, offset, range.Start - offset);
                offset = range.Start + range.Length;
            }
            writer.Write(source, offset, source.Length - offset);
            var output = new ZPackage(stream.ToArray());
            // Do not change authority until the complete packet has validated.
            if (rejected != null && rejectedRecords != null)
                foreach (var record in rejectedRecords) rejected(record.Id, record.Data, record.Owner);
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
