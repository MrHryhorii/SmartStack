global using Microsoft.Extensions.Configuration;
using Microsoft.ML.OnnxRuntime;
using STT_Runner.Services;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length >= 4 && args[0] == "probe")
{
    // Decode externally to 16 kHz float PCM so this probe observes production DSP directly.
    var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(args[3])).Build();
    var normalizer = new StreamingAudioNormalizer(AudioNormalizationSettings.Read(config));
    using var sessionOptions = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
    using var session = new InferenceSession(args[1], sessionOptions);
    var inference = new SileroVadStream(session);
    using var segmenter = new VadSegmenter(VadProfile.Read(config));
    float[] pcm = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(args[2])).ToArray();
    var probabilities = new List<float>();
    var pieces = new List<object>();
    for (int offset = 0; offset < pcm.Length; offset += 512)
    {
        Span<float> frame = pcm.AsSpan(offset, Math.Min(512, pcm.Length - offset));
        normalizer.Process(frame);
        float probability = inference.Predict(frame);
        probabilities.Add(probability);
        AddPiece(segmenter.Accept(frame, probability));
    }
    AddPiece(segmenter.Finish());
    Console.WriteLine(JsonSerializer.Serialize(new { samples = segmenter.SamplesRead, probabilities, pieces }));
    return;

    void AddPiece(AudioPiece? piece)
    {
        if (piece is null) return;
        using (piece.Owner)
            pieces.Add(new { start = piece.StartSample / 16000d,
                end = (piece.StartSample + piece.Length) / 16000d, samples = piece.Length });
    }
}

