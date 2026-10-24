using HeimKit.Shared;

namespace HeimKit.Features.TameTravel;

internal static class TameTravelPortalRules
{
    internal const float WitnessLifetime = 6;
    internal static string? PairRejection(bool linked, bool destinationPresent, string? destinationRejection,
        bool reciprocal, string sourceTag, string destinationTag, float separationSquared, bool portalsDisabled)
    {
        if (portalsDisabled) return "portals_disabled";
        if (!linked) return "source_unlinked";
        if (!destinationPresent) return "destination_record_missing";
        if (destinationRejection != null) return "destination_" + destinationRejection;
        if (!reciprocal) return "destination_link_not_reciprocal";
        // Native Game.ConnectPortals pairs equal tags, including two blank tags.
        if (!string.Equals(sourceTag, destinationTag, System.StringComparison.Ordinal)) return "portal_tag_mismatch";
        if (!PlayerWorldScope.Finite(separationSquared) || separationSquared < 400) return "portal_separation_invalid";
        return null;
    }
    internal static bool CanWait(string reason) => reason is "source_unlinked" or "destination_record_missing" or "destination_link_not_reciprocal";
    internal static bool WitnessValid(float age) => PlayerWorldScope.Finite(age) && age >= 0 && age <= WitnessLifetime;
}
