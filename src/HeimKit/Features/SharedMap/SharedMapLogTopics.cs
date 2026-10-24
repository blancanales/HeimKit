using HeimKit.Core.Diagnostics;

namespace HeimKit.Features.SharedMap;

public static class SharedMapLogTopics
{
    public static readonly LogTopic Exploration = new("exploration");
    public static readonly LogTopic Portals = new("portals");
    public static readonly LogTopic Resources = new("resources");
    public static readonly LogTopic Dungeons = new("dungeons");
    public static readonly LogTopic Landmarks = new("landmarks");
    public static readonly LogTopic Tables = new("tables");
    public static readonly LogTopic Save = new("save");
}
