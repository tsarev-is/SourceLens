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
    public const string ClaudeBinaryPathKey = "engine.claude.binaryPath";
    public const string CodexBinaryPathKey = "engine.codex.binaryPath";

    public const string ClaudeProvider = "Claude";
    public const string CodexProvider = "Codex";

    private readonly Func<SourceLensContext> _getContext;
    private readonly string _defaultProvider;
    private readonly string _defaultClaudeModel;
    private readonly string _defaultCodexModel;
    private readonly string _defaultClaudeBinaryPath;
    private readonly string _defaultCodexBinaryPath;

    public EngineSettings(Func<SourceLensContext> getContext, string defaultProvider = "",
        string defaultClaudeModel = "", string defaultCodexModel = "",
        string defaultClaudeBinaryPath = "", string defaultCodexBinaryPath = "")
    {
        _getContext = getContext;
        _defaultProvider = defaultProvider;
        _defaultClaudeModel = defaultClaudeModel;
        _defaultCodexModel = defaultCodexModel;
        _defaultClaudeBinaryPath = defaultClaudeBinaryPath;
        _defaultCodexBinaryPath = defaultCodexBinaryPath;
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

    /// <summary>
    /// Путь к бинарю CLI движка: override из app_settings или дефолт из конфига.
    /// </summary>
    public string GetBinaryPath(string provider)
    {
        return provider switch
        {
            ClaudeProvider => Read(ClaudeBinaryPathKey, _defaultClaudeBinaryPath),
            CodexProvider => Read(CodexBinaryPathKey, _defaultCodexBinaryPath),
            _ => string.Empty,
        };
    }

    public void SaveBinaryPath(string provider, string binaryPath)
    {
        var key = provider switch
        {
            ClaudeProvider => ClaudeBinaryPathKey,
            CodexProvider => CodexBinaryPathKey,
            _ => null,
        };
        if (key == null)
            return;

        using var ctx = _getContext();
        ctx.SetSetting(key, binaryPath);
        ctx.SaveChanges();
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
