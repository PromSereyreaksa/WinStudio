namespace WinStudio.Export;

public readonly record struct ExportProgress(int ProcessedFrames, int TotalFrames, double FractionComplete);

