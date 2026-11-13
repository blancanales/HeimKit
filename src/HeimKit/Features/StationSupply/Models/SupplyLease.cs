using HeimKit.Core.Ownership;
namespace HeimKit.Features.StationSupply.Models;
internal sealed class SupplyLease : OwnershipLease
{
    // Static transactions drain and release; they never hand off to a destination peer.
    public override ZNetPeer DestinationPeer => throw new System.InvalidOperationException("A static supply lease has no destination peer.");
    internal SupplyLease(ZDO record) { Record = record; Id = record.m_uid; StaticControl = true; }
}
