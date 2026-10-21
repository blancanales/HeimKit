namespace HeimKit.Features.TameTravel.Models;

internal sealed class FollowingSession
{
    public AnimalTransfer Lease = null!;
    public bool Acquired;
    public bool Started;
    public bool Stopped;
    public float Requested;
    public float ContextAt = float.NegativeInfinity;
    public float NextContext;
    public float UnreadySince = -1;
    public float StopDistance;
    public float ResumeDistance;
    public UnityEngine.Vector3 ContextPosition;
}
