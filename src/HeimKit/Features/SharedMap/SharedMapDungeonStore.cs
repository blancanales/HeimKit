using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

public sealed class SharedMapDungeonStore
{
    private const uint Magic = 0x47444B48; // HKDG
    public const int MaximumDiscoveries = 65536;
    public const int MaximumMarkers = 4096;
    private readonly long _world;
    private Dictionary<string, MapMarker> _discoveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Total, int Picked)> _progress = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
    private long _revision, _savedRevision;
    private float _nextSave;
    private IReadOnlyList<MapMarker>? _markers;
    public string FilePath { get; }
    public int Count => _discoveries.Count;
    public SharedMapDungeonStore(string directory, long world)
    {
        _world = world;
        FilePath = Path.Combine(directory, "world-" + world.ToString("X16", CultureInfo.InvariantCulture) + ".entrances");
    }

    public bool Contains(string identity) => _discoveries.ContainsKey(identity);
    public bool Visited(string identity) => _visited.Contains(identity);
    public bool Visit(string identity)
    {
        if (!_discoveries.ContainsKey(identity) || !_visited.Add(identity)) return false;
        _revision++; _markers = null; return true;
    }
    public bool VisitFromPickups(string identity, int total, int picked)
    {
        // Called only after a fresh, fully matched inventory scan. Stored history alone is insufficient.
        var accepted = ProgressState(identity);
        return total > 0 && picked > 0 && picked <= total && accepted.Total == total && accepted.Picked == picked && Visit(identity);
    }
    public int ShareVisits(IReadOnlyList<string> locationGateways)
    {
        // The caller supplies gateways from one validated placed location, never nearby locations.
        if (!locationGateways.Any(Visited)) return 0;
        int changed = 0; foreach (string gateway in locationGateways) if (Visit(gateway)) changed++;
        return changed;
    }
    public (int Total, int Picked) ProgressState(string identity) => _progress.TryGetValue(identity, out var value) ? value : (0, -1);
    private static bool Valid(MapMarker marker) => SharedMapPinRemovals.ValidId(marker.Identity) &&
        marker.Name.Length > 0 && Encoding.UTF8.GetByteCount(marker.Name) <= 128 &&
        SharedMapExploration.Finite(marker.X) && SharedMapExploration.Finite(marker.Y) && SharedMapExploration.Finite(marker.Z) &&
        Math.Abs(marker.X) <= 100000 && Math.Abs(marker.Z) <= 100000 && Math.Abs(marker.Y) < 4000;

    public bool Discover(MapMarker marker)
    {
        if (!Valid(marker)) throw new InvalidDataException("Invalid dungeon discovery.");
        if (_discoveries.ContainsKey(marker.Identity)) return false;
        if (Count >= MaximumDiscoveries) throw new InvalidOperationException("Dungeon discovery storage limit exceeded; existing discoveries were retained.");
        _discoveries.Add(marker.Identity, marker); _revision++; _markers = null; return true;
    }

    public bool DiscoverNearby(MapMarker marker, float playerX, float playerZ, float radius)
    {
        if (!SharedMapExploration.Finite(playerX) || !SharedMapExploration.Finite(playerZ) ||
            !SharedMapExploration.Finite(radius) || radius <= 0) return false;
        double dx = (double)marker.X - playerX, dz = (double)marker.Z - playerZ;
        return dx * dx + dz * dz <= (double)radius * radius && Discover(marker);
    }

    public bool DiscoverExplored(MapMarker marker, SharedMapExploration map) =>
        map.IsExplored(marker.X, marker.Z) && Discover(marker);

    public bool Progress(string identity, int total, int picked, bool verifiedEmpty = false)
    {
        if (!_discoveries.ContainsKey(identity) || total < 0 || total > 65536 || picked < 0 || picked > total) return false;
        bool established = _progress.TryGetValue(identity, out var previous);
        var next = total < previous.Total || total == 0 && !verifiedEmpty
            ? (Total: previous.Total, Picked: -1) : (Total: total, Picked: picked);
        if (established && next == previous) return false;
        _progress[identity] = next; _revision++; _markers = null; return true;
    }
    public static string ProgressLabel(string name, int total, int picked) => total >= 0 && picked >= 0 && picked <= total
        ? name + " " + (total == 0 ? 0 : (int)Math.Ceiling(100d * (total - picked) / total)).ToString(CultureInfo.InvariantCulture) + "%" : name;
    public IReadOnlyList<MapMarker> Read(bool progress = true)
    {
        if (Count > MaximumMarkers) throw new InvalidOperationException("Dungeon marker limit exceeded; prior table data must be retained.");
        if (!progress) return _discoveries.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
        return _markers ??= _discoveries.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => {
            var marker = p.Value; var state = ProgressState(p.Key);
            return new MapMarker(Visited(p.Key) ? ProgressLabel(marker.Name, state.Total, state.Picked) : marker.Name,
                marker.X, marker.Y, marker.Z, marker.Identity);
        }).ToArray();
    }

    public bool Load()
    {
        if (!File.Exists(FilePath))
        {
            if (File.Exists(FilePath + ".bak")) throw new InvalidDataException("Dungeon discoveries are missing but their backup exists.");
            return false;
        }
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 52 || stream.Length > 52L + MaximumDiscoveries * 183L)
            throw new InvalidDataException("Invalid dungeon discovery file length.");
        using var reader = new BinaryReader(stream);
        byte[] body = reader.ReadBytes((int)stream.Length - 32), checksum = reader.ReadBytes(32);
        using var sha = SHA256.Create();
        if (!sha.ComputeHash(body).SequenceEqual(checksum)) throw new InvalidDataException("Dungeon discovery checksum mismatch.");
        using var fields = new BinaryReader(new MemoryStream(body, false), new UTF8Encoding(false, true));
        uint magic = fields.ReadUInt32(); int format = fields.ReadInt32();
        if (magic != Magic || (format != 1 && format != 2 && format != 3) || fields.ReadInt64() != _world)
            throw new InvalidDataException("Unsupported dungeon discovery format or world.");
        int count = fields.ReadInt32();
        if (count < 0 || count > MaximumDiscoveries) throw new InvalidDataException("Invalid dungeon discovery count.");
        var loaded = new Dictionary<string, MapMarker>(StringComparer.Ordinal);
        var progress = new Dictionary<string, (int Total, int Picked)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            byte[] identity = fields.ReadBytes(32);
            if (identity.Length != 32) throw new InvalidDataException("Truncated dungeon identity.");
            // A fixed ushort length prevents BinaryReader.ReadString from allocating unbounded input.
            int length = fields.ReadUInt16();
            if (length < 1 || length > 128) throw new InvalidDataException("Invalid dungeon label length.");
            byte[] label = fields.ReadBytes(length);
            if (label.Length != length) throw new InvalidDataException("Truncated dungeon label.");
            var marker = new MapMarker(new UTF8Encoding(false, true).GetString(label), fields.ReadSingle(), fields.ReadSingle(), fields.ReadSingle(),
                BitConverter.ToString(identity).Replace("-", "").ToLowerInvariant());
            if (!Valid(marker) || loaded.ContainsKey(marker.Identity)) throw new InvalidDataException("Invalid or duplicate dungeon discovery.");
            loaded.Add(marker.Identity, marker);
            if (format >= 2)
            {
                int total = fields.ReadInt32(), picked = fields.ReadInt32();
                if (total < 0 || total > 65536 || picked < -1 || picked > total) throw new InvalidDataException("Invalid dungeon pickup progress.");
                if (format == 2 && total == 0) picked = -1; // Earlier formats never established an empty inventory.
                progress[marker.Identity] = (total, picked);
            }
            if (format >= 3)
            {
                byte flag = fields.ReadByte();
                if (flag > 1) throw new InvalidDataException("Invalid dungeon visit state.");
                if (flag == 1) visited.Add(marker.Identity);
            }
        }
        if (fields.BaseStream.Position != fields.BaseStream.Length) throw new InvalidDataException("Trailing dungeon discovery data.");
        _discoveries = loaded; _progress.Clear(); foreach (var pair in progress) _progress.Add(pair.Key, pair.Value);
        _visited.Clear(); foreach (string identity in visited) _visited.Add(identity);
        _revision = _savedRevision = 0; _markers = null; return true;
    }

    public bool SaveIfDue(float now, float interval)
    {
        if (now < _nextSave) return false;
        _nextSave = now + interval; return Save();
    }

    public bool Save()
    {
        if (_revision == _savedRevision) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var body = new MemoryStream();
            using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
            {
                writer.Write(Magic); writer.Write(3); writer.Write(_world); writer.Write(Count);
                foreach (var marker in _discoveries.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
                {
                    for (int i = 0; i < 64; i += 2) writer.Write(byte.Parse(marker.Identity.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    byte[] name = Encoding.UTF8.GetBytes(marker.Name);
                    writer.Write((ushort)name.Length); writer.Write(name); writer.Write(marker.X); writer.Write(marker.Y); writer.Write(marker.Z);
                    var progress = ProgressState(marker.Identity); writer.Write(progress.Total); writer.Write(progress.Picked);
                    writer.Write((byte)(Visited(marker.Identity) ? 1 : 0));
                }
            }
            byte[] bytes = body.ToArray(); using var sha = SHA256.Create();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length); byte[] checksum = sha.ComputeHash(bytes);
                output.Write(checksum, 0, checksum.Length); output.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak"); else File.Move(temporary, FilePath);
            _savedRevision = _revision; return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
