using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace HeimKit.Shared;

public static class DiagnosticLog
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static void StartSession() => Clock.Restart();
    public static string Format(string feature, DiagnosticEvent category, string message,
        IEnumerable<(string Key, object? Value)>? fields = null, double? elapsedSeconds = null)
    {
        string line = $"{Escape(feature)}: {category.ToString().ToUpperInvariant()} | {Escape(message)} | elapsed_s={(elapsedSeconds ?? Clock.Elapsed.TotalSeconds).ToString("F1", CultureInfo.InvariantCulture)}";
        var names = new HashSet<string> { "elapsed_s" };
        if (fields != null)
            foreach (var field in fields)
            {
                if (!names.Add(field.Key) || field.Key.Length == 0 || field.Key.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
                    throw new ArgumentException("Diagnostic field names must contain letters, digits or underscores.");
                line += $" | {field.Key}={Escape(Value(field.Value))}";
            }
        return line;
    }
    public static void Write(ManualLogSource logger, string feature, DiagnosticEvent category, string message,
        IEnumerable<(string Key, object? Value)>? fields = null, LogLevel level = LogLevel.Info)
        => logger.Log(level, Format(feature, category, message, fields));
    public static string Value(object? value) => value == null ? "unknown" : value is bool flag ? (flag ? "true" : "false") :
        value is float single ? single.ToString("R", CultureInfo.InvariantCulture) : value is double precise ? precise.ToString("R", CultureInfo.InvariantCulture) :
        value is IFormattable number ? number.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? "unknown";
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", "\\r").Replace("\n", "\\n");
}
