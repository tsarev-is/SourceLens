using NUnit.Framework;
using SourceLens.Domain;
using SourceLens.Domain.Prompts;
using SourceLens.Domain.Rag.Models;

namespace SourceLens.Tests.Llm;

public class LlmContextAndInferencesTests
{
    [Test]
    public void SystemPromptAndReferenceMaterials_DefaultToNull()
    {
        Assert.That(LlmContext.SystemPrompt, Is.Null);
        Assert.That(LlmContext.ReferenceMaterials, Is.Null);
    }

    [Test]
    public void WithSystemPrompt_RestoresPreviousOnDispose()
    {
        using (LlmContext.WithSystemPrompt("outer"))
        {
            Assert.That(LlmContext.SystemPrompt, Is.EqualTo("outer"));
            using (LlmContext.WithSystemPrompt("inner"))
                Assert.That(LlmContext.SystemPrompt, Is.EqualTo("inner"));
            Assert.That(LlmContext.SystemPrompt, Is.EqualTo("outer"));
        }

        Assert.That(LlmContext.SystemPrompt, Is.Null);
    }

    [Test]
    public void WithReferenceMaterials_RestoresPreviousOnDispose()
    {
        Assert.That(LlmContext.ReferenceMaterials, Is.Null);

        using (LlmContext.WithReferenceMaterials(new[] { new KnowledgeChunk { Text = "x" } }))
        {
            Assert.That(LlmContext.ReferenceMaterials, Is.Not.Null);
            Assert.That(LlmContext.ReferenceMaterials!.Count, Is.EqualTo(1));
        }

        Assert.That(LlmContext.ReferenceMaterials, Is.Null);
    }

    [Test]
    public async Task AskWithRag_WrapsCallInRagSystemPrompt_AndRestoresAfter()
    {
        var llm = new CapturingLlm();

        var answer = await llm.AskWithRag("question");

        Assert.That(llm.CapturedSystemPrompt, Is.EqualTo(PromptCatalog.RagSystem()));
        Assert.That(LlmContext.SystemPrompt, Is.Null, "System prompt scope must be restored after the call");
        Assert.That(answer, Is.EqualTo("the answer [1]"), "Answer must be trimmed");
    }

    [Test]
    public async Task AskWithRag_PropagatesAmbientReferenceMaterialsIntoPrompt()
    {
        var llm = new CapturingLlm();
        var refs = new[]
        {
            new KnowledgeChunk { Text = "ambient-chunk", SourceTitle = "Bk", SourceLocation = "p.7" },
        };

        using (LlmContext.WithReferenceMaterials(refs))
        {
            await llm.AskWithRag("question");
        }

        Assert.That(llm.LastPrompt, Does.Contain("<<<REFERENCE_MATERIALS"));
        Assert.That(llm.LastPrompt, Does.Contain("[1] (source: Bk, p.7): ambient-chunk"));
    }

    [Test]
    public async Task AskWithRag_NoAmbientReferenceMaterials_OmitsBlock()
    {
        var llm = new CapturingLlm();

        await llm.AskWithRag("question");

        Assert.That(llm.LastPrompt, Does.Not.Contain("<<<REFERENCE_MATERIALS"));
    }

    [Test]
    public async Task AskWithRag_PriorContext_IsRenderedAsDialogHistoryBlock()
    {
        var llm = new CapturingLlm();

        await llm.AskWithRag("current question", "Q: old question\nA: old answer");

        Assert.That(llm.LastPrompt, Does.Contain("[DIALOG_HISTORY]"));
        Assert.That(llm.LastPrompt, Does.Contain("Q: old question"));
        Assert.That(llm.LastPrompt, Does.Contain("A: old answer"));
        Assert.That(llm.LastPrompt, Does.Contain("\"\"\"current question\"\"\""));
    }

    [Test]
    public async Task AskWithRag_DefaultPriorContext_OmitsDialogHistoryBlock()
    {
        var llm = new CapturingLlm();

        await llm.AskWithRag("current question");

        Assert.That(llm.LastPrompt, Does.Not.Contain("[DIALOG_HISTORY]"));
    }

    [Test]
    public async Task SummariseChunk_EmptyText_ReturnsEmptyWithoutLlmCall()
    {
        var llm = new CapturingLlm();

        var summary = await llm.SummariseChunk("   ");

        Assert.That(summary, Is.Empty);
        Assert.That(llm.LastPrompt, Is.Empty);
    }

    [Test]
    public async Task SummariseChunk_PassesPassageToLlm_AndTrimsAnswer()
    {
        var llm = new CapturingLlm();

        var summary = await llm.SummariseChunk("PASSAGE");

        Assert.That(llm.LastPrompt, Does.Contain("PASSAGE"));
        Assert.That(summary, Is.EqualTo("the answer [1]"));
    }
}
