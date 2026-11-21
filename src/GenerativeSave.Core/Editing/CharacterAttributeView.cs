using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>
/// One AI-generated character from GenCharacter__AIGenCharacter.sav.
/// This module is where the game keeps an NPC's real attributes: Strength,
/// Agility, Constitution, Intelligence and Luck live in the BaseAttribute
/// struct. NpcSubsystem only stores percentage-style status values, so the two
/// modules are complementary.
/// </summary>
public sealed class CharacterAttributeView
{
    public string CharacterId { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;

    /// <summary>The whole element property list, for anything not surfaced here.</summary>
    public List<SaveProperty> Props { get; init; } = new();

    public SaveProperty? Prop(string name) => Props.FirstOrDefault(x => x.Name == name);

    /// <summary>The BaseAttribute struct, or null when the element lacks one.</summary>
    public SaveProperty? BaseAttribute => Prop("BaseAttribute");

    /// <summary>The five attribute properties inside BaseAttribute.</summary>
    public List<SaveProperty> Attributes =>
        BaseAttribute?.Children ?? new List<SaveProperty>();

    public SaveProperty? Attr(string name) =>
        Attributes.FirstOrDefault(x => x.Name == name);

    public int Strength => Int(Attr("Strength"));
    public int Agility => Int(Attr("Agility"));
    public int Constitution => Int(Attr("Constitution"));
    public int Intelligence => Int(Attr("Intelligence"));
    public int Luck => Int(Attr("Luck"));

    private static int Int(SaveProperty? p) =>
        ValueEditor.EffectiveValueOrNull(p) is int i ? i : 0;

    public string Display =>
        string.IsNullOrWhiteSpace(CharacterName)
            ? CharacterId
            : $"{CharacterName}（{CharacterId}）";

    /// <summary>True when this character can be edited (has the attribute struct).</summary>
    public bool HasAttributes => BaseAttribute is not null && Attributes.Count > 0;

    // ------------------------------------------------------------ AttributeMap
    /// <summary>
    /// The runtime combat attributes. This is the field that actually decides
    /// max health, max mana, damage, crit and speed — BaseAttribute above is only
    /// the generation input, which is why raising Constitution does not move the
    /// health bar.
    ///
    /// Layout: a MapProperty whose single entry value holds ten repeated
    /// (AttributeName, Attribute, AttributeOwner, Value, Curve, RegistryType)
    /// groups, so it has to be split in sixes rather than read as a normal map.
    /// </summary>
    public SaveProperty? AttributeMapProp => Prop("AttributeMap");

    private List<CombatAttribute>? _combat;
    public List<CombatAttribute> Combat => _combat ??= ReadCombat();

    private List<CombatAttribute> ReadCombat()
    {
        var list = new List<CombatAttribute>();
        var map = AttributeMapProp;
        if (map?.MapEntries is null) return list;

        foreach (var entry in map.MapEntries)
        {
            // Key carries (AttributeName, Attribute, AttributeOwner); the value
            // carries (Value, Curve, RegistryType).
            var name = entry.KeyProps.FirstOrDefault(p => p.Name == "AttributeName")?.Value as string
                       ?? entry.KeyProps.FirstOrDefault(p => p.Name == "AttributeName")?.Value?.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            var owner = entry.KeyProps.FirstOrDefault(p => p.Name == "AttributeOwner")?.Value?.ToString() ?? "";
            var valueProp = entry.ValueProps.FirstOrDefault(p => p.Name == "Value");
            if (valueProp is null) continue;

            list.Add(new CombatAttribute
            {
                Name = name,
                Owner = owner,
                ValueProp = valueProp,
                Value = ToDouble(ValueEditor.EffectiveValueOrNull(valueProp)),
            });
        }
        return list;
    }

    public CombatAttribute? CombatAttr(string name) =>
        Combat.FirstOrDefault(a => a.Name == name);

    private static double ToDouble(object? o) => o switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        _ => 0,
    };
}

/// <summary>One entry of the runtime AttributeMap (MaxHealth, CritRate, …).</summary>
public sealed class CombatAttribute
{
    public string Name { get; init; } = "";
    /// <summary>The owning attribute set, e.g. /Script/GenerativeGame.HealthAttributeSet.</summary>
    public string Owner { get; init; } = "";
    public SaveProperty? ValueProp { get; init; }
    public double Value { get; init; }

    /// <summary>Chinese label, falling back to the raw name for unknown entries.</summary>
    public string Label => Name switch
    {
        "PhysicalDamage" => "物理攻击",
        "MagicDamage" => "法术攻击",
        "MaxHealth" => "血量上限",
        "MaxMana" => "魔力上限",
        "CritRate" => "暴击率",
        "CriticalDamage" => "暴击伤害",
        "DodgeChance" => "闪避率",
        "AttackSpeed" => "攻击速度",
        "MoveSpeed" => "移动速度",
        "MoveAcceleration" => "移动加速度",
        _ => Name,
    };

    /// <summary>Percent-style stats are stored as a fraction and shown as %.</summary>
    public bool IsPercent => Name is "CritRate" or "DodgeChance";

    /// <summary>CriticalDamage is a multiplier (1.17 = 117%).</summary>
    public bool IsMultiplier => Name is "CriticalDamage";

    public string Display => IsPercent
        ? $"{Value * 100:0.##}%"
        : IsMultiplier
            ? $"{Value * 100:0.##}%"
            : $"{Value:0.###}";

    /// <summary>
    /// Text the user types is in display units: percent for the fraction stats.
    /// </summary>
    public bool TryEncode(string text, out byte[]? bytes, out string error)
    {
        error = "";
        bytes = null;
        if (ValueProp is null) { error = "该属性没有可写的值字段"; return false; }

        if (!double.TryParse(text.Trim().TrimEnd('%'), out var entered))
        {
            error = "需要一个数字";
            return false;
        }

        double raw = IsPercent || IsMultiplier ? entered / 100.0 : entered;

        bytes = ValueEditor.Encode(ValueProp, raw.ToString("R",
            System.Globalization.CultureInfo.InvariantCulture), out error);
        return bytes is not null;
    }
}
