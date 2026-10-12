using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using HeimKit.Features.WeatherGuard;

internal static class WeatherGuardPatchesTests
{
    public static void Run()
    {
        var type = typeof(WeatherGuard).Assembly.GetType("HeimKit.Features.WeatherGuard.WeatherGuardPatches")!;
        var transpiler = type.GetMethod("WeatherDamage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var target = typeof(WearNTear).GetMethod("UpdateWear", new[] { typeof(float) })!;
        // Loader Harmony targets Mono and its runtime helpers do not initialize on .NET 10.
        // Decode the installed method without loading those runtime helpers.
        var original = Instructions(target);
        var result = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original })!).ToList();
        var flag = typeof(WearNTear).GetField("m_noRoofWear")!;
        var timer = typeof(WearNTear).GetField("m_rainTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int gate = original.FindIndex(i => i.opcode == OpCodes.Ldc_R4 && Equals(i.operand, 5f) &&
            original[original.IndexOf(i) - 2].opcode == OpCodes.Stfld && Equals(original[original.IndexOf(i) - 2].operand, timer));
        if (result.Count != original.Count + 2 || result[gate + 1].opcode != OpCodes.Ldarg_0 ||
            result[gate + 2].operand is not MethodInfo method || method.Name != "WeatherContribution")
            throw new Exception("Weather suppression was not inserted at the native timed weather addition.");
        result.RemoveRange(gate + 1, 2);
        if (!result.Zip(original).All(pair => pair.First.opcode == pair.Second.opcode && Equals(pair.First.operand, pair.Second.operand)))
            throw new Exception("Weather patch changed native wetness, support, damage or control flow.");
        foreach (string native in new[] { "UpdateSupport", "ApplyDamage", "IsWet", "GetHealthPercentage" })
            if (!result.Any(i => i.operand is MethodInfo call && call.Name == native))
                throw new Exception("Native wear dependency missing: " + native);
        var changed = original.Concat(new[] { new CodeInstruction(OpCodes.Ldfld, flag) }).ToList();
        bool rejected = false;
        try { transpiler.Invoke(null, new object[] { changed }); }
        catch (TargetInvocationException error) when (error.InnerException is MissingMethodException) { rejected = true; }
        if (!rejected) throw new Exception("Changed game IL was silently patched.");
        var allow = type.GetMethod("WeatherContribution", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (float value in new[] { 0f, 5f, 100f })
            if ((float)allow.Invoke(null, new object?[] { value, null })! != value)
                throw new Exception("Inactive weather hook changes vanilla behavior.");
        var changedTimer = original.Select(i => new CodeInstruction(i.opcode, i.operand)).ToList();
        foreach (var i in changedTimer) if (i.opcode == OpCodes.Ldc_R4 && Equals(i.operand, 60f)) i.operand = 61f;
        rejected = false;
        try { transpiler.Invoke(null, new object[] { changedTimer }); }
        catch (TargetInvocationException error) when (error.InnerException is MissingMethodException) { rejected = true; }
        if (!rejected) throw new Exception("Changed native wet-timer threshold was silently patched.");
        if (typeof(WearNTearUpdater).GetMethod("UpdateWearNTear", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(float), typeof(float) }, null) == null)
            throw new Exception("Native wear pass boundary is unavailable.");
        var updater = typeof(WearNTearUpdater).GetMethod("UpdateWearNTear", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var updaterCode = Instructions(updater);
        var indexField = typeof(WearNTearUpdater).GetField("m_index", BindingFlags.Instance | BindingFlags.NonPublic);
        int nativeWearCall = updaterCode.FindIndex(i => i.operand is MethodInfo call && call.Name == "UpdateWear");
        int indexStore = updaterCode.FindIndex(i => i.opcode == OpCodes.Stfld && Equals(i.operand, indexField));
        if (indexField?.FieldType != typeof(int) || nativeWearCall < 0 || indexStore <= nativeWearCall ||
            !updaterCode.Skip(indexStore + 1).Any(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, indexField)))
            throw new Exception("Installed updater no longer exposes the audited post-wear sweep index boundary.");
        foreach (string field in new[] { "m_haveRoof", "m_shieldChangeID", "m_rainTimer", "m_rainWet" })
            if (typeof(WearNTear).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic) == null) throw new Exception("Missing evidence field " + field);
        foreach (string name in new[] { "ShouldUpdate", "IsUnderWater", "CanBeRemoved", "RPC_Damage", "RPC_Repair", "RPC_Remove" })
            if (typeof(WearNTear).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) == null) throw new Exception("Missing native/evidence method " + name);
        System.Console.WriteLine("PASS: installed UpdateWear IL accepts suppression only at the timed weather addition, preserves all original instructions/native calls, rejects changed shape and leaves inactive behavior vanilla. Unity patch execution still requires live testing.");
    }
    private static List<CodeInstruction> Instructions(MethodInfo method)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(opcode => (ushort)opcode.Value);
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var result = new List<CodeInstruction>();
        while (reader.BaseStream.Position < bytes.Length)
        {
            ushort key = reader.ReadByte();
            if (key == 0xfe) key = (ushort)(0xfe00 | reader.ReadByte());
            var opcode = opcodes[key];
            object? operand = opcode.OperandType switch
            {
                OperandType.InlineNone => null,
                OperandType.InlineField => method.Module.ResolveField(reader.ReadInt32()),
                OperandType.InlineMethod => method.Module.ResolveMethod(reader.ReadInt32()),
                OperandType.InlineType => method.Module.ResolveType(reader.ReadInt32()),
                OperandType.InlineTok => method.Module.ResolveMember(reader.ReadInt32()),
                OperandType.InlineString => method.Module.ResolveString(reader.ReadInt32()),
                OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineSig => reader.ReadInt32(),
                OperandType.ShortInlineI or OperandType.ShortInlineBrTarget => reader.ReadSByte(),
                OperandType.ShortInlineVar => reader.ReadByte(),
                OperandType.InlineVar => reader.ReadUInt16(),
                OperandType.InlineI8 => reader.ReadInt64(),
                OperandType.ShortInlineR => reader.ReadSingle(),
                OperandType.InlineR => reader.ReadDouble(),
                OperandType.InlineSwitch => Enumerable.Range(0, reader.ReadInt32()).Select(_ => reader.ReadInt32()).ToArray(),
                _ => throw new Exception("Unsupported IL operand " + opcode.OperandType)
            };
            result.Add(new CodeInstruction(opcode, operand));
        }
        return result;
    }
}
