using GenerativeSave.Core.IO;
using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Parsing;

/// <summary>
/// GVAS parser for the GenerativeGame save format (UE 5.3).
/// Produces a tree annotated with byte offsets so edits can be applied as
/// surgical patches rather than a full re-serialisation.
/// </summary>
public sealed class SaveParser
{
    private static readonly byte[] GvasMagic = "GVAS"u8.ToArray();

    private readonly SaveReader _r;
    public SaveHeader Header { get; } = new();
    public List<string> Warnings { get; } = new();
    public int EndPosition => _r.Position;

    public SaveParser(byte[] gvas)
    {
        _r = new SaveReader(gvas);
    }

    // ------------------------------------------------------------------ header
    public SaveHeader ParseHeader()
    {
        var magic = _r.Take(4).ToArray();
        if (!magic.SequenceEqual(GvasMagic))
            throw new SaveFormatException("not a GVAS payload");

        Header.SaveGameFileVersion = _r.I32();
        Header.PackageUe4Version = _r.I32();
        Header.PackageUe5Version = _r.I32();
        Header.EngineMajor = _r.U16();
        Header.EngineMinor = _r.U16();
        Header.EnginePatch = _r.U16();
        Header.EngineChangelist = _r.U32();
        Header.EngineBranch = _r.FString();
        Header.CustomVersionFormat = _r.I32();
        int n = _r.I32();
        n = Math.Clamp(n, 0, 1000);
        for (int i = 0; i < n; i++)
        {
            var g = _r.Guid();
            int v = _r.I32();
            Header.CustomVersions.Add(new CustomVersion(g, v));
        }
        Header.SaveGameClass = _r.FString();
        return Header;
    }

    // -------------------------------------------------------------- properties
    public List<SaveProperty> ParseProperties(int? limit = null, bool stopAtFirstNone = false)
    {
        var props = new List<SaveProperty>();
        while (true)
        {
            if (limit is int lim && _r.Position >= lim) break;
            if (_r.Remaining < 9) break;

            int save = _r.Position;
            string name;
            try { name = _r.FName(); }
            catch { _r.Seek(save); break; }

            if (name == "None")
            {
                // A second property list may follow; only continue when a tag
                // really starts here. Array elements are single lists, and their
                // payloads can contain tag-shaped binary (PortraitImageData),
                // so those must stop at the first terminator.
                if (stopAtFirstNone) break;
                if (LooksLikeTag(_r.Position, limit)) continue;
                break;
            }
            if (string.IsNullOrEmpty(name) || name.Length > 512)
            {
                _r.Seek(save);
                break;
            }

            try
            {
                props.Add(ParseTag(name, save));
            }
            catch (Exception e)
            {
                _r.Seek(save);
                Warnings.Add($"@{save} '{name}': {e.Message}");
                break;
            }
        }
        return props;
    }

    private bool LooksLikeTag(int pos, int? limit)
    {
        if (limit is int lim && pos >= lim) return false;
        if (pos + 9 > _r.Length) return false;

        int save = _r.Position;
        try
        {
            _r.Seek(pos);
            var n = _r.FString();
            if (string.IsNullOrEmpty(n) || n == "None" || n.Length > 512) return false;
            var t = _r.FString();
            return t.EndsWith("Property", StringComparison.Ordinal);
        }
        catch { return false; }
        finally { _r.Seek(save); }
    }

