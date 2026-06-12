namespace SourceLens.Domain.Rag.Models;

public class DocumentSegment
{
    public string Text { get; init; } = string.Empty;

    public string SourceLocation { get; init; } = string.Empty;

    public SegmentBreakHint BreakHint { get; init; } = SegmentBreakHint.None;
}
