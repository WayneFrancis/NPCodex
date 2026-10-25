using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly TabPage _aliasesTab = new("Character Aliases");
    private readonly ComboBox _aliasTarget = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox _aliasList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _aliasInput = new() { Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly Button _aliasAdd = new() { Text = "Add alias", AutoSize = true };
    private readonly Button _aliasDelete = new() { Text = "Delete selected alias", AutoSize = true };
    private bool _loadingAliases;

    private void BuildAliasesTab()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        _aliasesTab.Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = true,
            Text = "Create nicknames for existing PCs and NPCs. Aliases are not " +
                   "new characters; they link to the same identity. " +
                   "Names must be unambiguous within a campaign. " +
                   "Existing memories are never rewritten."
        }, 0, 0);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill,
            Text = "Character to edit", TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        layout.Controls.Add(_aliasTarget, 0, 2);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill,
            Text = "Saved aliases", TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        layout.Controls.Add(_aliasList, 0, 4);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_aliasInput, 0, 0);
        buttons.Controls.Add(_aliasAdd, 1, 0);
        buttons.Controls.Add(_aliasDelete, 2, 0);
        layout.Controls.Add(buttons, 0, 5);
    }

    private void WireAliasEvents()
    {
        _aliasTarget.SelectedIndexChanged += async (_, _) =>
        {
            if (!_loadingAliases && !_busy)
                await RunBusyAsync(LoadAliasesAsync);
        };
        _aliasAdd.Click += async (_, _) => await RunBusyAsync(AddAliasAsync);
        _aliasDelete.Click += async (_, _) => await RunBusyAsync(DeleteAliasAsync);
    }

    private void PopulateAliasTargets(long? preferredCharacterId = null)
    {
        preferredCharacterId ??= NpcId;
        _loadingAliases = true;
        try
        {
            _aliasTarget.Items.Clear();
            foreach (var person in _campaignCharacters)
                _aliasTarget.Items.Add(new CharacterChoice(
                    person.Id, $"{person.Name} ({person.Type.ToUpperInvariant()})"));
            _aliasTarget.SelectedItem = _aliasTarget.Items.Cast<CharacterChoice>()
                .FirstOrDefault(c => c.Id == preferredCharacterId)
                ?? _aliasTarget.Items.Cast<CharacterChoice>().FirstOrDefault();
        }
        finally { _loadingAliases = false; }
    }

    private async Task LoadAliasesAsync()
    {
        _aliasList.Items.Clear();
        _aliasInput.Clear();
        if (CampaignId is not long campaignId ||
            (_aliasTarget.SelectedItem as CharacterChoice)?.Id is not long characterId)
            return;

        var aliases = await _database.GetCharacterAliasesAsync(campaignId, characterId);
        foreach (var alias in aliases)
            _aliasList.Items.Add(new AliasChoice(alias));
        _status.Text = $"{aliases.Count} alias(es) for the selected character.";
    }

    private async Task AddAliasAsync()
    {
        if (CampaignId is not long campaignId ||
            (_aliasTarget.SelectedItem as CharacterChoice)?.Id is not long characterId)
            throw new InvalidOperationException("Select a character.");
        if (string.IsNullOrWhiteSpace(_aliasInput.Text))
            throw new InvalidOperationException("Type an alias before saving.");

        var alias = _aliasInput.Text.Trim();
        await _database.AddCharacterAliasAsync(campaignId, characterId, alias);
        await LoadAliasesAsync();
        ResetSuggestions();
        _status.Text = $"Saved alias '{alias}'. New dialogue can recognise it.";
    }

    private async Task DeleteAliasAsync()
    {
        if (CampaignId is not long campaignId ||
            (_aliasTarget.SelectedItem as CharacterChoice)?.Id is not long characterId ||
            _aliasList.SelectedItem is not AliasChoice choice)
            throw new InvalidOperationException("Select an existing alias.");
        if (MessageBox.Show(
            $"Delete alias '{choice.Value.Alias}'? This won't delete any character or memory.",
            "Delete alias", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (!await _database.DeleteCharacterAliasAsync(
            campaignId, characterId, choice.Value.Id))
            throw new InvalidOperationException("Alias was already deleted. Refresh.");

        await LoadAliasesAsync();
        ResetSuggestions();
        _status.Text = "Alias deleted. Saved interactions and memories are unchanged.";
    }

    private sealed record AliasChoice(CharacterAlias Value)
    {
        public override string ToString() => Value.Alias;
    }
}
