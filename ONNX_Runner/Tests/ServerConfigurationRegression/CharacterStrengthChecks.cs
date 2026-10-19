using System.Reflection;
using ONNX_Runner.Models;
using ONNX_Runner.Services;

internal static class CharacterStrengthChecks
{
    /// <summary>Checks declared internal strength and full-output character independently of historical mixer fits.</summary>
    internal static void Run(Action<string, Action> check)
    {
        foreach (var point in new (float Amount, float Drive)[]
        {
            (.01f, 1.0956982f), (.05f, 1.4688733f), (.1f, 1.9137017f), (.25f, 3.1039183f),
            (.5f, 4.606717f), (.75f, 5.508396f), (.95f, 5.7969337f), (1f, 5.808956f)
        })
        {
            check($"Overdrive prepares the declared eased strength at {point.Amount:R}", () =>
            {
                var engine = NewEngine(22050);
                engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.Overdrive, point.Amount);
                float drive = (float)Field(engine, "_overdriveDrive");
                Require(MathF.Abs(drive - point.Drive) < 1e-6f, "The request is no longer mapped to the declared wet strength.");
            });
        }

        check("Tactical codec strength follows the same eased input as radio filtering", () =>
        {
            var engine = NewEngine(22050);
            engine.ApplyEffect(Span<float>.Empty, VoiceEffectType.TacticalRadio, .5f);
            object radio = Field(engine, "_radio");
            float root = (float)radio.GetType().GetField("TacticalSqrtAmount")!.GetValue(radio)!;
            Require(MathF.Abs(root * root - .75f) < 1e-6f, "The radio codec retained linear internal strength.");
        });

        check("Bitcrusher reaches seven-bit wet quantization at request midpoint", () =>
        {
            var engine = NewEngine(22050);
            engine.ApplyEffect(new float[] { .2f }, VoiceEffectType.Bitcrusher, .5f);
            object crusher = Field(engine, "_bc");
            float held = (float)crusher.GetType().GetField("Hold")!.GetValue(crusher)!;
            Require(held == .1875f, "Midpoint no longer reaches the declared stronger quantization grid.");
        });

