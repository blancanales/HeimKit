namespace HeimKit.Features.WeatherGuard.Models;

public sealed class WeatherGuardStatus
{
    public int Protected { get; set; }
    public int Eligible { get; set; }
    public int Waiting { get; set; }
    public int WaitingTerrain { get; set; }
    public int WaitingInstance { get; set; }
    public int WaitingObjects { get; set; }
    public int Regions { get; set; }
    public int Nearby { get; set; }
    public int Distant { get; set; }
    public long Acquired { get; set; }
    public long Released { get; set; }
    public long BlockedData { get; set; }
    public long BlockedOwner { get; set; }
    public long BlockedBroadcast { get; set; }
    public long Commands { get; set; }
    public long WearPasses { get; set; }
    public bool Limited { get; set; }
}
