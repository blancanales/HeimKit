namespace HeimKit.Features.WeatherGuard.Models;

internal struct WearProcessingState
{
    public bool Protected;
    public WearObservation? Observation;
    public WearObservation? PreviousObservation;
}
