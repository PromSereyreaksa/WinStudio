using WinStudio.App.Models;
using WinStudio.Export;

namespace WinStudio.App.Services;

public interface IStudioExportService
{
    Task<string> ExportAsync(
        StudioProcessingResult result,
        ExportPreset preset,
        CancellationToken cancellationToken);
}

