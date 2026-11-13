using System.Collections.Generic;
using UnityEngine;
namespace HeimKit.Features.StationSupply.Models;
internal sealed class SupplyTransaction
{
    internal SupplyLease Station = null!, Chest = null!;
    internal SupplyStation Definition = null!;
    internal SupplyIntake Intake = null!;
    internal Vector3 StationPosition, ChestPosition;
    internal Quaternion StationRotation;
    internal float Started, PublishedAt;
    internal bool Committed;
    internal string? StationWait, ChestWait, Item, Stage;
    internal int BatchSize;
    internal int PendingRefresh;
    internal long PendingPeer;
    internal SupplySelection Selection;
    internal HeimKit.Core.Native.InventoryReadEvidence Inventory;
    internal byte[]? SourceInventory;
    internal uint SourceRevision;
    internal readonly List<(ZNetPeer Peer, float Nonce)> Echoes = new();
    internal readonly List<long> PendingPeers = new();
}
