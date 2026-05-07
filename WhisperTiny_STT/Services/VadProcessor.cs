using System.Buffers;
using System.Threading.Channels;
using SherpaOnnx;

namespace WhisperTiny_STT.Services;

/// <summary>
/// Task 2: Two-Tier VAD (Voice Activity Detection) Pipeline.
///
/// Consumes 512-sample audio chunks from Channel 1 (produced by AudioProcessor).
/// Uses Sherpa-ONNX's built-in VAD to monitor the stream and trigger two types of events:
/// 
/// 1. Short Pause (e.g., 0.4s): Yields the accumulated speech segment to be transcribed.
/// 2. Long Pause  (e.g., 0.8s): Yields an End-Of-Turn (EOF) signal to tell the agent to reply.
/// </summary>
public sealed class VadProcessor : IDisposable
{
    private readonly VoiceActivityDetector _vad;
    private readonly int _longPauseSamples;
    private readonly int _forceFlushSamples;
    private readonly float[] _processBuffer = new float[WindowSize];

    // Default audio chunk size from AudioProcessor
    private const int WindowSize = 512;

    public VadProcessor(IConfiguration config, string vadModelPath)
    {


        float threshold = config.GetValue("SttSettings:VadSpeechThreshold", 0.5f);
        float shortPauseSec = config.GetValue("SttSettings:VadShortPauseSeconds", 0.4f);
        float longPauseSec = config.GetValue("SttSettings:VadLongPauseSeconds", 0.8f);
        float minSpeechSec = config.GetValue("SttSettings:VadMinSpeechSeconds", 0.1f);
        float maxAudioBufferSec = config.GetValue("SttSettings:VadMaxBufferSeconds", 60.0f);
        int sampleRate = 16000;

        // Configure Sherpa VAD (This handles the "Short Pause" chunking automatically)
        var vadConfig = new VadModelConfig();
        vadConfig.SileroVad.Model = vadModelPath;
        vadConfig.SileroVad.Threshold = threshold;
        vadConfig.SileroVad.MinSilenceDuration = shortPauseSec;
        vadConfig.SileroVad.MinSpeechDuration = minSpeechSec;
        vadConfig.SampleRate = sampleRate;

        // maxAudioBufferSec - Size of the internal circular buffer in seconds
        _vad = new VoiceActivityDetector(vadConfig, maxAudioBufferSec);
        _longPauseSamples = (int)(longPauseSec * sampleRate);

        // Force flush is a safety mechanism to prevent the VAD from holding onto audio indefinitely if the stream never ends or if there are very long pauses.
        float forceFlushSec = Math.Max(1.0f, maxAudioBufferSec - 2.0f);
        _forceFlushSamples = (int)(forceFlushSec * sampleRate);

        Console.WriteLine($"[SYSTEM] Two-Tier VAD Initialized. Short Pause: {shortPauseSec}s, Long Pause (EOF): {longPauseSec}s, Max Buffer: {maxAudioBufferSec}s, Force Flush: {forceFlushSec}s");
    }

    // Consumes audio chunks from inputChannel, processes them through the VAD, and writes results to outputChannel.
    // The output is a tuple: (Audio Segment, IsEndOfTurn). Audio Segment is null when IsEndOfTurn is true.
    public async Task ProcessVadChannelAsync(
        ChannelReader<IMemoryOwner<float>> inputChannel,
        ChannelWriter<(float[]? Audio, bool IsEndOfTurn)> outputChannel,
        CancellationToken ct = default)
    {
        int silenceSamplesAccumulated = 0;
        int continuousAudioSamples = 0;
        bool eofFired = false;

        try
        {
            await foreach (var owner in inputChannel.ReadAllAsync(ct))
            {
                using (owner)
                {
                    // Zero-allocation copy from rented memory to our dedicated processing buffer
                    owner.Memory.Span[..WindowSize].CopyTo(_processBuffer);

                    // Feed the audio chunk to the Sherpa-ONNX VAD engine
                    _vad.AcceptWaveform(_processBuffer);

                    // Track how much audio has been continuously processed to prevent buffer overflow
                    continuousAudioSamples += WindowSize;

                    // If speech is detected, reset the silence counter and unlock the EOF flag.
                    // Otherwise, accumulate the silence duration.
                    if (_vad.IsSpeechDetected())
                    {
                        silenceSamplesAccumulated = 0;
                        eofFired = false;
                    }
                    else
                    {
                        silenceSamplesAccumulated += WindowSize;
                    }

                    // ── TIER 1: SHORT PAUSE (Chunk Ready) ─────────────────────
                    // When the VAD detects a short pause, it cuts the audio and queues it.
                    while (!_vad.IsEmpty())
                    {
                        var segment = _vad.Front();
                        _vad.Pop();

                        await outputChannel.WriteAsync((segment.Samples, false), ct);

                        // Reset the continuous audio counter since we successfully yielded a chunk
                        continuousAudioSamples = 0;
                    }

                    // ── TIER 3: ANTI-OVERFLOW (Safety Flush) ──────────────────
                    // If continuous audio (e.g., loud background noise) approaches the maximum buffer limit,
                    // force a flush to prevent Sherpa from dropping the oldest data.
                    if (continuousAudioSamples >= _forceFlushSamples)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("[WARNING] VAD buffer nearing limit. Forcing chunk flush to prevent data loss.");
                        Console.ResetColor();

                        _vad.Flush();
                        while (!_vad.IsEmpty())
                        {
                            var segment = _vad.Front();
                            _vad.Pop();
                            await outputChannel.WriteAsync((segment.Samples, false), ct);
                        }

                        // Reset the continuous audio counter after the forced flush
                        continuousAudioSamples = 0;
                    }

                    // ── TIER 2: LONG PAUSE (End of Turn) ──────────────────────
                    // If the user has been silent for the defined "Long Pause" threshold,
                    // signal the downstream Transcriptor that the conversational turn has ended.
                    if (!eofFired && silenceSamplesAccumulated >= _longPauseSamples)
                    {
                        await outputChannel.WriteAsync((null, true), ct);
                        eofFired = true;

                        // Cap the accumulated silence to prevent integer overflow during long idle periods
                        silenceSamplesAccumulated = _longPauseSamples;
                    }
                }
            }

            // ── END OF STREAM HANDLING ────────────────────────────────────────
            // When the input channel completes (e.g., file upload finished or socket closed),
            // flush any remaining speech segments trapped in the VAD buffer.
            _vad.Flush();
            while (!_vad.IsEmpty())
            {
                var segment = _vad.Front();
                _vad.Pop();
                await outputChannel.WriteAsync((segment.Samples, false), ct);
            }

            // Send one final End-of-Turn signal to ensure the downstream pipeline finalizes the text
            await outputChannel.WriteAsync((null, true), ct);
        }
        finally
        {
            // Ensure the output channel is marked as complete to gracefully shut down the Transcriptor
            outputChannel.TryComplete();
        }
    }

    // Optional warm-up to mitigate initial latency on the first inference call.
    public void WarmUp()
    {
        Console.WriteLine("[SYSTEM] Warming up VAD...");
        _vad.AcceptWaveform(new float[WindowSize]);
        _vad.Reset();
        Console.WriteLine("[SYSTEM] VAD warm-up complete.");
    }

    // Dispose pattern to clean up unmanaged resources used by the VAD.
    public void Dispose()
    {
        _vad.Dispose();
        GC.SuppressFinalize(this);
    }
}