namespace SourceLens.Domain.Audio;

/// <summary>
/// Условие записи, управляемое кнопкой UI (toggle Record/Stop).
/// </summary>
public class WhileConditionOnButton : IWhileCondition
{
    public bool IsActive { get; private set; } = true;

    public void Start() { IsActive = true; }

    public void Stop() { IsActive = false; }
}
