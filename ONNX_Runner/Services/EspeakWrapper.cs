using System.Runtime.InteropServices;
using System.Reflection;

namespace ONNX_Runner.Services;

/// <summary>
/// A lightweight C# wrapper for the native C/C++ espeak-ng library.
/// Uses Platform Invocation Services (P/Invoke) and modern LibraryImport 
/// to interface directly with the compiled library for lightning-fast text-to-phoneme conversion.
/// </summary>
public partial class EspeakWrapper : IDisposable
{
    // Universal library name without path or extension.
    // .NET will automatically append .dll on Windows, .so on Linux, and .dylib on macOS.
    private const string DllPath = "espeak-ng";

    // Windows-specific API to convert long paths to short 8.3 format, ensuring compatibility with older C++ libraries that may not handle long paths well.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern int GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, int cchBuffer);

    /// <summary>
    /// Static constructor sets up a smart cross-platform DLL resolver.
    /// Since espeak-ng is a native C++ binary and not a standard .NET NuGet package, 
    /// this resolver ensures smooth execution on both local Windows machines (using the local PiperNative folder) 
    /// and Docker Linux containers (using system-installed libraries).
    /// </summary>
    static EspeakWrapper()
    {
        NativeLibrary.SetDllImportResolver(typeof(EspeakWrapper).Assembly, ImportResolver);
    }

    private static IntPtr ImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // For Windows: explicitly route to the local structured folder to keep the project root clean.
        if (libraryName == DllPath && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return NativeLibrary.Load(@"PiperNative\espeak-ng.dll", assembly, searchPath);
        }

        // For Linux/macOS (Docker): return IntPtr.Zero to let .NET fall back to its default behavior,
        // which perfectly locates system-installed libraries (e.g., via apt-get install espeak-ng).
        return IntPtr.Zero;
    }

    // UTF-8 marshalling is critical for correctly passing string data (like voice names) to the native library,
    // especially when dealing with internationalization and non-ASCII characters.
    [LibraryImport(DllPath, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int espeak_Initialize(int output, int buflength, string path, int options);

    // Setting the voice by name allows for dynamic language switching at runtime, which is essential for multi-language TTS applications.
    [LibraryImport(DllPath, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int espeak_SetVoiceByName(string name);

    // This function is the core of the wrapper, converting raw text to IPA phonemes.
    [LibraryImport(DllPath)]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial IntPtr espeak_TextToPhonemes(ref IntPtr textptr, int textmode, int phonememode);

    public EspeakWrapper(string dataDirectory, string voice)
    {
        // MAGIC: If we are on Windows, convert potentially problematic paths (like Cyrillic or spaces) 
        // into safe 8.3 ASCII short paths before passing them to the C++ library.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var shortPath = new System.Text.StringBuilder(255);
            int result = GetShortPathName(dataDirectory, shortPath, shortPath.Capacity);
            if (result > 0)
            {
                dataDirectory = shortPath.ToString();
            }
        }

        int initResult = espeak_Initialize(2, 0, dataDirectory, 0);
        if (initResult < 0)
        {
            throw new Exception($"Failed to initialize espeak-ng. Error code: {initResult}");
        }

        int voiceResult = espeak_SetVoiceByName(voice);
        if (voiceResult != 0)
        {
            Console.WriteLine($"[WARNING] Failed to set espeak voice to '{voice}'.");
        }
    }

    /// <summary>
    /// Allows dynamic language/voice switching just before transcription.
    /// Essential for multi-language or mixed-language TTS generation.
    /// </summary>
    public void SetVoice(string voice)
    {
        int voiceResult = espeak_SetVoiceByName(voice);
        if (voiceResult != 0)
        {
            Console.WriteLine($"[WARNING] Failed to set espeak voice to '{voice}'.");

            // Throw an exception so the higher-level fallback mechanism 
            // (e.g., PhonemeFallbackMapper) can intercept it and take over.
            throw new Exception("Voice not found");
        }
    }

    /// <summary>
    /// Converts raw text into IPA (International Phonetic Alphabet) phonemes using the native espeak-ng engine.
    /// Carefully manages unmanaged memory allocations to prevent memory leaks during heavy server load.
    /// </summary>
    public string GetIpaPhonemes(string text)
    {
        // Allocate unmanaged memory for the UTF-8 string to pass it to the C++ library
        IntPtr textPtr = Marshal.StringToCoTaskMemUTF8(text);
        IntPtr currentPtr = textPtr;
        var sb = new System.Text.StringBuilder();

        try
        {
            while (currentPtr != IntPtr.Zero)
            {
                // 1 = textmode (UTF8), 2 = phonememode (IPA)
                IntPtr resultPtr = espeak_TextToPhonemes(ref currentPtr, 1, 2);

                if (resultPtr != IntPtr.Zero)
                {
                    string part = Marshal.PtrToStringUTF8(resultPtr) ?? string.Empty;
                    sb.Append(part);
                }
            }

            // Return the clean, concatenated IPA result
            return sb.ToString().Trim();
        }
        finally
        {
            // CRITICAL: Always free the unmanaged memory to prevent severe memory leaks.
            // If this is missed, the server's RAM usage will grow infinitely with each request.
            Marshal.FreeCoTaskMem(textPtr);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}