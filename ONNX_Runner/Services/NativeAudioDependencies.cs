using ONNX_Runner.Models;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ONNX_Runner.Services;

/// <summary>
/// Process-wide native audio capability snapshot.
/// eSpeak is required by the TTS pipeline; LAME is optional and only gates MP3-backed formats.
/// </summary>
public sealed class NativeAudioDependencies
{
    private NativeAudioDependencies(
        bool espeakAvailable,
        bool mp3Available,
        string? espeakLoadedFrom,
        string? lameLoadedFrom)
    {
        EspeakAvailable = espeakAvailable;
        Mp3Available = mp3Available;
        EspeakLoadedFrom = espeakLoadedFrom;
        LameLoadedFrom = lameLoadedFrom;
    }

    public bool EspeakAvailable { get; }

    public bool Mp3Available { get; }

    public string? EspeakLoadedFrom { get; }

    public string? LameLoadedFrom { get; }

    public static NativeAudioDependencies Detect()
    {
        // Detect native capabilities once at startup. Request handling only reads this snapshot.
        NativeLibraryResolver.Initialize();

        bool espeakAvailable =
            NativeLibraryResolver.TryLoadEspeak(
                out string? espeakLoadedFrom);

        bool mp3Available =
            NativeLibraryResolver.TryLoadLame(
                out string? lameLoadedFrom);

        return new NativeAudioDependencies(
            espeakAvailable,
            mp3Available,
            espeakLoadedFrom,
            lameLoadedFrom);
    }

    public bool IsFormatAvailable(
        AudioFormat format)
    {
        // Formats backed entirely by managed code remain available when LAME is missing.
        return !RequiresLame(format) ||
               Mp3Available;
    }

    public static bool RequiresLame(
        AudioFormat format)
    {
        return format is
            AudioFormat.Mp3 or
            AudioFormat.B64Json;
    }
}

/// <summary>
/// Central native-library loader.
/// Linux/macOS locations are delegated to the platform dynamic loader by SONAME.
/// </summary>
internal static class NativeLibraryResolver
{
    internal const string EspeakImportName =
        "espeak-ng";

    private const string LameOverrideVariable =
        "TSUBAKI_LAME_LIBRARY";

    private const string EspeakOverrideVariable =
        "TSUBAKI_ESPEAK_LIBRARY";

    // A library is considered usable only when every symbol used by the MP3 path exists.
    private static readonly string[] RequiredLameExports =
    [
        "lame_init",
        "lame_set_in_samplerate",
        "lame_set_num_channels",
        "lame_set_mode",
        "lame_set_brate",
        "lame_set_bWriteVbrTag",
        "lame_init_params",
        "lame_encode_buffer",
        "lame_encode_flush",
        "lame_close"
    ];

    // Validate the complete eSpeak surface used by EspeakWrapper before TTS initialization.
    private static readonly string[] RequiredEspeakExports =
    [
        "espeak_Initialize",
        "espeak_SetVoiceByName",
        "espeak_TextToPhonemes"
    ];

    // Native loading and resolver registration are process-wide and must be serialized.
    private static readonly object Sync =
        new();

    private static bool _initialized;

    // Successful handles stay loaded for the process lifetime because function pointers
    // and eSpeak P/Invoke calls depend on them remaining valid.
    private static IntPtr _lameHandle;
    private static string? _lameLoadedFrom;

    private static IntPtr _espeakHandle;
    private static string? _espeakLoadedFrom;

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            // One resolver is allowed per assembly. Only eSpeak still enters through P/Invoke;
            // LAME uses exports resolved directly from its cached native handle.
            NativeLibrary.SetDllImportResolver(
                typeof(NativeLibraryResolver).Assembly,
                ResolveImport);

