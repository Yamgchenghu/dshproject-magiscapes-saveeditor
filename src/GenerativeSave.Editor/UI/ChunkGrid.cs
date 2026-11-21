using System.Drawing.Drawing2D;
using GenerativeSave.Core.Editing;
using GenerativeSave.Core.Library;

namespace GenerativeSave.Editor.UI;

/// <summary>
/// A clickable picture of one chunk's reshaped terrain.
///
/// The game's tile artwork and its tileset-name table both live inside packed
/// .ucas archives with hashed paths, so the save does not tell us "this is a
/// stone wall". What it does record per reshaped cell is the layer, tileset
/// index and tile index. So each cell is painted by a semantic palette that
/// keeps related tilesets in one colour family (walls grey-brown, floors pale,
/// water blue, plants green), and every cell carries its tile id so the exact
/// tile is never hidden behind the colour.
///
/// Only reshaped cells are drawn. Layers 9 and 10 cover nearly every cell with
/// the same tile and carry no information, so they are dropped from the picture.
/// </summary>
internal sealed class ChunkGrid : Control
{

    /// <summary>
    /// Colour lookups are pure, and painting asks for the same few thousand
    /// (layer, tile) pairs on every frame. Caching them keeps a repaint from
    /// re-deriving colours for 22 500 cells.
    /// </summary>
    /// <summary>
    /// Keyed by (tileset, layer, tile image). Tile image alone is NOT unique:
    /// in one chunk layer 8 / image 5 is used by both tileset 7 (water) and
    /// tileset 69 (pasture grass), so a key without the tileset let whichever
    /// colour was cached first win and painted water green.
    /// </summary>
    private readonly Dictionary<(int TileSet, int Layer, int Tile), Color> _colorCache = new(4096);

    /// <summary>
    /// Colour for one tile: the user's own colour if they taught one, otherwise
    /// a family default chosen by layer. The save proves the layer, never the
    /// material, so an untaught tileset gets a neutral colour rather than a
    /// confident-looking guess.
    /// </summary>
    private static Color ColorFor(ChunkFacade.TerrainEdit t, TilesetPalette palette)
    {
        var e = palette.Get(t.TileSetIndex);
        if (e is not null && TilesetPalette.ParseColor(e.Color) is { } rgb)
            return Color.FromArgb(rgb.R, rgb.G, rgb.B);
        return LayerColor(t.LayerId, t.TileImageIndex);
    }

    /// <summary>
    /// Layer is the only structural fact the save guarantees: 5 = decoration,
    /// 7 = ground cover, 8 = structures, 9/10 = base. Shades within a layer keep
    /// neighbouring tiles distinguishable.
    /// </summary>
    private static Color LayerColor(int layerId, int tileIndex) => layerId switch
    {
        5 => Shade(Color.FromArgb(126, 170, 112), tileIndex),   // 装饰/植被
        7 => Shade(Color.FromArgb(186, 156, 118), tileIndex),   // 地面/木
        8 => Shade(Color.FromArgb(138, 134, 128), tileIndex),   // 墙体/石
        // Layers 9/10 are the base tile of a changed cell, not "no data": they
        // are what a re-paved floor or a dug-out area shows up as. Give them a
        // real ground tone instead of near-white, which read as blank.
        // 9/10 are the base ground the game records for a cell. Kept visibly
        // distinct from 7/8 so that, when 全部层 is on, you can tell a base
        // record from a floor you laid down.
        9 => Shade(Color.FromArgb(150, 176, 132), tileIndex),   // 基础层（地面，绿）
        10 => Shade(Color.FromArgb(176, 166, 146), tileIndex),  // 基础层（基底，土）
        _ => Shade(Color.FromArgb(160, 160, 168), tileIndex),
    };

