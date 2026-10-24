using System.Reflection;
using BepInEx;

internal static class ApplicationIdentityTests
{
    public static void Run()
    {
        var type = typeof(HeimKit.Plugin);
        var assembly = type.Assembly;
        var registration = type.CustomAttributes.Single(attribute => attribute.AttributeType == typeof(BepInPlugin));
        if (assembly.GetName().Name != "HeimKit" || type.FullName != "HeimKit.Plugin" ||
            (string?)registration.ConstructorArguments[0].Value != "heimkit" ||
            (string?)registration.ConstructorArguments[1].Value != "HeimKit" ||
            assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product != "HeimKit")
            throw new Exception("The rebuild must retain the HeimKit application, assembly, namespace and loader identity.");
        if (type.CustomAttributes.Any(attribute => attribute.AttributeType == typeof(BepInIncompatibility)))
            throw new Exception("The rebuild must not declare incompatibility with its own loader identity.");
        System.Console.WriteLine("PASS: rebuilt plugin retains HeimKit product, DLL, namespace and loader identity without self-incompatibility.");
    }
}
