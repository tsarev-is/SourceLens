using System;
using System.Threading.Tasks;
using SourceLens.Domain.Audio;

namespace SourceLens;

/// <summary>
/// Обёртка записи для UI: toggle-протокол RunRecording (старт) / GetRecord (стоп + данные).
/// </summary>
public class UiRecorder
{
    private readonly IRecorder _recorder;

    private readonly WhileConditionOnButton _whileCondition = new();
    private Task<byte[]>? _dataTask;

    public UiRecorder(IRecorder recorder)
    {
        _recorder = recorder;
        _whileCondition.Stop();
    }

    public bool IsRecording => _whileCondition.IsActive;

    public void RunRecording()
    {
        if (_whileCondition.IsActive)
            throw new InvalidOperationException("Condition is already active");

        _whileCondition.Start();
        _dataTask = _recorder.Record(_whileCondition);
    }

    public async Task<byte[]> GetRecord()
    {
        if (!_whileCondition.IsActive)
            throw new InvalidOperationException("Condition is not active");

        _whileCondition.Stop();
        return await _dataTask!;
    }

    public AudioOptions GetAudioOptions() => _recorder.GetAudioOptions();
}
