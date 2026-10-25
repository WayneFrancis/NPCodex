using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly Button _addNpcButton = new() { Text = "Add NPC", AutoSize = true };
    private readonly Button _addPcButton = new() { Text = "Add PC", AutoSize = true };

    private Task AddNpcAsync(long? preselectedLocationId = null) =>
        AddCharacterAsync("npc", preselectedLocationId);

    private Task AddPcAsync(long? preselectedLocationId = null) =>
        AddCharacterAsync("pc", preselectedLocationId);

    private async Task AddCharacterAsync(string type, long? locationId)
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign first.");
        if (type is not ("npc" or "pc"))
            throw new ArgumentException("Character type must be npc or pc.");

        using var dialog = new Form
        {
            Text = type == "npc" ? "Create D&D NPC" : "Create D&D Player Character",
            Size = new Size(490, 360),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12),
            ColumnCount = 2, RowCount = 7
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var name = new TextBox { Dock = DockStyle.Fill, MaxLength = 200 };
        var occupation = new TextBox { Dock = DockStyle.Fill, MaxLength = 200 };
        var species = new TextBox { Dock = DockStyle.Fill, MaxLength = 100 };
        var characterClass = new TextBox { Dock = DockStyle.Fill, MaxLength = 100 };
        var level = new NumericUpDown
        {
            Minimum = 1, Maximum = 20, Value = 1, Width = 90
        };

        Control[] entries = [name, occupation, species, characterClass, level];
        string[] labels = ["Name", "Occupation / role", "Species / ancestry", "Class", "Level"];
        for (var index = 0; index < entries.Length; index++)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            fields.Controls.Add(new Label
            {
                Text = labels[index], Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, index);
            fields.Controls.Add(entries[index], 1, index);
        }
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.Controls.Add(new Label
        {
            Text = locationId is long id
                ? $"Current location: {_campaignLocations.FirstOrDefault(l => l.Id == id)?.Name ?? "Selected location"}"
                : "Current location: Unassigned",
            AutoSize = true, Dock = DockStyle.Fill
        }, 1, 5);
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var commands = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        var confirm = new Button
        {
            Text = "Create", DialogResult = DialogResult.OK, AutoSize = true
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true
        };
        commands.Controls.Add(confirm);
        commands.Controls.Add(cancel);
        fields.Controls.Add(commands, 1, 6);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(fields);
        dialog.ActiveControl = name;
        AttachDictationToTextBoxes(dialog);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(name.Text))
            throw new InvalidOperationException("A character name is required.");

        var characterId = await _database.CreateCharacterAsync(
            campaignId, name.Text.Trim(), type,
            currentLocationId: locationId,
            occupation: string.IsNullOrWhiteSpace(occupation.Text)
                ? null : occupation.Text.Trim());

        // The character already exists at this point; a sheet failure must
        // not lead the DM to retry creating the same NPC unknowingly.
        try
        {
            await _database.SaveDndSheetAsync(campaignId, characterId,
                new DndCharacterSheet(
                    Species: species.Text.Trim(),
                    CharacterClass: characterClass.Text.Trim(),
                    Level: (int)level.Value));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Created {name.Text.Trim()}, but its character sheet did not save. " +
                $"Select the character to edit the sheet.\n\n{ex.Message}",
                "Character created; sheet pending",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        await LoadCharactersAsync();
        if (!_rosterIds.Contains(characterId)) _rosterIds.Add(characterId);
        RebuildConversationRoster();
        PopulateLocationTree();
        await LoadRosterHistoryAsync();

        if (type == "npc")
        {
            _ignoreSelectionEvents = true;
            try
            {
                _npcs.SelectedItem = _npcs.Items.Cast<CharacterChoice>()
                    .FirstOrDefault(c => c.Id == characterId);
            }
            finally { _ignoreSelectionEvents = false; }
            await LoadMemoriesAsync();
            await LoadPersonalityAsync();
        }

        await LoadDndCharacterSheetAsync(characterId);
        _status.Text = $"Created {type.ToUpperInvariant()} '{name.Text.Trim()}' " +
                       $"(#{characterId}) at " +
                       (locationId is long ? "the selected location." : "an unassigned location.");
    }
}
