using System.Buffers;
using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

/// <summary>
/// Shared core synthesis pipeline. Every external API shape (OpenAI, Tsubaki's own
/// extended endpoint, and any future ones) funnels through this single service after its
/// own adapter translates the wire request into a SynthesisRequest — so there is exactly
/// one place where the GPU/CPU semaphore is acquired and released, no matter how many
/// different endpoints exist. Endpoints and adapters should never call GenerateAsync or
/// touch the semaphore directly; they should only ever call SynthesizeAsync.
/// </summary>
public class SpeechSynthesisService(SemaphoreSlim gpuSemaphore, IServiceProvider services)
{
    public async Task<IResult> SynthesizeAsync(
        SynthesisRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // =================================================================
        // REQUEST VALIDATION
        // =================================================================
        // Wire-format-specific validation (empty input, invalid response_format string) is
        // the adapter's responsibility, done before this method is ever called — by the time
        // a SynthesisRequest reaches here, Input is non-empty and Format is already resolved.

        // =================================================================
        // TEXT LENGTH LIMITATION (OOM PROTECTION)
        // =================================================================
        // Protects the server from Out-Of-Memory errors and GPU timeout limits.
        // If the client sends a massive block of text (like a whole book in one request), 
        // we smoothly truncate it to the allowed limit rather than rejecting the entire request.
        var apiSettings = services.GetRequiredService<ApiSettings>();
        if (apiSettings.MaxTextLength > 0 && request.Input.Length > apiSettings.MaxTextLength)
        {
            request.Input = request.Input[..apiSettings.MaxTextLength];
        }

        // Safely verify if the base TTS model was successfully loaded at startup.
        // If not, we return a 500 Internal Server Error without crashing the server.
        var piperConfig = services.GetService<PiperConfig>();
        if (piperConfig == null)
            return Results.Problem("Model is not loaded properly.", statusCode: 500);

        System.Threading.Channels.Channel<(byte[] Buffer, int Length)>? networkChannel = null;
        Task? networkSenderTask = null;
        try
        {
            // =================================================================
            // CONCURRENCY CONTROL (SEMAPHORE PATTERN)
            // =================================================================
            // Wait for an available slot in the execution queue. This strictly limits 
            // concurrent ONNX inferences to prevent GPU VRAM Out-Of-Memory (OOM) errors 
            // or CPU thread starvation.
            await gpuSemaphore.WaitAsync(cancellationToken);

            try
            {
                var ctx = BuildRequestContext(request, services, piperConfig);

                if (ctx.UseStreaming)
                {
                    httpContext.Response.ContentType = AudioStreamManager.GetMimeType(request.Format);
                    httpContext.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{AudioStreamManager.GetFileName(request.Format)}\"");
                    httpContext.Response.Headers.Append("X-Audio-Sample-Rate", ctx.DisplaySampleRate.ToString());
                    await httpContext.Response.StartAsync(cancellationToken);

                    var channelOptions = new System.Threading.Channels.BoundedChannelOptions(50)
                    {
                        FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
                    };
                    networkChannel = System.Threading.Channels.Channel.CreateBounded<(byte[] Buffer, int Length)>(channelOptions);

                    int chunkSize = ctx.StreamConfig.MinChunkSizeKb * 1024;
                    ctx.TargetStream = new BridgingStream(networkChannel.Writer, chunkSize);

                    // Background task to push bytes from the channel to the HTTP response body
                    networkSenderTask = Task.Run(async () =>
                    {
                        await foreach (var chunk in networkChannel.Reader.ReadAllAsync(cancellationToken))
                        {
                            try
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                // Write only the valid data length from the rented array
                                await httpContext.Response.Body.WriteAsync(chunk.Buffer.AsMemory(0, chunk.Length), cancellationToken);
                                await httpContext.Response.Body.FlushAsync(cancellationToken);
                            }
                            finally
                            {
                                // CRITICAL ZERO-ALLOCATION REQUIREMENT: 
                                // Always return the network chunk array to the shared pool after it has been sent.
                                ArrayPool<byte>.Shared.Return(chunk.Buffer);
                            }
                        }
                    }, cancellationToken);
                }
                else
                {
                    ctx.TargetStream = new MemoryStream(1024 * 1024); // Pre-allocate 1 MB for non-streaming requests
                }

                // =================================================================
                // ASYNCHRONOUS AUDIO GENERATION (PRODUCER-CONSUMER PATTERN)
                // =================================================================
                // No outer Task.Run here: GenerateAsync is already fully async and
                // non-blocking end to end, so wrapping it in Task.Run would only add
                // an extra ThreadPool hop with no benefit under ASP.NET Core's null
                // SynchronizationContext.
                byte[]? finalAudioBytes = await GenerateAsync(ctx, networkChannel, cancellationToken);

                // Gracefully close the network bridge
                if (ctx.UseStreaming)
                {
                    try { ctx.TargetStream!.Flush(); }
                    catch (ObjectDisposedException) { }
                    networkChannel?.Writer.Complete();
                }

                // =================================================================
                // RESPONSE DISPATCHING
                // =================================================================
                if (ctx.UseStreaming)
                {
                    if (networkSenderTask != null) await networkSenderTask;
                    return Results.Empty;
                }

                httpContext.Response.Headers.Append("X-Audio-Sample-Rate", ctx.DisplaySampleRate.ToString());
                return Results.File(finalAudioBytes ?? [], AudioStreamManager.GetMimeType(request.Format), AudioStreamManager.GetFileName(request.Format));
            }
            finally
            {
                // CRITICAL: Always release the semaphore slot, even if an error occurs, 
                // so the next request in the queue can proceed.
                gpuSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Triggered if the client disconnects/cancels the request midway through generation
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [INFO] Client disconnected. Generation stopped to save resources.");
            Console.ResetColor();
            return Results.Empty;
        }
        catch (Exception ex)
        {
            // Always complete the channel so networkSenderTask doesn't hang indefinitely
            networkChannel?.Writer.TryComplete(ex);

            if (httpContext.Response.HasStarted)
            {
                // If streaming already started, we can't send a 500 status code anymore, just abort gracefully
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] Stream aborted unexpectedly: {ex.Message}");
                Console.ResetColor();
                // Wait for the sender task to finish cleanly before releasing resources
                if (networkSenderTask != null)
                {
                    try { await networkSenderTask; } catch { /* expected cancellation */ }
                }
                return Results.Empty;
            }
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }

    // Bundles everything GenerateAsync needs so the producer/consumer pipeline
    // below doesn't have to be threaded through a dozen separate parameters.
    private sealed class RequestContext
    {
        public required SynthesisRequest Request;
        public required PiperConfig PiperConfig;
        public required StreamSettings StreamConfig;
        public required ClonerSettings ClonerConfig;
        public required DspSettings DspConfig;
        public required EffectsSettings EffectsConfig;
        public required ChunkerSettings ChunkerConfig;
        public required TextChunker TextChunker;
        public required UnifiedPhonemizer Phonemizer;
        public required PiperRunner PiperRunner;
        public required bool UseStreaming;
        public required bool CanClone;
        public required OpenVoiceRunner? OpenVoice;
        public required AudioProcessor? AudioProc;
        public required int OutSampleRate;
        public required int FinalSampleRate;
        public required int DisplaySampleRate;
        public Stream? TargetStream;

        // Convenience passthrough so GenerateAsync doesn't need to write ctx.Request.Format
        // everywhere — mirrors how the pre-migration code carried Format alongside Request.
        public AudioFormat Format => Request.Format;
    }

    private static RequestContext BuildRequestContext(
        SynthesisRequest request, IServiceProvider services, PiperConfig piperConfig)
    {
        var streamConfig = services.GetRequiredService<StreamSettings>();
        var clonerConfig = services.GetRequiredService<ClonerSettings>();

        bool shouldStream = request.Stream ?? streamConfig.EnableStreaming;
        // WAV format requires the total file size to be written in its header upfront.
        // Therefore, true chunked streaming is conceptually impossible for WAV.
        bool useStreaming = shouldStream && request.Format != AudioFormat.Wav;

        bool useOpenVoice = !string.IsNullOrEmpty(request.Voice) &&
            !string.Equals(request.Voice, "piper_base", StringComparison.OrdinalIgnoreCase);

        var openVoice = services.GetService<OpenVoiceRunner>();
        var audioProc = services.GetService<AudioProcessor>();

        // Global toggle for Voice Cloning. Ensures all prerequisites (config enabled, 
        // target voice requested, and models loaded) are met before activating the heavy cloner.
        bool canClone = clonerConfig.EnableCloning && useOpenVoice && openVoice != null && audioProc != null;

        int outSampleRate = canClone ? openVoice!.GetTargetSamplingRate() : piperConfig.Audio.SampleRate;
        int finalSampleRate = outSampleRate;

        if (request.Format == AudioFormat.Opus)
        {
            // Ogg Opus strictly requires specific sample rates (e.g., 24kHz, 48kHz)
            int[] validOpusRates = [8000, 12000, 16000, 24000, 48000];
            finalSampleRate = validOpusRates.OrderBy(r => Math.Abs(r - outSampleRate)).First();
        }

        int displaySampleRate = request.Format == AudioFormat.Opus ? 48000 : finalSampleRate;

        return new RequestContext
        {
            Request = request,
            PiperConfig = piperConfig,
            StreamConfig = streamConfig,
            ClonerConfig = clonerConfig,
            DspConfig = services.GetRequiredService<DspSettings>(),
            EffectsConfig = services.GetRequiredService<EffectsSettings>(),
            ChunkerConfig = services.GetRequiredService<ChunkerSettings>(),
            TextChunker = services.GetRequiredService<TextChunker>(),
            Phonemizer = services.GetRequiredService<UnifiedPhonemizer>(),
            PiperRunner = services.GetRequiredService<PiperRunner>(),
            UseStreaming = useStreaming,
            CanClone = canClone,
            OpenVoice = openVoice,
            AudioProc = audioProc,
            OutSampleRate = outSampleRate,
            FinalSampleRate = finalSampleRate,
            DisplaySampleRate = displaySampleRate
        };
    }

    // Runs the full producer/consumer generation pipeline. Returns the complete
    // in-memory audio bytes for non-streaming requests, or null for streaming
    // requests (where bytes are pushed to networkChannel as they're produced).
    private static async Task<byte[]?> GenerateAsync(
        RequestContext ctx,
        System.Threading.Channels.Channel<(byte[] Buffer, int Length)>? networkChannel,
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

        using var pitchShifter = new PitchShifter(ctx.PiperConfig.Audio.SampleRate);
        if (usePitchShift)
        {
            pitchShifter.SetPitch(targetPitch);
        }

        // Volume Priority: explicit request value → server default from config → fallback 1.0 (no change)
        float targetVolume = request.Volume ?? ctx.DspConfig.DefaultVolume;
        bool useVolumeShift = Math.Abs(targetVolume - 1.0f) > 0.001f;
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

            filter = NAudio.Dsp.BiQuadFilter.LowPassFilter(
                ctx.FinalSampleRate, 
                safeCutoff, 
                ctx.DspConfig.LowPassQFactor
            );
        }

        byte[]? finalAudioBytes = null;

        using (var streamManager = new AudioStreamManager(ctx.Format, ctx.FinalSampleRate, ctx.TargetStream!))
        {
            // =================================================================
            // PRE-CALCULATE VOICE BLEND (Zero-Allocation Optimization)
            // =================================================================
            // Calculate the latent space blending strictly once per request,
            // rather than re-calculating it for every audio chunk inside the loop.
            float[]? blendedTarget = null;
            if (ctx.CanClone && targetFingerprint != null && sourceFingerprint != null)
            {
                blendedTarget = new float[targetFingerprint.Length];
                // Clone Intensity Priority: explicit request value → server default from config,
                // same ?? pattern already used for Pitch/Volume above.
                float intensity = request.CloneIntensity ?? ctx.ClonerConfig.CloneIntensity;
                for (int j = 0; j < blendedTarget.Length; j++)
                {
                    blendedTarget[j] = sourceFingerprint[j] + (targetFingerprint[j] - sourceFingerprint[j]) * intensity;
                }
            }

            // Internal channel for passing raw audio chunks between the Generator and the DSP Processor
            var channel = System.Threading.Channels.Channel.CreateBounded<(float[] Buffer, int Length)>(10);

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

                        // Update local state for the next iteration safely
                        previousChunkWasFinished = isFinished;

                        // Generate the base voice using neural network
                        string phonemes = ctx.Phonemizer.GetPhonemes(chunk);

                        // Pass the streaming flags to the generator
                        var rawResult = ctx.PiperRunner.SynthesizeAudioRaw(phonemes, isContinuation, isFinished, request.Speed, request.NoiseScale, request.NoiseW);

                        // Apply volume adjustment if requested
                        if (useVolumeShift)
                        {
                            VolumeShifter.ApplyVolume(rawResult.Buffer.AsSpan(0, rawResult.Length), targetVolume);
                        }

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
                                foreach (var segment in pitchShifter.ProcessChunk(rawResult.Buffer.AsSpan(0, rawResult.Length)))
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
                                foreach (var segment in pitchShifter.Flush())
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
                                await channel.Writer.WriteAsync((accumulatedBuffer, accumulatedLength), cancellationToken);
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
                                await channel.Writer.WriteAsync(rawResult, cancellationToken);
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
                finally
                {
                    // CRITICAL: Always close the channel
                    channel.Writer.Complete();
                }
            }, cancellationToken);

            // CONSUMER: Applies voice cloning, resampling, effects, and pushes to the network stream
            var consumerTask = Task.Run(async () =>
            {
                await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    float[] currentBuffer = chunk.Buffer;
                    int currentLength = chunk.Length;

                    float[]? rentedBuffer1 = null;
                    float[]? rentedBuffer2 = null;
                    float[]? rentedBuffer3 = null;

                    try
                    {
                        if (ctx.CanClone && blendedTarget != null && sourceFingerprint != null)
                        {
                            // OpenVoice requires a specific sample rate (typically 22050 Hz)
                            var r1 = ctx.AudioProc!.Resample(currentBuffer, currentLength, ctx.PiperConfig.Audio.SampleRate, ctx.OutSampleRate);
                            rentedBuffer1 = r1.Buffer;

                            var specChunk = ctx.AudioProc.GetMagnitudeSpectrogram(rentedBuffer1.AsSpan(0, r1.Length));
                            if (specChunk.GetLength(0) > 0)
                            {
                                // Tone Temperature Priority: explicit request value → server default from config,
                                // same ?? pattern already used for Pitch/Volume above.
                                float tau = request.ToneTemperature ?? ctx.ClonerConfig.ToneTemperature;
                                // Apply tone color cloning in the latent space and decode back to audio. 
                                // This is the most computationally expensive step, so we do it strictly 
                                // once per sentence rather than per smaller chunk to optimize performance.
                                var rClone = ctx.OpenVoice!.ApplyToneColor(specChunk, sourceFingerprint, blendedTarget, tau);

                                rentedBuffer3 = rClone.Buffer;
                                currentBuffer = rentedBuffer3;
                                currentLength = rClone.Length;
                            }
                            else
                            {
                                currentBuffer = rentedBuffer1;
                                currentLength = r1.Length;
                            }
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

                        if (ctx.UseStreaming && ctx.StreamConfig.FlushAfterEachSentence)
                        {
                            ctx.TargetStream!.Flush();
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
            }, cancellationToken);

            await Task.WhenAll(producerTask, consumerTask);

            // If not streaming, grab the complete audio file from memory once generation is done
            if (!ctx.UseStreaming)
            {
                finalAudioBytes = streamManager.GetFinalAudioBytes();
            }
        }

        return finalAudioBytes;
    }
}
