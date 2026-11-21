using System.Text.RegularExpressions;
using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>
/// Per-chunk access to the map. The save keeps the world in 117 separate
/// GenMap__* modules; reading all of them costs ~28 s, but one chunk is
/// 0.2-1.3 s. This facade exposes a single chunk at a time so the editor can
/// show and edit one area without paying for the whole world.
/// </summary>
public static class ChunkFacade
{
    /// <summary>One placeable object sitting on a chunk's detail grid.</summary>
    public sealed class ChunkObject
    {
        public string Coord { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public string ItemId { get; init; } = "";
        /// <summary>True for the OCCUPIED placeholder rows that mark blocked cells.</summary>
        public bool Occupied { get; init; }
        public bool PlayerPlaced { get; init; }
        public bool Main { get; init; }
        public bool Turn90 { get; init; }
        public int RotateIndex { get; init; }
        public SaveProperty? IdProp { get; init; }
        public SaveProperty? TurnProp { get; init; }
        public SaveProperty? RotateProp { get; init; }
        /// <summary>Container state at the same coordinate, when the object has one.</summary>
        public string SaveType { get; init; } = "";
        public bool IsBox => SaveType == "EPlaceableItemSavedType::Box";
        public bool IsDoor => SaveType == "EPlaceableItemSavedType::Door";
        public bool IsBonfire => SaveType == "EPlaceableItemSavedType::Bonfire";

        public string Label => Occupied ? "（占位）" : ItemId;
        public string TypeLabel => SaveType.Length == 0
            ? ""
            : SaveType.Replace("EPlaceableItemSavedType::", "");
    }

    /// <summary>One terrain override on a chunk's detail grid.</summary>
    public sealed class TerrainEdit
    {
        public string Key { get; init; } = "";
        public int DetailIndex { get; init; }
        public int LayerId { get; init; }
        public int TileSetIndex { get; init; }
        public int TileImageIndex { get; init; }
        public SaveProperty? LayerIdProp { get; init; }
        public SaveProperty? TileSetProp { get; init; }
        public SaveProperty? TileImageProp { get; init; }

        public string Coord
        {
            get
            {
                // The key is a packed detail index; the game stores it as a single
                // int. Decode to x,y using the chunk's terrain size.
                return Key;
            }
        }
    }

    /// <summary>Everything one chunk module holds, decoded.</summary>
    public sealed class ChunkView
    {
        public string ModuleName { get; init; } = "";
        public int Index { get; init; }
        public int TerrainSize { get; init; }
        public bool IsUnderground { get; init; }
        public List<ChunkObject> Objects { get; init; } = new();
        public List<TerrainEdit> Terrain { get; init; } = new();
        public int EmptyDetailCount { get; set; }

        public string Label => $"{(IsUnderground ? "地下" : "地表")} {Index}（区块 {BiomeX},{BiomeY}）";
        public int ObjectCount => Objects.Count(o => !o.Occupied);
        public int OccupiedCount => Objects.Count(o => o.Occupied);
        public int BoxCount => Objects.Count(o => o.IsBox);
        public int DoorCount => Objects.Count(o => o.IsDoor);

        /// <summary>
        /// Biome column count. Proven, not guessed: teleport stones record both
        /// BiomeX/BiomeY and a module-index shape, and the three stones sharing
        /// BiomeY=1 sit at BiomeX 1,2,3 → modules 18,19,20. Consecutive numbering
        /// at y=1 forces width 17, which also maps every other stone (11 distinct
        /// biomes) onto a module that actually exists in this save.
        /// </summary>
        public const int BiomeWidth = 17;

        /// <summary>Module index for a biome cell: index = BiomeY * 17 + BiomeX.</summary>
        public static int IndexOf(int biomeX, int biomeY) => biomeY * BiomeWidth + biomeX;