    private SaveProperty ParseTag(string name, int tagOffset)
    {
        var p = new SaveProperty
        {
            Name = name,
            TagOffset = tagOffset,
        };

        p.TypeName = _r.FName();
        p.SizeFieldOffset = _r.Position;
        int size = _r.I32();
        p.DeclaredSize = size;
        p.ArrayIndex = _r.I32();

        switch (p.TypeName)
        {
            case "StructProperty":
                p.StructName = _r.FName();
                break;
            case "BoolProperty":
                // The value sits in the tag header; remember where so a toggle
                // can be written back without touching the value block.
                p.BoolOffset = _r.Position;
                p.BoolValue = _r.U8() != 0;
                break;
            case "ByteProperty":
            case "EnumProperty":
                p.EnumName = _r.FName();
                break;
            case "ArrayProperty":
            case "SetProperty":
                p.InnerType = _r.FName();
                break;
            case "MapProperty":
                p.KeyType = _r.FName();
                p.ValueType = _r.FName();
                break;
        }

        p.HasGuid = _r.U8() != 0;
        if (p.HasGuid)
            p.GuidValue = _r.Guid();
        else if (p.TypeName == "StructProperty")
            _r.Skip(16);   // struct values always carry a 16-byte guid block

        p.ValueOffset = _r.Position;
        p.Kind = KindOf(p.TypeName);

        if (p.TypeName == "BoolProperty")
        {
            // value already consumed in the tag header; declared size is 0
            p.Value = p.BoolValue;
            p.OriginalValue = Array.Empty<byte>();
            return p;
        }

        int start = _r.Position;
        int end = start + size;

        p.Value = ParseValue(p, size, end);

        // resync forward to the declared size
        if (_r.Position != end && end >= start && end <= _r.Length)
            _r.Seek(end);

        int actual = _r.Position - start;
        if (actual > 0 && actual <= _r.Length - start)
            p.OriginalValue = _r.Buffer.AsSpan(start, actual).ToArray();

        return p;
    }

    private object? ParseValue(SaveProperty p, int size, int end)
    {
        switch (p.TypeName)
        {
            case "BoolProperty": return p.BoolValue;
            case "IntProperty": return _r.I32();
            case "UInt32Property": return _r.U32();
            case "Int64Property": return _r.I64();
            case "UInt64Property": return _r.U64();
            case "Int16Property": return _r.I16();
            case "UInt16Property": return _r.U16();
            case "Int8Property": return _r.I8();
            case "ByteProperty":
                return size == 1 ? _r.U8() : (object)_r.FName();
            case "FloatProperty": return _r.F32();
            case "DoubleProperty": return _r.F64();
            case "StrProperty":
            case "NameProperty":
            case "ObjectProperty":
            case "SoftObjectProperty":
            case "ClassProperty":
            case "WeakObjectProperty":
            case "LazyObjectProperty":
            case "InterfaceProperty":
            case "SoftClassProperty":
                return _r.FString();
            case "TextProperty":
                return ParseText(size);
            case "EnumProperty":
                return _r.FName();
            case "StructProperty":
                return ParseStruct(p, size, end);
            case "ArrayProperty":
                return ParseArray(p, size, end);
            case "SetProperty":
                return ParseSet(p, size, end);
            case "MapProperty":
                return ParseMap(p, size, end);
            default:
                _r.Skip(size);
                return $"<{p.TypeName}:{size}B>";
        }
    }

    // ------------------------------------------------------------------- text
    /// <summary>
    /// FText has two shapes in this game:
    ///   i32 flags, u8 hist, i32 word, FString
    ///   i32 flags, u8 hist, i32 word, u8, FString
    /// With a known size, pick the offset that lands exactly on it. Without one
    /// (map values, array elements) take the first offset yielding a printable
    /// FString and leave the cursor untouched when none works.
    /// </summary>
    private string ParseText(int? size)
    {
        int start = _r.Position;

        if (size is int sz)
        {
            int end = start + sz;
            foreach (int skip in TextSkips)
            {
                if (TryPeekFString(start + skip, out int stop, out var s) && stop == end)
                {
                    _r.Seek(stop);
                    return s;
                }
            }
            foreach (int skip in TextSkips)
            {
                if (TryPeekFString(start + skip, out int stop, out var s))
                {
                    _r.Seek(Math.Min(stop, end));
                    return s;
                }
            }
            _r.Seek(end);
            return string.Empty;
        }

        foreach (int skip in TextSkips)
        {
            if (TryPeekFString(start + skip, out int stop, out var s))
            {
                _r.Seek(stop);
                return s;
            }
        }
        return string.Empty;   // cursor deliberately left at start
    }

    private static readonly int[] TextSkips = { 9, 10, 5, 6, 4, 0 };

