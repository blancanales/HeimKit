using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace HeimKit.Core.Players;

// One native observer for independent consumers; subscribing does not acquire AFK inference.
public sealed class PlayerRoutedMessages : IDisposable
{
    private sealed class Subscription : IDisposable
    {
        private readonly PlayerRoutedMessages _owner;
        public Action<ZRpc, ZPackage> Observed { get; }
        public Action<Exception> Failed { get; }
        public Subscription(PlayerRoutedMessages owner, Action<ZRpc, ZPackage> observed, Action<Exception> failed)
        { _owner = owner; Observed = observed; Failed = failed; }
        public void Dispose() => _owner.Remove(this);
    }
    private static PlayerRoutedMessages? _current;
    private IDisposable? _packets;
    private readonly List<Subscription> _subscriptions = new();
    private Subscription[] _snapshot = Array.Empty<Subscription>();
    public IDisposable Subscribe(Action<ZRpc, ZPackage> observed, Action<Exception> failed)
    {
        if (_subscriptions.Count == 0)
        {
            if (_current != null && !ReferenceEquals(_current, this)) throw new InvalidOperationException("A routed-message observer is already active.");
            try
            {
                _current = this;
                _packets = IncomingPackets.RegisterRouted<byte>(Packet, affectsOriginal: false);
            }
            catch { _packets?.Dispose(); _packets = null; _current = null; throw; }
        }
        var subscription = new Subscription(this, observed, failed);
        _subscriptions.Add(subscription); _snapshot = _subscriptions.ToArray(); return subscription;
    }
    private static bool Packet(IncomingPackets.Context context, ref ZPackage packet, out byte state)
    { state = 0; Observe(context.Rpc, packet); return true; }
    private static void Observe(ZRpc rpc, ZPackage pkg)
    {
        var current = _current;
        if (current == null) return;
        foreach (var subscription in current._snapshot)
        {
            if (!current._subscriptions.Contains(subscription)) continue;
            int cursor = pkg.GetPos();
            try { subscription.Observed(rpc, pkg); }
            catch (Exception error)
            {
                subscription.Dispose();
                try { subscription.Failed(error); } catch { /* Native traffic must continue. */ }
            }
            finally { pkg.SetPos(cursor); }
        }
    }
    private void Remove(Subscription subscription)
    {
        if (!_subscriptions.Remove(subscription)) return;
        _snapshot = _subscriptions.ToArray();
        if (_subscriptions.Count != 0) return;
        try { _packets?.Dispose(); _packets = null; }
        finally { if (ReferenceEquals(_current, this)) _current = null; }
    }
    public void Dispose()
    {
        _subscriptions.Clear();
        _snapshot = Array.Empty<Subscription>();
        try { _packets?.Dispose(); _packets = null; }
        finally { if (ReferenceEquals(_current, this)) _current = null; }
    }
}
