namespace BotNexus.Satellite.Client.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One process per Windows user/session, stronger than per-profile exclusion. No takeover ping-pong.
        using var mutex = new Mutex(true, "Local\\BotNexus.Satellite.Client.Tray", out var created);
        if (!created)
        {
            MessageBox.Show("The satellite tray client is already running in this Windows session.", "BotNexus satellite", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            ApplicationConfiguration.Initialize();
            using var context = new TrayApplication();
            Application.Run(context);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
