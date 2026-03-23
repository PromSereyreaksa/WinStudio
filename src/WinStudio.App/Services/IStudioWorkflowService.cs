using WinStudio.App.Models;

namespace WinStudio.App.Services;

public interface IStudioWorkflowService
{
    Task<StudioRunResult> RunPipelineAsync(StudioRunRequest request, CancellationToken cancellationToken);
}

