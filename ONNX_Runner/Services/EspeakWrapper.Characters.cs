using System.Runtime.InteropServices;

namespace ONNX_Runner.Services;

public partial class EspeakWrapper
{
    // Symbol descriptions and literal character names use the configured Piper voice.
    // eSpeak is process-global; callbacks and tracing are installed only under _espeakLock.
    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    private static extern int espeak_Char(int character);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativePhonemeCallback(IntPtr utf8Phonemes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeSynthCallback(IntPtr samples, int count, IntPtr events);

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    private static extern void espeak_SetPhonemeCallback(NativePhonemeCallback? callback);

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    private static extern void espeak_SetSynthCallback(NativeSynthCallback? callback);

    [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
    private static extern void espeak_SetPhonemeTrace(int phonemeMode, IntPtr stream);

    // The native DLL is UCRT-linked on Windows. FILE* must come from the same CRT.
    [DllImport("ucrtbase.dll", EntryPoint = "fopen", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr WindowsFopen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mode);

    [DllImport("libc", EntryPoint = "fopen", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr LinuxFopen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mode);

    [DllImport("libSystem.B.dylib", EntryPoint = "fopen", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr MacFopen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mode);

    private static readonly NativePhonemeCallback _phonemeCallback = CaptureNativePhonemes;
    private static readonly NativeSynthCallback _synthCallback = static (_, _, _) => 0;
    private static System.Text.StringBuilder? _capturedPhonemes;
    private static IntPtr _traceSink;

    /// <summary>
    /// Returns a spoken symbol description or literal character name as IPA.
    /// Unicode symbols prefer ordinary translation, which preserves multiword dictionary names.
    /// Returns false only when the configured eSpeak voice cannot be selected.
    /// </summary>
    public bool TryGetCharacterPhonemes(char character, string voice, out string phonemes)
    {
        lock (_espeakLock)
        {
            if (!TrySelectVoiceLocked(voice))
            {
                phonemes = string.Empty;
                return false;
            }

            // Normalize only compatible fullwidth punctuation/symbols, never the source text.
            if (character is >= '\uFF01' and <= '\uFF5E' &&
                (char.IsPunctuation(character) || char.IsSymbol(character)))
            {
                character = (char)(character - 0xFEE0);
            }

            bool preferText = char.IsSymbol(character);
            string? symbolText = preferText ? character.ToString() : null;
            if (preferText)
            {
                phonemes = RemoveCharacterLanguageLabels(GetIpaPhonemes(symbolText!));
                if (!string.IsNullOrWhiteSpace(phonemes))
                {
                    return true;
                }
            }

            phonemes = GetNativeCharacterPhonemesLocked(character, voice);

            // Keep working localized names. Only follow English when the native character
            // path already selected it, avoiding truncated/repeated $textmode descriptions.
            if (preferText && phonemes.Contains("(en)", StringComparison.Ordinal) &&
                TryGetEnglishSymbolDescriptionLocked(symbolText!, voice, out string description))
            {
                phonemes = description;
            }

            phonemes = RemoveCharacterLanguageLabels(phonemes);
            if (phonemes.Length == 0)
                throw new InvalidOperationException($"eSpeak returned no spoken name for U+{(int)character:X4} with voice '{voice}'.");
            return true;
        }
    }

    // The caller holds _espeakLock and has already selected the requested voice.
    private static string GetNativeCharacterPhonemesLocked(char character, string voice)
    {
        if (_capturedPhonemes != null)
        {
            throw new InvalidOperationException("Nested eSpeak character transcription is not supported.");
        }

        _capturedPhonemes = new System.Text.StringBuilder(32);
        try
        {
            // Without a native FILE*, the trace also prints to stdout.
            // Reuse one write-only null-device stream for the process lifetime.
            espeak_SetSynthCallback(_synthCallback);
            espeak_SetPhonemeCallback(_phonemeCallback);
            espeak_SetPhonemeTrace(2, GetTraceSinkLocked());

            int status = espeak_Char(character);
            if (status != 0)
            {
                throw new InvalidOperationException(
                    $"eSpeak could not read character U+{(int)character:X4} with voice '{voice}': {status}.");
            }

            string phonemes = _capturedPhonemes.ToString().Trim();
            if (phonemes.Length == 0)
            {
                throw new InvalidOperationException(
                    $"eSpeak returned no IPA for character U+{(int)character:X4} with voice '{voice}'.");
            }

            return phonemes;
        }
        finally
        {
            espeak_SetPhonemeTrace(0, IntPtr.Zero);
            espeak_SetPhonemeCallback(null);
            espeak_SetSynthCallback(null);
            _capturedPhonemes = null;
        }
    }

    // English is a native dictionary fallback, not a replacement for valid local names.
    private bool TryGetEnglishSymbolDescriptionLocked(string text, string requestedVoice, out string phonemes)
    {
        phonemes = string.Empty;
        try
        {
            if (!TrySelectVoiceLocked("en")) return false;
            phonemes = RemoveCharacterLanguageLabels(GetIpaPhonemes(text));
            return !string.IsNullOrWhiteSpace(phonemes);
        }
        finally
        {
            if (!TrySelectVoiceLocked(requestedVoice))
                throw new InvalidOperationException($"Unable to restore eSpeak voice '{requestedVoice}' after symbol fallback.");
        }
    }

    // Parenthesized language switches are native metadata, not Piper phonemes.
    private static string RemoveCharacterLanguageLabels(string phonemes)
    {
        int opening = phonemes.IndexOf('(');
        if (opening < 0) return phonemes;

        var output = new System.Text.StringBuilder(phonemes.Length);
        int start = 0;
        while (opening >= 0)
        {
            int closing = phonemes.IndexOf(')', opening + 1);
            if (closing < 0) break;
            output.Append(phonemes.AsSpan(start, opening - start));
            start = closing + 1;
            opening = phonemes.IndexOf('(', start);
        }
        output.Append(phonemes.AsSpan(start));
        return output.ToString().Trim();
    }

    // Executed synchronously by espeak_Char while _espeakLock is held.
    private static int CaptureNativePhonemes(IntPtr utf8Phonemes)
    {
        if (_capturedPhonemes == null || utf8Phonemes == IntPtr.Zero)
        {
            return 0;
        }

        string? part = Marshal.PtrToStringUTF8(utf8Phonemes);
        if (!string.IsNullOrWhiteSpace(part))
        {
            EspeakPhonemePartJoiner.Append(_capturedPhonemes, part.Trim());
        }

        return 0;
    }

    // A process-global FILE* must remain valid across native trace operations.
    // The operating system closes this single handle at process exit.
    private static IntPtr GetTraceSinkLocked()
    {
        if (_traceSink != IntPtr.Zero)
        {
            return _traceSink;
        }

        _traceSink = OperatingSystem.IsWindows()
            ? WindowsFopen("NUL", "w")
            : OperatingSystem.IsMacOS()
                ? MacFopen("/dev/null", "w")
                : LinuxFopen("/dev/null", "w");

        if (_traceSink == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to open a native trace sink for eSpeak.");
        }

        return _traceSink;
    }

}
