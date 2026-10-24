using HeimKit.Core.Diagnostics;

namespace HeimKit.Core.Players;

public static class PlayerLogTopics
{
    public static readonly LogTopic Presence = new("presence");
    public static readonly LogTopic Afk = new("afk");
}
