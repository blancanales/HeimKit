namespace HeimKit.Shared.Players;

// Preserve the exact activity signal without formatting unchanged equipment.
internal sealed class PlayerEquipment
{
    private (int, int, int, int, int, int, int, int, int) _items;
    private ZDOID _character;
    private string? _text;
    internal string Read(ZDO actor)
    {
        var items = (actor.GetInt(ZDOVars.s_rightItem), actor.GetInt(ZDOVars.s_leftItem), actor.GetInt(ZDOVars.s_chestItem),
            actor.GetInt(ZDOVars.s_helmetItem), actor.GetInt(ZDOVars.s_legItem), actor.GetInt(ZDOVars.s_shoulderItem),
            actor.GetInt(ZDOVars.s_utilityItem), actor.GetInt(ZDOVars.s_rightBackItem), actor.GetInt(ZDOVars.s_leftBackItem));
        if (_text == null || _character != actor.m_uid || !_items.Equals(items))
        {
            _items = items; _character = actor.m_uid;
            _text = $"{items.Item1}:{items.Item2}:{items.Item3}:{items.Item4}:{items.Item5}:{items.Item6}:{items.Item7}:{items.Item8}:{items.Item9}";
        }
        return _text;
    }
}
