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
