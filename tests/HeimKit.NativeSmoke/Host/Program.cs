using System.Runtime.InteropServices;
internal static class Program
{
    private const string Mono = "valheim-mono";
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern void mono_set_dirs(string lib, string etc);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern void mono_set_assemblies_path(string path);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern void mono_config_parse(string path);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr mono_jit_init_version(string name, string version);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr mono_domain_assembly_open(IntPtr domain, string path);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr mono_assembly_get_image(IntPtr assembly);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr mono_class_from_name(IntPtr image, string ns, string name);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr mono_class_get_method_from_name(IntPtr klass, string name, int count);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr mono_runtime_invoke(IntPtr method, IntPtr obj, IntPtr args, out IntPtr error);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr mono_object_to_string(IntPtr obj, out IntPtr error);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr mono_string_to_utf8(IntPtr value);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern void mono_free(IntPtr value);
    [DllImport(Mono, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr mono_get_runtime_build_info();
    private static int Main(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("Supply Mono runtime DLL, game managed directory, probe assembly, BepInEx core directory and plugin output directory.");
        var library = NativeLibrary.Load(args[0]);
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, asm, path) => name == Mono ? library : IntPtr.Zero);
        var runtime = Path.GetDirectoryName(Path.GetDirectoryName(args[0]))!;
        mono_set_dirs(args[1], Path.Combine(runtime, "etc"));
        mono_set_assemblies_path(args[1]+Path.PathSeparator+args[3]+Path.PathSeparator+args[4]+Path.PathSeparator+Path.GetDirectoryName(args[2]));
        mono_config_parse(null);
        var domain = mono_jit_init_version("HeimKit isolated accessor check", "v4.0.30319");
        if (domain == IntPtr.Zero) throw new InvalidOperationException("Mono initialization failed.");
        Console.WriteLine("Mono: " + Marshal.PtrToStringUTF8(mono_get_runtime_build_info()));
        var assembly = mono_domain_assembly_open(domain, args[2]);
        if (assembly == IntPtr.Zero) { Console.Error.WriteLine("Cannot load smoke assembly."); return 2; }
        var method = mono_class_get_method_from_name(mono_class_from_name(mono_assembly_get_image(assembly), "", "MonoSmoke"), "Run", 0);
        if (method == IntPtr.Zero) { Console.Error.WriteLine("Cannot resolve smoke entry."); return 3; }
        var result = mono_runtime_invoke(method, IntPtr.Zero, IntPtr.Zero, out var error);
        if (error != IntPtr.Zero) result = mono_object_to_string(error, out _);
        var text = mono_string_to_utf8(result);
        Console.WriteLine(Marshal.PtrToStringUTF8(text)); mono_free(text);
        return error == IntPtr.Zero ? 0 : 1;
    }
}
