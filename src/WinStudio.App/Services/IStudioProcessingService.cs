using WinStudio.App.Models;
using WinStudio.Common;
using WinStudio.Export;

namespace WinStudio.App.Services;

public interface IStudioProcessingService
{
    Task<StudioProcessingResult> ProcessAsync(
        CaptureSession session,
        ExportPreset preset,
        CancellationToken cancellationToken);
}

