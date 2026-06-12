using Microsoft.EntityFrameworkCore;
using SourceLens.Domain.Entities;

namespace SourceLens.Tests;

public class Global
{
    public static DbContextOptionsBuilder<SourceLensContext> GetBuilder() =>
        new DbContextOptionsBuilder<SourceLensContext>().UseInMemoryDatabase(databaseName: "SourceLensTestDB");

    public static SourceLensContext CreateContext(DbContextOptionsBuilder<SourceLensContext> builder)
    {
        var result = new SourceLensContext(builder.Options);
        result.Database.EnsureCreated();

        return result;
    }
}
