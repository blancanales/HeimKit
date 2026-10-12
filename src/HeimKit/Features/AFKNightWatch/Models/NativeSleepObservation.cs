using HeimKit.Features.AFKNightWatch;
using System;
namespace HeimKit.Features.AFKNightWatch.Models;
public sealed class NativeSleepObservation
{
    public float ObservedAt { get; set; }
    public bool? Server { get; set; }
    public bool? Sleeping { get; set; }
    public bool? SleepingAfter { get; set; }
    public double? GameTimeBefore { get; set; }
    public double? GameTimeAfter { get; set; }
    public bool? Afternoon { get; set; }
    public bool? Night { get; set; }
    public bool? SkippingBefore { get; set; }
    public bool? SkippingAfter { get; set; }
    public double? CooldownRemaining { get; set; }
    public bool Failed { get; set; }
    public string Gate => Server != true ? "server_gate_unknown_or_false" : Sleeping == true ? "already_sleeping" : SkippingBefore == null ? "time_skipping_gate_unknown" : SkippingBefore == true ? "already_time_skipping" :
        Afternoon == null || Night == null ? "time_gate_unknown" : Afternoon != true && Night != true ? "outside_sleep_time" : CooldownRemaining == null ? "cooldown_unknown" : CooldownRemaining > 0 ? "cooldown_pending" : "time_gates_open_vote_required";
    public string Outcome => Failed ? "native_exception" : SkippingBefore == null || SkippingAfter == null ? "unknown" :
        SkippingBefore == false && SkippingAfter == true ? "time_skip_became_active" : Sleeping == true && SleepingAfter == false && SkippingAfter == false ? "sleep_session_ended_server_state" : SkippingBefore == true && SkippingAfter == false ? "time_skip_ended" : "no_time_skip_state_change";
}
