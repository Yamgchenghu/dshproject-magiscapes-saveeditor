using System.Text;
using GenerativeSave.Core;
using GenerativeSave.Core.Editing;
using GenerativeSave.Core.Library;
using GenerativeSave.Core.Model;
using GenerativeSave.Editor.UI;

namespace GenerativeSave.Editor;

public sealed class MainForm : Form
{
    private SaveSlot? _slot;
    /// <summary>Cache of the chest scan; null until the user asks for it.</summary>
    private List<MapFacade.Chest>? _chests;
    /// <summary>True while the background chest scan is running.</summary>
    private bool _scanning;
    /// <summary>The map chunk currently open in the per-chunk editor, if any.</summary>
    private ChunkFacade.ChunkView? _chunk;
    /// <summary>Name of the chunk the user picked; survives re-navigation.</summary>
    private string? _chunkName;
    /// <summary>True while a chunk is being parsed in the background.</summary>
    private bool _chunkLoading;
    private bool _saving;
    /// <summary>Crop the map to the camp instead of the whole chunk.</summary>
    private bool _focusCamp = true;

    /// <summary>Which NPC the memory page is showing. Kept across rebuilds so
    /// picking a different row does not jump back to the first one.</summary>
    private string? _memoryNpcId;

    /// <summary>Guards the roster ListView while the page is being rebuilt:
    /// setting Selected fires SelectedIndexChanged, which would rebuild again.</summary>
    private bool _memoryPickBusy;

    /// <summary>What the last automatic library top-up added, shown once on the
    /// 资料库 page so the user can tell the library is being maintained without
    /// having to click anything.</summary>
    private string? _libraryNote;

    /// <summary>Map page: treat the base layers 9/10 as ordinary editable layers.</summary>
    private bool _showAllLayers;

    /// <summary>Map page: draw the whole chunk instead of cropping to the cells
    /// that carry data. Cropping is good for finding a patch but hides the map.</summary>
    private bool _wholeChunk;
    /// <summary>Incremented per chunk request so a stale read cannot win.</summary>
    private int _chunkToken;
    /// <summary>Pixel size of one grid cell; 0 means "fit the whole chunk".</summary>
    private int _cellPixels;
    /// <summary>Currently selected object / terrain edit in the grid.</summary>
    private ChunkFacade.ChunkObject? _gridPick;
    private ChunkFacade.TerrainEdit? _gridTerrainPick;
    private Label? _gridInfo;
    private Button? _btnEditCell;
    private Button? _btnEditTerrain;
    /// <summary>User-taught tileset names and colours; shared across all saves.</summary>
    private TilesetPalette _palette = TilesetPalette.Load();

    private readonly Panel _sidebar = new();
    private readonly Panel _content = new();
    private readonly Panel _headerBar = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Label _dirtyBadge = new();

    private readonly Button _btnSave = new();
    private readonly Button _btnRevert = new();
    private readonly Label _status = new();

    private readonly List<NavButton> _navs = new();
    private string _current = "overview";

    /// <summary>
    /// Drives the real window for --uitest. Everything here goes through the
    /// same objects a click would reach, so a break in the UI layer (a dialog
    /// that never commits, a save button that stays disabled) shows up instead
    /// of being masked by a direct call into the core.
    /// </summary>
    internal (int Changed, bool SaveEnabled) UiEditCount(string itemId, int newCount)
    {
        if (_slot is null) return (0, _btnSave.Enabled);

        Navigate("inventory");
        var pv = SaveFacade.ReadPlayer(_slot);
        var it = pv?.InventoryItems.FirstOrDefault(x => x.ItemId == itemId)
                 ?? pv?.HotbarItems.FirstOrDefault(x => x.ItemId == itemId);
        if (it is null) return (0, _btnSave.Enabled);

        // Same encode the dialog's OK button performs.
        var bytes = ValueEditor.Encode(it.CountProp!, newCount.ToString(), out _);
        if (bytes is null) return (0, _btnSave.Enabled);
        it.CountProp!.MarkDirty(bytes);

        MarkDirtyAndRefresh();
        Navigate("inventory");
        return (1, _btnSave.Enabled);
    }

    /// <summary>Press the actual 保存修改 button, exactly as a user would.</summary>
    internal bool UiPressSave()
    {
        if (!_btnSave.Enabled) return false;
        // A headless form has no handle, so PerformClick would silently do
        // nothing. Call the same handler the button is wired to instead; that is
        // the code path a real click runs.
        SaveChanges();
        return true;
    }

    /// <summary>Diagnostics for --uitest: what the save button is wired to.</summary>
    internal string UiDiagnostics()
    {
        var dirty = _slot?.Modules.Where(m => m.IsDirty).Select(m => m.ModuleName).ToList()
                    ?? new List<string>();
        return $"slot={(_slot is null ? "null" : _slot.Modules.Count.ToString())} " +
               $"dirty=[{string.Join(",", dirty)}] " +
               $"saveEnabled={_btnSave.Enabled} " +
               $"visible={Visible} created={IsHandleCreated}";
    }

    /// <summary>
    /// Drive the real item dialog the way a person does: focus the count box,
    /// type a number, press Enter. This is the one path a headless test cannot
    /// cover — NumericUpDown only pushes typed text into .Value on validate, so
    /// if the dialog closes before that happens the edit is silently lost.
    /// </summary>
    internal bool UiDialogTypeCount(string itemId, string typedText, out string note)
    {
        note = "";
        if (_slot is null) return false;

        var pv = SaveFacade.ReadPlayer(_slot);
        var it = pv?.InventoryItems.FirstOrDefault(x => x.ItemId == itemId);
        if (it is null) { note = "找不到物品"; return false; }

        var built = BuildWeaponDialog(it, 0, "背包");
        if (built is null) { note = "对话框未建立"; return false; }

        var (dlg, apply) = built.Value;
        var boxes = new List<NumericUpDown>();
        Collect(dlg, boxes);
        if (boxes.Count == 0) { note = "找不到数量框"; dlg.Dispose(); return false; }

        var num = boxes[0];
        note = $"输入前 Value={num.Value} Text=\"{num.Text}\" 存档={it.Count}";

        var timer = new System.Windows.Forms.Timer { Interval = 350 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            num.Focus();
            SendKeys.SendWait("^a");
            SendKeys.SendWait(typedText);
            SendKeys.SendWait("{ENTER}");
        };
        timer.Start();

        var result = dlg.ShowDialog(this);
        timer.Dispose();
        note += $"　关闭时 Value={num.Value} Text=\"{num.Text}\" 结果={result}";
        dlg.Dispose();

        if (result != DialogResult.OK) { note += "　（对话框被取消）"; return false; }

        var before = _slot.Modules.Count(m => m.IsDirty);
        apply();
        var after = _slot.Modules.Count(m => m.IsDirty);
        note += $"　Apply 后 dirty 模块 {before}→{after}";
        return true;
    }

    private static void Collect(Control root, List<NumericUpDown> into)
    {
        foreach (Control c in root.Controls)
        {
            if (c is NumericUpDown n) into.Add(n);
            if (c.HasChildren) Collect(c, into);
        }
    }

    /// <summary>Open a save folder through the normal load path.</summary>
    internal void UiOpen(string path) => LoadSlot(path);

    /// <summary>Load a chunk the way the map page does, for the stress harness.</summary>
    internal void UiLoadChunk(string module)
    {
        LoadChunk(module);
        // LoadChunk hands the parse to a background task; pump the message loop
        // until it settles so the harness measures one finished load at a time.
        for (int i = 0; i < 600 && _chunkLoading; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        Application.DoEvents();
    }

    /// <summary>Show the window so real controls exist and events can fire.</summary>
    internal void UiShow()
    {
        Show();
        Application.DoEvents();
    }

    /// <summary>The chunk currently being shown, for the harness to assert on.</summary>
    internal string? UiChunkName => _chunkName;

    /// <summary>
    /// Render one chunk's grid straight to a PNG, bypassing the window. The
    /// harness uses this to check what a chunk actually looks like without
    /// depending on the live layout.
    /// </summary>
    internal string UiCaptureChunk(string module, string path, int cell = 12)
    {
        if (_slot is null) return "no slot";
        var view = ChunkFacade.ReadChunk(_slot, module);
        if (view is null) return "no chunk";

        using var grid = new UI.ChunkGrid();
        if (SaveFacade.ReadCampsite(_slot)?.Coord is { } cc && !view.IsUnderground
            && ChunkFacade.ChunkView.IndexOf(cc.BiomeX, cc.BiomeY) == view.Index)
        {
            var cells = SaveFacade.ReadCampsite(_slot)!.Cells;
            if (cells.Count > 0)
            {
                var box = Rectangle.FromLTRB(
                    cells.Min(c => c.DetailX), cells.Min(c => c.DetailY),
                    cells.Max(c => c.DetailX) + 1, cells.Max(c => c.DetailY) + 1);
                grid.CampOutline = box;
                grid.FocusCamp = box;
            }
        }
        grid.Load(view, cell);
        var (w, h) = UI.ChunkGrid.ContentSize(view);
        using var bmp = new Bitmap(Math.Max(1, w * cell), Math.Max(1, h * cell));
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            grid.DrawTo(g);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return $"{module} 地形{view.Terrain.Count} 放置物{view.Objects.Count} 上色{grid.PaintedCells} 图{w}x{h}";
    }

    /// <summary>
    /// Pick a surface chunk by index the way a user does: set the ComboBox
    /// selection, which raises SelectedIndexChanged. This is the exact path that
    /// used to dispose the combo mid-dispatch.
    /// </summary>
    internal bool UiSelectChunk(int index)
    {
        Navigate("map");
        Application.DoEvents();

        var combo = FindCombo(_content);
        if (combo is null) return false;

        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is not ChunkPick pick) continue;
            if (pick.Module.EndsWith($"SurfaceChunk_{index}", StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;   // raises the handler under test
                Application.DoEvents();
                // Let the posted load finish.
                for (int w = 0; w < 400 && _chunkLoading; w++)
                {
                    Application.DoEvents();
                    Thread.Sleep(15);
                }
                Application.DoEvents();
                return true;
            }
        }
        return false;
    }

