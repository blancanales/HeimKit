using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Lifecycle;
using UnityEngine;

namespace HeimKit.Core.Diagnostics;

public static class DiagnosticLog
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static ConditionalWeakTable<ManualLogSource, Dictionary<string, DiagnosticBudget>> _budgets = new();
    [ThreadStatic] private static StringBuilder? _builder;
    [ThreadStatic] private static string?[]? _names;
    [ThreadStatic] private static Dictionary<object, string>? _enums;
    public static Func<DateTime> WallClock { get; set; } = () => DateTime.Now;
    public static ServerDebugActivity ServerActivity { get; } = new();
    public static bool VerboseConfigured => DiagnosticsSettings.Verbosity == DebugVerbosity.Verbose;
    public static LogField Field(string key, object? value) => new(key, value);
    public static LogField FieldAlways(string key, object? value) => new(key, value, true);
    // For dictionary and tuple sources: a value whose zero, false or empty form is the answer.
    public static object Kept(object? value) => new LogField("", value, true);
    public static void StartSession() { Clock.Restart(); ServerActivity.Reset(); _budgets = new(); }
    public static void ObservePlayers(bool? hasConnectedPlayers) => ServerActivity.Observe(Clock.Elapsed.TotalSeconds, hasConnectedPlayers);
    private static DiagnosticBudget Budget(ManualLogSource logger, string feature)
    {
        var features = _budgets.GetOrCreateValue(logger);
        if (!features.TryGetValue(feature, out var budget)) features.Add(feature, budget = new DiagnosticBudget());
        return budget;
    }
    public static bool DebugAvailable(ManualLogSource logger, string feature, bool verbose = false, int requiredSlots = 1) =>
        ServerActivity.Allowed && (!verbose || VerboseConfigured) && Budget(logger, feature).Available((float)Clock.Elapsed.TotalSeconds, "optional", 61 - Math.Max(1, requiredSlots));
    public static void Write(ManualLogSource logger, string feature, string message, LogLevel level = LogLevel.Info)
        => Write(logger, feature, CoreLogTopics.Lifecycle, message, level: level);
    public static void Write(ManualLogSource logger, string feature, LogTopic topic, string message,
        LogFields fields = default, LogLevel level = LogLevel.Info, bool verbose = false, Exception? error = null)
    {
        if (level == LogLevel.Debug && (!DebugAvailable(logger, feature, verbose) ||
            !Budget(logger, feature).Reserve((float)Clock.Elapsed.TotalSeconds, "optional", 60, false))) return;
        logger.Log(level, Format(feature, topic, message, fields, level, verbose, error));
    }
    public static string Format(string feature, LogTopic topic, string message, LogFields fields = default,
        LogLevel level = LogLevel.Debug, bool verbose = false, Exception? error = null)
    {
        var builder = _builder ??= new StringBuilder(1024); builder.Clear();
        Span<char> text = stackalloc char[512];
        WallClock().TryFormat(text, out int length, "HH:mm:ss", CultureInfo.InvariantCulture); builder.Append(text.Slice(0, length)); builder.Append(' ');
        Plain(builder, feature);
        bool structured = level == LogLevel.Debug || level == LogLevel.Error || level == LogLevel.Fatal;
        if (structured) { builder.Append('.'); builder.Append(topic.Name ?? "lifecycle"); }
        builder.Append(": "); Plain(builder, message);
        if (builder.Length > 0 && builder[builder.Length - 1] != '.' && builder[builder.Length - 1] != '!' && builder[builder.Length - 1] != '?') builder.Append('.');
        if (!structured) return builder.ToString();
        var names = _names ??= new string?[64]; int used = 0, emitted = 0, omitted = 0;
        int cap = verbose ? int.MaxValue : error == null ? 8 : 5;
        foreach (var field in fields)
        {
            if (field.Key == "omitted_fields" || error != null && (field.Key == "error_type" || field.Key == "error" || field.Key == "stack")) continue;
            object? value = field.Value; bool always = field.Always;
            if (value is LogField kept) { value = kept.Value; always |= kept.Always; }
            if (!LogTopic.Valid(field.Key) || (!always && Empty(value)) || value == null || Duplicate(names, used, field.Key)) continue;
            if (used == names.Length) { Array.Resize(ref names, names.Length * 2); _names = names; }
            names[used++] = field.Key;
            if (emitted >= cap) { omitted++; continue; }
            AppendField(builder, field.Key, value, text); emitted++;
        }
        if (error != null)
        {
            AppendField(builder, "error_type", error.GetType().FullName, text);
            AppendField(builder, "error", error.Message, text);
            var stack = error.InnerException == null ? error.StackTrace : error.ToString();
            if (!string.IsNullOrEmpty(stack)) AppendField(builder, "stack", stack, text);
        }
        if (omitted > 0) { builder.Append(" omitted_fields="); omitted.TryFormat(text, out length, provider: CultureInfo.InvariantCulture); builder.Append(text.Slice(0, length)); }
        Array.Clear(names, 0, used);
        return builder.ToString();
    }
    private static bool Duplicate(string?[] names, int count, string key)
    { for (int i = 0; i < count; i++) if (names[i] == key) return true; return false; }
    private static bool Empty(object? value) => value == null || value is string s && (s.Length == 0 || s == "unknown") ||
        value is bool b && !b || value is byte u8 && u8 == 0 || value is sbyte i8 && i8 == 0 ||
        value is short i16 && i16 == 0 || value is ushort u16 && u16 == 0 || value is int i32 && i32 == 0 ||
        value is uint u32 && u32 == 0 || value is long i64 && i64 == 0 || value is ulong u64 && u64 == 0 ||
        value is float f && f == 0 || value is double d && d == 0 || value is decimal m && m == 0 ||
        value is ZDOID id && id == ZDOID.None;
    private static void Plain(StringBuilder builder, string? value)
    { if (value == null) return; foreach (char c in value) builder.Append(char.IsControl(c) ? ' ' : c); }
    private static void AppendField(StringBuilder builder, string key, object? value, Span<char> buffer)
    {
        if (value == null) return;
        int start = builder.Length;
        try
        {
            builder.Append(' '); builder.Append(key); builder.Append('=');
            if (value is string s) { Quoted(builder, s); return; }
            if (value is bool flag) { builder.Append(flag ? "true" : "false"); return; }
            string format = key.EndsWith("_ms", StringComparison.Ordinal) ? "F2" : key.EndsWith("_m", StringComparison.Ordinal) || key.EndsWith("_s", StringComparison.Ordinal) ? "F1" : "0.###";
            int written;
            switch (value)
            {
                case Vector3 v:
                    v.x.TryFormat(buffer, out written, "F1", CultureInfo.InvariantCulture); builder.Append(buffer.Slice(0, written)); builder.Append(',');
                    v.y.TryFormat(buffer, out written, "F1", CultureInfo.InvariantCulture); builder.Append(buffer.Slice(0, written)); builder.Append(',');
                    v.z.TryFormat(buffer, out written, "F1", CultureInfo.InvariantCulture); builder.Append(buffer.Slice(0, written)); return;
                case ZDOID id:
                    id.UserID.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); builder.Append(buffer.Slice(0, written)); builder.Append(':');
                    id.ID.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); break;
                case float f: f.TryFormat(buffer, out written, format, CultureInfo.InvariantCulture); break;
                case double d: d.TryFormat(buffer, out written, format, CultureInfo.InvariantCulture); break;
                case decimal m: m.TryFormat(buffer, out written, format, CultureInfo.InvariantCulture); break;
                case int n: n.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); break;
                case long n: n.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); break;
                case uint n: n.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); break;
                case ulong n: n.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); break;
                case short n: n.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); break;
                case ushort n: n.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); break;
                case byte n: n.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); break;
                case sbyte n: n.TryFormat(buffer, out written, provider: CultureInfo.InvariantCulture); break;
                case Enum e:
                    var cache = _enums ??= new(); if (!cache.TryGetValue(e, out var name)) cache.Add(e, name = e.ToString()); Quoted(builder, name); return;
                default: Quoted(builder, value.ToString() ?? ""); return;
            }
            builder.Append(buffer.Slice(0, written));
        }
        catch { builder.Length = start; }
    }
    private static void Quoted(StringBuilder builder, string value)
    {
        bool quote = value.Length == 0;
        foreach (char c in value) if (char.IsWhiteSpace(c) || c == '"' || c == '=' || c == '\\' || char.IsControl(c)) { quote = true; break; }
        if (!quote) { builder.Append(value); return; }
        builder.Append('"');
        foreach (char c in value)
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) { builder.Append("\\u"); const string hex = "0123456789abcdef"; builder.Append(hex[(c >> 12) & 15]); builder.Append(hex[(c >> 8) & 15]); builder.Append(hex[(c >> 4) & 15]); builder.Append(hex[c & 15]); }
                    else builder.Append(c);
                    break;
            }
        builder.Append('"');
    }
    public static string Value(object? value) => value == null ? "unknown" : value is bool flag ? (flag ? "true" : "false") :
        value is IFormattable number ? number.ToString(null, CultureInfo.InvariantCulture) : value.ToString() ?? "unknown";
}
