using System.Collections.Generic;
using System.Linq;
using GenerativeSave.Core.Container;
using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>
/// Read/write access to the map-side save data: the minimap fog bitmap, the
/// explored zones, the teleport stones, and the placed objects (chests, rocks,
/// player-built props) that live in the player map module.
/// </summary>
public static class MapFacade
{
    // ---- minimap fog ------------------------------------------------------

    public sealed class FogLayer
    {
        public string WorldType { get; init; } = "";
        public SaveProperty? WidthProp { get; init; }
        public SaveProperty? HeightProp { get; init; }
        public SaveProperty? AlphaProp { get; init; }
        public SaveProperty? ValidProp { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public int Explored { get; init; }
        public int Total => Width * Height;
        public double Percent => Total == 0 ? 0 : Explored * 100.0 / Total;
        public string Label => WorldType switch
        {
            "ETopDownWorldType::Surface" => "地表",
            "ETopDownWorldType::Underground" => "地下",
            "ETopDownWorldType::CalamityRift" => "灾厄裂隙",
            _ => WorldType,
        };
    }

    public static List<FogLayer> ReadFog(SaveSlot slot)
    {
        var list = new List<FogLayer>();
        var m = slot.LoadModule("Minimap__Fog");
        if (m is null) return list;
        m.Parse();

        foreach (var root in m.Properties)
        {
            if (root.MapEntries is null) continue;
            foreach (var e in root.MapEntries)
            {
                var w = e.ValueProps.FirstOrDefault(p => p.Name == "Width");
                var h = e.ValueProps.FirstOrDefault(p => p.Name == "Height");
                var a = e.ValueProps.FirstOrDefault(p => p.Name == "AlphaData");
                var v = e.ValueProps.FirstOrDefault(p => p.Name == "bIsValid");

                int width = w?.Value is int wi ? wi : 0;
                int height = h?.Value is int hi ? hi : 0;
                int explored = 0;
                if (a?.Value is System.Collections.IList bytes)
                    foreach (var b in bytes) if (System.Convert.ToInt32(b) != 0) explored++;

                list.Add(new FogLayer
                {
                    WorldType = e.KeyDisplay,
                    WidthProp = w,
                    HeightProp = h,
                    AlphaProp = a,
                    ValidProp = v,
                    Width = width,
                    Height = height,
                    Explored = explored,
                });
            }
        }
        return list;
    }

    /// <summary>
    /// Fill an entire fog layer. Every byte becomes 255, which is the value the
    /// game uses for a fully revealed cell. The array is written whole because
    /// it lives inline in Value, not as parsed elements.
    /// </summary>
    public static bool RevealAll(FogLayer layer)
    {
        if (layer.AlphaProp is null) return false;
        var size = layer.Total;
        if (size <= 0) return false;
        var buf = new byte[size];
        for (int i = 0; i < size; i++) buf[i] = 255;
        layer.AlphaProp.MarkDirty(ValueEditor.EncodeByteArray(buf));
        return true;
    }

    /// <summary>Reset a layer to fully fogged.</summary>
    public static bool HideAll(FogLayer layer)
    {
        if (layer.AlphaProp is null) return false;
        var size = layer.Total;
        if (size <= 0) return false;
        layer.AlphaProp.MarkDirty(ValueEditor.EncodeByteArray(new byte[size]));
        return true;
    }

    // ---- teleport stones --------------------------------------------------

    public sealed class Stone
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string WorldType { get; init; } = "";
        public bool Activated { get; init; }
        public bool Destroyed { get; init; }
        public bool PlayerPlaced { get; init; }
        public SaveProperty? ActivatedProp { get; init; }
        public SaveProperty? DestroyedProp { get; init; }

        public string Label => DisplayName.Length > 0 ? DisplayName : Id;

        /// <summary>
        /// Teleport stone ids look like TS_SF_1_1_70_122 — the last two groups are
        /// the cell coordinates. Showing them makes the list scannable without
        /// forcing the player to decode the id.
        /// </summary>
        public string CoordLabel
        {
            get
            {
                var parts = Id.Split('_');
                if (parts.Length >= 5 &&
                    int.TryParse(parts[^2], out int x) &&
                    int.TryParse(parts[^1], out int y))
                    return $"({x}, {y})";
                return "";
            }
        }

        public string ShortId
        {
            get
            {
                var parts = Id.Split('_');
                return parts.Length >= 3 ? string.Join("_", parts.Take(parts.Length - 2)) : Id;
            }
        }

