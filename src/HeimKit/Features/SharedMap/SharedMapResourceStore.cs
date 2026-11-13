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

public sealed class SharedMapResourceStore
{
    private const uint Magic = 0x44524B48; // HKRD
    public const int MaximumDeposits = 65536;
    private readonly long _world;
    private readonly List<ResourceDeposit> _deposits = new();
    private readonly Dictionary<(int, int), List<ResourceDeposit>> _cells = new();
    private long _revision, _savedRevision;
    private readonly ScheduleToken _nextSave = new();
    public string FilePath { get; }
    public long Revision => _revision;
    public IReadOnlyList<ResourceDeposit> Deposits => _deposits;

    public SharedMapResourceStore(string directory, long world)
    {
        _world = world;
        FilePath = Path.Combine(directory, "world-" + world.ToString("X16", CultureInfo.InvariantCulture) + ".resources");
    }

    private void Add(ResourceDeposit deposit)
    {
        _deposits.Add(deposit);
        var key = ((int)Math.Floor(deposit.X), (int)Math.Floor(deposit.Z));
        if (!_cells.TryGetValue(key, out var cell)) _cells.Add(key, cell = new List<ResourceDeposit>());
        cell.Add(deposit);
    }

    // A half-metre tolerance accommodates native saved positions; ambiguous records are never merged.
    public ResourceDeposit? Find(ResourceKind kind, float x, float y, float z, out bool ambiguous)
    {
        ambiguous = false;
        ResourceDeposit? found = null;
        if (!ValidPosition(x, y, z)) return null;
        int cx = (int)Math.Floor(x), cz = (int)Math.Floor(z);
        for (int a = cx - 1; a <= cx + 1; a++)
            for (int b = cz - 1; b <= cz + 1; b++)
                if (_cells.TryGetValue((a, b), out var cell))
                    foreach (var d in cell)
                    {
                        double dx = (double)d.X - x, dy = (double)d.Y - y, dz = (double)d.Z - z;
                        if (d.Kind != kind || dx * dx + dy * dy + dz * dz > .25) continue;
                        if (found != null) { ambiguous = true; return null; }
                        found = d;
                    }
        return found;
    }

    public ResourceDeposit? Observe(ResourceKind kind, float x, float y, float z, bool? depleted, bool allowNew,
        MiningProgress? progress = null, bool silverDiscovered = false, int? sapPercent = null)
    {
        if (!Enum.IsDefined(typeof(ResourceKind), kind) || !ValidPosition(x, y, z)) return null;
        if (progress.HasValue && (!SharedMapResourceHealth.TracksProgress(kind) || progress.Value.TotalSections < 1 ||
            progress.Value.TotalSections > 8192 || progress.Value.RemainingSections < 0 ||
            progress.Value.RemainingSections > progress.Value.TotalSections ||
            depleted.HasValue && depleted.Value != (progress.Value.RemainingSections == 0)))
            throw new ArgumentException("Invalid observed resource progress.", nameof(progress));
        if (sapPercent.HasValue && (kind != ResourceKind.AncientRoot || sapPercent.Value < 0 || sapPercent.Value > 100))
            throw new ArgumentException("Invalid observed root sap percentage.", nameof(sapPercent));
        var d = Find(kind, x, y, z, out bool ambiguous);
        if (ambiguous || kind == ResourceKind.Silver && !silverDiscovered && d?.SilverDiscovered != true) return null;
        if (d == null)
        {
            // Unknown or already exhausted objects cannot create a new discovery.
            if (!allowNew || depleted != false) return null;
            if (_deposits.Count >= MaximumDeposits) throw new InvalidOperationException("Resource discovery storage limit exceeded; existing discoveries were retained.");
            d = new ResourceDeposit(_deposits.Count + 1, kind, x, y, z); Add(d); _revision++;
        }
        else if (depleted.HasValue && d.Depleted != depleted.Value)
        { d.Depleted = depleted.Value; _revision++; }
        if (kind == ResourceKind.Silver && silverDiscovered && !d.SilverDiscovered)
        { d.SilverDiscovered = true; _revision++; }
        if (progress.HasValue && (!d.Progress.HasValue ||
            d.Progress.Value.TotalSections != progress.Value.TotalSections ||
            d.Progress.Value.RemainingSections != progress.Value.RemainingSections))
        { d.Progress = progress; _revision++; }
        if (sapPercent.HasValue && d.SapPercent != sapPercent)
        { d.SapPercent = sapPercent; _revision++; }
        if (d.Depleted && d.Progress.HasValue && d.Progress.Value.RemainingSections != 0)
        { d.Progress = new MiningProgress(0, d.Progress.Value.TotalSections); _revision++; }
        return d;
    }

    public bool Retire(ResourceKind kind, float x, float y, float z)
    {
        var d = Find(kind, x, y, z, out _);
        if (d == null || d.Depleted) return false;
        d.Depleted = true;
        if (d.Progress.HasValue) d.Progress = new MiningProgress(0, d.Progress.Value.TotalSections);
        _revision++; return true;
    }

