using System.Collections.Generic;

namespace HeimKit.Core.Scheduling;

// Separate from the short cooperative-work window used by other features.
internal sealed class BattlePause
{
    private readonly HashSet<object> _consumers = new();
    private float _lastEvidence;
    internal bool Requested => _consumers.Count != 0;
    internal bool Active { get; private set; }
    internal float Started { get; private set; }
    internal string Reason { get; private set; } = "";
    internal int PeakSample { get; private set; }
    internal void Register(object owner) => _consumers.Add(owner);
    internal void Unregister(object owner) => _consumers.Remove(owner);

    internal void Update(float now, string? evidence, int observed)
    {
        if (now < _lastEvidence) Reset();
        if (Requested && evidence != null)
        {
            if (!Active) { Started = now; PeakSample = 0; }
            Active = true; Reason = evidence; _lastEvidence = now;
            PeakSample = System.Math.Max(PeakSample, observed);
        }
        else if (!Requested || now - _lastEvidence >= 30) Active = false;
    }

    internal void Reset() { Active = false; _lastEvidence = Started = 0; PeakSample = 0; Reason = ""; }
}
