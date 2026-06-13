using NUnit.Framework;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Tests.Rag;

public class SlidingWordChunkerTests
{
    private static async IAsyncEnumerable<DocumentSegment> Segments(params DocumentSegment[] items)
    {
        foreach (var s in items)
        {
            await Task.Yield();
            yield return s;
        }
    }

    private static DocumentSegment Seg(string text, string loc = "", SegmentBreakHint hint = SegmentBreakHint.None)
        => new() { Text = text, SourceLocation = loc, BreakHint = hint };

    private static async Task<List<Chunk>> Collect(SlidingWordChunker chunker, IAsyncEnumerable<DocumentSegment> segments)
    {
        var list = new List<Chunk>();
        await foreach (var c in chunker.Chunk(segments))
            list.Add(c);
        return list;
    }

    [Test]
    public async Task EmptyInput_ProducesNoChunks()
    {
        var chunker = new SlidingWordChunker(new ChunkerOptions { WindowSize = 5, Overlap = 1 });

        var chunks = await Collect(chunker, Segments());

        Assert.That(chunks, Is.Empty);
    }

    [Test]
    public async Task ShorterThanWindow_ProducesSingleChunk()
    {
        var chunker = new SlidingWordChunker(new ChunkerOptions { WindowSize = 10, Overlap = 2 });

        var chunks = await Collect(chunker, Segments(Seg("one two three", "p.1")));

        Assert.That(chunks, Has.Count.EqualTo(1));
        Assert.That(chunks[0].Text, Is.EqualTo("one two three"));
        Assert.That(chunks[0].TokenCount, Is.EqualTo(3));
        Assert.That(chunks[0].SourceLocation, Is.EqualTo("p.1"));
        Assert.That(chunks[0].Ordinal, Is.EqualTo(0));
    }

    [Test]
    public async Task SlidesWithOverlap()
    {
        var chunker = new SlidingWordChunker(new ChunkerOptions { WindowSize = 4, Overlap = 2 });
        var text = "a b c d e f g h";

        var chunks = await Collect(chunker, Segments(Seg(text, "p.1")));

        Assert.That(chunks.Select(c => c.Text), Is.EqualTo(new[] { "a b c d", "c d e f", "e f g h" }));
        Assert.That(chunks.Select(c => c.Ordinal), Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public async Task ChapterBreak_FlushesCurrentChunkAndStartsFresh()
    {
        var chunker = new SlidingWordChunker(new ChunkerOptions { WindowSize = 10, Overlap = 2 });

        var chunks = await Collect(chunker, Segments(
            Seg("alpha beta", "ch.1", SegmentBreakHint.Chapter),
            Seg("gamma delta", "ch.2", SegmentBreakHint.Chapter)));

        Assert.That(chunks, Has.Count.EqualTo(2));
        Assert.That(chunks[0].Text, Is.EqualTo("alpha beta"));
        Assert.That(chunks[0].SourceLocation, Is.EqualTo("ch.1"));
        Assert.That(chunks[1].Text, Is.EqualTo("gamma delta"));
        Assert.That(chunks[1].SourceLocation, Is.EqualTo("ch.2"));
    }

    [Test]
    public async Task PageBreak_KeepsAccumulating()
    {
        var chunker = new SlidingWordChunker(new ChunkerOptions { WindowSize = 10, Overlap = 0 });

        var chunks = await Collect(chunker, Segments(
            Seg("alpha beta", "p.1", SegmentBreakHint.Page),
            Seg("gamma delta", "p.2", SegmentBreakHint.Page)));

        Assert.That(chunks, Has.Count.EqualTo(1));
        Assert.That(chunks[0].Text, Is.EqualTo("alpha beta gamma delta"));
    }

    [Test]
    public async Task ChapterBreakRightAfterCarryover_DoesNotEmitPhantomChunk()
    {
        var chunker = new SlidingWordChunker(new ChunkerOptions { WindowSize = 4, Overlap = 2 });

        var chunks = await Collect(chunker, Segments(
            Seg("a b c d", "p.1", SegmentBreakHint.Page),
            Seg("more text", "ch.2", SegmentBreakHint.Chapter)));

        Assert.That(chunks.Select(c => c.Text), Is.EqualTo(new[] { "a b c d", "more text" }),
            "Chapter break must drop carryover and start fresh");
        Assert.That(chunks.Select(c => c.SourceLocation), Is.EqualTo(new[] { "p.1", "ch.2" }));
        Assert.That(chunks.Select(c => c.Ordinal), Is.EqualTo(new[] { 0, 1 }));
    }

    [Test]
    public async Task ChapterBreakWithEmptyBuffer_NoEmission()
    {
        var chunker = new SlidingWordChunker(new ChunkerOptions { WindowSize = 4, Overlap = 2 });

        var chunks = await Collect(chunker, Segments(
            Seg("", "ch.1", SegmentBreakHint.Chapter),
            Seg("alpha beta", "ch.2", SegmentBreakHint.Chapter)));

        Assert.That(chunks.Select(c => c.Text), Is.EqualTo(new[] { "alpha beta" }));
    }

    [Test]
    public void InvalidOptions_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SlidingWordChunker(new ChunkerOptions { WindowSize = 0, Overlap = 0 }));
        Assert.Throws<ArgumentException>(() => new SlidingWordChunker(new ChunkerOptions { WindowSize = 5, Overlap = 5 }));
        Assert.Throws<ArgumentException>(() => new SlidingWordChunker(new ChunkerOptions { WindowSize = 5, Overlap = 6 }));
    }

