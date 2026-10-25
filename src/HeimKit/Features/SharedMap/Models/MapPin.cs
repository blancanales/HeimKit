using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
namespace HeimKit.Features.SharedMap.Models;

public sealed class MapPin
{
    public long Owner { get; }
    public string Name { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public int Icon { get; }
    public bool Checked { get; }
    public string Author { get; }
    public MapPin(long owner, string name, float x, float y, float z, int icon, bool isChecked = false, string author = "")
    { Owner = owner; Name = name; X = x; Y = y; Z = z; Icon = icon; Checked = isChecked; Author = author; }
}
