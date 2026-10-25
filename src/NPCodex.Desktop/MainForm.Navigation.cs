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
        Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true,
        AllowDrop = true
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
    private readonly object _unassignedNodeTag = new();

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
        var editLocation = new ToolStripMenuItem("Edit location details...");
        editLocation.Click += (_, _) =>
        {
            if (_contextLocationNode?.Tag is LocationInfo location)
                LoadLocationDetails(location.Id, switchTab: true);
        };
        var deleteLocation = new ToolStripMenuItem("Delete location and children...");
        deleteLocation.Click += async (_, _) =>
        {
            if (_contextLocationNode?.Tag is LocationInfo location)
                await RunBusyAsync(() => DeleteTreeLocationAsync(location.Id));
        };
        menu.Items.Add(addNpc);
        menu.Items.Add(addPc);
        menu.Items.Add(addLocation);
        menu.Items.Add(move);
        menu.Items.Add(editLocation);
        menu.Items.Add(deleteLocation);
        menu.Opening += (_, _) =>
        {
            move.Visible = _contextLocationNode?.Tag is CharacterInfo;
            addLocation.Visible = _contextLocationNode?.Tag is LocationInfo;
            editLocation.Visible = deleteLocation.Visible =
                _contextLocationNode?.Tag is LocationInfo;
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
        WireLocationDragAndDrop();
        _campaignTree.AfterSelect += async (_, e) =>
        {
            if (_updatingTree || _busy) return;
            if (e.Node?.Tag is LocationInfo selectedLocation &&
                _tabs.SelectedTab == _locationsTab)
            {
                LoadLocationDetails(selectedLocation.Id);
                return;
            }
            if (e.Node?.Tag is CharacterInfo character &&
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
                e.Node?.Tag is CharacterInfo character)
                await AddToConversationAsync(character.Id);
        };
        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_busy) return;
            if (_tabs.SelectedTab == _locationsTab)
            {
                if (_campaignTree.SelectedNode?.Tag is LocationInfo location)
                    LoadLocationDetails(location.Id);
                return;
            }
            if (_tabs.SelectedTab != _memoriesTab &&
                _tabs.SelectedTab != _personalityTab &&
                _tabs.SelectedTab != _aliasesTab &&
                _tabs.SelectedTab != _characterSheetTab)
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
        _conversationRoster.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Delete) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (_busy || _conversationRoster.SelectedItem is not CharacterChoice choice ||
                choice.Id is not long id) return;
            // Keyboard removal deliberately has no confirmation; it does not
            // delete the character or any approved campaign records.
            _rosterIds.Remove(id);
            RebuildConversationRoster();
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
        // Build parents and children in two phases. Previously Sort() on a
        // partially built TreeView sometimes hid root/Unassigned nodes until
        // filtering. This version never sorts an already-populated tree.
        var filter = _treeFilter.Text.Trim();
        var selectedTag = _campaignTree.SelectedNode?.Tag;
        var expandedLocations = new HashSet<long>();
        foreach (TreeNode root in _campaignTree.Nodes)
            CaptureExpanded(root);

        void CaptureExpanded(TreeNode node)
        {
            if (node.IsExpanded && node.Tag is LocationInfo location)
                expandedLocations.Add(location.Id);
            foreach (TreeNode child in node.Nodes) CaptureExpanded(child);
        }

        var locations = _campaignLocations.ToDictionary(l => l.Id);
        bool Matches(string? value) =>
            filter.Length == 0 ||
            (!string.IsNullOrEmpty(value) &&
             value.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var directMatches = _campaignLocations
            .Where(l => Matches(l.Name) || Matches(l.Type))
            .Select(l => l.Id).ToHashSet();

        // If the filter matches a district or building, show its descendants.
        var included = new HashSet<long>(directMatches);
        var childrenByParent = _campaignLocations
            .Where(l => l.ParentLocationId is not null)
            .GroupBy(l => l.ParentLocationId!.Value)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var queue = new Queue<long>(directMatches);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parent, out var children)) continue;
            foreach (var child in children)
                if (included.Add(child.Id)) queue.Enqueue(child.Id);
        }

        var matchedCharacters = _campaignCharacters.Where(c =>
            Matches(c.Name) || Matches(c.Occupation) ||
            _treeAliases.Any(a => a.CharacterId == c.Id && Matches(a.Alias)) ||
            (c.CurrentLocationId is long id && included.Contains(id)))
            .ToArray();

        void IncludeAncestors(long id)
        {
            var visited = new HashSet<long>();
            while (locations.TryGetValue(id, out var current) && visited.Add(id))
            {
                included.Add(id);
                if (current.ParentLocationId is not long parentId) break;
                id = parentId;
            }
        }
        foreach (var id in included.ToArray()) IncludeAncestors(id);
        foreach (var c in matchedCharacters)
            if (c.CurrentLocationId is long id) IncludeAncestors(id);

        _updatingTree = true;
        try
        {
            _campaignTree.BeginUpdate();
            _campaignTree.Nodes.Clear();
            // Keep Unassigned first and permanent, regardless of filter or
            // whether the campaign currently has unplaced characters.
            var unassigned = new TreeNode("Unassigned")
            {
                Tag = _unassignedNodeTag,
                ToolTipText = "Drop characters here to clear their current location."
            };
            _campaignTree.Nodes.Add(unassigned);
            var nodes = new Dictionary<long, TreeNode>();
            foreach (var loc in _campaignLocations.Where(l => included.Contains(l.Id)))
                nodes[loc.Id] = new TreeNode(loc.Name)
                {
                    Tag = loc, ToolTipText = loc.Type
                };

            foreach (var loc in _campaignLocations.Where(l => nodes.ContainsKey(l.Id))
                         .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var node = nodes[loc.Id];
                if (loc.ParentLocationId is long parentId &&
                    nodes.TryGetValue(parentId, out var parent) &&
                    !WouldMakeTreeCycle(loc.Id, parentId, locations))
                    parent.Nodes.Add(node);
                else
                    _campaignTree.Nodes.Add(node);
            }

            bool WouldMakeTreeCycle(
                long childId, long parentId, Dictionary<long, LocationInfo> index)
            {
                var checkedParents = new HashSet<long>();
                while (checkedParents.Add(parentId) &&
                       index.TryGetValue(parentId, out var current))
                {
                    if (childId == parentId) return true;
                    if (current.ParentLocationId is not long next) return false;
                    parentId = next;
                }
                return true;
            }

            foreach (var character in matchedCharacters
                         .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var title = character.Name +
                    (string.IsNullOrWhiteSpace(character.Occupation)
                        ? "" : $" ({character.Occupation})");
                var node = new TreeNode(title)
                {
                    Tag = character, ToolTipText = character.Type.ToUpperInvariant()
                };
                if (character.CurrentLocationId is long locId &&
                    nodes.TryGetValue(locId, out var parent))
                    parent.Nodes.Add(node);
                else
                    unassigned.Nodes.Add(node);
            }

            if (filter.Length > 0)
                _campaignTree.ExpandAll();
            else
            {
                foreach (TreeNode root in _campaignTree.Nodes)
                    root.Expand();
                foreach (var id in expandedLocations)
                    if (nodes.TryGetValue(id, out var node)) node.Expand();
                unassigned.Expand();
            }

            TreeNode? toSelect = null;
            if (selectedTag is CharacterInfo selectedCharacter)
                toSelect = FindTreeNode(n => n.Tag is CharacterInfo c &&
                                            c.Id == selectedCharacter.Id);
            else if (selectedTag is LocationInfo selectedLocation)
                toSelect = FindTreeNode(n => n.Tag is LocationInfo l &&
                                            l.Id == selectedLocation.Id);
            else if (ReferenceEquals(selectedTag, _unassignedNodeTag))
                toSelect = unassigned;
            if (toSelect is not null)
                _campaignTree.SelectedNode = toSelect;
        }
        finally
        {
            _campaignTree.EndUpdate();
            _updatingTree = false;
        }
    }

    private TreeNode? FindTreeNode(Func<TreeNode, bool> predicate)
    {
        TreeNode? Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                if (predicate(node)) return node;
                var nested = Walk(node.Nodes);
                if (nested is not null) return nested;
            }
            return null;
        }
        return Walk(_campaignTree.Nodes);
    }

    private void SelectTreeLocation(long locationId)
    {
        var node = FindTreeNode(n => n.Tag is LocationInfo l && l.Id == locationId);
        if (node is null) return;
        var previous = _updatingTree;
        _updatingTree = true;
        try
        {
            _campaignTree.SelectedNode = node;
            node.EnsureVisible();
        }
        finally { _updatingTree = previous; }
    }

    private string LocationPath(long locationId)
    {
        var lookup = _campaignLocations.ToDictionary(l => l.Id);
        var names = new Stack<string>();
        var visited = new HashSet<long>();
        var current = (long?)locationId;
        while (current is long id && visited.Add(id) &&
               lookup.TryGetValue(id, out var location))
        {
            names.Push(location.Name);
            current = location.ParentLocationId;
        }
        return string.Join(" / ", names);
    }

    private async Task AddChildLocationAsync(long? parentId)
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign.");
        using var form = new Form
        {
            Text = "Add location", Size = new Size(540, 310),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var name = new TextBox { Left = 145, Top = 18, Width = 350 };
        var kind = new TextBox { Left = 145, Top = 57, Width = 350, Text = "place" };
        var parents = new ComboBox
        {
            Left = 145, Top = 96, Width = 350,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        parents.Items.Add(new CharacterChoice(null, "(Top-level location)"));
        foreach (var loc in _campaignLocations.OrderBy(x => x.Name))
            parents.Items.Add(new CharacterChoice(loc.Id, LocationPath(loc.Id)));
        parents.SelectedItem = parents.Items.Cast<CharacterChoice>()
            .FirstOrDefault(c => c.Id == parentId) ?? parents.Items[0];
        var description = new TextBox
        {
            Left = 145, Top = 135, Width = 350, Height = 65,
            Multiline = true, ScrollBars = ScrollBars.Vertical
        };
        var ok = new Button
        {
            Left = 395, Top = 217, Text = "Create",
            DialogResult = DialogResult.OK
        };
        form.Controls.AddRange([
            new Label { Text = "Name", Left = 15, Top = 22, Width = 125 },
            name,
            new Label { Text = "Type", Left = 15, Top = 61, Width = 125 },
            kind,
            new Label { Text = "Parent", Left = 15, Top = 100, Width = 125 },
            parents,
            new Label { Text = "Description", Left = 15, Top = 138, Width = 125 },
            description, ok
        ]);
        form.AcceptButton = ok;
        form.ActiveControl = name;
        if (form.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(name.Text))
            throw new InvalidOperationException("Enter a location name.");
        var createdId = await _database.CreateLocationAsync(
            campaignId, name.Text.Trim(),
            string.IsNullOrWhiteSpace(kind.Text) ? "place" : kind.Text.Trim(),
            (parents.SelectedItem as CharacterChoice)?.Id,
            string.IsNullOrWhiteSpace(description.Text) ? null : description.Text.Trim());
        await LoadCharactersAsync();
        // A new root must be visible immediately even if the current filter
        // would otherwise hide it.
        _treeFilter.Clear();
        SelectTreeLocation(createdId);
        _status.Text = $"Created location '{name.Text.Trim()}'.";
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
            destination.Items.Add(new CharacterChoice(
                location.Id, LocationPath(location.Id)));
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
        if (ReplyAll)
            _relationshipStatus.Text = "Question directed to everyone: the AI will choose an NPC present to answer.";
        else if (ReplyCharacterId is long answeringId &&
                 !SelectedResponderIsNpc)
        {
            var characterName = _campaignCharacters
                .FirstOrDefault(c => c.Id == answeringId)?.Name ?? "Selected character";
            _relationshipStatus.Text =
                $"{characterName} is a player character. AI replies are disabled. " +
                "Write the response in 'Or write your own dialogue', select it, then Approve.";
        }
        else if (SelectedResponderIsNpc)
            _relationshipStatus.Text =
                "NPC answering: Generate 3 replies is available, or write your own response.";
        else
            _relationshipStatus.Text =
                "Select an answering NPC to generate dialogue, or a player character to answer manually.";
        _suggestNpcOpening.Enabled = SelectedResponderIsNpc &&
            PlayerId is long speakerId && _campaignCharacters.Any(c =>
                c.Id == speakerId && c.Type == "npc");
    }
}
