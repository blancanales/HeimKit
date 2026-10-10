using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.InfiniteTorches;
using HeimKit.Configuration;

// Exercise actual feature configuration and startup logs without a running Unity world.
var cases = new[]
{
    (Enabled: true, Version: "1.0.17", Active: true, Message: "Infinite Torches: ENABLED"),
    (Enabled: false, Version: "1.0.17", Active: false, Message: "DISABLED (Torches.Enabled = false)"),
    (Enabled: true, Version: "1.0.18", Active: false, Message: "DISABLED (unsupported Valheim version 1.0.18"),
    (Enabled: false, Version: "1.0.18", Active: false, Message: "DISABLED (Torches.Enabled = false)")
};
foreach (var test in cases)
{
    var config = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false)
    {
        SaveOnConfigSet = false
    };
    config.Bind("Torches", "Enabled", test.Enabled);
    using var logger = new ManualLogSource("HeimKit checks");
    var messages = new List<string>();
    logger.LogEvent += (_, args) => messages.Add(args.Data.ToString()!);
    var feature = new InfiniteTorches(config, logger);
    feature.Initialize(test.Version);
    if (feature.IsActive != test.Active || messages.Count != 1 || !messages[0].Contains(test.Message))
        throw new Exception($"Incorrect startup state/log: {test}. Logs: {string.Join("; ", messages)}");
    if (!test.Active && messages.Any(m => m.Contains(": ENABLED")))
        throw new Exception("Disabled feature falsely logged enabled.");
    feature.Shutdown();
    feature.Shutdown();
    if (feature.IsActive) throw new Exception("Shutdown did not stop the feature.");
    System.Console.WriteLine($"PASS: Enabled={test.Enabled}, version={test.Version}; startup log and repeated shutdown.");
}
System.Console.WriteLine("All 4 lifecycle scenarios passed. Live networking is not tested by these checks.");

var suppliedConfig = new ConfigFile(Path.Combine(AppContext.BaseDirectory, "heimkit.cfg"), false)
{
    SaveOnConfigSet = false
};
using var configLogger = new ManualLogSource("Supplied config check");
var suppliedFeature = new InfiniteTorches(suppliedConfig, configLogger);
if (!suppliedConfig.TryGetEntry<bool>("Diagnostics", "DebugMode", out var diagnostics) || diagnostics.Value)
    throw new Exception("Supplied configuration must default DebugMode off.");
suppliedConfig.Bind("Torches", "Enabled", true).Value = false;
suppliedFeature.Initialize("1.0.17");
if (suppliedFeature.IsActive) throw new Exception("Existing disabled setting was overridden.");
System.Console.WriteLine("PASS: supplied heimkit.cfg defaults debug mode off and respects an existing disabled setting.");

