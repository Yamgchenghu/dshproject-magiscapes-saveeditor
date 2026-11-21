using System.Drawing.Drawing2D;
using GenerativeSave.Core.Editing;
using GenerativeSave.Core.Model;

namespace GenerativeSave.Editor.UI;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(245, 246, 249);
    public static readonly Color CardBg = Color.White;
    public static readonly Color Border = Color.FromArgb(226, 229, 236);
    public static readonly Color Text = Color.FromArgb(32, 36, 46);
    public static readonly Color Muted = Color.FromArgb(132, 140, 156);
    public static readonly Color Accent = Color.FromArgb(56, 108, 219);
    public static readonly Color AccentSoft = Color.FromArgb(233, 240, 255);
    public static readonly Color DirtyBg = Color.FromArgb(255, 247, 226);
    public static readonly Color DirtyEdge = Color.FromArgb(230, 174, 62);
    public static readonly Color Ok = Color.FromArgb(32, 148, 92);
    public static readonly Color Warn = Color.FromArgb(208, 118, 20);

    public static readonly Font Base = new("Microsoft YaHei UI", 9F);
    public static readonly Font Bold = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    public static readonly Font Title = new("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
    public static readonly Font H1 = new("Microsoft YaHei UI", 14F, FontStyle.Bold);
    public static readonly Font Small = new("Microsoft YaHei UI", 8.5F);
}

/// <summary>A rounded white panel with a title and an optional action strip.</summary>
internal sealed class Card : Panel
{
    private const int PadX = 16;
    private const int HeaderH = 40;
    private const int RowH = 38;
    private const int PadBottom = 14;

    private readonly Label _title;
    private readonly FlowLayoutPanel _actions;
    private readonly List<Control> _rows = new();

    public Card(string title)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Bg;

        _title = new Label
        {
            Text = title,
            Font = Theme.Title,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(PadX, 13),
            BackColor = Color.Transparent,
        };
        Controls.Add(_title);