    /// <summary>Parse one bare struct array element (native layout or a property list).</summary>
    private object? ParseStructAuto(string? structName, int end)
    {
        string sn = structName ?? string.Empty;
        if (NativeLayouts.TryGetValue(sn, out var layout))
        {
            var vals = new List<double>(layout.Count);
            for (int i = 0; i < layout.Count; i++)
                vals.Add(layout.Kind == 'd' ? _r.F64() : layout.Kind == 'f' ? _r.F32() : _r.I32());
            return vals;
        }
        if (sn == "Guid") return _r.Guid();

        int start = _r.Position;
        var props = ParseProperties(end);
        if (props.Count > 0) return props;
        _r.Seek(start);
        return null;
    }

    private bool TryPeekFString(int at, out int stop, out string value)
    {
        stop = at;
        value = string.Empty;
        int save = _r.Position;
        try
        {
            _r.Seek(at);
            value = _r.FString();
            stop = _r.Position;

            // Reject only control characters. Restricting this to printable
            // ASCII silently dropped every Chinese name/title (e.g. "春水之灵
            // 绯樱"), which made TextProperty values come back empty.
            foreach (char c in value)
                if (char.IsControl(c) && c is not ('\t' or '\n' or '\r')) return false;

            return true;
        }
        catch { return false; }
        finally { _r.Seek(save); }
    }

    // ----------------------------------------------------------------- struct
    private object? ParseStruct(SaveProperty p, int size, int end)
    {
        string sn = p.StructName ?? string.Empty;

        if (sn == "Guid") return _r.Guid();
        if (sn is "DateTime" or "Timespan" or "FrameNumber") return _r.I64();

        if (NativeLayouts.TryGetValue(sn, out var layout))
        {
            var vals = new List<double>(layout.Count);
            for (int i = 0; i < layout.Count; i++)
                vals.Add(layout.Kind == 'd' ? _r.F64() : layout.Kind == 'f' ? _r.F32() : _r.I32());
            return vals;
        }

        if (sn == "Transform")
            return new Dictionary<string, object?>
            {
                ["translation"] = new[] { _r.F64(), _r.F64(), _r.F64() },
                ["rotation"] = new[] { _r.F64(), _r.F64(), _r.F64(), _r.F64() },
                ["scale"] = new[] { _r.F64(), _r.F64(), _r.F64() },
            };

        if (sn == "Color")
            return _r.Raw(4);

        if (sn == "GameplayTagContainer")
        {
            int save = _r.Position;
            try
            {
                int n = _r.I32();
                if (n is >= 0 and <= 10000)
                {
                    var tags = new List<string>(n);
                    for (int i = 0; i < n; i++) tags.Add(_r.FString());
                    _r.Seek(end);
                    return tags;
                }
            }
            catch (Exception e)
            {
                Warnings.Add($"struct {sn}: {e.Message}");
            }
            _r.Seek(end);
            return $"<struct {sn} {size}B>";
        }

        // generic: self-terminating property list
        var props = ParseProperties(end);
        foreach (var c in props) c.Parent = p;
        p.Children = props;
        return props;
    }

    private static readonly Dictionary<string, (char Kind, int Count)> NativeLayouts = new()
    {
        ["Vector"] = ('d', 3), ["Vector3f"] = ('f', 3), ["Vector3d"] = ('d', 3),
        ["Vector2D"] = ('d', 2), ["Vector2f"] = ('f', 2),
        ["Rotator"] = ('d', 3), ["Rotator3f"] = ('f', 3),
        ["Quat"] = ('d', 4), ["Quat4f"] = ('f', 4),
        ["LinearColor"] = ('f', 4),
        ["IntPoint"] = ('i', 2), ["IntVector"] = ('i', 3), ["IntVector4"] = ('i', 4),
        ["Vector2i"] = ('i', 2),
    };

    // ------------------------------------------------------------------ array
    /// <summary>Smallest number of bytes one element of this array can occupy.</summary>
    private static int MinElementSize(SaveProperty p) => (p.InnerType ?? "") switch
    {
        "ByteProperty" => 1,
        "BoolProperty" => 1,
        "IntProperty" => 4,
        "UInt32Property" => 4,
        "FloatProperty" => 4,
        "Int64Property" => 8,
        "DoubleProperty" => 8,
        "NameProperty" => 5,      // i32 len(0) + NUL
        "StrProperty" => 5,
        "EnumProperty" => 5,
        // Structs are property lists; the smallest possible is the None terminator.
        _ => 9,
    };

