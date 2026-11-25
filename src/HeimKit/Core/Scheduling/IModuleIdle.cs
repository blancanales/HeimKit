namespace HeimKit.Core.Scheduling;

public interface IModuleIdle
{
    void HoldNewWork(bool hold);
    bool Idle { get; }
}
