namespace SourceLens.Domain.Rag;

public class ChunkerOptions
{
    public string Version { get; set; } = "v2";

    // Верхняя граница чанка в словах. При включённом подсчёте токенов (ITokenCounter) реальным
    // лимитом обычно становится MaxTokens — для не-латиницы чанк закрывается по токенам раньше,
    // чем по словам; для латиницы (≈1.3 ток/слово) первым срабатывает этот словесный потолок.
    public int WindowSize { get; set; } = 250;

    public int Overlap { get; set; } = 50;

    // Бюджет токенов тела чанка. Держим заметно ниже capacity эмбеддера (512 вкл. BOS/EOS),
    // оставляя запас на embed-префикс "passage: " и служебные токены. Без token-aware режима
    // (ITokenCounter == null) не используется. См. LocalOnnxEmbedder.BuildIdsWithSpecials.
    public int MaxTokens { get; set; } = 480;
}
