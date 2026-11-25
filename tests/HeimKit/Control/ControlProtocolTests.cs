using HeimKit.Core.Control;
using HeimKit.Core.Control.Models;

internal static class ControlProtocolTests
{
    public static void Run()
    {
        Check(ControlProtocol.TryReadHello(ControlProtocol.WriteHello("test"), out var hello) && hello == (ControlProtocol.Version, "test"), "Hello round trip");
        var request = ControlProtocol.WriteSet(7, "Torches", "Enabled", false);
        Check(ControlProtocol.TryReadSet(request, out var set) && set == (7, "Torches", "Enabled", false), "Set round trip");
        var parameters = new ZPackage();
        ZRpc.Serialize(new object[] { request }, ref parameters);
        var envelope = new ZPackage();
        envelope.Write(parameters);
        Check(ControlProtocol.TryReadRoutedPayload(envelope, 0, out var routed) &&
            ControlProtocol.TryReadSet(routed, out var routedSet) && routedSet == set, "Native routed parameter framing");
        byte[] bytes = request.GetArray();
        for (int index = 0; index < bytes.Length; index++)
            Check(!ControlProtocol.TryReadSet(new ZPackage(bytes[..index]), out _), "Truncated set accepted");
        Check(!ControlProtocol.TryReadSet(new ZPackage(new byte[ControlProtocol.MaxBytes + 1]), out _), "Oversized set accepted");

        var numericRequest = ControlProtocol.WriteNumericSet(8, "DoorKeeper", "CloseDelaySeconds", 7.5);
        Check(ControlProtocol.TryReadNumericSet(numericRequest, out var numberSet) &&
            numberSet == (8, "DoorKeeper", "CloseDelaySeconds", 7.5) &&
            !ControlProtocol.TryReadSet(numericRequest, out _) &&
            !ControlProtocol.TryReadNumericSet(request, out _), "Numeric and boolean requests must stay distinct");

        var closeDelay = new NumericSettingState("DoorKeeper", "CloseDelaySeconds", "Close delay",
            "Continuous open time.", "Applies on the next door check.", false, 1, 300, 7.5);
        var sleepDelay = new NumericSettingState("AFKNightWatch", "SleepDelaySeconds", "Sleep delay",
            "Quorum wait.", "Applies on the next sleep vote.", true, 0, 300, 0);
        Check(closeDelay.Accepts(7.5) && !closeDelay.Accepts(double.NaN) && !closeDelay.Accepts(double.PositiveInfinity) &&
            !closeDelay.Accepts(0) && sleepDelay.Accepts(0) && sleepDelay.Accepts(300) &&
            !sleepDelay.Accepts(1.5) && !sleepDelay.Accepts(301), "Numeric bounds and precision");

        Check(closeDelay.Accepts(7.5) && !closeDelay.AcceptsWhole(7.5) &&
            closeDelay.AcceptsWhole(7), "Allow fractional snapshots but reject fractional edits");

        var definition = new SwitchDefinition("InfiniteTorches", "Enabled", "Enabled", "Automatic light refills.", true);
        var original = new ControlSnapshot
        {
            Protocol = ControlProtocol.Version, ServerVersion = "test", Revision = 12, IsAdmin = true, AdminChangesAllowed = true,
            RequestId = 4, Result = SetResult.Pending,
            Numbers = new[] { closeDelay, sleepDelay },
            Switches = new[] { new SwitchState(definition, true, false, SwitchStatus.On, UnavailableReason.None) }
        };
        var wire = ControlProtocol.WriteState(original);
        Check(ControlProtocol.TryReadState(wire, out var state) && state.Revision == 12 && state.IsAdmin &&
            state.AdminChangesAllowed && state.RequestId == 4 && state.Result == SetResult.Pending &&
            state.Switches.Length == 1 && state.Switches[0].PendingValue == false &&
            state.Switches[0].Definition.Description == definition.Description, "Snapshot round trip");
        Check(state.Numbers.Length == 2 && state.Numbers[0].Value == 7.5 &&
            state.Numbers[0].Applies == closeDelay.Applies && state.Numbers[1].Whole &&
            state.Numbers[1].Minimum == 0, "Numeric snapshot round trip");
        bytes = wire.GetArray();
        for (int index = 0; index < bytes.Length; index++)
            Check(!ControlProtocol.TryReadState(new ZPackage(bytes[..index]), out _), "Truncated snapshot accepted");
        var excessive = new ZPackage();
        excessive.Write(ControlProtocol.Version);
        excessive.Write((ushort)0);
        excessive.Write(0L);
        excessive.Write(false);
        excessive.Write(false);
        excessive.Write(0);
        excessive.Write((byte)0);
        excessive.Write(ControlProtocol.MaxEntries + 1);
        Check(!ControlProtocol.TryReadState(excessive, out _), "Over-count snapshot accepted");
        original.Protocol++;
        var mismatch = ControlProtocol.WriteState(original, versionOnly: true);
        Check(mismatch.Size() == 10 && ControlProtocol.TryReadState(mismatch, out var version) &&
            version.Protocol == ControlProtocol.Version + 1 && version.ServerVersion == "test" && version.Switches.Length == 0, "Mismatch response");
    }

    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
}
