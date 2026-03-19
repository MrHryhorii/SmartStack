using ONNX_Runner.Models;

namespace ONNX_Runner.Services;

public class TextChunker(ChunkerSettings settings)
{
    private readonly int _maxLength = settings.MaxChunkLength > 50 ? settings.MaxChunkLength : 250;
    private const string EmergencyGlue = "_";

    private static readonly char[] SentenceTerminators = ['.', '!', '?', '\n', '。', '！', '？', '؟', '۔', '։', ';'];
    private static readonly char[] PauseMarks = [',', ';', ':', '-', '—', '，', '、', '；', '：'];

    public List<string> Split(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        int currentIndex = 0;

        while (currentIndex < text.Length)
        {
            int nextTerminator = text.IndexOfAny(SentenceTerminators, currentIndex);

            int endIndex;
            if (nextTerminator == -1) endIndex = text.Length;
            else
            {
                endIndex = nextTerminator + 1;
                while (endIndex < text.Length && SentenceTerminators.Contains(text[endIndex])) endIndex++;
            }

            string sentence = text[currentIndex..endIndex].Trim();

            if (!string.IsNullOrWhiteSpace(sentence))
            {
                if (sentence.Length <= _maxLength) result.Add(sentence);
                else result.AddRange(SplitLongSentence(sentence)); // Аварійна нарізка
            }

            currentIndex = endIndex;
        }

        return result;
    }

    private List<string> SplitLongSentence(string sentence)
    {
        var result = new List<string>();
        int currentIndex = 0;

        while (currentIndex < sentence.Length)
        {
            int remainingLength = sentence.Length - currentIndex;
            if (remainingLength <= _maxLength)
            {
                result.Add(sentence[currentIndex..].Trim());
                break;
            }

            int windowEnd = currentIndex + _maxLength;
            int splitIndex = FindLastOccurrence(sentence, currentIndex, windowEnd, PauseMarks);

            if (splitIndex == -1) splitIndex = sentence.LastIndexOf(' ', windowEnd - 1, _maxLength);

            if (splitIndex == -1 || splitIndex < currentIndex) splitIndex = windowEnd;
            else splitIndex++;

            string chunk = sentence[currentIndex..splitIndex].Trim();

            if (!string.IsNullOrEmpty(chunk))
            {
                char lastChar = chunk[^1];
                if (!char.IsPunctuation(lastChar)) chunk += EmergencyGlue;
                result.Add(chunk);
            }

            currentIndex = splitIndex;
        }

        return result;
    }

    private static int FindLastOccurrence(string text, int startIndex, int endIndex, char[] charsToFind) =>
        text.LastIndexOfAny(charsToFind, endIndex - 1, endIndex - startIndex);
}