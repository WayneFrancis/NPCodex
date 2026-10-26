using System.Data;
using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

/// <summary>
/// A fixed heading and editable filter row immediately above a sortable,
/// read-only statements grid. Both grids share column widths and scroll
/// offsets so the filter boxes stay aligned with their corresponding fields.
/// </summary>
internal sealed class StatementHistoryGrid : UserControl
{
    private readonly DataGridView _filter = new();
    private readonly DataGridView _results = new();
    private readonly DataTable _table = new();
    private readonly Dictionary<int, string> _filters = [];
    private bool _syncing;
    private int _sortColumn = -1;
    private bool _sortDescending;
    private readonly bool _showRelevance;
    public event Action<long>? EditExchangeRequested;
    public event Action<long>? DeleteStatementRequested;
    public event Action<long>? DeleteExchangeRequested;

    public StatementHistoryGrid(bool showRelevance)
    {
        _showRelevance = showRelevance;
        Dock = DockStyle.Fill;

        string[] captions = showRelevance
            ? ["Campaign date/time", "Speaking character",
               "Characters hearing it", "What was said", "Relevance"]
            : ["Campaign date/time", "Speaking character",
               "Characters hearing it", "What was said"];

        foreach (var name in captions)
            _table.Columns.Add(name, name == "Relevance" ? typeof(double) : typeof(string));
        // Hidden numeric sort key keeps "Day 2" before "Day 10" while showing
        // natural, human-readable campaign dates in the visible grid.
        _table.Columns.Add("Chronology sort", typeof(long));
        _table.Columns.Add("Statement ID", typeof(long));

        var group = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        // Heading = 28px; filter = 31px, including cell border.
        group.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        group.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(group);

        ConfigureGrid(_filter, headersVisible: true);
        _filter.Dock = DockStyle.Fill;
        _filter.ScrollBars = ScrollBars.None;
        _filter.ReadOnly = false;
        _filter.MultiSelect = false;
        _filter.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _filter.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _filter.ColumnHeadersHeightSizeMode =
            DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _filter.ColumnHeadersHeight = 28;
        _filter.RowTemplate.Height = 30;
        _filter.DefaultCellStyle.BackColor = SystemColors.Info;
        _filter.DefaultCellStyle.SelectionBackColor = SystemColors.Info;
        _filter.DefaultCellStyle.SelectionForeColor = SystemColors.WindowText;

        ConfigureGrid(_results, headersVisible: false);
        _results.Dock = DockStyle.Fill;
        _results.ReadOnly = true;
        _results.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _results.AutoGenerateColumns = false;
        _results.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _results.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        _results.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _results.DefaultCellStyle.Alignment = DataGridViewContentAlignment.TopLeft;
        _results.DataSource = _table.DefaultView;
        var editMenu = new ContextMenuStrip();
        var viewStatement = new ToolStripMenuItem("View full statement...");
        viewStatement.Click += (_, _) => ViewCurrentStatement();
        var copyCell = new ToolStripMenuItem("Copy selected cell");
        copyCell.Click += (_, _) => CopyCurrentCell();
        var copyStatement = new ToolStripMenuItem("Copy full statement");
        copyStatement.Click += (_, _) => CopyCurrentStatement();
        editMenu.Items.Add(viewStatement);
        editMenu.Items.Add(copyCell);
        editMenu.Items.Add(copyStatement);
        editMenu.Items.Add(new ToolStripSeparator());
        var editMoment = new ToolStripMenuItem("Edit exchange date/time...");
        editMoment.Click += (_, _) => RequestEditCurrentStatement();
        editMenu.Items.Add(editMoment);
        editMenu.Items.Add(new ToolStripSeparator());
        var deleteStatement = new ToolStripMenuItem("Delete this statement...");
        deleteStatement.Click += (_, _) =>
        {
            if (SelectedStatementId() is long id)
                DeleteStatementRequested?.Invoke(id);
        };
        var deleteExchange = new ToolStripMenuItem("Delete entire exchange...");
        deleteExchange.Click += (_, _) =>
        {
            if (SelectedStatementId() is long id)
                DeleteExchangeRequested?.Invoke(id);
        };
        editMenu.Items.Add(deleteStatement);
        editMenu.Items.Add(deleteExchange);
        editMenu.Opening += (_, e) =>
        {
            var enabled = SelectedStatementId() is not null;
            editMoment.Enabled = enabled;
            viewStatement.Enabled = enabled;
            copyCell.Enabled = _results.CurrentCell is not null;
            copyStatement.Enabled = enabled;
            deleteStatement.Enabled = enabled;
            deleteExchange.Enabled = enabled;
            if (!enabled) e.Cancel = true;
        };
        _results.ContextMenuStrip = editMenu;
        _results.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) RequestEditCurrentStatement();
        };
        _results.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                _results.CurrentCell = _results.Rows[e.RowIndex].Cells[
                    Math.Max(0, e.ColumnIndex)];
        };

        double[] weights = showRelevance
            ? [92, 110, 155, 270, 63]
            : [92, 110, 155, 310];
        for (var i = 0; i < captions.Length; i++)
        {
            _results.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = captions[i],
                DataPropertyName = captions[i],
                Name = "Statement" + i,
                FillWeight = (float)weights[i],
                MinimumWidth = i == 3 ? 130 : 65,
                ReadOnly = true
            });
            _filter.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Filter" + i,
                HeaderText = captions[i],
                SortMode = DataGridViewColumnSortMode.NotSortable,
                MinimumWidth = 65
            });
            _filter.Columns[i].ToolTipText =
                "Enter text in the row below this header to filter statements.";
        }
        if (showRelevance)
            _results.Columns[_results.Columns.Count - 1]
                .DefaultCellStyle.Format = "0.000";

        _filter.Rows.Add();
        _filter.Rows[0].Height = 30;
        _filter.ClearSelection();
        _filter.CellEndEdit += (_, _) => ApplyFilters();
        _filter.EditingControlShowing += (_, e) =>
        {
            if (e.Control is TextBox editor)
            {
                editor.TextChanged -= OnEditingFilterChanged;
                editor.TextChanged += OnEditingFilterChanged;
            }
        };
        _filter.CellClick += (_, e) =>
        {
            if (e.RowIndex == 0 && e.ColumnIndex >= 0)
                _filter.BeginEdit(true);
        };
        _filter.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex < 0) return;
            _sortDescending = _sortColumn == e.ColumnIndex && !_sortDescending;
            _sortColumn = e.ColumnIndex;
            var sortName = _sortColumn == 0
                ? "Chronology sort" : captions[_sortColumn];
            _table.DefaultView.Sort = "[" + sortName.Replace("]", "]]") +
                "] " + (_sortDescending ? "DESC" : "ASC");
            _results.ClearSelection();
        };

        _results.ColumnWidthChanged += (_, _) => SyncWidths();
        // User resize of the HEADER/FILTER must also resize result cells.
        // The existing one-way result->filter handler ignored this case.
        _filter.ColumnWidthChanged += (_, e) =>
        {
            if (_syncing || e.Column is null ||
                e.Column.Index >= _results.Columns.Count) return;
            _syncing = true;
            try
            {
                _results.Columns[e.Column.Index].Width = e.Column.Width;
            }
            finally { _syncing = false; }
            SyncWidths();
        };
        _results.Scroll += (_, e) =>
        {
            if (e.ScrollOrientation == ScrollOrientation.HorizontalScroll)
                SyncWidths();
        };
        _results.SizeChanged += (_, _) => SyncWidths();
        group.Controls.Add(_filter, 0, 0);
        group.Controls.Add(_results, 0, 1);
        ShownInitialAlignment();
    }

    public void LoadStatements(IReadOnlyList<StatementInfo> statements)
    {
        _table.BeginLoadData();
        try
        {
            _table.Rows.Clear();
            foreach (var line in statements)
            {
                var date = line.WorldDay is int day
                    ? $"Day {day}" +
                      (line.WorldTime is TimeOnly time ? $" {time:HH:mm}" : "")
                    : "Undated";
                var chronological = line.WorldDay is int d
                    ? (long)d * 1440 +
                      (line.WorldTime is TimeOnly t ? t.Hour * 60 + t.Minute : 0)
                    : long.MinValue;
                if (_showRelevance)
                    _table.Rows.Add(date, line.Speaker, line.Hearers,
                        line.Content, Math.Round(line.Relevance, 3), chronological, line.Id);
                else
                    _table.Rows.Add(date, line.Speaker, line.Hearers,
                        line.Content, chronological, line.Id);
            }
        }
        finally { _table.EndLoadData(); }
        ApplyFilters();
        SyncWidths();
    }

    private long? SelectedStatementId()
    {
        if (_results.CurrentRow?.DataBoundItem is not DataRowView row)
            return null;
        return row.Row["Statement ID"] is long id ? id : null;
    }

    private void RequestEditCurrentStatement()
    {
        if (SelectedStatementId() is long id)
            EditExchangeRequested?.Invoke(id);
    }

    private string? CurrentStatement() =>
        (_results.CurrentRow?.DataBoundItem as DataRowView)?
            .Row["What was said"]?.ToString();

    private void CopyCurrentCell()
    {
        var value = _results.CurrentCell?.Value?.ToString();
        if (value is not null) Clipboard.SetText(value);
    }

    private void CopyCurrentStatement()
    {
        var text = CurrentStatement();
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    private void ViewCurrentStatement()
    {
        var text = CurrentStatement();
        if (text is null) return;
        using var window = new Form
        {
            Text = "Full approved statement",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(500, 280),
            ClientSize = new Size(850, 440)
        };
        var view = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = text
        };
        var copy = new Button { Text = "Copy all", AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(view.Text);
        var close = new Button
        {
            Text = "Close", AutoSize = true,
            DialogResult = DialogResult.OK
        };
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 40,
            FlowDirection = FlowDirection.RightToLeft
        };
        bottom.Controls.Add(close);
        bottom.Controls.Add(copy);
        window.Controls.Add(view);
        window.Controls.Add(bottom);
        window.AcceptButton = close;
        window.ShowDialog(this);
    }

    private void OnEditingFilterChanged(object? sender, EventArgs args)
    {
        if (_filter.CurrentCell is null ||
            _filter.CurrentCell.ColumnIndex < 0 ||
            sender is not TextBox editor)
            return;
        _filters[_filter.CurrentCell.ColumnIndex] = editor.Text.Trim();
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (_filter.CurrentCell is { } currentCell &&
            _filter.IsCurrentCellInEditMode &&
            _filter.EditingControl is TextBox input)
            _filters[currentCell.ColumnIndex] = input.Text.Trim();
        else
            for (var col = 0; col < _filter.Columns.Count; col++)
                _filters[col] = _filter.Rows[0].Cells[col].Value?.ToString()?.Trim() ?? "";

        _table.DefaultView.RowFilter =
            GridTextFilter.Build(_table.Columns, _filters);
    }

    private void ShownInitialAlignment()
    {
        _results.DataBindingComplete += (_, _) => SyncWidths();
        _results.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 3) return;
            e.ToolTipText = _results.Rows[e.RowIndex].Cells[3].Value?.ToString();
        };
        _results.ShowCellToolTips = true;
        _filter.HandleCreated += (_, _) => SyncWidths();
    }

    private void SyncWidths()
    {
        if (_syncing || !_filter.IsHandleCreated ||
            _filter.Columns.Count != _results.Columns.Count)
            return;
        _syncing = true;
        try
        {
            for (var i = 0; i < _results.Columns.Count; i++)
                if (_results.Columns[i].Width != _filter.Columns[i].Width)
                    _filter.Columns[i].Width = _results.Columns[i].Width;
            var offset = _results.HorizontalScrollingOffset;
            if (_filter.HorizontalScrollingOffset != offset)
                _filter.HorizontalScrollingOffset = offset;
        }
        finally { _syncing = false; }
    }

    private static void ConfigureGrid(DataGridView grid, bool headersVisible)
    {
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.ColumnHeadersVisible = headersVisible;
        grid.AutoGenerateColumns = false;
        grid.BackgroundColor = SystemColors.Window;
        grid.BorderStyle = BorderStyle.FixedSingle;
    }
}
