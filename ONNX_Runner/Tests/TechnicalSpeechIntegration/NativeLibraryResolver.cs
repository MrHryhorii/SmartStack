using System.Runtime.InteropServices;

namespace ONNX_Runner.Services;

// Test-only resolver that loads the exact native DLL from this repository.
internal static class NativeLibraryResolver
{
    internal const string EspeakImportName = "espeak-ng";

    /// <summary>Pins engine eSpeak imports to the explicitly supplied native library for this test process.</summary>
    internal static void Initialize(string libraryPath)
    {
        NativeLibrary.SetDllImportResolver(
            typeof(EspeakWrapper).Assembly,
            (name, _, _) => name == EspeakImportName
                ? NativeLibrary.Load(libraryPath)
                : IntPtr.Zero);
    }
}
