using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace HeimKit.Shared.Players;

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
    private readonly Harmony _harmony = new("heimkit.player_routed_messages");
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
                _harmony.Patch(AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC"),
                    prefix: new HarmonyMethod(typeof(PlayerRoutedMessages), nameof(Observe)));
            }
            catch { _harmony.UnpatchSelf(); _current = null; throw; }
        }
        var subscription = new Subscription(this, observed, failed);
        _subscriptions.Add(subscription); _snapshot = _subscriptions.ToArray(); return subscription;
    }
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
        try { _harmony.UnpatchSelf(); }
        finally { if (ReferenceEquals(_current, this)) _current = null; }
    }
    public void Dispose()
    {
        _subscriptions.Clear();
        _snapshot = Array.Empty<Subscription>();
        try { _harmony.UnpatchSelf(); }
        finally { if (ReferenceEquals(_current, this)) _current = null; }
    }
}
