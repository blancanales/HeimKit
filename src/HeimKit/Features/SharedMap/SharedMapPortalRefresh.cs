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
public sealed class SharedMapPortalRefresh
{
    private const uint Magic = 0x52504B48; // HKPR
    private static readonly (float X, float Z)[] Slots = {
        (0, 0), (2, 0), (2, 2), (0, 2), (-2, 2), (-2, 0), (-2, -2), (0, -2), (2, -2)
    };
    private readonly long _world;
    private readonly Action<PortalMarker?, PortalMarker, PortalMarker, int>? _published;
    private readonly Action<string, string>? _removed;
    private Dictionary<string, PortalRefreshState> _states = new(StringComparer.Ordinal);
    private long _revision, _savedRevision;
    private float _nextSave;
    public string FilePath { get; }
    public int Count => _states.Count;

    public SharedMapPortalRefresh(string directory, long world,
        Action<PortalMarker?, PortalMarker, PortalMarker, int>? published = null,
        Action<string, string>? removed = null)
    {
        _world = world; _published = published; _removed = removed;
        FilePath = Path.Combine(directory, "world-" + world.ToString("X16", CultureInfo.InvariantCulture) + ".portal-pins");
    }

    public IReadOnlyList<PortalMarker> Apply(IReadOnlyList<PortalMarker> markers)
    {
        if (markers.Count > 4096) throw new InvalidDataException("Too many portal publications.");
        var next = new Dictionary<string, PortalRefreshState>(StringComparer.Ordinal);
        var output = new List<PortalMarker>();
        bool changed = _states.Count != markers.Count;
        foreach (var marker in markers)
        {
            if (string.IsNullOrEmpty(marker.Identity) || Encoding.UTF8.GetByteCount(marker.Identity) > 256 ||
                Encoding.UTF8.GetByteCount(marker.Name) > 4096 || !SharedMapExploration.Finite(marker.X) ||
                !SharedMapExploration.Finite(marker.Y) || !SharedMapExploration.Finite(marker.Z) || next.ContainsKey(marker.Identity))
                throw new InvalidDataException("Invalid portal publication identity, label or position.");
            _states.TryGetValue(marker.Identity, out var previous);
            int slot = previous?.Slot ?? 0;
            if (previous != null && previous.Name != marker.Name)
            {
                for (int attempt = 0; attempt < Slots.Length; attempt++)
                {
                    slot = (slot + 1) % Slots.Length;
                    double dx = marker.X + Slots[slot].X - previous.X, dz = marker.Z + Slots[slot].Z - previous.Z;
                    if (dx * dx + dz * dz > 1.21) break;
                    if (attempt == Slots.Length - 1) throw new InvalidDataException("No separated portal publication position available.");
                }
            }
            float x = marker.X + Slots[slot].X, z = marker.Z + Slots[slot].Z;
            if (!SharedMapExploration.Finite(x) || !SharedMapExploration.Finite(z)) throw new InvalidDataException("Invalid published portal position.");
            next.Add(marker.Identity, new PortalRefreshState(marker.Name, slot, x, z));
            output.Add(new PortalMarker(marker.Name, x, marker.Y, z, marker.Identity));
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
                _published(old == null ? null : new PortalMarker(old.Name, old.X, actual.Y, old.Z, actual.Identity),
                    actual, publication, next[actual.Identity].Slot);
            }
        return output;
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
                _states[id] = new PortalRefreshState(old.Name, slot, nextX, nextZ);
                _revision++; changed = true;
                _published?.Invoke(new PortalMarker(old.Name, old.X, 0, old.Z, id),
                    new PortalMarker(old.Name, actualX, 0, actualZ, id),
                    new PortalMarker(old.Name, nextX, 0, nextZ, id), slot);
                break;
            }
        }
        return changed;
    }

    public bool Load()
    {
        if (!File.Exists(FilePath))
        {
            if (File.Exists(FilePath + ".bak")) throw new InvalidDataException("Portal refresh state is missing but its backup exists: " + FilePath);
            return false;
        }
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 52 || stream.Length > 20 * 1024 * 1024) throw new InvalidDataException("Invalid portal refresh state length.");
        using var reader = new BinaryReader(stream);
        byte[] body = reader.ReadBytes((int)stream.Length - 32), checksum = reader.ReadBytes(32);
        using var sha = SHA256.Create();
        if (!sha.ComputeHash(body).SequenceEqual(checksum)) throw new InvalidDataException("Portal refresh state checksum mismatch.");
        using var fields = new BinaryReader(new MemoryStream(body, false), new UTF8Encoding(false, true));
        if (fields.ReadUInt32() != Magic || fields.ReadInt32() != 1 || fields.ReadInt64() != _world)
            throw new InvalidDataException("Unsupported portal refresh state format or world.");
        int count = fields.ReadInt32();
        if (count < 0 || count > 4096) throw new InvalidDataException("Invalid portal refresh state count.");
        var loaded = new Dictionary<string, PortalRefreshState>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = ReadString(fields, 256), name = ReadString(fields, 4096);
            int slot = fields.ReadInt32(); float x = fields.ReadSingle(), z = fields.ReadSingle();
            if (key.Length == 0 || loaded.ContainsKey(key) || slot < 0 || slot >= Slots.Length ||
                !SharedMapExploration.Finite(x) || !SharedMapExploration.Finite(z)) throw new InvalidDataException("Invalid portal refresh record.");
            loaded.Add(key, new PortalRefreshState(name, slot, x, z));
        }
        if (fields.BaseStream.Position != fields.BaseStream.Length) throw new InvalidDataException("Trailing portal refresh data.");
        _states = loaded; _revision = _savedRevision = 0;
        return true;
    }

    private static string ReadString(BinaryReader reader, int maximum)
    {
        uint length = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            byte part = reader.ReadByte();
            if (shift == 28 && part > 7) throw new InvalidDataException("Invalid portal refresh string length.");
            length |= (uint)(part & 127) << shift;
            if ((part & 128) != 0) continue;
            if (length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Oversized or truncated portal refresh string.");
            return new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)length));
        }
        throw new InvalidDataException("Invalid portal refresh string length.");
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
                writer.Write(Magic); writer.Write(1); writer.Write(_world); writer.Write(_states.Count);
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
