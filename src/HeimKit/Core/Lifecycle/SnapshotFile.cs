using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace HeimKit.Core.Lifecycle;

// One immutable snapshot and one writer per file. Callers serialize native state
// on the game thread and acknowledge only the completed snapshot's generation.
internal sealed class SnapshotFile
{
    private readonly string _path;
    private readonly Action<byte[]> _write;
    private Task? _pending;
    private long _writing;
    internal long CompletedGeneration { get; private set; }
    internal bool Pending => _pending != null;
    internal SnapshotFile(string path, Action<byte[]>? write = null) { _path = path; _write = write ?? WriteBytes; }
    internal void Queue(byte[] snapshot, long generation)
    {
        if (_pending != null) throw new InvalidOperationException("A snapshot write is already pending.");
        _writing = generation; _pending = Task.Run(() => _write(snapshot));
    }
    internal bool Poll(bool wait = false)
    {
        if (_pending == null || !wait && !_pending.IsCompleted) return false;
        _pending.GetAwaiter().GetResult();
        _pending = null; CompletedGeneration = _writing; return true;
    }
    internal void Write(byte[] bytes)
    {
        if (_pending != null) throw new InvalidOperationException("A synchronous snapshot cannot overlap a pending write.");
        _write(bytes);
    }
    private void WriteBytes(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var sha = SHA256.Create(); byte[] checksum = sha.ComputeHash(bytes);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Write(checksum, 0, checksum.Length); file.Flush(true); }
            if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak"); else File.Move(temporary, _path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
