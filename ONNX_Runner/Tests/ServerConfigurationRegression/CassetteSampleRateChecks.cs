using System.Numerics;
using System.Reflection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CassetteSampleRateChecks
{
    private static readonly int[] Rates = [8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000];

    internal static void Run(Action<string, Action> check)
    {
        var reference = Curve(22050);
        check("Cassette reference azimuth retains every legacy coefficient", () =>
        {
            float high = 1f - MathF.Exp(-2f * MathF.PI * (22050f * .45f) / 22050f);
            float low = 1f - MathF.Exp(-2f * MathF.PI * 4500f / 22050f);
            for (int i = 0; i <= 1000; i++)
                Require(reference.At(i / 1000f) == Dsp.Lerp(high, low, i / 1000f), "Reference coloration changed.");
        });
        foreach (int rate in Rates)
        {
            check($"Cassette azimuth curve stays monotone and stable at {rate} Hz", () =>
            {
                var curve = Curve(rate);
                float previous = 1;
                for (int i = 0; i <= 1000; i++)
                {
                    float actual = curve.At(i / 1000f);
                    Require(float.IsFinite(actual) && actual > 0 && actual < 1 && actual <= previous,
                        "Azimuth curve reversed or left the stable coefficient range.");
                    previous = actual;
                }
                Require(curve.At(-1) == curve.High && curve.At(2) == curve.Low, "Curve boundary did not clamp.");
            });
            check($"Cassette azimuth matches measured reference attenuation at {rate} Hz", () =>
            {
                var curve = Curve(rate);
                double frequency = Math.Min(4500, rate * .45);
                foreach (float position in new[] { 0f, .25f, .5f, .75f, 1f })
                {
                    float alpha = curve.At(position), state = 0;
                    double inputPower = 0, outputPower = 0;
                    for (int i = 0; i < rate / 4; i++)
                    {
                        float input = .1f * (float)Math.Sin(2 * Math.PI * frequency * i / rate);
                        state += alpha * (input - state);
                        if (i < rate / 8) continue;
                        inputPower += input * (double)input; outputPower += state * (double)state;
                    }
                    double expected = Response(reference.At(position), 22050, frequency).Magnitude;
                    double delta = 10 * Math.Log10(outputPower / inputPower) - 20 * Math.Log10(expected);
                    Require(Math.Abs(delta) < .05, "The physical-frequency response missed its reference.");
                }
            });
            check($"Cassette azimuth reduces cross-rate band response error at {rate} Hz", () =>
            {
                if (rate == 22050) return;
                var curve = Curve(rate);
                float oldHigh = 1f - MathF.Exp(-2f * MathF.PI * Math.Min(11000f, rate * .45f) / rate);
                float oldLow = 1f - MathF.Exp(-2f * MathF.PI * Math.Min(4500f, rate * .45f) / rate);
                double oldError = 0, newError = 0;
                foreach (float amount in new[] { .1f, .25f, .5f, .75f, 1f })
                foreach (float lfo in new[] { 0f, .25f, .5f, .75f, 1f })
                foreach (int frequency in new[] { 500, 1000, 2000, 3000, 4500, 6000, 7000 })
                {
                    if (frequency >= Math.Min(rate, 22050) * .45) continue;
                    float position = amount * lfo;
                    double mix = .45 * amount * amount;
                    double target = (1 - mix + mix * Response(reference.At(position), 22050, frequency)).Magnitude;
                    double before = (1 - mix + mix * Response(Dsp.Lerp(oldHigh, oldLow, position), rate, frequency)).Magnitude;
                    double after = (1 - mix + mix * Response(curve.At(position), rate, frequency)).Magnitude;
                    oldError += Math.Pow(20 * Math.Log10(before / target), 2);
                    newError += Math.Pow(20 * Math.Log10(after / target), 2);
                }
                Require(newError < oldError * .64, "The new curve did not reduce RMS response error by 20%.");
            });
        }
        check("One-pole response matching rejects invalid setup parameters", () =>
        {
            Action[] invalid = [() => Dsp.MatchOnePoleResponse(0, 22050, .9f, .7f, 4500),
                () => Dsp.MatchOnePoleResponse(16000, 0, .9f, .7f, 4500),
                () => Dsp.MatchOnePoleResponse(16000, 22050, float.NaN, .7f, 4500),
                () => Dsp.MatchOnePoleResponse(16000, 22050, .9f, 1f, 4500),
                () => Dsp.MatchOnePoleResponse(16000, 22050, .9f, .7f, 0)];
            foreach (var action in invalid)
            {
                bool rejected = false;
                try { action(); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Require(rejected, "An invalid curve reached coefficient preparation.");
            }
        });
    }

    private static Dsp.OnePoleCurve Curve(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate);
        engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.LoFiTape, 1);
        object azimuth = typeof(AudioEffectsEngine).GetField("_azimuth", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        return (Dsp.OnePoleCurve)azimuth.GetType().GetField("Curve")!.GetValue(azimuth)!;
    }

    private static Complex Response(double alpha, int rate, double frequency) =>
        alpha / (1 - (1 - alpha) * Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / rate));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
