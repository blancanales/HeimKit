namespace HeimKit.Features.TameTravel.Models;

internal sealed class FollowBinding
{
    public ZNetPeer Peer = null!;
    public ZDOID Character;
    public ZDOID Animal;
    private ZDOID _sortAnimal;
    private string? _sortKey;
    internal string SortKey
    {
        get { if (_sortKey == null || _sortAnimal != Animal) { _sortAnimal = Animal; _sortKey = Animal.ToString(); } return _sortKey; }
    }
    public string Name = "";
    public float Consent;
    public float Requested;
    public bool Pending;
    public bool CommandSent = true;
    public bool AwaitingEarlierFollow;
    public TameOrder Order;
    public UnityEngine.Vector3 WaitPosition;
}
