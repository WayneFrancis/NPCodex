using NPCodex.AI;
using NPCodex.Data;

namespace NPCodex.Desktop;

/// <summary>
/// Initial DM-only WinForms workspace. Deliberately not player-facing:
/// suggested dialogue is never saved until the DM explicitly approves it.
/// </summary>
public sealed class MainForm : Form
{
    private readonly NPCodexDatabase _database;
    private readonly OllamaEmbeddingClient _embeddingClient = new();
    private readonly OllamaChatClient _chatClient = new();
    private readonly NpcMemoryService _memoryService;
    private readonly NpcDialogueService _dialogueService;

    private readonly ComboBox _campaigns = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox _npcs = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox _playerCharacters = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _playerLine = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly Button _generate = new() { Text = "Generate 3 replies", AutoSize = true };
    private readonly Button _approve = new() { Text = "Approve and save interaction", AutoSize = true };
    private readonly RadioButton[] _choiceChecks = new RadioButton[3];
    private readonly TextBox[] _choiceTexts = new TextBox[3];
    private readonly RadioButton _manualChoice = new() { Text = "Use my manually written response", AutoSize = true };
    private readonly TextBox _manualReply = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox _createMemoryOnApproval = new() { Text = "Also save a memory for this NPC", AutoSize = true };
    private readonly TextBox _newMemory = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly NumericUpDown _newMemoryImportance = new() { Minimum = 1, Maximum = 5, Value = 3, Width = 48 };
    private readonly CheckBox _newMemorySecret = new() { Text = "Secret", AutoSize = true };
    private readonly ListBox _sources = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _memoriesTab = new("Memories");
    private readonly ListBox _memories = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _memoryEditor = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly NumericUpDown _importance = new() { Minimum = 1, Maximum = 5, Value = 3, Width = 55 };
    private readonly CheckBox _isSecret = new() { Text = "Secret / DM-only", AutoSize = true };
    private readonly Button _saveMemory = new() { Text = "Save memory", AutoSize = true };
    private readonly Button _deleteMemory = new() { Text = "Delete selected", AutoSize = true };
    private readonly Button _newMemoryButton = new() { Text = "New memory", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true,
        Text = "Select an NPC to begin.", TextAlign = ContentAlignment.MiddleLeft };

    private NpcDialogueChoices? _latestOptions;
    private NpcMemoryRecord? _editingMemory;
    private bool _ignoreSelectionEvents;
    private bool _busy;

    private long? CampaignId => (_campaigns.SelectedItem as CampaignChoice)?.Id;
    private long? NpcId => (_npcs.SelectedItem as CharacterChoice)?.Id;
    private long? PlayerId => (_playerCharacters.SelectedItem as CharacterChoice)?.Id;

    public MainForm(string connectionString)
    {
        Text = "NPCodex — Dungeon Master Workspace";
        MinimumSize = new Size(1080, 740);
        Size = new Size(1450, 960);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        _database = new NPCodexDatabase(connectionString);
        _memoryService = new NpcMemoryService(_database, _embeddingClient);
        _dialogueService = new NpcDialogueService(_database, _memoryService, _chatClient);
        BuildLayout();
        WireEvents();

        Shown += async (_, _) => await RunBusyAsync(LoadCampaignsAsync);
        FormClosed += (_, _) =>
        {
            _chatClient.Dispose();
            _embeddingClient.Dispose();
            _database.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        Controls.Add(root);
        root.Controls.Add(_status, 0, 1);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, SplitterDistance = 275 };
        root.Controls.Add(split, 0, 0);
        split.Panel1MinSize = 230;
        split.Panel2MinSize = 740;

        var sidebar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(10),
            ColumnCount = 1, RowCount = 7
        };
        foreach (var height in new[] { 30f, 38f, 32f })
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        sidebar.Controls.Add(new Label { Text = "Campaign", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft }, 0, 0);
        sidebar.Controls.Add(_campaigns, 0, 1);
        sidebar.Controls.Add(new Label { Text = "NPCs", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft }, 0, 2);
        sidebar.Controls.Add(_npcs, 0, 3);
        sidebar.Controls.Add(new Label { Text = "Player character (optional)", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft }, 0, 4);
        sidebar.Controls.Add(_playerCharacters, 0, 5);
        var reload = new Button { Text = "Refresh campaign", Dock = DockStyle.Fill };
        reload.Click += async (_, _) => await RunBusyAsync(LoadCampaignsAsync);
        sidebar.Controls.Add(reload, 0, 6);
        split.Panel1.Controls.Add(sidebar);

