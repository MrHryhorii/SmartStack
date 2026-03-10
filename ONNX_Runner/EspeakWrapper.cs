using System.Runtime.InteropServices;

namespace ONNX_Runner.Services;

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

    // Дозволяє динамічно перемикати мову перед транскрипцією
    public void SetVoice(string voice)
    {
        int voiceResult = espeak_SetVoiceByName(voice);
        if (voiceResult != 0)
        {
            Console.WriteLine($"[WARNING] Failed to set espeak voice to '{voice}'.");
            throw new Exception("Voice not found"); // Кидаємо помилку, щоб спрацював наш фолбек
        }
    }

    public string GetIpaPhonemes(string text)
    {
        IntPtr textPtr = Marshal.StringToCoTaskMemUTF8(text);
        IntPtr currentPtr = textPtr;
        var sb = new System.Text.StringBuilder();

        try
        {
            while (currentPtr != IntPtr.Zero)
            {
                IntPtr resultPtr = espeak_TextToPhonemes(ref currentPtr, 1, 2);

                if (resultPtr != IntPtr.Zero)
                {
                    string part = Marshal.PtrToStringUTF8(resultPtr) ?? string.Empty;
                    sb.Append(part);
                }
            }
            return sb.ToString().Trim(); // Просто повертаємо чистий результат
        }
        finally
        {
            Marshal.FreeCoTaskMem(textPtr);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}