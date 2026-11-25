using HeimKit.Core.Control.Models;

namespace HeimKit.Core.Control.Server;

internal static class ControlRequestRules
{
    internal static SetResult Authorize(bool allowed, bool admin) =>
        !allowed ? SetResult.AdminChangesDisabled : !admin ? SetResult.NotAdmin : SetResult.None;

    internal static SetResult Validate(bool known, bool mayStart, bool busy, bool current, bool requested)
    {
        if (!known) return SetResult.UnknownSwitch;
        if (!mayStart) return SetResult.Unavailable;
        if (busy) return SetResult.Busy;
        return current == requested ? SetResult.Applied : SetResult.None;
    }

    internal static bool Spend(float now, float[] recent, ref int count, ref int next)
    {
        if (count == recent.Length && now >= recent[next] && now - recent[next] < 10) return false;
        recent[next] = now;
        next = (next + 1) % recent.Length;
        if (count < recent.Length) count++;
        return true;
    }
}
