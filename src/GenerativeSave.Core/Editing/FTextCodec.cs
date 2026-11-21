using System.Text;

namespace GenerativeSave.Core.Editing;

/// <summary>
/// Reads and rewrites the FText blobs the save stores for item names and
/// descriptions.
///
/// UE serialises an FText either as a plain string, as a string-table reference
/// (key + table + namespace), or as a history/format pattern with arguments.
/// The game uses the last two for items, which is why a description looked like
/// a bare GUID: the readable Chinese sits AFTER the reference, inside the
/// format-argument block. Everything here is about digging that text back out
/// and being able to write a replacement that the game still accepts.
/// </summary>
public static class FTextCodec
{
    /// <summary>One readable piece found inside an FText blob.</summary>
    public readonly record struct Piece(string Kind, string Text);

    /// <summary>
    /// Every readable string inside the blob, in serialisation order. Kind is
    /// "表名" for a table reference, "键" for a table key, "正文" for the actual
    /// human text, and "格式" for a format pattern.
    /// </summary>
    public static List<Piece> Split(byte[]? blob)
    {
        var list = new List<Piece>();
        if (blob is null || blob.Length < 8) return list;

        int i = 0;
        while (i <= blob.Length - 4)
        {
            int n = BitConverter.ToInt32(blob, i);

            // Negative length => UTF-16, and the count includes the NUL.
            if (n < 0)
            {
                int units = -n;
                if (units > 1 && units < 4000 && i + 4 + units * 2 <= blob.Length)
                {
                    var s = Encoding.Unicode.GetString(blob, i + 4, (units - 1) * 2);
                    if (s.Length > 0) list.Add(new Piece("正文", s));
                    i += 4 + units * 2;
                    continue;
                }
            }
            // Positive length => ANSI. Keys and table names are usually ASCII,
            // format patterns can be ANSI Chinese.
            else if (n > 0 && n < 400 && i + 4 + n <= blob.Length)
            {
                var s = Encoding.UTF8.GetString(blob, i + 4, n - 1);
                if (s.Length > 0 && s.All(ch => ch >= 0x20 && ch != '\uFFFD'))
                {
                    bool ascii = s.All(ch => ch < 128);
                    list.Add(new Piece(ascii ? "键" : "正文", s));
                }
                i += 4 + n;
                continue;
            }

            i++;
        }

        return list;
    }

    /// <summary>
    /// The human-readable description. For a format pattern the arguments come
    /// last, so the longest trailing CJK run wins; for a plain inline string
    /// there is only one piece anyway.
    /// </summary>
    public static string ReadText(byte[]? blob)
    {
        var pieces = Split(blob);
        if (pieces.Count == 0) return string.Empty;

        // Prefer the last piece that actually contains CJK — that is the real
        // description in both the reference and the format-pattern layouts.
        for (int i = pieces.Count - 1; i >= 0; i--)
            if (HasCjk(pieces[i].Text)) return pieces[i].Text;

        // No CJK anywhere: an English or numeric description. Skip table names
        // and keys, which are identifiers rather than prose.
        for (int i = pieces.Count - 1; i >= 0; i--)
        {
            var t = pieces[i].Text;
            if (pieces[i].Kind == "键") continue;
            if (t.Length >= 4) return t;
        }

        return pieces[^1].Text;
    }

    /// <summary>
    /// The display name. Same rule, but a name is short, so prefer the piece
    /// that does not look like an identifier.
    /// </summary>
    public static string ReadName(byte[]? blob)
    {
        var pieces = Split(blob);
        if (pieces.Count == 0) return string.Empty;

        // A format pattern such as "{BasePrefix}{EnchantPrefix}的{BaseName}"
        // names the slots; the arguments after it fill them in. For a double
        // prefix weapon the pieces run
        //   ... 大棒骨 | BasePrefix | DT_EquipmentAffix | Prefix_Sword_Crit_NameFragment | 精准 | ...
        // so the BASE NAME comes first and the prefixes follow. Taking the last
        // CJK run would return "迅捷" (a prefix) instead of "大棒骨".
        //
        // The base name is the first CJK piece after the item's own table key,
        // which is what the game displays before any prefix is added.
        for (int i = 0; i < pieces.Count; i++)
        {
            var t = pieces[i].Text;
            if (t.Contains('{') || t.Contains('}')) continue;
            if (LooksLikeIdentifier(t)) continue;
            if (HasCjk(t)) return t;
        }

        for (int i = 0; i < pieces.Count; i++)
        {
            var t = pieces[i].Text;
            if (t.Contains('{') || t.Contains('}')) continue;
            if (LooksLikeIdentifier(t)) continue;
            if (t.Length >= 2) return t;
        }

        return string.Empty;
    }

