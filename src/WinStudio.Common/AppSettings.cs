namespace WinStudio.Common;

public sealed class AppSettings
{
    private static readonly Lazy<AppSettings> LazyInstance = new(() => new AppSettings());

    private AppSettings()
    {
    }

    public static AppSettings Instance => LazyInstance.Value;

    public int TargetFramesPerSecond { get; set; } = 30;
    public string DefaultOutputDirectory { get; set; } = "output";
}