        /// <summary>
        /// ShortId alone is not unique — this save has two different TS_SF_1_1
        /// stones at different coordinates. Use the full id as the list key so a
        /// row can never be confused with its twin.
        /// </summary>
        public string UniqueLabel => $"{ShortId}  {CoordLabel}";

        public string WorldLabel => WorldType switch
        {
            "ETopDownWorldType::Surface" => "地表",
            "ETopDownWorldType::Underground" => "地下",
            "ETopDownWorldType::CalamityRift" => "裂隙",
            _ => WorldType.Replace("ETopDownWorldType::", ""),
        };
    }

    public static List<Stone> ReadStones(SaveSlot slot)
    {
        var list = new List<Stone>();
        var m = slot.LoadModule("TeleportStone__TeleportStoneRecords");
        if (m is null) return list;
        m.Parse();

        var recs = m.Properties.FirstOrDefault(p => p.Name == "Records");
        if (recs?.ArrayElements is null) return list;

        // Each element is one record's property list, but the game writes all
        // records into a single element, so walk the flat list and split on Id.
        foreach (var e in recs.ArrayElements)
        {
            string? id = null, display = null, world = null;
            bool act = false, dest = false, placed = false;
            SaveProperty? actProp = null, destProp = null;

            void Flush()
            {
                if (id is null) return;
                list.Add(new Stone
                {
                    Id = id,
                    DisplayName = display ?? "",
                    WorldType = world ?? "",
                    Activated = act,
                    Destroyed = dest,
                    PlayerPlaced = placed,
                    ActivatedProp = actProp,
                    DestroyedProp = destProp,
                });
                id = null; display = null; world = null;
                act = false; dest = false; placed = false;
                actProp = null; destProp = null;
            }

            foreach (var p in e.Properties)
            {
                switch (p.Name)
                {
                    case "StoneId":
                        Flush();
                        id = p.Value as string ?? "";
                        break;
                    case "DisplayName":
                        display = p.Value as string;
                        break;
                    case "WorldType":
                        world = p.Value as string;
                        break;
                    case "bActivated":
                        act = p.BoolValue;
                        actProp = p;
                        break;
                    case "bDestroyed":
                        dest = p.BoolValue;
                        destProp = p;
                        break;
                    case "bPlayerPlaced":
                        placed = p.BoolValue;
                        break;
                }
            }
            Flush();
        }
        return list;
    }

    public static bool SetStoneActive(Stone stone, bool on)
    {
        if (stone.ActivatedProp is null) return false;
        stone.ActivatedProp.MarkBool(on);
        return true;
    }

    // ---- placed objects ---------------------------------------------------

    public sealed class Placed
    {
        public string ItemId { get; init; } = "";
        public string Key { get; init; } = "";
        public int CellX { get; init; }
        public int CellY { get; init; }
        public int DetailX { get; init; }
        public int DetailY { get; init; }
        public bool Turn90 { get; init; }
        public bool Main { get; init; }
        public bool PlayerPlace { get; init; }
        public SaveProperty? IdProp { get; init; }
        public SaveProperty? TurnProp { get; init; }

        public string Coord => $"{CellX},{CellY} · 细节 {DetailX},{DetailY}";
    }

    public static List<Placed> ReadPlaceables(SaveSlot slot)
    {
        var list = new List<Placed>();
        var m = slot.LoadModule("GenMap__PlayerMap");
        if (m is null) return list;
        m.Parse();

        var detail = FindDeep(m.Properties, "DetailPlaceItemMapData", 0);
        if (detail?.MapEntries is null) return list;

        foreach (var e in detail.MapEntries)
        {
            var idp = e.ValueProps.FirstOrDefault(p => p.Name == "PlaceableItemID");
            if (idp?.Value is not string id || id.Length == 0) continue;

            int GetInt(string n) => e.ValueProps.FirstOrDefault(p => p.Name == n)?.Value is int v ? v : 0;
            bool GetBool(string n) => e.ValueProps.FirstOrDefault(p => p.Name == n)?.BoolValue ?? false;

            list.Add(new Placed
            {
                ItemId = id,
                Key = e.KeyDisplay,
                CellX = GetInt("PrimaryCellX"),
                CellY = GetInt("PrimaryCellY"),
                DetailX = GetInt("PrimaryDetailX"),
                DetailY = GetInt("PrimaryDetailY"),
                Turn90 = GetBool("bTurn90"),
                Main = GetBool("bMain"),
                PlayerPlace = GetBool("bPlayerPlace"),
                IdProp = idp,
                TurnProp = e.ValueProps.FirstOrDefault(p => p.Name == "bTurn90"),
            });
        }
        return list;
    }

