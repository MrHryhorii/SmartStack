global using Microsoft.Extensions.Configuration;
using STT_Runner.Services;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

// A local HTTP fixture proves that download sources do not depend on destination filenames.
byte[] whisper = new byte[48];
BinaryPrimitives.WriteInt32LittleEndian(whisper, 0x67676d6c);
BinaryPrimitives.WriteInt32LittleEndian(whisper.AsSpan(4), 51865);
BinaryPrimitives.WriteInt32LittleEndian(whisper.AsSpan(20), 12);
BinaryPrimitives.WriteInt32LittleEndian(whisper.AsSpan(40), 80);
byte[] vad = [1, 2, 3, 4];
using var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
using var listener = new HttpListener();
string origin = $"http://127.0.0.1:{port}";
listener.Prefixes.Add(origin + "/");
listener.Start();
int requests = 0;
Task serving = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync(); }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
        Interlocked.Increment(ref requests);
        byte[] body = context.Request.Url!.AbsolutePath == "/source-whisper" ? whisper : vad;
        if (context.Request.Url.AbsolutePath == "/missing") context.Response.StatusCode = 404;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }
});
string root = Path.Combine(Path.GetTempPath(), "mwandishi-model-checks-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var options = new Dictionary<string, string?>
    {
        ["SttSettings:ModelDirectory"] = root,
        ["SttSettings:WhisperModelName"] = "arbitrary voice.weights",
        ["SttSettings:VadModelName"] = "detector.weights",
        ["AutoDownload:WhisperUrl"] = origin + "/source-whisper?revision=1",
        ["AutoDownload:VadUrl"] = origin + "/source-vad",
        ["AutoDownload:Enable"] = "true"
    };
    IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(options).Build();
    var paths = await ModelManager.EnsureModelsExistAsync(Config());
    Assert(Path.GetFileName(paths.WhisperPath) == "arbitrary voice.weights" && requests == 2);
    Assert(File.ReadAllBytes(paths.WhisperPath).SequenceEqual(whisper));
    Assert(File.ReadAllBytes(paths.VadPath).SequenceEqual(vad));
    Assert(WhisperModelDetails.TryRead(paths.WhisperPath) is { Family: "small", Multilingual: true });
    Pass("separate custom URLs, renamed files, optional digests, header-based family");

    await ModelManager.EnsureModelsExistAsync(Config());
    Assert(requests == 2);
    Pass("existing weights are reused without another download");

    options["AutoDownload:WhisperSha256"] = Convert.ToHexString(SHA256.HashData(whisper));
    options["AutoDownload:VadSha256"] = Convert.ToHexString(SHA256.HashData(vad));
    File.Delete(paths.WhisperPath);
    await ModelManager.EnsureModelsExistAsync(Config());
    Assert(requests == 3);
    Pass("explicit matching checksum accepts downloaded and existing weights");

    File.Delete(paths.WhisperPath);
    options["AutoDownload:WhisperSha256"] = new string('0', 64);
    await Fails<IOException>(() => ModelManager.EnsureModelsExistAsync(Config()));
    Assert(!File.Exists(paths.WhisperPath) && !File.Exists(paths.WhisperPath + ".download"));
    Pass("checksum mismatch leaves no final or partial model");

    options["AutoDownload:WhisperSha256"] = "";
    options["AutoDownload:WhisperUrl"] = origin + "/missing";
    await Fails<IOException>(() => ModelManager.EnsureModelsExistAsync(Config()));
    Assert(!File.Exists(paths.WhisperPath) && !File.Exists(paths.WhisperPath + ".download"));
    Pass("HTTP failure leaves no final or partial model");

    options["AutoDownload:Enable"] = "false";
    int count = requests;
    await Fails<FileNotFoundException>(() => ModelManager.EnsureModelsExistAsync(Config()));
    Assert(requests == count);
    Pass("disabled downloads never access HTTP");

    options["SttSettings:ExactWhisperFilePath"] = paths.VadPath;
    options["AutoDownload:WhisperSha256"] = "invalid unused digest";
    var explicitPaths = await ModelManager.EnsureModelsExistAsync(Config());
    Assert(explicitPaths.WhisperPath == paths.VadPath && requests == count);
    Pass("explicit local paths bypass unused download settings");

    options["SttSettings:ExactWhisperFilePath"] = Path.Combine(root, "not-present");
    await Fails<FileNotFoundException>(() => ModelManager.EnsureModelsExistAsync(Config()));
    Pass("missing explicit paths fail clearly");

    options.Remove("SttSettings:ExactWhisperFilePath");
    options["AutoDownload:WhisperSha256"] = "";
    options["AutoDownload:WhisperUrl"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.bin";
    File.WriteAllBytes(paths.WhisperPath, whisper);
    await Fails<InvalidDataException>(() => ModelManager.EnsureModelsExistAsync(Config()));
    Pass("default URL retains pinned checksum even with a renamed file");
}
finally
{
    listener.Stop();
    await serving;
    Directory.Delete(root, recursive: true);
}

static void Pass(string message) => Console.WriteLine("PASS: " + message);
static void Assert(bool condition)
{
    if (!condition) throw new InvalidOperationException("Model management assertion failed.");
}
static async Task Fails<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
