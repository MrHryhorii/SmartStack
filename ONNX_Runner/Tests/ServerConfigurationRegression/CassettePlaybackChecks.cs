using System.Reflection;
using NAudio.Dsp;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CassettePlaybackChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];
    private static readonly FieldInfo Tape = typeof(AudioEffectsEngine)
        .GetField("_tape", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Prepare = typeof(AudioEffectsEngine)
        .GetMethod("PrepareTape", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Process = typeof(AudioEffectsEngine)
        .GetMethod("LoFiTape", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Checks playback resonance and independent scalar trims before voice-only level correction.</summary>
    internal static void Run(Action<string, Action> check)
    {
        foreach (int rate in Rates)
        {
            check($"Cassette enclosure resonance retains its 700 Hz center at {rate} Hz", () =>
            {
                double center = ResonanceDb(rate, 700);
                Require(Math.Abs(center - 1.5) < .02,
                    "The playback resonance does not provide the agreed 1.5 dB center gain.");
                Require(center > ResonanceDb(rate, 350) && center > ResonanceDb(rate, 1400),
                    "The enclosure resonance moved away from the voice midrange.");
                Require(Math.Abs(ResonanceDb(rate, 100)) < .1 && Math.Abs(ResonanceDb(rate, 3000)) < .1,
                    "The midrange resonance became a broadband boost.");
            });

            check($"Cassette playback trim approaches unity without amplifying at {rate} Hz", () =>
            {
                AudioEffectsEngine engine = NewEngine(rate);
                Action<float> prepare = Prepare.CreateDelegate<Action<float>>(engine);
                float previous = 1f;
                for (int point = 0; point <= 20; point++)
                {
                    prepare(point / 20f);
                    float gain = OutputGain(engine);
                    Require(float.IsFinite(gain) && gain > .6f && gain <= previous,
                        "Playback trim amplified, reversed, or produced an invalid gain.");
                    if (point == 0) Require(gain == 1f, "Playback trim changed internal zero.");
                    previous = gain;
                }
                Require(previous < .7f, "Full cassette playback lost its output attenuation.");
                prepare(1e-8f);
                Require(OutputGain(engine) == 1f, "Playback trim does not approach clean gain.");
            });

            check($"Cassette playback trim scales voice and independent noise before correction at {rate} Hz", () =>
            {
                foreach (float amount in new[] { .01f, .25f, .5f, .75f, 1f })
                {
                    AudioEffectsEngine actual = NewEngine(rate), unit = NewEngine(rate);
                    Prepare.CreateDelegate<Action<float>>(actual)(amount);
                    Prepare.CreateDelegate<Action<float>>(unit)(amount);
                    float gain = OutputGain(actual);
                    object unitState = Tape.GetValue(unit)!;
                    unitState.GetType().GetField("OutputGain")!.SetValue(unitState, 1f);
                    Tape.SetValue(unit, unitState);
                    var processActual = Process.CreateDelegate<Func<float, float, float>>(actual);
                    var processUnit = Process.CreateDelegate<Func<float, float, float>>(unit);
                    for (int i = 0; i < rate / 4; i++)
                    {
                        // Finish with silence so the same assertion also covers hiss and the filter tail.
                        float input = i < rate / 8 ? (float)(.23 * Math.Sin(2 * Math.PI * 173 * i / rate)
                            + .11 * Math.Sin(2 * Math.PI * 971 * i / rate)) : 0f;
                        float expected = processUnit(input, amount) * gain;
                        Require(processActual(input, amount) == expected,
                            "Playback trim changed upstream saturation or voice filter state.");
                        object actualState = Tape.GetValue(actual)!, referenceState = Tape.GetValue(unit)!;
                        float noise = (float)actualState.GetType().GetField("NoiseSample")!.GetValue(actualState)!;
                        float unitNoise = (float)referenceState.GetType().GetField("NoiseSample")!.GetValue(referenceState)!;
                        Require(noise == unitNoise * gain, "Background trim entered voice normalization.");
                    }
                }
            });
        }
    }

    /// <summary>Measures the actual playback filter with a settled sine, rather than reading its coefficients.</summary>
    private static double ResonanceDb(int rate, double frequency)
    {
        object state = Tape.GetValue(NewEngine(rate))!;
        var filter = (BiQuadFilter)state.GetType().GetField("BoomboxResonance")!.GetValue(state)!;
        double inputPower = 0, outputPower = 0;
        for (int i = 0; i < rate; i++)
        {
            float input = (float)(.1 * Math.Sin(2 * Math.PI * frequency * i / rate));
            float output = filter.Transform(input);
            if (i < rate / 2) continue;
            inputPower += (double)input * input;
            outputPower += (double)output * output;
        }
        return 10 * Math.Log10(outputPower / inputPower);
    }

    /// <summary>Reads the prepared output gain without duplicating the production intensity formula.</summary>
    private static float OutputGain(AudioEffectsEngine engine)
    {
        object state = Tape.GetValue(engine)!;
        return (float)state.GetType().GetField("OutputGain")!.GetValue(state)!;
    }

    /// <summary>Initializes a reproducible cassette without processing or saving audio.</summary>
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new EffectsSettings(), rate);
        engine.Reset(123456);
        engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.LoFiTape, 1f);
        return engine;
    }

    /// <summary>Fails a playback contract without accepting the received value as its own reference.</summary>
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
