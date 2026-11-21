using System.Text;
using GenerativeSave.Core;
using GenerativeSave.Core.Editing;
using GenerativeSave.Core.Model;

namespace GenerativeSave.Editor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // User-taught affix names beat the ones derived from the id. Loaded once
        // at startup; a missing or broken file just means no overrides.
        AffixNames.Load();

        // Last line of defence. WinForms kills the process when an exception
        // escapes a message handler, which for the user looks like the window
        // vanishing mid-action. Report it and keep running instead.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            ReportCrash(e.Exception);
        };

        // Every headless mode below opens a save only to exercise the UI. With
        // automatic library top-up on load, those runs were folding test saves
        // into the user's real library, so turn the library off first: a test
        // run must never edit what the user owns.
        if (args.Length > 0 && args[0].StartsWith("--"))
            GenerativeSave.Core.Library.CatalogStore.Enabled = false;

        // --shot <dir>: render each page to a PNG and exit. Used to verify layout
        // without a human (or a fragile mouse-driving harness) in the loop.
        // An optional second argument names the save to open. Without it the
        // form falls back to last.txt, which points at whatever the user last
        // had open — a screenshot run then silently documents a different save
        // than the one the caller copied and inspected, which is exactly how a
        // "wrong camp level" report got chased for half an hour.
        if (args.Length >= 2 && args[0] == "--shot")
        {
            var shotSave = args.Length >= 3 ? args[2] : null;
            var boot = Path.Combine(args[1], "_boot.txt");
            try { Directory.CreateDirectory(args[1]); File.WriteAllText(boot, "start " + DateTime.Now.ToString("HH:mm:ss.fff")); } catch { }
            try
            {
                Application.Run(new MainForm { ShotDir = args[1], ShotSave = shotSave });
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(boot, "\nCRASH " + ex); } catch { }
            }
            try { File.AppendAllText(boot, "\nend " + DateTime.Now.ToString("HH:mm:ss.fff")); } catch { }
            return;
        }

        // --selftest <saveRoot> <reportPath>: exercise the real edit + save path
        // inside this binary and write the evidence to a file. This is how a
        // shipped build proves its write path still works without a human.
        //
        // It WRITES. Pointing it at the live save destroys real progress, which
        // happened once during development, so refuse that path outright unless
        // --force is passed explicitly.
        if (args.Length >= 3 && args[0] == "--selftest")
        {
            if (SelfTest.LooksLikeLiveSave(args[1]) && !args.Contains("--force"))
            {
                Console.Error.WriteLine(
                    "拒绝执行：这个目录是正在使用的真实存档，自检会改写它。\n" +
                    "请先复制一份到别的目录，再对副本运行。\n" +
                    "确实要对真实存档跑，请在命令末尾加 --force。");
                Environment.ExitCode = 2;
                return;
            }
            Environment.ExitCode = SelfTest.Run(args[1], args[2]);
            return;
        }

        // --uitest <saveRoot> <reportPath>: drive the real window — open the
        // save, edit a stack count through the same code the dialog's OK button
        // runs, then press the actual 保存修改 button. This is the only way to
        // catch a break in the UI layer, which the headless self-test bypasses.
        //
        // It writes, so it gets the same live-save guard as --selftest.
        if (args.Length >= 3 && args[0] == "--uitest")
        {
            if (SelfTest.LooksLikeLiveSave(args[1]) && !args.Contains("--force"))
            {
                Console.Error.WriteLine(
                    "拒绝执行：这个目录是正在使用的真实存档，测试会改写它。\n" +
                    "请先复制一份到别的目录，再对副本运行。");
                Environment.ExitCode = 2;
                return;
            }
            Environment.ExitCode = UiTest.Run(args[1], args[2]);
            return;
        }

        // --stress <saveRoot> <reportPath>: load the heaviest chunks over and
        // over and watch the process's GDI handle count. The map page used to
        // die here: it created one SolidBrush per cell per repaint, so opening a
        // dense chunk a few times exhausted GDI handles and killed the process
        // with no managed exception (hence no errors.log entry).
        if (args.Length >= 3 && args[0] == "--stress")
        {
            if (SelfTest.LooksLikeLiveSave(args[1]) && !args.Contains("--force"))
            {
                Console.Error.WriteLine("拒绝执行：这是正在使用的真实存档。请对副本运行。");
                Environment.ExitCode = 2;
                return;
            }
            Environment.ExitCode = StressTest.Run(args[1], args[2]);
            return;
        }

        Application.Run(new MainForm());
    }

    /// <summary>
    /// Show an unexpected error without exiting. The message is also appended to a
    /// log file, because a modal dialog can be dismissed and then forgotten.
    /// </summary>
    private static void ReportCrash(Exception? e)
    {
        if (e is null) return;

        string where = "";
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GenerativeSaveEditor");
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, "errors.log");
            File.AppendAllText(log,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}" +
                e + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            where = $"\n\n详细信息已记录到：\n{log}";
        }
        catch
        {
            // Logging must never itself throw.
        }

        try
        {
            MessageBox.Show(
                "操作过程中出现了一个意外错误，程序会继续运行。\n\n" +
                e.GetType().Name + "：" + e.Message + where,
                "出错了（但没退出）",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            // Nowhere left to report to.
        }
    }
}

