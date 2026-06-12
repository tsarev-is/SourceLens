using SourceLens.Domain.Entities;

namespace SourceLens.Domain;

/// <summary>
/// Выбор движка ответов в таблице app_settings (приоритет над appsettings);
/// дефолты приходят из конфига через composition root.
/// </summary>
public class EngineSettings
{
    public const string ProviderKey = "engine.provider";
    public const string ClaudeModelKey = "engine.claude.model";
    public const string CodexModelKey = "engine.codex.model";

    public const string ClaudeProvider = "Claude";
    public const string CodexProvider = "Codex";

    private readonly Func<SourceLensContext> _getContext;
    private readonly string _defaultProvider;
    private readonly string _defaultClaudeModel;
    private readonly string _defaultCodexModel;

    public EngineSettings(Func<SourceLensContext> getContext, string defaultProvider = "",
        string defaultClaudeModel = "", string defaultCodexModel = "")
    {
        _getContext = getContext;
        _defaultProvider = defaultProvider;
        _defaultClaudeModel = defaultClaudeModel;
        _defaultCodexModel = defaultCodexModel;
    }

    public string Provider => Read(ProviderKey, _defaultProvider);

    public string GetModel(string provider)
    {
        return provider switch
        {
            ClaudeProvider => Read(ClaudeModelKey, _defaultClaudeModel),
            CodexProvider => Read(CodexModelKey, _defaultCodexModel),
            _ => string.Empty,
        };
    }

    public void Save(string provider, string model)
    {
        using var ctx = _getContext();
        ctx.SetSetting(ProviderKey, provider);
        var modelKey = provider switch
        {
            ClaudeProvider => ClaudeModelKey,
            CodexProvider => CodexModelKey,
            _ => null,
        };
        if (modelKey != null)
            ctx.SetSetting(modelKey, model);
        ctx.SaveChanges();
    }

    private string Read(string key, string fallback)
    {
        using var ctx = _getContext();
        return ctx.GetSetting(key) ?? fallback;
    }
}
