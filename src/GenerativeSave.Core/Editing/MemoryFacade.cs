using GenerativeSave.Core.Model;

namespace GenerativeSave.Core.Editing;

/// <summary>
/// NPC 记忆模块。存档里的结构是四层嵌套：
/// <code>
/// SavedNPCMemoryStorage        Map&lt;Name, struct&gt;   NPC 名 -> 记忆
///   └ MemoryByDay              Map&lt;Int,  struct&gt;   天 -> 当天的记录
///       └ EventGroups          Array&lt;struct&gt;        通常 1 个元素
///           └ Events           Array&lt;struct&gt;        通常 1 个元素
///               └ Type/Parameters/Priority            ← 真正的条目，平铺重复
/// </code>
/// 最内层那一层 <c>Events</c> 的「元素」其实是把 N 条记录首尾相接塞进同一个
/// 属性表里，所以一条记录 = 连续的三个属性（Type、Parameters、Priority）。
/// 本类按这三个一组的规律切开，对外只暴露一条记录一个对象。
/// </summary>
public static class MemoryFacade
{
    public const string ModuleName = "NPCMemory__NPCMemoryData";

    /// <summary>事件的五种类型，带中文说明。</summary>
    public static readonly (string Value, string Cn, string Desc)[] EventTypes =
    {
        ("Task",         "任务",   "接到/推进了某个任务"),
        ("Kill",         "击杀",   "击杀了一只怪物"),
        ("Death",        "死亡",   "死亡过一次"),
        ("Harvest",      "采集",   "采集到了东西"),
        ("MoodDeclined", "心情下降", "心情变差（常见于住处不满意）"),
    };

    public static string TypeLabel(string raw)
    {
        var name = raw;
        int p = name.LastIndexOf("::", StringComparison.Ordinal);
        if (p >= 0) name = name[(p + 2)..];
        foreach (var t in EventTypes)
            if (t.Value == name) return t.Cn;
        return name.Length == 0 ? "未知" : name;
    }

    /// <summary>把 "EMemoryEventType::Task" 归一成 "Task"。</summary>
    public static string TypeName(string raw)
    {
        int p = raw.LastIndexOf("::", StringComparison.Ordinal);
        return p >= 0 ? raw[(p + 2)..] : raw;
    }

    // ------------------------------------------------------------------ 读取

    /// <summary>整个记忆模块里出现过的 NPC 名字，按存档顺序。</summary>
    public static List<string> NpcIds(SaveSlot slot)
    {
        var root = Root(slot);
        if (root?.MapEntries is null) return new List<string>();
        return root.MapEntries
                   .Select(e => e.KeyDisplay)
                   .Where(s => !string.IsNullOrEmpty(s))
                   .ToList();
    }

    /// <summary>读一个 NPC 的全部记忆；NPC 不存在时返回空对象而不是 null。</summary>
    public static NpcMemory Read(SaveSlot slot, string npcId)
    {
        var mem = new NpcMemory { NpcId = npcId };
        var root = Root(slot);
        var entry = root?.MapEntries?.FirstOrDefault(
            e => string.Equals(e.KeyDisplay, npcId, StringComparison.Ordinal));
        if (entry is null) return mem;

        var byDay = entry.ValueProps.FirstOrDefault(p => p.Name == "MemoryByDay");
        if (byDay?.MapEntries is null) return mem;

        foreach (var dayEntry in byDay.MapEntries)
        {
            if (!int.TryParse(dayEntry.KeyDisplay, out int day)) day = dayEntry.Index;
            var dayView = new MemoryDay { Day = day, MapEntry = dayEntry };

            var groups = dayEntry.ValueProps.FirstOrDefault(p => p.Name == "EventGroups");
            if (groups?.ArrayElements is { } arr)
            {
                foreach (var grp in arr)
                {
                    var eventsProp = grp.Properties.FirstOrDefault(p => p.Name == "Events");
                    if (eventsProp?.ArrayElements is null) continue;
                    foreach (var el in eventsProp.ArrayElements)
                        SplitEvents(el.Properties, dayView.Events);
                }
            }
            mem.Days.Add(dayView);
        }

        mem.Days.Sort((a, b) => a.Day.CompareTo(b.Day));
        return mem;
    }

