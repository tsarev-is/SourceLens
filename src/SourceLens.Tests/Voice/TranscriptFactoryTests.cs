using NUnit.Framework;
using SourceLens.Domain.Audio;

namespace SourceLens.Tests.Voice;

public class TranscriptFactoryTests
{
    [Test]
    public void AcquireReturnsPooledInstance()
    {
        var transcriptor = new StubTranscriptor();
        var factory = new TranscriptFactory(transcriptor);

        var acquired = factory.Acquire();

        Assert.That(acquired, Is.SameAs(transcriptor));
        Assert.That(factory.AllCount, Is.EqualTo(1));
    }

    [Test]
    public void AcquireBlocksWhenPoolExhaustedAndReleaseUnblocks()
    {
        var factory = new TranscriptFactory(new StubTranscriptor());
        var first = factory.Acquire();

        var secondAcquire = Task.Run(() => factory.Acquire());
        Assert.That(secondAcquire.Wait(TimeSpan.FromMilliseconds(300)), Is.False,
            "Acquire must block while the pool is exhausted");

        factory.Release(first);

        Assert.That(secondAcquire.Wait(TimeSpan.FromSeconds(5)), Is.True,
            "Acquire must complete after Release returns an instance to the pool");
        Assert.That(secondAcquire.Result, Is.SameAs(first));

        factory.Release(secondAcquire.Result);
    }

    [Test]
    public void AcquireHandsOutAllDistinctInstances()
    {
        var a = new StubTranscriptor("a");
        var b = new StubTranscriptor("b");
        var factory = new TranscriptFactory(a, b);

        var first = factory.Acquire();
        var second = factory.Acquire();

        Assert.That(factory.AllCount, Is.EqualTo(2));
        Assert.That(new[] { first, second }, Is.EquivalentTo(new ITranscriptor[] { a, b }));

        factory.Release(first);
        factory.Release(second);
    }

    [Test]
    public void ReleaseNullThrows()
    {
        var factory = new TranscriptFactory(new StubTranscriptor());

        Assert.Throws<ArgumentNullException>(() => factory.Release(null!));
    }
}
