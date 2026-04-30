using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Whisper.net;
using System.Text;

namespace STT_Runner.Services;

/// <summary>
/// The core engine responsible for analyzing audio.
/// It uses Silero VAD to detect speech segments and Whisper.net to transcribe them.
/// </summary>
public class Transcriptor(IConfiguration config, string whisperPath, string vadPath) : IDisposable
{
    private readonly string _whisperPath = whisperPath;
    private readonly string _vadPath = vadPath;
    private readonly string _language = config["SttSettings:Language"] ?? "uk";

    // Whisper components
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _whisperProcessor;

    // ONNX components for VAD
    private InferenceSession? _vadSession;

    // Silero VAD requires a specific sample rate and window size
    private const int SampleRate = 16000;
    private const int WindowSize = 512;
    private const float SpeechThreshold = 0.5f;

    /// <summary>
    /// Initializes models into memory. Must be called before processing audio.
    /// </summary>
    public void Initialize()
    {
        Console.WriteLine("[SYSTEM] Initializing Transcriptor Engine...");

        // Initialize Whisper
        _whisperFactory = WhisperFactory.FromPath(_whisperPath);
        _whisperProcessor = _whisperFactory.CreateBuilder()
            .WithLanguage(_language)
            .WithProbabilities() // Enables confidence scores
            .Build();

        // Initialize Silero VAD (ONNX)
        // We use CPU for VAD because it is extremely lightweight and transferring 
        // tiny 512-sample chunks to the GPU actually slows it down due to bus latency.
        var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
        _vadSession = new InferenceSession(_vadPath, sessionOptions);

        Console.WriteLine("[SYSTEM] Transcriptor initialized successfully.");
    }

    /// <summary>
    /// Processes the full audio array, filters silence using VAD, and returns transcribed text.
    /// </summary>
    public async Task<string> TranscribeAsync(float[] audioSamples)
    {
        if (_whisperProcessor == null || _vadSession == null)
            throw new InvalidOperationException("Transcriptor is not initialized.");

        // We will store the extracted active speech here
        var activeSpeechBuffer = new List<float>();

        // Silero VAD internal state tensors (h and c). 
        // They must be maintained across chunks for continuous context.
        var hTensor = new DenseTensor<float>(new[] { 2, 1, 64 });
        var cTensor = new DenseTensor<float>(new[] { 2, 1, 64 });
        hTensor.Fill(0f);
        cTensor.Fill(0f);

        var srTensor = new DenseTensor<long>(new long[] { SampleRate }, new[] { 1 });

        // Iterate through the audio in chunks of 512 samples
        for (int i = 0; i < audioSamples.Length - WindowSize; i += WindowSize)
        {
            var chunk = new float[WindowSize];
            Array.Copy(audioSamples, i, chunk, 0, WindowSize);

            // Create input tensor for the current chunk
            var inputTensor = new DenseTensor<float>(chunk, new[] { 1, WindowSize });

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
                NamedOnnxValue.CreateFromTensor("sr", srTensor),
                NamedOnnxValue.CreateFromTensor("h", hTensor),
                NamedOnnxValue.CreateFromTensor("c", cTensor)
            };

            // Run VAD Inference
            using var results = _vadSession.Run(inputs);

            // Extract the speech probability
            var probTensor = results.First(v => v.Name == "output").AsTensor<float>();
            float probability = probTensor.GetValue(0);

            // Update internal states for the next iteration
            var nextH = (DenseTensor<float>)results.First(v => v.Name == "hn").AsTensor<float>();
            var nextC = (DenseTensor<float>)results.First(v => v.Name == "cn").AsTensor<float>();

            nextH.Buffer.Span.CopyTo(hTensor.Buffer.Span);
            nextC.Buffer.Span.CopyTo(cTensor.Buffer.Span);

            // If probability is above threshold, save the chunk
            if (probability >= SpeechThreshold)
            {
                activeSpeechBuffer.AddRange(chunk);
            }
        }

        // If no speech was detected in the entire file
        if (activeSpeechBuffer.Count == 0)
        {
            return string.Empty;
        }

        // Send the filtered speech buffer to Whisper
        var sb = new StringBuilder();

        // ProcessAsync expects an IAsyncEnumerable or array
        await foreach (var segment in _whisperProcessor.ProcessAsync(activeSpeechBuffer.ToArray()))
        {
            // We can also check segment.Probability here to filter out hallucinations
            sb.Append(segment.Text);
        }

        return sb.ToString().Trim();
    }

    // Dispose pattern to clean up ONNX sessions and Whisper resources
    public void Dispose()
    {
        _whisperProcessor?.Dispose();
        _whisperFactory?.Dispose();
        _vadSession?.Dispose();
        GC.SuppressFinalize(this);
    }
}