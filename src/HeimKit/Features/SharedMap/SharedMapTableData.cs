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
    public const long DungeonOwner = long.MinValue + 2;
    public const long RunestoneOwner = long.MinValue + 3;
    public const long SpawnerOwner = long.MinValue + 4;
    public static bool ManagedOwner(long owner) => owner == PortalOwner || owner == ResourceOwner || owner == DungeonOwner || owner == RunestoneOwner || owner == SpawnerOwner;

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
        IReadOnlyList<MapMarker>? resources = null, Func<MapPin, bool>? removePin = null,
        IReadOnlyList<MapMarker>? dungeons = null, Func<MapPin, string?, MapPin?>? publication = null,
        IReadOnlyList<MapMarker>? runestones = null, IReadOnlyList<MapMarker>? spawners = null,
        Action<int, int>? dungeonCleanup = null, float resourceMarkerHideDistanceMeters = 10f)
        => MergeValidated(exploration, Decode(exploration.Size, compressed), out removedDeaths, portals, managePortalIcons,
            matchingPreservedPin, resources, removePin, dungeons, publication, runestones, spawners, dungeonCleanup, resourceMarkerHideDistanceMeters);

    // Scoped to one normalization/reconciliation; never cache untrusted byte-array identities.
    internal sealed class ValidatedTable
    {
        internal readonly byte[] Raw;
        internal readonly int RemovedDeaths;
        internal ValidatedTable(byte[] raw, int removedDeaths) { Raw = raw; RemovedDeaths = removedDeaths; }
    }
    internal static ValidatedTable? Decode(int size, byte[]? compressed)
    {
        if (compressed == null || compressed.Length == 0) return null;
        int count = checked(size * size);
        byte[] raw = Decompress(compressed, count + MaximumPinBytes + 12);
        raw = ValidateAndClean(raw, count, out int removedDeaths);
        return new ValidatedTable(raw, removedDeaths);
    }
    internal static void ImportValidated(SharedMapExploration exploration, ValidatedTable? table)
    { if (table != null) exploration.Import(table.Raw, 8); }

    internal static byte[]? MergeValidated(SharedMapExploration exploration, ValidatedTable? table, out int removedDeaths,
        IReadOnlyList<MapMarker>? portals = null, bool managePortalIcons = false,
        Action<string, int, long, float, float, bool>? matchingPreservedPin = null,
        IReadOnlyList<MapMarker>? resources = null, Func<MapPin, bool>? removePin = null,
        IReadOnlyList<MapMarker>? dungeons = null, Func<MapPin, string?, MapPin?>? publication = null,
        IReadOnlyList<MapMarker>? runestones = null, IReadOnlyList<MapMarker>? spawners = null,
        Action<int, int>? dungeonCleanup = null, float resourceMarkerHideDistanceMeters = 10f)
    {
        removedDeaths = 0;
        int count = exploration.Size * exploration.Size;
        byte[] raw;
        bool changed = false;
        if (table == null)
        {
            if (exploration.ExploredPixels == 0 && (portals == null || portals.Count == 0) && (resources == null || resources.Count == 0) &&
                (dungeons == null || dungeons.Count == 0) && (runestones == null || runestones.Count == 0) && (spawners == null || spawners.Count == 0)) return null;
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
            removedDeaths = table.RemovedDeaths;
            changed = removedDeaths != 0;
            raw = table.Raw;
            for (int i = 0; i < count; i++)
                if (exploration.Pixels[i] != 0 && raw[8 + i] == 0)
                {
                    // Copy only when terrain needs changing; the validated input remains immutable.
                    if (ReferenceEquals(raw, table.Raw)) raw = (byte[])raw.Clone();
                    raw[8 + i] = 1; changed = true;
                }
        }
        if (portals != null || resources != null || removePin != null || dungeons != null || publication != null || runestones != null || spawners != null)
        {
            byte[] normalized = ReconcilePins(raw, count, portals ?? Array.Empty<MapMarker>(),
                resources ?? Array.Empty<MapMarker>(), dungeons ?? Array.Empty<MapMarker>(), managePortalIcons, matchingPreservedPin, removePin, publication,
                runestones ?? Array.Empty<MapMarker>(), spawners ?? Array.Empty<MapMarker>(), dungeonCleanup, resourceMarkerHideDistanceMeters);
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
        IReadOnlyList<MapMarker> resources, IReadOnlyList<MapMarker> dungeons, bool managePortalIcons,
        Action<string, int, long, float, float, bool>? matchingPreservedPin, Func<MapPin, bool>? removePin, Func<MapPin, string?, MapPin?>? publication,
        IReadOnlyList<MapMarker> runestones, IReadOnlyList<MapMarker> spawners, Action<int, int>? dungeonCleanup, float resourceMarkerHideDistanceMeters)
    {
        if (portals.Count > 4096 || resources.Count > 4096 || dungeons.Count > 4096 || runestones.Count > 4096 || spawners.Count > 4096) throw new InvalidDataException("Too many community markers.");
        var dungeonPins = new SharedMapDungeonPins(dungeons);
        dungeons = dungeonPins.Markers;
        int obsoleteDungeons = 0;
        portals = portals.Where(p => removePin?.Invoke(new MapPin(PortalOwner, p.Name, p.X, p.Y, p.Z, (int)Minimap.PinType.Icon4)) != true).ToArray();
        resources = resources.Where(p => removePin?.Invoke(new MapPin(ResourceOwner, p.Name, p.X, p.Y, p.Z, (int)Minimap.PinType.Icon3)) != true).ToArray();
        dungeons = dungeons.Where(p => removePin?.Invoke(new MapPin(DungeonOwner, p.Name, p.X, p.Y, p.Z, (int)Minimap.PinType.Icon2)) != true).ToArray();
        using var reader = new BinaryReader(new MemoryStream(raw, false), new UTF8Encoding(false, true));
        int version = reader.ReadInt32();
        reader.BaseStream.Position = 8 + count;
        int total = version >= 2 ? reader.ReadInt32() : 0;
        using var kept = new MemoryStream();
        using var keptWriter = new BinaryWriter(kept, Encoding.UTF8, true);
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
            bool check = reader.ReadBoolean();
            string author = version >= 3 ? reader.ReadString() : "";
            if (removePin?.Invoke(new MapPin(owner, name, x, y, z, type)) == true) continue;
            if (ManagedOwner(owner) || managePortalIcons && type == (int)Minimap.PinType.Icon4) continue;
            if (dungeonPins.Obsolete(new MapPin(owner, name, x, y, z, type, check, author)))
            { obsoleteDungeons++; continue; }
            if (byName.TryGetValue(name, out var matches))
            {
                bool obsolete = managePortalIcons && type != (int)Minimap.PinType.Icon2 && matches.Any(p =>
                    ((double)p.X - x) * (p.X - x) + ((double)p.Z - z) * (p.Z - z) <= 9);
                matchingPreservedPin?.Invoke(name, type, owner, x, z, obsolete);
                if (obsolete) continue;
            }
            if (publication == null) kept.Write(raw, start, (int)reader.BaseStream.Position - start);
            else
            {
                var published = publication(new MapPin(owner, name, x, y, z, type, check, author), null);
                if (published == null || removePin?.Invoke(published) == true) continue;
                if (published.Owner == owner && published.Name == name && published.X == x && published.Y == y && published.Z == z &&
                    published.Icon == type && published.Checked == check && published.Author == author)
                    kept.Write(raw, start, (int)reader.BaseStream.Position - start);
                else WritePin(keptWriter, version, published);
            }
            keptCount++;
        }
        if (version == 1 && portals.Count == 0 && resources.Count == 0 && dungeons.Count == 0 && runestones.Count == 0 && spawners.Count == 0) return raw;
        var generated = new List<MapPin>();
        void Add(IReadOnlyList<MapMarker> markers, long owner, int icon)
        {
            foreach (var marker in markers)
            {
                var pin = new MapPin(owner, marker.Name, marker.X, marker.Y, marker.Z, icon);
                if (removePin?.Invoke(pin) == true) continue;
                var published = publication == null ? pin : publication(pin, marker.Identity.Length > 0 ? marker.Identity : SharedMapPinRemovals.Identity(pin));
                if (published != null && removePin?.Invoke(published) != true) generated.Add(published);
            }
        }
        Add(portals, PortalOwner, (int)Minimap.PinType.Icon4); Add(resources, ResourceOwner, (int)Minimap.PinType.Icon3); Add(dungeons, DungeonOwner, (int)Minimap.PinType.Icon2);
        Add(runestones, RunestoneOwner, (int)Minimap.PinType.Icon3); Add(spawners, SpawnerOwner, (int)Minimap.PinType.Icon3);
        SharedMapResourcePinSpacing.Apply(generated, resourceMarkerHideDistanceMeters);
        if (keptCount + generated.Count > 8192) throw new InvalidDataException("Merged table exceeds the pin limit.");
        int outputVersion = version == 1 ? 3 : version;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(outputVersion); writer.Write(count); writer.Write(raw, 8, count);
        writer.Write(keptCount + generated.Count); writer.Write(kept.ToArray());
        foreach (var pin in generated) WritePin(writer, outputVersion, pin);
        if (output.Length > count + MaximumPinBytes + 12) throw new InvalidDataException("Merged marker data exceeds the payload limit.");
        dungeonCleanup?.Invoke(dungeonPins.Suppressed, obsoleteDungeons);
        return output.ToArray();
    }

    public static IReadOnlyList<MapPin> ReadPins(int size, byte[]? compressed, bool includeDeaths = false)
    {
        if (compressed == null || compressed.Length == 0) return Array.Empty<MapPin>();
        int count = checked(size * size);
        byte[] raw = Decompress(compressed, count + MaximumPinBytes + 12);
        byte[] cleaned = ValidateAndClean(raw, count, out _);
        if (!includeDeaths) raw = cleaned;
        return ReadValidatedPins(count, raw);
    }
    internal static IReadOnlyList<MapPin> ReadValidatedPins(int size, ValidatedTable? table)
        => table == null ? Array.Empty<MapPin>() : ReadValidatedPins(checked(size * size), table.Raw);
    private static IReadOnlyList<MapPin> ReadValidatedPins(int count, byte[] raw)
    {
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

    private static void WritePin(BinaryWriter writer, int version, MapPin pin)
    {
        _ = SharedMapPinRemovals.Identity(pin);
        writer.Write(pin.Owner); writer.Write(pin.Name); writer.Write(pin.X); writer.Write(pin.Y); writer.Write(pin.Z);
        writer.Write(pin.Icon); writer.Write(pin.Checked);
        if (version >= 3) writer.Write(pin.Author);
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
