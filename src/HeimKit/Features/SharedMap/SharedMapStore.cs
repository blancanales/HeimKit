using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace HeimKit.Features.SharedMap;

public sealed class SharedMapStore
{
    private const uint Magic = 0x4D534B48; // HKSM
    private readonly long _world;
    private readonly SharedMapExploration _map;
    private int _savedPixels;
    private readonly ScheduleToken _nextSave = new();
    private SnapshotFile? _snapshotFile;
    private SnapshotFile Persistence => _snapshotFile ??= new SnapshotFile(FilePath);
    public string FilePath { get; }

    public SharedMapStore(string directory, long world, SharedMapExploration map)
    {
        _world = world; _map = map;
        FilePath = Path.Combine(directory, "world-" + world.ToString("X16", CultureInfo.InvariantCulture) + ".map");
    }

    public bool Load()
    {
        if (!File.Exists(FilePath))
        {
            // A missing destination with a retained backup requires deliberate recovery, not an empty replacement.
            if (File.Exists(FilePath + ".bak")) throw new InvalidDataException("Community map is missing but its backup exists: " + FilePath);
            return false;
        }
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int packedLength = (_map.Size * _map.Size + 7) / 8;
        if (stream.Length != 28L + packedLength + 32) throw new InvalidDataException("Invalid community-map length: " + FilePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        byte[] body = reader.ReadBytes(28 + packedLength);
        byte[] checksum = reader.ReadBytes(32);
        using var sha = SHA256.Create();
        if (!sha.ComputeHash(body).SequenceEqual(checksum)) throw new InvalidDataException("Community-map checksum mismatch: " + FilePath);
        using var fields = new BinaryReader(new MemoryStream(body, false));
        if (fields.ReadUInt32() != Magic || fields.ReadInt32() != 1 || fields.ReadInt64() != _world ||
            fields.ReadInt32() != _map.Size || fields.ReadSingle() != _map.PixelSize || fields.ReadInt32() != packedLength)
            throw new InvalidDataException("Unsupported community-map format, world or grid: " + FilePath);
        _map.ImportPacked(fields.ReadBytes(packedLength));
        _savedPixels = _map.ExploredPixels;
        return true;
    }

    public bool SaveIfDue(float now, float interval)
    {
        bool saved = Poll();
        if (Persistence.Pending) return saved;
        if (!_nextSave.Ready(now)) return saved;
        _nextSave.Postpone(now, interval);
        if (_savedPixels != _map.ExploredPixels)
        {
            int pixels = _map.ExploredPixels;
            Persistence.Queue(Snapshot(), pixels);
        }
        return saved;
    }

    private bool Poll(bool wait = false)
    {
        if (!Persistence.Poll(wait)) return false;
        _savedPixels = checked((int)Persistence.CompletedGeneration);
        return true;
    }

    public bool Save(bool initialize = false)
    {
        bool saved = Poll(wait: true);
        if (!initialize && _savedPixels == _map.ExploredPixels) return saved;
        int pixels = _map.ExploredPixels;
        Persistence.Write(Snapshot());
        _savedPixels = pixels;
        return true;
    }

    private byte[] Snapshot()
    {
        byte[] packed = _map.ExportPacked();
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
        {
            writer.Write(Magic); writer.Write(1); writer.Write(_world);
            writer.Write(_map.Size); writer.Write(_map.PixelSize); writer.Write(packed.Length); writer.Write(packed);
        }
        return body.ToArray();
    }

}
