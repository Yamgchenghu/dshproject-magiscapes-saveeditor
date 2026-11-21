namespace GenerativeSave.Core.Editing;

/// <summary>
/// Chinese labels for the combat attributes an item can carry, plus the rule
/// that decides whether an edit will actually stick.
///
/// The game stores two very different kinds of number in
/// ItemConfig.EquipmentAttributeMap:
///
///   * A FLAT value — CritRate 0.30, AttackRange 0.69, TotalDamageAdd 0.03.
///     The game reads Value directly, so editing it works.
///
///   * A CURVE-BACKED value — PhysicalDamage with
///     CurveTable=ItemAttrCurveTable and RowName=3010046_PhysicalDamage.
///     Here Value is only a multiplier and the real number comes from the
///     curve row, which is keyed by the item's LEVEL. Editing Value does
///     nothing visible: the game recomputes the stat on load. This is the
///     "改了不生效" the user hit.
///
/// So the editor must say which kind each attribute is, and offer the lever
/// that actually works (the affix level / rarity) for the curve-backed ones.
/// </summary>
public static class ItemAttributes
{
    /// <summary>Display label, unit kind, and whether the value is a fraction.</summary>
    public readonly record struct Info(string Label, bool IsPercent, string Hint);

    private static readonly Dictionary<string, Info> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PhysicalDamage"]     = new("攻击力",        false, "武器的基础攻击力。带等级曲线，改数值无效，要改下面的词条等级。"),
        ["PhysicalDamageAdd"]  = new("物理伤害加成",  true,  "按比例提高物理伤害。直接生效。"),
        ["MagicDamage"]        = new("魔力攻击力",    false, "法杖的基础攻击力。带等级曲线，改数值无效。"),
        ["MagicDamageAdd"]     = new("魔法伤害加成",  true,  "按比例提高魔法伤害。直接生效。"),
        ["TotalDamageAdd"]     = new("总伤害加成",    true,  "按比例提高全部伤害。直接生效。"),
        ["MaxHealth"]          = new("最大生命",      false, "增加生命上限。带曲线时改数值无效。"),
        ["MaxMana"]            = new("最大魔力",      false, "增加魔力上限。直接生效。"),
        ["CritRate"]           = new("暴击率",        true,  "暴击发生概率。直接生效。"),
        ["CriticalDamage"]     = new("暴击伤害",      true,  "暴击时的额外伤害倍率。直接生效。"),
        ["DodgeChance"]        = new("闪避率",        true,  "闪避攻击的概率。直接生效。"),
        ["AttackRange"]        = new("攻击范围",      false, "武器能打到的距离。直接生效。"),
        ["BaseAttackRange"]    = new("基础攻击范围",  false, "攻击范围的基数。直接生效。"),
        ["AttackSpeed"]        = new("攻击速度",      false, "每秒攻击次数。直接生效。"),
        ["AttackSpeedAdd"]     = new("攻速加成",      true,  "按比例提高攻击速度。带曲线时改数值无效。"),
        ["MoveSpeed"]          = new("移动速度",      false, "装备后的移动速度。直接生效。"),
        ["MoveAcceleration"]   = new("移动加速度",    false, "起步加速快慢。直接生效。"),
        ["MaxHunger"]          = new("最大饱腹",      false, "饱腹度上限。直接生效。"),
        ["MaxHeat"]            = new("耐热上限",      false, "高温抗性上限。直接生效。"),
        ["MaxToxin"]           = new("抗毒上限",      false, "毒素抗性上限。直接生效。"),
    };

    public static Info Get(string attributeName)
        => Map.TryGetValue(attributeName, out var i)
           ? i
           : new Info(attributeName, false, "游戏未公开说明的属性。改数值通常直接生效。");

    public static bool IsKnown(string attributeName) => Map.ContainsKey(attributeName);

    /// <summary>All known attribute names, for a picker.</summary>
    public static IReadOnlyCollection<string> Names => Map.Keys;

    /// <summary>Rarity 0..5 as the game names it.</summary>
    public static string RarityName(int r) => r switch
    {
        0 => "白",
        1 => "绿",
        2 => "蓝",
        3 => "紫",
        4 => "橙",
        5 => "红",
        _ => $"未知({r})",
    };

    /// <summary>Affix tier names used by the game, same scale as item rarity.</summary>
    public static string TierName(string tier) => tier switch
    {
        "White" => "白",
        "Green" => "绿",
        "Blue" => "蓝",
        "Purple" => "紫",
        "Orange" => "橙",
        "Red" => "红",
        _ => tier,
    };
}
