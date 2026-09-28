using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Buffers;
using System.Threading.Channels;

namespace STT_Runner.Services;

/// <summary>
/// Task 2: VAD-Driven Sentence Segmentation.
///
/// Consumes 512-sample audio chunks from Channel 1 (produced by AudioProcessor),
/// runs each chunk through the Silero VAD ONNX model to detect speech, accumulates
/// speech regions into complete sentences, and writes each finished sentence to
/// Channel 2 for Whisper to transcribe.
///
/// A sentence is considered complete after <see cref="MaxSilenceChunks"/> consecutive
/// silent frames (≈ 800 ms) following a speech region.
///
/// Allocation strategy
/// ───────────────────
/// • inputTensor / stateTensor / srTensor — allocated once, reused every chunk.
/// • sentenceBuffer / silenceBuffer — rented from ArrayPool at the start of the
///   processing loop and returned in the finally block.  No per-chunk heap allocs.
/// • Only one MemoryPool rental per completed sentence (the output payload).
///
/// Ownership rules
/// ───────────────
/// • This class disposes every IMemoryOwner<float> received from the input channel.
/// • It transfers ownership of each output IMemoryOwner<float> to the Whisper consumer,
///   which is responsible for disposing it after use.
/// </summary>
public sealed class VadProcessor : IDisposable
{
    // ── Model constants ──────────────────────────────────────────────────────
    private const int SampleRate = 16_000;
    private const int WindowSize = 512;           // Must match AudioProcessor.VadChunkSize
    private const float SpeechThreshold = 0.5f;   // Silero output ≥ this → speech

    // ── Sentence-boundary timing ─────────────────────────────────────────────
    /// <summary>25 silent chunks × 32 ms = 800 ms → sentence boundary.</summary>
    private const int MaxSilenceChunks = 25;

    /// <summary>
    /// Silent frames appended to the end of each sentence for a natural cutoff.
    /// 3 chunks ≈ 96 ms — enough for Whisper to decode the final phoneme cleanly.
    /// </summary>
    private const int TailSilenceChunks = 3;

    private readonly int _maxSegmentSamples;
    private readonly int _maxBufferedSamples;

    /// <summary>Worst-case silence accumulation before a boundary is triggered.</summary>
    private const int SilenceBufferSamples = MaxSilenceChunks * WindowSize;
    private const int PreRollSamples = 6 * WindowSize;
    private const int SplitOverlapSamples = 4 * WindowSize;

    private readonly InferenceSession _vadSession;

    public VadProcessor(string vadModelPath, IConfiguration config)
    {
        int maxSegmentSeconds = config.GetValue<int>("SttSettings:MaxSegmentSeconds", 0);
        int maxBufferedSeconds = config.GetValue<int>("SttSettings:MaxBufferedSegmentSeconds", 600);
        if (maxSegmentSeconds < 0 || maxBufferedSeconds < 1 || maxBufferedSeconds > 3600 ||
            maxSegmentSeconds > maxBufferedSeconds)
            throw new ArgumentOutOfRangeException(nameof(config),
                "MaxSegmentSeconds must be 0 or positive and no greater than MaxBufferedSegmentSeconds (1–3600).");

        _maxSegmentSamples = checked(maxSegmentSeconds * SampleRate);
        _maxBufferedSamples = checked(maxBufferedSeconds * SampleRate);
        _vadSession = new InferenceSession(vadModelPath, new Microsoft.ML.OnnxRuntime.SessionOptions());
    }

