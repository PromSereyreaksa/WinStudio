using WinStudio.Common;

namespace WinStudio.Export;

public interface IFrameEncoder
{
    Task EncodeAsync(
        IReadOnlyList<ProcessedVideoFrame> frames,
        IReadOnlyList<AudioChunk> audioChunks,
        ExportPreset preset,
        string outputPath,
        CancellationToken cancellationToken = default);
}

