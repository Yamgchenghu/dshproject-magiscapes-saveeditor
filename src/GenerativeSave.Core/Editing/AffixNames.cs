namespace GenerativeSave.Core.Editing;

/// <summary>
/// Turns an affix id like <c>Prefix_Sword_Crit</c> into something readable.
///
/// Why this parses the id instead of using an official table: the game's
/// name table lives in Oodle-compressed <c>.ucas</c> data behind an
/// AES-encrypted <c>.pak</c> index, and no Oodle decoder ships with the game,
/// so the strings cannot be extracted. The ids themselves are structured and
/// self-describing, so the segments are translated one by one and joined.
///
/// This is a <b>literal reading of the id</b>, not an official translation.
/// Two names are confirmed from the in-game tooltip: the item name
/// 「精准迅捷的大棒骨」 is the two prefixes concatenated, which pins
/// <c>Prefix_Sword_Crit</c> = 精准 and <c>Prefix_Sword_AttackSpeed</c> = 迅捷.
/// Everything else is derived from the id's own words.
///
/// A user-supplied name always wins; see <see cref="SetCustom"/>.
/// </summary>
public static class AffixNames
{
    /// <summary>Segment → Chinese, for the words that actually appear in saves.</summary>
    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        // ---- weapon / armour class
        ["Sword"] = "剑",
        ["Bow"] = "弓",
        ["Staff"] = "法杖",
        ["Hammer"] = "锤",
        ["Helmet"] = "头盔",
        ["Leggings"] = "护腿",
        ["Armor"] = "护甲",
        ["Accessory"] = "饰品",
        ["Weapon"] = "武器",

        // ---- stat the affix grants
        ["Crit"] = "暴击",
        ["CritRate"] = "暴击率",
        ["CritDamage"] = "暴击伤害",
        ["AttackSpeed"] = "攻速",
        ["AttackRange"] = "攻击范围",
        ["RangedRange"] = "远程射程",
        ["Physical"] = "物理",
        ["PhysicalDamage"] = "物理伤害",
        ["PhysAdd"] = "物理加成",
        ["MagicDamage"] = "法术伤害",
        ["MagicDefend"] = "法术防御",
        ["Mana"] = "魔力",
        ["ManaRegen"] = "魔力回复",
        ["Regen"] = "生命回复",
        ["Resist"] = "抗性",
        ["SkillDamage"] = "技能伤害",
        ["Distance"] = "射程",
        ["Heavy"] = "重击",

        // ---- element / school
        ["Earth"] = "土",
        ["Water"] = "水",
        ["Light"] = "光",
        ["Lightning"] = "雷",
        ["Metal"] = "金",
        ["Shadow"] = "暗影",
        ["Dispel"] = "驱散",
        ["Arcane"] = "奥术",
        ["Pyro"] = "火焰",
        ["Hydro"] = "水",
        ["Element"] = "元素",
        ["ElementDispel"] = "元素驱散",
        ["ElementShadow"] = "暗影元素",

        // ---- prefix families
        ["Spec"] = "专精",
        ["SpecSword"] = "剑专精",
        ["SpecArcane"] = "奥术专精",
        ["SpecPyro"] = "火焰专精",
        ["SpecHydro"] = "水系专精",
        ["Defense"] = "防御",
        ["Vital"] = "生命",
        ["Motion"] = "迅捷",
        ["Fortune"] = "幸运",
        ["Guard"] = "守护",
        ["Colossus"] = "巨像",
        ["Charm"] = "护符",
        ["AccessoryFortune"] = "饰品·幸运",
        ["DefenseCharm"] = "防御护符",
        ["HammerColossus"] = "锤·巨像",
        ["ManaCharm"] = "魔力护符",
        ["MotionCharm"] = "迅捷护符",
        ["VitalCharm"] = "生命护符",
        ["VitalGuard"] = "生命守护",

