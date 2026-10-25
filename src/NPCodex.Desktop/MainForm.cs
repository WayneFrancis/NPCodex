using System.Drawing;
using System.Windows.Forms;
using NPCodex.AI;
using NPCodex.Data;

namespace NPCodex.Desktop;

/// <summary>
/// Initial DM-only WinForms workspace. Deliberately not player-facing:
/// suggested dialogue is never saved until the DM explicitly approves it.
/// </summary>
public sealed partial class MainForm : Form
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
    private readonly Label _relationshipStatus = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Text = "Attitude: not evaluated. Select a player character and generate replies.",
        ForeColor = Color.DarkSlateBlue,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Button _approve = new() { Text = "Approve and save interaction", AutoSize = true };
    private readonly RadioButton[] _choiceChecks = new RadioButton[3];
    private readonly TextBox[] _choiceTexts = new TextBox[3];
    private readonly RadioButton _manualChoice = new() { Text = "Use my manually written response", AutoSize = true };
    private readonly TextBox _manualReply = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox _createMemoryOnApproval = new() { Text = "Also save a memory for this NPC", AutoSize = true };
    private readonly Button _draftMemoryButton = new() { Text = "Draft from dialogue", AutoSize = true };
    private readonly TextBox _newMemory = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly NumericUpDown _newMemoryImportance = new() { Minimum = 1, Maximum = 5, Value = 3, Width = 48 };
    private readonly CheckBox _newMemorySecret = new() { Text = "Secret", AutoSize = true };
    private readonly ListBox _sources = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _memoriesTab = new("Memories");
    private readonly Button _newConversation = new() { Text = "End conversation / start new", AutoSize = true };
    private readonly Label _conversationLabel = new() { AutoSize = true, Text = "No active conversation" };
    private readonly RichTextBox _history = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true,
        BackColor = SystemColors.Window, BorderStyle = BorderStyle.None
    };
    private long? _activeConversationId;
    private IReadOnlyList<ConversationTurn> _approvedTurns = Array.Empty<ConversationTurn>();
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
    private long? PlayerId => (_asking.SelectedItem as CharacterChoice)?.Id;

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
        AttachDictationToTextBoxes(this);

        Shown += async (_, _) => await RunBusyAsync(LoadCampaignsAsync);
        FormClosed += (_, _) =>
        {
            DisposeDictation();
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

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6,
            Size = new Size(1300, 850), SplitterDistance = 275 };
        root.Controls.Add(split, 0, 0);
        split.Panel1MinSize = 230;
        split.Panel2MinSize = 740;

        split.Panel1.Controls.Add(BuildCampaignNavigation());

        var dialogueTab = new TabPage("Dialogue");
        var dialogueHistoryTab = new TabPage("Conversation History");
        _tabs.TabPages.Add(dialogueTab);
        _tabs.TabPages.Add(dialogueHistoryTab);
        _tabs.TabPages.Add(_memoriesTab);
        _tabs.TabPages.Add(_memorySearchTab);
        _tabs.TabPages.Add(_personalityTab);
        _tabs.TabPages.Add(_aliasesTab);
        _tabs.TabPages.Add(_characterSheetTab);
        _tabs.TabPages.Add(_locationsTab);
        split.Panel2.Controls.Add(_tabs);

        var conversation = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
            Padding = new Padding(9), AutoScroll = true
        };
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 155));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 244));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 165));
        conversation.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        dialogueTab.Controls.Add(conversation);
        dialogueHistoryTab.Controls.Add(BuildConversationHistoryArea());

        conversation.Controls.Add(BuildConversationHeader(), 0, 0);

        var playerGroup = new GroupBox { Text = "What the initiating character says", Dock = DockStyle.Fill };
        playerGroup.Controls.Add(_playerLine);
        conversation.Controls.Add(playerGroup, 0, 1);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var generateRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        generateRow.Controls.Add(_generate);
        generateRow.Controls.Add(_allowPreparedLies);
        generateRow.Controls.Add(new Label
        {
            Text = "DM-only drafts — nothing is saved until approved.",
            AutoSize = true,
            Padding = new Padding(8, 9, 0, 0)
        });
        actions.Controls.Add(generateRow, 0, 0);
        actions.Controls.Add(_relationshipStatus, 0, 1);
        conversation.Controls.Add(actions, 0, 2);

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
        conversation.Controls.Add(choices, 0, 3);

        var manualGroup = new GroupBox { Text = "Or write your own dialogue", Dock = DockStyle.Fill };
        var manualLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        manualLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        manualLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        manualLayout.Controls.Add(_manualChoice, 0, 0);
        manualLayout.Controls.Add(_manualReply, 0, 1);
        manualGroup.Controls.Add(manualLayout);
        _manualReply.Enter += (_, _) => _manualChoice.Checked = true;
        conversation.Controls.Add(manualGroup, 0, 4);

        var memoryGroup = new GroupBox { Text = "Optional memory — write only what actually happened", Dock = DockStyle.Fill };
        var memoryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        memoryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        memoryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill };
        settings.Controls.Add(_createMemoryOnApproval);
        settings.Controls.Add(_draftMemoryButton);
        settings.Controls.Add(new Label { Text = "Importance:", AutoSize = true,
            Padding = new Padding(12, 5, 0, 0) });
        settings.Controls.Add(_newMemoryImportance);
        settings.Controls.Add(_newMemorySecret);
        memoryLayout.Controls.Add(settings, 0, 0);
        memoryLayout.Controls.Add(_newMemory, 0, 1);
        memoryGroup.Controls.Add(memoryLayout);
        conversation.Controls.Add(memoryGroup, 0, 5);
        conversation.Controls.Add(CreateSceneControls(), 0, 6);

        var saveRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        saveRow.Controls.Add(_approve);
        saveRow.Controls.Add(new Label { Text = "Saves the chosen dialogue; memory is optional.",
            AutoSize = true, Padding = new Padding(8, 9, 0, 0) });
        conversation.Controls.Add(saveRow, 0, 7);

        BuildMemoryTab();
        BuildMemorySearchTab();
        BuildPersonalityTab();
        BuildAliasesTab();
        BuildDndSheetTab();
        BuildLocationsTab();
    }

    private void BuildMemoryTab()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            Size = new Size(900, 700), SplitterDistance = 240 };
        _memoriesTab.Controls.Add(split);
        split.Panel1.Controls.Add(_memories);

        var editorLayout = new TableLayoutPanel { Dock = DockStyle.Fill,
            Padding = new Padding(8), ColumnCount = 1, RowCount = 4 };
        editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        editorLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        split.Panel2.Controls.Add(editorLayout);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "Importance:", AutoSize = true,
            Padding = new Padding(0, 6, 0, 0) });
        header.Controls.Add(_importance);
        header.Controls.Add(_isSecret);
        header.Controls.Add(_memoryUseWorldDate);
        header.Controls.Add(new Label { Text = "Day:", AutoSize = true,
            Padding = new Padding(5, 6, 0, 0) });
        header.Controls.Add(_memoryWorldDay);
        header.Controls.Add(new Label { Text = "Time:", AutoSize = true,
            Padding = new Padding(5, 6, 0, 0) });
        header.Controls.Add(_memoryWorldTime);
        header.Controls.Add(new Label { Text = "Edit the NPC's actual knowledge, not the AI's guesses.",
            AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        editorLayout.Controls.Add(header, 0, 0);
        editorLayout.Controls.Add(CreateMemoryKnowledgeControls(), 0, 1);
        editorLayout.Controls.Add(_memoryEditor, 0, 2);

        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill };
        commands.Controls.Add(_newMemoryButton);
        commands.Controls.Add(_saveMemory);
        commands.Controls.Add(_deleteMemory);
        editorLayout.Controls.Add(commands, 0, 3);
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
                await RunBusyAsync(async () =>
                {
                    await LoadMemoriesAsync();
                    await LoadPersonalityAsync();
                    PopulateAliasTargets();
                    await LoadAliasesAsync();
                });
        };
        _playerLine.TextChanged += (_, _) => { if (!_busy) ResetSuggestions(); };
        for (var i = 0; i < _choiceChecks.Length; i++)
        {
            var index = i;
            _choiceChecks[i].CheckedChanged += (_, _) =>
            {
                if (_choiceChecks[index].Checked) SelectChoice(index);
            };
        }
        _manualChoice.CheckedChanged += (_, _) =>
        {
            if (_manualChoice.Checked) SelectChoice(-1);
        };
        _draftMemoryButton.Click += (_, _) => DraftMemoryFromDialogue();
        _newConversation.Click += async (_, _) => await RunBusyAsync(EndConversationAsync);
        _generate.Click += async (_, _) => await RunBusyAsync(GenerateAsync);
        _approve.Click += async (_, _) => await RunBusyAsync(ApproveAsync);
        _memories.SelectedIndexChanged += (_, _) => SelectMemory();
        _newMemoryButton.Click += (_, _) => ClearMemoryEditor();
        _saveMemory.Click += async (_, _) => await RunBusyAsync(SaveMemoryAsync);
        _deleteMemory.Click += async (_, _) => await RunBusyAsync(DeleteMemoryAsync);
        WirePersonalityEvents();
        WireAliasEvents();
        WireSceneEvents();
        WireNavigationEvents();
        WireHistoryEvents();
        WireMemorySearchEvents();
        WireEpistemicEvents();
        WireDndSheetEvents();
        WireLocationsTab();
        _addNpcButton.Click += async (_, _) => await RunBusyAsync(() => AddNpcAsync());
        _addPcButton.Click += async (_, _) => await RunBusyAsync(() => AddPcAsync());
    }

    private async Task LoadCampaignsAsync()
    {
        if (!await _database.HasConversationSchemaAsync())
            throw new InvalidOperationException(
                "Apply database migration 0002_conversations.sql in DBeaver.");
        if (!await _database.HasPersonalitySchemaAsync())
            throw new InvalidOperationException(
                "Apply database migration 0003_npc_profiles.sql in DBeaver.");
        if (!await _database.HasCharacterAliasSchemaAsync())
            throw new InvalidOperationException(
                "Apply database migration 0004_character_aliases.sql in DBeaver.");
        if (!await _database.HasWorldEventsSchemaAsync())
            throw new InvalidOperationException(
                "Apply database migration 0005_world_events.sql in DBeaver.");
        if (!await _database.HasConversationBrowserSchemaAsync())
            throw new InvalidOperationException(
                "Apply database migration 0006_conversation_browser.sql in DBeaver.");
        if (!await _database.HasMemoryProvenanceSchemaAsync())
            throw new InvalidOperationException(
                "Apply database migration 0007_memory_provenance.sql in DBeaver.");
        if (!await _database.HasConversationMaintenanceSchemaAsync())
            throw new InvalidOperationException(
                "Apply database migration 0008_conversation_maintenance.sql in DBeaver.");
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
        try { _npcs.Items.Clear(); }
        finally { _ignoreSelectionEvents = false; }

        if (CampaignId is not long campaignId)
        {
            _memories.Items.Clear();
            ClearMemoryEditor();
            return;
        }

        var world = await _database.GetCampaignBrowserAsync(campaignId);
        _campaignCharacters = world.Characters;
        _campaignLocations = world.Locations;
        _treeAliases = (await _database.GetCampaignCharacterNamesAsync(
            campaignId)).Aliases;
        _ignoreSelectionEvents = true;
        try
        {
            foreach (var person in world.Characters.Where(c => c.Type == "npc"))
                _npcs.Items.Add(new CharacterChoice(person.Id, person.Name));
            // Initiators may now be PCs or other NPCs; populated below.
            _npcs.SelectedItem = _npcs.Items.Cast<CharacterChoice>()
                .FirstOrDefault(p => p.Id == previousId)
                ?? _npcs.Items.Cast<CharacterChoice>().FirstOrDefault();
        }
        finally { _ignoreSelectionEvents = false; }

        if (_rosterCampaignId != campaignId)
        {
            // Never retain results or a sheet selection from another campaign.
            foreach (var tab in _savedSearches.Keys.ToArray())
            {
                _statementTabs.TabPages.Remove(tab);
                tab.Dispose();
            }
            _savedSearches.Clear();
            _sheetTargetId = null;
            _sheetCharacter.Text = "Select a character in this campaign's tree";
            _editLocationId = null;
            _locationSelected.Text = "Select a location in this campaign's tree";
            _locationName.Clear();
            _locationType.Clear();
            _locationDescription.Clear();
            _locationParent.Items.Clear();
            _saveLocation.Enabled = _deleteLocation.Enabled = false;
            _rosterCampaignId = campaignId;
            // A new campaign starts with an empty conversation. The DM chooses
            // who is present by double-clicking them in the location tree.
            _rosterIds.Clear();
            _allowPreparedLies.Checked = false;
            ResetMemorySearchForCampaign();
        }
        else
        {
            // Maintain the DM's scoped characters across a tree refresh,
            // discarding IDs only if their character was deleted.
            _memorySearchSelectedOwnerIds.RemoveWhere(
                id => !_campaignCharacters.Any(c => c.Id == id));
            PopulateMemorySearchCharacters();
        }
        RebuildConversationRoster();
        PopulateLocationTree();
        if (_editLocationId is long editingId &&
            _campaignLocations.Any(l => l.Id == editingId))
            LoadLocationDetails(editingId);
        await LoadRosterHistoryAsync();
        await LoadMemoriesAsync();
        await LoadConversationAsync();
        await LoadPersonalityAsync();
        PopulateAliasTargets();
        await LoadAliasesAsync();
        _status.Text = $"Campaign loaded: {world.Characters.Count(c => c.Type == "npc")} NPCs.";
    }

    private async Task LoadConversationAsync()
    {
        _activeConversationId = null;
        _approvedTurns = Array.Empty<ConversationTurn>();
        _history.Clear();
        _conversationLabel.Text = "No active conversation — the first approved reply starts one.";
        if (CampaignId is not long campaignId ||
            ReplyCharacterId is not long npcId) return;

        var session = await _database.FindOpenConversationAsync(
            campaignId, npcId, PlayerId);
        if (session is null) return;

        _activeConversationId = session.Id;
        _approvedTurns = await _database.GetConversationTurnsAsync(
            campaignId, npcId, session.Id);
        _conversationLabel.Text = $"Conversation #{session.Id} — {_approvedTurns.Count} approved exchanges";

        var sb = new System.Text.StringBuilder();
        foreach (var turn in _approvedTurns)
        {
            var speaker = turn.SpeakerCharacterId is long speakerId
                ? _campaignCharacters.FirstOrDefault(c => c.Id == speakerId)?.Name
                  ?? $"Character #{speakerId}"
                : "Unspecified speaker";
            var respondingNpc = _campaignCharacters.FirstOrDefault(c => c.Id == npcId)?.Name
                ?? $"NPC #{npcId}";
            sb.AppendLine(turn.WorldDay is int day
                ? $"[Day {day}" +
                  (turn.WorldTime is TimeOnly time ? $" at {time:HH:mm}" : "") + "]"
                : "[Game date not recorded]");
            sb.AppendLine($"{speaker}: {turn.PlayerLine}");
            sb.AppendLine($"{respondingNpc}: {turn.NpcReply}");
            sb.AppendLine();
        }
        _history.Text = sb.ToString();
        _history.SelectionStart = _history.TextLength;
        _history.ScrollToCaret();
    }

    private async Task EndConversationAsync()
    {
        if (CampaignId is not long campaignId || ReplyCharacterId is not long npcId)
            throw new InvalidOperationException("Select a character to answer first.");

        var text = _activeConversationId is null
            ? "Discard any current, unapproved text and start a fresh conversation?"
            : "End this conversation? Its approved transcript will be preserved. " +
              "A new conversation starts with your next approved reply.";
        if (MessageBox.Show(text, "New conversation",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (_activeConversationId is long sessionId)
            if (!await _database.EndConversationAsync(campaignId, npcId, sessionId))
                throw new InvalidOperationException(
                    "Could not end the conversation. Refresh to check its state.");

        ResetDialogue();
        _allowPreparedLies.Checked = false;
        await LoadConversationAsync();
        _status.Text = "Ready for a fresh conversation. Previous approved records remain saved.";
    }

    private async Task LoadMemoriesAsync(long? selectId = null)
    {
        _memories.Items.Clear();
        _editingMemory = null;
        _memoryEditor.Text = "";
        _importance.Value = 3;
        _isSecret.Checked = false;
        _memoryUseWorldDate.Checked = true;
        SetSelectedMemoryKnowledgeStatus(MemoryKnowledgeStatus.Recollection);
        _memoryKnowledgeSource.Clear();

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
        SetSelectedMemoryKnowledgeStatus(item.Memory.KnowledgeStatus);
        _memoryKnowledgeSource.Text = item.Memory.KnowledgeSource ?? "";
        _memoryUseWorldDate.Checked = item.Memory.WorldDay is not null;
        if (item.Memory.WorldDay is int day)
            _memoryWorldDay.Value = Math.Clamp(
                day, (int)_memoryWorldDay.Minimum, (int)_memoryWorldDay.Maximum);
        if (item.Memory.WorldTime is TimeOnly worldTime)
            _memoryWorldTime.Value = DateTime.Today.Add(worldTime.ToTimeSpan());
    }

    private void ClearMemoryEditor()
    {
        _memories.ClearSelected();
        _editingMemory = null;
        _memoryEditor.Clear();
        _importance.Value = 3;
        _isSecret.Checked = false;
        SetSelectedMemoryKnowledgeStatus(MemoryKnowledgeStatus.Recollection);
        _memoryKnowledgeSource.Clear();
        _memoryUseWorldDate.Checked = true;
        _memoryWorldDay.Value = _worldDay.Value;
        _memoryWorldTime.Value = _worldTime.Value;
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
            _isSecret.Checked, _editingMemory?.Content,
            when: _memoryUseWorldDate.Checked ? MemoryWorldMoment : null,
            knowledgeStatus: SelectedMemoryKnowledgeStatus,
            knowledgeSource: _memoryKnowledgeSource.Text);

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
        if (CampaignId is not long campaignId || PlayerId is not long askingId)
            throw new InvalidOperationException("Choose an initiating character from the roster.");
        if (string.IsNullOrWhiteSpace(_playerLine.Text))
            throw new InvalidOperationException("Enter what the initiating character says.");

        long npcId;
        if (ReplyAll)
        {
            var candidates = _rosterIds
                .Where(id => id != askingId &&
                    _campaignCharacters.Any(c => c.Id == id && c.Type == "npc"))
                .ToArray();
            npcId = await _dialogueService.ChooseReplyingNpcAsync(
                campaignId, askingId, candidates, _playerLine.Text);
            _updatingCast = true;
            try
            {
                _answering.SelectedItem = _answering.Items.Cast<CharacterChoice>()
                    .First(c => c.Id == npcId);
            }
            finally { _updatingCast = false; }
            UpdateRecipientOptions();
            await LoadConversationAsync();
        }
        else if (ReplyCharacterId is long selectedId && SelectedResponderIsNpc)
            npcId = selectedId;
        else
            throw new InvalidOperationException(
                "A player-character response is written manually. Select an NPC to generate replies.");

        ResetSuggestions();
        _status.Text = "Generating three reply candidates using local Ollama...";
        var choices = await _dialogueService.GenerateOptionsAsync(
            campaignId, npcId, _playerLine.Text,
            approvedHistory: _approvedTurns,
            playerCharacterId: PlayerId,
            currentMoment: CurrentWorldMoment,
            allowPreparedLies: _allowPreparedLies.Checked);
        _latestOptions = choices;
        var fixedCount = choices.Options.Count(o => o.SpeakerWordingAdjusted);
        var speakerWarnings = choices.Options.Count(o => o.NeedsSpeakerReview);
        var disclosureWarnings = choices.Options.Count(o => o.NeedsDisclosureReview);
        _relationshipStatus.Text = choices.RelationshipStatus +
            (fixedCount > 0 ? $" Corrected speaker wording in {fixedCount} draft(s)." : "") +
            (speakerWarnings > 0
                ? $" CHECK SPEAKER REFERENCES in {speakerWarnings} draft(s) before approving."
                : "") +
            (disclosureWarnings > 0
                ? $" {disclosureWarnings} candidate(s) have similar disclosures: edit or retry."
                : "");
        for (var i = 0; i < choices.Options.Count; i++)
        {
            _choiceTexts[i].Text = choices.Options[i].Text;
            _choiceTexts[i].Enabled = true;
        }
        _choiceChecks[0].Checked = true;
        foreach (var source in choices.RetrievedMemories)
            _sources.Items.Add(
                $"Memory #{source.Id} " +
                (source.WorldDay is int day
                    ? $"(Day {day}" +
                      (source.WorldTime is TimeOnly time ? $" {time:HH:mm}" : "") +
                      ") " : "(undated) ") +
                $"distance {source.CosineDistance:F3}: {source.Content}");

        _status.Text = choices.UsedEvidenceFallback
            ? "No on-topic evidence at this campaign time: prepared cautious " +
              "editable drafts without calling the chat model. Nothing saved."
            :
            $"Generated {choices.Options.Count} suggestions with {choices.ModelName}. " +
            (speakerWarnings > 0
                ? $"{speakerWarnings} draft(s) may incorrectly refer to the selected PC in third person; review before approval. "
                : fixedCount > 0
                    ? $"Corrected third-person speaker references in {fixedCount} draft(s). "
                    : "") +
            (disclosureWarnings > 0
                ? $"Check {disclosureWarnings} near-duplicate dialogue draft(s). "
                : "") +
            "Nothing saved.";
    }

    private void SelectChoice(int selected)
    {
        for (var i = 0; i < _choiceChecks.Length; i++)
        {
            if (i != selected) _choiceChecks[i].Checked = false;
        }
        if (selected >= 0) _manualChoice.Checked = false;
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

    private void DraftMemoryFromDialogue()
    {
        var player = _playerLine.Text.Trim();
        var reply = SelectedResponse();
        if (string.IsNullOrWhiteSpace(player) || string.IsNullOrWhiteSpace(reply))
        {
            MessageBox.Show("Enter player dialogue and choose an NPC reply first.",
                "Draft memory", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_newMemory.Text) &&
            MessageBox.Show("Replace the existing memory draft?", "Draft memory",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        // This describes speech only. It NEVER assumes an action (payment,
        // promises kept, gifts transferred, etc.) that the DM didn't confirm.
        _newMemory.Text =
            $"On {CurrentWorldMoment}, " +
            $"{_playerCharacters.SelectedItem ?? "someone"} said to me: \"{player}\". " +
            $"I answered: \"{reply}\".";
        _createMemoryOnApproval.Checked = true;
        _status.Text = "Memory drafted from dialogue. Edit it before approving; no data saved.";
    }

    private async Task ApproveAsync()
    {
        if (CampaignId is not long campaignId ||
            ReplyCharacterId is not long replyingId ||
            PlayerId is not long initiatingId)
            throw new InvalidOperationException(
                "Select two distinct characters from the conversation roster.");
        if (replyingId == initiatingId)
            throw new InvalidOperationException("The two speakers must be different.");
        var reply = SelectedResponse();
        if (string.IsNullOrWhiteSpace(_playerLine.Text) || string.IsNullOrWhiteSpace(reply))
            throw new InvalidOperationException("Enter player dialogue and choose or write a non-empty NPC response.");

        var withMemory = SelectedResponderIsNpc && _createMemoryOnApproval.Checked;
        if (withMemory && string.IsNullOrWhiteSpace(_newMemory.Text))
            throw new InvalidOperationException("Write the memory or uncheck the memory option.");

        var recipients = _giveListenerMemories.Checked
            ? MemoryRecipientIds().Where(id =>
                !_privateExchange.Checked || id == initiatingId).ToArray()
            : Array.Empty<long>();
        var notice =
            $"Approve dialogue on {CurrentWorldMoment}? " +
            (withMemory ? "Create the responding NPC's written memory. " :
                "No responding-NPC memory selected. ") +
            (recipients.Length > 0
                ? $"Also create {recipients.Length} attributed hearing memory/memories."
                : "No other character memories will be created.");
        if (MessageBox.Show(notice, "Approve NPC dialogue",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var result = await _database.RecordApprovedSceneAsync(
            campaignId, replyingId, initiatingId, _playerLine.Text, reply,
            CurrentWorldMoment, withMemory ? _newMemory.Text : null,
            (short)_newMemoryImportance.Value, _newMemorySecret.Checked,
            _activeConversationId, recipients,
            presentCharacters: _rosterIds, isPrivate: _privateExchange.Checked);

        var embeddedCount = 0;
        foreach (var memoryId in result.AllMemoryIds)
            if (await EmbedSavedMemoryAsync(campaignId, memoryId))
                embeddedCount++;

        if (NpcId == replyingId)
            await LoadMemoriesAsync(result.ReplyNpcMemoryId);
        await LoadConversationAsync();
        await LoadRosterHistoryAsync();
        await RefreshSavedSearchesAsync();
        _playerLine.Clear();
        _manualReply.Clear();
        _newMemory.Clear();
        _createMemoryOnApproval.Checked = false;
        _privateExchange.Checked = false;
        _allowPreparedLies.Checked = false;
        ResetSuggestions();
        _status.Text =
            $"Interaction #{result.InteractionId} approved on {CurrentWorldMoment}; " +
            $"{result.AllMemoryIds.Count} memories saved, {embeddedCount} embedded. " +
            "Historical words are recorded, not asserted true.";
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
        _relationshipStatus.Text =
            "Attitude: not evaluated. Select a player character and generate replies.";
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
            _deleteMemory.Enabled = _campaigns.Enabled = _npcs.Enabled =
            _saveProfile.Enabled = _saveAttitude.Enabled =
            _addNpcButton.Enabled = _suggestNpcOpening.Enabled = false;
        _playerLine.Enabled = _manualReply.Enabled = _newMemory.Enabled =
            _memoryEditor.Enabled = _memories.Enabled = _playerCharacters.Enabled =
            _newConversation.Enabled = _personalityEditor.Enabled =
            _speechEditor.Enabled = _mannerismsEditor.Enabled =
            _motivationsEditor.Enabled = _privateNotesEditor.Enabled =
            _attitudeEditor.Enabled = _disposition.Enabled = _attitudeTarget.Enabled =
            _aliasTarget.Enabled = _aliasInput.Enabled =
            _aliasAdd.Enabled = _aliasDelete.Enabled =
            _worldDay.Enabled = _worldTime.Enabled = _sceneTopic.Enabled =
            _memoryRecipients.Enabled = _giveListenerMemories.Enabled =
            _memoryUseWorldDate.Enabled = _campaignTree.Enabled =
            _treeFilter.Enabled = _conversationRoster.Enabled =
            _asking.Enabled = _answering.Enabled =
            _removeRosterMember.Enabled = _privateExchange.Enabled =
            _sheetSave.Enabled = _statementSearch.Enabled =
            _searchStatements.Enabled = _refreshStatements.Enabled =
            _statementSearchMode.Enabled = _embedStatements.Enabled =
            _memoryWorldDay.Enabled = _memoryWorldTime.Enabled =
            _addPcButton.Enabled = _addRootLocation.Enabled =
            _saveLocation.Enabled = _deleteLocation.Enabled =
            _allowPreparedLies.Enabled = _memoryKnowledgeStatus.Enabled =
            _memoryKnowledgeSource.Enabled =
            _memorySearchOwners.Enabled = _memorySearchOwnerFilter.Enabled =
            _memorySearchPhrase.Enabled = _memorySearchMode.Enabled =
            _memorySearchSecrets.Enabled = _memorySearchButton.Enabled =
            _memoryEmbedButton.Enabled = false;
        UseWaitCursor = true;
        try { await operation(); }
        catch (Exception ex)
        {
            _status.Text = "Operation failed. Check database state before retrying a save.";
            MessageBox.Show(ex.Message, "NPCodex", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _busy = false;
            _generate.Enabled = SelectedResponderIsNpc || ReplyAll;
            _approve.Enabled = ReplyCharacterId is not null &&
                PlayerId is not null && ReplyCharacterId != PlayerId;
            _saveMemory.Enabled = _deleteMemory.Enabled =
                _campaigns.Enabled = _npcs.Enabled = true;
            _saveProfile.Enabled = NpcId is not null;
            _addNpcButton.Enabled = CampaignId is not null;
            _saveAttitude.Enabled = NpcId is not null &&
                (_attitudeTarget.SelectedItem as CharacterChoice)?.Id is not null;
            _playerLine.Enabled = _manualReply.Enabled = _newMemory.Enabled =
                _memoryEditor.Enabled = _memories.Enabled = _playerCharacters.Enabled =
                _newConversation.Enabled = _personalityEditor.Enabled =
                _speechEditor.Enabled = _mannerismsEditor.Enabled =
                _motivationsEditor.Enabled = _privateNotesEditor.Enabled =
                _attitudeEditor.Enabled = _disposition.Enabled = _attitudeTarget.Enabled =
                _aliasTarget.Enabled = _aliasInput.Enabled =
                _aliasAdd.Enabled = _aliasDelete.Enabled =
            _worldDay.Enabled = _worldTime.Enabled = _sceneTopic.Enabled =
            _memoryRecipients.Enabled = _giveListenerMemories.Enabled =
            _memoryUseWorldDate.Enabled = _campaignTree.Enabled =
            _treeFilter.Enabled = _conversationRoster.Enabled =
            _asking.Enabled = _answering.Enabled =
            _removeRosterMember.Enabled = _privateExchange.Enabled =
            _sheetSave.Enabled = _statementSearch.Enabled =
            _searchStatements.Enabled = _refreshStatements.Enabled =
            _statementSearchMode.Enabled = _embedStatements.Enabled =
            _memoryWorldDay.Enabled = _memoryWorldTime.Enabled =
            _addPcButton.Enabled = _addRootLocation.Enabled = true;
            _saveLocation.Enabled = _deleteLocation.Enabled = _editLocationId is not null;
            _allowPreparedLies.Enabled = _memoryKnowledgeStatus.Enabled =
                _memoryKnowledgeSource.Enabled = true;
            _memorySearchOwners.Enabled = _memorySearchOwnerFilter.Enabled =
            _memorySearchPhrase.Enabled = _memorySearchMode.Enabled =
            _memorySearchSecrets.Enabled = _memorySearchButton.Enabled =
            _memoryEmbedButton.Enabled = true;
            _suggestNpcOpening.Enabled = CampaignId is not null;
            _createMemoryOnApproval.Enabled = SelectedResponderIsNpc;
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
            var when = Memory.WorldDay is int day
                ? $"[Day {day}" +
                  (Memory.WorldTime is TimeOnly time ? $" {time:HH:mm}" : "") +
                  "] "
                : "[Undated] ";
            return $"#{Memory.Id}  {when}" +
                   $"[{MemoryKnowledgeStatus.Label(Memory.KnowledgeStatus)}] " +
                   (Memory.IsSecret ? "[Secret] " : "") +
                   (Memory.HasEmbedding ? "[Embedded] " : "[Pending] ") +
                   description;
        }
    }
}
