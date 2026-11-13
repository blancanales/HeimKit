using System.Reflection;
using System.Xml.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;

internal static class ModuleFailureProtectionTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "heimkit-failures-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        using var log = new ManualLogSource("Persistent failure tests");
        try { RestartsAndIdentity(root, log); HostFailures(root, log); ModuleCallbacks(root, log); StorageFailures(root, log); }
        finally { Directory.Delete(root, true); }
        System.Console.WriteLine("PASS: persistent module failure overrides, restart/version/world isolation, backup association, native callback reporting, live enable guards and storage failure handling.");
    }

    private static WorldUpgradeBackup.Identity Identity(string root, string build = "test.1", string game = "99.0.0", long world = 1) =>
        new(world, Path.Combine(root, "world"), build, game, GameVersionCompatibility.AuditedVersion);

    private static void RestartsAndIdentity(string root, ManualLogSource log)
    {
        string directory = Path.Combine(root, "restart");
        var identity = Identity(root);
        string world = Path.Combine(root, "source.db"); File.WriteAllText(world, "world checkpoint");
        var backup = WorldUpgradeBackup.Ensure(directory, identity, request => StartupWorldBackup.Create(directory,
            new[] { new StartupWorldBackup.Source(world, "world/World.db", false) }, request.Metadata, request.ArchiveName, request.RecordVerifiedArchive));
        byte[] archive = File.ReadAllBytes(backup.ArchivePath), record = File.ReadAllBytes(backup.RecordPath);
        string failures;
        using (var protection = new ModuleFailureProtection(log))
        {
            // Constructor errors happen before the selected world becomes available.
            ModuleFailureProtection.Record("StartupBroken", new MissingMethodException("native API"), "configuration construction");
            protection.Attach(directory, identity, backup); failures = protection.RecordPath;
            ModuleFailureProtection.Record("WeatherGuard", new InvalidOperationException(new string('x', 5000) + "\0"), "native callback");
            string first = File.ReadAllText(failures);
            ModuleFailureProtection.Record("WeatherGuard", new Exception("later incident"), "later");
            Check(File.ReadAllText(failures) == first, "A later error replaced the first failure.");
            var entries = XElement.Load(failures).Elements("Module").ToArray();
            Check(entries.Length == 2 && entries.All(e => e.Element("BackupArchive")!.Value == backup.ArchivePath && e.Element("BackupRecord")!.Value == backup.RecordPath), "Failures lost their world backup association.");
            Check(entries.Single(e => e.Element("Name")!.Value == "WeatherGuard").Element("Message")!.Value.Length == 2048, "Exception evidence was not bounded.");
        }
        for (int startup = 0; startup < 50; startup++)
        {
            using var protection = new ModuleFailureProtection(log);
            protection.Attach(directory, identity, backup);
            Check(!ModuleFailureProtection.MayStart("WeatherGuard") && !ModuleFailureProtection.MayStart("StartupBroken") && ModuleFailureProtection.MayStart("Healthy"), "Restart lost a disable or blocked a healthy feature.");
        }
        Check(File.ReadAllBytes(backup.ArchivePath).SequenceEqual(archive) && File.ReadAllBytes(backup.RecordPath).SequenceEqual(record), "Recording failures modified the verified world checkpoint.");
        foreach (var other in new[] { Identity(root, build: "test.2"), Identity(root, game: "99.0.1"), Identity(root, world: 2), Identity(Path.Combine(root, "elsewhere")) })
        {
            using var protection = new ModuleFailureProtection(log);
            protection.Attach(directory, other, null);
            Check(ModuleFailureProtection.MayStart("WeatherGuard") && protection.RecordPath != failures, "A new version/world inherited another combination's disable.");
        }
        // Losing/replacing an archive must not erase module decisions.
        File.Delete(backup.ArchivePath);
        var replacement = WorldUpgradeBackup.Ensure(directory, identity, request => StartupWorldBackup.Create(directory,
            new[] { new StartupWorldBackup.Source(world, "world/World.db", false) }, request.Metadata, request.ArchiveName, request.RecordVerifiedArchive));
        using (var protection = new ModuleFailureProtection(log))
        { protection.Attach(directory, identity, replacement); Check(!ModuleFailureProtection.MayStart("WeatherGuard"), "Replacement backup reset failure protection."); }
        // Explicit stopped-server reset affects only the selected module.
        var xml = XElement.Load(failures);
        xml.Elements("Module").Single(e => e.Element("Name")!.Value == "WeatherGuard").Remove(); xml.Save(failures);
        using (var protection = new ModuleFailureProtection(log))
        { protection.Attach(directory, identity, replacement); Check(ModuleFailureProtection.MayStart("WeatherGuard") && !ModuleFailureProtection.MayStart("StartupBroken"), "Selective reset cleared unrelated decisions."); }
    }

    private sealed class Module : IServerModule
    {
        public string Name { get; }
        public bool IsActive { get; private set; }
        public int Starts, Ticks, Stops;
        public bool ThrowOnStart, ThrowOnTick;
        internal Module(string name) => Name = name;
        public void Initialize(string version) { Starts++; IsActive = true; if (ThrowOnStart) throw new MissingMethodException("startup"); }
        public void Tick(float now) { Ticks++; if (ThrowOnTick) throw new InvalidOperationException("runtime"); }
        public void Shutdown() { Stops++; IsActive = false; }
    }
    private static void HostFailures(string root, ManualLogSource log)
    {
        string directory = Path.Combine(root, "host"); var identity = Identity(root);
        using (var protection = new ModuleFailureProtection(log))
        using (var host = new ModuleHost(log))
        {
            host.Register(() => throw new InvalidOperationException("constructor"), "ConstructionBroken");
            protection.Attach(directory, identity, null);
            var startup = new Module("StartupBroken") { ThrowOnStart = true };
            var runtime = new Module("RuntimeBroken") { ThrowOnTick = true }; var healthy = new Module("Healthy");
            host.Register(() => startup, startup.Name); host.Register(() => runtime, runtime.Name); host.Register(() => healthy, healthy.Name);
            host.Initialize("99.0.0"); host.Tick(0); host.Tick(1);
            Check(startup.Stops == 1 && runtime.Stops == 1 && startup.Ticks == 0 && runtime.Ticks == 1 && healthy.Ticks == 2, "Failure isolation or same-session runtime blocking failed.");
        }
        using (var protection = new ModuleFailureProtection(log))
        using (var host = new ModuleHost(log))
        {
            protection.Attach(directory, identity, null);
            foreach (string name in new[] { "ConstructionBroken", "StartupBroken", "RuntimeBroken" })
            {
                var module = new Module(name); host.Register(() => module, name); host.Initialize("99.0.0"); host.Tick(0);
                Check(module.Starts == 0 && module.Ticks == 0, "Host activated a previously failed module.");
            }
        }
    }

    private static void ModuleCallbacks(string root, ManualLogSource log)
    {
        string directory = Path.Combine(root, "callbacks"), cfg = Path.Combine(root, "callbacks.cfg");
        var config = new ConfigFile(cfg, false) { SaveOnConfigSet = false };
        using var players = new PlayerServices(config, log);
        var modules = new IServerModule[] {
            new HeimKit.Features.InfiniteTorches.InfiniteTorches(config, log, players), new HeimKit.Features.WeatherGuard.WeatherGuard(config, log, players),
            new HeimKit.Features.AFKNightWatch.AFKNightWatch(config, log, players), new HeimKit.Features.AFKRaidWatch.AFKRaidWatch(config, log, players),
            new HeimKit.Features.SharedMap.SharedMap(config, log, players), new HeimKit.Features.DoorKeeper.DoorKeeper(config, log, players),
            new HeimKit.Features.ForestRenewal.ForestRenewal(config, log, players), new HeimKit.Features.SeaSalvage.SeaSalvage(config, log, players),
            new HeimKit.Features.TameTravel.TameTravel(config, log, players), new HeimKit.Features.HeimMender.HeimMender(config, log, players) };
        config.Save(); byte[] originalConfig = File.ReadAllBytes(cfg);
        using (var protection = new ModuleFailureProtection(log))
        {
            protection.Attach(directory, Identity(root), null);
            Check(!File.Exists(protection.RecordPath), "A healthy waiting session was recorded as unstable.");
            foreach (var module in modules)
            {
                // Exercise the actual callback error handler without installing Unity gameplay hooks.
                module.GetType().GetProperty("IsActive")!.GetSetMethod(true)!.Invoke(module, new object[] { true });
                var fail = module.GetType().GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic)!;
                object[] args = module.Name == "InfiniteTorches" ? new object[] { new MissingMethodException("callback"), "callback" } : new object[] { new MissingMethodException("callback") };
                fail.Invoke(module, args);
                Check(!module.IsActive && !ModuleFailureProtection.MayStart(module.Name), "Callback failed to shut down/persist " + module.Name);
            }
        }
        using (var protection = new ModuleFailureProtection(log))
        {
            protection.Attach(directory, Identity(root), null);
            foreach (var module in modules)
            {
                module.Initialize(GameVersionCompatibility.AuditedVersion);
                Check(!module.IsActive, "Direct Initialize bypassed a persistent disable: " + module.Name);
            }
            var mender = modules.Last();
            // Simulate a live Enabled toggle after an attempted reinitialization clears its old session flag.
            mender.GetType().GetField("_compatible", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mender, true);
            mender.GetType().GetMethod("ApplyEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mender, null);
            Check(!mender.IsActive && players.Events.Subscribers == 0, "Live enablement bypassed failure protection.");
        }
        foreach (var module in modules) module.Shutdown();
        Check(File.ReadAllBytes(cfg).SequenceEqual(originalConfig), "Protection changed the user's configuration.");
    }

    private static void StorageFailures(string root, ManualLogSource log)
    {
        string directory = Path.Combine(root, "storage"), path; var identity = Identity(root);
        using (var protection = new ModuleFailureProtection(log))
        {
            protection.Attach(directory, identity, null); path = protection.RecordPath;
            // A directory at the record path guarantees publication fails without requiring ACL changes.
            Directory.CreateDirectory(path);
            ModuleFailureProtection.Record("Broken", new Exception("runtime"), "runtime");
            Check(!ModuleFailureProtection.MayStart("Broken") && !ModuleFailureProtection.MayStart("Healthy"), "Persistence failure allowed further activation.");
            bool stopped = false; try { protection.ThrowIfFailed(); } catch (IOException) { stopped = true; }
            Check(stopped, "Shared processing was not told to stop after a write failure.");
            Directory.Delete(path);
        }
        File.WriteAllText(path, "<malformed>");
        using (var protection = new ModuleFailureProtection(log))
        {
            bool rejected = false; try { protection.Attach(directory, identity, null); } catch (System.Xml.XmlException) { rejected = true; }
            Check(rejected, "A corrupt protection file was silently discarded.");
        }
        File.WriteAllText(path, "<!DOCTYPE x [<!ENTITY leak SYSTEM 'file:///unused'>]><x>&leak;</x>");
        using (var protection = new ModuleFailureProtection(log))
        {
            bool rejected = false; try { protection.Attach(directory, identity, null); } catch (System.Xml.XmlException) { rejected = true; }
            Check(rejected, "Protection parsing accepted a DTD.");
        }
    }
}
