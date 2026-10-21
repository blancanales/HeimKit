namespace HeimKit.Features.TameTravel.Models;

internal sealed class FollowBinding
{
    public ZNetPeer Peer = null!;
    public ZDOID Character;
    public ZDOID Animal;
    public string Name = "";
    public float Consent;
    public float Requested;
    public bool Pending;
    public TameOrder Order;
    public UnityEngine.Vector3 WaitPosition;
}
