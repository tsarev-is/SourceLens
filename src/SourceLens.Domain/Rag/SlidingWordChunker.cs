using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain.Rag;

public class SlidingWordChunker : IChunker
{
    private readonly ChunkerOptions _options;
    private readonly ITokenCounter? _tokenCounter;

    public SlidingWordChunker(ChunkerOptions options, ITokenCounter? tokenCounter = null)
    {
        if (options.WindowSize <= 0)
            throw new ArgumentException("WindowSize must be positive", nameof(options));
        if (options.Overlap < 0 || options.Overlap >= options.WindowSize)
            throw new ArgumentException("Overlap must be in [0, WindowSize)", nameof(options));
        if (tokenCounter != null && options.MaxTokens <= 0)
            throw new ArgumentException("MaxTokens must be positive when a token counter is supplied", nameof(options));
        _options = options;
        _tokenCounter = tokenCounter;
    }

    public async IAsyncEnumerable<Chunk> Chunk(IAsyncEnumerable<DocumentSegment> segments, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var ordinal = 0;
        var buffer = new List<string>(_options.WindowSize);
        var tokens = new List<int>(_options.WindowSize); // per-word token counts, parallel to buffer; unused when no counter
        var runningTokens = 0;
        var firstLocation = string.Empty;
        var bufferIsCarryover = false;

        await foreach (var segment in segments.WithCancellation(ct))
        {
            if (segment.BreakHint == SegmentBreakHint.Chapter && buffer.Count > 0)
            {
                if (!bufferIsCarryover)
                    yield return BuildChunk(ordinal++, buffer, firstLocation);
                buffer.Clear();
                tokens.Clear();
                runningTokens = 0;
                firstLocation = string.Empty;
                bufferIsCarryover = false;
            }

            var words = Split(segment.Text);
            foreach (var word in words)
            {
                if (buffer.Count == 0)
                    firstLocation = segment.SourceLocation;

                buffer.Add(word);
                bufferIsCarryover = false;
                if (_tokenCounter != null)
                {
                    var wordTokens = _tokenCounter.CountTokens(word);
                    tokens.Add(wordTokens);
                    runningTokens += wordTokens;
                }

                // Закрываем чанк по словам ИЛИ по токенам (но не одиночным словом — иначе пара чанк/перекрытие
                // с гигантским словом зациклилась бы; такое слово эмбеддер обрежет сам и предупредит).
                var tokenBudgetReached = _tokenCounter != null && buffer.Count > 1 && runningTokens >= _options.MaxTokens;
                if (buffer.Count >= _options.WindowSize || tokenBudgetReached)
                {
                    yield return BuildChunk(ordinal++, buffer, firstLocation);
                    (buffer, tokens, runningTokens) = Carryover(buffer, tokens);
                    firstLocation = segment.SourceLocation;
                    bufferIsCarryover = true;
                }
            }
        }

        if (buffer.Count > 0 && !bufferIsCarryover)
            yield return BuildChunk(ordinal, buffer, firstLocation);
    }

    private (List<string> Buffer, List<int> Tokens, int RunningTokens) Carryover(List<string> buffer, List<int> tokens)
    {
        if (_options.Overlap == 0)
            return (new List<string>(_options.WindowSize), new List<int>(_options.WindowSize), 0);

        var carry = new List<string>(_options.WindowSize);
        carry.AddRange(buffer.GetRange(buffer.Count - _options.Overlap, _options.Overlap));

        var carryTokens = new List<int>(_options.WindowSize);
        var sum = 0;
        if (_tokenCounter != null)
        {
            carryTokens.AddRange(tokens.GetRange(tokens.Count - _options.Overlap, _options.Overlap));
            foreach (var t in carryTokens)
                sum += t;
        }
        return (carry, carryTokens, sum);
    }

    private static Chunk BuildChunk(int ordinal, List<string> words, string sourceLocation)
    {
        return new Chunk
        {
            Ordinal = ordinal,
            Text = string.Join(' ', words),
            SourceLocation = sourceLocation,
            TokenCount = words.Count,
        };
    }

    private static IEnumerable<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (start >= 0)
                {
                    yield return text.Substring(start, i - start);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }
        if (start >= 0)
            yield return text.Substring(start);
    }
}
