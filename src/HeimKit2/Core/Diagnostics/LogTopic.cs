using System;

namespace HeimKit.Core.Diagnostics;

public readonly struct LogTopic
{
    public string Name { get; }
    public LogTopic(string name)
    {
        if (!Valid(name)) throw new ArgumentException("A log topic must match [a-z][a-z0-9_]*.", nameof(name));
        Name = name;
    }
    internal static bool Valid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name![0] < 'a' || name[0] > 'z') return false;
        for (int i = 1; i < name.Length; i++)
            if (!(name[i] >= 'a' && name[i] <= 'z') && !(name[i] >= '0' && name[i] <= '9') && name[i] != '_') return false;
        return true;
    }
    public override string ToString() => Name ?? "lifecycle";
}

public static class CoreLogTopics
{
    public static readonly LogTopic Lifecycle = new("lifecycle");
    public static readonly LogTopic Config = new("config");
    public static readonly LogTopic Scope = new("scope");
    public static readonly LogTopic Timing = new("timing");
}
