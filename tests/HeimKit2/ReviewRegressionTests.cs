using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HeimKit.Core.Ownership;
using HeimKit.Core.Players;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using UnityEngine;

internal static class ReviewRegressionTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Run() { ChangeAllocations(); BroadOrderingAndLifetime(); Recency(); FailureIncidents(); LiveAccess(); }
    internal sealed class Presence : IPlayerPresence
    {
        public bool? HasConnectedPlayers => true;
        public bool? HasJoinedPlayers => true;
        public int? ConnectedCount => 1;
        public int? ReadyCount => 1;
        public int? JoinedCount => 1;
        public IReadOnlyList<ZNetPeer> Peers => Array.Empty<ZNetPeer>();
        public void Refresh() { }
        public void Clear() { }
    }
    internal sealed class World : IDisposable
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly FieldInfo _worldSlot = typeof(ZDOMan).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly FieldInfo _zonesSlot = typeof(ZoneSystem).GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _priorWorld, _priorZones;
        internal readonly ZDOMan Manager = (ZDOMan)RuntimeHelpers.GetUninitializedObject(typeof(ZDOMan));
        internal readonly Dictionary<ZDOID, ZDO> Records = new();
        private readonly List<ZDO>[] _sectors = new List<ZDO>[512 * 512];
        internal World()
        {
            _priorWorld = _worldSlot.GetValue(null); _priorZones = _zonesSlot.GetValue(null);
            typeof(ZDOMan).GetField("m_objectsByID", InstanceFields)!.SetValue(Manager, Records);
            typeof(ZDOMan).GetField("m_objectsBySector", InstanceFields)!.SetValue(Manager, _sectors);
            typeof(ZDOMan).GetField("m_portalObjects", InstanceFields)!.SetValue(Manager, new Dictionary<ZoneSystem.SectorIndex, List<ZDO>>());
            typeof(ZDOMan).GetField("m_visitedSectorIndices", InstanceFields)!.SetValue(Manager, new HashSet<ZoneSystem.SectorIndex>());
            var zones = (ZoneSystem)RuntimeHelpers.GetUninitializedObject(typeof(ZoneSystem)); zones.m_zoneSize = 64;
            _worldSlot.SetValue(null, Manager); _zonesSlot.SetValue(null, zones);
        }
        internal ZDO Add(uint id, int x = 0, int z = 0, bool distant = false)
        {
            var record = (ZDO)RuntimeHelpers.GetUninitializedObject(typeof(ZDO)); record.m_uid = new ZDOID(71, id);
            typeof(ZDO).GetField("m_prefab", InstanceFields)!.SetValue(record, 7);
            typeof(ZDO).GetField("m_position", InstanceFields)!.SetValue(record, new Vector3(64 * x, 0, 64 * z));
            if (distant) record.Distant = true;
            Records.Add(record.m_uid, record);
            uint sector = ZoneSystem.SectorToIndex(new Vector2s(x, z)).Sector;
            (_sectors[sector] ??= new List<ZDO>()).Add(record);
            return record;
        }
        internal void Move(ZDO record, int x, int z)
        {
            _sectors[ZoneSystem.SectorToIndex(ZoneSystem.GetZone(record.GetPosition())).Sector]?.Remove(record);
            typeof(ZDO).GetField("m_position", InstanceFields)!.SetValue(record, new Vector3(64 * x, 0, 64 * z));
            (_sectors[ZoneSystem.SectorToIndex(new Vector2s(x, z)).Sector] ??= new List<ZDO>()).Add(record);
        }
        internal void Remove(ZDO record)
        {
            _sectors[ZoneSystem.SectorToIndex(ZoneSystem.GetZone(record.GetPosition())).Sector]?.Remove(record);
            Records.Remove(record.m_uid);
        }
        internal void Reuse(ZDO record, uint id, int x, int z)
        {
            record.m_uid = new ZDOID(71, id); Records.Add(record.m_uid, record);
            typeof(ZDO).GetField("m_position", InstanceFields)!.SetValue(record, new Vector3(64 * x, 0, 64 * z));
            (_sectors[ZoneSystem.SectorToIndex(new Vector2s(x, z)).Sector] ??= new List<ZDO>()).Add(record);
        }
        public void Dispose() { _worldSlot.SetValue(null, _priorWorld); _zonesSlot.SetValue(null, _priorZones); }
    }
    private static void ChangeAllocations()
    {
        using var world = new World(); var torch = world.Add(1);
        using var provider = new WorldObjectProvider(new Presence(), observe: false);
        provider.BeginCycle(true);
        using var subscription = provider.Subscribe("lone_torch", z => z.GetPrefab() == 7);
        var result = new List<ZDO>();
        void Change() { provider.Enqueue(torch.m_uid); result.Clear(); subscription.Query(default, new SimulationDistance(0, 0, true), result); }
        Change(); for (int i = 0; i < 30; i++) Change();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Change();
        Check(GC.GetAllocatedBytesForCurrentThread() == before && result.Count == 1, "A same-cell lone match allocated while accepting changes.");
        typeof(ZDO).GetField("m_prefab", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(torch, 9);
        Change(); Check(result.Count == 0, "Changed eligibility retained a stale candidate.");
        System.Console.WriteLine("PASS: 1000 accepted changes to a lone same-cell match allocate zero bytes and changed eligibility still removes it.");
    }
    private static void BroadOrderingAndLifetime()
    {
        using var world = new World(); uint id = 1;
        // Deliberately insert in a different order from native sector traversal.
        for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++) world.Add(id++, x, z, (x + z) % 2 == 0);
        int scans = 0;
        void Scan(Vector2s sector, SimulationDistance distance, List<ZDO> near, List<ZDO>? far)
        { scans++; world.Manager.FindSectorObjects(sector, distance, near, far); }
        using var provider = new WorldObjectProvider(new Presence(), Scan, observe: false);
        provider.BeginCycle(true);
        var actual = new List<ZDO>(); var actualFar = new List<ZDO>(); var expected = new List<ZDO>(); var expectedFar = new List<ZDO>();
        var broad = provider.For("broad");
        broad.Query(default, new SimulationDistance(1, 1, true), actual, actualFar);
        Check(typeof(WorldObjectProvider).GetField("_index", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider) == null, "Broad-only discovery created an index and could acquire change hooks.");
        var added = world.Add(500);
        actual.Clear(); actualFar.Clear(); broad.Query(default, new SimulationDistance(1, 1, true), actual, actualFar);
        Check(scans == 2 && actual.Contains(added), "Broad-only discovery reused an unobserved stale frame cache.");
        using (var targeted = provider.Subscribe("target", _ => true))
        {
            foreach (var distance in new[] { new SimulationDistance(1, 2, true), new SimulationDistance(1, 2, false), new SimulationDistance(0, 2, false) })
            {
                expected.Clear(); expectedFar.Clear(); actual.Clear(); actualFar.Clear();
                world.Manager.FindSectorObjects(default, distance, expected, expectedFar);
                broad.Query(default, distance, actual, actualFar);
                Check(actual.SequenceEqual(expected) && actualFar.SequenceEqual(expectedFar), "Shared broad views changed installed native ring order or near/distant selection.");
                int prior = scans;
                actual.Clear(); actualFar.Clear(); broad.Query(default, distance, actual, actualFar);
                Check(scans == prior, "Observed broad consumers repeated native cells.");
            }
            provider.Enqueue(added.m_uid);
            actual.Clear(); actualFar.Clear(); broad.Query(default, new SimulationDistance(1, 2, true), actual, actualFar);
            Check(actual.SequenceEqual(NativeNear(world.Manager, new SimulationDistance(1, 2, true))), "A same-cell data change reordered native broad results.");
        }
        Check(typeof(WorldObjectProvider).GetField("_index", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider) == null, "The last targeted lease did not release the shared index.");
        int before = scans; actual.Clear(); broad.Query(default, new SimulationDistance(0, 0, true), actual);
        Check(scans == before + 1, "Broad consumers kept caching after targeted discovery stopped.");
        System.Console.WriteLine("PASS: broad views match installed native centre/ring order, share observed scans, bypass hooks/caches when broad-only and release the index with the last targeted lease.");
    }
    private static List<ZDO> NativeNear(ZDOMan world, SimulationDistance distance)
    { var near = new List<ZDO>(); world.FindSectorObjects(default, distance, near, new List<ZDO>()); return near; }
    private static void Recency()
    {
        var order = new AccessOrder<int>(); order.Add(1); order.Add(2); order.Add(3); order.Touch(1);
        Check(order.RemoveOldest() == 2, "A recently read cell was evicted before an older cell."); order.Add(4);
        for (int i = 0; i < 30; i++) { int key = order.RemoveOldest(); order.Add(key); order.Touch(1); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { int key = order.RemoveOldest(); order.Add(key); order.Touch(1); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before && order.Count == 3, "Recency maintenance allocated or grew past its owning cache.");
        System.Console.WriteLine("PASS: LRU eviction retains recently used cells and recycles nodes with zero warmed maintenance allocations.");
    }
    private sealed class FailedAdministration : IServerModule
    {
        internal int Site;
        public string Name => "Administration";
        public bool IsActive { get; private set; }
        public void Initialize(string version) => IsActive = true;
        public void Shutdown() => IsActive = false;
        public void Tick(float now) { if (Site == 0) First(); else Second(); }
        [MethodImpl(MethodImplOptions.NoInlining)] private static void First() => throw new InvalidOperationException("variable message " + Guid.NewGuid());
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Second() => throw new InvalidOperationException("another location");
    }
    private static void FailureIncidents()
    {
        using var log = new ManualLogSource("Review failure checks"); var messages = new List<string>();
        log.LogEvent += (_, entry) => messages.Add(LogCapture.Text(entry.Data));
        var module = new FailedAdministration();
        using var host = new ModuleHost(log); host.Register(() => module, module.Name); host.Initialize("test");
        host.Tick(0); host.Tick(1); host.Tick(2);
        Check(messages.Count == 1, "Volatile exception messages bypassed incident suppression.");
        host.Tick(60); Check(messages.Count == 2 && messages[1].Contains("repeats_suppressed=2") && !messages[1].Contains("variable message"), "Repeated failure did not produce a compact minute reminder.");
        module.Site = 1; host.Tick(61);
        Check(messages.Count == 3 && messages[2].Contains("another location"), "A distinct failure from inactive administration was swallowed.");
        host.Tick(62); Check(messages.Count == 3, "A repeated admin failure escaped its incident limit.");
        System.Console.WriteLine("PASS: inactive administration errors preserve distinct failure locations, suppress volatile repeats and emit compact minute reminders.");
    }
    private static void LiveAccess()
    {
        const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
        foreach (string name in new[] { "Freeze", "Pause" })
        {
            var method = typeof(ObjectTransferAuthority).GetMethod(name, methods | BindingFlags.Instance)!;
            var calls = Calls(method).ToArray();
            Check(calls.Any(call => call.Name == "GetComponent") && calls.All(call => call.DeclaringType != typeof(PrefabRegistry)), "Live authority path still routes component access through the prefab registry.");
        }
        Check(typeof(WorldObjectProvider).GetFields(BindingFlags.Static | BindingFlags.NonPublic).All(field => !field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(HarmonyLib.AccessTools.FieldRef<,>)), "Provider initialization still resolves a consumer-specific native field.");
        var live = typeof(WorldObjectProvider).GetNestedType("LiveInstances", BindingFlags.NonPublic)!;
        Check((live.Attributes & TypeAttributes.BeforeFieldInit) == 0, "Live-instance field resolution can run before the owning compatibility check.");
        System.Console.WriteLine("PASS: compiled live authority paths call GetComponent directly and provider startup defers consumer-specific live-instance resolution.");
    }
    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => code.Value);
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (int index = 0; index < bytes.Length;)
        {
            short value = bytes[index++]; if (value == 0xfe) value = (short)(0xfe00 | bytes[index++]);
            var code = codes[value];
            if (code == OpCodes.Call || code == OpCodes.Callvirt) yield return method.Module.ResolveMethod(BitConverter.ToInt32(bytes, index))!;
            index += code.OperandType switch { OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1, OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8, OperandType.InlineSwitch => 4 + BitConverter.ToInt32(bytes, index) * 4, _ => 4 };
        }
    }
}
