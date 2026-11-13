using System;
using System.Collections.Generic;
using System.IO;
namespace HeimKit.Core.Native;

// Preserve the original bytes of every item, including integer durability and custom data.
internal static class NativeInventory
{
    internal const int Format = 110;
    private static class Accessors
    {
        internal static readonly HarmonyLib.AccessTools.FieldRef<Container, uint> Revision = NativeAccess.Field<Container, uint>("m_lastRevision");
        internal static readonly Func<Container, bool> Load = NativeAccess.Method<Func<Container, bool>>(typeof(Container), "Load");
    }
    internal static void CheckCompatibility() { _ = Accessors.Load; _ = Accessors.Revision; }
    internal static void Reload(Container container)
    {
        if (container.IsInUse()) throw new InvalidOperationException("An open inventory cannot be reloaded for a supply transfer.");
        Accessors.Revision(container) = uint.MaxValue;
        Accessors.Load(container);
    }
    internal static InventoryWithdrawal? WithdrawOne(byte[] bytes, int width, int height, IReadOnlyList<int> preferred, Func<int, bool>? accept = null)
        => WithdrawOne(bytes, width, height, preferred, out _, accept);
    internal static InventoryWithdrawal? WithdrawOne(byte[] bytes, int width, int height, IReadOnlyList<int> preferred, out InventoryReadEvidence evidence, Func<int, bool>? accept = null)
    {
        evidence = new InventoryReadEvidence { Status = "invalid_inventory" };
        if (bytes == null || bytes.Length < 6 || bytes.Length > 1048576 || width < 1 || height < 1 || width * height > 512) return null;
        var packet = new ZPackage(bytes);
        try
        {
            if (packet.ReadInt() != Format) { evidence.Status = "unsupported_inventory_format"; return null; }
            int count = packet.ReadUShort();
            evidence.Stacks = count;
            if (count > width * height) return null;
            int chosenStart = -1, chosenEnd = 0, chosenRank = int.MaxValue, chosenStack = 0, stackOffset = 0, chosenPrefab = 0;
            bool cheated = false;
            Span<ulong> slots = stackalloc ulong[8]; slots.Clear();
            for (int i = 0; i < count; i++)
            {
                int start = packet.GetPos();
                var (prefab, item) = ItemDrop.ItemData.Load(packet, (global::Version.Item)Format);
                int end = packet.GetPos();
                if (end - start < 9 || prefab == 0 || item.m_stack < 1 || item.m_quality < 1 || item.m_gridPos.x < 0 || item.m_gridPos.y < 0 || item.m_gridPos.x >= width || item.m_gridPos.y >= height) return null;
                int slot = item.m_gridPos.y * width + item.m_gridPos.x;
                ulong mask = 1UL << (slot & 63);
                if ((slots[slot >> 6] & mask) != 0) return null;
                slots[slot >> 6] |= mask;
                bool accepted = accept != null && accept(prefab);
                if (accept != null && chosenStart < 0 && accepted)
                {
                    chosenStart = start; chosenEnd = end; chosenStack = item.m_stack; chosenPrefab = prefab; cheated = item.m_cheated;
                    stackOffset = start + 8 + ((bytes[start + 7] & 4) != 0 ? 2 : 0);
                }
                for (int rank = 0; accept == null && rank < preferred.Count; rank++)
                    if (prefab == preferred[rank])
                    {
                        accepted = true;
                        if (rank < chosenRank)
                        {
                            chosenRank = rank; chosenStart = start; chosenEnd = end; chosenStack = item.m_stack; chosenPrefab = prefab; cheated = item.m_cheated;
                            stackOffset = start + 8 + ((bytes[start + 7] & 4) != 0 ? 2 : 0);
                        }
                        break;
                    }
                if (!accepted) { evidence.Rejected++; if (evidence.FirstRejected == 0) evidence.FirstRejected = prefab; }
            }
            if (packet.GetPos() != packet.Size()) return null;
            if (chosenStart < 0) { evidence.Status = count == 0 ? "empty_inventory" : accept == null ? "no_compatible_resource" : "no_allowed_item"; return null; }
            byte[] result;
            if (chosenStack == 1)
            {
                result = new byte[bytes.Length - (chosenEnd - chosenStart)];
                Buffer.BlockCopy(bytes, 0, result, 0, chosenStart);
                Buffer.BlockCopy(bytes, chosenEnd, result, chosenStart, bytes.Length - chosenEnd);
                result[4] = (byte)(count - 1); result[5] = (byte)((count - 1) >> 8);
            }
            else
            {
                result = (byte[])bytes.Clone();
                result[stackOffset] = (byte)(chosenStack - 1); result[stackOffset + 1] = (byte)((chosenStack - 1) >> 8);
            }
            var withdrawal = new InventoryWithdrawal { ItemBytes = CanonicalItem(bytes, chosenStart, chosenEnd), Bytes = result, Prefab = chosenPrefab, Cheated = cheated, OriginalStack = chosenStack };
            evidence.Status = "resource_selected"; return withdrawal;
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }
    internal static InventoryBatch? WithdrawMany(byte[] bytes, int width, int height, IReadOnlyList<int> preferred, int maximum,
        out InventoryReadEvidence evidence, Func<int, bool>? accept = null, Func<int, bool>? room = null)
    {
        evidence = new InventoryReadEvidence { Status = "invalid_inventory" };
        if (bytes == null || bytes.Length < 6 || bytes.Length > 1048576 || width < 1 || height < 1 || width * height > 512 || maximum < 1 || maximum > 64) return null;
        var packet = new ZPackage(bytes);
        try
        {
            if (packet.ReadInt() != Format) { evidence.Status = "unsupported_inventory_format"; return null; }
            int count = packet.ReadUShort(); evidence.Stacks = count;
            if (count > width * height) return null;
            var stacks = new List<InventoryBatch.Stack>(count);
            Span<ulong> slots = stackalloc ulong[8]; slots.Clear();
            for (int i = 0; i < count; i++)
            {
                int start = packet.GetPos();
                var (prefab, item) = ItemDrop.ItemData.Load(packet, (global::Version.Item)Format);
                int end = packet.GetPos();
                if (end - start < 9 || prefab == 0 || item.m_stack < 1 || item.m_quality < 1 || item.m_gridPos.x < 0 || item.m_gridPos.y < 0 || item.m_gridPos.x >= width || item.m_gridPos.y >= height) return null;
                int slot = item.m_gridPos.y * width + item.m_gridPos.x; ulong mask = 1UL << (slot & 63);
                if ((slots[slot >> 6] & mask) != 0) return null; slots[slot >> 6] |= mask;
                int rank = int.MaxValue;
                if (accept != null) { if (accept(prefab)) rank = 0; }
                else for (int r = 0; r < preferred.Count; r++) if (prefab == preferred[r]) { rank = r; break; }
                if (rank == int.MaxValue) { evidence.Rejected++; if (evidence.FirstRejected == 0) evidence.FirstRejected = prefab; }
                stacks.Add(new InventoryBatch.Stack { Start = start, End = end, Offset = start + 8 + ((bytes[start + 7] & 4) != 0 ? 2 : 0),
                    Prefab = prefab, Original = item.m_stack, Remaining = item.m_stack, Rank = rank, Cheated = item.m_cheated });
            }
            if (packet.GetPos() != packet.Size()) return null;
            var result = new InventoryBatch(bytes, stacks);
            while (result.Items.Count < maximum)
            {
                int chosen = -1, rank = int.MaxValue;
                for (int i = 0; i < stacks.Count; i++) if (stacks[i].Remaining > 0 && stacks[i].Rank < rank) { chosen = i; rank = stacks[i].Rank; }
                if (chosen < 0 || room != null && !room(stacks[chosen].Prefab)) break;
                var stack = stacks[chosen]; stack.Canonical ??= CanonicalItem(bytes, stack.Start, stack.End);
                result.Items.Add(new InventoryWithdrawal { SourceIndex = chosen, ItemBytes = stack.Canonical, Prefab = stack.Prefab, Cheated = stack.Cheated, OriginalStack = stack.Remaining });
                stack.Remaining--; stacks[chosen] = stack;
            }
            if (result.Items.Count == 0) { evidence.Status = count == 0 ? "empty_inventory" : accept == null ? "no_compatible_resource" : "no_allowed_item"; return null; }
            evidence.Status = "resource_selected"; return result;
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    private struct InsertionStack
    {
        internal byte[] Bytes;
        internal int Start, End, Count, Prefab, Slot;
    }
    internal static byte[]? InsertMany(byte[] original, int width, int height, IReadOnlyList<InventoryWithdrawal> items,
        IReadOnlyList<int> maximumStacks, out int inserted, out InventoryReadEvidence evidence)
    {
        inserted = 0; evidence = new InventoryReadEvidence { Status = "invalid_inventory" };
        if (original == null || original.Length < 6 || original.Length > 1048576 || width < 1 || height < 1 || width > 256 || height > 256 || width * height > 512 ||
            items.Count < 1 || items.Count > 64 || maximumStacks.Count != items.Count) return null;
        try
        {
            var packet = new ZPackage(original);
            if (packet.ReadInt() != Format) { evidence.Status = "unsupported_inventory_format"; return null; }
            int count = packet.ReadUShort(); if (count > width * height) return null; evidence.Stacks = count;
            Span<bool> slots = stackalloc bool[width * height]; slots.Clear();
            var stacks = new List<InsertionStack>(count + items.Count);
            for (int i = 0; i < count; i++)
            {
                int start = packet.GetPos(); var (prefab, entry) = ItemDrop.ItemData.Load(packet, (global::Version.Item)Format); int end = packet.GetPos();
                if (end - start < 13 || prefab == 0 || entry.m_stack < 1 || entry.m_quality < 1 || entry.m_gridPos.x < 0 || entry.m_gridPos.y < 0 || entry.m_gridPos.x >= width || entry.m_gridPos.y >= height) return null;
                int slot = entry.m_gridPos.y * width + entry.m_gridPos.x; if (slots[slot]) return null; slots[slot] = true;
                stacks.Add(new InsertionStack { Bytes = original, Start = start, End = end, Count = entry.m_stack, Prefab = prefab, Slot = slot });
            }
            if (packet.GetPos() != packet.Size()) return null;
            int size = original.Length;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i]; int maximum = maximumStacks[i];
                if (maximum < 1 || maximum > ushort.MaxValue || item.ItemBytes.Length < 13) return null;
                var single = new ZPackage(item.ItemBytes); var (hash, data) = ItemDrop.ItemData.Load(single, (global::Version.Item)Format);
                if (hash != item.Prefab || data.m_stack != 1 || single.GetPos() != single.Size()) return null;
                int merge = -1;
                for (int j = 0; j < stacks.Count; j++)
                {
                    var entry = stacks[j];
                    if (entry.Prefab == item.Prefab && entry.Count < maximum && SameItem(entry.Bytes, entry.Start, entry.End, item.ItemBytes)) { merge = j; break; }
                }
                int extra;
                if (merge >= 0)
                {
                    var entry = stacks[merge]; extra = entry.Count == 1 && (entry.Bytes[entry.Start + 7] & 8) == 0 ? 2 : 0;
                    if (size + extra > 1048576) { evidence.Status = "inventory_size_limit"; break; }
                    entry.Count++; stacks[merge] = entry; evidence.Status = "item_stacked";
                }
                else
                {
                    int empty = -1; for (int j = 0; j < slots.Length; j++) if (!slots[j]) { empty = j; break; }
                    if (empty < 0) { evidence.Status = "inventory_full"; break; }
                    extra = item.ItemBytes.Length;
                    if (size + extra > 1048576) { evidence.Status = "inventory_size_limit"; break; }
                    slots[empty] = true;
                    stacks.Add(new InsertionStack { Bytes = item.ItemBytes, End = item.ItemBytes.Length, Count = 1, Prefab = item.Prefab, Slot = empty });
                    evidence.Status = "item_inserted";
                }
                size += extra; inserted++;
            }
            if (inserted == 0) return null;
            var result = new byte[size]; Buffer.BlockCopy(original, 0, result, 0, 6);
            result[4] = (byte)stacks.Count; result[5] = (byte)(stacks.Count >> 8);
            int at = 6;
            foreach (var entry in stacks)
            {
                int offset = 8 + ((entry.Bytes[entry.Start + 7] & 4) != 0 ? 2 : 0);
                bool counted = (entry.Bytes[entry.Start + 7] & 8) != 0, addCount = !counted && entry.Count > 1;
                Buffer.BlockCopy(entry.Bytes, entry.Start, result, at, offset);
                int tail = offset + (counted ? 2 : 0), outputTail = offset + (counted || addCount ? 2 : 0);
                Buffer.BlockCopy(entry.Bytes, entry.Start + tail, result, at + outputTail, entry.End - entry.Start - tail);
                result[at + 4] = (byte)(entry.Slot % width); result[at + 5] = (byte)(entry.Slot / width);
                if (counted || addCount) { result[at + 7] |= 8; result[at + offset] = (byte)entry.Count; result[at + offset + 1] = (byte)(entry.Count >> 8); }
                at += entry.End - entry.Start + (addCount ? 2 : 0);
            }
            return result;
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

    internal static byte[] Empty() => new byte[] { 110, 0, 0, 0, 0, 0 };

    // Normalize only placement/count encoding. The remaining bytes retain exact
    // durability, variant, quality, world level, crafter, custom and cheat data.
    private static byte[] CanonicalItem(byte[] bytes, int start, int end)
    {
        byte flags = bytes[start + 7]; bool counted = (flags & 8) != 0;
        int offset = start + 8 + ((flags & 4) != 0 ? 2 : 0);
        var result = new byte[end - start - (counted ? 2 : 0)];
        if (counted)
        {
            Buffer.BlockCopy(bytes, start, result, 0, offset - start);
            Buffer.BlockCopy(bytes, offset + 2, result, offset - start, end - offset - 2);
        }
        else Buffer.BlockCopy(bytes, start, result, 0, result.Length);
        result[4] = result[5] = 0; result[7] = (byte)(flags & ~8);
        return result;
    }

    // Compare the same normalized encoding without allocating one array per destination stack.
    private static bool SameItem(byte[] bytes, int start, int end, byte[] canonical)
    {
        byte flags = bytes[start + 7]; bool counted = (flags & 8) != 0;
        int offset = 8 + ((flags & 4) != 0 ? 2 : 0);
        int after = offset + (counted ? 2 : 0);
        return end - start - (counted ? 2 : 0) == canonical.Length && canonical[4] == 0 && canonical[5] == 0 &&
            canonical[6] == bytes[start + 6] && canonical[7] == (byte)(flags & ~8) &&
            bytes.AsSpan(start, 4).SequenceEqual(canonical.AsSpan(0, 4)) &&
            bytes.AsSpan(start + 8, offset - 8).SequenceEqual(canonical.AsSpan(8, offset - 8)) &&
            bytes.AsSpan(start + after, end - start - after).SequenceEqual(canonical.AsSpan(offset));
    }

    internal static byte[]? InsertOne(byte[] original, int width, int height, InventoryWithdrawal item, int maximumStack)
        => InsertOne(original, width, height, item, maximumStack, out _);
    internal static byte[]? InsertOne(byte[] original, int width, int height, InventoryWithdrawal item, int maximumStack, out InventoryReadEvidence evidence)
    {
        evidence = new InventoryReadEvidence { Status = "invalid_inventory" };
        if (original == null || original.Length < 6 || original.Length > 1048576 || width < 1 || height < 1 || width > 256 || height > 256 || width * height > 512 ||
            maximumStack < 1 || maximumStack > ushort.MaxValue || item.ItemBytes.Length < 13) return null;
        try
        {
            var single = new ZPackage(item.ItemBytes);
            var (hash, data) = ItemDrop.ItemData.Load(single, (global::Version.Item)Format);
            if (hash != item.Prefab || data.m_stack != 1 || single.GetPos() != single.Size()) return null;
            var packet = new ZPackage(original);
            if (packet.ReadInt() != Format) { evidence.Status = "unsupported_inventory_format"; return null; }
            int count = packet.ReadUShort(); if (count > width * height) return null;
            evidence.Stacks = count;
            Span<bool> slots = stackalloc bool[width * height]; slots.Clear();
            int mergeStart = -1, mergeStack = 0;
            for (int i = 0; i < count; i++)
            {
                int start = packet.GetPos();
                var (prefab, entry) = ItemDrop.ItemData.Load(packet, (global::Version.Item)Format);
                int end = packet.GetPos();
                if (end - start < 13 || prefab == 0 || entry.m_stack < 1 || entry.m_quality < 1 || entry.m_gridPos.x < 0 || entry.m_gridPos.y < 0 || entry.m_gridPos.x >= width || entry.m_gridPos.y >= height) return null;
                int slot = entry.m_gridPos.y * width + entry.m_gridPos.x;
                if (slots[slot]) return null; slots[slot] = true;
                if (mergeStart < 0 && prefab == item.Prefab && entry.m_stack < maximumStack &&
                    SameItem(original, start, end, item.ItemBytes))
                { mergeStart = start; mergeStack = entry.m_stack; }
            }
            if (packet.GetPos() != packet.Size()) return null;
            if (mergeStart >= 0)
            {
                byte flags = original[mergeStart + 7];
                int offset = mergeStart + 8 + ((flags & 4) != 0 ? 2 : 0);
                bool counted = (flags & 8) != 0;
                if (original.Length + (counted ? 0 : 2) > 1048576) { evidence.Status = "inventory_size_limit"; return null; }
                var result = new byte[original.Length + (counted ? 0 : 2)];
                if (counted) Buffer.BlockCopy(original, 0, result, 0, original.Length);
                else
                {
                    Buffer.BlockCopy(original, 0, result, 0, offset);
                    Buffer.BlockCopy(original, offset, result, offset + 2, original.Length - offset);
                    result[mergeStart + 7] |= 8;
                }
                result[offset] = (byte)(mergeStack + 1); result[offset + 1] = (byte)((mergeStack + 1) >> 8);
                evidence.Status = "item_stacked";
                return result;
            }
            int empty = -1; for (int i = 0; i < slots.Length; i++) if (!slots[i]) { empty = i; break; }
            if (empty < 0) { evidence.Status = "inventory_full"; return null; }
            if (original.Length + item.ItemBytes.Length > 1048576) { evidence.Status = "inventory_size_limit"; return null; }
            var appended = new byte[original.Length + item.ItemBytes.Length];
            Buffer.BlockCopy(original, 0, appended, 0, original.Length);
            Buffer.BlockCopy(item.ItemBytes, 0, appended, original.Length, item.ItemBytes.Length);
            appended[original.Length + 4] = (byte)(empty % width); appended[original.Length + 5] = (byte)(empty / width);
            appended[4] = (byte)(count + 1); appended[5] = (byte)((count + 1) >> 8);
            evidence.Status = "item_inserted";
            return appended;
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (IndexOutOfRangeException) { return null; }
    }

}
