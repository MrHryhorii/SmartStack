using System.Reflection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CassetteIntensityChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly float[] Amounts = [.00101f, .01f, .1f, .25f, .5f, .75f, 1f];
    private static readonly MethodInfo Prepare = typeof(AudioEffectsEngine)
        .GetMethod("PrepareTape", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Process = typeof(AudioEffectsEngine)
        .GetMethod("LoFiTape", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Checks the cassette core itself at zero, intensity continuity, streaming, and allocations.</summary>
    internal static void Run(Action<string, Action> check)
    {
        // Independently replayed from the frozen cassette prototype with the agreed enclosure
        // resonance and output trim: 22050 Hz, seed 123456, eight seconds of silence,
        // excluding one second of startup. The prior noise-floor contract changed with playback level.
        double[] referenceNoiseRms =
        [
            7.015065512324314e-07,
            7.003428268894397e-06,
            7.543374613541095e-05,
            0.0002039638957824946,
            0.0004055186635797206,
            0.0005394837717260337,
            0.0006715353285700821
        ];
        for (int index = 0; index < Amounts.Length; index++)
        {
            float amount = Amounts[index];
            double expected = referenceNoiseRms[index];
            check($"Cassette output noise retains its retuned playback level at {amount:0.#####} intensity", () =>
            {
                float[] samples = Noise(22050, amount);
                double power = 0;
                for (int i = 22050; i < samples.Length; i++) power += (double)samples[i] * samples[i];
                double actual = Math.Sqrt(power / (samples.Length - 22050));
                Require(Math.Abs(20 * Math.Log10(actual / expected)) < .05,
                    "Cassette playback noise changed by more than 0.05 dB from the independent reference.");
            });
        }

        check("Cassette reference noise has a moderate upper-band balance", () =>
        {
            float[] samples = Noise(22050, 1);
            double middle = BandPower(samples, 22050, 3000, 6000);
            double upper = BandPower(samples, 22050, 6000, 10000);
            double balanceDb = 10 * Math.Log10(middle / upper);
            // The agreed small treble shift targets 1.5-2.5 dB, rather than the old ~3 dB.
            Require(balanceDb is > 1.5 and < 2.5,
                "The 3-6 kHz versus 6-10 kHz noise balance is outside the agreed range.");
            double totalPower = 0;
            for (int i = 22050; i < samples.Length; i++) totalPower += (double)samples[i] * samples[i];
            totalPower /= samples.Length - 22050;
            // Lowering the relative band ratio must also reduce its share of total noise power.
            Require(middle / totalPower is > .35 and < .45,
                "The mid-band became more prominent despite the upper-band shift.");
        });

        foreach (int rate in Rates)
        {
            foreach (bool warmed in new[] { false, true })
            {
                check($"Cassette internal zero preserves clean PCM at {rate} Hz, warmed={warmed}", () =>
                {
                    var engine = NewEngine(rate);
                    Initialize(engine, rate, warmed);
                    var prepare = Prepare.CreateDelegate<Action<float>>(engine);
                    var process = Process.CreateDelegate<Func<float, float, float>>(engine);
                    prepare(0);
                    float[] input = Signal(rate, rate / 4);
                    foreach (float value in input)
                        Require(Math.Abs((double)process(value, 0) - value) <= 1e-15,
                            "The internal zero altered clean PCM without using the public bypass guard.");
                });

                check($"Cassette internal zero leaves no noise or tail at {rate} Hz, warmed={warmed}", () =>
                {
                    var engine = NewEngine(rate);
                    Initialize(engine, rate, warmed);
                    Prepare.CreateDelegate<Action<float>>(engine)(0);
                    var process = Process.CreateDelegate<Func<float, float, float>>(engine);
                    for (int i = 0; i < rate / 4; i++)
                        Require(process(0, 0) == 0,
                            "Warm delay, noise, or DC-filter history survived internal zero.");
                });
            }

            check($"Cassette core approaches clean PCM continuously at {rate} Hz", () =>
            {
                float[] input = Signal(rate, rate / 4);
                double firstError = CoreError(rate, input, .01f);
                double lastError = CoreError(rate, input, 1e-8f);
                Require(firstError > 1e-4, "The cassette stopped affecting an active input.");
                Require(lastError < 1e-5 && lastError < firstError / 100,
                    "Residual processing did not approach the clean signal as intensity decreased.");
            });

            check($"Cassette intensity retains its transport frequencies at {rate} Hz", () =>
            {
                var quiet = NewEngine(rate);
                var worn = NewEngine(rate);
                quiet.ApplyEffect(new float[rate / 4], VoiceEffectType.LoFiTape, .01f);
                worn.ApplyEffect(new float[rate / 4], VoiceEffectType.LoFiTape, 1f);
                foreach (string name in new[] { "LofiPhase", "LofiPhase2", "FlutterPhase" })
                    Require(TapeField(quiet, name).Equals(TapeField(worn, name)),
                        "Intensity changed oscillator speed instead of modulation depth.");
            });

            check($"Cassette intensity changes preserve streaming at {rate} Hz", () =>
            {
                float[] source = Signal(rate, rate / 4);
                var whole = NewEngine(rate);
                var split = NewEngine(rate);
                foreach (float amount in Amounts)
                {
                    float[] expected = (float[])source.Clone();
                    float[] actual = (float[])source.Clone();
                    whole.ApplyEffect(expected, VoiceEffectType.LoFiTape, amount);
                    for (int offset = 0; offset < actual.Length; offset += 257)
                        split.ApplyEffect(actual.AsSpan(offset, Math.Min(257, actual.Length - offset)),
                            VoiceEffectType.LoFiTape, amount);
                    Require(expected.AsSpan().SequenceEqual(actual), "Block boundaries changed a cassette transition.");
                    Require(actual.All(value => float.IsFinite(value) && Math.Abs(value) < 2),
                        "A cassette transition produced invalid or excessive PCM.");
                }
            });

            foreach (float amount in Amounts)
            {
                check($"Cassette processing at {amount:0.#####} intensity allocates no managed bytes at {rate} Hz", () =>
                {
                    var engine = NewEngine(rate);
                    float[] buffer = Signal(rate, 257);
                    for (int i = 0; i < 128; i++) engine.ApplyEffect(buffer, VoiceEffectType.LoFiTape, amount);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 128; i++) engine.ApplyEffect(buffer, VoiceEffectType.LoFiTape, amount);
                    Require(GC.GetAllocatedBytesForCurrentThread() == before,
                        "Intensity preparation or sample processing allocated managed memory.");
                });
            }
        }
    }

    /// <summary>Renders deterministic silence through the complete cassette playback path.</summary>
    private static float[] Noise(int rate, float amount)
    {
        float[] samples = new float[rate * 8];
        NewEngine(rate).ApplyEffect(samples, VoiceEffectType.LoFiTape, amount);
        return samples;
    }

    /// <summary>Measures band power with Hann-windowed Goertzel bins, independently of production filters.</summary>
    private static double BandPower(float[] samples, int rate, int lowHz, int highHz)
    {
        const int size = 1024;
        int blocks = (samples.Length - rate) / size;
        double[] window = new double[size];
        double windowPower = 0;
        for (int i = 0; i < size; i++)
        {
            window[i] = .5 - .5 * Math.Cos(2 * Math.PI * i / (size - 1));
            windowPower += window[i] * window[i];
        }
        double[] windowed = new double[blocks * size];
        for (int block = 0; block < blocks; block++)
            for (int i = 0; i < size; i++)
                windowed[block * size + i] = samples[rate + block * size + i] * window[i];

        int first = (int)Math.Ceiling(lowHz * (double)size / rate);
        int end = (int)Math.Ceiling(highHz * (double)size / rate);
        double power = 0;
        for (int bin = first; bin < end; bin++)
        {
            double coefficient = 2 * Math.Cos(2 * Math.PI * bin / size);
            for (int block = 0; block < blocks; block++)
            {
                double previous = 0, beforePrevious = 0;
                for (int i = 0; i < size; i++)
                {
                    double current = windowed[block * size + i] + coefficient * previous - beforePrevious;
                    beforePrevious = previous;
                    previous = current;
                }
                power += previous * previous + beforePrevious * beforePrevious
                    - coefficient * previous * beforePrevious;
            }
        }
        return 2 * power / (blocks * size * windowPower);
    }

    /// <summary>Measures relative PCM error through the real core, without the public amount guard.</summary>
    private static double CoreError(int rate, float[] input, float amount)
    {
        var engine = NewEngine(rate);
        Initialize(engine, rate, true);
        Prepare.CreateDelegate<Action<float>>(engine)(amount);
        var process = Process.CreateDelegate<Func<float, float, float>>(engine);
        double error = 0, power = 0;
        foreach (float value in input)
        {
            double difference = process(value, amount) - value;
            error += difference * difference;
            power += (double)value * value;
        }
        return Math.Sqrt(error / power);
    }

    /// <summary>Creates filter state normally, optionally retaining active tape, noise, and DC history.</summary>
    private static void Initialize(AudioEffectsEngine engine, int rate, bool warmed)
    {
        if (!warmed)
        {
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.LoFiTape, 1f);
            return;
        }
        engine.ApplyEffect(Signal(rate, rate / 4), VoiceEffectType.LoFiTape, 1f);
    }

    /// <summary>Reads oscillator phase only; output contracts exercise the real DSP code separately.</summary>
    private static object TapeField(AudioEffectsEngine engine, string name)
    {
        object state = typeof(AudioEffectsEngine).GetField("_tape", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine)!;
        return state.GetType().GetField(name)!.GetValue(state)!;
    }

    /// <summary>Creates reproducible managed DSP state without native synthesis.</summary>
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new EffectsSettings(), rate);
        engine.Reset(123456);
        return engine;
    }

    /// <summary>Combines DC, bass, and high speech-band tones to expose persistent filtering or delay.</summary>
    private static float[] Signal(int rate, int length) => Enumerable.Range(0, length)
        .Select(i => (float)(.035 + .12 * Math.Sin(2 * Math.PI * 60 * i / rate)
            + .10 * Math.Sin(2 * Math.PI * 971 * i / rate)
            + .04 * Math.Sin(2 * Math.PI * 2309 * i / rate))).ToArray();

    /// <summary>Fails a declared behavioral contract rather than substituting a new expected waveform.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
