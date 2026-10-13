namespace HeimKit.Shared;

public interface IServerModule
{
    string Name { get; }
    bool IsActive { get; }
    void Initialize(string gameVersion);
    void Tick(float now);
    void Shutdown();
}