    /// <summary>
    /// Builds a replacement value for an FText property.
    ///
    /// The original is inspected first: if it is already a plain inline string
    /// (flags 0x02 / history "None") the same shape is reused. Otherwise a
    /// minimal inline blob is produced, because keeping the old string-table
    /// reference would make the game look up the OLD key and show the old text.
    /// </summary>
    public static byte[] Encode(string text, byte[]? original)
    {
        bool utf16 = text.Any(c => c >= 128);

        // Reuse a plain-string header when the original already is one. The
        // 9-byte prefix is: flags(4) + history(1) + hasString(4).
        if (IsPlainInline(original))
        {
            var fs = FString(text, utf16);
            var head = new byte[9];
            Array.Copy(original!, head, 9);
            var outBytes = new byte[head.Length + fs.Length];
            head.CopyTo(outBytes, 0);
            fs.CopyTo(outBytes, head.Length);
            return outBytes;
        }

        // Minimal inline FText: flags = 2 (has a string), history = 0xFF
        // (None), then the string itself. This is exactly the shape the game
        // writes for AI-generated item descriptions, so it is known-good.
        var body = FString(text, utf16);
        var result = new byte[9 + body.Length];
        result[0] = 0x02;
        result[4] = 0xFF;
        result[5] = 0x01;   // one "has string" flag byte pair, matches the game
        body.CopyTo(result, 9);
        return result;
    }

    /// <summary>
    /// True when the blob is a plain inline string rather than a table
    /// reference or format pattern, i.e. the 9-byte prefix is followed directly
    /// by one FString that ends at the end of the blob.
    /// </summary>
    public static bool IsPlainInline(byte[]? blob)
    {
        if (blob is null || blob.Length < 13) return false;
        if (blob[4] != 0xFF) return false;

        int n = BitConverter.ToInt32(blob, 9);
        if (n < 0) return 13 + (-n) * 2 == blob.Length;
        if (n > 0) return 13 + n == blob.Length;
        return false;
    }

    private static byte[] FString(string text, bool utf16)
    {
        if (!utf16)
        {
            var b = new byte[4 + text.Length + 1];
            BitConverter.GetBytes(text.Length + 1).CopyTo(b, 0);
            for (int i = 0; i < text.Length; i++) b[4 + i] = (byte)text[i];
            b[^1] = 0;
            return b;
        }

        int units = text.Length + 1;
        var u = new byte[4 + units * 2];
        BitConverter.GetBytes(-units).CopyTo(u, 0);
        for (int i = 0; i < text.Length; i++)
        {
            var ch = BitConverter.GetBytes(text[i]);
            u[4 + i * 2] = ch[0];
            u[4 + i * 2 + 1] = ch[1];
        }
        return u;
    }

    public static bool HasCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    /// <summary>
    /// Table names, keys and raw GUIDs are all identifiers: ASCII only, no
    /// spaces, and either a 32-character hex run or a token with underscores.
    /// </summary>
    public static bool LooksLikeIdentifier(string s)
    {
        if (s.Length == 0) return true;
        if (HasCjk(s)) return false;
        if (s.Any(char.IsWhiteSpace)) return false;

        if (s.Length == 32 && s.All(Uri.IsHexDigit)) return true;
        if (s.Contains('_') && s.All(c => char.IsLetterOrDigit(c) || c == '_')) return true;
        if (s.Length >= 2 && s.All(c => char.IsLetterOrDigit(c))) return true;
        return false;
    }
}
