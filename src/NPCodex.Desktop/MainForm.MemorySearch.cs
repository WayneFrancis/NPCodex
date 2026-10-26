using System.Data;
using System.Drawing;
using System.Windows.Forms;
using NPCodex.AI;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly TabPage _memorySearchTab = new("Memory Search");
    private readonly TextBox _memorySearchOwnerFilter = new()
    {
        Dock = DockStyle.Fill,
        PlaceholderText = "Filter character names..."
    };
    private readonly CheckedListBox _memorySearchOwners = new()
    {
        Dock = DockStyle.Fill, CheckOnClick = true,
        IntegralHeight = false
    };
    private readonly HashSet<long> _memorySearchSelectedOwnerIds = [];
    private bool _updatingMemorySearchOwners;
    private readonly TextBox _memorySearchPhrase = new()
    {
        Width = 340,
        PlaceholderText = "Search memory text or meaning..."
    };
    private readonly ComboBox _memorySearchMode = new()
    {
        Width = 154,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly CheckBox _memorySearchSecrets = new()
    {
        Text = "Include DM-secret memories", AutoSize = true, Checked = true
    };
    private readonly Button _memorySearchButton = new()
    {
        Text = "Find memories", AutoSize = true
    };
    private readonly Button _memoryEmbedButton = new()
    {
        Text = "Embed missing (up to 100)", AutoSize = true
    };
    private readonly Button _forgetMemoryButton = new()
    {
        Text = "Forget selected memory...", AutoSize = true, Enabled = false
    };
    private readonly Label _memorySearchScope = new()
    {
        Dock = DockStyle.Fill,
        Text = "No characters selected: search the entire campaign.",
        AutoEllipsis = true
    };
    private readonly Label _memorySearchInfo = new()
    {
        Dock = DockStyle.Fill, AutoEllipsis = true,
        Text = "Searches saved memories from PCs and NPCs, including witnesses."
    };
    private readonly DataGridView _memorySearchGrid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    private readonly TextBox _memorySearchPreview = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical
    };
    private readonly DataTable _memorySearchTable = new();
    private IReadOnlyList<CampaignMemoryHit> _memorySearchResults = [];

    private void BuildMemorySearchTab()
    {
        var layout = new SplitContainer
        {
            Dock = DockStyle.Fill, SplitterDistance = 265,
            FixedPanel = FixedPanel.Panel1
        };
        _memorySearchTab.Controls.Add(layout);

        var selector = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(8),
            ColumnCount = 1, RowCount = 4
        };
        selector.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        selector.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        selector.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selector.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        selector.Controls.Add(new Label
        {
            Text = "Search these characters (check multiple)",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        selector.Controls.Add(_memorySearchOwnerFilter, 0, 1);
        selector.Controls.Add(_memorySearchOwners, 0, 2);
        var leftFooter = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var clear = new Button { Text = "Clear selection (all)", AutoSize = true };
        clear.Click += (_, _) =>
        {
            _memorySearchSelectedOwnerIds.Clear();
            PopulateMemorySearchCharacters();
        };
        leftFooter.Controls.Add(clear);
        leftFooter.Controls.Add(_memorySearchScope);
        selector.Controls.Add(leftFooter, 0, 3);
        layout.Panel1.Controls.Add(selector);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(8),
            ColumnCount = 1, RowCount = 4
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        var searchBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true,
            WrapContents = true
        };
        _memorySearchMode.Items.AddRange(["Text / keywords", "Meaning (vectors)"]);
        _memorySearchMode.SelectedIndex = 0;
        searchBar.Controls.Add(_memorySearchPhrase);
        searchBar.Controls.Add(_memorySearchMode);
        searchBar.Controls.Add(_memorySearchButton);
        searchBar.Controls.Add(_memorySearchSecrets);
        searchBar.Controls.Add(_memoryEmbedButton);
        searchBar.Controls.Add(_forgetMemoryButton);
        right.Controls.Add(searchBar, 0, 0);
        right.Controls.Add(_memorySearchInfo, 0, 1);

        string[] names = ["Memory ID", "Character", "Type", "Knowledge", "Day / time",
            "Secret", "Embedded", "Score", "Memory excerpt"];
        string[] dataNames = ["Id", "Character", "Type", "Knowledge", "When",
            "Secret", "Embedded", "Score", "Excerpt"];
        Type[] types = [typeof(long), typeof(string), typeof(string),
            typeof(string), typeof(string), typeof(bool), typeof(bool),
            typeof(double), typeof(string)];
        for (var i = 0; i < names.Length; i++)
        {
            _memorySearchTable.Columns.Add(dataNames[i], types[i]);
            _memorySearchGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = dataNames[i],
                HeaderText = names[i], ReadOnly = true,
                FillWeight = i == 8 ? 300 : i == 1 ? 125 : i == 3 ? 145 : 65,
                MinimumWidth = i == 8 ? 160 : 54
            });
        }
        _memorySearchGrid.Columns[7].DefaultCellStyle.Format = "0.000";
        _memorySearchGrid.DataSource = _memorySearchTable.DefaultView;
        var menu = new ContextMenuStrip();
        var forget = new ToolStripMenuItem("Forget/delete this character memory...");
        forget.Click += async (_, _) =>
            await RunBusyAsync(ForgetSelectedMemoryAsync);
        menu.Items.Add(forget);
        menu.Opening += (_, e) =>
        {
            forget.Enabled = SelectedMemorySearchHit() is not null && !_busy;
            if (!forget.Enabled) e.Cancel = true;
        };
        _memorySearchGrid.ContextMenuStrip = menu;
        _memorySearchGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                _memorySearchGrid.CurrentCell =
                    _memorySearchGrid.Rows[e.RowIndex].Cells[
                        Math.Max(e.ColumnIndex, 0)];
        };
        right.Controls.Add(_memorySearchGrid, 0, 2);
        var preview = new GroupBox { Text = "Selected memory (read-only)", Dock = DockStyle.Fill };
        preview.Controls.Add(_memorySearchPreview);
        right.Controls.Add(preview, 0, 3);
        layout.Panel2.Controls.Add(right);
    }

    private void WireMemorySearchEvents()
    {
        _memorySearchOwnerFilter.TextChanged += (_, _) =>
            PopulateMemorySearchCharacters();
        _memorySearchOwners.ItemCheck += (_, e) =>
        {
            if (_updatingMemorySearchOwners ||
                _memorySearchOwners.Items[e.Index] is not CharacterChoice choice ||
                choice.Id is not long id) return;
            if (e.NewValue == CheckState.Checked)
                _memorySearchSelectedOwnerIds.Add(id);
            else
                _memorySearchSelectedOwnerIds.Remove(id);
            UpdateMemorySearchScope();
        };
        _memorySearchButton.Click += async (_, _) =>
            await RunBusyAsync(SearchMemoriesAsync);
        _memoryEmbedButton.Click += async (_, _) =>
            await RunBusyAsync(EmbedPendingMemoriesAsync);
        _forgetMemoryButton.Click += async (_, _) =>
            await RunBusyAsync(ForgetSelectedMemoryAsync);
        _memorySearchPhrase.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await RunBusyAsync(SearchMemoriesAsync);
        };
        _memorySearchGrid.SelectionChanged += (_, _) =>
        {
            ShowMemorySearchPreview();
            _forgetMemoryButton.Enabled = !_busy &&
                SelectedMemorySearchHit() is not null;
        };
        _memorySearchOwners.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete ||
                _memorySearchOwners.SelectedItem is not CharacterChoice person ||
                person.Id is not long id) return;
            e.SuppressKeyPress = true;
            _memorySearchSelectedOwnerIds.Remove(id);
            PopulateMemorySearchCharacters();
        };
    }

    private void PopulateMemorySearchCharacters()
    {
        var query = _memorySearchOwnerFilter.Text.Trim();
        _updatingMemorySearchOwners = true;
        try
        {
            _memorySearchOwners.Items.Clear();
            foreach (var character in _campaignCharacters
                .Where(c => query.Length == 0 ||
                    c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (c.Occupation?.Contains(query,
                        StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(c => c.Name))
            {
                var choice = new CharacterChoice(
                    character.Id, $"{character.Name} ({character.Type.ToUpperInvariant()})");
                _memorySearchOwners.Items.Add(choice,
                    _memorySearchSelectedOwnerIds.Contains(character.Id));
            }
        }
        finally { _updatingMemorySearchOwners = false; }
        UpdateMemorySearchScope();
    }

    private void UpdateMemorySearchScope()
    {
        _memorySearchScope.Text = _memorySearchSelectedOwnerIds.Count == 0
            ? "None checked = ALL campaign characters."
            : $"{_memorySearchSelectedOwnerIds.Count} character(s) selected.";
    }

    private void AddMemorySearchCharacter(long id)
    {
        if (_campaignCharacters.All(c => c.Id != id)) return;
        _memorySearchSelectedOwnerIds.Add(id);
        PopulateMemorySearchCharacters();
        _status.Text = "Character added to memory-search scope. " +
                       "Click Find memories to run the search.";
    }

    private void ResetMemorySearchForCampaign()
    {
        _memorySearchSelectedOwnerIds.Clear();
        _memorySearchOwnerFilter.Clear();
        _memorySearchPhrase.Clear();
        _memorySearchResults = [];
        _memorySearchTable.Rows.Clear();
        _memorySearchPreview.Clear();
        _memorySearchInfo.Text =
            "Choose characters, or leave the list empty for everyone in the campaign.";
        _forgetMemoryButton.Enabled = false;
        PopulateMemorySearchCharacters();
    }

    private async Task SearchMemoriesAsync()
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign first.");
        var query = _memorySearchPhrase.Text.Trim();
        var owners = _memorySearchSelectedOwnerIds.ToArray();
        bool meaning = _memorySearchMode.SelectedIndex == 1;
        if (meaning && query.Length == 0)
            throw new InvalidOperationException(
                "Enter a phrase to search by meaning, or select Text / keywords to browse.");
        _memorySearchInfo.Text = meaning
            ? "Embedding your query using local Ollama..."
            : "Searching campaign memory text...";

        IReadOnlyList<CampaignMemoryHit> found;
        if (meaning)
        {
            var vector = await _embeddingClient.EmbedQueryAsync(query);
            found = await _database.SearchCampaignMemoriesByMeaningAsync(
                campaignId, owners, OllamaEmbeddingClient.ModelName,
                vector, _memorySearchSecrets.Checked);
        }
        else
            found = await _database.FindCampaignMemoriesAsync(
                campaignId, owners, query, _memorySearchSecrets.Checked);

        _memorySearchResults = found;
        _memorySearchTable.BeginLoadData();
        try
        {
            _memorySearchTable.Rows.Clear();
            foreach (var memory in found)
            {
                var when = memory.WorldDay is int day
                    ? "Day " + day +
                      (memory.WorldTime is TimeOnly time ? $" {time:HH:mm}" : "")
                    : "Undated";
                var excerpt = memory.Content.Length > 240
                    ? memory.Content[..240] + "..." : memory.Content;
                _memorySearchTable.Rows.Add(memory.MemoryId,
                    memory.OwnerName, memory.MemoryType,
                    MemoryKnowledgeStatus.Label(memory.KnowledgeStatus), when,
                    memory.IsSecret, memory.HasEmbedding,
                    Math.Round(memory.Relevance, 3), excerpt);
            }
        }
        finally { _memorySearchTable.EndLoadData(); }
        _memorySearchPreview.Clear();
        _forgetMemoryButton.Enabled = !_busy && SelectedMemorySearchHit() is not null;
        _memorySearchInfo.Text = $"{found.Count} memories shown" +
            (owners.Length == 0 ? " (all characters)" :
                $" ({owners.Length} selected characters)") +
            (meaning
                ? ". Scores are cosine similarity; unembedded memories are omitted."
                : ". Text matches and recent memories; empty query browses memories.");
    }

    private CampaignMemoryHit? SelectedMemorySearchHit()
    {
        if (_memorySearchGrid.CurrentRow?.Cells[0].Value is not long memoryId)
            return null;
        return _memorySearchResults.FirstOrDefault(m => m.MemoryId == memoryId);
    }

    private void ShowMemorySearchPreview()
    {
        var memory = SelectedMemorySearchHit();
        if (memory is null)
        {
            _memorySearchPreview.Clear();
            return;
        }
        _memorySearchPreview.Text =
            $"{memory.OwnerName} ({memory.OwnerType.ToUpperInvariant()}) | " +
            $"{memory.MemoryType} | " +
            $"{MemoryKnowledgeStatus.Label(memory.KnowledgeStatus)} | " +
            $"Importance {memory.Importance} | " +
            (memory.IsSecret ? "DM SECRET" : "Public") + Environment.NewLine +
            (memory.WorldDay is int day
                ? $"Day {day}" +
                  (memory.WorldTime is TimeOnly time ? $" at {time:HH:mm}" : "")
                : "In-world date unknown") + Environment.NewLine +
            (string.IsNullOrWhiteSpace(memory.KnowledgeSource)
                ? ""
                : Environment.NewLine + "Source: " + memory.KnowledgeSource) +
            Environment.NewLine + Environment.NewLine + memory.Content;
    }

    private async Task ForgetSelectedMemoryAsync()
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign.");
        var memory = SelectedMemorySearchHit() ??
            throw new InvalidOperationException("Choose the memory to forget.");

        var preview = memory.Content.Length > 360
            ? memory.Content[..360] + "..." : memory.Content;
        var linkedMessage = memory.MemoryType is "overheard" or "experience"
            ? "If this memory was created from approved dialogue, that " +
              "dialogue remains in history. Other characters may still remember it."
            : "Other characters' memories and the campaign's approved " +
              "dialogue remain unchanged.";

        if (MessageBox.Show(this,
            $"Permanently remove memory #{memory.MemoryId} from " +
            $"{memory.OwnerName}'s mind?\n\n\"{preview}\"\n\n" +
            linkedMessage +
            "\n\nOnly this one memory (and its embedding) will be deleted. " +
            "The operation cannot be undone.",
            "Forget character memory", MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
            != DialogResult.Yes)
            return;

        if (!await _database.ForgetCharacterMemoryAsync(campaignId,
            memory.OwnerCharacterId, memory.MemoryId, memory.Content))
            throw new InvalidOperationException(
                "Memory was already removed or modified. Search again.");
        ResetSuggestions(); // Prevent accepting a suggestion based on forgotten evidence.
        await SearchMemoriesAsync();
        if (NpcId == memory.OwnerCharacterId)
            await LoadMemoriesAsync();
        _status.Text =
            $"Memory #{memory.MemoryId} forgotten by {memory.OwnerName}. " +
            "Other characters' memories and conversation history are unchanged.";
    }

    private async Task EmbedPendingMemoriesAsync()
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign.");
        var result = await _memoryService.EmbedPendingAsync(campaignId, limit: 100);
        _memorySearchInfo.Text =
            $"Embedding pass: {result.Stored} stored, {result.Skipped} skipped. " +
            "Applies across this campaign; repeat to process more memories. " +
            "Click Find memories to refresh.";
        _status.Text = _memorySearchInfo.Text;
    }
}
