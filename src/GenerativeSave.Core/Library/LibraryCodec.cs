using System.Text;
using System.Text.Json;

namespace GenerativeSave.Core.Library;

/// <summary>
/// Export/import format for the shared library.
///
/// Pipeline (as specified):
///   catalog JSON → UTF-8 bytes → Base64 text → ASCII→hex text
///   → UTF-8 bytes of that hex text → binary → 1~2位叠数计量法
///
/// This is not real secrecy. It is a compact, copy-pasteable blob that reads as
/// gibberish in a chat window, so one player can hand a library to another
/// without a file transfer.
///
/// ── 1~2位叠数计量法 ────────────────────────────────────────────────────────
/// The bit string is written as one count per run of identical bits. The first
/// run is ones; if it is really zeros, a literal '0' is prepended as a marker.
/// Counts follow each other with no separator at all.
///
///   count 1..9      → 1 digit         count 10..549 → 2 chars (letter + digit)
///   count &gt; 549     → "@9" + "0" + rest
///
/// Letters: A..Z = 10..260, a..z = 270..520, % = 530, @ = 540.
///
/// The '0' in the middle is a truncation marker meaning "this same run goes on",
/// NOT a count of zero. That is the whole difference between these two:
///
///   "@93"  = @9 (549 个 1) then 3 (3 个 0)          → 549个1 + 3个0
///   "@903" = @9 (549) + 0 (还没完) + 3             → 552 个连续
///
/// Worked examples from the specification:
///   111000111111110000        → 3,3,8,4      → 3384
///   00111111111111000000111   → 0,2,12,6,3   → 02A263
///   10001111001               → 1,3,4,2,1    → 13421
///   0000000000                → 0,10         → 0A0
/// </summary>
public static class LibraryCodec
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The largest count a single letter+digit pair can express.</summary>
    public const int MaxSingle = 549;

    // ────────────────────────────────────────────── 叠数计量法：计数

    private static int BaseOf(char c)
    {
        if (c >= 'A' && c <= 'Z') return 10 + (c - 'A') * 10;
        if (c >= 'a' && c <= 'z') return 270 + (c - 'a') * 10;
        if (c == '%') return 530;
        if (c == '@') return 540;
        return -1;
    }

    private static char CharFor(int baseValue)
    {
        if (baseValue >= 10 && baseValue <= 260) return (char)('A' + (baseValue - 10) / 10);
        if (baseValue >= 270 && baseValue <= 520) return (char)('a' + (baseValue - 270) / 10);
        if (baseValue == 530) return '%';
        if (baseValue == 540) return '@';
        throw new ArgumentOutOfRangeException(nameof(baseValue), baseValue, "没有对应的字母");
    }

    /// <summary>Encode one run length: 12 → "A2", 552 → "@903".</summary>
    public static string EncodeCount(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n), n, "游程长度必须大于 0");
        if (n <= 9) return ((char)('0' + n)).ToString();
        if (n <= MaxSingle) return CharFor(n - n % 10).ToString() + (char)('0' + n % 10);
        return "@90" + EncodeCount(n - MaxSingle);
    }

    /// <summary>
    /// Read one count. Counts below 10 are a single digit; 10..549 are always
    /// exactly two characters, so a letter MUST be followed by a digit.
    /// </summary>
    private static int ReadCount(string s, ref int i)
    {
        if (i >= s.Length) throw new FormatException("数据在计数中途结束。");

        char c = s[i];

        if (c >= '1' && c <= '9') { i++; return c - '0'; }

        int baseValue = BaseOf(c);
        if (baseValue < 0)
            throw new FormatException($"第 {i + 1} 个字符 '{c}' 不是合法的计数字符。");

        // A count of 10..549 is always two characters. A bare letter would mean
        // the payload was cut short, so refuse it instead of guessing.
        if (i + 1 >= s.Length || s[i + 1] < '0' || s[i + 1] > '9')
            throw new FormatException($"第 {i + 1} 个字符 '{c}' 后面缺少个位数字。");

        int value = baseValue + (s[i + 1] - '0');
        i += 2;
        return value;
    }

    // ────────────────────────────────────────────── 位串 ⇄ 叠数

    /// <summary>Encode a bit string such as "1110001111" into counts.</summary>
    public static string EncodeBits(string bits)
    {
        if (string.IsNullOrEmpty(bits)) return string.Empty;

        var sb = new StringBuilder(bits.Length / 3 + 8);
        int i = 0;
        bool first = true;

        while (i < bits.Length)
        {
            char bit = bits[i];
            int j = i;
            while (j < bits.Length && bits[j] == bit) j++;

            if (first && bit == '0') sb.Append('0');   // leading-zeros marker
            sb.Append(EncodeCount(j - i));

            first = false;
            i = j;
        }
        return sb.ToString();
    }

    /// <summary>Decode counts back into a bit string.</summary>
    public static string DecodeBits(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length * 4);
        int i = 0;
        bool bit = true;

        if (text[0] == '0') { bit = false; i = 1; }   // leading-zeros marker

        while (i < text.Length)
        {
            int n = ReadCount(text, ref i);

            // A '0' here is the truncation marker: the run did not end, it just
            // could not be written in one pair. Keep adding the following count
            // until the next character is something else.
            while (i < text.Length && text[i] == '0')
            {
                i++;
                n += ReadCount(text, ref i);
            }

            sb.Append(bit ? '1' : '0', n);
            bit = !bit;
        }
        return sb.ToString();
    }

    // ────────────────────────────────────────────── 字节 ⇄ 位串

    /// <summary>Bytes to a bit string, most significant bit first.</summary>
    public static string ToBits(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 8);
        foreach (var b in data)
            for (int k = 7; k >= 0; k--)
                sb.Append(((b >> k) & 1) == 1 ? '1' : '0');
        return sb.ToString();
    }

    /// <summary>Bit string back to bytes. Length must be a multiple of 8.</summary>
    public static byte[] FromBits(string bits)
    {
        if (bits.Length % 8 != 0)
            throw new FormatException($"位串长度 {bits.Length} 不是 8 的倍数，数据不完整。");

        var data = new byte[bits.Length / 8];
        for (int i = 0; i < data.Length; i++)
        {
            int v = 0;
            for (int k = 0; k < 8; k++)
                v = (v << 1) | (bits[i * 8 + k] == '1' ? 1 : 0);
            data[i] = (byte)v;
        }
        return data;
    }

    // ────────────────────────────────────────────── 导出 / 导入

    /// <summary>Serialize a library to the export text.</summary>
    public static string Export(CatalogFile cat)
    {
        var json = JsonSerializer.Serialize(cat, JsonOpts);
        var utf8 = Encoding.UTF8.GetBytes(json);
        var base64 = Convert.ToBase64String(utf8);

        // ASCII → hex: every Base64 character becomes two hex digits.
        var hex = new StringBuilder(base64.Length * 2);
        foreach (var c in base64)
            hex.Append(((int)c).ToString("X2"));

        // UTF-8 → binary: the hex text itself is the payload from here on.
        var bytes = Encoding.UTF8.GetBytes(hex.ToString());
        return EncodeBits(ToBits(bytes));
    }

    /// <summary>
    /// Parse an export text. Accepts both readings of "ASCII→16进制": the hex
    /// digits taken as text (what Export writes) and the hex digits parsed back
    /// into bytes, so a file from a different tool still loads.
    /// </summary>
    public static bool TryImport(string text, out CatalogFile? cat, out string error)
    {
        cat = null;
        error = string.Empty;

        var clean = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (clean.Length == 0) { error = "文件是空的。"; return false; }

        string bits;
        try { bits = DecodeBits(clean); }
        catch (FormatException ex) { error = "无法还原位串：" + ex.Message; return false; }

        byte[] bytes;
        try { bytes = FromBits(bits); }
        catch (FormatException ex) { error = ex.Message; return false; }

        var text2 = Encoding.UTF8.GetString(bytes);

        // Export writes Base64 → hex → bits, so the payload here is the hex text.
        // Decode it back to Base64, then Base64 to JSON. Getting this order wrong
        // is what made the first round-trip test fail: reading the hex as JSON
        // directly always misses by exactly one layer.
        if (text2.Length > 0 && text2.Length % 2 == 0 && text2.All(Uri.IsHexDigit))
        {
            try
            {
                var b64 = Encoding.UTF8.GetString(Convert.FromHexString(text2));
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                if (TryJson(json, out cat)) return true;
            }
            catch (FormatException) { /* not this shape */ }
        }

        // Also accept a blob that skipped the hex step, so an older or simpler
        // exporter still loads.
        if (TryJson(text2, out cat)) return true;
        try
        {
            if (TryJson(Encoding.UTF8.GetString(Convert.FromBase64String(text2)), out cat)) return true;
        }
        catch (FormatException) { /* fall through to the error below */ }

        error = "内容能解开，但里面不是有效的资料库数据（可能不是本编辑器导出的文件）。";
        return false;
    }

    private static bool TryJson(string s, out CatalogFile? cat)
    {
        cat = null;
        try
        {
            cat = JsonSerializer.Deserialize<CatalogFile>(s);
            return cat is not null && (cat.Items.Count > 0 || cat.Npcs.Count > 0);
        }
        catch { return false; }
    }

    /// <summary>
    /// Add everything from <paramref name="incoming"/> that the target does not
    /// already know. Existing rows win, so importing never renames a library the
    /// user has been curating.
    /// </summary>
    public static (int Items, int Npcs) Merge(CatalogFile target, CatalogFile incoming)
    {
        int items = 0, npcs = 0;

        var haveItems = target.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var it in incoming.Items)
        {
            if (string.IsNullOrWhiteSpace(it.Id) || haveItems.Contains(it.Id)) continue;
            if (it.Name == it.Id) continue;              // worthless row
            target.Items.Add(new CatalogItem { Id = it.Id, Name = it.Name, Source = it.Source });
            haveItems.Add(it.Id);
            items++;
        }

        var haveNpcs = target.Npcs.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var n in incoming.Npcs)
        {
            if (string.IsNullOrWhiteSpace(n.Id) || haveNpcs.Contains(n.Id)) continue;
            target.Npcs.Add(new CatalogNpc { Id = n.Id, Cn = n.Cn, Source = n.Source });
            haveNpcs.Add(n.Id);
            npcs++;
        }

        return (items, npcs);
    }
}
