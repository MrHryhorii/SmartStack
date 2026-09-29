using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Buffers;
using System.Threading.Channels;

namespace STT_Runner.Services;

public sealed record VadSegmentationOptions(int SilenceDurationMs, int PrefixPaddingMs, float Threshold);

/// <summary>
/// Uses a shared Silero ONNX session to find speech boundaries in 512-sample
/// frames. Each request keeps separate recurrent state and audio buffers.
/// Incoming pooled frames are returned here; completed segment owners are
/// transferred to the Whisper channel.
/// </summary>
public sealed class VadProcessor : IDisposable
{
    private const int SampleRate = 16_000;
    private const int WindowSize = 512; // Must match AudioProcessor's output frame.
    private const float DefaultSpeechThreshold = 0.5f;

    /// <summary>Keep up to 96 ms of trailing silence at a VAD boundary.</summary>
    private const int TailSilenceChunks = 3;

    private readonly int _maxSegmentSamples;
    private readonly int _maxBufferedSamples;
    private readonly int _defaultSilenceChunks;

    /// <summary>Space for silence held until the next speech or VAD boundary.</summary>
    // Preserve 384 ms before speech so quiet onsets survive VAD boundaries.
    private const int DefaultPreRollSamples = 12 * WindowSize;
    private const int SplitOverlapSamples = 4 * WindowSize;

    private readonly InferenceSession _vadSession;

    public VadProcessor(string vadModelPath, IConfiguration config)
    {
        int maxSegmentSeconds = config.GetValue<int>("SttSettings:MaxSegmentSeconds", 0);
        int maxBufferedSeconds = config.GetValue<int>("SttSettings:MaxBufferedSegmentSeconds", 600);
        int pauseMs = config.GetValue<int>("SttSettings:VadPauseMs", 800);
        if (maxSegmentSeconds < 0 || maxBufferedSeconds < 1 || maxBufferedSeconds > 3600 ||
            maxSegmentSeconds > maxBufferedSeconds)
            throw new ArgumentOutOfRangeException(nameof(config),
                "MaxSegmentSeconds must be 0 or positive and no greater than MaxBufferedSegmentSeconds (1–3600).");
        if (pauseMs is < 32 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(config), "VadPauseMs must be between 32 and 5000.");

        // Zero disables forced splitting; the buffered limit still caps memory use.
        _maxSegmentSamples = checked(maxSegmentSeconds * SampleRate);
        _maxBufferedSamples = checked(maxBufferedSeconds * SampleRate);
        _defaultSilenceChunks = (pauseMs + 31) / 32;
        _vadSession = new InferenceSession(vadModelPath, new Microsoft.ML.OnnxRuntime.SessionOptions());
    }

