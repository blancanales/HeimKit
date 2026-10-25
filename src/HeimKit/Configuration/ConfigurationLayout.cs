using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HeimKit.Configuration;

// BepInEx sorts every section alphabetically; group shared sections ahead of feature sections.
public static class ConfigurationLayout
{
    private static readonly HashSet<string> SharedSections = new(StringComparer.Ordinal) { "Compatibility", "Diagnostics", "PlayerActivity" };
    public static void Apply(string path)
    {
        string contents = File.ReadAllText(path);
        var headers = Regex.Matches(contents, @"^\[([^\r\n]+)\]\r?$", RegexOptions.Multiline);
        if (headers.Count == 0) return;
        var sections = new List<(bool Shared, string Text)>();
        for (int i = 0; i < headers.Count; i++)
        {
            int start = headers[i].Index;
            int end = i + 1 < headers.Count ? headers[i + 1].Index : contents.Length;
            sections.Add((SharedSections.Contains(headers[i].Groups[1].Value), contents.Substring(start, end - start)));
        }
        string reordered = contents.Substring(0, headers[0].Index) + string.Concat(sections.OrderBy(section => !section.Shared).Select(section => section.Text));
        if (!string.Equals(contents, reordered, StringComparison.Ordinal)) File.WriteAllText(path, reordered, new UTF8Encoding(false));
    }
}