    private object? ParseArray(SaveProperty p, int size, int end)
    {
        int count = _r.I32();
        // The minimap fog bitmap is 2550x1500 = 3,825,000 bytes, so the old
        // 2,000,000 cap silently truncated it and the write-back then shrank the
        // array. Bound only by what the remaining bytes could possibly hold.
        int maxBySize = Math.Max(0, (end - _r.Position) / Math.Max(1, MinElementSize(p)));
        count = Math.Clamp(count, 0, Math.Min(maxBySize, 64_000_000));
        var inner = p.InnerType ?? string.Empty;

        if (inner == "StructProperty")
        {
            // InnerTag: name, type, elemSize, index, structName, hasGuid
            int save = _r.Position;
            string? elemStruct = p.InnerStructName;
            int elemSize = 0;
            try
            {
                _r.FName();
                var t2 = _r.FName();
                int sz2 = _r.I32();
                _r.I32();
                if (t2 == "StructProperty")
                {
                    var sn = _r.FName();
                    if (!string.IsNullOrEmpty(sn)) elemStruct = sn;
                    _r.U8();
                    elemSize = sz2;
                }
                else _r.Seek(save);
            }
            catch { _r.Seek(save); }

            p.InnerStructName = elemStruct;
            var elements = new List<SaveArrayElement>(Math.Min(count, 4096));

            for (int i = 0; i < count; i++)
            {
                if (_r.Position >= end) break;
                int elemOffset = _r.Position;

                // The writer emits a 16-byte guid block before the FIRST element
                // only; every later element abuts the previous one's "None"
                // terminator. Reading 16 bytes per element desynced the array
                // after element 0 (that is what hid the other characters).
                var guidBlock = Array.Empty<byte>();
                if (i == 0 && _r.Remaining >= 16)
                    guidBlock = _r.Raw(16);

                int elemStart = _r.Position;
                // The inner tag's size covers the whole array, not one element.
                // Each element does declare its own size though, and a byte array
                // inside an element (e.g. PortraitImageData, ~2 MB) can contain
                // sequences that look like property tags. Walk until the "None"
                // terminator, but never past the array bound.
                int elemLimit = end;
                // Some arrays (AI characters) hold large byte payloads that can
                // contain tag-shaped data, so their elements must stop at the
                // first "None". Inventory-style arrays legitimately continue into
                // a second list, so only apply this where it is needed.
                bool singleList = elemStruct is "NpcInfoConfig";
                var props = ParseProperties(elemLimit, stopAtFirstNone: singleList);
                if (props.Count == 0)
                {
                    // fall back to a bare struct element (native layout etc.)
                    _r.Seek(elemStart);
                    var auto = ParseStructAuto(elemStruct, end);
                    if (auto is null)
                    {
                        _r.Seek(elemStart);
                        break;
                    }
                }
                foreach (var c in props) c.Parent = p;
                elements.Add(new SaveArrayElement
                {
                    Index = i,
                    Offset = elemOffset,
                    GuidBlock = guidBlock,
                    Properties = props,
                });
            }
            p.ArrayElements = elements;
            return elements;
        }

