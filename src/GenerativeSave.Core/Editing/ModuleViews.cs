using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>Strongly-typed, editable views over the modules we fully decoded.</summary>
public abstract class ModuleView
{
    public SaveModule Module { get; init; } = null!;
    public SaveProperty? Root { get; init; }
    public abstract string Title { get; }
}

public sealed class PlayerView : ModuleView
{
    public override string Title => "玩家";
    public List<SaveProperty> MapProps { get; init; } = new();

    public ulong SteamId { get; set; }
    public string? PlayerName { get; set; }
    public float CurrentHealth { get; set; }
    public float MaxHealth { get; set; }
    public float CurrentMagic { get; set; }
    public float CurrentHunger { get; set; }
    public float CurrentHeat { get; set; }
    public float MaxHeat { get; set; }
    public float CurrentToxin { get; set; }
    public float MaxToxin { get; set; }
    public int ReputationLevel { get; set; }
    public long ReputationExperience { get; set; }
    public int CurrentHotbarIndex { get; set; }
    public string? TrackedQuest { get; set; }
    public string? SkinId { get; set; }
    public bool HasCalamityRiftReturn { get; set; }
    public List<SaveProperty>? Coord { get; set; }

    /// <summary>Real item count inside the single wrapper element.</summary>
    public int InventoryCount => InventoryGroups.Count;

    /// <summary>
    /// The inventory is one wrapper element holding 5 properties per item, all
    /// with repeating names (ItemID, Count, EquipmentHandle, ItemConfig,
    /// ItemSaveData). Group them positionally so each item keeps its own Count.
    /// </summary>
    public List<List<SaveProperty>> InventoryGroups => GroupsOf("InventoryItems");

    /// <summary>
    /// The 10 quick-bar slots. Identical group shape to the backpack, but a
    /// separate array; CurrentHotbarIndex points into it.
    /// </summary>
    public List<List<SaveProperty>> HotbarGroups => GroupsOf("HotbarItems");

    public List<InventoryItem> HotbarItems => ToItems(HotbarGroups);

    /// <summary>Worn gear: helmet / chest / legs / boots.</summary>
    public List<List<SaveProperty>> EquipmentGroups => GroupsOf("EquipmentItems");
    public List<InventoryItem> EquipmentItems => ToItems(EquipmentGroups);

    /// <summary>Rings, amulets and the like.</summary>
    public List<List<SaveProperty>> AccessoryGroups => GroupsOf("AccessoryItems");
    public List<InventoryItem> AccessoryItems => ToItems(AccessoryGroups);

    /// <summary>Weapons parked in the weapon rack, not the quick bar.</summary>
    public List<List<SaveProperty>> WeaponGroups => GroupsOf("WeaponItems");
    public List<InventoryItem> WeaponItems => ToItems(WeaponGroups);

    private List<List<SaveProperty>> GroupsOf(string arrayName)
    {
        var wrapper = MapProps.FirstOrDefault(p => p.Name == arrayName)
                                 ?.ArrayElements?.FirstOrDefault();
        var groups = new List<List<SaveProperty>>();
        if (wrapper is null) return groups;

        List<SaveProperty>? cur = null;
        foreach (var p in wrapper.Properties)
        {
            if (p.Name == "ItemID")
            {
                cur = new List<SaveProperty>();
                groups.Add(cur);
            }
            cur?.Add(p);
        }
        return groups;
    }

    /// <summary>Display rows for the inventory page: id, count, and item name.</summary>
    public List<InventoryItem> InventoryItems => ToItems(InventoryGroups);

    private static List<InventoryItem> ToItems(List<List<SaveProperty>> groups) =>
        groups.Select(g =>
        {
            var cfg = g.FirstOrDefault(x => x.Name == "ItemConfig");
            return new InventoryItem
            {
                Group = g,
                // ItemID is a NameProperty (string), not an int. Read through
                // EffectiveValue so a pending edit shows immediately.
                ItemId = ValueEditor.EffectiveValueOrNull(g.FirstOrDefault(x => x.Name == "ItemID")) as string ?? "?",
                CountProp = g.FirstOrDefault(x => x.Name == "Count"),
                Name = ValueEditor.EffectiveValueOrNull(cfg?.Child("Explain")) as string,
                MaxStack = ValueEditor.EffectiveValueOrNull(cfg?.Child("MaxStack")) is int m ? m : 0,
                ItemClass = ValueEditor.EffectiveValueOrNull(cfg?.Child("ItemClass")) as string,
                Icon = ValueEditor.EffectiveValueOrNull(cfg?.Child("Icon")) as string,
            };
        }).ToList();
}

