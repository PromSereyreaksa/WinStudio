namespace WinStudio.App.Models;

public sealed record RecordingOptions(
    string CaptureTarget,
    int FramesPerSecond,
    bool IncludeSystemAudio,
    nint SelectedWindowHandle,
    string? SelectedWindowTitle,
    float ZoomIntensity,
    float ZoomSensitivity,
    float FollowSpeed);