        var values = new List<object?>(Math.Min(count, 65536));
        switch (inner)
        {
            case "IntProperty": for (int i = 0; i < count; i++) values.Add(_r.I32()); break;
            case "UInt32Property": for (int i = 0; i < count; i++) values.Add(_r.U32()); break;
            case "Int64Property": for (int i = 0; i < count; i++) values.Add(_r.I64()); break;
            case "UInt64Property": for (int i = 0; i < count; i++) values.Add(_r.U64()); break;
            case "Int16Property": for (int i = 0; i < count; i++) values.Add(_r.I16()); break;
            case "FloatProperty": for (int i = 0; i < count; i++) values.Add(_r.F32()); break;
            case "DoubleProperty": for (int i = 0; i < count; i++) values.Add(_r.F64()); break;
            case "BoolProperty": for (int i = 0; i < count; i++) values.Add(_r.U8() != 0); break;
            case "ByteProperty": for (int i = 0; i < count; i++) values.Add(_r.U8()); break;
            case "StrProperty":
            case "NameProperty":
            case "ObjectProperty":
            case "SoftObjectProperty":
            case "ClassProperty":
                for (int i = 0; i < count; i++) values.Add(_r.FString());
                break;
            case "TextProperty":
                for (int i = 0; i < count; i++) values.Add(ParseText(null));
                break;
            case "EnumProperty":
                for (int i = 0; i < count; i++) values.Add(_r.FName());
                break;
            default:
                Warnings.Add($"array inner {inner} unsupported");
                break;
        }
        return values;
    }

    private object? ParseSet(SaveProperty p, int size, int end)
    {
        // TSet writes NumElementsToRemove first
        int save = _r.Position;
        try
        {
            _r.I32();
            return ParseArray(p, size - 4, end);
        }
        catch
        {
            _r.Seek(save);
            return ParseArray(p, size, end);
        }
    }

    // -------------------------------------------------------------------- map
    private object? ParseMap(SaveProperty p, int size, int end)
    {
        int save = _r.Position;
        try
        {
            _r.I32();                        // NumKeysToRemove
            int count = _r.I32();
            count = Math.Clamp(count, 0, 500_000);
            var entries = new List<SaveMapEntry>(Math.Min(count, 8192));
            var keyType = p.KeyType ?? string.Empty;
            var valType = p.ValueType ?? string.Empty;

            for (int i = 0; i < count; i++)
            {
                if (_r.Position >= end) break;
                int off = _r.Position;
                var entry = new SaveMapEntry { Index = i, Offset = off };

                int keyStart = _r.Position;
                if (keyType == "StructProperty")
                {
                    entry.Key = ParseStructMapKey(end, entry.KeyProps);
                }
                else
                {
                    entry.Key = ParseSimple(keyType);
                }
                entry.KeyRaw = _r.Buffer.AsSpan(keyStart, _r.Position - keyStart).ToArray();
                entry.KeyDisplay = FormatKey(entry.Key);

                if (valType == "StructProperty")
                {
                    entry.ValueIsStruct = true;
                    // A map value is exactly one self-terminating property list.
                    // Continuing past its "None" made entry 2 of
                    // PlaceableItemSaveDatas swallow 986 KB and desync every
                    // following entry, because the next key's bytes happened to
                    // look like a tag start.
                    var props = ParseProperties(end, stopAtFirstNone: true);
                    foreach (var c in props) c.Parent = p;
                    entry.ValueProps = props;
                    entry.Value = props;
                }
                else
                {
                    entry.Value = ParseSimple(valType);
                }
                entries.Add(entry);
            }

            RefineScalarTextValues(entries, end, valType);

            p.MapEntries = entries;
            return entries;
        }
        catch (Exception e)
        {
            Warnings.Add($"map {p.KeyType}->{p.ValueType}: {e.Message}");
            return $"<map {size}B>";
        }
        finally
        {
            _r.Seek(end);
        }
    }

