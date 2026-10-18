using System;
using System.IO;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

public static class SharedMapResourceHealth
{
    // null means unobserved/unsupported, never depleted. Missing native health means untouched.
    public static bool? Sections(string encoded, int expected)
    {
        var progress = Progress(encoded, expected);
        return progress.HasValue ? progress.Value.RemainingSections == 0 : (bool?)null;
    }

    public static MiningProgress? Progress(string encoded, int expected)
    {
        if (expected < 1 || expected > 8192) return null;
        if (encoded.Length == 0) return new MiningProgress(expected, expected);
        if (encoded.Length > 4 * ((4 + expected * 4 + 2) / 3)) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length != 4 + expected * 4) return null;
            using var reader = new BinaryReader(new MemoryStream(bytes, false));
            if (reader.ReadInt32() != expected) return null;
            int remaining = 0;
            for (int i = 0; i < expected; i++)
            {
                float health = reader.ReadSingle();
                if (!SharedMapExploration.Finite(health)) return null;
                if (health > 0) remaining++;
            }
            return new MiningProgress(remaining, expected);
        }
        catch (FormatException) { return null; }
    }
}