public sealed class InventoryItem
{
    public List<SaveProperty> Group { get; init; } = new();
    public string ItemId { get; init; } = "?";
    public SaveProperty? CountProp { get; init; }
    public string? Name { get; init; }
    public int MaxStack { get; init; }
    public string? ItemClass { get; init; }
    public string? Icon { get; init; }

    public int Count => ValueEditor.EffectiveValueOrNull(CountProp) is int i ? i : 0;

    /// <summary>True when this slot is one of the game's empty placeholders.</summary>
    public bool IsEmpty => ItemId is "None" or "" or "?"
                           || (string.IsNullOrEmpty(Name) && Count == 0);

    /// <summary>
    /// A readable fallback name for items whose Explain is blank. The game
    /// leaves Explain empty for a few vehicle/quest items and localises the
    /// real name through an FText key we cannot resolve offline, so derive
    /// something honest from the icon or blueprint path instead of showing
    /// nothing. This is display-only: nothing is written back.
    /// </summary>
    private string? DerivedName
    {
        get
        {
            // Icon: /Game/.../T_Tramcar_icon_Sprite.T_Tramcar_icon_Sprite
            var src = Icon ?? ItemClass;
            if (string.IsNullOrWhiteSpace(src)) return null;

            var tail = src;
            int dot = tail.LastIndexOf('.');
            if (dot >= 0) tail = tail[..dot];
            int slash = tail.LastIndexOf('/');
            if (slash >= 0) tail = tail[(slash + 1)..];

            tail = tail.Replace("_icon_Sprite", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("_Sprite", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("BP_", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("_C", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("T_", "", StringComparison.OrdinalIgnoreCase)
                       .Replace("_", " ")
                       .Trim();

            return string.IsNullOrWhiteSpace(tail) ? null : tail;
        }
    }

    /// <summary>Known names for ids whose own config carries no readable text.</summary>
    private string? KnownName => ItemId switch
    {
        "3040003" => "矿车",
        _ => null,
    };

    /// <summary>
    /// The display name, never blank, and always a single line. Explain can be
    /// an empty string (not null) for a few items, so a null-coalesce is not
    /// enough here. Weapon Explain carries the whole affix dump across many
    /// lines, which would wreck the list row, so only the first line is used.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                var first = Name!.Split('\n', '\r')[0].Trim();
                if (first.Length > 0) return first;
            }
            if (ItemId is "None" or "" or "?") return "（空槽位）";
            if (KnownName is not null) return $"{KnownName}（{ItemId}）";
            if (DerivedName is not null) return $"{DerivedName}（{ItemId}）";
            return $"（未知物品 {ItemId}）";
        }
    }

    /// <summary>The full multi-line Explain text, for detail panes.</summary>
    public string FullDescription => Name ?? "";

    /// <summary>Human label for the list: real name, or a clear placeholder.</summary>
    public string Display => IsEmpty ? "（空槽位）" : DisplayName;

}

public sealed class ManifestView : ModuleView
{
    public override string Title => "存档信息";

    public string? DisplayName { get; set; }
    public string? PlayerName { get; set; }
    public int MapSeed { get; set; }
    public double PlayTime { get; set; }
    public long ActiveVersion { get; set; }
    public long LastKnownGoodVersion { get; set; }
    public int KeepVersions { get; set; }
}

public sealed class WorldTimeView : ModuleView
{
    public override string Title => "世界时间";

    /// <summary>CurrentWorldTime is a float: total in-game hours.</summary>
    public float Hours { get; set; }

    public int Day => (int)(Hours / 24f) + 1;
    public int Hour => (int)Hours % 24;
    public int Minute => (int)((Hours - MathF.Floor(Hours)) * 60f);
}

public sealed class CampsiteView : ModuleView
{
    public override string Title => "营地";

    public bool HasCampsite { get; set; }
    public List<string> AiNpcIds { get; set; } = new();
    public List<string> RecruitedNpcIds { get; set; } = new();
    public List<string> KickedNpcIds { get; set; } = new();

    // ---- level ----
    public string CampsiteName { get; set; } = "";
    /// <summary>AllCampsite[].Level — the camp level shown in game.</summary>
    public int Level { get; set; }
    /// <summary>World day of the last upgrade, used by the game's upgrade timer.</summary>
    public int LastUpgradeWorldDay { get; set; }
    public int WorldDay { get; set; }

