using NUnit.Framework;

namespace SourceLens.Tests.Voice;

public class UiRecorderTests
{
    [Test]
    public async Task RunRecordingThenGetRecordReturnsRecordedBytes()
    {
        var data = new byte[] { 10, 20, 30 };
        var stub = new StubRecorder(data);
        var recorder = new UiRecorder(stub);

        Assert.That(recorder.IsRecording, Is.False);

        recorder.RunRecording();
        Assert.That(recorder.IsRecording, Is.True);

        var result = await recorder.GetRecord();

        Assert.That(result, Is.EqualTo(data));
        Assert.That(recorder.IsRecording, Is.False);
        Assert.That(stub.RecordCalls, Is.EqualTo(1));
    }

    [Test]
    public void GetRecordWithoutRunRecordingThrows()
    {
        var recorder = new UiRecorder(new StubRecorder());

        Assert.ThrowsAsync<InvalidOperationException>(() => recorder.GetRecord());
    }

    [Test]
    public async Task RunRecordingTwiceThrows()
    {
        var recorder = new UiRecorder(new StubRecorder());

        recorder.RunRecording();
        Assert.Throws<InvalidOperationException>(() => recorder.RunRecording());

        await recorder.GetRecord(); // освобождаем стаб
    }

    [Test]
    public void GetAudioOptionsDelegatesToRecorder()
    {
        var stub = new StubRecorder();
        var recorder = new UiRecorder(stub);

        Assert.That(recorder.GetAudioOptions(), Is.SameAs(stub.GetAudioOptions()));
    }
}
