using HeimKit.Core.Control.Models;

namespace HeimKit.Client;

internal enum LinkState { NotInWorld, ClientOnly, Checking, Connected, VersionMismatch, ServerDisabled, LocalHostedWorld }
internal enum ClientDecision { Running, LocalDisabled, ServerDisabled, ServerUnavailable, NeedsServer, Waiting }

internal static class ClientPolicy
{
    internal static ClientDecision Decide(LinkState link, SwitchStatus? paired, bool enabled, bool requiresServer)
    {
        if (link == LinkState.ServerDisabled) return ClientDecision.ServerDisabled;
        if (link == LinkState.Connected || link == LinkState.LocalHostedWorld && paired.HasValue)
        {
            if (!paired.HasValue) return ClientDecision.NeedsServer;
            if (paired == SwitchStatus.Off) return ClientDecision.ServerDisabled;
            if (paired == SwitchStatus.Unavailable) return ClientDecision.ServerUnavailable;
        }
        else if (link is LinkState.ClientOnly or LinkState.LocalHostedWorld)
        {
            if (requiresServer) return ClientDecision.NeedsServer;
        }
        else return ClientDecision.Waiting;
        return enabled ? ClientDecision.Running : ClientDecision.LocalDisabled;
    }
}
