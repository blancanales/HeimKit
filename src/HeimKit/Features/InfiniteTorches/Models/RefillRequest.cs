namespace HeimKit.Features.InfiniteTorches.Models;

internal readonly struct RefillRequest
{
    public readonly long Owner;
    public readonly float Before, Target, SentAt;
    public RefillRequest(long owner, float before, float target, float now)
    { Owner = owner; Before = before; Target = target; SentAt = now; }
}