    /// <summary>
    /// Reads frames from <paramref name="inputChannel"/> and writes speech
    /// timestamped owned speech segments to
    /// <paramref name="outputChannel"/>.
    /// Completes <paramref name="outputChannel"/> at input EOF or on failure.
    /// </summary>
    public async Task<long> ProcessVadChannelAsync(
        ChannelReader<float[]> inputChannel,
        ChannelWriter<AudioPiece> outputChannel,
        VadSegmentationOptions? options = null,
        CancellationToken ct = default)
    {
        int silenceChunksLimit = options is null
            ? _defaultSilenceChunks
            : (options.SilenceDurationMs + 31) / 32;
        int preRollSamples = options is null
            ? DefaultPreRollSamples
            : (options.PrefixPaddingMs + 31) / 32 * WindowSize;
        float speechThreshold = options?.Threshold ?? DefaultSpeechThreshold;

        // Recurrent VAD state starts at zero for every request and persists per frame.
        var stateTensor = new DenseTensor<float>([2, 1, 128]);
        stateTensor.Fill(0f);
        var srTensor = new DenseTensor<long>(new long[] { SampleRate }, [1]);
        var inputTensor = new DenseTensor<float>([1, WindowSize]);

        var onnxInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("sr",    srTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
        };

        // Rent growing speech storage while keeping silence and pre-roll separate.
        float[] sentenceBuffer = ArrayPool<float>.Shared.Rent(Math.Min(_maxBufferedSamples, SampleRate * 2));
        float[] silenceBuffer = ArrayPool<float>.Shared.Rent(silenceChunksLimit * WindowSize);
        float[] preRoll = preRollSamples == 0
            ? Array.Empty<float>()
            : ArrayPool<float>.Shared.Rent(preRollSamples);
        int preRollLength = 0;
        int sentenceLength = 0;
        int silenceLength = 0;
        int silenceChunks = 0;
        bool isSpeaking = false;
        long samplesRead = 0;
        long sentenceStart = 0;

        Exception? failure = null;
        try
        {
            await foreach (float[] frame in inputChannel.ReadAllAsync(ct))
            {
                // Release each PCM frame after copying the samples needed downstream.
                try
                {
                    samplesRead += WindowSize;
                    frame.AsSpan(0, WindowSize).CopyTo(inputTensor.Buffer.Span);

                    // The ONNX session is shared, while these input tensors are local.
                    using var results = _vadSession.Run(onnxInputs);

                    float probability = results
                        .First(v => v.Name == "output")
                        .AsTensor<float>()
                        .GetValue(0);

                    // Feed Silero's returned state into the next 32 ms window.
                    var nextState = (DenseTensor<float>)results.First(v => v.Name == "stateN").Value;

                    nextState.Buffer.Span.CopyTo(stateTensor.Buffer.Span);

                    if (probability >= speechThreshold)
                    {
                        // Retain short pauses inside speech; long pauses flush below.
                        if (silenceLength > 0)
                        {
                            // Emit before buffered silence would exceed a forced split.
                            if (_maxSegmentSamples > 0 && sentenceLength + silenceLength + WindowSize > _maxSegmentSamples)
                            {
                                await FlushSentenceAsync(sentenceBuffer, sentenceLength, silenceBuffer, 0, sentenceStart, outputChannel, ct);
                                int overlap = Math.Min(SplitOverlapSamples, sentenceLength);
                                sentenceBuffer.AsSpan(sentenceLength - overlap, overlap).CopyTo(sentenceBuffer);
                                sentenceStart += sentenceLength - overlap;
                                sentenceLength = overlap;
                            }
                            EnsureSentenceCapacity(ref sentenceBuffer, sentenceLength, sentenceLength + silenceLength + WindowSize);
                            silenceBuffer.AsSpan(0, silenceLength)
                                         .CopyTo(sentenceBuffer.AsSpan(sentenceLength));
                            sentenceLength += silenceLength;
                            silenceLength = 0;
                        }

                        if (!isSpeaking)
                        {
                            sentenceStart = samplesRead - WindowSize - preRollLength;
                            // Include audio immediately preceding the VAD trigger.
                            if (preRollLength > 0)
                            {
                                EnsureSentenceCapacity(ref sentenceBuffer, sentenceLength, preRollLength);
                                preRoll.AsSpan(0, preRollLength).CopyTo(sentenceBuffer);
                                sentenceLength = preRollLength;
                                preRollLength = 0;
                            }
                        }

                        if (_maxSegmentSamples > 0 && sentenceLength + WindowSize > _maxSegmentSamples)
                        {
                            // A short overlap helps preserve phonemes at forced splits.
                            await FlushSentenceAsync(sentenceBuffer, sentenceLength, silenceBuffer, 0, sentenceStart, outputChannel, ct);
                            int overlap = Math.Min(SplitOverlapSamples, sentenceLength);
                            sentenceBuffer.AsSpan(sentenceLength - overlap, overlap).CopyTo(sentenceBuffer);
                            sentenceStart += sentenceLength - overlap;
                            sentenceLength = overlap;
                        }

                        EnsureSentenceCapacity(ref sentenceBuffer, sentenceLength, sentenceLength + WindowSize);
                        frame.AsSpan(0, WindowSize).CopyTo(sentenceBuffer.AsSpan(sentenceLength));
                        sentenceLength += WindowSize;

                        silenceChunks = 0;
                        isSpeaking = true;
                    }
                    else if (isSpeaking)
                    {
                        // Delay the boundary until the pause reaches the VAD threshold.
                        frame.AsSpan(0, WindowSize).CopyTo(silenceBuffer.AsSpan(silenceLength));
                        silenceLength += WindowSize;
                        silenceChunks++;
                    }
                    else if (preRollSamples > 0)
                    {
                        // Keep only the most recent silence for the next pre-roll.
                        int shift = Math.Max(0, preRollLength + WindowSize - preRollSamples);
                        if (shift > 0)
                        {
                            preRoll.AsSpan(shift, preRollLength - shift).CopyTo(preRoll);
                            preRollLength -= shift;
                        }
                        frame.AsSpan(0, WindowSize).CopyTo(preRoll.AsSpan(preRollLength));
                        preRollLength += WindowSize;
                    }

                    // Emit after the configured pause and retain recent silence.
                    if (isSpeaking && silenceChunks >= silenceChunksLimit)
                    {
                        await FlushSentenceAsync(
                            sentenceBuffer, sentenceLength,
                            silenceBuffer, silenceLength,
                            sentenceStart, outputChannel, ct);

                        preRollLength = Math.Min(preRollSamples, silenceLength);
                        if (preRollLength > 0)
                            silenceBuffer.AsSpan(silenceLength - preRollLength, preRollLength).CopyTo(preRoll);
                        sentenceLength = 0;
                        silenceLength = 0;
                        silenceChunks = 0;
                        isSpeaking = false;
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(frame);
                }
            }

            // EOF also ends a speech segment when no long pause was observed.
            if (sentenceLength > 0)
            {
                await FlushSentenceAsync(
                    sentenceBuffer, sentenceLength,
                    silenceBuffer, silenceLength,
                    sentenceStart, outputChannel, ct);
            }
            return samplesRead;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(sentenceBuffer);
            ArrayPool<float>.Shared.Return(silenceBuffer);
            if (preRollSamples > 0) ArrayPool<float>.Shared.Return(preRoll);

            // Propagate the VAD failure to the Whisper channel reader.
            outputChannel.TryComplete(failure);
        }
    }

    private void EnsureSentenceCapacity(ref float[] buffer, int used, int required)
    {
        // Avoid unbounded growth when forced splitting is disabled.
        if (required > _maxBufferedSamples)
            throw new InvalidDataException("Speech segment exceeds SttSettings:MaxBufferedSegmentSeconds.");
        if (required <= buffer.Length) return;

        int capacity = (int)Math.Min(_maxBufferedSamples, Math.Max((long)buffer.Length * 2, required));
        float[] larger = ArrayPool<float>.Shared.Rent(capacity);
        buffer.AsSpan(0, used).CopyTo(larger);
        ArrayPool<float>.Shared.Return(buffer);
        buffer = larger;
    }

    /// <summary>
    /// Copies the completed segment and optional silence tail into an owned
    /// MemoryPool rental. Ownership passes to the channel after WriteAsync.
    /// </summary>
    private static async ValueTask FlushSentenceAsync(
        float[] sentenceBuffer, int sentenceLength,
        float[] silenceBuffer, int silenceLength,
        long startSample, ChannelWriter<AudioPiece> channel,
        CancellationToken ct)
    {
        // Tail silence is included only when a pause was observed.
        int tailSamples = Math.Min(TailSilenceChunks * WindowSize, silenceLength);
        int totalLength = sentenceLength + tailSamples;

        IMemoryOwner<float> output = MemoryPool<float>.Shared.Rent(totalLength);
        sentenceBuffer.AsSpan(0, sentenceLength).CopyTo(output.Memory.Span);
        silenceBuffer.AsSpan(0, tailSamples).CopyTo(output.Memory.Span[sentenceLength..]);

        try
        {
            await channel.WriteAsync(new AudioPiece(output, totalLength, startSample), ct);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Executes one silent inference pass before listening for HTTP requests.
    /// Warm-up state is discarded so each upload still starts from zero state.
    /// </summary>
    public void WarmUp()
    {
        Console.WriteLine("[SYSTEM] Warming up VAD...");

        var state = new DenseTensor<float>([2, 1, 128]);
        state.Fill(0f);
        var sr = new DenseTensor<long>(new long[] { SampleRate }, [1]);
        var input = new DenseTensor<float>([1, WindowSize]);
        input.Fill(0f);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", input),
            NamedOnnxValue.CreateFromTensor("sr",    sr),
            NamedOnnxValue.CreateFromTensor("state", state),
        };

        using var _ = _vadSession.Run(inputs);

        Console.WriteLine("[SYSTEM] VAD warm-up complete.");
    }

    public void Dispose()
    {
        _vadSession.Dispose();
        GC.SuppressFinalize(this);
    }
}
