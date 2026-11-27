using HeimKit.Core.Prefabs;

namespace HeimKit.Features.HeimDefender;
internal static class HeimDefenderDiagnostics
{
    internal static string Target(ZDO ballista, ZDOID target, string source)
    {
        var record = target.IsNone() ? null : ZDOMan.instance.GetZDO(target);
        var live = record == null ? null : ZNetScene.instance.FindInstance(record)?.GetComponent<Character>();
        string prefab = "unknown", faction = "unknown", player = "unknown";
        if (record != null && PrefabRegistry.Current.TryGet(record.GetPrefab(), out var info))
        {
            prefab = info.Name;
            faction = live?.GetFaction().ToString() ?? PrefabRegistry.Component<Character>(info.Prefab)?.m_faction.ToString() ?? "unknown";
            player = (PrefabRegistry.Component<Player>(info.Prefab) != null).ToString();
        }

        string enemy = live != null && Player.m_localPlayer != null ? BaseAI.IsEnemy(Player.m_localPlayer, live).ToString() : "unknown";
        return $"source={source} world={ZNet.instance.GetWorldUID()} ballista={ballista.m_uid} owner={ballista.GetOwner()} target={target} prefab={prefab} faction={faction} player={player} tamed={live?.IsTamed().ToString() ?? record?.GetBool(ZDOVars.s_tamed).ToString() ?? "unknown"} aggravated={live?.GetBaseAI()?.IsAggravated().ToString() ?? "unknown"} enemy_to_local_player={enemy} trophy_count={ballista.GetInt(ZDOVars.s_targets)}";
    }
}
