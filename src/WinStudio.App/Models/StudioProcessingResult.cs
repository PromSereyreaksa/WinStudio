using WinStudio.Common;

namespace WinStudio.App.Models;

public sealed record StudioProcessingResult(
    IReadOnlyList<ProcessedVideoFrame> Frames,
    IReadOnlyList<AudioChunk> AudioChunks,
    IReadOnlyList<ZoomKeyframe> ZoomKeyframes,
    IReadOnlyList<CursorEvent> SmoothedCursorEvents);

