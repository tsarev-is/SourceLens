namespace SourceLens.Domain;

/// <summary>
/// Фаза обработки вопроса; UI показывает «Retrieving sources…» / «Generating answer…».
/// </summary>
public enum RagPhase
{
    Retrieving,
    Generating,
}
