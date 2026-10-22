using ONNX_Runner.Services;

internal static class PeakLimiterChecks
{
    internal static void Run(Action<string, Action> check)
    {
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 24000, 44100, 48000, 96000, 192000 })
        {
            check($"FM peak protection preserves quiet PCM and final samples at {rate} Hz", () =>
            {
                foreach (int length in new[] { 0, 1, 17, rate / 10 })
                {
                    float[] source = Enumerable.Range(0, length).Select(i => .1f * MathF.Sin(i * .19f)).ToArray();
                    foreach (float volume in new[] { 0f, .25f, 1f, 2f })
                    {
                        float[] actual = Render(source, rate, volume, 31, 7);
                        Require(actual.SequenceEqual(source.Select(v => v * volume)), "Quiet PCM or the final samples changed.");
                    }
                }
            });
            check($"FM peak protection catches short peaks independently of chunks at {rate} Hz", () =>
            {
                var random = new Random(123456);
                float[] source = Enumerable.Range(0, rate / 5).Select(i =>
                    i % 313 < 17 ? (float)(random.NextDouble() * 16 - 8) : .3f * MathF.Sin(i * .13f)).ToArray();
                source[^1] = 16;
                float[] whole = Render(source, rate, 2.5f, source.Length, 256);
                float[] split = Render(source, rate, 2.5f, 37, 1);
                Require(whole.SequenceEqual(split), "Stream partitions changed the limiter output.");
                Require(whole.All(v => float.IsFinite(v) && Math.Abs(v) <= PeakLimiter.Ceiling + .000002f),
                    "A peak escaped the limiter ceiling.");
                Require(Math.Abs(whole[^1]) > .1f, "The final peak was lost during flush.");
            });
            check($"FM peak protection allocates nothing while processing at {rate} Hz", () =>
            {
                var limiter = new PeakLimiter(rate, 2.5f);
                var buffer = new float[1024];
                for (int i = 0; i < 5; i++) limiter.Process(buffer);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 8; i++) limiter.Process(buffer);
                Require(GC.GetAllocatedBytesForCurrentThread() == before, "The limiter allocated in its hot path.");
            });
        }
        check("FM peak protection smooths sustained clipping without flattening the waveform", () =>
        {
            const int rate = 22050;
            float[] source = Enumerable.Range(0, rate * 2).Select(i => .7f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate)).ToArray();
            float[] smooth = Render(source, rate, 3, 257, 13);
            float[] shaped = (float[])source.Clone();
            VolumeShifter.ApplyVolume(shaped, 3);
            Require(Residual(smooth, rate) < -65, "Sustained limiting introduced excessive harmonic distortion.");
            Require(Residual(smooth, rate) < Residual(shaped, rate) - 30, "Gain smoothing did not improve the former peak waveshaper.");
        });
    }

    private static float[] Render(float[] source, int rate, float volume, int block, int drain)
    {
        var limiter = new PeakLimiter(rate, volume);
        var output = new float[source.Length];
        int written = 0;
        for (int start = 0; start < source.Length; start += block)
        {
            float[] part = source.AsSpan(start, Math.Min(block, source.Length - start)).ToArray();
            int count = limiter.Process(part);
            part.AsSpan(0, count).CopyTo(output.AsSpan(written));
            written += count;
        }
        var tail = new float[drain];
        int length;
        while ((length = limiter.Flush(tail)) > 0)
        {
            tail.AsSpan(0, length).CopyTo(output.AsSpan(written));
            written += length;
        }
        Require(written == source.Length, "The limiter changed the audio duration.");
        return output;
    }

    private static double Residual(float[] source, int rate)
    {
        int start = rate / 2;
        double c = 0, s = 0;
        for (int i = 0; i < rate; i++)
        {
            double phase = 2 * Math.PI * 1000 * (start + i) / rate;
            c += 2 * source[start + i] * Math.Cos(phase) / rate;
            s += 2 * source[start + i] * Math.Sin(phase) / rate;
        }
        double error = 0, power = 0;
        for (int i = 0; i < rate; i++)
        {
            double phase = 2 * Math.PI * 1000 * (start + i) / rate;
            double expected = c * Math.Cos(phase) + s * Math.Sin(phase);
            error += Math.Pow(source[start + i] - expected, 2);
            power += expected * expected;
        }
        return 10 * Math.Log10(Math.Max(1e-30, error / power));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