    /// <summary>
    /// Blend a colour 78% toward white. Used for cells that only carry a base
    /// layer, so original terrain reads as background instead of as the
    /// player's work.
    /// </summary>
    internal static Color Fade(Color c)
    {
        const double keep = 0.22;
        int Mix(int v) => (int)Math.Round(255 - (255 - v) * keep);
        return Color.FromArgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    /// <summary>
    /// Colour for a placeable. Only ids the player has actually told us about get
    /// a specific material; everything else gets one neutral tone rather than a
    /// confident-looking guess. Containers keep their own colour because that is
    /// what a player scans the map for.
    /// </summary>
    internal static Color ObjectColor(ChunkFacade.ChunkObject o)
    {
        if (o.IsBox) return Color.FromArgb(198, 92, 18);        // 箱子：橙
        if (o.IsDoor) return Color.FromArgb(92, 62, 40);        // 门：深棕
        return o.ItemId switch
        {
            "4010046" => StoneGrey,                             // 石墙
            "4010045" => Color.FromArgb(150, 108, 68),          // 木墙
            "4010023" => Color.FromArgb(168, 128, 84),          // 木栅栏
            _ => Color.FromArgb(176, 172, 166),                 // 其它放置物
        };
    }

    /// <summary>Stone grey for walls, doors and other built objects.</summary>
    internal static readonly Color StoneGrey = Color.FromArgb(128, 126, 122);

    internal static readonly Color WallColor = Color.FromArgb(122, 118, 112);

    /// <summary>
    /// Border thickness for an object marker, scaled to the cell so a small cell
    /// still shows a visible edge and a large one does not get swallowed.
    /// Returns 0 when the cell is too small to draw anything meaningful.
    /// </summary>
    internal static int InsetFor(int cell) => cell switch
    {
        < 6 => 0,      // 整体 1–6 px：不显示
        < 12 => 1,     // 整体 6–12 px：1 px
        < 18 => 2,     // 整体 12–18 px：2 px
        < 24 => 3,     // 整体 18–24 px：3 px
        < 30 => 4,     // 整体 24–30 px：4 px
        _ => 5,        // 整体 ≥30 px：5 px
    };

    /// <summary>Nudge lightness by tile index so neighbouring tiles stay distinct.</summary>
    private static Color Shade(Color c, int tileIndex)
    {
        int step = (tileIndex % 7) - 3;         // -3..+3
        int d = step * 9;
        int Clamp(int v) => Math.Clamp(v + d, 0, 255);
        return Color.FromArgb(Clamp(c.R), Clamp(c.G), Clamp(c.B));
    }

    /// <summary>
    /// Paint order for a layer id, lowest first.
    ///
    /// Layer numbers are NOT draw order. 9 and 10 carry the largest ids but they
    /// are the <em>base ground</em> the game records for a cell, so they belong at
    /// the bottom: sorting by id alone put the base record on top and buried the
    /// floors, fields and water the player actually laid down. Everything else
    /// (4 decoration, 5 decoration, 7 ground cover, 8 structures) stacks above it
    /// in ascending id order.
    /// </summary>
    internal static int DrawPriority(int layer) => layer switch
    {
        10 => 0,
        9 => 1,
        _ => layer + 10,        // 4 -> 14, 5 -> 15, 7 -> 17, 8 -> 18
    };

    public sealed class Cell
    {
        public int X;
        public int Y;
        /// <summary>Every terrain record on this cell, all layers.</summary>
        public List<ChunkFacade.TerrainEdit> All = new();
        /// <summary>
        /// The layers that describe what the player laid down: decoration (5),
        /// ground cover (7) and structures (8). Base layers 9/10 are kept in
        /// <see cref="All"/> and drawn underneath, never on top.
        /// </summary>
        public List<ChunkFacade.TerrainEdit> Edits = new();
        /// <summary>The object standing here, if any.</summary>
        public ChunkFacade.ChunkObject? Obj;

        public bool HasEdit => Edits.Count > 0;

        /// <summary>Topmost layer of any kind, i.e. what covers everything else.</summary>
        public ChunkFacade.TerrainEdit? Top => Edits.Count == 0
            ? null
            : Edits.OrderByDescending(t => DrawPriority(t.LayerId)).First();

        /// <summary>True when a base layer (9/10) recorded this cell.</summary>
        public bool HasBase => All.Count > Edits.Count;
        public bool IsPlaceholder => Obj is not null && Obj.Occupied;
        public bool IsEmpty => Obj is null && !HasEdit;
    }

    private Cell[,]? _cells;
    private int _fullSize = 150;
    private int _w = 1, _h = 1;
    private int _originX, _originY;
    private int _cell = 10;
    private int _hoverX = -1, _hoverY = -1;
    private int _selX = -1, _selY = -1;
    private int _painted;
    private TilesetPalette _palette = TilesetPalette.Load();

    /// <summary>
    /// Outline of the player's camp, in this chunk's local cell coordinates.
    /// A camp is 79x79 and only a fraction of it is ever re-paved, so without
    /// the outline the reshaped area reads as random patches with no owner.
    /// </summary>
    public Rectangle? CampOutline { get; set; }

    /// <summary>
    /// Crop the view to this rectangle instead of to all edits in the chunk.
    /// Must be set before <see cref="Load"/>, because cropping happens there.
    /// </summary>
    public Rectangle? FocusCamp { get; set; }

    /// <summary>
    /// Treat every layer as editable terrain, including the base records 9/10.
    ///
    /// By default 9/10 are background: they mark "this cell was written down",
    /// and on the live save they cover far more cells than the player's actual
    /// work, so painting them on top buries the floors and fields. But they are
    /// real data the user asked to be able to read and edit, so this switch
    /// promotes them to ordinary layers: full colour, clickable, editable.
    /// </summary>
    public bool ShowAllLayers { get; set; }

    /// <summary>
    /// Show the whole chunk instead of cropping to the cells that carry data.
    /// Cropping makes the picture fill the viewport, which is what you want when
    /// hunting a specific patch, but it also means you can never see the chunk
    /// as a whole. With this on, the grid is always TerrainSize x TerrainSize.
    /// </summary>
    public bool WholeChunk { get; set; }

    /// <summary>Tileset colours; assignable so tests can load one explicitly.</summary>
    internal TilesetPalette Palette
    {
        get => _palette;
        set { _palette = value; _colorCache.Clear(); Invalidate(); }
    }

    public event Action<Cell>? CellClicked;
    public event Action<Cell>? CellDoubleClicked;

    public ChunkGrid()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        Font = Theme.Base;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    /// <summary>Terrain size of the whole chunk (not the cropped view).</summary>
    public int GridSize => _fullSize;

    /// <summary>Width of the cropped view, in cells.</summary>
    public int CroppedWidth => _w;

    /// <summary>Height of the cropped view, in cells.</summary>
    public int CroppedHeight => _h;
    /// <summary>How many cells carry at least one non-base layer.</summary>
    public int PaintedCells => _painted;

    /// <summary>
    /// The cell at chunk-local coordinates, or null when it is outside the
    /// current crop. Chunk coordinates, not view coordinates: with cropping on
    /// those differ by the origin.
    /// </summary>
    public Cell? CellAt(int x, int y)
    {
        if (_cells is null) return null;
        int vx = x - _originX, vy = y - _originY;
        if (vx < 0 || vy < 0 || vx >= _w || vy >= _h) return null;
        return _cells[vx, vy];
    }

    /// <summary>
    /// Bounding box of the painted cells, in grid-local cell coordinates.
    /// Empty when nothing is painted. Used to scroll the viewport onto the
    /// player's work instead of leaving it at the top of a tall picture.
    /// </summary>
    public Rectangle ContentBounds()
    {
        if (_cells is null) return Rectangle.Empty;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
            {
                if (!_cells[x, y].HasEdit) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        if (maxX < 0) return Rectangle.Empty;
        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>
    /// Size of the content area in cells, computed without building a grid. Used
    /// to choose a cell size that makes the picture fill the viewport.
    /// </summary>
    public static (int Width, int Height) ContentSize(ChunkFacade.ChunkView view)
    {
        int full = Math.Max(1, view.TerrainSize);
        int minX = full, minY = full, maxX = -1, maxY = -1;

        void Hit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= full || y >= full) return;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var t in view.Terrain)
            if (!BaseLayers.Contains(t.LayerId))
                Hit(t.DetailIndex % full, t.DetailIndex / full);

        // Only real objects count: a placeholder ("OCCUPIED") is a blocked cell,
        // and there are thousands of them spread over the whole chunk, which
        // would make the content box look like the entire map.
        foreach (var o in view.Objects)
            if (!o.Occupied && !o.PlayerPlaced)
                Hit(o.X, o.Y);

        if (maxX < 0) return (full, full);
        return (maxX - minX + 1 + 4, maxY - minY + 1 + 4);
    }

    /// <summary>
    /// Layers 9 and 10 are the base ground of a cell, not an edit to it. Proven
    /// on the live save: inside a 6 241-cell camp footprint all 716 recorded
    /// cells had layer 9 equal to that chunk's dominant ground tileset (2-*) —
    /// zero had been re-paved. Layer 9/10 records merely mark "this cell has
    /// been loaded/saved once", so painting them buried the real work: the same
    /// footprint holds only 121 cells of layer 7/8, and those are the floors,
    /// fields, water and pasture the player actually laid down.
    /// </summary>
    private static readonly HashSet<int> BaseLayers = new() { 9, 10 };

    /// <summary>Load a chunk. Only cells with real terrain edits become visible.</summary>
    public void Load(ChunkFacade.ChunkView view, int cellPixels = 10)
    {
        int full = Math.Max(1, view.TerrainSize);
        _cell = Math.Clamp(cellPixels, 3, 32);

        // Build the full grid first, then crop to the area that has content, so
        // the picture fills the viewport instead of showing a large white margin.
        var fullGrid = new Cell[full, full];
        for (int y = 0; y < full; y++)
            for (int x = 0; x < full; x++)
                fullGrid[x, y] = new Cell { X = x, Y = y };

        foreach (var t in view.Terrain)
        {
            int x = t.DetailIndex % full, y = t.DetailIndex / full;
            if (x < 0 || y < 0 || x >= full || y >= full) continue;
            var c = fullGrid[x, y];
            c.All.Add(t);
            // With ShowAllLayers every record is an edit, so 9/10 stop being
            // faded background and become pickable, editable terrain.
            if (ShowAllLayers || !BaseLayers.Contains(t.LayerId)) c.Edits.Add(t);
        }

        foreach (var o in view.Objects)
        {
            if (o.X < 0 || o.Y < 0 || o.X >= full || o.Y >= full) continue;
            var c = fullGrid[o.X, o.Y];
            if (c.Obj is null || (c.Obj.Occupied && !o.Occupied)) c.Obj = o;
        }

        // ---- crop to content ----
        int minX = full, minY = full, maxX = -1, maxY = -1;

        if (WholeChunk)
        {
            // No cropping at all: the user asked to see the entire chunk.
            minX = 0; minY = 0; maxX = full - 1; maxY = full - 1;
        }
        else
        {
            for (int y = 0; y < full; y++)
                for (int x = 0; x < full; x++)
                {
                    var c = fullGrid[x, y];
                    if (!c.HasEdit && (c.Obj is null || c.Obj.Occupied || c.Obj.PlayerPlaced)) continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
        }

        if (maxX < 0)
        {
            // Nothing to draw: keep a small empty grid so the control still paints.
            _cells = new Cell[1, 1];
            _cells[0, 0] = new Cell { X = 0, Y = 0 };
            _originX = _originY = 0;
            _w = _h = 1;
            _fullSize = full;
            Size = new Size(_cell + 1, _cell + 1);
            MinimumSize = Size;
            _painted = 0;
            Invalidate();
            return;
        }

        const int margin = 2;
        minX = Math.Max(0, minX - margin);
        minY = Math.Max(0, minY - margin);
        maxX = Math.Min(full - 1, maxX + margin);
        maxY = Math.Min(full - 1, maxY + margin);

        // When the caller asked to focus the camp, crop to the camp instead of
        // to every edit in the chunk. A 150x150 chunk shrunk into the viewport
        // makes a 79x79 camp unreadable, and the camp is what the player came
        // to look at. Still, a 79x79 camp at 14 px is wider than the viewport,
        // so prefer the camp's *content* when it fits inside the camp — that is
        // where the player's floors and fields actually are.
        if (!WholeChunk && FocusCamp is { } fc)
        {
            // Crop to what the player changed INSIDE the camp. The chunk also
            // holds unrelated work far outside it (776 cells in the live save),
            // and letting that widen the view is what made the camp look empty.
            int cx0 = int.MaxValue, cy0 = int.MaxValue, cx1 = -1, cy1 = -1;
            for (int y = Math.Max(0, fc.Top); y < Math.Min(full, fc.Bottom); y++)
                for (int x = Math.Max(0, fc.Left); x < Math.Min(full, fc.Right); x++)
                {
                    if (!fullGrid[x, y].HasEdit) continue;
                    if (x < cx0) cx0 = x;
                    if (y < cy0) cy0 = y;
                    if (x > cx1) cx1 = x;
                    if (y > cy1) cy1 = y;
                }

            if (cx1 < 0)
            {
                // Camp recorded but nothing re-paved inside it: show the camp.
                cx0 = Math.Clamp(fc.Left, 0, full - 1);
                cy0 = Math.Clamp(fc.Top, 0, full - 1);
                cx1 = Math.Clamp(fc.Right - 1, 0, full - 1);
                cy1 = Math.Clamp(fc.Bottom - 1, 0, full - 1);
            }

            int pad = 8;
            minX = Math.Max(0, cx0 - pad);
            minY = Math.Max(0, cy0 - pad);
            maxX = Math.Min(full - 1, cx1 + pad);
            maxY = Math.Min(full - 1, cy1 + pad);
        }

        _w = maxX - minX + 1;
        _h = maxY - minY + 1;
        _originX = minX;
        _originY = minY;
        _fullSize = full;

        _cells = new Cell[_w, _h];
        _painted = 0;
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
            {
                var src = fullGrid[minX + x, minY + y];
                _cells[x, y] = src;
                if (src.HasEdit) _painted++;
            }

        Size = new Size(_w * _cell + 1, _h * _cell + 1);
        MinimumSize = Size;

        _selX = _selY = -1;
        Invalidate();
    }

    /// <summary>The colour a tileset would get without a user override.</summary>
    public static Color DefaultColorFor(int tileSet, int layerId) => LayerColor(layerId, 0);

    /// <summary>
    /// Cell size that makes the whole chunk fit a viewport. Both dimensions
    /// matter: a 150x150 chunk at 8 px is 1200 px, which is wider than most
    /// windows and taller than the visible area, so the interesting part ends up
    /// off-screen.
    /// </summary>
    public static int FitCellSize(int terrainSize, int availWidth, int availHeight)
    {
        if (terrainSize <= 0) return 8;
        // Leave a little slack for the frame and the scrollbars.
        int byW = Math.Max(1, availWidth - 8) / terrainSize;
        int byH = Math.Max(1, availHeight - 8) / terrainSize;
        // Never shrink below 5 px: at 3 px a cell is smaller than the cursor and
        // you cannot click the tile you mean. The viewport scrolls instead.
        return Math.Clamp(Math.Min(byW, byH), 5, 32);
    }

    private (int X, int Y) Hit(Point p) => (p.X / _cell, p.Y / _cell);

    private bool InBounds(int x, int y) => _cells is not null && x >= 0 && y >= 0 && x < _w && y < _h;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (x, y) = Hit(e.Location);
        if (x == _hoverX && y == _hoverY) return;
        _hoverX = x; _hoverY = y;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = _hoverY = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var (x, y) = Hit(e.Location);
        if (!InBounds(x, y)) return;
        _selX = x; _selY = y;
        Invalidate();

        if (e.Button != MouseButtons.Left) return;
        var cell = _cells![x, y];
        if (e.Clicks >= 2) CellDoubleClicked?.Invoke(cell);
        else CellClicked?.Invoke(cell);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        DrawTo(e.Graphics);
    }

    /// <summary>
    /// Paint the grid onto any Graphics. Split out from OnPaint so the paint path
    /// can be driven offscreen by a test: this code is where the map page used to
    /// die, and a crash here leaves no managed exception to log.
    /// </summary>
    internal void DrawTo(Graphics g)
    {
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.None;

        // A visible frame proves the control is laid out and painting at all,
        // which is otherwise impossible to tell from a blank grid.
        using (var frame = new Pen(Color.FromArgb(214, 219, 228)))
            g.DrawRectangle(frame, 0, 0, Width - 1, Height - 1);

        if (_cells is null)
        {
            using var f = new SolidBrush(Theme.Muted);
            g.DrawString("选一个区块后这里会显示它改过的地形。", Font, f, new PointF(12, 12));
            return;
        }

        int px = _h * _cell;

        // ---- terrain, all layers ----
        // Every recorded layer is drawn, bottom-up: base 9/10 first, then 5, 7, 8.
        // Cells that only carry a base layer still get that layer's colour, so a
        // re-paved floor is visible instead of reading as blank.
        // One brush per colour instead of one per cell: the old code allocated a
        // SolidBrush for every one of the 22 500 cells on every paint, which on a
        // dense chunk exhausted GDI handles and killed the process.
        using var gridPen = new Pen(Color.FromArgb(236, 240, 246));
        var byColor = new Dictionary<Color, List<Rectangle>>(64);
        var placeholders = new List<Rectangle>();
        var whiteCells = new List<Rectangle>(256);

        for (int y = 0; y < _h; y++)
        {
            for (int x = 0; x < _w; x++)
            {
                var c = _cells[x, y];
                var rect = new Rectangle(x * _cell, y * _cell, _cell, _cell);

                if (c.IsPlaceholder)
                {
                    // Placeholders are blocked cells, not terrain: solid black.
                    placeholders.Add(rect);
                    continue;
                }

                // Pick the layer to paint: the topmost surface layer, else the
                // base layer when that is all the cell has.
                var top = c.Top ?? (c.All.Count > 0
                    ? c.All.OrderByDescending(t => t.LayerId).First()
                    : null);

                if (top is null)
                {
                    whiteCells.Add(rect);
                    continue;
                }

                var key = (top.TileSetIndex, top.LayerId, top.TileImageIndex);
                if (!_colorCache.TryGetValue(key, out var col))
                    _colorCache[key] = col = ColorFor(top, _palette);

                // A cell whose only record is a base layer is original terrain the
                // game happened to write down, not work the player did. Fade it
                // toward white so it reads as background and the surfaces the
                // player laid down stay the thing you actually see.
                // In 全部层 mode nothing is background: every layer is data the
                // user is inspecting, so fading would hide exactly what they
                // asked to see.
                if (!ShowAllLayers && c.Edits.Count == 0)
                    col = Fade(col);

                if (!byColor.TryGetValue(col, out var list))
                    byColor[col] = list = new List<Rectangle>(256);
                list.Add(rect);
            }
        }

        if (placeholders.Count > 0)
            g.FillRectangles(Brushes.Black, placeholders.ToArray());

        // Untouched cells first, so coloured cells always sit on top of them.
        if (whiteCells.Count > 0)
            g.FillRectangles(Brushes.White, whiteCells.ToArray());

        foreach (var (col, rects) in byColor)
        {
            using var brush = new SolidBrush(col);
            g.FillRectangles(brush, rects.ToArray());
        }

        // ---- grid, only when cells are big enough to read ----
        if (_cell >= 8)
        {
            using var pen = new Pen(Color.FromArgb(238, 241, 246));
            for (int i = 0; i <= _w; i++)
                g.DrawLine(pen, i * _cell, 0, i * _cell, px);
            for (int i = 0; i <= _h; i++)
                g.DrawLine(pen, 0, i * _cell, px, i * _cell);
        }

        // ---- tile labels, only when there is room ----
        if (_cell >= 26)
        {
            using var text = new SolidBrush(Color.FromArgb(40, 40, 40));
            using var small = new Font("Microsoft YaHei UI", 6.5f);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                {
                    var top = _cells[x, y].Top;
                    if (top is null) continue;
                    var rect = new RectangleF(x * _cell, y * _cell, _cell, _cell);
                    g.DrawString(top.TileImageIndex.ToString(), small, text, rect, fmt);
                }
        }

        // ---- objects on top of the terrain ----
        // Doors, walls and furniture used to be invisible unless they happened
        // to be a container, so a walled enclosure read as empty ground. Every
        // real placeable is now painted over the terrain in its category colour,
        // inset from all four edges so neighbours stay separate instead of
        // merging into one slab. The inset scales with the cell: a 6 px cell
        // shows nothing at all, a 32 px cell gets a 5 px margin.
        int inset = InsetFor(_cell);
        if (inset > 0)
        {
            var objFill = new Dictionary<Color, List<Rectangle>>(16);
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                {
                    var o = _cells[x, y].Obj;
                    if (o is null || o.Occupied) continue;

                    var col = ObjectColor(o);
                    var r = new Rectangle(
                        x * _cell + inset, y * _cell + inset,
                        Math.Max(1, _cell - inset * 2), Math.Max(1, _cell - inset * 2));
                    if (!objFill.TryGetValue(col, out var list))
                        objFill[col] = list = new List<Rectangle>(64);
                    list.Add(r);
                }

            foreach (var (col, rects) in objFill)
            {
                using var brush = new SolidBrush(col);
                g.FillRectangles(brush, rects.ToArray());
            }
        }

        // ---- hover + selection ----
        if (InBounds(_hoverX, _hoverY))
        {
            using var pen = new Pen(Color.FromArgb(110, 110, 110));
            g.DrawRectangle(pen, _hoverX * _cell, _hoverY * _cell, _cell - 1, _cell - 1);
        }
        if (InBounds(_selX, _selY))
        {
            using var pen = new Pen(Theme.Accent, 2);
            g.DrawRectangle(pen, _selX * _cell, _selY * _cell, _cell - 1, _cell - 1);
        }

        // ---- camp outline ----
        // Drawn last so it sits on top: the player needs to see which patches
        // belong to the camp and which are leftover work elsewhere in the chunk.
        if (CampOutline is { } camp)
        {
            int cx = (camp.X - _originX) * _cell;
            int cy = (camp.Y - _originY) * _cell;
            var r = new Rectangle(cx, cy, camp.Width * _cell, camp.Height * _cell);
            // In 整块显示 the cells are tiny and the outline sits on top of a
            // solid terrain colour, so a 2 px dash disappears. Thicken it.
            int w = WholeChunk ? 3 : 2;
            using var pen = new Pen(Color.FromArgb(206, 92, 18), w) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(pen, r);
        }

        // ---- content marker in whole-chunk mode ----
        // A 150x150 chunk at fit-to-view is 3 px per cell, and a chunk whose data
        // covers 162 cells is then a speck in the corner: technically correct and
        // useless. Ring the recorded area so the eye finds it immediately.
        if (WholeChunk && _painted > 0)
        {
            var cb = ContentBounds();
            // Only ring it when the data occupies a small part of the picture;
            // on a chunk that is full of edits the frame would just be the border.
            if (!cb.IsEmpty && (cb.Width < _w * 0.85 || cb.Height < _h * 0.85))
            {
                var r = new Rectangle(cb.X * _cell - 2, cb.Y * _cell - 2,
                                      cb.Width * _cell + 4, cb.Height * _cell + 4);
                using var pen = new Pen(Theme.Accent, 2);
                g.DrawRectangle(pen, r);
            }
        }
    }

    /// <summary>
    /// Legend entries for the tilesets actually present. Named tilesets are
    /// listed one per row because the user chose those names; unnamed ones share
    /// a single "按图层上色" row, so the legend stays one line instead of twenty.
    /// </summary>
    public static List<(Color Color, string Text)> LegendFor(
        IEnumerable<ChunkFacade.TerrainEdit> terrain, TilesetPalette palette,
        bool showAllLayers = false)
    {
        var list = new List<(Color, string)>();
        var seen = new HashSet<int>();
        bool anyUnnamed = false;
        bool anyEdit = false;

        foreach (var t in terrain)
        {
            anyEdit = true;
            if (!seen.Add(t.TileSetIndex)) continue;
            var e = palette.Get(t.TileSetIndex);
            if (e is not null && e.Name.Length > 0)
                list.Add((ColorFor(t, palette), e.Name));
            else
                anyUnnamed = true;
        }

        if (anyUnnamed)
        {
            // One swatch per layer that is actually unnamed in this chunk.
            var layers = terrain
                                .Where(t => palette.Get(t.TileSetIndex) is not { Name.Length: > 0 })
                                .Select(t => t.LayerId)
                                .Distinct()
                                .OrderBy(l => l);
            foreach (int layer in layers)
            {
                // Layers 9/10 are the faded base records, already described by
                // their own legend row; listing them again as "未命名层 9" made
                // the legend look like there was an unnamed surface layer.
                // Unless 全部层 is on, in which case they ARE editable layers.
                // 9/10 always get their own legend row below, so never list them
                // again here — with 显示全部层 on, that produced both
                // "未命名层 9" and "层9（基底，可编辑）" side by side.
                if (layer == 9 || layer == 10) continue;
                var name = layer switch
                {
                    8 => "未命名墙体（灰）",
                    7 => "未命名地面（棕）",
                    5 => "未命名装饰（绿）",
                    _ => $"未命名层 {layer}",
                };
                list.Add((LayerColor(layer, 0), name));
            }
        }

        if (showAllLayers)
        {
            // In 全部层 mode 9/10 are ordinary editable layers, so label them as
            // such instead of calling them background.
            foreach (int layer in terrain.Select(t => t.LayerId).Distinct().OrderBy(l => l))
                if (layer is 9 or 10 && !list.Any(x => x.Item2.Contains($"层{layer}")))
                    list.Add((LayerColor(layer, 0), $"层{layer}（基底，可编辑）"));
        }
        else if (terrain.Any(t => t.LayerId == 9 || t.LayerId == 10))
            list.Add((Color.FromArgb(232, 232, 230), "基底记录（淡灰，非你的改动）"));
        list.Add((ObjectColor(new ChunkFacade.ChunkObject { ItemId = "4010046" }), "石墙/门（灰框）"));
        list.Add((Color.FromArgb(198, 92, 18), "宝箱（橙实心）"));
        if (anyEdit) list.Add((Color.Black, "占位格（黑）"));
        return list;
    }

    /// <summary>Human-readable description of one cell, for the info strip.</summary>
    public static string Describe(Cell? c)
    {
        if (c is null) return "";
        var parts = new List<string> { $"格 ({c.X}, {c.Y})" };

        if (c.IsPlaceholder) parts.Add("占位格（不可通行）");
        else if (c.Obj is not null)
        {
            var kind = c.Obj.IsBox ? "宝箱" : c.Obj.IsDoor ? "门" : c.Obj.IsBonfire ? "篝火" : "放置物";
            parts.Add($"{kind} {c.Obj.ItemId}");
        }

        if (c.HasBase && !c.HasEdit)
            parts.Add("只有基底记录（这一格被游戏记录过，但你没改过地皮）");

        if (c.All.Count > 0)
        {
            // List every layer present, top-down, naming the tilesets the user
            // has taught and showing the raw id for the rest. A cell therefore
            // reads as "木地板（8-5）、层9 图块 2-5" instead of hiding the base
            // record, which is data the user explicitly wants to see and edit.
            var pal = TilesetPalette.Load();
            var tiles = c.All.OrderByDescending(t => t.LayerId).Select(t =>
            {
                var n = pal.Get(t.TileSetIndex)?.Name ?? "";
                var body = n.Length > 0
                    ? $"{n}（{t.TileSetIndex}-{t.TileImageIndex}）"
                    : $"层{t.LayerId} 图块 {t.TileSetIndex}-{t.TileImageIndex}";
                return $"{body}[层{t.LayerId}]";
            });
            parts.Add(string.Join("、", tiles));
        }
        else
        {
            parts.Add("存档没有记录这一格");
        }

        return string.Join(" · ", parts);
    }
}
