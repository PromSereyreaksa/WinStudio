using WinStudio.Export;

namespace WinStudio.App.Models;

public sealed record StudioRunRequest(
    ExportPreset Preset,
    string CaptureTarget,
    bool IncludeSystemAudio,
    bool IncludeMicrophoneAudio);