    // ---- position ----
    /// <summary>AllCampsite[].CampsiteCoord — where the camp is anchored.</summary>
    public CoordView? Coord { get; set; }

    /// <summary>Every cell the camp occupies (usually a small rectangle).</summary>
    public List<CoordView> Cells { get; set; } = new();
}

/// <summary>
/// A world coordinate. The game stores the same position redundantly as a float
/// triple plus tile indices, and the two must stay consistent: the indices drive
/// loading, the float is what the camera and physics use.
/// </summary>
public sealed class CoordView
{
    public string WorldType { get; set; } = "";
    public double LocalX { get; set; }
    public double LocalY { get; set; }
    public double LocalZ { get; set; }
    public int BiomeX { get; set; }
    public int BiomeY { get; set; }
    public int CellIndex { get; set; }
    public int DetailX { get; set; }
    public int DetailY { get; set; }
    public int DetailIndex { get; set; }

    public string Label =>
        $"{WorldType} · 区块({BiomeX},{BiomeY}) 格({DetailX},{DetailY}) · 世界坐标({LocalX:0},{LocalY:0})";
}

public sealed class StoreView : ModuleView
{
    public override string Title => "商店";

    public int LastRefreshDay { get; set; }
    public int LastCheckedDay { get; set; }
}

public sealed class RaiderView : ModuleView
{
    public override string Title => "袭击";
}

/// <summary>
/// One NPC from NpcSubsystem__NPC.sav. Every NPC stores six percentage
/// stats (0..1) plus a BackPack struct that mirrors the player's inventory
/// layout (InventoryItems / WeaponItems / AccessoryItems / EquipmentItems).
/// </summary>
public sealed class NpcView
{
    public string Id { get; init; } = string.Empty;
    public List<SaveProperty> Props { get; init; } = new();

    public SaveProperty? Prop(string name) => Props.FirstOrDefault(x => x.Name == name);

    public float Health => ValueEditor.EffectiveValueOrNull(Prop("CurrentPercentageHealth")) is float f ? f : 0f;
    public float Magic => ValueEditor.EffectiveValueOrNull(Prop("CurrentPercentageMagic")) is float f ? f : 0f;
    public float Hunger => ValueEditor.EffectiveValueOrNull(Prop("CurrentPercentageHunger")) is float f ? f : 0f;
    public float Sleep => ValueEditor.EffectiveValueOrNull(Prop("CurrentPercentageSleep")) is float f ? f : 0f;
    public float Heat => ValueEditor.EffectiveValueOrNull(Prop("CurrentPercentageHeat")) is float f ? f : 0f;
    public float Toxin => ValueEditor.EffectiveValueOrNull(Prop("CurrentPercentageToxin")) is float f ? f : 0f;

    /// <summary>
    /// Guard-type NPCs store -1 in every stat, meaning "not simulated".
    /// Editing them is meaningless, so the UI hides them.
    /// </summary>
    public bool IsSimulated => Health >= 0f || Hunger >= 0f;

    public SaveProperty? BackPack => Prop("BackPack");

    /// <summary>
    /// Count of real (non-"None") items across the four backpack arrays.
    /// Each array is one wrapper element holding 5 properties per slot, so the
    /// element count is the slot count, not the item count.
    /// </summary>
    public int ItemCount
    {
        get
        {
            int n = 0;
            foreach (var arrName in new[] { "InventoryItems", "WeaponItems", "AccessoryItems", "EquipmentItems" })
            {
                var els = BackPack?.Child(arrName)?.ArrayElements;
                if (els is not { Count: > 0 }) continue;

                var props = els[0].Properties;
                for (int i = 0; i + 4 < props.Count; i += 5)
                {
                    if (props[i].Name != "ItemID") continue;
                    if (ValueEditor.EffectiveValueOrNull(props[i]) is string id && id is not ("None" or ""))
                        n++;
                }
            }
            return n;
        }
    }
}

/// <summary>One entry of NPCMood__NPCMoodData.sav.</summary>
public sealed class NpcMoodView
{
    public string Id { get; init; } = string.Empty;
    public List<SaveProperty> Props { get; init; } = new();

    public SaveProperty? Prop(string name) => Props.FirstOrDefault(x => x.Name == name);

    public float Mood => ValueEditor.EffectiveValueOrNull(Prop("MoodValue")) is float f ? f : 0f;
    public SaveProperty? MoodProp => Prop("MoodValue");
}