            _initialized = true;
        }
    }

    public static bool TryLoadLame(
        out string? loadedFrom)
    {
        // Reuse the first validated handle; do not probe the filesystem on every MP3 request.
        Initialize();

        lock (Sync)
        {
            if (_lameHandle != IntPtr.Zero)
            {
                loadedFrom =
                    _lameLoadedFrom;

                return true;
            }

            bool loaded =
                TryLoadFirst(
                    GetLameCandidates(),
                    RequiredLameExports,
                    out _lameHandle,
                    out _lameLoadedFrom);

            loadedFrom =
                _lameLoadedFrom;

            return loaded;
        }
    }

    public static bool TryLoadEspeak(
        out string? loadedFrom)
    {
        // Reuse the first validated handle; eSpeak is shared by the phonemizer.
        Initialize();

        lock (Sync)
        {
            if (_espeakHandle != IntPtr.Zero)
            {
                loadedFrom =
                    _espeakLoadedFrom;

                return true;
            }

            bool loaded =
                TryLoadFirst(
                    GetEspeakCandidates(),
                    RequiredEspeakExports,
                    out _espeakHandle,
                    out _espeakLoadedFrom);

            loadedFrom =
                _espeakLoadedFrom;

            return loaded;
        }
    }

    public static IntPtr GetLameExport(
        string exportName)
    {
        // LameNative binds function pointers from the already validated process-wide handle.
        if (
            !TryLoadLame(out _)
        )
        {
            throw new PlatformNotSupportedException(
                "The native LAME MP3 library is not available on this system.");
        }

        return NativeLibrary.GetExport(
            _lameHandle,
            exportName);
    }

    private static IntPtr ResolveImport(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        // Return a cached eSpeak handle only for names used by this assembly.
        // IntPtr.Zero delegates every unrelated import back to the default .NET loader.
        if (
            IsEspeakImportName(
                libraryName)
        )
        {
            return TryLoadEspeak(out _)
                ? _espeakHandle
                : IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private static bool IsEspeakImportName(
        string libraryName)
    {
        return libraryName is
            EspeakImportName or
            "espeak-ng.dll" or
            "libespeak-ng.so" or
            "libespeak-ng.so.1" or
            "libespeak-ng.dylib" or
            "libespeak-ng.1.dylib";
    }

    private static IEnumerable<string>
        GetLameCandidates()
    {
        // Search order: explicit override -> app-local file -> platform loader by library name.
        string? explicitLibrary =
            Environment.GetEnvironmentVariable(
                LameOverrideVariable);

        if (
            !string.IsNullOrWhiteSpace(
                explicitLibrary)
        )
        {
            yield return explicitLibrary;
        }

        foreach (
            string candidate in
            GetAppLocalLameCandidates()
        )
        {
            yield return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            // Let ld.so resolve distro-specific directories instead of hard-coding /usr/lib paths.
            yield return "libmp3lame.so.0";
            yield return "libmp3lame.so";
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "libmp3lame.0.dylib";
            yield return "libmp3lame.dylib";
        }
    }

    private static IEnumerable<string>
        GetEspeakCandidates()
    {
        // Search order mirrors LAME so custom deployments can override either native dependency.
        string? explicitLibrary =
            Environment.GetEnvironmentVariable(
                EspeakOverrideVariable);

        if (
            !string.IsNullOrWhiteSpace(
                explicitLibrary)
        )
        {
            yield return explicitLibrary;
        }

        foreach (
            string candidate in
            GetAppLocalEspeakCandidates()
        )
        {
            yield return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            // Prefer the runtime SONAME; the unversioned name is commonly supplied by dev packages.
            yield return "libespeak-ng.so.1";
            yield return "libespeak-ng.so";
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "libespeak-ng.1.dylib";
            yield return "libespeak-ng.dylib";
        }
    }

    private static IEnumerable<string>
        GetAppLocalLameCandidates()
    {
        string baseDirectory =
            AppContext.BaseDirectory;

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(
                baseDirectory,
                Environment.Is64BitProcess
                    ? "libmp3lame.64.dll"
                    : "libmp3lame.32.dll");

            yield return Path.Combine(
                baseDirectory,
                "libmp3lame.dll");

            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(
                baseDirectory,
                "libmp3lame.so.0");

            yield return Path.Combine(
                baseDirectory,
                "libmp3lame.so");

            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(
                baseDirectory,
                "libmp3lame.dylib");
        }
    }

    private static IEnumerable<string>
        GetAppLocalEspeakCandidates()
    {
        string baseDirectory =
            AppContext.BaseDirectory;

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(
                baseDirectory,
                "PiperNative",
                "espeak-ng.dll");

            yield return Path.Combine(
                baseDirectory,
                "espeak-ng.dll");

            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(
                baseDirectory,
                "libespeak-ng.so.1");

            yield return Path.Combine(
                baseDirectory,
                "libespeak-ng.so");

            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(
                baseDirectory,
                "libespeak-ng.dylib");
        }
    }

    private static bool TryLoadFirst(
        IEnumerable<string> candidates,
        IReadOnlyList<string> requiredExports,
        out IntPtr handle,
        out string? loadedFrom)
    {
        // Stop at the first library that loads and exposes the complete required ABI.
        foreach (
            string candidate in
            candidates.Distinct(
                StringComparer.Ordinal)
        )
        {
            if (
                !TryLoadCandidate(
                    candidate,
                    requiredExports,
                    out handle)
            )
            {
                continue;
            }

            loadedFrom =
                candidate;

            return true;
        }

        handle =
            IntPtr.Zero;

        loadedFrom =
            null;

        return false;
    }

    private static bool TryLoadCandidate(
        string candidate,
        IReadOnlyList<string> requiredExports,
        out IntPtr handle)
    {
        handle =
            IntPtr.Zero;

        try
        {
            // Avoid loader exceptions for explicit/app-local paths that do not exist.
            if (
                LooksLikePath(candidate) &&
                !File.Exists(candidate)
            )
            {
                return false;
            }

            if (
                !NativeLibrary.TryLoad(
                    candidate,
                    out handle)
            )
            {
                return false;
            }

            // Loading alone is insufficient: reject wrong/incompatible library versions
            // before the capability is advertised to the rest of the server.
            foreach (
                string requiredExport in
                requiredExports)
            {
                if (
                    NativeLibrary.TryGetExport(
                        handle,
                        requiredExport,
                        out _)
                )
                {
                    continue;
                }

                NativeLibrary.Free(
                    handle);

                handle =
                    IntPtr.Zero;

                return false;
            }

            // Keep successful handles loaded. They are intentionally not freed until process exit.
            return true;
        }
        catch (
            Exception ex
        ) when (
            ex is DllNotFoundException or
            BadImageFormatException or
            FileLoadException)
        {
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(
                    handle);

                handle =
                    IntPtr.Zero;
            }

            return false;
        }
    }

    private static bool LooksLikePath(
        string value)
    {
        return Path.IsPathRooted(
                   value) ||
               value.Contains(
                   Path.DirectorySeparatorChar) ||
               value.Contains(
                   Path.AltDirectorySeparatorChar);
    }
}
