namespace WinStudio.App.Models;

public sealed record StudioRunResult(
    int FrameCount,
    int ZoomKeyframeCount,
    TimeSpan Duration,
    string OutputPath);

