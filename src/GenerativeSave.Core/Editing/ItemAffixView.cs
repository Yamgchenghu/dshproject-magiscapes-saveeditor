using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>
/// One affix package on an item: the roll seed plus the level/rarity/prefix the
/// game uses to regenerate the actual stat lines. Editing Level or Rarity and
/// leaving Seed alone re-rolls nothing; editing Seed re-rolls the stat values.
/// </summary>
public sealed class AffixPackageView
{
    public string Label { get; init; } = "";
    public List<SaveProperty>? Props { get; init; }

    public SaveProperty? ValidProp => Props?.FirstOrDefault(p => p.Name == "bValid");
    public SaveProperty? SeedProp => Props?.FirstOrDefault(p => p.Name == "Seed");
    public SaveProperty? LevelProp => Props?.FirstOrDefault(p => p.Name == "Level");
    public SaveProperty? RarityProp => Props?.FirstOrDefault(p => p.Name == "Rarity");
    public SaveProperty? PrefixProp => Props?.FirstOrDefault(p => p.Name == "PrefixId");
    public SaveProperty? SuffixProp => Props?.FirstOrDefault(p => p.Name == "SuffixIds");

    public bool Valid => ValueEditor.EffectiveValueOrNull(ValidProp) is bool b && b;
    public int Seed => ValueEditor.EffectiveValueOrNull(SeedProp) is int i ? i : 0;
    public int Level => ValueEditor.EffectiveValueOrNull(LevelProp) is int i ? i : 0;
    public int Rarity => ValueEditor.EffectiveValueOrNull(RarityProp) is int i ? i : 0;
    public string Prefix => ValueEditor.EffectiveValueOrNull(PrefixProp) as string ?? "None";

    /// <summary>Suffix entries, read straight from the array's pending value.</summary>
    public List<string> Suffixes =>
        SuffixProp is null ? new List<string>() : ValueEditor.ReadStringArray(SuffixProp);

    /// <summary>Write a new suffix list back as one FString array block.</summary>
    public bool SetSuffixes(IEnumerable<string> values)
    {
        if (SuffixProp is null) return false;
        SuffixProp.MarkDirty(ValueEditor.EncodeStringArray(values));
        return true;
    }

    public string RarityName => Rarity switch
    {
        0 => "白", 1 => "绿", 2 => "蓝", 3 => "紫", 4 => "橙", 5 => "红",
        _ => $"稀有度 {Rarity}",
    };
}

/// <summary>All affix data attached to one item, split into its three packages.</summary>
public sealed class ItemAffixView
{
    public List<SaveProperty>? SaveData { get; init; }
    public List<AffixPackageView> Packages { get; init; } = new();

    public AffixPackageView? Base => Packages.FirstOrDefault(p => p.Label == "基础词条");
    public AffixPackageView? Enchant => Packages.FirstOrDefault(p => p.Label == "附魔词条");
    public AffixPackageView? Scroll => Packages.FirstOrDefault(p => p.Label == "卷轴词条");

    public SaveProperty? ReforgeProp =>
        SaveData?.FirstOrDefault(p => p.Name == "EquipmentAffixData")?.Child("ReforgeCount");

    public static ItemAffixView? From(List<SaveProperty>? group)
    {
        var sd = group?.FirstOrDefault(p => p.Name == "ItemSaveData");
        if (sd is null) return null;

        var views = new List<AffixPackageView>();

        // EquipmentAffixData holds two identically-shaped packages.
        var affixData = sd.Child("EquipmentAffixData");
        var basePkg = affixData?.Child("BasePackage");
        var enchPkg = affixData?.Child("EnchantPackage");
        if (basePkg?.Children is { Count: > 0 } b)
            views.Add(new AffixPackageView { Label = "基础词条", Props = b });
        if (enchPkg?.Children is { Count: > 0 } e)
            views.Add(new AffixPackageView { Label = "附魔词条", Props = e });

        // The scroll package sits outside EquipmentAffixData.
        var scroll = sd.Child("EnchantScrollAffixData");
        if (scroll?.Children is { Count: > 0 } s)
            views.Add(new AffixPackageView { Label = "卷轴词条", Props = s });

        return new ItemAffixView { SaveData = sd.Children, Packages = views };
    }
}
