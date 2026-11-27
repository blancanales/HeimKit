using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HeimKit.Features.SharedMap;

internal static class SharedMapPortalTint
{
    // UpdatePins has already applied visibility, the shared-data fade and map filters here.
    // Adjust the existing icon colour in the native loop without walking the pin list again.
    internal static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var icon = AccessTools.Field(typeof(Minimap.PinData), "m_iconElement");
        var setColor = AccessTools.PropertySetter(typeof(Graphic), nameof(Graphic.color));
        for (int i = 1; i + 2 < code.Count; i++)
        {
            if (!code[i].LoadsField(icon) || code[i - 1].opcode != OpCodes.Ldloc_S ||
                code[i + 1].opcode != OpCodes.Ldloc_S || !code[i + 2].Calls(setColor)) continue;
            code.InsertRange(i + 2, new[] {
                new CodeInstruction(code[i - 1].opcode, code[i - 1].operand),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedMapPortalTint), nameof(Tint)))
            });
            return code;
        }
        throw new InvalidOperationException("Minimap.UpdatePins portal icon colour assignment unavailable.");
    }

    private static Color Tint(Color native, Minimap.PinData pin)
    {
        if (!SharedMapClientPortalStatus.IsPortal(pin)) return native;
        bool disconnected = SharedMapClient.DisplayPortalStatus(pin) ??
            (pin.m_ownerID == SharedMapTableData.UnconnectedPortalOwner);
        return disconnected ? new Color(1f, 1f, 1f, native.a) : native;
    }
}