int passed = 0;
Check("silence emits no segments", () =>
{
    using var segmenter = new VadSegmenter(new());
    for (int index = 0; index < 100; index++) Assert(segmenter.Accept(new float[512], 0) is null);
    Assert(segmenter.Finish() is null);
});
Check("partial EOF preserves exactly 5003 real samples", () =>
{
    float[] samples = Enumerable.Range(0, 5003).Select(index => (float)Math.Sin(index * .03)).ToArray();
    using var segmenter = new VadSegmenter(new() { PrefixPaddingMs = 0, MinSpeechMs = 0 });
    for (int offset = 0; offset < samples.Length; offset += 512)
        Assert(segmenter.Accept(samples.AsSpan(offset, Math.Min(512, samples.Length - offset)), .9f) is null);
    AudioPiece piece = segmenter.Finish()!;
    using (piece.Owner)
    {
        Assert(piece.Length == samples.Length && segmenter.SamplesRead == samples.Length);
        Assert(piece.Owner.Memory.Span[..piece.Length].SequenceEqual(samples));
    }
});
Check("short speech honors 500 ms without a two-second override", () =>
{
    using var segmenter = new VadSegmenter(new() { PauseMs = 500, PrefixPaddingMs = 0 });
    float[] frame = new float[512];
    for (int index = 0; index < 10; index++) Assert(segmenter.Accept(frame, .9f) is null);
    for (int index = 0; index < 16; index++) Assert(segmenter.Accept(frame, .1f) is null);
    AudioPiece piece = segmenter.Accept(frame, .1f)!;
    using (piece.Owner) Assert(piece.Length == 5120 + 4800);
    Assert(segmenter.Finish() is null);
});
Check("request pause overrides are honored for short speech", () =>
{
    using var segmenter = new VadSegmenter(new(), new(800, 0, .5f));
    float[] frame = new float[512];
    for (int index = 0; index < 10; index++) Assert(segmenter.Accept(frame, .9f) is null);
    for (int index = 0; index < 25; index++) Assert(segmenter.Accept(frame, .1f) is null);
    AudioPiece piece = segmenter.Accept(frame, .1f)!;
    piece.Owner.Dispose();
});
Check("hysteresis preserves intermediate-probability speech", () =>
{
    using var segmenter = new VadSegmenter(new());
    float[] frame = new float[512];
    for (int index = 0; index < 10; index++) Assert(segmenter.Accept(frame, .9f) is null);
    for (int index = 0; index < 60; index++) Assert(segmenter.Accept(frame, .4f) is null);
    AudioPiece piece = segmenter.Finish()!;
    using (piece.Owner) Assert(piece.Length == 70 * 512);
});
Check("interior pauses retain every PCM sample unchanged", () =>
{
    float[] samples = Enumerable.Range(0, 38 * 512).Select(index => index / 20000f).ToArray();
    using var segmenter = new VadSegmenter(new() { PrefixPaddingMs = 0 });
    for (int index = 0; index < 38; index++)
        Assert(segmenter.Accept(samples.AsSpan(index * 512, 512), index is >= 10 and < 18 ? .1f : .9f) is null);
    AudioPiece piece = segmenter.Finish()!;
    using (piece.Owner) Assert(piece.Owner.Memory.Span[..piece.Length].SequenceEqual(samples));
});
Check("brief noise candidates are discarded", () =>
{
    using var segmenter = new VadSegmenter(new());
    float[] frame = new float[512];
    Assert(segmenter.Accept(frame, .9f) is null);
    for (int index = 0; index < 30; index++) Assert(segmenter.Accept(frame, .1f) is null);
    Assert(segmenter.Finish() is null);
});
Check("normal pause padding does not duplicate neighboring audio", () =>
{
    using var segmenter = new VadSegmenter(new() { PauseMs = 32, MinSpeechMs = 0 });
    float[] frame = new float[512];
    for (int index = 0; index < 10; index++) segmenter.Accept(frame, .9f);
    segmenter.Accept(frame, .1f);
    AudioPiece first = segmenter.Accept(frame, .1f)!;
    for (int index = 0; index < 10; index++) segmenter.Accept(frame, .9f);
    AudioPiece second = segmenter.Finish()!;
    using (first.Owner)
    using (second.Owner) Assert(second.StartSample >= first.StartSample + first.Length);
});
Check("forced splits preserve coverage and bounded overlap", () =>
{
    using var segmenter = new VadSegmenter(new() { MaxSegmentSeconds = 1, PrefixPaddingMs = 0, MinSpeechMs = 0 });
    float[] samples = new float[40003];
    long end = 0;
    for (int offset = 0; offset < samples.Length; offset += 512)
        Inspect(segmenter.Accept(samples.AsSpan(offset, Math.Min(512, samples.Length - offset)), .9f));
    Inspect(segmenter.Finish());
    Assert(end == samples.Length);
    void Inspect(AudioPiece? piece)
    {
        if (piece is null) return;
        using (piece.Owner)
        {
            Assert(piece.Length <= 16000 && piece.StartSample <= end && end - piece.StartSample <= 2048);
            end = piece.StartSample + piece.Length;
        }
    }
});
Check("normalizer raises quiet speech with bounded gain", () =>
{
    var normalizer = new StreamingAudioNormalizer(new() { MaxGainDb = 12 });
    double finalRms = 0;
    for (int frame = 0; frame < 200; frame++)
    {
        float[] values = Enumerable.Range(0, 512).Select(index => .04f * (float)Math.Sin((frame * 512 + index) * .1)).ToArray();
        float[] original = values.ToArray();
        normalizer.Process(values);
        for (int index = 0; index < values.Length; index++)
            Assert(Math.Abs(values[index]) <= Math.Abs(original[index]) * 3.982f + 1e-6);
        finalRms = Math.Sqrt(values.Select(value => (double)value * value).Average());
    }
    Assert(finalRms is > .09 and < .11);
});
Check("normalizer preserves silence and does not amplify floor noise", () =>
{
    var normalizer = new StreamingAudioNormalizer(new());
    float[] quiet = Enumerable.Repeat(.000001f, 512).ToArray();
    for (int index = 0; index < 50; index++) normalizer.Process(quiet);
    Assert(quiet.All(value => Math.Abs(value) <= .00000101));
    float[] silence = new float[512];
    normalizer.Process(silence);
    Assert(silence.All(value => value == 0));
});
Check("normalizer sanitizes nonfinite values and limits peaks", () =>
{
    var normalizer = new StreamingAudioNormalizer(new());
    float[] values = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 3f, -3f, .1f];
    normalizer.Process(values);
    Assert(values.All(float.IsFinite) && values.All(value => Math.Abs(value) <= .891251f));
});
Check("disabled normalizer preserves finite PCM exactly", () =>
{
    var normalizer = new StreamingAudioNormalizer(new() { Enabled = false });
    float[] values = [-.04f, .01f, .2f];
    float[] expected = values.ToArray();
    normalizer.Process(values);
    Assert(values.SequenceEqual(expected));
});
Check("default normalization recovers very quiet speech within the 30 dB ceiling", () =>
{
    var settings = new AudioNormalizationSettings();
    Assert(settings.NoiseFloorDbfs == -70 && settings.MaxGainDb == 30);
    var normalizer = new StreamingAudioNormalizer(settings);
    double finalRms = 0;
    for (int frame = 0; frame < 200; frame++)
    {
        float[] values = Enumerable.Range(0, 512)
            .Select(index => .001f * (float)Math.Sin((frame * 512 + index) * .1)).ToArray();
        float[] original = values.ToArray();
        normalizer.Process(values);
        for (int index = 0; index < values.Length; index++)
            Assert(Math.Abs(values[index]) <= Math.Abs(original[index]) * 31.623f + 1e-6);
        finalRms = Math.Sqrt(values.Select(value => (double)value * value).Average());
    }
    Assert(finalRms is > .02 and < .024);
});
Console.WriteLine($"PASS: {passed} audio pipeline checks");

void Check(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine($"PASS: {name}");
}
static void Assert(bool condition)
{
    if (!condition) throw new InvalidOperationException("Audio pipeline assertion failed.");
}