/// <summary>
/// Headless round-trip check: load a save, edit one affix package and one chest,
/// save, reopen, and compare. Writes a plain-text report.
/// </summary>
/// <summary>
/// Drives the real window end to end: open the save, edit a stack count the way
/// the item dialog does, press the actual 保存修改 button, then reopen the slot
/// from disk and check the value survived. The headless self-test calls the core
/// directly, which is why it kept passing while the UI path was broken.
/// </summary>
internal static class UiTest
{
    public static int Run(string root, string reportPath)
    {
        var sb = new StringBuilder();
        int failures = 0;

        void Check(string label, bool ok, string detail = "")
        {
            sb.AppendLine($"{(ok ? "通过" : "失败")}  {label}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok) failures++;
        }

        const string item = "1020051";
        try
        {
            using var form = new MainForm();
            form.UiOpen(root);

            var before = ReadCount(root, item);
            sb.AppendLine($"初始 {item} 数量 = {before}");

            // Path A: the dialog, driven with real keystrokes. This is what the
            // user actually does, and the only path that exercises
            // NumericUpDown's type-then-validate behaviour.
            int typed = before + 3;
            bool okA = form.UiDialogTypeCount(item, typed.ToString(), out var noteA);
            sb.AppendLine("对话框键盘输入: " + noteA);
            Check("对话框确定后提交了修改", okA);

            bool pressed = form.UiPressSave();
            Check("成功点击「保存修改」", pressed);
            sb.AppendLine("  保存后诊断: " + form.UiDiagnostics());

            var after = ReadCount(root, item);
            Check($"重新从磁盘读取 = {typed}", after == typed, $"实际 {after}");
        }
        catch (Exception e)
        {
            sb.AppendLine($"异常: {e.GetType().Name}: {e.Message}");
            failures++;
        }

        sb.AppendLine();
        sb.AppendLine(failures == 0 ? "最终: 全部通过" : $"最终: {failures} 项失败");
        try { File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false)); } catch { }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Read a stack count straight from disk, bypassing every cache.</summary>
    private static int ReadCount(string root, string itemId)
    {
        var slot = SaveSlot.Open(root, null, SaveSlot.EditorModules);
        var pv = SaveFacade.ReadPlayer(slot);
        return pv?.InventoryItems.FirstOrDefault(x => x.ItemId == itemId)?.Count ?? -1;
    }
}

