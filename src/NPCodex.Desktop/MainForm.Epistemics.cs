using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private readonly ComboBox _memoryKnowledgeStatus = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240
    };
    private readonly TextBox _memoryKnowledgeSource = new()
    {
        Width = 270, MaxLength = 300,
        PlaceholderText = "Source / who told them (optional)"
    };
    private readonly CheckBox _allowPreparedLies = new()
    {
        AutoSize = true,
        Text = "Permit DM-authored prepared lies in generated replies",
        Checked = false
    };

    private sealed record KnowledgeChoice(string Code, string Title)
    {
        public override string ToString() => Title;
    }

    private Control CreateMemoryKnowledgeControls()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false
        };
        row.Controls.Add(new Label
        {
            Text = "What kind of knowledge?", AutoSize = true,
            Padding = new Padding(0, 7, 0, 0)
        });
        foreach (var choice in MemoryKnowledgeStatus.Options)
            _memoryKnowledgeStatus.Items.Add(
                new KnowledgeChoice(choice.Code, choice.Label));
        row.Controls.Add(_memoryKnowledgeStatus);
        row.Controls.Add(new Label
        {
            Text = "Source:", AutoSize = true,
            Padding = new Padding(12, 7, 0, 0)
        });
        row.Controls.Add(_memoryKnowledgeSource);
        SetSelectedMemoryKnowledgeStatus(MemoryKnowledgeStatus.Recollection);
        return row;
    }

    private string SelectedMemoryKnowledgeStatus =>
        (_memoryKnowledgeStatus.SelectedItem as KnowledgeChoice)?.Code
            ?? MemoryKnowledgeStatus.Recollection;

    private void SetSelectedMemoryKnowledgeStatus(string status)
    {
        _memoryKnowledgeStatus.SelectedItem =
            _memoryKnowledgeStatus.Items.Cast<KnowledgeChoice>()
                .FirstOrDefault(c => c.Code == status)
            ?? _memoryKnowledgeStatus.Items.Cast<KnowledgeChoice>()
                .FirstOrDefault(c => c.Code == MemoryKnowledgeStatus.Recollection);
    }

    private void WireEpistemicEvents()
    {
        _allowPreparedLies.CheckedChanged += (_, _) =>
        {
            if (!_busy) ResetSuggestions();
        };
        _memoryKnowledgeStatus.SelectedIndexChanged += (_, _) =>
        {
            if (SelectedMemoryKnowledgeStatus == MemoryKnowledgeStatus.CoverStory)
                _status.Text = "Prepared lies must be explicitly authored by the DM. " +
                    "They are excluded from AI generation unless permitted on Dialogue.";
        };
    }
}
