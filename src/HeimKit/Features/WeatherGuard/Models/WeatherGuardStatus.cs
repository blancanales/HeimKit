namespace HeimKit.Features.WeatherGuard.Models;

public sealed class WeatherGuardStatus
{
    public bool RainProtection { get; set; } = true;
    public bool UnderwaterProtection { get; set; } = true;
    public System.Collections.Generic.Dictionary<string, int> ReleaseTotals { get; set; } = new();
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
    public int Peers { get; set; }
    public int ReadyPeers { get; set; }
    public int CharacterWaits { get; set; }
    public System.Collections.Generic.Dictionary<string, int> Exclusions { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, int> ReleaseReasons { get; set; } = new();
    public long OwnerLosses { get; set; }
    public long WeatherGates { get; set; }
    public long WetOpportunities { get; set; }
    public long NativeCommands { get; set; }
    public long SupportPasses { get; set; }
}
