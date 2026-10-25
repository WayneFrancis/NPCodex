using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly TabPage _locationsTab = new("Locations");
    private readonly Label _locationSelected = new()
    {
        AutoSize = true, Text = "Select a location in the tree."
    };
    private readonly TextBox _locationName = new() { Dock = DockStyle.Fill, MaxLength = 200 };
    private readonly TextBox _locationType = new() { Dock = DockStyle.Fill, MaxLength = 100 };
    private readonly ComboBox _locationParent = new()
    {
        Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox _locationDescription = new()
    {
        Dock = DockStyle.Fill, Multiline = true,
        ScrollBars = ScrollBars.Vertical
    };
    private readonly Button _saveLocation = new() { Text = "Save location changes", AutoSize = true };
    private readonly Button _deleteLocation = new() { Text = "Delete this location and children...", AutoSize = true };
    private long? _editLocationId;

    private void BuildLocationsTab()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16),
            ColumnCount = 2, RowCount = 7
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        _locationsTab.Controls.Add(layout);

        var nameLabels = new[]
        {
            "Selected location", "Name", "Type", "Parent location", "Description"
        };
        Control[] values =
        [
            _locationSelected, _locationName, _locationType,
            _locationParent, _locationDescription
        ];
        for (var i = 0; i < values.Length; i++)
        {
            layout.Controls.Add(new Label
            {
                Text = nameLabels[i], Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, i);
            layout.Controls.Add(values[i], 1, i);
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        buttons.Controls.Add(_saveLocation);
        buttons.Controls.Add(_deleteLocation);
        layout.Controls.Add(buttons, 1, 5);
        layout.Controls.Add(new Label
        {
            Text = "Location IDs are stable when renamed or moved. " +
                   "Previously written memories may still mention an old place name; " +
                   "they are not rewritten. A structured inventory of items " +
                   "found at a location is a future feature.",
            Dock = DockStyle.Fill, AutoEllipsis = true
        }, 1, 6);
    }

    private void WireLocationsTab()
    {
        _saveLocation.Click += async (_, _) =>
            await RunBusyAsync(SaveLocationDetailsAsync);
        _deleteLocation.Click += async (_, _) =>
        {
            if (_editLocationId is long id)
                await RunBusyAsync(() => DeleteTreeLocationAsync(id));
        };
    }

    private void LoadLocationDetails(long id, bool switchTab = false)
    {
        var location = _campaignLocations.FirstOrDefault(l => l.Id == id);
        if (location is null) return;
        _editLocationId = id;
        _locationSelected.Text = $"{location.Name}  (#{location.Id})";
        _locationName.Text = location.Name;
        _locationType.Text = location.Type;
        _locationDescription.Text = location.Description ?? "";
        _locationParent.Items.Clear();
        _locationParent.Items.Add(new CharacterChoice(null, "(Top-level location)"));

        var excluded = new HashSet<long> { id };
        // Exclude descendants so the dropdown cannot create a cycle.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var l in _campaignLocations)
                if (l.ParentLocationId is long parent && excluded.Contains(parent))
                    changed |= excluded.Add(l.Id);
        }
        foreach (var l in _campaignLocations.Where(l => !excluded.Contains(l.Id))
                     .OrderBy(l => l.Name))
            _locationParent.Items.Add(new CharacterChoice(l.Id, LocationPath(l.Id)));
        _locationParent.SelectedItem = _locationParent.Items.Cast<CharacterChoice>()
            .FirstOrDefault(item => item.Id == location.ParentLocationId)
            ?? _locationParent.Items[0];
        _saveLocation.Enabled = true;
        _deleteLocation.Enabled = true;
        if (switchTab) _tabs.SelectedTab = _locationsTab;
    }

    private async Task SaveLocationDetailsAsync()
    {
        if (CampaignId is not long campaignId || _editLocationId is not long locationId)
            throw new InvalidOperationException("Select a location in the tree first.");
        if (string.IsNullOrWhiteSpace(_locationName.Text))
            throw new InvalidOperationException("A location name is required.");
        var original = _campaignLocations.FirstOrDefault(l => l.Id == locationId)
            ?? throw new InvalidOperationException("Location no longer exists.");
        var parentId = (_locationParent.SelectedItem as CharacterChoice)?.Id;
        // Validate and move first. If it would create a cycle, do not rename.
        if (original.ParentLocationId != parentId)
            await _database.MoveLocationAsync(campaignId, locationId, parentId);
        await _database.UpdateLocationAsync(
            campaignId, locationId, _locationName.Text,
            string.IsNullOrWhiteSpace(_locationType.Text) ? "place" : _locationType.Text,
            _locationDescription.Text);
        await LoadCharactersAsync();
        _treeFilter.Clear();
        SelectTreeLocation(locationId);
        LoadLocationDetails(locationId);
        _status.Text = "Location saved. Existing memories retain their original wording.";
    }

    private async Task DeleteTreeLocationAsync(long locationId)
    {
        if (CampaignId is not long campaignId)
            throw new InvalidOperationException("Select a campaign first.");
        var location = _campaignLocations.FirstOrDefault(l => l.Id == locationId)
            ?? throw new InvalidOperationException("Location no longer exists.");
        var impact = await _database.GetLocationDeletionImpactAsync(campaignId, locationId);
        if (impact.Locations == 0)
            throw new InvalidOperationException("Location has already been deleted.");
        var message = $"Permanently delete '{location.Name}' and " +
                      $"{impact.Locations - 1} child/descendant location(s)?\n\n" +
                      $"{impact.AssignedCharacters} characters will become Unassigned.\n" +
                      $"{impact.Interactions} historical interactions will lose their " +
                      "location link. Dialogue, memories and characters are preserved.\n\n" +
                      "This cannot be undone. Move anything you want to keep first.";
        if (MessageBox.Show(message, "Delete location subtree permanently?",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var deleted = await _database.DeleteLocationSubtreeAsync(
            campaignId, locationId);
        _editLocationId = null;
        _locationSelected.Text = "No location selected";
        _locationName.Clear();
        _locationType.Clear();
        _locationDescription.Clear();
        _locationParent.Items.Clear();
        _saveLocation.Enabled = _deleteLocation.Enabled = false;
        await LoadCharactersAsync();
        _treeFilter.Clear();
        _status.Text = $"Deleted {deleted} location(s); affected characters are Unassigned.";
    }
}
