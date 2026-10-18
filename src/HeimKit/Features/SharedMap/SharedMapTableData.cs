using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// Validate first; preserve surviving native pin records without rewriting their identities.
public static class SharedMapTableData
{
    public const int MaximumCompressedBytes = 2 * 1024 * 1024;
    private const int MaximumPinBytes = 2 * 1024 * 1024;
    public const long PortalOwner = long.MinValue;
    public const long ResourceOwner = long.MinValue + 1;

    // Startup imports terrain only, after validating the entire native payload; no table write is produced.
    public static void Import(SharedMapExploration exploration, byte[] compressed)
    {
        int count = exploration.Size * exploration.Size;
        byte[] raw = Decompress(compressed, count + MaximumPinBytes + 12);
        raw = ValidateAndClean(raw, count, out _);
        exploration.Import(raw, 8);
    }

    public static byte[]? Merge(SharedMapExploration exploration, byte[]? compressed) => Merge(exploration, compressed, out _);

    public static byte[]? Merge(SharedMapExploration exploration, byte[]? compressed, out int removedDeaths,
        IReadOnlyList<MapMarker>? portals = null, bool managePortalIcons = false,
        Action<string, int, long, float, float, bool>? matchingPreservedPin = null,
        IReadOnlyList<MapMarker>? resources = null, Func<MapPin, bool>? removePin = null)
    {
        removedDeaths = 0;
        int count = exploration.Size * exploration.Size;
        byte[] raw;
        bool changed = false;
        if (compressed == null || compressed.Length == 0)
        {
            if (exploration.ExploredPixels == 0 && (portals == null || portals.Count == 0) && (resources == null || resources.Count == 0)) return null;
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            writer.Write(1); // Native exploration-only format: do not reconcile imported pins.
            writer.Write(count);
            writer.Write(exploration.Pixels);
            raw = output.ToArray();
            changed = true;
        }
        else
        {
            raw = Decompress(compressed, count + MaximumPinBytes + 12);
            byte[] cleaned = ValidateAndClean(raw, count, out removedDeaths);
            changed = removedDeaths != 0;
            raw = cleaned;
            for (int i = 0; i < count; i++)
                if (exploration.Pixels[i] != 0 && raw[8 + i] == 0) { raw[8 + i] = 1; changed = true; }
        }
        if (portals != null || resources != null || removePin != null)
        {
            byte[] normalized = ReconcilePins(raw, count, portals ?? Array.Empty<MapMarker>(),
                resources ?? Array.Empty<MapMarker>(), managePortalIcons, matchingPreservedPin, removePin);
            changed |= !raw.SequenceEqual(normalized);
            raw = normalized;
        }
        if (!changed) { exploration.Import(raw, 8); return null; }
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, true)) gzip.Write(raw, 0, raw.Length);
        if (stream.Length > MaximumCompressedBytes) throw new InvalidDataException("Merged table exceeds the compressed-data limit.");
        exploration.Import(raw, 8);
        return stream.ToArray();
    }

    private static byte[] ReconcilePins(byte[] raw, int count, IReadOnlyList<MapMarker> portals,
        IReadOnlyList<MapMarker> resources, bool managePortalIcons,
        Action<string, int, long, float, float, bool>? matchingPreservedPin, Func<MapPin, bool>? removePin)
    {
        if (portals.Count > 4096 || resources.Count > 4096) throw new InvalidDataException("Too many community markers.");
        portals = portals.Where(p => removePin?.Invoke(new MapPin(PortalOwner, p.Name, p.X, p.Y, p.Z, (int)Minimap.PinType.Icon4)) != true).ToArray();
        resources = resources.Where(p => removePin?.Invoke(new MapPin(ResourceOwner, p.Name, p.X, p.Y, p.Z, (int)Minimap.PinType.Icon3)) != true).ToArray();
        using var reader = new BinaryReader(new MemoryStream(raw, false), new UTF8Encoding(false, true));
        int version = reader.ReadInt32();
        reader.BaseStream.Position = 8 + count;
        int total = version >= 2 ? reader.ReadInt32() : 0;
        using var kept = new MemoryStream();
        int keptCount = 0;
        var byName = portals.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < total; i++)
        {
            int start = (int)reader.BaseStream.Position;
            long owner = reader.ReadInt64();
            string name = reader.ReadString();
            float x = reader.ReadSingle(); float y = reader.ReadSingle(); float z = reader.ReadSingle();
            int type = reader.ReadInt32();
            reader.ReadBoolean();
            if (version >= 3) reader.ReadString();
            if (removePin?.Invoke(new MapPin(owner, name, x, y, z, type)) == true) continue;
            if (owner == PortalOwner || owner == ResourceOwner || managePortalIcons && type == (int)Minimap.PinType.Icon4) continue;
            if (byName.TryGetValue(name, out var matches))
            {
                bool obsolete = managePortalIcons && matches.Any(p =>
                    ((double)p.X - x) * (p.X - x) + ((double)p.Z - z) * (p.Z - z) <= 9);
                matchingPreservedPin?.Invoke(name, type, owner, x, z, obsolete);
                if (obsolete) continue;
            }
            kept.Write(raw, start, (int)reader.BaseStream.Position - start); keptCount++;
        }
        if (version == 1 && portals.Count == 0 && resources.Count == 0) return raw;
        if (keptCount + portals.Count + resources.Count > 8192) throw new InvalidDataException("Merged table exceeds the pin limit.");
        int outputVersion = version == 1 ? 3 : version;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(outputVersion); writer.Write(count); writer.Write(raw, 8, count);
        writer.Write(keptCount + portals.Count + resources.Count); writer.Write(kept.ToArray());
        WriteMarkers(writer, outputVersion, portals, PortalOwner, (int)Minimap.PinType.Icon4);
        WriteMarkers(writer, outputVersion, resources, ResourceOwner, (int)Minimap.PinType.Icon3);
        if (output.Length > count + MaximumPinBytes + 12) throw new InvalidDataException("Merged marker data exceeds the payload limit.");
        return output.ToArray();
    }

    public static IReadOnlyList<MapPin> ReadPins(int size, byte[]? compressed, bool includeDeaths = false)
    {
        if (compressed == null || compressed.Length == 0) return Array.Empty<MapPin>();
        int count = checked(size * size);
        byte[] raw = Decompress(compressed, count + MaximumPinBytes + 12);
        byte[] cleaned = ValidateAndClean(raw, count, out _);
        if (!includeDeaths) raw = cleaned;
        using var reader = new BinaryReader(new MemoryStream(raw, false), new UTF8Encoding(false, true));
        int version = reader.ReadInt32(); reader.BaseStream.Position = 8 + count;
        int total = version >= 2 ? reader.ReadInt32() : 0;
        var pins = new List<MapPin>();
        for (int i = 0; i < total; i++)
        {
            long owner = reader.ReadInt64(); string name = reader.ReadString();
            float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle(); int icon = reader.ReadInt32();
            bool check = reader.ReadBoolean(); string author = version >= 3 ? reader.ReadString() : "";
            pins.Add(new MapPin(owner, name, x, y, z, icon, check, author));
        }
        return pins;
    }

    private static void WriteMarkers(BinaryWriter writer, int version, IReadOnlyList<MapMarker> markers, long owner, int type)
    {
        foreach (var marker in markers)
        {
            if (Encoding.UTF8.GetByteCount(marker.Name) > 4096 || !SharedMapExploration.Finite(marker.X) ||
                !SharedMapExploration.Finite(marker.Y) || !SharedMapExploration.Finite(marker.Z))
                throw new InvalidDataException("Invalid community marker name or position.");
            writer.Write(owner); writer.Write(marker.Name);
            writer.Write(marker.X); writer.Write(marker.Y); writer.Write(marker.Z);
            writer.Write(type); writer.Write(false);
            if (version >= 3) writer.Write("");
        }
    }

    private static byte[] Decompress(byte[] compressed, int limit)
    {
        if (compressed.Length > MaximumCompressedBytes) throw new InvalidDataException("Oversized compressed table.");
        using var input = new MemoryStream(compressed, false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length))) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Oversized decompressed table.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] ValidateAndClean(byte[] raw, int count, out int removedDeaths)
    {
        removedDeaths = 0;
        using var input = new MemoryStream(raw, false);
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
        int version = reader.ReadInt32();
        if (version < 1 || version > 3 || reader.ReadInt32() != count)
            throw new InvalidDataException("Unsupported table format or exploration dimensions.");
        if (input.Length - input.Position < count) throw new InvalidDataException("Truncated exploration.");
        for (int i = 0; i < count; i++) ReadBoolean(reader);
        using var surviving = new MemoryStream();
        int pins = 0;
        if (version >= 2)
        {
            pins = reader.ReadInt32();
            if (pins < 0 || pins > 8192) throw new InvalidDataException("Invalid pin count.");
            for (int i = 0; i < pins; i++)
            {
                int start = (int)input.Position;
                reader.ReadInt64();
                SkipString(reader, 4096);
                for (int axis = 0; axis < 3; axis++)
                    if (!SharedMapExploration.Finite(reader.ReadSingle())) throw new InvalidDataException("Invalid pin position.");
                int type = reader.ReadInt32();
                if (type < 0 || type > 16) throw new InvalidDataException("Unsupported native pin type.");
                ReadBoolean(reader);
                if (version >= 3) SkipString(reader, 512);
                if (type == (int)Minimap.PinType.Death) removedDeaths++;
                else surviving.Write(raw, start, (int)input.Position - start);
            }
        }
        if (input.Position != input.Length) throw new InvalidDataException("Trailing table data.");
        if (removedDeaths == 0) return raw;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(raw, 0, 8 + count); writer.Write(pins - removedDeaths); writer.Write(surviving.ToArray());
        return output.ToArray();
    }

    private static void ReadBoolean(BinaryReader reader)
    {
        if (reader.ReadByte() > 1) throw new InvalidDataException("Invalid boolean byte.");
    }

    private static void SkipString(BinaryReader reader, int limit)
    {
        // BinaryWriter's bounded 7-bit UTF-8 length; never call an allocating ReadString first.
        uint length = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte part = reader.ReadByte();
            if (shift == 28 && part > 7) throw new InvalidDataException("Invalid string length.");
            length |= (uint)(part & 127) << shift;
            if ((part & 128) != 0) continue;
            if (length > limit || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Oversized or truncated pin string.");
            _ = new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)length));
            return;
        }
        throw new InvalidDataException("Invalid string length.");
    }
}
