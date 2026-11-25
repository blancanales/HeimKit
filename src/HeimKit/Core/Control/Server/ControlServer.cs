using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Configuration;
using HeimKit.Core.Control.Models;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Ownership;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;

namespace HeimKit.Core.Control.Server;

internal sealed partial class ControlServer : IDisposable
{
    private static readonly int HelloHash = ControlProtocol.Hello.GetStableHashCode();
    private static readonly int SetHash = ControlProtocol.Set.GetStableHashCode();
    private readonly ControlSettings _settings;
    private readonly Func<bool>? _localAccess;
    private readonly Client? _localClient;
    private readonly ModuleHost _host;
    private readonly PlayerServices _players;
    private readonly ManualLogSource _log;
    private readonly string _version;
    private readonly GameVersionCompatibility _compatibility;
    private readonly string _gameVersion;
    private readonly Dictionary<ZRpc, Client> _clients = new();
    private readonly Queue<Request> _queue = new(64);
    private readonly List<ZRpc> _departed = new();
    private readonly List<Module> _modules = new();
    private IDisposable? _lease;
    private ZDOMan? _world;
    private ZNet? _net;
    private long _worldUid;
    private float _next;
    private readonly WarningIncident _malformed = new(), _denied = new();
    private bool _stopped, _dirty, _lastChangesAllowed;
    private long _revision;

    private sealed class Client
    {
        internal ZNetPeer Peer = null!;
        internal bool IsLocal;
        internal readonly ModerationDelivery Delivery = new();
        internal int Protocol, Id;
        internal SetResult Result;
        internal bool Admin, Refresh;
        internal readonly float[] Requests = new float[10];
        internal int Count, Next;
    }

    private sealed class Module
    {
        internal IServerModule Server = null!;
        internal readonly List<Entry> Entries = new();
        internal readonly List<NumericEntry> Numbers = new();
        internal bool Active, Allowed;
        internal Pending? Change;
    }

    private sealed class Entry
    {
        internal SwitchBinding Binding = null!;
        internal SwitchDefinition Definition;
        internal bool Value;
    }

    private sealed class NumericEntry
    {
        internal readonly NumericSettingBinding Binding;
        private readonly string _description;
        internal NumericSettingState State;

        internal NumericEntry(string module, NumericSettingBinding binding)
        {
            Binding = binding;
            string suffix = " Default: " + Convert.ToString(binding.Entry.DefaultValue, CultureInfo.InvariantCulture) + ".";
            _description = binding.Description.Substring(0, Math.Min(binding.Description.Length, 200 - suffix.Length)) + suffix;
            State = Read(module);
        }

        internal NumericSettingState Read(string module) => new(module, Binding.Entry.Definition.Key,
            Binding.Label, _description, Binding.Applies(), Binding.Whole,
            Binding.Minimum, Binding.Maximum, Binding.Value);
    }

    private sealed class Pending
    {
        internal Client Client = null!;
        internal Entry Entry = null!;
        internal int Id;
        internal PendingChange? Hold;
        internal bool Value;
    }

    private static ControlServer? _identification;
    private Harmony? _identificationHooks;

