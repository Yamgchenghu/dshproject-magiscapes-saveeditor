using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>One attribute row inside ItemConfig.EquipmentAttributeMap.</summary>
public sealed class ItemAttributeRow
{
    public required string Name { get; init; }
    public required SaveProperty ValueProp { get; init; }
    public SaveProperty? CurveTableProp { get; init; }
    public SaveProperty? CurveRowProp { get; init; }

    public float Value
        => ValueEditor.EffectiveValueOrNull(ValueProp) is float f ? f
         : ValueEditor.EffectiveValueOrNull(ValueProp) is double d ? (float)d
         : ValueEditor.EffectiveValueOrNull(ValueProp) is int i ? i
         : 0f;

    public string CurveTable => CurveTableProp is null ? "" : ValueEditor.Format(CurveTableProp) ?? "";
    public string CurveRow => CurveRowProp is null ? "" : ValueEditor.Format(CurveRowProp) ?? "";

    /// <summary>
    /// True when the game recomputes this number from a level curve, so writing
    /// Value has no visible effect. This is what made edits look ignored.
    /// </summary>
    public bool IsCurveBacked =>
        CurveTable.Contains("ItemAttrCurveTable") && CurveRow.Length > 0;

    public string Label => ItemAttributes.Get(Name).Label;
    public bool IsPercent => ItemAttributes.Get(Name).IsPercent;

    /// <summary>"攻击力 99" or "暴击率 30%".</summary>
    public string Display
    {
        get
        {
            var info = ItemAttributes.Get(Name);
            if (IsPercent) return $"{Label} {Value * 100:0.##}%";
            return $"{Label} {Value:0.##}";
        }
    }
}

/// <summary>One affix package (base / enchant / scroll).</summary>
public sealed class ItemAffixPackage
{
    public required string Label { get; init; }
    public required List<SaveProperty> Props { get; init; }

    public SaveProperty? Valid => Props.FirstOrDefault(p => p.Name == "bValid");
    public SaveProperty? Seed => Props.FirstOrDefault(p => p.Name == "Seed");
    public SaveProperty? Level => Props.FirstOrDefault(p => p.Name == "Level");
    public SaveProperty? Rarity => Props.FirstOrDefault(p => p.Name == "Rarity");
    public SaveProperty? PrefixId => Props.FirstOrDefault(p => p.Name == "PrefixId");
    public SaveProperty? SuffixIds => Props.FirstOrDefault(p => p.Name == "SuffixIds");

    public int LevelValue => ValueEditor.EffectiveValueOrNull(Level) is int i ? i : 0;
    public int RarityValue => ValueEditor.EffectiveValueOrNull(Rarity) is int i ? i : 0;
    public string RarityName => ItemAttributes.RarityName(RarityValue);
    public string PrefixText => PrefixId is null ? "" : ValueEditor.Format(PrefixId) ?? "";
}

/// <summary>
/// Everything an item carries that the game will honour after an edit: its
/// name and description text, its flat attributes, its affix levels, its
/// stack limit and rarity.
///
/// Curve-backed attributes are exposed as read-only with an explanation,
/// because writing them is exactly the trap the user hit.
/// </summary>
public sealed class ItemDetailView
{
    public required List<SaveProperty> Group { get; init; }
    public required string ItemId { get; init; }

    public SaveProperty? Config { get; init; }
    public SaveProperty? SaveData { get; init; }

    /// <summary>
    /// True for a procedurally generated item (8+ digit id, 100000xx / 200000xx).
    ///
    /// This single flag decides what may be written, and the reason is not
    /// cosmetic. An OFFICIAL item's ItemConfig is only a cached copy of a row in
    /// the game's own DT_Items data table: the game rebuilds it from that table
    /// when the save loads, so an edited name, description or attribute is
    /// silently replaced — and a value whose shape no longer matches the table
    /// can corrupt the save. An AI item is the opposite: the save IS the only
    /// definition, the game reads it back verbatim, so its text and attributes
    /// are genuinely editable.
    /// </summary>
    public bool IsAiGenerated => ItemId.Length >= 8;

    /// <summary>Only AI items may have their text, attributes and affixes written.</summary>
    public bool CanEditContent => IsAiGenerated;

    // ---- text -------------------------------------------------------------

    public SaveProperty? NameProp => Config?.Child("ItemName");
    public SaveProperty? DescProp => Config?.Child("Desc");
    public SaveProperty? ExplainProp => Config?.Child("Explain");

    public string CurrentName => FTextCodec.ReadName(NameProp?.OriginalValue);
    public string CurrentDesc => FTextCodec.ReadText(DescProp?.OriginalValue);

    /// <summary>
    /// ItemConfig.Explain is usually just the item name, but for weapons and
    /// armour the game stuffs the whole affix summary in there:
    ///
    ///   大棒骨
    ///   [Affix] Lv=65 Rarity=2
    ///   - 精准的 [Prefix_Sword_Crit] (Tier=Purple)
    ///   ...
    ///
    /// Only the first line is the name; the rest is a generated dump that the
    /// game overwrites. So the name field must never be written back into
    /// Explain verbatim, or the tooltip header becomes the whole summary.
    /// </summary>
    public string CurrentExplain
    {
        get
        {
            if (ExplainProp is null) return "";
            var raw = ValueEditor.Format(ExplainProp) ?? "";
            var line = raw.Split('\n', '\r')[0].Trim();
            return line;
        }
    }

