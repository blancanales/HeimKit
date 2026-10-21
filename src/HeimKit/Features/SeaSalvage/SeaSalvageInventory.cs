using System;
using System.Collections.Generic;
using System.IO;
using HeimKit.Features.SeaSalvage.Models;

namespace HeimKit.Features.SeaSalvage;
internal static class SeaSalvageInventory
{
    // Audited Inventory.Save header + exact current ItemData payloads.
    // Re-serializing a loaded durability float can truncate another hundredth.
    // Keep the original integer durability and all metadata bytes; replace only grid coordinates.
    public static byte[] Pack(IReadOnlyList<SalvageSource> sources)
    {
        if (sources.Count < 1 || sources.Count > 4) throw new ArgumentOutOfRangeException(nameof(sources));
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(110); writer.Write((ushort)sources.Count);
        for (int i = 0; i < sources.Count; i++)
        {
            byte[] bytes = (byte[])sources[i].Original.Clone();
            if (bytes.Length < 10 || bytes[0] != 110 || (bytes[8] & 64) == 0) throw new InvalidDataException("Unsupported native item payload.");
            bytes[5] = (byte)(i % 2); bytes[6] = (byte)(i / 2);
            writer.Write(bytes, 1, bytes.Length - 1);
        }
        writer.Flush(); return stream.ToArray();
    }
}
