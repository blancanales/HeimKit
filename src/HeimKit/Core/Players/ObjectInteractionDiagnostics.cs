using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Ownership;
using UnityEngine;

namespace HeimKit.Core.Players;

// The shared subscription sees inbound RPCs before forwarding. Local calls never traverse it.
internal sealed class ObjectInteractionDiagnostics : IDisposable
{
    private readonly struct Operation
    {
        internal Operation(string name, bool reply) { Name = name; Reply = reply; }
        internal readonly string Name;
        internal readonly bool Reply;
    }

    private static readonly Dictionary<int, Operation> Methods = new()
    {
        ["RPC_RequestOpen".GetStableHashCode()] = new("open", false),
        ["RPC_OpenResponse".GetStableHashCode()] = new("open", true),
        ["RPC_RequestStack".GetStableHashCode()] = new("stack", false),
        ["RPC_StackResponse".GetStableHashCode()] = new("stack", true),
        ["RPC_RequestTakeAll".GetStableHashCode()] = new("take_all", false),
        ["RPC_TakeAllResponse".GetStableHashCode()] = new("take_all", true)
    };
    private sealed class Pending
    {
        internal long Recipient;
        internal long? Owner;
        internal Vector3? Position;
        internal float First, Last;
        internal int Attempts;
        internal bool ReportedWait;
    }

    private readonly Dictionary<(long Player, ZDOID Object, string Action), Pending> _pending = new();
    private readonly List<(long Player, ZDOID Object, string Action)> _expired = new();
    private readonly DiagnosticBudget _budget = new();
    private readonly IPlayerPresence _presence;
    private readonly PlayerRoutedMessages _messages;
    private readonly ManualLogSource _log;
    private IDisposable? _subscription;
    private ZDOMan? _world;
    private float _nextSweep;
    private bool _failed;

    internal ObjectInteractionDiagnostics(IPlayerPresence presence, PlayerRoutedMessages messages, ManualLogSource log)
    { _presence = presence; _messages = messages; _log = log; }

    internal void Tick(float now)
    {
        try
        {
            if (!ReferenceEquals(ZDOMan.instance, _world))
            {
                Dispose();
                _world = ZDOMan.instance;
                _failed = false;
            }
            if (_failed || _world == null || _presence.HasJoinedPlayers != true)
            {
                if (_subscription != null) Dispose();
                return;
            }
            _subscription ??= _messages.Subscribe(Observe, Fail);
            if (_pending.Count == 0 || now < _nextSweep) return;
            _nextSweep = now + 1;
            float clock = Time.realtimeSinceStartup;
            _expired.Clear();
            foreach (var pair in _pending)
            {
                var pending = pair.Value;
                float age = clock - pending.First;
                if (age >= 30) _expired.Add(pair.Key);
                else if (age >= 2 && !pending.ReportedWait && pending.Recipient != ZDOMan.GetSessionID() && pending.Recipient != 0)
                {
                    pending.ReportedWait = true;
                    Report(pair.Key, pending, "awaiting_reply", clock);
                }
            }
            foreach (var key in _expired)
            {
                var pending = _pending[key];
                _pending.Remove(key);
                Report(key, pending, "no_reply_observed", clock);
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void Observe(ZRpc rpc, ZPackage packet)
    {
        RoutedPacketHeader header;
        try { header = ZdoPackets.Header(packet); }
        catch (Exception) { return; }
        if (header.Target.IsNone() || !Methods.TryGetValue(header.Method, out var operation)) return;
        var peer = PlayerPeerLookup.Ready(_presence.Peers, rpc);
        if (peer == null || header.Sender != peer.m_uid) return;
        int size = operation.Reply ? sizeof(byte) : sizeof(long);
        if (packet.Size() - header.End != sizeof(int) + size) return;
        packet.SetPos(header.End);
        if (packet.ReadInt() != size) return;
        float now = Time.realtimeSinceStartup;
        var key = (operation.Reply ? header.TargetPeer : header.Sender, header.Target, operation.Name);
        if (operation.Reply)
        {
            byte granted = packet.ReadByte();
            if (granted > 1 || !_pending.TryGetValue(key, out var p) || p.Recipient != header.Sender) return;
            _pending.Remove(key);
            Report(key, p, granted == 1 ? "granted" : "denied", now);
            return;
        }

        if (_pending.TryGetValue(key, out var previous))
        {
            if (previous.Recipient == header.TargetPeer)
            {
                previous.Attempts++;
                previous.Last = now;
                return;
            }
            _pending.Remove(key);
            Report(key, previous, "retry_changed_recipient", now);
        }
        if (_pending.Count >= 128) return;
        var record = _world?.GetZDO(header.Target);
        _pending.Add(key, new Pending { Recipient = header.TargetPeer, Owner = record?.GetOwner(),
            Position = record?.GetPosition(), First = now, Last = now, Attempts = 1 });
    }

    private void Report((long Player, ZDOID Object, string Action) key, Pending p, string result, float now)
    {
        bool localRoute = p.Recipient == ZDOMan.GetSessionID() || p.Recipient == 0;
        if (localRoute && result == "no_reply_observed") result = "local_or_broadcast_reply_unobserved";
        bool routine = p.Attempts == 1 && p.Owner == p.Recipient &&
            (result == "local_or_broadcast_reply_unobserved" || (result == "granted" && now - p.First < 1));
        if (!DiagnosticLog.DebugAvailable(_log, "HeimKit") ||
            !_budget.Reserve(now, routine ? "routine" : "incident", routine ? 3 : 24, false)) return;
        DiagnosticLog.Write(_log, "HeimKit", new LogTopic("interaction"), key.Action + " " + result + " for object " + key.Object,
            new LogField[] { ("requester", key.Player), DiagnosticLog.FieldAlways("recipient", p.Recipient),
                DiagnosticLog.FieldAlways("owner_at_request", p.Owner), ("position", p.Position),
                DiagnosticLog.FieldAlways("owner_at_sample", _world?.GetZDO(key.Object)?.GetOwner()),
                ("attempts", p.Attempts), DiagnosticLog.FieldAlways("since_first_ms", (long)((now - p.First) * 1000)),
                DiagnosticLog.FieldAlways("since_last_ms", (long)((now - p.Last) * 1000)) }, LogLevel.Debug);
    }

    private void Fail(Exception error)
    {
        _failed = true;
        Dispose();
        DiagnosticLog.Write(_log, "HeimKit", new LogTopic("interaction"), "Object interaction diagnostics stopped for this world.",
            level: LogLevel.Warning, error: error);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _pending.Clear();
        _expired.Clear();
        _nextSweep = 0;
        _budget.Reset();
    }
}
