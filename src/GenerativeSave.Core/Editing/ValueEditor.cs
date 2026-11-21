using GenerativeSave.Core.IO;
using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>
/// Builds the replacement byte block for a single property value and hands it
/// to the patcher. Only fixed-size primitives are edited in place; anything
/// variable-length (strings, arrays, maps) is deliberately rejected so the
/// editor can never emit a mis-sized block.
/// </summary>
public static class ValueEditor
{
    /// <summary>Can this property be edited in place without resizing the file?</summary>
    public static bool IsEditable(SaveProperty p) => p.TypeName switch
    {
        "BoolProperty" => false,     // value lives in the tag header
        "StrProperty" or "NameProperty" or "TextProperty" => true,
        "Int8Property" or "ByteProperty" => p.DeclaredSize == 1,
        "Int16Property" or "UInt16Property" => true,
        "IntProperty" or "UInt32Property" or "FloatProperty" => true,
        "Int64Property" or "UInt64Property" or "DoubleProperty" => true,
        "EnumProperty" => true,     // FName text; the patcher resizes the enclosing sizes
        _ => false,
    };

    /// <summary>
    /// An array of raw bytes held inline in Value (the minimap fog bitmap is
    /// one). Layout on disk is just i32 count followed by that many bytes.
    /// </summary>
    public static bool IsByteArray(SaveProperty p) =>
        p.TypeName == "ArrayProperty"
        && p.InnerType is "ByteProperty" or "Int8Property"
        && p.ArrayElements is null or { Count: 0 }
        && p.Value is System.Collections.IList;

    /// <summary>Read the bytes of a byte array.</summary>
    public static byte[] ReadByteArray(SaveProperty p) =>
        p.Value is System.Collections.IList l
            ? l.Cast<object?>().Select(x => (byte)Convert.ToInt32(x)).ToArray()
            : Array.Empty<byte>();

    /// <summary>Encode a byte array: i32 count followed by the raw bytes.</summary>
    public static byte[] EncodeByteArray(IEnumerable<byte> values)
    {
        var list = values as IList<byte> ?? values.ToList();
        var buf = new byte[4 + list.Count];
        BitConverter.GetBytes(list.Count).CopyTo(buf, 0);
        for (int i = 0; i < list.Count; i++) buf[4 + i] = list[i];
        return buf;
    }

    /// <summary>Strings change length, so they take the resizing patch path.</summary>
    public static bool IsVariableLength(SaveProperty p) =>
        p.TypeName is "StrProperty" or "NameProperty" or "TextProperty" or "EnumProperty"
        || IsStringArray(p) || IsByteArray(p);

    /// <summary>
    /// A plain array of FStrings (SuffixIds on an affix package is one). These
    /// carry their elements inline in Value rather than as parsed ArrayElements,
    /// so they are edited as a whole list.
    /// </summary>
    public static bool IsStringArray(SaveProperty p) =>
        p.TypeName == "ArrayProperty"
        && p.InnerType is "NameProperty" or "StrProperty"
        && p.ArrayElements is null or { Count: 0 }
        && p.Value is System.Collections.IList;

    /// <summary>Read the current elements of a string array.</summary>
    public static List<string> ReadStringArray(SaveProperty p) =>
        p.Value is System.Collections.IList l
            ? l.Cast<object?>().Select(x => x?.ToString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();

    /// <summary>
    /// Encode a string array: i32 count followed by that many FStrings. This is
    /// exactly the on-disk shape, so it round-trips through the resizing patcher.
    /// </summary>
    public static byte[] EncodeStringArray(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(list.Count));
        foreach (var v in list)
        {
            var bytes = EncodeFString(v, out _) ?? Array.Empty<byte>();
            ms.Write(bytes);
        }
        return ms.ToArray();
    }

    /// <summary>Editability including booleans, which need a tag-header write.</summary>
    public static bool IsBoolEditable(SaveProperty p) => p.TypeName == "BoolProperty";

    public static string Describe(SaveProperty p) => p.TypeName switch
    {
        "IntProperty" => "整数 (int32)",
        "UInt32Property" => "无符号整数 (uint32)",
        "Int64Property" => "长整数 (int64)",
        "UInt64Property" => "无符号长整数 (uint64)",
        "Int16Property" => "短整数 (int16)",
        "UInt16Property" => "无符号短整数 (uint16)",
        "Int8Property" => "字节 (int8)",
        "ByteProperty" => "字节 (uint8)",
        "FloatProperty" => "浮点数 (float32)",
        "DoubleProperty" => "浮点数 (float64)",
        "BoolProperty" => "布尔值",
        _ => p.TypeName,
    };

