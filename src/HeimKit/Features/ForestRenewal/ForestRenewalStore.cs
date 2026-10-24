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
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HeimKit.Features.ForestRenewal.Models;
using UnityEngine;

namespace HeimKit.Features.ForestRenewal;
internal sealed class ForestRenewalStore
{
    public const int Capacity = 16384;
    public readonly Dictionary<ZDOID, RenewalRecord> Records = new();
    private readonly string _path;
    private readonly long _world;
    private readonly object _writeGate = new();
    public ForestRenewalStore(string directory, long world)
    {
        _world = world; _path = Path.Combine(directory, "world-" + world.ToString("X16") + ".renewals");
        if (!File.Exists(_path))
        {
            if (File.Exists(_path + ".bak")) throw new InvalidDataException("Renewal ledger is missing while its backup exists.");
            return;
        }
        long length = new FileInfo(_path).Length;
        if (length < 52 || length > 1024 * 1024) throw new InvalidDataException("Invalid renewal ledger length.");
        byte[] data = File.ReadAllBytes(_path);
        if (data.Length < 52 || data.Length > 1024 * 1024) throw new InvalidDataException("Invalid renewal ledger length.");
        using var sha = SHA256.Create();
        byte[] body = data.Take(data.Length - 32).ToArray();
        if (!sha.ComputeHash(body).SequenceEqual(data.Skip(body.Length))) throw new InvalidDataException("Renewal ledger checksum mismatch.");
        using var reader = new BinaryReader(new MemoryStream(body));
        if (reader.ReadUInt32() != 0x52464B48) throw new InvalidDataException("Renewal ledger signature mismatch.");
        int format = reader.ReadInt32();
        if ((format != 1 && format != 2) || reader.ReadInt64() != world) throw new InvalidDataException("Renewal ledger world/format mismatch.");
        int count = reader.ReadInt32();
        if (count < 0 || count > Capacity) throw new InvalidDataException("Renewal ledger capacity exceeded.");
        for (int i = 0; i < count; i++)
        {
            var record = new RenewalRecord { Source = new ZDOID(reader.ReadInt64(), reader.ReadUInt32()),
                Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), Sapling = reader.ReadInt32(),
                DueTicks = reader.ReadInt64(), DecisionTicks = reader.ReadInt64(), State = reader.ReadByte() };
            if (record.Source.IsNone() || !PlayerWorldScope.Finite(record.Position) || record.State > (format == 1 ? 2 : 3) || record.DueTicks < 0 || record.DecisionTicks < 0 || Records.ContainsKey(record.Source)) throw new InvalidDataException("Invalid renewal record.");
            Records.Add(record.Source, record);
        }
        if (reader.BaseStream.Position != body.Length) throw new InvalidDataException("Trailing renewal ledger data.");
    }
    public void Save()
    {
        WriteSnapshot(Snapshot());
    }
    // Called on the main thread: no live record or Unity object is passed to the writer.
    public Task<double> SaveAsync()
    {
        byte[] snapshot = Snapshot();
        return Task.Run(() =>
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            WriteSnapshot(snapshot);
            return timer.Elapsed.TotalMilliseconds;
        });
    }
    private byte[] Snapshot()
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write(0x52464B48u); writer.Write(2); writer.Write(_world); writer.Write(Records.Count);
            foreach (var record in Records.Values)
            {
                writer.Write(record.Source.UserID); writer.Write(record.Source.ID);
                writer.Write(record.Position.x); writer.Write(record.Position.y); writer.Write(record.Position.z);
                writer.Write(record.Sapling); writer.Write(record.DueTicks); writer.Write(record.DecisionTicks); writer.Write(record.State);
            }
        }
        return buffer.ToArray();
    }
    private void WriteSnapshot(byte[] body)
    {
        lock (_writeGate)
        {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var sha = SHA256.Create(); byte[] checksum = sha.ComputeHash(body);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(body, 0, body.Length); stream.Write(checksum, 0, checksum.Length); stream.Flush(true); }
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak"); else File.Move(temporary, _path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
