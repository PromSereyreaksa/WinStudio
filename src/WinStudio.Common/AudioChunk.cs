namespace WinStudio.Common;

public sealed record AudioChunk(
    long TimestampTicks,
    byte[] PcmData,
    int SampleRate,
    int Channels);

