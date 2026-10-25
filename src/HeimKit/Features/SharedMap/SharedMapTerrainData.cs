using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.IO;
using System.IO.Compression;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// Validate the whole native terrain record before using its last-operation point as evidence.
public static class SharedMapTerrainData
{
    public static TerrainSnapshot? Read(byte[]? compressed)
    {
        if (compressed == null || compressed.Length == 0 || compressed.Length > 2 * 1024 * 1024) return null;
        try
        {
            using var input = new MemoryStream(compressed, false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); var buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > 4 * 1024 * 1024) return null;
                output.Write(buffer, 0, read);
            }
            output.Position = 0; using var reader = new BinaryReader(output);
            if (reader.ReadInt32() != 1) return null;
            int operations = reader.ReadInt32();
            float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle(), radius = reader.ReadSingle();
            if (operations < 0 || !Finite(x) || !Finite(y) || !Finite(z) || !Finite(radius) || radius < 0 || radius > 1000) return null;
            int count = reader.ReadInt32();
            if (count < 1 || count > 66049) return null;
            var heights = new float[count];
            for (int i = 0; i < count; i++)
            {
                byte modified = reader.ReadByte(); if (modified > 1) return null;
                if (modified == 0) continue;
                float level = reader.ReadSingle(), smooth = reader.ReadSingle();
                if (!Finite(level) || !Finite(smooth) || !Finite(level + smooth)) return null;
                heights[i] = level + smooth;
            }
            int paint = reader.ReadInt32(); if (paint < 0 || paint > 65536) return null;
            for (int i = 0; i < paint; i++)
            {
                byte modified = reader.ReadByte(); if (modified > 1) return null;
                if (modified != 0)
                    for (int channel = 0; channel < 4; channel++)
                        if (!Finite(reader.ReadSingle())) return null;
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) return null;
            return new TerrainSnapshot(operations, x, y, z, heights);
        }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException) { return null; }
    }

    private static bool Finite(float value) => SharedMapExploration.Finite(value);
    public static bool Lowered(TerrainSnapshot? before, TerrainSnapshot? after)
    {
        if (before == null || after == null || after.Operations <= before.Operations ||
            (long)after.Operations - before.Operations > 64 || before.Heights.Length != after.Heights.Length) return false;
        for (int i = 0; i < before.Heights.Length; i++)
            if (after.Heights[i] < before.Heights[i] - .01f) return true;
        return false;
    }
}
