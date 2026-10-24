using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly TabPage _personalityTab = new("Personality & Relationships");

    private readonly TextBox _personalityEditor = CreateProfileEditor(1000);
    private readonly TextBox _speechEditor = CreateProfileEditor(1000);
    private readonly TextBox _mannerismsEditor = CreateProfileEditor(1000);
    private readonly TextBox _motivationsEditor = CreateProfileEditor(1000);
    private readonly TextBox _privateNotesEditor = CreateProfileEditor(4000);
    private readonly Button _saveProfile = new() { Text = "Save NPC personality", AutoSize = true };

    private readonly Label _attitudeTarget = new()
    {
        AutoSize = true,
        Text = "Select a player character from the left sidebar."
    };
    private readonly NumericUpDown _disposition = new()
    {
        Minimum = -100, Maximum = 100, Value = 0, Width = 92
    };
    private readonly TextBox _attitudeEditor = CreateProfileEditor(1200);
    private readonly CheckBox _attitudeSecret = new()
    {
        AutoSize = true,
        Text = "Private opinion — do not send to Qwen"
    };
    private readonly Button _saveAttitude = new()
    {
        Text = "Save NPC opinion of player",
        AutoSize = true
    };

    private static TextBox CreateProfileEditor(int limit) => new()
    {
        Multiline = true, Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Vertical, MaxLength = limit
    };

    private void BuildPersonalityTab()
    {
        var sections = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8),
            AutoScroll = true
        };
        sections.RowStyles.Add(new RowStyle(SizeType.Absolute, 548));
        sections.RowStyles.Add(new RowStyle(SizeType.Absolute, 237));
        _personalityTab.Controls.Add(sections);

        var profileGroup = new GroupBox
        {
            Text = "Consistent NPC characterisation",
            Dock = DockStyle.Fill
        };
        sections.Controls.Add(profileGroup, 0, 0);

        var profileGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 2,
            RowCount = 6
        };
        profileGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        profileGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 5; row++)
            profileGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        profileGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));

        AddProfileField(profileGrid, 0, "Personality traits", _personalityEditor,
            "Practical, observant, cautious with strangers...");
        AddProfileField(profileGrid, 1, "Speaking style", _speechEditor,
            "Short sentences, dry wit, direct when discussing money...");
        AddProfileField(profileGrid, 2, "Mannerisms", _mannerismsEditor,
            "Checks a ledger; folds hands while listening...");
        AddProfileField(profileGrid, 3, "Motivations", _motivationsEditor,
            "Wants to maintain a reputable shop and reliable trade...");
        AddProfileField(profileGrid, 4, "Private DM notes", _privateNotesEditor,
            "Never sent to the AI. Use for undisclosed secrets or DM planning.");

        profileGrid.Controls.Add(_saveProfile, 1, 5);
        profileGroup.Controls.Add(profileGrid);

        var attitudeGroup = new GroupBox
        {
            Text = "Directed opinion of selected player character",
            Dock = DockStyle.Fill
        };
        sections.Controls.Add(attitudeGroup, 0, 1);
        var attitudeGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 2,
            RowCount = 4
        };
        attitudeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        attitudeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        attitudeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        attitudeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        attitudeGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        attitudeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        attitudeGrid.Controls.Add(ProfileLabel("Target character"), 0, 0);
        attitudeGrid.Controls.Add(_attitudeTarget, 1, 0);
        attitudeGrid.Controls.Add(ProfileLabel("Disposition (-100 to +100)"), 0, 1);
        attitudeGrid.Controls.Add(_disposition, 1, 1);
        attitudeGrid.Controls.Add(ProfileLabel("NPC's opinion / history"), 0, 2);
        attitudeGrid.Controls.Add(_attitudeEditor, 1, 2);

        var attitudeActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        attitudeActions.Controls.Add(_attitudeSecret);
        attitudeActions.Controls.Add(_saveAttitude);
        attitudeGrid.Controls.Add(attitudeActions, 1, 3);
        attitudeGroup.Controls.Add(attitudeGrid);
    }

    private static Label ProfileLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static void AddProfileField(
        TableLayoutPanel grid, int row, string caption, TextBox editor,
        string hint)
    {
        grid.Controls.Add(ProfileLabel(caption), 0, row);
        grid.Controls.Add(editor, 1, row);
        var tips = new ToolTip();
        tips.SetToolTip(editor, hint);
    }

    private void WirePersonalityEvents()
    {
        _saveProfile.Click += async (_, _) =>
            await RunBusyAsync(SavePersonalityAsync);
        _saveAttitude.Click += async (_, _) =>
            await RunBusyAsync(SaveAttitudeAsync);
    }

    private async Task LoadPersonalityAsync()
    {
        _personalityEditor.Clear();
        _speechEditor.Clear();
        _mannerismsEditor.Clear();
        _motivationsEditor.Clear();
        _privateNotesEditor.Clear();

        if (CampaignId is long campaignId && NpcId is long npcId)
        {
            var profile = await _database.GetNpcPersonalityAsync(campaignId, npcId);
            _personalityEditor.Text = profile.PersonalityTraits;
            _speechEditor.Text = profile.SpeakingStyle;
            _mannerismsEditor.Text = profile.Mannerisms;
            _motivationsEditor.Text = profile.Motivations;
            _privateNotesEditor.Text = profile.PrivateDmNotes;
        }

        await LoadAttitudeAsync();
    }

    private async Task LoadAttitudeAsync()
    {
        _disposition.Value = 0;
        _attitudeEditor.Clear();
        _attitudeSecret.Checked = false;
        _attitudeTarget.Text = "Select a player character from the left sidebar.";

        if (CampaignId is not long campaignId || NpcId is not long npcId ||
            PlayerId is not long pcId)
        {
            _saveAttitude.Enabled = false;
            return;
        }

        var target = _playerCharacters.SelectedItem as CharacterChoice;
        _attitudeTarget.Text = target?.Name ?? $"Character #{pcId}";

        var attitude = await _database.GetNpcAttitudeAsync(campaignId, npcId, pcId);
        if (attitude is not null)
        {
            _disposition.Value = attitude.Disposition;
            _attitudeEditor.Text = attitude.Description;
            _attitudeSecret.Checked = attitude.IsSecret;
        }
        _saveAttitude.Enabled = true;
    }

    private async Task SavePersonalityAsync()
    {
        if (CampaignId is not long campaignId || NpcId is not long npcId)
            throw new InvalidOperationException("Select an NPC first.");

        await _database.SaveNpcPersonalityAsync(campaignId, npcId,
            new NpcPersonality(
                _personalityEditor.Text, _speechEditor.Text,
                _mannerismsEditor.Text, _motivationsEditor.Text,
                _privateNotesEditor.Text));

        // Existing generated alternatives are now stale; never let the DM
        // accidentally accept a draft based on an old personality.
        ResetSuggestions();
        _status.Text = "NPC personality saved. Regenerate replies to use the new profile.";
    }

    private async Task SaveAttitudeAsync()
    {
        if (CampaignId is not long campaignId || NpcId is not long npcId ||
            PlayerId is not long playerId)
            throw new InvalidOperationException(
                "Select an NPC and a player character to record an opinion.");

        await _database.SaveNpcAttitudeAsync(
            campaignId, npcId, playerId,
            (short)_disposition.Value, _attitudeEditor.Text, _attitudeSecret.Checked);
        ResetSuggestions();
        _status.Text = "NPC opinion saved. New dialogue uses it unless marked private.";
    }
}
