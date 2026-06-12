namespace SourceLens.Domain;

public class RagDialogOptions
{
    /// <summary>
    /// Максимум пар Q/A в контексте диалога. 0 — контекст отключён (история всё равно пишется).
    /// </summary>
    public int HistoryDepth { get; set; } = 20;

    /// <summary>
    /// Жёсткий бюджет символов на блок DIALOG_HISTORY; старые пары отбрасываются первыми.
    /// </summary>
    public int MaxHistoryChars { get; set; } = 13000;
}
