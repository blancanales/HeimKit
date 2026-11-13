namespace HeimKit.Core.Native;

// Outcomes from the same parse used for a transfer; diagnostics never reparses an inventory.
internal struct InventoryReadEvidence
{
    internal string Status;
    internal int Stacks, Rejected, FirstRejected;
}