        _actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
        };
        Controls.Add(_actions);
    }

    public void AddAction(string text, Action onClick)
    {
        // A fixed width clipped longer Chinese labels ("管理招募 / 驱逐…" came
        // out as "管理招募 / 驱"). Measure the text and keep a floor so short
        // buttons still line up.
        var probe = TextRenderer.MeasureText(text, Theme.Base);
        int width = Math.Max(96, probe.Width + 28);

        var b = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(width, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.AccentSoft,
            ForeColor = Theme.Accent,
            Font = Theme.Base,
            Cursor = Cursors.Hand,
            Margin = new Padding(6, 0, 0, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => onClick();
        _actions.Controls.Add(b);
        LayoutActions();
    }

    /// <summary>
    /// Add a row. Tall rows (a ListView, a multi-line note) keep their own
    /// height; uniform rows snap to RowH so the grid stays even.
    /// </summary>
    public void AddRow(Control row)
    {
        int y = HeaderH;
        foreach (var r in _rows) y += Math.Max(RowH, r.Height);

        int h = Math.Max(RowH, row.Height);
        row.Height = h;
        row.Location = new Point(PadX, y);
        row.Width = Math.Max(120, Width - PadX * 2);
        row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        Controls.Add(row);
        _rows.Add(row);
        Height = y + h + PadBottom;
    }

    public void AddNote(string text)
    {
        // Notes carry explanations, often two or three lines. A fixed height
        // silently clipped anything past the first line, so size to the text.
        int lines = Math.Max(1, text.Count(c => c == '\n') + 1);
        var lbl = new Label
        {
            Text = text,
            AutoSize = false,
            Height = 18 + (lines - 1) * 17,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        AddRow(lbl);
    }

    /// <summary>
    /// A small caps-style divider heading used to group rows inside one card,
    /// e.g. "武器　1 / 1 格" above the weapon row.
    /// </summary>
    public void AddSection(string text)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = false,
            Height = 30,
            ForeColor = Theme.Muted,
            Font = Theme.Bold,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(0, 0, 0, 4),
        };
        AddRow(lbl);
    }

    private void LayoutActions()
    {
        if (_actions.Controls.Count == 0) return;
        _actions.Location = new Point(Width - PadX - _actions.Width, 10);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        foreach (var r in _rows)
            r.Width = Math.Max(120, Width - PadX * 2);
        LayoutActions();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounded(rect, 10);
        using (var fill = new SolidBrush(Theme.CardBg)) g.FillPath(fill, path);
        using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);

        // header divider
        using var line = new Pen(Theme.Border);
        g.DrawLine(line, PadX, HeaderH - 1, Width - PadX, HeaderH - 1);
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>
/// One editable numeric field: label, spinner, original-value hint, revert link.
/// Spinner beats a raw text box for humans: no typing needed, no parse errors.
/// </summary>
internal sealed class NumberField : Panel
{
    private readonly SaveProperty _prop;
    private readonly NumericUpDown _num;
    private readonly Label _hint;
    private readonly Button _revert;
    private readonly decimal _original;
    private bool _loading;

    public event Action? Changed;

    public NumberField(SaveProperty prop, string label, string? unit = null)
    {
        _prop = prop;
        Height = 34;
        BackColor = Color.Transparent;

        var lbl = new Label
        {
            Text = label,
            Width = 170,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };

        bool isFloat = prop.TypeName is "FloatProperty" or "DoubleProperty";
        _num = new NumericUpDown
        {
            Width = 170,
            Dock = DockStyle.Left,
            Font = Theme.Base,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Left,
            DecimalPlaces = isFloat ? 2 : 0,
            Increment = isFloat ? 1m : 1m,
            Minimum = -1_000_000_000m,
            Maximum = 1_000_000_000m,
            ThousandsSeparator = false,
        };

        // _original is the on-disk value, used by 撤销 and by the "原值" hint.
        // The spinner starts at the *effective* value so a rebuilt page shows
        // pending edits instead of stale parsed numbers.
        _original = ToDecimal(prop.Value);
        _loading = true;
        _num.Value = Clamp(ToDecimal(ValueEditor.EffectiveValue(prop)));
        _loading = false;

        _hint = new Label
        {
            Width = 190,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Padding = new Padding(10, 0, 0, 0),
        };

        _revert = new Button
        {
            Text = "撤销",
            Width = 52,
            Dock = DockStyle.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Theme.Accent,
            Font = Theme.Small,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _revert.FlatAppearance.BorderSize = 0;
        _revert.Click += (_, _) => Revert();

        Controls.Add(_revert);
        Controls.Add(_hint);
        Controls.Add(_num);
        Controls.Add(lbl);

        _num.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            Commit();
        };
        // typing then pressing Enter should not fight the spinner
        _num.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); }
            if (e.KeyCode == Keys.Escape) Revert();
        };

        UpdateHint(unit);
    }

    private decimal Clamp(decimal v) =>
        v < _num.Minimum ? _num.Minimum : v > _num.Maximum ? _num.Maximum : v;

    private static decimal ToDecimal(object? v) => v switch
    {
        float f => (decimal)f,
        double d => (decimal)d,
        int i => i,
        long l => l,
        uint u => u,
        ulong ul => ul,
        short s => s,
        ushort us => us,
        byte b => b,
        sbyte sb => sb,
        _ => 0m,
    };

    private void Commit()
    {
        var text = _num.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bytes = ValueEditor.Encode(_prop, text, out var err);
        if (bytes is null)
        {
            _hint.Text = err;
            _hint.ForeColor = Theme.Warn;
            return;
        }
        _prop.MarkDirty(bytes);
        UpdateHint(null);
        Changed?.Invoke();
    }

    private void Revert()
    {
        _prop.ClearDirty();
        _loading = true;
        _num.Value = Clamp(_original);
        _loading = false;
        UpdateHint(null);
        Changed?.Invoke();
    }

    private void UpdateHint(string? unit)
    {
        bool dirty = _prop.IsDirty;
        _num.BackColor = dirty ? Theme.DirtyBg : Color.White;
        _revert.Visible = dirty;

        if (dirty)
        {
            _hint.Text = $"已改 · 原值 {_original}";
            _hint.ForeColor = Theme.DirtyEdge;
        }
        else
        {
            _hint.Text = unit ?? string.Empty;
            _hint.ForeColor = Theme.Muted;
        }
    }
}

