using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HeimKit.Features.SharedMap.Models;

namespace HeimKit.Features.SharedMap;

public sealed class SharedMapPinRemovals
{
    private const uint Magic = 0x4D524B48; // HKRM
    private const int Maximum = 8192;
    private readonly long _world;
    private HashSet<string> _ids = new(StringComparer.Ordinal);
    public string FilePath { get; }
    public SharedMapPinRemovals(string directory, long world)
    { _world = world; FilePath = Path.Combine(directory, "world-" + world.ToString("X16", CultureInfo.InvariantCulture) + ".pin-removals"); }

    public static string Identity(MapPin pin)
    {
        if (Encoding.UTF8.GetByteCount(pin.Name) > 4096 || !SharedMapExploration.Finite(pin.X) ||
            !SharedMapExploration.Finite(pin.Y) || !SharedMapExploration.Finite(pin.Z) || pin.Icon < 0 || pin.Icon > 16)
            throw new InvalidDataException("Invalid selected table pin.");
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
        {
            writer.Write(pin.Owner); writer.Write(pin.Name);
            // Canonicalize signed zero; all other finite native floats retain their exact identity.
            writer.Write(pin.X == 0 ? 0f : pin.X); writer.Write(pin.Y == 0 ? 0f : pin.Y); writer.Write(pin.Z == 0 ? 0f : pin.Z);
            writer.Write(pin.Icon);
        }
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(body.ToArray())).Replace("-", "").ToLowerInvariant();
    }
    public static bool ValidId(string id) => id.Length == 64 && id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    public bool Contains(MapPin pin) => _ids.Count > 0 && _ids.Contains(Identity(pin));
    public bool Contains(string id) => _ids.Contains(id);

    public void Load()
    {
        if (!File.Exists(FilePath))
        {
            if (File.Exists(FilePath + ".bak")) throw new InvalidDataException("Pin-removal rules are missing but a backup exists.");
            return;
        }
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 52 || stream.Length > 52L + Maximum * 32L) throw new InvalidDataException("Invalid pin-removal file length.");
        using var reader = new BinaryReader(stream);
        byte[] body = reader.ReadBytes((int)stream.Length - 32), checksum = reader.ReadBytes(32);
        using var sha = SHA256.Create();
        if (!sha.ComputeHash(body).SequenceEqual(checksum)) throw new InvalidDataException("Pin-removal checksum mismatch.");
        using var fields = new BinaryReader(new MemoryStream(body, false));
        if (fields.ReadUInt32() != Magic || fields.ReadInt32() != 1 || fields.ReadInt64() != _world)
            throw new InvalidDataException("Unsupported pin-removal format or world.");
        int count = fields.ReadInt32();
        if (count < 0 || count > Maximum || fields.BaseStream.Length != 20L + count * 32L) throw new InvalidDataException("Invalid pin-removal count.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
            if (!ids.Add(BitConverter.ToString(fields.ReadBytes(32)).Replace("-", "").ToLowerInvariant())) throw new InvalidDataException("Duplicate pin-removal identity.");
        _ids = ids;
    }

    public bool Set(string id, bool removed)
    {
        if (!ValidId(id)) throw new ArgumentException("Use the full lowercase pin ID reported by find.", nameof(id));
        if (_ids.Contains(id) == removed) return false;
        var next = new HashSet<string>(_ids, StringComparer.Ordinal);
        if (removed) next.Add(id); else next.Remove(id);
        if (next.Count > Maximum) throw new InvalidOperationException("Pin-removal rule limit exceeded.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var body = new MemoryStream();
            using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
            {
                writer.Write(Magic); writer.Write(1); writer.Write(_world); writer.Write(next.Count);
                foreach (string value in next.OrderBy(v => v, StringComparer.Ordinal))
                    for (int i = 0; i < 64; i += 2) writer.Write(byte.Parse(value.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            byte[] bytes = body.ToArray(); using var sha = SHA256.Create();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, bytes.Length); byte[] checksum = sha.ComputeHash(bytes);
                output.Write(checksum, 0, checksum.Length); output.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak"); else File.Move(temporary, FilePath);
            _ids = next; return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