// Use a new isolated directory to exercise real file creation and reload, not just in-memory settings.
var scratch = Path.Combine(AppContext.BaseDirectory, "config-check-" + Guid.NewGuid());
var directory = Path.Combine(scratch, "BepInEx", "config");
var path = Path.Combine(directory, "heimkit.cfg");
var setupMessages = new List<string>();
using var setupLogger = new ManualLogSource("First-use config checks");
setupLogger.LogEvent += (_, args) => setupMessages.Add(args.Data.ToString()!);
try
{
    ConfigurationLoader.Load(directory, setupLogger, config => new InfiniteTorches(config, setupLogger));
    if (!File.Exists(path)) throw new Exception("First-use config was not created in the correct folder.");
    var text = File.ReadAllText(path);
    foreach (string setting in new[] { "[Torches]", "Enabled = true", "IntervalSeconds = 5", "MaxMessagesPerPass = 32", "Prefabs = piece_groundtorch_wood", "[Diagnostics]", "DebugMode = false" })
        if (!text.Contains(setting)) throw new Exception("Missing default setting: " + setting);
    if (!setupMessages.Any(m => m.Contains("Created default configuration:")))
        throw new Exception("Creation was not logged.");
    System.Console.WriteLine("PASS: missing config directory and file created with every default and a creation log.");

    // Simulate an existing user config that disables refilling and diagnostics and omits newer settings.
    File.WriteAllText(path, "[Torches]\nEnabled = false\nIntervalSeconds = 12\n\n[Diagnostics]\nDebugMode = true\nLogStatus = false\nLogRefillRequests = false\nLogRefillDetails = false\nStatusIntervalSeconds = 120\n");
    setupMessages.Clear();
    var reloaded = ConfigurationLoader.Load(directory, setupLogger, config => new InfiniteTorches(config, setupLogger));
    reloaded.Initialize("1.0.17");
    text = File.ReadAllText(path);
    foreach (string retired in new[] { "LogStatus", "LogRefillRequests", "LogRefillDetails", "StatusIntervalSeconds" })
        if (text.Contains(retired)) throw new Exception("Retired diagnostic setting remains: " + retired);
    if (reloaded.IsActive || !text.Contains("Enabled = false") || !text.Contains("IntervalSeconds = 12") || !text.Contains("DebugMode = true") || !text.Contains("MaxMessagesPerPass = 32"))
        throw new Exception("Existing settings were reset or missing defaults were not added.");
    if (!setupMessages.Any(m => m.Contains("Loaded configuration:")) || setupMessages.Any(m => m.Contains("Created default configuration:")))
        throw new Exception("Reload was logged incorrectly.");
    System.Console.WriteLine("PASS: existing settings preserved, missing defaults added, disabled feature respected and reload logged.");

    // A file occupying the expected directory must fail clearly, without reporting successful setup.
    var blocked = Path.Combine(scratch, "blocked");
    File.WriteAllText(blocked, "This is a file, not a configuration directory.");
    setupMessages.Clear();
    bool failed = false;
    try { ConfigurationLoader.Load(blocked, setupLogger, config => new InfiniteTorches(config, setupLogger)); }
    catch (IOException) { failed = true; }
    if (!failed || !setupMessages.Any(m => m.Contains("Could not load or create configuration")) || setupMessages.Any(m => m.StartsWith("Created default configuration:")))
        throw new Exception("Configuration setup failure was not handled correctly.");
    System.Console.WriteLine("PASS: unwritable configuration location reports failure instead of successful creation.");
}

finally
{
    // Only remove the isolated directory created by this test run.
    var resolvedScratch = Path.GetFullPath(scratch);
    var allowedRoot = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolvedScratch.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Refusing cleanup outside the test output directory.");
    if (Directory.Exists(resolvedScratch)) Directory.Delete(resolvedScratch, true);
}

var statusConfig = new ConfigFile(Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".cfg"), false)
{
    SaveOnConfigSet = false
};
using var statusLogger = new ManualLogSource("Diagnostic checks");
var statusMessages = new List<string>();
statusLogger.LogEvent += (_, args) => statusMessages.Add(args.Data.ToString()!);
var status = new TorchDiagnostics(statusConfig, statusLogger);
status.Report(0f, () => throw new Exception("Debug mode off must suppress diagnostic formatting."));
if (statusMessages.Count != 0 || status.IsDebugEnabled || status.BeginRefillDetails(0f))
    throw new Exception("Debug mode off did not suppress diagnostics.");
statusConfig.Bind("Diagnostics", "DebugMode", false).Value = true;
status.Report(0f, () => "Waiting for world services.");
status.Report(5f, () => throw new Exception("Throttled message should not be formatted."));
status.Report(59f, () => "No ready connected players.");
if (statusMessages.Count != 1) throw new Exception("Status messages flooded during the configured interval.");
status.Report(60f, () => "All examined torches have enough fuel.");
if (statusMessages.Count != 2 || !statusMessages[1].Contains("enough fuel"))
    throw new Exception("Status did not report the latest reason at the next interval.");
statusConfig.Bind("Diagnostics", "DebugMode", false).Value = false;
status.Report(120f, () => throw new Exception("Disabled diagnostics should not format a message."));
if (statusMessages.Count != 2) throw new Exception("Disabled status diagnostics emitted a message.");
statusConfig.Bind("Diagnostics", "DebugMode", false).Value = true;
status.Reset();
status.Report(0f, () => "Restarted status reporting.");
if (statusMessages.Count != 3) throw new Exception("Diagnostic reset retained the old timer.");
System.Console.WriteLine("PASS: status diagnostics throttle changed reasons, defer formatting, respect their off switch and reset on shutdown.");
if (!status.BeginRefillDetails(0f) || status.BeginRefillDetails(5f) || !status.BeginRefillDetails(60f))
    throw new Exception("Detailed refill passes were not rate limited.");
