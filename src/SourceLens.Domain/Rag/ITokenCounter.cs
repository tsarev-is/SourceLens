namespace SourceLens.Domain.Rag;

/// <summary>
/// Считает, во сколько токенов модели развернётся фрагмент текста — тем же токенизатором,
/// что и <see cref="IEmbedder"/>. Нужен чанкеру, чтобы держать чанк под лимитом эмбеддера
/// независимо от языка (для не-латиницы SentencePiece даёт ~2–3 токена на слово, и счёт слов
/// перестаёт быть надёжной оценкой длины в токенах).
/// </summary>
public interface ITokenCounter
{
    /// <summary>
    /// Число токенов тела (без служебных BOS/EOS и без embed-префикса query:/passage:).
    /// </summary>
    int CountTokens(string text);
}
