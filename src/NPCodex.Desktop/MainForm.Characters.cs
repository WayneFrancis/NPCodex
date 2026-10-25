using System.Drawing;
using System.Windows.Forms;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly Button _addNpcButton = new() { Text = "Add NPC", AutoSize = true };

    private async Task AddNpcAsync(long? preselectedLocationId = null)
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign before creating an NPC.");

        using var dialog = new Form
        {
            Text = "Create NPC", Size = new Size(430, 220),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false
        };
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3,
            Padding = new Padding(12)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var name = new TextBox { Dock = DockStyle.Fill, MaxLength = 200 };
        var occupation = new TextBox { Dock = DockStyle.Fill, MaxLength = 200 };
        panel.Controls.Add(new Label { Text = "Name", Dock = DockStyle.Fill }, 0, 0);
        panel.Controls.Add(name, 1, 0);
        panel.Controls.Add(new Label { Text = "Occupation", Dock = DockStyle.Fill }, 0, 1);
        panel.Controls.Add(occupation, 1, 1);
        var commands = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        var confirm = new Button { Text = "Create", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        commands.Controls.Add(confirm);
        commands.Controls.Add(cancel);
        panel.Controls.Add(commands, 1, 2);
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(panel);
        dialog.ActiveControl = name;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(name.Text))
            throw new InvalidOperationException("An NPC name is required.");

        var npcId = await _database.CreateCharacterAsync(
            campaignId, name.Text.Trim(), "npc",
            currentLocationId: preselectedLocationId,
            occupation: string.IsNullOrWhiteSpace(occupation.Text)
                ? null : occupation.Text.Trim());

        await LoadCharactersAsync();
        if (!_rosterIds.Contains(npcId)) _rosterIds.Add(npcId);
        RebuildConversationRoster();
        PopulateLocationTree();
        _ignoreSelectionEvents = true;
        try
        {
            _npcs.SelectedItem = _npcs.Items.Cast<CharacterChoice>()
                .FirstOrDefault(c => c.Id == npcId);
        }
        finally { _ignoreSelectionEvents = false; }

        ResetDialogue();
        await LoadMemoriesAsync();
        await LoadConversationAsync();
        await LoadPersonalityAsync();
        PopulateAliasTargets();
        await LoadAliasesAsync();
        _status.Text = $"Created NPC '{name.Text.Trim()}' (#{npcId}).";
    }
}