        // ---- expansion (词条扩展) families
        ["Status"] = "状态",
        ["Legend"] = "传奇",
        ["Event"] = "事件",
        ["Offense"] = "进攻",
        ["Environment"] = "环境",
        ["WeaponReach"] = "武器·攻击距离",
        ["WeaponFury"] = "武器·狂怒",
        ["WeaponPrecision"] = "武器·精准",
        ["WeaponSorcery"] = "武器·法术",
        ["AccessoryCritDamage"] = "饰品·暴击伤害",
        ["AccessoryGuard"] = "饰品·守护",
        ["AccessoryLife"] = "饰品·生命",
        ["ArmorVitality"] = "护甲·生命",
        ["BowDistance"] = "弓·射程",
        ["ImbueAfterCast"] = "施法后附魔",
        ["BurnOnHit"] = "命中点燃",

        // ---- tier
        ["Basic"] = "基础",
        ["Mid"] = "中级",
        ["High"] = "高级",
        ["Aligned"] = "同调",
    };

    /// <summary>Segment names that read better as a category than a word.</summary>
    private static readonly HashSet<string> ClassSegments = new(StringComparer.Ordinal)
    {
        "Sword", "Bow", "Staff", "Hammer", "Helmet", "Leggings", "Armor", "Accessory",
    };

    /// <summary>
    /// Names confirmed from the game itself, which beat anything derived from
    /// the id. The item name 「精准迅捷的大棒骨」 in the tooltip is exactly the
    /// two prefixes concatenated, which pins these two down for good.
    /// </summary>
    private static readonly Dictionary<string, string> Confirmed = new(StringComparer.Ordinal)
    {
        ["Prefix_Sword_Crit"] = "精准",
        ["Prefix_Sword_AttackSpeed"] = "迅捷",
    };

    /// <summary>
    /// Render an affix id for display. Unknown words are kept as-is so a new
    /// affix added by a game update still shows something truthful.
    /// </summary>
    public static string Describe(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id == "None") return "无";

        if (Custom.TryGetValue(id, out var custom)) return custom;
        if (Confirmed.TryGetValue(id, out var confirmed)) return confirmed;

        var segs = id.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return id;

        var parts = new List<string>();

        // Family prefixes carry no meaning once you are looking at the card.
        // "Expansion" is the affix-expansion pool and "Prefix" inside it is a
        // category marker, so both are dropped. AffixV2* is a schema version.
        int start = 0;
        if (segs[0] is "Prefix" or "Suffix") start = 1;
        else if (segs[0] == "Expansion")
        {
            start = 1;
            if (start < segs.Length && segs[start] == "Prefix") start++;
            else if (start < segs.Length && segs[start] == "Legend") { parts.Add("传奇"); start++; }
        }
        else if (segs[0].StartsWith("AffixV2", StringComparison.Ordinal)) start = 1;

        for (int i = start; i < segs.Length; i++)
        {
            var s = segs[i];
            // A trailing bare number ("Offense_26") is a table row, not a word.
            if (i == segs.Length - 1 && s.All(char.IsDigit)) continue;
            parts.Add(Words.TryGetValue(s, out var w) ? w : s);
        }

        return parts.Count == 0 ? id : string.Join("·", parts);
    }

    /// <summary>True when the id is a prefix (affects the generated item name).</summary>
    public static bool IsPrefix(string id) => id.StartsWith("Prefix_", StringComparison.Ordinal);

    /// <summary>True when the id is a suffix.</summary>
    public static bool IsSuffix(string id) =>
        id.StartsWith("Suffix_", StringComparison.Ordinal)
        || id.StartsWith("Expansion_", StringComparison.Ordinal)
        || id.StartsWith("AffixV2", StringComparison.Ordinal)
        || id.StartsWith("Legend_", StringComparison.Ordinal);

    /// <summary>
    /// What the affix does, when the id itself says so. This is read off the
    /// stat word in the id, so it describes the stat the affix targets, not the
    /// exact roll — the numbers are rolled from the package seed and level and
    /// are not stored in the save.
    /// </summary>
    public static string Effect(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id == "None") return "";

        // Most specific first: a compound word beats its parts.
        if (id.Contains("WeaponReach")) return "延长武器的攻击距离";
        if (id.Contains("WeaponFury")) return "击中后短时间提升攻击力";
        if (id.Contains("WeaponPrecision")) return "提升暴击相关表现";
        if (id.Contains("WeaponSorcery")) return "提升武器附带的法术效果";
        if (id.Contains("AccessoryCritDamage")) return "提升暴击伤害";
        if (id.Contains("AccessoryGuard")) return "提升减伤";
        if (id.Contains("AccessoryLife")) return "提升最大生命";
        if (id.Contains("ArmorVitality")) return "提升护甲提供的生命";
        if (id.Contains("BowDistance")) return "提升弓的射程";
        if (id.Contains("CritDamage")) return "提升暴击伤害";
        if (id.Contains("CritRate") || id.EndsWith("_Crit")) return "提升暴击率";
        if (id.Contains("AttackSpeed")) return "提升攻击速度";
        if (id.Contains("AttackRange")) return "扩大攻击范围";
        if (id.Contains("RangedRange")) return "扩大远程射程";
        if (id.Contains("SkillDamage")) return "提升技能伤害";
        if (id.Contains("MagicDamage")) return "提升法术攻击";
        if (id.Contains("PhysicalDamage")) return "提升物理攻击";
        if (id.Contains("PhysAdd")) return "追加物理伤害";
        if (id.Contains("MagicDefend")) return "提升法术防御";
        if (id.Contains("ManaRegen")) return "提升魔力回复";
        if (id.Contains("Resist")) return "提升元素抗性";
        if (id.Contains("ImbueAfterCast")) return "施法后武器获得附魔";
        if (id.Contains("BurnOnHit")) return "命中时点燃目标";
        if (id.Contains("Regen")) return "缓慢回复生命";
        if (id.Contains("Mana")) return "提升最大魔力";
        if (id.Contains("Vital")) return "提升最大生命";
        if (id.Contains("Heavy")) return "增加击退与硬直";
        if (id.Contains("Fortune")) return "提升掉落品质";

        // AffixV2Aligned_* are elemental damage rows.
        if (id.StartsWith("AffixV2Aligned_", StringComparison.Ordinal))
        {
            var el = id.Split('_').ElementAtOrDefault(1) ?? "";
            var tier = id.Split('_').LastOrDefault() ?? "";
            var elName = Words.TryGetValue(el, out var e) ? e : el;
            var tierName = Words.TryGetValue(tier, out var t) ? t : tier;
            return $"{tierName}的{elName}属性伤害";
        }

        return "";
    }

    // ------------------------------------------------------------------ custom names
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _custom;

    private static Dictionary<string, string> Custom
    {
        get
        {
            lock (Gate) return _custom ??= new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Path of the user-editable override file (global, next to the app).</summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GenerativeSaveEditor", "affix-names.json");

    /// <summary>Teach a name that beats the derived one. Pass null/empty to forget.</summary>
    public static void SetCustom(string id, string? name)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(name)) Custom.Remove(id);
            else Custom[id] = name.Trim();
        }
    }

    public static IReadOnlyDictionary<string, string> CustomNames
    {
        get { lock (Gate) return new Dictionary<string, string>(Custom, StringComparer.Ordinal); }
    }

    /// <summary>Load the override file. Missing or broken file is not an error.</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var text = File.ReadAllText(FilePath);
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(text);
            if (map is null) return;
            lock (Gate)
            {
                _custom = new Dictionary<string, string>(map, StringComparer.Ordinal);
            }
        }
        catch
        {
            // A corrupt override file must never stop the editor from opening.
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Dictionary<string, string> snapshot;
            lock (Gate) snapshot = new(Custom, StringComparer.Ordinal);
            File.WriteAllText(FilePath,
                System.Text.Json.JsonSerializer.Serialize(snapshot,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignored on purpose: naming is a convenience, not save data.
        }
    }
}
