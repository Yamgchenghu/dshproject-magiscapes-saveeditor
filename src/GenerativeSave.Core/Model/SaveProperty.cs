namespace GenerativeSave.Core.Model;

public enum SaveValueKind
{
    Unknown = 0,
    Bool,
    Int8, Int16, Int32, Int64,
    UInt8, UInt16, UInt32, UInt64,
    Float, Double,
    String, Name, Object, Text, Enum,
    Struct, Array, Set, Map,
}

/// <summary>
/// One parsed property. Crucially this records the exact byte offsets of the
/// tag and its value, so the editor can patch bytes in place instead of
/// re-serialising the whole file (which would risk corrupting data the parser
/// does not fully understand).
/// </summary>
public sealed class SaveProperty
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public SaveValueKind Kind { get; set; }
    public int ArrayIndex { get; set; }

    // type-specific tag extras
    public string? StructName { get; set; }
    public string? InnerType { get; set; }
    public string? InnerStructName { get; set; }
    public string? KeyType { get; set; }
    public string? ValueType { get; set; }
    public string? EnumName { get; set; }
    public bool BoolValue { get; set; }

    /// <summary>Offset of the bool byte inside the tag header.</summary>
    public int BoolOffset { get; set; } = -1;
    public bool HasGuid { get; set; }
    public Guid GuidValue { get; set; }

    /// <summary>Offset of the FName that starts this property's tag.</summary>
    public int TagOffset { get; set; }

    /// <summary>Offset of the int32 size field inside the tag.</summary>
    public int SizeFieldOffset { get; set; }

    /// <summary>Declared value size in bytes (as written in the tag).</summary>
    public int DeclaredSize { get; set; }

    /// <summary>Offset where the value bytes begin.</summary>
    public int ValueOffset { get; set; }

    /// <summary>Original value bytes (used for round-trip verification).</summary>
    public byte[] OriginalValue { get; set; } = Array.Empty<byte>();

    // ---- decoded payload -------------------------------------------------
    public object? Value { get; set; }
    public List<SaveProperty>? Children { get; set; }
    public List<SaveMapEntry>? MapEntries { get; set; }
    public List<SaveArrayElement>? ArrayElements { get; set; }

    public SaveProperty? Parent { get; set; }

    /// <summary>Set when the user changed this node.</summary>
    public byte[]? PendingValue { get; private set; }

    /// <summary>
    /// Pending boolean for a BoolProperty. Its value lives in the tag header
    /// rather than the value block, so it cannot travel through PendingValue.
    /// </summary>
    public bool? PendingBool { get; private set; }

    /// <summary>Record a BoolProperty toggle.</summary>
    public void MarkBool(bool value)
    {
        PendingBool = value == BoolValue ? null : value;
    }

    public bool IsDirty => PendingValue is not null || PendingBool is not null;
    public bool HasDirtyDescendant =>
        IsDirty || (Children?.Any(c => c.HasDirtyDescendant) ?? false)
              || (ArrayElements?.Any(e => e.Properties.Any(p => p.HasDirtyDescendant)) ?? false)
              || (MapEntries?.Any(e => e.ValueProps.Any(p => p.HasDirtyDescendant)
                                    || e.KeyProps.Any(p => p.HasDirtyDescendant)) ?? false);

    /// <summary>
    /// Record a replacement value. Writing back the bytes that are already on
    /// disk is treated as "not an edit" and clears the pending value, so the UI
    /// never claims a change when the number is unchanged.
    /// </summary>
    public void MarkDirty(byte[] newValue)
    {
        PendingValue = newValue.AsSpan().SequenceEqual(OriginalValue) ? null : newValue;
    }

    public void ClearDirty()
    {
        // The pending bytes are what was just written to disk, so they become
        // the new baseline. Previously this only dropped PendingValue, which
        // left Value holding the OLD number: after saving, every read showed
        // stale data and the edit looked like it had been reverted even though
        // the file was correct. That is exactly the "改了不生效，刷新也没用"
        // symptom.
        if (PendingValue is not null)
        {
            OriginalValue = PendingValue;
            Value = DecodeFrom(PendingValue);
        }
        if (PendingBool is not null) Value = PendingBool.Value;
        PendingValue = null;
        PendingBool = null;
        if (Children is not null)
            foreach (var c in Children) c.ClearDirty();
        if (ArrayElements is not null)
            foreach (var e in ArrayElements)
                foreach (var p in e.Properties) p.ClearDirty();
        if (MapEntries is not null)
            foreach (var e in MapEntries)
            {
                foreach (var p in e.ValueProps) p.ClearDirty();
                foreach (var p in e.KeyProps) p.ClearDirty();
            }
    }

    /// <summary>
    /// Decode freshly written bytes back into a Value, mirroring what
    /// ValueEditor.EffectiveValue does for a dirty property.
    /// </summary>
    private object? DecodeFrom(byte[] b)
    {
        if (b is null || b.Length == 0) return Value;
        try
        {
            return Editing.ValueEditor.DecodeForType(TypeName, b, Value);
        }
        catch
        {
            return Value;   // keep the old value rather than showing garbage
        }
    }

    public byte[] CurrentValueBytes => PendingValue ?? OriginalValue;

    public override string ToString() => $"{Name} : {TypeName}";
}

public sealed class SaveArrayElement
{
    public int Index { get; set; }
    public int Offset { get; set; }
    public byte[] GuidBlock { get; set; } = Array.Empty<byte>();
    public List<SaveProperty> Properties { get; set; } = new();
}

public sealed class SaveMapEntry
{
    public int Index { get; set; }
    public int Offset { get; set; }
    public object? Key { get; set; }
    public string KeyDisplay { get; set; } = string.Empty;
    public byte[] KeyRaw { get; set; } = Array.Empty<byte>();
    public List<SaveProperty> KeyProps { get; set; } = new();
    public List<SaveProperty> ValueProps { get; set; } = new();
    public object? Value { get; set; }
    public bool ValueIsStruct { get; set; }
}
