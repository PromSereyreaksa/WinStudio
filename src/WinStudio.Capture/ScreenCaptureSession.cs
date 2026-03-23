using System.Threading.Channels;
using WinStudio.Common;

namespace WinStudio.Capture;

public sealed class ScreenCaptureSession
{
    private readonly Channel<RawVideoFrame> _frames = Channel.CreateUnbounded<RawVideoFrame>();
    private bool _isRunning;

    public long StartTimestampTicks { get; private set; }

    public ChannelReader<RawVideoFrame> Frames => _frames.Reader;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            return Task.CompletedTask;
        }

        _isRunning = true;
        StartTimestampTicks = DateTime.UtcNow.Ticks;
        return Task.CompletedTask;
    }

    public ValueTask PushFrameAsync(RawVideoFrame frame, CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Capture session has not been started.");
        }

        return _frames.Writer.WriteAsync(frame, cancellationToken);
    }

    public Task StopAsync()
    {
        _isRunning = false;
        _frames.Writer.TryComplete();
        return Task.CompletedTask;
    }
}

