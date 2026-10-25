using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// A discovery and its terminal tombstone share one stable identity. No session ZDO IDs are saved.
public sealed class SharedMapLandmarkStore
{
    public const int MaximumDiscoveries = 65536;
    public const int MaximumMarkers = 4096;
    private const uint Magic = 0x4C4D4B48;
    private readonly long _world;
    private readonly bool _spawners;
    private Dictionary<string, MapMarker> _records = new(StringComparer.Ordinal);
    private HashSet<string> _retired = new(StringComparer.Ordinal);
    private bool _dirty;
    private readonly ScheduleToken _nextSave = new();
    private IReadOnlyList<MapMarker>? _activeMarkers, _historicalMarkers;
    public string FilePath { get; }
    public int Count => _records.Count;
    public SharedMapLandmarkStore(string directory, long world, bool spawners)
    {
        _world = world; _spawners = spawners;
        FilePath = Path.Combine(directory, "world-" + world.ToString("X16", CultureInfo.InvariantCulture) + (spawners ? ".spawners" : ".runestones"));
    }
    public bool Contains(string identity) => _records.ContainsKey(identity);
    public static string SourceIdentity(string prefab, float x, float y, float z, string path, bool spawner)
    {
        if (!SharedMapExploration.Finite(y)) throw new ArgumentException("Invalid source height.");
        return SharedMapDungeonIdentity.Create(prefab, x, spawner ? 0 : y, z, path);
    }
    public bool Retired(string identity) => _retired.Contains(identity);
    private static bool Valid(MapMarker marker) => SharedMapPinRemovals.ValidId(marker.Identity) &&
        marker.Name.Length > 0 && Encoding.UTF8.GetByteCount(marker.Name) <= 128 && !marker.Name.Contains("$") &&
        SharedMapExploration.Finite(marker.X) && SharedMapExploration.Finite(marker.Y) && SharedMapExploration.Finite(marker.Z) &&
        Math.Abs(marker.X) <= 100000 && Math.Abs(marker.Z) <= 100000 && Math.Abs(marker.Y) < 4000;

