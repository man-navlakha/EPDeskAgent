namespace EPDeskOldDataUploader;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => ShowCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ShowCrash(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
    }

    private static void ShowCrash(Exception? exception)
    {
        MessageBox.Show(
            exception?.ToString() ?? "Unknown error.",
            "EPDesk Old User Data Uploader",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error
        );
    }
}
