using System;
using System.Collections.Generic;
using HarmonyLib;
using HeimKit.Core.Native;
namespace HeimKit.Core.Ownership;

// Preserve registration order and the loader's HarmonyX all-prefix/AND semantics.
// Typed, pooled invocations retain independent state across nested native calls.
internal static class IncomingPackets
{
    internal delegate bool Prefix<T>(Context context, ref ZPackage packet, out T state);
    internal delegate Exception? Finalizer<T>(Exception? error, T state);
    private static readonly Harmony Hooks = new("heimkit.incoming_packets");
    private static readonly Channel Data = new(false), Routed = new(true);
    [ThreadStatic] private static Context? _current;
    [ThreadStatic] private static Stack<Context>? _pool;
    internal static bool NeedsDataFilter => _current == null || _current.Channel.IsRouted || !_current.Inspected || _current.ScanFailed || _current.Matches[_current.Index];
    internal static bool TryPeer(ZRpc rpc, out ZNetPeer? peer)
    {
        peer = _current?.Peer; return _current != null && ReferenceEquals(_current.Rpc, rpc);
    }
    internal static bool TryRosterPeer(ZRpc rpc, IReadOnlyList<ZNetPeer> peers, out ZNetPeer? peer)
    {
        peer = null; var context = _current;
        if (context == null || !ReferenceEquals(context.Rpc, rpc)) return false;
        if (ReferenceEquals(context.Roster, peers)) { peer = context.RosterPeer; return true; }
        // The hub snapshot normally has the native list's order. Validate membership
        // without scanning it again. During a join/departure preserve that snapshot's
        // original decision rather than widening authentication to the native roster.
        int index = context.PeerIndex;
        if (index >= 0 && index < peers.Count && ReferenceEquals(peers[index], context.Peer)) peer = context.Peer;
        else for (int i = 0; i < peers.Count; i++)
            if (ReferenceEquals(peers[i].m_rpc, rpc)) { peer = peers[i].IsReady() ? peers[i] : null; break; }
        context.Roster = peers; context.RosterPeer = peer; return true;
    }
    internal static bool TryHeader(ZPackage packet, out RoutedPacketHeader header, out Exception? error)
    {
        header = default; error = null;
        var context = _current;
        if (context == null || !context.Channel.IsRouted || !ReferenceEquals(context.Packet, packet) || packet.GetPos() != context.Cursor) return false;
        context.ReadHeader(); header = context.Header; error = context.HeaderError; return true;
    }
    internal static IDisposable RegisterData<T>(Prefix<T> prefix, Action<T>? postfix = null, Finalizer<T>? finalizer = null,
        Func<ZDOID, bool>? retained = null, Action<Exception>? failed = null, bool affectsOriginal = true, Func<bool>? filtering = null)
        => Data.Register(prefix, postfix, finalizer, retained, failed, affectsOriginal, filtering);
    internal static IDisposable RegisterRouted<T>(Prefix<T> prefix, Action<T>? postfix = null, Finalizer<T>? finalizer = null,
        Action<Exception>? failed = null, bool affectsOriginal = true)
        => Routed.Register(prefix, postfix, finalizer, null, failed, affectsOriginal);
    internal sealed class Context
    {
        internal Channel Channel = null!;
        internal Context? Previous;
        internal ZRpc Rpc = null!;
        internal ZNetPeer? Peer, RosterPeer;
        internal IReadOnlyList<ZNetPeer>? Roster;
        internal int PeerIndex;
        internal ZPackage Packet = null!;
        internal int Cursor, Index, Count;
        internal Invocation?[] Calls = Array.Empty<Invocation?>();
        internal bool[] Matches = Array.Empty<bool>(), Filters = Array.Empty<bool>();
        internal Handler[] Handlers = Array.Empty<Handler>();
        internal bool Inspected, ScanFailed, HeaderRead, Finalized;
        internal RoutedPacketHeader Header;
        internal Exception? HeaderError;
        internal readonly Func<ZDOID, bool> Observe;
        internal Context() { Observe = ObserveId; }
        private bool ObserveId(ZDOID id)
        {
            for (int i = 0; i < Handlers.Length; i++)
                if (Filters[i] && Handlers[i].Retained?.Invoke(id) == true) Matches[i] = true;
            return false;
        }
        internal void ReadHeader()
        {
            if (HeaderRead) return; HeaderRead = true;
            int cursor = Packet.GetPos();
            try { Header = new RoutedPacketHeader(Packet); }
            catch (Exception error) { HeaderError = error; }
            finally { Packet.SetPos(cursor); }
        }
    }
    internal sealed class Channel
    {
        internal readonly bool IsRouted;
        private readonly List<Handler> _handlers = new();
        internal Handler[] Snapshot = Array.Empty<Handler>();
        private readonly bool _install;
        internal Channel(bool routed, bool install = true) { IsRouted = routed; _install = install; }
        internal IDisposable Register<T>(Prefix<T> prefix, Action<T>? postfix = null, Finalizer<T>? finalizer = null,
            Func<ZDOID, bool>? retained = null, Action<Exception>? failed = null, bool affectsOriginal = true, Func<bool>? filtering = null)
            => Add(new Handler<T>(this, prefix, postfix, finalizer, retained, failed, affectsOriginal, filtering));
        private System.Reflection.MethodInfo Target => NativeAccess.Target(IsRouted ? typeof(ZRoutedRpc) : typeof(ZDOMan), IsRouted ? "RPC_RoutedRPC" : "RPC_ZDOData", typeof(ZRpc), typeof(ZPackage));
        internal IDisposable Add(Handler handler)
        {
            if (_handlers.Count == 0 && _install)
            {
                string kind = IsRouted ? "Routed" : "Data";
                try { Hooks.Patch(Target, prefix: new HarmonyMethod(typeof(IncomingPackets), kind + "Before"),
                    postfix: new HarmonyMethod(typeof(IncomingPackets), nameof(Applied)), finalizer: new HarmonyMethod(typeof(IncomingPackets), nameof(Finished))); }
                catch { Hooks.Unpatch(Target, HarmonyPatchType.All, Hooks.Id); throw; }
            }
            _handlers.Add(handler); Snapshot = _handlers.ToArray(); return handler;
        }
        internal void Remove(Handler handler)
        {
            if (!_handlers.Remove(handler)) return;
            Snapshot = _handlers.ToArray();
            if (_handlers.Count == 0 && _install) Hooks.Unpatch(Target, HarmonyPatchType.All, Hooks.Id);
        }
    }
    internal abstract class Handler : IDisposable
    {
        internal readonly Func<ZDOID, bool>? Retained;
        internal readonly Action<Exception>? Failed;
        internal readonly Func<bool>? Filtering;
        internal readonly bool AffectsOriginal;
        private readonly Channel _channel;
        internal bool Disposed;
        protected Handler(Channel channel, Func<ZDOID, bool>? retained, Action<Exception>? failed, bool affectsOriginal, Func<bool>? filtering)
        { _channel = channel; Retained = retained; Failed = failed; AffectsOriginal = affectsOriginal; Filtering = filtering; }
        internal abstract Invocation Rent();
        public void Dispose() { if (Disposed) return; Disposed = true; _channel.Remove(this); }
        internal void Fail(Exception error) { try { Failed?.Invoke(error); } catch { /* Preserve the native packet's exception. */ } }
    }
    private sealed class Handler<T> : Handler
    {
        internal readonly Prefix<T> Before;
        internal readonly Action<T>? After;
        internal readonly Finalizer<T>? Finally;
        private readonly Stack<TypedInvocation> _free = new();
        internal Handler(Channel channel, Prefix<T> prefix, Action<T>? postfix, Finalizer<T>? finalizer, Func<ZDOID, bool>? retained, Action<Exception>? failed, bool affectsOriginal, Func<bool>? filtering)
            : base(channel, retained, failed, affectsOriginal, filtering) { Before = prefix; After = postfix; Finally = finalizer; }
        internal override Invocation Rent() => _free.Count == 0 ? new TypedInvocation(this) : _free.Pop();
        private sealed class TypedInvocation : Invocation
        {
            private readonly Handler<T> _handler;
            private T _state = default!;
            internal TypedInvocation(Handler<T> handler) : base(handler) { _handler = handler; }
            internal override bool Before(Context context, ref ZPackage packet) => _handler.Before(context, ref packet, out _state);
            internal override void After() => _handler.After?.Invoke(_state);
            internal override Exception? Finally(Exception? error) => _handler.Finally == null ? error : _handler.Finally(error, _state);
            internal override void Release() { _state = default!; _handler._free.Push(this); }
        }
    }
    internal abstract class Invocation
    {
        internal readonly Handler Handler;
        protected Invocation(Handler handler) { Handler = handler; }
        internal abstract bool Before(Context context, ref ZPackage packet);
        internal abstract void After();
        internal abstract Exception? Finally(Exception? error);
        internal abstract void Release();
    }
    private static bool DataBefore(ZRpc rpc, ref ZPackage pkg, out Context __state) => Before(Data, rpc, ref pkg, out __state);
    private static bool RoutedBefore(ZRpc rpc, ref ZPackage pkg, out Context __state) => Before(Routed, rpc, ref pkg, out __state);
    internal static bool Before(Channel channel, ZRpc rpc, ref ZPackage packet, out Context state)
    {
        var context = _pool != null && _pool.Count != 0 ? _pool.Pop() : new Context();
        state = context; context.Channel = channel; context.Rpc = rpc; context.Previous = _current; _current = context;
        context.Packet = packet; context.Cursor = packet.GetPos(); context.Handlers = channel.Snapshot; context.Count = 0;
        context.Inspected = context.ScanFailed = context.HeaderRead = context.Finalized = false; context.Header = default; context.HeaderError = null; context.Peer = context.RosterPeer = null; context.Roster = null; context.PeerIndex = -1;
        if (context.Calls.Length < context.Handlers.Length) { context.Calls = new Invocation?[context.Handlers.Length]; context.Matches = new bool[context.Handlers.Length]; context.Filters = new bool[context.Handlers.Length]; }
        Array.Clear(context.Matches, 0, context.Matches.Length);
        var peers = ZNet.instance == null ? null : ZNet.instance.GetPeers();
        if (peers != null) for (int i = 0; i < peers.Count; i++)
            if (ReferenceEquals(peers[i].m_rpc, rpc)) { context.PeerIndex = i; context.Peer = peers[i].IsReady() ? peers[i] : null; break; }
        if (channel.IsRouted) context.ReadHeader();
        else if (context.Peer != null)
        {
            bool filters = false;
            for (int i = 0; i < context.Handlers.Length; i++)
            {
                var handler = context.Handlers[i];
                context.Filters[i] = handler.Retained != null && (handler.Filtering?.Invoke() ?? true); filters |= context.Filters[i];
            }
            if (filters)
            {
                context.Inspected = true;
                try { ZdoPackets.ContainsData(packet, context.Observe); }
                catch (Exception) { context.ScanFailed = true; } // Each original filter owns malformed-input failure policy.
            }
        }
        bool run = true;
        for (int i = 0; i < context.Handlers.Length; i++)
        {
            var handler = context.Handlers[i]; var call = handler.Rent(); context.Calls[context.Count++] = call; context.Index = i;
            if (handler.Disposed) continue;
            var original = packet; int cursor = original.GetPos();
            try { if (!call.Before(context, ref packet)) run = false; }
            catch (Exception error) { handler.Fail(error); if (handler.AffectsOriginal) run = false; }
            finally
            {
                original.SetPos(cursor);
                if (!ReferenceEquals(context.Packet, packet))
                {
                    context.Packet = packet; context.Cursor = packet.GetPos(); context.HeaderRead = false; context.HeaderError = null; context.Header = default;
                }
            }
        }
        return run;
    }
    internal static void Applied(Context? __state)
    {
        if (__state == null) return;
        for (int i = 0; i < __state.Count; i++)
        {
            var call = __state.Calls[i]!;
            try { call.After(); } catch (Exception error) { call.Handler.Fail(error); }
        }
    }
    internal static Exception? Finished(Exception? __exception, Context? __state)
    {
        if (__state == null || __state.Finalized) return __exception;
        __state.Finalized = true;
        try
        {
            for (int i = 0; i < __state.Count; i++)
            {
                var call = __state.Calls[i]!;
                try { __exception = call.Finally(__exception); } catch (Exception error) { call.Handler.Fail(error); }
            }
            return __exception;
        }
        finally
        {
            for (int i = 0; i < __state.Count; i++) { __state.Calls[i]!.Release(); __state.Calls[i] = null; }
            _current = __state.Previous; __state.Previous = null; __state.Peer = __state.RosterPeer = null; __state.Roster = null; __state.Rpc = null!; __state.Packet = null!;
            __state.Handlers = Array.Empty<Handler>(); __state.HeaderError = null;
            (_pool ??= new()).Push(__state);
        }
    }
}
