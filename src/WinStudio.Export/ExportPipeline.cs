using WinStudio.Common;

namespace WinStudio.Export;

public sealed class ExportPipeline
{
    private readonly IFrameEncoder _encoder;

    public ExportPipeline(IFrameEncoder? encoder = null)
    {
        _encoder = encoder ?? new ManifestFrameEncoder();
    }

    public async Task RunAsync(
        ExportJob job,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        job.Validate();

        var framesInRange = job.Frames
            .Where(frame => frame.TimestampTicks >= job.TrimInTicks && frame.TimestampTicks <= job.TrimOutTicks)
            .OrderBy(static frame => frame.TimestampTicks)
            .ToArray();

        if (framesInRange.Length == 0)
        {
            throw new InvalidOperationException("No frames exist in the requested trim range.");
        }

        var audioInRange = job.AudioChunks
            .Where(chunk => chunk.TimestampTicks >= job.TrimInTicks && chunk.TimestampTicks <= job.TrimOutTicks)
            .OrderBy(static chunk => chunk.TimestampTicks)
            .ToArray();

        for (var i = 0; i < framesInRange.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress(i + 1, framesInRange.Length, (i + 1d) / framesInRange.Length));
        }

        await _encoder
            .EncodeAsync(framesInRange, audioInRange, job.Preset, job.OutputPath, cancellationToken)
            .ConfigureAwait(false);
    }
}