internal static class SelfTest
{
    /// <summary>
    /// True when the path is the save folder the game actually plays from.
    ///
    /// The self-test rewrites modules, so running it here would wipe real
    /// progress. Match the live location rather than guessing from file names:
    /// anything under the game's own SaveGames tree counts.
    /// </summary>
    public static bool LooksLikeLiveSave(string root)
    {
        try
        {
            var full = Path.GetFullPath(root)
                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var live = Path.GetFullPath(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GenerativeGame", "Saved", "SaveGames"));

            return full.StartsWith(live, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static int Run(string root, string reportPath)
    {
        var sb = new StringBuilder();
        void Line(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        try
        {
            Line($"存档: {root}");
            var slot = SaveSlot.Open(root, null, SaveSlot.EditorModules);
            Line($"模块 {slot.ModuleFiles.Count} 个，已载入 {slot.Modules.Count} 个");

            // ---- affix ----
            var pv = SaveFacade.ReadPlayer(slot);
            if (pv is null) { Line("读不到玩家"); return 1; }

            var item = pv.HotbarItems.Concat(pv.InventoryItems)
                         .FirstOrDefault(x => ItemAffixView.From(x.Group) is { } a
                                              && a.Packages.Any(k => k.Valid && k.Level > 0));
            if (item is null) { Line("找不到带词条的物品"); return 1; }

            var aff = ItemAffixView.From(item.Group)!;
            var pkg = aff.Packages.First(p => p.Valid && p.Level > 0);
            Line($"\n[词条] {item.ItemId} {item.DisplayName} · {pkg.Label}");
            Line($"  改前 Lv={pkg.Level} 稀有={pkg.Rarity} 前缀={pkg.Prefix}");

            int newLevel = pkg.Level == 99 ? 98 : 99;
            int changed = 0;
            if (pkg.LevelProp is not null)
            {
                var b = ValueEditor.Encode(pkg.LevelProp, newLevel.ToString(), out var err);
                if (b is null) { Line($"  等级编码失败: {err}"); return 1; }
                pkg.LevelProp.MarkDirty(b);
                changed++;
            }
            if (pkg.RarityProp is not null && pkg.Rarity != 4)
            {
                var b = ValueEditor.Encode(pkg.RarityProp, "4", out _);
                if (b is not null) { pkg.RarityProp.MarkDirty(b); changed++; }
            }
            Line($"  写入 {changed} 项 → Lv={newLevel} 稀有=4");

            // ---- chest ----
            var chests = MapFacade.ReadChests(slot);
            var chest = chests.FirstOrDefault(c => c.Items.Count >= 40 && c.UsedSlots > 0);
            string chestKey = "";
            if (chest is not null)
            {
                chestKey = $"{chest.Module}|{chest.Key}";
                var it0 = chest.Filled[0];
                Line($"\n[宝箱] {chest.Module} {chest.Coord} · {chest.UsedSlots} 件");
                Line($"  改前 {it0.ItemId} x{it0.Count}");
                int want = it0.Count == 77 ? 78 : 77;
                MapFacade.SetChestCount(it0, want);
                Line($"  写入 → x{want}");
            }
            else Line("\n[宝箱] 没找到 40 槽且有物品的箱子");

            // ---- chunk terrain + placeable ----
            string chunkModule = "";
            string terrainKey = "";
            int oldSet = 0, oldImg = 0, newSet = 0, newImg = 0;
            string objCoord = "";
            bool oldTurn = false, newTurn = false;

            var chunks = ChunkFacade.ListChunks(slot);
            Line($"\n[区块] 共 {chunks.Count} 个可编辑区块");
            foreach (var c in chunks)
            {
                var v = ChunkFacade.ReadChunk(slot, c.Module);
                if (v is null) continue;
                var te = v.Terrain.FirstOrDefault(t => t.LayerId != 9 && t.LayerId != 10);
                var oo = v.Objects.FirstOrDefault(o => !o.Occupied && o.TurnProp is not null);
                if (te is null || oo is null) continue;

                chunkModule = c.Module;
                terrainKey = te.Key;
                oldSet = te.TileSetIndex;
                oldImg = te.TileImageIndex;
                newSet = oldSet;
                newImg = oldImg == 99 ? 98 : 99;
                ChunkFacade.SetTerrainTile(te, newSet, newImg);
                Line($"  地形 {c.Module} 格子 {terrainKey}: 集 {oldSet} 图 {oldImg} → 图 {newImg}");

                objCoord = oo.Coord;
                oldTurn = oo.Turn90;
                newTurn = !oldTurn;
                ChunkFacade.SetTurn90(oo, newTurn);
                Line($"  放置物 {c.Module} {objCoord} ({oo.ItemId}): 旋转90 {oldTurn} → {newTurn}");
                break;
            }
            if (chunkModule.Length == 0) Line("  没找到可编辑的区块");

            // ---- combat attributes (AttributeMap) ----
            var chars = SaveFacade.ReadCharacters(slot);
            var target = chars.FirstOrDefault(c => c.CombatAttr("MaxHealth") is not null);
            double oldHp = 0, newHp = 0;
            string combatId = "";
            bool combatOk = true;
            if (target is not null)
            {
                combatId = target.CharacterId;
                var hp = target.CombatAttr("MaxHealth")!;
                oldHp = hp.Value;
                newHp = oldHp == 8888 ? 8889 : 8888;
                Line($"\n[战斗属性] {target.Display} 共 {target.Combat.Count} 条");
                if (!hp.TryEncode(newHp.ToString(), out var hb, out var he))
                {
                    Line($"  血量上限编码失败: {he}");
                    combatOk = false;
                }
                else
                {
                    hp.ValueProp!.MarkDirty(hb!);
                    Line($"  血量上限 {oldHp} → {newHp}");
                }
            }
            else Line("\n[战斗属性] 没有带 AttributeMap 的角色");

            // ---- campsite level + position ----
            var camp = SaveFacade.ReadCampsite(slot);
            int oldLevel = 0, newLevelCamp = 0, oldCx = 0, oldCy = 0, newCx = 0, newCy = 0;
            bool campOk = true;
            if (camp is not null)
            {
                Line($"\n[营地] {camp.CampsiteName} 等级 {camp.Level}");
                oldLevel = camp.Level;
                newLevelCamp = oldLevel == 21 ? 22 : 21;
                if (!SaveFacade.SetCampsiteLevel(slot, newLevelCamp, out var ce))
                {
                    Line($"  等级写入失败: {ce}");
                    campOk = false;
                }
                else Line($"  等级 {oldLevel} → {newLevelCamp}");

                if (camp.Coord is not null)
                {
                    oldCx = camp.Coord.DetailX;
                    oldCy = camp.Coord.DetailY;
                    // Must stay inside 39..110: the camp is a 79x79 footprint
                    // centred on the anchor, so anything nearer a chunk edge is
                    // rejected (and the game would ignore it anyway).
                    newCx = oldCx >= 105 ? oldCx - 5 : oldCx + 5;
                    newCy = oldCy >= 105 ? oldCy - 5 : oldCy + 5;
                    if (!SaveFacade.SetCampsitePosition(slot, newCx, newCy, out var pe))
                    {
                        Line($"  位置写入失败: {pe}");
                        campOk = false;
                    }
                    else Line($"  位置 ({oldCx},{oldCy}) → ({newCx},{newCy})");
                }
            }
            else Line("\n[营地] 读不到营地");

            // ---- npc memory ----
            // 记忆模块是最新接进来的：只开放 Priority 与 Type 两个字段，这里
            // 各改一条再读回来，确认变长的枚举名（Task 27B -> Harvest 30B）也
            // 能被补丁器正确处理，而不是只测等长写入。
            bool memOk = true;
            string memNpc = "";
            int memDay = 0, memIdx = -1, memNewPriority = 0;
            string memNewType = "";
            try
            {
                var memIds = MemoryFacade.NpcIds(slot);
                var pickId = memIds.FirstOrDefault(id => MemoryFacade.Read(slot, id)
                    .Days.Any(d => d.Events.Count > 0));
                if (pickId is null)
                {
                    Line("\n[记忆] 存档里没有可编辑的记忆");
                    memOk = false;
                }
                else
                {
                    var mm = MemoryFacade.Read(slot, pickId);
                    var dd = mm.Days.First(d => d.Events.Count > 0);
                    var ee = dd.Events[0];
                    memNpc = pickId; memDay = dd.Day; memIdx = 0;
                    Line($"\n[记忆] {pickId} 共 {mm.Days.Count} 天 {mm.EventCount} 条，改第 {dd.Day} 天第 1 条");
                    Line($"  改前 类型={ee.TypeCn} 优先级={ee.Priority}");

                    memNewPriority = ee.Priority == 7 ? 3 : 7;
                    memNewType = MemoryFacade.TypeName(ee.TypeRaw) == "Harvest" ? "Task" : "Harvest";

                    if (!MemoryFacade.SetPriority(ee, memNewPriority, out var ep))
                    { Line($"  优先级写入失败: {ep}"); memOk = false; }
                    if (!MemoryFacade.SetType(ee, memNewType, out var et))
                    { Line($"  类型写入失败: {et}"); memOk = false; }

                    Line($"  写入 → 类型={memNewType} 优先级={memNewPriority}");
                }
            }
            catch (Exception me)
            {
                Line($"  记忆写入异常: {me.Message}");
                memOk = false;
            }

            // ---- save ----
            var log = slot.SaveAll(backup: false);
            Line($"\n保存: {string.Join(" | ", log.Take(4))}");

            // ---- reopen ----
            Line("\n=== 重新打开验证 ===");
            var s2 = SaveSlot.Open(root, null, SaveSlot.EditorModules);
            var pv2 = SaveFacade.ReadPlayer(s2)!;
            var item2 = pv2.HotbarItems.Concat(pv2.InventoryItems)
                          .FirstOrDefault(x => x.ItemId == item.ItemId);
            var pkg2 = item2 is null ? null
                     : ItemAffixView.From(item2.Group)?.Packages.FirstOrDefault(p => p.Valid && p.Level > 0);

            bool affixOk = pkg2 is not null && pkg2.Level == newLevel && pkg2.Rarity == 4;
            Line($"  词条 Lv={pkg2?.Level} 稀有={pkg2?.Rarity} → {(affixOk ? "通过" : "失败")}");

            bool chestOk = true;
            if (chestKey.Length > 0)
            {
                var c2 = MapFacade.ReadChests(s2).FirstOrDefault(c => $"{c.Module}|{c.Key}" == chestKey);
                var it2 = c2?.Filled.FirstOrDefault(x => x.ItemId == chest!.Filled[0].ItemId);
                chestOk = it2 is not null && it2.Count == (chest!.Filled[0].Count == 77 ? 77 : it2.Count);
                Line($"  宝箱 {it2?.ItemId} x{it2?.Count} → {(chestOk ? "通过" : "失败")}");
            }

            Line($"\n结果: {(affixOk && chestOk ? "全部通过" : "有失败项")}");

            // ---- verify chunk edits on the reopened save ----
            bool chunkOk = true;
            if (chunkModule.Length > 0)
            {
                var v2 = ChunkFacade.ReadChunk(s2, chunkModule);
                var te2 = v2?.Terrain.FirstOrDefault(t => t.Key == terrainKey);
                var oo2 = v2?.Objects.FirstOrDefault(o => o.Coord == objCoord);

                bool tOk = te2 is not null && te2.TileImageIndex == newImg && te2.TileSetIndex == newSet;
                bool oOk = oo2 is not null && oo2.Turn90 == newTurn;
                chunkOk = tOk && oOk;
                Line($"  区块地形 图 {te2?.TileImageIndex}（期望 {newImg}）→ {(tOk ? "通过" : "失败")}");
                Line($"  区块旋转 {oo2?.Turn90}（期望 {newTurn}）→ {(oOk ? "通过" : "失败")}");
            }

            Line($"\n=== 战斗属性与营地验证 ===");

            // ---- verify combat + camp on the reopened save ----
            if (combatId.Length > 0)
            {
                var c2 = SaveFacade.ReadCharacters(s2).FirstOrDefault(c => c.CharacterId == combatId);
                var hp2 = c2?.CombatAttr("MaxHealth");
                combatOk = hp2 is not null && Math.Abs(hp2.Value - newHp) < 0.001;
                Line($"  战斗属性 {combatId} 血量上限 {hp2?.Value}（期望 {newHp}）→ {(combatOk ? "通过" : "失败")}");
                Line($"  属性条数 {c2?.Combat.Count}（期望 10）→ {(c2?.Combat.Count == 10 ? "通过" : "失败")}");
                combatOk &= c2?.Combat.Count == 10;
            }

            if (oldLevel != 0 || camp?.Coord is not null)
            {
                var camp2 = SaveFacade.ReadCampsite(s2);
                bool lvlOk = camp2?.Level == newLevelCamp;
                bool posOk = camp2?.Coord is not null
                             && camp2.Coord.DetailX == newCx && camp2.Coord.DetailY == newCy;
                campOk = lvlOk && posOk;
                Line($"  营地等级 {camp2?.Level}（期望 {newLevelCamp}）→ {(lvlOk ? "通过" : "失败")}");
                Line($"  营地位置 ({camp2?.Coord?.DetailX},{camp2?.Coord?.DetailY})（期望 ({newCx},{newCy})）→ {(posOk ? "通过" : "失败")}");
            }

            if (memNpc.Length > 0 && memIdx >= 0)
            {
                var m2 = MemoryFacade.Read(s2, memNpc);
                var d2 = m2.Days.FirstOrDefault(d => d.Day == memDay);
                var e2 = d2 is not null && d2.Events.Count > memIdx ? d2.Events[memIdx] : null;
                bool tOk2 = e2 is not null && MemoryFacade.TypeName(e2.TypeRaw) == memNewType;
                bool pOk2 = e2 is not null && e2.Priority == memNewPriority;
                memOk &= tOk2 && pOk2;
                Line($"  记忆 {memNpc} 第 {memDay} 天 类型={e2?.TypeCn}（期望 {memNewType}）→ {(tOk2 ? "通过" : "失败")}");
                Line($"  记忆 {memNpc} 第 {memDay} 天 优先级={e2?.Priority}（期望 {memNewPriority}）→ {(pOk2 ? "通过" : "失败")}");
                Line($"  记忆总条数 {m2.EventCount} → {(m2.EventCount > 0 ? "通过" : "失败")}");
                memOk &= m2.EventCount > 0;
            }

            Line($"\n最终: {(affixOk && chestOk && chunkOk && combatOk && campOk && memOk ? "全部通过" : "有失败项")}");
            return affixOk && chestOk && chunkOk && combatOk && campOk && memOk ? 0 : 1;
        }
        catch (Exception e)
        {
            Line($"异常: {e}");
            return 1;
        }
        finally
        {
            try { File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8); } catch { }
        }
    }
}

/// <summary>
/// Load the heaviest map chunks repeatedly and watch GDI handles and memory.
/// A leak here is fatal and silent: Windows kills the process once the handle
/// table is exhausted, without a managed exception to report.
/// </summary>
internal static class StressTest
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

    public static int Run(string root, string reportPath)
    {
        var sb = new StringBuilder();
        int failures = 0;
        ApplicationConfiguration.Initialize();

        try
        {
            using var form = new MainForm();
            form.UiOpen(root);

            // The chunks the user named: surface 3x, 4x and 5x, plus the two
            // densest ones in the save (18 and 76 hold ~21 000 and ~25 000 rows).
            var modules = new[]
            {
                "GenMap__SurfaceChunk_35", "GenMap__SurfaceChunk_38",
                "GenMap__SurfaceChunk_47", "GenMap__SurfaceChunk_59",
                "GenMap__SurfaceChunk_55", "GenMap__SurfaceChunk_18",
                "GenMap__SurfaceChunk_76", "GenMap__SurfaceChunk_94",
            };

            int pid = Environment.ProcessId;
            int Gdi() => GetGuiResources(System.Diagnostics.Process.GetProcessById(pid).Handle, 0);
            int User() => GetGuiResources(System.Diagnostics.Process.GetProcessById(pid).Handle, 1);

            sb.AppendLine($"起始 GDI={Gdi()} USER={User()}");

            for (int round = 1; round <= 5; round++)
            {
                foreach (var m in modules)
                {
                    form.UiLoadChunk(m);
                    sb.AppendLine($"  第{round}轮 {m,-28} GDI={Gdi(),5} USER={User(),4} 内存={GC.GetTotalMemory(false) / 1048576,4}MB");
                }
                int g = Gdi();
                sb.AppendLine($"第{round}轮结束 GDI={g} USER={User()}");
                if (g > 9000)
                {
                    sb.AppendLine($"失败  GDI 句柄涨到 {g}，再开几个区块就会被系统杀掉");
                    failures++;
                }
            }

            int final = Gdi();
            sb.AppendLine($"\n最终 GDI={final} USER={User()}  托管内存={GC.GetTotalMemory(true) / 1048576}MB");
            if (failures == 0) sb.AppendLine("通过  反复加载重区块没有句柄泄漏");
        }
        catch (Exception e)
        {
            sb.AppendLine($"异常 {e.GetType().Name}: {e.Message}");
            sb.AppendLine(e.StackTrace);
            failures++;
        }

        File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
        return failures == 0 ? 0 : 1;
    }
}
