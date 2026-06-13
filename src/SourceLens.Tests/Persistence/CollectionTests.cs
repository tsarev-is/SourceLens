using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using SourceLens.Domain.Entities;
using SourceLens.Domain.Entities.Models;

namespace SourceLens.Tests.Persistence;

/// <summary>
/// Коллекции источников: дефолтная (General) как виртуальный бакет «без членства», many-to-many,
/// счётчики и резолв области поиска в id книг.
/// </summary>
public class CollectionTests
{
    private DbContextOptionsBuilder<SourceLensContext> _builder = Global.GetBuilder();

    [TearDown]
    public void TearDown()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.Database.EnsureDeleted();
    }

    private static int AddDoc(SourceLensContext ctx, string title)
    {
        var doc = ctx.Set<BookDocumentItem>().Add(
            BookDocumentItem.Create(title, $"/books/{title}.pdf", "sha-" + title, "v1", "fake-v1", 4, 10)).Entity;
        ctx.SaveChanges();
        return doc.Id;
    }

    [Test]
    public void EnsureDefaultCollection_IsIdempotent()
    {
        using var ctx = Global.CreateContext(_builder);
        var first = ctx.EnsureDefaultCollection("General", "#7a818d");
        var second = ctx.EnsureDefaultCollection("General", "#7a818d");

        Assert.That(second.Id, Is.EqualTo(first.Id));
        Assert.That(ctx.GetCollectionSummaries().Count(c => c.IsDefault), Is.EqualTo(1));
    }

    [Test]
    public async Task DefaultCollection_HoldsUncategorizedDocuments()
    {
        using var ctx = Global.CreateContext(_builder);
        var general = ctx.EnsureDefaultCollection("General", "#7a818d");
        var d1 = AddDoc(ctx, "alpha");
        var d2 = AddDoc(ctx, "beta");

        var dense = await ctx.AddCollection("Dense", "#6aa6ff");
        await ctx.AddCollectionMember(dense.Id, d1);

        // d1 в Dense → General держит только d2.
        Assert.That(ctx.GetCollectionDocumentIds(general.Id), Is.EquivalentTo(new[] { d2 }));
        Assert.That(ctx.GetCollectionDocumentIds(dense.Id), Is.EquivalentTo(new[] { d1 }));

        var counts = ctx.GetCollectionCounts();
        Assert.That(counts[general.Id], Is.EqualTo(1));
        Assert.That(counts[dense.Id], Is.EqualTo(1));
    }

    [Test]
    public async Task ManyToMany_DocumentCanBelongToMultipleCollections()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.EnsureDefaultCollection("General", "#7a818d");
        var d1 = AddDoc(ctx, "alpha");
        var a = await ctx.AddCollection("A", "#6aa6ff");
        var b = await ctx.AddCollection("B", "#4ec98a");

        await ctx.AddCollectionMember(a.Id, d1);
        await ctx.AddCollectionMember(b.Id, d1);

        Assert.That(ctx.GetDocumentCollectionIds(d1), Is.EquivalentTo(new[] { a.Id, b.Id }));
    }

    [Test]
    public async Task AddCollectionMember_IsDuplicateSafe()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.EnsureDefaultCollection("General", "#7a818d");
        var d1 = AddDoc(ctx, "alpha");
        var a = await ctx.AddCollection("A", "#6aa6ff");

        await ctx.AddCollectionMember(a.Id, d1);
        await ctx.AddCollectionMember(a.Id, d1);

        Assert.That(ctx.GetCollectionDocumentIds(a.Id), Is.EquivalentTo(new[] { d1 }));
    }

    [Test]
    public async Task DeleteCollection_DropsMemberships_AndDocFallsBackToGeneral()
    {
        using var ctx = Global.CreateContext(_builder);
        var general = ctx.EnsureDefaultCollection("General", "#7a818d");
        var d1 = AddDoc(ctx, "alpha");
        var a = await ctx.AddCollection("A", "#6aa6ff");
        await ctx.AddCollectionMember(a.Id, d1);

        var deleted = await ctx.DeleteCollection(a.Id);

        Assert.That(deleted, Is.True);
        Assert.That(ctx.GetCollectionSummaries().Any(c => c.Id == a.Id), Is.False);
        // Осиротевший документ снова числится в General.
        Assert.That(ctx.GetCollectionDocumentIds(general.Id), Is.EquivalentTo(new[] { d1 }));
        Assert.That(ctx.GetDocumentCollectionIds(d1), Is.Empty);
    }

    [Test]
    public void DeleteCollection_DefaultIsProtected()
    {
        using var ctx = Global.CreateContext(_builder);
        var general = ctx.EnsureDefaultCollection("General", "#7a818d");

        Assert.That(ctx.DeleteCollection(general.Id).GetAwaiter().GetResult(), Is.False);
        Assert.That(ctx.GetCollectionSummaries().Any(c => c.IsDefault), Is.True);
    }

    [Test]
    public async Task DeleteBookDocument_RemovesItsMemberships()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.EnsureDefaultCollection("General", "#7a818d");
        var d1 = AddDoc(ctx, "alpha");
        var a = await ctx.AddCollection("A", "#6aa6ff");
        await ctx.AddCollectionMember(a.Id, d1);

        await ctx.DeleteBookDocument(d1);

        Assert.That(ctx.GetCollectionDocumentIds(a.Id), Is.Empty);
        Assert.That(ctx.GetCollectionCounts()[a.Id], Is.EqualTo(0));
    }

    [Test]
    public void GetCollectionSummaries_DefaultFirstThenByName()
    {
        using var ctx = Global.CreateContext(_builder);
        ctx.EnsureDefaultCollection("General", "#7a818d");
        ctx.AddCollection("Zeta", "#6aa6ff").GetAwaiter().GetResult();
        ctx.AddCollection("Alpha", "#4ec98a").GetAwaiter().GetResult();

        var names = ctx.GetCollectionSummaries().Select(c => c.Name).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "General", "Alpha", "Zeta" }));
    }
}
