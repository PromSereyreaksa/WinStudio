namespace WinStudio.Common;

public sealed record CaptureSession(
    long StartTimestampTicks,
    IReadOnlyList<RawVideoFrame> Frames,
    IReadOnlyList<AudioChunk> AudioChunks,
    IReadOnlyList<CursorEvent> CursorEvents);