        foreach (FullReference reference in FullReferences)
        {
            check($"{reference.Effect} full output preserves its scaled character at {reference.Rate} Hz", () =>
            {
                float[] signal = Signal(reference.Rate, reference.Rate * 3);
                NewEngine(reference.Rate).ApplyEffect(signal, reference.Effect, 1f);
                double power = 0; float peak = 0;
                foreach (float sample in signal)
                {
                    Require(float.IsFinite(sample), "Non-finite full-effect output.");
                    power += sample * (double)sample; peak = Math.Max(peak, MathF.Abs(sample));
                }
                Require(Math.Abs(10 * Math.Log10(power / signal.Length) - reference.RmsDbfs) < .005,
                    "Full-output level differs from the independently captured accepted waveform.");
                Require(MathF.Abs(peak - reference.Peak) < 3e-6f, "The full-output peak differs from the accepted reference.");
                for (int i = 0; i < reference.Samples.Length; i++)
                {
                    int index = (int)((i + .5) * signal.Length / reference.Samples.Length);
                    Require(MathF.Abs(signal[index] - reference.Samples[i]) < 3e-6f,
                        "Waveform checkpoints differ from the accepted reference.");
                }
            });
        }
    }

    /// <summary>Reads only the prepared state needed to verify the public intensity mapping.</summary>
    private static object Field(AudioEffectsEngine engine, string name) =>
        typeof(AudioEffectsEngine).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;

    /// <summary>Creates fixed request state without model weights or native synthesis.</summary>
    private static AudioEffectsEngine NewEngine(int rate)
    {
        var engine = new AudioEffectsEngine(new(), rate); engine.Reset(123456); return engine;
    }

    /// <summary>Recreates the unchanged multi-band input used by independent full-strength references.</summary>
    private static float[] Signal(int rate, int count)
    {
        var signal = new float[count];
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / rate;
            signal[i] = (float)((.18 * Math.Sin(2 * Math.PI * 110 * t) + .08 * Math.Sin(2 * Math.PI * 330 * t)
                + .05 * Math.Sin(2 * Math.PI * 1700 * t) + .03 * Math.Sin(2 * Math.PI * Math.Min(3200, rate * .3) * t))
                * (.7 + .3 * Math.Sin(2 * Math.PI * .73 * t)));
        }
        return signal;
    }

    /// <summary>Reports a violated signal contract to the console runner.</summary>
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private readonly record struct FullReference(VoiceEffectType Effect, int Rate, double RmsDbfs, float Peak, float[] Samples);

    // Frozen from the accepted full algorithms and trims, including the stronger Overdrive prototype.
    // The source algorithm and reference generator are retained in the development research archive.
    private static readonly FullReference[] FullReferences =
    [
        new(VoiceEffectType.Overdrive, 8000, -22.821074668294223, 0.18930212f, [0.002614439f, 0.06788077f, -0.1279901f, -0.13453953f, 0.03777961f, -0.000113316564f, 0.027665516f, 0.06812735f, 0.0032862201f, 0.06766997f, -0.11587276f, -0.10734002f, 0.030874584f, -0.0001406217f, 0.036790054f, 0.08164571f]),
        new(VoiceEffectType.Overdrive, 22050, -22.891515568221106, 0.19096987f, [0.0019324797f, 0.049219057f, -0.14389578f, -0.1343588f, 0.035879433f, 0.0035229886f, 0.023340758f, 0.061619304f, 0.0024924225f, 0.049075183f, -0.13227837f, -0.10729705f, 0.02929975f, 0.0040031387f, 0.031195853f, 0.07398489f]),
        new(VoiceEffectType.Overdrive, 48000, -22.915161418671644, 0.19054013f, [-0.00041781698f, 0.063694425f, -0.12900971f, -0.13404068f, 0.03641783f, -0.0014714438f, 0.026064195f, 0.06489521f, -0.00023646365f, 0.06351392f, -0.11700366f, -0.10704821f, 0.029753238f, -0.0016832121f, 0.03469413f, 0.077841066f]),
        new(VoiceEffectType.TacticalRadio, 8000, -21.251084671223502, 0.24603088f, [0.037100915f, -0.03416151f, -0.07873353f, -0.10530375f, 0.0032215596f, -0.016523825f, -0.07241034f, -0.08910538f, 0.07935804f, 0.12507196f, -0.098976806f, -0.16523975f, -0.024278654f, 0.005240898f, 0.008572341f, -0.04335433f]),
        new(VoiceEffectType.TacticalRadio, 22050, -20.426937533450477, 0.24766803f, [0.06260288f, 0.08347759f, -0.13234189f, -0.15410344f, 0.032430016f, -0.016411535f, -0.07204659f, 0.028348302f, 0.13291825f, 0.07609257f, -0.14036654f, -0.105234556f, -0.024509372f, -0.050732356f, -0.1458259f, 0.058156226f]),
        new(VoiceEffectType.TacticalRadio, 48000, -20.589512430543508, 0.2510568f, [0.109351724f, 0.039668348f, -0.11525303f, -0.09907622f, 0.10152342f, -0.025557086f, -0.023940343f, 0.084513895f, 0.17793946f, -0.0062692105f, -0.11628724f, -0.15398075f, 0.087634236f, -0.044999972f, -0.12210464f, 0.09784675f]),
        new(VoiceEffectType.FmRadio, 8000, -21.565112864828009, 0.25236416f, [0.085564874f, 0.08542257f, -0.1527408f, -0.16423297f, 0.0022716373f, -0.0058220006f, -0.05074758f, -0.00014449596f, 0.098326236f, 0.084221445f, -0.13318571f, -0.12901925f, 0.0024038726f, -0.007899115f, -0.06638351f, -0.0005959412f]),
        new(VoiceEffectType.FmRadio, 22050, -21.923509521171731, 0.23540556f, [0.105027154f, 0.023197494f, -0.16296563f, -0.12124378f, 0.027342904f, -0.018050179f, -0.050769288f, 0.07715491f, 0.1224364f, 0.023468371f, -0.14263164f, -0.093181156f, 0.021741481f, -0.01823566f, -0.065972656f, 0.09424255f]),
        new(VoiceEffectType.FmRadio, 48000, -21.961064269073294, 0.23632815f, [0.10309997f, 0.047539108f, -0.16460934f, -0.1320966f, 0.017744703f, -0.017009083f, -0.05160447f, 0.037310645f, 0.11821575f, 0.047632594f, -0.14414132f, -0.10253859f, 0.0139907515f, -0.019166514f, -0.068808585f, 0.044973724f]),
        new(VoiceEffectType.RingModulator, 8000, -21.448549508943625, 0.32421538f, [0.033853468f, 0.069525845f, -0.10201417f, 0.14014472f, 0.07126545f, 0.0088278195f, -0.034802373f, 0.054401956f, 0.039272346f, 0.066284776f, -0.089245275f, 0.10776867f, 0.05794054f, 0.0098739555f, -0.046696402f, 0.06606766f]),
        new(VoiceEffectType.RingModulator, 22050, -21.52089782706199, 0.33798987f, [0.037410375f, 0.05505436f, -0.11943263f, 0.13928877f, 0.06840444f, 0.009449757f, -0.026397398f, 0.039204236f, 0.043434177f, 0.05330316f, -0.108158715f, 0.108023815f, 0.055172443f, 0.010213189f, -0.037853584f, 0.048624665f]),
        new(VoiceEffectType.RingModulator, 48000, -21.542734157261396, 0.34197024f, [0.03392184f, 0.07201924f, -0.09431193f, 0.13676937f, 0.07048634f, 0.008844818f, -0.033528768f, 0.052798007f, 0.03931015f, 0.06669107f, -0.08846702f, 0.10684029f, 0.056585677f, 0.00956782f, -0.045844324f, 0.0641069f]),
    ];
}
