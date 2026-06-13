using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Entities.Models;
using SourceLens.Domain.Rag;
using SourceLens.Domain.Rag.Models;
using SourceLens.Tests.Llm;

namespace SourceLens.Tests.Managers;

public class RagDialogManagerTests
{
    private DbContextOptionsBuilder<SourceLensContext> _builder = Global.GetBuilder();
    private CapturingLlm _llm = new();
    private StubRetriever _retriever = new();

    [SetUp]
    public void SetUp()
    {
        _llm = new CapturingLlm();
        _retriever = new StubRetriever();
    }

    [TearDown]
    public void TearDown()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.Database.EnsureDeleted();
    }

    private RagDialogManager CreateManager(RagDialogOptions? options = null, RetrievalOptions? retrievalOptions = null)
    {
        return new RagDialogManager(() => Global.CreateContext(_builder), () => _llm, _retriever,
            retrievalOptions ?? new RetrievalOptions { TopK = 4, MinQueryLength = 1 },
            options ?? new RagDialogOptions());
    }

    private async Task<RagSessionItem> Seed(params (string Question, string Answer)[] pairs)
    {
        await using var ctx = Global.CreateContext(_builder);
        var session = await ctx.AddRagSession();
        await ctx.SaveChangesAsync();
        foreach (var (question, answer) in pairs)
            (await ctx.AddRagExchange(session, question, "[]")).SetAnswer(answer);
        await ctx.SaveChangesAsync();
        return session;
    }

    private sealed class RecordingProgress : IProgress<RagPhase>
    {
        public List<RagPhase> Phases { get; } = new();

        public void Report(RagPhase value) => Phases.Add(value);
    }

    [Test]
    public void Constructor_NoSessions_CreatesAndPersistsNewSession()
    {
        var manager = CreateManager();

        using var ctx = Global.CreateContext(_builder);
        var sessions = ctx.GetRagSessions();
        Assert.That(sessions.Length, Is.EqualTo(1));
        Assert.That(manager.CurrentSession.Id, Is.EqualTo(sessions[0].Id));
        Assert.That(manager.ContextSize, Is.Zero);
    }

    [Test]
    public async Task Constructor_StartsNewDialog_OldSessionsRemainInHistory()
    {
        var old = await Seed(("old question", "old answer"));

        var manager = CreateManager();

        Assert.That(manager.CurrentSession.Id, Is.Not.EqualTo(old.Id), "старт — всегда новый диалог");
        Assert.That(manager.BuildPriorContext(), Is.Empty);
        Assert.That(manager.GetSessions().Select(p => p.Id), Does.Contain(old.Id), "старый диалог остался в истории");
    }

    [Test]
    public void Constructor_EmptyLastSession_IsReused()
    {
        var first = CreateManager();
        var second = CreateManager();

        Assert.That(second.CurrentSession.Id, Is.EqualTo(first.CurrentSession.Id),
            "перезапуски не плодят пустые сессии");
        Assert.That(second.GetSessions().Length, Is.EqualTo(1));
    }

    [Test]
    public async Task Ask_PersistsExchangeWithSources_RoundTripsSourcesJson()
    {
        _retriever.Chunks = new[]
        {
            new KnowledgeChunk { Text = "Entropy always grows.", SourceTitle = "Thermo", SourceLocation = "p.42", Score = 0.91f },
            new KnowledgeChunk { Text = "ΔS ≥ 0.", SourceTitle = "Physics 101", SourceLocation = "ch.3", Score = 0.84f },
        };
        var manager = CreateManager();

        var result = await manager.Ask("What is entropy?");

        Assert.That(result.Answer, Is.EqualTo("the answer [1]"));
        Assert.That(result.Sources, Is.SameAs(_retriever.Chunks));

        var exchanges = manager.GetExchanges(manager.CurrentSession.Id);
        Assert.That(exchanges.Length, Is.EqualTo(1));
        Assert.That(exchanges[0].Question, Is.EqualTo("What is entropy?"));
        Assert.That(exchanges[0].Answer, Is.EqualTo("the answer [1]"));
        Assert.That(exchanges[0].CreatedAt, Is.Not.EqualTo(default(DateTimeOffset)));

        var sources = exchanges[0].Sources;
        Assert.That(sources.Length, Is.EqualTo(2));
        Assert.That(sources.Select(p => p.Text), Is.EqualTo(_retriever.Chunks.Select(p => p.Text)));
        Assert.That(sources.Select(p => p.SourceTitle), Is.EqualTo(_retriever.Chunks.Select(p => p.SourceTitle)));
        Assert.That(sources.Select(p => p.SourceLocation), Is.EqualTo(_retriever.Chunks.Select(p => p.SourceLocation)));
        Assert.That(sources.Select(p => p.Score), Is.EqualTo(_retriever.Chunks.Select(p => p.Score)));
    }

    private sealed class DelegatingLlm : AbstractLlmInferences
    {
        private readonly Func<string, Task<string>> _onQuestion;

        public DelegatingLlm(Func<string, Task<string>> onQuestion)
        {
            _onQuestion = onQuestion;
        }

        public override Task<string> Question(string input) => _onQuestion(input);
    }

    [Test]
    public async Task Ask_CurrentSessionSwitchedDuringGeneration_AnswerLandsInOriginalDialog()
    {
        var original = await Seed(("q1", "a1"));
        var other = await Seed(("other q", "other a"));

        RagDialogManager manager = null!;
        // Пока «генерируется» ответ, пользователь переключается на другой диалог.
        var llm = new DelegatingLlm(_ =>
        {
            manager.ResumeSession(other.Id);
            return Task.FromResult("late answer");
        });
        manager = new RagDialogManager(() => Global.CreateContext(_builder), () => llm, _retriever,
            new RetrievalOptions { TopK = 4, MinQueryLength = 1 }, new RagDialogOptions());
        manager.ResumeSession(original.Id);

        await manager.Ask("follow-up");

        Assert.That(manager.CurrentSession.Id, Is.EqualTo(original.Id),
            "по завершении ответа диалог вопроса снова текущий");
        Assert.That(manager.GetExchanges(original.Id).Select(p => p.Question),
            Is.EqualTo(new[] { "q1", "follow-up" }), "обмен лёг в диалог, из которого задан вопрос");
        Assert.That(manager.GetExchanges(other.Id).Select(p => p.Question),
            Is.EqualTo(new[] { "other q" }), "чужой диалог не тронут");
    }

    [Test]
    public async Task Ask_FirstQuestion_SetsSessionTitleTruncatedTo60()
    {
        var manager = CreateManager();
        var longQuestion = new string('q', 50) + " and some long tail that exceeds the limit";

        await manager.Ask(longQuestion);

        var title = manager.GetSessions().Single().Title;
        Assert.That(title, Is.EqualTo(longQuestion[..60]));
        Assert.That(title!.Length, Is.EqualTo(60));

        await manager.Ask("second question");
        Assert.That(manager.GetSessions().Single().Title, Is.EqualTo(longQuestion[..60]), "Title is set only from the first question");
    }

    [Test]
    public async Task BuildPriorContext_FormatsPairsInChronologicalOrder()
    {
        var session = await Seed(("first question", "first answer"), ("second question", "second answer"));
        var manager = CreateManager();
        manager.ResumeSession(session.Id);

        Assert.That(manager.BuildPriorContext(),
            Is.EqualTo("Q: first question\nA: first answer\nQ: second question\nA: second answer"));
        Assert.That(manager.ContextSize, Is.EqualTo(2));
    }

    [Test]
    public async Task BuildPriorContext_TrimsToHistoryDepth()
    {
        var session = await Seed(("q1", "a1"), ("q2", "a2"), ("q3", "a3"), ("q4", "a4"));
        var manager = CreateManager(new RagDialogOptions { HistoryDepth = 2 });
        manager.ResumeSession(session.Id);

        Assert.That(manager.BuildPriorContext(), Is.EqualTo("Q: q3\nA: a3\nQ: q4\nA: a4"));
        Assert.That(manager.ContextSize, Is.EqualTo(2));
    }

    [Test]
    public async Task BuildPriorContext_MaxHistoryChars_DropsOldestPairsFirst()
    {
        var session = await Seed(("old old old", "aaaa"), ("new", "bb"));
        // "Q: old old old\nA: aaaa" = 22 chars, "Q: new\nA: bb" = 12 chars; budget 20 keeps only the newest pair.
        var manager = CreateManager(new RagDialogOptions { HistoryDepth = 6, MaxHistoryChars = 20 });
        manager.ResumeSession(session.Id);

        Assert.That(manager.BuildPriorContext(), Is.EqualTo("Q: new\nA: bb"));
        Assert.That(manager.ContextSize, Is.EqualTo(1));
    }

    [Test]
    public async Task BuildPriorContext_HistoryDepthZero_EmptyContextButHistoryIsWritten()
    {
        var manager = CreateManager(new RagDialogOptions { HistoryDepth = 0 });

        await manager.Ask("first question");
        await manager.Ask("second question");

        Assert.That(manager.BuildPriorContext(), Is.Empty);
        Assert.That(manager.ContextSize, Is.Zero);
        Assert.That(_llm.LastPrompt, Does.Not.Contain("[DIALOG_HISTORY]"));
        Assert.That(manager.GetExchanges(manager.CurrentSession.Id).Length, Is.EqualTo(2));
    }

    [Test]
    public async Task StartNewSession_ResetsContext()
    {
        var manager = CreateManager();
        await manager.Ask("a question to remember");
        Assert.That(manager.ContextSize, Is.EqualTo(1));
        var oldSessionId = manager.CurrentSession.Id;

        var session = await manager.StartNewSession();

        Assert.That(session.Id, Is.Not.EqualTo(oldSessionId));
        Assert.That(manager.CurrentSession.Id, Is.EqualTo(session.Id));
        Assert.That(manager.BuildPriorContext(), Is.Empty);
        Assert.That(manager.ContextSize, Is.Zero);
        Assert.That(manager.GetSessions().Length, Is.EqualTo(2));
    }

    [Test]
    public async Task StartNewSession_EmptyCurrentSession_IsReused()
    {
        var manager = CreateManager();
        var first = await manager.StartNewSession();
        var second = await manager.StartNewSession();

        Assert.That(second.Id, Is.EqualTo(first.Id));
        Assert.That(manager.GetSessions().Length, Is.EqualTo(1));
    }

    [Test]
    public async Task ResumeSession_MakesOldSessionCurrent_AskContinuesIt()
    {
        var old = await Seed(("old question", "old answer"));
        var latest = await Seed(("latest question", "latest answer"));
        var manager = CreateManager();
        Assert.That(manager.CurrentSession.Id, Is.Not.EqualTo(latest.Id), "старт — новый диалог");

        Assert.That(manager.ResumeSession(old.Id), Is.True);

        Assert.That(manager.CurrentSession.Id, Is.EqualTo(old.Id));
        Assert.That(manager.BuildPriorContext(), Is.EqualTo("Q: old question\nA: old answer"));

        await manager.Ask("follow-up");
        Assert.That(manager.CurrentSession.Id, Is.EqualTo(old.Id));
        Assert.That(manager.GetExchanges(old.Id).Select(p => p.Question),
            Is.EqualTo(new[] { "old question", "follow-up" }));
    }

    [Test]
    public async Task ResumeSession_UnknownSession_ReturnsFalseAndKeepsCurrent()
    {
        var session = await Seed(("a question", "an answer"));
        var manager = CreateManager();
        var currentId = manager.CurrentSession.Id;

        Assert.That(manager.ResumeSession(session.Id + 100), Is.False);
        Assert.That(manager.CurrentSession.Id, Is.EqualTo(currentId));
    }

    [Test]
    public async Task DeleteExchange_SoftDeletes()
    {
        var manager = CreateManager();
        await manager.Ask("keep");
        await manager.Ask("remove");
        var session = manager.CurrentSession;
        var removeId = manager.GetExchanges(session.Id).Single(p => p.Question == "remove").Id;

        await manager.DeleteExchange(removeId);

        Assert.That(manager.GetExchanges(session.Id).Select(p => p.Question), Is.EqualTo(new[] { "keep" }));
        Assert.That(manager.GetSessions().Length, Is.EqualTo(1));
        Assert.That(manager.ContextSize, Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteExchange_LastExchangeOfSession_HidesSession()
    {
        var emptied = await Seed(("only question", "only answer"));
        var manager = CreateManager();
        await manager.Ask("current question");
        var currentId = manager.CurrentSession.Id;

        await manager.DeleteExchange(manager.GetExchanges(emptied.Id).Single().Id);

        Assert.That(manager.GetSessions().Select(p => p.Id), Is.EqualTo(new[] { currentId }));
        Assert.That(manager.CurrentSession.Id, Is.EqualTo(currentId));
    }

    [Test]
    public async Task DeleteExchange_LastExchangeOfCurrentSession_FallsBackToPreviousSession()
    {
        var previous = await Seed(("previous question", "previous answer"));
        var manager = CreateManager();
        await manager.Ask("current question");
        var currentId = manager.CurrentSession.Id;

        await manager.DeleteExchange(manager.GetExchanges(currentId).Single().Id);

        Assert.That(manager.GetSessions().Select(p => p.Id), Is.EqualTo(new[] { previous.Id }));
        Assert.That(manager.CurrentSession.Id, Is.EqualTo(previous.Id));
    }

    [Test]
    public async Task DeleteSession_SoftDeletes_AndCurrentFallsBackOrRecreates()
    {
        var previous = await Seed(("previous question", "previous answer"));
        var manager = CreateManager();
        await manager.Ask("current question");
        var currentId = manager.CurrentSession.Id;

        await manager.DeleteSession(currentId);
        Assert.That(manager.GetSessions().Select(p => p.Id), Is.EqualTo(new[] { previous.Id }));
        Assert.That(manager.CurrentSession.Id, Is.EqualTo(previous.Id));

        await manager.DeleteSession(previous.Id);
        var remaining = manager.GetSessions();
        Assert.That(remaining.Length, Is.EqualTo(1), "A fresh session is created when the last one is deleted");
        Assert.That(remaining[0].Id, Is.Not.EqualTo(previous.Id).And.Not.EqualTo(currentId));
        Assert.That(manager.CurrentSession.Id, Is.EqualTo(remaining[0].Id));
    }

    [Test]
    public async Task Ask_PassesChunksThroughLlmContext_AndPriorContextIntoPrompt_AndPersists()
    {
        var session = await Seed(("prior question", "prior answer"));
        _retriever.Chunks = new[]
        {
            new KnowledgeChunk { Text = "retrieved-passage", SourceTitle = "Bk", SourceLocation = "p.7", Score = 0.9f },
        };
        var manager = CreateManager();
        manager.ResumeSession(session.Id);
        var progress = new RecordingProgress();

        var result = await manager.Ask("What does the law say?", progress: progress);

        Assert.That(_llm.CapturedReferenceMaterials, Is.SameAs(_retriever.Chunks), "Chunks must travel via LlmContext");
        Assert.That(LlmContext.ReferenceMaterials, Is.Null, "Reference materials scope must be restored");
        Assert.That(_llm.LastPrompt, Does.Contain("[DIALOG_HISTORY]"));
        Assert.That(_llm.LastPrompt, Does.Contain("Q: prior question"));
        Assert.That(_llm.LastPrompt, Does.Contain("A: prior answer"));
        Assert.That(_llm.LastPrompt, Does.Contain("retrieved-passage"));
        Assert.That(_retriever.Calls, Is.EqualTo(new[] { ("the answer [1]", 4) }));
        Assert.That(_retriever.Scopes.Single(), Is.Not.Null);
        Assert.That(progress.Phases, Is.EqualTo(new[] { RagPhase.Retrieving, RagPhase.Generating }));
        Assert.That(result.Answer, Is.EqualTo("the answer [1]"));

        var exchanges = manager.GetExchanges(manager.CurrentSession.Id);
        Assert.That(exchanges.Length, Is.EqualTo(2));
        Assert.That(exchanges[^1].Question, Is.EqualTo("What does the law say?"));
        Assert.That(exchanges[^1].Answer, Is.EqualTo("the answer [1]"));
        Assert.That(exchanges[^1].Sources.Single().Text, Is.EqualTo("retrieved-passage"));
    }

    [Test]
    public async Task Ask_QuestionShorterThanMinQueryLength_SkipsRetrieval()
    {
        var manager = CreateManager(retrievalOptions: new RetrievalOptions { TopK = 4, MinQueryLength = 20 });

        var result = await manager.Ask("short");

        Assert.That(_retriever.Calls, Is.Empty);
        Assert.That(result.Sources, Is.Empty);
        Assert.That(result.Retrieval, Is.EqualTo(RetrievalState.Skipped));
        Assert.That(_llm.LastPrompt, Does.Not.Contain("<<<REFERENCE_MATERIALS"));
        Assert.That(manager.GetExchanges(manager.CurrentSession.Id).Length, Is.EqualTo(1));
    }

    [Test]
    public async Task Ask_NoHistory_RetrievesWithRawQuestion_NoRewrite()
    {
        _retriever.Chunks = new[] { new KnowledgeChunk { Text = "x", Score = 0.9f } };
        var manager = CreateManager();

        await manager.Ask("What is entropy in thermodynamics?");

        Assert.That(_retriever.Calls.Single().Query, Is.EqualTo("What is entropy in thermodynamics?"),
            "первый вопрос диалога не переписывается");
    }

    [Test]
    public async Task Ask_FollowUp_RewritesQueryAsStandaloneUsingHistory()
    {
        var session = await Seed(("prior question", "prior answer"));
        var llm = new DelegatingLlm(prompt => Task.FromResult(
            prompt.Contains("self-contained search query") ? "standalone rewritten query" : "final answer"));
        var manager = new RagDialogManager(() => Global.CreateContext(_builder), () => llm, _retriever,
            new RetrievalOptions { TopK = 4, MinQueryLength = 1 }, new RagDialogOptions());
        manager.ResumeSession(session.Id);

        await manager.Ask("and then?");

        Assert.That(_retriever.Calls.Single().Query, Is.EqualTo("standalone rewritten query"),
            "уточняющий вопрос переписан в самодостаточный запрос перед ретривом");
    }

    [Test]
    public async Task Ask_FollowUp_RewriteDisabled_UsesHeuristicWithoutExtraLlmCall()
    {
        var session = await Seed(("prior question", "prior answer"));
        var calls = 0;
        var llm = new DelegatingLlm(_ => { calls++; return Task.FromResult("final answer"); });
        var manager = new RagDialogManager(() => Global.CreateContext(_builder), () => llm, _retriever,
            new RetrievalOptions { TopK = 4, MinQueryLength = 1, RewriteFollowUpQueries = false },
            new RagDialogOptions());
        manager.ResumeSession(session.Id);

        await manager.Ask("and then?");

        Assert.That(_retriever.Calls.Single().Query, Is.EqualTo("prior question and then?"),
            "переписывание выключено → эвристика: предыдущий вопрос + текущий");
        Assert.That(calls, Is.EqualTo(1), "только вызов ответа, без отдельного вызова переписывания");
    }

    [Test]
    public async Task Ask_RetrievalFindsNothing_ReportsNoneFound_AndPromptSaysSo()
    {
        _retriever.Chunks = Array.Empty<KnowledgeChunk>();
        var manager = CreateManager();

        var result = await manager.Ask("a sufficiently long question with no matches");

        Assert.That(result.Retrieval, Is.EqualTo(RetrievalState.NoneFound));
        Assert.That(result.Sources, Is.Empty);
        Assert.That(_llm.LastPrompt, Does.Contain("none found"));
    }

    [Test]
    public async Task Ask_FoundSources_ReportsFound()
    {
        _retriever.Chunks = new[] { new KnowledgeChunk { Text = "hit", Score = 0.9f } };
        var manager = CreateManager();

        var result = await manager.Ask("a sufficiently long question");

        Assert.That(result.Retrieval, Is.EqualTo(RetrievalState.Found));
    }

    [Test]
    public async Task SetCollectionScope_ResolvesToMemberDocuments_AndIsPassedToRetriever()
    {
        var (collectionId, docIds) = SeedCollectionWithDocuments("Dense retrieval", "#6aa6ff", 2);
        _retriever.Chunks = Array.Empty<KnowledgeChunk>();
        var manager = CreateManager();

        manager.SetCollectionScope(collectionId);
        Assert.That(manager.CurrentScopeCollectionId, Is.EqualTo(collectionId));

        await manager.Ask("a sufficiently long question");

        var scope = _retriever.Scopes.Single();
        Assert.That(scope!.DocumentIds, Is.EquivalentTo(docIds));

        // Сброс на всю библиотеку.
        manager.SetCollectionScope(null);
        Assert.That(manager.CurrentScopeCollectionId, Is.Null);
    }

    [Test]
    public async Task Ask_EmptyCollectionScope_SkipsRetrieval_AndReportsEmpty()
    {
        int collectionId;
        using (var ctx = Global.CreateContext(_builder))
            collectionId = ctx.AddCollection("Empty", "#4ec98a").GetAwaiter().GetResult().Id;

        _retriever.Chunks = new[] { new KnowledgeChunk { Text = "should not be used", Score = 0.9f } };
        var manager = CreateManager();
        manager.SetCollectionScope(collectionId);

        var result = await manager.Ask("a sufficiently long question");

        Assert.That(_retriever.Calls, Is.Empty, "ретрив по пустой коллекции не должен запускаться");
        Assert.That(result.Retrieval, Is.EqualTo(RetrievalState.NoneFound));
        Assert.That(result.ScopeEmpty, Is.True);
        Assert.That(result.ScopeName, Is.EqualTo("Empty"));
    }

    [Test]
    public async Task Ask_SnapshotsScopeNameAndColor_OnExchange()
    {
        var (collectionId, _) = SeedCollectionWithDocuments("Dense retrieval", "#6aa6ff", 1);
        _retriever.Chunks = new[] { new KnowledgeChunk { Text = "hit", Score = 0.9f } };
        var manager = CreateManager();
        manager.SetCollectionScope(collectionId);

        await manager.Ask("a sufficiently long question");

        var exchange = manager.GetExchanges(manager.CurrentSession.Id).Single();
        Assert.That(exchange.ScopeName, Is.EqualTo("Dense retrieval"));
        Assert.That(exchange.ScopeColor, Is.EqualTo("#6aa6ff"));
    }

    [Test]
    public async Task DeleteSession_RemovesPersistedScopeSetting()
    {
        var manager = CreateManager();
        var sessionId = manager.CurrentSession.Id;
        int collectionId;
        using (var ctx = Global.CreateContext(_builder))
            collectionId = ctx.AddCollection("Lexical", "#4ec98a").GetAwaiter().GetResult().Id;
        manager.SetCollectionScope(collectionId);

        using (var ctx = Global.CreateContext(_builder))
            Assert.That(ctx.GetSetting("rag.scope." + sessionId), Is.EqualTo(collectionId.ToString()));

        await manager.DeleteSession(sessionId);

        using (var ctx = Global.CreateContext(_builder))
            Assert.That(ctx.GetSetting("rag.scope." + sessionId), Is.Null,
                "scope-настройка удалённой сессии не должна копиться в app_settings");
    }

    /// <summary>
    /// Создаёт пользовательскую коллекцию с N книгами-членами; возвращает id коллекции и книг.
    /// </summary>
    private (int CollectionId, int[] DocumentIds) SeedCollectionWithDocuments(string name, string color, int count)
    {
        using var ctx = Global.CreateContext(_builder);
        var docIds = new int[count];
        for (var i = 0; i < count; i++)
        {
            var doc = ctx.Set<BookDocumentItem>().Add(
                BookDocumentItem.Create($"{name} doc {i}", $"/books/{name}-{i}.pdf", $"sha-{name}-{i}", "v1", "fake-v1", 4, 10)).Entity;
            ctx.SaveChanges();
            docIds[i] = doc.Id;
        }

        var collectionId = ctx.AddCollection(name, color).GetAwaiter().GetResult().Id;
        foreach (var id in docIds)
            ctx.AddCollectionMember(collectionId, id).GetAwaiter().GetResult();

        return (collectionId, docIds);
    }
}
