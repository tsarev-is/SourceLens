namespace SourceLens.Domain.Rag;

public class ChunkerOptions
{
    public string Version { get; set; } = "v1";

    public int WindowSize { get; set; } = 500;

    public int Overlap { get; set; } = 100;
}
