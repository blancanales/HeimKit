using System.IO.Compression;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.World;

internal static class StartupBackupTests
{
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "heimkit-backup-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try { Archives(root); VersionRecords(root); Gate(root); NativeLayout(); }
        finally { Directory.Delete(root, true); }
        System.Console.WriteLine("PASS: complete chunked/legacy/state archives, verified bytes, one checkpoint across fifty restarts, persistent version/world identity, interrupted-write recovery, missing/corrupt archive replacement, startup gating and native adapter signatures.");
    }

    private static void Archives(string root)
    {
        string world = Path.Combine(root, "world");
        Directory.CreateDirectory(Path.Combine(world, "chunks"));
        byte[] chunk = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(world, "chunks", "001.db"), chunk);
        File.WriteAllText(Path.Combine(world, "world.fwl2"), "metadata");
        string cfg = Path.Combine(root, "heimkit.cfg"); File.WriteAllText(cfg, "configuration");
        string shared = Path.Combine(root, "SharedMap"); Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "world.map"), "map bytes");
        string renewal = Path.Combine(root, "ForestRenewal"); Directory.CreateDirectory(renewal);
        File.WriteAllText(Path.Combine(renewal, "world.renewals"), "renewal bytes");
        var sources = new[] {
            new StartupWorldBackup.Source(world, "world/Example", true),
            new StartupWorldBackup.Source(cfg, "configuration/heimkit.cfg", false),
            new StartupWorldBackup.Source(shared, "configuration/HeimKit/SharedMap", true),
            new StartupWorldBackup.Source(renewal, "configuration/HeimKit/ForestRenewal", true),
            new StartupWorldBackup.Source(Path.Combine(root, "absent"), "optional", true, optional: true)
        };
        string output = Path.Combine(root, "backups");
        string first = StartupWorldBackup.Create(output, sources, "world_uid=123\n");
        using (var archive = ZipFile.OpenRead(first))
        {
            using var data = archive.GetEntry("world/Example/chunks/001.db")!.Open();
            using var copy = new MemoryStream(); data.CopyTo(copy);
            Check(copy.ToArray().SequenceEqual(chunk), "Chunk bytes were changed or truncated.");
            foreach (string name in new[] { "world/Example/world.fwl2", "configuration/heimkit.cfg", "configuration/HeimKit/SharedMap/world.map", "configuration/HeimKit/ForestRenewal/world.renewals", "backup-info.txt" })
                Check(archive.GetEntry(name) != null, "Required backup data was omitted: " + name);
        }
        string second = StartupWorldBackup.Create(output, sources, "world_uid=123\n");
        Check(first != second && File.Exists(first) && File.Exists(second), "A previous backup was replaced.");
        Check(File.ReadAllBytes(Path.Combine(world, "chunks", "001.db")).SequenceEqual(chunk), "Capture modified live source data.");
        using (var writer = new FileStream(cfg, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            Reject(() => StartupWorldBackup.Create(output, sources, ""), "Capture accepted a source open for writing.");
        Reject(() => StartupWorldBackup.Create(Path.Combine(world, "nested-backups"), sources, ""), "Capture included its own destination.");
        Reject(() => StartupWorldBackup.Create(output, new[] { new StartupWorldBackup.Source(Path.Combine(root, "missing.db"), "world/missing.db", false) }, ""), "A missing world source produced a completed backup.");
        Reject(() => StartupWorldBackup.Create(output, new[] { new StartupWorldBackup.Source(cfg, "../escape", false) }, ""), "Unsafe archive path was accepted.");
        Reject(() => StartupWorldBackup.Create(output, new[] { new StartupWorldBackup.Source(cfg, "same", false), new StartupWorldBackup.Source(cfg, "same", false) }, ""), "Duplicate archive paths were accepted.");
        Check(Directory.GetFiles(output, "*.zip").Length == 2 && Directory.GetFiles(output, "*.partial").Length == 0, "Failed capture left a completed or partial backup.");
        string db = Path.Combine(root, "Legacy.db"), fwl = Path.Combine(root, "Legacy.fwl");
        File.WriteAllText(db, "database"); File.WriteAllText(fwl, "legacy metadata");
        string legacy = StartupWorldBackup.Create(output, new[] { new StartupWorldBackup.Source(db, "world/Legacy.db", false), new StartupWorldBackup.Source(fwl, "world/Legacy.fwl", false) }, "legacy");
        using var old = ZipFile.OpenRead(legacy);
        Check(old.GetEntry("world/Legacy.db") != null && old.GetEntry("world/Legacy.fwl") != null, "Legacy paired saves were not kept together.");
    }

    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (IOException) { rejected = true; }
        Check(rejected, message);
    }

    private static void VersionRecords(string root)
    {
        string world = Path.Combine(root, "record-world"); Directory.CreateDirectory(world);
        string source = Path.Combine(world, "world.db"); File.WriteAllText(source, "original world");
        string output = Path.Combine(root, "recorded-backups");
        int copies = 0;
        string Capture(WorldUpgradeBackup.Request request)
        {
            copies++;
            return StartupWorldBackup.Create(output, new[] { new StartupWorldBackup.Source(source, "world/world.db", false) }, request.Metadata, request.ArchiveName, request.RecordVerifiedArchive);
        }
        WorldUpgradeBackup.Identity Key(long uid = 17, string heimkit = "0.2.1", string game = "1.0.22", string? location = null) =>
            new(uid, location ?? world, heimkit, game, GameVersionCompatibility.AuditedVersion);
        var first = WorldUpgradeBackup.Ensure(output, Key(), Capture);
        Check(first.Created && File.Exists(first.RecordPath), "First upgrade backup was not recorded.");
        for (int restart = 0; restart < 50; restart++)
        {
            // Normal gameplay changes the live world; the original upgrade checkpoint must still be reused.
            File.WriteAllText(source, "world after restart " + restart);
            var repeated = WorldUpgradeBackup.Ensure(output, Key(), Capture);
            Check(!repeated.Created && !repeated.Recovered && repeated.ArchivePath == first.ArchivePath && repeated.CapturedUtc == first.CapturedUtc,
                "A restart replaced or recopied the original world upgrade checkpoint.");
        }
        Check(copies == 1 && Directory.GetFiles(output, "*.zip").Length == 1, "Fifty restarts made duplicate world backups.");
        Check(!WorldUpgradeBackup.Ensure(output, Key(location: world.ToUpperInvariant()), Capture).Created,
            "Case-only changes in a Windows save path made another world backup.");
        Check(WorldUpgradeBackup.Ensure(output, Key(game: "1.0.23"), Capture).Created, "New Valheim version reused another version's checkpoint.");
        Check(WorldUpgradeBackup.Ensure(output, Key(heimkit: "0.2.2"), Capture).Created, "New HeimKit build was attributed to the previous build.");
        Check(WorldUpgradeBackup.Ensure(output, Key(uid: 18), Capture).Created, "Different world reused another world's checkpoint.");
        Check(WorldUpgradeBackup.Ensure(output, Key(location: Path.Combine(root, "different-save")), Capture).Created, "Another save location reused the original checkpoint.");
        File.Delete(first.ArchivePath);
        var replaced = WorldUpgradeBackup.Ensure(output, Key(), Capture);
        Check(replaced.Created && replaced.ArchivePath != first.ArchivePath, "A missing archive was trusted because its record existed.");
        File.AppendAllText(replaced.ArchivePath, "changed backup");
        var repaired = WorldUpgradeBackup.Ensure(output, Key(), Capture);
        Check(repaired.Created, "A changed archive bypassed checksum verification.");
        File.WriteAllText(repaired.RecordPath, "invalid XML");
        Check(WorldUpgradeBackup.Ensure(output, Key(), Capture).Created, "A corrupt record was trusted.");

        var interrupted = Key(uid: 19);
        int interruptedCopies = 0;
        Reject(() => WorldUpgradeBackup.Ensure(output, interrupted, request =>
        {
            interruptedCopies++; Capture(request);
            throw new IOException("crash after archive completion, before record completion");
        }), "Interrupted capture did not retain its failure.");
        var recovered = WorldUpgradeBackup.Ensure(output, interrupted, _ => throw new Exception("Completed archive should be recovered without another copy."));
        Check(recovered.Recovered && !recovered.Created && interruptedCopies == 1, "An interrupted record update made a duplicate backup.");
        var beforeRename = Key(uid: 20);
        Reject(() => WorldUpgradeBackup.Ensure(output, beforeRename, request =>
        {
            string completed = Capture(request);
            File.Move(completed, completed + ".partial");
            throw new IOException("crash after archive verification, before its final rename");
        }), "Interrupted final rename did not retain its failure.");
        Check(WorldUpgradeBackup.Ensure(output, beforeRename, _ => throw new Exception("Verified temporary archive should be recovered without another copy.")).Recovered,
            "A verified temporary archive was needlessly copied again.");
        using var guard = new FileStream(recovered.RecordPath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Reject(() => WorldUpgradeBackup.Ensure(output, interrupted, _ => throw new Exception("Another capture must not start.")), "Concurrent backup-record access was not excluded.");
    }

    private static void Gate(string root)
    {
        using var log = new ManualLogSource("Startup backup gate");
        var errors = new List<string>(); log.LogEvent += (_, args) => { if (args.Level == LogLevel.Error) errors.Add(LogCapture.Text(args.Data)); };
        var config = new ConfigFile(Path.Combine(root, "gate.cfg"), false) { SaveOnConfigSet = false };
        var policy = GameVersionCompatibility.Resolve(config, log);
        using var pending = new UpgradeStartup(policy, "9.9.9", root, log);
        Check(!pending.Ready && !pending.Failed, "Unverified modules were enabled before backup.");
        int captures = 0;
        pending.Capture(() => { Check(!pending.Ready, "Modules activated during backup."); captures++; });
        pending.Capture(() => captures++);
        Check(pending.Ready && captures == 1, "Successful startup capture was repeated or did not release the gate.");
        using var failed = new UpgradeStartup(policy, "9.9.9", root, log);
        failed.Capture(() => throw new IOException("injected full disk"));
        failed.Capture(() => captures++);
        Check(!failed.Ready && failed.Failed && captures == 1 && errors.Count == 1, "Backup failure enabled modules, retried automatically or lost its error.");
        using var confirmed = new UpgradeStartup(policy, GameVersionCompatibility.AuditedVersion, root, log);
        confirmed.Capture(() => throw new Exception("Audited startup should not capture"));
        Check(confirmed.Ready, "Audited startup was blocked by an upgrade backup.");
        Check(GameVersionCompatibility.CompareToAudited("1.0.20") < 0 && !policy.NeedsStartupBackup("1.0.20"), "An older server version triggered the newer-version backup policy.");
        Check(GameVersionCompatibility.CompareToAudited(GameVersionCompatibility.AuditedVersion + ".0") == 0,
            "An equivalent numeric version with a zero revision was treated as newer.");
        var strictConfig = new ConfigFile(Path.Combine(root, "strict.cfg"), false) { SaveOnConfigSet = false };
        strictConfig.Bind("Compatibility", "AllowUnverifiedVersions", false);
        var strict = GameVersionCompatibility.Resolve(strictConfig, log);
        using var rejected = new UpgradeStartup(strict, "9.9.9", root, log);
        Check(rejected.Ready && !strict.Supports("9.9.9"), "Strict version rejection incorrectly requested a backup or allowed modules.");
    }

    private static void NativeLayout()
    {
        var load = typeof(ZNet).GetMethod("ServerLoadWorld", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        Check(load?.ReturnType == typeof(void), "Native pre-load backup hook is unavailable.");
        Check(typeof(global::World).GetMethod("GetSavePaths", Type.EmptyTypes)?.ReturnType == typeof(List<string>) &&
            typeof(global::World).GetMethod("IsChunkedSave", Type.EmptyTypes)?.ReturnType == typeof(bool) &&
            typeof(global::World).GetMethod("GetSaveFWLPath", Type.EmptyTypes)?.ReturnType == typeof(string) &&
            typeof(global::World).GetMethod("GetSaveDirectory", new[] { typeof(FileHelpers.FileSource) })?.ReturnType == typeof(string), "Native world save layout access is incompatible.");
    }
}
