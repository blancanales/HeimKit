using System.IO.Compression;
using System.Runtime.CompilerServices;
using HeimKit.Core.Lifecycle;
using HeimKit.Core.Native;
using HeimKit.Core.Prefabs;
using HeimKit.Features.SharedMap;
using SoftReferenceableAssets;
using UnityEngine;

internal static class MetadataAndMapPerformanceTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run() { MetadataReuse(); AssetReferences(); MapEvidenceAndAllocation(); SaveGuard(); }
    private static void MetadataReuse()
    {
        var registry = new PrefabRegistry();
        var first = registry.Catalog<int>("dungeon"); first[123] = 456; first.Clear();
        var next = registry.Catalog<int>("dungeon");
        Check(next.Count == 0 && next.TryReuse(123, out int value) && value == 456 && next.Count == 1,
            "Controller recreation could not reattach to retained pure metadata.");
        Check(!registry.Catalog<int>("other").TryReuse(123, out _) && !registry.Catalog<string>("dungeon").TryReuse(123, out _),
            "A cache owner or value type reused another catalog's metadata.");
        registry.Clear();
        Check(!registry.Catalog<int>("dungeon").TryReuse(123, out _), "Scene/plugin reset retained stale metadata.");
        System.Console.WriteLine("PASS: recreated metadata consumers reuse classification without selection leakage, while owner/type boundaries and scene resets invalidate stale entries.");
    }
    private static void AssetReferences()
    {
        var a = new SoftReference<GameObject>(new AssetID(0, 0, 0, 1));
        var b = new SoftReference<GameObject>(new AssetID(0, 0, 0, 2));
        int starts = 0, releases = 0; bool loaded = false, loading = true;
        var fake = (GameObject)RuntimeHelpers.GetUninitializedObject(typeof(GameObject));
        NativeAccess.Field<UnityEngine.Object, IntPtr>("m_CachedPtr")(fake) = new IntPtr(1);
        using var request = new PrefabAssetRequest(_ => true, _ => starts++, _ => loaded, _ => loading, _ => fake, _ => releases++);
        Check(!request.TryGet(a, 1, out _) && starts == 1 && request.Pending, "Async start synchronously returned metadata or failed to retain its reference.");
        for (int i = 0; i < 20; i++) Check(!request.TryGet(a, 2, out _), "An incomplete asset became usable.");
        Check(starts == 1 && releases == 0, "Polling acquired duplicate native references.");
        Check(!request.TryGet(b, 3, out _) && starts == 2 && releases == 1, "Replacing a request leaked the original reference.");
        loaded = true;
        Check(request.TryGet(b, 4, out var asset) && ReferenceEquals(asset, fake) && request.LastOutcome == "ready", "Completed async metadata did not become usable.");
        request.Dispose(); request.Dispose(); Check(releases == 2, "Disposal leaked or double-released a native reference.");
        loaded = false; loading = true;
        request.TryGet(a, 10, out _);
        Check(request.TryGet(a, 131, out _) && request.LastOutcome == "timeout", "An unavailable async load retained its wait indefinitely.");
        request.Dispose(); Check(releases == 3, "Timeout cleanup leaked its reference.");
        using var failed = new PrefabAssetRequest(_ => true, _ => throw new IOException("injected start fault"), _ => false, _ => false, _ => null, _ => releases++);
        bool surfaced = false; try { failed.TryGet(a, 1, out _); } catch (IOException) { surfaced = true; }
        Check(surfaced && !failed.Pending && releases == 4, "A native start failure was swallowed or leaked its held reference.");
        System.Console.WriteLine("PASS: async asset admission never waits or reacquires while polling; replacement, completion, timeout, failed starts and repeated disposal balance native references.");
    }
    private static void MapEvidenceAndAllocation()
    {
        const int size = 2048, count = size * size;
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
        {
            writer.Write(3); writer.Write(count); writer.Write(new byte[count]); writer.Write(2);
            void Pin(string name, int icon)
            { writer.Write(42L); writer.Write(name); writer.Write(1f); writer.Write(2f); writer.Write(3f); writer.Write(icon); writer.Write(true); writer.Write("author"); }
            Pin("Keep this note", 1); Pin("Death", (int)Minimap.PinType.Death);
        }
        byte[] original = raw.ToArray();
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, true)) gzip.Write(original);
        var decoded = SharedMapTableData.Decode(size, compressed.ToArray());
        Check(SharedMapTableData.ReadValidatedPins(size, decoded, true).Count == 2 && SharedMapTableData.ReadValidatedPins(size, decoded).Count == 1,
            "Trusted pin evidence lost the original death pin or gameplay retained it.");
        var exploration = new SharedMapExploration(size, 12, 50, .3f);
        SharedMapTableData.ValidatedTable? output = null;
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[]? merged = SharedMapTableData.MergeValidated(exploration, decoded, out int removed,
            portals: Array.Empty<HeimKit.Features.SharedMap.Models.MapMarker>(), validatedOutput: value => output = value);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated < count * 2L, "Pin normalization grew/copied whole-map stream buffers beyond the bounded single terrain result.");
        Check(removed == 1 && output != null && SharedMapTableData.ReadValidatedPins(size, output, true).Single().Name == "Keep this note",
            "Normalized evidence lost a player pin or retained a removed death pin.");
        Check(SharedMapTableData.ReadPins(size, merged).Single().Name == "Keep this note" && original.AsSpan().SequenceEqual(decoded!.OriginalRaw),
            "Trusted evidence disagrees with native-format output or changed the validated source bytes.");
        System.Console.WriteLine("PASS: full-sized table pin normalization bounds large allocations, preserves immutable original/death-pin evidence and supplies diagnostics without another decompression.");
    }
    private static void SaveGuard()
    {
        Check(ServerSaveRequests.NeedsFallback(true, true, true, false), "The observed missing-platform dedicated-server case was not guarded.");
        Check(!ServerSaveRequests.NeedsFallback(false, true, true, false) && !ServerSaveRequests.NeedsFallback(true, false, true, false) &&
            !ServerSaveRequests.NeedsFallback(true, true, false, false) && !ServerSaveRequests.NeedsFallback(true, true, true, true),
            "Save fallback changed a client, non-dedicated, unready or ordinary platform guard.");
        var error = new IOException("native save failure");
        foreach (string method in new[] { "RequestEnded", "SaveEnded" })
        {
            var end = NativeAccess.Method<Func<Exception?, long, Exception?>>(typeof(ServerSaveRequests), method, typeof(Exception), typeof(long));
            Check(ReferenceEquals(end(error, 0), error), "Save observations swallowed or replaced a native exception.");
        }
        var active = typeof(ServerSaveRequests).GetField("_current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        object? previous = active.GetValue(null);
        using var failingLog = new BepInEx.Logging.ManualLogSource("Failing save observation");
        failingLog.LogEvent += (_, _) => throw new InvalidOperationException("injected listener failure");
        var observation = (ServerSaveRequests)RuntimeHelpers.GetUninitializedObject(typeof(ServerSaveRequests));
        NativeAccess.Field<ServerSaveRequests, BepInEx.Logging.ManualLogSource>("_log")(observation) = failingLog;
        try
        {
            active.SetValue(null, observation);
            var end = NativeAccess.Method<Func<Exception?, long, Exception?>>(typeof(ServerSaveRequests), "RequestEnded", typeof(Exception), typeof(long));
            Check(ReferenceEquals(end(error, 0), error), "A failing diagnostics listener replaced the original native save exception.");
        }
        finally { active.SetValue(null, previous); }
        System.Console.WriteLine("PASS: save fallback is limited to ready dedicated servers missing platform metadata; native errors remain unchanged.");
    }
}
