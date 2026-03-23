namespace WinStudio.App.Models;

public sealed record RecordingResult(
    string RawVideoPath,
    string VideoPath,
    string CursorLogPath,
    string ZoomKeyframesPath,
    int CursorEventCount,
    int ZoomKeyframeCount,
    TimeSpan Duration,
    string? ProcessingError = null);
