using WinStudio.App.Models;

namespace WinStudio.App.Services;

public sealed class StudioWorkflowService : IStudioWorkflowService
{
    private readonly IStudioCaptureService _captureService;
    private readonly IStudioProcessingService _processingService;
    private readonly IStudioExportService _exportService;

    public StudioWorkflowService(
        IStudioCaptureService captureService,
        IStudioProcessingService processingService,
        IStudioExportService exportService)
    {
        _captureService = captureService;
        _processingService = processingService;
        _exportService = exportService;
    }

    public async Task<StudioRunResult> RunPipelineAsync(StudioRunRequest request, CancellationToken cancellationToken)
    {
        var session = await _captureService.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
        var processed = await _processingService.ProcessAsync(session, request.Preset, cancellationToken).ConfigureAwait(false);
        var outputPath = await _exportService.ExportAsync(processed, request.Preset, cancellationToken).ConfigureAwait(false);

        var endTicks = session.Frames.Count == 0
            ? session.StartTimestampTicks
            : session.Frames[^1].TimestampTicks;

        return new StudioRunResult(
            session.Frames.Count,
            processed.ZoomKeyframes.Count,
            TimeSpan.FromTicks(Math.Max(0, endTicks - session.StartTimestampTicks)),
            outputPath);
    }
}

