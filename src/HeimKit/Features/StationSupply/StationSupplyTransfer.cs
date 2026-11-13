using System;
using System.Collections.Generic;
using HeimKit.Core.Native;
using HeimKit.Features.StationSupply.Models;
namespace HeimKit.Features.StationSupply;
internal static class StationSupplyTransfer
{
    internal static bool TryCommit(SupplyTransaction t, byte[] original, InventoryWithdrawal withdrawal, string item, long timestamp, Action? reload = null)
        => TryCommit(t, original, withdrawal, item, timestamp, out _, reload);
    internal static bool TryCommit(SupplyTransaction t, byte[] original, InventoryWithdrawal withdrawal, string item, long timestamp, out string reason, Action? reload = null)
        => Commit(t, original, withdrawal.Bytes, new[] { withdrawal }, new[] { item }, timestamp, out reason, reload);
    internal static bool TryCommit(SupplyTransaction t, byte[] original, InventoryBatch batch, IReadOnlyList<string> items, long timestamp, out string reason, Action? reload = null)
        => Commit(t, original, batch.Bytes, batch.Items, items, timestamp, out reason, reload);
    private static bool Commit(SupplyTransaction t, byte[] original, byte[] source, IReadOnlyList<InventoryWithdrawal> resources, IReadOnlyList<string> items,
        long timestamp, out string reason, Action? reload)
    {
        reason = "resource_transferred";
        int count = resources.Count;
        if (count < 1 || count > 64 || items.Count != count || t.Intake.Kind == SupplyKind.Fermenter && count != 1) { reason = "invalid_resource_stack"; return false; }
        bool batchCheated = false;
        for (int i = 0; i < count; i++) batchCheated |= resources[i].Cheated;
        var station = t.Station.Record; var chest = t.Chest.Record;
        var current = chest.GetByteArray(ZDOVars.s_items);
        if (current == null || !original.AsSpan().SequenceEqual(current)) { reason = "source_inventory_changed"; return false; }
        float fuel = station.GetFloat(ZDOVars.s_fuel, t.Intake.DefaultFuel);
        int queued = station.GetInt(ZDOVars.s_queued), content = station.GetInt(ZDOVars.s_content);
        if (StationSupplyRules.VisitRoom(t.Intake.Kind, fuel, queued, content, t.Intake.Capacity, count) < count) { reason = "station_capacity_changed"; return false; }
        long start = station.GetLong(ZDOVars.s_startTime); bool cheated = station.GetBool(ZDOVars.s_cheatedQueued);
        float addedFuel = fuel; for (int i = 0; i < count; i++) addedFuel += 1f;
        var queueKeys = t.Intake.Kind == SupplyKind.Material ? new string[count] : Array.Empty<string>();
        var oldItems = new string[queueKeys.Length];
        for (int i = 0; i < queueKeys.Length; i++) { queueKeys[i] = "item" + (queued + i); oldItems[i] = station.GetString(queueKeys[i]); }
        try
        {
            chest.Set(ZDOVars.s_items, source);
            if (t.Intake.Kind == SupplyKind.Fuel) station.Set(ZDOVars.s_fuel, addedFuel);
            else
            {
                station.Set(ZDOVars.s_cheatedQueued, batchCheated || cheated && t.Intake.Kind == SupplyKind.Material && (queued > 0 || station.GetInt(ZDOVars.s_spawnAmount) > 0));
                if (t.Intake.Kind == SupplyKind.Material) { for (int i = 0; i < count; i++) station.Set(queueKeys[i], items[i]); station.Set(ZDOVars.s_queued, queued + count); }
                else { station.Set(ZDOVars.s_content, resources[0].Prefab); station.Set(ZDOVars.s_startTime, timestamp); }
            }
            reload?.Invoke();
            return true;
        }
        catch
        {
            chest.Set(ZDOVars.s_items, original);
            if (t.Intake.Kind == SupplyKind.Fuel) station.Set(ZDOVars.s_fuel, fuel);
            else
            {
                station.Set(ZDOVars.s_cheatedQueued, cheated);
                if (t.Intake.Kind == SupplyKind.Material) { station.Set(ZDOVars.s_queued, queued); for (int i = 0; i < queueKeys.Length; i++) station.Set(queueKeys[i], oldItems[i]); }
                else { station.Set(ZDOVars.s_content, content); station.Set(ZDOVars.s_startTime, start); }
            }
            reload?.Invoke();
            throw;
        }
    }
}
