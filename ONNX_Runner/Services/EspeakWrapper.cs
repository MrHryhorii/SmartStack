using System.Runtime.InteropServices;

namespace ONNX_Runner.Services;

/// <summary>
/// A lightweight C# wrapper for the native C/C++ espeak-ng library.
/// Uses Platform Invocation Services (P/Invoke) and modern LibraryImport 
/// to interface directly with the compiled DLL for lightning-fast text-to-phoneme conversion.
/// </summary>
public partial class EspeakWrapper : IDisposable
{
    private const string DllPath = @"PiperNative\espeak-ng.dll";

    [LibraryImport(DllPath, StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(System.Runtime.InteropServices.Marshalling.AnsiStringMarshaller))]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int espeak_Initialize(int output, int buflength, string path, int options);

    [LibraryImport(DllPath, StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(System.Runtime.InteropServices.Marshalling.AnsiStringMarshaller))]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int espeak_SetVoiceByName(string name);

    [LibraryImport(DllPath)]
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial IntPtr espeak_TextToPhonemes(ref IntPtr textptr, int textmode, int phonememode);

    public EspeakWrapper(string dataDirectory, string voice)
    {
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
        // Allocate unmanaged memory for the UTF-8 string to pass it to the C++ DLL
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