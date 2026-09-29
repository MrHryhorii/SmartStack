using System.Buffers;

namespace STT_Runner.Services;

// Sample offsets refer to the decoded 16 kHz stream, including VAD pre-roll.
public sealed record AudioPiece(IMemoryOwner<float> Owner, int Length, long StartSample);

public sealed record TranscriptWord(double Start, double End, string Word);

public sealed record TranscriptSegment(
    int Id, double Start, double End, string Text, int[] Tokens,
    double Temperature, double AvgLogprob, double CompressionRatio,
    double NoSpeechProb);

public sealed record RecognizedChunk(
    string Text, string? Language, double Start, double End,
    IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<TranscriptWord> Words);

public sealed record TranscriptResult(
    string Text, string Language, double Duration,
    IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<TranscriptWord> Words);
