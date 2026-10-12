namespace HeimKit.Features.AFKRaidWatch.Models;
public sealed class RaidStartObservation
{
    public float ObservedAt { get; set; }
    public string Requested { get; set; } = "none";
    public string? Current { get; set; }
    public string Origin { get; set; } = "explicit_or_other";
    public bool StateChanged { get; set; }
    public bool Failed { get; set; }
    public bool Started => !Failed && Requested != "none" && Current == Requested && StateChanged;
    public string Outcome => Failed ? "native_exception_partial_state_unknown" : Started ? "random_event_started_server_state" : Current == null && StateChanged ? "random_event_cleared" : "no_confirmed_event_start";
}