    // ---- chests -----------------------------------------------------------

    /// <summary>One item stack inside a chest's ItemCountArray.</summary>
    public sealed class ChestItem
    {
        public string ItemId { get; init; } = "None";
        public int Count { get; init; }
        public SaveProperty? IdProp { get; init; }
        public SaveProperty? CountProp { get; init; }
        public bool IsEmpty => ItemId is "" or "None" || Count <= 0;
    }

    /// <summary>
    /// A placed container. The game marks these with
    /// <c>EPlaceableItemSavedType::Box</c>; the contents live in ItemCountArray
    /// as 40 slots of (ItemID, ItemType, ItemSaveData, Count).
    /// </summary>
    public sealed class Chest
    {
        public string Module { get; init; } = "";
        public string Key { get; init; } = "";
        public int CellX { get; init; }
        public int CellY { get; init; }
        public string PlaceableId { get; init; } = "";
        public bool HasCustomName { get; init; }
        public string FilterMode { get; init; } = "";
        public List<ChestItem> Items { get; init; } = new();
        public SaveProperty? NameArrayProp { get; init; }
        public SaveProperty? IntArrayProp { get; init; }

        public List<ChestItem> Filled => Items.Where(i => !i.IsEmpty).ToList();
        public int UsedSlots => Filled.Count;
        public string Coord => $"{CellX},{CellY}";
        public string Title => HasCustomName && FilterMode.Length > 0
            ? $"{Coord} · {FilterMode}"
            : $"{Coord} · {PlaceableId}";
    }

    /// <summary>
    /// Chests are expensive to collect (every map chunk must be inflated and
    /// scanned), so the result is cached per slot and only recomputed after an
    /// edit dirties the map modules.
    /// </summary>
    private static readonly Dictionary<string, (int Stamp, List<Chest> Chests)> ChestCache = new();

    /// <summary>
    /// Bumped whenever a chest edit lands, so the cache drops even though the
    /// files on disk have not been rewritten yet.
    /// </summary>
    private static int _chestEpoch;

    private static void BumpChestEpoch() => Interlocked.Increment(ref _chestEpoch);

    private static int CacheStamp(SaveSlot slot)
    {
        // Deliberately NOT keyed on slot.Modules.Count(IsDirty): the first scan
        // registers the chunk modules it parsed, which changes that count and
        // made every second call look stale. File mtimes plus an explicit epoch
        // is stable across scans and still catches both disk and in-memory edits.
        int stamp = _chestEpoch;
        foreach (var p in slot.ModuleFiles.Where(k => Path.GetFileName(k).StartsWith("GenMap__")))
        {
            try { stamp = HashCode.Combine(stamp, File.GetLastWriteTimeUtc(p).Ticks); }
            catch { }
        }
        return stamp;
    }

    /// <summary>Read every chest, reusing the previous scan when nothing changed.</summary>
    public static List<Chest> ReadChestsCached(SaveSlot slot, Action<string>? progress = null)
    {
        var key = slot.RootPath ?? "";
        int stamp = CacheStamp(slot);
        lock (ChestCache)
        {
            if (ChestCache.TryGetValue(key, out var hit) && hit.Stamp == stamp)
                return hit.Chests;
        }
        var chests = ReadChests(slot, progress);
        lock (ChestCache)
            ChestCache[key] = (CacheStamp(slot), chests);
        return chests;
    }

    /// <summary>Drop the cached chest scan for a slot.</summary>
    public static void InvalidateChests(SaveSlot slot)
    {
        lock (ChestCache) ChestCache.Remove(slot.RootPath ?? "");
    }

