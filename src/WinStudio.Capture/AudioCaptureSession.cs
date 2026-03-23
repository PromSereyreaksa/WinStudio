using System.Threading.Channels;
using WinStudio.Common;

namespace WinStudio.Capture;

public sealed class AudioCaptureSession
{
    private readonly Channel<AudioChunk> _audioChunks = Channel.CreateUnbounded<AudioChunk>();
    private bool _isRunning;

    public ChannelReader<AudioChunk> Chunks => _audioChunks.Reader;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _isRunning = true;
        return Task.CompletedTask;
    }

    public ValueTask PushAudioChunkAsync(AudioChunk chunk, CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
        {
            throw new InvalidOperationException("Audio session has not been started.");
        }

        return _audioChunks.Writer.WriteAsync(chunk, cancellationToken);
    }

    public Task StopAsync()
    {
        _isRunning = false;
        _audioChunks.Writer.TryComplete();
        return Task.CompletedTask;
    }
}