/// <summary>
/// An editable text field for names. Strings change byte length, so this takes
/// the resizing patch path rather than the fixed-width one.
/// </summary>
internal sealed class NameField : Panel
{
    private readonly SaveProperty _prop;
    private readonly SaveProperty[] _mirrors;
    private readonly TextBox _box;
    private readonly Label _hint;
    private readonly Button _revert;
    private readonly string _original;
    private bool _loading;

    public event Action? Changed;

    /// <param name="mirrors">
    /// Other properties that must carry the same text. The player name lives in
    /// both the player module and Manifest; editing one without the other
    /// leaves the save inconsistent, so both are written together.
    /// </param>
    public NameField(SaveProperty prop, string label, int maxLength = 48,
        params SaveProperty?[] mirrors)
    {
        _prop = prop;
        _mirrors = mirrors.Where(m => m is not null).Cast<SaveProperty>().ToArray();
        Height = 34;
        BackColor = Color.Transparent;

        var lbl = new Label
        {
            Text = label,
            Width = 170,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };

        _original = prop.Value as string ?? string.Empty;

        _box = new TextBox
        {
            Width = 240,
            Dock = DockStyle.Left,
            Font = Theme.Base,
            BorderStyle = BorderStyle.FixedSingle,
            MaxLength = maxLength,
        };
        _loading = true;
        // Prefer a pending edit on either the primary or a mirror, so a page
        // rebuild never shows the stale on-disk text after a rename.
        var pending = new[] { prop }.Concat(_mirrors)
            .FirstOrDefault(x => x.IsDirty) ?? prop;
        _box.Text = ValueEditor.EffectiveValue(pending) as string ?? _original;
        _loading = false;

        _hint = new Label
        {
            Width = 210,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Padding = new Padding(10, 0, 0, 0),
        };

        _revert = new Button
        {
            Text = "撤销",
            Width = 52,
            Dock = DockStyle.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Theme.Accent,
            Font = Theme.Small,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _revert.FlatAppearance.BorderSize = 0;
        _revert.Click += (_, _) => Revert();

        Controls.Add(_revert);
        Controls.Add(_hint);
        Controls.Add(_box);
        Controls.Add(lbl);

        _box.TextChanged += (_, _) => { if (!_loading) Commit(); };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); }
            if (e.KeyCode == Keys.Escape) Revert();
        };

        UpdateHint();
    }

    private void Commit()
    {
        var text = _box.Text;
        if (text.Length == 0)
        {
            _hint.Text = "名字不能为空";
            _hint.ForeColor = Theme.Warn;
            return;
        }

        var bytes = ValueEditor.EncodeFString(text, out var err);
        if (bytes is null)
        {
            _hint.Text = err;
            _hint.ForeColor = Theme.Warn;
            return;
        }

        _prop.MarkDirty(bytes);
        foreach (var m in _mirrors) m.MarkDirty(bytes);
        UpdateHint();
        Changed?.Invoke();
    }

    private void Revert()
    {
        _prop.ClearDirty();
        foreach (var m in _mirrors) m.ClearDirty();
        _loading = true;
        _box.Text = _original;
        _loading = false;
        UpdateHint();
        Changed?.Invoke();
    }

    private void UpdateHint()
    {
        bool dirty = _prop.IsDirty || _mirrors.Any(m => m.IsDirty);
        _box.BackColor = dirty ? Theme.DirtyBg : Color.White;
        _revert.Visible = dirty;

        if (dirty)
        {
            _hint.Text = $"已改 · 原值 {_original}";
            _hint.ForeColor = Theme.DirtyEdge;
        }
        else
        {
            _hint.Text = $"{_original.Length} 个字符";
            _hint.ForeColor = Theme.Muted;
        }
    }
}

