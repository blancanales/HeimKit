using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

// World-scoped publication state. Actual world objects are never moved by the rename workaround.
public sealed class SharedMapMarkerRefresh
{
    private readonly uint _magic;
    private static readonly (float X, float Z)[] Slots = {
        (0, 0), (2, 0), (2, 2), (0, 2), (-2, 2), (-2, 0), (-2, -2), (0, -2), (2, -2)
    };
    private readonly long _world;
    private readonly Action<MapMarker?, MapMarker, MapMarker, int>? _published;
    private readonly Action<string, string>? _removed;
    private Dictionary<string, MarkerPublicationState> _states = new(StringComparer.Ordinal);
    private long _revision, _savedRevision;
    private float _nextSave;
    public string FilePath { get; }
    public int Count => _states.Count;

    public SharedMapMarkerRefresh(string directory, long world,
        Action<MapMarker?, MapMarker, MapMarker, int>? published = null,
        Action<string, string>? removed = null, bool resourcePins = false)
    {
        _world = world; _published = published; _removed = removed;
        _magic = resourcePins ? 0x52524B48u : 0x52504B48u;
        FilePath = Path.Combine(directory, "world-" + world.ToString("X16", CultureInfo.InvariantCulture) + (resourcePins ? ".resource-pins" : ".portal-pins"));
    }

