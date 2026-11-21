using GenerativeSave.Core.Container;

namespace GenerativeSave.Core;

/// <summary>A save slot folder: Manifest.sav plus Primary/Modules and Versions.</summary>
public sealed class SaveSlot
{
    public string RootPath { get; }
    public string DisplayName { get; }
    public SaveModule? Manifest { get; private set; }

    /// <summary>Modules parsed eagerly (the ones the editor exposes).</summary>
    public List<SaveModule> Modules { get; } = new();

    /// <summary>Every module file on disk, parsed or not.</summary>
    public List<string> ModuleFiles { get; } = new();

    public List<string> Warnings { get; } = new();

    private SaveSlot(string root)
    {
        RootPath = root;
        DisplayName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Open a slot. Only the modules in <paramref name="eager"/> are fully
    /// parsed; the large GenMap chunks (up to 23 MB each, 117 of them) are left
    /// on disk so opening the save stays fast. Use <see cref="LoadModule"/> to
    /// pull one in later.
    /// </summary>
    public static SaveSlot Open(string root, Action<string>? progress = null,
                                IReadOnlyCollection<string>? eager = null)
    {
        var slot = new SaveSlot(root);

        var manifestPath = Path.Combine(root, "Manifest.sav");
        if (File.Exists(manifestPath))
        {
            try { slot.Manifest = SaveModule.Load(manifestPath); }
            catch (Exception e) { slot.Warnings.Add($"Manifest: {e.Message}"); }
        }

        var modulesDir = Path.Combine(root, "Primary", "Modules");
        if (!Directory.Exists(modulesDir)) return slot;

        var files = Directory.GetFiles(modulesDir, "*.sav")
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                             .ToList();
        slot.ModuleFiles.AddRange(files);

        var wanted = files.Where(f => eager is null ||
                                      eager.Contains(Path.GetFileNameWithoutExtension(f)))
                          .ToList();

        var loaded = new SaveModule?[wanted.Count];
        Parallel.For(0, wanted.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1)
        }, i =>
        {
            var f = wanted[i];
            progress?.Invoke(Path.GetFileName(f));
            try { loaded[i] = SaveModule.Load(f); }
            catch (Exception e) { lock (slot.Warnings) slot.Warnings.Add($"{Path.GetFileName(f)}: {e.Message}"); }
        });

        foreach (var m in loaded)
            if (m is not null) slot.Modules.Add(m);

        slot.Modules.Sort((a, b) =>
            string.Compare(a.ModuleName, b.ModuleName, StringComparison.OrdinalIgnoreCase));

