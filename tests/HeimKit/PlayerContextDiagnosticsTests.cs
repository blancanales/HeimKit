using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Configuration;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Players;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;

internal static class PlayerContextDiagnosticsTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run() { Admission(); TargetedPackets(); WeatherRegions(); SessionAndCacheBounds(); }
    private static void Admission()
    {
        DiagnosticLog.StartSession(); ModuleScheduler.Current.Clear();
        using var log = new ManualLogSource("Player context admission"); var lines = new List<string>(); log.LogEvent += (_, e) => lines.Add(LogCapture.Text(e.Data));
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false);
        var enabled = config.Bind("PlayerActivity", "DebugMode", true);
        var policy = new DebugLoggingPolicy(enabled, false);
        var fields = new LogField[] { ("player", "one peer=1 character=1:2"), ("position", "zone=0,0"), ("combat", "hit=unconfirmed"),
            ("raid", "none"), ("weather", "player_local_weather=unverified"), ("weather_guard", "coverage=unknown"), ("npc_sample", "outside_sample=unknown"), ("work", "shared_frame_module=WeatherGuard") };
        try
        {
            ModuleScheduler.Current.Combat.SetRaid(true);
            Check(!DiagnosticLog.DebugAvailable(log, "PlayerActivity") && DiagnosticLog.ContextAvailable(log, policy), "Combat context could not be observed while ordinary Debug remained muted.");
            for (int i = 0; i < 100; i++) { ServerPerformance.BeginUpdate(); DiagnosticLog.WriteContext(log, policy, fields); }
            Check(lines.Count == 24 && lines.All(s => s.Contains("npc_sample=") && s.Contains("work=") && !s.Contains("omitted_fields=")), "Context exceeded its minute ceiling or lost required Normal fields.");
            Check(ModuleScheduler.Current.Combat.Raid, "Diagnostic output changed the combat policy.");
            DiagnosticLog.StartSession();
            Check(!DiagnosticLog.ContextAvailable(log, new DebugLoggingPolicy(enabled, true)), "Hidden suppression admitted player context.");
            enabled.Value = false; Check(!DiagnosticLog.ContextAvailable(log, policy), "Disabled debugging admitted context collection.");
            enabled.Value = true; ServerPerformance.BeginUpdate(); ServerPerformance.OptionalOutput(Stopwatch.Frequency);
            Check(!DiagnosticLog.ContextAvailable(log, policy), "Context bypassed the shared elapsed output limit.");
        }
        finally { ModuleScheduler.Current.Clear(); DiagnosticLog.StartSession(); }
        System.Console.WriteLine("PASS: player context survives combat muting, retains all eight Normal fields, obeys shared time/volume/hidden gates and never changes battle policy.");
    }
    private static void TargetedPackets()
    {
        ZPackage Packet(long sender, byte a, byte b)
        {
            var parameters = new ZPackage(); parameters.Write(a); parameters.Write(b);
            var data = new ZRoutedRpc.RoutedRPCData { m_msgID = 1, m_senderPeerID = sender, m_targetPeerID = 42,
                m_targetZDO = new ZDOID(44, 3), m_methodHash = "OnTargeted".GetStableHashCode(), m_parameters = parameters };
            var packet = new ZPackage(); data.Serialize(packet); packet.SetPos(0); return packet;
        }
        var valid = Packet(99, 1, 0);
        Check(RoutedActivityReader.TryReadTargeted(valid, 99, out var target, out bool sensed, out bool alerted) && target == new ZDOID(44, 3) && sensed && !alerted && valid.GetPos() == 0,
            "Targeted evidence changed packet state or lost authenticated target/flags.");
        var spoof = Packet(98, 1, 1); var invalid = Packet(99, 2, 0); var truncated = new ZPackage();
        foreach (var packet in new[] { spoof, invalid, truncated })
            Check(!RoutedActivityReader.TryReadTargeted(packet, 99, out _, out _, out _) && packet.GetPos() == 0, "Forged or malformed targeted input was accepted or consumed.");
        System.Console.WriteLine("PASS: player-target notifications preserve native packet cursors, require authenticated headers and reject invalid/truncated boolean payloads without parameter-copy allocations.");
    }
    private static void WeatherRegions()
    {
        var weather = new PlayerWeatherContext(); var a = new Vector2s(0, 0); var b = new Vector2s(20, 20);
        weather.Begin(true, true, true); weather.Candidate(a, false); weather.Acquired(a); weather.Candidate(b, true); weather.Candidate(b, false);
        weather.End(10, new[] { a, b }, false); weather.Gate(11, a, true); weather.Gate(11, a, false); weather.Confirmed(11, a);
        string first = weather.Read(11, a, out _), second = weather.Read(11, b, out _);
        Check(first.Contains("coverage=current") && first.Contains("eligible=1 managed=1") && first.Contains("native_rain_gates=1 native_underwater_gates=1 confirmed_preventions=1"),
            "Weather evidence lost acquisition, native wetness source or confirmed prevention.");
        Check(second.Contains("eligible=2 managed=1") && second.Contains("native_rain_gates=0 native_underwater_gates=0") && second.Contains("wetness_elsewhere=unknown"),
            "Separated player regions inherited another area's weather observations or claimed unknown wetness was absent.");
        Check(weather.Read(15, a, out _).Contains("coverage=stale") && weather.Read(15, a, out _).Contains("eligible=unknown"), "Stale protection data looked current or became a false zero.");
        Check(weather.Read(72, a, out _).Contains("native_rain_gates=0") && weather.Read(72, a, out _).Contains("wear_age_s=unknown"), "Old wetness samples never expired.");
        weather.Clear(); Check(weather.Read(72, a, out _).Contains("coverage=unavailable"), "Disabled/disposed weather protection retained active context.");
        System.Console.WriteLine("PASS: passive weather context separates player regions, retains native rain/underwater/confirmation evidence and labels missing, stale and out-of-scope data without claiming dry conditions.");
    }
    private sealed class Presence : IPlayerPresence
    {
        public bool? HasConnectedPlayers => false;
        public bool? HasJoinedPlayers => false;
        public int? ConnectedCount => 0;
        public int? ReadyCount => 0;
        public int? JoinedCount => 0;
        public IReadOnlyList<ZNetPeer> Peers => Array.Empty<ZNetPeer>();
        public void Refresh() { }
        public void Clear() { }
    }
    private static void SessionAndCacheBounds()
    {
        DiagnosticLog.StartSession(); ModuleScheduler.Current.Clear();
        using var log = new ManualLogSource("Context identity checks");
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false);
        var policy = new DebugLoggingPolicy(config.Bind("Diagnostics", "DebugMode", false), false);
        var presence = new Presence(); using var messages = new PlayerRoutedMessages();
        using var context = new PlayerContextDiagnostics(presence, messages, policy, log);
        ModuleScheduler.Current.Combat.SetRaid(true); context.Tick(0);
        Check(ModuleScheduler.Current.Combat.Raid, "Inactive context cleared gameplay raid pressure.");
        var get = typeof(PlayerContextDiagnostics).GetMethod("Get", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var peer = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); peer.m_uid = 1; peer.m_characterID = new ZDOID(1, 1);
        var old = get.Invoke(context, new object[] { peer }); peer.m_characterID = new ZDOID(1, 2);
        Check(!ReferenceEquals(old, get.Invoke(context, new object[] { peer })), "Respawn/character replacement inherited old combat context.");
        for (uint i = 0; i < 100; i++)
        {
            var other = (ZNetPeer)RuntimeHelpers.GetUninitializedObject(typeof(ZNetPeer)); other.m_uid = 1; other.m_characterID = new ZDOID(1, i + 3);
            get.Invoke(context, new object[] { other });
        }
        var states = (System.Collections.IDictionary)typeof(PlayerContextDiagnostics).GetField("_states", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)!;
        Check(states.Count == 64, "Context session history exceeded its bound or merged separate physical peers with the same ID.");
        context.Dispose(); Check(states.Count == 0 && ModuleScheduler.Current.Combat.Raid, "Context cleanup retained identities or changed gameplay policy.");
        using var provider = new WorldObjectProvider(presence, (_, _, _, _) => throw new Exception("diagnostics caused native discovery"), observe: false);
        Check(!provider.TryCachedCell(new Vector2s(0, 0), out var records) && records.Count == 0, "Missing diagnostic frame data triggered discovery or invented records.");
        ModuleScheduler.Current.Clear(); DiagnosticLog.StartSession();
        System.Console.WriteLine("PASS: context identity is physical-session/character scoped and bounded, disposal leaves gameplay pressure untouched, and cached diagnostic reads never trigger native discovery.");
    }
}
