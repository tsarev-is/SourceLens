namespace SourceLens.Domain;

/// <summary>
/// Держит текущий движок ответов. Клиенты пересоздаются фабрикой-делегатом
/// `(provider, model) => AbstractLlmInferences` (сама фабрика — в composition root);
/// выбор персистится в app_settings и восстанавливается при создании.
/// </summary>
public class AnswerEngineManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly Func<string, string, AbstractLlmInferences> _factory;
    private readonly EngineSettings _settings;

    public AnswerEngineManager(Func<string, string, AbstractLlmInferences> factory, EngineSettings settings)
    {
        _factory = factory;
        _settings = settings;
        Provider = settings.Provider;
        Model = settings.GetModel(Provider);
        Current = factory(Provider, Model);
    }

    public AbstractLlmInferences Current { get; private set; }

    public string Provider { get; private set; }

    public string Model { get; private set; }

    /// <summary>
    /// Подпись для статус-бара: `Claude · sonnet` / `Not connected`.
    /// </summary>
    public string EngineLabel => string.IsNullOrWhiteSpace(Provider)
        ? "Not connected"
        : string.IsNullOrWhiteSpace(Model) ? Provider : $"{Provider} · {Model}";

    public AbstractLlmInferences Switch(string provider, string model)
    {
        Provider = provider;
        Model = model;
        Current = _factory(provider, model);
        _settings.Save(provider, model);
        Logger.Info("Answer engine switched to {0}", EngineLabel);
        return Current;
    }
}
