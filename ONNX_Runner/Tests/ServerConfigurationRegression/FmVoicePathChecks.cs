using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class FmVoicePathChecks
{
    // Independently verified from the convolution of the microphone, resampling and channel FIRs.
    private static readonly (int Rate, int Delay)[] Rates =
    [
        (8000, 85), (11025, 112), (16000, 155), (22050, 208), (24000, 225),
        (44100, 400), (48000, 434), (96000, 852), (192000, 1688)
    ];

    internal static void Run(Action<string, Action> check)
    {
        foreach (var point in Rates)
        {
            int rate = point.Rate;
            check($"FM retains its speech band and linear radio response at {rate} Hz", () =>
            {
                var middle = Response(rate, 1000, .01f);
                var bass = Response(rate, 80, .01f);
                Require(bass.GainDb < middle.GainDb - 15, "FM lost its voice-band bass rejection.");
                var strongMiddle = Response(rate, 1000, .35f);
                var high = Response(rate, 3000, .01f);
                var strongHigh = Response(rate, 3000, .35f);
                Require(Math.Abs(strongHigh.GainDb - high.GainDb) < .0001 &&
                    Math.Abs(strongMiddle.GainDb - middle.GainDb) < .0001,
                    "The fixed radio branch introduced level-dependent waveshaping.");
                if (rate >= 16000)
                    Require(Response(rate, 6000, .01f).GainDb < middle.GainDb - 45,
                        "FM lost its channel filtering.");
                Require(Response(rate, 1000, .1f).ResidualDb < -60,
                    "Moderate speech-band peaks acquired excessive distortion.");
            });
            check($"FM keeps quiet speech linear and has no artificial noise at {rate} Hz", () =>
            {
                var silence = new float[rate / 4];
                Engine(rate).ApplyEffect(silence, VoiceEffectType.FmRadio, 1);
                Require(silence.All(x => x == 0), "Strong-signal FM generated a noise floor on silence.");
                float[] quiet = Tone(rate, 1000, .00001f), louder = Tone(rate, 1000, .0001f);
                Engine(rate).ApplyEffect(quiet, VoiceEffectType.FmRadio, 1);
                Engine(rate).ApplyEffect(louder, VoiceEffectType.FmRadio, 1);
                Require(quiet.Any(x => Math.Abs(x) > 1e-7), "Quiet FM speech was muted.");
                for (int i = 0; i < quiet.Length; i++)
                    Require(Math.Abs(quiet[i] * 10 - louder[i]) < 1e-8, "Quiet FM speech was gated or given level-dependent noise.");
            });
            check($"FM aligns its dry branch with the complete radio path at {rate} Hz", () =>
            {
                var source = new float[4 * point.Delay + 129];
                source[17] = .0001f;
                float[] full = (float[])source.Clone();
                Engine(rate).ApplyEffect(full, VoiceEffectType.FmRadio, 1);
                foreach (float amount in new[] { .1f, .5f, .9f })
                {
                    float[] actual = (float[])source.Clone();
                    Engine(rate).ApplyEffect(actual, VoiceEffectType.FmRadio, amount);
                    float[] dry = new float[source.Length];
                    for (int i = point.Delay; i < dry.Length; i++) dry[i] = source[i - point.Delay];
                    float[] expected = (float[])full.Clone();
                    new StreamingPowerMatchedMixer(rate).Mix(dry, expected, amount);
                    for (int i = 0; i < actual.Length; i++)
                        Require(BitConverter.SingleToInt32Bits(actual[i]) == BitConverter.SingleToInt32Bits(expected[i]),
                            "The FM clean branch uses a different delay from its complete filtered branch.");
                }
            });
        }

        check("FM keeps strong high-frequency input free of folded harmonics at an 8 kHz host rate", () =>
        {
            const int rate = 8000;
            float[] source = Tone(rate, 2500, .35f, 2);
            Engine(rate).ApplyEffect(source, VoiceEffectType.FmRadio, 1);
            double fundamental = Component(source, rate, 2500, 4000, 8000);
            foreach (int frequency in new[] { 500, 1500 })
                Require(20 * Math.Log10(Component(source, rate, frequency, 4000, 8000) / fundamental) < -60,
                    "Nonlinear FM harmonics folded into the voice band.");
        });
    }

    private static (double GainDb, double ResidualDb) Response(int rate, double frequency, float amplitude)
    {
        float[] source = Tone(rate, frequency, amplitude);
        Engine(rate).ApplyEffect(source, VoiceEffectType.FmRadio, 1);
        int start = rate / 4, end = 3 * rate / 4;
        double cc = 0, ss = 0, cs = 0, yc = 0, ys = 0;
        for (int i = start; i < end; i++)
        {
            double c = Math.Cos(2 * Math.PI * frequency * i / rate), s = Math.Sin(2 * Math.PI * frequency * i / rate);
            cc += c * c; ss += s * s; cs += c * s;
            yc += source[i] * c; ys += source[i] * s;
        }
        double determinant = cc * ss - cs * cs;
        double cGain = (yc * ss - ys * cs) / determinant, sGain = (ys * cc - yc * cs) / determinant;
        double residual = 0, fundamental = 0;
        for (int i = start; i < end; i++)
        {
            double fitted = cGain * Math.Cos(2 * Math.PI * frequency * i / rate) + sGain * Math.Sin(2 * Math.PI * frequency * i / rate);
            residual += (source[i] - fitted) * (source[i] - fitted);
            fundamental += fitted * fitted;
        }
        return (10 * Math.Log10((cGain * cGain + sGain * sGain) / ((double)amplitude * amplitude)),
            10 * Math.Log10(Math.Max(1e-30, residual) / Math.Max(1e-30, fundamental)));
    }

    private static double Component(float[] samples, int rate, double frequency, int start, int count)
    {
        double real = 0, imaginary = 0;
        for (int i = start; i < start + count; i++)
        {
            real += samples[i] * Math.Cos(2 * Math.PI * frequency * i / rate);
            imaginary += samples[i] * Math.Sin(2 * Math.PI * frequency * i / rate);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
    }

    private static float[] Tone(int rate, double frequency, float amplitude, int seconds = 1)
    {
        var samples = new float[rate * seconds];
        for (int i = 0; i < samples.Length; i++) samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / rate);
        return samples;
    }

    private static AudioEffectsEngine Engine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate);
        engine.Reset(123456);
        return engine;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