    private static bool ValidPosition(float x, float y, float z) =>
        SharedMapExploration.Finite(x) && SharedMapExploration.Finite(y) && SharedMapExploration.Finite(z) &&
        Math.Abs(x) <= 100000 && Math.Abs(y) <= 100000 && Math.Abs(z) <= 100000;

    public bool Load()
    {
        if (!File.Exists(FilePath))
        {
            if (File.Exists(FilePath + ".bak")) throw new InvalidDataException("Resource discoveries are missing but their backup exists: " + FilePath);
            return false;
        }
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 52 || stream.Length > 52L + MaximumDeposits * 34L)
            throw new InvalidDataException("Invalid resource discovery file length.");
        using var reader = new BinaryReader(stream);
        byte[] body = reader.ReadBytes((int)stream.Length - 32), checksum = reader.ReadBytes(32);
        using var sha = SHA256.Create();
        if (!sha.ComputeHash(body).SequenceEqual(checksum)) throw new InvalidDataException("Resource discovery checksum mismatch.");
        using var fields = new BinaryReader(new MemoryStream(body, false));
        uint magic = fields.ReadUInt32(); int format = fields.ReadInt32(); long world = fields.ReadInt64();
        if (magic != Magic || (format < 1 || format > 4) || world != _world)
            throw new InvalidDataException("Unsupported resource discovery format or world.");
        int count = fields.ReadInt32();
        if (count < 0 || count > MaximumDeposits || fields.BaseStream.Length != 20L + count * (format == 1 ? 21L : format == 2 ? 29L : format == 3 ? 30L : 34L))
            throw new InvalidDataException("Invalid resource discovery count or length.");
        var loaded = new List<ResourceDeposit>();
        for (int i = 0; i < count; i++)
        {
            int id = fields.ReadInt32(), kind = fields.ReadInt32();
            float x = fields.ReadSingle(), y = fields.ReadSingle(), z = fields.ReadSingle();
            byte depleted = fields.ReadByte();
            int remaining = format >= 2 ? fields.ReadInt32() : -1;
            int total = format >= 2 ? fields.ReadInt32() : 0;
            byte discovered = format >= 3 ? fields.ReadByte() : (byte)0;
            int sapPercent = format >= 4 ? fields.ReadInt32() : -1;
            if (sapPercent < -1 || sapPercent > 100 || kind != (int)ResourceKind.AncientRoot && sapPercent != -1)
                throw new InvalidDataException("Invalid root sap percentage.");
            if (discovered > 1 || kind != (int)ResourceKind.Silver && discovered != 0)
                throw new InvalidDataException("Invalid Silver discovery evidence.");
            if (id != i + 1 || !Enum.IsDefined(typeof(ResourceKind), kind) || !ValidPosition(x, y, z) || depleted > 1)
                throw new InvalidDataException("Invalid resource discovery record.");
            if ((remaining != -1 || total != 0) && (!SharedMapResourceHealth.TracksProgress((ResourceKind)kind) || total < 1 || total > 8192 ||
                remaining < 0 || remaining > total || (depleted != 0) != (remaining == 0)))
                throw new InvalidDataException("Invalid resource discovery progress.");
            MiningProgress? progress = total > 0 ? new MiningProgress(remaining, total) : null;
            loaded.Add(new ResourceDeposit(id, (ResourceKind)kind, x, y, z, depleted != 0, progress, discovered != 0, sapPercent >= 0 ? sapPercent : (int?)null));
        }
        _deposits.Clear(); _cells.Clear();
        foreach (var d in loaded) Add(d);
        _revision = _savedRevision = 0; return true;
    }

    private SnapshotFile? _snapshotFile;
    private SnapshotFile Persistence => _snapshotFile ??= new SnapshotFile(FilePath);
    public bool SaveIfDue(float now, float interval)
    {
        bool saved = Persistence.Poll();
        if (saved) _savedRevision = Persistence.CompletedGeneration;
        if (Persistence.Pending || !_nextSave.Ready(now)) return saved;
        _nextSave.Postpone(now, interval);
        if (_revision != _savedRevision) Persistence.Queue(Snapshot(), _revision);
        return saved;
    }
    public bool Save()
    {
        bool saved = Persistence.Poll(wait: true);
        if (saved) _savedRevision = Persistence.CompletedGeneration;
        if (_revision == _savedRevision) return saved;
        long generation = _revision; Persistence.Write(Snapshot()); _savedRevision = generation; return true;
    }
    private byte[] Snapshot()
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
        {
            bool roots = _deposits.Any(d => d.Kind == ResourceKind.AncientRoot);
            writer.Write(Magic); writer.Write(roots ? 4 : 3); writer.Write(_world); writer.Write(_deposits.Count);
            foreach (var d in _deposits)
            {
                writer.Write(d.Identity); writer.Write((int)d.Kind); writer.Write(d.X); writer.Write(d.Y); writer.Write(d.Z); writer.Write(d.Depleted);
                writer.Write(d.Progress?.RemainingSections ?? -1); writer.Write(d.Progress?.TotalSections ?? 0); writer.Write(d.SilverDiscovered);
                if (roots) writer.Write(d.SapPercent ?? -1);
            }
        }
        return body.ToArray();
    }
}
