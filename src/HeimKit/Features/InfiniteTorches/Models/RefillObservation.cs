namespace HeimKit.Features.InfiniteTorches.Models;

public readonly struct RefillObservation
{
    public readonly long Owner;
    public readonly float Fuel;
    public RefillObservation(long owner, float fuel) { Owner = owner; Fuel = fuel; }
}
