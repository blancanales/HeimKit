using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.World;

namespace HeimKit.Core.Lifecycle;

// The plugin owns this session. Explicit disabling paths report here, never through log parsing.
internal sealed class ModuleFailureProtection : IDisposable
{
    private const int MaximumCharacters = 1048576, MaximumModules = 64;
    private static ModuleFailureProtection? _current;
    private readonly object _sync = new();
    private readonly ManualLogSource _logger;
    private readonly Dictionary<string, Failure> _failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private WorldUpgradeBackup.Identity? _identity;
    private WorldUpgradeBackup.Result? _backup;
    private FileStream? _guard;
    private Exception? _persistenceError;
    internal string RecordPath { get; private set; } = "";
    internal bool Attached => _identity != null;
    private sealed class Failure
    {
        internal string Name = "", Utc = "", Operation = "", Type = "", Message = "", Stack = "";
        internal string BackupRecord = "", BackupArchive = "";
    }

    internal ModuleFailureProtection(ManualLogSource logger)
    {
        if (_current != null) throw new InvalidOperationException("A module failure protection session already exists.");
        _logger = logger; _current = this;
    }

    internal static bool MayStart(string module) => _current?.Allows(module) ?? true;
    internal static void Record(string module, Exception error, string operation) => _current?.Capture(module, error, operation);

    internal void Attach(string directory, WorldUpgradeBackup.Identity identity, WorldUpgradeBackup.Result? backup)
    {
        lock (_sync)
        {
            ThrowIfFailed();
            if (_identity != null)
            {
                if (_identity.Key != identity.Key) throw new InvalidOperationException("The selected world changed during module failure protection.");
                return;
            }
            directory = Path.GetFullPath(directory);
            StartupWorldBackup.ValidatePath(directory);
            Directory.CreateDirectory(directory);
            RecordPath = Path.Combine(directory, "upgrade-" + identity.Key + ".failures.xml");
            StartupWorldBackup.ValidatePath(RecordPath);
            StartupWorldBackup.ValidatePath(RecordPath + ".lock");
            // Hold this for the session: another process must not overwrite decisions using stale state.
            _guard = new FileStream(RecordPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                var pending = new List<Failure>(_failures.Values);
                var existing = Read(identity);
                _failures.Clear();
                foreach (var failure in existing) _failures.Add(failure.Name, failure);
                _identity = identity; _backup = backup;
                bool changed = false;
                foreach (var failure in pending)
                {
                    if (_failures.ContainsKey(failure.Name)) continue;
                    LinkBackup(failure); _failures.Add(failure.Name, failure); changed = true;
                }
                if (changed) Save();
                DiagnosticLog.Write(_logger, "HeimKit", "Loaded persistent feature protection for world " + identity.WorldUid +
                    ", HeimKit " + identity.HeimKitVersion + ", Valheim " + identity.ValheimVersion + "; disabled features: " +
                    _failures.Count + ". Record: " + RecordPath + ". World backup: " + (backup?.ArchivePath ?? "no automatic upgrade backup required") + ".");
            }
            catch { _guard.Dispose(); _guard = null; throw; }
        }
    }

    internal void ThrowIfFailed()
    {
        lock (_sync)
            if (_persistenceError != null) throw new IOException("Persistent feature protection could not save a disabling failure: " + RecordPath, _persistenceError);
    }

    private bool Allows(string module)
    {
        lock (_sync)
        {
            if (_persistenceError != null) return false;
            if (!_failures.TryGetValue(module, out var failure)) return true;
            if (_reported.Add(module))
                DiagnosticLog.Write(_logger, module, CoreLogTopics.Lifecycle,
                    "DISABLED by persistent feature protection; overrides Enabled=true without changing configuration. First failure UTC: " +
                    failure.Utc + "; operation: " + failure.Operation + "; HeimKit " + (_identity?.HeimKitVersion ?? "pending world selection") +
                    "; Valheim " + (_identity?.ValheimVersion ?? "pending world selection") + ". Record: " + RecordPath +
                    ". Look for the latest HeimKit release documented as compatible with this Valheim version; see docs/server-setup.md#persistent-feature-disables.",
                    level: LogLevel.Warning);
            return false;
        }
    }

    private void Capture(string module, Exception error, string operation)
    {
        lock (_sync)
        {
            // Reporting must not throw before the caller releases its hooks/ownership.
            try
            {
                if (_failures.ContainsKey(module)) return;
                if (!ValidName(module) || _failures.Count >= MaximumModules) throw new InvalidDataException("Invalid or excessive disabled-module identity.");
                var cause = error.GetBaseException();
                var failure = new Failure { Name = module, Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Operation = Bounded(operation, 256), Type = Bounded(cause.GetType().FullName ?? cause.GetType().Name, 512),
                    Message = Bounded(cause.Message, 2048), Stack = Bounded(error.StackTrace ?? "", 8192) };
                LinkBackup(failure); _failures.Add(module, failure);
                if (_identity == null) return; // Constructor errors are queued until the world is selected.
                Save();
                DiagnosticLog.Write(_logger, module, CoreLogTopics.Lifecycle,
                    "Saved disabling failure for HeimKit " + _identity.HeimKitVersion + ", Valheim " + _identity.ValheimVersion +
                    "; this feature will remain disabled on future starts for this world/version combination, overriding Enabled=true. Record: " +
                    RecordPath + ". World backup: " + ( _backup?.ArchivePath ?? "no automatic upgrade backup required") +
                    ". Look for the latest HeimKit release documented as compatible with this Valheim version.", level: LogLevel.Error);
            }
            catch (Exception persistenceError)
            {
                _persistenceError ??= persistenceError;
                DiagnosticLog.Write(_logger, module, CoreLogTopics.Lifecycle,
                    "Could not persist the disabling failure. The feature remains blocked in memory; shared HeimKit processing will stop. Record: " + RecordPath,
                    level: LogLevel.Error, error: persistenceError);
            }
        }
    }