    public bool Discover(MapMarker marker, float? playerX, float? playerZ, float radius, SharedMapExploration map, bool retrospective)
    {
        if (!Valid(marker)) throw new InvalidDataException("Invalid landmark discovery.");
        bool nearby = playerX.HasValue && playerZ.HasValue && SharedMapExploration.Finite(playerX.Value) &&
            SharedMapExploration.Finite(playerZ.Value) && SharedMapExploration.Finite(radius) && radius > 0 &&
            Math.Pow((double)marker.X - playerX.Value, 2) + Math.Pow((double)marker.Z - playerZ.Value, 2) <= (double)radius * radius;
        if (!nearby && !(retrospective && map.IsExplored(marker.X, marker.Z))) return false;
        if (_records.TryGetValue(marker.Identity, out var old))
        {
            // Exact horizontal source reassociation only. Native vertical settling retains accepted height;
            // contradictory horizontal coordinates or source labels cannot replace discovery history.
            if (old.X != marker.X || !_spawners && old.Y != marker.Y || old.Z != marker.Z || old.Name != marker.Name)
                throw new InvalidDataException("Conflicting landmark source identity.");
            return false;
        }
        if (Count >= MaximumDiscoveries) throw new InvalidOperationException("Landmark discovery limit exceeded; prior data was retained.");
        _records.Add(marker.Identity, marker); _dirty = true; _activeMarkers = _historicalMarkers = null; return true;
    }
    public bool Retire(string identity)
    {
        if (!_spawners || !_records.ContainsKey(identity) || !_retired.Add(identity)) return false;
        _dirty = true; _activeMarkers = null; return true;
    }
    public IReadOnlyList<MapMarker> Read(bool enabled, bool removeDestroyed = true)
    {
        if (!enabled) return Array.Empty<MapMarker>();
        var cached = removeDestroyed ? _activeMarkers : _historicalMarkers;
        if (cached != null) return cached;
        // Schema 1 stores source identity hashes and labels, not reversible prefab names.
        // Suppress legacy hidden-point labels before the publication limit while retaining
        // discoveries/tombstones. Never infer native source destruction from this policy filter.
        var markers = _records.Where(p => (!_spawners || SharedMapLandmarkPrefab.PhysicalSpawnerLabel(p.Value.Name)) &&
            (!removeDestroyed || !_retired.Contains(p.Key))).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
        if (markers.Length > MaximumMarkers) throw new InvalidOperationException("Landmark publication limit exceeded; prior table data must be retained.");
        if (removeDestroyed) _activeMarkers = markers; else _historicalMarkers = markers;
        return markers;
    }
    public bool Load()
    {
        if (!File.Exists(FilePath))
        {
            if (File.Exists(FilePath + ".bak")) throw new InvalidDataException("Landmark store is missing beside its backup.");
            return false;
        }
        using var input = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 53 || input.Length > 53L + MaximumDiscoveries * 175L) throw new InvalidDataException("Invalid landmark store length.");
        using var reader = new BinaryReader(input);
        byte[] body = reader.ReadBytes((int)input.Length - 32);
        using var sha = SHA256.Create();
        if (!sha.ComputeHash(body).SequenceEqual(reader.ReadBytes(32))) throw new InvalidDataException("Landmark checksum mismatch.");
        using var fields = new BinaryReader(new MemoryStream(body, false), new UTF8Encoding(false, true));
        if (fields.ReadUInt32() != Magic || fields.ReadInt32() != 1 || fields.ReadInt64() != _world || fields.ReadByte() != (_spawners ? 1 : 0))
            throw new InvalidDataException("Landmark schema, world or source kind mismatch.");
        int count = fields.ReadInt32();
        if (count < 0 || count > MaximumDiscoveries) throw new InvalidDataException("Invalid landmark record count.");
        var loaded = new Dictionary<string, MapMarker>(StringComparer.Ordinal); var retired = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            byte[] id = fields.ReadBytes(32); if (id.Length != 32) throw new EndOfStreamException();
            int length = fields.ReadUInt16(); if (length < 1 || length > 128) throw new InvalidDataException("Invalid landmark label length.");
            byte[] name = fields.ReadBytes(length); if (name.Length != length) throw new EndOfStreamException();
            var marker = new MapMarker(new UTF8Encoding(false, true).GetString(name), fields.ReadSingle(), fields.ReadSingle(), fields.ReadSingle(),
                BitConverter.ToString(id).Replace("-", "").ToLowerInvariant());
            byte terminal = fields.ReadByte();
            if (!Valid(marker) || terminal > 1 || !_spawners && terminal != 0 || loaded.ContainsKey(marker.Identity)) throw new InvalidDataException("Invalid landmark record.");
            loaded.Add(marker.Identity, marker); if (terminal == 1) retired.Add(marker.Identity);
        }
        if (fields.BaseStream.Position != fields.BaseStream.Length) throw new InvalidDataException("Trailing landmark data.");
        _records = loaded; _retired = retired; _dirty = false; _activeMarkers = _historicalMarkers = null; return true;
    }
    public bool SaveIfDue(float now, float interval) { if (!_nextSave.Ready(now)) return false; _nextSave.Postpone(now, interval); return Save(); }
    public bool Save()
    {
        if (!_dirty) return false;
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
        {
            writer.Write(Magic); writer.Write(1); writer.Write(_world); writer.Write((byte)(_spawners ? 1 : 0)); writer.Write(Count);
            foreach (var marker in _records.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
            {
                for (int i = 0; i < 64; i += 2) writer.Write(byte.Parse(marker.Identity.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                byte[] label = Encoding.UTF8.GetBytes(marker.Name); writer.Write((ushort)label.Length); writer.Write(label);
                writer.Write(marker.X); writer.Write(marker.Y); writer.Write(marker.Z); writer.Write((byte)(Retired(marker.Identity) ? 1 : 0));
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = body.ToArray(); using var sha = SHA256.Create();
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); byte[] checksum = sha.ComputeHash(bytes); file.Write(checksum, 0, checksum.Length); file.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak"); else File.Move(temporary, FilePath);
            _dirty = false; return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
