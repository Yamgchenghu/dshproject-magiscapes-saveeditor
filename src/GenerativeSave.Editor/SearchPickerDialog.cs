using GenerativeSave.Editor.UI;

namespace GenerativeSave.Editor;

/// <summary>
/// A searchable picker shared by every "add from library" flow. Rows are plain
/// strings; the caller maps the chosen index back to its own object.
/// </summary>
internal sealed class SearchPickerDialog : Form
{
    private readonly TextBox _search = new();
    private readonly ListView _list = new();
    private readonly Label _status = new();
    private readonly List<int> _rowToIndex = new();
    private readonly List<(string Text, string? Tag)> _source;

    public int ChosenIndex { get; private set; } = -1;

    /// <summary>
    /// When true the search box doubles as an id entry: if what was typed is not
    /// one of the rows, it is accepted as a literal id. Set <see cref="Validate"/>
    /// to reject ids the caller cannot handle.
    /// </summary>
    public bool AllowFreeId { get; init; }

    /// <summary>The literal id the user typed, when <see cref="AllowFreeId"/> is on.</summary>
    public string FreeId { get; private set; } = string.Empty;

    /// <summary>
    /// Called with a typed id; return null to accept it, or a message explaining
    /// why it cannot be used. The message is shown in the dialog.
    /// </summary>
    public Func<string, string?>? Validate { get; init; }

    public SearchPickerDialog(
        string title,
        string hint,
        IEnumerable<(string Text, string? Tag)> source,
        string? emptyNote = null,
        string confirmText = "确定",
        string column1 = "名称",
        string column2 = "ID",
        bool showThird = false)
    {
        _source = source.ToList();

        Text = title;
        Width = 660;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.White;
        Font = Theme.Base;

        var hintLbl = new Label
        {
            Text = hint,
            Location = new Point(20, 14),
            Size = new Size(610, 40),
            ForeColor = Theme.Muted,
            Font = Theme.Small,
        };

        _search.Location = new Point(20, 58);
        _search.Width = 610;
        _search.Font = Theme.Base;
        _search.BorderStyle = BorderStyle.FixedSingle;

        _list.Location = new Point(20, 92);
        _list.Size = new Size(610, 360);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = false;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Font = Theme.Base;
        _list.BackColor = Color.White;
        _list.Columns.Add(column1, showThird ? 330 : 430);
        _list.Columns.Add(column2, 160);

        _status.Location = new Point(20, 462);
        _status.Size = new Size(610, 22);
        _status.ForeColor = Theme.Muted;
        _status.Font = Theme.Small;

        var ok = new Button
        {
            Text = confirmText,
            Location = new Point(420, 490),
            Width = 100,
            Height = 34,
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Bold,
        };
        ok.FlatAppearance.BorderSize = 0;

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(532, 490),
            Width = 98,
            Height = 34,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
        };
        cancel.FlatAppearance.BorderColor = Theme.Border;

        Controls.AddRange(new Control[] { hintLbl, _search, _list, _status, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;

        _search.TextChanged += (_, _) => Refill();
        _list.DoubleClick += (_, _) => Confirm();

        ok.Click += (_, _) => Confirm();

        Refill();
        _search.Focus();

        if (_source.Count == 0 && emptyNote is not null)
            _status.Text = emptyNote;
    }

    private void Confirm()
    {
        // A typed id that matches no row is taken literally when the caller
        // allows it, so an item the library has never seen can still be added.
        if (AllowFreeId)
        {
            var typed = _search.Text.Trim();
            bool isRow = _list.SelectedIndices.Count > 0
                         && string.Equals(CurrentRowId(), typed, StringComparison.OrdinalIgnoreCase);
            if (typed.Length > 0 && !isRow && _rowToIndex.Count == 0)
            {
                var problem = Validate?.Invoke(typed);
                if (problem is not null) { _status.Text = problem; return; }
                FreeId = typed;
                ChosenIndex = -1;
                DialogResult = DialogResult.OK;
                return;
            }
        }

        if (_list.SelectedIndices.Count == 0) { _status.Text = "请先选一项，或输入完整物品 ID"; return; }
        ChosenIndex = _rowToIndex[_list.SelectedIndices[0]];
        DialogResult = DialogResult.OK;
    }

    /// <summary>The id shown in the selected row, or an empty string.</summary>
    private string CurrentRowId()
    {
        if (_list.SelectedIndices.Count == 0) return string.Empty;
        var row = _list.SelectedItems[0];
        return row.SubItems.Count > 1 ? row.SubItems[1].Text : string.Empty;
    }

    /// <summary>Pre-fill the search box, for layout screenshots.</summary>
    internal void UiSetQuery(string q) => _search.Text = q;

    private void Refill()
    {
        var q = _search.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        _rowToIndex.Clear();

        for (int i = 0; i < _source.Count; i++)
        {
            var (text, tag) = _source[i];
            if (q.Length > 0
                && !text.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !(tag?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;

            // the row text is "名称（ID）"; split it for the columns
            var open = text.LastIndexOf('（');
            var name = open > 0 ? text[..open] : text;
            var id = tag ?? (open > 0 ? text[(open + 1)..].TrimEnd('）') : "");

            _list.Items.Add(new ListViewItem(new[] { name, id }));
            _rowToIndex.Add(i);
        }

        if (_list.Items.Count > 0) _list.Items[0].Selected = true;

        if (q.Length == 0)
            _status.Text = AllowFreeId
                ? $"共 {_list.Items.Count} 项 · 也可以直接输入物品 ID 后点确定"
                : $"共 {_list.Items.Count} 项";
        else if (_list.Items.Count > 0)
            _status.Text = $"匹配 {_list.Items.Count} 项";
        else
            _status.Text = AllowFreeId
                ? $"资料库里没有「{q}」· 点确定就按这个 ID 添加"
                : $"没有匹配「{q}」的项";

        _list.EndUpdate();
    }
}
