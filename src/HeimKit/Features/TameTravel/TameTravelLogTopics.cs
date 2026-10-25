using HeimKit.Core.Diagnostics;

namespace HeimKit.Features.TameTravel;

public static class TameTravelLogTopics
{
    public static readonly LogTopic Command = new("command");
    public static readonly LogTopic Follow = new("follow");
    public static readonly LogTopic Travel = new("travel");
}
