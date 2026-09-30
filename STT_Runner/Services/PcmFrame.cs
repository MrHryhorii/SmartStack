namespace STT_Runner.Services;

/// <summary>A pooled PCM buffer with its actual length, excluding final VAD padding.</summary>
public readonly record struct PcmFrame(float[] Samples, int Length);
