using WinStudio.Common;
using WinStudio.Export;
using Xunit;

namespace WinStudio.Export.Tests;

public sealed class ExportPipelineTests
{
    [Fact]
    public async Task RunAsync_WhenTrimIsInvalid_Throws()
    {
        var pipeline = new ExportPipeline(new InMemoryEncoder());
        var frame = CreateFrame(1000);
        var job = new ExportJob
        {
            Frames = [frame],
            AudioChunks = [],
            Preset = ExportPresets.DemoHD,
            TrimInTicks = 1000,
            TrimOutTicks = 1000,
            OutputPath = "output/test.json"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RunAsync(job));
    }

    [Fact]
    public async Task RunAsync_WhenValid_EncodesFramesInRange()
    {
        var encoder = new InMemoryEncoder();
        var pipeline = new ExportPipeline(encoder);
        var frames = new[]
        {
            CreateFrame(1000),
            CreateFrame(2000),
            CreateFrame(3000)
        };

        var job = new ExportJob
        {
            Frames = frames,
            AudioChunks = [],
            Preset = ExportPresets.DemoHD,
            TrimInTicks = 1500,
            TrimOutTicks = 3000,
            OutputPath = "output/test.json"
        };

        await pipeline.RunAsync(job);

        Assert.Equal(2, encoder.FrameCount);
    }

    private static ProcessedVideoFrame CreateFrame(long ticks)
    {
        return new ProcessedVideoFrame(
            ticks,
            1920,
            1080,
            new byte[4],
            new RectF(0f, 0f, 1920f, 1080f),
            1f,
            1f);
    }

    private sealed class InMemoryEncoder : IFrameEncoder
    {
        public int FrameCount { get; private set; }

        public Task EncodeAsync(
            IReadOnlyList<ProcessedVideoFrame> frames,
            IReadOnlyList<AudioChunk> audioChunks,
            ExportPreset preset,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            FrameCount = frames.Count;
            return Task.CompletedTask;
        }
    }
}
