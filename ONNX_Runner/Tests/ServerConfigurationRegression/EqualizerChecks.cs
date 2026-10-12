using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ONNX_Runner.Endpoints;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class EqualizerChecks
{
    // Independent public API fixture, rather than names derived from the production catalog.
    private static readonly string[] ExpectedIds =
    [
        "wearable-class-device", "handset-class-device", "headphone-class-device",
        "small-bluetooth-speaker-class-device", "medium-bluetooth-speaker-class-device",
        "large-home-entertainment-class-device", "large-automotive-class-device",
        "telephony-class-application"
    ];

    /// <summary>Checks request semantics, colored frequency response, streaming state, and allocation contracts.</summary>
    internal static void Run(Action<string, Action> check)
    {
        check("Equalizer discovery lists exactly the eight public device-profile IDs", () =>
        {
            var result = (IValueHttpResult)InfoEndpoints.GetEqualizers();
            JsonElement json = JsonSerializer.SerializeToElement(result.Value);
            Require(json.GetProperty("equalizers").EnumerateArray().Select(item => item.GetString())
                .SequenceEqual(ExpectedIds), "Discovery differs from the declared profile IDs.");
            Require(AudioEqualizerProfiles.All is ICollection<AudioEqualizerProfile> { IsReadOnly: true } &&
                AudioEqualizerProfiles.Ids is ICollection<string> { IsReadOnly: true }, "Catalog exposes mutable collections.");
        });

        CheckProfileLimit(check);

        foreach (string json in new[]
        {
            "{\"input\":\"Ready.\"}", "{\"input\":\"Ready.\",\"equalizer\":null}",
            "{\"input\":\"Ready.\",\"equalizer\":[]}",
            "{\"input\":\"Ready.\",\"equalizer\":[\"unknown\",null,\"\"]}"
        })
        {
            check($"Equalizer bypasses inactive request {json}", () =>
            {
                var dto = JsonSerializer.Deserialize<TsubakiSpeechRequest>(json)!;
                var adapted = TsubakiRequestAdapter.ToSynthesisRequest(dto);
                Require(adapted.ValidationError == null && adapted.Request != null &&
                    AudioEqualizer.Create(adapted.Request.Equalizer, 24000) == null, "Inactive profile list changed the path.");
            });
        }
        check("Equalizer adapter preserves requested order, repetitions, and unknown entries", () =>
        {
            string?[] requested = [ExpectedIds[7], "unknown", ExpectedIds[0], ExpectedIds[7], null];
            var dto = JsonSerializer.Deserialize<TsubakiSpeechRequest>(JsonSerializer.Serialize(new
                { input = "Ready.", equalizer = requested }))!;
            var adapted = TsubakiRequestAdapter.ToSynthesisRequest(dto);
            Require(adapted.ValidationError == null && adapted.Request!.Equalizer!.SequenceEqual(requested),
                "Adapter reordered, deduplicated, or rejected the requested profiles.");
        });
        check("OpenAI requests do not activate the Tsubaki equalizer extension", () =>
        {
            var dto = JsonSerializer.Deserialize<OpenAiSpeechRequest>(
                "{\"input\":\"Ready.\",\"equalizer\":[\"telephony-class-application\"]}")!;
            Require(OpenAiRequestAdapter.ToSynthesisRequest(dto).Request!.Equalizer == null,
                "The strict OpenAI adapter acquired an extended effect parameter.");
        });
        foreach (string value in new[] { "\"telephony-class-application\"", "1", "true", "{}", "[1]", "[true]" })
        {
            check($"Equalizer rejects incorrect JSON shape {value}", () =>
            {
                bool rejected = false;
                try { JsonSerializer.Deserialize<TsubakiSpeechRequest>("{\"input\":\"Ready.\",\"equalizer\":" + value + "}"); }
                catch (JsonException) { rejected = true; }
                Require(rejected, "An incorrect profile-list shape was silently accepted.");
            });
        }

        check("Supported equalizer IDs allow case and whitespace without changing the filter", () =>
        {
            float[] expected = Signal(12000), actual = Signal(12000);
            AudioEqualizer.Create([ExpectedIds[7]], 24000)!.Apply(expected);
            AudioEqualizer.Create([" TELEPHONY-CLASS-APPLICATION "], 24000)!.Apply(actual);
            Require(expected.AsSpan().SequenceEqual(actual), "ID normalization changed the profile.");
        });
        check("Unsupported names among supported profiles do not change PCM", () =>
        {
            float[] expected = Signal(12000), actual = Signal(12000);
            AudioEqualizer.Create([ExpectedIds[0], ExpectedIds[7]], 24000)!.Apply(expected);
            AudioEqualizer.Create(["unknown", ExpectedIds[0], null, ExpectedIds[7], ""], 24000)!.Apply(actual);
            Require(expected.AsSpan().SequenceEqual(actual), "Unknown entries changed processing.");
        });
        check("Combined and repeated equalizers match sequential applications", () =>
        {
            string[] ids = [ExpectedIds[0], ExpectedIds[2], ExpectedIds[7], ExpectedIds[7]];
            float[] expected = Signal(16000), actual = Signal(16000);
            foreach (string id in ids) AudioEqualizer.Create([id], 48000)!.Apply(expected);
            AudioEqualizer.Create(ids, 48000)!.Apply(actual);
            Require(expected.AsSpan().SequenceEqual(actual), "The cascade mixed, reordered, or deduplicated profiles.");
        });
        check("Repeating a profile performs a second filtering pass", () =>
        {
            double once = ToneGain([ExpectedIds[7]], 48000, 100);
            double twice = ToneGain([ExpectedIds[7], ExpectedIds[7]], 48000, 100);
            Require(twice < once * .2 && Math.Abs(twice - once * once) < .0001,
                "Repeated profile did not multiply the measured frequency response.");
        });
        check("Telephony preserves speech mids and attenuates low and high frequencies", () =>
        {
            double low = ToneGain([ExpectedIds[7]], 48000, 100);
            double middle = ToneGain([ExpectedIds[7]], 48000, 1000);
            double high = ToneGain([ExpectedIds[7]], 48000, 10000);
            Require(low < .15 && middle > 1.3 && middle < 1.45 && high < .1,
                $"Unexpected colored telephone response: {low:F5}, {middle:F5}, {high:F5}.");
        });

        foreach (string id in ExpectedIds)
        {
            check($"Equalizer {id} has bounded broad coloration", () =>
            {
                foreach (int frequency in new[] { 20, 40, 70, 120, 180, 250, 300, 350, 500, 800,
                    900, 1000, 1500, 2000, 2500, 3000, 3400, 4500, 5000, 6000, 8000, 10000, 18000, 22000 })
                {
                    double gain = ToneGain([id], 48000, frequency);
                    Require(double.IsFinite(gain) && gain <= 1.65, $"Excessive gain at {frequency} Hz: {gain:F6}.");
                }
                Require(AudioEqualizerProfiles.Find(id)!.Bands is ICollection<AudioEqualizerBand> { IsReadOnly: true },
                    "Catalog exposes mutable tonal bands.");
            });
            check($"Equalizer {id} remains linear and adds no generated sound", () =>
            {
                float[] ordinary = Signal(12000), doubled = ordinary.Select(sample => sample * 2).ToArray();
                ordinary[0] = 2f;
                doubled[0] = 4f;
                AudioEqualizer.Create([id], 24000)!.Apply(ordinary);
                AudioEqualizer.Create([id], 24000)!.Apply(doubled);
                for (int i = 0; i < ordinary.Length; i++)
                    Require(Math.Abs(doubled[i] - ordinary[i] * 2) < .000001f, "Profile applies nonlinear coloration.");
                float[] silence = new float[12000];
                AudioEqualizer.Create([id], 24000)!.Apply(silence);
                Require(silence.All(sample => sample == 0), "Profile generates noise or a nonzero silence floor.");
            });
            check($"Equalizer {id} rejects DC without adding a persistent tail", () =>
            {
                float[] dc = Enumerable.Repeat(.25f, 48000).ToArray();
                var engine = AudioEqualizer.Create([id], 24000)!;
                engine.Apply(dc);
                Require(dc.AsSpan(24000).ToArray().All(sample => Math.Abs(sample) < .00001f), "DC was retained.");
                float[] pause = new float[48000];
                engine.Apply(pause);
                Require(pause.AsSpan(24000).ToArray().All(sample => Math.Abs(sample) < .00001f),
                    "A dry filter produced a persistent tail.");
            });

            foreach (int rate in new[] { 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000 })
            {
                check($"Equalizer {id} remains stable at {rate} Hz", () =>
                {
                    float[] samples = Signal(rate);
                    AudioEqualizer.Create([id], rate)!.Apply(samples);
                    Require(samples.All(float.IsFinite) && samples.Max(Math.Abs) < 1,
                        "Filter became unstable at the processing sample rate.");
                });
            }
        }

        // Independent listening targets: each device must retain its intended broad accent.
        foreach (var target in new (int Index, int Frequency, double Minimum, double Maximum)[]
        {
            (0, 2500, 1.25, 1.5), (1, 1500, 1.3, 1.5), (2, 120, 1.1, 1.3),
            (3, 250, 1.2, 1.5), (4, 180, 1.15, 1.4), (5, 900, .75, .95),
            (6, 180, 1.2, 1.5), (7, 1000, 1.3, 1.45)
        })
        {
            check($"Equalizer {ExpectedIds[target.Index]} retains its characteristic band", () =>
            {
                double gain = ToneGain([ExpectedIds[target.Index]], 48000, target.Frequency);
                Require(gain > target.Minimum && gain < target.Maximum, $"Missing tonal character: {gain:F6}.");
            });
        }
        check("Repeated boosted profiles accumulate their measured gain", () =>
        {
            double once = ToneGain([ExpectedIds[7]], 48000, 1000);
            double repeated = ToneGain(Enumerable.Repeat(ExpectedIds[7], 16).ToArray(), 48000, 1000);
            Require(Math.Abs(repeated / Math.Pow(once, 16) - 1) < .001,
                "Repeated boosts were normalized, clipped, or changed between passes.");
        });
        foreach (string id in ExpectedIds)
        {
            check($"Sixteen repetitions of {id} remain finite across chunks", () =>
            {
                float[] samples = Signal(48000);
                var engine = AudioEqualizer.Create(Enumerable.Repeat(id, 16).ToArray(), 24000)!;
                engine.Apply(samples);
                Require(samples.All(float.IsFinite), "Repeated boosts overflowed PCM.");
            });
        }

        check("Final limiter protects repeated boosts at unity volume without changing filter linearity", () =>
        {
            float[] samples = new float[24000];
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)(.5 * Math.Sin(2 * Math.PI * 1000 * i / 24000));
            AudioEqualizer.Create(Enumerable.Repeat(ExpectedIds[7], 16).ToArray(), 24000)!.Apply(samples);
            Require(samples.Max(Math.Abs) > 1, "Fixture does not exercise accumulated boost.");
            VolumeShifter.ApplyVolume(samples, 1f, forceLimiter: true);
            Require(samples.All(float.IsFinite) && samples.Max(Math.Abs) < 1f, "Boosted PCM exceeded encoder headroom.");
            float[] unaffected = [.1f, -.7f, 1.2f];
            VolumeShifter.ApplyVolume(unaffected, 1f);
            Require(unaffected.AsSpan().SequenceEqual(new float[] { .1f, -.7f, 1.2f }),
                "The legacy unity-volume bypass changed without active equalization.");
            float[] quiet = [.1f, -.7f];
            VolumeShifter.ApplyVolume(quiet, 1f, forceLimiter: true);
            Require(quiet.AsSpan().SequenceEqual(new float[] { .1f, -.7f }), "Limiter changed samples below its knee.");
            VolumeShifter.ApplyVolume(samples, 0f, forceLimiter: true);
            Require(samples.All(sample => sample == 0), "Active equalization defeated muted volume.");
        });

        check("Equalizer state survives irregular chunks, empty buffers, and sentence pauses", () =>
        {
            float[] whole = Signal(36000);
            Array.Clear(whole, 8000, 12000);
            float[] chunks = (float[])whole.Clone();
            AudioEqualizer.Create(ExpectedIds, 24000)!.Apply(whole);
            var engine = AudioEqualizer.Create(ExpectedIds, 24000)!;
            int offset = 0;
            int[] lengths = [1, 17, 257, 1024, 31, 4096];
            for (int i = 0; offset < chunks.Length; i++)
            {
                engine.Apply(Span<float>.Empty);
                int length = Math.Min(lengths[i % lengths.Length], chunks.Length - offset);
                engine.Apply(chunks.AsSpan(offset, length));
                offset += length;
            }
            Require(whole.AsSpan().SequenceEqual(chunks), "Chunk boundaries or pauses reset the filters.");
        });
        check("Parallel equalizer requests retain independent filter state", () =>
        {
            var expected = Enumerable.Range(0, 8).Select(index => Signal(12000 + index * 37)).ToArray();
            var actual = expected.Select(samples => (float[])samples.Clone()).ToArray();
            foreach (float[] samples in expected) AudioEqualizer.Create(ExpectedIds, 24000)!.Apply(samples);
            Parallel.For(0, actual.Length, index => AudioEqualizer.Create(ExpectedIds, 24000)!.Apply(actual[index]));
            Require(expected.Zip(actual).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)),
                "Parallel requests share filter state.");
        });
        check("Unrepresentable upper cutoffs preserve the available low-rate band", () =>
        {
            double gain = ToneGain([ExpectedIds[2]], 8000, 3000);
            Require(gain > .999, $"A cutoff above Nyquist was moved into the audible band: {gain:F6}.");
        });
        check("An active equalizer rejects an invalid processing rate", () =>
        {
            bool rejected = false;
            try { AudioEqualizer.Create([ExpectedIds[7]], 0); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected, "Invalid rate created unstable filter coefficients.");
        });
        check("Equalizer bypass and PCM processing allocate no managed bytes after warmup", () =>
        {
            string?[] unknown = ["unknown", "  unknown  ", null, "", "   "];
            float[] source = Signal(1024), samples = new float[1024];
            var engine = AudioEqualizer.Create(ExpectedIds, 24000)!;
            for (int i = 0; i < 2048; i++)
            {
                AudioEqualizer.Create(null, 24000);
                AudioEqualizer.Create(Array.Empty<string>(), 24000);
                AudioEqualizer.Create(unknown, 24000);
                source.CopyTo(samples, 0);
                engine.Apply(samples);
            }
            long start = GC.GetAllocatedBytesForCurrentThread();
            long timestamp = Stopwatch.GetTimestamp();
            for (int i = 0; i < 4096; i++)
            {
                AudioEqualizer.Create(null, 24000);
                AudioEqualizer.Create(Array.Empty<string>(), 24000);
                AudioEqualizer.Create(unknown, 24000);
                source.CopyTo(samples, 0);
                engine.Apply(samples);
            }
            long elapsed = Stopwatch.GetTimestamp() - timestamp;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Require(allocated == 0, $"Equalizer hot path allocated {allocated} managed bytes.");
            double seconds = elapsed / (double)Stopwatch.Frequency;
            Console.WriteLine($"Equalizer PCM benchmark: eight profiles, {4096 * samples.Length / 24000.0:F2}s audio, {seconds * 1000:F2}ms processing, {allocated} managed bytes.");
        });
    }

    /// <summary>Checks the raw 16-entry prefix, boundary acceptance, bounded work, and allocation behavior.</summary>
    private static void CheckProfileLimit(Action<string, Action> check)
    {
        check("Equalizer request limit is the declared 16 entries", () =>
            Require(AudioEqualizerProfiles.MaxProfilesPerRequest == 16, "The request limit differs from its API contract."));

        check("Equalizer keeps arrays up to 16 entries without copying", () =>
        {
            var dto = new TsubakiSpeechRequest { Input = "Ready." };
            foreach (int length in new[] { 0, 1, 15, 16 })
            {
                string[] profiles = Enumerable.Repeat(ExpectedIds[7], length).ToArray();
                dto.Equalizer = profiles;
                Require(ReferenceEquals(dto.Equalizer, profiles), "An accepted profile list was copied or shortened.");
            }
            dto.Equalizer = null;
            Require(dto.Equalizer == null, "The optional null profile list was replaced.");
        });

        check("Equalizer truncates a direct 17-entry request without modifying its source", () =>
        {
            string?[] profiles = Enumerable.Range(0, 17).Select(i => (string?)ExpectedIds[i % 8]).ToArray();
            string?[] original = (string?[])profiles.Clone();
            var dto = new TsubakiSpeechRequest { Input = "Ready.", Equalizer = profiles };
            Require(dto.Equalizer!.Length == 16 && dto.Equalizer.SequenceEqual(original.Take(16)),
                "The request did not retain its exact 16-entry prefix.");
            Require(profiles.SequenceEqual(original), "Truncation modified the caller's array.");
            var adapted = TsubakiRequestAdapter.ToSynthesisRequest(dto);
            Require(adapted.ValidationError == null && ReferenceEquals(adapted.Request!.Equalizer, dto.Equalizer),
                "The adapter lost or recopied the accepted prefix.");
        });

        foreach (int length in new[] { 16, 17, 101, 1000 })
        {
            check($"Equalizer accepts and truncates a {length}-entry JSON array", () =>
            {
                string?[] profiles = Enumerable.Range(0, length).Select(i => (i % 4) switch
                {
                    0 => null, 1 => "unknown", 2 => " TELEPHONY-CLASS-APPLICATION ", _ => ExpectedIds[0]
                }).ToArray();
                var dto = JsonSerializer.Deserialize<TsubakiSpeechRequest>(JsonSerializer.Serialize(new
                    { input = "Ready.", equalizer = profiles }))!;
                Require(dto.Equalizer!.Length == 16 && dto.Equalizer.SequenceEqual(profiles.Take(16)),
                    "JSON parsing rejected, reordered, deduplicated, or extended the accepted prefix.");
            });
        }

        check("Equalizer ignores a valid 17th profile after 16 unsupported entries", () =>
        {
            string?[] profiles = Enumerable.Repeat<string?>("unknown", 16).Append(ExpectedIds[7]).ToArray();
            var dto = new TsubakiSpeechRequest { Input = "Ready.", Equalizer = profiles };
            Require(AudioEqualizer.Create(profiles, 24000) == null &&
                AudioEqualizer.Create(dto.Equalizer, 24000) == null,
                "Unknown names were removed before limiting the raw list positions.");
        });

        check("Equalizer nulls and unknown names consume prefix positions", () =>
        {
            string?[] profiles = new string?[100];
            profiles[0] = ExpectedIds[0]; profiles[7] = "unknown";
            profiles[8] = ExpectedIds[7]; profiles[15] = ExpectedIds[2]; profiles[16] = ExpectedIds[7];
            float[] expected = Signal(16000), actual = Signal(16000);
            foreach (string id in new[] { ExpectedIds[0], ExpectedIds[7], ExpectedIds[2] })
                AudioEqualizer.Create([id], 24000)!.Apply(expected);
            AudioEqualizer.Create(profiles, 24000)!.Apply(actual);
            Require(expected.AsSpan().SequenceEqual(actual), "Entries beyond the raw prefix changed PCM.");
        });

        check("Equalizer caps 1000 repetitions at exactly 16 filtering passes", () =>
        {
            float[] expected = Signal(16000), actual = Signal(16000);
            for (int i = 0; i < 16; i++) AudioEqualizer.Create([ExpectedIds[7]], 24000)!.Apply(expected);
            AudioEqualizer.Create(Enumerable.Repeat(ExpectedIds[7], 1000).ToArray(), 24000)!.Apply(actual);
            Require(expected.AsSpan().SequenceEqual(actual), "Repeated profiles were deduplicated or applied beyond 16 passes.");
        });

        check("Equalizer does not read beyond 16 entries in a million-entry internal list", () =>
        {
            float[] expected = Signal(16000), actual = Signal(16000);
            for (int i = 0; i < 16; i++) AudioEqualizer.Create([ExpectedIds[7]], 24000)!.Apply(expected);
            var profiles = new GuardedProfileList(Enumerable.Repeat<string?>(ExpectedIds[7], 16).ToArray(), 1_000_000);
            AudioEqualizer.Create(profiles, 24000)!.Apply(actual);
            Require(expected.AsSpan().SequenceEqual(actual), "The internal list did not apply its full accepted prefix.");
        });

        check("Equalizer oversized arrays retain strict JSON element validation", () =>
        {
            string items = string.Join(",", Enumerable.Repeat("\"telephony-class-application\"", 16).Append("1"));
            bool rejected = false;
            try { JsonSerializer.Deserialize<TsubakiSpeechRequest>("{\"input\":\"Ready.\",\"equalizer\":[" + items + "]}"); }
            catch (JsonException) { rejected = true; }
            Require(rejected, "The array limit silently relaxed the string-ID JSON contract.");
        });

        check("Equalizer bounded bypass and accepted DTO arrays allocate no managed bytes after warmup", () =>
        {
            string?[] oversized = Enumerable.Repeat<string?>(" unknown ", 4096).ToArray();
            string?[] accepted = Enumerable.Repeat<string?>(null, 16).ToArray();
            var dto = new TsubakiSpeechRequest { Input = "Ready." };
            for (int i = 0; i < 2048; i++) { AudioEqualizer.Create(oversized, 24000); dto.Equalizer = accepted; }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4096; i++) { AudioEqualizer.Create(oversized, 24000); dto.Equalizer = accepted; }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0, $"The bounded bypass or accepted DTO array allocated {allocated} managed bytes.");
        });
    }

    /// <summary>Exposes a large logical count and fails if production reads or enumerates the discarded suffix.</summary>
    private sealed class GuardedProfileList(string?[] prefix, int count) : IReadOnlyList<string?>
    {
        public int Count => count;
        public string? this[int index]
        {
            get
            {
                Require((uint)index < (uint)prefix.Length, "The equalizer inspected a discarded list entry.");
                return prefix[index];
            }
        }

        /// <summary>Rejects unbounded enumeration of the synthetic list.</summary>
        public IEnumerator<string?> GetEnumerator() => throw new InvalidOperationException("The equalizer enumerated a discarded suffix.");

        /// <summary>Rejects non-generic enumeration through the same guard.</summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Measures steady-state sine RMS gain independently of the production filter coefficients.</summary>
    private static double ToneGain(string[] ids, int rate, int frequency)
    {
        float[] samples = new float[rate];
        double inputPower = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(.125 * Math.Sin(2 * Math.PI * frequency * i / rate));
            if (i >= rate / 2) inputPower += samples[i] * (double)samples[i];
        }
        AudioEqualizer.Create(ids, rate)!.Apply(samples);
        double outputPower = 0;
        for (int i = rate / 2; i < samples.Length; i++) outputPower += samples[i] * (double)samples[i];
        return Math.Sqrt(outputPower / inputPower);
    }

    /// <summary>Builds bounded deterministic broadband PCM without synthesis or native dependencies.</summary>
    private static float[] Signal(int length)
    {
        var random = new Random(71291);
        float[] samples = new float[length];
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(random.NextDouble() * .5 - .25);
        return samples;
    }

    /// <summary>Throws with the supplied diagnostic when a regression assertion fails.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