    private object? ParseStructMapKey(int end, List<SaveProperty> keyProps)
    {
        // A struct map key comes in three shapes:
        //   1. a self-terminating property list (AttributeMap, BedRegistry, ...)
        //   2. a bare 16-byte GUID          (CoordToAutoZoneMap)
        //   3. a plain (i32,i32) pair       (DetailPlaceItemMapData, ...)
        //
        // Shape 2 is the one that used to break: the reader treated the first
        // eight bytes of the GUID as an (i32,i32) pair, so the "value" that
        // followed started eight bytes early. Every entry then split in two --
        // CoordToAutoZoneMap's 14 real entries came out as 28 half-entries,
        // alternating between "key only" and "value only".
        int save = _r.Position;
        bool ok = false;
        if (_r.Remaining >= 8)
        {
            int n = _r.PeekU32() > int.MaxValue ? -1 : BitConverter.ToInt32(_r.Buffer, _r.Position);
            if (n is >= 2 and <= 200 && _r.Remaining >= 4 + n)
            {
                var raw = _r.Buffer.AsSpan(_r.Position + 4, n);
                ok = raw[^1] == 0;
                if (ok)
                {
                    for (int i = 0; i < n - 1; i++)
                        if (raw[i] < 32 || raw[i] > 126) { ok = false; break; }
                }
            }
        }
        if (ok)
        {
            // A struct key is exactly one self-terminating property list. Parsing
            // to `end` instead swallowed the whole map into the first key: the
            // AttributeMap of an AI character came out as a single entry with 60
            // fields (ten attributes x six fields) and every following entry was
            // lost. Stopping at the first None is what makes each entry separate.
            var props = ParseProperties(end, stopAtFirstNone: true);
            if (props.Count > 0)
            {
                keyProps.AddRange(props);
                return props;
            }
            _r.Seek(save);
        }

        // Not a property list: either a bare 16-byte GUID or a (i32,i32) pair.
        //
        // The only reliable way to tell them apart is to check which reading
        // leaves the cursor on a valid property tag afterwards, so do exactly
        // that instead of guessing from the values. An earlier version guessed
        // from the numeric range and silently changed how many chests the map
        // modules reported (138 -> 141), which is unacceptable for a reader.
        if (_r.Remaining >= 16 && GuidKeyAligns())
        {
            var g = _r.Raw(16);
            return new Guid(
                BitConverter.ToInt32(g, 0),
                BitConverter.ToInt16(g, 4),
                BitConverter.ToInt16(g, 6),
                g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
        }

        return new[] { _r.I32(), _r.I32() };
    }

    /// <summary>
    /// True when consuming 16 bytes as a GUID leaves the cursor on something that
    /// looks like a real property tag, while consuming only 8 bytes as an
    /// (i32,i32) pair does not. That is a structural test, not a value heuristic:
    /// it cannot reclassify a coordinate pair just because the numbers look big.
    /// </summary>
    private bool GuidKeyAligns()
    {
        int pos = _r.Position;

        bool PairLooksLikeTag()
        {
            int p = pos + 8;
            return LooksLikeTagAt(p);
        }

        bool GuidLooksLikeTag()
        {
            int p = pos + 16;
            return LooksLikeTagAt(p);
        }

        return GuidLooksLikeTag() && !PairLooksLikeTag();
    }

    /// <summary>
    /// Cheap structural check: a property tag starts with an FString name whose
    /// length is small and positive, and the name is printable ASCII ending in a
    /// NUL. Used only to disambiguate map key widths.
    /// </summary>
    private bool LooksLikeTagAt(int p)
    {
        if (p < 0 || p + 9 > _r.Buffer.Length) return false;
        int n = BitConverter.ToInt32(_r.Buffer, p);
        if (n is < 2 or > 80) return false;
        if (p + 4 + n >= _r.Buffer.Length) return false;
        var raw = _r.Buffer.AsSpan(p + 4, n);
        if (raw[^1] != 0) return false;
        for (int i = 0; i < n - 1; i++)
            if (raw[i] < 32 || raw[i] > 126) return false;
        return true;
    }

    /// <summary>
    /// A map's scalar TextProperty values are FText, and FText has half a dozen
    /// on-disk prefix lengths. ParseSimple has no size to work with, so
    /// ParseText(null) tries the prefixes in order and takes the first that
    /// yields a readable string -- which for an EMPTY text (prefix 9, then a
    /// zero-length FString) walks straight past the value and returns the name
    /// of the next property. That is why "Content" used to render as
    /// "Priority".
    ///
    /// Once every entry is parsed we know each value's exact byte range: it runs
    /// from the end of its key to the start of the next entry's key. Re-decode
    /// with that size, requiring the text to end exactly at the boundary. A
    /// value whose layout matches no known prefix reads as empty rather than as
    /// somebody else's property name.
    /// </summary>
    private void RefineScalarTextValues(List<SaveMapEntry> entries, int end, string valType)
    {
        if (valType != "TextProperty") return;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            int valueStart = entry.Offset + entry.KeyRaw.Length;
            int valueEnd = i + 1 < entries.Count ? entries[i + 1].Offset : end;
            int size = valueEnd - valueStart;
            if (size <= 0) continue;

            entry.Value = ReadTextExact(valueStart, size);
        }
    }

