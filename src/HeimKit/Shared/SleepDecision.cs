namespace HeimKit.Shared;

public sealed class SleepDecision
{
    public int Participants { get; set; }
    public int Sleepers { get; set; }
    public int Required { get; set; }
    public int ExcludedIdle { get; set; }
    public bool Unknown { get; set; }
    public bool Met { get; set; }
}