    internal void InstallIdentification()
    {
        var target = AccessTools.Method(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) });
        var hooks = new Harmony("heimkit.client_identification.server");
        try
        {
            hooks.Patch(target, postfix: new HarmonyMethod(typeof(ControlServer), nameof(RegisterIdentification)));
            _identificationHooks = hooks;
            _identification = this;
        }
        catch { hooks.UnpatchSelf(); throw; }
    }

    private static void RegisterIdentification(ZNetPeer peer)
    {
        if (_identification == null) return;
        peer.m_rpc.Register<ZPackage>(ControlProtocol.Identify, (rpc, payload) => _identification?.Identify(peer, rpc, payload));
    }

    private void Identify(ZNetPeer peer, ZRpc rpc, ZPackage payload)
    {
        if (_stopped || !ReferenceEquals(peer.m_rpc, rpc) || !peer.IsReady() ||
            !ControlProtocol.TryReadHello(payload, out var hello)) return;
        if (_settings.Enabled.Value)
            _players.Connections.ConfirmHeimKit(peer, hello.Version, hello.Protocol == ControlProtocol.Version);
        else
            rpc.Invoke(ControlProtocol.Disabled, ControlProtocol.WriteHello(_version));
        rpc.Unregister(ControlProtocol.Identify);
    }

    internal bool DetectsClients => !_stopped && _settings.Enabled.Value;

    private readonly struct Request
    {
        internal Request(ZNetPeer? peer, int protocol, int id, string? module, string? key, bool value, string? clientVersion = null, double? number = null)
        {
            Peer = peer; Protocol = protocol; Id = id; Module = module; Key = key; Value = value; ClientVersion = clientVersion; Number = number;
        }

        internal readonly ZNetPeer? Peer;
        internal readonly int Protocol, Id;
        internal readonly string? Module, Key, ClientVersion;
        internal readonly bool Value;
        internal readonly double? Number;
    }

    internal ControlServer(ControlSettings settings, ModuleHost host, PlayerServices players,
        ManualLogSource log, string version, GameVersionCompatibility compatibility, string gameVersion, Func<bool>? localAccess = null)
    {
        _settings = settings; _host = host; _players = players; _log = log; _version = version;
        _compatibility = compatibility; _gameVersion = gameVersion;
        _localAccess = localAccess;
        if (localAccess != null) _localClient = new Client { IsLocal = true, Admin = true };
        foreach (var server in host.Modules)
        {
            if (server is not IModuleSwitches switches) continue;
            var module = new Module { Server = server };
            foreach (var binding in switches.Switches)
            {
                var entry = binding.Entry;
                string description = entry.Description.Description
                    .Replace("File edits require a restart.", "")
                    .Replace("HeimKit Settings applies changes live.", "")
                    .Replace("Restart after edits.", "")
                    .Replace("Reloads during play.", "").Trim();
                if (binding.ApplyLive)
                    description = "Enable this feature's server debug logging. Global DebugMode can still enable logging when this is off. Server suppression and log filters still apply.";
                if (description.Length > 200) description = description.Substring(0, 200);
                module.Entries.Add(new Entry
                {
                    Binding = binding,
                    Definition = new SwitchDefinition(server.Name, entry.Definition.Key, binding.Label, description, module.Entries.Count == 0)
                });
            }
            if (server is IModuleNumericSettings numeric)
                foreach (var binding in numeric.NumericSettings)
                    module.Numbers.Add(new NumericEntry(server.Name, binding));
            _modules.Add(module);
        }
    }

    private void Observe(ZRpc rpc, ZPackage packet)
    {
        if (!IncomingPackets.TryHeader(packet, out var header, out var error) || error != null ||
            (header.Method != HelloHash && header.Method != SetHash)) return;
        if (_queue.Count >= 64 || !header.Target.IsNone() || header.TargetPeer != ZNet.GetUID()) return;
        var peer = PlayerPeerLookup.Ready(_players.Presence.Peers, rpc);
        if (peer == null || peer.m_uid != header.Sender) return;
        if (!ControlProtocol.TryReadRoutedPayload(packet, header.End, out var payload))
        {
            _queue.Enqueue(default);
            return;
        }
        if (header.Method == HelloHash && ControlProtocol.TryReadHello(payload, out var hello))
            _queue.Enqueue(new Request(peer, hello.Protocol, 0, null, null, false, hello.Version));
        else if (header.Method == SetHash && ControlProtocol.TryReadSet(payload, out var set))
            _queue.Enqueue(new Request(peer, 0, set.Id, set.Module, set.Key, set.Value));
        else if (header.Method == SetHash && ControlProtocol.TryReadNumericSet(payload, out var number))
            _queue.Enqueue(new Request(peer, 0, number.Id, number.Module, number.Key, false, number: number.Value));
        else _queue.Enqueue(default);
    }

    internal void Tick(float now)
    {
        if (_stopped || now < _next) return;
        _next = now + 1;
        using var timing = PerformanceStages.Measure("HeimKit", "control");
        try { Process(now); }
        catch (Exception error) { Fail(error); }
    }

    private void Process(float now)
    {
        var net = ZNet.instance;
        long worldUid = net == null || ZDOMan.instance == null ? 0 : net.GetWorldUID();
        if (!ReferenceEquals(_world, ZDOMan.instance) || !ReferenceEquals(_net, net) || _worldUid != worldUid)
        {
            Reset();
            _net = net;
            _world = ZDOMan.instance;
            _worldUid = worldUid;
        }
        if (net == null || !_settings.Enabled.Value || _players.Presence.HasJoinedPlayers == false)
        {
            if (!_settings.Enabled.Value)
                foreach (var client in _clients.Values)
                    if (Ready(client.Peer)) client.Peer.m_rpc.Invoke(ControlProtocol.Disabled, ControlProtocol.WriteHello(_version));
            if (_lease != null) Reset();
            return;
        }
        if (_players.Presence.HasJoinedPlayers == true)
            _lease ??= _players.RoutedMessages.Subscribe(Observe, Fail);
        if (_lease == null || !_players.Presence.HasJoinedPlayers.HasValue) return;

        foreach (var pair in _clients)
        {
            var client = pair.Value;
            if (!Ready(client.Peer))
            {
                _departed.Add(pair.Key);
                continue;
            }
            bool admin = IsAdmin(client.Peer);
            if (client.Admin != admin) { client.Admin = admin; client.Refresh = true; }
        }
        foreach (var rpc in _departed)
        {
            rpc.Unregister(ModerationProtocol.Action);
            _clients.Remove(rpc);
        }
        _departed.Clear();

        foreach (var module in _modules) Complete(module, now);
        while (_queue.Count != 0)
        {
            var request = _queue.Dequeue();
            if (request.Peer == null) { Warn(now, _malformed, "Rejected an invalid control package."); continue; }
            if (!Ready(request.Peer)) continue;
            if (request.Id == 0) Hello(request);
            else if (_clients.TryGetValue(request.Peer.m_rpc, out var client) && client.Protocol == ControlProtocol.Version)
                Set(client, request, now);
        }

        if (_clients.Count == 0) return;
        bool changesAllowed = _settings.AllowAdminChanges.Value;
        RefreshCatalog(changesAllowed);
        SwitchState[]? switches = null;
        NumericSettingState[]? numbers = null;
        foreach (var client in _clients.Values)
        {
            if ((!client.Refresh && (!_dirty || client.Protocol != ControlProtocol.Version)) || !Ready(client.Peer)) continue;
            client.Refresh = false;
            var state = new ControlSnapshot { Protocol = ControlProtocol.Version, ServerVersion = _version };
            bool mismatch = client.Protocol != ControlProtocol.Version;
            if (!mismatch)
            {
                state.Revision = _revision;
                state.IsAdmin = client.Admin;
                state.AdminChangesAllowed = changesAllowed;
                state.RequestId = client.Id;
                state.Result = client.Result;
                state.Switches = switches ??= Snapshot();
                state.Numbers = numbers ??= NumericSnapshot();
            }
            client.Peer.m_rpc.Invoke(ControlProtocol.State, ControlProtocol.WriteState(state, mismatch));
        }
        foreach (var client in _clients.Values) SendModeration(client);
        _dirty = false;
    }

    internal ControlSnapshot? ReadLocal(float now, long knownRevision)
    {
        if (_stopped || _localClient == null || _localAccess?.Invoke() != true) return null;
        foreach (var module in _modules) Complete(module, now);
        RefreshCatalog(true);
        if (knownRevision == _revision && !_localClient.Refresh) return null;
        _dirty = false;
        _localClient.Refresh = false;
        return new ControlSnapshot
        {
            Protocol = ControlProtocol.Version, ServerVersion = _version, Revision = _revision,
            IsAdmin = true, AdminChangesAllowed = true, RequestId = _localClient.Id, Result = _localClient.Result,
            Switches = Snapshot(), Numbers = NumericSnapshot()
        };
    }

    internal bool SetLocal(int id, string module, string key, bool value, double? number, float now)
    {
        if (_stopped || _localClient == null || _localAccess?.Invoke() != true) return false;
        Set(_localClient, new Request(null, ControlProtocol.Version, id, module, key, value, number: number), now);
        return true;
    }

    private void RefreshCatalog(bool changesAllowed)
    {
        _dirty |= changesAllowed != _lastChangesAllowed;
        _lastChangesAllowed = changesAllowed;
        foreach (var module in _modules)
        {
            bool active = module.Server.IsActive, allowed = ModuleFailureProtection.MayStart(module.Server.Name);
            _dirty |= active != module.Active || allowed != module.Allowed;
            module.Active = active;
            module.Allowed = allowed;
            foreach (var entry in module.Entries)
            {
                bool value = entry.Binding.Entry.Value;
                _dirty |= entry.Value != value;
                entry.Value = value;
            }
            foreach (var entry in module.Numbers)
            {
                var state = entry.Read(module.Server.Name);
                _dirty |= state.Value != entry.State.Value || state.Applies != entry.State.Applies;
                entry.State = state;
            }
        }

        if (_dirty) _revision++;
    }

    private bool Ready(ZNetPeer peer) => peer.m_rpc.IsConnected() && ReferenceEquals(PlayerPeerLookup.Ready(_players.Presence.Peers, peer.m_rpc), peer);
    private static bool IsAdmin(ZNetPeer peer) => ZNet.instance.IsAdmin(peer.m_rpc.GetSocket().GetHostName());

    private void Hello(Request request)
    {
        var peer = request.Peer!;
        _players.Connections.ConfirmHeimKit(peer, request.ClientVersion!, request.Protocol == ControlProtocol.Version);
        if (!_clients.TryGetValue(peer.m_rpc, out var client))
        {
            if (_clients.Count >= 256) return;
            _clients.Add(peer.m_rpc, client = new Client { Peer = peer });
            peer.m_rpc.Register<ZPackage>(ModerationProtocol.Action, ReceiveModeration);
        }
        client.Protocol = request.Protocol;
        if (DiagnosticsSettings.VerboseEnabled && DiagnosticLog.DebugAvailable(_log, "HeimKit", verbose: true))
            DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control, "Accepted client handshake", new (string, object?)[]
            { ("peer", peer.m_uid), ("protocol", request.Protocol) }, LogLevel.Debug, verbose: true);
        client.Admin = IsAdmin(peer);
        client.Refresh = true;
    }

    private void Set(Client client, Request request, float now)
    {
        if (!client.IsLocal && _settings.AllowAdminChanges.Value) client.Admin = IsAdmin(client.Peer);
        var result = client.IsLocal ? SetResult.None : ControlRequestRules.Authorize(_settings.AllowAdminChanges.Value, client.Admin);
        if (result == SetResult.NotAdmin) Warn(now, _denied, "Rejected a control request from a player outside the admin list.");
        if (result == SetResult.None && !ControlRequestRules.Spend(now, client.Requests, ref client.Count, ref client.Next))
            result = SetResult.RateLimited;
        if (result != SetResult.None) { Reply(client, request.Id, result); return; }

        var module = _modules.Find(m => m.Server.Name == request.Module);
        if (request.Number is double number)
        {
            result = SetNumber(client, module, request.Key, number);
            Reply(client, request.Id, result, request.Module, request.Key);
            return;
        }

        var entry = module?.Entries.Find(e => e.Definition.Key == request.Key);
        result = ControlRequestRules.Validate(entry != null, module == null || ModuleFailureProtection.MayStart(module.Server.Name),
            module?.Change != null && entry?.Binding.ApplyLive != true, entry?.Binding.Entry.Value == true, request.Value);
        if (result == SetResult.None && module != null && entry != null)
        {
            var change = new Pending { Client = client, Entry = entry, Id = request.Id, Value = request.Value };
            if (!entry.Binding.ApplyLive && module.Server.IsActive && module.Server is IModuleIdle gate && !gate.Idle)
            {
                change.Hold = new PendingChange(gate, now);
                module.Change = change;
                _dirty = true;
                result = SetResult.Pending;
            }
            else result = Apply(module, change);
        }
        Reply(client, request.Id, result, request.Module, request.Key);
    }

    private SetResult SetNumber(Client client, Module? module, string? key, double value)
    {
        if (module == null) return SetResult.UnknownSwitch;
        var entry = module.Numbers.Find(e => e.State.Key == key);
        if (entry == null) return SetResult.UnknownSwitch;
        if (!ModuleFailureProtection.MayStart(module.Server.Name)) return SetResult.Unavailable;
        if (!entry.State.AcceptsWhole(value)) return SetResult.InvalidValue;

        var binding = entry.Binding;
        var previous = binding.Entry.BoxedValue;
        var requested = binding.Box(value);
        if (Equals(previous, requested)) return SetResult.Applied;

        try { binding.Entry.BoxedValue = requested; }
        catch (Exception error)
        {
            var config = binding.Entry.ConfigFile;
            bool save = config.SaveOnConfigSet;
            try
            {
                config.SaveOnConfigSet = false;
                binding.Entry.BoxedValue = previous;
            }
            finally { config.SaveOnConfigSet = save; }
            DiagnosticLog.Write(_log, module.Server.Name, CoreLogTopics.Control,
                "Could not save the numeric setting.", level: LogLevel.Error, error: error);
            return SetResult.Failed;
        }

        binding.OnChanged?.Invoke();
        DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control,
            $"{DescribeAdmin(client)} set {module.Server.Name}.{key} to {binding.Value.ToString(CultureInfo.InvariantCulture)}.");
        _dirty = true;
        return SetResult.Applied;
    }

    private NumericSettingState[] NumericSnapshot()
    {
        var entries = new List<NumericSettingState>();
        foreach (var module in _modules)
        foreach (var entry in module.Numbers)
            entries.Add(entry.State);
        return entries.ToArray();
    }

    private void Complete(Module module, float now)
    {
        var change = module.Change;
        if (change == null) return;
        bool available = module.Server.IsActive && ModuleFailureProtection.MayStart(module.Server.Name);
        var result = change.Hold!.Poll(now, available, () =>
        {
            var authorization = change.Client.IsLocal
                ? (_localAccess?.Invoke() == true ? SetResult.None : SetResult.Unavailable)
                : ControlRequestRules.Authorize(_settings.AllowAdminChanges.Value,
                    Ready(change.Client.Peer) && IsAdmin(change.Client.Peer));
            return authorization == SetResult.None ? Apply(module, change) : authorization;
        });
        if (result == SetResult.Pending) return;
        module.Change = null;
        _dirty = true;
        Reply(change.Client, change.Id, result, module.Server.Name, change.Entry.Definition.Key);
    }

    private SetResult Apply(Module module, Pending change)
    {
        var binding = change.Entry.Binding;
        if (binding.ApplyLive)
        {
            try { binding.Entry.Value = change.Value; }
            catch (Exception error)
            {
                DiagnosticLog.Write(_log, module.Server.Name, CoreLogTopics.Control,
                    "Could not apply the admin debug setting.", level: LogLevel.Error, error: error);
                return SetResult.Failed;
            }
        }
        else if (!_host.Reapply(module.Server.Name, () => binding.Entry.Value = change.Value, binding)) return SetResult.Failed;
        DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control,
            $"{DescribeAdmin(change.Client)} set {module.Server.Name}.{change.Entry.Definition.Key} to {change.Value}.");
        _dirty = true;
        return SetResult.Applied;
    }

    private static string DescribeAdmin(Client client) => client.IsLocal
        ? "Local host"
        : $"Admin {client.Peer.m_playerName} ({client.Peer.m_rpc.GetSocket().GetHostName()})";

    private void Reply(Client client, int id, SetResult result, string? module = null, string? key = null)
    {
        client.Id = id; client.Result = result; client.Refresh = true;
        if (result == SetResult.Pending || result == SetResult.Busy)
            DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control,
                $"Admin setting {module}.{key} from {DescribeAdmin(client)} (request {id}) is {result}.");
    }

    private SwitchState[] Snapshot()
    {
        var entries = new List<SwitchState>();
        foreach (var module in _modules)
        foreach (var entry in module.Entries)
        {
            SwitchStatus status = SwitchStatus.Off;
            UnavailableReason reason = UnavailableReason.None;
            if (entry.Definition.IsModuleSwitch && (entry.Value || !module.Allowed))
            {
                status = module.Active && module.Allowed ? SwitchStatus.On : SwitchStatus.Unavailable;
                if (status == SwitchStatus.Unavailable)
                    reason = !module.Allowed ? UnavailableReason.FailureProtection : !_compatibility.Supports(_gameVersion)
                        ? UnavailableReason.UnsupportedGameVersion : UnavailableReason.StoppedAfterError;
            }
            var pending = module.Change;
            entries.Add(new SwitchState(entry.Definition, entry.Value,
                pending?.Entry == entry ? pending.Value : (bool?)null, status, reason));
        }
        return entries.ToArray();
    }

    private sealed class WarningIncident
    {
        internal float Next;
        internal long Suppressed;
    }

    private void Warn(float now, WarningIncident incident, string message)
    {
        if (now < incident.Next) { incident.Suppressed++; return; }
        if (incident.Suppressed != 0) message += $" Suppressed {incident.Suppressed} similar requests.";
        incident.Next = now + 300;
        incident.Suppressed = 0;
        DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control, message, level: LogLevel.Warning);
    }

    private void Reset()
    {
        var lease = _lease; _lease = null;
        try { lease?.Dispose(); }
        finally
        {
            foreach (var module in _modules)
            {
                var hold = module.Change?.Hold;
                module.Change = null;
                if (hold != null) ModuleCleanup.Run(_log, "HeimKit", hold.Dispose);
            }
            _queue.Clear();
            foreach (var rpc in _clients.Keys) rpc.Unregister(ModerationProtocol.Action);
            _clients.Clear();
            _departed.Clear();
        }
    }

    private void Fail(Exception error)
    {
        if (_stopped) return;
        _stopped = true;
        ModuleCleanup.Run(_log, "HeimKit", Dispose);
        DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control, "HeimKit Settings server controls stopped after an error.", level: LogLevel.Error, error: error);
    }

    public void Dispose()
    {
        _stopped = true;
        if (ReferenceEquals(_identification, this)) _identification = null;
        try { _identificationHooks?.UnpatchSelf(); }
        finally { Reset(); }
    }
}
