using Microsoft.UI.Xaml;
using System.Text;

namespace WinStudio.App;

public partial class App : Application
{
    private Window? _window;

    /// <summary>The active application window. Available after <see cref="OnLaunched"/>.</summary>
    public static Window? ActiveWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        ActiveWindow = _window;
        _window.Activate();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        TryWriteStartupLog("UnhandledException", e.Exception);
    }

    private static void TryWriteStartupLog(string phase, Exception exception)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "WinStudio", "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "startup.log");
            var text = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {phase}")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(path, text, Encoding.UTF8);
        }
        catch
        {
            // Swallow logging failures.
        }
    }
}
