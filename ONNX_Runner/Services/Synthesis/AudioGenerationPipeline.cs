using System.Buffers;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services.Synthesis;
/// <summary>
/// Executes the synthesis producer/consumer pipeline after request-level concerns
/// have already been resolved. This class knows about audio generation and processing,
/// but it does not know whether the final HTTP response is raw audio, JSON, a file,
/// a network stream, or any future wire format.
/// </summary>
internal static class AudioGenerationPipeline
{
    /// <summary>
    /// Statistics for one completed generation pipeline.
    /// </summary>
    internal readonly record struct GenerationResult(long SamplesWritten, int SampleRate)
    {
        public double AudioSeconds => SampleRate > 0
            ? SamplesWritten / (double)SampleRate
            : 0.0;
    }
    /// <summary>
    /// Runs synthesis, cloning, DSP, and encoding through the producer/consumer pipeline.
    /// Returns the exact logical output duration produced by the audio path.
    /// </summary>
    public static async Task<GenerationResult> GenerateAsync(
        SynthesisContext ctx,
        Stream targetStream,
        bool flushAfterEachSentence,
        CancellationToken cancellationToken)
    {
        var request = ctx.Request;
        bool earlySplit = request.EarlySplit ?? ctx.ChunkerConfig.EarlySplit;
        var textChunks = ctx.TextChunker.Split(request.Input, earlySplit);
        float[]? targetFingerprint = null;
        float[]? sourceFingerprint = null;
        // Fetch pre-computed tone embeddings from the Voice Library if cloning is active
        if (ctx.CanClone)
        {
            ctx.OpenVoice!.VoiceLibrary.TryGetValue(request.Voice, out targetFingerprint);
            ctx.OpenVoice.VoiceLibrary.TryGetValue("piper_base", out sourceFingerprint);
        }

        // =================================================================
        // DSP MODIFIERS SETUP (PITCH, VOLUME & EFFECTS)
        // =================================================================
        if (!Enum.TryParse(request.Effect ?? ctx.EffectsConfig.DefaultEffect, true, out VoiceEffectType effectType))
        {
            effectType = VoiceEffectType.None;
        }
        float effectAmount = Math.Clamp(request.EffectIntensity ?? ctx.EffectsConfig.DefaultIntensity, 0f, 1f);
        if (!Enum.TryParse(request.Environment ?? ctx.EffectsConfig.DefaultEnvironment, true, out SpatialEnvironment envType))
        {
            envType = SpatialEnvironment.None;
        }
        float envIntensity = Math.Clamp(request.EnvironmentIntensity ?? ctx.EffectsConfig.DefaultEnvironmentIntensity, 0f, 1f);

        // Avoid constructing stateful DSP engines when the request will never use them.
        // SpatialEffectsEngine owns several delay/reverb buffers, so this also removes the
        // largest avoidable per-request allocation from the clean-voice path.
        bool useEffect =
            ctx.EffectsConfig.EnableGlobalEffects &&
            effectType != VoiceEffectType.None &&
            effectAmount > 0.001f;
        bool useEnvironment =
            ctx.EffectsConfig.EnableGlobalEffects &&
            envType != SpatialEnvironment.None &&
            envIntensity > 0.001f;

        AudioEqualizer? equalizer = ctx.EffectsConfig.EnableGlobalEffects
            ? AudioEqualizer.Create(request.Equalizer ?? ctx.EffectsConfig.DefaultEqualizer, ctx.ProcessingSampleRate)
            : null;

        using AudioEffectsEngine? effectsEngine = useEffect
            ? new AudioEffectsEngine(ctx.EffectsConfig, ctx.ProcessingSampleRate)
            : null;
        SpatialEffectsEngine? spatialEngine = useEnvironment
            ? new SpatialEffectsEngine(ctx.ProcessingSampleRate)
            : null;

        // Pitch Priority: explicit request value → server default from config → fallback 1.0 (no shift)
        float targetPitch = request.Pitch ?? ctx.DspConfig.DefaultPitch;
        bool usePitchShift = Math.Abs(targetPitch - 1.0f) > 0.001f;

        using var pitchShifter = usePitchShift
            ? new PitchShifter(ctx.PiperConfig.Audio.SampleRate)
            : null;

        pitchShifter?.SetPitch(targetPitch);

        // Model/codec input resamplers preserve the established per-chunk resets.
        // Output conversion below retains its DSP state across the complete response.
        var cloneResampler =
            ctx.CanClone && ctx.PiperConfig.Audio.SampleRate != ctx.OutSampleRate
                ? new AudioResampler(ctx.PiperConfig.Audio.SampleRate, ctx.OutSampleRate)
                : null;
        var processingResampler =
            ctx.OutSampleRate != ctx.ProcessingSampleRate
                ? new AudioResampler(ctx.OutSampleRate, ctx.ProcessingSampleRate)
                : null;
        using var outputResampler = ctx.ProcessingSampleRate != ctx.FinalSampleRate
            ? new AudioOutputResampler(ctx.ProcessingSampleRate, ctx.FinalSampleRate)
            : null;

        // Volume Priority: explicit request value → server default from config → fallback 1.0 (no change)
        float requestedVolume = request.Volume ?? ctx.DspConfig.DefaultVolume;
        // Converts VolumeBoosterDb from dB to linear gain and merges it with requestedVolume.
        // Evaluated as a single float multiply, applying flat compensation without an extra audio pass.
        float boosterLinear = MathF.Pow(10f, ctx.DspConfig.VolumeBoosterDb / 20f);
        float targetVolume = requestedVolume * boosterLinear;
        var outputVolume = new VolumeShifter(ctx.FinalSampleRate, targetVolume);
        // =================================================================
        float currentSpeed = (request.Speed > 0.1f) ? request.Speed : 1.0f;
        int silenceSamplesCount = (int)(ctx.ProcessingSampleRate * (ctx.ChunkerConfig.SentencePauseSeconds / currentSpeed));
        float[] absoluteSilence = new float[silenceSamplesCount];
        // Optional anti-aliasing low-pass filter to clean up cloning artifacts
        NAudio.Dsp.BiQuadFilter? filter = null;
        if (ctx.CanClone && ctx.DspConfig.EnableLowPassFilter)
        {
            // Find the Nyquist frequency (half of the Sample Rate)
            float nyquistFrequency = ctx.ProcessingSampleRate / 2.0f;
            // Limit the cutoff frequency, leaving a small margin of safety (e.g. 10 Hz),
            // so that the BiQuad filter math never approaches a critical limit.
            float safeCutoff = Math.Min(ctx.DspConfig.LowPassCutoffFrequency, nyquistFrequency - 10f);

            // Q-Factor Priority: explicit request value → server default from config
            float targetQFactor = request.LowPassQFactor ?? ctx.DspConfig.LowPassQFactor;
            filter = NAudio.Dsp.BiQuadFilter.LowPassFilter(
                ctx.ProcessingSampleRate,
                safeCutoff,
                targetQFactor
            );
        }

        using var streamManager = new AudioStreamManager(ctx.AudioFormat, ctx.FinalSampleRate, targetStream,
            enforceSampleRate: ctx.EnforceSampleRate);
        // =================================================================
        // PRE-CALCULATE VOICE BLEND
        // =================================================================
        // Calculate the latent space blending strictly once per request,
        // rather than re-calculating it for every audio chunk inside the loop.
        float[]? blendedTarget = null;
        if (ctx.CanClone && targetFingerprint != null && sourceFingerprint != null)
        {
            // Clone Intensity Priority: explicit request value → server default from config,
            // same ?? pattern already used for Pitch/Volume above.
            float intensity = request.CloneIntensity ?? ctx.ClonerConfig.CloneIntensity;
            // We use SLERP for natural mixing of latent vectors. Slerp owns the single
            // result allocation; avoid allocating and immediately discarding a second array here.
            blendedTarget = Slerp(sourceFingerprint, targetFingerprint, intensity);
        }
        // Internal channel for passing raw audio chunks between the Generator and the DSP Processor.
        // Capacity stays intentionally generous for local use; SingleReader/SingleWriter let the
        // channel skip synchronization paths that this one-producer/one-consumer pipeline never needs.
        var channelOptions = new System.Threading.Channels.BoundedChannelOptions(10)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        };
        var channel = System.Threading.Channels.Channel.CreateBounded<(float[] Buffer, int Length, bool IsSentenceFinished)>(channelOptions);
        // Deadlock guard: Channel<T> lacks a native reader-abort signal. If the consumer faults while
        // the producer waits on WriteAsync (bounded capacity), the producer would hang forever and
        // leak the request gate. The consumer catch block cancels this linked token to unstick it.
        using var producerUnstickCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken producerToken = producerUnstickCts.Token;
        // Preserves the root-cause consumer exception, preventing it from being masked by
        // the synthetic OperationCanceledException triggered when unsticking the producer.
        Exception? consumerFault = null;
        // PRODUCER: Phonemizes text and generates raw base audio using Piper ONNX
        var producerTask = Task.Run(async () =>
        {
            try
            {
                // LOCAL STATE: Tracks sentence continuation across chunks within the same request.
                // Defaults to true, assuming the very first chunk is the start of a new thought.
                bool previousChunkWasFinished = true;
                bool pitchPending = false;
                foreach (var textChunk in textChunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string chunk = textChunk.Text;
                    ReadOnlySpan<char> cleanChunk = chunk.AsSpan().Trim();
                    // =========================================================
                    // EMPTY INPUT GUARD
                    // =========================================================
                    // Skip empty chunks without changing sentence continuation state.
                    if (cleanChunk.IsEmpty)
                    {
                        continue;
                    }
                    // =========================================================
                    // SENTENCE CONTINUATION
                    // =========================================================
                    bool isContinuation = false;
                    bool isFinished = textChunk.IsSentenceFinished;
                    // Find the first letter or digit after prose punctuation. Leading numbers must
                    // participate so a lowercase word after a number does not imply continuation.
                    int firstLetterIdx = 0;
                    while (firstLetterIdx < cleanChunk.Length && !char.IsLetterOrDigit(cleanChunk[firstLetterIdx]))
                    {
                        firstLetterIdx++;
                    }
                    // A chunk following an unfinished sentence remains a continuation regardless of case.
                    if (!previousChunkWasFinished)
                    {
                        isContinuation = true;
                    }
                    // Otherwise, a lowercase first content character suggests continuation.
                    // Without that signal, a completed previous chunk starts a fresh thought.
                    else if (firstLetterIdx < cleanChunk.Length)
                    {
                        isContinuation = char.IsLower(cleanChunk[firstLetterIdx]);
                    }
                    // Sentence completion is determined once by TextChunker. This avoids
                    // rescanning trailing punctuation here and keeps emergency splits as continuations.

                    // Generate the base voice phonemes first. A non-empty text chunk can become
                    // empty after normalization (for example, a standalone closing quote).
                    string phonemes = ctx.Phonemizer.GetPhonemes(chunk, request.Language);
                    // Never invoke Piper with an empty phoneme stream. The wrapper IDs alone can
                    // produce a short voiced artifact even though there is no pronounceable input.
                    // Deliberately do NOT reject punctuation-only phonemes here: supported sequences
                    // such as ?, !, ?!, ⁉, and ‽ must remain available to the acoustic model.
                    if (string.IsNullOrWhiteSpace(phonemes))
                    {
                        continue;
                    }
                    // Update local state only for chunks that are actually synthesized. A stripped
                    // quote-only chunk must not change continuation state for the next real chunk.
                    previousChunkWasFinished = isFinished;
                    // Pass the streaming flags to the generator
                    var rawResult = ctx.PiperRunner.SynthesizeAudioRaw(phonemes, isContinuation, isFinished, request.Speed, request.NoiseScale, request.NoiseW);
                    // NOTE: Volume is intentionally NOT applied here. It's applied in the
                    // consumer task, after voice cloning (if active), so the cloning model
                    // always sees Piper's natural, un-boosted waveform.
                    // Apply Pitch Shifting if requested.
                    // SoundTouch stays continuous across generated chunks.
                    // A finished sentence only means its buffered tail must be flushed
                    // before the consumer inserts the configured sentence pause.
                    if (usePitchShift)
                    {
                        bool flushPitch = isFinished;
                        pitchPending = !flushPitch;
                        int estimatedSize = Math.Max(rawResult.Length, 1024);
                        float[] accumulatedBuffer = ArrayPool<float>.Shared.Rent(estimatedSize);
                        int accumulatedLength = 0;
                        bool handedOff = false;
                        try
                        {
                            pitchShifter!.ProcessChunk(rawResult.Buffer, rawResult.Length);
                            DrainPitch(pitchShifter, ref accumulatedBuffer, ref accumulatedLength);

                            if (flushPitch)
                            {
                                pitchShifter.Flush();
                                DrainPitch(pitchShifter, ref accumulatedBuffer, ref accumulatedLength);
                                pitchShifter.Reset();
                            }

                            // SoundTouch may legitimately have no ready output after a very short
                            // continuation chunk. In that case, keep the samples buffered internally;
                            // a later chunk (or Flush at the sentence/request end) will release them.
                            if (accumulatedLength > 0)
                            {
                                await channel.Writer.WriteAsync(
                                    (accumulatedBuffer, accumulatedLength, isFinished),
                                    producerToken);
                                handedOff = true;
                            }
                        }
                        finally
                        {
                            if (!handedOff)
                            {
                                ArrayPool<float>.Shared.Return(accumulatedBuffer);
                            }

                            // rawResult.Buffer is never handed off when pitch shifting is active.
                            ArrayPool<float>.Shared.Return(rawResult.Buffer);
                        }
                    }
                    else
                    {
                        bool handedOff = false;
                        try
                        {
                            // If Pitch is exactly 1.0, bypass DSP and send the original raw audio chunk directly
                            await channel.Writer.WriteAsync((rawResult.Buffer, rawResult.Length, isFinished), producerToken);
                            handedOff = true;
                        }
                        finally
                        {
                            // If the handoff failed (e.g. cancellation thrown during WriteAsync), we must return it
                            if (!handedOff)
                            {
                                ArrayPool<float>.Shared.Return(rawResult.Buffer);
                            }
                        }
                    }
                }

                // Flush any pitch tail still pending when the request ends without a sentence boundary.
                // Keep IsSentenceFinished=false so the consumer does not add a sentence pause.
                if (usePitchShift && pitchPending)
                {
                    float[] tailBuffer = ArrayPool<float>.Shared.Rent(1024);
                    int tailLength = 0;
                    bool handedOff = false;
                    try
                    {
                        pitchShifter!.Flush();
                        DrainPitch(pitchShifter, ref tailBuffer, ref tailLength);

                        if (tailLength > 0)
                        {
                            await channel.Writer.WriteAsync(
                                (tailBuffer, tailLength, false),
                                producerToken);
                            handedOff = true;
                        }
                    }
                    finally
                    {
                        if (!handedOff)
                        {
                            ArrayPool<float>.Shared.Return(tailBuffer);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Completing with the exception forces the consumer's `await foreach` to rethrow once drained,
                // preventing it from executing graceful completion (reverb tails, clean EOF) on a truncated stream.
                channel.Writer.TryComplete(ex);
                throw; // preserve existing propagation to Task.WhenAll / the outer catch
            }
            finally
            {
                // Must use TryComplete: if catch already faulted the channel, calling Complete()
                // would throw InvalidOperationException and mask the original failure.
                channel.Writer.TryComplete();
            }
        }, cancellationToken);
        // CONSUMER: Applies voice cloning, resampling, effects, and pushes to the network stream
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    float[]? rentedBuffer1 = null;
                    float[]? rentedBuffer2 = null;
                    float[]? rentedBuffer3 = null;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        float[] currentBuffer = chunk.Buffer;
                        int currentLength = chunk.Length;
                        if (ctx.CanClone && blendedTarget != null && sourceFingerprint != null)
                        {
                            // OpenVoice requires its configured sampling rate. Avoid cloning the whole
                            // sentence into a second pooled buffer when Piper already produces that rate.
                            float[] cloneInputBuffer = currentBuffer;
                            int cloneInputLength = currentLength;
                            if (cloneResampler != null)
                            {
                                var r1 = cloneResampler.Resample(currentBuffer, currentLength);
                                rentedBuffer1 = r1.Buffer;
                                cloneInputBuffer = rentedBuffer1;
                                cloneInputLength = r1.Length;
                            }
                            // Runtime cloning computes the spectrogram directly into a pooled flat
                            // [bins, frames] buffer, matching the ONNX tensor layout and avoiding both
                            // a large float[,] allocation and a full transpose/copy before inference.
                            var specChunk = ctx.AudioProc!.GetColorizerSpectrogram(
                                cloneInputBuffer.AsSpan(0, cloneInputLength));
                            if (specChunk.Buffer != null)
                            {
                                try
                                {
                                    float tau = request.ToneTemperature ?? ctx.ClonerConfig.ToneTemperature;
                                    var rClone = ctx.OpenVoice!.ApplyToneColor(
                                        specChunk.Buffer,
                                        specChunk.Frames,
                                        specChunk.Bins,
                                        sourceFingerprint,
                                        blendedTarget,
                                        tau);
                                    rentedBuffer3 = rClone.Buffer;
                                    currentBuffer = rentedBuffer3;
                                    currentLength = rClone.Length;
                                }
                                finally
                                {
                                    ArrayPool<float>.Shared.Return(specChunk.Buffer);
                                }
                            }
                            else
                            {
                                currentBuffer = cloneInputBuffer;
                                currentLength = cloneInputLength;
                            }
                        }
                        // Preserve the established DSP input rate before applying effects.
                        if (processingResampler != null)
                        {
                            var r2 = processingResampler.Resample(currentBuffer, currentLength);
                            rentedBuffer2 = r2.Buffer;
                            currentBuffer = rentedBuffer2;
                            currentLength = r2.Length;
                        }
                        // Apply character effects before spatial acoustics.
                        currentLength = effectsEngine?.ApplyEffect(currentBuffer.AsSpan(0, currentLength), effectType, effectAmount)
                            ?? currentLength;

                        // Apply spatial acoustics AFTER character effects.
                        spatialEngine?.ApplyEnvironment(currentBuffer.AsSpan(0, currentLength), envType, envIntensity);

                        // Device profiles color the completed signal, including generated DSP sound.
                        equalizer?.Apply(currentBuffer.AsSpan(0, currentLength));

                        // Cleanup and output conversion precede the common final gain and peak protection.
                        WriteOutput(currentBuffer.AsSpan(0, currentLength), streamManager, outputResampler, filter, outputVolume);
                        // SentencePauseSeconds belongs only to real sentence boundaries.
                        // EarlySplit and emergency chunks already carry their own punctuation/context.
                        if (chunk.IsSentenceFinished)
                        {
                            Array.Clear(absoluteSilence, 0, absoluteSilence.Length);
                            // Advance the same DSP state during pauses, then filter the resulting tails.
                            int pauseLength = effectsEngine?.ApplyEffect(absoluteSilence.AsSpan(), effectType, effectAmount)
                                ?? absoluteSilence.Length;
                            spatialEngine?.ApplyEnvironment(absoluteSilence.AsSpan(0, pauseLength), envType, envIntensity);
                            equalizer?.Apply(absoluteSilence.AsSpan(0, pauseLength));

                            WriteOutput(absoluteSilence.AsSpan(0, pauseLength), streamManager, outputResampler, filter, outputVolume);
                        }
                        // Keep flushing every generated chunk so an EarlySplit chunk reaches the client
                        // immediately. The setting name is retained for configuration compatibility.
                        if (flushAfterEachSentence)
                        {
                            targetStream.Flush();
                        }
                    }
                    finally
                    {
                        // Return every temporary buffer even when generation or output fails.
                        ArrayPool<float>.Shared.Return(chunk.Buffer);
                        if (rentedBuffer1 != null) ArrayPool<float>.Shared.Return(rentedBuffer1);
                        if (rentedBuffer2 != null) ArrayPool<float>.Shared.Return(rentedBuffer2);
                        if (rentedBuffer3 != null) ArrayPool<float>.Shared.Return(rentedBuffer3);
                    }
                }
                // Complete delayed character audio before draining spatial reflections or output protection.
                if (effectsEngine?.HasPendingOutput == true)
                {
                    DrainEffectTail(effectsEngine, spatialEngine, envType, envIntensity, ctx.ProcessingSampleRate,
                        equalizer, outputVolume, streamManager,
                        outputResampler, filter, cancellationToken);
                }
                // =================================================================
                // REVERB TAIL EXTENSION (Flushes spatial acoustics once at the end)
                // =================================================================
                // Drains residual reverb until output drops below the configured silence floor.
                // Drain spatial state through device filters; exclude character effects that generate noise.
                // Priority: request.ExtendReverbTail -> ctx.EffectsConfig.ExtendReverbTailOnFinish.
                bool extendTail = request.ExtendReverbTail ?? ctx.EffectsConfig.ExtendReverbTailOnFinish;
                if (extendTail && spatialEngine != null)
                {
                    DrainSpatialTail(spatialEngine, envType, envIntensity, ctx.ProcessingSampleRate,
                        ctx.EffectsConfig.ReverbTailSilenceFloor, equalizer, outputVolume,
                        targetVolume, streamManager, outputResampler, filter, cancellationToken);
                    if (flushAfterEachSentence)
                    {
                        targetStream.Flush();
                    }
                }
                // Final converted samples must enter peak protection before its retained audio is emitted.
                outputResampler?.Complete(streamManager, outputVolume, cancellationToken);
                FlushOutputVolume(outputVolume, streamManager, cancellationToken);
            }
            catch (Exception ex)
            {
                // Preserve root cause so the producer's synthetic OperationCanceledException doesn't mask it.
                consumerFault = ex;
                // Unstick producer waiting on bounded WriteAsync to prevent deadlock.
                producerUnstickCts.Cancel();
                throw;
            }
        }, cancellationToken);
        try
        {
            await Task.WhenAll(producerTask, consumerTask);
            cancellationToken.ThrowIfCancellationRequested();
            streamManager.Dispose();
        }
        catch
        {
            // Incomplete output must not be finalized into a closed response.
            streamManager.Abort();

            // The consumer may stop before reading every pooled chunk from the channel.
            while (channel.Reader.TryRead(out var pending))
            {
                ArrayPool<float>.Shared.Return(pending.Buffer);
            }

            // Prefer the consumer's real failure over a synthetic cancellation that may
            // have been raised in the producer purely to unstick it from a full channel.
            if (consumerFault != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(consumerFault).Throw();
            }

            throw;
        }
        return new GenerationResult(
            streamManager.SamplesWritten,
            ctx.FinalSampleRate);
    }

    /// <summary>Passes retained character audio through the remaining request stages without reapplying the effect.</summary>
    private static void DrainEffectTail(AudioEffectsEngine effectsEngine, SpatialEffectsEngine? spatialEngine,
        SpatialEnvironment environment, float intensity, int sampleRate, AudioEqualizer? equalizer,
        VolumeShifter outputVolume, AudioStreamManager manager,
        AudioOutputResampler? outputResampler, NAudio.Dsp.BiQuadFilter? filter, CancellationToken cancellationToken)
    {
        if (!effectsEngine.HasPendingOutput) return;

        int probeSamples = Math.Max(1, sampleRate / 50);
        float[] probe = ArrayPool<float>.Shared.Rent(probeSamples);
        try
        {
            while (effectsEngine.HasPendingOutput)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = effectsEngine.Complete(probe.AsSpan(0, probeSamples));
                var samples = probe.AsSpan(0, length);
                spatialEngine?.ApplyEnvironment(samples, environment, intensity);
                equalizer?.Apply(samples);
                WriteOutput(samples, manager, outputResampler, filter, outputVolume);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(probe);
        }
    }

    /// <summary>Drains spatial state until a complete reflection window stays quiet or the safety cap is reached.</summary>
    private static void DrainSpatialTail(SpatialEffectsEngine spatialEngine, SpatialEnvironment environment,
        float intensity, int sampleRate, float silenceFloor, AudioEqualizer? equalizer,
        VolumeShifter outputVolume, float volume, AudioStreamManager manager,
        AudioOutputResampler? outputResampler, NAudio.Dsp.BiQuadFilter? filter, CancellationToken cancellationToken)
    {
        const int maxTailSeconds = 4;
        int probeSamples = Math.Max(1, sampleRate / 50);
        int maxSamples = checked(sampleRate * maxTailSeconds);
        int quietWindow = spatialEngine.TailSilenceWindowSamples;
        int quietSamples = 0, processed = 0;
        float[] probe = ArrayPool<float>.Shared.Rent(probeSamples);
        try
        {
            while (processed < maxSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int length = Math.Min(probeSamples, maxSamples - processed);
                var samples = probe.AsSpan(0, length);
                samples.Clear();
                // Character noise generators must not keep an otherwise finished spatial tail alive.
                spatialEngine.ApplyEnvironment(samples, environment, intensity);
                equalizer?.Apply(samples);
                float peak = 0f;
                for (int i = 0; i < length; i++) peak = Math.Max(peak, MathF.Abs(probe[i]));
                peak *= MathF.Abs(volume);
                WriteOutput(samples, manager, outputResampler, filter, outputVolume);
                processed += length;
                quietSamples = peak < silenceFloor ? quietSamples + length : 0;
                if (quietSamples >= quietWindow) break;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(probe);
        }
    }

    private static void FlushOutputVolume(VolumeShifter volume, AudioStreamManager manager,
        CancellationToken cancellationToken)
    {
        Span<float> tail = stackalloc float[256];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = volume.Flush(tail);
            if (count == 0) return;
            manager.WriteChunk(tail[..count]);
        }
    }

    /// <summary>Filters and converts PCM before applying the common final gain and peak protection.</summary>
    private static void WriteOutput(Span<float> samples, AudioStreamManager manager,
        AudioOutputResampler? resampler, NAudio.Dsp.BiQuadFilter? filter, VolumeShifter volume)
    {
        if (samples.IsEmpty) return;
        if (resampler != null)
        {
            resampler.Write(samples, manager, filter, volume);
            return;
        }
        if (filter != null)
        {
            for (int i = 0; i < samples.Length; i++) samples[i] = filter.Transform(samples[i]);
        }
        int written = volume.Process(samples);
        manager.WriteChunk(samples[..written]);
    }

    /// <summary>
