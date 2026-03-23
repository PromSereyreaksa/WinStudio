using WinStudio.App.Models;
using WinStudio.Export;

namespace WinStudio.App.Services;

public sealed class StudioExportService : IStudioExportService
{
    private readonly ExportPipeline _pipeline;

    public StudioExportService(ExportPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public async Task<string> ExportAsync(
        StudioProcessingResult result,
        ExportPreset preset,
        CancellationToken cancellationToken)
    {
        if (result.Frames.Count == 0)
        {
            throw new InvalidOperationException("Cannot export with zero processed frames.");
        }

        var outputDir = Path.Combine(AppContext.BaseDirectory, "output");
        Directory.CreateDirectory(outputDir);

        var outputPath = Path.Combine(
            outputDir,
            $"winstudio-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{preset.Name}.manifest.json");

        var job = new ExportJob
        {
            Frames = result.Frames,
            AudioChunks = result.AudioChunks,
            Preset = preset,
            TrimInTicks = result.Frames[0].TimestampTicks,
            TrimOutTicks = result.Frames[^1].TimestampTicks,
            OutputPath = outputPath
        };

        await _pipeline.RunAsync(job, cancellationToken: cancellationToken).ConfigureAwait(false);
        return outputPath;
    }
}

