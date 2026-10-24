using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.WeatherGuard.Models;

internal struct WearProcessingState
{
    public bool Protected;
    public ZDOID Identity;
    public string? Prefab;
    public bool PreviousSuppression;
    public WearObservation? Observation;
    public WearObservation? PreviousObservation;
}