        return slot;
    }

    /// <summary>Modules the editor actually edits — everything except map chunks.</summary>
    public static readonly string[] EditorModules =
    {
        "Player__Players",
        "CampsiteSave__CampsiteSave",
        "Store__StoreData",
        "Raider__Raider",
        "WorldTime__WorldTime",
        "weather__weather",
        "QuestSystem__QuestData",
        "NpcSubsystem__NPC",
        "GenCharacter__AIGenCharacter",
        "BossSummon__BossSummon",
        "CollectionSystem__CollectionData",
        "DialogueSubsystem__DynamicDialogues",
        "Feast__Quota",
        "ZoneSubsystem__Zone",
        "Minimap__Fog",
        "TeleportStone__TeleportStoneRecords",
        "GenMap__PlayerMap",
        "GenPlaceable__AIGenPlaceable",
        "Reward__Reward",
        "GlobalCondition__GlobalCondition",
        "NPCMood__NPCMoodData",
        "NPCCraftRequests__NPCCraftRequests",
        "NPCMemory__NPCMemoryData",
    };

    /// <summary>Load one module by name if it is not already parsed.</summary>
    public SaveModule? LoadModule(string moduleName)
    {
        var hit = Modules.FirstOrDefault(m => m.ModuleName == moduleName);
        if (hit is not null) return hit;

        var path = ModuleFiles.FirstOrDefault(f =>
            string.Equals(Path.GetFileNameWithoutExtension(f), moduleName, StringComparison.OrdinalIgnoreCase));
        if (path is null) return null;

        try
        {
            var m = SaveModule.Load(path);
            Modules.Add(m);
            return m;
        }
        catch (Exception e)
        {
            Warnings.Add($"{moduleName}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Drop a parsed module from the cache without touching its file. Map chunks
    /// hold up to ~25 000 terrain rows and are read one at a time, so keeping
    /// every visited chunk alive grew the process by hundreds of megabytes per
    /// click until it was killed. Only unedited modules are evicted; anything
    /// holding pending edits stays so the user cannot lose work.
    /// </summary>
    public void ReleaseModule(string moduleName)
    {
        var m = Modules.FirstOrDefault(x => x.ModuleName == moduleName);
        if (m is null || m.IsDirty) return;
        Modules.Remove(m);
    }

    // ----------------------------------------------------------- manifest data
    public string SaveDisplayName => ManifestValue<string>("SaveDisplayName") ?? DisplayName;
    public string PlayerName => ManifestValue<string>("PlayerName") ?? "?";

    /// <summary>The player name the game actually loads, preferring the player module.</summary>
    public string EffectivePlayerName =>
        Editing.SaveFacade.PlayerNameProp(this) is { } p
            ? Editing.ValueEditor.EffectiveValue(p) as string ?? PlayerName
            : PlayerName;

    public int MapSeed => ManifestValue<int>("MapSeed");
    public double PlayTime => ManifestValue<double>("PlayTime");
    public long ActiveVersion => ManifestValue<long>("ActiveVersion");
    public long[] Versions => ManifestValue<long[]>("Versions") ?? Array.Empty<long>();
    public long LastKnownGoodVersion => ManifestValue<long>("LastKnownGoodVersion");

    private T? ManifestValue<T>(string name)
    {
        var prop = Manifest?.FindProperty(name);
        if (prop is null) return default;
        var v = Editing.ValueEditor.EffectiveValue(prop);
        if (v is null) return default;

        // Manifest mixes float and double; convert rather than pattern-match
        // so PlayTime (a FloatProperty) is not silently reported as 0.
        try
        {
            if (v is T direct) return direct;
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (target == typeof(double)) return (T)(object)Convert.ToDouble(v);
            if (target == typeof(float)) return (T)(object)Convert.ToSingle(v);
            if (target == typeof(int)) return (T)(object)Convert.ToInt32(v);
            if (target == typeof(long)) return (T)(object)Convert.ToInt64(v);
            if (target == typeof(string)) return (T)(object)Convert.ToString(v)!;
        }
        catch { /* fall through */ }
        return default;
    }

    public SaveModule? Find(string moduleName) =>
        Modules.FirstOrDefault(m => m.ModuleName == moduleName);

    public bool IsDirty => Modules.Any(m => m.IsDirty) || (Manifest?.IsDirty ?? false);

    /// <summary>
    /// Save every dirty module. Each file is verified by re-parsing the result
    /// before it replaces the original. Manifest.sav is plain GVAS rather than
    /// a chunked container, so it is written raw.
    ///
    /// The game keeps rolling snapshots under Versions\v_&lt;id&gt;\Modules and
    /// will happily restore one of them, which silently undoes an edit made only
    /// in Primary. Every version snapshot that already contains a changed module
    /// is therefore patched with the same bytes, so whichever copy the game
    /// picks up agrees with the editor.
    /// </summary>
    public List<string> SaveAll(bool backup = true)
    {
        var log = new List<string>();
        var written = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        if (Manifest is { IsDirty: true })
        {
            try
            {
                var payload = Manifest.BuildPayload();
                var probe = new Parsing.SaveParser(payload);
                probe.ParseHeader();
                probe.ParseProperties();
                if (probe.Warnings.Count > Manifest.Warnings.Count)
                    throw new InvalidOperationException(
                        $"patched manifest introduced {probe.Warnings.Count - Manifest.Warnings.Count} new warnings");

                if (backup) BackupFile(Manifest.FilePath);

                File.WriteAllBytes(Manifest.FilePath, payload);
                Manifest.DiscardEdits();
                log.Add($"saved {Path.GetFileName(Manifest.FilePath)}");
            }
            catch (Exception e)
            {
                log.Add($"FAILED {Path.GetFileName(Manifest.FilePath)}: {e.Message}");
            }
        }

        foreach (var m in Modules.Where(x => x.IsDirty))
        {
            try
            {
                var payload = m.BuildPayload();

                // sanity: the patched payload must still parse cleanly
                var probe = new Parsing.SaveParser(payload);
                probe.ParseHeader();
                probe.ParseProperties();
                if (probe.Warnings.Count > m.Warnings.Count)
                    throw new InvalidOperationException(
                        $"patched payload introduced {probe.Warnings.Count - m.Warnings.Count} new warnings");

                if (backup) BackupFile(m.FilePath);

                var encoded = SaveContainer.Wrap(payload);
                File.WriteAllBytes(m.FilePath, encoded);
                written[Path.GetFileName(m.FilePath)] = encoded;
                m.DiscardEdits();
                log.Add($"saved {Path.GetFileName(m.FilePath)}");
            }
            catch (Exception e)
            {
                log.Add($"FAILED {Path.GetFileName(m.FilePath)}: {e.Message}");
            }
        }

        if (written.Count > 0) MirrorToVersions(written, backup, log);

        return log;
    }

    /// <summary>
    /// Back a file up outside the save folder.
    ///
    /// A .bak sitting next to the module used to look harmless, but the game
    /// scans its module directories and a stray non-.sav entry can confuse the
    /// loader or its consistency check. Backups therefore live under
    /// %LOCALAPPDATA%\GenerativeSaveEditor\backups\&lt;slot&gt;\&lt;timestamp&gt;,
    /// mirroring the relative path, so the save tree keeps exactly the files
    /// the game wrote.
    /// </summary>
    private void BackupFile(string path)
    {
        try
        {
            var root = Path.GetFullPath(RootPath);
            var full = Path.GetFullPath(path);
            var rel = Path.GetRelativePath(root, full);
            if (rel.StartsWith("..")) rel = Path.GetFileName(full);   // outside the slot

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GenerativeSaveEditor", "backups", Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)),
                stamp);
            var dest = Path.Combine(dir, rel);

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest)) File.Copy(full, dest);
        }
        catch { /* a failed backup must never block the save */ }
    }

    /// <summary>
    /// Copy freshly written module bytes into every Versions\v_*\Modules folder
    /// that holds the same file name. Snapshots are what the game may restore,
    /// so leaving them stale is what makes an edit appear to "roll back".
    /// </summary>
    private void MirrorToVersions(IReadOnlyDictionary<string, byte[]> written, bool backup, List<string> log)
    {
        var versionsRoot = Path.Combine(RootPath, "Versions");
        if (!Directory.Exists(versionsRoot)) return;

        foreach (var vdir in Directory.GetDirectories(versionsRoot))
        {
            var modDir = Path.Combine(vdir, "Modules");
            if (!Directory.Exists(modDir)) continue;

            int n = 0;
            foreach (var (name, bytes) in written)
            {
                var target = Path.Combine(modDir, name);
                if (!File.Exists(target)) continue;

                try
                {
                    if (backup) BackupFile(target);
                    File.WriteAllBytes(target, bytes);
                    n++;
                }
                catch (Exception e)
                {
                    log.Add($"FAILED version {Path.GetFileName(vdir)}/{name}: {e.Message}");
                }
            }

            if (n > 0) log.Add($"同步快照 {Path.GetFileName(vdir)} ({n} 个模块)");
        }
    }
}
