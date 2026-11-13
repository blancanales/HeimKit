using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.SharedMap.Models;

// These values are persisted in resource discovery files; never renumber them.
public enum ResourceKind { Copper = 0, Tin = 1, Silver = 2, Obsidian = 3, Flametal = 4, BlackMarble = 5, AncientRoot = 6 }
