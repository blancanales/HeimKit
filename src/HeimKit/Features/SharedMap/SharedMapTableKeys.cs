using System;
using System.Security.Cryptography;

namespace HeimKit.Features.SharedMap;
// Vanilla reads its original map field. HeimKit can associate each managed row with its source,
// even after a portal rename or a publication offset change, using this payload-bound companion.
internal static class SharedMapTableKeys
{
    internal static readonly int Field = "heimkit.sharedmap.sources.v1".GetStableHashCode();
    private static int Nibble(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
    internal static void Encode(string id, byte[] destination, int offset)
    {
        for (int i = 0; i < 32; i++)
            destination[offset + i] = (byte)((Nibble(id[2 * i]) << 4) | Nibble(id[2 * i + 1]));
    }

    internal static void Restore(ZDO table, byte[]? trusted)
    {
        if (SharedMapController.Equal(trusted, table.GetByteArray(Field)))
            return;
        if (trusted != null)
            table.Set(Field, trusted);
        else if (table.RemoveByteArray(Field))
        {
            table.DataRevision++;
            ZDOMan.instance.SetDirtySector(table);
        }
    }

    internal static void Publish(ZDO table, byte[]? payload, byte[]? keys)
    {
        if (payload == null || keys == null || keys.Length == 0)
            return;
        using var sha = SHA256.Create();
        var packed = new byte[32 + keys.Length];
        sha.ComputeHash(payload).CopyTo(packed, 0);
        keys.CopyTo(packed, 32);
        if (!SharedMapController.Equal(packed, table.GetByteArray(Field)))
            table.Set(Field, packed);
    }

    internal static string[]? Read(ZDO? table, int count)
    {
        if (table == null || count < 1 || count > 8192)
            return null;
        var packed = table.GetByteArray(Field);
        var map = table.GetByteArray(ZDOVars.s_data);
        if (packed == null || map == null || packed.Length != 32 + 32 * count || map.Length > SharedMapTableData.MaximumCompressedBytes)
            return null;
        using var sha = SHA256.Create();
        if (!packed.AsSpan(0, 32).SequenceEqual(sha.ComputeHash(map)))
            return null;
        var ids = new string[count];
        var unique = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            ids[i] = BitConverter.ToString(packed, 32 + i * 32, 32).Replace("-", "").ToLowerInvariant();
            if (!unique.Add(ids[i]))
                return null;
        }

        return ids;
    }
}
