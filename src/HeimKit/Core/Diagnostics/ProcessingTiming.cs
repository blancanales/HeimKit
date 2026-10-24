using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Diagnostics;
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
    public ProcessingTiming(ManualLogSource log, string name, DebugLoggingPolicy policy) { _log = log; _name = name; _policy = policy; }
    public IDisposable Measure(float now, Func<int> scope) => new Measurement(this, now, scope);
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
