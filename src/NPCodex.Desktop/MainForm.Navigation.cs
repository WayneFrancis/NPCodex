using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

/// <summary>Location browser is independent of the active conversation cast.</summary>
public sealed partial class MainForm
{
    private readonly TextBox _treeFilter = new()
    {
        Dock = DockStyle.Fill, PlaceholderText = "Filter locations or characters..."
    };
    private readonly TreeView _campaignTree = new()
    {
        Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true
    };
    private readonly ListBox _conversationRoster = new()
    {
        Dock = DockStyle.Fill, IntegralHeight = false
    };
    private readonly ComboBox _asking = new()
    {
        Width = 210, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly ComboBox _answering = new()
    {
        Width = 210, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Button _addRootLocation = new()
    {
        Text = "Add location", AutoSize = true
    };
    private readonly Button _removeRosterMember = new()
    {
        Text = "Remove", AutoSize = true
    };
    private readonly CheckBox _privateExchange = new()
    {
        Text = "Private / whispered — only the two speakers hear this exchange",
        AutoSize = true
    };

    private long? _rosterCampaignId;
    private readonly List<long> _rosterIds = [];
    private IReadOnlyList<LocationInfo> _campaignLocations = Array.Empty<LocationInfo>();
    private IReadOnlyList<CharacterAlias> _treeAliases = Array.Empty<CharacterAlias>();
    private bool _updatingCast;
    private bool _updatingTree;
    private TreeNode? _contextLocationNode;

    private long? ReplyCharacterId => (_answering.SelectedItem as CharacterChoice)?.Id;
    private bool ReplyAll => (_answering.SelectedItem as CharacterChoice)?.Name == "(All)";
    private bool SelectedResponderIsNpc =>
        ReplyCharacterId is long id && _campaignCharacters.Any(c => c.Id == id && c.Type == "npc");

    private Control BuildCampaignNavigation()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(8),
            ColumnCount = 1, RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        layout.Controls.Add(new Label
        {
            Text = "Campaign", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        layout.Controls.Add(_campaigns, 0, 1);
        layout.Controls.Add(_treeFilter, 0, 2);
        layout.Controls.Add(_campaignTree, 0, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += async (_, _) => await RunBusyAsync(LoadCampaignsAsync);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(_addNpcButton);
        buttons.Controls.Add(_addPcButton);
        buttons.Controls.Add(_addRootLocation);
        _addRootLocation.Click += async (_, _) =>
            await RunBusyAsync(() => AddChildLocationAsync(null));
        layout.Controls.Add(buttons, 0, 4);

        var menu = new ContextMenuStrip();
        var addNpc = new ToolStripMenuItem("Add NPC here");
        addNpc.Click += async (_, _) => await RunBusyAsync(async () =>
        {
            var locationId = _contextLocationNode?.Tag is LocationInfo l ? l.Id :
                _contextLocationNode?.Tag is CharacterInfo c ? c.CurrentLocationId : null;
            await AddNpcAsync(locationId);
        });
        var addLocation = new ToolStripMenuItem("Add child location");
        addLocation.Click += async (_, _) => await RunBusyAsync(async () =>
        {
            await AddChildLocationAsync(
                _contextLocationNode?.Tag is LocationInfo l ? l.Id : null);
        });
        var addPc = new ToolStripMenuItem("Add PC here");
        addPc.Click += async (_, _) => await RunBusyAsync(async () =>
        {
            var locationId = _contextLocationNode?.Tag is LocationInfo l ? l.Id :
                _contextLocationNode?.Tag is CharacterInfo c ? c.CurrentLocationId : null;
            await AddPcAsync(locationId);
        });
        var move = new ToolStripMenuItem("Move this character...");
        move.Click += async (_, _) => await RunBusyAsync(async () =>
        {
            if (_contextLocationNode?.Tag is CharacterInfo character)
                await MoveTreeCharacterAsync(character.Id);
        });
        menu.Items.Add(addNpc);
        menu.Items.Add(addPc);
        menu.Items.Add(addLocation);
        menu.Items.Add(move);
        menu.Opening += (_, _) =>
        {
            move.Visible = _contextLocationNode?.Tag is CharacterInfo;
        };
        _campaignTree.ContextMenuStrip = menu;
        _campaignTree.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right &&
                _campaignTree.GetNodeAt(e.Location) is null)
                _contextLocationNode = null;
        };
        _campaignTree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _contextLocationNode = e.Node;
                _campaignTree.SelectedNode = e.Node;
            }
        };
        return layout;
    }

