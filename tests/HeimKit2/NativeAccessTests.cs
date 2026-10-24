using System.Reflection;
using HeimKit.Core.Native;
using UnityEngine;

internal static class NativeAccessTests
{
    private class BaseFields { private long _owner = 0; public long Owner => _owner; }
    private sealed class Fields : BaseFields
    {
        private bool _flag = false;
        private float _timer = 0;
        private List<Vector3> _path = new();
        public bool Flag => _flag;
        public float Timer => _timer;
        public List<Vector3> Path => _path;
    }
    public static void Run()
    {
        var value = new Fields();
        var flag = NativeAccess.Field<Fields, bool>("_flag");
        var timer = NativeAccess.Field<Fields, float>("_timer");
        var owner = NativeAccess.Field<Fields, long>("_owner");
        var path = NativeAccess.Field<Fields, List<Vector3>>("_path");
        var points = new List<Vector3> { new(1, 2, 3) };
        flag(value) = true; timer(value) = 12.5f; owner(value) = 123; path(value) = points;
        if (!value.Flag || value.Timer != 12.5f || value.Owner != 123 || !ReferenceEquals(value.Path, points) ||
            !ReferenceEquals(flag, NativeAccess.Field<Fields, bool>("_flag")))
            throw new Exception("Cached native fields lost inherited/private access, assignment or delegate identity.");
        for (int i = 0; i < 100; i++) timer(value) += 1;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) timer(value) += 1;
        if (GC.GetAllocatedBytesForCurrentThread() != before) throw new Exception("Warmed native field access allocated.");
        foreach (string name in new[] { "_missing", "_flag" })
        {
            bool rejected = false;
            try { NativeAccess.Field<Fields, int>(name); } catch (MissingFieldException) { rejected = true; }
            if (!rejected) throw new Exception("Missing or mistyped native field was accepted.");
        }
        System.Console.WriteLine("PASS: cached native access reads/writes private and inherited fields without boxing or per-call allocations and rejects missing/mistyped members.");
    }
}
