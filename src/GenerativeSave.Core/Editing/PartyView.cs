using System.Text;
using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>One slot in the player's party.</summary>
public sealed class PartyMember
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>True for a built-in named resident, false for a rolled code.</summary>
    public bool IsBuiltIn => Id.Any(char.IsLower);

    /// <summary>"流云（Liuyun）", or just the id when no name is known.</summary>
    public string Display =>
        Name.Length > 0 && Name != Id ? $"{Name}（{Id}）" : Id;
}

/// <summary>
/// The player's party — the NPCs that follow you around, stored in
/// Player__Players as FollowNpcIDs.
///
/// This is NOT the camp roster. Three different lists live in the save and they
/// are easy to confuse:
///
///   FollowNpcIDs              the party, max 3, what this class edits
///   CampsiteRecruitedNPCIds   everyone ever recruited to the camp
///   CampsiteAINPCIds          the camp's resident AI characters
///
/// A party member must already be recruited; adding a stranger produces a slot
/// the game cannot spawn, so the editor only offers recruited NPCs.
/// </summary>
public sealed class PartyView
{
    /// <summary>
    /// The UI limit the game enforces: three party slots.
    /// </summary>
    public const int VanillaSlots = 3;

    /// <summary>
    /// Ceiling on the unlock. FollowNpcIDs is a plain string array whose only
    /// length field is its element count, so the save format imposes no limit at
    /// all; four has been verified in game (the extra member follows, offers
    /// 离开小队, and survives a save/load cycle). Larger counts are the same
    /// bytes, so they should behave the same — the cap here exists to catch
    /// typos, not to describe a format limit.
    /// </summary>
    public const int MaxAllowedSlots = 64;

    /// <summary>The value the UI offers as "4 人" and as the unlock default.</summary>
    public const int UnlockDefault = 4;

    /// <summary>Slot cap actually applied. Defaults to the vanilla three.</summary>
    public static int MaxSlots { get; set; } = VanillaSlots;

    public required SaveProperty Prop { get; init; }

    public List<PartyMember> Members { get; private set; } = new();

    /// <summary>Every NPC the player may legally put in a slot.</summary>
    public List<PartyMember> Available { get; private set; } = new();

    /// <summary>
    /// Names and the legal-recruit set, captured once so a refresh does not have
    /// to re-read the save. Kept out of the public surface because callers only
    /// care about Members/Available.
    /// </summary>
    private Func<string, string> _nameOf = id => id;
    private List<string> _recruitable = new();

