using WinStudio.App.Models;
using WinStudio.Common;

namespace WinStudio.App.Services;

public interface IStudioCaptureService
{
    Task<CaptureSession> CaptureAsync(StudioRunRequest request, CancellationToken cancellationToken);
}

