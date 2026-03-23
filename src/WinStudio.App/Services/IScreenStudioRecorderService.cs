using WinStudio.App.Models;

namespace WinStudio.App.Services;

public interface IScreenStudioRecorderService
{
    bool IsRecording { get; }

    bool IsPaused { get; }

    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken);

    Task<bool> TogglePauseAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);

    Task<RecordingResult> StopAsync(CancellationToken cancellationToken);
}
