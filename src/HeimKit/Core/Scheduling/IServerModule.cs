using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Core.Scheduling;

public interface IServerModule
{
    string Name { get; }
    bool IsActive { get; }
    void Initialize(string gameVersion);
    void Tick(float now);
    void Shutdown();
}
