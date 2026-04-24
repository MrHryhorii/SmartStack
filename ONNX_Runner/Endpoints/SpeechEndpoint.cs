using Microsoft.AspNetCore.Mvc;
using ONNX_Runner.Models;
using ONNX_Runner.Services;
using System.Buffers;

namespace ONNX_Runner.Endpoints;

public static class SpeechEndpoint
{
    // Global set of allowed audio response formats
    private static readonly HashSet<string> _allowedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "wav", "mp3", "opus", "pcm"
    };

    public static async Task<IResult> HandleSpeechRequest(
        HttpContext httpContext,
        [FromBody] OpenAiSpeechRequest request,
        [FromServices] SemaphoreSlim gpuSemaphore,
        [FromServices] IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // =================================================================
        // REQUEST VALIDATION
        // =================================================================
        if (string.IsNullOrWhiteSpace(request.Input))
            return Results.BadRequest(new { error = "Input text cannot be empty." });

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

        if (string.IsNullOrWhiteSpace(request.ResponseFormat) || !_allowedFormats.Contains(request.ResponseFormat))
        {
            return Results.BadRequest(new
            {
                error = $"Unsupported response_format: '{request.ResponseFormat}'. Supported formats are: {string.Join(", ", _allowedFormats)}."
            });
        }

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
                // Retrieve necessary services and configurations from the Dependency Injection (DI) container
                var streamConfig = services.GetRequiredService<StreamSettings>();
                var clonerConfig = services.GetRequiredService<ClonerSettings>();
                var dspConfig = services.GetRequiredService<DspSettings>();
                var effectsConfig = services.GetRequiredService<EffectsSettings>();
                var chunkerConfig = services.GetRequiredService<ChunkerSettings>();

                var textChunker = services.GetRequiredService<TextChunker>();
                var unifiedPhonemizer = services.GetRequiredService<UnifiedPhonemizer>();
                var piperRunner = services.GetRequiredService<PiperRunner>();

                bool shouldStream = request.Stream ?? streamConfig.EnableStreaming;
                bool isWav = request.ResponseFormat.Equals("wav", StringComparison.OrdinalIgnoreCase);

                // WAV format requires the total file size to be written in its header upfront.
                // Therefore, true chunked streaming is conceptually impossible for WAV.
                bool useStreaming = shouldStream && !isWav;

                // Determine if OpenVoice cloning should be applied based on the requested voice and server configuration.
                bool useOpenVoice = !string.IsNullOrEmpty(request.Voice) &&
                    !string.Equals(request.Voice, "piper_base", StringComparison.OrdinalIgnoreCase);

                var openVoice = services.GetService<OpenVoiceRunner>();
                var audioProc = services.GetService<AudioProcessor>();

                // Global toggle for Voice Cloning. Ensures all prerequisites (config enabled, 
                // target voice requested, and models loaded) are met before activating the heavy cloner.
                bool canClone = clonerConfig.EnableCloning && useOpenVoice && openVoice != null && audioProc != null;

                // Determine target sample rates based on the active pipeline
                int outSampleRate = canClone ? openVoice!.GetTargetSamplingRate() : piperConfig.Audio.SampleRate;
                int finalSampleRate = outSampleRate;
                bool isOpus = request.ResponseFormat.Equals("opus", StringComparison.OrdinalIgnoreCase);

                if (isOpus)
                {
                    // Ogg Opus strictly requires specific sample rates (e.g., 24kHz, 48kHz)
                    int[] validOpusRates = [8000, 12000, 16000, 24000, 48000];
                    finalSampleRate = validOpusRates.OrderBy(r => Math.Abs(r - outSampleRate)).First();
                }

                int displaySampleRate = isOpus ? 48000 : finalSampleRate;

                // =================================================================
                // NETWORK GATEWAY SETUP (BRIDGING & BACKPRESSURE)
                // =================================================================
                Stream targetStream;
                System.Threading.Channels.Channel<byte[]>? networkChannel = null;
                Task? networkSenderTask = null;

                if (useStreaming)
                {
                    // Prepare HTTP headers for chunked audio streaming
                    httpContext.Response.ContentType = AudioStreamManager.GetMimeType(request);
                    httpContext.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{AudioStreamManager.GetFileName(request)}\"");
                    httpContext.Response.Headers.Append("X-Audio-Sample-Rate", displaySampleRate.ToString());
                    await httpContext.Response.StartAsync(cancellationToken);

                    // Create a bounded channel for backpressure. If the client downloads slowly,
                    // generation pauses until the client catches up, saving server RAM.
                    var channelOptions = new System.Threading.Channels.BoundedChannelOptions(50)
                    {
                        FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
                    };
                    networkChannel = System.Threading.Channels.Channel.CreateBounded<byte[]>(channelOptions);

                    int chunkSize = streamConfig.MinChunkSizeKb * 1024;
                    targetStream = new BridgingStream(networkChannel.Writer, chunkSize);

                    // Background task to push bytes from the channel to the HTTP response body
                    networkSenderTask = Task.Run(async () =>
                    {
                        await foreach (var chunk in networkChannel.Reader.ReadAllAsync(cancellationToken))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await httpContext.Response.Body.WriteAsync(chunk, cancellationToken);
                            await httpContext.Response.Body.FlushAsync(cancellationToken);
                        }
                    }, cancellationToken);
                }
                else
                {
                    targetStream = new MemoryStream(1024 * 1024); // Pre-allocate 1 MB for non-streaming requests
                }

                // =================================================================
                // ASYNCHRONOUS AUDIO GENERATION (PRODUCER-CONSUMER PATTERN)
                // =================================================================
                byte[]? finalAudioBytes = null;

                await Task.Run(async () =>
                {
                    var textChunks = textChunker.Split(request.Input);

                    float[]? targetFingerprint = null;
                    float[]? sourceFingerprint = null;

                    // Fetch pre-computed tone embeddings from the Voice Library if cloning is active
                    if (canClone)
                    {
                        openVoice!.VoiceLibrary.TryGetValue(request.Voice, out targetFingerprint);
                        openVoice.VoiceLibrary.TryGetValue("piper_base", out sourceFingerprint);
                    }

                    // Initialize audio processing engines with the final sample rate determined for this request
                    var effectsEngine = new AudioEffectsEngine(effectsConfig, finalSampleRate);
                    var spatialEngine = new SpatialEffectsEngine(finalSampleRate);

                    // =================================================================
                    // DSP MODIFIERS SETUP (PITCH & VOLUME)
                    // =================================================================
                    // Pitch
                    float targetPitch = request.Pitch.GetValueOrDefault(1.0f);
                    bool usePitchShift = Math.Abs(targetPitch - 1.0f) > 0.001f;

                    using var pitchShifter = new PitchShifter(piperConfig!.Audio.SampleRate);
                    if (usePitchShift)
                    {
                        pitchShifter.SetPitch(targetPitch);
                    }

                    // Volume
                    float targetVolume = request.Volume.GetValueOrDefault(1.0f);
                    bool useVolumeShift = Math.Abs(targetVolume - 1.0f) > 0.001f;
                    // =================================================================

                    // Determine target effect settings, falling back to global defaults if not specified in the request
                    string targetEnvironment = request.Environment ?? effectsConfig.DefaultEnvironment;
                    float targetEnvIntensity = request.EnvironmentIntensity ?? effectsConfig.DefaultEnvironmentIntensity;

                    float currentSpeed = (request.Speed > 0.1f) ? request.Speed : 1.0f;
                    int silenceSamplesCount = (int)(finalSampleRate * (chunkerConfig.SentencePauseSeconds / currentSpeed));
                    float[] absoluteSilence = new float[silenceSamplesCount];

                    // Optional anti-aliasing low-pass filter to clean up cloning artifacts
                    NAudio.Dsp.BiQuadFilter? filter = null;
                    if (canClone && dspConfig.EnableLowPassFilter)
                        filter = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, dspConfig.LowPassCutoffFrequency, dspConfig.LowPassQFactor);

                    using (var streamManager = new AudioStreamManager(request, finalSampleRate, targetStream))
                    {
                        // =================================================================
                        // PRE-CALCULATE VOICE BLEND (Zero-Allocation Optimization)
                        // =================================================================
                        // Calculate the latent space blending strictly once per request,
                        // rather than re-calculating it for every audio chunk inside the loop.
                        float[]? blendedTarget = null;
                        if (canClone && targetFingerprint != null && sourceFingerprint != null)
                        {
                            blendedTarget = new float[targetFingerprint.Length];
                            float intensity = clonerConfig.CloneIntensity;
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
                                foreach (var chunk in textChunks)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    // Generate the base voice using neural network
                                    string phonemes = unifiedPhonemizer.GetPhonemes(chunk);
                                    var rawResult = piperRunner.SynthesizeAudioRaw(phonemes, request.Speed, request.NoiseScale, request.NoiseW);
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
                                        // Return the original buffer from Piper back to the pool
                                        ArrayPool<float>.Shared.Return(rawResult.Buffer);
                                    }
                                    else
                                    {
                                        // If Pitch is exactly 1.0, bypass DSP and send the original raw audio chunk directly
                                        await channel.Writer.WriteAsync(rawResult, cancellationToken);
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
                                    if (canClone && blendedTarget != null && sourceFingerprint != null)
                                    {
                                        // OpenVoice requires a specific sample rate (typically 22050 Hz)
                                        var r1 = audioProc!.Resample(currentBuffer, currentLength, piperConfig!.Audio.SampleRate, outSampleRate);
                                        rentedBuffer1 = r1.Buffer;

                                        var specChunk = audioProc.GetMagnitudeSpectrogram(rentedBuffer1.AsSpan(0, r1.Length));
                                        if (specChunk.GetLength(0) > 0)
                                        {
                                            float tau = clonerConfig.ToneTemperature;
                                            // Apply tone color cloning in the latent space and decode back to audio. 
                                            // This is the most computationally expensive step, so we do it strictly 
                                            // once per sentence rather than per smaller chunk to optimize performance.
                                            var rClone = openVoice!.ApplyToneColor(specChunk, sourceFingerprint, blendedTarget, tau);

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
                                    if (outSampleRate != finalSampleRate)
                                    {
                                        var r2 = audioProc!.Resample(currentBuffer, currentLength, outSampleRate, finalSampleRate);
                                        rentedBuffer2 = r2.Buffer;
                                        currentBuffer = rentedBuffer2;
                                        currentLength = r2.Length;
                                    }

                                    // Apply character DSP effects (Telephone, Cassette, etc.)
                                    effectsEngine.ApplyEffect(currentBuffer.AsSpan(0, currentLength), request.Effect, request.EffectIntensity);
                                    // Apply spatial acoustics AFTER character effects
                                    spatialEngine.ApplyEnvironment(currentBuffer.AsSpan(0, currentLength), targetEnvironment, targetEnvIntensity);
                                    streamManager.WriteChunk(currentBuffer.AsSpan(0, currentLength), filter);

                                    // Append a brief pause (silence) between sentences for natural pacing
                                    Array.Clear(absoluteSilence, 0, absoluteSilence.Length);

                                    // Apply character effects to silence (e.g. tape hiss continues during pauses)
                                    effectsEngine.ApplyEffect(absoluteSilence.AsSpan(), request.Effect, request.EffectIntensity);
                                    // Apply spatial acoustics to silence so reverb tails ring out naturally
                                    spatialEngine.ApplyEnvironment(absoluteSilence.AsSpan(), targetEnvironment, targetEnvIntensity);
                                    streamManager.WriteChunk(absoluteSilence.AsSpan(), filter);

                                    if (useStreaming && streamConfig.FlushAfterEachSentence)
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
                        }, cancellationToken);

                        await Task.WhenAll(producerTask, consumerTask);

                        // If not streaming, grab the complete audio file from memory once generation is done
                        if (!useStreaming)
                        {
                            finalAudioBytes = streamManager.GetFinalAudioBytes();
                        }
                    }

                    // Gracefully close the network bridge
                    if (useStreaming)
                    {
                        try { targetStream.Flush(); }
                        catch (ObjectDisposedException) { }
                        networkChannel?.Writer.Complete();
                    }
                }, cancellationToken);

                // =================================================================
                // RESPONSE DISPATCHING
                // =================================================================
                if (useStreaming)
                {
                    if (networkSenderTask != null) await networkSenderTask;
                    return Results.Empty;
                }
                else
                {
                    httpContext.Response.Headers.Append("X-Audio-Sample-Rate", displaySampleRate.ToString());
                    return Results.File(finalAudioBytes!, AudioStreamManager.GetMimeType(request), AudioStreamManager.GetFileName(request));
                }
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
            if (httpContext.Response.HasStarted)
            {
                // If streaming already started, we can't send a 500 status code anymore, just abort gracefully
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] Stream aborted unexpectedly: {ex.Message}");
                Console.ResetColor();
                return Results.Empty;
            }
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }
}