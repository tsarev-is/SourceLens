namespace SourceLens.Domain.Rag;

public class RetrievalOptions
{
    public int TopK { get; set; } = 5;

    public int MinQueryLength { get; set; } = 20;
}
