using System.Collections.Generic;
using System.Globalization;
using System.Text;
namespace HeimKit.Features.StationSupply.Models;

// Operational counts continue when optional records are suppressed. No item payloads or identities are retained.
internal sealed class SupplyCounts
{
    internal long Transactions, Transfers, Cancelled, Failures, SourceDrains, CacheResets, StateSnapshots, RefreshCompleted;
    internal long ManualQueued, ManualYielded, ManualReplayed, ManualUnavailable;
    private readonly Dictionary<string, long> _blocked = new();
    private StringBuilder? _text;
    internal bool HasActivity => Transactions != 0 || Transfers != 0 || Cancelled != 0 || Failures != 0 ||
        ManualQueued != 0 || ManualYielded != 0 || ManualReplayed != 0 || ManualUnavailable != 0 || _blocked.Count != 0;
    internal static bool Routine(string reason) => reason == "resource_transferred" || reason == "native_caches_refreshed" ||
        reason == "draining_native_state" || reason == "client_refresh_wait" || reason == "player_scope_ready";
    internal void Observe(string reason)
    {
        if (Routine(reason)) return;
        if (!_blocked.TryGetValue(reason, out long count) && _blocked.Count >= 127) { reason = "other"; _blocked.TryGetValue(reason, out count); }
        _blocked[reason] = count + 1;
    }
    internal string BlockedChecks()
    {
        var text = _text ??= new StringBuilder(); text.Clear();
        foreach (var pair in _blocked)
        {
            if (text.Length != 0) text.Append(',');
            text.Append(pair.Key).Append(':').Append(pair.Value.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }
    internal void Reset()
    {
        Transactions = Transfers = Cancelled = Failures = SourceDrains = CacheResets = StateSnapshots = RefreshCompleted = 0;
        ManualQueued = ManualYielded = ManualReplayed = ManualUnavailable = 0; _blocked.Clear();
    }
}