    public IReadOnlyList<MapMarker> Apply(IReadOnlyList<MapMarker> markers)
    {
        if (markers.Count > 4096) throw new InvalidDataException("Too many marker publications.");
        var next = new Dictionary<string, MarkerPublicationState>(StringComparer.Ordinal);
        var output = new List<MapMarker>();
        bool changed = _states.Count != markers.Count;
        var migrated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var marker in markers)
        {
            if (string.IsNullOrEmpty(marker.Identity) || Encoding.UTF8.GetByteCount(marker.Identity) > 256 ||
                Encoding.UTF8.GetByteCount(marker.Name) > 4096 || !SharedMapExploration.Finite(marker.X) ||
                !SharedMapExploration.Finite(marker.Y) || !SharedMapExploration.Finite(marker.Z) || next.ContainsKey(marker.Identity))
                throw new InvalidDataException("Invalid marker publication identity, label or position.");
            _states.TryGetValue(marker.Identity, out var previous);
            if (previous == null && _magic == 0x52504B48u && marker.Identity.StartsWith("portal:", StringComparison.Ordinal))
            {
                // Recover a legacy publication by its actual location, never by its reassigned native ID.
                var matches = _states.Where(p => !migrated.Contains(p.Key) && LegacyPortalKey(p.Key) &&
                    DistanceSquared(marker.X, marker.Z, p.Value.X - Slots[p.Value.Slot].X, p.Value.Z - Slots[p.Value.Slot].Z) <= .25)
                    .Take(2).ToArray();
                if (matches.Length == 1) { previous = matches[0].Value; migrated.Add(matches[0].Key); changed = true; }
            }
            int slot = previous?.Slot ?? 0;
            if (previous != null && previous.Name != marker.Name)
            {
                for (int attempt = 0; attempt < Slots.Length; attempt++)
                {
                    slot = (slot + 1) % Slots.Length;
                    double dx = marker.X + Slots[slot].X - previous.X, dz = marker.Z + Slots[slot].Z - previous.Z;
                    if (dx * dx + dz * dz > 1.21) break;
                    if (attempt == Slots.Length - 1) throw new InvalidDataException("No separated marker publication position available.");
                }
            }
            float x = marker.X + Slots[slot].X, z = marker.Z + Slots[slot].Z;
            if (!SharedMapExploration.Finite(x) || !SharedMapExploration.Finite(z)) throw new InvalidDataException("Invalid published marker position.");
            next.Add(marker.Identity, new MarkerPublicationState(marker.Name, slot, x, z));
            output.Add(new MapMarker(marker.Name, x, marker.Y, z, marker.Identity));
            changed |= previous == null || previous.Name != marker.Name || previous.Slot != slot || previous.X != x || previous.Z != z;
        }
        if (!changed) return output;
        var prior = _states;
        _states = next; _revision++;
        if (_removed != null)
            foreach (var item in prior)
                if (!next.ContainsKey(item.Key)) _removed(item.Key, item.Value.Name);
        if (_published != null)
            for (int i = 0; i < markers.Count; i++)
            {
                var actual = markers[i]; var publication = output[i];
                prior.TryGetValue(actual.Identity, out var old);
                if (old != null && old.Name == actual.Name && old.X == publication.X && old.Z == publication.Z) continue;
                _published(old == null ? null : new MapMarker(old.Name, old.X, actual.Y, old.Z, actual.Identity),
                    actual, publication, next[actual.Identity].Slot);
            }
        return output;
    }

    private static double DistanceSquared(float x, float z, float otherX, float otherZ)
    { double dx = (double)x - otherX, dz = (double)z - otherZ; return dx * dx + dz * dz; }
    private static bool LegacyPortalKey(string key)
    {
        int separator = key.IndexOf(':');
        return separator > 0 && long.TryParse(key.Substring(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
            uint.TryParse(key.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    // Move a matching publication away from a stale shared pin that vanilla would otherwise retain.
    public bool AvoidObsoletePin(string name, float x, float z)
    {
        bool changed = false;
        foreach (var id in _states.Keys.ToArray())
        {
            var old = _states[id];
            double dx = (double)old.X - x, dz = (double)old.Z - z;
            if (!string.Equals(old.Name, name, StringComparison.OrdinalIgnoreCase) || dx * dx + dz * dz >= 1) continue;
            float actualX = old.X - Slots[old.Slot].X, actualZ = old.Z - Slots[old.Slot].Z;
            for (int attempt = 1; attempt < Slots.Length; attempt++)
            {
                int slot = (old.Slot + attempt) % Slots.Length;
                float nextX = actualX + Slots[slot].X, nextZ = actualZ + Slots[slot].Z;
                dx = (double)nextX - x; dz = (double)nextZ - z;
                if (dx * dx + dz * dz <= 1.21 || dx * dx + dz * dz > 9) continue;
                _states[id] = new MarkerPublicationState(old.Name, slot, nextX, nextZ);
                _revision++; changed = true;
                _published?.Invoke(new MapMarker(old.Name, old.X, 0, old.Z, id),
                    new MapMarker(old.Name, actualX, 0, actualZ, id),
                    new MapMarker(old.Name, nextX, 0, nextZ, id), slot);
                break;
            }
        }
        return changed;
    }

    public bool Load()
    {
        if (!File.Exists(FilePath))
        {
            if (File.Exists(FilePath + ".bak")) throw new InvalidDataException("Marker refresh state is missing but its backup exists: " + FilePath);
            return false;
        }
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 52 || stream.Length > 20 * 1024 * 1024) throw new InvalidDataException("Invalid marker refresh state length.");
        using var reader = new BinaryReader(stream);
        byte[] body = reader.ReadBytes((int)stream.Length - 32), checksum = reader.ReadBytes(32);
        using var sha = SHA256.Create();
        if (!sha.ComputeHash(body).SequenceEqual(checksum)) throw new InvalidDataException("Marker refresh state checksum mismatch.");
        using var fields = new BinaryReader(new MemoryStream(body, false), new UTF8Encoding(false, true));
        if (fields.ReadUInt32() != _magic || fields.ReadInt32() != 1 || fields.ReadInt64() != _world)
            throw new InvalidDataException("Unsupported marker refresh state format or world.");
        int count = fields.ReadInt32();
        if (count < 0 || count > 4096) throw new InvalidDataException("Invalid marker refresh state count.");
        var loaded = new Dictionary<string, MarkerPublicationState>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = ReadString(fields, 256), name = ReadString(fields, 4096);
            int slot = fields.ReadInt32(); float x = fields.ReadSingle(), z = fields.ReadSingle();
            if (key.Length == 0 || loaded.ContainsKey(key) || slot < 0 || slot >= Slots.Length ||
                !SharedMapExploration.Finite(x) || !SharedMapExploration.Finite(z)) throw new InvalidDataException("Invalid marker refresh record.");
            loaded.Add(key, new MarkerPublicationState(name, slot, x, z));
        }
        if (fields.BaseStream.Position != fields.BaseStream.Length) throw new InvalidDataException("Trailing marker refresh data.");
        _states = loaded; _revision = _savedRevision = 0;
        return true;
    }

    private static string ReadString(BinaryReader reader, int maximum)
    {
        uint length = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte part = reader.ReadByte();
            if (shift == 28 && part > 7) throw new InvalidDataException("Invalid marker refresh string length.");
            length |= (uint)(part & 127) << shift;
            if ((part & 128) != 0) continue;
            if (length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Oversized or truncated marker refresh string.");
            return new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)length));
        }
        throw new InvalidDataException("Invalid marker refresh string length.");
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
            using (var writer = new BinaryWriter(body, new UTF8Encoding(false, true), true))
            {
                writer.Write(_magic); writer.Write(1); writer.Write(_world); writer.Write(_states.Count);
                foreach (var pair in _states.OrderBy(p => p.Key, StringComparer.Ordinal))
                { writer.Write(pair.Key); writer.Write(pair.Value.Name); writer.Write(pair.Value.Slot); writer.Write(pair.Value.X); writer.Write(pair.Value.Z); }
            }
            byte[] bytes = body.ToArray();
            using var sha = SHA256.Create(); byte[] checksum = sha.ComputeHash(bytes);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes, 0, bytes.Length); output.Write(checksum, 0, checksum.Length); output.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
            _savedRevision = _revision; return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