    private void WireNavigationEvents()
    {
        _treeFilter.TextChanged += (_, _) => PopulateLocationTree();
        _campaignTree.AfterSelect += async (_, e) =>
        {
            if (_updatingTree || _busy) return;
            if (e.Node.Tag is CharacterInfo character &&
                (_tabs.SelectedTab == _memoriesTab ||
                 _tabs.SelectedTab == _personalityTab ||
                 _tabs.SelectedTab == _aliasesTab ||
                 _tabs.SelectedTab == _characterSheetTab))
                await RunBusyAsync(async () =>
                    await SelectEditableCharacterAsync(character.Id));
        };
        _campaignTree.NodeMouseDoubleClick += async (_, e) =>
        {
            if (_tabs.SelectedTab?.Text == "Dialogue" &&
                e.Node.Tag is CharacterInfo character)
                await AddToConversationAsync(character.Id);
        };
        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_busy || (_tabs.SelectedTab != _memoriesTab &&
                          _tabs.SelectedTab != _personalityTab &&
                          _tabs.SelectedTab != _aliasesTab &&
                          _tabs.SelectedTab != _characterSheetTab))
                return;
            if (_campaignTree.SelectedNode?.Tag is CharacterInfo character)
                await RunBusyAsync(async () =>
                    await SelectEditableCharacterAsync(character.Id));
        };
        _asking.SelectedIndexChanged += async (_, _) =>
        {
            if (_updatingCast || _busy) return;
            RepopulateResponders();
            await RunBusyAsync(async () =>
            {
                ResetDialogue();
                await LoadConversationAsync();
                await LoadRosterHistoryAsync();
            });
        };
        _answering.SelectedIndexChanged += async (_, _) =>
        {
            if (_updatingCast || _busy) return;
            UpdateRecipientOptions();
            ResetDialogue();
            await RunBusyAsync(async () =>
            {
                await LoadConversationAsync();
                await LoadRosterHistoryAsync();
            });
        };
        _removeRosterMember.Click += async (_, _) =>
        {
            if (_conversationRoster.SelectedItem is CharacterChoice choice &&
                choice.Id is long id)
            {
                _rosterIds.Remove(id);
                RebuildConversationRoster();
                await RunBusyAsync(async () =>
                {
                    await LoadConversationAsync();
                    await LoadRosterHistoryAsync();
                });
            }
        };
        _privateExchange.CheckedChanged += (_, _) =>
        {
            // Restore normal automatic hearing when leaving whisper mode.
            if (!_privateExchange.Checked) _memoryRecipients.Items.Clear();
            UpdateRecipientOptions();
        };
    }

    private async Task SelectEditableCharacterAsync(long id)
    {
        if (_campaignCharacters.All(c => c.Id != id)) return;
        if (_campaignCharacters.First(c => c.Id == id).Type != "npc")
        {
            PopulateAliasTargets(id);
            await LoadAliasesAsync();
            await LoadDndCharacterSheetAsync(id);
            return;
        }
        _ignoreSelectionEvents = true;
        try
        {
            _npcs.SelectedItem = _npcs.Items.Cast<CharacterChoice>()
                .FirstOrDefault(c => c.Id == id);
        }
        finally { _ignoreSelectionEvents = false; }
        await LoadMemoriesAsync();
        await LoadPersonalityAsync();
        PopulateAliasTargets();
        await LoadAliasesAsync();
        await LoadDndCharacterSheetAsync(id);
    }

    private void PopulateLocationTree()
    {
        var filter = _treeFilter.Text.Trim();
        var selected = (_campaignTree.SelectedNode?.Tag as CharacterInfo)?.Id;
        _updatingTree = true;
        try
        {
            _campaignTree.BeginUpdate();
            _campaignTree.Nodes.Clear();
            var locations = _campaignLocations.ToDictionary(l => l.Id);
            var children = new Dictionary<long, TreeNode>();
            bool Matches(string s) =>
                filter.Length == 0 || s.Contains(filter, StringComparison.OrdinalIgnoreCase);
            var matchedCharacters = _campaignCharacters
                .Where(c => Matches(c.Name) ||
                            (c.Occupation is not null && Matches(c.Occupation)) ||
                            _treeAliases.Any(a => a.CharacterId == c.Id && Matches(a.Alias)) ||
                            LocationAncestorsMatch(c.CurrentLocationId))
                .ToArray();

            bool LocationAncestorsMatch(long? locationId)
            {
                var seen = new HashSet<long>();
                while (locationId is long id && locations.TryGetValue(id, out var location)
                       && seen.Add(id))
                {
                    if (Matches(location.Name)) return true;
                    locationId = location.ParentLocationId;
                }
                return false;
            }

            var visibleLocations = new HashSet<long>();
            foreach (var loc in _campaignLocations.Where(l => Matches(l.Name)))
                AddAncestors(loc.Id);
            foreach (var person in matchedCharacters)
                if (person.CurrentLocationId is long locationId)
                    AddAncestors(locationId);

            void AddAncestors(long locationId)
            {
                var guard = new HashSet<long>();
                while (locations.TryGetValue(locationId, out var loc) && guard.Add(locationId))
                {
                    visibleLocations.Add(locationId);
                    if (loc.ParentLocationId is not long parentId) break;
                    locationId = parentId;
                }
            }

            TreeNode EnsureLocation(long id, HashSet<long>? visited = null)
            {
                if (children.TryGetValue(id, out var existing)) return existing;
                var loc = locations[id];
                var node = new TreeNode(loc.Name)
                {
                    Tag = loc, ToolTipText = loc.Type, ImageIndex = -1
                };
                children[id] = node;
                visited ??= [];
                if (loc.ParentLocationId is long parent &&
                    visibleLocations.Contains(parent) && locations.ContainsKey(parent) &&
                    visited.Add(id) && !visited.Contains(parent))
                    EnsureLocation(parent, visited).Nodes.Add(node);
                else
                    _campaignTree.Nodes.Add(node);
                return node;
            }
            foreach (var loc in _campaignLocations.Where(l => visibleLocations.Contains(l.Id))
                                                  .OrderBy(l => l.Name))
                EnsureLocation(loc.Id);

            TreeNode? unplaced = null;
            foreach (var person in matchedCharacters.OrderBy(c => c.Name))
            {
                var title = person.Name +
                    (string.IsNullOrWhiteSpace(person.Occupation)
                        ? "" : $" ({person.Occupation})");
                var node = new TreeNode(title) { Tag = person, ToolTipText = person.Type.ToUpperInvariant() };
                if (person.CurrentLocationId is long locId &&
                    children.TryGetValue(locId, out var parent))
                    parent.Nodes.Add(node);
                else
                {
                    if (unplaced is null)
                    {
                        unplaced = new TreeNode("Unassigned location");
                        _campaignTree.Nodes.Add(unplaced);
                    }
                    unplaced.Nodes.Add(node);
                }
                if (person.Id == selected) _campaignTree.SelectedNode = node;
            }
            _campaignTree.Sort();
            if (filter.Length > 0) _campaignTree.ExpandAll();
            else
                foreach (TreeNode node in _campaignTree.Nodes) node.Expand();
        }
        finally
        {
            _campaignTree.EndUpdate();
            _updatingTree = false;
        }
    }

    private async Task AddChildLocationAsync(long? parentId)
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign.");
        using var form = new Form
        {
            Text = "Add location", Size = new Size(430, 200),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog
        };
        var name = new TextBox { Left = 115, Top = 20, Width = 270 };
        var kind = new TextBox { Left = 115, Top = 60, Width = 270, Text = "place" };
        var ok = new Button { Left = 260, Top = 105, Text = "Create",
            DialogResult = DialogResult.OK };
        form.Controls.AddRange([
            new Label { Text = "Location", Left = 15, Top = 23, Width = 95 },
            name,
            new Label { Text = "Type", Left = 15, Top = 63, Width = 95 },
            kind, ok
        ]);
        form.AcceptButton = ok;
        if (form.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(name.Text))
            throw new InvalidOperationException("Enter a location name.");
        await _database.CreateLocationAsync(
            campaignId, name.Text, string.IsNullOrWhiteSpace(kind.Text)
                ? "place" : kind.Text, parentId);
        await LoadCharactersAsync();
    }

    private async Task MoveTreeCharacterAsync(long characterId)
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign first.");
        using var dialog = new Form
        {
            Text = "Move character to a location",
            Size = new Size(495, 170),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent
        };
        var destination = new ComboBox
        {
            Left = 13, Top = 14, Width = 450,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        destination.Items.Add(new CharacterChoice(null, "(Unassigned location)"));
        foreach (var location in _campaignLocations.OrderBy(l => l.Name))
            destination.Items.Add(new CharacterChoice(location.Id, location.Name));
        var oldLocationId = _campaignCharacters
            .FirstOrDefault(c => c.Id == characterId)?.CurrentLocationId;
        destination.SelectedItem = destination.Items.Cast<CharacterChoice>()
            .FirstOrDefault(c => c.Id == oldLocationId)
            ?? destination.Items[0];
        var ok = new Button
        {
            Text = "Move", Left = 370, Top = 58,
            DialogResult = DialogResult.OK
        };
        dialog.Controls.AddRange([destination, ok]);
        dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await _database.MoveCharacterAsync(
            campaignId, characterId,
            (destination.SelectedItem as CharacterChoice)?.Id);
        await LoadCharactersAsync();
        _status.Text = "Character location updated in this campaign.";
    }

    private Control BuildConversationHeader()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 39));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 61));
        var cast = new GroupBox { Text = "Characters present — double-click tree to add", Dock = DockStyle.Fill };
        var castLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        castLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        castLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        castLayout.Controls.Add(_conversationRoster, 0, 0);
        castLayout.Controls.Add(_removeRosterMember, 1, 0);
        cast.Controls.Add(castLayout);
        layout.Controls.Add(cast, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var selectors = new FlowLayoutPanel { Dock = DockStyle.Fill };
        selectors.Controls.Add(new Label
        {
            Text = "Asking", AutoSize = true, Padding = new Padding(0, 8, 0, 0)
        });
        selectors.Controls.Add(_asking);
        selectors.Controls.Add(new Label
        {
            Text = "Answering", AutoSize = true, Padding = new Padding(8, 8, 0, 0)
        });
        selectors.Controls.Add(_answering);
        right.Controls.Add(selectors, 0, 0);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill };
        bar.Controls.Add(_newConversation);
        bar.Controls.Add(_conversationLabel);
        right.Controls.Add(bar, 0, 1);
        right.Controls.Add(new Label
        {
            Text = "For a PC answer, write the reply manually. '(All)' chooses an NPC " +
                   "from those present for AI generation.",
            Dock = DockStyle.Fill
        }, 0, 2);
        layout.Controls.Add(right, 1, 0);
        return layout;
    }

    private async Task AddToConversationAsync(long characterId)
    {
        if (_campaignCharacters.All(c => c.Id != characterId)) return;
        if (!_rosterIds.Contains(characterId))
            _rosterIds.Add(characterId);
        RebuildConversationRoster();
        await RunBusyAsync(async () =>
        {
            await LoadConversationAsync();
            await LoadRosterHistoryAsync();
        });
    }

    private void RebuildConversationRoster()
    {
        _updatingCast = true;
        var priorAsking = PlayerId;
        var priorAnswering = ReplyCharacterId;
        try
        {
            _conversationRoster.Items.Clear();
            foreach (var id in _rosterIds.ToArray())
            {
                var person = _campaignCharacters.FirstOrDefault(c => c.Id == id);
                if (person is null) { _rosterIds.Remove(id); continue; }
                _conversationRoster.Items.Add(new CharacterChoice(
                    id, $"{person.Name} ({person.Type.ToUpperInvariant()})"));
            }
            _asking.Items.Clear();
            foreach (CharacterChoice person in _conversationRoster.Items)
                _asking.Items.Add(person);
            _asking.SelectedItem = _asking.Items.Cast<CharacterChoice>()
                .FirstOrDefault(c => c.Id == priorAsking)
                ?? _asking.Items.Cast<CharacterChoice>().FirstOrDefault();
            RepopulateResponders(priorAnswering);
        }
        finally { _updatingCast = false; }
        UpdateRecipientOptions();
    }

    private void RepopulateResponders(long? previous = null)
    {
        previous ??= ReplyCharacterId;
        var previousAll = ReplyAll;
        var speakerId = PlayerId;
        _updatingCast = true;
        try
        {
            _answering.Items.Clear();
            foreach (CharacterChoice choice in _conversationRoster.Items)
                if (choice.Id != speakerId)
                    _answering.Items.Add(choice);

            if (_conversationRoster.Items.Count > 2 &&
                _answering.Items.Cast<CharacterChoice>().Any(c =>
                    _campaignCharacters.Any(p => p.Id == c.Id && p.Type == "npc")))
                _answering.Items.Add(new CharacterChoice(null, "(All)"));

            _answering.SelectedItem = previousAll
                ? _answering.Items.Cast<CharacterChoice>()
                    .FirstOrDefault(c => c.Name == "(All)")
                : _answering.Items.Cast<CharacterChoice>()
                    .FirstOrDefault(c => c.Id == previous);
            _answering.SelectedItem ??= _answering.Items.Cast<CharacterChoice>()
                .FirstOrDefault();
        }
        finally { _updatingCast = false; }
        UpdateRecipientOptions();
    }

    private void UpdateRecipientOptions()
    {
        var existingChecks = _memoryRecipients.CheckedItems.Cast<CharacterChoice>()
            .Where(c => c.Id is long).Select(c => c.Id!.Value).ToHashSet();
        // The roster is the complete list of those present. Everyone hears
        // normal exchanges. Private exchanges include only the two speakers.
        var earlierRecipients = _memoryRecipients.Items.Cast<CharacterChoice>()
            .Where(c => c.Id is long).Select(c => c.Id!.Value).ToHashSet();
        _memoryRecipients.Items.Clear();
        foreach (CharacterChoice person in _conversationRoster.Items)
        {
            if (person.Id == ReplyCharacterId) continue;
            var canHear = !_privateExchange.Checked || person.Id == PlayerId;
            _memoryRecipients.Items.Add(person,
                canHear && (!earlierRecipients.Contains(person.Id!.Value) ||
                            existingChecks.Contains(person.Id!.Value)));
        }
        _memoryRecipients.Enabled = !_privateExchange.Checked;
        _giveListenerMemories.Checked = true;
        _createMemoryOnApproval.Enabled = SelectedResponderIsNpc;
        if (!SelectedResponderIsNpc) _createMemoryOnApproval.Checked = false;
        _generate.Enabled = SelectedResponderIsNpc || ReplyAll;
        _suggestNpcOpening.Enabled = SelectedResponderIsNpc &&
            PlayerId is long speakerId && _campaignCharacters.Any(c =>
                c.Id == speakerId && c.Type == "npc");
    }
}
