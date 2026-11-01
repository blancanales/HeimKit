using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HeimKit.Core.World;

// One durable world checkpoint for a HeimKit/Valheim version pair, not one per process start.
internal static class WorldUpgradeBackup
{
    private const int MaximumRecordCharacters = 1048576;
    internal sealed class Identity
    {
        internal readonly string WorldUid, SaveDirectory, HeimKitVersion, ValheimVersion, AuditedVersion;
        internal Identity(long worldUid, string saveDirectory, string heimKitVersion, string valheimVersion, string auditedVersion)
        {
            WorldUid = worldUid.ToString("X16", CultureInfo.InvariantCulture);
            SaveDirectory = Path.GetFullPath(saveDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            HeimKitVersion = heimKitVersion; ValheimVersion = valheimVersion; AuditedVersion = auditedVersion;
        }
        internal string Key => Digest(Encoding.UTF8.GetBytes(WorldUid + "\0" + SaveDirectory.ToUpperInvariant() + "\0" + HeimKitVersion + "\0" + ValheimVersion));
    }
    internal sealed class Request
    {
        internal string ArchiveName = "", Metadata = "", Reason = "";
        internal Action<string> RecordVerifiedArchive = null!;
        internal bool Replacing;
    }
    internal sealed class Result
    {
        internal string ArchivePath = "", RecordPath = "", CapturedUtc = "";
        internal bool Created, Recovered;
    }
    private sealed class Record
    {
        internal string State = "", CaptureId = "", StartedUtc = "", ArchiveName = "", Checksum = "", SaveDirectory = "";
        internal long Length;
    }

    internal static Result Ensure(string directory, Identity identity, Func<Request, string> capture)
    {
        directory = Path.GetFullPath(directory);
        StartupWorldBackup.ValidatePath(directory);
        Directory.CreateDirectory(directory);
        string recordPath = Path.Combine(directory, "upgrade-" + identity.Key + ".xml");
        StartupWorldBackup.ValidatePath(recordPath);
        string lockPath = recordPath + ".lock";
        StartupWorldBackup.ValidatePath(lockPath);
        // Two processes must not both decide the same version pair needs its first backup.
        using var guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string reason = "No completed world upgrade backup is recorded for this world and HeimKit/Valheim version pair.";
        bool replacing = false;
        if (File.Exists(recordPath))
        {
            Record? previous = null;
            try
            {
                previous = Read(recordPath, identity);
                string archivePath = Path.Combine(directory, previous.ArchiveName);
                if (previous.State == "InProgress" && !File.Exists(archivePath) && File.Exists(archivePath + ".partial"))
                {
                    Verify(archivePath + ".partial", previous, identity);
                    File.Move(archivePath + ".partial", archivePath);
                }
                else Verify(archivePath, previous, identity);
            }
            catch (Exception error) when (error is FileNotFoundException || error is DirectoryNotFoundException || error is InvalidDataException || error is OverflowException || error is XmlException || error is FormatException || error is InvalidOperationException || error is ArgumentException)
            {
                previous = null;
                replacing = true;
                reason = "The earlier world upgrade backup record or its archive is missing, incomplete or invalid; a replacement checkpoint of the current world is required.";
            }
            if (previous != null)
            {
                bool recovered = previous.State == "InProgress";
                if (recovered)
                {
                    previous.State = "Complete";
                    // A record-update I/O failure must stop startup, not cause another world copy.
                    Write(recordPath, previous, identity);
                }
                return new Result { ArchivePath = Path.Combine(directory, previous.ArchiveName), RecordPath = recordPath, CapturedUtc = previous.StartedUtc, Recovered = recovered };
            }
        }
        var record = new Record { State = "InProgress", CaptureId = Guid.NewGuid().ToString("N"), StartedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), SaveDirectory = identity.SaveDirectory };
        record.ArchiveName = "upgrade-" + identity.Key + "-" + record.CaptureId + ".zip";
        string metadata = Metadata(record, identity);
        if (Encoding.UTF8.GetByteCount(metadata) > MaximumRecordCharacters) throw new InvalidDataException("World backup identity is oversized.");
        // Persist the attempt and predetermined archive name before copying any world files.
        Write(recordPath, record, identity);
        string expected = Path.Combine(directory, record.ArchiveName);
        string completed = capture(new Request
        {
            ArchiveName = record.ArchiveName, Metadata = metadata, Reason = reason, Replacing = replacing,
            RecordVerifiedArchive = partial =>
            {
                if (!string.Equals(Path.GetFullPath(partial), expected + ".partial", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("World backup verification returned an unexpected temporary archive.");
                RecordArchiveDigest(record, partial);
                Write(recordPath, record, identity);
            }
        });
        if (!string.Equals(Path.GetFullPath(completed), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("World backup capture returned an unexpected archive path.");
        Verify(expected, record, identity);
        record.State = "Complete";
        Write(recordPath, record, identity);
        return new Result { ArchivePath = expected, RecordPath = recordPath, CapturedUtc = record.StartedUtc, Created = true };
    }

    private static string Metadata(Record record, Identity identity) =>
        "HeimKit world upgrade backup\ninitiated_by=HeimKit\ncapture_id=" + record.CaptureId + "\nUTC=" + record.StartedUtc +
        "\nworld_uid=" + identity.WorldUid + "\nworld_save_directory=" + record.SaveDirectory + "\nheimkit_version=" + identity.HeimKitVersion +
        "\nvalheim_version=" + identity.ValheimVersion + "\naudited_valheim_version=" + identity.AuditedVersion + "\n";

    private static void Verify(string path, Record record, Identity identity)
    {
        StartupWorldBackup.ValidatePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (record.Length <= 0 || !HexDigest(record.Checksum) || file.Length != record.Length || Hash(file) != record.Checksum)
            throw new InvalidDataException("The recorded world upgrade archive has changed.");
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > StartupWorldBackup.MaximumFiles + 1) throw new InvalidDataException("World backup archive inventory is oversized.");
        var info = archive.GetEntry("backup-info.txt") ?? throw new InvalidDataException("World backup identity is missing.");
        if (info.Length > MaximumRecordCharacters) throw new InvalidDataException("World backup identity is oversized.");
        using (var reader = new StreamReader(info.Open()))
            if (reader.ReadToEnd() != Metadata(record, identity)) throw new InvalidDataException("World backup belongs to another capture or version pair.");
        bool worldFile = false;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("world/", StringComparison.Ordinal) && !entry.FullName.EndsWith("/", StringComparison.Ordinal)) worldFile = true;
        }
        if (!worldFile) throw new InvalidDataException("The recorded archive contains no world save files.");
    }