    /// <summary>
    /// Encode a Vector / Vector3d / Vector3f from three numbers. The layout is
    /// three consecutive scalars with no header, so the byte length must match the
    /// declared size exactly.
    /// </summary>
    public static byte[]? EncodeVector3(SaveProperty p, double x, double y, double z, out string error)
    {
        error = string.Empty;
        bool single = p.TypeName is "Vector3f" or "Vector3fProperty";
        int unit = single ? 4 : 8;

        if (p.DeclaredSize > 0 && p.DeclaredSize != unit * 3)
        {
            error = $"字段大小 {p.DeclaredSize} 与三元向量不符（期望 {unit * 3}）";
            return null;
        }

        var buf = new byte[unit * 3];
        if (single)
        {
            BitConverter.GetBytes((float)x).CopyTo(buf, 0);
            BitConverter.GetBytes((float)y).CopyTo(buf, 4);
            BitConverter.GetBytes((float)z).CopyTo(buf, 8);
        }
        else
        {
            BitConverter.GetBytes(x).CopyTo(buf, 0);
            BitConverter.GetBytes(y).CopyTo(buf, 8);
            BitConverter.GetBytes(z).CopyTo(buf, 16);
        }
        return buf;
    }

    /// <summary>
    /// Encode a new value for the property. Returns null when the text cannot
    /// be parsed or the property is not safely editable.
    /// </summary>
    public static byte[]? Encode(SaveProperty p, string text, out string error)
    {
        error = string.Empty;
        text = text.Trim();

        switch (p.TypeName)
        {
            case "IntProperty":
                if (!int.TryParse(text, out var i32)) { error = "需要 32 位整数"; return null; }
                return BitConverter.GetBytes(i32);
            case "UInt32Property":
                if (!uint.TryParse(text, out var u32)) { error = "需要 32 位无符号整数"; return null; }
                return BitConverter.GetBytes(u32);
            case "Int64Property":
                if (!long.TryParse(text, out var i64)) { error = "需要 64 位整数"; return null; }
                return BitConverter.GetBytes(i64);
            case "UInt64Property":
                if (!ulong.TryParse(text, out var u64)) { error = "需要 64 位无符号整数"; return null; }
                return BitConverter.GetBytes(u64);
            case "Int16Property":
                if (!short.TryParse(text, out var i16)) { error = "需要 16 位整数"; return null; }
                return BitConverter.GetBytes(i16);
            case "UInt16Property":
                if (!ushort.TryParse(text, out var u16)) { error = "需要 16 位无符号整数"; return null; }
                return BitConverter.GetBytes(u16);
            case "Int8Property":
                if (!sbyte.TryParse(text, out var i8)) { error = "需要 -128..127"; return null; }
                return new[] { unchecked((byte)i8) };
            case "ByteProperty":
                if (p.DeclaredSize != 1) { error = "该字节属性是枚举名，暂不支持编辑"; return null; }
                if (!byte.TryParse(text, out var u8)) { error = "需要 0..255"; return null; }
                return new[] { u8 };
            case "FloatProperty":
                if (!float.TryParse(text, out var f32)) { error = "需要数字"; return null; }
                if (float.IsNaN(f32) || float.IsInfinity(f32)) { error = "不接受 NaN/Infinity"; return null; }
                return BitConverter.GetBytes(f32);
            case "DoubleProperty":
                if (!double.TryParse(text, out var f64)) { error = "需要数字"; return null; }
                if (double.IsNaN(f64) || double.IsInfinity(f64)) { error = "不接受 NaN/Infinity"; return null; }
                return BitConverter.GetBytes(f64);
            case "StrProperty":
            case "NameProperty":
                return EncodeFString(text, out error);
            case "TextProperty":
                return EncodeText(p, text, out error);
            case "EnumProperty":
                return EncodeEnum(p, text, out error);
            default:
                error = $"暂不支持编辑 {p.TypeName}";
                return null;
        }
    }

