using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Core.Diagnostics;
// Semantic changes are polled at most once a minute; unchanged relevant scope gets a compact cadence.
public sealed class SummaryCadence
{
    private float _poll = float.NegativeInfinity, _emitted = float.NegativeInfinity;
    private string? _key;
    public bool PollReady(float now) => !(now >= _poll && now - _poll < 60);
    public bool CanPoll(float now)
    {
        if (now >= _poll && now - _poll < 60) return false;
        _poll = now; return true;
    }
    public bool Changed { get; private set; }
    public bool ShouldEmit(float now, string key)
    {
        if (key == _key && now >= _emitted && now - _emitted < 300) return false;
        Changed = key != _key;
        _key = key; _emitted = now; return true;
    }
    public void Reset() { _key = null; }
}