    /// <summary>
    /// Reads chunks from <paramref name="inputChannel"/>, segments them into sentences
    /// using the Silero VAD model, and writes each sentence as an
    /// <c>(IMemoryOwner&lt;float&gt; Owner, int Length)</c> tuple to
    /// <paramref name="outputChannel"/>.
    /// Completes <paramref name="outputChannel"/> when the input channel is exhausted.
    /// </summary>
    public async Task ProcessVadChannelAsync(
        ChannelReader<IMemoryOwner<float>> inputChannel,
        ChannelWriter<(IMemoryOwner<float> Owner, int Length)> outputChannel,
        CancellationToken ct = default)
    {
        // ── ONNX tensors — allocated once, mutated in-place every iteration ──
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

        // ── Pre-allocated sentence + silence buffers (zero per-chunk allocs) ─
        float[] sentenceBuffer = ArrayPool<float>.Shared.Rent(Math.Min(_maxBufferedSamples, SampleRate * 10));
        float[] silenceBuffer = ArrayPool<float>.Shared.Rent(SilenceBufferSamples);
        float[] preRoll = new float[PreRollSamples];
        int preRollLength = 0;
        int sentenceLength = 0;
        int silenceLength = 0;
        int silenceChunks = 0;
        bool isSpeaking = false;

        Exception? failure = null;
        try
        {
            await foreach (var owner in inputChannel.ReadAllAsync(ct))
            {
                // Dispose the incoming chunk rental once we have copied what we need.
                using (owner)
                {

                    // ── Copy chunk into ONNX input tensor ────────────────────────
                    owner.Memory.Span[..WindowSize].CopyTo(inputTensor.Buffer.Span);

                    // ── Run Silero VAD ────────────────────────────────────────────
                    using var results = _vadSession.Run(onnxInputs);

                    float probability = results
                        .First(v => v.Name == "output")
                        .AsTensor<float>()
                        .GetValue(0);

                    // Silero returns the next state as an output tensor; copy it back into our state tensor for the next iteration.
                    var nextState = (DenseTensor<float>)results.First(v => v.Name == "stateN").Value;

                    // Сopy the next state back into the state tensor for the next iteration.
                    nextState.Buffer.Span.CopyTo(stateTensor.Buffer.Span);

                    // ── State machine ─────────────────────────────────────────────
                    if (probability >= SpeechThreshold)
                    {
                        // Speech detected.
                        // First: commit any buffered silence back into the sentence
                        // so we don't lose inter-word pauses shorter than the boundary threshold.
                        if (silenceLength > 0)
                        {
                            if (_maxSegmentSamples > 0 && sentenceLength + silenceLength + WindowSize > _maxSegmentSamples)
                            {
                                await FlushSentenceAsync(sentenceBuffer, sentenceLength, silenceBuffer, 0, outputChannel, ct);
                                int overlap = Math.Min(SplitOverlapSamples, sentenceLength);
                                sentenceBuffer.AsSpan(sentenceLength - overlap, overlap).CopyTo(sentenceBuffer);
                                sentenceLength = overlap;
                            }
                            EnsureSentenceCapacity(ref sentenceBuffer, sentenceLength + silenceLength + WindowSize);
                            silenceBuffer.AsSpan(0, silenceLength)
                                         .CopyTo(sentenceBuffer.AsSpan(sentenceLength));
                            sentenceLength += silenceLength;
                            silenceLength = 0;
                        }

                        if (!isSpeaking && preRollLength > 0)
                        {
                            EnsureSentenceCapacity(ref sentenceBuffer, preRollLength);
                            preRoll.AsSpan(0, preRollLength).CopyTo(sentenceBuffer);
                            sentenceLength = preRollLength;
                            preRollLength = 0;
                        }

                        if (_maxSegmentSamples > 0 && sentenceLength + WindowSize > _maxSegmentSamples)
                        {
                            await FlushSentenceAsync(sentenceBuffer, sentenceLength, silenceBuffer, 0, outputChannel, ct);
                            int overlap = Math.Min(SplitOverlapSamples, sentenceLength);
                            sentenceBuffer.AsSpan(sentenceLength - overlap, overlap).CopyTo(sentenceBuffer);
                            sentenceLength = overlap;
                        }

                        EnsureSentenceCapacity(ref sentenceBuffer, sentenceLength + WindowSize);
                        owner.Memory.Span[..WindowSize].CopyTo(sentenceBuffer.AsSpan(sentenceLength));
                        sentenceLength += WindowSize;

                        silenceChunks = 0;
                        isSpeaking = true;
                    }
                    else if (isSpeaking)
                    {
                        // Silence after speech — hold in the side buffer, not yet committed.
                        owner.Memory.Span[..WindowSize].CopyTo(silenceBuffer.AsSpan(silenceLength));
                        silenceLength += WindowSize;
                        silenceChunks++;
                    }
                    else
                    {
                        int shift = Math.Max(0, preRollLength + WindowSize - PreRollSamples);
                        if (shift > 0)
                        {
                            preRoll.AsSpan(shift, preRollLength - shift).CopyTo(preRoll);
                            preRollLength -= shift;
                        }
                        owner.Memory.Span[..WindowSize].CopyTo(preRoll.AsSpan(preRollLength));
                        preRollLength += WindowSize;
                    }

                    // ── Sentence-boundary check ───────────────────────────────────
                    if (isSpeaking && silenceChunks >= MaxSilenceChunks)
                    {
                        await FlushSentenceAsync(
                            sentenceBuffer, sentenceLength,
                            silenceBuffer, silenceLength,
                            outputChannel, ct);

                        preRollLength = Math.Min(PreRollSamples, silenceLength);
                        if (preRollLength > 0)
                            silenceBuffer.AsSpan(silenceLength - preRollLength, preRollLength).CopyTo(preRoll);
                        sentenceLength = 0;
                        silenceLength = 0;
                        silenceChunks = 0;
                        isSpeaking = false;
                    }
                }
            }

            // ── Flush any remaining speech if the stream was cut off mid-sentence ─
            if (sentenceLength > 0)
            {
                await FlushSentenceAsync(
                    sentenceBuffer, sentenceLength,
                    silenceBuffer, silenceLength,
                    outputChannel, ct);
            }
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

            // Signal Whisper that no more sentences are coming.
            outputChannel.TryComplete(failure);
        }
    }

