using System;
using System.IO;
using HeimKit.Core.Ownership;
using HeimKit.Core.Native;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;

namespace HeimKit.Features.HeimDefender;
internal sealed class HeimDefender : IServerModule, IModuleSwitches, IModuleNumericSettings
{
    private readonly HeimDefenderSettings _settings;
    private readonly ManualLogSource _log;
    private readonly PlayerServices _players;
    private readonly GameVersionCompatibility _compatibility;
    private readonly PlayerProcessingNotices _notices;
    private InitialStaticRevision? _ownershipHook;
    private IDisposable? _subscription;
    private HeimDefenderController? _controller;
    private ZDOMan? _world;
    private bool _occupied, _eligible, _restore;
    public string Name => "HeimDefender";
    public bool IsActive => _eligible && _settings.Enabled.Value;
    public IReadOnlyList<SwitchBinding> Switches { get; }
    public IReadOnlyList<NumericSettingBinding> NumericSettings { get; }
    public SwitchApplyMode ApplyMode => SwitchApplyMode.SelfReconciling;

    internal HeimDefender(ConfigFile file, ManualLogSource log, PlayerServices players)
    {
        _settings = new(file, log);
        _log = log;
        _players = players;
        _compatibility = GameVersionCompatibility.Resolve(file, log);
        _notices = new(log, Name, () => _settings.Debug.Enabled);
        NumericSettings = new[]
        {
            NumericSettingBinding.Create("Bolt type", _settings.BoltType,
                _settings.BoltType.Description.Description, () => "Applies to the next reload."),
            NumericSettingBinding.Create("Bolts per reload", _settings.BoltsPerReload,
                "Target bolt count after a free refill (1 to 40), capped by magazine capacity.",
                () => "Applies to the next reload.")
        };
        Switches = new SwitchBinding[]
        {
            new("Enabled", _settings.Enabled),
            new("Free bolts", _settings.FreeWoodBolts),
            new("Debug logging", _settings.DebugMode, applyLive: true)
        };
    }

    public void Initialize(string gameVersion)
    {
        Shutdown();
        _eligible = _compatibility.Supports(gameVersion) && ModuleFailureProtection.MayStart(Name);
        DiagnosticLog.Write(_log, Name, !_eligible ? "Disabled for this game version or failure state." : IsActive ? "Enabled." : "Disabled.");
        _settings.Debug.ReportStartup(_log, Name, IsActive);
        if (!_eligible)
            return;
        try
        {
            _ownershipHook = new(HeimDefenderBallistaPolicy.Eligible);
            _subscription = _players.Events.Subscribe(Presence, Fail, () => _notices.IsEmptyPaused, () => _notices.IsRunning, () => _notices.IsServerReady);
        }
        catch (Exception error) { Fail(error); }
    }

    private void Presence(bool? occupied)
    {
        _occupied = occupied == true;
        if (_occupied)
            return;
        Stop();
        if (occupied.HasValue)
            _notices.EmptyPaused();
        else
            _notices.Unavailable("player presence unavailable");
    }

    public void Tick(float now)
    {
        if (!_eligible)
            return;
        if (WorldServices.World)
            _notices.ServerReady();
        if (!_occupied || !WorldObjectProvider.Current.Ready)
        {
            Stop();
            return;
        }

        if (!ReferenceEquals(_world, ZDOMan.instance))
        {
            Stop();
            _world = ZDOMan.instance;
            _restore = File.Exists(MarkerPath());
        }

        if (!IsActive && !_restore)
            return;
        if (IsActive && !_restore)
        {
            Directory.CreateDirectory(_settings.Directory);
            File.WriteAllText(MarkerPath(), "1");
            _restore = true;
        }

        _controller ??= new HeimDefenderController(_players, _settings, _log, Fail);
        _notices.Ready();
        _controller.Tick(now);
    }

    private string MarkerPath() => Path.Combine(_settings.Directory, $"world-{ZNet.instance.GetWorldUID()}.managed");
    private void Fail(Exception error)
    {
        ModuleFailureProtection.Record(Name, error, "applying HeimDefender settings");
        DiagnosticLog.Write(_log, Name, CoreLogTopics.Lifecycle, "HeimDefender processing stopped.", level: LogLevel.Error, error: error);
        Shutdown();
    }

    private void Stop()
    {
        var old = _controller;
        _controller = null;
        _world = null;
        old?.Dispose();
    }

    public void Shutdown()
    {
        _eligible = _occupied = false;
        _ownershipHook?.Dispose();
        _ownershipHook = null;
        _subscription?.Dispose();
        _subscription = null;
        Stop();
        _notices.Reset();
    }
}
