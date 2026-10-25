using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HeimKit.Features.SharedMap;
using HeimKit.Features.SharedMap.Models;

internal static class SharedMapWritePolicyTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimkit-write-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var logger = new ManualLogSource("SharedMap write-policy checks");
        try
        {
            string configPath = Path.Combine(directory, "heimkit.cfg");
            var config = new ConfigFile(configPath, false) { SaveOnConfigSet = false };
            var settings = new SharedMapSettings(config, logger);
            settings.AllowPlayerMapWrites.Value = false;
            settings.PortalMarkerSync.Value = settings.ResourceMarkerSync.Value = settings.DungeonMarkerSync.Value = false;
            var map = new SharedMapExploration(64, 12, 50, .25f);
            map.Reveal(-200, -200);
            byte[] prior = SharedMapTableData.Merge(map, null)!;
            var uploaded = new SharedMapExploration(64, 12, 50, .25f); uploaded.Reveal(200, 200);
            byte[] incoming = SharedMapTableData.Merge(uploaded, null, out _, dungeons: new[] { new MapMarker("Unwanted", 200, 1, 200) })!;
            // Exercise the actual normalizer without creating Unity world objects or installing hooks.
            var assembly = typeof(SharedMap).Assembly;
            var controllerType = assembly.GetType("HeimKit.Features.SharedMap.SharedMapController", true)!;
            var controller = RuntimeHelpers.GetUninitializedObject(controllerType);
            var diagnosticType = assembly.GetType("HeimKit.Features.SharedMap.SharedMapDiagnostics", true)!;
            var diagnostics = Activator.CreateInstance(diagnosticType, new object?[] { logger, settings.DebugLogging, null })!;
            var adminType = assembly.GetType("HeimKit.Features.SharedMap.SharedMapPinAdministration", true)!;
            var admin = Activator.CreateInstance(adminType, new object?[] { null, 64, directory, 123L, logger, (Func<int>)(() => 1), (Action<ZDO>)(_ => { }) })!;
            void Field(string name, object value) => controllerType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, value);
            Field("_settings", settings); Field("_exploration", map); Field("_diagnostics", diagnostics); Field("_pinAdministration", admin);
            Field("_pinState", new SharedMapPinState(directory, 123));
            var normalize = controllerType.GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic)!;
            byte[]? Normalize(byte[]? data, byte[]? previous) => (byte[]?)normalize.Invoke(controller, new object?[] { data, previous, 0 });
            Check(Normalize(incoming, prior)!.SequenceEqual(prior) && !map.IsExplored(200, 200), "Rejected upload imported exploration or replaced server data.");
            Check(Normalize(new byte[] { 1, 2, 3 }, prior)!.SequenceEqual(prior), "Malformed rejected upload changed server data.");
            Check(Normalize(null, prior)!.SequenceEqual(prior), "Removed map field erased prior server data.");
            byte[] initialized = Normalize(incoming, null)!;
            Check(SharedMapTableData.ReadPins(64, initialized).Count == 0 && !map.IsExplored(200, 200), "A first upload bypassed rejection on an empty table.");
            map.Reveal(0, 0);
            Check(!Normalize(incoming, prior)!.SequenceEqual(prior) && map.IsExplored(0, 0) && !map.IsExplored(200, 200),
                "Write rejection prevented new server exploration from reaching tables.");
            settings.AllowPlayerMapWrites.Value = true;
            Normalize(incoming, prior);
            Check(map.IsExplored(200, 200) && map.IsExplored(-200, -200), "Allowed uploads failed to union prior and incoming exploration.");
            // Both normalization passes may reuse decoded input, but neither may mutate its terrain.
            var decode = typeof(SharedMapTableData).GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic)!;
            var merge = typeof(SharedMapTableData).GetMethod("MergeValidated", BindingFlags.Static | BindingFlags.NonPublic)!;
            object decoded = decode.Invoke(null, new object[] { 64, prior })!;
            byte[]? MergeDecoded(SharedMapExploration terrain)
            {
                var args = merge.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                args[0] = terrain; args[1] = decoded; args[2] = 0;
                return (byte[]?)merge.Invoke(null, args);
            }
            var expanded = new SharedMapExploration(64, 12, 50, .25f); expanded.Reveal(200, 200);
            Check(MergeDecoded(expanded) != null, "Validated input failed to merge additional terrain.");
            var original = new SharedMapExploration(64, 12, 50, .25f);
            Check(MergeDecoded(original) == null && original.IsExplored(-200, -200) && !original.IsExplored(200, 200),
                "A prior merge mutated the reusable validated input.");
            incoming[0] = 0;
            // Packet batches defer diagnostic timestamps until the native runtime completes the batch.
            Field("_batchDepth", 1);
            Check(Normalize(incoming, prior)!.SequenceEqual(prior), "An in-place changed upload bypassed fresh payload validation.");
            Field("_batchDepth", 0);
            settings.AllowPlayerMapWrites.Value = false;
            settings.DungeonMarkerSync.Value = false;
            config.Save();
            var retained = new SharedMapSettings(new ConfigFile(configPath, false) { SaveOnConfigSet = false }, logger);
            Check(!retained.AllowPlayerMapWrites.Value && !retained.DungeonMarkerSync.Value,
                "Rebinding configuration overwrote operator opt-outs.");
            ((IDisposable)admin).Dispose();
        }
        finally { Directory.Delete(directory, true); }
        System.Console.WriteLine("PASS: rejected/allowed uploads, absent or malformed fields, terrain non-import and continued server collection.");
    }
}