        /// <summary>Inverse of <see cref="IndexOf"/>.</summary>
        public static (int BiomeX, int BiomeY) BiomeOf(int index) =>
            (index % BiomeWidth, index / BiomeWidth);

        public int BiomeX => BiomeOf(Index).BiomeX;
        public int BiomeY => BiomeOf(Index).BiomeY;

        /// <summary>"地表 区块(7,4)" — what the player sees in game.</summary>
        public string BiomeLabel => $"{(IsUnderground ? "地下" : "地表")} 区块({BiomeX},{BiomeY})";

        /// <summary>
        /// World coordinate of a cell's centre. Verified against all 17 teleport
        /// stones in the live save with zero error.
        /// </summary>
        public static (long X, long Y) WorldOf(int biomeX, int biomeY, int detailX, int detailY)
            => (128L * (biomeX * 150 + detailX) + 64, 128L * (biomeY * 150 + detailY + 1));
    }

    private static readonly Regex ChunkPattern = new(@"GenMap__(Surface|Underground)Chunk_(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// List the chunk modules without parsing any of them. Cheap: names come
    /// from the file list, so this is what the chunk picker binds to.
    /// </summary>
    public static List<(string Module, bool Underground, int Index)> ListChunks(SaveSlot slot)
    {
        var list = new List<(string, bool, int)>();
        foreach (var path in slot.ModuleFiles)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = ChunkPattern.Match(name);
            if (!m.Success) continue;
            list.Add((name, m.Groups[1].Value == "Underground", int.Parse(m.Groups[2].Value)));
        }
        return list.OrderBy(x => x.Item2).ThenBy(x => x.Item3).ToList();
    }

