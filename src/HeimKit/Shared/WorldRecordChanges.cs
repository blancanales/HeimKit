using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace HeimKit.Shared;

// One set of native hooks, leased only by occupied controllers. Hooks enqueue
// identities; classification and all game work run later in the consumer.
internal static class WorldRecordChanges
{
    private static readonly Harmony Hooks = new("heimkit.world-record-changes");
    private static readonly List<WorldCandidateIndex> Consumers = new();
    private static readonly HashSet<ZDOID> Pending = new();
    private static bool _overflow;
    internal static void Add(WorldCandidateIndex consumer)
    {
        if (Consumers.Count == 0)
        {
            try
            {
                var changed = new HarmonyMethod(typeof(WorldRecordChanges), nameof(Changed));
                foreach (var method in NativeTargets()) Hooks.Patch(method, postfix: changed);
            }
            catch { Hooks.UnpatchSelf(); throw; }
        }
        Consumers.Add(consumer);
    }
    internal static IEnumerable<MethodInfo> NativeTargets()
    {
        foreach (var method in typeof(ZDO).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            if (method.Name == "Deserialize" || method.Name == "SetPrefab" || method.Name == "InternalSetPosition" ||
                method.Name == "SetOwnerInternal" || method.Name == "SetRotation" || method.Name == "SetDistant" ||
                method.Name == "Set" && method.GetParameters()[0].ParameterType == typeof(int) ||
                method.Name.StartsWith("Remove", StringComparison.Ordinal)) yield return method;
    }
    internal static void Remove(WorldCandidateIndex consumer)
    {
        Consumers.Remove(consumer);
        if (Consumers.Count == 0)
        { Pending.Clear(); _overflow = false; Hooks.UnpatchSelf(); }
    }
    internal static void Dispatch()
    {
        // Fan out deduplicated IDs at processing time, rather than making every
        // native setter insert into every feature's queue.
        if (_overflow) foreach (var consumer in Consumers) consumer.Invalidate();
        foreach (var id in Pending) foreach (var consumer in Consumers) consumer.Enqueue(id);
        Pending.Clear(); _overflow = false;
    }
    private static void Changed(ZDO __instance)
    {
        if (Consumers.Count == 0 || __instance == null || __instance.m_uid.IsNone()) return;
        if (Pending.Count < WorldCandidateIndex.Capacity || Pending.Contains(__instance.m_uid)) Pending.Add(__instance.m_uid);
        else _overflow = true;
    }
}
