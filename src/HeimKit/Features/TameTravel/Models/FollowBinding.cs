using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
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
    public bool CommandSent = true;
    public bool AwaitingEarlierFollow;
    public string ReclaimName = "";
    public TameOrder Order;
    public UnityEngine.Vector3 WaitPosition;
}