    /// <summary>
    /// Encode an FText: the leading flags/history/word block is kept verbatim
    /// and only the trailing FString is replaced, so the history type the game
    /// wrote stays intact.
    /// </summary>
    /// <summary>
    /// An EnumProperty value is a single FName: the text plus its NUL, and
    /// (when the name carries an instance number) a trailing i32. Enum values
    /// in this game are written as "EnumType::Member", so the text itself is
    /// what changes and the length can differ from the old value.
    /// </summary>
    private static byte[]? EncodeEnum(SaveProperty p, string text, out string error)
    {
        if (string.IsNullOrWhiteSpace(text)) { error = "枚举值不能为空"; return null; }

        // Keep the declared enum prefix honest: writing "Task" where the game
        // expects "EMemoryEventType::Task" would make the game read an empty
        // enum. Accept both spellings and normalise.
        var enumName = p.EnumName;
        if (!string.IsNullOrEmpty(enumName) && !text.Contains("::"))
            text = enumName + "::" + text;

        var fs = EncodeFString(text, out error);
        if (fs is null) return null;

        // Preserve a trailing FName number if the old value carried one.
        int body = FNameBodyLength(p.OriginalValue);
        if (body > 0 && p.OriginalValue.Length - body == 4)
        {
            var result = new byte[fs.Length + 4];
            fs.CopyTo(result, 0);
            Array.Copy(p.OriginalValue, p.OriginalValue.Length - 4, result, fs.Length, 4);
            return result;
        }
        return fs;
    }

    /// <summary>Length of the FString part (text + NUL) at the head of a FName.</summary>
    private static int FNameBodyLength(byte[] b)
    {
        if (b.Length < 4) return 0;
        int len = BitConverter.ToInt32(b, 0);
        int total = len > 0 ? 4 + len : len < 0 ? 4 + (-len) * 2 : 4;
        return total <= b.Length ? total : 0;
    }

    private static byte[]? EncodeText(SaveProperty p, string text, out string error)
    {
        var fs = EncodeFString(text, out error);
        if (fs is null) return null;

        int prefix = TextPrefixLength(p);
        if (prefix <= 0) return fs;
        if (prefix > p.OriginalValue.Length)
        {
            error = "文本头部异常，已取消";
            return null;
        }

        var result = new byte[prefix + fs.Length];
        Array.Copy(p.OriginalValue, 0, result, 0, prefix);
        fs.CopyTo(result, prefix);
        return result;
    }

    /// <summary>
    /// Locate the FString inside an FText value block. UE writes a few header
    /// layouts, so the offset is recovered by finding the one whose length
    /// prefix lands exactly on the end of the original block.
    /// </summary>
    private static int TextPrefixLength(SaveProperty p)
    {
        var ov = p.OriginalValue;
        if (ov.Length < 4) return 0;

        foreach (int skip in new[] { 9, 10, 5, 6, 4, 0 })
        {
            if (skip + 4 > ov.Length) continue;
            int len = BitConverter.ToInt32(ov, skip);
            int total = len > 0 ? 4 + len
                      : len < 0 ? 4 + (-len) * 2
                      : 4;
            if (total >= 4 && skip + total == ov.Length) return skip;
        }
        return 9;   // the layout this game uses for every CharacterName we have seen
    }

    /// <summary>
    /// Encode a UE FString: int32 length, then either ASCII (positive length)
    /// or UTF-16LE (negative length), both including a trailing NUL.
    /// </summary>
    public static byte[]? EncodeFString(string text, out string error)
    {
        error = string.Empty;

        bool ascii = text.All(c => c < 128);
        if (ascii)
        {
            var bytes = new byte[4 + text.Length + 1];
            BitConverter.GetBytes(text.Length + 1).CopyTo(bytes, 0);
            for (int i = 0; i < text.Length; i++) bytes[4 + i] = (byte)text[i];
            bytes[^1] = 0;
            return bytes;
        }

        // UTF-16: length is negative and counts UTF-16 code units incl. NUL
        int units = text.Length + 1;
        var u16 = new byte[4 + units * 2];
        BitConverter.GetBytes(-units).CopyTo(u16, 0);
        for (int i = 0; i < text.Length; i++)
        {
            var ch = BitConverter.GetBytes(text[i]);
            u16[4 + i * 2] = ch[0];
            u16[4 + i * 2 + 1] = ch[1];
        }
        u16[^1] = 0;
        u16[^2] = 0;
        return u16;
    }

