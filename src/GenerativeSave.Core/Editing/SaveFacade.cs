using System.Text;
using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>
/// Typed, human-friendly accessors over the decoded save. Every property name
/// and path here was verified against the actual GameSave_2 data.
/// </summary>
public static class SaveFacade
{
    // ------------------------------------------------------------ helpers
    public static SaveProperty? Child(this SaveProperty? p, string name) =>
        p?.Children?.FirstOrDefault(x => x.Name == name);

    public static SaveProperty? Deep(this SaveProperty? p, params string[] path)
    {
        var cur = p;
        foreach (var seg in path)
        {
            cur = cur.Child(seg);
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>First struct element of an array property (this game wraps lists in one element).</summary>
    public static SaveProperty? Elem(this SaveProperty? p, int index = 0) =>
        p?.ArrayElements is { Count: > 0 } els && index < els.Count ? els[index].Properties.FirstOrDefault() : null;

    public static IEnumerable<SaveProperty> ElemProps(this SaveProperty? p, int index = 0) =>
        p?.ArrayElements is { Count: > 0 } els && index < els.Count
            ? els[index].Properties
            : Enumerable.Empty<SaveProperty>();

    /// <summary>The single map value of a one-entry map property (e.g. PlayersData).</summary>
    public static List<SaveProperty> MapValueProps(this SaveProperty? p, int index = 0) =>
        p?.MapEntries is { Count: > 0 } es && index < es.Count
            ? es[index].ValueProps
            : new List<SaveProperty>();

    public static SaveProperty? InMap(this SaveProperty? p, string name, int index = 0) =>
        p.MapValueProps(index).FirstOrDefault(x => x.Name == name);

    // ------------------------------------------------------------ player
    public static PlayerView? ReadPlayer(SaveSlot slot)
    {
        var m = slot.Find("Player__Players");
        var pd = m?.Properties.FirstOrDefault();
        if (m is null || pd is null || pd.MapEntries is not { Count: > 0 }) return null;

        var props = pd.MapValueProps();
        var v = new PlayerView { Module = m, Root = pd, MapProps = props };

        v.SteamId = props.FirstOrDefault(x => x.Name == "SteamID")?.Value is ulong u ? u : 0;
        v.PlayerName = S(props, "PlayerName");
        v.CurrentHealth = F(props, "CurrentHealth");
        v.MaxHealth = F(props, "MaxHealth");
        v.CurrentMagic = F(props, "CurrentMagic");
        v.CurrentHunger = F(props, "CurrentHunger");
        v.CurrentHeat = F(props, "CurrentHeat");
        v.MaxHeat = F(props, "MaxHeat");
        v.CurrentToxin = F(props, "CurrentToxin");
        v.MaxToxin = F(props, "MaxToxin");
        v.ReputationLevel = I(props, "ReputationLevel");
        v.ReputationExperience = L(props, "ReputationExperience");
        v.CurrentHotbarIndex = I(props, "CurrentHotbarIndex");
        v.TrackedQuest = S(props, "TrackedQuest");
        v.SkinId = S(props, "SkinID");
        v.HasCalamityRiftReturn = props.FirstOrDefault(x => x.Name == "bHasCalamityRiftReturnPoint")?.Value is true;
        v.Coord = props.FirstOrDefault(x => x.Name == "EnterGameStartCoord")?.Children;
        return v;
    }

    // ------------------------------------------------------------ manifest
    public static ManifestView? ReadManifest(SaveSlot slot)
    {
        var m = slot.Manifest;
        if (m is null) return null;
        return new ManifestView
        {
            Module = m,
            DisplayName = S(m.Properties, "SaveDisplayName"),
            PlayerName = S(m.Properties, "PlayerName"),
            MapSeed = I(m.Properties, "MapSeed"),
            PlayTime = D(m.Properties, "PlayTime"),
            ActiveVersion = L(m.Properties, "ActiveVersion"),
            LastKnownGoodVersion = L(m.Properties, "LastKnownGoodVersion"),
            KeepVersions = I(m.Properties, "KeepVersions"),
        };
    }

    // ------------------------------------------------------------ world time
    public static WorldTimeView? ReadWorldTime(SaveSlot slot)
    {
        var m = slot.Find("WorldTime__WorldTime");
        var p = m?.Properties.FirstOrDefault();
        if (m is null || p is null) return null;
        return new WorldTimeView
        {
            Module = m,
            Root = p,
            Hours = F(m.Properties, "CurrentWorldTime"),
        };
    }

    // ------------------------------------------------------------ campsite
    /// <summary>
    /// Every built-in NPC the game ships with, mapped from the internal id the
    /// save stores to the Chinese name shown in game.
    ///
    /// Source: the official wiki (https://magiscapes.wiki/#/分类/npcs) lists all
    /// 55 residents. The save only ever contains the English id — the Chinese
    /// name is localisation text and is not written into any module — so this
    /// table is the only way to show a readable name for a built-in resident.
    /// The six 村民 entries share one display name because the game does too.
    /// </summary>
    public static readonly (string Id, string Cn)[] BuiltInNpcs =
    {
        ("Aurelia", "金缕"),
        ("Alex", "小狼"),
        ("Seventh", "『七号』"),
        ("SnowWillow", "雪柳"),
        ("Assistant", "『冒险的伙伴』"),
        ("Yidou", "一斗"),
        ("Ivan", "伊凡"),
        ("Ignacia", "伊格娜希娅"),
        ("Guard", "侍卫"),
        ("Princess", "公主"),
        ("Qasim", "卡西姆"),
        ("Luxordo", "卢克索多"),
        ("Harzan", "哈尔赞"),
        ("Harrison", "哈里森"),
        ("Elvie", "埃尔薇"),
        ("Taka", "塔卡"),
        ("Ceris", "塞瑞丝"),
        ("Celestine", "塞莱丝汀"),
        ("Anna", "安娜"),
        ("Xiaoman", "小满"),
        ("Nyxel", "尼赛尔"),
        ("Balden", "巴尔登"),
        ("Brownbill", "布朗比尔"),
        ("Brog", "布罗格"),
        ("Bruno", "布鲁诺"),
        ("Tobill", "托比尔"),
        ("Thomas", "托马斯"),
        ("Tsukiaya", "月绫"),
        ("Gerson", "格尔森"),
        ("Merlin", "梅林"),
        ("Luoen", "洛恩"),
        ("Liuyun", "流云"),
        ("Veyra", "瓦伊"),
        ("Cole", "科尔"),
        ("Nadira", "纳迪娅"),
        ("Victor", "维克多"),
        ("Elsa", "艾尔莎"),
        ("Adrian", "艾德里安"),
        ("Ela", "艾拉"),
        ("Erina", "艾瑞娜"),
        ("Amelia", "艾米莉亚"),
        ("Lilith", "莉莉丝"),
        ("Safiya", "莎菲雅"),
        ("Layla", "莱拉"),
        ("Peiyan", "裴厌"),
        ("Herbert", "赫伯特"),
        ("Azrum", "阿兹鲁姆"),
        ("Astra", "阿斯特拉"),
        ("MaleVillager_A", "村民"),
        ("MaleVillager_B", "村民"),
        ("MaleVillager_C", "村民"),
        ("FemaleVillager_A", "村民"),
        ("FemaleVillager_B", "村民"),
        ("FemaleVillager_C", "村民"),
    };

    public static CampsiteView? ReadCampsite(SaveSlot slot)
    {
        var m = slot.Find("CampsiteSave__CampsiteSave");
        if (m is null) return null;
        var v = new CampsiteView
        {
            Module = m,
            HasCampsite = m.Properties.FirstOrDefault(x => x.Name == "HasCampsite")?.Value is true,
            AiNpcIds = Names(m.Properties.FirstOrDefault(x => x.Name == "CampsiteAINPCIds")),
            RecruitedNpcIds = Names(m.Properties.FirstOrDefault(x => x.Name == "CampsiteRecruitedNPCIds")),
            KickedNpcIds = Names(m.Properties.FirstOrDefault(x => x.Name == "CampsiteKickedNPCIds")),
        };

        // The per-camp record lives inside AllCampsite; the top-level arrays above
        // are the authoritative roster, so they stay as they are.
        var camp = m.Properties.FirstOrDefault(x => x.Name == "AllCampsite")?.ArrayElements?.FirstOrDefault();
        if (camp?.Properties is not null)
        {
            // EffectiveValue, not Value: MarkDirty only stages the new bytes, so reading
            // .Value here showed the OLD name after a rename and the edit looked ignored.
            v.CampsiteName = ValueEditor.EffectiveValueOrNull(
                camp.Properties.FirstOrDefault(x => x.Name == "CampsiteName")) as string ?? "";
            v.Level = I(camp.Properties, "Level");
            v.LastUpgradeWorldDay = I(camp.Properties, "LastUpgradeWorldDay");
            v.Coord = ReadCoord(camp.Properties.FirstOrDefault(x => x.Name == "CampsiteCoord"));
        }

        // The camp module has no "current day" of its own; the world clock does.
        var wt = ReadWorldTime(slot);
        v.WorldDay = wt?.Day ?? 0;

        // CampsiteCells holds one element whose property list repeats a whole
        // coordinate block per cell, so read it in groups of nine.
        var cells = m.Properties.FirstOrDefault(x => x.Name == "CampsiteCells");
        if (cells?.ArrayElements is not null)
        {
            foreach (var el in cells.ArrayElements)
            {
                foreach (var g in SplitCoordGroups(el.Properties))
                {
                    var c = ReadCoordFromList(g);
                    if (c is not null) v.Cells.Add(c);
                }
            }
        }

        return v;
    }

    /// <summary>
    /// One property list holds a whole coordinate block per cell, back to back.
    /// A block starts at WorldType, so split on that name.
    /// </summary>
    private static List<List<SaveProperty>> SplitCoordGroups(List<SaveProperty> props)
    {
        var groups = new List<List<SaveProperty>>();
        List<SaveProperty>? cur = null;
        foreach (var p in props)
        {
            if (p.Name == "WorldType")
            {
                if (cur is { Count: > 0 }) groups.Add(cur);
                cur = new List<SaveProperty>();
            }
            cur?.Add(p);
        }
        if (cur is { Count: > 0 }) groups.Add(cur);
        return groups;
    }

    /// <summary>Read one coordinate struct (WorldType / LocalPosition / tile indices).</summary>
    private static CoordView? ReadCoord(SaveProperty? p)
    {
        if (p?.Children is null) return null;
        return ReadCoordFromList(p.Children);
    }

    private static CoordView? ReadCoordFromList(List<SaveProperty> props)
    {
        if (props is null || props.Count == 0) return null;

        var c = new CoordView
        {
            WorldType = ValueEditor.EffectiveValueOrNull(props.FirstOrDefault(x => x.Name == "WorldType"))?.ToString() ?? "",
            BiomeX = I(props, "BiomeX"),
            BiomeY = I(props, "BiomeY"),
            CellIndex = I(props, "CellIndex"),
            DetailX = I(props, "DetailX"),
            DetailY = I(props, "DetailY"),
            DetailIndex = I(props, "DetailIndex"),
        };

        var pos = props.FirstOrDefault(x => x.Name == "WorldPosition")
                  ?? props.FirstOrDefault(x => x.Name == "LocalPosition");
        // Read the EFFECTIVE value: after an edit the property holds the new
        // bytes in PendingValue and Value still has the old number. Reading
        // Value here made the page show stale coordinates right after a change,
        // which looked exactly like "the edit did not apply".
        var vec = pos is null ? null : ValueEditor.EffectiveValue(pos);
        if (vec is System.Collections.IList list && list.Count >= 3)
        {
            c.LocalX = ToDouble(list[0]);
            c.LocalY = ToDouble(list[1]);
            c.LocalZ = ToDouble(list[2]);
        }
        return c;
    }

    private static double ToDouble(object? o) => o switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        _ => 0,
    };

    // ------------------------------------------------------------ store
    public static StoreView? ReadStore(SaveSlot slot)
    {
        var m = slot.Find("Store__StoreData");
        if (m is null) return null;
        return new StoreView
        {
            Module = m,
            LastRefreshDay = I(m.Properties, "LastRefreshDay"),
            LastCheckedDay = I(m.Properties, "LastCheckedDay"),
        };
    }

    // ------------------------------------------------------------ raider
    public static RaiderView? ReadRaider(SaveSlot slot)
    {
        var m = slot.Find("Raider__Raider");
        if (m is null) return null;
        return new RaiderView { Module = m };
    }

    // ------------------------------------------------------------ inventory add
    /// <summary>
    /// Copy a whole inventory group (ItemID / Count / EquipmentHandle /
    /// ItemConfig / ItemSaveData) into an empty slot.
    ///
    /// Why clone instead of writing a bare id? An empty slot still carries a
    /// full 27-field ItemConfig template with ItemName/Icon/ItemClass blank.
    /// Filling in only the id would leave the game with an item it cannot
    /// resolve, so we take the bytes of a real item that already exists in this
    /// save and place them in the free slot.
    /// </summary>
    public static bool CloneInventoryGroup(List<SaveProperty> src, List<SaveProperty> dst,
                                           int count, out string error)
    {
        error = string.Empty;

        if (src.Count != 5 || dst.Count != 5)
        {
            error = $"分组属性数异常（{src.Count}/{dst.Count}）";
            return false;
        }

        // ItemConfig is the expensive one; cloning it is what makes the item real
        var srcCfg = src.FirstOrDefault(p => p.Name == "ItemConfig");
        if (srcCfg is null || srcCfg.DeclaredSize <= 0) { error = "源物品缺少 ItemConfig"; return false; }

        // 1) copy ItemConfig and ItemSaveData verbatim
        foreach (var name in new[] { "ItemConfig", "ItemSaveData" })
        {
            var s = src.FirstOrDefault(p => p.Name == name);
            var d = dst.FirstOrDefault(p => p.Name == name);
            if (s is null || d is null) { error = $"缺少 {name}"; return false; }
            d.MarkDirty(s.OriginalValue);
        }

        // 2) ItemID follows the config
        var sId = src.FirstOrDefault(p => p.Name == "ItemID");
        var dId = dst.FirstOrDefault(p => p.Name == "ItemID");
        if (sId is null || dId is null) { error = "缺少 ItemID"; return false; }
        var idBytes = ValueEditor.EncodeFString(sId.Value as string ?? "None", out error);
        if (idBytes is null) return false;
        dId.MarkDirty(idBytes);

        // 3) count is a plain int32
        var dCount = dst.FirstOrDefault(p => p.Name == "Count");
        if (dCount is null) { error = "缺少 Count"; return false; }
        dCount.MarkDirty(BitConverter.GetBytes(count));

        // 4) equipment handle: cloned items are never equipped
        var dHandle = dst.FirstOrDefault(p => p.Name == "EquipmentHandle");
        dHandle?.MarkDirty(ValueEditor.EncodeFString("None", out _)!);

        return true;
    }

    /// <summary>Locate the ArrayElement wrapper that holds all 40 inventory groups.</summary>
    public static Model.SaveArrayElement? InventoryWrapper(SaveSlot slot) =>
        ReadPlayer(slot)?.MapProps.FirstOrDefault(p => p.Name == "InventoryItems")
            ?.ArrayElements?.FirstOrDefault();

    /// <summary>
    /// The 10 quick-bar slots. Same 5-property group shape as the backpack, but
    /// stored in its own HotbarItems array; the game keeps a separate
    /// CurrentHotbarIndex pointer into it.
    /// </summary>
    public static Model.SaveArrayElement? HotbarWrapper(SaveSlot slot) =>
        ReadPlayer(slot)?.MapProps.FirstOrDefault(p => p.Name == "HotbarItems")
            ?.ArrayElements?.FirstOrDefault();

    public static SaveProperty? CurrentHotbarIndexProp(SaveSlot slot) =>
        ReadPlayer(slot)?.MapProps.FirstOrDefault(p => p.Name == "CurrentHotbarIndex");

    public static SaveProperty? ReputationLevelProp(SaveSlot slot) =>
        ReadPlayer(slot)?.MapProps.FirstOrDefault(p => p.Name == "ReputationLevel");

    public static SaveProperty? ReputationExperienceProp(SaveSlot slot) =>
        ReadPlayer(slot)?.MapProps.FirstOrDefault(p => p.Name == "ReputationExperience");


    // ------------------------------------------------------------ npc
    /// <summary>
    /// All NPCs from NpcSubsystem__NPC.sav. The module's NpcMap is keyed by
    /// the NPC id string, and each value is a 16-property list.
    /// </summary>
    public static List<NpcView> ReadNpcs(SaveSlot slot)
    {
        var m = slot.Find("NpcSubsystem__NPC");
        var map = m?.Properties.FirstOrDefault(x => x.Name == "NpcMap");
        if (map?.MapEntries is null) return new List<NpcView>();

        return map.MapEntries.Select(e => new NpcView
        {
            Id = e.Key?.ToString() ?? e.KeyDisplay,
            Props = e.ValueProps,
        }).ToList();
    }

    /// <summary>
    /// AI-generated characters with their real attributes. This lives in its own
    /// module (GenCharacter__AIGenCharacter) and only contains characters the
    /// player recruited from the AI generator; the built-in NPCs are defined in
    /// game data, not saved, so they have no BaseAttribute entry here.
    /// </summary>
    public static List<CharacterAttributeView> ReadCharacters(SaveSlot slot)
    {
        var m = slot.Find("GenCharacter__AIGenCharacter");
        var arr = m?.Properties.FirstOrDefault(x => x.Name == "GenCharacterInfos");
        if (arr?.ArrayElements is null) return new List<CharacterAttributeView>();

        var list = new List<CharacterAttributeView>();
        foreach (var el in arr.ArrayElements)
        {
            var id = ValueEditor.EffectiveValueOrNull(el.Properties.FirstOrDefault(p => p.Name == "CharacterID")) as string;
            var name = ValueEditor.EffectiveValueOrNull(el.Properties.FirstOrDefault(p => p.Name == "CharacterName")) as string;

            list.Add(new CharacterAttributeView
            {
                CharacterId = id ?? string.Empty,
                CharacterName = name ?? string.Empty,
                Props = el.Properties,
            });
        }
        return list;
    }

    /// <summary>NPC mood values, keyed by the same ids.</summary>
    public static List<NpcMoodView> ReadNpcMoods(SaveSlot slot)
    {
        var m = slot.Find("NPCMood__NPCMoodData");
        var map = m?.Properties.FirstOrDefault(x => x.Name == "SavedNPCMoodStorage");
        if (map?.MapEntries is null) return new List<NpcMoodView>();

        return map.MapEntries.Select(e => new NpcMoodView
        {
            Id = e.Key?.ToString() ?? e.KeyDisplay,
            Props = e.ValueProps,
        }).ToList();
    }

    // ------------------------------------------------------------ npc lists
    /// <summary>
    /// Rewrite a string-array property (count + FStrings). Used to recruit or
    /// kick NPCs. The array's own size field and every enclosing size are
    /// repaired by the patcher, which now walks the whole parent chain.
    /// </summary>
    public static bool SetStringArray(SaveProperty arrayProp, IEnumerable<string> values, out string error)
    {
        error = string.Empty;

        if (arrayProp.TypeName != "ArrayProperty" || arrayProp.InnerType != "StrProperty")
        {
            error = $"不是字符串数组（{arrayProp.TypeName}/{arrayProp.InnerType}）";
            return false;
        }

        var list = values.ToList();

        var buf = new List<byte>();
        buf.AddRange(BitConverter.GetBytes(list.Count));
        foreach (var s in list)
        {
            var enc = ValueEditor.EncodeFString(s, out error);
            if (enc is null) return false;
            buf.AddRange(enc);
        }

        arrayProp.MarkDirty(buf.ToArray());
        return true;
    }

    public static SaveProperty? RecruitedNpcProp(SaveSlot slot) =>
        slot.Find("CampsiteSave__CampsiteSave")
            ?.Properties.FirstOrDefault(x => x.Name == "CampsiteRecruitedNPCIds");

    public static SaveProperty? KickedNpcProp(SaveSlot slot) =>
        slot.Find("CampsiteSave__CampsiteSave")
            ?.Properties.FirstOrDefault(x => x.Name == "CampsiteKickedNPCIds");

    /// <summary>
    /// The camp's own resident list. Distinct from the recruited list: these are
    /// the NPCs physically living at the camp (AI-generated ones mostly), and the
    /// game lets them be expelled too.
    /// </summary>
    public static SaveProperty? CampNpcProp(SaveSlot slot) =>
        slot.Find("CampsiteSave__CampsiteSave")
            ?.Properties.FirstOrDefault(x => x.Name == "CampsiteAINPCIds");

    /// <summary>
    /// Move an NPC into the camp's resident list. This is what 按 ID 招募 has to
    /// do for a code the player rolled themselves — the game shows those in the
    /// 营地 NPC row, not the recruited row.
    /// </summary>
    public static bool AddCampNpc(SaveSlot slot, string id, out string error)
    {
        error = string.Empty;
        var prop = CampNpcProp(slot);
        if (prop is null) { error = "未找到营地 NPC 列表"; return false; }

        var list = Names(prop).ToList();
        if (list.Contains(id, StringComparer.Ordinal)) { error = "已经在营地 NPC 列表里"; return false; }
        list.Add(id);
        if (!SetStringArray(prop, list, out error)) return false;

        // A resident must not also sit on the expelled list.
        RemoveFromKicked(slot, id, out _);
        return true;
    }

    /// <summary>
    /// Expel an NPC from the camp's resident list. Same shape as <see cref="KickNpc"/>
    /// but on CampsiteAINPCIds.
    /// </summary>
    public static bool ExpelCampNpc(SaveSlot slot, string id, out string error)
    {
        error = string.Empty;
        var prop = CampNpcProp(slot);
        if (prop is null) { error = "未找到营地 NPC 列表"; return false; }

        var list = Names(prop).ToList();
        if (list.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal)) == 0)
        {
            error = "该 NPC 不在营地 NPC 列表里";
            return false;
        }
        if (!SetStringArray(prop, list, out error)) return false;

