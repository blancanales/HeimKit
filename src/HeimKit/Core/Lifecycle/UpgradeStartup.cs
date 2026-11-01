using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Native;
using HeimKit.Core.World;

namespace HeimKit.Core.Lifecycle;

internal sealed class UpgradeStartup : IDisposable
{
    private readonly ManualLogSource _logger;
    private readonly string _configDirectory, _gameVersion;
    private readonly Harmony _hooks = new("heimkit.startup-backup");
    private static UpgradeStartup? _current;
    private bool _attempted, _hooked, _protectionAttached;
    private WorldUpgradeBackup.Identity? _identity;
    private WorldUpgradeBackup.Result? _backup;
    private global::World? _selectedWorld;
    internal bool Ready { get; private set; }
    internal bool Failed { get; private set; }

    internal UpgradeStartup(GameVersionCompatibility policy, string version, string configDirectory, ManualLogSource logger)
    { _logger = logger; _configDirectory = configDirectory; _gameVersion = version; Ready = !policy.NeedsStartupBackup(version); }

    internal void Initialize()
    {
        if (Ready) return;
        try
        {
            var target = NativeAccess.Target(typeof(ZNet), "ServerLoadWorld");
            if (target.IsStatic || target.ReturnType != typeof(void)) throw new MissingMethodException("Dedicated world-load entry point is incompatible.");
            _current = this;
            _hooked = true;
            _hooks.Patch(target, prefix: new HarmonyMethod(typeof(UpgradeStartup), nameof(BeforeWorldLoad)));
            string comparison = GameVersionCompatibility.CompareToAudited(_gameVersion) is int ? "is newer than" : "cannot be ordered against";
            DiagnosticLog.Write(_logger, "HeimKit", "Valheim " + _gameVersion + " " + comparison + " the tested baseline " +
                GameVersionCompatibility.AuditedVersion + "; checking the persistent world upgrade backup record before enabling features.");
        }
        catch (Exception error) { Block(error); Dispose(); }
    }

    private static void BeforeWorldLoad()
    {
        var current = _current;
        if (current == null) return;
        // The native adapter is invoked inside the guarded action, including JIT/member-resolution failures.
        current.Capture(current.BackupWorld);
    }

    internal void Capture(Action backup)
    {
        if (Ready || _attempted || Failed) return;
        _attempted = true;
        try { backup(); Ready = true; }
        catch (Exception error) { Block(error); }
    }

    private void Block(Exception error)
    {
        Ready = false; Failed = true;
        DiagnosticLog.Write(_logger, "HeimKit", CoreLogTopics.Lifecycle,
            "The required world upgrade backup or its persistent record could not be verified; all HeimKit features remain inactive for this startup. Vanilla server loading continues.",
            level: LogLevel.Error, error: error);
    }

    private void BackupWorld()
    {
        var net = ZNet.instance;
        var world = ZNet.World;
        if (net == null || !net.IsServer() || !net.IsDedicated() || world == null)
            throw new InvalidOperationException("No selected dedicated-server world is available for backup.");
        if (world.m_fileSource != FileHelpers.FileSource.Local)
            throw new NotSupportedException("Startup backup requires local dedicated-server world storage.");
        var identity = Identify(world);
        string heimKitVersion = identity.HeimKitVersion;
        string directory = Path.Combine(_configDirectory, "HeimKit", "Backups");
        var result = WorldUpgradeBackup.Ensure(directory, identity, request =>
        {
            DiagnosticLog.Write(_logger, "HeimKit", request.Reason + " Creating the world checkpoint for world " + identity.WorldUid +
                ", HeimKit " + heimKitVersion + " and Valheim " + _gameVersion + ".", level: request.Replacing ? LogLevel.Warning : LogLevel.Info);
            return CaptureWorldArchive(world, directory, request);
        });
        string outcome = result.Created ? "Created and verified a world backup for" : result.Recovered ? "Recovered the completed backup and finalized its record for" : "Verified the existing backup for";
        DiagnosticLog.Write(_logger, "HeimKit", outcome + " world " + identity.WorldUid + ", HeimKit " + heimKitVersion +
            " and Valheim " + _gameVersion + ". Captured UTC: " + result.CapturedUtc + ". Archive: " + result.ArchivePath +
            ". Record: " + result.RecordPath + "." + (result.Created ? "" : " No new world backup was created."));
        _identity = identity; _backup = result; _selectedWorld = world;
    }

    private WorldUpgradeBackup.Identity Identify(global::World world)
    {
        if (world.m_fileSource != FileHelpers.FileSource.Local)
            throw new NotSupportedException("Persistent feature protection requires local dedicated-server world storage.");
        string build = typeof(UpgradeStartup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("HeimKit build identity is unavailable for persistent feature protection.");
        return new WorldUpgradeBackup.Identity(world.m_uid, world.GetSaveDirectory(FileHelpers.FileSource.Local), build, _gameVersion, GameVersionCompatibility.AuditedVersion);
    }

    internal bool PrepareFailureProtection(ModuleFailureProtection protection)
    {
        if (!Ready) return false;
        var net = ZNet.instance;
        var world = ZNet.World;
        if (net == null || !net.IsServer() || !net.IsDedicated() || world == null) return false;
        if (_selectedWorld != null && !ReferenceEquals(_selectedWorld, world))
            throw new InvalidOperationException("The selected world changed after startup protection was established.");
        if (_protectionAttached) return true;
        _selectedWorld = world;
        _identity ??= Identify(world);
        protection.Attach(Path.Combine(_configDirectory, "HeimKit", "Backups"), _identity, _backup);
        _protectionAttached = true;
        return true;
    }

    private string CaptureWorldArchive(global::World world, string directory, WorldUpgradeBackup.Request request)
    {
        var paths = world.GetSavePaths();
        bool chunked = world.IsChunkedSave();
        if (paths.Count != (chunked ? 1 : 2)) throw new InvalidDataException("Unrecognized native world save layout.");
        if (chunked)
        {
            string folder = Path.GetFullPath(paths[0]).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string worldMetadata = Path.GetFullPath(world.GetSaveFWLPath());
            if (!worldMetadata.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(worldMetadata))
                throw new FileNotFoundException("The selected chunked world's metadata is missing or outside its save directory.");
        }
        var sources = new List<StartupWorldBackup.Source>();
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(full);
            if (name.Length == 0) throw new InvalidDataException("World save path has no world-specific name.");
            sources.Add(new StartupWorldBackup.Source(full, "world/" + name, chunked));
        }
        sources.Add(new StartupWorldBackup.Source(Path.Combine(_configDirectory, "heimkit.cfg"), "configuration/heimkit.cfg", false));
        foreach (string name in new[] { "SharedMap", "ForestRenewal" })
            sources.Add(new StartupWorldBackup.Source(Path.Combine(_configDirectory, "HeimKit", name), "configuration/HeimKit/" + name, true, optional: true));
        return StartupWorldBackup.Create(directory, sources, request.Metadata, request.ArchiveName, request.RecordVerifiedArchive);
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        if (!_hooked) return;
        _hooked = false;
        _hooks.UnpatchSelf();
    }
}
