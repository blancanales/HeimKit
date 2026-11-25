using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HeimKit.Core.Control;

internal enum RosterGroup : byte { Online, Previous, Banned }

internal sealed class ModerationRow
{
    internal ModerationRow(RosterGroup group, string id, string name)
    {
        Group = group; Id = id; Name = name;
    }
    internal RosterGroup Group { get; }
    internal string Id { get; }
    internal string Name { get; }
    internal string Label => Name.Length == 0 ? Id : Name + "  (" + Id + ")";
}

internal static class ModerationProtocol
{
    internal const string State = "HeimKit_Moderation", Action = "HeimKit_Moderate";
    internal const int ChunkBytes = 12000;

    // ZRpc is ordered. A first chunk replaces any incomplete prior generation.
    internal static byte[] Encode(ModerationRow[] rows)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        foreach (var row in rows)
        {
            writer.Write((byte)row.Group);
            writer.Write(row.Id);
            writer.Write(row.Name);
        }
        return stream.ToArray();
    }

    internal static ZPackage Chunk(byte[] bytes, int offset, out int next)
    {
        int count = Math.Min(ChunkBytes, bytes.Length - offset);
        next = offset + count;
        var result = new byte[count + 1];
        result[0] = (byte)((offset == 0 ? 1 : 0) | (next == bytes.Length ? 2 : 0));
        Buffer.BlockCopy(bytes, offset, result, 1, count);
        return new ZPackage(result);
    }

    internal static ModerationRow[] Decode(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var rows = new List<ModerationRow>();
        while (stream.Position < stream.Length)
        {
            var group = (RosterGroup)reader.ReadByte();
            if ((byte)group > (byte)RosterGroup.Banned) throw new InvalidDataException();
            rows.Add(new ModerationRow(group, reader.ReadString(), reader.ReadString()));
        }
        return rows.ToArray();
    }
}