    [Test]
    public void TokenCounterWithNonPositiveMaxTokens_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new SlidingWordChunker(new ChunkerOptions { WindowSize = 5, Overlap = 1, MaxTokens = 0 }, new FixedTokenCounter(1)));
    }

    [Test]
    public async Task TokenBudget_FlushesBeforeWordWindow()
    {
        // 3 tokens/word, budget 10 → chunk closes once running tokens reach the budget (after the 4th word: 12 ≥ 10).
        var chunker = new SlidingWordChunker(
            new ChunkerOptions { WindowSize = 100, Overlap = 0, MaxTokens = 10 },
            new FixedTokenCounter(3));

        var chunks = await Collect(chunker, Segments(Seg("a b c d e f g h", "p.1")));

        Assert.That(chunks.Select(c => c.Text), Is.EqualTo(new[] { "a b c d", "e f g h" }));
    }

    [Test]
    public async Task TokenBudget_RespectsOverlapAcrossChunks()
    {
        // Overlap words are carried into the next buffer's running token total, so the budget stays honoured.
        var chunker = new SlidingWordChunker(
            new ChunkerOptions { WindowSize = 100, Overlap = 1, MaxTokens = 10 },
            new FixedTokenCounter(3));

        var chunks = await Collect(chunker, Segments(Seg("a b c d e f g", "p.1")));

        // 1st: a b c d (12). Carry "d" (3). 2nd accumulates d e f g → flush at "d e f g" (12). Carry "g". Tail "g" only is carryover → no phantom chunk.
        Assert.That(chunks.Select(c => c.Text), Is.EqualTo(new[] { "a b c d", "d e f g" }));
    }

    [Test]
    public async Task WordWindowStillCapsTokenSparseText()
    {
        // 1 token/word never reaches the big budget, so the word window remains the limit (back-compat behaviour).
        var chunker = new SlidingWordChunker(
            new ChunkerOptions { WindowSize = 4, Overlap = 2, MaxTokens = 1000 },
            new FixedTokenCounter(1));

        var chunks = await Collect(chunker, Segments(Seg("a b c d e f g h", "p.1")));

        Assert.That(chunks.Select(c => c.Text), Is.EqualTo(new[] { "a b c d", "c d e f", "e f g h" }));
    }

    private sealed class FixedTokenCounter : ITokenCounter
    {
        private readonly int _perWord;
        public FixedTokenCounter(int perWord) => _perWord = perWord;
        public int CountTokens(string text) => _perWord;
    }
}