    /// <summary>
    /// 最内层属性表是 N 条记录首尾相接。按 Type 开头、Priority 收尾切成一条条。
    /// 遇到不成组的多余属性就丢掉，绝不因为一个脏属性把后面的记录一起毁掉。
    /// </summary>
    private static void SplitEvents(List<SaveProperty> props, List<MemoryEvent> into)
    {
        MemoryEvent? cur = null;
        foreach (var p in props)
        {
            switch (p.Name)
            {
                case "Type":
                    if (cur is not null && cur.PriorityProp is not null) into.Add(cur);
                    cur = new MemoryEvent { TypeProp = p, TypeRaw = Text(p) };
                    break;
                case "Parameters":
                    if (cur is null) cur = new MemoryEvent();
                    cur.ParametersProp = p;
                    break;
                case "Priority":
                    if (cur is null) cur = new MemoryEvent();
                    cur.PriorityProp = p;
                    cur.Priority = ToInt(p);
                    into.Add(cur);
                    cur = null;
                    break;
            }
        }
        // 末尾缺 Priority 的半条记录：保留下来，宁可显示也不能悄悄吞掉。
        if (cur is not null && (cur.TypeProp is not null || cur.ParametersProp is not null))
            into.Add(cur);
    }

    // ------------------------------------------------------------------ 编辑

    /// <summary>Priority 是可写的整数（0 = 最高优先级，游戏按它挑要播的事件）。</summary>
    public static bool CanEditPriority(MemoryEvent ev) =>
        ev.PriorityProp is not null && ValueEditor.IsEditable(ev.PriorityProp);

    /// <summary>Type 是可写的枚举名；写回时按 EnumType::Member 补全。</summary>
    public static bool CanEditType(MemoryEvent ev) =>
        ev.TypeProp is not null && ValueEditor.IsEditable(ev.TypeProp);

    public static bool SetPriority(MemoryEvent ev, int value, out string error)
    {
        error = string.Empty;
        var p = ev.PriorityProp;
        if (p is null) { error = "这条记录没有优先级字段"; return false; }

        var bytes = ValueEditor.Encode(p, value.ToString(), out error);
        if (bytes is null) return false;

        p.MarkDirty(bytes);
        ev.Priority = value;
        return true;
    }

    public static bool SetType(MemoryEvent ev, string typeName, out string error)
    {
        error = string.Empty;
        var p = ev.TypeProp;
        if (p is null) { error = "这条记录没有类型字段"; return false; }

        var bytes = ValueEditor.Encode(p, typeName, out error);
        if (bytes is null) return false;

        p.MarkDirty(bytes);
        ev.TypeRaw = ValueEditor.EffectiveValueOrNull(p)?.ToString() ?? typeName;
        return true;
    }

    // ------------------------------------------------------------------ 内部

    private static SaveProperty? Root(SaveSlot slot) =>
        slot.LoadModule(ModuleName)?.Properties.FirstOrDefault();

    private static string Text(SaveProperty p) =>
        ValueEditor.EffectiveValueOrNull(p)?.ToString() ?? string.Empty;

    private static int ToInt(SaveProperty p) =>
        int.TryParse(ValueEditor.EffectiveValueOrNull(p)?.ToString(), out int v) ? v : 0;
}

/// <summary>一个 NPC 的全部记忆。</summary>
public sealed class NpcMemory
{
    public string NpcId { get; set; } = string.Empty;
    public List<MemoryDay> Days { get; } = new();

    public int EventCount => Days.Sum(d => d.Events.Count);
    public bool IsEmpty => Days.Count == 0;
}

/// <summary>某一天的记忆。</summary>
public sealed class MemoryDay
{
    public int Day { get; init; }
    public SaveMapEntry? MapEntry { get; init; }
    public List<MemoryEvent> Events { get; } = new();

    /// <summary>这一天里出现过的类型，去重后按首次出现顺序。</summary>
    public List<string> TypeSummary()
    {
        var seen = new List<string>();
        foreach (var e in Events)
        {
            var cn = MemoryFacade.TypeLabel(e.TypeRaw);
            if (!seen.Contains(cn)) seen.Add(cn);
        }
        return seen;
    }
}

/// <summary>一条记忆条目。</summary>
public sealed class MemoryEvent
{
    public SaveProperty? TypeProp { get; set; }
    public SaveProperty? ParametersProp { get; set; }
    public SaveProperty? PriorityProp { get; set; }

    public string TypeRaw { get; set; } = string.Empty;
    public int Priority { get; set; }

    public string TypeCn => MemoryFacade.TypeLabel(TypeRaw);

    /// <summary>Parameters 是 Map&lt;Name, Text&gt;，按出现顺序给出可读的键值对。</summary>
    public List<(string Key, string Value)> Parameters()
    {
        var list = new List<(string, string)>();
        if (ParametersProp?.MapEntries is null) return list;
        foreach (var e in ParametersProp.MapEntries)
            list.Add((e.KeyDisplay, e.Value?.ToString() ?? string.Empty));
        return list;
    }

    /// <summary>一行摘要，用于列表里显示，例如「击杀　MonsterID=40302　Count=1」。</summary>
    public string Summary()
    {
        var parts = new List<string>();
        foreach (var (k, v) in Parameters())
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            parts.Add($"{k}={v}");
        }
        return parts.Count == 0 ? "（无参数）" : string.Join("　", parts);
    }
}