    /// <summary>Read one chunk. Parses only that module, so it stays sub-second.</summary>
    public static ChunkView? ReadChunk(SaveSlot slot, string moduleName)
    {
        var m = slot.LoadModule(moduleName);
        if (m is null) return null;
        m.Parse();

        var idx = m.Properties.FirstOrDefault(p => p.Name == "Index")?.Value is int i ? i : -1;
        var cell = m.Properties.FirstOrDefault(p => p.Name == "Cell");
        if (cell?.Children is null) return null;

        SaveProperty? F(string n) => cell.Children.FirstOrDefault(c => c.Name == n);

        var view = new ChunkView
        {
            ModuleName = moduleName,
            Index = idx,
            TerrainSize = F("TerrainSize")?.Value is int ts ? ts : 0,
            IsUnderground = moduleName.Contains("Underground"),
        };

        // --- container state, keyed by the same "x, y" coordinate ---
        var saveTypes = new Dictionary<string, string>();
        var psd = F("PlaceableItemSaveDatas");
        if (psd?.MapEntries is not null)
        {
            foreach (var e in psd.MapEntries)
            {
                var t = e.ValueProps.FirstOrDefault(v => v.Name == "SaveType")?.Value?.ToString();
                if (!string.IsNullOrEmpty(t)) saveTypes[e.KeyDisplay] = t;
            }
        }

        // --- placeables ---
        var det = F("DetailPlaceItemMapData");
        if (det?.MapEntries is not null)
        {
            foreach (var e in det.MapEntries)
            {
                var idp = e.ValueProps.FirstOrDefault(p => p.Name == "PlaceableItemID");
                if (idp?.Value is not string id) continue;

                int GetInt(string n) => e.ValueProps.FirstOrDefault(p => p.Name == n)?.Value is int v ? v : 0;
                bool GetBool(string n) => e.ValueProps.FirstOrDefault(p => p.Name == n)?.BoolValue ?? false;

                saveTypes.TryGetValue(e.KeyDisplay, out var st);

                view.Objects.Add(new ChunkObject
                {
                    Coord = e.KeyDisplay,
                    X = GetInt("PrimaryDetailX"),
                    Y = GetInt("PrimaryDetailY"),
                    ItemId = id,
                    Occupied = id == "OCCUPIED",
                    PlayerPlaced = GetBool("bPlayerPlace"),
                    Main = GetBool("bMain"),
                    Turn90 = GetBool("bTurn90"),
                    RotateIndex = GetInt("RotateIndex"),
                    IdProp = idp,
                    TurnProp = e.ValueProps.FirstOrDefault(p => p.Name == "bTurn90"),
                    RotateProp = e.ValueProps.FirstOrDefault(p => p.Name == "RotateIndex"),
                    SaveType = st ?? "",
                });
            }
        }

        // --- terrain overrides ---
        var mod = F("ModifiedOptimizedDataByDetailIndex");
        if (mod?.MapEntries is not null)
        {
            foreach (var e in mod.MapEntries)
            {
                int key = int.TryParse(e.KeyDisplay, out var k) ? k : -1;
                var layers = e.ValueProps.FirstOrDefault(p => p.Name == "Layers");
                if (layers?.ArrayElements is null) continue;

                // The array holds one element whose property list repeats the
                // three fields once per layer, so walk it in groups of three.
                foreach (var el in layers.ArrayElements)
                {
                    int lid = 0, tsi = 0, tii = 0;
                    SaveProperty? lp = null, tsp = null, tip = null;
                    foreach (var p in el.Properties)
                    {
                        switch (p.Name)
                        {
                            case "LayerID":
                                if (lp is not null)
                                {
                                    view.Terrain.Add(new TerrainEdit
                                    {
                                        Key = e.KeyDisplay, DetailIndex = key,
                                        LayerId = lid, TileSetIndex = tsi, TileImageIndex = tii,
                                        LayerIdProp = lp, TileSetProp = tsp, TileImageProp = tip,
                                    });
                                    tsi = tii = 0; tsp = tip = null;
                                }
                                lid = p.Value is byte b1 ? b1 : 0;
                                lp = p;
                                break;
                            case "TileSetIndex":
                                tsi = p.Value is byte b2 ? b2 : 0;
                                tsp = p;
                                break;
                            case "TileImageIndex":
                                tii = p.Value is byte b3 ? b3 : 0;
                                tip = p;
                                break;
                        }
                    }
                    if (lp is not null)
                        view.Terrain.Add(new TerrainEdit
                        {
                            Key = e.KeyDisplay, DetailIndex = key,
                            LayerId = lid, TileSetIndex = tsi, TileImageIndex = tii,
                            LayerIdProp = lp, TileSetProp = tsp, TileImageProp = tip,
                        });
                }
            }
        }

        var empty = F("EmptyDetailCoords");
        if (empty?.Value is System.Collections.IList el2) view.EmptyDetailCount = el2.Count;

        return view;
    }

    /// <summary>Rotate a placeable by 90 degrees.</summary>
    public static bool SetTurn90(ChunkObject obj, bool on)
    {
        if (obj.TurnProp is null) return false;
        obj.TurnProp.MarkBool(on);
        return true;
    }

    /// <summary>Set a placeable's rotation index (0-3).</summary>
    public static bool SetRotateIndex(ChunkObject obj, int value)
    {
        if (obj.RotateProp is null) return false;
        var bytes = ValueEditor.Encode(obj.RotateProp, value.ToString(), out _);
        if (bytes is null) return false;
        obj.RotateProp.MarkDirty(bytes);
        return true;
    }

    /// <summary>Change the terrain tile of one layer.</summary>
    public static bool SetTerrainTile(TerrainEdit t, int tileSetIndex, int tileImageIndex)
    {
        if (t.TileSetProp is null || t.TileImageProp is null) return false;
        var a = ValueEditor.Encode(t.TileSetProp, tileSetIndex.ToString(), out _);
        var b = ValueEditor.Encode(t.TileImageProp, tileImageIndex.ToString(), out _);
        if (a is null || b is null) return false;
        t.TileSetProp.MarkDirty(a);
        t.TileImageProp.MarkDirty(b);
        return true;
    }
}
