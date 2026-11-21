using GenerativeSave.Core.Container;
using GenerativeSave.Core.IO;
using GenerativeSave.Core.Model;
using GenerativeSave.Core.Parsing;
using GenerativeSave.Core.Patching;

namespace GenerativeSave.Core;

/// <summary>One .sav module file (a single SaveGame class instance).</summary>
public sealed class SaveModule
{
    public string FilePath { get; }
    public string ModuleName { get; }
    public byte[] GvasPayload { get; private set; }
    public SaveHeader Header { get; private set; } = new();
    public List<SaveProperty> Properties { get; private set; } = new();
    public List<string> Warnings { get; } = new();
    public int ParsedBytes { get; private set; }
    public int TotalBytes => GvasPayload.Length;

    /// <summary>
    /// True when the chunked container can be rebuilt byte-identically.
    /// Computed on demand: re-compressing a 20 MB chunk costs seconds, so it
    /// must never happen during load. Null means "not checked yet".
    /// </summary>
    public bool? ContainerRoundTrips { get; private set; }

    private readonly byte[] _originalFile;

    private SaveModule(string path, byte[] gvas, byte[] originalFile)
    {
        FilePath = path;
        ModuleName = Path.GetFileNameWithoutExtension(path);
        GvasPayload = gvas;
        _originalFile = originalFile;
    }

    public static SaveModule Load(string path, bool verifyContainer = false)
    {
        var file = File.ReadAllBytes(path);
        var gvas = SaveContainer.Unwrap(file);
        var m = new SaveModule(path, gvas, file);
        m.Parse();
        if (verifyContainer) m.VerifyContainer();
        return m;
    }

    /// <summary>Expensive: re-compresses the payload to prove the codec is exact.</summary>
    public bool VerifyContainer()
    {
        try
        {
            var rebuilt = SaveContainer.Wrap(GvasPayload);
            ContainerRoundTrips = rebuilt.AsSpan().SequenceEqual(_originalFile);
        }
        catch { ContainerRoundTrips = false; }
        return ContainerRoundTrips == true;
    }

    public void Parse()
    {
        var parser = new SaveParser(GvasPayload);
        Header = parser.ParseHeader();
        Properties = parser.ParseProperties();
        Warnings.Clear();
        Warnings.AddRange(parser.Warnings);
        ParsedBytes = parser.EndPosition;
    }

    public bool IsDirty => Properties.Any(p => p.HasDirtyDescendant);

    /// <summary>Bytes trailing the parsed property list (this game writes 4 zero bytes).</summary>
    public byte[] Trailer => GvasPayload.AsSpan(ParsedBytes).ToArray();

    public SaveProperty? FindProperty(string name) =>
        Properties.FirstOrDefault(p => p.Name == name);

    /// <summary>Apply edits, returning a fresh GVAS payload.</summary>
    public byte[] BuildPayload()
    {
        var res = SavePatcher.Apply(GvasPayload, Properties);
        return res.Data;
    }

    public void SaveAs(string path)
    {
        var payload = BuildPayload();
        var file = SaveContainer.Wrap(payload);
        File.WriteAllBytes(path, file);
    }

    public void DiscardEdits()
    {
        foreach (var p in Properties) p.ClearDirty();
    }

    public string Describe()
    {
        var cls = Header.SaveGameClass.Split('.')[^1];
        return $"{ModuleName,-42} {cls,-34} {ParsedBytes,10}/{TotalBytes,-10} " +
               $"props={Properties.Count,-3} warn={Warnings.Count}";
    }
}
