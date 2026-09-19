using System.Runtime.InteropServices;

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
    private const string DllPath = NativeLibraryResolver.EspeakImportName;

    // Thread-safety lock object. Since the underlying espeak-ng C++ library is not thread-safe 
    // and uses global states, this lock prevents race conditions and segmentation faults (segfaults) 
    // under parallel requests from AI agents or multi-threaded pipelines.
    private static readonly Lock _espeakLock = new();

    // eSpeak keeps the selected voice in process-wide native state. This cache is therefore
    // static as well and is read/written only while _espeakLock is held. It lets repeated
    // same-language chunks skip redundant espeak_SetVoiceByName calls without introducing
    // per-instance state that could become stale under concurrent requests.
    private static string? _currentNativeVoice;

    // Windows-specific API to convert long paths to short 8.3 format, ensuring compatibility with older C++ libraries that may not handle long paths well.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, int cchBuffer);

    // Native library resolution is centralized in NativeLibraryResolver.

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

        // Initialization and initial voice selection both mutate process-wide eSpeak state,
        // so they must participate in the same synchronization domain as runtime phonemization.
        lock (_espeakLock)
        {
            int initResult = espeak_Initialize(2, 0, dataDirectory, 0);
            if (initResult < 0)
            {
                throw new Exception($"Failed to initialize espeak-ng. Error code: {initResult}");
            }

            // A fresh initialization may reset native state, so invalidate any previous cache.
            _currentNativeVoice = null;

            if (!TrySelectVoiceLocked(voice))
            {
                // Preserve the constructor's historical behavior: warn, but do not fail startup.
                _currentNativeVoice = null;
            }
        }
    }

    /// <summary>
    /// Allows dynamic language/voice switching for callers that need explicit state changes.
    /// The main mixed-language pipeline should prefer TryGetIpaPhonemes(text, voice), which keeps
    /// voice selection and transcription atomic under the same native-state lock.
    /// </summary>
    public void SetVoice(string voice)
    {
        lock (_espeakLock)
        {
            if (!TrySelectVoiceLocked(voice))
            {
                // Preserve the existing public API contract so external callers that rely on
                // SetVoice exceptions continue to behave exactly as before.
                throw new Exception("Voice not found");
            }
        }
    }

    /// <summary>
    /// Converts raw text into IPA using the currently selected native eSpeak voice.
    /// This method remains for compatibility. When language switching is involved, prefer the
    /// atomic TryGetIpaPhonemes(text, voice) overload below.
    /// </summary>
    public string GetIpaPhonemes(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        IntPtr textPtr = Marshal.StringToCoTaskMemUTF8(text);
        IntPtr currentPtr = textPtr;
        var sb = new System.Text.StringBuilder();

        try
        {
            lock (_espeakLock)
            {
                PhonemizeLocked(ref currentPtr, sb);
            }

            return sb.ToString().Trim();
        }
        finally
        {
            Marshal.FreeCoTaskMem(textPtr);
        }
    }

    /// <summary>
    /// Atomically selects the requested eSpeak voice and converts text to IPA while holding the
    /// same lock for the entire native operation. Returns false only when the requested voice
    /// cannot be selected; native transcription failures still propagate to the caller.
    /// </summary>
    public bool TryGetIpaPhonemes(string text, string voice, out string phonemes)
    {
        if (string.IsNullOrEmpty(text))
        {
            phonemes = string.Empty;
            return true;
        }

        IntPtr textPtr = Marshal.StringToCoTaskMemUTF8(text);
        IntPtr currentPtr = textPtr;
        var sb = new System.Text.StringBuilder();

        try
        {
            lock (_espeakLock)
            {
                if (!TrySelectVoiceLocked(voice))
                {
                    phonemes = string.Empty;
                    return false;
                }

                PhonemizeLocked(ref currentPtr, sb);
            }

            phonemes = sb.ToString().Trim();
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(textPtr);
        }
    }

    // Must be called only while _espeakLock is held.
    private static bool TrySelectVoiceLocked(string voice)
    {
        if (string.Equals(_currentNativeVoice, voice, StringComparison.Ordinal))
        {
            return true;
        }

        int voiceResult = espeak_SetVoiceByName(voice);
        if (voiceResult != 0)
        {
            // Do not trust the cached state after a failed native transition. Even though
            // eSpeak normally leaves the previous voice intact, invalidating the cache makes
            // the next successful call explicitly re-establish the requested native state.
            _currentNativeVoice = null;
            Console.WriteLine($"[WARNING] Failed to set espeak voice to '{voice}'.");
            return false;
        }

        _currentNativeVoice = voice;
        return true;
    }

    // Must be called only while _espeakLock is held. eSpeak advances currentPtr internally
    // until the complete UTF-8 input has been consumed.
    private static void PhonemizeLocked(
        ref IntPtr currentPtr,
        System.Text.StringBuilder output)
    {
        while (currentPtr != IntPtr.Zero)
        {
            // 1 = textmode (UTF8), 2 = phonememode (IPA)
            IntPtr resultPtr = espeak_TextToPhonemes(ref currentPtr, 1, 2);

            if (resultPtr != IntPtr.Zero)
            {
                string part = Marshal.PtrToStringUTF8(resultPtr) ?? string.Empty;
                output.Append(part);
            }
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}