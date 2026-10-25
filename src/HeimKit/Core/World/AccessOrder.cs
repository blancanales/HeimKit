using System.Collections.Generic;

namespace HeimKit.Core.World;

// Bounded by its owning cache. Hits move existing nodes without allocations;
// eviction/clear recycle nodes for later cache misses.
internal sealed class AccessOrder<T> where T : notnull
{
    private readonly LinkedList<T> _order = new();
    private readonly Dictionary<T, LinkedListNode<T>> _nodes = new();
    private readonly Stack<LinkedListNode<T>> _spares = new();
    internal int Count => _nodes.Count;
    internal bool Touch(T key)
    {
        if (!_nodes.TryGetValue(key, out var node)) return false;
        if (!ReferenceEquals(node, _order.Last)) { _order.Remove(node); _order.AddLast(node); }
        return true;
    }
    internal void Add(T key)
    {
        var node = _spares.Count > 0 ? _spares.Pop() : new LinkedListNode<T>(key);
        node.Value = key; _nodes.Add(key, node); _order.AddLast(node);
    }
    internal T RemoveOldest()
    {
        var node = _order.First!; var key = node.Value;
        _order.RemoveFirst(); _nodes.Remove(key); node.Value = default!; _spares.Push(node);
        return key;
    }
    internal void Clear() { while (_order.Count > 0) RemoveOldest(); }
}