    /// <summary>True when Explain carries only the name, so it can be replaced.</summary>
    public bool ExplainIsPureName
    {
        get
        {
            if (ExplainProp is null) return false;
            var raw = ValueEditor.Format(ExplainProp) ?? "";
            return !raw.Contains('\n') && raw.Trim().Length <= 60;
        }
    }

    /// <summary>The short name the game shows above the tooltip.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(CurrentExplain) ? CurrentExplain
      : !string.IsNullOrWhiteSpace(CurrentName) ? CurrentName
      : ItemId;

    // ---- simple numbers ---------------------------------------------------

    public SaveProperty? RarityProp => Config?.Child("ItemRarity");
    public SaveProperty? LevelMinProp => Config?.Child("EquipmentLevelMin");
    public SaveProperty? LevelMaxProp => Config?.Child("EquipmentLevelMax");
    public SaveProperty? MaxStackProp => Config?.Child("MaxStack");
    public SaveProperty? MaxChargesProp => Config?.Child("MaxCharges");

    public int Rarity => ValueEditor.EffectiveValueOrNull(RarityProp) is int i ? i : 0;
    public string RarityName => ItemAttributes.RarityName(Rarity);
    public int LevelMin => ValueEditor.EffectiveValueOrNull(LevelMinProp) is int i ? i : 0;
    public int LevelMax => ValueEditor.EffectiveValueOrNull(LevelMaxProp) is int i ? i : 0;
    public int MaxStack => ValueEditor.EffectiveValueOrNull(MaxStackProp) is int i ? i : 0;

    // ---- attributes -------------------------------------------------------

    public List<ItemAttributeRow> Attributes { get; init; } = new();

    public bool HasAttributes => Attributes.Count > 0;
    public int EditableAttributeCount => Attributes.Count(a => !a.IsCurveBacked);
    public int CurveAttributeCount => Attributes.Count(a => a.IsCurveBacked);

    // ---- affixes ----------------------------------------------------------

    public List<ItemAffixPackage> Packages { get; init; } = new();
    public bool HasAffixes => Packages.Count > 0;

    // ---- construction -----------------------------------------------------

    public static ItemDetailView? From(InventoryItem item)
    {
        if (item.Group is null || item.Group.Count == 0) return null;

        var cfg = item.Group.FirstOrDefault(p => p.Name == "ItemConfig");
        var sd = item.Group.FirstOrDefault(p => p.Name == "ItemSaveData");

        var view = new ItemDetailView
        {
            Group = item.Group,
            ItemId = item.ItemId,
            Config = cfg,
            SaveData = sd,
        };

        // Attribute map: each entry is a key list (AttributeName / Attribute /
        // AttributeOwner) plus a value list (Value / Curve / RegistryType).
        var map = cfg?.Child("EquipmentAttributeMap");
        if (map?.MapEntries is { Count: > 0 })
        {
            var rows = new List<ItemAttributeRow>();
            foreach (var e in map.MapEntries)
            {
                var nameProp = e.KeyProps?.FirstOrDefault(k => k.Name == "AttributeName");
                if (nameProp is null) continue;
                var an = ValueEditor.Format(nameProp) ?? "";
                if (an.Length == 0) continue;

                var valueProp = e.ValueProps?.FirstOrDefault(v => v.Name == "Value");
                if (valueProp is null) continue;

                var curve = e.ValueProps?.FirstOrDefault(v => v.Name == "Curve");
                rows.Add(new ItemAttributeRow
                {
                    Name = an,
                    ValueProp = valueProp,
                    CurveTableProp = curve?.Child("CurveTable"),
                    CurveRowProp = curve?.Child("RowName"),
                });
            }
            view = new ItemDetailView
            {
                Group = view.Group, ItemId = view.ItemId, Config = view.Config,
                SaveData = view.SaveData, Attributes = rows,
            };
        }

        // Affix packages: base and enchant live under EquipmentAffixData, the
        // scroll package is its own sibling.
        var packages = new List<ItemAffixPackage>();
        var affixData = sd?.Child("EquipmentAffixData");
        if (affixData?.Child("BasePackage")?.Children is { Count: > 0 } b)
            packages.Add(new ItemAffixPackage { Label = "基础词条", Props = b });
        if (affixData?.Child("EnchantPackage")?.Children is { Count: > 0 } en)
            packages.Add(new ItemAffixPackage { Label = "附魔词条", Props = en });
        if (sd?.Child("EnchantScrollAffixData")?.Children is { Count: > 0 } sc)
            packages.Add(new ItemAffixPackage { Label = "卷轴词条", Props = sc });

        if (packages.Count > 0)
            view = new ItemDetailView
            {
                Group = view.Group, ItemId = view.ItemId, Config = view.Config,
                SaveData = view.SaveData, Attributes = view.Attributes,
                Packages = packages,
            };

        return view;
    }

