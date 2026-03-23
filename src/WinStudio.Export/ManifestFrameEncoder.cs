using System.Text.Json;
using WinStudio.Common;

namespace WinStudio.Export;

public sealed class ManifestFrameEncoder : IFrameEncoder
{
    public async Task EncodeAsync(
        IReadOnlyList<ProcessedVideoFrame> frames,
        IReadOnlyList<AudioChunk> audioChunks,
        ExportPreset preset,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        var manifest = new
        {
            preset.Name,
            preset.Width,
            preset.Height,
            preset.FramesPerSecond,
            FrameCount = frames.Count,
            AudioChunkCount = audioChunks.Count,
            CreatedUtc = DateTime.UtcNow
        };

        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

