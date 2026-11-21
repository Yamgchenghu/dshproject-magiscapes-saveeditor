using System.Text.Json;
using System.Text.Json.Serialization;
using GenerativeSave.Core.Editing;

namespace GenerativeSave.Core.Library;

/// <summary>
/// One known item. The library stores only identity — name and id — because the
/// full ItemConfig is already inside whichever save owns the item. Injection
/// therefore looks the config up live (current save first, then sibling saves)
/// instead of keeping a private copy of the bytes.
/// </summary>
public sealed class CatalogItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Which save this entry was first seen in (informational only).</summary>
    public string Source { get; set; } = string.Empty;

    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Name) ? Id : $"{Name}（{Id}）";
}

/// <summary>One built-in NPC that can be recruited into any save.</summary>
public sealed class CatalogNpc
{
    public string Id { get; set; } = string.Empty;
    public string Cn { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;

    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Cn) ? Id : $"{Cn}（{Id}）";
}

public sealed class CatalogFile
{
    public int Version { get; set; } = 2;
    public List<CatalogItem> Items { get; set; } = new();
    public List<CatalogNpc> Npcs { get; set; } = new();

    public bool IsEmpty => Items.Count == 0 && Npcs.Count == 0;
}

/// <summary>
/// Persistent cross-save index stored under %LOCALAPPDATA%. Deliberately small:
/// it records which items and NPCs exist anywhere, so the pickers can offer them
/// in every save, while the bulky config bytes stay in the saves themselves.
/// </summary>
public static class CatalogStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// When false, the library may still be READ but never written.
    ///
    /// The headless modes (--selftest, --shot, --uitest) open save copies to
    /// exercise the UI, and with automatic library top-up those runs were
    /// silently folding test saves into the user's real library: a self-test on
    /// GameSave_1 left the library holding GameSave_2's 37 extra items. The
    /// library belongs to the user, so a test run must not write it.
    ///
    /// Reading stays allowed on purpose. A screenshot run that showed an empty
    /// library could not verify the real page, and blocking reads once caused a
    /// worse bug: the page saw 0 built-in NPCs, seeded 54 into a no-op save,
    /// rebuilt itself, and recursed until the stack overflowed.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Redirect the store to a scratch folder. Test harnesses set this so a probe
    /// can never touch the real catalog.json — the same class of accident that
    /// once wrote 116 junk rows into the live library.
    /// </summary>
    public static string? DirOverride { get; set; }

    public static string Dir =>
        DirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GenerativeSaveEditor");

    public static string FilePath => Path.Combine(Dir, "catalog.json");

    /// <summary>
    /// Write the copy of catalog.json baked into the executable, but only when
    /// the user has none. An existing catalog is user data and is never
    /// overwritten — it grows as they play, so replacing it would destroy work.
    /// </summary>
    public static void SeedIfMissing()
    {
        try
        {
            if (!Enabled) return;
            if (File.Exists(FilePath)) return;

            var asm = typeof(CatalogStore).Assembly;
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return;

            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            using (var outFile = File.Create(tmp)) stream.CopyTo(outFile);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // A failed seed is not fatal: the editor works with an empty library.
        }
    }

    public static CatalogFile Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new CatalogFile();
            var json = File.ReadAllText(FilePath);
            var cat = JsonSerializer.Deserialize<CatalogFile>(json, JsonOpts) ?? new CatalogFile();

            // Older catalogs may hold AI-generated entries; drop them on sight so
            // they never appear in a picker again.
            PruneNonOfficial(cat);
            return cat;
        }
        catch
        {
            // A corrupt catalog must never stop the editor from opening a save.
            return new CatalogFile();
        }
    }

    public static bool Save(CatalogFile cat, out string error)
    {
        error = string.Empty;
        // Report failure rather than a silent success. Callers branch on the
        // return value (the built-in-NPC top-up navigates and rebuilds on
        // success), so claiming success here used to drive an infinite
        // rebuild loop that overflowed the stack.
        if (!Enabled)
        {
            error = "资料库处于只读模式（测试运行），未写入。";
            return false;
        }
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cat, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Official item ids are always 7 digits. The game's AI item generator writes
    /// 8-digit ids, so anything 8 digits or longer is player-generated content
    /// that does not belong in a shared library — its name is literally the
    /// prompt that produced it.
    /// </summary>
    public static bool IsOfficialItemId(string id)
    {
        if (id.Length != 7) return false;
        foreach (var c in id) if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    /// <summary>Marks an entry the player named by hand rather than one we read.</summary>
    public const string ManualSource = "手动填写";

    /// <summary>
    /// Record a user-supplied name for an item id. This is the only way to name
    /// the chest items that no save has ever put in a bag: their ItemConfig does
    /// not exist anywhere on disk, so the name has to come from the player.
    /// Returns false when the id is not an official 7-digit id.
    /// </summary>
    public static bool SetItemName(CatalogFile cat, string id, string name, out string error)
    {
        error = string.Empty;
        id = id.Trim();
        name = name.Trim();

        if (!IsOfficialItemId(id))
        {
            error = "物品 ID 必须是 7 位数字。";
            return false;
        }
        if (name.Length == 0)
        {
            error = "名称不能为空。";
            return false;
        }

        var hit = cat.Items.FirstOrDefault(i => i.Id == id);
        if (hit is null) cat.Items.Add(new CatalogItem { Id = id, Name = name, Source = ManualSource });
        else { hit.Name = name; hit.Source = ManualSource; }
        return true;
    }

    /// <summary>Item id → name for everything the library knows.</summary>
    public static Dictionary<string, string> ItemNameMap(CatalogFile cat)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in cat.Items)
            if (!string.IsNullOrWhiteSpace(i.Name)) map[i.Id] = i.Name;
        return map;
    }

    /// <summary>
    /// Record every real item in this save. Returns how many were new.
    ///
    /// The player's own bag is only part of the picture: an NPC backpack carries
    /// the same ItemConfig block, and between them the NPCs of one save know 103
    /// item ids the player has never picked up. Those are the only offline source
    /// of names for chest contents, so they are collected too.
    /// </summary>
    /// <summary>
    /// A short, human label for where an entry came from.
    ///
    /// The caller used to pass slot.RootPath, so the 来源存档 column filled up
    /// with full C:\Users\... paths, and headless runs stamped rows with
    /// "test"/"NEW1" — meaningless to the user and actively misleading in a
    /// library they are meant to read. Keep just the folder name, and fold the
    /// scratch names the test harness uses into one honest label.
    /// </summary>
    public static string LabelFor(string savePath)
    {
        try
        {
            var name = Path.GetFileName(savePath.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = savePath;
            if (name is "test" or "NEW1" or "NEW2" or "AU1" or "AU2"
                or "RG1" or "RG2" or "RG3" or "RG4" or "FR1" or "PF12"
                or "SH12" or "SH13" or "SH14" or "SH15" or "SHB" or "TG1"
                or "PY1" or "PY2" or "PY3" or "LiveBackup_20261114_145151")
                return "本地存档";
            return name;
        }
        catch { return savePath; }
    }

    public static int CaptureItems(SaveSlot slot, string saveLabel, CatalogFile cat)
    {
        var found = new List<(string Id, string Name)>();

        // Every item array the player owns, not a hand-picked five. PlayersData
        // holds nine: the five bag-like ones plus ItemHolderItems, SkinItems,
        // VehicleItems, DestroyItems and ReputationRewardGrantRecords. Scanning
        // only the obvious five silently dropped real official items — 1020146
        // (椰枣木家具配方套组) lives in DestroyItems, i.e. the player threw it
        // away, and the library never learned its name.
        var player = SaveFacade.ReadPlayer(slot);
        if (player is not null)
            foreach (var (id, nm) in ReadAllItemGroups(player))
                found.Add((id, nm));

        found.AddRange(ReadNpcBagNames(slot));

        int added = 0;
        foreach (var (id, name) in found)
        {
            if (string.IsNullOrWhiteSpace(id) || id == "None") continue;

            // skip AI-generated items: their ids are 8+ digits
            if (!IsOfficialItemId(id)) continue;

            // A row whose name is just the id tells the user nothing and makes
            // the library look broken, so an unnameable item is simply not
            // recorded. It will still show up wherever it is used, as a bare id.
            var clean = CleanName(name, id);
            if (clean == id) continue;

            if (cat.Items.Any(x => x.Id == id)) continue;

            cat.Items.Add(new CatalogItem
            {
                Id = id,
                Name = clean,
                Source = saveLabel,
            });
            added++;
        }
        return added;
    }

    /// <summary>
    /// What one top-up run changed.
    /// </summary>
    public readonly record struct TopUpResult(int AddedItems, int AddedNpcs,
                                              int TotalItems, int TotalNpcs, bool Wrote);

    /// <summary>
    /// Fold a save into the library and persist the result, but only when
    /// something actually changed.
    ///
    /// This is the whole "每次读取存档自动更新索引库" behaviour, kept here rather
    /// than inside the form so it can be exercised without a window: the UI is
    /// a thin caller, and a headless run can prove the same path.
    ///
    /// Writing only on a real change matters: merely opening a save must not
    /// rewrite the user's library file, otherwise its mtime churns on every
    /// launch and a failed write could clobber a good library for no reason.
    /// </summary>
    public static TopUpResult TopUpFrom(SaveSlot slot, string saveLabel, out string error)
    {
        error = string.Empty;
        var cat = Load();
        int items0 = cat.Items.Count, npcs0 = cat.Npcs.Count;
        saveLabel = LabelFor(saveLabel);

        // Read-only mode: report the current counts and claim nothing.
        if (!Enabled)
            return new TopUpResult(0, 0, items0, npcs0, false);

        int pruned = PruneNonOfficial(cat);
        int addItems = CaptureItems(slot, saveLabel, cat);
        int addNpcs = CaptureNpcs(slot, saveLabel, cat);

        bool changed = addItems > 0 || addNpcs > 0 || pruned > 0
                       || cat.Items.Count != items0 || cat.Npcs.Count != npcs0;

        if (!changed)
            return new TopUpResult(0, 0, cat.Items.Count, cat.Npcs.Count, false);

        if (!Save(cat, out error))
            return new TopUpResult(addItems, addNpcs, cat.Items.Count, cat.Npcs.Count, false);

        return new TopUpResult(addItems, addNpcs, cat.Items.Count, cat.Npcs.Count, true);
    }

    /// <summary>
    /// Every 5-property item group under PlayersData, whatever array it sits in.
    ///
    /// Grouping is positional (ItemID, Count, EquipmentHandle, ItemConfig,
    /// ItemSaveData) because the names repeat. Walking the map values directly
    /// means a new array added by a game update is picked up for free, instead
    /// of needing another name added to a list.
    /// </summary>
    private static IEnumerable<(string Id, string Name)> ReadAllItemGroups(PlayerView player)
    {
        foreach (var prop in player.MapProps)
        {
            if (prop.TypeName != "ArrayProperty") continue;
            var wrapper = prop.ArrayElements?.FirstOrDefault();
            if (wrapper?.Properties is null) continue;

            List<Model.SaveProperty>? group = null;
            foreach (var p in wrapper.Properties)
            {
                if (p.Name is "ItemID" or "ItemId") group = new List<Model.SaveProperty>();
                group?.Add(p);
                if (group is null || group.Count < 5) continue;

                var id = ValueEditor.EffectiveValueOrNull(
                    group.FirstOrDefault(x => x.Name is "ItemID" or "ItemId")) as string;
                var cntProp = group.FirstOrDefault(x => x.Name == "Count");
                int count = cntProp is null ? 0
                          : Convert.ToInt32(ValueEditor.EffectiveValueOrNull(cntProp) ?? 0);
                var cfg = group.FirstOrDefault(x => x.Name == "ItemConfig");
                group = null;

                if (string.IsNullOrEmpty(id) || id == "None") continue;
                if (count <= 0) continue;   // empty slot

                var nm = ValueEditor.EffectiveValueOrNull(cfg?.Child("Explain")) as string;
                yield return (id, nm ?? string.Empty);
            }
        }
    }

    /// <summary>
    /// Item id → name, read from every NPC backpack in the save. Uses the same
    /// ItemConfig.Explain path as the player's bag, so the result is a real name
    /// rather than something guessed.
    /// </summary>
    public static List<(string Id, string Name)> ReadNpcBagNames(SaveSlot slot)
    {
        var list = new List<(string, string)>();
        foreach (var npc in SaveFacade.ReadNpcs(slot))
        {
            var bp = npc.BackPack;
            if (bp?.Children is null) continue;

            foreach (var arr in bp.Children)
            {
                var wrapper = arr.ArrayElements?.FirstOrDefault();
                if (wrapper?.Properties is null) continue;

                // Same layout as the player bag: five properties per slot,
                // starting at ItemID.
                List<Model.SaveProperty>? group = null;
                foreach (var p in wrapper.Properties)
                {
                    if (p.Name == "ItemID") group = new List<Model.SaveProperty>();
                    group?.Add(p);
                    if (group is null || group.Count < 5) continue;

                    var id = ValueEditor.EffectiveValueOrNull(
                        group.FirstOrDefault(x => x.Name == "ItemID")) as string;
                    var cfg = group.FirstOrDefault(x => x.Name == "ItemConfig");
                    group = null;

                    if (string.IsNullOrEmpty(id) || id == "None") continue;
                    var nm = ValueEditor.EffectiveValueOrNull(cfg?.Child("Explain")) as string;
                    if (!string.IsNullOrWhiteSpace(nm)) list.Add((id!, nm!));
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Some ItemConfig.Explain values are a description, not a name ("雪原中常见的
    /// 石头，与平原石头颜色上有差别。"), and a few are leftover English asset names
    /// (GoldCoin, SandTile). Keep the first line and cut anything that reads as a
    /// sentence, so the library stays a name list instead of a text dump.
    /// </summary>
    public static string CleanName(string raw, string id)
    {
        if (string.IsNullOrWhiteSpace(raw)) return id;
        var line = raw.Split('\n', '\r')[0].Trim();
        if (line.Length == 0) return id;

        // 【武器】：技能展开后… — for weapons, accessories and placeables the
        // 【category】 prefix plus a sentence IS the only name the save carries.
        // Dropping those rows left the library missing 5 real official items and
        // made them show up as bare ids everywhere. Keep the category and the
        // first clause, which is what the player recognises.
        if (line.StartsWith('【'))
        {
            int close = line.IndexOf('】');
            if (close > 0 && close < line.Length - 1)
            {
                var head = line[..(close + 1)];
                var rest = line[(close + 1)..].TrimStart('：', ':', ' ');
                var clause = FirstClause(rest);
                var composed = clause.Length > 0 ? head + clause : head;
                return composed.Length > 40 ? composed[..40] : composed;
            }
            return line.Length > 40 ? line[..40] : line;
        }

        // A plain sentence with no category prefix is still better than a bare id
        // when nothing else names the item, but trim it to something readable.
        var cut = FirstClause(line);
        if (cut.Length > 24) cut = cut[..24];
        return cut.Length == 0 ? id : cut;
    }

    /// <summary>Text up to the first clause break, so a description becomes a label.</summary>
    private static string FirstClause(string s)
    {
        foreach (var ch in new[] { '。', '，', '；', '、', '.', ',', ';' })
        {
            int i = s.IndexOf(ch);
            if (i > 0) s = s[..i];
        }
        return s.Trim();
    }

    /// <summary>
    /// Official NPC ids are readable names (Alex, SnowWillow, MaleVillager_A), so
    /// they always contain a lowercase letter. The NPCs the player personally
    /// recruits get a generated 6-character code in caps and digits
    /// (YEDK65, 1XAP7L) and must not enter the shared library — they only mean
    /// something inside the save that generated them.
    /// </summary>
    public static bool IsOfficialNpcId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        foreach (var c in id) if (char.IsLower(c)) return true;
        return false;
    }

    /// <summary>
    /// Drop entries that should never have been catalogued: AI-generated item
    /// ids and player-recruited NPC codes. Returns how many were removed.
    /// </summary>
    public static int PruneNonOfficial(CatalogFile cat)
    {
        int before = cat.Items.Count + cat.Npcs.Count;
        cat.Items.RemoveAll(i => !IsOfficialItemId(i.Id));

        // An entry whose name is its own id carries no information — it is what
        // CleanName produces for a description, and it used to be written to the
        // library anyway. Keep only hand-named rows in that situation, since the
        // player may deliberately have typed the id as the name.
        cat.Items.RemoveAll(i => i.Name == i.Id && i.Source != ManualSource);

        cat.Npcs.RemoveAll(n => !IsOfficialNpcId(n.Id));
        return before - (cat.Items.Count + cat.Npcs.Count);
    }

    /// <summary>
    /// Add every built-in resident the game ships with, whether or not this
    /// particular save happens to contain them. The official wiki lists 55
    /// residents; a save only holds the ones it has met, so seeding from the
    /// table is what makes the library complete instead of save-dependent.
    /// </summary>
    public static int SeedBuiltInNpcs(CatalogFile cat)
    {
        int added = 0;
        foreach (var (id, cn) in SaveFacade.BuiltInNpcs)
        {
            if (string.IsNullOrEmpty(id)) continue;
            if (cat.Npcs.Any(x => x.Id == id)) continue;
            cat.Npcs.Add(new CatalogNpc
            {
                Id = id,
                Cn = cn ?? string.Empty,
                Source = "游戏内置",
            });
            added++;
        }
        return added;
    }

    public static int CaptureNpcs(SaveSlot slot, string saveLabel, CatalogFile cat)
    {
        // Start from the full built-in table so the library is complete even for
        // residents this save has never met.
        int added = SeedBuiltInNpcs(cat);

        foreach (var npc in SaveFacade.ReadNpcs(slot))
        {
            if (!npc.IsSimulated) continue;

            // skip player-recruited codes (all caps + digits, no lowercase)
            if (!IsOfficialNpcId(npc.Id)) continue;

            if (cat.Npcs.Any(x => x.Id == npc.Id)) continue;

            var known = SaveFacade.BuiltInNpcs.FirstOrDefault(b => b.Id == npc.Id);
            cat.Npcs.Add(new CatalogNpc
            {
                Id = npc.Id,
                Cn = known.Cn ?? string.Empty,
                Source = saveLabel,
            });
            added++;
        }
        return added;
    }
}
