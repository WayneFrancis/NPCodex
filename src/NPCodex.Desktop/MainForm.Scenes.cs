using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly CheckBox _memoryUseWorldDate = new()
    {
        Text = "Memory has a known in-world date/time",
        AutoSize = true,
        Checked = true
    };

    private readonly NumericUpDown _worldDay = new()
    {
        Minimum = 1, Maximum = 1_000_000, Value = 1, Width = 88
    };

    private readonly DateTimePicker _worldTime = new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "HH:mm",
        ShowUpDown = true,
        Width = 85,
        Value = DateTime.Today.AddHours(9)
    };

    private readonly TextBox _sceneTopic = new()
    {
        Width = 175,
        PlaceholderText = "NPC opening topic (optional)",
        MaxLength = 500
    };
    private readonly Button _suggestNpcOpening = new()
    {
        Text = "Suggest NPC opening", AutoSize = true
    };
    private readonly CheckedListBox _memoryRecipients = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false
    };
    private readonly CheckBox _giveListenerMemories = new()
    {
        AutoSize = true,
        Text = "Give checked characters their own memory of what they heard"
    };
    private readonly Label _worldHelp = new()
    {
        AutoSize = true,
        Text = "Campaign day/time, not real-world calendar date"
    };

    private WorldMoment CurrentWorldMoment =>
        new((int)_worldDay.Value, TimeOnly.FromDateTime(_worldTime.Value));

    private IReadOnlyList<long> MemoryRecipientIds()
    {
        return _memoryRecipients.CheckedItems.Cast<CharacterChoice>()
            .Where(c => c.Id is not null)
            .Select(c => c.Id!.Value)
            .ToArray();
    }

    private Control CreateSceneControls()
    {
        var frame = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "In-world time / NPC scene / people who heard the exchange"
        };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(6)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        frame.Controls.Add(grid);

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        var clock = new FlowLayoutPanel { Width = 480, Height = 35 };
        clock.Controls.Add(new Label { Text = "Campaign day", AutoSize = true,
            Padding = new Padding(0, 6, 0, 0) });
        clock.Controls.Add(_worldDay);
        clock.Controls.Add(new Label { Text = "Time", AutoSize = true,
            Padding = new Padding(10, 6, 0, 0) });
        clock.Controls.Add(_worldTime);
        left.Controls.Add(clock);
        left.Controls.Add(_worldHelp);
        var opening = new FlowLayoutPanel { Width = 490, Height = 38 };
        opening.Controls.Add(_sceneTopic);
        opening.Controls.Add(_suggestNpcOpening);
        left.Controls.Add(opening);
        left.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(485, 0),
            Text = "Select another NPC as the initiating speaker. " +
                   "Suggest opening drafts that NPC's first line; approve the " +
                   "exchange before any interaction or memory is saved."
        });
        grid.Controls.Add(left, 0, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(_giveListenerMemories, 0, 0);
        right.Controls.Add(_memoryRecipients, 0, 1);
        grid.Controls.Add(right, 1, 0);
        return frame;
    }

    private void WireSceneEvents()
    {
        _suggestNpcOpening.Click += async (_, _) =>
            await RunBusyAsync(SuggestNpcOpeningAsync);
    }

    private void PopulateMemoryRecipients()
    {
        var checkedIds = _memoryRecipients.CheckedItems.Cast<CharacterChoice>()
            .Where(c => c.Id.HasValue)
            .Select(c => c.Id!.Value).ToHashSet();
        _memoryRecipients.Items.Clear();
        foreach (var c in _campaignCharacters.Where(c => c.Id != NpcId))
        {
            var item = new CharacterChoice(c.Id, $"{c.Name} ({c.Type.ToUpperInvariant()})");
            _memoryRecipients.Items.Add(item, checkedIds.Contains(c.Id));
        }
    }

    private void RefreshInitiators(long? preferredId = null)
    {
        preferredId ??= PlayerId;
        _ignoreSelectionEvents = true;
        try
        {
            _playerCharacters.Items.Clear();
            _playerCharacters.Items.Add(new CharacterChoice(
                null, "(No selected initiating character)"));
            foreach (var c in _campaignCharacters.Where(c => c.Id != NpcId))
                _playerCharacters.Items.Add(new CharacterChoice(
                    c.Id, $"{c.Name} ({c.Type.ToUpperInvariant()})"));
            _playerCharacters.SelectedItem =
                _playerCharacters.Items.Cast<CharacterChoice>()
                    .FirstOrDefault(c => c.Id == preferredId)
                ?? _playerCharacters.Items[0];
        }
        finally { _ignoreSelectionEvents = false; }
        PopulateMemoryRecipients();
    }

    private async Task SuggestNpcOpeningAsync()
    {
        if (CampaignId is not long campaignId || NpcId is not long npcId ||
            PlayerId is not long initiatingId)
            throw new InvalidOperationException(
                "Select the responding NPC and a different initiating NPC.");
        if (_campaignCharacters.FirstOrDefault(c => c.Id == initiatingId)?.Type != "npc")
            throw new InvalidOperationException(
                "Suggest NPC opening requires an NPC as the initiating speaker.");
        if (!string.IsNullOrWhiteSpace(_playerLine.Text) &&
            MessageBox.Show(
                "Replace the currently typed opening line with an unsaved AI suggestion?",
                "Replace opening?", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var line = await _dialogueService.SuggestNpcOpeningAsync(
            campaignId, initiatingId, npcId,
            _sceneTopic.Text, CurrentWorldMoment);
        _playerLine.Text = line;
        ResetSuggestions();
        _status.Text = "Generated an unsaved NPC opening. Edit it and generate responses.";
    }
}
