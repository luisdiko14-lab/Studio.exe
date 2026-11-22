using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace StudioApp;

public partial class App : Application
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Luis Apps Inc.", "Studio.exe", "Logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (!e.Args.Contains("--crash-restart", StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(Environment.GetEnvironmentVariable("STUDIO_DISABLE_CRASH_HANDLER"), "1", StringComparison.Ordinal))
        {
            StartCrashHandler();
        }

        base.OnStartup(e);
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteCrash("Startup failed", ex);
            MessageBox.Show(
                "Studio.exe could not start. A diagnostic log was saved here:\n\n" +
                Path.Combine(LogDirectory, "startup.log") + "\n\n" + ex.Message,
                "Studio.exe startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }


    private static void StartCrashHandler()
    {
        try
        {
            string appDirectory = AppContext.BaseDirectory;
            string handlerPath = Path.Combine(appDirectory, "Studio.CrashHandler.exe");
            string applicationPath = Environment.ProcessPath
                ?? Path.Combine(appDirectory, "Studio.exe");

            if (!File.Exists(handlerPath))
            {
                WriteCrash("Studio Crash Handler was not found. Automatic restart protection is unavailable.");
                return;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = handlerPath,
                WorkingDirectory = appDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--watch");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(applicationPath);
            startInfo.ArgumentList.Add(appDirectory);

            System.Diagnostics.Process.Start(startInfo)?.Dispose();
        }
        catch (Exception ex)
        {
            WriteCrash("Could not start Studio Crash Handler. Automatic restart protection is unavailable.", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            "Studio.exe encountered an unexpected error. Details were saved to startup.log.\n\n" +
            e.Exception.Message,
            "Studio.exe error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WriteCrash("Fatal unhandled exception", exception);
        else
            WriteCrash("Fatal unhandled exception", new Exception(e.ExceptionObject?.ToString() ?? "Unknown exception"));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrash("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private static void WriteCrash(string heading, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var details = exception is null ? string.Empty : Environment.NewLine + exception;
            File.AppendAllText(
                Path.Combine(LogDirectory, "startup.log"),
                $"[{DateTimeOffset.Now:O}] {heading}{details}{Environment.NewLine}{new string('-', 72)}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch { }
    }
}