/// Drains ready SoundTouch samples into the rented destination, growing it when required.
/// The caller retains ownership of the updated pooled buffer.
/// </summary>
    private static void DrainPitch(
        PitchShifter pitchShifter,
        ref float[] buffer,
        ref int length)
    {
        while (true)
        {
            if (length == buffer.Length)
            {
                float[] expanded = ArrayPool<float>.Shared.Rent(buffer.Length * 2);
                buffer.AsSpan(0, length).CopyTo(expanded);
                ArrayPool<float>.Shared.Return(buffer);
                buffer = expanded;
            }

            int received = pitchShifter.Drain(buffer.AsSpan(length));
            if (received <= 0)
            {
                return;
            }

            length += received;
        }
    }

    /// <summary>Blends equal-length speaker fingerprints with SLERP, using linear interpolation for nearly collinear vectors or extrapolation.</summary>
    private static float[] Slerp(float[] source, float[] target, float t)
    {
        float[] result = new float[source.Length];

        // Calculate the Dot Product (cosine of the angle between vectors)
        // and the squared magnitudes of both vectors.
        float dot = 0f;
        float sourceMagSq = 0f;
        float targetMagSq = 0f;
        for (int i = 0; i < source.Length; i++)
        {
            dot += source[i] * target[i];
            sourceMagSq += source[i] * source[i];
            targetMagSq += target[i] * target[i];
        }

        // Normalize the dot product to the range [-1, 1]
        float magnitude = MathF.Sqrt(sourceMagSq * targetMagSq);
        if (magnitude > 0.0001f)
        {
            dot /= magnitude;
        }

        dot = Math.Clamp(dot, -1.0f, 1.0f);
        // Fallback to LERP if vectors are almost parallel (DotThreshold)
        // or if t is outside [0, 1] (Extrapolation / "Overdrive" mode).
        // SLERP is mathematically unstable/cyclic outside the 0..1 range.
        const float DotThreshold = 0.9995f;
        if (Math.Abs(dot) > DotThreshold || t < 0.0f || t > 1.0f)
        {
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = source[i] + (target[i] - source[i]) * t;
            }
            return result;
        }
        // Calculate the angles for Spherical Linear Interpolation
        float theta = MathF.Acos(dot);          // The angle between the vectors
        float sinTheta = MathF.Sin(theta);      // The sine of the angle

        float weightSource = MathF.Sin((1.0f - t) * theta) / sinTheta;
        float weightTarget = MathF.Sin(t * theta) / sinTheta;
        // Apply the computed weights to construct the final interpolated vector
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (source[i] * weightSource) + (target[i] * weightTarget);
        }

        return result;
    }
}
