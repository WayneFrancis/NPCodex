using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private async Task EditConversationMomentAsync(long statementId)
    {
        if (_busy || CampaignId is not long campaignId) return;
        await RunBusyAsync(async () =>
        {
            var original = await _database.GetConversationMomentAsync(
                campaignId, statementId);
            if (original is null)
                throw new InvalidOperationException(
                    "Statement no longer exists. Refresh the conversation view.");

            using var editor = new Form
            {
                Text = $"Correct exchange #{original.InteractionId} date/time",
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(720, 365),
                MinimumSize = new Size(600, 350),
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = false, MinimizeBox = false
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(12),
                ColumnCount = 1, RowCount = 4
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            editor.Controls.Add(layout);

            var current = original.WorldDay is int oldDay
                ? $"Day {oldDay}" + (original.WorldTime is TimeOnly oldTime
                    ? $" at {oldTime:HH:mm}" : " (time unknown)")
                : "Undated";
            layout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = $"Current: {current}. " +
                    $"This exchange has {original.StatementCount} statement(s) " +
                    $"and {original.LinkedMemoryCount} linked memories. " +
                    "Editing the time applies to the entire exchange.",
                AutoSize = false
            }, 0, 0);

            var timeRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
            timeRow.Controls.Add(new Label
            {
                Text = "New campaign day:", AutoSize = true,
                Padding = new Padding(0, 7, 0, 0)
            });
            var day = new NumericUpDown
            {
                Width = 125, Minimum = -1_000_000, Maximum = 1_000_000,
                Value = Math.Clamp(original.WorldDay ?? 1, -1_000_000, 1_000_000)
            };
            timeRow.Controls.Add(day);
            timeRow.Controls.Add(new Label
            {
                Text = "New time:", AutoSize = true,
                Padding = new Padding(14, 7, 0, 0)
            });
            var time = new DateTimePicker
            {
                Width = 106, Format = DateTimePickerFormat.Custom,
                CustomFormat = "HH:mm", ShowUpDown = true,
                Value = DateTime.Today.Add(
                    (original.WorldTime ?? new TimeOnly(9, 0)).ToTimeSpan())
            };
            timeRow.Controls.Add(time);
            layout.Controls.Add(timeRow, 0, 1);

            var preview = new GroupBox
            {
                Dock = DockStyle.Fill,
                Text = $"Selected statement by {original.Speaker} (read-only)"
            };
            preview.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
                ScrollBars = ScrollBars.Vertical, Text = original.Content
            });
            layout.Controls.Add(preview, 0, 2);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
            };
            var save = new Button { Text = "Save date/time", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            actions.Controls.Add(save);
            actions.Controls.Add(cancel);
            editor.AcceptButton = save;
            editor.CancelButton = cancel;
            layout.Controls.Add(actions, 0, 3);

            if (editor.ShowDialog(this) != DialogResult.OK) return;
            var proposed = new WorldMoment(
                (int)day.Value, TimeOnly.FromDateTime(time.Value));
            if (original.WorldDay == proposed.Day && original.WorldTime == proposed.Time)
            {
                _status.Text = "Exchange date/time was not changed.";
                return;
            }

            if (MessageBox.Show(this,
                "Update the date/time of every statement in this exchange, " +
                "the interaction record, and linked memories still using " +
                "the original date? Generated witness-memory text will be " +
                "corrected and re-embedding may be needed. " +
                "Your change will be recorded in the audit table.",
                "Confirm exchange time correction",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            var changedMemories = await _database.CorrectConversationMomentAsync(
                campaignId, original, proposed);
            await LoadRosterHistoryAsync();
            await RefreshSavedSearchesAsync();
            await LoadConversationAsync();
            _status.Text = $"Exchange #{original.InteractionId} moved to {proposed}. " +
                $"{changedMemories} linked memories updated. " +
                "Use memory embedding to refresh any changed witness text.";
        });
    }
}