    /// <summary>
    /// Read every chest in the save. Chests are scattered across the surface
    /// and underground chunk modules, so this walks all of them. Parsing ~120
    /// chunk modules serially took 32 s, so they are loaded in parallel and only
    /// the ones that actually mention PlaceableItemSaveDatas are parsed.
    /// </summary>
    public static List<Chest> ReadChests(SaveSlot slot, Action<string>? progress = null)
    {
        var list = new List<Chest>();
        var paths = slot.ModuleFiles
            .Where(k => Path.GetFileName(k).StartsWith("GenMap__"))
            .OrderBy(x => x)
            .ToList();

        // Most chunks hold no container at all. The save stores the type name as
        // literal ASCII, so a byte scan for "Box" next to the field name rejects
        // the empty ones in milliseconds instead of parsing 1-3 MB of properties.
        var candidates = new List<(string Path, string Name)>();
        foreach (var path in paths)
        {
            try
            {
                var raw = SaveContainer.Unwrap(File.ReadAllBytes(path));
                if (Contains(raw, "PlaceableItemSaveDatas") && Contains(raw, "EPlaceableItemSavedType::Box"))
                    candidates.Add((path, Path.GetFileNameWithoutExtension(path)));
            }
            catch { }
        }

        var found = new System.Collections.Concurrent.ConcurrentBag<(string Name, List<Chest> Chests, SaveModule? Module)>();

        Parallel.ForEach(candidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1)
        }, item =>
        {
            progress?.Invoke(item.Name);
            try
            {
                // Parse at most once per module.
                //
                // This used to call Parse() unconditionally, so a second scan in
                // the same session re-parsed every already-loaded chunk: 49 s
                // instead of 24 s, and worse the more chunks had been visited.
                // A module that is already in the cache has been parsed.
                var m = slot.Modules.FirstOrDefault(x => x.ModuleName == item.Name);
                if (m is null)
                {
                    m = SaveModule.Load(item.Path);
                    m.Parse();
                }
                else if (m.Properties.Count == 0)
                {
                    m.Parse();
                }

                var chests = ExtractChests(m, item.Name);
                if (chests.Count > 0) found.Add((item.Name, chests, m));
            }
            catch
            {
                // A chunk that fails to parse simply contributes no chests.
            }
        });

        foreach (var (name, chests, module) in found.OrderBy(x => x.Name))
        {
            // Register the chunk on the slot so SaveAll writes it. Modules loaded
            // outside slot.Modules were silently dropped from the save, which made
            // chest edits vanish on reload.
            if (module is not null && !slot.Modules.Any(x => x.ModuleName == name))
                slot.Modules.Add(module);
            list.AddRange(chests);
        }
        return list;
    }

    private static bool Contains(byte[] haystack, string needle)
    {
        // Span.IndexOf is vectorised; the hand-rolled loop this replaced walked
        // the buffer one byte at a time, and the chest pre-filter calls it on
        // every chunk (116 modules, 1-3 MB each) so it dominated the scan.
        return haystack.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(needle)) >= 0;
    }

    private static List<Chest> ExtractChests(SaveModule m, string moduleName)
    {
        var list = new List<Chest>();
        var sd = FindDeep(m.Properties, "PlaceableItemSaveDatas", 0);
        if (sd?.MapEntries is null) return list;

        var detail = FindDeep(m.Properties, "DetailPlaceItemMapData", 0);

        foreach (var e in sd.MapEntries)
        {
            var type = e.ValueProps.FirstOrDefault(p => p.Name == "SaveType")?.Value?.ToString() ?? "";
            if (!type.Contains("Box")) continue;

            var items = new List<ChestItem>();
            var ica = e.ValueProps.FirstOrDefault(p => p.Name == "ItemCountArray");
            if (ica?.ArrayElements is not null)
            {
                foreach (var el in ica.ArrayElements)
                {
                    string? id = null;
                    int count = 0;
                    SaveProperty? idProp = null, countProp = null;

                    void Flush()
                    {
                        if (id is null) return;
                        items.Add(new ChestItem { ItemId = id, Count = count, IdProp = idProp, CountProp = countProp });
                        id = null; count = 0; idProp = null; countProp = null;
                    }

                    foreach (var p in el.Properties)
                    {
                        switch (p.Name)
                        {
                            case "ItemID":
                                Flush();
                                id = p.Value?.ToString() ?? "None";
                                idProp = p;
                                break;
                            case "Count":
                                count = p.Value is int ci ? ci : 0;
                                countProp = p;
                                break;
                        }
                    }
                    Flush();
                }
            }

            var strings = e.ValueProps.FirstOrDefault(p => p.Name == "StringArray");
            var nameArr = (strings?.Value as System.Collections.IList)?.Cast<object?>().Select(x => x?.ToString() ?? "").ToList() ?? new();
            bool custom = nameArr.Any(s => s.Contains("HasCustomName__=1"));
            var mode = nameArr.FirstOrDefault(s => s.Contains("FilterMode__="))?.Split('=').Last() ?? "";

            var placedId = detail?.MapEntries?.FirstOrDefault(d => d.KeyDisplay == e.KeyDisplay)
                ?.ValueProps.FirstOrDefault(p => p.Name == "PlaceableItemID")?.Value?.ToString() ?? "";

            var keyParts = e.KeyDisplay.Split(',');
            int.TryParse(keyParts.ElementAtOrDefault(0)?.Trim(), out int cx);
            int.TryParse(keyParts.ElementAtOrDefault(1)?.Trim(), out int cy);

            list.Add(new Chest
            {
                Module = moduleName,
                Key = e.KeyDisplay,
                CellX = cx,
                CellY = cy,
                PlaceableId = placedId,
                HasCustomName = custom,
                FilterMode = mode,
                Items = items,
                NameArrayProp = strings,
                IntArrayProp = e.ValueProps.FirstOrDefault(p => p.Name == "IntArray"),
            });
        }
        return list;
    }

    /// <summary>Put an item into the first free slot of a chest.</summary>
    public static bool AddToChest(Chest chest, string itemId, int count)
    {
        var free = chest.Items.FirstOrDefault(i => i.IsEmpty && i.IdProp is not null && i.CountProp is not null);
        if (free is null) return false;
        var idBytes = ValueEditor.Encode(free.IdProp!, itemId, out var err);
        if (idBytes is null) return false;
        free.IdProp!.MarkDirty(idBytes);
        var countBytes = ValueEditor.Encode(free.CountProp!, count.ToString(), out _);
        if (countBytes is null) return false;
        free.CountProp!.MarkDirty(countBytes);
        BumpChestEpoch();
        return true;
    }

    /// <summary>Change the stack size of one chest slot.</summary>
    public static bool SetChestCount(ChestItem item, int count)
    {
        if (item.CountProp is null) return false;
        var bytes = ValueEditor.Encode(item.CountProp, count.ToString(), out _);
        if (bytes is null) return false;
        item.CountProp.MarkDirty(bytes);
        BumpChestEpoch();
        return true;
    }

    /// <summary>Empty a chest slot by blanking its id.</summary>
    public static bool ClearChestSlot(ChestItem item)
    {
        if (item.IdProp is null || item.CountProp is null) return false;
        var idBytes = ValueEditor.Encode(item.IdProp, "None", out _);
        var countBytes = ValueEditor.Encode(item.CountProp, "0", out _);
        if (idBytes is null || countBytes is null) return false;
        item.IdProp.MarkDirty(idBytes);
        item.CountProp.MarkDirty(countBytes);
        BumpChestEpoch();
        return true;
    }

    // ---- AI generated placeables -----------------------------------------

    public sealed class AiPlaceable
    {
        public string TaskId { get; init; } = "";
        public string Prompt { get; init; } = "";
        public string Size { get; init; } = "";
        public string Name { get; init; } = "";
        public SaveProperty? PromptProp { get; init; }

        public string Short => Prompt.Length > 40 ? Prompt[..40] + "…" : Prompt;
    }

    public static List<AiPlaceable> ReadAiPlaceables(SaveSlot slot)
    {
        var list = new List<AiPlaceable>();
        var m = slot.LoadModule("GenPlaceable__AIGenPlaceable");
        if (m is null) return list;
        m.Parse();

        var recs = m.Properties.FirstOrDefault(p => p.Name == "GeneratedPlaceableRecords");
        if (recs?.ArrayElements is null) return list;

        foreach (var e in recs.ArrayElements)
        {
            string? task = null, prompt = null, size = null, name = null;
            SaveProperty? promptProp = null;

            void Flush()
            {
                if (task is null) return;
                list.Add(new AiPlaceable
                {
                    TaskId = task,
                    Prompt = prompt ?? "",
                    Size = size ?? "",
                    Name = name ?? "",
                    PromptProp = promptProp,
                });
                task = null; prompt = null; size = null; name = null; promptProp = null;
            }

            foreach (var p in e.Properties)
            {
                switch (p.Name)
                {
                    case "TaskID":
                        Flush();
                        task = p.Value as string ?? "";
                        break;
                    case "Prompt":
                        prompt = p.Value as string;
                        promptProp = p;
                        break;
                    case "RequestedSize":
                        size = p.Value as string;
                        break;
                    case "ItemName":
                        name = p.Value as string;
                        break;
                }
            }
            Flush();
        }
        return list;
    }

    // ---- zones ------------------------------------------------------------

    public sealed class Zone
    {
        public string Guid { get; init; } = "";
        public int MinX { get; init; }
        public int MinY { get; init; }
        public int MaxX { get; init; }
        public int MaxY { get; init; }
        public int CellCount { get; init; }
        public string Tag { get; init; } = "";

        public string Size => $"{MaxX - MinX + 1}×{MaxY - MinY + 1}";
    }

    public static List<Zone> ReadZones(SaveSlot slot)
    {
        var list = new List<Zone>();
        var m = slot.LoadModule("ZoneSubsystem__Zone");
        if (m is null) return list;
        m.Parse();

        var zones = m.Properties.FirstOrDefault(p => p.Name == "Zones");
        if (zones?.ArrayElements is null) return list;

        foreach (var e in zones.ArrayElements)
        {
            string? guid = null, tag = null;
            int minX = 0, minY = 0, maxX = 0, maxY = 0, cells = 0;

            void Flush()
            {
                if (guid is null) return;
                list.Add(new Zone
                {
                    Guid = guid, Tag = tag ?? "", MinX = minX, MinY = minY,
                    MaxX = maxX, MaxY = maxY, CellCount = cells,
                });
                guid = null; tag = null;
                minX = minY = maxX = maxY = cells = 0;
            }

            foreach (var p in e.Properties)
            {
                switch (p.Name)
                {
                    case "ZoneId":
                        Flush();
                        guid = p.Value?.ToString() ?? "";
                        break;
                    case "PackedCoveredCells":
                        cells = (p.Value as System.Collections.IList)?.Count ?? 0;
                        break;
                    case "AABB":
                        foreach (var c in p.Children ?? new List<SaveProperty>())
                        {
                            int v = c.Value is int vi ? vi : 0;
                            switch (c.Name)
                            {
                                case "MinX": minX = v; break;
                                case "MinY": minY = v; break;
                                case "MaxX": maxX = v; break;
                                case "MaxY": maxY = v; break;
                            }
                        }
                        break;
                    case "ZoneTags":
                        if (p.Value is System.Collections.IList tl && tl.Count > 0)
                            tag = tl[0]?.ToString() ?? "";
                        else
                        {
                            var tn = p.Children?.FirstOrDefault(c => c.Name == "TagName");
                            tag = tn?.Value?.ToString() ?? "";
                        }
                        break;
                }
            }
            Flush();
        }
        return list;
    }

    // ---- collection / codex ----------------------------------------------

    public sealed class Codex
    {
        public SaveProperty? UnlockedItemsProp { get; init; }
        public SaveProperty? UnlockedMonstersProp { get; init; }
        public int Items { get; init; }
        public int Monsters { get; init; }
    }

    public static Codex? ReadCodex(SaveSlot slot)
    {
        var m = slot.LoadModule("CollectionSystem__CollectionData");
        if (m is null) return null;
        m.Parse();

        var items = m.Properties.FirstOrDefault(p => p.Name == "UnlockedItems");
        var monsters = m.Properties.FirstOrDefault(p => p.Name == "UnlockedMonsters");
        return new Codex
        {
            UnlockedItemsProp = items,
            UnlockedMonstersProp = monsters,
            Items = (items?.Value as System.Collections.IList)?.Count ?? 0,
            Monsters = (monsters?.Value as System.Collections.IList)?.Count ?? 0,
        };
    }

    public static SaveProperty? FindDeep(IEnumerable<SaveProperty> roots, string name, int depth)
    {
        foreach (var p in roots)
        {
            var hit = FindDeep(p, name, depth);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static SaveProperty? FindDeep(SaveProperty p, string name, int depth)
    {
        if (p.Name == name) return p;
        if (depth > 6) return null;
        if (p.Children is not null)
            foreach (var c in p.Children) { var r = FindDeep(c, name, depth + 1); if (r is not null) return r; }
        if (p.ArrayElements is not null)
            foreach (var e in p.ArrayElements)
                foreach (var c in e.Properties) { var r = FindDeep(c, name, depth + 1); if (r is not null) return r; }
        if (p.MapEntries is not null)
            foreach (var e in p.MapEntries)
                foreach (var c in e.ValueProps) { var r = FindDeep(c, name, depth + 1); if (r is not null) return r; }
        return null;
    }
}
