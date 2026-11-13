using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HeimKit.Core.Scheduling;

// Cooperative main-thread work. The caller retains/disposes the iterator; native
// calls are never preempted. Both elapsed time and unit count bound each admission.
internal static class WorkSlice
{
    internal static bool Advance(IEnumerator<bool> work, double milliseconds = 2, int maximumUnits = 64, Func<long>? clock = null)
    {
        clock ??= Stopwatch.GetTimestamp;
        long started = clock();
        for (int units = 0; units < maximumUnits; units++)
        {
            if (!work.MoveNext()) return true;
            if ((clock() - started) * 1000d / Stopwatch.Frequency >= milliseconds) break;
        }
        return false;
    }
}
