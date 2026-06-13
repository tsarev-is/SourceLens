using Newtonsoft.Json;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Entities.Models;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Domain;

/// <summary>
/// Оркестрация RAG-диалога: ретрив источников, контекст истории, вызов LLM и персист обменов.
/// Старт приложения — всегда новый пустой диалог (прошлые доступны через ResumeSession);
/// пустая последняя сессия переиспользуется, чтобы перезапуски не плодили «(empty dialog)».
/// </summary>
public class RagDialogManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private const int MaxTitleLength = 60;

    private readonly Func<SourceLensContext> _getContext;
    private readonly Func<AbstractLlmInferences> _getLlm;
    private readonly IKnowledgeRetriever _retriever;
    private readonly RetrievalOptions _retrievalOptions;

    // Область поиска сессии — id коллекции (null — «All sources», вся библиотека). Кэш + персист в app_settings.
    private readonly Dictionary<int, int?> _scopeCollectionBySession = new();

    private const string ScopeSettingPrefix = "rag.scope.";

    public RagDialogManager(Func<SourceLensContext> getContext, Func<AbstractLlmInferences> getLlm,
        IKnowledgeRetriever retriever, RetrievalOptions retrievalOptions, RagDialogOptions options)
    {
        _getContext = getContext;
        _getLlm = getLlm;
        _retriever = retriever;
        _retrievalOptions = retrievalOptions;
        Options = options;

        using var ctx = _getContext();
        var last = ctx.GetLastRagSession();
        CurrentSession = last != null && ctx.GetRagExchanges(last.Id).Length == 0
            ? last
            : CreateSession(ctx).GetAwaiter().GetResult();
    }

    public RagDialogOptions Options { get; }

    /// <summary>
    /// Текущий диалог: при старте — новый пустой (или переиспользованная пустая последняя сессия).
    /// </summary>
    public RagSessionItem CurrentSession { get; private set; }

    /// <summary>
    /// Сколько пар Q/A реально войдёт в следующий промпт (индикатор «context: N exchanges»).
    /// </summary>
    public int ContextSize => GetContextPairs(CurrentSession.Id).Length;

    public RagSessionItem[] GetSessions()
    {
        using var ctx = _getContext();
        return ctx.GetRagSessions();
    }

    public RagExchangeView[] GetExchanges(int sessionId)
    {
        using var ctx = _getContext();
        return ctx.GetRagExchanges(sessionId).Select(p => new RagExchangeView
        {
            Id = p.Id,
            CreatedAt = p.CreatedAt,
            Question = p.Question,
            Answer = p.Answer ?? string.Empty,
            Sources = DeserializeSources(p.SourcesJson),
            ScopeName = p.ScopeName,
            ScopeColor = p.ScopeColor,
        }).ToArray();
    }

    /// <summary>
    /// Явный сброс контекста диалога. Пустая текущая сессия переиспользуется.
    /// </summary>
    public async Task<RagSessionItem> StartNewSession()
    {
        await using var ctx = _getContext();
        if (ctx.FindRagSession(CurrentSession.Id) != null && ctx.GetRagExchanges(CurrentSession.Id).Length == 0)
            return CurrentSession;

        CurrentSession = await CreateSession(ctx);
        return CurrentSession;
    }

    /// <summary>
    /// Делает сессию текущей: открытый из истории старый диалог продолжается следующими вопросами,
    /// а не уходит в последнюю/новую сессию.
    /// </summary>
    public bool ResumeSession(int sessionId)
    {
        if (CurrentSession.Id == sessionId)
            return true;

        using var ctx = _getContext();
        var session = ctx.FindRagSession(sessionId);
        if (session == null)
            return false;

        CurrentSession = session;
        return true;
    }

    /// <summary>
    /// Мягко удаляет обмен; если сессия осталась без обменов — скрывает и её.
    /// </summary>
    public async Task DeleteExchange(int exchangeId)
    {
        await using var ctx = _getContext();
        var exchange = ctx.FindRagExchange(exchangeId);
        if (exchange == null)
            return;

        exchange.MarkDeleted();
        await ctx.SaveChangesAsync();

        if (ctx.GetRagExchanges(exchange.RagSessionId).Length == 0)
            await HideSession(ctx, exchange.RagSessionId);
    }

    /// <summary>
    /// Мягко удаляет сессию; если она была текущей — переключается на последнюю оставшуюся или новую.
    /// </summary>
    public async Task DeleteSession(int sessionId)
    {
        await using var ctx = _getContext();
        await HideSession(ctx, sessionId);
    }

    /// <summary>
    /// Хвост ≤ HistoryDepth пар текущей сессии в формате `Q: ...\nA: ...`,
    /// срезанный по MaxHistoryChars (старые пары отбрасываются первыми).
    /// </summary>
    public string BuildPriorContext()
    {
        return string.Join("\n", GetContextPairs(CurrentSession.Id));
    }

    public async Task<RagAskResult> Ask(string question, CancellationToken ct = default, IProgress<RagPhase>? progress = null)
    {
        // Диалог фиксируется на момент вопроса: если пользователь переключит текущую сессию,
        // пока генерируется ответ, обмен всё равно ляжет в исходный диалог,
        // и по завершении этот диалог снова станет текущим.
        var sessionId = CurrentSession.Id;

        var priorPairs = GetContextPairs(sessionId);
        var priorContext = string.Join("\n", priorPairs);

        progress?.Report(RagPhase.Retrieving);

        // Область поиска: коллекция → её книги. Пустая коллекция (без проиндексированных книг) —
        // ретрив не запускаем, чтобы не «расширяться» на всю библиотеку (RetrievalScope трактует
        // пустой DocumentIds как WholeLibrary), а честно вернуть «нет источников».
        var resolved = ResolveScope(sessionId);

        var retrievalRan = question.Length >= _retrievalOptions.MinQueryLength;
        var sources = Array.Empty<KnowledgeChunk>();
        if (retrievalRan && !resolved.EmptyCollection)
        {
            // Уточняющий вопрос ("а что было дальше?") сам по себе даёт бессмысленный вектор —
            // переписываем его в самодостаточный запрос с опорой на историю диалога.
            var retrievalQuery = priorPairs.Length > 0
                ? await BuildStandaloneQuery(question, priorContext, sessionId)
                : question;
            // priorPairs.Length == 0 — первый вопрос диалога, переписывать нечего.
            sources = await _retriever.Retrieve(retrievalQuery, _retrievalOptions.TopK, resolved.Scope, ct);
        }

        var state = !retrievalRan
            ? RetrievalState.Skipped
            : sources.Length > 0
                ? RetrievalState.Found
                : RetrievalState.NoneFound;

        Logger.Info("RAG ask: question {0} chars, retrieval {1}, {2} sources, prior context {3} chars",
            question.Length, state, sources.Length, priorContext.Length);

        ct.ThrowIfCancellationRequested();
        progress?.Report(RagPhase.Generating);

        // null — ретрив пропущен (короткий вопрос): без блока источников.
        // Пустой не-null — ретрив выполнен, но ничего не найдено: явный «no sources» в промпте.
        IReadOnlyList<KnowledgeChunk>? materials = retrievalRan ? sources : null;
        string answer;
        using (LlmContext.WithReferenceMaterials(materials))
            answer = await _getLlm().AskWithRag(question, priorContext);

        await using var ctx = _getContext();
        var session = ctx.FindRagSession(sessionId) ?? await CreateSession(ctx);
        if (string.IsNullOrWhiteSpace(session.Title))
            session.SetTitle(BuildTitle(question));

        var exchange = await ctx.AddRagExchange(session, question, JsonConvert.SerializeObject(sources),
            resolved.Name, resolved.Color);
        exchange.SetAnswer(answer);
        await ctx.SaveChangesAsync(CancellationToken.None);
        CurrentSession = session;

        return new RagAskResult
        {
            Answer = answer,
            Sources = sources,
            Retrieval = state,
            ScopeName = resolved.Name,
            ScopeEmpty = resolved.EmptyCollection,
        };
    }

    /// <summary>
    /// LLM-переписывание уточняющего вопроса в самодостаточный запрос; при сбое (или когда
    /// переписывание выключено в настройках) — дешёвая эвристика (конкатенация предыдущего вопроса
    /// пользователя). Не валит весь ответ из-за переписывания.
    /// </summary>
    private async Task<string> BuildStandaloneQuery(string question, string priorContext, int sessionId)
    {
        if (!_retrievalOptions.RewriteFollowUpQueries)
            return HeuristicStandaloneQuery(question, sessionId);

        try
        {
            return await _getLlm().RewriteFollowUpQuery(question, priorContext);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Follow-up query rewrite failed; using heuristic fallback");
            return HeuristicStandaloneQuery(question, sessionId);
        }
    }

    private string HeuristicStandaloneQuery(string question, int sessionId)
    {
        var previous = LastQuestion(sessionId);
        return string.IsNullOrWhiteSpace(previous) ? question : $"{previous} {question}";
    }

    private string? LastQuestion(int sessionId)
    {
        using var ctx = _getContext();
        return ctx.GetRagExchanges(sessionId).LastOrDefault()?.Question;
    }

    // ---------- Область поиска (per-session, по коллекции) ----------

    /// <summary>
    /// Id коллекции области поиска текущей сессии (null — «All sources», вся библиотека).
    /// </summary>
    public int? CurrentScopeCollectionId => GetScopeCollectionId(CurrentSession.Id);

    /// <summary>
    /// Задаёт область поиска текущей сессии коллекцией (null — вся библиотека) и сохраняет её.
    /// </summary>
    public void SetCollectionScope(int? collectionId)
    {
        var sessionId = CurrentSession.Id;
        _scopeCollectionBySession[sessionId] = collectionId;

        using var ctx = _getContext();
        ctx.SetSetting(ScopeSettingPrefix + sessionId, collectionId?.ToString() ?? string.Empty);
        ctx.SaveChanges();
    }

    private int? GetScopeCollectionId(int sessionId)
    {
        if (_scopeCollectionBySession.TryGetValue(sessionId, out var cached))
            return cached;

        using var ctx = _getContext();
        // Значение — id коллекции; пусто или нераспознанное (в т.ч. легаси-список книг) — вся библиотека.
        var raw = ctx.GetSetting(ScopeSettingPrefix + sessionId);
        var id = int.TryParse(raw, out var parsed) ? parsed : (int?)null;
        _scopeCollectionBySession[sessionId] = id;
        return id;
    }

    /// <summary>
    /// Резолвит коллекцию области поиска в книги + снимок её имени/цвета для персиста.
    /// </summary>
    private ResolvedScope ResolveScope(int sessionId)
    {
        var collectionId = GetScopeCollectionId(sessionId);
        if (collectionId == null)
            return new ResolvedScope(RetrievalScope.WholeLibrary, false, null, null);

        using var ctx = _getContext();
        var collection = ctx.FindCollection(collectionId.Value);
        if (collection == null)
        {
            // Коллекция удалена — область сбрасывается на всю библиотеку.
            _scopeCollectionBySession[sessionId] = null;
            return new ResolvedScope(RetrievalScope.WholeLibrary, false, null, null);
        }

        var documentIds = ctx.GetCollectionDocumentIds(collectionId.Value);
        return documentIds.Length == 0
            ? new ResolvedScope(RetrievalScope.WholeLibrary, true, collection.Name, collection.Color)
            : new ResolvedScope(RetrievalScope.ForDocuments(documentIds), false, collection.Name, collection.Color);
    }

    private readonly record struct ResolvedScope(RetrievalScope Scope, bool EmptyCollection, string? Name, string? Color);

    private async Task HideSession(SourceLensContext ctx, int sessionId)
    {
        var session = ctx.FindRagSession(sessionId);
        if (session == null)
            return;

        session.MarkDeleted();
        // Область поиска привязана к сессии — удаляем её настройку, чтобы ключи rag.scope.* не копились.
        ctx.DeleteSetting(ScopeSettingPrefix + sessionId);
        _scopeCollectionBySession.Remove(sessionId);
        await ctx.SaveChangesAsync();

        if (CurrentSession.Id == sessionId)
            CurrentSession = ctx.GetLastRagSession() ?? await CreateSession(ctx);
    }

    private string[] GetContextPairs(int sessionId)
    {
        if (Options.HistoryDepth <= 0)
            return Array.Empty<string>();

        using var ctx = _getContext();
        var tail = ctx.GetRagExchanges(sessionId)
            .TakeLast(Options.HistoryDepth)
            .Select(p => $"Q: {p.Question}\nA: {p.Answer}")
            .ToArray();

        var included = new LinkedList<string>();
        var total = 0;
        for (var i = tail.Length - 1; i >= 0; i--)
        {
            var cost = tail[i].Length + (included.Count > 0 ? 1 : 0); // +1 за '\n'-разделитель пар
            if (total + cost > Options.MaxHistoryChars)
                break;
            total += cost;
            included.AddFirst(tail[i]);
        }

        return included.ToArray();
    }

    private static async Task<RagSessionItem> CreateSession(SourceLensContext ctx)
    {
        var session = await ctx.AddRagSession();
        await ctx.SaveChangesAsync();
        return session;
    }

    private static string BuildTitle(string question)
    {
        var title = question.Trim();
        return title.Length <= MaxTitleLength ? title : title[..MaxTitleLength];
    }

    private static KnowledgeChunk[] DeserializeSources(string sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
            return Array.Empty<KnowledgeChunk>();

        return JsonConvert.DeserializeObject<KnowledgeChunk[]>(sourcesJson) ?? Array.Empty<KnowledgeChunk>();
    }
}