        AddToKicked(slot, id, out _);
        return true;
    }

    private static void RemoveFromKicked(SaveSlot slot, string id, out string error)
    {
        error = string.Empty;
        if (KickedNpcProp(slot) is not { } prop) return;
        var list = Names(prop).ToList();
        if (list.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal)) > 0)
            SetStringArray(prop, list, out error);
    }

    private static void AddToKicked(SaveSlot slot, string id, out string error)
    {
        error = string.Empty;
        if (KickedNpcProp(slot) is not { } prop) return;
        var list = Names(prop).ToList();
        if (list.Contains(id, StringComparer.Ordinal)) return;
        list.Add(id);
        SetStringArray(prop, list, out error);
    }

    /// <summary>Recruit an NPC, clearing any matching "kicked" entry.</summary>
    public static bool RecruitNpc(SaveSlot slot, string id, out string error)
    {
        error = string.Empty;

        var recruitedProp = RecruitedNpcProp(slot);
        var kickedProp = KickedNpcProp(slot);
        if (recruitedProp is null) { error = "未找到招募列表"; return false; }

        var recruited = Names(recruitedProp).ToList();
        if (recruited.Contains(id, StringComparer.Ordinal)) { error = "已经在营地中"; return false; }
        recruited.Add(id);
        if (!SetStringArray(recruitedProp, recruited, out error)) return false;

        // a recruited NPC must not also be marked as kicked
        RemoveFromKicked(slot, id, out _);
        return true;
    }

    public static bool KickNpc(SaveSlot slot, string id, out string error)
    {
        error = string.Empty;
        var prop = RecruitedNpcProp(slot);
        if (prop is null) { error = "未找到招募列表"; return false; }

        var list = Names(prop).ToList();
        if (list.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal)) == 0)
        {
            error = "该 NPC 不在营地中";
            return false;
        }
        if (!SetStringArray(prop, list, out error)) return false;

        // A kicked NPC belongs on the kicked list, otherwise the game still
        // counts them as a former resident and the roster shows nothing.
        AddToKicked(slot, id, out _);
        return true;
    }

    /// <summary>
    /// Recruit by id into whichever roster the id belongs to. Player-rolled
    /// codes (6 uppercase chars) live in the camp's resident list; named
    /// residents go on the recruited list. Doing the right one matters: putting
    /// a rolled code on the recruited list leaves the 营地 NPC row unchanged,
    /// which is what made 按 ID 招募 look broken.
    /// </summary>
    public static bool RecruitAnywhere(SaveSlot slot, string id, out string error, out bool intoCamp)
    {
        intoCamp = !IsNamedNpcId(id);
        return intoCamp ? AddCampNpc(slot, id, out error) : RecruitNpc(slot, id, out error);
    }

    /// <summary>
    /// True for ids that belong in the camp resident list: player-rolled codes
    /// are six uppercase letters/digits with no lowercase (e.g. YEDK65).
    /// </summary>
    public static bool IsNamedNpcId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        foreach (var c in id) if (char.IsLower(c)) return true;
        return false;
    }

    /// <summary>
    /// Take an NPC off the kicked list entirely. The roster in game only keeps a
    /// kicked entry so it can be shown as "expelled"; deleting it removes the
    /// record, which is what the user means by 删除.
    /// </summary>
    public static bool ForgetKickedNpc(SaveSlot slot, string id, out string error)
    {
        error = string.Empty;
        var prop = KickedNpcProp(slot);
        if (prop is null) { error = "未找到已驱逐列表"; return false; }

        var list = Names(prop).ToList();
        if (list.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal)) == 0)
        {
            error = "该 NPC 不在已驱逐列表中";
            return false;
        }
        return SetStringArray(prop, list, out error);
    }

    // ------------------------------------------------------------ renaming
    /// <summary>
    /// The three user-visible names, each located in a specific module.
    /// Returns null when the module or field is absent.
    /// </summary>
    public static SaveProperty? SaveDisplayNameProp(SaveSlot slot) =>
        slot.Manifest?.FindProperty("SaveDisplayName");

    public static SaveProperty? ManifestPlayerNameProp(SaveSlot slot) =>
        slot.Manifest?.FindProperty("PlayerName");

    public static SaveProperty? PlayerNameProp(SaveSlot slot) =>
        slot.Find("Player__Players")?.Properties.FirstOrDefault()
            ?.MapValueProps().FirstOrDefault(x => x.Name == "PlayerName");

    public static SaveProperty? CampsiteNameProp(SaveSlot slot)
    {
        var camp = slot.Find("CampsiteSave__CampsiteSave");
        var all = camp?.Properties.FirstOrDefault(x => x.Name == "AllCampsite");
        return all?.ElemProps().FirstOrDefault(x => x.Name == "CampsiteName");
    }

    /// <summary>
    /// Rename the player. The name lives in both Manifest and the player
    /// module; the game reads the player module, so both are kept in sync.
    /// </summary>
    public static bool SetPlayerName(SaveSlot slot, string name, out string error)
    {
        error = string.Empty;
        var targets = new[] { PlayerNameProp(slot), ManifestPlayerNameProp(slot) }
            .Where(p => p is not null).Cast<SaveProperty>().ToList();

        if (targets.Count == 0) { error = "未找到角色名字段"; return false; }

        foreach (var p in targets)
        {
            var bytes = ValueEditor.EncodeFString(name, out error);
            if (bytes is null) return false;
            p.MarkDirty(bytes);
        }
        return true;
    }

    public static bool SetSaveName(SaveSlot slot, string name, out string error)
    {
        error = string.Empty;
        var p = SaveDisplayNameProp(slot);
        if (p is null) { error = "未找到存档名字段"; return false; }
        var bytes = ValueEditor.EncodeFString(name, out error);
        if (bytes is null) return false;
        p.MarkDirty(bytes);
        return true;
    }

    public static bool SetCampsiteName(SaveSlot slot, string name, out string error)
    {
        error = string.Empty;
        var p = CampsiteNameProp(slot);
        if (p is null) { error = "未找到营地名字段"; return false; }
        var bytes = ValueEditor.EncodeFString(name, out error);
        if (bytes is null) return false;
        p.MarkDirty(bytes);
        return true;
    }

    // ------------------------------------------------------------ campsite level / position
    /// <summary>The Level IntProperty of AllCampsite[0], for direct UI binding.</summary>
    public static SaveProperty? CampsiteLevelProp(SaveSlot slot) => CampProperty(slot, "Level");

    /// <summary>The LastUpgradeWorldDay IntProperty of AllCampsite[0].</summary>
    public static SaveProperty? CampsiteUpgradeDayProp(SaveSlot slot) =>
        CampProperty(slot, "LastUpgradeWorldDay");

    private static SaveProperty? CampProperty(SaveSlot slot, string name)
    {
        var m = slot.Find("CampsiteSave__CampsiteSave");
        var camp = m?.Properties.FirstOrDefault(x => x.Name == "AllCampsite")?.ArrayElements?.FirstOrDefault();
        return camp?.Properties?.FirstOrDefault(x => x.Name == name);
    }

    /// <summary>Set AllCampsite[].Level. The game reads this directly.</summary>
    public static bool SetCampsiteLevel(SaveSlot slot, int level, out string error)
    {
        error = string.Empty;
        var p = CampProperty(slot, "Level");
        if (p is null) { error = "未找到营地等级字段"; return false; }
        if (level < 0 || level > 9999) { error = "营地等级应在 0–9999 之间"; return false; }

        var bytes = ValueEditor.Encode(p, level.ToString(), out error);
        if (bytes is null) return false;
        p.MarkDirty(bytes);
        return true;
    }

    /// <summary>
    /// Set AllCampsite[].LastUpgradeWorldDay. The game's upgrade timer counts from
    /// this day, so lowering it lets the camp be upgraded again immediately.
    /// </summary>
    public static bool SetCampsiteLastUpgradeDay(SaveSlot slot, int day, out string error)
    {
        error = string.Empty;
        var p = CampProperty(slot, "LastUpgradeWorldDay");
        if (p is null) { error = "未找到营地升级日期字段"; return false; }
        if (day < 0) { error = "天数不能为负"; return false; }

        var bytes = ValueEditor.Encode(p, day.ToString(), out error);
        if (bytes is null) return false;
        p.MarkDirty(bytes);
        return true;
    }

    /// <summary>
    /// Move the camp. The save stores the same position twice — as a float triple
    /// and as tile indices — and the game uses both, so every field has to be
    /// rewritten together or the camp ends up in two places at once.
    ///
    /// DetailX/DetailY are the authoritative cell; WorldPosition is derived from
    /// them with the same scale the game uses (one detail cell = 1024 units).
    /// </summary>
    /// <summary>
    /// True when the camp anchor no longer sits at the centre of its footprint,
    /// or its world vector disagrees with the anchor. An earlier build wrote the
    /// anchor with a wrong scale and left saves in this state; the game then
    /// rebuilds the camp from the footprint, so the camp appears not to move.
    /// </summary>
    public static bool CampsiteNeedsRepair(SaveSlot slot, out string reason)
    {
        reason = string.Empty;
        var v = ReadCampsite(slot);
        if (v?.Coord is null || v.Cells is null || v.Cells.Count == 0) return false;

        var c = v.Coord;
        int cx = (v.Cells.Min(x => x.DetailX) + v.Cells.Max(x => x.DetailX)) / 2;
        int cy = (v.Cells.Min(x => x.DetailY) + v.Cells.Max(x => x.DetailY)) / 2;
        if (c.DetailX != cx || c.DetailY != cy)
        {
            reason = $"锚点格({c.DetailX},{c.DetailY})不在足迹中心({cx},{cy})";
            return true;
        }

        double ex = (c.BiomeX * 150 + c.DetailX) * 128.0 + 64;
        double ey = (c.BiomeY * 150 + c.DetailY + 1) * 128.0;
        if (Math.Abs(c.LocalX - ex) > 0.5 || Math.Abs(c.LocalY - ey) > 0.5)
        {
            reason = $"世界坐标({c.LocalX:0},{c.LocalY:0})与锚点推算值({ex:0},{ey:0})不符";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Put the anchor back where the footprint says it should be, without moving
    /// the footprint itself. This repairs saves written by the old build.
    /// </summary>
    public static bool RepairCampsite(SaveSlot slot, out string error)
    {
        error = string.Empty;
        var v = ReadCampsite(slot);
        if (v?.Coord is null || v.Cells is null || v.Cells.Count == 0)
        {
            error = "没有可修复的营地数据";
            return false;
        }

        int cx = (v.Cells.Min(x => x.DetailX) + v.Cells.Max(x => x.DetailX)) / 2;
        int cy = (v.Cells.Min(x => x.DetailY) + v.Cells.Max(x => x.DetailY)) / 2;
        if (v.Coord.DetailX == cx && v.Coord.DetailY == cy)
        {
            // Anchor already centred; only the vector is stale. Rewrite it in
            // place so the footprint is untouched.
            var coord = CampProperty(slot, "CampsiteCoord");
            if (coord?.Children is null) { error = "未找到营地坐标字段"; return false; }
            double ex = (v.Coord.BiomeX * 150 + cx) * 128.0 + 64;
            double ey = (v.Coord.BiomeY * 150 + cy + 1) * 128.0;
            foreach (var name in new[] { "LocalPosition", "WorldPosition" })
            {
                var p = coord.Children.FirstOrDefault(x => x.Name == name);
                if (p?.Value is not System.Collections.IList l || l.Count < 3) continue;
                var b = ValueEditor.EncodeVector3(p, ex, ey, ToDouble(l[2]), out var e2);
                if (b is null) { error = e2; return false; }
                p.MarkDirty(b);
            }
            return true;
        }

        // Move the anchor to the footprint centre with a zero delta on the cells,
        // so only the anchor and its vector change.
        return SetCampsitePositionNoShift(slot, cx, cy, out error);
    }

    /// <summary>Set the anchor without touching the footprint.</summary>
    private static bool SetCampsitePositionNoShift(SaveSlot slot, int detailX, int detailY, out string error)
    {
        error = string.Empty;
        var coord = CampProperty(slot, "CampsiteCoord");
        if (coord?.Children is null) { error = "未找到营地坐标字段"; return false; }

        SaveProperty? F(string n) => coord.Children!.FirstOrDefault(c => c.Name == n);
        int biomeX = F("BiomeX")?.Value is int bx ? bx : 0;
        int biomeY = F("BiomeY")?.Value is int by ? by : 0;
        double wx = (biomeX * 150 + detailX) * 128.0 + 64;
        double wy = (biomeY * 150 + detailY + 1) * 128.0;

        bool Put(string name, int value)
        {
            var p = F(name);
            if (p is null) return false;
            var b = ValueEditor.Encode(p, value.ToString(), out _);
            if (b is null) return false;
            p.MarkDirty(b);
            return true;
        }

        Put("DetailX", detailX);
        Put("DetailY", detailY);
        Put("DetailIndex", detailY * 150 + detailX);

        foreach (var name in new[] { "LocalPosition", "WorldPosition" })
        {
            var p = F(name);
            if (p?.Value is not System.Collections.IList l || l.Count < 3) continue;
            var b = ValueEditor.EncodeVector3(p, wx, wy, ToDouble(l[2]), out var e2);
            if (b is null) { error = e2; return false; }
            p.MarkDirty(b);
        }
        return true;
    }

    public static bool SetCampsitePosition(SaveSlot slot, int detailX, int detailY, out string error)
    {
        error = string.Empty;
        var coord = CampProperty(slot, "CampsiteCoord");
        if (coord?.Children is null) { error = "未找到营地坐标字段"; return false; }

        // The camp is a 79x79 footprint centred on the anchor (verified: the
        // cells span anchor-39 .. anchor+39). A chunk is 150 cells, so any anchor
        // outside 39..110 pushes part of the footprint past the chunk edge. The
        // game then ignores the move, which is exactly the "移不动" symptom, so
        // reject it here instead of writing a position the game will discard.
        const int halfFootprint = 39;
        const int lastCell = 149;
        if (detailX < halfFootprint || detailX > lastCell - halfFootprint ||
            detailY < halfFootprint || detailY > lastCell - halfFootprint)
        {
            error = $"格子坐标应在 {halfFootprint}–{lastCell - halfFootprint} 之间" +
                    "（营地是 79×79 的地皮，太靠边会越出区块）";
            return false;
        }

        // World position, derived from the one sample the game itself wrote:
        //   Biome(7,4) Detail(100,74) -> World(147264, 86400)
        // and that is exactly
        //   X: 128 * (7*150 + 100) +  64 = 147264   (half a cell: cell centre)
        //   Y: 128 * (4*150 +  74 + 1)    =  86400   (a full cell)
        // so a detail cell is 128 world units, 150 cells make a chunk, and the
        // two axes anchor differently. The old code used Detail * 1024, which is
        // an order of magnitude out: it made the anchor and this vector disagree
        // and the game ignored the move.
        const double cellSize = 128.0;
        const int chunkCells = 150;
        int biomeX = F("BiomeX")?.Value is int bx ? bx : 0;
        int biomeY = F("BiomeY")?.Value is int by ? by : 0;
        double wx = (biomeX * chunkCells + detailX) * cellSize + cellSize / 2;
        double wy = (biomeY * chunkCells + detailY + 1) * cellSize;

        SaveProperty? F(string n) => coord.Children!.FirstOrDefault(c => c.Name == n);

        // The delta the camp has to move by. Read before overwriting the anchor.
        int startX = F("DetailX")?.Value is int sx ? sx : detailX;
        int startY = F("DetailY")?.Value is int sy ? sy : detailY;

        // `error` is an out parameter, so local functions cannot capture it.
        // Collect the first failure into a local and assign it at the end.
        string? firstError = null;

        bool SetInt(string name, int value)
        {
            var p = F(name);
            if (p is null) { firstError ??= $"未找到 {name}"; return false; }
            var b = ValueEditor.Encode(p, value.ToString(), out var err);
            if (b is null) { firstError ??= err; return false; }
            p.MarkDirty(b);
            return true;
        }

        bool SetFloat3(string name, double x, double y)
        {
            var p = F(name);
            if (p is null || p.Value is not System.Collections.IList list || list.Count < 3)
            {
                firstError ??= $"未找到 {name} 或它不是三元向量";
                return false;
            }
            var b = ValueEditor.EncodeVector3(p, x, y, ToDouble(list[2]), out var err);
            if (b is null) { firstError ??= err; return false; }
            p.MarkDirty(b);
            return true;
        }

        if (!SetInt("DetailX", detailX)) { error = firstError ?? "写 DetailX 失败"; return false; }
        if (!SetInt("DetailY", detailY)) { error = firstError ?? "写 DetailY 失败"; return false; }

        // DetailIndex is derived: the map packs Y * size + X, with size 150.
        SetInt("DetailIndex", detailY * 150 + detailX);

        SetFloat3("LocalPosition", wx, wy);
        SetFloat3("WorldPosition", wx, wy);

        // Moving only the anchor is not enough: the camp's footprint lives in
        // CampsiteCells (one flat property list holding a coordinate block per
        // cell). The game builds the camp from that footprint, so if the cells
        // stay behind the camp does not move. Shift every cell by the same
        // delta the anchor moved by.
        int dx = detailX - startX, dy = detailY - startY;
        if (dx != 0 || dy != 0)
        {
            int moved = ShiftCampsiteCells(slot, dx, dy, ref firstError);
            if (moved <= 0)
            {
                error = firstError ?? "没能移动营地占格，营地位置不会生效";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Translate every coordinate block in CampsiteCells by (dx, dy).
    ///
    /// Only DetailX / DetailY are touched. That is deliberate, and it is the fix
    /// for "移动营地后游戏不认":
    ///
    /// CampsiteCells holds 6241 blocks, and in the real save EVERY block has
    ///   CellIndex   = -1
    ///   DetailIndex = -1
    ///   LocalPosition / WorldPosition = (0,0,0)
    /// Those are placeholders: the game derives them at runtime from
    /// DetailX/DetailY plus BiomeX/BiomeY. The previous version wrote real values
    /// into all three, so after a move the footprint no longer looked like the
    /// save the game had produced, and it discarded the change. Filling them in
    /// is what broke the move, not what fixed it.
    ///
    /// Returns how many cells moved, or 0 when the list could not be read.
    /// </summary>
    private static int ShiftCampsiteCells(SaveSlot slot, int dx, int dy, ref string? firstError)
    {
        var m = slot.Find("CampsiteSave__CampsiteSave");
        var cells = m?.Properties.FirstOrDefault(x => x.Name == "CampsiteCells");
        if (cells?.ArrayElements is null) { firstError ??= "未找到 CampsiteCells"; return 0; }

        int moved = 0;

        foreach (var el in cells.ArrayElements)
        {
            var props = el.Properties;
            // Walk the flat list, shifting each block in place. A block starts at
            // WorldType and ends right before the next one.
            int i = 0;
            while (i < props.Count)
            {
                if (props[i].Name != "WorldType") { i++; continue; }

                int end = i + 1;
                while (end < props.Count && props[end].Name != "WorldType") end++;

                var block = props.Skip(i).Take(end - i).ToList();
                SaveProperty? F(string n) => block.FirstOrDefault(c => c.Name == n);

                var px = F("DetailX");
                var py = F("DetailY");
                if (px?.Value is int ox && py?.Value is int oy)
                {
                    int nx = Math.Clamp(ox + dx, 0, 4095);
                    int ny = Math.Clamp(oy + dy, 0, 4095);

                    bool ok = true;
                    void Put(SaveProperty? p, int value)
                    {
                        if (p is null) return;
                        var b = ValueEditor.Encode(p, value.ToString(), out _);
                        if (b is null) { ok = false; return; }
                        p.MarkDirty(b);
                    }

                    Put(px, nx);
                    Put(py, ny);
                    // CellIndex / DetailIndex / the position vectors stay at the
                    // placeholders the game itself wrote (-1 and zeros).
                    if (ok) moved++;
                }

                i = end;
            }
        }

        if (moved == 0) firstError ??= "CampsiteCells 里没有可移动的坐标块";
        return moved;
    }

    // ------------------------------------------------------------ primitive reads
    private static string? S(IEnumerable<SaveProperty> ps, string n) =>
        ValueEditor.EffectiveValueOrNull(ps.FirstOrDefault(p => p.Name == n)) as string;

    private static int I(IEnumerable<SaveProperty> ps, string n)
    {
        var p = ps.FirstOrDefault(x => x.Name == n);
        // EffectiveValue, not Value: an edited property keeps the old number in
        // Value and the new one in PendingValue until the payload is rebuilt.
        return ValueEditor.EffectiveValue(p) switch
        {
            int i => i, long l => (int)l, uint u => (int)u, short s => s, byte b => b, _ => 0
        };
    }

    private static long L(IEnumerable<SaveProperty> ps, string n) =>
        ps.FirstOrDefault(p => p.Name == n)?.Value switch
        {
            long l => l, int i => i, ulong u => (long)u, uint x => x, _ => 0
        };

    // F/D/N must read EffectiveValue, not Value. MarkDirty stages the new bytes
    // in PendingValue and leaves Value at the old number until the payload is
    // rebuilt, so a plain .Value read made every float field (player and NPC
    // health, percentages) show the pre-edit number right after an edit — the
    // "改完还是老数值" symptom.
    private static float F(IEnumerable<SaveProperty> ps, string n) =>
        ValueEditor.EffectiveValueOrNull(ps.FirstOrDefault(p => p.Name == n)) switch
        {
            float f => f, double d => (float)d, int i => i, long l => l, _ => 0f
        };

    private static double D(IEnumerable<SaveProperty> ps, string n) =>
        ValueEditor.EffectiveValueOrNull(ps.FirstOrDefault(p => p.Name == n)) switch
        {
            double d => d, float f => f, int i => i, long l => l, _ => 0d
        };

    /// <summary>
    /// Read a string array's current contents. When the property has a pending
    /// edit we decode that instead of the parsed value, otherwise a recruit
    /// followed by a kick would operate on stale data.
    /// </summary>
    private static List<string> Names(SaveProperty? p)
    {
        if (p is null) return new List<string>();

        if (p.IsDirty && p.PendingValue is { } pending)
            return DecodeStringArray(pending);

        return p.Value is List<object?> l
            ? l.Select(x => x?.ToString() ?? "").ToList()
            : new List<string>();
    }

    /// <summary>Decode <c>int32 count</c> followed by <c>count</c> FStrings.</summary>
    public static List<string> DecodeStringArray(byte[] b)
    {
        var result = new List<string>();
        if (b.Length < 4) return result;

        int count = BitConverter.ToInt32(b, 0);
        int pos = 4;
        for (int i = 0; i < count && pos + 4 <= b.Length; i++)
        {
            int len = BitConverter.ToInt32(b, pos);
            if (len == 0) { pos += 4; result.Add(string.Empty); continue; }

            if (len > 0)
            {
                int n = len - 1;
                if (pos + 4 + n > b.Length) break;
                result.Add(Encoding.ASCII.GetString(b, pos + 4, n));
                pos += 4 + len;
            }
            else
            {
                int units = -len;
                if (pos + 4 + units * 2 > b.Length) break;
                result.Add(Encoding.Unicode.GetString(b, pos + 4, (units - 1) * 2));
                pos += 4 + units * 2;
            }
        }
        return result;
    }
}
