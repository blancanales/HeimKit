using HeimKit.Core.Control.Models;
using HeimKit.Core.Control.Server;
using HeimKit.Core.Scheduling;

internal static class ControlRequestRulesTests
{
    private sealed class Gate : IModuleIdle
    {
        public bool Idle { get; set; }
        internal bool Held;
        public void HoldNewWork(bool hold) => Held = hold;
    }

    public static void Run()
    {
        Check(ControlRequestRules.Authorize(false, true) == SetResult.AdminChangesDisabled, "Disabled administration");
        Check(ControlRequestRules.Authorize(true, false) == SetResult.NotAdmin, "Non-admin");
        Check(ControlRequestRules.Validate(false, true, false, false, true) == SetResult.UnknownSwitch, "Unknown switch");
        Check(ControlRequestRules.Validate(true, false, false, false, true) == SetResult.Unavailable, "Held module");
        Check(ControlRequestRules.Validate(true, true, false, true, true) == SetResult.Applied, "No change");
        Check(ControlRequestRules.Validate(true, true, true, true, false) == SetResult.Busy, "Second pending request");
        int count = 0, next = 0;
        var recent = new float[10];
        for (int index = 0; index < recent.Length; index++)
            Check(ControlRequestRules.Spend(index, recent, ref count, ref next), "Request wrongly rate limited");
        Check(!ControlRequestRules.Spend(9.9f, recent, ref count, ref next), "Rate limit bypassed");
        Check(ControlRequestRules.Spend(10, recent, ref count, ref next), "Rate window did not expire");

        var gate = new Gate();
        int writes = 0;
        SetResult Apply() { writes++; return SetResult.Applied; }
        using (var pending = new PendingChange(gate, 0))
        {
            Check(gate.Held && pending.Poll(1, true, Apply) == SetResult.Pending && writes == 0, "Pending wrote config");
            gate.Idle = true;
            Check(pending.Poll(2, true, Apply) == SetResult.Applied && writes == 1 && !gate.Held, "Idle change did not apply");
        }
        gate.Idle = false;
        using (var pending = new PendingChange(gate, 5))
            Check(pending.Poll(65, true, Apply) == SetResult.Busy && writes == 1 && !gate.Held, "Timeout committed or retained hold");
        using (var pending = new PendingChange(gate, 0))
            Check(pending.Poll(2, false, Apply) == SetResult.Unavailable && writes == 1 && !gate.Held, "Failure committed or retained hold");
    }

    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
}