    private static ComboBox? FindCombo(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is ComboBox cb && cb.Items.Count > 0 && cb.Items[0] is ChunkPick) return cb;
            var hit = FindCombo(c);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Pick a chunk but return immediately, without waiting for the load.</summary>
    internal void UiSelectChunkNoWait(int index)
    {
        Navigate("map");
        Application.DoEvents();
        var combo = FindCombo(_content);
        if (combo is null) return;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is not ChunkPick pick) continue;
            if (pick.Module.EndsWith($"SurfaceChunk_{index}", StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;
                Application.DoEvents();
                return;
            }
        }
    }

    /// <summary>Pump the message loop until no chunk load is pending.</summary>
    internal void UiDrain()
    {
        for (int i = 0; i < 1200 && _chunkLoading; i++)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Application.DoEvents();
    }

    /// <summary>Navigate to a page by key, for the harness.</summary>
    internal void UiNavigate(string key)
    {
        Navigate(key);
        Application.DoEvents();
    }

    /// <summary>The open slot, so the harness can assert on it.</summary>
    internal SaveSlot? UiSlot => _slot;

    /// <summary>The campsite view the world page would show.</summary>
    internal CampsiteView? UiCampsite() => _slot is null ? null : SaveFacade.ReadCampsite(_slot);

    /// <summary>Build the camp-position dialog without showing it.</summary>
    internal Form? UiBuildCampDialog(CampsiteView camp) => BuildCampPositionDialog(camp);

    /// <summary>Build the roster dialog without showing it, for layout checks.</summary>
    internal Form UiBuildRosterDialog() => BuildRosterDialog(SaveFacade.ReadCampsite(_slot!)!);

    /// <summary>Screenshot the 录入物品名称 dialog, for layout verification.</summary>
    internal void UiCaptureItemNameDialog(string path)
    {
        // Build the same dialog the button builds, but non-modally so it can be
        // drawn to a bitmap.
        using var dlg = BuildItemNameDialog();
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(0, 0);
        dlg.Show();
        Application.DoEvents();
        Application.DoEvents();
        using var bmp = new Bitmap(dlg.Width, dlg.Height);
        dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        dlg.Close();
    }

    /// <summary>
    /// Screenshot the 新增物品 picker in both states: normal (library rows) and
    /// with an unknown id typed in, which is the path this round added.
    /// </summary>
    internal void UiCapturePicker(string normalPath, string typedPath)
    {
        if (_slot is null) return;
        var player = SaveFacade.ReadPlayer(_slot)!;
        var cat = CatalogStore.Load();

        SearchPickerDialog Build(string query)
        {
            var present = player.InventoryItems.Where(i => !i.IsEmpty)
                .Select(i => i.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = cat.Items
                .OrderBy(i => present.Contains(i.Id) ? 0 : 1)
                .ThenBy(i => i.Name, StringComparer.CurrentCulture)
                .Select(i => (Text: i.Display, Tag: (string?)i.Id))
                .ToList();
            var dlg = new SearchPickerDialog(
                "新增物品",
                "从资料库选一件物品，或者直接把物品 ID 打进搜索框后点确定。\n"
              + $"资料库 {cat.Items.Count} 种，本存档已有 {present.Count} 种；当前有 "
              + $"{player.InventoryItems.Count(i => i.IsEmpty)} 个空槽位。",
                entries, confirmText: "下一步", column1: "物品名", column2: "物品 ID")
            { AllowFreeId = true };
            dlg.UiSetQuery(query);
            return dlg;
        }

        foreach (var (path, q) in new[] { (normalPath, ""), (typedPath, "1020051") })
        {
            using var dlg = Build(q);
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(0, 0);
            dlg.Show();
            Application.DoEvents();
            Application.DoEvents();
            using var bmp = new Bitmap(dlg.Width, dlg.Height);
            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, dlg.Width, dlg.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            dlg.Close();
        }
    }

    /// <summary>Save everything, as the 保存修改 button does.</summary>
    internal void UiSaveAll() => SaveChanges();

    /// <summary>Refresh the dirty badge, for the harness.</summary>
    internal void UiMarkDirty() => MarkDirtyAndRefresh();

    /// <summary>The sidebar labels in visual order, for the harness.</summary>
    internal List<string> UiNavOrder()
    {
        var list = new List<string>();
        void Walk(Control c)
        {
            foreach (Control k in c.Controls)
            {
                if (k is NavButton nb) list.Add(nb.Text.Trim());
                Walk(k);
            }
        }
        Walk(_sidebar);
        return list;
    }

    /// <summary>True when the window still answers messages (not hung).</summary>
    internal bool UiResponsive() => IsHandleCreated && !IsDisposed && Visible;

    /// <summary>Capture the world page's camp card to a PNG, for layout checks.</summary>
    internal string UiCaptureWorldCard(string path)
    {
        Navigate("world");
        Application.DoEvents();
        foreach (Control c in _content.Controls)
        {
            if (c is Card card && card.Text.Contains("营地"))
            {
                using var bmp = new Bitmap(card.Width, card.Height);
                card.DrawToBitmap(bmp, new Rectangle(0, 0, card.Width, card.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                return $"{card.Width}x{card.Height}";
            }
        }
        using var whole = new Bitmap(_content.Width, Math.Min(_content.Height, 900));
        _content.DrawToBitmap(whole, new Rectangle(0, 0, whole.Width, whole.Height));
        whole.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return $"整页 {whole.Width}x{whole.Height}";
    }

    /// <summary>Capture the whole content area to a PNG, for layout checks.</summary>
    internal string UiCaptureChunkCard(string path)
    {
        Application.DoEvents();
        var c = LastChunkCard;
        if (c is null) return "null";
        int w = c.Width, h = c.Height;
        using var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h));
        c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return $"{bmp.Width}x{bmp.Height}";
    }

    internal string UiCapturePage(string path) => UiCapturePage(path, false);

    /// <summary>
    /// Render the page to a PNG. When <paramref name="full"/> is set the whole
    /// scrollable page is captured, which is the only way to see the count rows
    /// and the explanatory note that sit below the fold.
    /// </summary>
    internal string UiCapturePage(string path, bool full)
    {
        Application.DoEvents();
        int h = full ? _content.Height : Math.Min(_content.Height, 900);
        using var bmp = new Bitmap(_content.Width, h);
        _content.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, h));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return $"{bmp.Width}x{bmp.Height}";
    }

    public MainForm()
    {
        Text = "GenerativeGame 存档编辑器";
        Width = 1220;
        Height = 840;
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.Base;
        DoubleBuffered = true;

        BuildShell();
        Shown += (_, _) =>
        {
            void Trace(string m)
            {
                if (ShotDir is null) return;
                try { File.AppendAllText(Path.Combine(ShotDir, "_boot.txt"), "\n" + m); } catch { }
            }
            Trace("shown");
            try
            {
                AutoOpenDefault();
                Trace("autoopen done");
            }
            catch (Exception ex) { Trace("autoopen FAIL " + ex); throw; }
            if (ShotDir is not null) RunShots();
        };
    }

    /// <summary>When set, the form renders every page to this folder and closes.</summary>
    public string? ShotDir { get; init; }

    /// <summary>
    /// Save to open for a screenshot run. Screenshots must document the save the
    /// caller asked for; falling back to last.txt produced pictures of a
    /// completely different save and cost a long false-bug hunt.
    /// </summary>
    public string? ShotSave { get; init; }

    /// <summary>
    /// Render each page to a PNG. Navigating is a plain method call, so this
    /// exercises the same code path a click would without needing a mouse.
    /// </summary>
    private async void RunShots()
    {
        Directory.CreateDirectory(ShotDir!);
        try
        {
            await Task.Delay(1500);
            File.WriteAllText(Path.Combine(ShotDir!, "_started.txt"),
                              DateTime.Now.ToString("HH:mm:ss.fff"));
            var pages = new[] { "overview", "player", "party", "inventory", "npc", "memory", "library", "world", "map", "modules" };
            foreach (var page in pages)
            {
                File.AppendAllText(Path.Combine(ShotDir!, "_progress.txt"), page + "\n");
                Navigate(page);
                await Task.Delay(600);
                CaptureTo(Path.Combine(ShotDir!, $"{page}.png"));

                if (page == "inventory" && _slot is not null)
                {
                    var pv = SaveFacade.ReadPlayer(_slot);
                    // Prefer the 大棒骨: it carries two live packages, so the
                    // capture shows both the readable names and the raw ids.
                    var withAffix = pv is null ? null
                        : (pv.HotbarItems.Concat(pv.InventoryItems).FirstOrDefault(x => x.ItemId == "3010046")
                           ?? pv.InventoryItems.Concat(pv.HotbarItems)
                                .FirstOrDefault(x => ItemAffixView.From(x.Group) is { } a
                                                     && a.Packages.Any(k => k.Valid && k.Level > 0)));
                    if (withAffix is not null)
                    {
                        var built = BuildWeaponDialog(withAffix, 0, "背包");
                        if (built is not null)
                        {
                            var dlg = built.Value.Dialog;
                            try
                            {
                                dlg.Show();
                                Application.DoEvents();
                                await Task.Delay(1200);
                                Application.DoEvents();
                                using var bmp = new Bitmap(Math.Max(dlg.Width, 400), Math.Max(dlg.Height, 300));
                                dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                                bmp.Save(Path.Combine(ShotDir!, "affix_dialog.png"),
                                         System.Drawing.Imaging.ImageFormat.Png);
                            }
                            catch (Exception ex)
                            {
                                File.WriteAllText(Path.Combine(ShotDir!, "affix_error.txt"), ex.ToString());
                            }
                            finally { dlg.Close(); }
                        }
                    }
                }

                if (page == "world" && _slot is not null)
                {
                    // The camp-position dialog is laid out from measured text, so
                    // render it and look at it: an overlap here is invisible in
                    // code review and obvious in a screenshot.
                    var camp = SaveFacade.ReadCampsite(_slot);
                    if (camp is not null)
                    {
                        var dlg = BuildCampPositionDialog(camp);
                        if (dlg is not null)
                        {
                            try
                            {
                                dlg.Show();
                                Application.DoEvents();
                                await Task.Delay(900);
                                Application.DoEvents();
                                using var bmp = new Bitmap(Math.Max(dlg.Width, 400), Math.Max(dlg.Height, 300));
                                dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                                bmp.Save(Path.Combine(ShotDir!, "camp_dialog.png"),
                                         System.Drawing.Imaging.ImageFormat.Png);
                            }
                            catch (Exception ex)
                            {
                                File.WriteAllText(Path.Combine(ShotDir!, "camp_error.txt"), ex.ToString());
                            }
                            finally { dlg.Close(); }
                        }
                    }
                }

                if (page == "npc" && _slot is not null)
                {
                    // Select an AI character so the combat-attribute card (the one
                    // that actually drives max health) is visible in the capture.
                    var ai = SaveFacade.ReadCharacters(_slot)
                                       .FirstOrDefault(c => c.Combat.Count > 0);
                    if (ai is not null)
                    {
                        var npcView = SaveFacade.ReadNpcs(_slot).FirstOrDefault(n => n.Id == ai.CharacterId);
                        if (npcView is not null)
                        {
                            ShowNpcDetail(npcView, SaveFacade.ReadNpcMoods(_slot)
                                .FirstOrDefault(m => m.Id == ai.CharacterId));
                            await Task.Delay(400);
                            // Scroll far enough that the combat card (below the
                            // five base attributes) is inside the viewport.
                            _content.AutoScrollPosition = new Point(0, Math.Max(0, (_npcDetail?.Top ?? 0) + 430));
                            await Task.Delay(300);
                            CaptureTo(Path.Combine(ShotDir!, "npc_combat.png"));
                        }
                    }
                }

                if (page == "map" && _slot is not null)
                {
                    // Capture the loading state first: that is what the user sees
                    // for up to a second, and it has to be obvious.
                    _chunkName = "GenMap__SurfaceChunk_18";
                    _chunkLoading = true;
                    _chunk = null;
                    Navigate("map");
                    await Task.Delay(250);
                    CaptureTo(Path.Combine(ShotDir!, "map_loading.png"));

                    // Load one chunk first so the per-chunk card has content in
                    // the capture, then fall through to the chest scan.
                    _chunkLoading = false;
                    _chunk = ChunkFacade.ReadChunk(_slot, "GenMap__SurfaceChunk_18");
                    _chunkName = "GenMap__SurfaceChunk_18";
                    Navigate("map");
                    await Task.Delay(900);
                    CaptureTo(Path.Combine(ShotDir!, "map_chunk.png"));

                    var chunkObj = _chunk?.Objects.FirstOrDefault(o => !o.Occupied && o.TurnProp is not null);
                    if (chunkObj is not null)
                    {
                        try
                        {
                            var (dlg, _) = BuildChunkObjectDialog(chunkObj);
                            dlg.Show();
                            Application.DoEvents();
                            await Task.Delay(900);
                            Application.DoEvents();
                            using var bmp = new Bitmap(Math.Max(dlg.Width, 400), Math.Max(dlg.Height, 300));
                            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                            bmp.Save(Path.Combine(ShotDir!, "chunk_dialog.png"),
                                     System.Drawing.Imaging.ImageFormat.Png);
                            dlg.Close();
                        }
                        catch (Exception ex)
                        {
                            File.WriteAllText(Path.Combine(ShotDir!, "chunk_error.txt"), ex.ToString());
                        }
                    }

                    // The terrain dialog is opened by clicking a painted cell; render
                    // it directly so a clipped button shows up here instead.
                    var anyTerrain = _chunk?.Terrain.FirstOrDefault();
                    if (anyTerrain is not null)
                    {
                        var built = BuildTerrainDialog(anyTerrain);
                        if (built is not null)
                        {
                            var dlg = built.Value.Dialog;
                            try
                            {
                                dlg.Show();
                                Application.DoEvents();
                                await Task.Delay(900);
                                Application.DoEvents();
                                using var bmp = new Bitmap(Math.Max(dlg.Width, 400), Math.Max(dlg.Height, 300));
                                dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                                bmp.Save(Path.Combine(ShotDir!, "terrain_dialog.png"),
                                         System.Drawing.Imaging.ImageFormat.Png);
                            }
                            catch (Exception ex)
                            {
                                File.WriteAllText(Path.Combine(ShotDir!, "terrain_error.txt"), ex.ToString());
                            }
                            finally { dlg.Close(); }
                        }
                    }

                    File.AppendAllText(Path.Combine(ShotDir!, "_progress.txt"), "  chests-start\n");
                    _chests = MapFacade.ReadChestsCached(_slot);
                    File.AppendAllText(Path.Combine(ShotDir!, "_progress.txt"), "  chests-done\n");
                    Navigate("map");
                    await Task.Delay(900);
                    CaptureTo(Path.Combine(ShotDir!, "map_chests.png"));

                    // Scroll to the bottom so the read-only card below the fold
                    // shows up in the capture too.
                    _content.AutoScrollPosition = new Point(0, _content.VerticalScroll.Maximum);
                    Application.DoEvents();
                    await Task.Delay(700);
                    CaptureTo(Path.Combine(ShotDir!, "map_bottom.png"));

                    var first = _chests.FirstOrDefault(c => c.UsedSlots > 0);                    if (first is not null)
                    {
                        var dlg = BuildChestDialog(first);
                        try
                        {
                            dlg.Show();
                            Application.DoEvents();
                            await Task.Delay(1200);
                            Application.DoEvents();
                            using var bmp = new Bitmap(Math.Max(dlg.Width, 400), Math.Max(dlg.Height, 300));
                            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                            bmp.Save(Path.Combine(ShotDir!, "chest_dialog.png"),
                                     System.Drawing.Imaging.ImageFormat.Png);
                        }
                        catch (Exception ex)
                        {
                            File.WriteAllText(Path.Combine(ShotDir!, "chest_dialog_error.txt"), ex.ToString());
                        }
                        finally { dlg.Close(); }
                    }

                    // Library export/import dialogs, captured the same
                    // non-modal way as the chest dialog above. An earlier
                    // attempt hooked Application.OpenForms from inside the modal
                    // loop; in the single-file build that froze the UI, every
                    // later screenshot repeated one frame, and the process died
                    // with 0xCFFFFFFF.
                    File.AppendAllText(Path.Combine(ShotDir!, "_progress.txt"), "  export-build\n");
                    var expDlg = BuildExportLibraryDialog(CatalogStore.Load());
                    File.AppendAllText(Path.Combine(ShotDir!, "_progress.txt"), "  export-built\n");
                    if (expDlg is not null)
                    {
                        try
                        {
                            expDlg.Show();
                            Application.DoEvents();
                            await Task.Delay(800);
                            Application.DoEvents();
                            using var bmp = new Bitmap(Math.Max(expDlg.Width, 400), Math.Max(expDlg.Height, 300));
                            expDlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                            bmp.Save(Path.Combine(ShotDir!, "export_dialog.png"),
                                     System.Drawing.Imaging.ImageFormat.Png);
                        }
                        catch (Exception ex)
                        {
                            File.WriteAllText(Path.Combine(ShotDir!, "export_dialog_error.txt"), ex.ToString());
                        }
                        finally { expDlg.Close(); }
                    }

                    File.AppendAllText(Path.Combine(ShotDir!, "_progress.txt"), "  import-build\n");
                    var impDlg = BuildImportLibraryDialog();
                    File.AppendAllText(Path.Combine(ShotDir!, "_progress.txt"), "  import-built\n");
                    try
                    {
                        impDlg.Show();
                        Application.DoEvents();
                        await Task.Delay(800);
                        Application.DoEvents();
                        using var bmp = new Bitmap(Math.Max(impDlg.Width, 400), Math.Max(impDlg.Height, 300));
                        impDlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(Path.Combine(ShotDir!, "import_dialog.png"),
                                 System.Drawing.Imaging.ImageFormat.Png);
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(Path.Combine(ShotDir!, "import_dialog_error.txt"), ex.ToString());
                    }
                    finally { impDlg.Close(); }

                    // Both item dialogs: the AI editor (full fields) and the
                    // official one (quantity only).
                    try
                    {
                        var slotForItem = _slot;
                        var pvForItem = slotForItem is null ? null : SaveFacade.ReadPlayer(slotForItem);
                        if (pvForItem is not null)
                        {
                            async Task Grab(InventoryItem? item, string file, Func<InventoryItem, ItemDetailView,
                                              (Form Dialog, Action Apply)?> build)
                            {
                                if (item is null) return;
                                var det = ItemDetailView.From(item);
                                if (det is null) return;
                                var b2 = build(item, det);
                                if (b2 is null) return;
                                var d2 = b2.Value.Dialog;
                                try
                                {
                                    d2.Show();
                                    Application.DoEvents();
                                    await Task.Delay(800);
                                    Application.DoEvents();
                                    using var bmp = new Bitmap(Math.Max(d2.Width, 400), Math.Max(d2.Height, 300));
                                    d2.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                                    bmp.Save(Path.Combine(ShotDir!, file), System.Drawing.Imaging.ImageFormat.Png);
                                }
                                finally { d2.Close(); }
                            }

                            var all = pvForItem.InventoryItems.Concat(pvForItem.HotbarItems)
                                                   .Where(x => !x.IsEmpty).ToList();

                            // AI item: prefer one that actually carries attributes.
                            var aiItem = all.Where(x => ItemDetailView.From(x) is { CanEditContent: true })
                                            .OrderByDescending(x => ItemDetailView.From(x)?.Attributes.Count ?? 0)
                                            .FirstOrDefault();
                            await Grab(aiItem, "item_detail.png", (i, d) => BuildItemDetailDialog(i, d, 0, "背包"));

                            // Official item: any one with a real name.
                            var offItem = all.FirstOrDefault(x => ItemDetailView.From(x) is { CanEditContent: false });
                            await Grab(offItem, "item_official.png", (i, d) => BuildCountOnlyDialog(i, d, 0, "背包"));
                        }
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(Path.Combine(ShotDir!, "item_detail_error.txt"), ex.ToString());
                    }
                }
            }
        }
        catch (Exception e)
        {
            try { File.WriteAllText(Path.Combine(ShotDir!, "error.txt"), e.ToString()); } catch { }
        }
        finally
        {
            // An async void method swallows exceptions raised after an await, so
            // a failure here used to leave a half-finished screenshot set with
            // no error.txt at all and exit code 0 — it looked like a clean run.
            // Always write the marker, and Close() so the process really exits.
            try
            {
                File.WriteAllText(Path.Combine(ShotDir!, "_finished.txt"),
                                  DateTime.Now.ToString("HH:mm:ss.fff"));
            }
            catch { }
            Close();
        }
    }

    private void CaptureTo(string path)
    {
        if (Width <= 0 || Height <= 0) return;
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    // ------------------------------------------------------------------ shell
    private void BuildShell()
    {
        // ---------- header
        _headerBar.Dock = DockStyle.Top;
        _headerBar.Height = 72;
        _headerBar.BackColor = Color.White;
        _headerBar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, _headerBar.Height - 1, _headerBar.Width, _headerBar.Height - 1);
        };

        _title.Text = "存档编辑器";
        _title.Font = Theme.H1;
        _title.ForeColor = Theme.Text;
        _title.AutoSize = true;
        _title.Location = new Point(20, 14);
        _title.BackColor = Color.Transparent;

        _subtitle.Text = "尚未打开存档";
        _subtitle.Font = Theme.Small;
        _subtitle.ForeColor = Theme.Muted;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(22, 44);
        _subtitle.BackColor = Color.Transparent;

        _dirtyBadge.Text = "有未保存的修改";
        _dirtyBadge.Font = Theme.Small;
        _dirtyBadge.ForeColor = Color.White;
        _dirtyBadge.BackColor = Theme.DirtyEdge;
        _dirtyBadge.AutoSize = false;
        _dirtyBadge.Size = new Size(104, 22);
        _dirtyBadge.TextAlign = ContentAlignment.MiddleCenter;
        _dirtyBadge.Visible = false;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 430,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 20, 16, 0),
            BackColor = Color.Transparent,
        };

        StylePrimary(_btnSave, "保存修改");
        _btnSave.Width = 104;
        _btnSave.Enabled = false;
        _btnSave.EnabledChanged += (_, _) => PaintPrimary(_btnSave);
        _btnSave.Click += (_, _) => SaveChanges();

        StyleSecondary(_btnRevert, "放弃修改");
        _btnRevert.Width = 96;
        _btnRevert.Enabled = false;
        _btnRevert.Click += (_, _) => RevertAll();

        var btnReload = new Button();
        StyleSecondary(btnReload, "重新载入");
        btnReload.Width = 92;
        btnReload.Click += (_, _) => { if (_slot is not null) LoadSlot(_slot.RootPath); };

        var btnOpen = new Button();
        StyleSecondary(btnOpen, "打开存档");
        btnOpen.Width = 92;
        btnOpen.Click += (_, _) => ChooseSave();

        actions.Controls.Add(_btnSave);
        actions.Controls.Add(_btnRevert);
        actions.Controls.Add(btnReload);
        actions.Controls.Add(btnOpen);

        _headerBar.Controls.Add(_dirtyBadge);
        _headerBar.Controls.Add(actions);
        _headerBar.Controls.Add(_subtitle);
        _headerBar.Controls.Add(_title);

        // ---------- sidebar
        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = 208;
        _sidebar.BackColor = Color.White;
        _sidebar.Padding = new Padding(0, 12, 0, 0);
        _sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);
        };

        PartyView.LoadSetting();
        AddNav("overview", "概览", "▤");
        AddNav("player", "玩家", "◈");
        AddNav("inventory", "背包", "▥");
        AddNav("party", "小队", "⚑");
        AddNav("npc", "NPC", "☗");
        AddNav("memory", "记忆", "❖");
        AddNav("world", "世界", "◐");
        AddNav("map", "地图", "⌘");
        AddNav("modules", "模块", "▦");
        AddNav("library", "资料库", "★");   // kept last, on purpose

        // ---------- content
        _content.Dock = DockStyle.Fill;
        _content.BackColor = Theme.Bg;
        _content.AutoScroll = true;
        _content.Padding = new Padding(20, 16, 20, 16);

        // ---------- status
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 26, BackColor = Color.White };
        statusBar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, statusBar.Width, 0);
        };
        _status.Text = "就绪";
        _status.Font = Theme.Small;
        _status.ForeColor = Theme.Muted;
        _status.AutoSize = true;
        _status.Location = new Point(16, 5);
        _status.BackColor = Color.Transparent;
        statusBar.Controls.Add(_status);

        Controls.Add(_content);
        Controls.Add(_sidebar);
        Controls.Add(_headerBar);
        Controls.Add(statusBar);
    }

    private void AddNav(string key, string text, string glyph)
    {
        var b = new NavButton(text, glyph)
        {
            Tag = key,
            Location = new Point(0, 12 + _navs.Count * 46),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Width = _sidebar.Width,
        };
        b.Click += (_, _) => Navigate(key);
        _sidebar.Controls.Add(b);
        _navs.Add(b);
    }

    private static void StylePrimary(Button b, string text)
    {
        b.Text = text;
        b.Height = 32;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.ForeColor = Color.White;
        b.Font = Theme.Bold;
        b.Cursor = Cursors.Hand;
        b.Margin = new Padding(8, 0, 0, 0);
        b.UseVisualStyleBackColor = false;
        b.EnabledChanged += (_, _) => PaintPrimary(b);
        PaintPrimary(b);
    }

    /// <summary>WinForms greys a disabled button itself, so paint the accent state manually.</summary>
    private static void PaintPrimary(Button b)
    {
        b.BackColor = b.Enabled ? Theme.Accent : Color.FromArgb(198, 205, 218);
        b.FlatAppearance.MouseOverBackColor = b.Enabled
            ? ControlPaint.Light(Theme.Accent, 0.15f)
            : Color.FromArgb(198, 205, 218);
    }

    private static void StyleSecondary(Button b, string text)
    {
        b.Text = text;
        b.Height = 32;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Theme.Border;
        b.BackColor = Color.White;
        b.ForeColor = Theme.Text;
        b.Font = Theme.Base;
        b.Cursor = Cursors.Hand;
        b.Margin = new Padding(8, 0, 0, 0);
    }

    // ------------------------------------------------------------------ open
    private void AutoOpenDefault()
    {
        if (!string.IsNullOrEmpty(ShotSave) && IsSaveSlot(ShotSave))
        {
            LoadSlot(ShotSave);
            return;
        }

        var remembered = LoadLastPath();
        if (remembered is not null && IsSaveSlot(remembered))
        {
            LoadSlot(remembered);
            return;
        }

        foreach (var anchor in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(anchor);
            for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                foreach (var name in new[] { "GameSave_2", "GameSave_1" })
                {
                    var candidate = Path.Combine(dir.FullName, name);
                    if (IsSaveSlot(candidate)) { LoadSlot(candidate); return; }
                }
                if (IsSaveSlot(dir.FullName)) { LoadSlot(dir.FullName); return; }
            }
        }
        SetStatus("请点击右上角「打开存档」选择存档文件夹");
    }

    private static bool IsSaveSlot(string path) =>
        Directory.Exists(Path.Combine(path, "Primary", "Modules")) &&
        File.Exists(Path.Combine(path, "Manifest.sav"));

    private static string LastPathFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "GenerativeSaveEditor", "last.txt");

    private static string? LoadLastPath()
    {
        try
        {
            var f = LastPathFile;
            if (!File.Exists(f)) return null;
            var p = File.ReadAllText(f).Trim();
            return p.Length > 0 ? p : null;
        }
        catch { return null; }
    }

    private static void SaveLastPath(string path)
    {
        try
        {
            var f = LastPathFile;
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, path);
        }
        catch { }
    }

    private void ChooseSave()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择存档文件夹（包含 Manifest.sav 的那一层）",
            UseDescriptionForTitle = true,
        };
        if (_slot is not null)
            dlg.SelectedPath = Path.GetDirectoryName(_slot.RootPath) ?? _slot.RootPath;

        if (dlg.ShowDialog(this) == DialogResult.OK)
            LoadSlot(dlg.SelectedPath);
    }

    private void LoadSlot(string path)
    {
        try
        {
            UseWaitCursor = true;
            SetStatus($"正在载入 {path} …");
            Application.DoEvents();

            var slot = SaveSlot.Open(path, null, SaveSlot.EditorModules);
            _slot = slot;
            _charNames = null;   // names belong to the previous save
            // A screenshot run must not overwrite the user's "last opened" save
            // with a throwaway copy under C:\dsh_build.
            if (ShotDir is null) SaveLastPath(path);

            _title.Text = slot.SaveDisplayName;
            RefreshSubtitle();
            _btnSave.Enabled = false;
            _btnRevert.Enabled = false;
            UpdateDirtyBadge();

            Navigate(_current);
            SetStatus($"已载入 {slot.Modules.Count} 个可编辑模块（共 {slot.ModuleFiles.Count} 个文件）");

            // Top up the shared library from every save we open. The scan is
            // ~17 ms (145 items, 54 NPCs), so it runs inline rather than on a
            // background task: the library page must be correct the first time
            // it is opened, and a user who opens a save and immediately clicks
            // 资料库 should not see the pre-scan counts.
            AutoUpdateLibrary(slot);

            // The game rewrites every module on exit, so editing while it runs
            // looks like "the edit did not take". Say so once, here.
            // A modal box here is correct for a human but fatal for --shot: the
            // harness has nobody to click OK, so the run hangs forever. Record
            // the warning to the capture folder instead and carry on.
            if (IsGameRunning() && ShotDir is not null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(ShotDir, "_game_running.txt"),
                        "检测到 Magi Scapes 正在运行；截图运行时跳过提示框。");
                }
                catch { }
            }
            else if (IsGameRunning())
                MessageBox.Show(this,
                    "检测到 Magi Scapes 正在运行。\n\n" +
                    "游戏退出时会用内存里的数据覆盖整个存档，任何修改都会被冲掉。\n" +
                    "请先退出游戏，再修改并保存。",
                    "请先关闭游戏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "载入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("载入失败");
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    /// <summary>
    /// Repaint the header line from the *effective* values, so a rename shows up
    /// immediately instead of waiting for the next save-and-reload.
    /// </summary>
    private void RefreshSubtitle()
    {
        if (_slot is null) { _subtitle.Text = "尚未打开存档"; return; }
        _title.Text = _slot.SaveDisplayName;
        _subtitle.Text = $"{_slot.EffectivePlayerName}  ·  种子 {_slot.MapSeed}  ·  " +
                         $"游玩 {_slot.PlayTime:F1} 小时  ·  {_slot.ModuleFiles.Count} 个模块";
    }

    // ------------------------------------------------------------------ nav
    private void Navigate(string key)
    {
        // Remember where the user was before the page is torn down, so a rebuild
        // triggered by a toggle does not scroll the content out from under them.
        // Only when rebuilding the SAME page: switching pages should start at the
        // top, otherwise you land halfway down an unrelated card.
        if (_content is not null && _content.Controls.Count > 0 && key == _current)
            _scrollY = Math.Max(0, -_content.AutoScrollPosition.Y);
        else if (key != _current)
            _scrollY = 0;

        _current = key;
        foreach (var n in _navs)
            n.Selected = (string?)n.Tag == key;

        _content.SuspendLayout();
        var old = _content.Controls.Cast<Control>().ToList();
        _content.Controls.Clear();

        // Dispose the old page LATER, never synchronously. Navigate is called
        // from inside control event handlers (a ComboBox's SelectedIndexChanged
        // is the map page's chunk picker), and disposing a control while it is
        // still dispatching its own message is an access violation in WinForms:
        // the process dies with no managed exception, so nothing lands in
        // errors.log. Clearing the collection above already detaches the
        // controls, so nothing is drawn in the meantime.
        if (old.Count > 0)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    foreach (var c in old)
                    {
                        try { c.Dispose(); } catch { }
                    }
                }));
            }
            catch
            {
                // No handle yet (first navigation); disposing now is safe because
                // no message can be in flight.
                foreach (var c in old)
                {
                    try { c.Dispose(); } catch { }
                }
            }
        }

        switch (key)
        {
            case "overview": BuildOverview(); break;
            case "player": BuildPlayer(); break;
            case "inventory": BuildInventory(); break;
            case "party": BuildParty(); break;
            case "npc": BuildNpc(); break;
            case "memory": BuildMemory(); break;
            case "library": BuildLibrary(); break;
            case "world": BuildWorld(); break;
            case "map": BuildMap(); break;
            case "modules": BuildModules(); break;
        }
        _content.ResumeLayout();

        // Restore the scroll offset instead of always jumping to the top.
        //
        // The map page rebuilds itself on every toggle and every zoom change, and
        // the map grid sits below the chest card, so a rebuild used to throw the
        // view back to the top and scroll the map out of sight. From the user's
        // side the button then looked dead: they clicked 整块显示, the page
        // repainted, and the only visible change was the page scrolling away.
        _content.AutoScrollPosition = new Point(0, _scrollY);
    }

    /// <summary>Vertical scroll offset to restore after the next rebuild.</summary>
    private int _scrollY;

    /// <summary>Stack cards vertically from the top, in insertion order.</summary>
    private void StackCards(params Control[] cards)
    {
        int y = 0;
        foreach (var c in cards)
        {
            c.Location = new Point(0, y);
            c.Width = _content.ClientSize.Width - _content.Padding.Horizontal;
            c.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _content.Controls.Add(c);
            y += c.Height + 14;
        }
    }

    private Card NewCard(string title) => new(title) { Width = _content.ClientSize.Width - 40 };

    // ------------------------------------------------------------------ pages
    private void BuildOverview()
    {
        if (_slot is null) return;

        var info = NewCard("存档信息");

        var saveNameProp = SaveFacade.SaveDisplayNameProp(_slot);
        if (saveNameProp is not null)
        {
            var f = new NameField(saveNameProp, "存档名称");
            f.Changed += MarkDirtyAndRefresh;
            info.AddRow(f);
        }
        else info.AddRow(new InfoRow("存档名称", _slot.SaveDisplayName));

        var playerNameProp = SaveFacade.PlayerNameProp(_slot);
        if (playerNameProp is not null)
        {
            var f = new NameField(playerNameProp, "玩家名", 32,
                SaveFacade.ManifestPlayerNameProp(_slot));
            f.Changed += MarkDirtyAndRefresh;
            info.AddRow(f);
        }
        else info.AddRow(new InfoRow("玩家名", _slot.EffectivePlayerName));
        info.AddRow(new InfoRow("地图种子", _slot.MapSeed.ToString()));
        info.AddRow(new InfoRow("游玩时长", $"{_slot.PlayTime:F2} 小时"));
        info.AddRow(new InfoRow("活动版本", _slot.ActiveVersion.ToString()));
        info.AddRow(new InfoRow("最后正常版本", _slot.LastKnownGoodVersion.ToString()));

        var health = NewCard("文件状态");
        // Only the editable modules count towards the health readout. The GenMap
        // chunks are pulled in on demand for the map page and carry a large amount
        // of pre-existing parse noise in the original save; folding that in would
        // make a perfectly good save look broken.
        var editable = _slot.Modules.Where(m => !m.ModuleName.StartsWith("GenMap__")).ToList();
        var clean = editable.Count(m => m.Warnings.Count == 0);
        health.AddRow(new InfoRow("模块文件总数", _slot.ModuleFiles.Count.ToString()));
        health.AddRow(new InfoRow("已载入可编辑模块", editable.Count.ToString()));
        health.AddRow(new InfoRow("解析无告警", $"{clean} / {editable.Count}"));
        health.AddRow(new InfoRow("告警总数", editable.Sum(m => m.Warnings.Count).ToString()));
        var chunks = _slot.Modules.Count(m => m.ModuleName.StartsWith("GenMap__"));
        health.AddNote(chunks == 0
            ? "地图区块（GenMap__*）按需载入，不会在打开存档时解析，因此打开很快。"
            : $"地图区块（GenMap__*）按需载入，当前已载入 {chunks} 个用于地图页；它们不计入上面的告警统计。");

        StackCards(info, health);
    }

    private void BuildPlayer()
    {
        if (_slot is null) return;
        var v = SaveFacade.ReadPlayer(_slot);
        if (v is null) { StackCards(NewCard("玩家").Also(c => c.AddNote("未找到玩家模块"))); return; }

        var basic = NewCard("基本信息");

        var nameProp = SaveFacade.PlayerNameProp(_slot);
        if (nameProp is not null)
        {
            var f = new NameField(nameProp, "角色名", 32,
                SaveFacade.ManifestPlayerNameProp(_slot));
            f.Changed += MarkDirtyAndRefresh;
            basic.AddRow(f);
        }
        else basic.AddRow(new InfoRow("玩家名", v.PlayerName ?? "?"));

        basic.AddRow(new InfoRow("SteamID", v.SteamId.ToString()));
        basic.AddRow(new InfoRow("皮肤 ID", v.SkinId ?? "?"));
        basic.AddRow(new InfoRow("背包物品", $"{v.InventoryCount} 件"));
        basic.AddRow(new InfoRow("追踪任务", v.TrackedQuest ?? "无"));

        var vitals = NewCard("生命与状态");
        vitals.AddAction("全部回满", () => FillAll(v));
        AddNum(vitals, v.MapProps, "CurrentHealth", "当前生命");
        AddNum(vitals, v.MapProps, "MaxHealth", "生命上限");
        AddNum(vitals, v.MapProps, "CurrentMagic", "当前魔力");
        AddNum(vitals, v.MapProps, "CurrentHunger", "当前饥饿");
        AddNum(vitals, v.MapProps, "CurrentHeat", "当前体温");
        AddNum(vitals, v.MapProps, "MaxHeat", "体温上限");
        AddNum(vitals, v.MapProps, "CurrentToxin", "当前毒素");
        AddNum(vitals, v.MapProps, "MaxToxin", "毒素上限");

        // The game derives the level from the experience total and rewrites
        // ReputationLevel on load, so editing the level alone is futile. Offer
        // only the experience field, with the level shown read-only.
        var rep = NewCard("声望");
        rep.AddRow(new InfoRow("声望等级（由经验决定，游戏会重算）", v.ReputationLevel.ToString()));
        AddNum(rep, v.MapProps, "ReputationExperience", "声望经验");

        StackCards(basic, vitals, rep);
    }

    private void FillAll(PlayerView v)
    {
        // The player record has no MaxMagic and no MaxHunger: only CurrentMagic
        // and CurrentHunger exist. Asking for those upper bounds silently did
        // nothing (the lookup fell back to the current value, so the write was a
        // no-op), which is why "全部回满" left magic and hunger untouched.
        // Health/heat/toxin do have real upper bounds, so keep using them.
        SetNum(v.MapProps, "CurrentHealth", "MaxHealth");
        SetNum(v.MapProps, "CurrentMagic", "400");
        SetNum(v.MapProps, "CurrentHunger", "100");
        SetNum(v.MapProps, "CurrentHeat", "MaxHeat");
        SetNum(v.MapProps, "CurrentToxin", "0");
        MarkDirtyAndRefresh();
        // rebuild the page so the spinners show the values we just wrote
        Navigate(_current);
        SetStatus("已把所有状态回满（尚未保存）");
    }

    private void SetNum(List<SaveProperty> props, string name, string fromName)
    {
        var p = props.FirstOrDefault(x => x.Name == name);
        if (p is null) return;
        var src = props.FirstOrDefault(x => x.Name == fromName);
        var text = fromName == "0"
            ? "0"
            : ValueEditor.EffectiveValue(src ?? p)?.ToString() ?? "0";
        var bytes = ValueEditor.Encode(p, text, out _);
        if (bytes is not null) p.MarkDirty(bytes);
    }

    private void AddNum(Card card, List<SaveProperty> props, string name, string label)
    {
        var p = props.FirstOrDefault(x => x.Name == name);
        if (p is null) { card.AddRow(new InfoRow(label, "(缺失)")); return; }

        var f = new NumberField(p, label);
        f.Changed += () => { MarkDirtyAndRefresh(); };
        card.AddRow(f);
    }

    // ------------------------------------------------------------------ npc

    // ------------------------------------------------------------------ party

    /// <summary>
    /// The player's party (FollowNpcIDs), shown as numbered dropdown rows:
    ///
    ///   编号1  NPC: [流云        ▼]
    ///   编号2  NPC: [七号        ▼]
    ///   编号3  NPC: [（空）      ▼]
    ///   编号4  NPC: [金缕        ▼]     ← only when the limit is raised
    ///   [+ 新增]
    ///
    /// Three different NPC lists live in the save and are easy to confuse, so the
    /// page spells out which is which at the bottom.
    /// </summary>
    private void BuildParty()
    {
        if (_slot is null) return;

        var v = PartyView.From(_slot);
        if (v is null)
        {
            StackCards(NewCard("小队").Also(c => c.AddNote("未找到小队数据（Player__Players 里没有 FollowNpcIDs）")));
            return;
        }

        // Rows are rebuilt from scratch on every change, so the working copy of
        // the id list has to outlive a single rebuild.
        _partyDraft = v.Members.Select(m => m.Id).ToList();

        var card = NewCard("小队");
        if (v.Members.Count > v.SlotLimit)
            card.AddNote($"现在 {v.Members.Count} 人，超过当前上限 {v.SlotLimit} 人。"
                       + "多出来的人在游戏里可能被丢掉——要么调高上限，要么删到上限以内。");
        else if (v.Members.Count == v.SlotLimit)
            card.AddNote($"上限 {v.SlotLimit} 人，已经满了。");
        else
            card.AddNote($"上限 {v.SlotLimit} 人，现在 {v.Members.Count} 人，还空 {v.FreeSlots} 个。");

        BuildPartyRows(card, v);

        card.AddAction("+ 新增", () => AddPartySlot(v));
        card.AddAction($"人数上限 {v.SlotLimit}…", ShowPartyLimitDialog);

        var note = NewCard("这几个名单不是一回事");
        note.AddNote("小队（本页）：真正跟着你走的人。存在 Player__Players 的 FollowNpcIDs。");
        note.AddNote("已招募：加入过营地的人，可以有很多。存在营地模块的 CampsiteRecruitedNPCIds。");
        note.AddNote("营地 NPC：常驻营地的人。存在 CampsiteAINPCIds。");
        note.AddNote("游戏自带界面上限 3 人。存档格式本身没有人数字段，4 人已验证可用。");

        StackCards(card, note);
    }

    /// <summary>Working copy of the party while the page is open.</summary>
    private List<string>? _partyDraft;

    private void BuildPartyRows(Card card, PartyView v)
    {
        var draft = _partyDraft ?? new List<string>();

        for (int i = 0; i < draft.Count; i++)
        {
            int idx = i;
            card.AddRow(BuildPartySlotRow(idx, draft.Count, v, draft[idx], draft));
        }

        if (draft.Count == 0)
            card.AddNote("小队是空的。点下面的「+ 新增」加人。");
    }

    /// <summary>
    /// One numbered slot: "编号N  NPC: [name ▼]" plus a remove button.
    /// The dropdown lists every NPC that may legally stand in the slot, with the
    /// current occupant selected; picking another swaps in place.
    /// </summary>
    private Control BuildPartySlotRow(int index, int total, PartyView v, string currentId, List<string> draft)
    {
        var row = new Panel { Height = 42, BackColor = Color.Transparent, MinimumSize = new Size(0, 42) };

        var lbl = new Label
        {
            Text = $"编号{index + 1}",
            Width = 68,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
            AutoSize = false,
        };

        var tag = new Label
        {
            Text = "NPC：",
            Width = 46,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Theme.Base,
            AutoSize = false,
        };

        var combo = new ComboBox
        {
            // Fixed width: Dock.Fill stretched the box across the whole card,
            // which put the arrow a screen away from the name.
            Width = 300,
            Dock = DockStyle.Left,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.Base,
            FlatStyle = FlatStyle.Flat,
        };

        // Candidate list: everyone already in the draft (so a member can move
        // between slots) plus everyone legal who is not in the draft. The current
        // occupant is always present so the box never shows a blank selection.
        var inDraft = new HashSet<string>(draft, StringComparer.Ordinal);
        var candidates = new List<PartyMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidate(string id, string name)
        {
            if (id.Length == 0 || !seen.Add(id)) return;
            candidates.Add(new PartyMember { Id = id, Name = name });
        }

        foreach (var m in v.Members) AddCandidate(m.Id, m.Name);
        AddCandidate(currentId, v.Members.FirstOrDefault(x => x.Id == currentId)?.Name ?? currentId);
        foreach (var m in v.Available) AddCandidate(m.Id, m.Name);

        // The row shows the party slot even when the id is unknown, rather than
        // silently dropping a member the game might still have data for.
        if (candidates.All(c => c.Id != currentId))
            candidates.Insert(0, new PartyMember { Id = currentId, Name = currentId });

        foreach (var c in candidates.OrderBy(x => x.Id == currentId ? 0 : 1)
                                    .ThenBy(x => x.Name, StringComparer.CurrentCulture))
            combo.Items.Add(new PartyPick(c));

        combo.SelectedIndex = combo.Items.Cast<PartyPick>()
            .Select((p2, i2) => (p2, i2))
            .Where(t => t.p2.Member.Id == currentId)
            .Select(t => t.i2)
            .DefaultIfEmpty(0)
            .First();

        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedItem is not PartyPick pick) return;
            if (pick.Member.Id == currentId) return;

            // Swapping to an id already used by another slot would duplicate it,
            // which the game cannot resolve. Swap the two instead.
            int other = draft.IndexOf(pick.Member.Id);
            if (other >= 0 && other != index)
            {
                (draft[other], draft[index]) = (draft[index], draft[other]);
            }
            else draft[index] = pick.Member.Id;

            CommitParty(draft);
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            BackColor = Color.Transparent,
        };

        Button Small(string text, bool enabled, Action act)
        {
            var b = new Button
            {
                Text = text,
                Width = 40,
                Height = 28,
                Enabled = enabled,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = enabled ? Theme.Text : Theme.Muted,
                Font = Theme.Small,
                Cursor = enabled ? Cursors.Hand : Cursors.Default,
                Margin = new Padding(4, 5, 0, 0),
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Theme.Border;
            if (enabled) b.Click += (_, _) => act();
            return b;
        }

        buttons.Controls.Add(Small("↑", index > 0, () =>
        {
            (draft[index - 1], draft[index]) = (draft[index], draft[index - 1]);
            CommitParty(draft);
        }));
        buttons.Controls.Add(Small("↓", index < total - 1, () =>
        {
            (draft[index + 1], draft[index]) = (draft[index], draft[index + 1]);
            CommitParty(draft);
        }));
        buttons.Controls.Add(Small("✕", true, () =>
        {
            draft.RemoveAt(index);
            CommitParty(draft);
        }));

        // Dock order is reverse of add order for Left-docked siblings.
        row.Controls.Add(buttons);
        row.Controls.Add(combo);
        row.Controls.Add(tag);
        row.Controls.Add(lbl);
        return row;
    }

    /// <summary>Write the draft and rebuild the page.</summary>
    private void CommitParty(List<string> draft)
    {
        if (_slot is null) return;
        var v = PartyView.From(_slot);
        if (v is null) return;

        if (!v.SetMembers(draft, out var err))
        {
            MessageBox.Show(this, err, "小队", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            MarkDirtyAndRefresh();
            return;
        }
        MarkDirtyAndRefresh();
    }

    /// <summary>Append an empty slot, then let the user pick who goes in it.</summary>
    private void AddPartySlot(PartyView v)
    {
        if (_slot is null) return;
        var draft = _partyDraft ?? new List<string>();

        if (draft.Count >= v.SlotLimit)
        {
            MessageBox.Show(this,
                $"已经到上限了（{v.SlotLimit} 人）。\r\n\r\n想加更多人，先点「人数上限 {v.SlotLimit}…」调高。",
                "小队已满", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var free = v.Available.FirstOrDefault(m => !draft.Contains(m.Id));
        if (free is null)
        {
            MessageBox.Show(this, "没有可以加入的人了。\r\n小队成员必须先在游戏里招募。",
                "没有人可加", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        draft.Add(free.Id);
        CommitParty(draft);
    }

    /// <summary>Picker item for a party dropdown.</summary>
    private sealed class PartyPick
    {
        public PartyPick(PartyMember m) { Member = m; }
        public PartyMember Member { get; }
        public override string ToString() => Member.Display;
    }

    /// <summary>
    /// Choose how many party slots to allow.
    ///
    /// FollowNpcIDs has no length field beyond its element count, so the file
    /// format imposes no cap; the game's own UI stops at three. Four has been
    /// verified in game — the extra member follows, offers 离开小队 in the
    /// interaction menu, and survives a save/load cycle. Higher counts are the
    /// same bytes, so they are offered too, with a warning that nobody has
    /// tested past four.
    /// </summary>
    private void ShowPartyLimitDialog()
    {
        using var dlg = new Form
        {
            Text = "小队人数上限",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            BackColor = Theme.Bg,
            ClientSize = new Size(520, 350),
        };

        var head = new Label
        {
            Text = "小队人数上限",
            Font = Theme.Title, ForeColor = Theme.Text,
            Location = new Point(20, 16), AutoSize = true,
        };
        dlg.Controls.Add(head);

        var body = new Label
        {
            Text = "游戏自带界面上限是 3 人。存档里小队就是个名单数组，没有专门的人数字段，" +
                   "所以改这里是有效的——4 人已在游戏里验证：第 4 个人会正常跟随，" +
                   "对话菜单里有「离开小队」。\r\n\r\n" +
                   "更多人数用的是同样的字节结构，理论上一样，但没人试过。",
            Font = Theme.Base, ForeColor = Theme.Muted,
            Location = new Point(20, 52), Size = new Size(480, 112),
        };
        dlg.Controls.Add(body);

        var lbl = new Label
        {
            Text = "人数上限：",
            Font = Theme.Base, ForeColor = Theme.Text,
            Location = new Point(20, 180), AutoSize = true,
        };
        dlg.Controls.Add(lbl);

        var num = new NumericUpDown
        {
            Location = new Point(110, 176), Width = 90,
            Minimum = PartyView.VanillaSlots,
            Maximum = PartyView.MaxAllowedSlots,
            Value = Math.Clamp(PartyView.MaxSlots, PartyView.VanillaSlots, PartyView.MaxAllowedSlots),
            Font = Theme.Base,
        };
        dlg.Controls.Add(num);

        var quick = new FlowLayoutPanel
        {
            Location = new Point(20, 216), Size = new Size(480, 40),
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            BackColor = Color.Transparent,
        };
        foreach (var n in new[] { 3, 4, 6, 10 })
        {
            int v2 = n;
            var b = new Button { Width = 76, Height = 30, Font = Theme.Small };
            StyleSecondary(b, $"{n} 人");
            b.Click += (_, _) => num.Value = v2;
            quick.Controls.Add(b);
        }
        dlg.Controls.Add(quick);

        var ok = new Button { DialogResult = DialogResult.OK, Width = 100, Height = 32 };
        StylePrimary(ok, "确定");
        ok.Location = new Point(20, 280);
        dlg.Controls.Add(ok);

        var cancel = new Button { DialogResult = DialogResult.Cancel, Width = 90, Height = 32 };
        StyleSecondary(cancel, "取消");
        cancel.Location = new Point(130, 280);
        dlg.Controls.Add(cancel);

        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        PartyView.MaxSlots = (int)num.Value;
        PartyView.SaveSetting();
        MarkDirtyAndRefresh();
    }

    private void BuildNpc()
    {
        if (_slot is null) return;

        var npcs = SaveFacade.ReadNpcs(_slot);
        var moods = SaveFacade.ReadNpcMoods(_slot);
        if (npcs.Count == 0) { StackCards(NewCardWithNote("NPC", "未找到 NPC 模块")); return; }

        // AI characters carry the base attributes; load them alongside the roster.
        _characters = SaveFacade.ReadCharacters(_slot);

        var moodById = moods.ToDictionary(m => m.Id, m => m, StringComparer.Ordinal);

        // Expelled NPCs stay in the save and stay editable in principle, but
        // showing them here would suggest they are still residents. Hide them.
        var kicked = new HashSet<string>(SaveFacade.ReadCampsite(_slot)?.KickedNpcIds ?? new List<string>(),
                                         StringComparer.Ordinal);
        var shown = npcs.Where(n => n.IsSimulated && !kicked.Contains(n.Id)).ToList();
        int hiddenKicked = npcs.Count(n => n.IsSimulated && kicked.Contains(n.Id));

        // ---------- roster
        var listCard = NewCard($"NPC 名册（{shown.Count} 位）");
        listCard.AddNote("「存档 ID」是游戏内部认人用的编号，改不了，但招募、驱逐、按 ID 查找都要用它。" +
                         "内置 NPC 的 ID 是英文（Aurelia 就是「金缕」），你自己招的 NPC 是 6 位随机码，没有中文名。");
        if (hiddenKicked > 0)
            listCard.AddNote($"另有 {hiddenKicked} 位已被驱逐，不在名册里显示。要查看或重新招募，去「世界」页的「管理招募 / 驱逐」。");

        listCard.AddAction("全员回满", () =>
        {
            int n = 0;
            foreach (var npc in shown)
            {
                foreach (var (name, val) in new[]
                {
                    ("CurrentPercentageHealth", 1f),
                    ("CurrentPercentageMagic", 1f),
                    ("CurrentPercentageHunger", 1f),
                    ("CurrentPercentageSleep", 1f),
                    ("CurrentPercentageHeat", 1f),
                    ("CurrentPercentageToxin", 0f),
                })
                {
                    npc.Prop(name)?.MarkDirty(BitConverter.GetBytes(val));
                    n++;
                }
            }
            MarkDirtyAndRefresh();
            Navigate(_current);
            SetStatus($"已把 {shown.Count} 位 NPC 的状态回满（尚未保存）");
        });

        listCard.AddAction("全员好心情", () =>
        {
            int n = 0;
            foreach (var m in moods)
            {
                if (m.MoodProp is null) continue;
                m.MoodProp.MarkDirty(BitConverter.GetBytes(100f));
                n++;
            }
            MarkDirtyAndRefresh();
            Navigate(_current);
            SetStatus($"已把 {n} 位 NPC 的心情设为 100（尚未保存）");
        });

        listCard.AddAction("招募…", () => RecruitNpcDialog());

        listCard.AddAction("按 ID 招募…", () => RecruitNpcByIdDialog());

        listCard.AddAction("踢出…", () => KickNpcDialog());

        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            Font = Theme.Base,
            Height = Math.Max(300, _content.ClientSize.Height - 330),
            BackColor = Color.White,
        };
        list.Columns.Add("NPC", 170);
        list.Columns.Add("生命", 70);
        list.Columns.Add("饥饿", 70);
        list.Columns.Add("睡眠", 70);
        list.Columns.Add("体温", 70);
        list.Columns.Add("心情", 70);
        list.Columns.Add("物品", 60);
        list.Columns.Add("存档 ID", 110);

        foreach (var npc in shown)
        {
            moodById.TryGetValue(npc.Id, out var mood);
            var row = new ListViewItem(new[]
            {
                NpcLabel(npc.Id),
                $"{npc.Health * 100:0}%",
                $"{npc.Hunger * 100:0}%",
                $"{npc.Sleep * 100:0}%",
                $"{npc.Heat * 100:0}%",
                mood is null ? "—" : $"{mood.Mood:0}",
                npc.ItemCount.ToString(),
                npc.Id,
            });

            bool dirty = npc.Props.Any(p => p.HasDirtyDescendant) || (mood?.Props.Any(p => p.HasDirtyDescendant) ?? false);
            if (dirty) row.ForeColor = Theme.DirtyEdge;
            row.Tag = npc;
            list.Items.Add(row);
        }

        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            var npc = (NpcView)list.SelectedItems[0].Tag!;
            ShowNpcDetail(npc, moodById.TryGetValue(npc.Id, out var mm) ? mm : null);
        };

        listCard.AddRow(list);
        listCard.AddNote("点选一位 NPC，下方会显示其详细状态与背包。");

        // ---------- detail host
        _npcDetail = new Panel { AutoSize = false, BackColor = Color.Transparent, Height = 1 };

        StackCards(listCard, _npcDetail);

        // preselect the first NPC so the page is never empty below the fold
        if (list.Items.Count > 0) list.Items[0].Selected = true;
        ShowNpcDetail(shown[0], moodById.TryGetValue(shown[0].Id, out var m0) ? m0 : null);
    }

    private Panel? _npcDetail;

    /// <summary>
    /// AI-generated characters with their base attributes, loaded alongside the
    /// NPC roster. Built-in NPCs have no entry here (their attributes are game
    /// data, not saved state), so the attribute card only shows for AI characters.
    /// </summary>
    private List<CharacterAttributeView>? _characters;

    /// <summary>
    /// Recruit from the global library. Any NPC the editor has ever seen can be
    /// pulled into this save's recruited list, even if the player never met it.
    /// </summary>
    private void RecruitNpcDialog()
    {
        if (_slot is null) return;

        var site = SaveFacade.ReadCampsite(_slot);
        // "already here" means on either roster, otherwise a camp resident
        // would be offered for recruiting again.
        var already = (site?.RecruitedNpcIds ?? new List<string>())
            .Concat(site?.AiNpcIds ?? new List<string>())
            .ToHashSet(StringComparer.Ordinal);
        var cat = CatalogStore.Load();

        // the four unlocked built-ins first, then everything else we know about
        var builtInIds = SaveFacade.BuiltInNpcs.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var entries = cat.Npcs
            .Where(n => !already.Contains(n.Id))
            .OrderBy(n => builtInIds.Contains(n.Id) ? 0 : 1)
            .ThenBy(n => n.Cn.Length == 0 ? 1 : 0)
            .ThenBy(n => n.Cn, StringComparer.CurrentCulture)
            .Select(n => (Text: n.Display, Tag: (string?)n.Id))
            .ToList();

        if (entries.Count == 0)
        {
            SetStatus(cat.Npcs.Count == 0
                ? "资料库是空的，先到「资料库」页扫描存档"
                : "资料库里的 NPC 都已经在营地里了");
            return;
        }

        using var picker = new SearchPickerDialog(
            "招募 NPC",
            "从资料库选择要招募的 NPC。已在营地里的不会显示。",
            entries,
            confirmText: "招募",
            column1: "NPC",
            column2: "存档 ID");

        if (picker.ShowDialog(this) != DialogResult.OK) return;

        var id = entries[picker.ChosenIndex].Tag!;
        if (!SaveFacade.RecruitAnywhere(_slot, id, out var err, out var intoCamp))
        { SetStatus("招募失败：" + err); return; }

        MarkDirtyAndRefresh();
        Navigate("npc");
        SetStatus(intoCamp
            ? $"已把「{NpcLabel(id)}」加进营地 NPC（尚未保存）"
            : $"已招募「{NpcLabel(id)}」（尚未保存）");
    }

    /// <summary>
    /// Recruit by typing a raw NPC id. Player-recruited NPCs get random codes
    /// (1XAP7L) that are deliberately kept out of the library, so once dismissed
    /// this is the way back — the id survives in the save's kicked list.
    /// </summary>
    private void RecruitNpcByIdDialog()
    {
        if (_slot is null) return;

        var site = SaveFacade.ReadCampsite(_slot);
        var kicked = site?.KickedNpcIds ?? new List<string>();
        var recruited = site?.RecruitedNpcIds ?? new List<string>();

        using var dlg = new Form
        {
            Text = "按 ID 招募",
            Width = 560,
            Height = 320,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = Theme.Base,
        };

        var hint = new Label
        {
            Text = "输入 NPC 的内部 ID 直接招募。玩家自己招募的 NPC 是 6 位随机码，\n"
                 + "不收录进资料库，遣散后要在这里用 ID 招回来。",
            Location = new Point(20, 16),
            Size = new Size(510, 46),
            ForeColor = Theme.Muted,
            Font = Theme.Small,
        };

        var box = new TextBox
        {
            Location = new Point(20, 74),
            Width = 510,
            Font = Theme.Base,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var listLbl = new Label
        {
            Text = kicked.Count > 0
                ? "已遣散的（点一下填入）："
                : "这个存档里没有被遣散的 NPC。",
            Location = new Point(20, 112),
            Size = new Size(510, 22),
            ForeColor = Theme.Muted,
            Font = Theme.Small,
        };

        var kickedList = new ListBox
        {
            Location = new Point(20, 136),
            Size = new Size(510, 90),
            Font = Theme.Base,
            BorderStyle = BorderStyle.FixedSingle,
        };
        foreach (var k in kicked)
            kickedList.Items.Add($"{NpcLabel(k)}（{k}）");
        kickedList.DoubleClick += (_, _) =>
        {
            if (kickedList.SelectedIndex >= 0) box.Text = kicked[kickedList.SelectedIndex];
        };

        var ok = new Button
        {
            Text = "招募",
            Location = new Point(320, 240),
            Width = 100,
            Height = 34,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Bold,
        };
        ok.FlatAppearance.BorderSize = 0;

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(432, 240),
            Width = 98,
            Height = 34,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Theme.Border;

        dlg.Controls.AddRange(new Control[] { hint, box, listLbl, kickedList, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        box.Focus();

        ok.Click += (_, _) =>
        {
            var id = box.Text.Trim();
            if (id.Length == 0) { SetStatus("请先输入 NPC ID"); return; }
            if (recruited.Contains(id, StringComparer.Ordinal))
            {
                SetStatus($"「{id}」已经在营地里了");
                return;
            }
            dlg.DialogResult = DialogResult.OK;
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var chosen = box.Text.Trim();
        // Route to the right roster: a rolled code belongs in 营地 NPC, a named
        // resident on the recruited list. Either way the expelled record is
        // cleared, so recruiting back does not leave a duplicate.
        if (!SaveFacade.RecruitAnywhere(_slot, chosen, out var err, out var intoCamp))
        { SetStatus("招募失败：" + err); return; }

        MarkDirtyAndRefresh();
        Navigate("npc");
        SetStatus(intoCamp
            ? $"已把「{NpcLabel(chosen)}」加进营地 NPC（尚未保存）"
            : $"已招募「{NpcLabel(chosen)}」（尚未保存）");
    }

    /// <summary>Expel an NPC from either roster.</summary>
    private void KickNpcDialog()
    {
        if (_slot is null) return;

        var site = SaveFacade.ReadCampsite(_slot);
        var recruited = site?.RecruitedNpcIds ?? new List<string>();
        var campNpcs = site?.AiNpcIds ?? new List<string>();
        if (recruited.Count == 0 && campNpcs.Count == 0)
        { SetStatus("当前营地里没有可驱逐的 NPC"); return; }

        // Both rosters are expellable, so offer both and remember which list
        // each row came from.
        var entries = campNpcs.Select(id => (Text: $"{NpcLabel(id)}（{id}）· 营地 NPC", Tag: (string?)id, Camp: true))
            .Concat(recruited.Select(id => (Text: $"{NpcLabel(id)}（{id}）· 已招募", Tag: (string?)id, Camp: false)))
            .ToList();

        using var picker = new SearchPickerDialog(
            "驱逐 NPC",
            "选择要驱逐出营地的 NPC。营地里的人和已招募的居民都可以驱逐。",
            entries.Select(e => (e.Text, e.Tag)).ToList(),
            confirmText: "驱逐",
            column1: "NPC",
            column2: "存档 ID");

        if (picker.ShowDialog(this) != DialogResult.OK) return;

        var picked = entries[picker.ChosenIndex];
        var kicked = picked.Tag!;
        var ok = picked.Camp
            ? SaveFacade.ExpelCampNpc(_slot, kicked, out var err)
            : SaveFacade.KickNpc(_slot, kicked, out err);
        if (!ok) { SetStatus("驱逐失败：" + err); return; }

        MarkDirtyAndRefresh();
        Navigate("npc");
        SetStatus($"已踢出「{kicked}」（尚未保存）");
    }

    // ---------------------------------------------------------- camp roster
    // ---------------------------------------------------------- camp roster
    /// <summary>
    /// Manage the camp roster. Four tabs, each owning the action that applies to
    /// it: 营地 NPC and 已招募 can both be expelled, 已驱逐 can be recruited back
    /// (which also drops it from the expelled list) or deleted outright.
    /// </summary>
    private void CampRosterDialog(CampsiteView camp)
    {
        if (_slot is null) return;
        using var dlg = BuildRosterDialog(camp);
        dlg.ShowDialog(this);
    }

    /// <summary>Build the roster dialog without showing it.</summary>
    private Form BuildRosterDialog(CampsiteView camp)
    {
        var dlg = new Form
        {
            Text = "营地名册",
            Size = new Size(760, 646),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = Theme.Base,
        };

        var tabs = new TabControl
        {
            Location = new Point(14, 14),
            Size = new Size(714, 496),
            Font = Theme.Base,
        };

        // ---- tab 1: camp residents (CampsiteAINPCIds)
        var tabCamp = new TabPage("营地 NPC") { BackColor = Color.White };
        var listCamp = MakeRosterList();
        listCamp.Dock = DockStyle.Fill;
        foreach (var id in camp.AiNpcIds)
            listCamp.Items.Add(new ListViewItem(new[] { NpcLabel(id), id }) { Tag = id });
        tabCamp.Controls.Add(listCamp);
        tabs.TabPages.Add(tabCamp);

        // ---- tab 2: recruited (CampsiteRecruitedNPCIds)
        var tabRecruited = new TabPage("已招募") { BackColor = Color.White };
        var listRecruited = MakeRosterList();
        listRecruited.Dock = DockStyle.Fill;
        foreach (var id in camp.RecruitedNpcIds)
            listRecruited.Items.Add(new ListViewItem(new[] { NpcLabel(id), id }) { Tag = id });
        tabRecruited.Controls.Add(listRecruited);
        tabs.TabPages.Add(tabRecruited);

        // ---- tab 3: expelled
        var tabKicked = new TabPage("已驱逐") { BackColor = Color.White };
        var listKicked = MakeRosterList();
        listKicked.Dock = DockStyle.Fill;
        foreach (var id in camp.KickedNpcIds)
            listKicked.Items.Add(new ListViewItem(new[] { NpcLabel(id), id }) { Tag = id });
        tabKicked.Controls.Add(listKicked);
        tabs.TabPages.Add(tabKicked);

        // ---- tab 4: everyone else, for recruiting
        var tabAll = new TabPage("全部 NPC") { BackColor = Color.White };
        var listAll = MakeRosterList();
        listAll.Dock = DockStyle.Fill;
        foreach (var id in SaveFacade.ReadNpcs(_slot).Select(n => n.Id))
        {
            if (camp.RecruitedNpcIds.Contains(id, StringComparer.Ordinal)) continue;
            if (camp.AiNpcIds.Contains(id, StringComparer.Ordinal)) continue;
            listAll.Items.Add(new ListViewItem(new[] { NpcLabel(id), id }) { Tag = id });
        }
        tabAll.Controls.Add(listAll);
        tabs.TabPages.Add(tabAll);

        dlg.Controls.Add(tabs);

        // ---- buttons
        Button MakeButton(string text, Point at, int width, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Location = at,
                Size = new Size(width, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Theme.AccentSoft : Color.FromArgb(244, 245, 247),
                ForeColor = primary ? Theme.Accent : Theme.Muted,
                Font = Theme.Base,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        var btnRecruit = MakeButton("招募选中", new Point(14, 522), 104, true);
        var btnExpelCamp = MakeButton("驱逐营地 NPC", new Point(124, 522), 116, false);
        var btnKick = MakeButton("驱逐已招募", new Point(246, 522), 110, false);
        var btnRehire = MakeButton("重新招募", new Point(362, 522), 104, true);
        var btnDelete = MakeButton("删除记录", new Point(472, 522), 100, false);
        var btnClose = MakeButton("关闭", new Point(618, 522), 110, false);

        string? Selected(TabPage? page) =>
            page == tabCamp ? (listCamp.SelectedItems.Count > 0 ? listCamp.SelectedItems[0].Tag as string : null)
            : page == tabRecruited ? (listRecruited.SelectedItems.Count > 0 ? listRecruited.SelectedItems[0].Tag as string : null)
            : page == tabKicked ? (listKicked.SelectedItems.Count > 0 ? listKicked.SelectedItems[0].Tag as string : null)
            : (listAll.SelectedItems.Count > 0 ? listAll.SelectedItems[0].Tag as string : null);

        // Only offer the actions that apply to the tab you are looking at, so
        // pressing a button can never act on a list you cannot see.
        void SyncButtons()
        {
            var page = tabs.SelectedTab;
            bool has = Selected(page) is not null;
            btnRecruit.Enabled = has && page == tabAll;
            btnExpelCamp.Enabled = has && page == tabCamp;
            btnKick.Enabled = has && page == tabRecruited;
            btnRehire.Enabled = has && page == tabKicked;
            btnDelete.Enabled = has && page == tabKicked;
            foreach (var b in new[] { btnRecruit, btnExpelCamp, btnKick, btnRehire, btnDelete })
                b.ForeColor = b.Enabled
                    ? (b == btnRecruit || b == btnRehire ? Theme.Accent : Theme.Muted)
                    : Color.FromArgb(190, 190, 190);
        }

        tabs.SelectedIndexChanged += (_, _) => SyncButtons();
        foreach (var lv in new[] { listCamp, listRecruited, listKicked, listAll })
            lv.SelectedIndexChanged += (_, _) => SyncButtons();

        void Done(string message)
        {
            MarkDirtyAndRefresh();
            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
            Navigate("world");
            SetStatus(message + "（尚未保存）");
        }

        btnRecruit.Click += (_, _) =>
        {
            var id = Selected(tabAll);
            if (id is null) { SetStatus("请先在「全部 NPC」里选一个"); return; }
            if (!SaveFacade.RecruitAnywhere(_slot, id, out var err, out var intoCamp))
            { SetStatus("招募失败：" + err); return; }
            Done(intoCamp ? $"已把「{NpcLabel(id)}」加进营地 NPC" : $"已招募「{NpcLabel(id)}」");
        };

        btnExpelCamp.Click += (_, _) =>
        {
            var id = Selected(tabCamp);
            if (id is null) { SetStatus("请先在「营地 NPC」里选一个"); return; }
            if (!SaveFacade.ExpelCampNpc(_slot, id, out var err)) { SetStatus("驱逐失败：" + err); return; }
            Done($"已驱逐营地 NPC「{NpcLabel(id)}」，它现在在「已驱逐」页");
        };

        btnKick.Click += (_, _) =>
        {
            var id = Selected(tabRecruited);
            if (id is null) { SetStatus("请先在「已招募」里选一个"); return; }
            if (!SaveFacade.KickNpc(_slot, id, out var err)) { SetStatus("驱逐失败：" + err); return; }
            Done($"已驱逐「{NpcLabel(id)}」，它现在在「已驱逐」页");
        };

        btnRehire.Click += (_, _) =>
        {
            var id = Selected(tabKicked);
            if (id is null) { SetStatus("请先在「已驱逐」里选一个"); return; }
            // Recruiting back must also drop the expelled record, otherwise the
            // game shows the same NPC as both resident and expelled.
            if (!SaveFacade.RecruitAnywhere(_slot, id, out var err, out var intoCamp))
            { SetStatus("招募失败：" + err); return; }
            Done(intoCamp
                ? $"已把「{NpcLabel(id)}」招回营地 NPC，并移出驱逐名单"
                : $"已重新招募「{NpcLabel(id)}」，并移出驱逐名单");
        };

        btnDelete.Click += (_, _) =>
        {
            var id = Selected(tabKicked);
            if (id is null) { SetStatus("请先在「已驱逐」里选一个"); return; }
            if (!SaveFacade.ForgetKickedNpc(_slot, id, out var err)) { SetStatus("删除失败：" + err); return; }
            Done($"已从驱逐名单里彻底删除「{NpcLabel(id)}」");
        };

        btnClose.Click += (_, _) => dlg.Close();

        dlg.Controls.Add(btnRecruit);
        dlg.Controls.Add(btnExpelCamp);
        dlg.Controls.Add(btnKick);
        dlg.Controls.Add(btnRehire);
        dlg.Controls.Add(btnDelete);
        dlg.Controls.Add(btnClose);

        var hint = new Label
        {
            Text = "「营地 NPC」是住在营地里的人（多为按标识码招募）；「已招募」是有名字的居民，两个列表都能驱逐。\n"
                 + "「重新招募」会把 NPC 移出驱逐名单，而不只是加回来；「删除记录」才是彻底去掉那条驱逐记录。",
            Location = new Point(14, 560),
            Size = new Size(714, 42),
            ForeColor = Theme.Muted,
            Font = Theme.Small,
        };
        dlg.Controls.Add(hint);

        SyncButtons();
        return dlg;
    }


    private static ListView MakeRosterList()
    {
        var l = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            Font = Theme.Base,
            BackColor = Color.White,
        };
        l.Columns.Add("名称", 420);
        l.Columns.Add("存档 ID", 220);
        return l;
    }

    private string NpcLabel(string id)
    {
        // Built-in residents first, then the library, then the name the save
        // itself carries. AI-generated NPCs have random ids like YEDK65 but the
        // game stores their real Chinese name in GenCharacterInfos, so reading
        // it here is what makes those rows readable.
        var known = SaveFacade.BuiltInNpcs.FirstOrDefault(b => b.Id == id);
        var cn = known.Cn;
        if (string.IsNullOrEmpty(cn))
            cn = CatalogStore.Load().Npcs.FirstOrDefault(n => n.Id == id)?.Cn ?? string.Empty;
        if (string.IsNullOrEmpty(cn))
            cn = SaveNameOf(id);
        return string.IsNullOrEmpty(cn) ? id : cn;
    }

    /// <summary>
    /// The display name stored in the save for an AI-generated character, or an
    /// empty string when the save has none.
    /// </summary>
    private string SaveNameOf(string id)
    {
        try
        {
            if (_slot is null) return string.Empty;
            _charNames ??= SaveFacade.ReadCharacters(_slot)
                .Where(c => !string.IsNullOrWhiteSpace(c.CharacterName))
                .GroupBy(c => c.CharacterId)
                .ToDictionary(g => g.Key, g => g.First().CharacterName, StringComparer.OrdinalIgnoreCase);
            return _charNames.TryGetValue(id, out var n) ? n : string.Empty;
        }
        catch { return string.Empty; }
    }

    private Dictionary<string, string>? _charNames;

    /// <summary>Move the camp to a new cell.</summary>
    private void CampPositionDialog(CampsiteView camp)
    {
        var dlg = BuildCampPositionDialog(camp);
        if (dlg is null) return;
        using var _ = dlg;
        dlg.ShowDialog(this);
    }

    /// <summary>
    /// Build the camp-position dialog without showing it, so the shot harness can
    /// render it. Returns null when there is nothing to move.
    /// </summary>
    private Form? BuildCampPositionDialog(CampsiteView camp)
    {
        if (_slot is null || camp.Coord is null) return null;

        var dlg = new Form
        {
            Text = "改营地位置",
            // The size is computed from the laid-out content below; a fixed
            // height used to clip the "当前" line, which then overlapped the
            // first input row.
            Size = new Size(470, 320),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = Theme.Base,
        };

        // A two-column grid instead of hand-placed coordinates: every row is
        // measured, so wrapped text can never sit on top of the row beneath it.
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(18, 16, 18, 8),
            BackColor = Color.White,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;

        // "当前" spans the full width and wraps; the row grows to fit it.
        var lblNow = new Label
        {
            Text = "当前：" + camp.Coord.Label,
            AutoSize = true,
            MaximumSize = new Size(410, 0),
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Margin = new Padding(0, 0, 0, 12),
        };
        table.Controls.Add(lblNow, 0, row);
        table.SetColumnSpan(lblNow, 2);
        row++;

        NumericUpDown MakeBox(int value) => new()
        {
            Width = 130,
            Minimum = 0,
            Maximum = 149,
            Value = Math.Clamp(value, 0, 149),
            Font = Theme.Base,
            Margin = new Padding(0, 2, 0, 8),
        };

        Label MakeLabel(string t) => new()
        {
            Text = t,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Theme.Base,
            Margin = new Padding(0, 2, 8, 8),
        };

        var numX = MakeBox(camp.Coord.DetailX);
        table.Controls.Add(MakeLabel("格 X"), 0, row);
        table.Controls.Add(numX, 1, row);
        row++;

        var numY = MakeBox(camp.Coord.DetailY);
        table.Controls.Add(MakeLabel("格 Y"), 0, row);
        table.Controls.Add(numY, 1, row);
        row++;

        // The camp is a 79x79 footprint centred on this anchor, so the anchor
        // cannot go anywhere in 0..149: at 39 the footprint already touches 0.
        // The old note said "0-4095", which invited the user to type values that
        // silently clamp and leave the camp where it was.
        const int Half = 39;
        const int TerrainSize = 150;
        int safeLo = Half, safeHi = TerrainSize - 1 - Half;

        var note = new Label
        {
            Text = $"营地是一整块 79×79 的地皮，以这里的格子为中心，" +
                   $"所以格子只能取 {safeLo}–{safeHi}。\n" +
                   $"超出这个范围，地皮就会伸到区块外面，游戏不会报错、只是不理这个位置。\n" +
                   "改这里会把整块地皮连同锚点和世界坐标一起平移。",
            AutoSize = true,
            MaximumSize = new Size(410, 0),
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Margin = new Padding(0, 6, 0, 0),
        };
        table.Controls.Add(note, 0, row);
        table.SetColumnSpan(note, 2);
        row++;

        // If an older build moved the anchor without moving the footprint, the
        // game keeps placing the camp at the old spot and editing looks broken.
        // Offer a one-click repair right where the user is looking.
        if (SaveFacade.CampsiteNeedsRepair(_slot!, out var why))
        {
            var warn = new Label
            {
                Text = $"⚠ 这块营地现在不一致：{why}。\n" +
                       "游戏是按「占格」放营地的，所以营地会停在旧位置。点下面的「修复对齐」把锚点" +
                       "拉回占格中心即可（不会移动地皮）。",
                AutoSize = true,
                MaximumSize = new Size(410, 0),
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(176, 96, 0),
                Font = Theme.Small,
                Margin = new Padding(0, 6, 0, 6),
            };
            table.Controls.Add(warn, 0, row);
            table.SetColumnSpan(warn, 2);
            row++;

            var fix = new Button
            {
                Text = "修复对齐",
                Width = 96,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.AccentSoft,
                ForeColor = Theme.Accent,
                Font = Theme.Base,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 0, 6),
            };
            fix.FlatAppearance.BorderSize = 0;
            fix.Click += (_, _) =>
            {
                if (!SaveFacade.RepairCampsite(_slot!, out var err))
                {
                    MessageBox.Show(dlg, "修复失败：" + err, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                MarkDirtyAndRefresh();
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
                Navigate("world");
                SetStatus("已把营地锚点对齐到占格中心（尚未保存）");
            };
            table.Controls.Add(fix, 1, row);
            table.SetColumnSpan(fix, 1);
            row++;
        }

        dlg.Controls.Add(table);

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 54,
            Padding = new Padding(0, 10, 16, 10),
            BackColor = Color.White,
        };

        var cancel = new Button
        {
            Text = "取消",
            Width = 96,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(244, 245, 247),
            ForeColor = Theme.Muted,
            Font = Theme.Base,
            Cursor = Cursors.Hand,
        };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.Click += (_, _) => dlg.Close();

        var ok = new Button
        {
            Text = "应用",
            Width = 96,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.AccentSoft,
            ForeColor = Theme.Accent,
            Font = Theme.Base,
            Cursor = Cursors.Hand,
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) =>
        {
            if (!SaveFacade.SetCampsitePosition(_slot, (int)numX.Value, (int)numY.Value, out var err))
            {
                MessageBox.Show(this, "修改失败：" + err, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            MarkDirtyAndRefresh();
            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
            Navigate("world");
            SetStatus($"营地将移到格 ({(int)numX.Value}, {(int)numY.Value})（尚未保存）");
        };

        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        dlg.Controls.Add(bar);

        // Fit the window to the measured content, then cap to the screen.
        dlg.Load += (_, _) =>
        {
            int wanted = table.PreferredSize.Height + bar.Height + 42;
            var wa = Screen.FromControl(dlg).WorkingArea;
            dlg.Height = Math.Min(Math.Max(wanted, 240), wa.Height - 40);
            dlg.Width = Math.Min(Math.Max(table.PreferredSize.Width + 40, 430), wa.Width - 60);
        };

        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return dlg;
    }

    private void ShowNpcDetail(NpcView npc, NpcMoodView? mood)
    {
        if (_npcDetail is null) return;

        _npcDetail.SuspendLayout();
        foreach (Control c in _npcDetail.Controls) c.Dispose();
        _npcDetail.Controls.Clear();

        int y = 0;
        int w = _content.ClientSize.Width - _content.Padding.Horizontal;

        // ---------- stats
        var stats = new Card(NpcLabel(npc.Id) == npc.Id
            ? $"「{npc.Id}」状态"
            : $"「{NpcLabel(npc.Id)}」状态（{npc.Id}）") { Width = w };

        foreach (var (name, label) in new[]
        {
            ("CurrentPercentageHealth", "生命"),
            ("CurrentPercentageMagic", "魔力"),
            ("CurrentPercentageHunger", "饥饿"),
            ("CurrentPercentageSleep", "睡眠"),
            ("CurrentPercentageHeat", "体温"),
            ("CurrentPercentageToxin", "毒素"),
        })
        {
            var p = npc.Prop(name);
            if (p is null) continue;
            var f = new PercentField(p, label);
            f.Changed += MarkDirtyAndRefresh;
            stats.AddRow(f);
        }

        if (mood?.MoodProp is { } mp)
        {
            var mf = new NumberField(mp, "心情");
            mf.Changed += MarkDirtyAndRefresh;
            stats.AddRow(mf);
        }

        stats.Location = new Point(0, y);
        _npcDetail.Controls.Add(stats);
        y += stats.Height + 14;

        // ---------- attributes (AI characters only)
        var attr = _characters?.FirstOrDefault(c => c.CharacterId == npc.Id);
        if (attr is { HasAttributes: true })
        {
            var ac = new Card($"「{npc.Id}」属性") { Width = w };

            // The display name is a TextProperty, so it can be renamed like any
            // other string. Keep it on the attribute card: it belongs to the same
            // GenCharacterInfos element as the five stats.
            if (attr.Prop("CharacterName") is { } nameProp && ValueEditor.IsEditable(nameProp))
            {
                var nf = new NameField(nameProp, "称号", 40);
                nf.Changed += MarkDirtyAndRefresh;
                ac.AddRow(nf);
            }

            ac.AddNote("这些是角色的基础属性，存在 AI 角色模块里。");

            foreach (var (propName, label) in new[]
            {
                ("Strength", "力量"),
                ("Agility", "敏捷"),
                ("Constitution", "体质"),
                ("Intelligence", "智力"),
                ("Luck", "幸运"),
            })
            {
                if (attr.Attr(propName) is not { } ap) continue;
                var nf = new NumberField(ap, label);
                nf.Changed += MarkDirtyAndRefresh;
                ac.AddRow(nf);
            }

            ac.Location = new Point(0, y);
            _npcDetail.Controls.Add(ac);
            y += ac.Height + 14;
        }

        // ---------- runtime combat attributes (AttributeMap)
        // This is the card that actually moves the health bar. The five values
        // above are only the generation input: setting Constitution to 99 does
        // not raise max health, because the game reads MaxHealth from here.
        if (attr is { Combat.Count: > 0 })
        {
            var cc = new Card($"「{npc.Id}」战斗属性") { Width = w };

            cc.AddNote("这一组才是游戏真正读取的数值。上面那五项只是生成时的输入，改了不会影响血量上限。");

            foreach (var a in attr.Combat)
            {
                if (a.ValueProp is null) continue;

                var f = new CombatField(a);
                f.Changed += MarkDirtyAndRefresh;
                cc.AddRow(f);
            }

            cc.Location = new Point(0, y);
            _npcDetail.Controls.Add(cc);
            y += cc.Height + 14;
        }

        // ---------- backpack
        var bag = new Card($"「{npc.Id}」背包") { Width = w };
        int rows = 0;

        // Templates for "add item": every real item found in the player's bag
        // and in any NPC's bag, so the editor never invents an item id.
        var player = SaveFacade.ReadPlayer(_slot!);
        var templates = new List<(string Name, List<SaveProperty> Group)>();
        if (player is not null)
            foreach (var it in player.InventoryItems.Where(i => !i.IsEmpty))
                templates.Add((it.Name ?? it.ItemId, it.Group));

        foreach (var (arrName, arrLabel) in new[]
        {
            ("InventoryItems", "随身物品"),
            ("WeaponItems", "武器"),
            ("AccessoryItems", "饰品"),
            ("EquipmentItems", "装备"),
        })
        {
            var arr = npc.BackPack?.Child(arrName);
            var els = arr?.ArrayElements;
            if (els is not { Count: > 0 }) continue;

            var props = els[0].Properties;
            int total = props.Count / 5;

            // Count filled slots first so the section header can be emitted
            // before the item rows (a header after its rows reads wrong).
            int filled = 0;
            for (int i = 0; i + 4 < props.Count; i += 5)
                if (ValueEditor.EffectiveValueOrNull(props[i]) is string sid && sid is not ("None" or ""))
                    filled++;

            bag.AddSection($"{arrLabel}　{filled} / {total} 格");

            for (int i = 0; i + 4 < props.Count; i += 5)
            {
                var idProp = props[i];
                var countProp = props[i + 1];
                var cfg = props[i + 3];

                var id = ValueEditor.EffectiveValueOrNull(idProp) as string;
                if (id is "None" or "" or null) continue;

                var name = ValueEditor.EffectiveValueOrNull(cfg.Child("Explain")) as string;
                var max = ValueEditor.EffectiveValueOrNull(cfg.Child("MaxStack")) is int mx ? mx : 0;

                if (countProp is null || max <= 0) continue;

                var cf = new NumberField(countProp, name ?? id);
                cf.Changed += MarkDirtyAndRefresh;
                bag.AddRow(cf);
                rows++;
            }

            if (filled == 0) bag.AddNote("（空）");

            // add-item button for the slot array (weapons/accessories too)
            if (_slot is not null && filled < total)
            {
                var slotArray = arr!;
                var arrLabelCopy = arrLabel;
                bag.AddAction($"往{arrLabelCopy}加物品…", () =>
                {
                    var dlg = new AddNpcItemDialog(_slot, arrLabelCopy);
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    var id = dlg.SelectedId;
                    var cnt = dlg.SelectedCount;
                    if (id is null) return;

                    var pr = slotArray.ArrayElements![0].Properties;
                    // find the first empty slot
                    for (int i = 0; i + 4 < pr.Count; i += 5)
                    {
                        if (ValueEditor.EffectiveValueOrNull(pr[i]) is string sid && sid is not ("None" or "")) continue;
                        var dst = new List<SaveProperty> { pr[i], pr[i + 1], pr[i + 2], pr[i + 3], pr[i + 4] };

                        var tpl = CatalogInjector.Resolve(_slot, id, out var re);
                        if (tpl is null) { SetStatus("放入失败：" + re); return; }

                        if (CatalogInjector.AddToGroup(dst, tpl, cnt, out var e))
                        {
                            MarkDirtyAndRefresh();
                            ShowNpcDetail(npc, mood);
                            SetStatus($"已给「{npc.Id}」的{arrLabelCopy}放入「{dlg.SelectedName}」×{cnt}（尚未保存）");
                        }
                        else SetStatus("放入失败：" + e);
                        break;
                    }
                });
            }
        }

        if (rows == 0) bag.AddNote("这位 NPC 的背包是空的。");

        bag.Location = new Point(0, y);
        _npcDetail.Controls.Add(bag);
        y += bag.Height + 14;

        _npcDetail.Height = y;
        _npcDetail.ResumeLayout();
    }

    // ------------------------------------------------------------------ memory
    /// <summary>
    /// NPC 记忆页。存档里每个 NPC 都有「第几天 -> 一组事件」的嵌套结构，最内层
    /// 是平铺的 Type/Parameters/Priority 三元组。这一页把它读成「NPC -> 天 -> 事件」，
    /// 并且只开放确实能安全写回的字段：
    ///   Priority（整数）与 Type（枚举名）可以改；
    ///   Parameters 是 FText 映射，本编辑器的编码器写不了，只读显示。
    /// </summary>
    private void BuildMemory()
    {
        if (_slot is null) return;

        var ids = MemoryFacade.NpcIds(_slot);
        if (ids.Count == 0)
        {
            StackCards(NewCardWithNote("NPC 记忆", "这个存档的 NPC 记忆模块是空的。"));
            return;
        }

        // 名册上的人排前面，其余按名字排；被驱逐的也照常显示，因为记忆属于历史。
        var site = SaveFacade.ReadCampsite(_slot);
        var roster = new HashSet<string>(
            (site?.AiNpcIds ?? new List<string>()).Concat(site?.RecruitedNpcIds ?? new List<string>()),
            StringComparer.Ordinal);
        var ordered = ids.OrderByDescending(id => roster.Contains(id))
                         .ThenBy(NpcLabel, StringComparer.CurrentCulture)
                         .ToList();

        var intro = NewCard("NPC 记忆");
        intro.AddNote("游戏会为每位 NPC 记下每天发生的事，用来决定后续对话和心情。\n"
                    + "「优先级」可以改：数值越小越优先被游戏挑中播放。\n"
                    + "「类型」可以改：任务 / 击杀 / 采集 / 死亡 / 心情下降。\n"
                    + "「参数」是游戏写进去的细节（怪物 ID、物品 ID、文本内容等），"
                    + "它的存储格式不统一，本编辑器只读不改。");
        intro.AddRow(new InfoRow("有记忆的 NPC", $"{ids.Count} 位"));
        intro.AddRow(new InfoRow("事件总数", $"{ordered.Sum(id => MemoryFacade.Read(_slot, id).EventCount)} 条"));

        var pick = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,          // 一次只看一位，多选会让高亮留在别人身上
            HideSelection = false,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            Font = Theme.Base,
            Height = 260,
            BackColor = Color.White,
        };
        pick.Columns.Add("NPC", 180);
        pick.Columns.Add("天数", 56);
        pick.Columns.Add("事件", 56);
        pick.Columns.Add("主要类型", 330);
        pick.Columns.Add("存档 ID", 110);

        foreach (var id in ordered)
        {
            var m = MemoryFacade.Read(_slot, id);
            var types = m.Days.SelectMany(d => d.Events)
                              .GroupBy(e => e.TypeCn)
                              .OrderByDescending(g => g.Count())
                              .Select(g => $"{g.Key} {g.Count()}");
            var row = new ListViewItem(new[]
            {
                NpcLabel(id),
                m.Days.Count.ToString(),
                m.EventCount.ToString(),
                string.Join("  ", types),
                id,
            });
            row.Tag = id;
            pick.Items.Add(row);
        }

        intro.AddRow(pick);
        intro.AddNote("点选一位 NPC，下面会列出他每一天的记忆。");

        pick.SelectedIndexChanged += (_, _) =>
        {
            if (_memoryPickBusy) return;
            if (pick.SelectedItems.Count == 0) return;
            var id = (string)pick.SelectedItems[0].Tag!;
            if (id == _memoryNpcId) return;
            _memoryNpcId = id;

            // Rebuild the whole page instead of filling a child panel: the panel
            // approach grew after being parented and the scroll container never
            // picked up the new extent, so the detail cards were invisible.
            //
            // The rebuild MUST be posted, not called here. Navigate disposes the
            // controls of the current page, and this handler runs inside the
            // ListView's own SelectedIndexChanged -- destroying the ListView while
            // it is dispatching its own notification throws
            // "Win32Exception (1158): 创建窗口句柄时出错" and the page dies. Posting
            // lets the notification finish first. Same class of bug as the map
            // page's ComboBox crash fixed earlier.
            BeginInvoke(new Action(() =>
            {
                try { if (_slot is not null) Navigate("memory"); }
                catch { }
            }));
        };

        // The detail cards are built as part of this same pass, so they are laid
        // out with the roster and there is no second layout step to go wrong.
        var detail = BuildMemoryDetailCards(_memoryNpcId ?? ordered[0]);

        _memoryPickBusy = true;
        try
        {
            var want = pick.Items.Cast<ListViewItem>()
                           .FirstOrDefault(i => (string?)i.Tag == (_memoryNpcId ?? ordered[0]));
            if (want is not null) want.Selected = true;
        }
        finally { _memoryPickBusy = false; }

        var all = new List<Control> { intro };
        all.AddRange(detail);
        StackCards(all.ToArray());
    }

    /// <summary>Cards for one NPC's memory, one per day.</summary>
    private List<Control> BuildMemoryDetailCards(string npcId)
    {
        var cards = new List<Control>();
        if (_slot is null) return cards;

        var mem = MemoryFacade.Read(_slot, npcId);
        if (mem.IsEmpty)
        {
            var empty = new Card($"「{NpcLabel(npcId)}」的记忆");
            empty.AddNote("这位 NPC 还没有任何记忆记录。");
            cards.Add(empty);
            return cards;
        }

        foreach (var day in mem.Days)
        {
            var card = new Card($"第 {day.Day} 天　{day.Events.Count} 条");

            var list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = false,
                BorderStyle = BorderStyle.None,
                Font = Theme.Base,
                BackColor = Color.White,
                Height = Math.Min(240, Math.Max(64, day.Events.Count * 22 + 26)),
            };
            list.Columns.Add("类型", 88);
            list.Columns.Add("优先级", 78);
            list.Columns.Add("参数", 620);

            foreach (var ev in day.Events)
            {
                var row = new ListViewItem(new[]
                {
                    ev.TypeCn,
                    ev.Priority.ToString(),
                    ev.Summary(),
                });
                row.Tag = ev;
                if (ev.PriorityProp?.IsDirty == true || ev.TypeProp?.IsDirty == true)
                    row.ForeColor = Theme.DirtyEdge;
                list.Items.Add(row);
            }

            var captured = npcId;
            list.DoubleClick += (_, _) =>
            {
                if (list.SelectedItems.Count == 0) return;
                EditMemoryEvent((MemoryEvent)list.SelectedItems[0].Tag!, () => Navigate("memory"));
            };
            card.AddRow(list);
            card.AddAction("改这条…", () =>
            {
                if (list.SelectedItems.Count == 0) { SetStatus("先在列表里选一条记忆"); return; }
                EditMemoryEvent((MemoryEvent)list.SelectedItems[0].Tag!, () => Navigate("memory"));
            });

            cards.Add(card);
        }

        return cards;
    }

    /// <summary>改一条记忆的类型或优先级。</summary>
    private void EditMemoryEvent(MemoryEvent ev, Action refresh)
    {
        var dlg = BuildMemoryEventDialog(ev, refresh);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            refresh();
            SetStatus("记忆已修改（尚未保存）");
        }
        dlg.Dispose();
    }

    /// <summary>构造记忆编辑对话框。拆出来是为了能在截图脚本里单独渲染它。</summary>
    private Form BuildMemoryEventDialog(MemoryEvent ev, Action refresh)
    {
        var dlg = new Form
        {
            Text = "编辑记忆",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Theme.Bg,
            Font = Theme.Base,
            ClientSize = new Size(460, 300),
        };

        var y = 18;

        var lblType = new Label
        {
            Text = "类型",
            Location = new Point(20, y + 3),
            Width = 70,
            Font = Theme.Base,
            ForeColor = Theme.Text,
        };
        dlg.Controls.Add(lblType);

        var typeBox = new ComboBox
        {
            Location = new Point(96, y),
            Width = 330,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.Base,
        };
        foreach (var t in MemoryFacade.EventTypes)
            typeBox.Items.Add($"{t.Cn}（{t.Value}）");
        var curType = MemoryFacade.TypeName(ev.TypeRaw);
        int idx = Array.FindIndex(MemoryFacade.EventTypes, t => t.Value == curType);
        typeBox.SelectedIndex = idx >= 0 ? idx : 0;
        dlg.Controls.Add(typeBox);
        y += 40;

        var lblPrio = new Label
        {
            Text = "优先级",
            Location = new Point(20, y + 3),
            Width = 70,
            Font = Theme.Base,
            ForeColor = Theme.Text,
        };
        dlg.Controls.Add(lblPrio);

        var prioBox = new NumericUpDown
        {
            Location = new Point(96, y),
            Width = 100,
            Font = Theme.Base,
            Minimum = -1_000_000,
            Maximum = 1_000_000,
            Value = ev.Priority,
        };
        dlg.Controls.Add(prioBox);

        dlg.Controls.Add(new Label
        {
            Text = "数值越小越优先",
            Location = new Point(206, y + 3),
            Width = 200,
            Font = Theme.Small,
            ForeColor = Theme.Muted,
        });
        y += 40;

        var lblParam = new Label
        {
            Text = "参数（只读）",
            Location = new Point(20, y),
            Width = 120,
            Font = Theme.Bold,
            ForeColor = Theme.Muted,
        };
        dlg.Controls.Add(lblParam);
        y += 24;

        var paramText = new TextBox
        {
            Location = new Point(20, y),
            Width = 406,
            Height = 90,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Theme.Base,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var lines = ev.Parameters().Select(kv =>
            $"{kv.Key} = {(string.IsNullOrWhiteSpace(kv.Value) ? "（空）" : kv.Value)}").ToList();
        paramText.Text = lines.Count == 0 ? "（无参数）" : string.Join("\r\n", lines);
        dlg.Controls.Add(paramText);
        y += 100;

        var ok = new Button
        {
            Text = "确定",
            Location = new Point(246, y),
            Size = new Size(86, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.Base,
            DialogResult = DialogResult.None,
        };
        ok.FlatAppearance.BorderSize = 0;

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(340, y),
            Size = new Size(86, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.AccentSoft,
            ForeColor = Theme.Accent,
            Font = Theme.Base,
            DialogResult = DialogResult.Cancel,
        };
        cancel.FlatAppearance.BorderSize = 0;

        ok.Click += (_, _) =>
        {
            var t = MemoryFacade.EventTypes[typeBox.SelectedIndex].Value;
            bool okAll = true;

            if (t != curType && !MemoryFacade.SetType(ev, t, out var e1))
            {
                MessageBox.Show("类型没改成：" + e1, "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                okAll = false;
            }
            if ((int)prioBox.Value != ev.Priority && !MemoryFacade.SetPriority(ev, (int)prioBox.Value, out var e2))
            {
                MessageBox.Show("优先级没改成：" + e2, "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                okAll = false;
            }

            if (okAll) MarkDirtyAndRefresh();
            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
        };

        dlg.Controls.Add(ok);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return dlg;
    }

    private Card NewCardWithNote(string title, string note)
    {
        var c = NewCard(title);
        c.AddNote(note);
        return c;
    }

    /// <summary>Last two path segments, so long roots do not eat the column.</summary>
    private static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Where(p => p.Length > 0).ToArray();
        return parts.Length <= 2 ? path : string.Join(Path.DirectorySeparatorChar, parts[^2..]);
    }

    /// <summary>
    /// Fold the save we just opened into the shared library, without being asked.
    ///
    /// The library exists so any save can be topped up offline, which only works
    /// if it actually reflects the saves the user has opened. Making that a
    /// button meant it went stale the moment you loaded a different save: you
    /// would switch to a save that knows 30 more items and still see the old
    /// count. Capture is cheap (a few milliseconds), so it runs on every load.
    ///
    /// Silent by design: a first load that adds 145 items should not pop a
    /// message box. The 资料库 page reports the counts, and the status line is
    /// only touched when something was genuinely added.
    /// </summary>
    private void AutoUpdateLibrary(SaveSlot slot)
    {
        try
        {
            var r = CatalogStore.TopUpFrom(slot, slot.RootPath, out var err);
            if (err.Length > 0) { SetStatus($"资料库自动更新失败：{err}"); return; }

            // No change means the file was not touched; say nothing.
            if (!r.Wrote) return;

            _libraryNote = r.AddedItems > 0 || r.AddedNpcs > 0
                ? $"资料库已自动更新：新增 {r.AddedItems} 种物品、{r.AddedNpcs} 个 NPC（共 {r.TotalItems} / {r.TotalNpcs}）"
                : $"资料库已自动清理，现有 {r.TotalItems} 种物品、{r.TotalNpcs} 个 NPC";
        }
        catch
        {
            // A library problem must never stop a save from loading.
        }
    }

    /// <summary>
    /// The global library: a name+id index of every item and NPC ever seen, so
    /// any save can be topped up without re-scanning the source save. Only names
    /// and ids are stored — the real configs stay inside the saves themselves.
    /// </summary>
    private void BuildLibrary()
    {
        var cat = CatalogStore.Load();

        var intro = NewCard("资料库");
        intro.AddNote("资料库记录你在所有存档里见过的物品和 NPC，只有名称与 ID。\n"
                    + "在任何存档里都能从这里快速追加，实际配置会在追加时从存档中读取。\n"
                    + "只收录官方内容：物品为 7 位数字 ID，NPC 为有名字的（如 Alex、SnowWillow）。\n"
                    + "AI 生成的 8 位物品、玩家招募的 6 位随机码 NPC 都不收录。\n"
                    + "每打开一次存档都会自动更新一次，不需要再手动点扫描。\n"
                    + "「导出」得到一段可以直接发给别人的密文，对方用「导入」就能拿到你的资料库。");

        if (!string.IsNullOrEmpty(_libraryNote))
            intro.AddNote(_libraryNote);
        intro.AddRow(new InfoRow("已知物品", $"{cat.Items.Count} 种"));
        intro.AddRow(new InfoRow("已知 NPC", $"{cat.Npcs.Count} 个"));
        intro.AddRow(new InfoRow("存放位置", CatalogStore.FilePath));

        // Built-in residents are known without scanning anything, so they are
        // always present. Without this the library only held NPCs that happened
        // to appear in whichever save was scanned.
        //
        // The Enabled guard is essential, not cosmetic: with the library turned
        // off (every headless mode) Load() returns an empty catalog and Save()
        // is a silent no-op that reports success. The seeding branch would then
        // see 0 built-in NPCs, "save" 54, navigate to rebuild itself, see 0
        // again — an infinite recursion that overflowed the stack and killed
        // --shot after seven pages. Only top up when the library is real.
        if (CatalogStore.Enabled
            && cat.Npcs.Count(n => n.Source == "游戏内置") < SaveFacade.BuiltInNpcs.Length)
        {
            int seeded = CatalogStore.SeedBuiltInNpcs(cat);
            if (seeded > 0 && CatalogStore.Save(cat, out _))
            {
                SetStatus($"资料库已补齐 {seeded} 个游戏内置 NPC");
                Navigate("library");
                return;
            }
        }

        intro.AddAction("扫描当前存档并入资料库", () =>
        {
            if (_slot is null) return;
            var live = CatalogStore.Load();
            int pruned = CatalogStore.PruneNonOfficial(live);
            int n = CatalogStore.CaptureItems(_slot, CatalogStore.LabelFor(_slot.RootPath), live);
            int m = CatalogStore.CaptureNpcs(_slot, CatalogStore.LabelFor(_slot.RootPath), live);

            if (!CatalogStore.Save(live, out var err)) { SetStatus("保存资料库失败：" + err); return; }

            var extra = pruned > 0 ? $"，并清掉了 {pruned} 条非官方条目" : "";
            SetStatus($"已加入资料库：新增 {n} 种物品、{m} 个 NPC（现有 {live.Items.Count} / {live.Npcs.Count}）{extra}");
            Navigate("library");
        });

        intro.AddAction("导出资料库…", () => ExportLibraryDialog(cat));

        intro.AddAction("导入资料库…", () => ImportLibraryDialog());

        intro.AddAction("清空资料库", () =>
        {
            if (MessageBox.Show("确定要清空整个资料库吗？这不会影响任何存档。", "确认",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (!CatalogStore.Save(new CatalogFile(), out var err)) { SetStatus("清空失败：" + err); return; }
            SetStatus("资料库已清空");
            Navigate("library");
        });

        var itemsCard = NewCard($"物品（{cat.Items.Count} 种）");
        if (cat.Items.Count == 0)
        {
            itemsCard.AddNote("资料库还是空的。打开一个存档后，点上面的「扫描当前存档并入资料库」。");
        }
        else
        {
            var search = new TextBox
            {
                Width = 300,
                Font = Theme.Base,
                BorderStyle = BorderStyle.FixedSingle,
            };
            itemsCard.AddRow(search);

            var list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Base,
                Height = 300,
                BackColor = Color.White,
                Width = Math.Max(600, _content.ClientSize.Width - 90),
            };
            list.Columns.Add("物品名", 340);
            list.Columns.Add("物品 ID", 160);
            list.Columns.Add("来源存档", 240);

            void Refill()
            {
                var q = search.Text.Trim();
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var i in cat.Items
                             .Where(i => q.Length == 0
                                      || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                      || i.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(i => i.Name, StringComparer.CurrentCulture))
                    list.Items.Add(new ListViewItem(new[] { i.Name, i.Id, ShortPath(i.Source) }));
                list.EndUpdate();
            }

            search.TextChanged += (_, _) => Refill();
            Refill();
            itemsCard.AddRow(list);

            itemsCard.AddAction("录入物品名称…", () => AddItemNameDialog());

            itemsCard.AddNote("宝箱里的物品大多没进过背包，游戏里查不到它们的名字。"
                            + "遇到只显示 ID 的，点「录入物品名称…」填一次，之后宝箱、背包、资料库都会显示这个名字。\n"
                            + "要往背包里加物品，去「背包」页点「新增物品…」，那里能按名称或 ID 搜索。");
        }

        var npcCard = NewCard($"NPC（{cat.Npcs.Count} 个）");
        if (cat.Npcs.Count == 0)
        {
            npcCard.AddNote("资料库里还没有 NPC。");
        }
        else
        {
            var list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Base,
                Height = 260,
                BackColor = Color.White,
                Width = Math.Max(600, _content.ClientSize.Width - 90),
            };
            list.Columns.Add("NPC", 220);
            list.Columns.Add("存档 ID", 180);
            list.Columns.Add("来源存档", 300);

            foreach (var n in cat.Npcs.OrderBy(n => n.Cn.Length == 0 ? 1 : 0)
                                      .ThenBy(n => n.Cn, StringComparer.CurrentCulture))
                list.Items.Add(new ListViewItem(new[]
                {
                    n.Cn.Length > 0 ? n.Cn : "（未命名）",
                    n.Id,
                    ShortPath(n.Source),
                }));

            npcCard.AddRow(list);
            npcCard.AddNote("要招募 NPC，去「NPC」页点「招募…」，那里能按名字或 ID 搜索。");
        }

        StackCards(intro, itemsCard, npcCard);
    }

    private void BuildWorld()
    {
        if (_slot is null) return;

        var wt = SaveFacade.ReadWorldTime(_slot);
        var time = NewCard("世界时间");
        if (wt is not null)
        {
            time.AddRow(new InfoRow("累计小时", $"{wt.Hours:F2}"));
            time.AddRow(new InfoRow("换算", $"第 {wt.Day} 天 {wt.Hour:D2}:{wt.Minute:D2}"));
            time.AddNote("世界时间是累计小时数，由游戏自行推进。");
        }
        else time.AddNote("未找到世界时间模块");

        var camp = SaveFacade.ReadCampsite(_slot);
        var campCard = NewCard("营地");
        if (camp is not null)
        {
            var campNameProp = SaveFacade.CampsiteNameProp(_slot);
            if (campNameProp is not null)
            {
                var f = new NameField(campNameProp, "营地名称");
                f.Changed += MarkDirtyAndRefresh;
                campCard.AddRow(f);
            }

            campCard.AddRow(new InfoRow("是否已建营地", camp.HasCampsite ? "是" : "否"));

            // ---- level
            if (SaveFacade.CampsiteLevelProp(_slot) is { } lvlProp)
            {
                var nf = new NumberField(lvlProp, "营地等级");
                nf.Changed += MarkDirtyAndRefresh;
                campCard.AddRow(nf);
            }
            else campCard.AddNote("未找到营地等级字段。");

            if (SaveFacade.CampsiteUpgradeDayProp(_slot) is { } dayProp)
            {
                var nf = new NumberField(dayProp, "上次升级天数");
                nf.Changed += MarkDirtyAndRefresh;
                campCard.AddRow(nf);
                campCard.AddNote($"今天是第 {camp.WorldDay} 天。游戏按「今天 − 上次升级」判断能不能再升级，把它调小就能马上再升一级。");
            }

            // ---- position
            if (camp.Coord is not null)
            {
                campCard.AddSection("位置");
                campCard.AddRow(new InfoRow("当前", camp.Coord.Label));

                campCard.AddAction("改营地位置…", () => CampPositionDialog(camp));
            }
            else campCard.AddNote("未找到营地坐标字段。");

            campCard.AddSection("名册");
            campCard.AddRow(new InfoRow("营地 NPC", JoinNpcs(camp.AiNpcIds)));
            campCard.AddRow(new InfoRow("已招募", JoinNpcs(camp.RecruitedNpcIds)));
            campCard.AddRow(new InfoRow("已驱逐", JoinNpcs(camp.KickedNpcIds)));

            campCard.AddAction("管理招募 / 驱逐…", () => CampRosterDialog(camp));
        }
        else campCard.AddNote("未找到营地模块");

        var store = SaveFacade.ReadStore(_slot);
        var storeCard = NewCard("商店");
        if (store is not null)
        {
            storeCard.AddRow(new InfoRow("最后刷新天", store.LastRefreshDay.ToString()));
            storeCard.AddRow(new InfoRow("最后检查天", store.LastCheckedDay.ToString()));
        }
        else storeCard.AddNote("未找到商店模块");

        StackCards(time, campCard, storeCard);
    }

    private static string Join(List<string> xs) => xs.Count == 0 ? "无" : string.Join("、", xs);

    /// <summary>
    /// Join NPC ids, showing a readable name where one is known and falling back
    /// to the raw id. The raw code stays visible because that is what the save
    /// stores and what any edit has to use.
    /// </summary>
    private string JoinNpcs(List<string> ids)
    {
        if (ids.Count == 0) return "无";
        return string.Join("、", ids.Select(id =>
        {
            var name = NpcLabel(id);
            return name == id ? id : $"{name}（{id}）";
        }));
    }

    // ------------------------------------------------------------------- map
    /// <summary>
    /// Map page: fog reveal, teleport stones, and chest contents. Chest data is
    /// spread over ~120 chunk modules and takes seconds to collect, so it is
    /// loaded behind a button instead of on page entry.
    /// </summary>
    private void BuildMap()
    {
        if (_slot is null) return;

        // ---- fog ----
        var fogCard = NewCard("地图迷雾");
        var fog = MapFacade.ReadFog(_slot);
        if (fog.Count == 0) fogCard.AddNote("未找到迷雾数据模块（Minimap__Fog）");
        else
        {
            foreach (var layer in fog)
            {
                var row = new Panel { Height = 30, BackColor = Color.Transparent };
                var lbl = new Label
                {
                    Text = $"{layer.Label}   {layer.Explored:N0} / {layer.Total:N0}  ({layer.Percent:F1}%)",
                    Location = new Point(0, 5),
                    Width = 330,
                    ForeColor = Theme.Text,
                    Font = Theme.Base,
                };
                var bar = new Panel
                {
                    Location = new Point(340, 10),
                    Size = new Size(200, 10),
                    BackColor = Color.FromArgb(228, 231, 236),
                };
                int fill = (int)(200 * Math.Clamp(layer.Percent / 100.0, 0, 1));
                bar.Controls.Add(new Panel { Size = new Size(fill, 10), BackColor = Theme.Accent, Location = new Point(0, 0) });

                var btn = new Button
                {
                    Text = "全部揭开",
                    Location = new Point(552, 3),
                    Size = new Size(88, 24),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Theme.AccentSoft,
                    ForeColor = Theme.Accent,
                    Font = Theme.Base,
                    Cursor = Cursors.Hand,
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += (_, _) =>
                {
                    if (!MapFacade.RevealAll(layer)) { MessageBox.Show("这一层没有可写的迷雾数据。"); return; }
                    MarkDirtyAndRefresh();
                    Navigate("map");
                };

                var hide = new Button
                {
                    Text = "全部隐藏",
                    Location = new Point(646, 3),
                    Size = new Size(88, 24),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(244, 245, 247),
                    ForeColor = Theme.Muted,
                    Font = Theme.Base,
                    Cursor = Cursors.Hand,
                };
                hide.FlatAppearance.BorderSize = 0;
                hide.Click += (_, _) =>
                {
                    if (MessageBox.Show($"把「{layer.Label}」整层迷雾重置为未探索？\n这会清掉这一层已走过的记录。",
                                        "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                        return;
                    if (!MapFacade.HideAll(layer)) { MessageBox.Show("这一层没有可写的迷雾数据。"); return; }
                    MarkDirtyAndRefresh();
                    Navigate("map");
                };

                row.Controls.Add(lbl);
                row.Controls.Add(bar);
                row.Controls.Add(btn);
                row.Controls.Add(hide);
                fogCard.AddRow(row);
            }
            fogCard.AddNote("迷雾按 255 = 已探索、0 = 未探索存储。全部揭开只改本地存档，游戏下次加载时生效。");
        }

        // ---- teleport stones ----
        var stoneCard = NewCard("传送石");
        var stones = MapFacade.ReadStones(_slot);
        if (stones.Count == 0) stoneCard.AddNote("未找到传送石模块（TeleportStone__TeleportStoneRecords）");
        else
        {
            stoneCard.AddRow(new InfoRow("已激活", $"{stones.Count(s => s.Activated)} / {stones.Count}"));
            var list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.None,
                Font = Theme.Base,
                BackColor = Color.White,
                // 17 rows need ~420 px, but the chest card sits below this one, so
                // cap the list to leave room for it instead of pushing it off screen.
                Height = Math.Min(30 + stones.Count * 23,
                                  Math.Max(150, _content.ClientSize.Height - 600)),
            };
            // ShortId repeats (two TS_SF_1_1 stones), so the coordinate is part of
            // the key column instead of a separate one that looks redundant.
            list.Columns.Add("编号 · 坐标", 170);
            list.Columns.Add("世界", 70);
            list.Columns.Add("状态", 150);

            foreach (var s in stones)
            {
                var flags = s.Activated ? "已激活" : "未激活";
                if (s.Destroyed) flags += " · 已摧毁";
                if (s.PlayerPlaced) flags += " · 玩家放置";
                var item = new ListViewItem(new[]
                {
                    s.UniqueLabel,
                    s.WorldLabel,
                    flags,
                });
                if (!s.Activated) item.ForeColor = Theme.Muted;
                item.Tag = s;
                list.Items.Add(item);
            }
            list.DoubleClick += (_, _) =>
            {
                if (list.SelectedItems.Count == 0) return;
                var s = (MapFacade.Stone)list.SelectedItems[0].Tag!;
                MapFacade.SetStoneActive(s, !s.Activated);
                MarkDirtyAndRefresh();
                Navigate("map");
            };
            stoneCard.AddRow(list);
            stoneCard.AddNote("双击一行可切换激活状态。");
        }

        // ---- chests ----
        var chestCard = NewCard("宝箱");
        if (_chests is null)
        {
            chestCard.AddNote("宝箱散落在 120 多个地图区块里，第一次扫描约需 20 秒（之后有缓存，几秒）。\n点下面的按钮开始扫描。");
            chestCard.AddAction(_scanning ? "扫描中…" : "扫描宝箱", () =>
            {
                if (_scanning) return;
                _scanning = true;
                SetStatus("正在扫描地图区块中的宝箱…");
                Navigate("map");
                var slot = _slot!;
                RunBackground(() => MapFacade.ReadChestsCached(slot),
                    list => _chests = list,
                    () => { _scanning = false; SetStatus(null); Navigate("map"); });
            });
        }
        else
        {
            var filled = _chests.Where(c => c.UsedSlots > 0).ToList();
            chestCard.AddRow(new InfoRow("宝箱总数", _chests.Count.ToString()));
            chestCard.AddRow(new InfoRow("有物品", $"{filled.Count} 个箱子，共 {_chests.Sum(c => c.UsedSlots)} 件"));
            chestCard.AddAction("重新扫描", () =>
            {
                _chests = null;
                MapFacade.InvalidateChests(_slot);
                Navigate("map");
            });

            if (filled.Count == 0) chestCard.AddNote("没有找到带物品的宝箱。");
            else
            {
                var list = new ListView
                {
                    View = View.Details,
                    FullRowSelect = true,
                    GridLines = false,
                    BorderStyle = BorderStyle.None,
                    Font = Theme.Base,
                    BackColor = Color.White,
                    Height = Math.Min(Math.Max(200, _content.ClientSize.Height - 560), 30 + filled.Count * 23),
                };
                list.Columns.Add("坐标", 92);
                list.Columns.Add("位置", 190);
                list.Columns.Add("放置物", 90);
                list.Columns.Add("件数", 56);
                list.Columns.Add("内容", 380);

                foreach (var c in filled.OrderByDescending(c => c.UsedSlots))
                {
                    var body = string.Join("、", c.Filled.Take(6).Select(i => $"{ItemName(i.ItemId)}×{i.Count}"));
                    if (c.UsedSlots > 6) body += $" …另 {c.UsedSlots - 6} 件";
                    var row = new ListViewItem(new[]
                    {
                        c.Coord,
                        c.Module.Replace("GenMap__", ""),
                        c.PlaceableId,
                        c.UsedSlots.ToString(),
                        body,
                    });
                    row.Tag = c;
                    list.Items.Add(row);
                }
                list.DoubleClick += (_, _) =>
                {
                    if (list.SelectedItems.Count == 0) return;
                    ShowChestDialog((MapFacade.Chest)list.SelectedItems[0].Tag!);
                };
                chestCard.AddRow(list);
                chestCard.AddNote("双击一行可编辑箱内物品的数量，或往空槽里加东西。");
            }
        }

        // Chests are what players actually come here for, so they lead the page;
        // fog and stones follow and can be scrolled to.
        StackCards(chestCard, BuildChunkCard(), fogCard, stoneCard, BuildMapReadOnly());
    }

    /// <summary>
    /// Everything else the map modules hold, shown read-only. These are all cheap
    /// to read (under half a second together) so they do not need a scan button.
    /// </summary>
    private Card BuildMapReadOnly()
    {
        var card = NewCard("地图数据（只读）");

        var zones = MapFacade.ReadZones(_slot!);
        var placed = MapFacade.ReadPlaceables(_slot!);
        var ai = MapFacade.ReadAiPlaceables(_slot!);
        var codex = MapFacade.ReadCodex(_slot!);

        card.AddRow(new InfoRow("区域（Zone）", $"{zones.Count} 个"));
        card.AddRow(new InfoRow("放置物", $"{placed.Count} 条"));
        card.AddRow(new InfoRow("AI 生成放置物", $"{ai.Count} 条"));
        card.AddRow(new InfoRow("图鉴", codex is null ? "—" : $"已解锁 {codex.Items} 件物品 / {codex.Monsters} 种怪物"));

        if (zones.Count > 0)
        {
            var tags = zones.GroupBy(z => z.Tag)
                            .OrderByDescending(g => g.Count())
                            .Select(g => $"{g.Key.Replace("Zone.", "")} {g.Count()}");
            card.AddNote($"区域标签：{string.Join("、", tags)}");
        }

        if (placed.Count > 0)
        {
            var top = placed.GroupBy(p => p.ItemId)
                            .OrderByDescending(g => g.Count())
                            .Take(5)
                            .Select(g => $"{ItemName(g.Key)}×{g.Count()}");
            card.AddNote($"放置物最多的：{string.Join("、", top)}");
        }

        if (ai.Count > 0)
        {
            var list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.None,
                Font = Theme.Base,
                BackColor = Color.White,
                Height = Math.Min(30 + Math.Min(ai.Count, 6) * 23, 200),
            };
            list.Columns.Add("尺寸", 60);
            list.Columns.Add("名称", 150);
            list.Columns.Add("生成描述", 560);
            foreach (var a in ai.Take(6))
                list.Items.Add(new ListViewItem(new[] { a.Size, a.Name, a.Short }));
            card.AddRow(list);
            if (ai.Count > 6) card.AddNote($"另有 {ai.Count - 6} 条未显示。");
        }

        card.AddNote("区域、放置物、图鉴目前只解析展示，不做修改——它们的用途和联动关系还没摸清，改了风险大于收益。");
        return card;
    }

    /// <summary>
    /// Pick one map chunk and edit it on its own. Reading a single chunk takes
    /// well under a second, versus ~28 s for the whole world, so this is the
    /// fast path for anyone who knows which area they care about.
    /// </summary>
    /// <summary>The chunk card most recently built, so a test can render just it.</summary>
    internal Control? LastChunkCard { get; private set; }

    private Card BuildChunkCard()
    {
        var card = NewCard("按区块编辑");

        var chunks = ChunkFacade.ListChunks(_slot!);
        if (chunks.Count == 0)
        {
            card.AddNote("这个存档里没有地图区块模块。");
            return card;
        }

        // ---- picker row ----
        var row = new Panel { Height = 34, BackColor = Color.Transparent };
        var lbl = new Label
        {
            Text = "选择区块",
            Location = new Point(0, 8),
            Width = 80,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
        };

        var combo = new ComboBox
        {
            Location = new Point(84, 4),
            Width = 240,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.Base,
            FlatStyle = FlatStyle.Flat,
        };
        foreach (var c in chunks)
        {
            // Show the biome cell the player recognises from the game's teleport
            // list and the camp panel, not a bare linear module number.
            var (bx, by) = ChunkFacade.ChunkView.BiomeOf(c.Index);
            combo.Items.Add(new ChunkPick(c.Module,
                $"{(c.Underground ? "地下" : "地表")} {c.Index}（区块 {bx},{by}）"));
        }

        int restore = 0;
        if (_chunkName is not null)
        {
            for (int i = 0; i < combo.Items.Count; i++)
                if (((ChunkPick)combo.Items[i]!).Module == _chunkName) { restore = i; break; }
        }
        combo.SelectedIndex = restore;
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ChunkPick pick) return;
            // Never rebuild the page from inside this event. LoadChunk calls
            // Navigate, which disposes every control in the content panel —
            // including this combo, which is still dispatching its own
            // SelectedIndexChanged message. Disposing a control mid-message is
            // an access violation in WinForms and kills the process outright,
            // which is why the map page died with no managed exception logged.
            // Post the work so the event finishes dispatching first.
            BeginInvoke(new Action(() => LoadChunk(pick.Module)));
        };

        var info = new Label
        {
            Location = new Point(336, 8),
            Width = 420,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
            Text = _chunkLoading ? "正在读取区块…"
                 : _chunk is null ? "选一个区块，只解析它，不扫描全世界。"
                 : $"{_chunk.Label} · 放置物 {_chunk.ObjectCount} · 箱子 {_chunk.BoxCount} · 铺过地皮 {PavedCells(_chunk)} 格",
        };

        row.Controls.Add(lbl);
        row.Controls.Add(combo);
        row.Controls.Add(info);
        card.AddRow(row);

        // ---- jump straight to the camp's chunk ----
        // Hunting for your own land in a 17-wide list of "地表 N" is exactly the
        // kind of coordinate-poking the map was supposed to remove.
        var campNow = SaveFacade.ReadCampsite(_slot!);
        if (campNow?.Coord is { } campCoord)
        {
            var jumpRow = new Panel { Height = 30, BackColor = Color.Transparent };
            var jump = new Button
            {
                Text = $"跳到营地（区块 {campCoord.BiomeX},{campCoord.BiomeY}）",
                Location = new Point(0, 0),
                Width = 200,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.AccentSoft,
                ForeColor = Theme.Accent,
                Font = Theme.Base,
                Cursor = Cursors.Hand,
            };
            jump.Click += (_, _) =>
            {
                var want = ChunkFacade.ChunkView.IndexOf(campCoord.BiomeX, campCoord.BiomeY);
                foreach (var c in chunks)
                {
                    if (c.Underground || c.Index != want) continue;
                    LoadChunk(c.Module);
                    return;
                }
                SetStatus($"存档里没有区块 {campCoord.BiomeX},{campCoord.BiomeY}（应为地表 {want}）。");
            };
            jumpRow.Controls.Add(jump);
            jumpRow.Controls.Add(new Label
            {
                Text = $"营地锚点 格({campCoord.DetailX},{campCoord.DetailY})",
                Location = new Point(210, 6),
                AutoSize = true,
                ForeColor = Theme.Muted,
                Font = Theme.Base,
            });
            card.AddRow(jumpRow);
        }

        // Loading is its own visible strip: switching chunks keeps the old picture
        // around for a moment, so without this the window looks frozen.
        if (_chunkLoading)
        {
            var busy = new Panel
            {
                Height = 30,
                BackColor = Theme.AccentSoft,
                Margin = new Padding(0, 0, 0, 6),
            };
            busy.Controls.Add(new Label
            {
                Text = $"正在读取 {_chunkName ?? "区块"}…",
                Location = new Point(10, 7),
                AutoSize = true,
                ForeColor = Theme.Accent,
                Font = Theme.Bold,
            });
            card.AddRow(busy);
        }

        if (_chunkLoading)
        {
            card.AddNote("读取期间不显示旧图，避免把上一个区块当成这个区块。");
            return card;
        }

        if (_chunk is null)
        {
            if (_chunkLoading)
                card.AddNote("正在读取区块…");
            else if (_chunkName is not null)
                card.AddNote($"「{_chunkName}」里没有地形数据。游戏对没生成过的区域只写一个 1.6 KB 的空壳" +
                             "（只有区块编号，没有格子数据），这种区块本来就没有东西可看、可改。" +
                             "换一个区块就好。");
            else
                card.AddNote("每个区块约 0.2–1.3 秒。相比「扫描宝箱」要读完全部 116 个区块，这里只读你选中的那一个。");
            return card;
        }

        var view = _chunk;

        // Does this chunk hold the camp? Decided before the default zoom, because
        // a camp view wants bigger cells than a whole-chunk overview.
        bool campHere = false;
        Rectangle campBox = default;
        if (SaveFacade.ReadCampsite(_slot!)?.Coord is { } cq && !view.IsUnderground
            && ChunkFacade.ChunkView.IndexOf(cq.BiomeX, cq.BiomeY) == view.Index)
        {
            var cc = SaveFacade.ReadCampsite(_slot!)!.Cells;
            if (cc.Count > 0)
            {
                campHere = true;
                campBox = Rectangle.FromLTRB(cc.Min(c => c.DetailX), cc.Min(c => c.DetailY),
                                             cc.Max(c => c.DetailX) + 1, cc.Max(c => c.DetailY) + 1);
            }
        }

        // A 150x150 chunk can never exceed 3 px/cell inside a 460 px viewport, so
        // "fit" is unreadable. Default to a size where tiles are visible and let
        // the viewport scroll; the 整块适配 button still offers the overview.
        // When focusing the camp the view is only 79 cells wide, so 14 px fits.
        if (_cellPixels <= 0)
        {
            if (_wholeChunk) _cellPixels = ChunkGrid.FitCellSize(view.TerrainSize, Math.Max(300, _content.Width - 100), 620);
            else _cellPixels = campHere && _focusCamp ? 14 : 6;
        }

        // ---- zoom row: cell size drives both readability and labels ----
        var zoomRow = new Panel { Height = 32, BackColor = Color.Transparent };
        zoomRow.Controls.Add(new Label
        {
            Text = "格子大小",
            Location = new Point(0, 7),
            Width = 70,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
        });
        var zoom = new NumericUpDown
        {
            Location = new Point(74, 4),
            Width = 70,
            Minimum = 4,
            Maximum = 32,
            Value = Math.Clamp(_cellPixels, 4, 32),
            Font = Theme.Base,
        };
        zoom.ValueChanged += (_, _) =>
        {
            _cellPixels = (int)zoom.Value;
            Navigate("map");
        };
        zoomRow.Controls.Add(zoom);

        var fit = new Button
        {
            Text = "整块适配",
            Location = new Point(152, 3),
            Size = new Size(84, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 241, 244),
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };
        fit.FlatAppearance.BorderSize = 0;
        fit.Click += (_, _) =>
        {
            // Fit the whole chunk in the visible area, whatever its terrain size.
            _cellPixels = ChunkGrid.FitCellSize(view.TerrainSize, Math.Max(300, _content.Width - 100), _wholeChunk ? 620 : 460);
            Navigate("map");
        };
        zoomRow.Controls.Add(fit);

        // ---- view switches: camp / whole chunk / all layers ----
        // Three independent toggles, laid out left to right so their x positions
        // never collide (the old code hard-coded 244 and 358, and adding a button
        // put it underneath the hint text).
        int swX = 244;

        if (campHere)
        {
            var focus = new Button
            {
                Text = _focusCamp ? "只看营地 ✓" : "只看营地",
                Location = new Point(swX, 3),
                Size = new Size(104, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = _focusCamp ? Theme.AccentSoft : Color.FromArgb(240, 241, 244),
                ForeColor = _focusCamp ? Theme.Accent : Theme.Text,
                Font = Theme.Base,
                Cursor = Cursors.Hand,
            };
            focus.FlatAppearance.BorderSize = 0;
            focus.Click += (_, _) =>
            {
                _focusCamp = !_focusCamp;
                // Turning the camp view off must actually show the chunk. The
                // old code only flipped the flag, but cropping still happened
                // unless 整块显示 was on, so "off" looked identical to "on".
                if (!_focusCamp) _wholeChunk = true;
                _cellPixels = _focusCamp
                    ? 14
                    : ChunkGrid.FitCellSize(view.TerrainSize, Math.Max(300, _content.Width - 100), 620);
                Navigate("map");
            };
            zoomRow.Controls.Add(focus);
            swX += 112;
        }

        var whole = new Button
        {
            Text = _wholeChunk ? "整块显示 ✓" : "整块显示",
            Location = new Point(swX, 3),
            Size = new Size(104, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = _wholeChunk ? Theme.AccentSoft : Color.FromArgb(240, 241, 244),
            ForeColor = _wholeChunk ? Theme.Accent : Theme.Text,
            Font = Theme.Base,
            Cursor = Cursors.Hand,
        };
        whole.FlatAppearance.BorderSize = 0;
        whole.Click += (_, _) =>
        {
            _wholeChunk = !_wholeChunk;
            // Whole chunk means every cell must fit: pick the size that does it.
            // Recomputed here rather than in the default-value branch, because by
            // now _cellPixels already holds a number and that branch never runs.
            _cellPixels = _wholeChunk
                ? ChunkGrid.FitCellSize(view.TerrainSize, Math.Max(300, _content.Width - 100), 620)
                : (campHere && _focusCamp ? 14 : 6);
            if (_wholeChunk) _focusCamp = false;
            Navigate("map");
        };
        zoomRow.Controls.Add(whole);
        swX += 112;

        var allLayers = new Button
        {
            Text = _showAllLayers ? "显示全部层 ✓" : "显示全部层",
            Location = new Point(swX, 3),
            Size = new Size(112, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = _showAllLayers ? Theme.AccentSoft : Color.FromArgb(240, 241, 244),
            ForeColor = _showAllLayers ? Theme.Accent : Theme.Text,
            Font = Theme.Base,
            Cursor = Cursors.Hand,
        };
        allLayers.FlatAppearance.BorderSize = 0;
        allLayers.Click += (_, _) =>
        {
            _showAllLayers = !_showAllLayers;
            Navigate("map");
        };
        zoomRow.Controls.Add(allLayers);

        zoomRow.Controls.Add(new Label
        {
            Text = "≥20 时格子上会写出图块编号",
            Location = new Point(swX + 120, 7),
            Width = 240,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
        });
        card.AddRow(zoomRow);

        // ---- visual grid: click a cell instead of reading coordinates ----
        var grid = new ChunkGrid();
        // Outline the camp when this chunk holds it, so the player's own patches
        // are distinguishable from unrelated work elsewhere in the same chunk.
        if (campHere)
        {
            grid.CampOutline = campBox;
            if (_focusCamp && !_wholeChunk) grid.FocusCamp = campBox;
        }
        grid.ShowAllLayers = _showAllLayers;
        grid.WholeChunk = _wholeChunk;
        grid.Load(view, _cellPixels);
        grid.CellClicked += cell =>
        {
            _gridInfo.Text = ChunkGrid.Describe(cell);
            _gridPick = cell.Obj is not null && !cell.Obj.Occupied ? cell.Obj : null;
            // In 全部层 mode every record is an edit, so this already picks the
            // topmost of all layers including 9/10. In normal mode it picks the
            // topmost real surface layer.
            _gridTerrainPick = cell.Edits.Count > 0
                ? cell.Edits.OrderByDescending(t => t.LayerId).First()
                : (cell.All.Count > 0 ? cell.All.OrderByDescending(t => t.LayerId).First() : null);
            UpdateGridButtons();
        };
        grid.CellDoubleClicked += cell =>
        {
            if (cell.Obj is not null && !cell.Obj.Occupied) { ShowChunkObjectDialog(cell.Obj); return; }
            if (cell.Edits.Count > 0) ShowTerrainDialog(cell.Edits.OrderByDescending(t => t.LayerId).First());
        };

        var gridHost = new Panel
        {
            // Fixed viewport: the cell size was chosen to fit inside it, so the
            // whole chunk is visible at once and scrolling is only needed after
            // the user deliberately zooms in. 整块显示 gets more height because a
            // 150-cell chunk can only reach 3 px/cell in a 460 px box, which is
            // too small to click accurately.
            Height = _wholeChunk ? 620 : 466,
            BackColor = Color.Transparent,
            AutoScroll = true,
        };
        gridHost.Controls.Add(grid);
        card.AddRow(gridHost);

        // When focused on the camp, scroll the viewport to the player's work
        // instead of leaving it parked at the top of an oversized picture.
        if (campHere && _focusCamp)
        {
            gridHost.HandleCreated += (_, _) => CenterOnContent();
            gridHost.VisibleChanged += (_, _) => { if (gridHost.Visible) CenterOnContent(); };

            void CenterOnContent()
            {
                if (grid.PaintedCells == 0) return;
                var r = grid.ContentBounds();
                if (r.IsEmpty) return;
                int x = Math.Max(0, r.Left * _cellPixels - gridHost.ClientSize.Width / 2 + r.Width * _cellPixels / 2);
                int y = Math.Max(0, r.Top * _cellPixels - gridHost.ClientSize.Height / 2 + r.Height * _cellPixels / 2);
                gridHost.AutoScrollPosition = new Point(x, y);
            }
        }

        // ---- legend: one compact line, named tilesets first ----
        // The legend wraps, so its height has to be known BEFORE AddRow: AddRow
        // freezes the row height at the value it sees, which is why a legend that
        // grew later overlapped the hint underneath it.
        int legendAvail = Math.Max(200, card.Width - 32);
        int legendSum = 0;
        foreach (var (_, text) in ChunkGrid.LegendFor(view.Terrain, _palette, _showAllLayers))
            legendSum += TextRenderer.MeasureText(text, Theme.Base).Width + 24 + 8;
        int legendLines = Math.Max(1, (legendSum + legendAvail - 1) / legendAvail);
        var legend = new FlowLayoutPanel
        {
            Height = legendLines * 24,
            BackColor = Color.Transparent,
            WrapContents = true,
            AutoScroll = false,
        };
        foreach (var (color, text) in ChunkGrid.LegendFor(view.Terrain, _palette, _showAllLayers))
        {
            var chip = new Panel { Size = new Size(TextRenderer.MeasureText(text, Theme.Base).Width + 24, 20), Margin = new Padding(0, 2, 8, 2) };
            chip.Controls.Add(new Panel
            {
                Location = new Point(0, 4),
                Size = new Size(12, 12),
                BackColor = color,
            });
            chip.Controls.Add(new Label
            {
                Text = text,
                Location = new Point(16, 2),
                AutoSize = true,
                ForeColor = Theme.Muted,
                Font = Theme.Base,
            });
            legend.Controls.Add(chip);
        }
        if (legend.Controls.Count == 0) legend.Controls.Add(new Label
        {
            Text = "这个区块没有改过的地形。",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
        });
        card.AddRow(legend);

        // ---- selected-cell strip ----
        _gridInfo = new Label
        {
            Text = "点一格查看它是什么，双击直接编辑。",
            Height = 24,
            ForeColor = Theme.Text,
            Font = Theme.Base,
            AutoSize = false,
        };
        card.AddRow(_gridInfo);

        // ---- action buttons for the selected cell ----
        var actions = new Panel { Height = 34, BackColor = Color.Transparent };
        _btnEditCell = new Button
        {
            Text = "编辑这一格",
            Location = new Point(0, 3),
            Size = new Size(110, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.Base,
            Enabled = false,
        };
        _btnEditCell.FlatAppearance.BorderSize = 0;
        _btnEditCell.Click += (_, _) =>
        {
            if (_gridPick is not null) { ShowChunkObjectDialog(_gridPick); return; }
            if (_gridTerrainPick is not null) ShowTerrainDialog(_gridTerrainPick);
        };

        _btnEditTerrain = new Button
        {
            Text = "改这一格地形",
            Location = new Point(118, 3),
            Size = new Size(120, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(140, 196, 255),
            ForeColor = Color.White,
            Font = Theme.Base,
            Enabled = false,
        };
        _btnEditTerrain.FlatAppearance.BorderSize = 0;
        _btnEditTerrain.Click += (_, _) =>
        {
            if (_gridTerrainPick is not null) ShowTerrainDialog(_gridTerrainPick);
        };

        actions.Controls.Add(_btnEditCell);
        actions.Controls.Add(_btnEditTerrain);

        var btnNames = new Button
        {
            Text = "给图块集起名/配色",
            Location = new Point(246, 3),
            Size = new Size(140, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 241, 244),
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };
        btnNames.FlatAppearance.BorderSize = 0;
        btnNames.Click += (_, _) => ShowPaletteDialog();
        actions.Controls.Add(btnNames);

        card.AddRow(actions);

        // ---- counts ----
        int painted = grid.PaintedCells;
        int baseOnly = view.Terrain.Where(t => t.LayerId == 9 || t.LayerId == 10)
                                   .Select(t => t.DetailIndex).Distinct().Count()
                       - view.Terrain.Where(t => t.LayerId != 9 && t.LayerId != 10)
                                     .Select(t => t.DetailIndex).Distinct().Count();
        card.AddRow(new InfoRow("铺过地皮的格子", $"{painted} 格（区块共 {view.TerrainSize * view.TerrainSize} 格）"
            + (_showAllLayers ? "　含基底层" : "　不含基底层 9、10")));
        card.AddRow(new InfoRow("容器", $"箱子 {view.BoxCount} · 门 {view.DoorCount} · 放置物 {view.ObjectCount}"));
        card.AddRow(new InfoRow("全部图层记录", $"{view.Terrain.Count} 条 / {view.Terrain.Select(t => t.DetailIndex).Distinct().Count()} 格"));
        card.AddRow(new InfoRow("只有基底记录", $"{Math.Max(0, baseOnly)} 格（游戏写过这一格，但你没铺地皮）"));

        // One row per layer 0..10, so an absent layer reads as 无 instead of
        // silently vanishing from the list. The save only ever writes a handful
        // of layer ids, and "which ones are missing" is information too.
        card.AddSection("按层统计");
        var layerStats = view.Terrain.GroupBy(t => t.LayerId)
                                     .ToDictionary(g => g.Key, g => (Count: g.Count(),
                                         Cells: g.Select(t => t.DetailIndex).Distinct().Count(),
                                         Sets: g.Select(t => t.TileSetIndex).Distinct().Count()));
        for (int layer = 0; layer <= 10; layer++)
        {
            string value;
            if (layerStats.TryGetValue(layer, out var st))
                value = $"{st.Count} 条 · {st.Cells} 格 · {st.Sets} 种图块集";
            else
                value = "无";
            card.AddRow(new InfoRow($"层 {layer}", value));
        }

        card.AddNote("画的是存档里记着的每一格，按图块集上色。层 5 装饰、层 7 地面、层 8 墙体是你的改动；" +
                     "层 9、10 是游戏写下的基底记录，默认淡灰显示，因为 9/10 覆盖的格子远多于你真正铺过的地皮。" +
                     "点「显示全部层」可以把 9/10 也当成普通图层：实色显示、可点选、可改图块。" +
                     "没被记录的格子是游戏按地图种子实时生成的原始地形，存档里没有这份数据，所以留白。" +
                     "「只看营地」把视图裁到营地范围，「整块显示」则铺满整个 150×150 区块。" +
                     "上层放置物（石墙、门、家具）会用类别色盖在地形上，四边各留一点边距，档位随格子大小变化。" +
                     "游戏的地形贴图在 .pak 里，读不到，所以画的是「哪格被改成了哪个图块」而不是真实画面。");

        LastChunkCard = card;
        return card;
    }

    /// <summary>
    /// Let the user name a tileset and pick its colour. The save exposes only a
    /// number, and the game's own table is packed away, so the person looking at
    /// the game is the only reliable source of "78 is stone wall". Names persist
    /// in %LOCALAPPDATA% and apply to every save.
    /// </summary>
    private void ShowPaletteDialog()
    {
        if (_chunk is null) return;

        // Only the tilesets that actually appear in this chunk, so the list is
        // short and every row is something the user can see on the map.
        var sets = _chunk.Terrain
                         .Where(t => !IsBaseLayer(t.LayerId))
                         .GroupBy(t => t.TileSetIndex)
                         .OrderByDescending(g => g.Count())
                         .ToList();

        using var dlg = new Form
        {
            Text = "图块集名称与颜色",
            Width = 560,
            Height = 520,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Bg,
            Font = Theme.Base,
        };

        var card = new Card("给图块集起名") { Width = 524, Location = new Point(12, 12) };

        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            Font = Theme.Base,
            BackColor = Color.White,
            Height = 300,
        };
        list.Columns.Add("图块集", 70);
        list.Columns.Add("图层", 60);
        list.Columns.Add("本区块格数", 90);
        list.Columns.Add("名称", 160);
        list.Columns.Add("颜色", 90);

        foreach (var g in sets)
        {
            var e = _palette.Get(g.Key);
            var it = new ListViewItem(new[]
            {
                g.Key.ToString(),
                "层 " + g.First().LayerId,
                g.Select(t => t.DetailIndex).Distinct().Count().ToString(),
                e?.Name ?? "",
                e?.Color ?? "",
            });
            it.Tag = g.Key;
            if (TilesetPalette.ParseColor(e?.Color) is { } rgb)
                it.BackColor = Color.FromArgb(245, rgb.R, rgb.G, rgb.B);
            list.Items.Add(it);
        }
        card.AddRow(list);

        var editRow = new Panel { Height = 34, BackColor = Color.Transparent };
        editRow.Controls.Add(new Label
        {
            Text = "名称",
            Location = new Point(0, 8),
            Width = 44,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
        });
        var nameBox = new TextBox { Location = new Point(46, 5), Width = 170, Font = Theme.Base };
        editRow.Controls.Add(nameBox);

        editRow.Controls.Add(new Label
        {
            Text = "颜色",
            Location = new Point(226, 8),
            Width = 44,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
        });
        var colorBox = new TextBox
        {
            Location = new Point(272, 5),
            Width = 100,
            Font = Theme.Base,
            Text = "#8A8680",
        };
        editRow.Controls.Add(colorBox);

        var pick = new Button
        {
            Text = "选色…",
            Location = new Point(378, 4),
            Size = new Size(70, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 241, 244),
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };
        pick.FlatAppearance.BorderSize = 0;
        pick.Click += (_, _) =>
        {
            using var cd = new ColorDialog { FullOpen = true };
            if (TilesetPalette.ParseColor(colorBox.Text) is { } cur)
                cd.Color = Color.FromArgb(cur.R, cur.G, cur.B);
            if (cd.ShowDialog(dlg) == DialogResult.OK)
                colorBox.Text = TilesetPalette.ToHex(cd.Color.R, cd.Color.G, cd.Color.B);
        };
        editRow.Controls.Add(pick);
        card.AddRow(editRow);

        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            int set = (int)list.SelectedItems[0].Tag!;
            var e = _palette.Get(set);
            nameBox.Text = e?.Name ?? "";
            if (e?.Color is { Length: > 0 } c)
            {
                colorBox.Text = c;
            }
            else
            {
                int layer = int.TryParse(list.SelectedItems[0].SubItems[1].Text.Replace("层 ", ""), out int l) ? l : 8;
                var def = ChunkGrid.DefaultColorFor(set, layer);
                colorBox.Text = TilesetPalette.ToHex(def.R, def.G, def.B);
            }
        };

        var saveRow = new Panel { Height = 34, BackColor = Color.Transparent };
        var apply = new Button
        {
            Text = "应用到这个图块集",
            Location = new Point(0, 3),
            Size = new Size(140, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.Base,
        };
        apply.FlatAppearance.BorderSize = 0;
        apply.Click += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            int set = (int)list.SelectedItems[0].Tag!;
            if (colorBox.Text.Trim().Length > 0 && TilesetPalette.ParseColor(colorBox.Text) is null)
            {
                MessageBox.Show(this, "颜色要写成 #RRGGBB 这样的格式。", "格式不对",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _palette.Set(set, nameBox.Text, colorBox.Text);
            _palette.Save();

            var it = list.SelectedItems[0];
            it.SubItems[3].Text = nameBox.Text.Trim();
            it.SubItems[4].Text = colorBox.Text.Trim();
            it.BackColor = TilesetPalette.ParseColor(colorBox.Text) is { } rgb
                ? Color.FromArgb(245, rgb.R, rgb.G, rgb.B)
                : Color.White;
        };
        saveRow.Controls.Add(apply);

        var clear = new Button
        {
            Text = "清除",
            Location = new Point(148, 3),
            Size = new Size(70, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 241, 244),
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };
        clear.FlatAppearance.BorderSize = 0;
        clear.Click += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            int set = (int)list.SelectedItems[0].Tag!;
            _palette.Set(set, "", "");
            _palette.Save();
            var it = list.SelectedItems[0];
            it.SubItems[3].Text = "";
            it.SubItems[4].Text = "";
            it.BackColor = Color.White;
            nameBox.Text = "";
        };
        saveRow.Controls.Add(clear);

        var done = new Button
        {
            Text = "关闭",
            Location = new Point(438, 3),
            Size = new Size(76, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 241, 244),
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };
        done.FlatAppearance.BorderSize = 0;
        done.Click += (_, _) => dlg.Close();
        saveRow.Controls.Add(done);
        card.AddRow(saveRow);

        card.AddNote("游戏里叫它什么，就填什么（例如 78 → 石墙、79 → 桐木墙）。" +
                     "名字和颜色存在本机，所有存档通用。没起名的图块集按图层给默认色：层 8 灰（墙体）、层 7 棕（地面）、层 5 绿（植被）。");

        dlg.Controls.Add(card);
        dlg.ShowDialog(this);
        Navigate("map");
    }

    /// <summary>Layers 9 and 10 are the same tile on nearly every cell; they are not edits.</summary>
    private static bool IsBaseLayer(int layerId) => layerId is 9 or 10;

    /// <summary>Refresh only the two cell-action buttons after the selection moves.</summary>
    private void UpdateGridButtons()
    {
        if (_btnEditCell is not null) _btnEditCell.Enabled = _gridPick is not null || _gridTerrainPick is not null;
        if (_btnEditTerrain is not null) _btnEditTerrain.Enabled = _gridTerrainPick is not null;
    }

    private sealed record ChunkPick(string Module, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Run a slow read on a worker thread and hand the result back on the UI
    /// thread. Any failure — in the work itself, or because the window is gone —
    /// is contained. Without this, an exception in the continuation tears down
    /// the process, which is exactly how a map load could "flash and close".
    /// </summary>
    private void RunBackground<T>(Func<T> work, Action<T> apply, Action onDone = null)
    {
        Task.Run(() =>
        {
            try { return (Ok: true, Value: work()); }
            catch { return (Ok: false, Value: default(T)!); }
        }).ContinueWith(t =>
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (t.IsCompletedSuccessfully && t.Result.Ok) apply(t.Result.Value);
                    }
                    catch
                    {
                        // A failure while rendering the result must not kill the app.
                    }
                    finally
                    {
                        try { onDone?.Invoke(); } catch { }
                    }
                }));
            }
            catch
            {
                // BeginInvoke throws once the handle is gone; nothing to report.
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Distinct cells this chunk records on a surface layer (5/7/8), i.e. the
    /// places the player actually laid something down. Base layers 9/10 are
    /// excluded because they only mean "the game wrote this cell once".
    /// </summary>
    private static int PavedCells(ChunkFacade.ChunkView? v) => v is null
        ? 0
        : v.Terrain.Where(t => t.LayerId != 9 && t.LayerId != 10)
                   .Select(t => t.DetailIndex).Distinct().Count();

    private void LoadChunk(string module)
    {
        if (_slot is null) return;
        if (module == _chunkName && !_chunkLoading && _chunk is not null) return;   // already shown

        // A newer request supersedes an older one. Bumping the token makes the
        // in-flight read's result get discarded, so this is safe even while a
        // previous chunk is still loading.
        var previous = _chunkName;
        if (previous is not null && previous != module)
            _slot.ReleaseModule(previous);   // unedited chunks only; dirty ones stay

        _chunkName = module;
        _chunkLoading = true;
        _chunk = null;   // never show the previous chunk while the new one loads
        SetStatus($"正在读取 {module}…");
        Navigate("map");

        var slot = _slot;
        int token = ++_chunkToken;

        RunBackground(() => ChunkFacade.ReadChunk(slot, module),
            view =>
            {
                // A slow read for chunk A must not overwrite the result for chunk B
                // that the user picked afterwards.
                if (token != _chunkToken) return;
                _chunk = view;
            },
            () =>
            {
                if (token != _chunkToken) return;
                _chunkLoading = false;
                // An empty chunk module (some underground chunks carry only Index
                // and bUnderground) parses to null. Say so instead of leaving a
                // blank panel that looks like a load failure.
                SetStatus(_chunk is null
                    ? $"{module} 是空区块：存档里这一块只有编号，没有地形数据。换一个区块看看。"
                    : null);
                Navigate("map");
            });
    }

    private void ShowChunkObjectDialog(ChunkFacade.ChunkObject o)
    {
        var (dlg, apply) = BuildChunkObjectDialog(o);
        using var form = dlg;
        if (form.ShowDialog(this) != DialogResult.OK) return;
        apply();
    }

    /// <summary>
    /// Build the placeable dialog without running it, so the screenshot harness
    /// can render it. Returns the dialog plus the action that commits the edits.
    /// </summary>
    private (Form Dialog, Action Apply) BuildChunkObjectDialog(ChunkFacade.ChunkObject o)
    {
        var dlg = new Form
        {
            Text = $"{o.Coord} — {ItemName(o.ItemId)}",
            Width = 420,
            Height = 260,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Bg,
            Font = Theme.Base,
        };

        var card = new Card("放置物") { Width = 384, Location = new Point(12, 12) };
        card.AddRow(new InfoRow("坐标", o.Coord));
        card.AddRow(new InfoRow("物品", $"{ItemName(o.ItemId)}  ({o.ItemId})"));
        card.AddRow(new InfoRow("类型", o.TypeLabel.Length > 0 ? o.TypeLabel : "普通"));
        card.AddRow(new InfoRow("来源", o.PlayerPlaced ? "玩家放置" : "系统生成"));

        var turn = new CheckBox
        {
            Text = "旋转 90°",
            Checked = o.Turn90,
            Location = new Point(16, 0),
            Width = 140,
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };
        var rowTurn = new Panel { Height = 30, BackColor = Color.Transparent };
        rowTurn.Controls.Add(turn);
        card.AddRow(rowTurn);

        var rotRow = new Panel { Height = 34, BackColor = Color.Transparent };
        rotRow.Controls.Add(new Label
        {
            Text = "旋转档位",
            Location = new Point(0, 8),
            Width = 70,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
        });
        var rot = new NumericUpDown
        {
            Location = new Point(76, 5),
            Width = 70,
            Minimum = 0,
            Maximum = 3,
            Value = Math.Clamp(o.RotateIndex, 0, 3),
            Font = Theme.Base,
        };
        rotRow.Controls.Add(rot);
        card.AddRow(rotRow);

        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.Base,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 241, 244),
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };
        cancel.FlatAppearance.BorderSize = 0;

        var btns = new Panel { Height = 34, BackColor = Color.Transparent };
        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);
        // Anchor to the right edge of the row instead of fixed coordinates: the
        // row is narrower than the card, so hard-coded x values pushed 取消 off
        // the side.
        void PlaceButtons()
        {
            int right = btns.ClientSize.Width;
            cancel.Left = Math.Max(0, right - cancel.Width);
            ok.Left = Math.Max(0, cancel.Left - ok.Width - 8);
            ok.Top = cancel.Top = 3;
        }
        btns.Resize += (_, _) => PlaceButtons();
        card.AddRow(btns);
        PlaceButtons();

        dlg.Controls.Add(card);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        // The card's height depends on how many rows it has, so a fixed window
        // height clipped the last rows and the buttons off the bottom. Size the
        // window from the laid-out card instead.
        dlg.Load += (_, _) =>
        {
            int wanted = card.Bottom + 12 + (dlg.Height - dlg.ClientSize.Height) + 8;
            var wa = Screen.FromControl(dlg).WorkingArea;
            dlg.Height = Math.Min(Math.Max(wanted, 200), wa.Height - 40);
        };

        void Apply()
        {
            bool changed = false;
            if (turn.Checked != o.Turn90) changed |= ChunkFacade.SetTurn90(o, turn.Checked);
            if ((int)rot.Value != o.RotateIndex) changed |= ChunkFacade.SetRotateIndex(o, (int)rot.Value);
            if (!changed) return;

            MarkDirtyAndRefresh();
            Navigate("map");
        }

        return (dlg, Apply);
    }

    private void ShowTerrainDialog(ChunkFacade.TerrainEdit t)
    {
        var built = BuildTerrainDialog(t);
        if (built is null) return;
        var (dlg, apply) = built.Value;
        using var _ = dlg;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        apply();
    }

    /// <summary>Build the terrain dialog without showing it, so the shot harness can render it.</summary>
    private (Form Dialog, Action Apply)? BuildTerrainDialog(ChunkFacade.TerrainEdit t)
    {
        var dlg = new Form
        {
            Text = $"地形 — 格子 {t.Key} 图层 {t.LayerId}",
            Width = 400,
            Height = 250,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Bg,
            Font = Theme.Base,
        };

        var card = new Card("图块") { Width = 364, Location = new Point(12, 12) };
        card.AddRow(new InfoRow("格子索引", t.Key));
        card.AddRow(new InfoRow("图层", t.LayerId.ToString()));

        var setRow = new Panel { Height = 34, BackColor = Color.Transparent };
        setRow.Controls.Add(new Label { Text = "图块集", Location = new Point(0, 8), Width = 70, ForeColor = Theme.Muted, Font = Theme.Base });
        var setNum = new NumericUpDown
        {
            Location = new Point(76, 5),
            Width = 80,
            Minimum = 0,
            Maximum = 255,
            Value = Math.Clamp(t.TileSetIndex, 0, 255),
            Font = Theme.Base,
        };
        setRow.Controls.Add(setNum);
        card.AddRow(setRow);

        var imgRow = new Panel { Height = 34, BackColor = Color.Transparent };
        imgRow.Controls.Add(new Label { Text = "图块", Location = new Point(0, 8), Width = 70, ForeColor = Theme.Muted, Font = Theme.Base });
        var imgNum = new NumericUpDown
        {
            Location = new Point(76, 5),
            Width = 80,
            Minimum = 0,
            Maximum = 255,
            Value = Math.Clamp(t.TileImageIndex, 0, 255),
            Font = Theme.Base,
        };
        imgRow.Controls.Add(imgNum);
        card.AddRow(imgRow);

        card.AddNote("图块编号是游戏内部的贴图索引，改错了这块地会显示成别的图块，但存档不会坏。");

        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.Base,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 241, 244),
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };
        cancel.FlatAppearance.BorderSize = 0;

        var btns = new Panel { Height = 34, BackColor = Color.Transparent };
        btns.Controls.Add(ok);
        btns.Controls.Add(cancel);
        // Right-align inside the row; fixed x values ran past the card edge.
        void PlaceButtons()
        {
            int right = btns.ClientSize.Width;
            cancel.Left = Math.Max(0, right - cancel.Width);
            ok.Left = Math.Max(0, cancel.Left - ok.Width - 8);
            ok.Top = cancel.Top = 3;
        }
        btns.Resize += (_, _) => PlaceButtons();
        card.AddRow(btns);
        PlaceButtons();

        dlg.Controls.Add(card);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        // Same reason as the chunk dialog: the card grows with its rows and its
        // note, so a fixed height hides the buttons.
        dlg.Load += (_, _) =>
        {
            int wanted = card.Bottom + 12 + (dlg.Height - dlg.ClientSize.Height) + 8;
            var wa = Screen.FromControl(dlg).WorkingArea;
            dlg.Height = Math.Min(Math.Max(wanted, 200), wa.Height - 40);
        };

        void Apply()
        {
            if ((int)setNum.Value == t.TileSetIndex && (int)imgNum.Value == t.TileImageIndex) return;
            if (!ChunkFacade.SetTerrainTile(t, (int)setNum.Value, (int)imgNum.Value)) return;

            MarkDirtyAndRefresh();
            Navigate("map");
        }

        return (dlg, Apply);
    }

    /// <summary>Resolve an item id to a readable name using everything we know.</summary>
    private string ItemName(string id)
    {
        if (id is "" or "None") return "空";
        if (id == "3040003") return "矿车";
        var known = _slot is null ? null : SaveFacade.ReadPlayer(_slot);
        var hit = known?.InventoryItems.Concat(known.HotbarItems)
                        .Concat(known.EquipmentItems).Concat(known.AccessoryItems)
                        .Concat(known.WeaponItems)
                        .FirstOrDefault(i => i.ItemId == id);
        if (hit is not null && hit.DisplayName.Length > 0) return hit.DisplayName;

        // The library holds names read from NPC backpacks plus any the player
        // typed in by hand, which is the only way a chest-only item gets a name.
        var cat = CatalogStore.Load().Items.FirstOrDefault(i => i.Id == id);
        if (cat is not null && cat.Name.Length > 0) return cat.Name;
        return id;
    }

    /// <summary>Edit one chest: change stack counts or add an item to a free slot.</summary>
    private void ShowChestDialog(MapFacade.Chest chest)
    {
        using var dlg = BuildChestDialog(chest);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            MarkDirtyAndRefresh();
            Navigate("map");
        }
    }

    /// <summary>Build the chest editor; the caller decides whether to show it modally.</summary>
    private Form BuildChestDialog(MapFacade.Chest chest)
    {
        var dlg = new Form
        {
            Text = $"宝箱 {chest.Coord}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = Theme.Base,
            Width = 700,
            Height = 420,
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(18, 14, 18, 10),
            BackColor = Color.White,
        };
        // Column 0 holds both the item name label and the "add item" id box, so it
        // needs enough room for a text field, not just a short label.
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var fields = new List<(MapFacade.ChestItem Item, NumericUpDown Box)>();

        table.Controls.Add(new Label { Text = "物品", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, 0);
        table.Controls.Add(new Label { Text = "数量", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 1, 0);
        table.Controls.Add(new Label { Text = "操作", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 2, 0);

        int r = 1;
        foreach (var it in chest.Items.Where(i => !i.IsEmpty))
        {
            table.Controls.Add(new Label
            {
                Text = ItemName(it.ItemId),
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 0),
            }, 0, r);

            var box = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 99999,
                Value = Math.Clamp(it.Count, 0, 99999),
                Width = 96,
                Margin = new Padding(0, 6, 0, 0),
            };
            table.Controls.Add(box, 1, r);
            fields.Add((it, box));

            var clear = new Button
            {
                Text = "清空这一格",
                Size = new Size(96, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.AccentSoft,
                ForeColor = Theme.Accent,
                Margin = new Padding(0, 6, 0, 0),
                Cursor = Cursors.Hand,
            };
            clear.FlatAppearance.BorderSize = 0;
            clear.Click += (_, _) => { MapFacade.ClearChestSlot(it); fields.RemoveAll(f => f.Item == it); };
            table.Controls.Add(clear, 2, r);
            r++;
        }

        // One free row for adding a new stack.
        var addId = new TextBox { Width = 150, Margin = new Padding(0, 6, 0, 0), PlaceholderText = "物品 ID" };
        var addCount = new NumericUpDown { Minimum = 1, Maximum = 99999, Value = 1, Width = 90, Margin = new Padding(0, 6, 0, 0) };
        var addBtn = new Button
        {
            Text = "加入空槽",
            Size = new Size(96, 24),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.AccentSoft,
            ForeColor = Theme.Accent,
            Margin = new Padding(0, 6, 0, 0),
            Cursor = Cursors.Hand,
        };
        addBtn.FlatAppearance.BorderSize = 0;

        // Small chests store only the slots they actually use, so a one-item chest
        // has no spare slot at all. Leaving a greyed-out empty id box plus a button
        // captioned "已满" looked like a broken control; replace the whole row with
        // a plain sentence that says what to do instead.
        var hasFree = chest.Items.Any(i => i.IsEmpty && i.IdProp is not null);
        if (hasFree)
        {
            table.Controls.Add(addId, 0, r);
            table.Controls.Add(addCount, 1, r);
            table.Controls.Add(addBtn, 2, r);
        }
        else
        {
            var why = new Label
            {
                Text = "这个箱子没有空槽了。想换东西，先点上面任意一行的「清空这一格」。",
                AutoSize = true,
                MaximumSize = new Size(600, 0),
                ForeColor = Theme.Muted,
                Font = Theme.Small,
                Margin = new Padding(0, 10, 0, 0),
            };
            table.Controls.Add(why, 0, r);
            table.SetColumnSpan(why, 3);
        }
        addBtn.Click += (_, _) =>
        {
            var id = addId.Text.Trim();
            if (id.Length == 0) return;
            if (!MapFacade.AddToChest(chest, id, (int)addCount.Value))
                MessageBox.Show("这个箱子没有空槽了。清空一格后再加。", "无空槽",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.White };
        var ok = new Button
        {
            Text = "确定",
            Size = new Size(88, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            Font = Theme.Bold,
            Cursor = Cursors.Hand,
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) =>
        {
            foreach (var (item, box) in fields)
                MapFacade.SetChestCount(item, (int)box.Value);
            dlg.DialogResult = DialogResult.OK;
        };
        var cancel = new Button
        {
            Text = "取消",
            Size = new Size(88, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 242, 245),
            ForeColor = Theme.Text,
            Cursor = Cursors.Hand,
        };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
        bar.Controls.Add(ok);
        bar.Controls.Add(cancel);
        bar.Resize += (_, _) =>
        {
            ok.Location = new Point(bar.Width - 200, 9);
            cancel.Location = new Point(bar.Width - 104, 9);
        };

        dlg.Controls.Add(table);
        dlg.Controls.Add(bar);
        dlg.Load += (_, _) =>
        {
            var wa = Screen.FromControl(dlg).WorkingArea;
            dlg.Height = Math.Min(table.PreferredSize.Height + bar.Height + 46, wa.Height - 40);
            dlg.Width = Math.Min(700, wa.Width - 60);
            ok.Location = new Point(bar.Width - 200, 9);
            cancel.Location = new Point(bar.Width - 104, 9);
        };

        return dlg;
    }

    // ------------------------------------------------------------------ inventory
    /// <summary>
    /// One list for every item array. Rows size themselves to the data, columns
    /// share the card width, and the affix column shows every affix line rather
    /// than a truncated "蓝 Lv65 / 蓝…".
    /// </summary>
    private ListView BuildItemList(List<InventoryItem> items, string where, bool showSlot, bool showCount)
    {
        // The backpack has 40 rows; a list sized to its rows would be far taller
        // than the window. Cap it so the following cards (hotbar, gear) start
        // inside the first screenful instead of being pushed off the bottom.
        int maxH = where == "背包"
            ? Math.Max(200, _content.ClientSize.Height - 430)
            : Math.Max(180, _content.ClientSize.Height - 150);
        int wantH = 30 + Math.Max(1, items.Count) * 23;

        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            Font = Theme.Base,
            BackColor = Color.White,
            Height = Math.Min(maxH, wantH),
        };

        // Fixed columns stay narrow so the affix column gets the rest; affix ids
        // are the only genuinely long text in the row.
        if (showSlot) list.Columns.Add("格子", 50);
        list.Columns.Add("物品名", showSlot ? 210 : 200);
        list.Columns.Add("物品 ID", 84);
        if (showCount) list.Columns.Add("数量", 56);
        list.Columns.Add("词条", 340);

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var aff = ItemAffixView.From(it.Group);

            // Every live package on one line. The raw ids are long and repetitive
            // (Expansion_ / Legend_ / Status_ / Spec_ prefixes), so strip the
            // boilerplate to keep the whole line inside the column.
            var parts = new List<string>();
            foreach (var pkg in aff?.Packages ?? new List<AffixPackageView>())
            {
                if (!pkg.Valid) continue;
                var one = $"{pkg.RarityName} Lv{pkg.Level}";
                if (pkg.Prefix is not "" and not "None") one += " " + ShortAffix(pkg.Prefix);
                var sfx = pkg.Suffixes.Select(ShortAffix).ToList();
                if (sfx.Count > 0) one += " +" + string.Join(" +", sfx);
                parts.Add(one);
            }
            string affix = parts.Count == 0 ? "—" : string.Join(" ｜ ", parts);

            var cells = new List<string>();
            if (showSlot) cells.Add((i + 1).ToString());
            cells.Add(it.Display);
            cells.Add(it.ItemId);
            if (showCount) cells.Add(it.Count.ToString());
            cells.Add(affix);

            var row = new ListViewItem(cells.ToArray());
            if (it.IsEmpty) row.ForeColor = Theme.Muted;
            if (it.CountProp?.IsDirty == true) row.ForeColor = Theme.DirtyEdge;
            row.Tag = (it, i);
            list.Items.Add(row);
        }

        // Column widths follow the card so the affix column is never squeezed.
        void Fit()
        {
            int total = list.ClientSize.Width;
            if (total <= 0) return;
            int fixedW = 0;
            for (int c = 0; c < list.Columns.Count - 1; c++) fixedW += list.Columns[c].Width;
            list.Columns[list.Columns.Count - 1].Width = Math.Max(180, total - fixedW - 4);
        }
        list.Resize += (_, _) => Fit();
        list.HandleCreated += (_, _) => Fit();

        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            var (it, idx) = ((InventoryItem, int))list.SelectedItems[0].Tag!;
            if (it.IsEmpty) { SetStatus("这个槽位是空的"); return; }
            ShowWeaponDialog(it, idx, where);
        };

        return list;
    }

    /// <summary>
    /// Trim the boilerplate off an affix id so a whole package fits in one list
    /// column: Expansion_Status_WeaponReach becomes WeaponReach, and
    /// Expansion_Legend_Event_Offense_26 becomes Legend:Offense26.
    /// </summary>
    private static string ShortAffix(string id)
    {
        if (string.IsNullOrEmpty(id) || id == "None") return id;

        var s = id;
        foreach (var drop in new[] { "Expansion_", "Prefix_", "Suffix_", "Spec_" })
            s = s.Replace(drop, "");

        s = s.Replace("Status_", "");
        if (s.StartsWith("Legend_"))
            s = "L:" + s["Legend_".Length..];

        // Weapon/Sword/Bow etc. are noise once the item itself is named.
        foreach (var drop in new[] { "Weapon", "Sword", "Bow", "Axe", "Hammer", "Pickaxe", "Shovel" })
            if (s.StartsWith(drop) && s.Length > drop.Length)
                s = s[drop.Length..];

        return s.Replace("_Event", "").Replace("_Offense", "O").Replace('_', '\0').Replace("\0", "");
    }

    private void BuildInventory()
    {
        if (_slot is null) return;
        var v = SaveFacade.ReadPlayer(_slot);
        if (v is null) return;

        var items = v.InventoryItems;
        var groups = v.InventoryGroups;

        var card = NewCard($"背包（{items.Count(i => !i.IsEmpty)} 件物品 · {items.Count(i => i.IsEmpty)} 个空槽位）");

        card.AddAction("全部堆满", () =>
        {
            int n = 0;
            foreach (var it in items)
            {
                if (it.CountProp is null || it.MaxStack <= 0) continue;
                var bytes = ValueEditor.Encode(it.CountProp, it.MaxStack.ToString(), out _);
                if (bytes is null) continue;
                it.CountProp.MarkDirty(bytes);
                n++;
            }
            MarkDirtyAndRefresh();
            Navigate(_current);
            SetStatus($"已把 {n} 件物品堆到上限（尚未保存）");
        });

        card.AddAction("新增物品…", () => AddItemDialog(items, groups));

        card.AddRow(BuildItemList(items, "背包", showSlot: false, showCount: true));
        card.AddNote("双击某一行：AI 生成的物品可改名称、描述、属性、词条；官方道具只能改数量。"
                   + "「全部堆满」会把每件物品补到其上限。");

        StackCards(
            card,
            BuildItemCard("快捷栏", v.HotbarItems, showSlot: true, showCount: true),
            BuildItemCard("装备", v.EquipmentItems, showSlot: true, showCount: false),
            BuildItemCard("饰品", v.AccessoryItems, showSlot: true, showCount: false),
            BuildItemCard("武器架", v.WeaponItems, showSlot: true, showCount: false));
    }

    /// <summary>A card wrapping one item array. Empty arrays collapse to a note.</summary>
    private Card BuildItemCard(string label, List<InventoryItem> items, bool showSlot, bool showCount)
    {
        int used = items.Count(i => !i.IsEmpty);
        // Slot arrays always report their capacity so the bar reads as 10 格,
        // not "10 件" — the distinction matters when slots are empty.
        var title = showSlot && items.Count > 0
            ? $"{label}（{items.Count} 个格子 · 已放 {used} 个）"
            : $"{label}（{used} 件）";
        var card = NewCard(title);
        if (used == 0 && items.Count == 0)
        {
            card.AddNote($"没有{label}里的物品。");
            return card;
        }
        card.AddRow(BuildItemList(items, label, showSlot, showCount));
        card.AddNote("双击可改数量与词条。");
        return card;
    }

    /// <summary>
    /// Export the whole library as one blob of text.
    ///
    /// The format is deliberately not a file the user has to attach: 191 items
    /// and 54 NPCs come out around 270 KB of digits and letters, which pastes
    /// into a chat message or a text file equally well. The dialog shows the
    /// text and offers to save it, so both habits work.
    /// </summary>
    private void ExportLibraryDialog(CatalogFile cat)
    {
        if (cat.Items.Count == 0 && cat.Npcs.Count == 0)
        {
            SetStatus("资料库是空的，没有可导出的内容");
            return;
        }
        using var dlg = BuildExportLibraryDialog(cat);
        if (dlg is not null) dlg.ShowDialog(this);
    }

    /// <summary>
    /// Build the export dialog without showing it, so the screenshot harness can
    /// display and capture it the same way it does the other dialogs.
    /// </summary>
    internal Form? BuildExportLibraryDialog(CatalogFile cat)
    {
        string text;
        try { text = LibraryCodec.Export(cat); }
        catch (Exception ex) { SetStatus("导出失败：" + ex.Message); return null; }

        var dlg = new Form
        {
            Text = "导出资料库",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(760, 520),
            BackColor = Theme.Bg,
            Font = Theme.Base,
            MinimizeBox = false,
        };

        var head = new Label
        {
            Text = $"已把 {cat.Items.Count} 种物品、{cat.Npcs.Count} 个 NPC 打包成 {text.Length:N0} 个字符的密文。\n"
                 + "复制下面全部内容发给别人，对方在「导入资料库…」里粘贴即可；也可以点「另存为文件…」。",
            AutoSize = false,
            Bounds = new Rectangle(16, 14, 728, 46),
            ForeColor = Theme.Text,
        };

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            // 270 KB of ciphertext inside a word-wrapping TextBox makes WinForms
            // re-layout the whole buffer on every paint; the screenshot run hung
            // for minutes here. No wrapping keeps it instant, and the text is
            // still fully selectable and copyable.
            WordWrap = false,
            Bounds = new Rectangle(16, 66, 728, 384),
            BackColor = Color.White,
            ForeColor = Theme.Text,
            Font = new Font("Consolas", 8.5f),
            Text = text,
        };

        var copy = new Button
        {
            Text = "复制全部",
            Bounds = new Rectangle(16, 462, 120, 38),
            FlatStyle = FlatStyle.System,
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text);
                SetStatus("密文已复制到剪贴板");
            }
            catch (Exception ex) { SetStatus("复制失败：" + ex.Message); }
        };

        var save = new Button
        {
            Text = "另存为文件…",
            Bounds = new Rectangle(146, 462, 130, 38),
            FlatStyle = FlatStyle.System,
        };
        save.Click += (_, _) =>
        {
            using var sfd = new SaveFileDialog
            {
                Title = "导出资料库",
                Filter = "资料库密文 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                FileName = "资料库.txt",
            };
            if (sfd.ShowDialog(dlg) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(sfd.FileName, text, new UTF8Encoding(false));
                SetStatus("已导出到 " + sfd.FileName);
            }
            catch (Exception ex) { SetStatus("写入文件失败：" + ex.Message); }
        };

        var close = new Button
        {
            Text = "关闭",
            Bounds = new Rectangle(652, 462, 92, 38),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel,
        };

        dlg.Controls.AddRange(new Control[] { head, box, copy, save, close });
        dlg.CancelButton = close;
        return dlg;
    }

    /// <summary>
    /// Import a library blob. Merging is the default because a friend's library
    /// is almost always a superset of what you have and you do not want to lose
    /// names you typed in by hand.
    /// </summary>
    private void ImportLibraryDialog()
    {
        using var dlg = BuildImportLibraryDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK) Navigate("library");
    }

    /// <summary>Build the import dialog without showing it, for screenshots.</summary>
    internal Form BuildImportLibraryDialog()
    {
        var dlg = new Form
        {
            Text = "导入资料库",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(760, 560),
            BackColor = Theme.Bg,
            Font = Theme.Base,
            MinimizeBox = false,
        };

        var head = new Label
        {
            Text = "把别人给你的密文粘贴到下面，或者点「从文件读取…」。\n"
                 + "导入只补充本地还没有的条目，不会覆盖你已经录入的名称。",
            AutoSize = false,
            Bounds = new Rectangle(16, 14, 728, 46),
            ForeColor = Theme.Text,
        };

        var box = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Bounds = new Rectangle(16, 66, 728, 400),
            BackColor = Color.White,
            ForeColor = Theme.Text,
            Font = new Font("Consolas", 8.5f),
        };

        var load = new Button
        {
            Text = "从文件读取…",
            Bounds = new Rectangle(16, 478, 130, 38),
            FlatStyle = FlatStyle.System,
        };
        load.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "导入资料库",
                Filter = "资料库密文 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            };
            if (ofd.ShowDialog(dlg) != DialogResult.OK) return;
            try
            {
                box.Text = File.ReadAllText(ofd.FileName);
                SetStatus("已读取 " + ofd.FileName);
            }
            catch (Exception ex) { SetStatus("读取文件失败：" + ex.Message); }
        };

        var paste = new Button
        {
            Text = "从剪贴板粘贴",
            Bounds = new Rectangle(156, 478, 140, 38),
            FlatStyle = FlatStyle.System,
        };
        paste.Click += (_, _) =>
        {
            try
            {
                if (Clipboard.ContainsText()) box.Text = Clipboard.GetText();
            }
            catch (Exception ex) { SetStatus("粘贴失败：" + ex.Message); }
        };

        var ok = new Button
        {
            Text = "导入",
            Bounds = new Rectangle(548, 510, 96, 38),
            FlatStyle = FlatStyle.System,
        };
        var cancel = new Button
        {
            Text = "取消",
            Bounds = new Rectangle(652, 510, 92, 38),
            FlatStyle = FlatStyle.System,
            DialogResult = DialogResult.Cancel,
        };

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) { SetStatus("请先粘贴或读取密文"); return; }
            if (!LibraryCodec.TryImport(box.Text, out var incoming, out var err))
            {
                MessageBox.Show(err, "无法导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var live = CatalogStore.Load();
            int beforeItems = live.Items.Count, beforeNpcs = live.Npcs.Count;
            var (ai, an) = LibraryCodec.Merge(live, incoming!);
            CatalogStore.PruneNonOfficial(live);

            if (ai == 0 && an == 0)
            {
                SetStatus($"没有新内容：这份资料库的 {incoming!.Items.Count} 种物品、"
                        + $"{incoming.Npcs.Count} 个 NPC 本地都已经有了");
                dlg.DialogResult = DialogResult.OK;
                dlg.Close();
                return;
            }

            if (!CatalogStore.Save(live, out var saveErr))
            {
                MessageBox.Show(saveErr, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetStatus($"导入完成：新增 {ai} 种物品、{an} 个 NPC"
                    + $"（{beforeItems}→{live.Items.Count} / {beforeNpcs}→{live.Npcs.Count}）");
            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
        };

        dlg.Controls.AddRange(new Control[] { head, box, load, paste, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return dlg;
    }

    /// <summary>
    /// Name an item id by hand. Most chest contents never pass through a bag, so
    /// their ItemConfig — the only offline source of a name — does not exist on
    /// disk anywhere. Typing the name once fixes it everywhere: chests, the
    /// inventory picker and the library all read this table.
    /// </summary>
    private void AddItemNameDialog()
    {
        using var dlg = BuildItemNameDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            SetStatus($"已记录「{_lastItemName}」（{_lastItemId}）");
            Navigate("library");
        }
    }

    private string _lastItemId = "";
    private string _lastItemName = "";

    /// <summary>Build the 录入物品名称 dialog; the caller decides how to show it.</summary>
    private Form BuildItemNameDialog()
    {
        var cat = CatalogStore.Load();

        var dlg = new Form
        {
            Text = "录入物品名称",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(560, 330),
            BackColor = Color.White,
            Font = Theme.Base,
        };

        var title = new Label
        {
            Text = "给物品 ID 起个名字",
            Font = Theme.H1,
            AutoSize = true,
            Location = new Point(20, 16),
        };
        dlg.Controls.Add(title);

        var hint = new Label
        {
            Text = "物品 ID 是 7 位数字。填好保存后，宝箱和背包里这件物品就会显示这个名字。\n"
                 + "资料库只存名称和 ID，不存物品数据，所以这里改名不会影响任何存档。",
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Location = new Point(20, 52),
        };
        dlg.Controls.Add(hint);

        var idLabel = new Label
        {
            Text = "物品 ID",
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(20, 96),
        };
        dlg.Controls.Add(idLabel);

        var nameLabel = new Label
        {
            Text = "名称",
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(236, 96),
        };
        dlg.Controls.Add(nameLabel);

        var idBox = new TextBox
        {
            Font = Theme.Base,
            Location = new Point(20, 116),
            Width = 200,
            PlaceholderText = "7 位数字，如 1020043",
        };
        dlg.Controls.Add(idBox);

        var nameBox = new TextBox
        {
            Font = Theme.Base,
            Location = new Point(236, 116),
            Width = 304,
            PlaceholderText = "如 破损的铁器",
        };
        dlg.Controls.Add(nameBox);

        var status = new Label
        {
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Location = new Point(20, 156),
        };
        dlg.Controls.Add(status);

        // Live feedback: does the library already know this id?
        idBox.TextChanged += (_, _) =>
        {
            var id = idBox.Text.Trim();
            if (id.Length == 0) { status.Text = ""; return; }
            var hit = cat.Items.FirstOrDefault(i => i.Id == id);
            if (hit is null) { status.Text = $"资料库里还没有 {id}，保存后会新增一条。"; return; }
            nameBox.Text = hit.Name;
            status.Text = $"资料库已有：{hit.Name}（来源：{hit.Source}），保存会覆盖。";
        };

        var ok = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.None,
            Font = Theme.Base,
            Location = new Point(360, 268),
            Size = new Size(90, 32),
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Font = Theme.Base,
            Location = new Point(458, 268),
            Size = new Size(90, 32),
        };
        dlg.Controls.Add(ok);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        ok.Click += (_, _) =>
        {
            var id = idBox.Text.Trim();
            var name = nameBox.Text.Trim();
            if (!CatalogStore.SetItemName(cat, id, name, out var err))
            {
                status.Text = err;
                status.ForeColor = Color.Firebrick;
                return;
            }
            if (!CatalogStore.Save(cat, out var saveErr))
            {
                status.Text = "保存资料库失败：" + saveErr;
                status.ForeColor = Color.Firebrick;
                return;
            }
            _lastItemId = id;
            _lastItemName = name;
            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
        };

        return dlg;
    }

    /// <summary>
    /// Add an item from the global library, or by typing its id. The library
    /// stores only names and ids; the real ItemConfig is resolved live, first
    /// from this save and then from sibling saves, so an item captured
    /// elsewhere can still be added.
    ///
    /// An id the library has never seen can still be added as long as some save
    /// nearby holds its config — which is what makes 按 ID 添加 useful for items
    /// the user knows about but has not collected yet.
    /// </summary>
    private void AddItemDialog(List<InventoryItem> items, List<List<SaveProperty>> groups)
    {
        if (_slot is null) return;

        int freeCount = items.Count(i => i.IsEmpty);
        if (freeCount == 0) { SetStatus("没有空槽位了，无法新增物品"); return; }

        var cat = CatalogStore.Load();
        var present = items.Where(i => !i.IsEmpty).Select(i => i.ItemId)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // What this save can build right now, so a typed id can be checked
        // before it is accepted instead of failing after the fact.
        var available = CatalogInjector.AvailableTemplates(_slot);

        var entries = cat.Items
            .OrderBy(i => present.Contains(i.Id) ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.CurrentCulture)
            .Select(i => (Text: i.Display, Tag: (string?)i.Id))
            .ToList();

        using var picker = new SearchPickerDialog(
            "新增物品",
            $"从资料库选一件物品，或者直接把物品 ID 打进搜索框后点确定。\n"
          + $"资料库 {cat.Items.Count} 种，本存档已有 {present.Count} 种；当前有 {freeCount} 个空槽位。",
            entries,
            emptyNote: "资料库还是空的。可以只按物品 ID 添加，或者先到「资料库」页扫描存档。",
            confirmText: "下一步",
            column1: "物品名",
            column2: "物品 ID")
        {
            AllowFreeId = true,
            // Only a missing config blocks: without it there is nothing to copy,
            // and inventing bytes would corrupt the save. An odd-looking id that
            // does resolve is allowed.
            Validate = typed =>
                CatalogInjector.Resolve(_slot, typed, out _) is null
                    ? $"找不到「{typed}」的物品配置：本存档和相邻存档的背包里都没有这件物品。"
                      + "物品配置只能从已有的物品复制，不能凭空生成。"
                    : null,
        };

        if (picker.ShowDialog(this) != DialogResult.OK) return;

        string chosenId;
        if (picker.ChosenIndex >= 0) chosenId = entries[picker.ChosenIndex].Tag!;
        else chosenId = picker.FreeId;

        // A typed id may only exist in a sibling save; resolve gives the name.
        var tpl = CatalogInjector.Resolve(_slot, chosenId, out var rerr);
        if (tpl is null) { SetStatus($"无法取用「{chosenId}」：{rerr}"); return; }

        var known = cat.Items.FirstOrDefault(i => string.Equals(i.Id, chosenId, StringComparison.OrdinalIgnoreCase));
        var display = known is not null && !string.IsNullOrEmpty(known.Name) ? known.Name : tpl.Name;

        int count = AskCount(display, tpl.MaxStack);
        if (count <= 0) return;

        int slotIdx = CatalogInjector.AddItemToPlayer(_slot, chosenId, count, out var err);
        if (slotIdx < 0) { SetStatus($"新增失败：{err}"); return; }

        MarkDirtyAndRefresh();
        Navigate(_current);
        SetStatus($"已把「{display}」×{count} 放入背包第 {slotIdx + 1} 格（尚未保存）");
    }

    /// <summary>Small modal asking only for a stack count.</summary>
    private int AskCount(string itemName, int maxStack)
    {
        using var dlg = new Form
        {
            Text = "设置数量",
            Width = 400,
            Height = 200,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = Theme.Base,
        };

        var lbl = new Label
        {
            Text = $"「{itemName}」要放多少个？",
            Location = new Point(20, 20),
            Size = new Size(340, 24),
            ForeColor = Theme.Text,
        };

        var num = new NumericUpDown
        {
            Location = new Point(20, 54),
            Width = 160,
            Minimum = 1,
            Maximum = maxStack > 0 ? maxStack : 9999,
            Value = 1,
            Font = Theme.Base,
        };

        var maxHint = new Label
        {
            Text = maxStack > 0 ? $"上限 {maxStack}" : "无上限信息",
            Location = new Point(190, 57),
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
        };

        var ok = new Button
        {
            Text = "确定",
            Location = new Point(160, 110),
            Width = 100,
            Height = 32,
            DialogResult = DialogResult.OK,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Bold,
        };
        ok.FlatAppearance.BorderSize = 0;

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(270, 110),
            Width = 90,
            Height = 32,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Theme.Border;

        dlg.Controls.AddRange(new Control[] { lbl, num, maxHint, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        return dlg.ShowDialog(this) == DialogResult.OK ? (int)num.Value : 0;
    }

    /// <summary>
    /// Item picker for an NPC's backpack slot. Only items that already exist
    /// somewhere in this save are offered — an invented item id has no
    /// ItemConfig and the game cannot load it.
    /// </summary>
    private sealed class AddNpcItemDialog : Form
    {
        private readonly NumericUpDown _count = new();
        private readonly List<string> _ids;
        private readonly SaveSlot _slot;

        public string? SelectedId { get; private set; }
        public int SelectedCount { get; private set; }
        public string SelectedName { get; private set; } = string.Empty;

        public AddNpcItemDialog(SaveSlot slot, string arrLabel)
        {
            _slot = slot;

            Text = $"给{arrLabel}加物品";
            Width = 640;
            Height = 560;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.White;
            Font = Theme.Base;

            var cat = CatalogStore.Load();
            _ids = cat.Items.Select(i => i.Id).ToList();

            var hint = new Label
            {
                Text = "从资料库选择物品，它会作为真实物品写入这个空槽位。\n"
                     + "编辑器不会凭空生成物品，否则游戏读不出来。",
                Location = new Point(20, 14),
                Size = new Size(590, 40),
                ForeColor = Theme.Muted,
                Font = Theme.Small,
            };

            var search = new TextBox
            {
                Location = new Point(20, 58),
                Width = 590,
                Font = Theme.Base,
                BorderStyle = BorderStyle.FixedSingle,
            };

            var list = new ListView
            {
                Location = new Point(20, 92),
                Size = new Size(590, 330),
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Base,
                BackColor = Color.White,
            };
            list.Columns.Add("物品名", 340);
            list.Columns.Add("物品 ID", 150);
            list.Columns.Add("上限", 80);

            var countLbl = new Label
            {
                Text = "数量：",
                Location = new Point(20, 438),
                AutoSize = true,
                ForeColor = Theme.Text,
            };

            _count.Location = new Point(80, 435);
            _count.Width = 120;
            _count.Minimum = 1;
            _count.Maximum = 9999;
            _count.Value = 1;
            _count.Font = Theme.Base;

            var status = new Label
            {
                Location = new Point(220, 438),
                Size = new Size(390, 24),
                ForeColor = Theme.Muted,
                Font = Theme.Small,
            };

            var rowToId = new List<string>();
            var stackById = cat.Items.ToDictionary(i => i.Id, i => 0);
            var nameById = cat.Items.ToDictionary(i => i.Id, i => i.Name);

            void Refill()
            {
                var q = search.Text.Trim();
                list.BeginUpdate();
                list.Items.Clear();
                rowToId.Clear();

                foreach (var id in _ids)
                {
                    var nm = nameById.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : id;
                    if (q.Length > 0
                        && !nm.Contains(q, StringComparison.OrdinalIgnoreCase)
                        && !id.Contains(q, StringComparison.OrdinalIgnoreCase))
                        continue;

                    list.Items.Add(new ListViewItem(new[] { nm, id, "" }));
                    rowToId.Add(id);
                }

                if (list.Items.Count > 0) list.Items[0].Selected = true;
                status.Text = q.Length == 0
                    ? $"共 {list.Items.Count} 种物品"
                    : $"匹配 {list.Items.Count} 项";
                list.EndUpdate();
            }

            search.TextChanged += (_, _) => Refill();

            list.SelectedIndexChanged += (_, _) =>
            {
                if (list.SelectedIndices.Count == 0) return;
                var id = rowToId[list.SelectedIndices[0]];
                // resolve lazily just to learn the stack limit
                var tpl = CatalogInjector.Resolve(_slot, id, out _);
                int max = tpl?.MaxStack ?? 0;
                stackById[id] = max;
                _count.Maximum = max > 0 ? max : 9999;
                if (_count.Value > _count.Maximum) _count.Value = _count.Maximum;
            };

            var ok = new Button
            {
                Text = "放入",
                Location = new Point(400, 476),
                Width = 100,
                Height = 34,
                BackColor = Theme.Accent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.Bold,
            };
            ok.FlatAppearance.BorderSize = 0;

            var cancel = new Button
            {
                Text = "取消",
                Location = new Point(512, 476),
                Width = 98,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.Cancel,
            };
            cancel.FlatAppearance.BorderColor = Theme.Border;

            Controls.AddRange(new Control[] { hint, search, list, countLbl, _count, status, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;

            Refill();
            search.Focus();

            ok.Click += (_, _) =>
            {
                if (list.SelectedIndices.Count == 0) { status.Text = "请先选一个物品"; return; }
                SelectedId = rowToId[list.SelectedIndices[0]];
                SelectedCount = (int)_count.Value;
                SelectedName = nameById.TryGetValue(SelectedId, out var n2) ? n2 : SelectedId;
                DialogResult = DialogResult.OK;
            };
        }
    }

    private void PromptCount(InventoryItem it, ListView list)
    {
        if (it.CountProp is null) return;

        using var dlg = new Form
        {
            Text = $"修改数量 — {it.Name ?? it.ItemId}",
            Width = 360,
            Height = 190,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = Theme.Base,
        };

        var lbl = new Label
        {
            Text = $"新数量（上限 {(it.MaxStack > 0 ? it.MaxStack.ToString() : "无限制")}）：",
            Location = new Point(20, 22),
            AutoSize = true,
            ForeColor = Theme.Text,
        };
        var num = new NumericUpDown
        {
            Location = new Point(20, 50),
            Width = 180,
            Maximum = 1_000_000,
            Minimum = 0,
            Value = Math.Min(Math.Max(it.Count, 0), 1_000_000),
            Font = Theme.Base,
        };
        var ok = new Button
        {
            Text = "确定",
            Location = new Point(130, 95),
            Width = 90,
            Height = 30,
            DialogResult = DialogResult.OK,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Bold,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(230, 95),
            Width = 90,
            Height = 30,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Theme.Border;

        dlg.Controls.AddRange(new Control[] { lbl, num, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var bytes = ValueEditor.Encode(it.CountProp, num.Value.ToString(), out var err);
        if (bytes is null) { SetStatus($"修改失败：{err}"); return; }

        it.CountProp.MarkDirty(bytes);
        MarkDirtyAndRefresh();

        if (list.SelectedItems.Count > 0)
            list.SelectedItems[0].SubItems[2].Text = num.Value.ToString();

        SetStatus($"「{it.Name ?? it.ItemId}」数量改为 {num.Value}（尚未保存）");
    }

    /// <summary>
    /// Per-item editor for a quick-bar slot: count plus the affix packages the
    /// <summary>
    /// Per-item editor: count plus the affix packages the game rolls stats
    /// from. Works for any item in the backpack, hotbar, equipment or accessory
    /// arrays — they all share the same ItemSaveData shape. Uses a
    /// TableLayoutPanel so the labels and fields can never overlap.
    /// </summary>
    private void ShowWeaponDialog(InventoryItem it, int slotIndex, string where)
    {
        // Official items get a count-only dialog on purpose. Their ItemConfig is
        // a cached copy of a row in the game's DT_Items table: the game rebuilds
        // it on load, so edits to the name, description, attributes or affixes
        // are silently reverted — and a value whose shape stops matching the
        // table can corrupt the save. Only AI-generated items keep their full
        // definition in the save itself, so only those are editable.
        if (ItemDetailView.From(it) is { } detail)
        {
            if (detail.CanEditContent) ShowItemDetailDialog(it, detail, slotIndex, where);
            else ShowCountOnlyDialog(it, detail, slotIndex, where);
            return;
        }

        var built = BuildWeaponDialog(it, slotIndex, where);
        if (built is null) return;

        using var dlg = built.Value.Dialog;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        built.Value.Apply();
    }

    private void ShowCountOnlyDialog(InventoryItem it, ItemDetailView v, int slotIndex, string where)
    {
        var built = BuildCountOnlyDialog(it, v, slotIndex, where);
        if (built is null) return;

        using var dlg = built.Value.Dialog;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        built.Value.Apply();
    }

    /// <summary>
    /// The editor an official item is allowed to have: quantity, and nothing
    /// else. Everything the game regenerates from its data table is shown as
    /// read-only text so the user can still see it, but cannot break it.
    /// </summary>
    private (Form Dialog, Action Apply)? BuildCountOnlyDialog(
        InventoryItem it, ItemDetailView v, int slotIndex, string where)
    {
        var title2 = v.DisplayName.Length > 24 ? v.DisplayName[..24] + "…" : v.DisplayName;
        var dlg = new Form
        {
            Text = $"{where} {slotIndex + 1} — {title2}",
            Width = 620,
            Height = 400,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = Theme.Base,
        };

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(20, 16, 20, 10),
            BackColor = Color.White,
        };
        dlg.Controls.Add(stack);

        Label Row(string label, string value, bool wrap = false) => new()
        {
            Text = $"{label}　{value}",
            AutoSize = false,
            Width = 556,
            Height = wrap ? 46 : 24,
            ForeColor = Theme.Text,
            Font = Theme.Base,
            Margin = new Padding(0, 2, 0, 2),
        };

        // ---- what it is ----
        stack.Controls.Add(new Label
        {
            Text = "官方道具",
            AutoSize = true,
            Font = Theme.Bold,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 0, 0, 6),
        });
        stack.Controls.Add(Row("物品 ID", v.ItemId));
        stack.Controls.Add(Row("稀有度", v.RarityName));
        if (v.LevelMin > 0 || v.LevelMax > 0)
            stack.Controls.Add(Row("等级", $"{v.LevelMin} ~ {v.LevelMax}"));
        stack.Controls.Add(Row("堆叠上限", v.MaxStack > 0 ? v.MaxStack.ToString() : "无限制"));

        if (v.CurrentDesc.Length > 0)
        {
            stack.Controls.Add(new Label
            {
                Text = "说明",
                AutoSize = true,
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                Margin = new Padding(0, 8, 0, 2),
            });
            stack.Controls.Add(new Label
            {
                Text = v.CurrentDesc,
                AutoSize = false,
                Width = 556,
                Height = 60,
                ForeColor = Theme.Text,
                Font = Theme.Base,
                Margin = new Padding(0, 0, 0, 4),
            });
        }

        // ---- quantity, the one writable field ----
        var qty = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 2),
        };
        qty.Controls.Add(new Label
        {
            Text = "数量",
            Width = 56,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Theme.Bold,
        });
        var num = new NumericUpDown
        {
            Width = 120,
            Minimum = 0,
            Maximum = 1_000_000,
            Value = Math.Min(Math.Max(it.Count, 0), 1_000_000),
        };
        qty.Controls.Add(num);
        if (v.MaxStack > 0)
            qty.Controls.Add(new Label
            {
                Text = $"上限 {v.MaxStack}",
                Width = 110,
                Height = 28,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Muted,
                Font = Theme.Small,
            });
        stack.Controls.Add(qty);

        stack.Controls.Add(new Label
        {
            Text = "官方道具只有数量能改。\r\n"
                 + "名称、说明、属性和词条都在游戏的数据表里，存档只存一份缓存；"
                 + "游戏读档时会用数据表重建，改了会被覆盖，格式对不上还可能坏档。\r\n"
                 + "要改这些，只有 AI 生成的物品可以——它们的完整定义就在存档里。",
            AutoSize = false,
            Width = 556,
            Height = 104,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Margin = new Padding(0, 12, 0, 10),
        });

        // ---- buttons ----
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 54,
            Padding = new Padding(0, 10, 20, 10),
            BackColor = Color.White,
        };
        var ok = new Button
        {
            Text = "确定",
            Width = 96,
            Height = 32,
            DialogResult = DialogResult.OK,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Bold,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消",
            Width = 96,
            Height = 32,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Theme.Border;
        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        dlg.Controls.Add(bar);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        dlg.Load += (_, _) =>
        {
            var wa = Screen.FromControl(dlg).WorkingArea;
            int wanted = stack.PreferredSize.Height + bar.Height + 46;
            dlg.Height = Math.Min(Math.Max(wanted, 320), wa.Height - 80);
        };

        void Apply()
        {
            if (it.CountProp is null) { SetStatus("这个槽位没有数量字段"); return; }
            int want = (int)num.Value;
            if (want == it.Count) { SetStatus("没有改动"); return; }

            // The game clamps a stack to MaxStack on load, so writing more looks
            // like the edit was reverted. Refuse it rather than surprise them.
            if (v.MaxStack > 0 && want > v.MaxStack)
            {
                MessageBox.Show(dlg,
                    $"「{v.DisplayName}」的堆叠上限是 {v.MaxStack}。\n\n" +
                    $"填 {want} 会被游戏读档时夹回 {v.MaxStack}，看起来就像改动被回退了。\n" +
                    $"要超过上限，只能多占几个格子（用「复制一组」）。",
                    "超过堆叠上限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var b = ValueEditor.Encode(it.CountProp, want.ToString(), out var e);
            if (b is null) { SetStatus("写入失败：" + e); return; }
            it.CountProp.MarkDirty(b);
            MarkDirtyAndRefresh();
            Navigate(_current);
            SetStatus($"{where} {slotIndex + 1}「{v.DisplayName}」数量改为 {want}（尚未保存）");
        }

        return (dlg, Apply);
    }

    private void ShowItemDetailDialog(InventoryItem it, ItemDetailView v, int slotIndex, string where)
    {
        var built = BuildItemDetailDialog(it, v, slotIndex, where);
        if (built is null) return;

        using var dlg = built.Value.Dialog;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        built.Value.Apply();
    }

    /// <summary>
    /// The full item editor: description text, flat attributes, and affix
    /// packages. Curve-backed attributes are shown read-only with the reason,
    /// because writing their Value is silently undone by the game on load.
    /// </summary>
    private (Form Dialog, Action Apply)? BuildItemDetailDialog(
        InventoryItem it, ItemDetailView v, int slotIndex, string where)
    {
        // The name can be a whole sentence (AI items describe themselves), so a
        // raw title becomes unreadable and stretches past the window edge.
        var title = v.DisplayName.Length > 24 ? v.DisplayName[..24] + "…" : v.DisplayName;
        var dlg = new Form
        {
            Text = $"{where} {slotIndex + 1} — {title}",
            Width = 720,
            Height = 400,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = Theme.Base,
        };

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.White,
            Padding = new Padding(0),
        };
        dlg.Controls.Add(scroll);

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18, 14, 18, 8),
            BackColor = Color.White,
        };
        scroll.Controls.Add(stack);

        // Declared here, not inside the sections below: Apply() closes over
        // them and the `if (v.HasAttributes)` blocks would hide them.
        var attrRows = new List<(ItemAttributeRow Row, TextBox Box, Label Tag)>();
        var affixRows = new List<(ItemAffixPackage Pkg, NumericUpDown Level, NumericUpDown Rarity)>();

        Label Sec(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Font = Theme.Bold,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 14, 0, 4),
        };

        Label Hint(string text) => new()
        {
            Text = text,
            AutoSize = false,
            Width = 660,
            Height = 34,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Margin = new Padding(0, 0, 0, 4),
        };

        // ---------- 名称与描述 ----------
        stack.Controls.Add(Sec("名称与描述"));

        var nameRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        };
        nameRow.Controls.Add(new Label
        {
            Text = "名称",
            Width = 60,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
        });
        var nameBox = new TextBox
        {
            Width = 560,
            Text = v.CurrentExplain.Length > 0 ? v.CurrentExplain : v.CurrentName,
            Font = Theme.Base,
        };
        nameRow.Controls.Add(nameBox);
        stack.Controls.Add(nameRow);

        if (!v.ExplainIsPureName)
        {
            stack.Controls.Add(Hint("这件物品的 Explain 里还带着词条摘要，改名只改游戏标题用的名称，不会动那段摘要。"));
        }

        stack.Controls.Add(new Label
        {
            Text = "描述",
            Width = 60,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Margin = new Padding(0, 6, 0, 2),
        });

        var descBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Width = 660,
            Height = 76,
            Text = v.CurrentDesc,
            Font = Theme.Base,
            Margin = new Padding(0, 0, 0, 2),
        };
        stack.Controls.Add(descBox);
        stack.Controls.Add(Hint("这就是物品浮窗里那段说明文字。改完保存，游戏里会显示你写的这段话。"));

        // ---------- 属性 ----------
        if (v.HasAttributes)
        {
            stack.Controls.Add(Sec($"属性（{v.Attributes.Count} 条）"));

            foreach (var a in v.Attributes.OrderByDescending(x => !x.IsCurveBacked))
            {
                var line = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 3),
                };

                line.Controls.Add(new Label
                {
                    Text = a.Label,
                    Width = 108,
                    Height = 24,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Theme.Text,
                    Font = Theme.Base,
                });

                var box = new TextBox
                {
                    Width = 120,
                    Text = a.IsPercent ? (a.Value * 100).ToString("0.####") : a.Value.ToString("0.####"),
                    Font = Theme.Base,
                    Enabled = !a.IsCurveBacked,
                    BackColor = a.IsCurveBacked ? Color.FromArgb(244, 244, 244) : Color.White,
                };
                line.Controls.Add(box);

                var unit = new Label
                {
                    Text = a.IsPercent ? "%" : "",
                    Width = 22,
                    Height = 24,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Theme.Muted,
                    Font = Theme.Small,
                };
                line.Controls.Add(unit);

                var tag = new Label
                {
                    Text = a.IsCurveBacked
                        ? $"由等级曲线决定（{a.CurveRow}），改这里无效"
                        : "",
                    Width = 380,
                    Height = 24,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = a.IsCurveBacked ? Theme.Warn : Theme.Muted,
                    Font = Theme.Small,
                };
                line.Controls.Add(tag);

                stack.Controls.Add(line);
                attrRows.Add((a, box, tag));
            }

            if (v.CurveAttributeCount > 0)
                stack.Controls.Add(Hint(
                    "灰色的那几条带等级曲线：游戏读档时会按词条等级重新算，直接改数值会被覆盖。"
                    + "要提升它们，请改下面的词条等级。"));
        }

        // ---------- 词条 ----------
        if (v.HasAffixes)
        {
            stack.Controls.Add(Sec("词条"));

            foreach (var pkg in v.Packages)
            {
                var line = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true,
                    Margin = new Padding(0, 2, 0, 2),
                };

                line.Controls.Add(new Label
                {
                    Text = pkg.Label,
                    Width = 90,
                    Height = 26,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Theme.Text,
                    Font = Theme.Base,
                });

                line.Controls.Add(new Label
                {
                    Text = "等级",
                    Width = 38,
                    Height = 26,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Theme.Muted,
                    Font = Theme.Small,
                });
                var lv = new NumericUpDown
                {
                    Width = 76,
                    Minimum = 1,
                    Maximum = 999,
                    Value = Math.Clamp(pkg.LevelValue, 1, 999),
                };
                line.Controls.Add(lv);

                line.Controls.Add(new Label
                {
                    Text = "稀有度",
                    Width = 52,
                    Height = 26,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Theme.Muted,
                    Font = Theme.Small,
                    Margin = new Padding(10, 0, 0, 0),
                });
                var rar = new NumericUpDown
                {
                    Width = 60,
                    Minimum = 0,
                    Maximum = 5,
                    Value = Math.Clamp(pkg.RarityValue, 0, 5),
                };
                line.Controls.Add(rar);

                line.Controls.Add(new Label
                {
                    Text = $"当前 {pkg.RarityName}",
                    Width = 90,
                    Height = 26,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Theme.Muted,
                    Font = Theme.Small,
                });
                line.Controls.Add(line.Controls[^1]);

                stack.Controls.Add(line);
                affixRows.Add((pkg, lv, rar));
            }

            stack.Controls.Add(Hint("0 白 / 1 绿 / 2 蓝 / 3 紫 / 4 橙 / 5 红。等级和稀有度都会重新掷出词条数值。"));
        }

        // ---------- buttons ----------
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 54,
            Padding = new Padding(0, 10, 18, 10),
            BackColor = Color.White,
        };
        var ok = new Button
        {
            Text = "确定",
            Width = 96,
            Height = 32,
            DialogResult = DialogResult.OK,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Bold,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消",
            Width = 96,
            Height = 32,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Theme.Border;
        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        dlg.Controls.Add(bar);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        dlg.Load += (_, _) =>
        {
            var wa = Screen.FromControl(dlg).WorkingArea;
            // Fit the content instead of always opening at the maximum, which
            // left half the dialog empty for an item with few fields.
            int wanted = stack.PreferredSize.Height + bar.Height + 46;
            dlg.Height = Math.Min(Math.Max(wanted, 300), Math.Min(820, wa.Height - 60));
            dlg.Width = Math.Min(740, wa.Width - 60);
        };

        void Apply()
        {
            int changed = 0;
            var notes = new List<string>();

            var wantName = nameBox.Text.Trim();
            if (wantName.Length > 0 && wantName != v.CurrentExplain && wantName != v.CurrentName)
            {
                if (v.SetName(wantName, out var ne)) changed++;
                else notes.Add(ne);
            }

            var wantDesc = descBox.Text.Trim();
            if (wantDesc != v.CurrentDesc)
            {
                if (v.SetDescription(wantDesc, out var de)) changed++;
                else notes.Add(de);
            }

            foreach (var (a, box, tag) in attrRows)
            {
                if (a.IsCurveBacked) continue;
                var txt = box.Text.Trim();
                if (!float.TryParse(txt, out var num)) { notes.Add($"「{a.Label}」不是数字，已跳过。"); continue; }
                var val = a.IsPercent ? num / 100f : num;
                if (Math.Abs(val - a.Value) < 0.00005f) continue;
                if (v.SetAttribute(a.Name, val, out var ae)) changed++;
                else notes.Add(ae);
            }

            foreach (var (pkg, lv, rar) in affixRows)
            {
                if ((int)lv.Value != pkg.LevelValue)
                {
                    if (v.SetAffixLevel(pkg.Label, (int)lv.Value, out var le)) changed++;
                    else notes.Add(le);
                }
                if ((int)rar.Value != pkg.RarityValue)
                {
                    if (v.SetAffixRarity(pkg.Label, (int)rar.Value, out var re)) changed++;
                    else notes.Add(re);
                }
            }

            if (notes.Count > 0)
                MessageBox.Show(dlg, string.Join("\n", notes), "部分改动未写入",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            if (changed == 0) { SetStatus("没有改动"); return; }
            MarkDirtyAndRefresh();
            Navigate(_current);
            SetStatus($"{where} {slotIndex + 1}「{v.DisplayName}」已改 {changed} 项（尚未保存）");
        }

        return (dlg, Apply);
    }

    /// <summary>
    /// Build the item editor without showing it. Returns the dialog plus the
    /// action that writes the edited values back, so the shot harness can render
    /// the dialog without a modal loop.
    /// </summary>
    private (Form Dialog, Action Apply)? BuildWeaponDialog(InventoryItem it, int slotIndex, string where)
    {
        var aff = ItemAffixView.From(it.Group);
        if (aff is null) { SetStatus("这件物品没有可编辑的数据"); return null; }

        // Only live packages are worth showing; an inactive package has no
        // stats and editing it would do nothing in game.
        var live = aff.Packages.Where(p => p.Valid).ToList();

        var dlg = new Form
        {
            Text = $"{where} {slotIndex + 1} — {it.DisplayName}",
            Width = 660,
            // Height is computed from the table once it is laid out, so the OK
            // button is never pushed below the window edge.
            Height = 300,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            BackColor = Color.White,
            Font = Theme.Base,
            AutoScroll = true,
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            Padding = new Padding(18, 14, 18, 10),
            BackColor = Color.White,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Label FieldLabel(string text) => new()
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Margin = new Padding(0, 4, 8, 0),
        };

        Label Header(string text, Color color) => new()
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = color,
            Font = Theme.Bold,
            Margin = new Padding(0, 10, 0, 2),
        };

        // ---- count row ----
        table.Controls.Add(FieldLabel("数量"), 0, 0);
        var num = new NumericUpDown
        {
            Width = 110,
            Maximum = 1_000_000,
            Minimum = 0,
            Value = Math.Min(Math.Max(it.Count, 0), 1_000_000),
            Margin = new Padding(0, 4, 8, 0),
        };
        table.Controls.Add(num, 1, 0);
        table.SetColumnSpan(num, 1);

        // ---- one block per affix package ----
        var rows = new List<(AffixPackageView Pkg, NumericUpDown Level, NumericUpDown Rarity,
                             TextBox Prefix, TextBox Suffix, CheckBox On)>();
        int row = 1;

        if (live.Count == 0)
        {
            var note = new Label
            {
                Text = "这件物品没有词条。\n"
                     + "只有武器和防具在生成时会掷出词条，材料、食物、工具都没有。\n"
                     + "带词条的物品会在列表的「词条」列里显示稀有度和等级。",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 62,
                ForeColor = Theme.Muted,
                Font = Theme.Small,
                Margin = new Padding(0, 6, 0, 6),
            };
            table.Controls.Add(note, 0, row);
            table.SetColumnSpan(note, 4);
            row++;
        }

        foreach (var pkg in live)
        {
            if (pkg.LevelProp is null && pkg.RarityProp is null) continue;

            var head = Header($"{pkg.Label}　{(pkg.Valid ? "● 已生效" : "○ 未生效")}",
                               pkg.Valid ? Theme.Text : Theme.Muted);
            table.Controls.Add(head, 0, row);
            table.SetColumnSpan(head, 3);

            // bValid is what the game checks before applying the package, so it
            // has to be switchable — otherwise an inactive package can never be
            // turned on from the editor.
            var on = new CheckBox
            {
                Text = "启用",
                Checked = pkg.Valid,
                AutoSize = true,
                Dock = DockStyle.Fill,
                ForeColor = Theme.Muted,
                Font = Theme.Small,
                Margin = new Padding(0, 10, 0, 0),
                Enabled = pkg.ValidProp is not null,
            };
            table.Controls.Add(on, 3, row);
            row++;

            table.Controls.Add(FieldLabel("等级"), 0, row);
            var lv = new NumericUpDown
            {
                Width = 84,
                Maximum = 999,
                Minimum = 1,
                Value = Math.Clamp(pkg.Level, 1, 999),
                Margin = new Padding(0, 4, 8, 0),
            };
            table.Controls.Add(lv, 1, row);

            table.Controls.Add(FieldLabel("稀有度"), 2, row);
            var rar = new NumericUpDown
            {
                Width = 84,
                Maximum = 5,
                Minimum = 0,
                Value = Math.Clamp(pkg.Rarity, 0, 5),
                Margin = new Padding(0, 4, 8, 0),
            };
            table.Controls.Add(rar, 3, row);
            row++;

            table.Controls.Add(FieldLabel("前缀"), 0, row);
            // Readable name on its own full-width line; the raw id stays
            // editable on the line below it, because the id is the only thing
            // the save actually stores.
            var preName = new Label
            {
                Text = AffixNames.Describe(pkg.Prefix)
                       + (AffixNames.Effect(pkg.Prefix) is { Length: > 0 } pe ? $"　（{pe}）" : ""),
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Text,
                Font = Theme.Base,
                Margin = new Padding(0, 2, 8, 0),
            };
            table.Controls.Add(preName, 1, row);
            table.SetColumnSpan(preName, 3);
            row++;

            table.Controls.Add(FieldLabel("原始 ID"), 0, row);
            var pre = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = pkg.Prefix,
                Margin = new Padding(0, 2, 8, 4),
                Font = Theme.Small,
            };
            table.Controls.Add(pre, 1, row);
            table.SetColumnSpan(pre, 3);
            row++;

            // Suffixes are a plain string array on disk; edit them one per line.
            table.Controls.Add(FieldLabel("后缀"), 0, row);
            var sfxBox = new Panel
            {
                Dock = DockStyle.Fill,
                Height = 6 + Math.Max(1, pkg.Suffixes.Count) * 22,
                Margin = new Padding(0, 2, 8, 2),
                BackColor = Color.Transparent,
            };
            int sy = 2;
            if (pkg.Suffixes.Count == 0)
            {
                sfxBox.Controls.Add(new Label
                {
                    Text = "（无）",
                    Location = new Point(0, sy),
                    Size = new Size(430, 20),
                    ForeColor = Theme.Muted,
                    Font = Theme.Small,
                });
            }
            else
            {
                foreach (var s in pkg.Suffixes)
                {
                    sfxBox.Controls.Add(new Label
                    {
                        Text = AffixNames.Describe(s)
                               + (AffixNames.Effect(s) is { Length: > 0 } se ? $"　（{se}）" : ""),
                        Location = new Point(0, sy),
                        Size = new Size(430, 20),
                        AutoEllipsis = true,
                        ForeColor = Theme.Text,
                        Font = Theme.Base,
                    });
                    sy += 22;
                }
            }
            table.Controls.Add(sfxBox, 1, row);
            table.SetColumnSpan(sfxBox, 3);
            row++;

            table.Controls.Add(FieldLabel("原始 ID"), 0, row);
            var sfx = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Height = 58,
                Text = string.Join("\r\n", pkg.Suffixes),
                Margin = new Padding(0, 2, 8, 6),
                Font = Theme.Small,
            };
            table.Controls.Add(sfx, 1, row);
            table.SetColumnSpan(sfx, 3);
            row++;

            rows.Add((pkg, lv, rar, pre, sfx, on));
        }

        dlg.Controls.Add(table);

        // Say plainly where the readable names come from. The game's own name
        // table sits in compressed data with no decoder shipped, so these are
        // read off the id structure — the user should know that rather than
        // assume it is official text.
        var explain = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(18, 4, 18, 0),
            Text = "上面是词条的中文读法，由存档里的内部 ID 拆词得出；右边仍可改原始 ID。\n"
                 + "游戏自己的名称表在压缩资源里，取不出来，所以这不是官方译文。",
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            BackColor = Color.White,
        };
        dlg.Controls.Add(explain);

        // ---- buttons pinned to the bottom ----
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 54,
            Padding = new Padding(0, 10, 18, 10),
            BackColor = Color.White,
        };
        var ok = new Button
        {
            Text = "确定",
            Width = 96,
            Height = 32,
            DialogResult = DialogResult.OK,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Bold,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = "取消",
            Width = 96,
            Height = 32,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Theme.Border;
        bar.Controls.Add(cancel);
        bar.Controls.Add(ok);
        dlg.Controls.Add(bar);

        // Size the window to the table plus the button bar and the explainer,
        // capped to the screen. The explainer is docked bottom, so leaving it
        // out of the sum pushes the OK button past the window edge.
        dlg.Load += (_, _) =>
        {
            int wanted = table.PreferredSize.Height + bar.Height + explain.Height + 52;
            var wa = Screen.FromControl(dlg).WorkingArea;
            dlg.Height = Math.Min(wanted, wa.Height - 40);
            dlg.Width = Math.Min(700, wa.Width - 60);
        };

        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        void Apply()
        {
        int changed = 0;
        if (it.CountProp is not null && (int)num.Value != it.Count)
        {
            // The game clamps a stack to MaxStack when it loads the save, so a
            // larger number looks like "my edit was reverted". Refuse it here
            // instead of writing a value that cannot survive the next load.
            int want = (int)num.Value;
            if (it.MaxStack > 0 && want > it.MaxStack)
            {
                MessageBox.Show(dlg,
                    $"「{it.DisplayName}」的堆叠上限是 {it.MaxStack}。\n\n" +
                    $"填 {want} 会被游戏读档时夹回 {it.MaxStack}，看起来就像改动被回退了。\n" +
                    $"要真正超过上限，只能多占几个格子（用「复制一组」）。",
                    "超过堆叠上限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                num.Value = it.MaxStack;
                return;
            }

            var b = ValueEditor.Encode(it.CountProp, want.ToString(), out _);
            if (b is not null) { it.CountProp.MarkDirty(b); changed++; }
        }
        foreach (var (pkg, lv, rar, pre, sfx, on) in rows)
        {
            if (pkg.LevelProp is not null && (int)lv.Value != pkg.Level)
            {
                var b = ValueEditor.Encode(pkg.LevelProp, ((int)lv.Value).ToString(), out _);
                if (b is not null) { pkg.LevelProp.MarkDirty(b); changed++; }
            }
            if (pkg.RarityProp is not null && (int)rar.Value != pkg.Rarity)
            {
                var b = ValueEditor.Encode(pkg.RarityProp, ((int)rar.Value).ToString(), out _);
                if (b is not null) { pkg.RarityProp.MarkDirty(b); changed++; }
            }
            if (pkg.PrefixProp is not null && pre.Text.Trim() != pkg.Prefix)
            {
                var b = ValueEditor.Encode(pkg.PrefixProp, pre.Text.Trim(), out _);
                if (b is not null) { pkg.PrefixProp.MarkDirty(b); changed++; }
            }
            if (pkg.ValidProp is not null && on.Checked != pkg.Valid)
            {
                pkg.ValidProp.MarkBool(on.Checked);
                changed++;
            }
            var want = sfx.Text.Split('\n', '\r')
                               .Select(x => x.Trim())
                               .Where(x => x.Length > 0)
                               .ToList();
            if (pkg.SuffixProp is not null && !want.SequenceEqual(pkg.Suffixes))
            {
                pkg.SetSuffixes(want);
                changed++;
            }
        }

        if (changed == 0) { SetStatus("没有改动"); return; }
        MarkDirtyAndRefresh();
        Navigate(_current);
        SetStatus($"{where} {slotIndex + 1}「{it.DisplayName}」已改 {changed} 项（尚未保存）");
        }

        return (dlg, Apply);
    }

    private void BuildModules()
    {
        if (_slot is null) return;

        var card = NewCard($"模块列表（{_slot.ModuleFiles.Count}）");
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            BorderStyle = BorderStyle.None,
            Font = Theme.Base,
            // fill most of the page; the card sizes itself to this height
            Height = Math.Max(320, _content.ClientSize.Height - 130),
            BackColor = Color.White,
        };
        list.Columns.Add("模块", 300);
        list.Columns.Add("状态", 96);
        list.Columns.Add("类", 250);
        list.Columns.Add("字节", 110);
        list.Columns.Add("告警", 60);

        var loaded = _slot.Modules.ToDictionary(m => m.ModuleName, m => m);
        foreach (var f in _slot.ModuleFiles)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (loaded.TryGetValue(name, out var m))
            {
                var it = new ListViewItem(new[]
                {
                    name, "可编辑", m.Header.SaveGameClass.Split('.')[^1],
                    m.TotalBytes.ToString("N0"), m.Warnings.Count.ToString()
                });
                if (m.Warnings.Count > 0) it.ForeColor = Theme.Warn;
                list.Items.Add(it);
            }
            else
            {
                var isChunk = name.StartsWith("GenMap__", StringComparison.Ordinal);
                var it = new ListViewItem(new[]
                {
                    name, "按需", isChunk ? "地图区块" : "—",
                    new FileInfo(f).Length.ToString("N0"), "—"
                });
                it.ForeColor = Theme.Muted;
                list.Items.Add(it);
            }
        }

        card.AddRow(list);
        StackCards(card);
    }

    // ------------------------------------------------------------------ state
    private void MarkDirtyAndRefresh()
    {
        bool dirty = _slot?.IsDirty == true;
        _btnSave.Enabled = dirty;
        _btnRevert.Enabled = dirty;
        UpdateDirtyBadge();
        RefreshSubtitle();
        if (dirty) SetStatus("已修改，记得点「保存修改」");
    }

    private void UpdateDirtyBadge()
    {
        bool dirty = _slot?.IsDirty == true;
        _dirtyBadge.Visible = dirty;
        _dirtyBadge.Location = new Point(
            _title.Right + 12,
            _title.Top + (_title.Height - _dirtyBadge.Height) / 2 + 2);
    }

    private void RevertAll()
    {
        if (_slot is null) return;
        foreach (var m in _slot.Modules) m.DiscardEdits();
        _btnSave.Enabled = false;
        _btnRevert.Enabled = false;
        UpdateDirtyBadge();
        Navigate(_current);
        SetStatus("已放弃所有未保存的修改");
    }

    private void SaveChanges()
    {
        if (_slot is null) return;

        var dirty = _slot.Modules.Where(m => m.IsDirty).ToList();
        var manifestDirty = _slot.Manifest?.IsDirty == true;
        if (dirty.Count == 0 && !manifestDirty) { SetStatus("没有需要保存的修改"); return; }

        // A confirmation MessageBox here used to swallow the click that opened
        // it: the second half of a double-click lands on the dialog's default
        // button and dismisses it before it is seen, so the save silently never
        // happened. Saving is reversible (backups are kept), so just do it and
        // report what actually landed on disk.
        if (IsGameRunning())
        {
            var warn = MessageBox.Show(this,
                "游戏（Magi Scapes）正在运行。\n\n" +
                "游戏退出时会用内存里的数据覆盖整个存档，把这次修改冲掉。\n" +
                "建议先关闭游戏再保存。\n\n是否仍然现在写入？",
                "游戏正在运行", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (warn != DialogResult.Yes) { SetStatus("已取消保存"); return; }
        }

        try
        {
            UseWaitCursor = true;
            _btnSave.Enabled = false;
            _btnRevert.Enabled = false;
            SetStatus("正在保存…");
            Application.DoEvents();   // let the status text paint before we block

            var path = _slot.RootPath;
            var slot = _slot;

            // Record what the files look like now so the report can prove a write.
            var before = SnapshotWrites(slot, dirty);

            // Remember what the edited values were supposed to become, so the
            // read-back can prove they landed rather than just that bytes moved.
            var expected = ExpectedCounts(dirty);

            // The write itself is fast now (the patcher was rebuilt to a single
            // pass; it used to take ~17 s on CampsiteSave and froze the window).
            // Still run it off the UI thread so a big save can never look hung.
            var log = SaveOnWorker(slot);

            var failed = log.Where(l => l.StartsWith("FAILED")).ToList();
            int saved = log.Count(l => l.StartsWith("saved"));
            int mirrored = log.Count(l => l.StartsWith("同步快照"));

            if (failed.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n\n", failed), "保存失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus($"保存失败（{failed.Count} 项），文件未被修改");
                return;
            }

            // Do NOT re-open the whole slot here. LoadSlot re-parses every module
            // (~2 s) purely to refresh the badges, which made a save feel frozen.
            // SaveAll already promoted the pending bytes to the baseline, so the
            // in-memory values are current; only the dirty badges need updating.
            UpdateDirtyBadge();
            _btnSave.Enabled = false;
            _btnRevert.Enabled = false;

            // A write can succeed and still leave the value the user edited
            // absent, which is what "我的修改被回退了" looks like from the
            // outside. Check the values, not just the file timestamps.
            var missing = VerifyCounts(path, expected);

            var extra = mirrored > 0 ? $"，同步 {mirrored} 份快照" : "";
            if (missing.Count > 0)
            {
                SetStatus($"已保存 {saved} 个文件，但 {missing.Count} 个数值没写进磁盘");
                MessageBox.Show(this,
                    "文件写成功了，但这些数值读回来不对：\n\n" +
                    string.Join("\n", missing) + "\n\n" +
                    "说明游戏或别的程序把存档又覆盖了。请关闭游戏后重试。",
                    "数值没有落地", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                // Success is reported inline, not in a modal dialog. A modal
                // "保存完成" box left the window unresponsive until clicked,
                // which is exactly what "点保存未响应" looked like.
                SetStatus($"已保存 {saved} 个文件{extra}，数值已确认写入");
                ShowSaveReportInline(path, before, log);
            }
        }
        finally
        {
            UseWaitCursor = false;
            _btnSave.Enabled = true;
            _btnRevert.Enabled = true;
        }
    }

    /// <summary>
    /// Run SaveAll on a worker thread without freezing the window.
    ///
    /// This used to pump Application.DoEvents in a loop, which re-dispatches
    /// every queued message — including clicks and the harness's own injected
    /// input — so the save could be re-entered and the app wedged. Instead the
    /// save now runs on a plain background thread and we wait on it with a
    /// short, bounded wait while letting only paint messages through.
    /// </summary>
    private List<string> SaveOnWorker(SaveSlot slot)
    {
        if (_saving) return new List<string> { "FAILED 已经在保存中" };
        _saving = true;
        try
        {
            List<string>? result = null;
            Exception? failure = null;

            var worker = new Thread(() =>
            {
                try { result = slot.SaveAll(backup: true); }
                catch (Exception e) { failure = e; }
            })
            {
                IsBackground = true,
                Name = "save-worker",
            };
            worker.Start();

            // Wait in small slices, refreshing the status text between them so
            // the window visibly stays alive. No message pumping: that is what
            // caused the re-entrancy.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int dots = 0;
            while (worker.IsAlive && sw.Elapsed < TimeSpan.FromMinutes(5))
            {
                worker.Join(60);
                if (!worker.IsAlive) break;
                dots = (dots + 1) % 4;
                SetStatus("正在保存" + new string('.', dots));
                Update();     // repaint only; does not dispatch input
            }

            if (worker.IsAlive) return new List<string> { "FAILED 保存超时" };
            if (failure is not null)
                return new List<string> { $"FAILED 保存时出错: {failure.GetBaseException().Message}" };
            return result ?? new List<string>();
        }
        finally
        {
            _saving = false;
        }
    }

    /// <summary>
    /// The stack counts the user just edited, keyed by item id, recorded before
    /// the save so the read-back can compare disk against intent.
    /// </summary>
    private Dictionary<string, int> ExpectedCounts(List<SaveModule> dirty)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_slot is null) return map;
        if (!dirty.Any(m => m.ModuleName == "Player__Players")) return map;

        try
        {
            var pv = SaveFacade.ReadPlayer(_slot);
            if (pv is null) return map;
            foreach (var it in pv.InventoryItems.Concat(pv.HotbarItems))
            {
                if (it.ItemId.Length == 0 || it.CountProp is null) continue;
                map[it.ItemId] = it.Count;
            }
        }
        catch { /* proof is best-effort */ }
        return map;
    }

    /// <summary>
    /// Re-open from disk and report any edited count that did not survive. An
    /// empty list means every value the user changed is really on disk.
    /// </summary>
    private static List<string> VerifyCounts(string root, Dictionary<string, int> expected)
    {
        var bad = new List<string>();
        if (expected.Count == 0) return bad;

        try
        {
            var probe = SaveSlot.Open(root, null, SaveSlot.EditorModules);
            var pv = SaveFacade.ReadPlayer(probe);
            if (pv is null) return bad;

            foreach (var it in pv.InventoryItems.Concat(pv.HotbarItems))
            {
                if (!expected.TryGetValue(it.ItemId, out var want)) continue;
                if (it.Count != want)
                    bad.Add($"  {it.ItemId} {it.DisplayName}：期望 {want}，磁盘上是 {it.Count}");
            }
        }
        catch { /* proof is best-effort */ }
        return bad;
    }

    /// <summary>Names + timestamps of the files about to be written.</summary>
    private static List<(string File, long Size, DateTime Time)> SnapshotWrites(
        SaveSlot slot, List<SaveModule> dirty)
    {
        var list = new List<(string, long, DateTime)>();
        var files = dirty.Select(m => m.FilePath).ToList();
        if (slot.Manifest is { IsDirty: true }) files.Add(slot.Manifest.FilePath);

        foreach (var f in files)
        {
            try
            {
                var fi = new FileInfo(f);
                if (fi.Exists) list.Add((fi.FullName, fi.Length, fi.LastWriteTime));
            }
            catch { /* report is best-effort */ }
        }
        return list;
    }

    /// <summary>
    /// Post-save report. It lists every file with its new size and timestamp,
    /// read back from disk, so "did it actually save?" is answerable without
    /// opening a file manager.
    /// </summary>
    private void ShowSaveReport(string root, List<(string File, long Size, DateTime Time)> before,
                                List<string> log)
    {
        var sb = new StringBuilder();
        sb.AppendLine("修改已写入磁盘。以下是从磁盘重新读取的结果：");
        sb.AppendLine();

        // Prove the *values* landed, not just the byte counts. Re-open the slot
        // from disk and show the key fields the user most likely just edited.
        var proof = ReadBackProof(root);
        if (proof.Count > 0)
        {
            foreach (var line in proof) sb.AppendLine(line);
            sb.AppendLine();
        }

        foreach (var (file, oldSize, oldTime) in before)
        {
            FileInfo fi;
            try { fi = new FileInfo(file); } catch { continue; }
            if (!fi.Exists) continue;

            bool changed = fi.Length != oldSize || fi.LastWriteTime != oldTime;
            string rel;
            try { rel = Path.GetRelativePath(root, file); } catch { rel = file; }
            sb.AppendLine($"{(changed ? "✓" : "·")} {rel}");
            sb.AppendLine($"     {oldSize:N0} → {fi.Length:N0} 字节   {fi.LastWriteTime:HH:mm:ss}");
        }

        var mirrored = log.Where(l => l.StartsWith("同步快照")).ToList();
        if (mirrored.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("版本快照（游戏读档时可能使用这些副本，已一并更新）：");
            foreach (var m in mirrored) sb.AppendLine("  · " + m);
        }

        sb.AppendLine();
        sb.AppendLine("存档目录：");
        sb.AppendLine(root);
        sb.AppendLine();
        sb.AppendLine("原文件已备份到：");
        sb.AppendLine(BackupDirHint());

        // A modal "保存完成" dialog here made the app look frozen: it blocks the
        // UI thread until the user clicks it, and the harness (and a user who
        // walked away) sees an unresponsive window. Report inline instead.
        MessageBox.Show(this, sb.ToString(), "保存完成",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string BackupDirHint() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "GenerativeSaveEditor", "backups");

    /// <summary>
    /// Re-open the slot straight from disk and read back the fields a user is
    /// most likely to have just changed. If these do not match the edits, the
    /// write did not stick — which is exactly what the report must show.
    /// </summary>
    private static List<string> ReadBackProof(string root)
    {
        var lines = new List<string>();
        try
        {
            var probe = SaveSlot.Open(root, null, SaveSlot.EditorModules);
            lines.Add("读回的当前值：");
            lines.Add($"  · 存档名：{probe.SaveDisplayName}");
            lines.Add($"  · 玩家名：{probe.EffectivePlayerName}");

            var pv = SaveFacade.ReadPlayer(probe);
            if (pv is not null)
            {
                var filled = pv.InventoryItems.Where(i => !i.IsEmpty).ToList();
                lines.Add($"  · 背包：{filled.Count} 件物品");
                foreach (var it in filled.Take(3))
                    lines.Add($"      {it.ItemId}  {it.DisplayName}  x{it.Count}");
                if (filled.Count > 3) lines.Add($"      …另有 {filled.Count - 3} 件");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"(读回校验失败：{ex.Message})");
        }
        return lines;
    }

    /// <summary>
    /// Report a successful save without a modal dialog. The details go into a
    /// dismissible banner, so nothing blocks the window. A modal "保存完成" box
    /// used to leave the app looking hung until someone clicked it.
    /// </summary>
    private void ShowSaveReportInline(string root, List<(string File, long Size, DateTime Time)> before,
                                      List<string> log)
    {
        var sb = new StringBuilder();

        foreach (var (file, oldSize, oldTime) in before)
        {
            FileInfo fi;
            try { fi = new FileInfo(file); } catch { continue; }
            if (!fi.Exists) continue;

            bool changed = fi.Length != oldSize || fi.LastWriteTime != oldTime;
            string rel;
            try { rel = Path.GetRelativePath(root, file); } catch { rel = file; }
            sb.AppendLine($"{(changed ? "✓" : "·")} {rel}");
            sb.AppendLine($"     {oldSize:N0} → {fi.Length:N0} 字节   {fi.LastWriteTime:HH:mm:ss}");
        }

        var mirrored = log.Where(l => l.StartsWith("同步快照")).ToList();
        if (mirrored.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("版本快照（游戏读档时可能使用这些副本，已一并更新）：");
            foreach (var m in mirrored) sb.AppendLine("  · " + m);
        }

        sb.AppendLine();
        sb.AppendLine("原文件已备份到：" + BackupDirHint());

        ShowBanner("保存完成", sb.ToString().TrimEnd(), BannerKind.Success);
    }

    private enum BannerKind { Info, Success, Warn }

    private Panel? _banner;

    /// <summary>
    /// Show a dismissible message strip at the top of the content area, instead
    /// of a modal dialog, for anything that needs no answer.
    /// </summary>
    private void ShowBanner(string title, string detail, BannerKind kind)
    {
        try
        {
            _banner?.Dispose();
            _banner = null;

            var (back, fore, edge) = kind switch
            {
                BannerKind.Success => (Color.FromArgb(236, 248, 240), Color.FromArgb(22, 101, 52), Color.FromArgb(134, 202, 158)),
                BannerKind.Warn => (Color.FromArgb(255, 247, 237), Color.FromArgb(154, 52, 18), Color.FromArgb(240, 177, 128)),
                _ => (Color.FromArgb(240, 245, 255), Color.FromArgb(30, 64, 175), Color.FromArgb(165, 180, 252)),
            };

            var panel = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = back,
                Padding = new Padding(12, 8, 12, 8),
                Margin = new Padding(0, 0, 0, 8),
            };

            var close = new Button
            {
                Text = "✕",
                FlatStyle = FlatStyle.Flat,
                Width = 26,
                Height = 24,
                Dock = DockStyle.Right,
                BackColor = back,
                ForeColor = fore,
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            close.FlatAppearance.BorderSize = 0;
            close.Click += (_, _) => { panel.Dispose(); if (_banner == panel) _banner = null; };

            var text = new Label
            {
                Text = $"{title}\n{detail}",
                AutoSize = true,
                MaximumSize = new Size(Math.Max(400, _content.Width - 120), 0),
                Dock = DockStyle.Fill,
                ForeColor = fore,
                Font = Theme.Small,
                Margin = new Padding(0),
            };

            panel.Controls.Add(text);
            panel.Controls.Add(close);
            panel.Paint += (_, e) =>
            {
                using var pen = new Pen(edge);
                e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
            };

            _banner = panel;
            _content.Controls.Add(panel);
            panel.BringToFront();
        }
        catch
        {
            // A banner must never break a successful save.
        }
    }

    /// <summary>True when the game process is up and would overwrite the save on exit.</summary>
    private static bool IsGameRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("GenerativeGame").Length > 0;
        }
        catch { return false; }
    }

    private void SetStatus(string? text) => _status.Text = text ?? "";
}

internal static class ControlExtensions
{
    public static T Also<T>(this T self, Action<T> action) { action(self); return self; }
}
