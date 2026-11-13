using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using BepInEx.Logging;
using HeimKit.Configuration;
namespace HeimKit.Core.Diagnostics;

// Optional controller-pass measurements, shared by the world-processing diagnostics.
internal sealed class ProcessingTiming
{
    private readonly ManualLogSource _log;
    private readonly string _name;
    private readonly DebugLoggingPolicy _policy;
    private float _next;
    private int _passes;
    private double _total, _maximum;
    private Dictionary<string, (long Calls, double Total, double Maximum)>? _stages;
    private List<string>? _stageNames;
    public ProcessingTiming(ManualLogSource log, string name, DebugLoggingPolicy policy) { _log = log; _name = name; _policy = policy; }
    public IDisposable Measure(float now, Func<int> scope) => new Measurement(this, now, scope);
    // Value scope for consumers that retain their scope callback once at construction.
    // Existing reference-based callers keep their disposal contract.
    internal TimedScope MeasureValue(float now, Func<int> scope) => new(this, now, scope);
    internal void RegisterStages(params string[] names)
    {
        _stages ??= new(); _stageNames ??= new();
        foreach (string name in names)
        {
            if (!LogTopic.Valid(name)) throw new ArgumentException("Invalid processing stage.", nameof(names));
            if (_stages.ContainsKey(name)) continue;
            if (_stageNames.Count >= 16) throw new ArgumentException("Too many processing stages.", nameof(names));
            _stages.Add(name, default); _stageNames.Add(name);
        }
    }
    internal StageScope MeasureStage(string stage) => _stages != null && _stages.ContainsKey(stage) ? new(this, stage) : default;
    internal struct StageScope : IDisposable
    {
        private readonly ProcessingTiming? _owner;
        private readonly string? _stage;
        private readonly long _started;
        private bool _done;
        internal StageScope(ProcessingTiming owner, string stage)
        { _owner = owner; _stage = stage; _started = Stopwatch.GetTimestamp(); _done = false; }
        public void Dispose()
        {
            if (_owner == null || _done) return;
            _done = true;
            _owner.CompleteStage(_stage!, (Stopwatch.GetTimestamp() - _started) * 1000d / Stopwatch.Frequency);
        }
    }
    private void CompleteStage(string stage, double duration)
    {
        PerformanceStages.Record(_name, stage, duration);
        if (!_policy.Enabled || _stages == null || !_stages.TryGetValue(stage, out var data)) return;
        _stages[stage] = (data.Calls + 1, data.Total + duration, Math.Max(data.Maximum, duration));
    }
    private void FlushStages()
    {
        if (_stages == null || _stageNames == null) return;
        if (!DiagnosticLog.DebugAvailable(_log, _name)) return;
        var summary = new System.Text.StringBuilder();
        foreach (string name in _stageNames)
        {
            var data = _stages[name];
            if (data.Calls == 0) continue;
            if (summary.Length > 0) summary.Append("; ");
            summary.Append(name).Append(" calls=").Append(data.Calls).Append(" avg/max_ms=")
                .Append((data.Total / data.Calls).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('/')
                .Append(data.Maximum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            _stages[name] = default;
        }
        if (summary.Length > 0) DiagnosticLog.Write(_log, _name, CoreLogTopics.Timing,
            "Stage measurements since the previous emitted stage summary: " + summary + ".", level: LogLevel.Debug);
    }
    internal struct TimedScope : IDisposable
    {
        private readonly ProcessingTiming? _owner;
        private readonly Func<int>? _scope;
        private readonly float _now;
        private readonly long _started;
        private bool _done;
        internal TimedScope(ProcessingTiming owner, float now, Func<int> scope)
        { _owner = owner; _scope = scope; _now = now; _started = Stopwatch.GetTimestamp(); _done = false; }
        public void Dispose()
        {
            if (_owner == null || _done) return;
            _done = true;
            _owner.Complete(_now, _scope!(), (Stopwatch.GetTimestamp() - _started) * 1000d / Stopwatch.Frequency);
        }
    }
    private void Complete(float now, int scope, double duration)
    {
        if (!_policy.Enabled) return;
        _passes++; _total += duration; _maximum = Math.Max(_maximum, duration);
        if (now < _next) return;
        _next = now + 60;
        if (scope > 0 && DiagnosticLog.DebugAvailable(_log, _name))
            DiagnosticLog.Write(_log, _name, CoreLogTopics.Timing,
                $"{_passes} passes in the last minute, avg {(_total / _passes).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} ms, max {_maximum.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} ms.",
                new (string, object?)[] { ("scope", scope) }, LogLevel.Debug);
        FlushStages();
        _passes = 0; _total = _maximum = 0;
    }
    private sealed class Measurement : IDisposable
    {
        private readonly ProcessingTiming _owner;
        private readonly float _now;
        private readonly Func<int> _scope;
        private readonly long _started = Stopwatch.GetTimestamp();
        private bool _done;
        public Measurement(ProcessingTiming owner, float now, Func<int> scope) { _owner = owner; _now = now; _scope = scope; }
        public void Dispose()
        {
            if (_done) return; _done = true;
            double elapsed = (Stopwatch.GetTimestamp() - _started) * 1000d / Stopwatch.Frequency;
            _owner.Complete(_now, _scope(), elapsed);
        }
    }
}
