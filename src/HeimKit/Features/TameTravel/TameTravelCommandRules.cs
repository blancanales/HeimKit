using System;
using HeimKit.Features.TameTravel.Models;
namespace HeimKit.Features.TameTravel;
internal static class TameTravelCommandRules
{
    internal static float SelectionRange(TameOrder order, float commandRange) => order == TameOrder.Stay ? 32 : commandRange;
    internal static bool AwaitingEarlierFollow(bool pending, TameOrder previous, bool sent) => pending && previous == TameOrder.Follow && sent;
    public static bool StopAssociated(string following, string player, bool bound)
        => player.Length != 0 && (following == player || bound && following.Length == 0);
    public static bool Chat(string text, out TameOrder order)
    {
        order = default;
        switch (text.Trim().ToLowerInvariant())
        {
            case "!follow": order = TameOrder.Follow; return true;
            case "!stay": order = TameOrder.Stay; return true;
            case "!wait": order = TameOrder.Wait; return true;
            default: return false;
        }
    }
    public static bool Emote(string text, out TameOrder order)
    {
        order = default;
        switch (text)
        {
            case "comehere": order = TameOrder.Follow; return true;
            case "nonono": order = TameOrder.Stay; return true;
            case "wave": order = TameOrder.Wait; return true;
            default: return false;
        }
    }
    public static string ExpectedFollow(TameOrder order, string player) => order == TameOrder.Follow ? player : "";
    public static bool ConsentExpired(TameOrder order, float age, float lifetime)
        => !HeimKit.Shared.PlayerWorldScope.Finite(age) || age < 0 ||
            order != TameOrder.Follow && age > lifetime;
    public static bool ConsentValid(TameOrder order, string follow, string player)
        => order != TameOrder.Stay && string.Equals(follow, ExpectedFollow(order, player), StringComparison.Ordinal);
}
