using System;
using System.Diagnostics;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;

namespace HeimKit.Core.Scheduling;

// A host controller owns one instance per stable work type. Measurements live only for its lifetime.
internal sealed class AdaptiveOperation : IDisposable
{
    private readonly ManualLogSource _log;
    private readonly DebugLoggingPolicy _policy;
    private readonly string _feature, _name;
    private readonly ModuleScheduler _scheduler;
    private readonly double _slice;
    private readonly int _maximum;
    private int _limit = 1, _samples, _healthy, _units, _spikes, _budgetWaits, _cooldownWaits, _combatWaits;
    private int _peakUnits, _bytes, _peakBytes;
    private double _unitMean, _peak, _peakBaseline;
    private float _delay, _eligible, _now, _nextReport, _pendingSince = -1, _oldest;
    private bool _oneRequestedUnit;
    private long _start, _blockedFrame = -1;

    internal AdaptiveOperation(ManualLogSource log, DebugLoggingPolicy policy, string feature, string name,
        int maximum = 1, double slice = 2, ModuleScheduler? scheduler = null)
    {
        _log = log; _policy = policy; _feature = feature; _name = name;
        _maximum = maximum; _slice = slice; _scheduler = scheduler ?? ModuleScheduler.Current;
    }

    internal bool CanStartEarly(float now) => now >= _eligible && _scheduler.Frame != _blockedFrame &&
        _scheduler.RemainingMilliseconds > Math.Min(_unitMean, _scheduler.FrameBudgetMilliseconds / 2);

    // Operator-requested jobs keep making bounded progress during combat or sustained host pressure.
    internal bool RequestedDue(object owner, float now)
    {
        if (now < _eligible || _scheduler.Frame == _blockedFrame) return false;
        _oneRequestedUnit = !_scheduler.HasBudget;
        return _scheduler.Due(owner, _name, now, _oneRequestedUnit ? .25f : 0);
    }

    internal bool Due(object owner, float now, float interval)
    {
        var scheduler = _scheduler;
        if (now < _eligible || scheduler.Frame == _blockedFrame)
        {
            _cooldownWaits++;
            Wait(now);
            return false;
        }
        long previous = scheduler.Deferrals;
        if (scheduler.Due(owner, _name, now, interval, cooperative: true, estimate: _unitMean))
        {
            if (_pendingSince >= 0) _oldest = Math.Max(_oldest, now - _pendingSince);
            _pendingSince = -1;
            return true;
        }
        if (scheduler.Deferrals != previous)
        {
            if (scheduler.CombatActive) _combatWaits++; else _budgetWaits++;
            Wait(now);
        }
        return false;
    }

    private void Wait(float now)
    {
        if (_pendingSince < 0) _pendingSince = now;
    }

    internal AdaptiveOperation Begin(float now)
    {
        _now = now; _units = _bytes = 0; _start = _scheduler.Timestamp;
        return this;
    }

    internal void Count(int units = 1, int bytes = 0) { _units += units; _bytes += bytes; }

    internal void CountCompletion()
    {
        if (_units == 0 && Elapsed >= 1) Count();
    }

    internal bool CanTake => _units < _limit && (_units == 0 ||
        (!_oneRequestedUnit && Elapsed + _unitMean < _slice && _scheduler.RemainingMilliseconds > _unitMean));

    internal bool Take()
    {
        if (!CanTake) return false;
        _units++;
        return true;
    }

    private double Elapsed => (_scheduler.Timestamp - _start) * 1000d / Stopwatch.Frequency;

    public void Dispose()
    {
        if (_units == 0) return;
        double elapsed = Elapsed;
        double baseline = _unitMean * _units;
        PerformanceStages.Record(_feature, _name, elapsed, _units, _bytes);
        bool spike = _samples >= 8 && elapsed > 1 && elapsed > 3 * baseline;
        bool expensive = spike || elapsed > Math.Min(_slice, _scheduler.FrameBudgetMilliseconds);
        if (elapsed > _peak)
        {
            _peak = elapsed; _peakBaseline = baseline; _peakUnits = _units; _peakBytes = _bytes;
        }
        if (_samples == 0 || (!expensive && _delay == 0))
            _unitMean = _samples == 0 ? elapsed / _units : _unitMean + (elapsed / _units - _unitMean) / 8;
        if (_samples < 8) _samples++;
        if (expensive)
        {
            _spikes++; _healthy = 0; _limit = Math.Max(1, _limit / 2);
            _blockedFrame = _scheduler.Frame + 1;
            if (_units == 1) _delay = Math.Min(1, Math.Max(.032f, _delay * 2));
            _eligible = _now + _delay;
        }
        else if (++_healthy >= 8)
        {
            _healthy = 0; _limit = Math.Min(_maximum, _limit + 1);
            _delay = _delay <= .032f ? 0 : _delay / 2;
        }
        if (_delay > 0) _eligible = _now + _delay;
        if (_now < _nextReport) return;
        _nextReport = _now + 60;
        if (_policy.Enabled && DiagnosticLog.DebugAvailable(_log, _feature))
            DiagnosticLog.Write(_log, _feature, CoreLogTopics.Timing,
                $"Adaptive work {_name}: peak_ms={_peak:F2} baseline_for_peak_ms={_peakBaseline:F2} peak_units={_peakUnits} peak_bytes={_peakBytes} unit_limit={_limit} expensive_batches={_spikes} budget_deferrals={_budgetWaits} cooldown_deferrals={_cooldownWaits} combat_deferrals={_combatWaits} oldest_wait_s={_oldest:F2}.",
                level: LogLevel.Debug);
        _spikes = _budgetWaits = _cooldownWaits = _combatWaits = 0;
        _oldest = 0; _peak = 0;
    }
}
