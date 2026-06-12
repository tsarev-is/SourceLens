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

        progress?.Report(RagPhase.Retrieving);
        var sources = question.Length >= _retrievalOptions.MinQueryLength
            ? await _retriever.Retrieve(question, _retrievalOptions.TopK, ct)
            : Array.Empty<KnowledgeChunk>();

        var priorContext = string.Join("\n", GetContextPairs(sessionId));
        Logger.Info("RAG ask: question {0} chars, {1} sources, prior context {2} chars", question.Length, sources.Length, priorContext.Length);

        ct.ThrowIfCancellationRequested();
        progress?.Report(RagPhase.Generating);
        string answer;
        using (LlmContext.WithReferenceMaterials(sources))
            answer = await _getLlm().AskWithRag(question, priorContext);

        await using var ctx = _getContext();
        var session = ctx.FindRagSession(sessionId) ?? await CreateSession(ctx);
        if (string.IsNullOrWhiteSpace(session.Title))
            session.SetTitle(BuildTitle(question));

        var exchange = await ctx.AddRagExchange(session, question, JsonConvert.SerializeObject(sources));
        exchange.SetAnswer(answer);
        await ctx.SaveChangesAsync(CancellationToken.None);
        CurrentSession = session;

        return new RagAskResult { Answer = answer, Sources = sources };
    }

    private async Task HideSession(SourceLensContext ctx, int sessionId)
    {
        var session = ctx.FindRagSession(sessionId);
        if (session == null)
            return;

        session.MarkDeleted();
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
