using System.Text.Json;
using System.Text.Json.Serialization;

namespace GenerativeSave.Core.Library;

/// <summary>
/// A user-taught name and colour for one tileset.
///
/// The game keeps its tileset-name table inside packed .ucas archives with
/// hashed paths, so the save only exposes a number. Rather than invent a
/// mapping and present it as fact, the editor starts from the one thing the
/// save DOES prove — which layer a tileset belongs to — and lets the user name
/// tilesets themselves ("78 是石墙") once they have looked at the game. Those
/// names are stored here and apply to every save.
/// </summary>
public sealed class TilesetPalette
{
    public sealed class Entry
    {
        /// <summary>Tileset index as it appears in the save.</summary>
        [JsonPropertyName("tileSet")] public int TileSet { get; set; }
        /// <summary>User-given name, e.g. "石墙". Empty means unnamed.</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        /// <summary>User-given colour as "#RRGGBB". Empty means use the layer default.</summary>
        [JsonPropertyName("color")] public string Color { get; set; } = "";
    }

    private sealed class PaletteFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "GenerativeSaveEditor");

    public static string FilePath => Path.Combine(Dir, "tilesets.json");

    private readonly Dictionary<int, Entry> _map = new();

    public static TilesetPalette Load()
    {
        var p = new TilesetPalette();
        try
        {
            if (!File.Exists(FilePath)) return p;
            var f = JsonSerializer.Deserialize<PaletteFile>(File.ReadAllText(FilePath), JsonOptions);
            if (f?.Entries is null) return p;
            foreach (var e in f.Entries) p._map[e.TileSet] = e;
        }
        catch
        {
            // A corrupt palette must never stop the editor from opening.
        }
        return p;
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var f = new PaletteFile { Entries = _map.Values.OrderBy(e => e.TileSet).ToList() };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(f, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Entry? Get(int tileSet) => _map.TryGetValue(tileSet, out var e) ? e : null;

    public string NameOf(int tileSet) => Get(tileSet)?.Name ?? "";

    /// <summary>Teach a name and/or colour for one tileset. Empty strings clear them.</summary>
    public void Set(int tileSet, string name, string color)
    {
        name = (name ?? "").Trim();
        color = (color ?? "").Trim();
        if (name.Length == 0 && color.Length == 0) _map.Remove(tileSet);
        else _map[tileSet] = new Entry { TileSet = tileSet, Name = name, Color = color };
    }

    /// <summary>Parse "#RRGGBB" or "RRGGBB" into RGB components; null when invalid.</summary>
    public static (int R, int G, int B)? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6) return null;
        if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v)) return null;
        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    /// <summary>Format RGB components as "#RRGGBB".</summary>
    public static string ToHex(int r, int g, int b) => $"#{r:X2}{g:X2}{b:X2}";
}
