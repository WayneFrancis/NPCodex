using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private async Task DeleteApprovedSpeechAsync(long statementId, bool wholeExchange)
    {
        if (_busy || CampaignId is not long campaignId) return;

        await RunBusyAsync(async () =>
        {
            var item = await _database.GetApprovedStatementDeletionInfoAsync(
                campaignId, statementId);
            if (item is null)
                throw new InvalidOperationException(
                    "This statement was already deleted. Refresh conversation history.");

            string scope = wholeExchange
                ? $"PERMANENTLY DELETE THE ENTIRE EXCHANGE #{item.InteractionId} " +
                  $"({item.StatementCount} statements)?"
                : $"PERMANENTLY DELETE {item.Speaker}'s SELECTED STATEMENT " +
                  $"from exchange #{item.InteractionId}?";
            var warning =
                scope + Environment.NewLine + Environment.NewLine +
                $"Selected speech: \"{(item.Content.Length > 300
                    ? item.Content[..300] + "…" : item.Content)}\"" +
                Environment.NewLine + Environment.NewLine +
                $"This also deletes {item.LinkedMemoryCount} memories sourced from " +
                "the exchange, INCLUDING copies made for other witnesses, " +
                "and its paired conversation turn. " +
                (wholeExchange
                    ? "The interaction and all its statements will be erased."
                    : "Other individual statement records in this exchange remain, " +
                      "but the original two-line turn cannot be retained.") +
                Environment.NewLine + Environment.NewLine +
                "This is permanent; character-specific forgetting is a separate " +
                "action under Memory Search." +
                Environment.NewLine +
                "Unrelated memories and other exchanges are not affected.";

            if (MessageBox.Show(this, warning,
                wholeExchange ? "Delete entire approved exchange" :
                    "Delete approved statement",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            var result = await _database.DeleteApprovedSpeechAsync(
                campaignId, item, wholeExchange);
            await LoadRosterHistoryAsync();
            await RefreshSavedSearchesAsync();
            await LoadConversationAsync();
            await LoadMemoriesAsync();
            await SearchMemoriesAsyncIfAlreadyLoaded();
            ResetSuggestions(); // Prior AI replies could quote the removed evidence.
            _status.Text =
                $"Deleted {result.RemovedStatements} statement(s), " +
                $"{result.RemovedMemories} memory/memories and " +
                $"{result.RemovedTurns} conversation turn(s) from " +
                $"exchange #{result.InteractionId}." +
                (result.InteractionRemoved ? " The interaction was removed." : "");
        });
    }

    private async Task SearchMemoriesAsyncIfAlreadyLoaded()
    {
        // Avoid an unexpected Ollama call when no memory-search session
        // has been run. A visible previous result set needs refreshing.
        if (_memorySearchResults.Count > 0)
            await SearchMemoriesAsync();
        else
        {
            _memorySearchTable.Rows.Clear();
            _memorySearchPreview.Clear();
        }
    }
}
