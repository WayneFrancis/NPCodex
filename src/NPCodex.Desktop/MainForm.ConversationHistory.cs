using System.Data;
using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly TabControl _statementTabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _statementSearch = new()
    {
        Width = 310, PlaceholderText = "Search all campaign dialogue: maps, sword, etc."
    };
    private readonly Button _searchStatements = new()
    {
        Text = "Search conversations", AutoSize = true
    };
    private readonly Button _refreshStatements = new()
    {
        Text = "Refresh history", AutoSize = true
    };
    private readonly TabPage _rosterHistoryTab = new("Conversation members");
    private readonly DataGridView _rosterGrid = MakeStatementGrid();
    private readonly Dictionary<TabPage, string> _savedSearches = [];

    private Control BuildConversationHistoryArea()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var searchBar = new FlowLayoutPanel { Dock = DockStyle.Fill };
        searchBar.Controls.Add(_statementSearch);
        searchBar.Controls.Add(_searchStatements);
        searchBar.Controls.Add(_refreshStatements);
        searchBar.Controls.Add(new Label
        {
            Text = "Click headers to sort; right-click a header to filter its column",
            AutoSize = true, Padding = new Padding(9, 8, 0, 0)
        });
        layout.Controls.Add(searchBar, 0, 0);
        _rosterHistoryTab.Controls.Add(_rosterGrid);
        _statementTabs.TabPages.Add(_rosterHistoryTab);
        layout.Controls.Add(_statementTabs, 0, 1);
        return layout;
    }

    private void WireHistoryEvents()
    {
        _searchStatements.Click += async (_, _) => await RunBusyAsync(SearchHistoryAsync);
        _statementSearch.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await RunBusyAsync(SearchHistoryAsync);
            }
        };
        _refreshStatements.Click += async (_, _) => await RunBusyAsync(async () =>
        {
            await LoadRosterHistoryAsync();
            await RefreshSavedSearchesAsync();
        });
        _statementTabs.MouseDoubleClick += (_, e) =>
        {
            // Double-click a result tab to close it; the roster tab is permanent.
            for (var i = 1; i < _statementTabs.TabCount; i++)
            {
                if (!_statementTabs.GetTabRect(i).Contains(e.Location)) continue;
                var tab = _statementTabs.TabPages[i];
                _savedSearches.Remove(tab);
                _statementTabs.TabPages.Remove(tab);
                tab.Dispose();
                return;
            }
        };
    }

    private async Task LoadRosterHistoryAsync()
    {
        if (CampaignId is not long campaignId)
        {
            _rosterGrid.DataSource = null;
            return;
        }
        var lines = await _database.GetStatementsAsync(campaignId, _rosterIds);
        SetGridData(_rosterGrid, lines, false);
        _rosterHistoryTab.Text = $"Conversation members ({lines.Count})";
    }

    private async Task SearchHistoryAsync()
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign first.");
        var query = _statementSearch.Text.Trim();
        if (query.Length == 0)
            throw new InvalidOperationException("Enter a topic or phrase to search.");
        if (query.Length > 250)
            throw new InvalidOperationException("Use a search shorter than 250 characters.");

        var result = await _database.GetStatementsAsync(campaignId, _rosterIds, query);
        var title = query.Length > 23 ? query[..23] + "…" : query;
        var tab = new TabPage($"{title} ({result.Count})");
        var grid = MakeStatementGrid();
        tab.Controls.Add(grid);
        SetGridData(grid, result, true);
        _statementTabs.TabPages.Add(tab);
        _savedSearches.Add(tab, query);
        _statementTabs.SelectedTab = tab;
        _status.Text = $"Found {result.Count} approved statements for '{query}'. " +
                       "Double-click the search tab to close it.";
    }

    private async Task RefreshSavedSearchesAsync()
    {
        if (CampaignId is not long campaignId) return;
        foreach (var entry in _savedSearches.ToArray())
        {
            if (entry.Key.Controls.OfType<DataGridView>().FirstOrDefault() is not { } grid)
                continue;
            var found = await _database.GetStatementsAsync(
                campaignId, _rosterIds, entry.Value);
            SetGridData(grid, found, true);
        }
    }

    private static DataGridView MakeStatementGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false
        };
        grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.ColumnIndex < 0 ||
                grid.DataSource is not DataTable table)
                return;
            var column = table.Columns[e.ColumnIndex].ColumnName;
            using var prompt = new Form
            {
                Text = $"Filter column: {column}",
                Size = new Size(460, 160),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent
            };
            var entry = new TextBox { Left = 12, Top = 15, Width = 420 };
            var ok = new Button
            {
                Text = "Apply filter", Left = 315, Top = 57,
                DialogResult = DialogResult.OK
            };
            prompt.Controls.AddRange([entry, ok]);
            AttachDictationToTextBoxes(prompt);
            prompt.AcceptButton = ok;
            if (prompt.ShowDialog(grid.FindForm()) != DialogResult.OK) return;
            if (string.IsNullOrWhiteSpace(entry.Text))
            {
                table.DefaultView.RowFilter = "";
                return;
            }
            var safe = entry.Text.Replace("'", "''").Replace("[", "[[]")
                .Replace("%", "[%]").Replace("*", "[*]");
            table.DefaultView.RowFilter =
                $"CONVERT([{column}], 'System.String') LIKE '%{safe}%'";
        };
        return grid;
    }

    private static void SetGridData(
        DataGridView grid, IReadOnlyList<StatementInfo> statements, bool relevance)
    {
        var table = new DataTable();
        table.Columns.Add("Campaign date/time", typeof(string));
        table.Columns.Add("Speaking character", typeof(string));
        table.Columns.Add("Characters hearing it", typeof(string));
        table.Columns.Add("What was said", typeof(string));
        if (relevance) table.Columns.Add("Relevance", typeof(double));
        foreach (var line in statements)
        {
            var date = line.WorldDay is int day
                ? $"Day {day}" + (line.WorldTime is TimeOnly time
                    ? $" {time:HH:mm}" : "")
                : "Undated";
            if (relevance)
                table.Rows.Add(date, line.Speaker, line.Hearers,
                               line.Content, Math.Round(line.Relevance, 3));
            else
                table.Rows.Add(date, line.Speaker, line.Hearers, line.Content);
        }
        grid.DataSource = table;
        if (grid.Columns.Count > 3)
            grid.Columns[3].FillWeight = 220;
        if (relevance && grid.Columns.Count > 4)
        {
            grid.Columns[4].FillWeight = 55;
            grid.Columns[4].DefaultCellStyle.Format = "0.000";
        }
    }
}
