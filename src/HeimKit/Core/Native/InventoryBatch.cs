using System;
using System.Collections.Generic;
namespace HeimKit.Core.Native;

internal sealed class InventoryBatch
{
    internal struct Stack
    {
        internal int Start, End, Offset, Prefab, Original, Remaining, Rank;
        internal bool Cheated;
        internal byte[]? Canonical;
    }
    internal readonly byte[] Original;
    internal readonly List<Stack> Stacks;
    internal readonly List<InventoryWithdrawal> Items = new();
    private byte[]? _bytes;
    internal InventoryBatch(byte[] original, List<Stack> stacks) { Original = original; Stacks = stacks; }
    internal void Limit(int count)
    {
        if (_bytes != null || count < 0 || count > Items.Count) throw new InvalidOperationException("Invalid staged inventory batch limit.");
        for (int i = count; i < Items.Count; i++) { int index = Items[i].SourceIndex; var stack = Stacks[index]; stack.Remaining++; Stacks[index] = stack; }
        Items.RemoveRange(count, Items.Count - count);
    }
    internal byte[] Bytes
    {
        get
        {
            if (_bytes != null) return _bytes;
            int size = Original.Length, count = Stacks.Count;
            foreach (var stack in Stacks) if (stack.Remaining == 0) { size -= stack.End - stack.Start; count--; }
            var bytes = new byte[size]; Buffer.BlockCopy(Original, 0, bytes, 0, 6);
            bytes[4] = (byte)count; bytes[5] = (byte)(count >> 8);
            int offset = 6;
            foreach (var stack in Stacks)
            {
                if (stack.Remaining == 0) continue;
                Buffer.BlockCopy(Original, stack.Start, bytes, offset, stack.End - stack.Start);
                if (stack.Remaining != stack.Original)
                { int at = offset + stack.Offset - stack.Start; bytes[at] = (byte)stack.Remaining; bytes[at + 1] = (byte)(stack.Remaining >> 8); }
                offset += stack.End - stack.Start;
            }
            return _bytes = bytes;
        }
    }
}
