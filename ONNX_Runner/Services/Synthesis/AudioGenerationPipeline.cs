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
        var textChunks = ctx.TextChunker.Split(request.Input);
        float[]? targetFingerprint = null;
        float[]? sourceFingerprint = null;

        // Fetch pre-computed tone embeddings from the Voice Library if cloning is active
        if (ctx.CanClone)
        {
            ctx.OpenVoice!.VoiceLibrary.TryGetValue(request.Voice, out targetFingerprint);
            ctx.OpenVoice.VoiceLibrary.TryGetValue("piper_base", out sourceFingerprint);
        }

        var effectsEngine = new AudioEffectsEngine(ctx.EffectsConfig, ctx.FinalSampleRate);
        var spatialEngine = new SpatialEffectsEngine(ctx.FinalSampleRate);

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

        // Pitch Priority: explicit request value → server default from config → fallback 1.0 (no shift)
        float targetPitch = request.Pitch ?? ctx.DspConfig.DefaultPitch;
        bool usePitchShift = Math.Abs(targetPitch - 1.0f) > 0.001f;

        using var pitchShifter = usePitchShift
            ? new PitchShifter(ctx.PiperConfig.Audio.SampleRate)
            : null;

        if (pitchShifter != null)
        {
            pitchShifter.SetPitch(targetPitch);
        }

        // Volume Priority: explicit request value → server default from config → fallback 1.0 (no change)
        float requestedVolume = request.Volume ?? ctx.DspConfig.DefaultVolume;

        // Converts VolumeBoosterDb from dB to linear gain and merges it with requestedVolume.
        // Evaluated as a single float multiply, applying flat compensation without an extra audio pass.
        float boosterLinear = MathF.Pow(10f, ctx.DspConfig.VolumeBoosterDb / 20f);
        float targetVolume = requestedVolume * boosterLinear;
        bool useVolumeShift = MathF.Abs(targetVolume - 1.0f) > 0.001f;
        // =================================================================

        float currentSpeed = (request.Speed > 0.1f) ? request.Speed : 1.0f;
        int silenceSamplesCount = (int)(ctx.FinalSampleRate * (ctx.ChunkerConfig.SentencePauseSeconds / currentSpeed));
        float[] absoluteSilence = new float[silenceSamplesCount];

        // Optional anti-aliasing low-pass filter to clean up cloning artifacts
        NAudio.Dsp.BiQuadFilter? filter = null;
        if (ctx.CanClone && ctx.DspConfig.EnableLowPassFilter)
        {
            // Find the Nyquist frequency (half of the Sample Rate)
            float nyquistFrequency = ctx.FinalSampleRate / 2.0f;

            // Limit the cutoff frequency, leaving a small margin of safety (e.g. 10 Hz),
            // so that the BiQuad filter math never approaches a critical limit.
            float safeCutoff = Math.Min(ctx.DspConfig.LowPassCutoffFrequency, nyquistFrequency - 10f);

            // Q-Factor Priority: explicit request value → server default from config
            float targetQFactor = request.LowPassQFactor ?? ctx.DspConfig.LowPassQFactor;

            filter = NAudio.Dsp.BiQuadFilter.LowPassFilter(
                ctx.FinalSampleRate,
                safeCutoff,
                targetQFactor
            );
        }


        using var streamManager = new AudioStreamManager(ctx.AudioFormat, ctx.FinalSampleRate, targetStream);
        // =================================================================
        // PRE-CALCULATE VOICE BLEND (Zero-Allocation Optimization)
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

        // Internal channel for passing raw audio chunks between the Generator and the DSP Processor
        var channel = System.Threading.Channels.Channel.CreateBounded<(float[] Buffer, int Length)>(10);

        // Deadlock guard: Channel<T> lacks a native reader-abort signal. If the consumer faults while
        // the producer waits on WriteAsync (bounded capacity), the producer would hang forever and
        // leak the GPU semaphore. The consumer catch block cancels this linked token to unstick it.
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

                foreach (var chunk in textChunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ReadOnlySpan<char> cleanChunk = chunk.AsSpan().Trim();

                    // =========================================================
                    // ULTIMATE DEFENSIVE PARANOIA
                    // =========================================================
                    // If the chunk is empty (e.g., due to a chunker edge case or specific input), 
                    // we skip it to save GPU resources and preserve the current state.
                    if (cleanChunk.IsEmpty)
                    {
                        continue;
                    }

                    // =========================================================
                    // MULTILINGUAL SMART CONTEXT DETECTION FOR STREAMING
                    // =========================================================
                    bool isContinuation = false;
                    bool isFinished;

                    // Ignore leading punctuation (e.g., quotes, dashes) to find the first actual
                    // content character (letter OR digit). Stopping at a digit rather than skipping
                    // past it matters: a sentence like "2024 was a good year." would otherwise have
                    // its case check applied to "was" instead of "2024", wrongly reading a fresh
                    // sentence as a continuation just because the word after the leading number
                    // happens to be lowercase.
                    int firstLetterIdx = 0;
                    while (firstLetterIdx < cleanChunk.Length && !char.IsLetterOrDigit(cleanChunk[firstLetterIdx]))
                    {
                        firstLetterIdx++;
                    }

                    // Determine if this chunk is a continuation of the previous thought
                    // If the previous chunk ended with an EmergencyGlue or lacked a terminator, 
                    // this is 100% a continuation, regardless of case.
                    if (!previousChunkWasFinished)
                    {
                        isContinuation = true;
                    }
                    // Otherwise, rely on the lowercase heuristic for bicameral scripts (Latin,
                    // Cyrillic, Greek...). For unicameral scripts with no case distinction at all
                    // (Chinese, Japanese, Thai, Arabic, Hebrew, Devanagari...), IsLower always
                    // returns false, which safely defaults to "fresh thought" — there is no
                    // orthographic signal available there either way, so this is the correct
                    // fallback, not a workaround.
                    else if (firstLetterIdx < cleanChunk.Length)
                    {
                        isContinuation = char.IsLower(cleanChunk[firstLetterIdx]);
                    }

                    // Check if the chunk ends with a known sentence terminator. TextChunker.Split()
                    // deliberately folds trailing closing quotes/brackets into the chunk (e.g. a
                    // sentence ending in `."` for quoted dialogue), so checking cleanChunk[^1] alone
                    // would wrongly call a complete sentence "unfinished" just because it ends in a
                    // quote mark. Walk back past any such closing punctuation to find the real
                    // terminator underneath, mirroring how TextChunker itself looks past it.
                    int lastRealCharIdx = cleanChunk.Length - 1;
                    while (lastRealCharIdx > 0 && TextChunker.ClosingPunctuation.AsSpan().Contains(cleanChunk[lastRealCharIdx]))
                    {
                        lastRealCharIdx--;
                    }
                    isFinished = TextChunker.SentenceTerminators.AsSpan().Contains(cleanChunk[lastRealCharIdx]);

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

                    // Apply Pitch Shifting if requested
                    if (usePitchShift)
                    {
                        // ZERO-ALLOCATION ACCUMULATOR:
                        int estimatedSize = (int)(rawResult.Length * 1.5);
                        float[] accumulatedBuffer = ArrayPool<float>.Shared.Rent(estimatedSize);
                        int accumulatedLength = 0;
                        bool handedOff = false;

                        try
                        {
                            // Process the main audio
                            foreach (var segment in pitchShifter!.ProcessChunk(rawResult.Buffer, rawResult.Length))
                            {
                                if (accumulatedLength + segment.Count > accumulatedBuffer.Length)
                                {
                                    float[] newBuffer = ArrayPool<float>.Shared.Rent(accumulatedBuffer.Length * 2);
                                    Array.Copy(accumulatedBuffer, newBuffer, accumulatedLength);
                                    ArrayPool<float>.Shared.Return(accumulatedBuffer);
                                    accumulatedBuffer = newBuffer;
                                }
                                segment.AsSpan().CopyTo(accumulatedBuffer.AsSpan(accumulatedLength));
                                accumulatedLength += segment.Count;
                            }
                            // Flush internal WSOLA buffers immediately for THIS sentence
                            foreach (var segment in pitchShifter!.Flush())
                            {
                                if (accumulatedLength + segment.Count > accumulatedBuffer.Length)
                                {
                                    float[] newBuffer = ArrayPool<float>.Shared.Rent(accumulatedBuffer.Length * 2);
                                    Array.Copy(accumulatedBuffer, newBuffer, accumulatedLength);
                                    ArrayPool<float>.Shared.Return(accumulatedBuffer);
                                    accumulatedBuffer = newBuffer;
                                }
                                segment.AsSpan().CopyTo(accumulatedBuffer.AsSpan(accumulatedLength));
                                accumulatedLength += segment.Count;
                            }

                            // Send the fully reassembled sentence to the Consumer
                            await channel.Writer.WriteAsync((accumulatedBuffer, accumulatedLength), producerToken);
                            handedOff = true; // Ownership successfully transferred to the Consumer
                        }
                        finally
                        {
                            // Only return accumulatedBuffer ourselves if the handoff never happened
                            if (!handedOff)
                            {
                                ArrayPool<float>.Shared.Return(accumulatedBuffer);
                            }

                            // rawResult.Buffer is NEVER handed off in this branch — it's always ours to return
                            ArrayPool<float>.Shared.Return(rawResult.Buffer);
                        }
                    }
                    else
                    {
                        bool handedOff = false;
                        try
                        {
                            // If Pitch is exactly 1.0, bypass DSP and send the original raw audio chunk directly
                            await channel.Writer.WriteAsync(rawResult, producerToken);
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

                            if (ctx.PiperConfig.Audio.SampleRate != ctx.OutSampleRate)
                            {
                                var r1 = ctx.AudioProc!.Resample(
                                    currentBuffer,
                                    currentLength,
                                    ctx.PiperConfig.Audio.SampleRate,
                                    ctx.OutSampleRate);

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

                        // Applies target volume post-cloning to protect OpenVoice from boosted input levels.
                        // Acts as unified gain staging for both cloned and base Piper outputs.
                        if (useVolumeShift)
                        {
                            VolumeShifter.ApplyVolume(currentBuffer.AsSpan(0, currentLength), targetVolume);
                        }

                        // Final resampling to match the requested output format (e.g., Opus requires 24kHz/48kHz)
                        if (ctx.OutSampleRate != ctx.FinalSampleRate)
                        {
                            var r2 = ctx.AudioProc!.Resample(currentBuffer, currentLength, ctx.OutSampleRate, ctx.FinalSampleRate);
                            rentedBuffer2 = r2.Buffer;
                            currentBuffer = rentedBuffer2;
                            currentLength = r2.Length;
                        }

                        // Apply character effects FIRST (Overdrive, Telephone, LoFiTape, etc.)
                        effectsEngine.ApplyEffect(currentBuffer.AsSpan(0, currentLength), effectType, effectAmount);

                        // Apply spatial acoustics AFTER character effects
                        spatialEngine.ApplyEnvironment(currentBuffer.AsSpan(0, currentLength), envType, envIntensity);
                        streamManager.WriteChunk(currentBuffer.AsSpan(0, currentLength), filter);

                        // Append a brief pause (silence) between sentences for natural pacing
                        Array.Clear(absoluteSilence, 0, absoluteSilence.Length);

                        // Apply character effects to silence (e.g. tape hiss continues during pauses)
                        effectsEngine.ApplyEffect(absoluteSilence.AsSpan(), effectType, effectAmount);

                        // Apply spatial acoustics to silence so reverb tails ring out naturally
                        spatialEngine.ApplyEnvironment(absoluteSilence.AsSpan(), envType, envIntensity);
                        streamManager.WriteChunk(absoluteSilence.AsSpan(), filter);

                        if (flushAfterEachSentence)
                        {
                            targetStream.Flush();
                        }
                    }
                    finally
                    {
                        // ZERO-ALLOCATION PATTERN: 
                        // Always return rented memory arrays to the shared pool to prevent Garbage Collector (GC) pressure and memory leaks.
                        ArrayPool<float>.Shared.Return(chunk.Buffer);
                        if (rentedBuffer1 != null) ArrayPool<float>.Shared.Return(rentedBuffer1);
                        if (rentedBuffer2 != null) ArrayPool<float>.Shared.Return(rentedBuffer2);
                        if (rentedBuffer3 != null) ArrayPool<float>.Shared.Return(rentedBuffer3);
                    }
                }

                // =================================================================
                // REVERB TAIL EXTENSION (Flushes spatial acoustics once at the end)
                // =================================================================
                // Drains residual reverb by feeding silence until output drops below -60 dBFS.
                // Strictly spatial-only: excludes character effects to avoid spinning on static noise floors.
                // Priority: request.ExtendReverbTail -> ctx.EffectsConfig.ExtendReverbTailOnFinish.
                bool extendTail = request.ExtendReverbTail ?? ctx.EffectsConfig.ExtendReverbTailOnFinish;

                if (extendTail && envType != SpatialEnvironment.None)
                {
                    float silenceFloor = ctx.EffectsConfig.ReverbTailSilenceFloor;     // perceptual silence threshold
                    const float maxTailSeconds = 4.0f;          // safety cap for environments that never fully settle

                    int probeSamples = ctx.FinalSampleRate / 50; // 20ms probe blocks
                    int maxSamples = (int)(ctx.FinalSampleRate * maxTailSeconds);
                    int written = 0;

                    // ZERO-ALLOCATION PATTERN: rent the probe buffer instead of `new float[]`.
                    // Rent() may return an array larger than requested, so every access below
                    // is explicitly bounded to probeSamples.
                    float[] tailProbe = ArrayPool<float>.Shared.Rent(probeSamples);
                    try
                    {
                        while (written < maxSamples)
                        {
                            Array.Clear(tailProbe, 0, probeSamples);
                            var probeSpan = tailProbe.AsSpan(0, probeSamples);

                            // effectsEngine intentionally NOT applied here — see note above.
                            spatialEngine.ApplyEnvironment(probeSpan, envType, envIntensity);
                            streamManager.WriteChunk(probeSpan, filter);
                            written += probeSamples;

                            float peak = 0f;
                            for (int i = 0; i < probeSamples; i++)
                            {
                                float absVal = MathF.Abs(tailProbe[i]);
                                if (absVal > peak) peak = absVal;
                            }
                            if (peak < silenceFloor) break; // tail has decayed below audibility
                        }
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(tailProbe);
                    }

                    if (flushAfterEachSentence)
                    {
                        targetStream.Flush();
                    }
                }
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
        }
        catch
        {
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

    // Auxiliary interpolation method
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
