using System.Buffers;
using Microsoft.AspNetCore.Mvc;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

namespace ONNX_Runner.Endpoints;

public static class SpeechEndpoint
{
    // Глобальний набір дозволених форматів
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
        // --- Request Validation ---
        if (string.IsNullOrWhiteSpace(request.Input))
            return Results.BadRequest(new { error = "Input text cannot be empty." });

        // Безпечно перевіряємо, чи завантажилася базова модель
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
            // --- Concurrency Control (Semaphore Pattern) ---
            await gpuSemaphore.WaitAsync(cancellationToken);

            try
            {
                // Отримуємо всі необхідні сервіси та конфіги з DI контейнера
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

                // WAV requires a file size in its header, so chunked streaming is conceptually impossible.
                bool useStreaming = shouldStream && !isWav;

                bool useOpenVoice = !string.IsNullOrEmpty(request.Voice);
                var openVoice = services.GetService<OpenVoiceRunner>();
                var audioProc = services.GetService<AudioProcessor>();

                // Глобальний рубильник EnableCloning.
                bool canClone = clonerConfig.EnableCloning && useOpenVoice && openVoice != null && audioProc != null;

                // Determine target sample rates
                int outSampleRate = canClone ? openVoice!.GetTargetSamplingRate() : piperConfig.Audio.SampleRate;
                int finalSampleRate = outSampleRate;
                bool isOpus = request.ResponseFormat.Equals("opus", StringComparison.OrdinalIgnoreCase);

                if (isOpus)
                {
                    // Ogg Opus strictly requires specific sample rates
                    int[] validOpusRates = [8000, 12000, 16000, 24000, 48000];
                    finalSampleRate = validOpusRates.OrderBy(r => Math.Abs(r - outSampleRate)).First();
                }

                int displaySampleRate = isOpus ? 48000 : finalSampleRate;

                // --- Network Gateway Setup (Bridging & Backpressure) ---
                Stream targetStream;
                System.Threading.Channels.Channel<byte[]>? networkChannel = null;
                Task? networkSenderTask = null;

                if (useStreaming)
                {
                    httpContext.Response.ContentType = AudioStreamManager.GetMimeType(request);
                    httpContext.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{AudioStreamManager.GetFileName(request)}\"");
                    httpContext.Response.Headers.Append("X-Audio-Sample-Rate", displaySampleRate.ToString());
                    await httpContext.Response.StartAsync(cancellationToken);

                    var channelOptions = new System.Threading.Channels.BoundedChannelOptions(50)
                    {
                        FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait
                    };
                    networkChannel = System.Threading.Channels.Channel.CreateBounded<byte[]>(channelOptions);

                    int chunkSize = streamConfig.MinChunkSizeKb * 1024;
                    targetStream = new BridgingStream(networkChannel.Writer, chunkSize);

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
                    targetStream = new MemoryStream(1024 * 1024); // Pre-allocate 1 MB
                }

                // --- Asynchronous Audio Generation (Producer-Consumer Pattern) ---
                byte[]? finalAudioBytes = null;

