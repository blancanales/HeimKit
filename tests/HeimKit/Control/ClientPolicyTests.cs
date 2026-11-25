using HeimKit.Client;
using HeimKit.Core.Control.Models;

internal static class ClientPolicyTests
{
    public static void Run()
    {
        foreach (bool required in new[] { false, true })
        foreach (bool enabled in new[] { false, true })
        {
            var local = enabled ? ClientDecision.Running : ClientDecision.LocalDisabled;
            Check(LinkState.Connected, SwitchStatus.On, enabled, required, local);
            Check(LinkState.Connected, SwitchStatus.Off, enabled, required, ClientDecision.ServerDisabled);
            Check(LinkState.Connected, SwitchStatus.Unavailable, enabled, required, ClientDecision.ServerUnavailable);
            Check(LinkState.Connected, null, enabled, required, ClientDecision.NeedsServer);
            Check(LinkState.ClientOnly, null, enabled, required, required ? ClientDecision.NeedsServer : local);
            Check(LinkState.ServerDisabled, SwitchStatus.On, enabled, required, ClientDecision.ServerDisabled);
            Check(LinkState.Checking, null, enabled, required, ClientDecision.Waiting);
            Check(LinkState.VersionMismatch, null, enabled, required, ClientDecision.Waiting);
            Check(LinkState.NotInWorld, null, enabled, required, ClientDecision.Waiting);
        }
    }

    private static void Check(LinkState link, SwitchStatus? paired, bool enabled, bool required, ClientDecision expected)
    {
        if (ClientPolicy.Decide(link, paired, enabled, required) != expected)
            throw new Exception($"Wrong policy for {link}, {paired}, enabled={enabled}, requiresServer={required}.");
    }
}
