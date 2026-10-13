using System.Reflection;
using System.Security.Cryptography;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class AudioEffectsChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly FieldInfo GlitchField = typeof(AudioEffectsEngine)
        .GetField("_glitch", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo CaptureField = typeof(AudioEffectsEngine)
        .GetField("_glitchCapture", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Checks codec tables and the authored rolling-glitch timing without assessing subjective sound.</summary>
    internal static void Run(Action<string, Action> check)
    {
        CheckCodec(check, "ALaw",
            "38488F6FD710F4686360EDC4D38639F96C491595EF93F8EB8D62D5E07CA6CE7B",
            "E04788D110E58FF8C70C93B8480190D973E3B67876B6119ABBAEC766CC75C174");
        CheckCodec(check, "MuLaw",
            "90C29DE505FB68E766118303BD552A16005DCF810873698BEE1D8F3B247CE28C",
            "3DAB54339E520BB2C924826E3B72A917A2B612E9FD12FC867500F1D983A75827");

        check("Mu-law negative boundaries use one's-complement magnitude", () =>
        {
            var encode = Codec<short, byte>("EncodeMuLaw");
            for (int value = 0; value <= short.MaxValue; value++)
                Require(encode((short)(-value - 1)) == (encode((short)value) ^ 0x80),
                    $"Negative decision boundary differs at {value}.");
        });

        foreach (int rate in Rates)
        {
            check($"Rolling glitch retains the reference capture duration at {rate} Hz", () =>
            {
                var engine = NewEngine(rate);
                int length = ((float[])CaptureField.GetValue(engine)!).Length;
                Require(Math.Abs((double)length / rate - 4096.0 / 22050) <= .5 / rate + 1e-12,
                    "Capture capacity changed the physical replacement interval.");
            });
            check($"Rolling glitch keeps live replacement near 153 ms at {rate} Hz", () =>
                CheckRollingReplacement(rate));
            foreach (int frequency in new[] { 600, 2000 })
            {
                check($"Glitch detector treats {frequency} Hz consistently at {rate} Hz", () =>
                {
                    var engine = NewEngine(rate);
                    float[] warm = Tone(rate, frequency, .01f, rate / 2);
                    engine.ApplyEffect(warm, VoiceEffectType.DecoderGlitch, 1);
                    // Fix only the random roll; energy and ZCR remain real detector inputs.
                    SetState(engine, "TriggerAccumulator", 1f);
                    float[] input = Tone(rate, frequency, .25f, rate, rate / 2);
                    float[] output = (float[])input.Clone();
                    engine.ApplyEffect(output, VoiceEffectType.DecoderGlitch, 1);
                    Require(output.All(float.IsFinite), "Glitch output is not finite.");
                    Require(output.AsSpan().SequenceEqual(input) == (frequency == 2000),
                        "The detector changed its low/high-frequency preference with sample rate.");
                });
            }
            check($"Glitch streaming is independent of buffer partitions at {rate} Hz", () =>
            {
                float[] input = Tone(rate, 200, .25f, rate * 2);
                float[] whole = (float[])input.Clone();
                float[] split = (float[])input.Clone();
                NewEngine(rate).ApplyEffect(whole, VoiceEffectType.DecoderGlitch, 1);
                var engine = NewEngine(rate);
                for (int offset = 0; offset < split.Length; offset += 257)
                    engine.ApplyEffect(split.AsSpan(offset, Math.Min(257, split.Length - offset)),
                        VoiceEffectType.DecoderGlitch, 1);
                Require(whole.AsSpan().SequenceEqual(split), "Buffer boundaries changed rolling playback.");
            });
            check($"Glitch sample processing allocates no managed bytes at {rate} Hz", () =>
            {
                var engine = NewEngine(rate);
                float[] buffer = Tone(rate, 200, .25f, 257);
                for (int i = 0; i < 256; i++) engine.ApplyEffect(buffer, VoiceEffectType.DecoderGlitch, 1);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 256; i++) engine.ApplyEffect(buffer, VoiceEffectType.DecoderGlitch, 1);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(allocated == 0, $"Hot processing allocated {allocated} bytes.");
            });
        }
    }

    /// <summary>Compares every PCM16 input and every decoded codeword with independent ITU reference digests.</summary>
    private static void CheckCodec(Action<string, Action> check, string name, string encodeHash, string decodeHash)
    {
        // Fixture hashes come from ITU-T G.191 g711.c v3.01, not from this engine or NAudio.
        // https://github.com/openitu/STL/tree/master/src/g711
        check($"G.711 {name} encoding matches all 65536 ITU reference inputs", () =>
        {
            var encode = Codec<short, byte>("Encode" + name);
            byte[] encoded = new byte[65536];
            for (int i = 0; i < encoded.Length; i++) encoded[i] = encode((short)(i + short.MinValue));
            Require(Convert.ToHexString(SHA256.HashData(encoded)) == encodeHash,
                "Encoded table differs from the independent ITU reference.");
        });
        check($"G.711 {name} decoding matches all 256 ITU reference codewords", () =>
        {
            var decode = Codec<byte, short>("Decode" + name);
            byte[] decoded = new byte[512];
            for (int i = 0; i < 256; i++)
            {
                short sample = decode((byte)i);
                decoded[i * 2] = (byte)(sample & 255);
                decoded[i * 2 + 1] = (byte)((sample >> 8) & 255);
            }
            Require(Convert.ToHexString(SHA256.HashData(decoded)) == decodeHash,
                "Decoded table differs from the independent ITU reference.");
        });
    }

    /// <summary>Measures when live recording first replaces a selected loop while the stutter remains active.</summary>
    private static void CheckRollingReplacement(int rate)
    {
        var engine = NewEngine(rate);
        engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.DecoderGlitch, 1);
        SetState(engine, "TriggerAccumulator", 1f);
        var process = typeof(AudioEffectsEngine).GetMethod("DecoderGlitch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<float, float, float>>(engine);
        float[] capture = (float[])CaptureField.GetValue(engine)!;
        int start = -1, loopStart = 0, loopLength = 0;
        float[]? snapshot = null;
        for (int i = 0; i < rate; i++)
        {
            process(.25f * MathF.Sin(2 * MathF.PI * 200 * i / rate), 1);
            object state = GlitchField.GetValue(engine)!;
            bool frozen = State<bool>(state, "Frozen");
            if (start < 0 && frozen)
            {
                start = i;
                loopStart = State<int>(state, "LoopStart");
                loopLength = State<int>(state, "LoopLen");
                snapshot = Enumerable.Range(0, loopLength).Select(j => capture[(loopStart + j) % capture.Length]).ToArray();
            }
            if (snapshot is null) continue;
            Require(frozen, "The active loop became a fixed snapshot until release.");
            for (int j = 0; j < loopLength; j++)
            {
                if (capture[(loopStart + j) % capture.Length] == snapshot[j]) continue;
                double elapsed = (double)(i - start) / rate;
                Require(Math.Abs(elapsed - (4096.0 / 22050 - .033)) <= 3.0 / rate,
                    "Live replacement timing differs from the 22050 Hz character.");
                return;
            }
        }
        throw new InvalidOperationException("No live replacement occurred during the active loop.");
    }

    /// <summary>Creates deterministic per-request DSP state.</summary>
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new EffectsSettings(), rate);
        engine.Reset(123456);
        return engine;
    }

    /// <summary>Creates a phase-continuous tone without native speech synthesis.</summary>
    private static float[] Tone(int rate, int frequency, float amplitude, int count, int offset = 0) =>
        Enumerable.Range(0, count).Select(i => amplitude * MathF.Sin(2 * MathF.PI * frequency * (i + offset) / rate)).ToArray();

    /// <summary>Reads one diagnostic field from the private effect-state snapshot.</summary>
    private static T State<T>(object state, string name) => (T)state.GetType().GetField(name)!.GetValue(state)!;

    /// <summary>Controls the random-roll accumulator without bypassing the real audio detector.</summary>
    private static void SetState(AudioEffectsEngine engine, string name, float value)
    {
        object state = GlitchField.GetValue(engine)!;
        state.GetType().GetField(name)!.SetValue(state, value);
        GlitchField.SetValue(engine, state);
    }

    /// <summary>Binds the actual production codec method for exhaustive fixture checks.</summary>
    private static Func<TInput, TOutput> Codec<TInput, TOutput>(string name) =>
        typeof(AudioEffectsEngine).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<TInput, TOutput>>();

    /// <summary>Reports an observable contract violation.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
