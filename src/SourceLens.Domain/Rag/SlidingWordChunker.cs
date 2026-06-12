using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain.Rag;

public class SlidingWordChunker : IChunker
{
    private readonly ChunkerOptions _options;

    public SlidingWordChunker(ChunkerOptions options)
    {
        if (options.WindowSize <= 0)
            throw new ArgumentException("WindowSize must be positive", nameof(options));
        if (options.Overlap < 0 || options.Overlap >= options.WindowSize)
            throw new ArgumentException("Overlap must be in [0, WindowSize)", nameof(options));
        _options = options;
    }

    public async IAsyncEnumerable<Chunk> Chunk(IAsyncEnumerable<DocumentSegment> segments, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var ordinal = 0;
        var buffer = new List<string>(_options.WindowSize);
        var firstLocation = string.Empty;
        var bufferIsCarryover = false;

        await foreach (var segment in segments.WithCancellation(ct))
        {
            if (segment.BreakHint == SegmentBreakHint.Chapter && buffer.Count > 0)
            {
                if (!bufferIsCarryover)
                    yield return BuildChunk(ordinal++, buffer, firstLocation);
                buffer.Clear();
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

                if (buffer.Count >= _options.WindowSize)
                {
                    yield return BuildChunk(ordinal++, buffer, firstLocation);
                    buffer = Carryover(buffer);
                    firstLocation = segment.SourceLocation;
                    bufferIsCarryover = true;
                }
            }
        }

        if (buffer.Count > 0 && !bufferIsCarryover)
            yield return BuildChunk(ordinal, buffer, firstLocation);
    }

    private List<string> Carryover(List<string> buffer)
    {
        if (_options.Overlap == 0)
            return new List<string>(_options.WindowSize);

        var carry = new List<string>(_options.WindowSize);
        carry.AddRange(buffer.GetRange(buffer.Count - _options.Overlap, _options.Overlap));
        return carry;
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