    private void LinkBackup(Failure failure)
    { failure.BackupRecord = _backup?.RecordPath ?? ""; failure.BackupArchive = _backup?.ArchivePath ?? ""; }

    private List<Failure> Read(WorldUpgradeBackup.Identity identity)
    {
        var result = new List<Failure>();
        // Opening directly distinguishes a missing file from denied access instead of hiding errors.
        FileStream stream;
        try { stream = new FileStream(RecordPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return result; }
        using (stream)
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumCharacters }))
        {
            var xml = XElement.Load(reader);
            if (xml.Name != "HeimKitModuleFailures" || (string?)xml.Attribute("format") != "1" || Value(xml, "WorldUid") != identity.WorldUid ||
                !string.Equals(Value(xml, "WorldSaveDirectory"), identity.SaveDirectory, StringComparison.OrdinalIgnoreCase) ||
                Value(xml, "HeimKitVersion") != identity.HeimKitVersion || Value(xml, "ValheimVersion") != identity.ValheimVersion)
                throw new InvalidDataException("Module failure record does not match this world and version combination.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in xml.Elements("Module"))
            {
                var failure = new Failure { Name = Value(node, "Name"), Utc = Value(node, "FirstFailureUtc"), Operation = Value(node, "Operation"),
                    Type = Value(node, "ExceptionType"), Message = Value(node, "Message"), Stack = Value(node, "StackTrace"),
                    BackupRecord = Value(node, "BackupRecord"), BackupArchive = Value(node, "BackupArchive") };
                if (!ValidName(failure.Name) || !names.Add(failure.Name) || result.Count >= MaximumModules ||
                    !DateTime.TryParse(failure.Utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) || time.Kind != DateTimeKind.Utc ||
                    failure.Operation.Length > 256 || failure.Type.Length > 512 || failure.Message.Length > 2048 || failure.Stack.Length > 8192 ||
                    failure.BackupRecord.Length > 32768 || failure.BackupArchive.Length > 32768)
                    throw new InvalidDataException("Malformed or duplicate disabled-module entry.");
                result.Add(failure);
            }
            foreach (var node in xml.Elements())
                if (node.Name != "WorldUid" && node.Name != "WorldSaveDirectory" && node.Name != "HeimKitVersion" && node.Name != "ValheimVersion" && node.Name != "Module")
                    throw new InvalidDataException("Unknown module failure record field.");
        }
        return result;
    }

    private void Save()
    {
        var identity = _identity ?? throw new InvalidOperationException("No world identity is attached.");
        var xml = new XElement("HeimKitModuleFailures", new XAttribute("format", "1"), new XElement("WorldUid", identity.WorldUid),
            new XElement("WorldSaveDirectory", identity.SaveDirectory), new XElement("HeimKitVersion", identity.HeimKitVersion), new XElement("ValheimVersion", identity.ValheimVersion));
        foreach (var failure in _failures.Values)
            xml.Add(new XElement("Module", new XElement("Name", failure.Name), new XElement("FirstFailureUtc", failure.Utc),
                new XElement("Operation", failure.Operation), new XElement("ExceptionType", failure.Type), new XElement("Message", failure.Message),
                new XElement("StackTrace", failure.Stack), new XElement("BackupRecord", failure.BackupRecord), new XElement("BackupArchive", failure.BackupArchive)));
        if (xml.ToString().Length + 64 > MaximumCharacters) throw new InvalidDataException("Module failure record is oversized.");
        StartupWorldBackup.ValidatePath(RecordPath);
        string temporary = RecordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = XmlWriter.Create(file, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false })) xml.Save(writer);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(RecordPath)) File.Replace(temporary, RecordPath, null);
            else File.Move(temporary, RecordPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Value(XElement node, string name)
    {
        string? value = null;
        foreach (var element in node.Elements(name))
        { if (value != null) throw new InvalidDataException("Duplicate module failure field."); value = element.Value; }
        return value ?? throw new InvalidDataException("Missing module failure field: " + name);
    }
    private static bool ValidName(string value)
    { if (value.Length == 0 || value.Length > 64) return false; foreach (char c in value) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9')) return false; return true; }
    private static string Bounded(string value, int maximum)
    {
        var text = new StringBuilder(Math.Min(value.Length, maximum));
        for (int i = 0; i < value.Length && i < maximum; i++) text.Append(XmlConvert.IsXmlChar(value[i]) ? value[i] : '?');
        return text.ToString();
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, this)) _current = null;
            _guard?.Dispose(); _guard = null;
        }
    }
}
