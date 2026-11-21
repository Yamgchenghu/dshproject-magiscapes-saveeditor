using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Patching;

/// <summary>
/// Applies edits to a GVAS payload as in-place byte patches.
///
/// Why not re-serialise the whole tree? The format has several quirks (dual
/// property lists, two FText layouts, per-element guid blocks) that a
/// round-trip writer could silently get wrong on data we only partially
/// understand. Patching keeps every untouched byte identical and only fixes
/// the size fields that actually enclose a change.
///
/// Size fields that must be repaired, walking from the edited value outward:
///   * the property's own int32 size in its tag
///   * every enclosing StructProperty / ArrayProperty / MapProperty / SetProperty
///     size field, and the InnerTag element size for struct arrays
///   * the file's two leading int32 length prefixes (recomputed at save time)
/// </summary>
public static class SavePatcher
{
    public sealed class PatchResult
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int PatchedCount { get; set; }
        public List<string> Notes { get; } = new();
    }

    /// <summary>
    /// Rebuild the payload with all dirty properties applied.
    /// Patches are applied back-to-front so earlier offsets stay valid.
    /// </summary>
    public static PatchResult Apply(byte[] payload, IReadOnlyList<SaveProperty> roots)
    {
        var dirty = new List<SaveProperty>();
        foreach (var r in roots) CollectDirty(r, dirty);

        var result = new PatchResult { Data = payload, PatchedCount = dirty.Count };
        if (dirty.Count == 0) return result;

        // Build the edit list: replace the value bytes of each dirty leaf,
        // then repair every enclosing size field.
        var edits = new List<(int Offset, int OldLength, byte[] Bytes)>();

        // One entry per size field. A leaf's delta is folded into every ancestor,
        // and several leaves often share ancestors, so the same offset would
        // otherwise be written once per leaf.
        var sizeEdits = new Dictionary<int, byte[]>();

        foreach (var p in dirty)
        {
            // A BoolProperty stores its value in the tag header, so it is a
            // single-byte patch with no size change and no ancestor repair.
            if (p.PendingBool is bool flag && p.BoolOffset >= 0)
            {
                edits.Add((p.BoolOffset, 1, new[] { (byte)(flag ? 1 : 0) }));
                result.Notes.Add($"{p.Name}: {p.BoolValue} -> {flag}");
                continue;
            }

            var newValue = p.CurrentValueBytes;
            int delta = newValue.Length - p.OriginalValue.Length;

            edits.Add((p.ValueOffset, p.OriginalValue.Length, newValue));

            // this property's own declared size
            edits.Add((p.SizeFieldOffset, 4, BitConverter.GetBytes(newValue.Length)));

            // every enclosing container must absorb the delta, all the way up.
            // The old code zeroed `shift` after the first parent, so only the
            // innermost container was repaired; that was invisible for
            // fixed-size edits (delta 0) but corrupts the file the moment a
            // string changes length.
            var parent = p.Parent;
            while (parent is not null)
            {
                if (delta != 0)
                {
                    parent.DeclaredSize += delta;
                    // Several dirty leaves can share one ancestor. Recording the
                    // same size field once per leaf used to be harmless only
                    // because the duplicate was silently skipped as overlapping
                    // — but totalLength still counted every copy, so the output
                    // buffer ended up longer than what was written and the tail
                    // was truncated. Keep one edit per offset, latest value wins.
                    sizeEdits[parent.SizeFieldOffset] = BitConverter.GetBytes(parent.DeclaredSize);
                }
                parent = parent.Parent;
            }

            if (delta != 0)
                result.Notes.Add($"{p.Name}: {p.OriginalValue.Length} -> {newValue.Length} bytes (delta {delta:+#;-#;0})");
        }

        // Fold the collected size-field repairs in. A value edit that lands on
        // the same offset as a size field cannot happen (sizes sit in tag
        // headers, values after them), but if it ever did the value wins.
        foreach (var kv in sizeEdits)
        {
            if (edits.Any(e => e.Offset == kv.Key)) continue;
            edits.Add((kv.Key, 4, kv.Value));
        }

        // Apply from the highest offset down so earlier offsets stay valid, and
        // rebuild the buffer in ONE forward pass.
        //
        // The old code did `List<byte>.RemoveRange` + `InsertRange` per edit.
        // Each of those shifts every byte after the offset, so an edit near the
        // start of the file costs a full copy. CampsiteSave holds ~56k editable
        // properties (6241 cells x 9 fields), which turned a save into ~16
        // seconds of memmove on the UI thread — the window simply froze.
        // Now the edits are applied by copying untouched runs across once.
        var ordered = edits.OrderBy(e => e.Offset).ToList();

        int totalLength = payload.Length;
        foreach (var e in ordered)
        {
            if (e.Offset < 0 || e.Offset + e.OldLength > payload.Length)
            {
                result.Notes.Add($"skipped out-of-range patch @{e.Offset}");
                continue;
            }
            totalLength += e.Bytes.Length - e.OldLength;
        }

        var outBuf = new byte[Math.Max(totalLength, 0)];
        int src = 0, dst = 0;

        foreach (var e in ordered)
        {
            if (e.Offset < 0 || e.Offset + e.OldLength > payload.Length) continue;
            if (e.Offset < src) continue;   // overlapping edit already covered

            // copy the untouched run before this edit
            int run = e.Offset - src;
            if (run > 0)
            {
                Buffer.BlockCopy(payload, src, outBuf, dst, run);
                src += run;
                dst += run;
            }

            Buffer.BlockCopy(e.Bytes, 0, outBuf, dst, e.Bytes.Length);
            dst += e.Bytes.Length;
            src += e.OldLength;
        }

        // tail
        if (src < payload.Length)
        {
            Buffer.BlockCopy(payload, src, outBuf, dst, payload.Length - src);
            dst += payload.Length - src;
        }

        result.Data = dst == outBuf.Length ? outBuf : outBuf.AsSpan(0, dst).ToArray();
        return result;
    }

    private static void CollectDirty(SaveProperty p, List<SaveProperty> into)
    {
        if (p.IsDirty)
        {
            into.Add(p);
            return;     // do not descend: a whole-value replacement wins
        }
        if (p.Children is not null)
            foreach (var c in p.Children) CollectDirty(c, into);
        if (p.ArrayElements is not null)
            foreach (var e in p.ArrayElements)
                foreach (var c in e.Properties) CollectDirty(c, into);
        if (p.MapEntries is not null)
            foreach (var e in p.MapEntries)
            {
                foreach (var c in e.KeyProps) CollectDirty(c, into);
                foreach (var c in e.ValueProps) CollectDirty(c, into);
            }
    }
}
