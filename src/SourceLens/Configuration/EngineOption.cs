namespace SourceLens.Configuration;

/// <summary>
/// Описание движка ответов для UI (Settings-окно): провайдер, бинарник CLI для probe
/// и модель по умолчанию. Список доступных моделей сообщает сам CLI (CliModelCatalog).
/// Заполняется в composition root.
/// </summary>
public sealed class EngineOption
{
    public required string Provider { get; init; }

    public required string BinaryPath { get; init; }

    public required string DefaultModel { get; init; }
}
