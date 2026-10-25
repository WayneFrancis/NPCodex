using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly TabPage _characterSheetTab = new("D&D Character Sheet");
    private readonly Label _sheetCharacter = new() { AutoSize = true, Text = "Select a character in the tree" };
    private readonly TextBox _sheetSpecies = new() { Width = 210 };
    private readonly TextBox _sheetOccupation = new() { Width = 310, MaxLength = 200 };
    private readonly TextBox _sheetClass = new() { Width = 210 };
    private readonly TextBox _sheetBackground = new() { Width = 210 };
    private readonly TextBox _sheetAlignment = new() { Width = 210 };
    private readonly NumericUpDown _sheetLevel = new() { Minimum = 1, Maximum = 20, Value = 1, Width = 75 };
    private readonly NumericUpDown[] _sheetAbilities =
        Enumerable.Range(0, 6).Select(_ => new NumericUpDown
        {
            Minimum = 1, Maximum = 30, Value = 10, Width = 75
        }).ToArray();
    private readonly NumericUpDown _sheetAC = new() { Minimum = 0, Maximum = 100, Value = 10, Width = 75 };
    private readonly NumericUpDown _sheetHP = new() { Minimum = 0, Maximum = 10000, Value = 1, Width = 75 };
    private readonly NumericUpDown _sheetSpeed = new() { Minimum = 0, Maximum = 1000, Value = 30, Width = 75 };
    private readonly TextBox _sheetFeatures = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill
    };
    private readonly TextBox _sheetEquipment = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill
    };
    private readonly TextBox _sheetNotes = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill
    };
    private readonly Button _sheetSave = new() { Text = "Save character sheet", AutoSize = true };
    private long? _sheetTargetId;

    private void BuildDndSheetTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true,
            Padding = new Padding(15), ColumnCount = 2
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _characterSheetTab.Controls.Add(root);

        var row = 0;
        void Add(string name, Control control, int height = 39)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.Controls.Add(new Label
            {
                Text = name, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, row);
            root.Controls.Add(control, 1, row++);
        }
        Add("Selected character", _sheetCharacter);
        Add("Occupation / role", _sheetOccupation);
        Add("Species / ancestry", _sheetSpecies);
        Add("Class", _sheetClass);
        Add("Level", _sheetLevel);
        Add("Background", _sheetBackground);
        Add("Alignment", _sheetAlignment);

        string[] abilities = ["STR", "DEX", "CON", "INT", "WIS", "CHA"];
        for (var i = 0; i < 6; i++)
        {
            var ability = _sheetAbilities[i];
            var modifier = new Label
            {
                AutoSize = true, Padding = new Padding(10, 7, 0, 0)
            };
            // Correct D&D floor division for scores below 10.
            void UpdateDndModifier(object? sender, EventArgs args) =>
                modifier.Text = $"Modifier: {Math.Floor(((int)ability.Value - 10) / 2.0):+0;-0;0}";
            ability.ValueChanged += UpdateDndModifier;
            UpdateDndModifier(null, EventArgs.Empty);
            var slot = new FlowLayoutPanel { Dock = DockStyle.Fill };
            slot.Controls.Add(ability);
            slot.Controls.Add(modifier);
            Add(abilities[i], slot);
        }

        Add("Armor Class", _sheetAC);
        Add("Hit Points", _sheetHP);
        Add("Speed (feet)", _sheetSpeed);
        Add("Features & traits", _sheetFeatures, 95);
        Add("Equipment", _sheetEquipment, 95);
        Add("DM notes", _sheetNotes, 100);
        Add("", _sheetSave, 45);
    }

    private void WireDndSheetEvents()
    {
        _sheetSave.Click += async (_, _) => await RunBusyAsync(SaveDndSheetAsync);
    }

    private async Task LoadDndCharacterSheetAsync(long characterId)
    {
        if (CampaignId is not long campaignId) return;
        var person = _campaignCharacters.FirstOrDefault(c => c.Id == characterId);
        if (person is null) return;
        var data = await _database.GetDndSheetAsync(campaignId, characterId);
        _sheetTargetId = characterId;
        _sheetCharacter.Text = $"{person.Name} ({person.Type.ToUpperInvariant()})";
        _sheetOccupation.Text = person.Occupation ?? "";
        _sheetSpecies.Text = data.Species;
        _sheetClass.Text = data.CharacterClass;
        _sheetLevel.Value = data.Level;
        _sheetBackground.Text = data.Background;
        _sheetAlignment.Text = data.Alignment;
        short[] scores = [
            data.Strength, data.Dexterity, data.Constitution,
            data.Intelligence, data.Wisdom, data.Charisma
        ];
        for (var i = 0; i < scores.Length; i++)
            _sheetAbilities[i].Value = scores[i];
        _sheetAC.Value = Math.Clamp(data.ArmorClass,
            (int)_sheetAC.Minimum, (int)_sheetAC.Maximum);
        _sheetHP.Value = Math.Clamp(data.HitPoints,
            (int)_sheetHP.Minimum, (int)_sheetHP.Maximum);
        _sheetSpeed.Value = Math.Clamp(data.SpeedFeet,
            (int)_sheetSpeed.Minimum, (int)_sheetSpeed.Maximum);
        _sheetFeatures.Text = data.Features;
        _sheetEquipment.Text = data.Equipment;
        _sheetNotes.Text = data.Notes;
    }

    private async Task SaveDndSheetAsync()
    {
        if (CampaignId is not long campaignId || _sheetTargetId is not long charId)
            throw new InvalidOperationException("Select a character in the location tree first.");
        var sheet = new DndCharacterSheet(
            _sheetSpecies.Text.Trim(), _sheetClass.Text.Trim(),
            (int)_sheetLevel.Value, _sheetBackground.Text.Trim(),
            _sheetAlignment.Text.Trim(),
            (short)_sheetAbilities[0].Value, (short)_sheetAbilities[1].Value,
            (short)_sheetAbilities[2].Value, (short)_sheetAbilities[3].Value,
            (short)_sheetAbilities[4].Value, (short)_sheetAbilities[5].Value,
            (int)_sheetAC.Value, (int)_sheetHP.Value, (int)_sheetSpeed.Value,
            _sheetFeatures.Text, _sheetEquipment.Text, _sheetNotes.Text);
        await _database.SaveDndSheetAsync(campaignId, charId, sheet);
        await _database.UpdateCharacterOccupationAsync(
            campaignId, charId, _sheetOccupation.Text);
        // Update the visible tree without reloading and clearing a draft
        // conversation that the DM may be writing on another tab.
        var occupation = string.IsNullOrWhiteSpace(_sheetOccupation.Text)
            ? null : _sheetOccupation.Text.Trim();
        _campaignCharacters = _campaignCharacters.Select(c =>
            c.Id == charId ? c with { Occupation = occupation } : c).ToArray();
        PopulateLocationTree();
        _status.Text =
            $"Saved D&D character sheet and occupation for {_sheetCharacter.Text}.";
    }
}
