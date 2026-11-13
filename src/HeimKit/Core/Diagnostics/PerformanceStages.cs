using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Core.Diagnostics;

// Fixed-cardinality main-thread health evidence, independent of Debug admission.
// Counts come from callers; this never queries worlds or retains object identities.
internal static class PerformanceStages
{
    private sealed class Entry
    {
        internal long Calls, Items, Bytes;
        internal double Total, Maximum;
        internal int Collections;
        internal string? Subject;
        internal long ArrivalEpoch = -1, ArrivalCalls;
        internal double ArrivalTotal, ArrivalMaximum;
        internal long ArrivalItems, ArrivalBytes;
        internal int ArrivalCollections;
        internal string? ArrivalSubject;
        internal void Clear() { Calls = Items = Bytes = 0; Total = Maximum = 0; Collections = 0; Subject = null; }
    }
    private const int Capacity = 96;
    private static readonly Dictionary<(string Module, string Stage), Entry> Entries = new();
    private static long _arrivalEpoch;
    internal static bool CaptureArrivals { get; set; }
    private static readonly Dictionary<string, (float At, int Pending, bool Active)> Progress = new();
    internal static void ArrivalProgress(string work, float now, int pending, bool active)
    {
        if (CaptureArrivals && (Progress.Count < 8 || Progress.ContainsKey(work))) Progress[work] = (now, pending, active);
    }
    internal static string ArrivalProgress(float now)
    {
        var text = new StringBuilder("coverage=partial");
        foreach (var pair in Progress)
        {
            var value = pair.Value;
            text.Append(' ').Append(pair.Key).Append("[age_s=").Append(F(Math.Max(0, now - value.At)))
                .Append(",pending=").Append(value.Pending).Append(",active=").Append(value.Active ? "true" : "false").Append(']');
        }
        return text.ToString();
    }
    internal static void ClearArrivalProgress() => Progress.Clear();
    internal readonly struct ArrivalSample
    {
        internal readonly string Module, Stage;
        internal readonly long Calls, Items, Bytes;
        internal readonly double Total, Maximum;
        internal readonly int Collections;
        internal readonly string? Subject;
        internal ArrivalSample(string module, string stage, long calls, double total, double maximum, long items, long bytes, int collections, string? subject)
        { Module = module; Stage = stage; Calls = calls; Total = total; Maximum = maximum; Items = items; Bytes = bytes; Collections = collections; Subject = subject; }
    }
    // Reuse the existing scope measurements, including callbacks between plugin updates.
    // Draining never resets the independent normal performance-window counters.
    internal static void EndArrivalFrame(Action<ArrivalSample>? observe)
    {
        try
        {
            if (observe != null)
                foreach (var pair in Entries)
                {
                    var e = pair.Value;
                    if (e.ArrivalEpoch == _arrivalEpoch && e.ArrivalCalls > 0)
                        observe(new(pair.Key.Module, pair.Key.Stage, e.ArrivalCalls, e.ArrivalTotal, e.ArrivalMaximum,
                            e.ArrivalItems, e.ArrivalBytes, e.ArrivalCollections, e.ArrivalSubject));
                }
        }
        finally { _arrivalEpoch++; }
    }
    internal static Scope Measure(string module, string stage, long items = -1, long bytes = -1, string? subject = null) => new(module, stage, items, bytes, subject);
    internal static void Record(string module, string stage, double milliseconds, long items = -1, long bytes = -1, int collections = -1, string? subject = null)
    {
        var key = (module, stage);
        if (!Entries.TryGetValue(key, out var entry))
        {
            if (Entries.Count >= Capacity) return;
            Entries.Add(key, entry = new Entry());
        }
        entry.Calls++; entry.Total += milliseconds;
        if (CaptureArrivals)
        {
            if (entry.ArrivalEpoch != _arrivalEpoch)
            { entry.ArrivalEpoch = _arrivalEpoch; entry.ArrivalCalls = 0; entry.ArrivalTotal = entry.ArrivalMaximum = 0; }
            entry.ArrivalCalls++; entry.ArrivalTotal += milliseconds;
            if (milliseconds >= entry.ArrivalMaximum)
            {
                entry.ArrivalMaximum = milliseconds; entry.ArrivalItems = items; entry.ArrivalBytes = bytes;
                entry.ArrivalCollections = collections;
                entry.ArrivalSubject = subject == null ? null : subject.Length <= 96 ? subject : subject.Substring(0, 96);
            }
        }
        if (milliseconds <= entry.Maximum) return;
        entry.Maximum = milliseconds; entry.Items = items; entry.Bytes = bytes; entry.Collections = collections;
        entry.Subject = subject == null ? null : subject.Length <= 96 ? subject : subject.Substring(0, 96);
    }
    internal static void Reset() { foreach (var entry in Entries.Values) entry.Clear(); }
    internal static void Report(ManualLogSource log)
    {
        if (!DiagnosticsSettings.VerboseEnabled || !DiagnosticLog.DebugAvailable(log, "HeimKit", verbose: true)) { Reset(); return; }
        var text = new StringBuilder();
        for (int i = 0; i < 6; i++)
        {
            (string Module, string Stage) key = default;
            Entry? best = null;
            foreach (var pair in Entries)
                if (pair.Value.Maximum >= 16 && (best == null || pair.Value.Maximum > best.Maximum)) { key = pair.Key; best = pair.Value; }
            if (best == null) break;
            if (text.Length > 0) text.Append("; ");
            text.Append(key.Module).Append('.').Append(key.Stage).Append(" calls=").Append(best.Calls)
                .Append(" avg/max_ms=").Append(F(best.Total / best.Calls)).Append('/').Append(F(best.Maximum));
            if (best.Items >= 0) text.Append(" max_sample_items=").Append(best.Items);
            if (best.Bytes >= 0) text.Append(" max_sample_bytes=").Append(best.Bytes);
            if (best.Collections >= 0) text.Append(" max_sample_gc0=").Append(best.Collections);
            if (best.Subject != null) text.Append(" subject=").Append(best.Subject);
            best.Maximum = 0;
        }
        if (text.Length > 0) DiagnosticLog.Write(log, "HeimKit", CoreLogTopics.Timing, "Slow work stages (elapsed scopes may overlap): " + text + ".", level: LogLevel.Debug, verbose: true);
        Reset();
    }
    private static string F(double n) => n.ToString("F2", CultureInfo.InvariantCulture);
    internal struct Scope : IDisposable
    {
        private readonly string _module, _stage;
        private readonly long _started, _items, _bytes;
        private readonly int _collections;
        private readonly string? _subject;
        private bool _done;
        internal Scope(string module, string stage, long items, long bytes, string? subject)
        { _module = module; _stage = stage; _items = items; _bytes = bytes; _subject = subject; _collections = GC.CollectionCount(0); _started = Stopwatch.GetTimestamp(); _done = false; }
        public void Dispose()
        {
            if (_done || _started == 0) return;
            _done = true;
            Record(_module, _stage, ServerPerformance.Milliseconds(Stopwatch.GetTimestamp() - _started), _items, _bytes, GC.CollectionCount(0) - _collections, _subject);
        }
    }
}
