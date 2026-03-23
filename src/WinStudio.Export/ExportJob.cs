using WinStudio.Common;

namespace WinStudio.Export;

public sealed record ExportJob
{
    public required IReadOnlyList<ProcessedVideoFrame> Frames { get; init; }
    public required IReadOnlyList<AudioChunk> AudioChunks { get; init; }
    public required ExportPreset Preset { get; init; }
    public required long TrimInTicks { get; init; }
    public required long TrimOutTicks { get; init; }
    public required string OutputPath { get; init; }

    public void Validate()
    {
        if (Frames.Count == 0)
        {
            throw new InvalidOperationException("Export job must include at least one frame.");
        }

        if (TrimOutTicks <= TrimInTicks)
        {
            throw new InvalidOperationException("TrimOutTicks must be greater than TrimInTicks.");
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new InvalidOperationException("OutputPath is required.");
        }
    }
}