    private static void RecordArchiveDigest(Record record, string archivePath)
    {
        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        record.Length = archive.Length; record.Checksum = Hash(archive);
    }

    private static Record Read(string path, Identity identity)
    {
        using var input = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumRecordCharacters });
        var xml = XElement.Load(input);
        if (xml.Name != "HeimKitWorldUpgradeBackup" || (string?)xml.Attribute("format") != "1" ||
            Value(xml, "InitiatedBy") != "HeimKit" || Value(xml, "WorldUid") != identity.WorldUid ||
            !string.Equals(Value(xml, "WorldSaveDirectory"), identity.SaveDirectory, StringComparison.OrdinalIgnoreCase) ||
            Value(xml, "HeimKitVersion") != identity.HeimKitVersion || Value(xml, "ValheimVersion") != identity.ValheimVersion ||
            Value(xml, "AuditedValheimVersion") != identity.AuditedVersion)
            throw new InvalidDataException("World upgrade backup record does not match this world and version pair.");
        var record = new Record { State = Value(xml, "State"), CaptureId = Value(xml, "CaptureId"), StartedUtc = Value(xml, "StartedUtc"), ArchiveName = Value(xml, "ArchiveName"), Checksum = Value(xml, "Sha256"), Length = long.Parse(Value(xml, "ArchiveBytes"), CultureInfo.InvariantCulture), SaveDirectory = Value(xml, "WorldSaveDirectory") };
        if ((record.State != "Complete" && record.State != "InProgress") || !Guid.TryParseExact(record.CaptureId, "N", out _) ||
            record.ArchiveName != "upgrade-" + identity.Key + "-" + record.CaptureId + ".zip" ||
            !DateTime.TryParse(record.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) || time.Kind != DateTimeKind.Utc ||
            record.Length < 0 || record.State == "Complete" && record.Length == 0 ||
            record.Length > 0 && !HexDigest(record.Checksum) || record.Length == 0 && record.Checksum.Length != 0)
            throw new InvalidDataException("World upgrade backup record is incomplete or malformed.");
        return record;
    }

    private static string Value(XElement xml, string name)
    {
        string? value = null;
        foreach (var element in xml.Elements(name))
        { if (value != null) throw new InvalidDataException("Duplicate backup record field."); value = element.Value; }
        return value ?? throw new InvalidDataException("Missing backup record field: " + name);
    }

    private static void Write(string path, Record record, Identity identity)
    {
        var xml = new XElement("HeimKitWorldUpgradeBackup", new XAttribute("format", "1"),
            new XElement("InitiatedBy", "HeimKit"), new XElement("State", record.State), new XElement("CaptureId", record.CaptureId),
            new XElement("StartedUtc", record.StartedUtc), new XElement("WorldUid", identity.WorldUid),
            new XElement("WorldSaveDirectory", record.SaveDirectory), new XElement("HeimKitVersion", identity.HeimKitVersion),
            new XElement("ValheimVersion", identity.ValheimVersion), new XElement("AuditedValheimVersion", identity.AuditedVersion),
            new XElement("ArchiveName", record.ArchiveName), new XElement("ArchiveBytes", record.Length.ToString(CultureInfo.InvariantCulture)), new XElement("Sha256", record.Checksum));
        if (xml.ToString().Length + 64 > MaximumRecordCharacters) throw new InvalidDataException("World backup record is oversized.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = XmlWriter.Create(file, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false })) xml.Save(writer);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Hash(Stream stream)
    { using var hash = SHA256.Create(); return Hex(hash.ComputeHash(stream)); }
    private static string Digest(byte[] value)
    { using var hash = SHA256.Create(); return Hex(hash.ComputeHash(value)); }
    private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
    private static bool HexDigest(string value)
    { if (value.Length != 64) return false; foreach (char c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false; return true; }
}
