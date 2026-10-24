using HeimKit.Core.Scheduling;
using HeimKit.Core.World;
using HeimKit.Core.Prefabs;
using HeimKit.Core.Native;
using HeimKit.Core.Ownership;
using HeimKit.Core.Diagnostics;
using HeimKit.Core.Lifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HeimKit.Features.SharedMap;

// Read-only migration of saved world records, separate from occupied-server gameplay.
internal sealed class SharedMapBootstrap
{
    private readonly ZDOMan _world;
    private readonly SharedMapExploration _map;
    private readonly List<ZDO> _batch = new();
    private readonly HashSet<ZDOID> _seen = new();
    private int _sector, _cursor;
    private bool _finishedScan;
    public int Tables { get; private set; }
    public int Imported { get; private set; }
    public int Invalid { get; private set; }

    public SharedMapBootstrap(ZDOMan world, SharedMapExploration map) { _world = world; _map = map; }

    public bool Tick(int budget)
    {
        if (_cursor == _batch.Count)
        {
            if (_finishedScan) return true;
            _batch.Clear(); _cursor = 0;
            _finishedScan = _world.GetAllZDOsWithPrefabIterative("piece_cartographytable", _batch, ref _sector);
        }
        // Native iteration can repeat a boundary sector; never import or count an identity twice.
        for (int processed = 0; processed < budget && _cursor < _batch.Count; processed++)
        {
            var table = _batch[_cursor++];
            if (!table.IsValid() || table.GetPrefab() != SharedMapController.TablePrefab || !_seen.Add(table.m_uid)) continue;
            Tables++;
            var data = table.GetByteArray(ZDOVars.s_data);
            if (data == null || data.Length == 0) continue;
            try { SharedMapTableData.Import(_map, data); Imported++; }
            catch (Exception error) when (error is IOException || error is DecoderFallbackException || error is ArgumentException)
            { Invalid++; }
        }
        return _finishedScan && _cursor == _batch.Count;
    }
}