/// <summary>
/// A 0–100 % spinner backed by a 0..1 float property. NPC stats are stored as
/// fractions, which a plain NumberField would show as a useless "0" or "1".
/// </summary>
internal sealed class PercentField : Panel
{
    private readonly SaveProperty _prop;
    private readonly NumericUpDown _num;
    private readonly Label _hint;
    private readonly Button _revert;
    private readonly decimal _original;     // original percent
    private bool _loading;

    public event Action? Changed;

    public PercentField(SaveProperty prop, string label)
    {
        _prop = prop;
        Height = 34;
        BackColor = Color.Transparent;

        var lbl = new Label
        {
            Text = label,
            Width = 150,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };

        _num = new NumericUpDown
        {
            Width = 110,
            Dock = DockStyle.Left,
            Font = Theme.Base,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Left,
            DecimalPlaces = 1,
            Increment = 5m,
            Minimum = 0m,
            Maximum = 100m,
            ThousandsSeparator = false,
        };

        _original = ToPercent(prop.Value);

        _loading = true;
        _num.Value = Clamp(ToPercent(ValueEditor.EffectiveValue(prop)));
        _loading = false;

        var pct = new Label
        {
            Text = "%",
            Width = 24,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
        };

        _hint = new Label
        {
            Width = 180,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Padding = new Padding(8, 0, 0, 0),
        };

        _revert = new Button
        {
            Text = "撤销",
            Width = 52,
            Dock = DockStyle.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Theme.Accent,
            Font = Theme.Small,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _revert.FlatAppearance.BorderSize = 0;
        _revert.Click += (_, _) => Revert();

        Controls.Add(_revert);
        Controls.Add(_hint);
        Controls.Add(pct);
        Controls.Add(_num);
        Controls.Add(lbl);

        _num.ValueChanged += (_, _) => { if (!_loading) Commit(); };
        _num.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); }
            if (e.KeyCode == Keys.Escape) Revert();
        };

        UpdateHint();
    }

    private static decimal ToPercent(object? v)
    {
        float f = v switch
        {
            float x => x,
            double d => (float)d,
            _ => 0f,
        };
        var pct = (decimal)(f * 100f);
        return pct < 0m ? 0m : pct > 100m ? 100m : pct;
    }

    private decimal Clamp(decimal v) =>
        v < _num.Minimum ? _num.Minimum : v > _num.Maximum ? _num.Maximum : v;

    private void Commit()
    {
        float frac = (float)_num.Value / 100f;
        _prop.MarkDirty(BitConverter.GetBytes(frac));
        UpdateHint();
        Changed?.Invoke();
    }

    private void Revert()
    {
        _prop.ClearDirty();
        _loading = true;
        _num.Value = Clamp(_original);
        _loading = false;
        UpdateHint();
        Changed?.Invoke();
    }

    private void UpdateHint()
    {
        bool dirty = _prop.IsDirty;
        _num.BackColor = dirty ? Theme.DirtyBg : Color.White;
        _revert.Visible = dirty;

        if (dirty)
        {
            _hint.Text = $"已改 · 原值 {_original:0.#}%";
            _hint.ForeColor = Theme.DirtyEdge;
        }
        else
        {
            _hint.Text = string.Empty;
            _hint.ForeColor = Theme.Muted;
        }
    }
}

/// <summary>
/// A row for one runtime combat attribute (MaxHealth, CritRate, …).
///
/// The stored value is a raw float: percentages are fractions (0.17 = 17%) and
/// CriticalDamage is a multiplier (1.17 = 117%). The user edits in display
/// units, so the conversion lives in CombatAttribute and this control only
/// forwards the text.
/// </summary>
internal sealed class CombatField : Panel
{
    private readonly CombatAttribute _attr;
    private readonly TextBox _box;
    private readonly Label _hint;
    private readonly Button _revert;
    private readonly string _originalText;
    private bool _loading;

    public event Action? Changed;

