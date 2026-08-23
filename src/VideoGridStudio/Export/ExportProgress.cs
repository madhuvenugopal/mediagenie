namespace VideoGridStudio.Export;

public sealed class ExportProgress
{
    public ExportProgress(double encodedSeconds, double totalSeconds, string message)
    {
        EncodedSeconds = encodedSeconds;
        TotalSeconds = totalSeconds;
        Message = message;
    }

    public double EncodedSeconds { get; }

    public double TotalSeconds { get; }

    public string Message { get; }

    public double Fraction => TotalSeconds > 0 ? Math.Clamp(EncodedSeconds / TotalSeconds, 0, 1) : 0;

    public int Percent => (int)Math.Round(Fraction * 100);
}
