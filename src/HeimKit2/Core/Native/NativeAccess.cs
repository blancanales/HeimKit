using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace HeimKit.Core.Native;

// Resolution belongs here; consumers retain typed accessors, never reflection metadata.
internal static class NativeAccess
{
    private static readonly Dictionary<(Type, string, Type), object> Fields = new();
    private static readonly Dictionary<(Type, string, Type), Delegate> Methods = new();
    internal static AccessTools.FieldRef<T, V> Field<T, V>(string name)
    {
        var key = (typeof(T), name, typeof(V));
        if (!Fields.TryGetValue(key, out var value))
        {
            var field = ResolveField(typeof(T), name);
            if (field.FieldType != typeof(V)) throw new MissingFieldException(typeof(T).FullName, name + " has an incompatible type");
            var getter = new DynamicMethod("fieldref_" + name, typeof(V).MakeByRefType(), new[] { typeof(T) }, typeof(NativeAccess).Module, true);
            var il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldflda, field); il.Emit(OpCodes.Ret);
            Fields[key] = value = getter.CreateDelegate(typeof(AccessTools.FieldRef<T, V>));
        }
        return (AccessTools.FieldRef<T, V>)value;
    }
    internal static T Method<T>(Type owner, string name, params Type[] parameters) where T : Delegate
    {
        var key = (owner, name, typeof(T));
        if (!Methods.TryGetValue(key, out var value))
            Methods[key] = value = Target(owner, name, parameters).CreateDelegate(typeof(T));
        return (T)value;
    }
    internal static MethodInfo Target(Type owner, string name, params Type[] parameters)
    {
        for (var type = owner; type != null; type = type.BaseType)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, parameters, null);
            if (method != null) return method;
        }
        throw new MissingMethodException(owner.FullName, name);
    }
    private static FieldInfo ResolveField(Type owner, string name)
    {
        for (var type = owner; type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(owner.FullName, name);
    }
    internal static Func<V> StaticField<V>(Type owner, string name)
    {
        var field = ResolveField(owner, name);
        return Expression.Lambda<Func<V>>(Expression.Convert(Expression.Field(null, field), typeof(V))).Compile();
    }

    // Private nested peer types cannot appear in public C# signatures. Compile their
    // accesses once to object-shaped delegates; no reflection runs when sending data.
    internal static Func<object, V> ObjectField<V>(Type owner, string name)
    {
        var key = (owner, name, typeof(Func<object, V>));
        if (!Fields.TryGetValue(key, out var cached))
        {
            var input = Expression.Parameter(typeof(object));
            var field = ResolveField(owner, name);
            Fields[key] = cached = Expression.Lambda<Func<object, V>>(Expression.Convert(Expression.Field(Expression.Convert(input, owner), field), typeof(V)), input).Compile();
        }
        return (Func<object, V>)cached;
    }
    internal static Func<ZDOMan, long, object> PeerGetter()
    {
        var world = Expression.Parameter(typeof(ZDOMan)); var id = Expression.Parameter(typeof(long));
        return Expression.Lambda<Func<ZDOMan, long, object>>(Expression.Convert(Expression.Call(world, Target(typeof(ZDOMan), "GetPeer", typeof(long)), id), typeof(object)), world, id).Compile();
    }
    internal static Func<uint, ushort, float, object> PeerInfo(Type type)
    {
        var data = Expression.Parameter(typeof(uint)); var owner = Expression.Parameter(typeof(ushort)); var now = Expression.Parameter(typeof(float));
        var ctor = type.GetConstructor(new[] { typeof(uint), typeof(ushort), typeof(float) }) ?? throw new MissingMethodException(type.FullName, ".ctor");
        return Expression.Lambda<Func<uint, ushort, float, object>>(Expression.Convert(Expression.New(ctor, data, owner, now), typeof(object)), data, owner, now).Compile();
    }
}