statusConfig.Bind("Diagnostics", "DebugMode", false).Value = false;
if (status.BeginRefillDetails(120f)) throw new Exception("Disabled refill details remained active.");
statusConfig.Bind("Diagnostics", "DebugMode", false).Value = true;
status.Reset();
if (!status.BeginRefillDetails(0f)) throw new Exception("Detail timer was not reset.");
System.Console.WriteLine("PASS: per-torch diagnostic samples respect their interval, single debug switch and reset.");
statusConfig.Bind("Diagnostics", "DebugMode", false).Value = false;
status.Report(1000f, () => throw new Exception("Disabled debug mode must suppress diagnostic output."));
if (status.IsDebugEnabled || status.BeginRefillDetails(1000f) || statusMessages.Count != 3)
    throw new Exception("Master debug switch failed to suppress enabled diagnostic categories.");
System.Console.WriteLine("PASS: master DebugMode defaults off and suppresses all optional diagnostics through one setting.");

var pending = new PendingRefills();
var torchId = new ZDOID(1L, 1U);
var otherTorchId = new ZDOID(1L, 2U);
var outcomes = new List<string>();
if (!pending.TryTrack(torchId, 10L, 0f, 4f, 0f) || pending.TryTrack(torchId, 10L, 0f, 4f, 5f))
    throw new Exception("Duplicate request was not suppressed by object ID.");
pending.Reconcile(5f, _ => new PendingRefills.Observation(10L, 0f), outcomes.Add);
if (!pending.Contains(torchId) || outcomes.Count != 0) throw new Exception("Pending request expired too early.");
pending.Reconcile(10f, _ => new PendingRefills.Observation(10L, 1f), outcomes.Add);
if (!pending.Contains(torchId)) throw new Exception("Insufficient partial fuel increase released the request.");
pending.Reconcile(15f, _ => new PendingRefills.Observation(10L, 3.99f), outcomes.Add);
if (pending.Count != 0 || !outcomes.Exists(m => m.Contains("observed fuel increase")))
    throw new Exception("Replicated fuel increase was not observed.");
pending.TryTrack(torchId, 10L, 0f, 4f, 20f);
pending.Reconcile(49f, _ => new PendingRefills.Observation(10L, 0f), outcomes.Add);
if (!pending.Contains(torchId)) throw new Exception("Retry permitted before the timeout.");
pending.Reconcile(50f, _ => new PendingRefills.Observation(10L, 0f), outcomes.Add);
if (pending.Contains(torchId) || !outcomes.Exists(m => m.Contains("retry allowed")))
    throw new Exception("Timeout did not permit retry.");
pending.TryTrack(torchId, 10L, 0f, 4f, 60f);
pending.TryTrack(otherTorchId, 10L, 0f, 4f, 60f);
pending.Reconcile(65f, id => id.Equals(torchId) ? new PendingRefills.Observation(20L, 0f) : (PendingRefills.Observation?)null, outcomes.Add);
if (pending.Count != 0 || !outcomes.Exists(m => m.Contains("owner changed")) || !outcomes.Exists(m => m.Contains("no longer available")))
    throw new Exception("Owner changes and removed objects were not cleared.");
for (uint index = 1; index <= PendingRefills.Capacity; index++)
    if (!pending.TryTrack(new ZDOID(2L, index), 10L, 0f, 4f, 70f)) throw new Exception("Unexpected tracking capacity.");
if (pending.TryTrack(new ZDOID(3L, 1U), 10L, 0f, 4f, 70f)) throw new Exception("Pending tracking is unbounded.");
pending.Reconcile(100f, _ => new PendingRefills.Observation(10L, 0f), null);
if (pending.Count != 0) throw new Exception("Out-of-area timed-out requests leaked tracking state.");
pending.TryTrack(torchId, 10L, 0f, 4f, 110f);
pending.Clear();
pending.Clear();
if (pending.Count != 0) throw new Exception("Shutdown did not clear pending tracking.");
System.Console.WriteLine("PASS: pending refills deduplicate IDs, wait for sufficient fuel, retry only after timeout, handle ownership/deletion, bound memory and clear on shutdown.");