    // ---- writing ----------------------------------------------------------

    /// <summary>
    /// Replace the item's description. The value is written as a plain inline
    /// FText so the game stops looking up the old table key.
    /// </summary>
    public bool SetDescription(string text, out string error)
    {
        error = string.Empty;
        if (!CanEditContent)
        {
            error = "官方道具的说明和词条来自游戏数据表，存档里这份只是缓存，"
                  + "改了会被游戏覆盖，还可能坏档。官方道具只能改数量。";
            return false;
        }
        if (DescProp is null) { error = "这件物品没有描述字段。"; return false; }
        if (text.Length > 2000) { error = "描述太长（上限 2000 字）。"; return false; }
        DescProp.MarkDirty(FTextCodec.Encode(text, DescProp.OriginalValue));
        return true;
    }

    /// <summary>
    /// Replace the item's display name. Explain is the short label the tooltip
    /// title uses, so it is updated together with the FText name.
    /// </summary>
    public bool SetName(string text, out string error)
    {
        error = string.Empty;
        if (!CanEditContent)
        {
            error = "官方道具的说明和词条来自游戏数据表，存档里这份只是缓存，"
                  + "改了会被游戏覆盖，还可能坏档。官方道具只能改数量。";
            return false;
        }
        if (text.Length == 0) { error = "名称不能为空。"; return false; }
        if (text.Length > 60) { error = "名称太长（上限 60 字）。"; return false; }

        if (NameProp is not null)
            NameProp.MarkDirty(FTextCodec.Encode(text, NameProp.OriginalValue));

        // Only touch Explain when it holds nothing but the name. For weapons it
        // also contains the generated affix summary, and flattening that to a
        // single line would replace the whole tooltip body.
        if (ExplainProp is not null && ExplainIsPureName)
        {
            var bytes = ValueEditor.EncodeFString(text, out var e2);
            if (bytes is null) { error = e2; return false; }
            ExplainProp.MarkDirty(bytes);
        }

        return true;
    }

    /// <summary>Write a flat attribute value. Refuses curve-backed rows.</summary>
    public bool SetAttribute(string attributeName, float value, out string error)
    {
        error = string.Empty;
        if (!CanEditContent)
        {
            error = "官方道具的说明和词条来自游戏数据表，存档里这份只是缓存，"
                  + "改了会被游戏覆盖，还可能坏档。官方道具只能改数量。";
            return false;
        }
        var row = Attributes.FirstOrDefault(a => a.Name == attributeName);
        if (row is null) { error = $"这件物品没有「{attributeName}」这条属性。"; return false; }
        if (row.IsCurveBacked)
        {
            error = $"「{row.Label}」的数值由等级曲线决定（{row.CurveRow}），"
                  + "直接改数值游戏会重算覆盖。请改下面的词条等级。";
            return false;
        }

        var bytes = ValueEditor.Encode(row.ValueProp, value.ToString("0.####"), out var e);
        if (bytes is null) { error = e; return false; }
        row.ValueProp.MarkDirty(bytes);
        return true;
    }

    /// <summary>Set the affix package level, which is what drives curve stats.</summary>
    public bool SetAffixLevel(string packageLabel, int level, out string error)
    {
        error = string.Empty;
        if (!CanEditContent)
        {
            error = "官方道具的说明和词条来自游戏数据表，存档里这份只是缓存，"
                  + "改了会被游戏覆盖，还可能坏档。官方道具只能改数量。";
            return false;
        }
        var pkg = Packages.FirstOrDefault(p => p.Label == packageLabel);
        if (pkg?.Level is null) { error = $"找不到「{packageLabel}」。"; return false; }
        if (level < 1 || level > 200) { error = "词条等级要在 1~200 之间。"; return false; }

        var bytes = ValueEditor.Encode(pkg.Level, level.ToString(), out var e);
        if (bytes is null) { error = e; return false; }
        pkg.Level.MarkDirty(bytes);
        return true;
    }

    /// <summary>Set the affix package rarity (0 白 … 5 红).</summary>
    public bool SetAffixRarity(string packageLabel, int rarity, out string error)
    {
        error = string.Empty;
        if (!CanEditContent)
        {
            error = "官方道具的说明和词条来自游戏数据表，存档里这份只是缓存，"
                  + "改了会被游戏覆盖，还可能坏档。官方道具只能改数量。";
            return false;
        }
        var pkg = Packages.FirstOrDefault(p => p.Label == packageLabel);
        if (pkg?.Rarity is null) { error = $"找不到「{packageLabel}」。"; return false; }
        if (rarity < 0 || rarity > 5) { error = "稀有度要在 0~5 之间。"; return false; }

        var bytes = ValueEditor.Encode(pkg.Rarity, rarity.ToString(), out var e);
        if (bytes is null) { error = e; return false; }
        pkg.Rarity.MarkDirty(bytes);
        return true;
    }
}
