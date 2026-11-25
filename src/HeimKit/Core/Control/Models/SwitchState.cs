namespace HeimKit.Core.Control.Models;

internal readonly struct SwitchState
{
    internal SwitchState(SwitchDefinition definition, bool value, bool? pendingValue, SwitchStatus status, UnavailableReason reason)
    {
        Definition = definition;
        Value = value;
        PendingValue = pendingValue;
        Status = status;
        Reason = reason;
    }

    internal SwitchDefinition Definition { get; }
    internal bool Value { get; }
    internal bool? PendingValue { get; }
    internal SwitchStatus Status { get; }
    internal UnavailableReason Reason { get; }
}