                await Task.Run(async () =>
                {
                    var textChunks = textChunker.Split(request.Input);

                    float[]? targetFingerprint = null;
                    float[]? sourceFingerprint = null;
                    if (canClone)
                    {
                        openVoice!.VoiceLibrary.TryGetValue(request.Voice, out targetFingerprint);
                        openVoice.VoiceLibrary.TryGetValue("piper_base", out sourceFingerprint);
                    }

                    var effectsEngine = new AudioEffectsEngine(effectsConfig, finalSampleRate);

                    float currentSpeed = (request.Speed > 0.1f) ? request.Speed : 1.0f;
                    int silenceSamplesCount = (int)(finalSampleRate * (chunkerConfig.SentencePauseSeconds / currentSpeed));
                    float[] absoluteSilence = new float[silenceSamplesCount];

                    NAudio.Dsp.BiQuadFilter? filter = null;
                    if (canClone && dspConfig.EnableLowPassFilter)
                        filter = NAudio.Dsp.BiQuadFilter.LowPassFilter(finalSampleRate, dspConfig.LowPassCutoffFrequency, dspConfig.LowPassQFactor);

                    using (var streamManager = new AudioStreamManager(request, finalSampleRate, targetStream))
                    {
                        var channel = System.Threading.Channels.Channel.CreateBounded<(float[] Buffer, int Length)>(10);

                        // PRODUCER: Generates audio
                        var producerTask = Task.Run(async () =>
                        {
                            foreach (var chunk in textChunks)
                            {
                                cancellationToken.ThrowIfCancellationRequested();

                                string phonemes = unifiedPhonemizer.GetPhonemes(chunk);
                                var rawResult = piperRunner.SynthesizeAudioRaw(phonemes, request.Speed, request.NoiseScale, request.NoiseW);

                                await channel.Writer.WriteAsync(rawResult, cancellationToken);
                            }
                            channel.Writer.Complete();
                        }, cancellationToken);

                        // CONSUMER: Processes audio
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
                                    if (canClone && targetFingerprint != null && sourceFingerprint != null)
                                    {
                                        float intensity = clonerConfig.CloneIntensity;
                                        float[] blendedTarget = new float[targetFingerprint.Length];

                                        for (int j = 0; j < blendedTarget.Length; j++)
                                        {
                                            blendedTarget[j] = sourceFingerprint[j] + (targetFingerprint[j] - sourceFingerprint[j]) * intensity;
                                        }

                                        var r1 = audioProc!.Resample(currentBuffer, currentLength, piperConfig!.Audio.SampleRate, outSampleRate);
                                        rentedBuffer1 = r1.Buffer;

                                        var specChunk = audioProc.GetMagnitudeSpectrogram(rentedBuffer1.AsSpan(0, r1.Length));
                                        if (specChunk.GetLength(0) > 0)
                                        {
                                            float tau = clonerConfig.ToneTemperature;
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

                                    if (outSampleRate != finalSampleRate)
                                    {
                                        var r2 = audioProc!.Resample(currentBuffer, currentLength, outSampleRate, finalSampleRate);
                                        rentedBuffer2 = r2.Buffer;
                                        currentBuffer = rentedBuffer2;
                                        currentLength = r2.Length;
                                    }

                                    // Apply Effects
                                    effectsEngine.ApplyEffect(currentBuffer.AsSpan(0, currentLength), request.Effect, request.EffectIntensity);
                                    streamManager.WriteChunk(currentBuffer.AsSpan(0, currentLength), filter);

                                    // Pause (Silence)
                                    Array.Clear(absoluteSilence, 0, absoluteSilence.Length);
                                    effectsEngine.ApplyEffect(absoluteSilence.AsSpan(), request.Effect, request.EffectIntensity);
                                    streamManager.WriteChunk(absoluteSilence.AsSpan(), filter);

                                    if (useStreaming && streamConfig.FlushAfterEachSentence)
                                    {
                                        targetStream.Flush();
                                    }
                                }
                                finally
                                {
                                    ArrayPool<float>.Shared.Return(chunk.Buffer);
                                    if (rentedBuffer1 != null) ArrayPool<float>.Shared.Return(rentedBuffer1);
                                    if (rentedBuffer2 != null) ArrayPool<float>.Shared.Return(rentedBuffer2);
                                    if (rentedBuffer3 != null) ArrayPool<float>.Shared.Return(rentedBuffer3);
                                }
                            }
                        }, cancellationToken);

                        await Task.WhenAll(producerTask, consumerTask);

                        if (!useStreaming)
                        {
                            finalAudioBytes = streamManager.GetFinalAudioBytes();
                        }
                    }

                    if (useStreaming)
                    {
                        try { targetStream.Flush(); }
                        catch (ObjectDisposedException) { }
                        networkChannel?.Writer.Complete();
                    }
                }, cancellationToken);

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
                gpuSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [INFO] Client disconnected. Generation stopped to save resources.");
            Console.ResetColor();
            return Results.Empty;
        }
        catch (Exception ex)
        {
            if (httpContext.Response.HasStarted)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ERROR] Stream aborted unexpectedly: {ex.Message}");
                Console.ResetColor();
                return Results.Empty;
            }
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }
}