using System;
using HeimKit.Core.Native;
using HeimKit.Features.StationSupply.Models;
namespace HeimKit.Features.StationSupply;

internal static class StationSupplyContainerTransfer
{
    internal static bool TryCommit(SupplyTransaction transaction, byte[] originalSource, byte[]? originalDestination,
        InventoryWithdrawal withdrawal, byte[] destination, Action? reloadSource = null, Action? reloadDestination = null)
        => TryCommit(transaction, originalSource, originalDestination, withdrawal, destination, out _, reloadSource, reloadDestination);
    internal static bool TryCommit(SupplyTransaction transaction, byte[] originalSource, byte[]? originalDestination,
        InventoryWithdrawal withdrawal, byte[] destination, out string reason, Action? reloadSource = null, Action? reloadDestination = null)
        => Commit(transaction, originalSource, originalDestination, withdrawal.Bytes, destination, out reason, reloadSource, reloadDestination);
    internal static bool TryCommit(SupplyTransaction transaction, byte[] originalSource, byte[]? originalDestination,
        InventoryBatch batch, byte[] destination, out string reason, Action? reloadSource = null, Action? reloadDestination = null)
        => Commit(transaction, originalSource, originalDestination, batch.Bytes, destination, out reason, reloadSource, reloadDestination);
    private static bool Commit(SupplyTransaction transaction, byte[] originalSource, byte[]? originalDestination,
        byte[] sourceBytes, byte[] destination, out string reason, Action? reloadSource, Action? reloadDestination)
    {
        reason = "resource_transferred";
        var source = transaction.Chest.Record; var target = transaction.Station.Record;
        var sourceNow = source.GetByteArray(ZDOVars.s_items); var targetNow = target.GetByteArray(ZDOVars.s_items);
        if (sourceNow == null || !originalSource.AsSpan().SequenceEqual(sourceNow)) { reason = "source_inventory_changed"; return false; }
        if (!Same(originalDestination, targetNow)) { reason = "destination_inventory_changed"; return false; }
        if (ReferenceEquals(source, target) || source.m_uid == target.m_uid) { reason = "source_equals_destination"; return false; }
        try
        {
            source.Set(ZDOVars.s_items, sourceBytes); target.Set(ZDOVars.s_items, destination);
            reloadSource?.Invoke(); reloadDestination?.Invoke();
            return true;
        }
        catch
        {
            source.Set(ZDOVars.s_items, originalSource);
            // Native Container.Load leaves its live inventory unchanged for an absent
            // field. Reload an empty payload first, then restore the original absence.
            target.Set(ZDOVars.s_items, originalDestination ?? NativeInventory.Empty());
            try { try { reloadSource?.Invoke(); } finally { reloadDestination?.Invoke(); } }
            finally
            {
                if (originalDestination == null && target.RemoveByteArray(ZDOVars.s_items)) { target.DataRevision++; ZDOMan.instance.SetDirtySector(target); }
            }
            throw;
        }
    }
    private static bool Same(byte[]? expected, byte[]? actual)
        => expected == null ? actual == null : actual != null && expected.AsSpan().SequenceEqual(actual);
}
