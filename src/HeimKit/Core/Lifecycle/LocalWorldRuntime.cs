using System;
using System.Globalization;
using System.IO;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Control.Server;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Players;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Features.WeatherGuard;

namespace HeimKit.Core.Lifecycle;

internal sealed class LocalWorldRuntime : IDisposable
{
    private const double HostedFrameBudgetMilliseconds = 1.5;
    private readonly string _configDirectory, _version;
    private readonly ManualLogSource _log;
    private Game? _game;
    private ZNet? _net;
    private ZDOMan? _records;
    private global::World? _selectedWorld;
    private ModuleHost? _host;
    private PlayerServices? _players;
    private WorldObjectProvider? _world;
    private ModuleFailureProtection? _protection;
    private ControlServer? _control;
    private bool _attempted;
    internal string Status { get; private set; } = "Waiting for local world";
    internal ControlServer? Control => CanEdit() ? _control : null;

    internal LocalWorldRuntime(string configDirectory, string version, ManualLogSource log)
    {
        _configDirectory = configDirectory;
        _version = version;
        _log = log;
    }

    internal void Tick(float now)
    {
        var game = Game.instance;
        var net = ZNet.instance;
        var records = ZDOMan.instance;
        var selected = ZNet.World;
        if (game == null || net == null || records == null || selected == null ||
            !WorldSession.IsPlayerHosted(WorldSession.Identify(net)))
        {
            if (_selectedWorld != null) Dispose();
            return;
        }
        if (!ReferenceEquals(_game, game) || !ReferenceEquals(_net, net) ||
            !ReferenceEquals(_records, records) || !ReferenceEquals(_selectedWorld, selected))
        {
            Dispose();
            _game = game;
            _net = net;
            _records = records;
            _selectedWorld = selected;
        }
        if (!_attempted)
        {
            if (Player.m_localPlayer == null || ZNetScene.instance == null || ZoneSystem.instance == null ||
                ZRoutedRpc.instance == null) return;
            _attempted = true;
            try { Start(selected); }
            catch (Exception error) { StopAfterFailure(error); }
        }
        if (_host == null) return;
        try
        {
            _protection!.ThrowIfFailed();
            long measured = ServerPerformance.BeginUpdate();
            ModuleScheduler.Current.BeginFrame(now);
            using (PerformanceStages.Measure("HeimKit", "world_services")) WorldServices.Refresh(playerHosted: true);
            using (PerformanceStages.Measure("HeimKit", "player_services")) _players!.Tick(now);
            using (PerformanceStages.Measure("HeimKit", "world_preparation")) _world!.Tick(now, playerHosted: true);
            using (PerformanceStages.Measure("HeimKit", "feature_dispatch")) _host.Tick(now, _players.Presence.HasJoinedPlayers, beginFrame: false);
            _protection.ThrowIfFailed();
            using (PerformanceStages.Measure("HeimKit", "player_reporting")) _players.Events.ReportProcessing();
            ServerPerformance.EndUpdate(measured, now, _log, _players.Presence.JoinedCount, _world.FrameRecords,
                ZNetScene.instance == null ? 0 : ZNetScene.instance.NrOfInstances(), WeatherGuardController.Active?.RegionCount ?? 0);
        }
        catch (Exception error) { StopAfterFailure(error); }
    }

    private bool CanEdit() => _host != null && _selectedWorld != null && ReferenceEquals(_selectedWorld, ZNet.World) &&
        ReferenceEquals(_game, Game.instance) && ReferenceEquals(_net, ZNet.instance) &&
        ReferenceEquals(_records, ZDOMan.instance) && Player.m_localPlayer != null &&
        WorldSession.IsPlayerHosted(WorldSession.Identify(_net));

    private void Start(global::World selected)
    {
        DiagnosticLog.StartSession();
        var directory = Path.Combine(_configDirectory, "HeimKit", "Worlds", selected.m_uid.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        _protection = new ModuleFailureProtection(_log);
        _host = new ModuleHost(_log);
        ControlSettings? controls = null;
        var configuration = ConfigurationLoader.Load(directory, _log, config =>
        {
            GameVersionCompatibility.Resolve(config, _log);
            controls = new ControlSettings(config);
            _players = new PlayerServices(config, _log, playerHosted: true);
            _host.AttachPresence(_players.Events);
            GameplayModules.Register(_host, config, _log, _players);
            return config;
        });
        string gameVersion = global::Version.CurrentVersion.ToString();
        var policy = GameVersionCompatibility.Resolve(configuration, _log);
        // Local worlds use Valheim's native save lifecycle. Module failures are
        // isolated for this session without opening the world's save files.
        _world = new WorldObjectProvider(_players!.Presence, logger: _log);
        _host.Initialize(gameVersion);
        ModuleScheduler.Current.FrameBudgetMilliseconds = HostedFrameBudgetMilliseconds;
        _protection.ThrowIfFailed();
        _control = new ControlServer(controls!, _host, _players, _log, _version, policy, gameVersion, CanEdit);
        Status = "Local world";
    }

    private void StopAfterFailure(Exception error)
    {
        Stop();
        Status = "Local startup failed";
        DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Lifecycle, "Local gameplay stopped.", level: LogLevel.Error, error: error);
    }

    private void Stop()
    {
        bool ownedServices = _host != null;
        ModuleCleanup.Run(_log, "HeimKit", () => _control?.Dispose(), () => _host?.Dispose(),
            () => _world?.Dispose(), () => _players?.Dispose(), () => _protection?.Dispose());
        _control = null;
        _host = null;
        _world = null;
        _players = null;
        _protection = null;
        if (ownedServices)
        {
            ServerPerformance.Reset();
            WorldServices.Refresh();
            PrefabRegistry.Current.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
        _game = null;
        _net = null;
        _records = null;
        _selectedWorld = null;
        _attempted = false;
        Status = "Waiting for local world";
    }
}
