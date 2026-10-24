using NPCodex.Data;

namespace NPCodex.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var connectionString = NPCodexConnectionSettings.CreateLocalConnectionString();
            Application.Run(new MainForm(connectionString));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "NPCodex could not start",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
