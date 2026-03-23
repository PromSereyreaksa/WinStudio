using WinStudio.App.Models;
using WinStudio.Common;

namespace WinStudio.App.Services;

public sealed class StudioCaptureService : IStudioCaptureService
{
    public Task<CaptureSession> CaptureAsync(StudioRunRequest request, CancellationToken cancellationToken)
    {
        const int width = 1280;
        const int height = 720;
        const int frameCount = 90;
        const int fps = 30;

        var startTicks = DateTime.UtcNow.Ticks;
        var frameIntervalTicks = TimeSpan.TicksPerSecond / fps;

        var frames = new List<RawVideoFrame>(frameCount);
        var cursorEvents = new List<CursorEvent>(frameCount + 6);
        var audioChunks = new List<AudioChunk>(frameCount / 10);

        for (var i = 0; i < frameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var timestamp = startTicks + (i * frameIntervalTicks);
            frames.Add(new RawVideoFrame(timestamp, width, height, [0, 0, 0, 255]));

            var x = 200f + (i * 8f);
            var y = 120f + (MathF.Sin(i / 8f) * 100f);
            cursorEvents.Add(new CursorEvent(timestamp, x, y, CursorEventType.Move));

            if (i is 30 or 58 or 76)
            {
                cursorEvents.Add(new CursorEvent(timestamp, x, y, CursorEventType.LeftDown));
            }

            if ((request.IncludeSystemAudio || request.IncludeMicrophoneAudio) && i % 10 == 0)
            {
                audioChunks.Add(new AudioChunk(timestamp, new byte[128], 48000, 2));
            }
        }

        var session = new CaptureSession(startTicks, frames, audioChunks, cursorEvents);
        return Task.FromResult(session);
    }
}

