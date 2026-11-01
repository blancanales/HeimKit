using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HeimKit.Core.World;

// Filesystem-only capture. Native world selection and startup interception live in UpgradeStartup.
internal static class StartupWorldBackup
{
    internal const int MaximumFiles = 100000;
    internal readonly struct Source
    {
        internal readonly string Path, Entry;
        internal readonly bool Directory, Optional;
        internal Source(string path, string entry, bool directory, bool optional = false)
        { Path = System.IO.Path.GetFullPath(path); Entry = entry; Directory = directory; Optional = optional; }
    }
    private sealed class Record
    {
        internal string Path = "", Entry = "";
        internal long Length, Modified;
        internal bool Directory;
    }

    internal static string Create(string destination, IReadOnlyList<Source> sources, string metadata, string? archiveName = null, Action<string>? beforePublish = null)
    {
        destination = Path.GetFullPath(destination);
        ValidatePath(destination);
        foreach (var source in sources)
        {
            ValidatePath(source.Path);
            if (Within(source.Path, destination) || source.Directory && Within(destination, source.Path))
                throw new IOException("Backup destination overlaps a source path.");
        }
        var records = Inventory(sources);
        if (!records.Exists(record => !record.Directory)) throw new IOException("No world backup files were available.");
        Directory.CreateDirectory(destination);
        archiveName ??= DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".zip";
        if (Path.GetFileName(archiveName) != archiveName || archiveName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !archiveName.EndsWith(".zip", StringComparison.Ordinal))
            throw new IOException("Invalid world backup archive filename.");
        string completed = Path.Combine(destination, archiveName);
        if (File.Exists(completed)) throw new IOException("A completed world backup must not be overwritten.");
        string partial = completed + ".partial";
        var streams = new List<FileStream>();
        var files = new List<Record>();
        bool success = false;
        try
        {
            // Hold all source files against writes/deletion across archive creation and verification.
            foreach (var record in records)
            {
                if (record.Directory) continue;
                var stream = new FileStream(record.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream); files.Add(record);
                if (stream.Length != record.Length || File.GetLastWriteTimeUtc(record.Path).Ticks != record.Modified)
                    throw new IOException("A backup source changed before it could be locked.");
            }
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var record in records)
                        if (record.Directory) archive.CreateEntry(record.Entry + "/");
                    for (int i = 0; i < streams.Count; i++)
                    {
                        var entry = archive.CreateEntry(files[i].Entry, CompressionLevel.Fastest);
                        using var target = entry.Open();
                        streams[i].CopyTo(target);
                    }
                    using var info = new StreamWriter(archive.CreateEntry("backup-info.txt").Open(), new UTF8Encoding(false));
                    info.Write(metadata);
                }
                output.Flush(flushToDisk: true);
                output.Position = 0;
                using var verify = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
                if (verify.Entries.Count != records.Count + 1) throw new InvalidDataException("The archive inventory is incomplete.");
                using (var info = new StreamReader((verify.GetEntry("backup-info.txt") ?? throw new InvalidDataException("Backup identity is missing.")).Open()))
                    if (info.ReadToEnd() != metadata) throw new InvalidDataException("Backup identity verification failed.");
                using var hash = SHA256.Create();
                for (int i = 0; i < streams.Count; i++)
                {
                    var entry = verify.GetEntry(files[i].Entry) ?? throw new InvalidDataException("An archive entry is missing.");
                    if (entry.Length != streams[i].Length) throw new InvalidDataException("An archive entry has the wrong length.");
                    streams[i].Position = 0;
                    byte[] expected = hash.ComputeHash(streams[i]);
                    using var contents = entry.Open();
                    byte[] actual = hash.ComputeHash(contents);
                    for (int j = 0; j < expected.Length; j++)
                        if (expected[j] != actual[j]) throw new InvalidDataException("Archive verification failed.");
                }
            }
            var after = Inventory(sources);
            if (after.Count != records.Count) throw new IOException("The backup source inventory changed during capture.");
            for (int i = 0; i < after.Count; i++)
                if (after[i].Entry != records[i].Entry || after[i].Length != records[i].Length || after[i].Modified != records[i].Modified)
                    throw new IOException("A backup source changed during capture.");
            beforePublish?.Invoke(partial);
            File.Move(partial, completed);
            success = true;
            return completed;
        }
        finally
        {
            foreach (var stream in streams) stream.Dispose();
            if (!success)
                try { if (File.Exists(partial)) File.Delete(partial); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
        }
    }

    private static List<Record> Inventory(IReadOnlyList<Source> sources)
    {
        var result = new List<Record>();
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "backup-info.txt" };
        foreach (var source in sources)
        {
            if (!File.Exists(source.Path) && !Directory.Exists(source.Path))
            {
                if (source.Optional) continue;
                throw new FileNotFoundException("A required world backup source is missing.", source.Path);
            }
            if (source.Directory != Directory.Exists(source.Path)) throw new IOException("Unexpected backup source type.");
            var pending = new Stack<string>(); pending.Push(source.Path);
            while (pending.Count != 0)
            {
                string path = pending.Pop(); ValidatePath(path);
                string relative = path == source.Path ? "" : Path.GetRelativePath(source.Path, path).Replace('\\', '/');
                string entry = source.Entry + (relative.Length == 0 ? "" : "/" + relative);
                if (entry.StartsWith("/") || entry.IndexOf(':') >= 0 || entry.IndexOf('\\') >= 0)
                    throw new IOException("Invalid backup archive path.");
                foreach (string part in entry.Split('/'))
                    if (part.Length == 0 || part == "." || part == "..") throw new IOException("Invalid backup archive path.");
                if (!entries.Add(entry)) throw new IOException("Duplicate backup archive entry.");
                bool directory = Directory.Exists(path);
                var record = new Record { Path = path, Entry = entry, Directory = directory };
                if (!directory)
                {
                    var info = new FileInfo(path); record.Length = info.Length; record.Modified = info.LastWriteTimeUtc.Ticks;
                }
                result.Add(record);
                if (result.Count > MaximumFiles) throw new IOException("World backup file-count limit exceeded.");
                if (directory)
                    foreach (string child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
            }
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Entry, b.Entry));
        return result;
    }

    private static bool Within(string path, string root) =>
        string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void ValidatePath(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not permitted in startup backup paths.");
    }
}
