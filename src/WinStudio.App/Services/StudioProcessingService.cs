using WinStudio.App.Models;
using WinStudio.Common;
using WinStudio.Export;
using WinStudio.Processing;

namespace WinStudio.App.Services;

public sealed class StudioProcessingService : IStudioProcessingService
{
    private readonly ZoomRegionGenerator _zoomGenerator = new();
    private readonly CursorSmoother _cursorSmoother = new();
    private readonly VideoCompositor _videoCompositor = new();

    public async Task<StudioProcessingResult> ProcessAsync(
        CaptureSession session,
        ExportPreset preset,
        CancellationToken cancellationToken)
    {
        if (session.Frames.Count == 0)
        {
            throw new InvalidOperationException("No captured frames were available for processing.");
        }

        var firstFrame = session.Frames[0];
        var zoomKeyframes = _zoomGenerator.Generate(session.CursorEvents, firstFrame.Width, firstFrame.Height);
        var smoothedCursor = _cursorSmoother.Smooth(session.CursorEvents);

        var processedFrames = await _videoCompositor
            .ProcessAsync(
                session.Frames,
                zoomKeyframes,
                smoothedCursor,
                preset.Width,
                preset.Height,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new StudioProcessingResult(
            processedFrames,
            session.AudioChunks,
            zoomKeyframes,
            smoothedCursor);
    }
}