    /// <summary>
    /// Recompute Members/Available from the property's current staged value.
    /// Every mutator calls this, otherwise the object keeps describing the party
    /// as it was when From() ran and the next edit works from a stale list.
    /// </summary>
    private void Refresh()
    {
        var ids = ReadIds(Prop);
        Members = ids.Select(id => new PartyMember { Id = id, Name = _nameOf(id) }).ToList();

        var inParty = new HashSet<string>(ids, StringComparer.Ordinal);
        Available = _recruitable
            .Where(id => id.Length > 0 && !inParty.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .Select(id => new PartyMember { Id = id, Name = _nameOf(id) })
            .OrderBy(m => m.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Slot cap for this view; defaults to the user's setting.</summary>
    public int SlotLimit { get; set; } = PartyView.MaxSlots;

    public bool IsFull => Members.Count >= SlotLimit;
    public int FreeSlots => Math.Max(0, SlotLimit - Members.Count);

    // ---- construction -----------------------------------------------------

    public static PartyView? From(SaveSlot slot)
    {
        var prop = FollowNpcProp(slot);
        if (prop is null) return null;

        var ids = ReadIds(prop);

        // Names come from the character table; a rolled-code NPC has no entry
        // there, so fall back to the id rather than inventing a label.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in SaveFacade.ReadCharacters(slot) ?? new())
            if (c.CharacterId.Length > 0 && c.CharacterName.Length > 0)
                names[c.CharacterId] = c.CharacterName;

        // Camp-built names for the built-in residents, which the character table
        // does not always cover.
        var builtIn = SaveFacade.BuiltInNpcs
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Cn, StringComparer.Ordinal);

        string NameOf(string id) =>
            names.TryGetValue(id, out var n) && n.Length > 0 ? n
          : builtIn.TryGetValue(id, out var b) && b.Length > 0 ? b
          : id;

        // Only recruited NPCs can legally stand in a slot. Offering the whole
        // character table would let the user create a follower the game has no
        // data for, which is how a save ends up unable to load.
        // The legal pool is every NPC that actually exists in this save.
        //
        // The camp lists alone are not trustworthy: Liuyun is in the party yet
        // absent from CampsiteRecruitedNPCIds, so a pool built from the camp
        // rejects a member the game itself put there. The NPC map is the
        // authoritative roster, so use it as the base and let the camp lists add
        // anything they know about.
        var recruited = new List<string>();
        foreach (var n in SaveFacade.ReadNpcs(slot) ?? new())
            if (n.Id.Length > 0) recruited.Add(n.Id);

        var camp = SaveFacade.ReadCampsite(slot);
        if (camp is not null)
        {
            recruited.AddRange(camp.RecruitedNpcIds);
            recruited.AddRange(camp.AiNpcIds);

            // A dismissed NPC is gone from the camp; offering them would create a
            // follower with no data behind it.
            var kicked = new HashSet<string>(camp.KickedNpcIds, StringComparer.Ordinal);
            recruited.RemoveAll(id => kicked.Contains(id) && !ids.Contains(id));
        }

        // Whoever is already in the party is legal by definition.
        recruited.AddRange(ids);

        var view = new PartyView
        {
            Prop = prop,
            SlotLimit = MaxSlots,
            _nameOf = NameOf,
            _recruitable = recruited,
        };
        view.Refresh();
        return view;
    }

    public static SaveProperty? FollowNpcProp(SaveSlot slot)
    {
        var m = slot.Find("Player__Players");
        if (m is null) return null;

        // Parse() is NOT idempotent: it rebuilds the whole property tree, so a
        // second call orphans every SaveProperty the caller still holds and any
        // MarkDirty staged on them silently disappears. Re-parsing here made
        // "add a member" look like it worked and then write nothing.
        if (m.Properties.Count == 0)
        {
            try { m.Parse(); } catch { return null; }
        }

        var entry = m.Properties.FirstOrDefault()?.MapEntries?.FirstOrDefault();
        return entry?.ValueProps?.FirstOrDefault(x => x.Name == "FollowNpcIDs");
    }

    /// <summary>Current ids, tolerating either a parsed list or raw bytes.</summary>
    public static List<string> ReadIds(SaveProperty prop)
    {
        // Read the staged bytes first. MarkDirty only records a pending value;
        // Value still holds what was parsed from disk, so reading it after an
        // edit returns the OLD list and makes a successful write look ignored.
        if (prop.PendingValue is { Length: >= 4 } pending)
        {
            var fromPending = DecodeStringArray(pending);
            if (fromPending is not null) return fromPending;
        }

        if (prop.Value is List<object?> list)
            return list.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
        if (prop.Value is List<string> ls)
            return ls.Where(x => x.Length > 0).ToList();
        return new List<string>();
    }

    /// <summary>Decode the raw ArrayProperty body: i32 count, then FStrings.</summary>
    private static List<string>? DecodeStringArray(byte[] buf)
    {
        try
        {
            int count = BitConverter.ToInt32(buf, 0);
            if (count < 0 || count > 64) return null;

            var result = new List<string>(count);
            int i = 4;
            for (int k = 0; k < count; k++)
            {
                if (i + 4 > buf.Length) return null;
                int n = BitConverter.ToInt32(buf, i);
                i += 4;

                if (n < 0)
                {
                    int units = -n;
                    if (units <= 0 || i + units * 2 > buf.Length) return null;
                    result.Add(Encoding.Unicode.GetString(buf, i, (units - 1) * 2));
                    i += units * 2;
                }
                else if (n > 0)
                {
                    if (i + n > buf.Length) return null;
                    result.Add(Encoding.UTF8.GetString(buf, i, n - 1));
                    i += n;
                }
                else result.Add(string.Empty);
            }
            return result;
        }
        catch { return null; }
    }

    // ---- editing ----------------------------------------------------------

    /// <summary>
    /// Write a new member list. Refuses more than three, refuses an id that is
    /// not recruited, and refuses duplicates — each of those produces a party
    /// the game cannot resolve on load.
    /// </summary>
    public bool SetMembers(IEnumerable<string> ids, out string error)
    {
        error = string.Empty;
        var list = ids.Where(x => x.Length > 0).ToList();

        if (list.Count > SlotLimit)
        {
            error = SlotLimit > VanillaSlots
                ? $"已开启破限，最多 {SlotLimit} 人，你填了 {list.Count} 个。"
                : $"小队最多 {SlotLimit} 人，你填了 {list.Count} 个。要超过 {VanillaSlots} 人请在设置里开启破限。";
            return false;
        }
        if (list.Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            error = "小队里有重复的成员。";
            return false;
        }

        var legal = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in Members) legal.Add(m.Id);
        foreach (var m in Available) legal.Add(m.Id);
        var bad = list.Where(id => !legal.Contains(id)).ToList();
        if (bad.Count > 0)
        {
            error = $"这些 NPC 不在可招募名单里，放进去游戏会找不到：{string.Join(", ", bad)}";
            return false;
        }

        if (!SaveFacade.SetStringArray(Prop, list, out error)) return false;
        Refresh();
        return true;
    }

    /// <summary>Append one member. Fails when the party is already full.</summary>
    public bool Add(string id, out string error)
    {
        error = string.Empty;
        if (Members.Count >= SlotLimit)
        {
            error = $"小队已经满了（{SlotLimit} 人）。要加人请先移除一个。";
            return false;
        }
        if (Members.Any(m => m.Id == id))
        {
            error = "这个人已经在小队里了。";
            return false;
        }

        var next = Members.Select(m => m.Id).ToList();
        next.Add(id);
        return SetMembers(next, out error);
    }

    /// <summary>Drop one member, leaving the rest in order.</summary>
    public bool Remove(string id, out string error)
    {
        error = string.Empty;
        if (!Members.Any(m => m.Id == id))
        {
            error = "这个人不在小队里。";
            return false;
        }
        var next = Members.Where(m => m.Id != id).Select(m => m.Id).ToList();
        return SetMembers(next, out error);
    }

    /// <summary>Replace the whole party with one member.</summary>
    public bool SetSingle(string id, out string error) => SetMembers(new[] { id }, out error);

    /// <summary>Empty the party.</summary>
    public bool Clear(out string error) => SetMembers(Array.Empty<string>(), out error);

    /// <summary>
    /// Move a member up or down. Order matters to the game: the first entry is
    /// the one that spawns closest to the player.
    /// </summary>
    public bool Move(string id, int delta, out string error)
    {
        error = string.Empty;
        var ids = Members.Select(m => m.Id).ToList();
        int i = ids.IndexOf(id);
        if (i < 0) { error = "这个人不在小队里。"; return false; }
        int j = i + delta;
        if (j < 0 || j >= ids.Count) { error = "已经到头了。"; return false; }

        (ids[i], ids[j]) = (ids[j], ids[i]);
        return SetMembers(ids, out error);
    }

    // ---- the 4-slot unlock, persisted next to the other editor settings ----

    private static string SettingPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GenerativeSaveEditor", "party.json");

    /// <summary>
    /// Read the saved unlock choice. Missing or unreadable file means vanilla.
    /// </summary>
    public static void LoadSetting()
    {
        try
        {
            if (!File.Exists(SettingPath)) { MaxSlots = VanillaSlots; return; }
            var text = File.ReadAllText(SettingPath);

            // Hand-rolled read: the file holds one integer, so pulling in a JSON
            // serializer for it would be noise. "slots" wins when present; an
            // older "unlock": true file still works.
            var m = System.Text.RegularExpressions.Regex.Match(text, "\"slots\"\\s*:\\s*(\\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            {
                MaxSlots = Math.Clamp(n, VanillaSlots, MaxAllowedSlots);
                return;
            }

            MaxSlots = text.Contains("\"unlock\": true", StringComparison.OrdinalIgnoreCase)
                ? UnlockDefault
                : VanillaSlots;
        }
        catch { MaxSlots = VanillaSlots; }
    }

    public static void SaveSetting()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingPath)!);
            File.WriteAllText(SettingPath,
                "{\r\n  \"slots\": " + MaxSlots + "\r\n}\r\n");
        }
        catch { /* a settings write must never break editing */ }
    }
}
