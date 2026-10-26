using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;
using NPCodex.AI;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly TabControl _statementTabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _statementSearch = new()
    {
        Width = 310, PlaceholderText = "Search approved statements (literal words)..."
    };
    private readonly Button _searchStatements = new()
    {
        Text = "Search conversations", AutoSize = true
    };
    private readonly Button _refreshStatements = new()
    {
        Text = "Refresh history", AutoSize = true
    };
    private readonly ComboBox _statementSearchMode = new()
    {
        Width = 170, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Button _embedStatements = new()
    {
        Text = "Embed 100 statements", AutoSize = true
    };
    private readonly Button _closeSearchTab = new()
    {
        Text = "Close selected search tab", AutoSize = true, Enabled = false
    };
    private readonly TabPage _rosterHistoryTab = new("Conversation members");
    private readonly StatementHistoryGrid _rosterGrid = new(showRelevance: false);
    private sealed record ConversationSearch(string Query, bool Semantic);
    private readonly Dictionary<TabPage, ConversationSearch> _savedSearches = [];

    private Control BuildConversationHistoryArea()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 85));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var searchBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true
        };
        _statementSearchMode.Items.AddRange(["Text / keywords", "Meaning (vectors)"]);
        _statementSearchMode.SelectedIndex = 0;
        searchBar.Controls.Add(_statementSearch);
        searchBar.Controls.Add(_statementSearchMode);
        searchBar.Controls.Add(_searchStatements);
        searchBar.Controls.Add(_refreshStatements);
        searchBar.Controls.Add(_closeSearchTab);
        searchBar.Controls.Add(_embedStatements);
        layout.Controls.Add(searchBar, 0, 0);

        _rosterHistoryTab.Controls.Add(_rosterGrid);
        WireStatementDeletion(_rosterGrid);
        _rosterGrid.EditExchangeRequested += async id =>
            await EditConversationMomentAsync(id);
        _statementTabs.TabPages.Add(_rosterHistoryTab);
        _statementTabs.SelectedIndexChanged += (_, _) =>
            _closeSearchTab.Enabled = _statementTabs.SelectedTab != _rosterHistoryTab;
        var tabMenu = new ContextMenuStrip();
        var closeMenu = new ToolStripMenuItem("Close this search tab");
        closeMenu.Click += (_, _) =>
        {
            if (_statementTabs.SelectedTab is { } selected)
                CloseSearchTab(selected);
        };
        tabMenu.Items.Add(closeMenu);
        tabMenu.Opening += (_, e) =>
        {
            closeMenu.Enabled = _statementTabs.SelectedTab != _rosterHistoryTab;
            if (!closeMenu.Enabled) e.Cancel = true;
        };
        _statementTabs.ContextMenuStrip = tabMenu;
        layout.Controls.Add(_statementTabs, 0, 1);
        return layout;
    }

    private void WireStatementDeletion(StatementHistoryGrid grid)
    {
        grid.DeleteStatementRequested += async id =>
            await DeleteApprovedSpeechAsync(id, wholeExchange: false);
        grid.DeleteExchangeRequested += async id =>
            await DeleteApprovedSpeechAsync(id, wholeExchange: true);
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
        _embedStatements.Click += async (_, _) =>
            await RunBusyAsync(EmbedConversationStatementsAsync);
        _closeSearchTab.Click += (_, _) =>
        {
            if (_statementTabs.SelectedTab is { } current)
                CloseSearchTab(current);
        };
        _statementTabs.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            // Context menu acts on the tab clicked, not the previously selected tab.
            for (var i = 0; i < _statementTabs.TabCount; i++)
                if (_statementTabs.GetTabRect(i).Contains(e.Location))
                {
                    _statementTabs.SelectedIndex = i;
                    return;
                }
        };
        _statementTabs.MouseDoubleClick += (_, e) =>
        {
            for (var i = 1; i < _statementTabs.TabCount; i++)
                if (_statementTabs.GetTabRect(i).Contains(e.Location))
                {
                    CloseSearchTab(_statementTabs.TabPages[i]);
                    return;
                }
        };
        _statementTabs.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Middle) return;
            for (var i = 1; i < _statementTabs.TabCount; i++)
                if (_statementTabs.GetTabRect(i).Contains(e.Location))
                {
                    CloseSearchTab(_statementTabs.TabPages[i]);
                    return;
                }
        };
    }

    private void CloseSearchTab(TabPage tab)
    {
        if (tab == _rosterHistoryTab || !_savedSearches.Remove(tab)) return;
        _statementTabs.TabPages.Remove(tab);
        tab.Dispose();
        _closeSearchTab.Enabled = _statementTabs.SelectedTab != _rosterHistoryTab;
    }

    private async Task LoadRosterHistoryAsync()
    {
        if (CampaignId is not long campaignId)
        {
            _rosterGrid.LoadStatements(Array.Empty<StatementInfo>());
            return;
        }
        var lines = await _database.GetStatementsAsync(campaignId, _rosterIds);
        _rosterGrid.LoadStatements(lines);
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

        var semantic = _statementSearchMode.SelectedIndex == 1;
        var result = await FindConversationStatementsAsync(
            campaignId, query, semantic);
        var title = query.Length > 23 ? query[..23] + "…" : query;
        var tab = new TabPage(
            $"{(semantic ? "≈ " : "")}{title} ({result.Count})");
        var grid = new StatementHistoryGrid(showRelevance: true);
        WireStatementDeletion(grid);
        grid.EditExchangeRequested += async id =>
            await EditConversationMomentAsync(id);
        tab.Controls.Add(grid);
        grid.LoadStatements(result);
        _statementTabs.TabPages.Add(tab);
        _savedSearches.Add(tab, new ConversationSearch(query, semantic));
        _statementTabs.SelectedTab = tab;
        _status.Text =
            $"Found {result.Count} {(semantic ? "semantic" : "text")} " +
            $"statement matches for '{query}'. " +
            "Type in the filter row below any heading to narrow the grid. " +
            "Select Close search tab to remove a result view.";
    }

    private async Task<IReadOnlyList<StatementInfo>> FindConversationStatementsAsync(
        long campaignId, string query, bool semantic)
    {
        if (!semantic)
            return await _database.GetStatementsAsync(campaignId, _rosterIds, query);
        var vector = await _embeddingClient.EmbedQueryAsync(query);
        return await _database.SearchStatementsByMeaningAsync(
            campaignId, OllamaEmbeddingClient.ModelName, vector);
    }

    private async Task EmbedConversationStatementsAsync()
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign first.");
        var service = new ConversationEmbeddingService(_database, _embeddingClient);
        var result = await service.EmbedPendingAsync(campaignId, limit: 100);
        _status.Text =
            $"Approved statement embedding pass: {result.Stored} stored, " +
            $"{result.Skipped} skipped. Run another batch if needed; " +
            "search again to refresh semantic results.";
    }

    private async Task RefreshSavedSearchesAsync()
    {
        if (CampaignId is not long campaignId) return;
        foreach (var entry in _savedSearches.ToArray())
        {
            if (entry.Key.Controls.OfType<StatementHistoryGrid>()
                    .FirstOrDefault() is not { } grid)
                continue;
            var found = await FindConversationStatementsAsync(
                campaignId, entry.Value.Query, entry.Value.Semantic);
            grid.LoadStatements(found);
            var title = entry.Value.Query.Length > 23
                ? entry.Value.Query[..23] + "…" : entry.Value.Query;
            entry.Key.Text =
                $"{(entry.Value.Semantic ? "≈ " : "")}{title} ({found.Count})";
        }
    }
}