    /// <summary>Format the current value for display in an edit box.</summary>
    public static string Format(SaveProperty p) => p.Value switch
    {
        float f => f.ToString("R"),
        double d => d.ToString("R"),
        _ => p.Value?.ToString() ?? string.Empty,
    };

    /// <summary>
    /// The value the property will actually carry if saved: the pending bytes
    /// when dirty, otherwise the parsed value. <see cref="SaveProperty.Value"/>
    /// keeps the on-disk value forever, so a UI that edits and then rebuilds
    /// must read this instead or it will show stale numbers.
    /// </summary>
    /// <summary>Null-safe helper for optional properties.</summary>
    public static object? EffectiveValueOrNull(SaveProperty? p) => p is null ? null : EffectiveValue(p);

    /// <summary>
    /// Decode a freshly written byte block back into a Value, given the type it
    /// belongs to. Used after a save so the in-memory model matches the file
    /// instead of keeping the pre-edit number.
    /// </summary>
    public static object? DecodeForType(string typeName, byte[] b, object? previous)
    {
        if (b is null || b.Length == 0) return previous;

        // EnumProperty stores a plain FName body (no enum name prefix), exactly
        // like NameProperty. It used to fall through to `default: return
        // previous`, so after SetType the model still reported the OLD type and
        // the memory list looked like the edit had been ignored.
        if (typeName is "StrProperty" or "NameProperty" or "TextProperty" or "EnumProperty")
            return DecodeFString(b);

        switch (typeName)
        {
            case "IntProperty" when b.Length >= 4: return BitConverter.ToInt32(b, 0);
            case "UInt32Property" when b.Length >= 4: return BitConverter.ToUInt32(b, 0);
            case "Int64Property" when b.Length >= 8: return BitConverter.ToInt64(b, 0);
            case "UInt64Property" when b.Length >= 8: return BitConverter.ToUInt64(b, 0);
            case "Int16Property" when b.Length >= 2: return BitConverter.ToInt16(b, 0);
            case "UInt16Property" when b.Length >= 2: return BitConverter.ToUInt16(b, 0);
            case "Int8Property" when b.Length >= 1: return unchecked((sbyte)b[0]);
            case "ByteProperty" when b.Length >= 1: return b[0];
            case "FloatProperty" when b.Length >= 4: return BitConverter.ToSingle(b, 0);
            case "DoubleProperty" when b.Length >= 8: return BitConverter.ToDouble(b, 0);
            case "BoolProperty" when b.Length >= 1: return b[0] != 0;
            case "StructProperty" when b.Length >= 24:
            {
                // Vector / Vector3f: three numbers, double or float depending on
                // the original value's element type.
                bool asDouble = previous is System.Collections.IList pl && pl.Count > 0
                                && pl[0] is double;
                var list = new List<object>(3);
                if (asDouble)
                    for (int i = 0; i < 3; i++) list.Add(BitConverter.ToDouble(b, i * 8));
                else
                    for (int i = 0; i < 3; i++) list.Add(BitConverter.ToSingle(b, i * 4));
                return list;
            }
            default:
                return previous;
        }
    }

    public static object? EffectiveValue(SaveProperty p)
    {
        // BoolProperty keeps its value in the tag header, so MarkBool stages a
        // PendingBool rather than a PendingValue. Checking only PendingValue here
        // meant every boolean edit (placeable Turn90, the "enabled" checkboxes on
        // affixes, and anything else going through MarkBool) still read back the
        // old state after being set — the same "cannot assign a new value"
        // symptom the string and float paths had.
        if (p.PendingBool is { } b) return b;

        if (!p.IsDirty) return p.Value;

        // DecodeForType handles vectors as well as scalars and strings, so one
        // path serves both the "show me the pending edit" and "adopt what I just
        // wrote" cases.
        return DecodeForType(p.TypeName, p.PendingValue!, p.Value);
    }

    /// <summary>Decode an FString byte block back to text.</summary>
    public static string DecodeFString(byte[] b)
    {
        if (b.Length < 4) return string.Empty;
        int len = BitConverter.ToInt32(b, 0);
        if (len == 0) return string.Empty;

        if (len > 0)
        {
            int n = Math.Min(len - 1, b.Length - 4);
            return n <= 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(b, 4, n);
        }

        int units = Math.Min(-len - 1, (b.Length - 4) / 2);
        return units <= 0 ? string.Empty : System.Text.Encoding.Unicode.GetString(b, 4, units * 2);
    }
}