    private void EnsureSentenceCapacity(ref float[] buffer, int required)
    {
        if (required > _maxBufferedSamples)
            throw new InvalidDataException("Speech segment exceeds SttSettings:MaxBufferedSegmentSeconds.");
        if (required <= buffer.Length) return;

        int capacity = (int)Math.Min(_maxBufferedSamples, Math.Max((long)buffer.Length * 2, required));
        float[] larger = ArrayPool<float>.Shared.Rent(capacity);
        buffer.AsSpan().CopyTo(larger);
        ArrayPool<float>.Shared.Return(buffer);
        buffer = larger;
    }

    /// <summary>
    /// Rents an output buffer from MemoryPool, copies the sentence (+ a short natural tail
    /// of silence) into it, and writes the rental + exact length to the output channel.
    /// Ownership of the rental transfers to the channel consumer.
    /// </summary>
    private static async ValueTask FlushSentenceAsync(
        float[] sentenceBuffer, int sentenceLength,
        float[] silenceBuffer, int silenceLength,
        ChannelWriter<(IMemoryOwner<float>, int)> channel,
        CancellationToken ct)
    {
        // Append a short tail of silence for a clean Whisper decode.
        int tailSamples = Math.Min(TailSilenceChunks * WindowSize, silenceLength);
        int totalLength = sentenceLength + tailSamples;

        IMemoryOwner<float> output = MemoryPool<float>.Shared.Rent(totalLength);
        sentenceBuffer.AsSpan(0, sentenceLength).CopyTo(output.Memory.Span);
        silenceBuffer.AsSpan(0, tailSamples).CopyTo(output.Memory.Span[sentenceLength..]);

        try
        {
            await channel.WriteAsync((output, totalLength), ct);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Performs a single dummy inference pass to force ONNX Runtime JIT graph compilation
    /// before the first real audio chunk arrives, eliminating the cold-start latency spike.
    ///
    /// The warmup state tensors are intentionally discarded — this only primes the runtime,
    /// not the model's recurrent state (which must start zeroed for each new audio stream).
    /// </summary>
    public void WarmUp()
    {
        Console.WriteLine("[SYSTEM] Warming up VAD...");

        var state = new DenseTensor<float>([2, 1, 128]);
        state.Fill(0f);
        var sr = new DenseTensor<long>(new long[] { SampleRate }, [1]);
        var input = new DenseTensor<float>([1, WindowSize]);
        input.Fill(0f);   // Silent frame — sufficient to trigger JIT compilation.

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