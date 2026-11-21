using GenerativeSave.Core.Library;
using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>
/// Adds library entries to a save. The library keeps only names and ids, so the
/// real ItemConfig is looked up live: first in the save being edited, then in
/// sibling saves next to it. That keeps catalog.json tiny while still letting an
/// item from save A be added to save B.
/// </summary>
public static class CatalogInjector
{
    /// <summary>A resolved item template: the bytes needed to build a real item.</summary>
    public sealed class ItemTemplate
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int MaxStack { get; init; }
        public byte[] Config { get; init; } = Array.Empty<byte>();
        public byte[] SaveData { get; init; } = Array.Empty<byte>();
        public string Source { get; init; } = string.Empty;
    }

    /// <summary>
    /// Find an item template by id. Searches the given save first, then every
    /// sibling save folder beside it, so an item captured elsewhere is usable.
    /// </summary>
    public static ItemTemplate? Resolve(SaveSlot slot, string itemId, out string error)
    {
        error = string.Empty;

        // 1) the save currently open
        var local = FindInSlot(slot, itemId);
        if (local is not null) return local;

        // 2) sibling saves: each directory next to this one holding a Manifest.sav
        var root = slot.RootPath;
        var parent = Directory.GetParent(root)?.FullName;
        if (parent is null)
        {
            error = "这个存档里没有该物品，附近也没有别的存档可以取用";
            return null;
        }

        foreach (var dir in Directory.GetDirectories(parent))
        {
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(Path.Combine(dir, "Manifest.sav"))) continue;

            try
            {
                var other = SaveSlot.Open(dir, null, new[] { "Player__Players" });
                var hit = FindInSlot(other, itemId);
                if (hit is not null) return hit;
            }
            catch
            {
                // a broken sibling save must not stop the lookup
            }
        }

        error = $"找不到物品 {itemId} 的配置：当前存档和相邻存档里都没有它";
        return null;
    }

    /// <summary>
    /// Every item id this save can supply a template for, with its display name
    /// and stack limit. Used by 按 ID 添加 so the user can see, before typing an
    /// id, whether the save actually holds a config for it.
    /// </summary>
    public static Dictionary<string, (string Name, int MaxStack)> AvailableTemplates(SaveSlot slot)
    {
        var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        var player = SaveFacade.ReadPlayer(slot);
        if (player is null) return map;

        foreach (var it in player.InventoryItems.Where(i => !i.IsEmpty))
        {
            if (string.IsNullOrWhiteSpace(it.ItemId) || it.ItemId == "None") continue;
            if (map.ContainsKey(it.ItemId)) continue;

            var cfg = it.Group.FirstOrDefault(p => p.Name == "ItemConfig");
            if (cfg is null || cfg.OriginalValue.Length == 0) continue;
            map[it.ItemId] = (it.DisplayName, it.MaxStack);
        }
        return map;
    }

    /// <summary>Look up one item inside a single save.</summary>
    public static ItemTemplate? FindInSlot(SaveSlot slot, string itemId)
    {
        var player = SaveFacade.ReadPlayer(slot);
        if (player is null) return null;

        var it = player.InventoryItems.FirstOrDefault(i => !i.IsEmpty && i.ItemId == itemId);
        if (it is null) return null;

        var cfg = it.Group.FirstOrDefault(p => p.Name == "ItemConfig");
        var data = it.Group.FirstOrDefault(p => p.Name == "ItemSaveData");
        if (cfg is null || data is null || cfg.OriginalValue.Length == 0) return null;

        return new ItemTemplate
        {
            Id = it.ItemId,
            Name = it.Name ?? it.ItemId,
            MaxStack = it.MaxStack,
            Config = cfg.OriginalValue,
            SaveData = data.OriginalValue,
            Source = slot.RootPath,
        };
    }

    /// <summary>
    /// Put a library item into the first free bag slot. Returns the slot index,
    /// or -1 with <paramref name="error"/> set.
    /// </summary>
    public static int AddItemToPlayer(SaveSlot slot, string itemId, int count, out string error)
    {
        // A count of 0 (or less) would write a slot that holds a real item id but
        // no quantity: the game shows an empty-looking slot that can never be
        // picked up or removed, and the slot stays occupied. Refuse it instead of
        // quietly creating that ghost entry.
        if (count <= 0) { error = "数量必须大于 0"; return -1; }

        var tpl = Resolve(slot, itemId, out error);
        if (tpl is null) return -1;

        var player = SaveFacade.ReadPlayer(slot);
        if (player is null) { error = "读不到玩家数据"; return -1; }

        var items = player.InventoryItems;
        var groups = player.InventoryGroups;

        int dstIdx = items.FindIndex(i => i.IsEmpty);
        if (dstIdx < 0) { error = "背包没有空槽位"; return -1; }

        return AddToGroup(groups[dstIdx], tpl, count, out error) ? dstIdx : -1;
    }

    /// <summary>Write a resolved template into one 5-property slot group.</summary>
    public static bool AddToGroup(List<SaveProperty> dst, ItemTemplate tpl, int count, out string error)
    {
        error = string.Empty;

        if (dst.Count != 5) { error = $"目标槽位属性数异常（{dst.Count}）"; return false; }

        var dCfg = dst.FirstOrDefault(p => p.Name == "ItemConfig");
        var dData = dst.FirstOrDefault(p => p.Name == "ItemSaveData");
        var dId = dst.FirstOrDefault(p => p.Name == "ItemID");
        var dCount = dst.FirstOrDefault(p => p.Name == "Count");
        var dHandle = dst.FirstOrDefault(p => p.Name == "EquipmentHandle");

        if (dCfg is null || dData is null || dId is null || dCount is null)
        {
            error = "目标槽位缺少必要字段";
            return false;
        }

        if (!string.Equals(dCfg.StructName, "ItemConfig", StringComparison.Ordinal))
        {
            error = $"目标槽位的 ItemConfig 结构名异常（{dCfg.StructName}）";
            return false;
        }

        dCfg.MarkDirty(tpl.Config);
        dData.MarkDirty(tpl.SaveData);

        var idBytes = ValueEditor.EncodeFString(tpl.Id, out error);
        if (idBytes is null) return false;
        dId.MarkDirty(idBytes);

        dCount.MarkDirty(BitConverter.GetBytes(count));
        dHandle?.MarkDirty(ValueEditor.EncodeFString("None", out _)!);

        return true;
    }
}
