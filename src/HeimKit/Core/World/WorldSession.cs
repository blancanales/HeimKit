namespace HeimKit.Core.World;

internal enum WorldSessionRole
{
    Unavailable,
    RemoteClient,
    DedicatedServer,
    PrivateLocalHost,
    NetworkHost
}

internal static class WorldSession
{
    internal static bool IsPlayerHosted(WorldSessionRole role) =>
        role is WorldSessionRole.PrivateLocalHost or WorldSessionRole.NetworkHost;

    internal static WorldSessionRole Identify(ZNet? net)
    {
        if (net == null) return WorldSessionRole.Unavailable;
        if (!net.IsServer()) return WorldSessionRole.RemoteClient;
        if (net.IsDedicated()) return WorldSessionRole.DedicatedServer;
        return ZNet.IsOpenServer() ? WorldSessionRole.NetworkHost : WorldSessionRole.PrivateLocalHost;
    }
}
