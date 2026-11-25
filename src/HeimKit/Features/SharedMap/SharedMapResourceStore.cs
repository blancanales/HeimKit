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
    private readonly List<ResourceDeposit> _pillars = new();
    internal IReadOnlyList<ResourceDeposit> Pillars => _pillars;
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
        if (deposit.Kind == ResourceKind.FlametalPillar) { _pillars.Add(deposit); return; }
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
        if (kind == ResourceKind.FlametalPillar || !Enum.IsDefined(typeof(ResourceKind), kind) || !ValidPosition(x, y, z)) return null;
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

    internal ResourceDeposit AddPillar(float x, float y, float z)
    {
        if (!ValidPosition(x, y, z) || _deposits.Count >= MaximumDeposits)
            throw new InvalidOperationException("Invalid or excess Flametal pillar discovery.");
        var deposit = new ResourceDeposit(_deposits.Count + 1, ResourceKind.FlametalPillar, x, y, z);
        Add(deposit); _revision++;
        return deposit;
    }

    internal void RetirePillar(ResourceDeposit deposit)
    {
        if (deposit.Depleted) return;
        deposit.Depleted = true;
        _revision++;
    }

    internal void RecordSkull(ResourceDeposit deposit, SkullBounds bounds)
    {
        deposit.Skull = bounds;
        _revision++;
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
        if (stream.Length < 52 || stream.Length > 52L + MaximumDeposits * 59L)
            throw new InvalidDataException("Invalid resource discovery file length.");
        using var reader = new BinaryReader(stream);
        byte[] body = reader.ReadBytes((int)stream.Length - 32), checksum = reader.ReadBytes(32);
        using var sha = SHA256.Create();
        if (!sha.ComputeHash(body).SequenceEqual(checksum)) throw new InvalidDataException("Resource discovery checksum mismatch.");
        using var fields = new BinaryReader(new MemoryStream(body, false));
        uint magic = fields.ReadUInt32(); int format = fields.ReadInt32(); long world = fields.ReadInt64();
        if (magic != Magic || (format < 1 || format > 6) || world != _world)
            throw new InvalidDataException("Unsupported resource discovery format or world.");
        int count = fields.ReadInt32();
        if (count < 0 || count > MaximumDeposits || fields.BaseStream.Length != 20L + count * (format == 1 ? 21L : format == 2 ? 29L : format == 3 ? 30L : format == 4 ? 34L : 59L))
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
            if (format < 6 && kind == (int)ResourceKind.FlametalPillar || id != i + 1 || !Enum.IsDefined(typeof(ResourceKind), kind) || !ValidPosition(x, y, z) || depleted > 1)
                throw new InvalidDataException("Invalid resource discovery record.");
            if ((remaining != -1 || total != 0) && (!SharedMapResourceHealth.TracksProgress((ResourceKind)kind) || total < 1 || total > 8192 ||
                remaining < 0 || remaining > total || (depleted != 0) != (remaining == 0)))
                throw new InvalidDataException("Invalid resource discovery progress.");
            MiningProgress? progress = total > 0 ? new MiningProgress(remaining, total) : null;
            var deposit = new ResourceDeposit(id, (ResourceKind)kind, x, y, z, depleted != 0, progress, discovered != 0, sapPercent >= 0 ? sapPercent : (int?)null);
            if (format >= 5)
            {
                byte skull = fields.ReadByte();
                var bounds = new SkullBounds(fields.ReadSingle(), fields.ReadSingle(), fields.ReadSingle(),
                    fields.ReadSingle(), fields.ReadSingle(), fields.ReadSingle());
                if (skull > 1 || skull == 1 && (deposit.Kind != ResourceKind.BlackMarble || !bounds.Valid))
                    throw new InvalidDataException("Invalid skull placement.");
                if (skull == 1) deposit.Skull = bounds;
            }
            loaded.Add(deposit);
        }
        _deposits.Clear(); _cells.Clear(); _pillars.Clear();
        foreach (var d in loaded) Add(d);
        _revision = _savedRevision = 0; return true;
    }

    private SnapshotFile? _snapshotFile;
    private SnapshotFile Persistence => _snapshotFile ??= new SnapshotFile(FilePath);
    internal bool Clean => _revision == _savedRevision && _snapshotFile?.Pending != true;
    internal SnapshotPreparation? Preparation { get; set; }
    public bool SaveIfDue(float now, float interval)
    {
        bool saved = Persistence.Poll();
        if (saved) _savedRevision = Persistence.CompletedGeneration;
        if (Persistence.Pending) return saved;
        bool due = Preparation?.Ready(_nextSave, now, interval, _revision != _savedRevision) ?? (interval <= 0 || _nextSave.Ready(now));
        if (!due) return saved;
        _nextSave.Postpone(now, interval);
        if (_revision != _savedRevision) SnapshotPreparation.Queue(Preparation, now, Persistence, Snapshot, _revision);
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
            bool pillars = _pillars.Count != 0;
            bool tissue = pillars || _deposits.Any(d => d.Skull.HasValue || d.Kind == ResourceKind.SoftTissue);
            writer.Write(Magic); writer.Write(pillars ? 6 : tissue ? 5 : roots ? 4 : 3); writer.Write(_world); writer.Write(_deposits.Count);
            foreach (var d in _deposits)
            {
                writer.Write(d.Identity); writer.Write((int)d.Kind); writer.Write(d.X); writer.Write(d.Y); writer.Write(d.Z); writer.Write(d.Depleted);
                writer.Write(d.Progress?.RemainingSections ?? -1); writer.Write(d.Progress?.TotalSections ?? 0); writer.Write(d.SilverDiscovered);
                if (roots || tissue) writer.Write(d.SapPercent ?? -1);
                if (tissue)
                {
                    writer.Write(d.Skull.HasValue);
                    var bounds = d.Skull.GetValueOrDefault();
                    writer.Write(bounds.MinX); writer.Write(bounds.MinY); writer.Write(bounds.MinZ);
                    writer.Write(bounds.MaxX); writer.Write(bounds.MaxY); writer.Write(bounds.MaxZ);
                }
            }
        }
        return body.ToArray();
    }
}
