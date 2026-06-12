using Newtonsoft.Json;
using NUnit.Framework;
using SourceLens.Configuration;

namespace SourceLens.Tests;

/// <summary>
/// Шаблон конфигурации, который копируется в appsettings.json при первом старте,
/// обязан парситься в GeneralOptions и проходить валидацию composition root.
/// </summary>
public class ConfigurationTests
{
    private static string TemplatePath =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "appsettings.template.json");

    [Test]
    public void Template_DeserializesAndValidates()
    {
        var options = JsonConvert.DeserializeObject<GeneralOptions>(File.ReadAllText(TemplatePath))!;

        Assert.That(options, Is.Not.Null);
        Assert.DoesNotThrow(() => options.Validate());

        Assert.That(options.AiModel.Provider, Is.EqualTo(GeneralOptions.AiOptions.ProviderKind.Claude));
        Assert.That(options.AiModel.Claude.ExtraArgs, Is.EqualTo(new[] { "-p", "--output-format", "text" }));
        Assert.That(options.Rag.Enabled, Is.True);
        Assert.That(options.Rag.LocalOnnx.Dimensions, Is.EqualTo(384));
        Assert.That(options.Transcription.PoolSize, Is.EqualTo(1));
        Assert.That(options.Audio.Rate, Is.EqualTo(16000));
    }

    [Test]
    public void Validate_ChunkOverlapNotLessThanChunkSize_Throws()
    {
        var options = JsonConvert.DeserializeObject<GeneralOptions>(File.ReadAllText(TemplatePath))!;
        options.Rag.ChunkOverlap = options.Rag.ChunkSize;

        Assert.Throws<System.Data.DataException>(() => options.Validate());
    }

    [Test]
    public void Validate_MissingBinaryPathForSelectedProvider_Throws()
    {
        var options = JsonConvert.DeserializeObject<GeneralOptions>(File.ReadAllText(TemplatePath))!;
        options.AiModel.Claude.BinaryPath = "";

        Assert.Throws<System.Data.DataException>(() => options.Validate());
    }
}
