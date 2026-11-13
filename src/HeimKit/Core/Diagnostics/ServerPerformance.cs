using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using BepInEx.Logging;
using HeimKit.Configuration;

namespace HeimKit.Core.Diagnostics;

// Constant-space elapsed measurements. Native callbacks and log listeners can run
// outside Plugin.Update; their counters deliberately have a separate boundary.
internal static class ServerPerformance
{
    internal sealed class Counter
    {
        private long _calls, _ticks, _maximum;
        internal bool Add(long ticks)
        {
            Interlocked.Increment(ref _calls); Interlocked.Add(ref _ticks, ticks);
            long seen = Interlocked.Read(ref _maximum);
            while (ticks > seen) { long prior = Interlocked.CompareExchange(ref _maximum, ticks, seen); if (prior == seen) return true; seen = prior; }
            return false;
        }
        internal (long Calls, double Total, double Maximum) Take() =>
            (Interlocked.Exchange(ref _calls, 0), Milliseconds(Interlocked.Exchange(ref _ticks, 0)), Milliseconds(Interlocked.Exchange(ref _maximum, 0)));
    }
    internal static readonly Counter LogFormat = new(), LogListeners = new(), NativeContext = new();
    internal static readonly Counter OwnershipAttempts = new(), SourceRequests = new(), RefreshGroups = new(), RetryDeferrals = new();
    internal static readonly Counter SceneMaintenance = new(), SceneRetention = new(), ScenePass = new();
    internal static readonly Counter SaveGuardFallbacks = new();
    internal static readonly Counter CandidateTrims = new(), CandidateFallbacks = new();
    private static int _candidateRecordsMax, _candidateCellsMax;
    internal static void Candidates(int records, int cells)
    {
        if (records > _candidateRecordsMax) _candidateRecordsMax = records;
        if (cells > _candidateCellsMax) _candidateCellsMax = cells;
    }
    private static long _lastUpdate;
    internal static double CurrentInterval { get; private set; }
    internal static int UpdateCollections { get; private set; }
    internal static long UpdateSequence { get; private set; }
    private static float _nextReport;
    private static int _updates, _stalls, _gen0, _gen1, _gen2;
    private static int _playersMax, _recordsMax, _instancesMax, _regionsMax;
    private static long _lastDeferrals;
    private static long _lastCombatDeferrals;
    private static int _combatUpdates, _bossUpdates, _raidUpdates;
    private static double _gapMax, _workTotal, _workMax, _moduleMax, _cpu;
    private static string _module = "none";
    private static string _logFeature = "none", _logTopic = "lifecycle";
    private static string _updateModule = "none", _previousModule = "none", _gapModule = "none";
    private static double _updateModuleMax, _previousWork, _previousModuleMax, _gapWork, _gapModuleMax;
    internal static void SlowestListener(string feature, string topic) { _logFeature = feature; _logTopic = topic; }
    internal static string ContextWork() => "shared_frame_module=" + _updateModule + " max_ms=" + F(_updateModuleMax) +
        " deferrals_total=" + Scheduling.ModuleScheduler.Current.Deferrals + " combat_deferrals_total=" + Scheduling.ModuleScheduler.Current.CombatDeferrals;
    private static long _optionalLogTicks;
    internal static bool OptionalOutputAvailable => _lastUpdate == 0 || Milliseconds(Interlocked.Read(ref _optionalLogTicks)) < 2;
    internal static void OptionalOutput(long elapsed) => Interlocked.Add(ref _optionalLogTicks, elapsed);
    internal static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    internal static void Reset()
    {
        _lastUpdate = 0; _nextReport = 0; _updates = _stalls = 0;
        CurrentInterval = 0; UpdateSequence = 0; UpdateCollections = GC.CollectionCount(0);
        PerformanceStages.EndArrivalFrame(null);
        _optionalLogTicks = 0;
        _gapMax = _workTotal = _workMax = _moduleMax = _cpu = 0; _module = "none";
        _logFeature = _updateModule = _previousModule = _gapModule = "none"; _logTopic = "lifecycle";
        _updateModuleMax = _previousWork = _previousModuleMax = _gapWork = _gapModuleMax = 0;
        _gen0 = GC.CollectionCount(0); _gen1 = GC.CollectionCount(1); _gen2 = GC.CollectionCount(2);
        LogFormat.Take(); LogListeners.Take(); NativeContext.Take();
        OwnershipAttempts.Take(); SourceRequests.Take(); RefreshGroups.Take(); RetryDeferrals.Take();
        SceneMaintenance.Take(); SceneRetention.Take(); ScenePass.Take();
        PerformanceStages.Reset();
        SaveGuardFallbacks.Take();
        CandidateTrims.Take(); CandidateFallbacks.Take();
        _candidateRecordsMax = _candidateCellsMax = 0;
        _combatUpdates = _bossUpdates = _raidUpdates = 0; _lastCombatDeferrals = 0;
        _playersMax = _recordsMax = _instancesMax = _regionsMax = 0; _lastDeferrals = 0;
    }
    internal static long BeginUpdate()
    {
        long stamp = Stopwatch.GetTimestamp();
        UpdateSequence++; UpdateCollections = GC.CollectionCount(0);
        CurrentInterval = _lastUpdate == 0 ? 0 : Milliseconds(stamp - _lastUpdate);
        Interlocked.Exchange(ref _optionalLogTicks, 0);
        if (_lastUpdate != 0)
        {
            double gap = Milliseconds(stamp - _lastUpdate);
            if (gap > _gapMax) { _gapMax = gap; _gapWork = _previousWork; _gapModule = _previousModule; _gapModuleMax = _previousModuleMax; }
            if (gap >= 250) _stalls++;
        }
        _updateModuleMax = 0; _updateModule = "none";
        _lastUpdate = stamp; return stamp;
    }
    internal static void Module(string name, long started)
    {
        double elapsed = Milliseconds(Stopwatch.GetTimestamp() - started);
        PerformanceStages.Record(name, "module_tick", elapsed);
        if (elapsed > _moduleMax) { _module = name; _moduleMax = elapsed; }
        if (elapsed > _updateModuleMax) { _updateModule = name; _updateModuleMax = elapsed; }
    }
    internal static void EndUpdate(long started, float now, ManualLogSource log, int? players, int records, int instances, int regions)
    {
        double elapsed = Milliseconds(Stopwatch.GetTimestamp() - started);
        _updates++; _workTotal += elapsed; _workMax = Math.Max(_workMax, elapsed);
        if (Scheduling.ModuleScheduler.Current.Combat.Active(now)) { _combatUpdates++; if (Scheduling.ModuleScheduler.Current.Combat.Boss) _bossUpdates++; if (Scheduling.ModuleScheduler.Current.Combat.Raid) _raidUpdates++; }
        _previousWork = elapsed; _previousModule = _updateModule; _previousModuleMax = _updateModuleMax;
        _playersMax = Math.Max(_playersMax, players ?? 0); _recordsMax = Math.Max(_recordsMax, records);
        _instancesMax = Math.Max(_instancesMax, instances); _regionsMax = Math.Max(_regionsMax, regions);
        if (_nextReport == 0) { _nextReport = now + 60; if (DiagnosticsSettings.VerboseEnabled) SampleProcess(out _, out _); return; }
        if (now < _nextReport) return;
        _nextReport = now + 60;
        var format = LogFormat.Take(); var listeners = LogListeners.Take(); var context = NativeContext.Take();
        string logSubject = _logFeature + "." + _logTopic; _logFeature = "none"; _logTopic = "lifecycle";
        var ownership = OwnershipAttempts.Take(); var source = SourceRequests.Take(); var refresh = RefreshGroups.Take(); var retry = RetryDeferrals.Take();
        var maintenance = SceneMaintenance.Take(); var retention = SceneRetention.Take(); var scene = ScenePass.Take();
        var saveFallbacks = SaveGuardFallbacks.Take();
        var trims = CandidateTrims.Take(); var fallbacks = CandidateFallbacks.Take();
        long deferred = Scheduling.ModuleScheduler.Current.Deferrals;
        long combatDeferred = Scheduling.ModuleScheduler.Current.CombatDeferrals;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        if (DiagnosticsSettings.VerboseEnabled)
        {
            // Keep the CPU baseline current across windows even when Debug output is suppressed.
            bool sampled = SampleProcess(out double cpu, out long workingSet);
            if ((_playersMax > 0 || _stalls > 0 || saveFallbacks.Calls > 0) && DiagnosticLog.DebugAvailable(log, "HeimKit", verbose: true))
            {
                string process = sampled ? " Process CPU " + F(cpu) + " ms." : "";
                if (workingSet > 0) process += " Working set " + (workingSet / 1048576) + " MiB.";
                DiagnosticLog.Write(log, "HeimKit", CoreLogTopics.Timing, "Performance window: " + _updates + " updates; update interval max " + F(_gapMax) +
                    " ms, intervals >=250 ms " + _stalls + "; measured HeimKit avg/max " + F(_workTotal / _updates) + "/" + F(_workMax) +
                    " ms; slowest module " + _module + " " + F(_moduleMax) + " ms; scope maxima players/regions/frame records/instances " + _playersMax + "/" + _regionsMax + "/" + _recordsMax + "/" + _instancesMax +
                    ". Native context calls/max " + context.Calls + "/" + F(context.Maximum) +
                    " ms; log records " + listeners.Calls + ", format/listener max " + F(format.Maximum) + "/" + F(listeners.Maximum) +
                    " ms, slowest listener subject " + logSubject + "; ownership/source requests/refresh groups/retry deferrals " + ownership.Calls + "/" + source.Calls + "/" + refresh.Calls + "/" + retry.Calls +
                    "; largest update interval followed measured HeimKit " + F(_gapWork) + " ms (module " + _gapModule + " " + F(_gapModuleMax) + " ms)" +
                    "; scene pass/maintenance/retention max " + F(scene.Maximum) + "/" + F(maintenance.Maximum) + "/" + F(retention.Maximum) + " ms" +
                    "; combat pressure/boss updates " + _combatUpdates + "/" + _bossUpdates + ", combat deferrals " + Math.Max(0, combatDeferred - _lastCombatDeferrals) +
                    "; raid-paused updates " + _raidUpdates +
                    "; native save guard fallbacks " + saveFallbacks.Calls +
                    "; candidate records/cells max " + _candidateRecordsMax + "/" + _candidateCellsMax +
                    ", trimmed cells " + trims.Calls + ", selection fallbacks " + fallbacks.Calls +
                    "; cooperative deferrals " + Math.Max(0, deferred - _lastDeferrals) + ". Managed memory " + (GC.GetTotalMemory(false) / 1048576) + " MiB; GC collections " + (g0 - _gen0) + "/" + (g1 - _gen1) + "/" + (g2 - _gen2) + "." + process, level: LogLevel.Debug, verbose: true);
            }
        }
        PerformanceStages.Report(log);
        _updates = _stalls = 0; _gapMax = _workTotal = _workMax = _moduleMax = 0; _module = "none";
        _gen0 = g0; _gen1 = g1; _gen2 = g2;
        _playersMax = _recordsMax = _instancesMax = _regionsMax = 0; _lastDeferrals = deferred;
        _candidateRecordsMax = _candidateCellsMax = 0;
        _combatUpdates = _bossUpdates = _raidUpdates = 0; _lastCombatDeferrals = combatDeferred;
    }
    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
    private static bool SampleProcess(out double cpu, out long memory)
    {
        cpu = 0; memory = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            double current = process.TotalProcessorTime.TotalMilliseconds; cpu = Math.Max(0, current - _cpu); _cpu = current;
            memory = process.WorkingSet64; return true;
        }
        catch (Exception error) when (error is NotSupportedException || error is System.ComponentModel.Win32Exception || error is InvalidOperationException) { return false; }
    }
    internal static NativeScope MeasureNativeContext() => new();
    internal static WorkScope Measure(Counter counter) => new(counter);
    internal struct WorkScope : IDisposable
    {
        private readonly Counter _counter;
        private readonly long _started;
        private bool _done;
        internal WorkScope(Counter counter) { _counter = counter; _started = Stopwatch.GetTimestamp(); _done = false; }
        public void Dispose() { if (_done) return; _done = true; _counter.Add(Stopwatch.GetTimestamp() - _started); }
    }
    internal struct NativeScope : IDisposable
    {
        private readonly long _started;
        private bool _done;
        public NativeScope() { _started = Stopwatch.GetTimestamp(); _done = false; }
        public void Dispose() { if (_done) return; _done = true; NativeContext.Add(Stopwatch.GetTimestamp() - _started); }
    }
}
