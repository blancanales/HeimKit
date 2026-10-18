using System;

namespace HeimKit.Features.SharedMap.Models;

public readonly struct MiningProgress
{
    public int RemainingSections { get; }
    public int TotalSections { get; }
    public MiningProgress(int remainingSections, int totalSections)
    {
        if (totalSections < 1 || totalSections > 8192 || remainingSections < 0 || remainingSections > totalSections)
            throw new ArgumentOutOfRangeException(nameof(remainingSections));
        RemainingSections = remainingSections; TotalSections = totalSections;
    }
}