    /// <summary>
    /// Decode an FText of a known byte length. Returns an empty string when no
    /// prefix layout consumes the value exactly; never reads past the end.
    /// </summary>
    private string ReadTextExact(int start, int size)
    {
        int save = _r.Position;
        try
        {
            int end = start + size;
            if (start < 0 || end > _r.Buffer.Length) return string.Empty;

            foreach (int skip in TextSkips)
            {
                if (skip + 4 > size) continue;
                if (TryPeekFString(start + skip, out int stop, out var s) && stop == end)
                    return s;
            }
            return string.Empty;
        }
        catch { return string.Empty; }
        finally { _r.Seek(save); }
    }

    private object? ParseSimple(string t)
    {
        switch (t)
        {
            case "IntProperty": return _r.I32();
            case "UInt32Property": return _r.U32();
            case "Int64Property": return _r.I64();
            case "UInt64Property": return _r.U64();
            case "Int16Property": return _r.I16();
            case "FloatProperty": return _r.F32();
            case "DoubleProperty": return _r.F64();
            case "BoolProperty": return _r.U8() != 0;
            case "ByteProperty": return _r.U8();
            case "StrProperty":
            case "NameProperty":
            case "ObjectProperty":
            case "SoftObjectProperty":
            case "ClassProperty": return _r.FString();
            case "TextProperty": return ParseText(null);
            case "EnumProperty": return _r.FName();
            case "StructProperty": return ParseProperties();
            default: throw new SaveFormatException($"unsupported map type {t}");
        }
    }

    private static string FormatKey(object? key) => key switch
    {
        null => "(null)",
        string s => s,
        int[] arr => string.Join(", ", arr),
        List<SaveProperty> props => string.Join(", ",
            props.Select(x => $"{x.Name}={x.Value}")),
        _ => key.ToString() ?? "(null)",
    };

    private static SaveValueKind KindOf(string typeName) => typeName switch
    {
        "BoolProperty" => SaveValueKind.Bool,
        "Int8Property" => SaveValueKind.Int8,
        "Int16Property" => SaveValueKind.Int16,
        "IntProperty" => SaveValueKind.Int32,
        "Int64Property" => SaveValueKind.Int64,
        "UInt16Property" => SaveValueKind.UInt16,
        "UInt32Property" => SaveValueKind.UInt32,
        "UInt64Property" => SaveValueKind.UInt64,
        "FloatProperty" => SaveValueKind.Float,
        "DoubleProperty" => SaveValueKind.Double,
        "StrProperty" => SaveValueKind.String,
        "NameProperty" => SaveValueKind.Name,
        "ObjectProperty" => SaveValueKind.Object,
        "TextProperty" => SaveValueKind.Text,
        "EnumProperty" => SaveValueKind.Enum,
        "StructProperty" => SaveValueKind.Struct,
        "ArrayProperty" => SaveValueKind.Array,
        "SetProperty" => SaveValueKind.Set,
        "MapProperty" => SaveValueKind.Map,
        _ => SaveValueKind.Unknown,
    };
}

public sealed class SaveHeader
{
    public int SaveGameFileVersion { get; set; }
    public int PackageUe4Version { get; set; }
    public int PackageUe5Version { get; set; }
    public ushort EngineMajor { get; set; }
    public ushort EngineMinor { get; set; }
    public ushort EnginePatch { get; set; }
    public uint EngineChangelist { get; set; }
    public string EngineBranch { get; set; } = string.Empty;
    public int CustomVersionFormat { get; set; }
    public List<CustomVersion> CustomVersions { get; } = new();
    public string SaveGameClass { get; set; } = string.Empty;

    public string EngineVersion => $"{EngineMajor}.{EngineMinor}.{EnginePatch}";
}

public readonly record struct CustomVersion(Guid Key, int Version);
