using System;
using BepInEx.Logging;
using HarmonyLib;
using HeimKit.Core.Control;
using HeimKit.Core.Control.Models;
using HeimKit.Core.Control.Server;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.World;
using HeimKit.Core.Diagnostics;
using UnityEngine;

namespace HeimKit.Client;

internal sealed partial class ClientLink : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly LocalWorldRuntime? _local;
    private ControlServer? _localControl;
    internal string LocalStatus { get; private set; } = "";
    private readonly string _version;
    private Game? _game;
    private global::World? _sessionWorld;
    private ZNet? _net;
    private ZRpc? _rpc;
    private long _peer;
    private int _attempts, _lastId, _request;
    private float _next, _nextHello, _deadline, _nextWarning;
    internal LinkState State { get; private set; }
    internal ControlSnapshot? Snapshot { get; private set; }
    internal string? RequestModule { get; private set; }
    internal string? RequestKey { get; private set; }
    internal SetResult Result { get; private set; }
    internal bool TimedOut { get; private set; }
    internal bool Waiting => _request != 0;
    internal event Action? Changed;

    private static ClientLink? _current;
    private readonly Harmony _identification = new("heimkit.client_identification.client");
    private ZRpc? _identified;
    private string? _disabledVersion;
    internal string? DisabledServerVersion => ReferenceEquals(_rpc, _identified) ? _disabledVersion : null;

    internal ClientLink(string version, ManualLogSource log, LocalWorldRuntime? local = null)
    {
        _version = version; _log = log; _local = local;
        try
        {
            _identification.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.SetCharacterID)),
                prefix: new HarmonyMethod(typeof(ClientLink), nameof(BeforeCharacterId)));
            _current = this;
        }
        catch (Exception error)
        {
            _identification.UnpatchSelf();
            DiagnosticLog.Write(log, "HeimKit", CoreLogTopics.Control, "Early client identification is unavailable.",
                level: LogLevel.Warning, error: error);
        }
    }

    private static void BeforeCharacterId(ZNet __instance, ZDOID id)
    {
        if (id.IsNone() || __instance.IsServer() || _current is not { } link) return;
        var peers = __instance.GetPeers();
        var rpc = peers.Count == 0 ? null : peers[0].m_rpc;
        if (rpc == null || ReferenceEquals(link._identified, rpc)) return;
        try
        {
            link._identified?.Unregister(ControlProtocol.Disabled);
            link._identified = rpc;
            link._disabledVersion = null;
            rpc.Register<ZPackage>(ControlProtocol.Disabled, link.ReceiveDisabled);
            rpc.Invoke(ControlProtocol.Identify, ControlProtocol.WriteHello(link._version));
        }
        catch (Exception error)
        {
            DiagnosticLog.Write(link._log, "HeimKit", CoreLogTopics.Control, "Early client identification could not be sent.",
                level: LogLevel.Warning, error: error);
        }
    }

    internal void Tick(float now)
    {
        if (now < _next) return;
        _next = now + 1;
        var game = Game.instance;
        var net = ZNet.instance;
        if (game == null || net == null)
        {
            if (State != LinkState.NotInWorld) Reset(LinkState.NotInWorld);
            return;
        }

        var worldRole = WorldSession.Identify(net);
        var server = worldRole == WorldSessionRole.RemoteClient ? net.GetServerPeer() : null;
        var rpc = server?.m_rpc;
        if (rpc != null && !rpc.IsConnected())
        {
            if (State != LinkState.NotInWorld) Reset(LinkState.NotInWorld);
            return;
        }
        if (!ReferenceEquals(game, _game) || !ReferenceEquals(net, _net) ||
            !ReferenceEquals(ZNet.World, _sessionWorld) || !ReferenceEquals(rpc, _rpc) || State == LinkState.NotInWorld)
        {
            if (Player.m_localPlayer == null)
            {
                if (State != LinkState.NotInWorld) Reset(LinkState.NotInWorld);
                return;
            }
            Reset(worldRole switch
            {
                WorldSessionRole.RemoteClient => LinkState.Checking,
                WorldSessionRole.PrivateLocalHost or WorldSessionRole.NetworkHost => LinkState.LocalHostedWorld,
                _ => LinkState.ClientOnly
            });
            _game = game; _net = net; _sessionWorld = ZNet.World; _rpc = rpc; _peer = server?.m_uid ?? 0;
            _rpc?.Register<ZPackage>(ControlProtocol.State, Receive);
            _rpc?.Register<ZPackage>(ModerationProtocol.State, ReceiveModeration);
            if (DisabledServerVersion != null) SetState(LinkState.ServerDisabled);
        }

        if (Player.m_localPlayer == null) return;

        if (State == LinkState.LocalHostedWorld) ReadLocal(now);

        if (State == LinkState.Checking && now >= _nextHello)
        {
            if (_attempts >= 7) SetState(LinkState.ClientOnly);
            else
            {
                if (_rpc != null) SendHello(now);
                else _nextHello = now + 5;
                _attempts++;
            }
        }

        if (Waiting && now >= _deadline)
        {
            _request = 0; TimedOut = true;
            Changed?.Invoke();
        }
    }

    internal void Refresh(float now)
    {
        if (DisabledServerVersion != null || _rpc == null || now < _nextHello || State == LinkState.Checking) return;
        SendHello(now);
    }

    private void SendHello(float now)
    {
        if (ZRoutedRpc.instance == null || _peer == 0 || _rpc?.IsConnected() != true)
        {
            _nextHello = now + 5;
            return;
        }
        _nextHello = now + 5;
        ZRoutedRpc.instance.InvokeRoutedRPC(_peer, ControlProtocol.Hello, ControlProtocol.WriteHello(_version));
    }

    internal void Set(SwitchState entry, float now) =>
        SendSet(entry.Definition.Module, entry.Definition.Key, now,
            id => ControlProtocol.WriteSet(id, entry.Definition.Module, entry.Definition.Key, !entry.Value), value: !entry.Value);

    internal void Set(NumericSettingState entry, double value, float now) =>
        SendSet(entry.Module, entry.Key, now,
            id => ControlProtocol.WriteNumericSet(id, entry.Module, entry.Key, value), number: value);

    private void ReadLocal(float now)
    {
        var control = _local?.Control;
        string status = _local?.Status ?? "Unavailable";
        bool changed = status != LocalStatus || !ReferenceEquals(_localControl, control);
        LocalStatus = status;
        if (!ReferenceEquals(_localControl, control))
        {
            ClearModeration();
            _localControl = control;
            Snapshot = null;
            _request = 0;
            Result = SetResult.None;
            TimedOut = false;
            RequestModule = null;
            RequestKey = null;
        }
        var roster = control?.ReadLocalModeration();
        changed |= !ReferenceEquals(Moderation, roster);
        Moderation = roster;
        var snapshot = control?.ReadLocal(now, Snapshot?.Revision ?? -1);
        if (snapshot != null) AcceptSnapshot(snapshot, LinkState.LocalHostedWorld);
        else if (changed) Changed?.Invoke();
    }

    private void SendSet(string module, string key, float now, Func<int, ZPackage> encode, bool value = false, double? number = null)
    {
        if (Waiting || Snapshot?.IsAdmin != true || !Snapshot.AdminChangesAllowed) return;
        bool local = State == LinkState.LocalHostedWorld;
        if (local)
        {
            if (_localControl == null || !ReferenceEquals(_localControl, _local?.Control)) return;
        }
        else if (State != LinkState.Connected || ZRoutedRpc.instance == null || _peer == 0 || _rpc?.IsConnected() != true) return;
        _lastId = _lastId == int.MaxValue ? 1 : _lastId + 1;
        _request = _lastId; _deadline = now + 5;
        RequestModule = module; RequestKey = key;
        Result = SetResult.None; TimedOut = false;
        if (local)
        {
            if (_localControl!.SetLocal(_request, module, key, value, number, now))
            {
                ReadLocal(now);
                return;
            }
            _request = 0;
            Result = SetResult.Unavailable;
        }
        else ZRoutedRpc.instance.InvokeRoutedRPC(_peer, ControlProtocol.Set, encode(_request));
        Changed?.Invoke();
    }

    private void ReceiveDisabled(ZRpc rpc, ZPackage payload)
    {
        if (!ReferenceEquals(rpc, _identified) || !ControlProtocol.TryReadHello(payload, out var notice)) return;
        _disabledVersion = notice.Version;
        if (ReferenceEquals(rpc, _rpc)) SetState(LinkState.ServerDisabled);
    }

    private void Receive(ZRpc rpc, ZPackage payload)
    {
        if (!ReferenceEquals(rpc, _rpc)) return;
        if (!ControlProtocol.TryReadState(payload, out var snapshot))
        {
            float now = Time.unscaledTime;
            if (now >= _nextWarning)
            {
                _nextWarning = now + 300;
                DiagnosticLog.Write(_log, "HeimKit", CoreLogTopics.Control, "Rejected an invalid server control snapshot.", level: LogLevel.Warning);
            }
            return;
        }

        AcceptSnapshot(snapshot, snapshot.Protocol == ControlProtocol.Version ? LinkState.Connected : LinkState.VersionMismatch);
    }

    private void AcceptSnapshot(ControlSnapshot snapshot, LinkState state)
    {
        if (snapshot.Protocol == ControlProtocol.Version && Snapshot != null && snapshot.Revision < Snapshot.Revision) return;
        if (snapshot.Protocol != ControlProtocol.Version) _request = 0;
        Snapshot = snapshot;
        if (Waiting && snapshot.RequestId == _request && snapshot.Result != SetResult.None)
        {
            if (snapshot.Result == SetResult.Pending)
            {
                if (Result != SetResult.Pending) _deadline = Time.unscaledTime + 75;
            }
            else _request = 0;
            Result = snapshot.Result;
        }
        SetState(state);
    }

    private void SetState(LinkState state)
    {
        if ((Moderation != null || _moderationChunks != null) &&
            (Snapshot?.IsAdmin != true || (state != LinkState.Connected && state != LinkState.LocalHostedWorld)))
            ClearModeration();
        State = state;
        Changed?.Invoke();
    }

    private void Reset(LinkState state)
    {
        ClearModeration();
        _rpc?.Unregister(ModerationProtocol.State);
        _rpc?.Unregister(ControlProtocol.State);
        _rpc = null; _sessionWorld = null; _game = null; _net = null; _peer = 0; _nextHello = 0; _attempts = 0;
        _request = 0; Snapshot = null; Result = SetResult.None; TimedOut = false;
        RequestModule = null; RequestKey = null;
        _localControl = null; LocalStatus = "";
        SetState(state);
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        _identified?.Unregister(ControlProtocol.Disabled);
        try { _identification.UnpatchSelf(); }
        finally { Reset(LinkState.NotInWorld); }
    }
}
