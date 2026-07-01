using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace OutplayOverlay;

public partial class App : System.Windows.Application
{
    // A GUI app has no console to print an unhandled exception to - without this, a crash during
    // startup (e.g. constructing MainWindow) just silently kills the process with zero visible
    // explanation, which is exactly what was reported: the app starts speaking its startup TTS
    // test, then dies mid-utterance with the overlay window never appearing. Every path that can
    // terminate the process unexpectedly is hooked here so the actual exception ends up in a log
    // file next to the exe's data folder, readable without a debugger attached.
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                $"Outplay crashed on startup:\n\n{args.Exception}\n\nDetails were also written to:\n{CrashLogPath}",
                "Outplay Overlay - Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true; // prevent the default silent/instant process termination
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OutplayOverlay", "crash.log");

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = CrashLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ---\n{ex}\n\n");
        }
        catch
        {
            // If even crash logging fails (e.g. disk full, permissions), there's nothing more we
            // can safely do here - deliberately swallow rather than throw from a crash handler.
        }
    }
}