        var dialogueTab = new TabPage("Dialogue");
        _tabs.TabPages.Add(dialogueTab);
        _tabs.TabPages.Add(_memoriesTab);
        split.Panel2.Controls.Add(_tabs);

        var conversation = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
            Padding = new Padding(9), AutoScroll = true
        };
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 244));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        conversation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        dialogueTab.Controls.Add(conversation);

        var playerGroup = new GroupBox { Text = "What the player says", Dock = DockStyle.Fill };
        playerGroup.Controls.Add(_playerLine);
        conversation.Controls.Add(playerGroup, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.Add(_generate);
        actions.Controls.Add(new Label { Text = "Suggestions are drafts, not campaign facts.",
            AutoSize = true, Padding = new Padding(8, 9, 0, 0) });
        conversation.Controls.Add(actions, 0, 1);

        var choices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        for (var i = 0; i < 3; i++)
        {
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
            var panel = new GroupBox { Text = new[] { "A — Natural", "B — Warm", "C — Guarded" }[i],
                Dock = DockStyle.Fill, Padding = new Padding(6) };
            var inside = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            inside.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            inside.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var check = new RadioButton { Text = "Use this reply", AutoSize = true };
            var editor = new TextBox { Dock = DockStyle.Fill, Multiline = true,
                ScrollBars = ScrollBars.Vertical, Enabled = false };
            var index = i;
            editor.Enter += (_, _) => _choiceChecks[index].Checked = true;
            inside.Controls.Add(check, 0, 0);
            inside.Controls.Add(editor, 0, 1);
            panel.Controls.Add(inside);
            choices.Controls.Add(panel, i, 0);
            _choiceChecks[i] = check;
            _choiceTexts[i] = editor;
        }
        conversation.Controls.Add(choices, 0, 2);

        var manualGroup = new GroupBox { Text = "Or write your own dialogue", Dock = DockStyle.Fill };
        var manualLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        manualLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        manualLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        manualLayout.Controls.Add(_manualChoice, 0, 0);
        manualLayout.Controls.Add(_manualReply, 0, 1);
        manualGroup.Controls.Add(manualLayout);
        _manualReply.Enter += (_, _) => _manualChoice.Checked = true;
        conversation.Controls.Add(manualGroup, 0, 3);

        var memoryGroup = new GroupBox { Text = "Optional memory — write only what actually happened", Dock = DockStyle.Fill };
        var memoryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        memoryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        memoryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill };
        settings.Controls.Add(_createMemoryOnApproval);
        settings.Controls.Add(new Label { Text = "Importance:", AutoSize = true,
            Padding = new Padding(12, 5, 0, 0) });
        settings.Controls.Add(_newMemoryImportance);
        settings.Controls.Add(_newMemorySecret);
        memoryLayout.Controls.Add(settings, 0, 0);
        memoryLayout.Controls.Add(_newMemory, 0, 1);
        memoryGroup.Controls.Add(memoryLayout);
        conversation.Controls.Add(memoryGroup, 0, 4);

        var saveRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        saveRow.Controls.Add(_approve);
        saveRow.Controls.Add(new Label { Text = "Saves the chosen dialogue; memory is optional.",
            AutoSize = true, Padding = new Padding(8, 9, 0, 0) });
        conversation.Controls.Add(saveRow, 0, 5);

        var sourcesGroup = new GroupBox { Text = "Retrieved memories — DM reference only", Dock = DockStyle.Fill };
        sourcesGroup.Controls.Add(_sources);
        conversation.Controls.Add(sourcesGroup, 0, 6);

        BuildMemoryTab();
    }

    private void BuildMemoryTab()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterDistance = 240 };
        _memoriesTab.Controls.Add(split);
        split.Panel1.Controls.Add(_memories);

        var editorLayout = new TableLayoutPanel { Dock = DockStyle.Fill,
            Padding = new Padding(8), ColumnCount = 1, RowCount = 3 };
        editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        editorLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        split.Panel2.Controls.Add(editorLayout);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "Importance:", AutoSize = true,
            Padding = new Padding(0, 6, 0, 0) });
        header.Controls.Add(_importance);
        header.Controls.Add(_isSecret);
        header.Controls.Add(new Label { Text = "Edit the NPC's actual knowledge, not the AI's guesses.",
            AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        editorLayout.Controls.Add(header, 0, 0);
        editorLayout.Controls.Add(_memoryEditor, 0, 1);

        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill };
        commands.Controls.Add(_newMemoryButton);
        commands.Controls.Add(_saveMemory);
        commands.Controls.Add(_deleteMemory);
        editorLayout.Controls.Add(commands, 0, 2);
    }

    private void WireEvents()
    {
        _campaigns.SelectedIndexChanged += async (_, _) =>
        {
            if (!_ignoreSelectionEvents && !_busy)
                await RunBusyAsync(LoadCharactersAsync);
        };
        _npcs.SelectedIndexChanged += async (_, _) =>
        {
            if (!_ignoreSelectionEvents && !_busy)
            {
                ResetDialogue();
                await RunBusyAsync(LoadMemoriesAsync);
            }
        };
        _generate.Click += async (_, _) => await RunBusyAsync(GenerateAsync);
        _approve.Click += async (_, _) => await RunBusyAsync(ApproveAsync);
        _memories.SelectedIndexChanged += (_, _) => SelectMemory();
        _newMemoryButton.Click += (_, _) => ClearMemoryEditor();
        _saveMemory.Click += async (_, _) => await RunBusyAsync(SaveMemoryAsync);
        _deleteMemory.Click += async (_, _) => await RunBusyAsync(DeleteMemoryAsync);
    }

    private async Task LoadCampaignsAsync()
    {
        var previousId = CampaignId;
        var campaigns = await _database.GetCampaignsAsync();
        _ignoreSelectionEvents = true;
        try
        {
            _campaigns.Items.Clear();
            foreach (var campaign in campaigns)
                _campaigns.Items.Add(new CampaignChoice(campaign.Id, campaign.Name));
            _campaigns.SelectedItem = _campaigns.Items.Cast<CampaignChoice>()
                .FirstOrDefault(c => c.Id == previousId)
                ?? _campaigns.Items.Cast<CampaignChoice>().FirstOrDefault();
        }
        finally { _ignoreSelectionEvents = false; }
        await LoadCharactersAsync();
    }

    private async Task LoadCharactersAsync()
    {
        ResetDialogue();
        var previousId = NpcId;
        _ignoreSelectionEvents = true;
        try
        {
            _npcs.Items.Clear();
            _playerCharacters.Items.Clear();
            _playerCharacters.Items.Add(new CharacterChoice(null, "(No specific player character)"));
            _playerCharacters.SelectedIndex = 0;
        }
        finally { _ignoreSelectionEvents = false; }

        if (CampaignId is not long campaignId)
        {
            _memories.Items.Clear();
            ClearMemoryEditor();
            return;
        }

        var world = await _database.GetCampaignWorldAsync(campaignId);
        _ignoreSelectionEvents = true;
        try
        {
            foreach (var person in world.Characters.Where(c => c.Type == "npc"))
                _npcs.Items.Add(new CharacterChoice(person.Id, person.Name));
            foreach (var person in world.Characters.Where(c => c.Type == "pc"))
                _playerCharacters.Items.Add(new CharacterChoice(person.Id, person.Name));
            _npcs.SelectedItem = _npcs.Items.Cast<CharacterChoice>()
                .FirstOrDefault(p => p.Id == previousId)
                ?? _npcs.Items.Cast<CharacterChoice>().FirstOrDefault();
        }
        finally { _ignoreSelectionEvents = false; }

        await LoadMemoriesAsync();
        _status.Text = $"Campaign loaded: {world.Characters.Count(c => c.Type == "npc")} NPCs.";
    }

    private async Task LoadMemoriesAsync(long? selectId = null)
    {
        _memories.Items.Clear();
        _editingMemory = null;
        _memoryEditor.Text = "";
        _importance.Value = 3;
        _isSecret.Checked = false;

        if (CampaignId is not long campaignId || NpcId is not long npcId) return;

        var records = await _database.GetNpcMemoriesAsync(campaignId, npcId);
        foreach (var record in records)
            _memories.Items.Add(new MemoryChoice(record));

        if (selectId is long id)
            _memories.SelectedItem = _memories.Items.Cast<MemoryChoice>()
                .FirstOrDefault(m => m.Memory.Id == id);

        if (_memories.SelectedIndex < 0 && _memories.Items.Count > 0)
            _memories.SelectedIndex = 0;
    }

    private void SelectMemory()
    {
        if (_memories.SelectedItem is not MemoryChoice item)
            return;

        _editingMemory = item.Memory;
        _memoryEditor.Text = item.Memory.Content;
        _importance.Value = item.Memory.Importance;
        _isSecret.Checked = item.Memory.IsSecret;
    }

    private void ClearMemoryEditor()
    {
        _memories.ClearSelected();
        _editingMemory = null;
        _memoryEditor.Clear();
        _importance.Value = 3;
        _isSecret.Checked = false;
        _memoryEditor.Focus();
    }

    private async Task SaveMemoryAsync()
    {
        if (CampaignId is not long campaignId || NpcId is not long npcId)
            throw new InvalidOperationException("Select an NPC first.");
        if (string.IsNullOrWhiteSpace(_memoryEditor.Text))
            throw new InvalidOperationException("Write a memory before saving.");

        var memoryId = await _database.SaveNpcMemoryAsync(
            campaignId, npcId, _editingMemory?.Id,
            _memoryEditor.Text, (short)_importance.Value,
            _isSecret.Checked, _editingMemory?.Content);

        var embedded = await EmbedSavedMemoryAsync(campaignId, memoryId);
        await LoadMemoriesAsync(memoryId);
        _status.Text = embedded
            ? $"Memory #{memoryId} saved and embedded."
            : $"Memory #{memoryId} saved (embedding may still be pending).";
    }

    private async Task DeleteMemoryAsync()
    {
        if (CampaignId is not long campaignId || NpcId is not long npcId
            || _editingMemory is null)
            throw new InvalidOperationException("Select an existing memory to delete.");

        if (MessageBox.Show(
            "Delete this NPC memory permanently? Existing interaction history will remain.",
            "Confirm memory deletion", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            != DialogResult.Yes) return;

        if (!await _database.DeleteNpcMemoryAsync(
                campaignId, npcId, _editingMemory.Id, _editingMemory.Content))
            throw new InvalidOperationException("The memory changed. Reload before deleting.");

        await LoadMemoriesAsync();
        _status.Text = "Memory deleted.";
    }

    private async Task GenerateAsync()
    {
        if (CampaignId is not long campaignId || NpcId is not long npcId)
            throw new InvalidOperationException("Select an NPC first.");
        if (string.IsNullOrWhiteSpace(_playerLine.Text))
            throw new InvalidOperationException("Enter the player's words first.");

        ResetSuggestions();
        _status.Text = "Generating three reply candidates using local Ollama...";
        var choices = await _dialogueService.GenerateOptionsAsync(
            campaignId, npcId, _playerLine.Text);
        _latestOptions = choices;
        for (var i = 0; i < choices.Options.Count; i++)
        {
            _choiceTexts[i].Text = choices.Options[i].Text;
            _choiceTexts[i].Enabled = true;
        }
        _choiceChecks[0].Checked = true;
        foreach (var source in choices.RetrievedMemories)
            _sources.Items.Add($"Memory #{source.Id}  distance {source.CosineDistance:F3}: {source.Content}");

        _status.Text = $"Generated {choices.Options.Count} suggestions with {choices.ModelName}. Nothing saved.";
    }

    private string SelectedResponse()
    {
        if (_manualChoice.Checked)
            return _manualReply.Text.Trim();
        for (var i = 0; i < _choiceChecks.Length; i++)
        {
            if (_choiceChecks[i].Checked)
                return _choiceTexts[i].Text.Trim();
        }
        return "";
    }

    private async Task ApproveAsync()
    {
        if (CampaignId is not long campaignId || NpcId is not long npcId)
            throw new InvalidOperationException("Select an NPC first.");
        var reply = SelectedResponse();
        if (string.IsNullOrWhiteSpace(_playerLine.Text) || string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("Enter player dialogue and choose or write a non-empty NPC response.");

        var withMemory = _createMemoryOnApproval.Checked;
        if (withMemory && string.IsNullOrWhiteSpace(_newMemory.Text))
            throw new InvalidOperationException("Write the memory or uncheck the memory option.");

        var notice = withMemory
            ? "Save the selected NPC reply as an interaction AND the separate memory you wrote?"
            : "Save the selected NPC reply as an interaction WITHOUT creating a memory?";
        if (MessageBox.Show(notice, "Approve NPC dialogue",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var result = await _database.RecordApprovedDialogueAsync(
            campaignId, npcId, PlayerId, _playerLine.Text, reply,
            withMemory ? _newMemory.Text : null,
            (short)_newMemoryImportance.Value, _newMemorySecret.Checked);

        var embedded = result.MemoryId is long memoryId
            ? await EmbedSavedMemoryAsync(campaignId, memoryId)
            : false;

        await LoadMemoriesAsync(result.MemoryId);
        _playerLine.Clear();
        _manualReply.Clear();
        _newMemory.Clear();
        _createMemoryOnApproval.Checked = false;
        ResetSuggestions();
        _status.Text = result.MemoryId is long id
            ? $"Interaction #{result.InteractionId} approved, memory #{id} saved" +
              (embedded ? " and embedded." : " (embedding pending).")
            : $"Interaction #{result.InteractionId} approved and saved. No memory created.";
    }

    private async Task<bool> EmbedSavedMemoryAsync(long campaignId, long memoryId)
    {
        try
        {
            return await _memoryService.EmbedMemoryAsync(campaignId, memoryId);
        }
        catch (Exception ex)
        {
            // The SQL transaction has already committed: never tell the DM
            // the save failed when only the optional embedding failed.
            MessageBox.Show(
                "The memory was saved in PostgreSQL, but Ollama could not embed it yet.\n" +
                "You can retry using the embed-memories command.\n\n" + ex.Message,
                "Memory saved; embedding pending", MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
    }

    private void ResetDialogue()
    {
        _playerLine.Clear();
        _manualReply.Clear();
        _newMemory.Clear();
        _createMemoryOnApproval.Checked = false;
        ResetSuggestions();
    }

    private void ResetSuggestions()
    {
        _latestOptions = null;
        _sources.Items.Clear();
        for (var i = 0; i < _choiceTexts.Length; i++)
        {
            _choiceTexts[i].Clear();
            _choiceTexts[i].Enabled = false;
            _choiceChecks[i].Checked = false;
        }
        _manualChoice.Checked = true;
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        _generate.Enabled = _approve.Enabled = _saveMemory.Enabled =
            _deleteMemory.Enabled = _campaigns.Enabled = _npcs.Enabled = false;
        UseWaitCursor = true;
        try { await operation(); }
        catch (Exception ex)
        {
            _status.Text = "Operation failed. No unapproved dialogue was saved.";
            MessageBox.Show(ex.Message, "NPCodex", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _busy = false;
            _generate.Enabled = _approve.Enabled = _saveMemory.Enabled =
                _deleteMemory.Enabled = _campaigns.Enabled = _npcs.Enabled = true;
        }
    }

    private sealed record CampaignChoice(long Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record CharacterChoice(long? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record MemoryChoice(NpcMemoryRecord Memory)
    {
        public override string ToString()
        {
            var description = Memory.Content.Replace("\r", " ").Replace("\n", " ");
            if (description.Length > 130) description = description[..130] + "...";
            return $"#{Memory.Id}  {(Memory.IsSecret ? "[Secret] " : "")}" +
                   $"{(Memory.HasEmbedding ? "[Embedded] " : "[Pending] ")}{description}";
        }
    }
}
