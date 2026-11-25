namespace HeimKit.Client;

internal interface IClientModule
{
    string Name { get; }
    bool RequiresServer { get; }
    void Initialize();
    void SetRunning(bool running);
    void Tick(float now);
    void Shutdown();
}