    public CombatField(CombatAttribute attr)
    {
        _attr = attr;
        Height = 34;
        BackColor = Color.Transparent;

        var lbl = new Label
        {
            Text = attr.Label,
            Width = 110,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Theme.Base,
        };

        _box = new TextBox
        {
            Width = 120,
            Dock = DockStyle.Left,
            Font = Theme.Base,
            BorderStyle = BorderStyle.FixedSingle,
            Text = attr.Display,
        };
        _originalText = _box.Text;

        var unit = new Label
        {
            Text = attr.IsPercent || attr.IsMultiplier ? "%" : "",
            Width = 22,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
        };

        _hint = new Label
        {
            Width = 190,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Padding = new Padding(8, 0, 0, 0),
        };

        _revert = new Button
        {
            Text = "撤销",
            Width = 52,
            Dock = DockStyle.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Theme.Accent,
            Font = Theme.Small,
            Cursor = Cursors.Hand,
            Visible = false,
        };
        _revert.FlatAppearance.BorderSize = 0;
        _revert.Click += (_, _) => Revert();

        Controls.Add(_revert);
        Controls.Add(_hint);
        Controls.Add(unit);
        Controls.Add(_box);
        Controls.Add(lbl);

        _box.TextChanged += (_, _) => { if (!_loading) Commit(); };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); }
            if (e.KeyCode == Keys.Escape) Revert();
        };

        UpdateHint();
    }

    private void Commit()
    {
        if (!_attr.TryEncode(_box.Text, out var bytes, out var error) || bytes is null)
        {
            _hint.Text = error;
            _hint.ForeColor = Theme.Warn;
            return;
        }

        _attr.ValueProp!.MarkDirty(bytes);
        UpdateHint();
        Changed?.Invoke();
    }

    private void Revert()
    {
        _attr.ValueProp?.ClearDirty();
        _loading = true;
        _box.Text = _originalText;
        _loading = false;
        UpdateHint();
        Changed?.Invoke();
    }

    private void UpdateHint()
    {
        bool dirty = _attr.ValueProp?.IsDirty ?? false;
        _box.BackColor = dirty ? Theme.DirtyBg : Color.White;
        _revert.Visible = dirty;

        if (dirty)
        {
            _hint.Text = $"已改 · 原值 {_originalText}";
            _hint.ForeColor = Theme.DirtyEdge;
        }
        else
        {
            _hint.Text = string.Empty;
            _hint.ForeColor = Theme.Muted;
        }
    }
}

/// <summary>A read-only information row: label + plain text, no fake input box.</summary>
internal sealed class InfoRow : Panel
{
    public InfoRow(string label, string value)
    {
        BackColor = Color.Transparent;
        MinimumSize = new Size(0, 32);

        var lbl = new Label
        {
            Text = label,
            Width = 170,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Base,
            AutoSize = false,
        };

        // Values can be long (a whole NPC roster, a list of ids). Let them wrap
        // and grow the row instead of clipping at 32 px, which used to push the
        // following rows out of alignment.
        var val = new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Theme.Base,
            AutoSize = false,
        };

        Controls.Add(val);
        Controls.Add(lbl);

        // Height follows the taller of the two columns, measured with the width
        // the row will actually get.
        void Fit()
        {
            int w = Math.Max(120, Width - lbl.Width - 8);
            var size = TextRenderer.MeasureText(val.Text, val.Font,
                new Size(w, int.MaxValue), TextFormatFlags.WordBreak);
            Height = Math.Max(32, size.Height + 10);
        }

        Resize += (_, _) => Fit();
        HandleCreated += (_, _) => Fit();
        Fit();
    }
}

/// <summary>Sidebar navigation button with a left accent bar when selected.</summary>
internal sealed class NavButton : Button
{
    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public NavButton(string text, string glyph)
    {
        Text = $"  {glyph}   {text}";
        Height = 44;
        Width = 200;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(14, 0, 0, 0);
        Font = Theme.Base;
        Cursor = Cursors.Hand;
        BackColor = Color.White;
        ForeColor = Theme.Text;
        Margin = new Padding(0, 2, 0, 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_selected ? Theme.AccentSoft : BackColor);

        if (_selected)
        {
            using var bar = new SolidBrush(Theme.Accent);
            g.FillRectangle(bar, 0, 6, 4, Height - 12);
        }

        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(Padding.Left, 0, Width - Padding.Left, Height),
            _selected ? Theme.Accent : Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}
