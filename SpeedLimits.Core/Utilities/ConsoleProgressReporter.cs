namespace SpeedLimits.Core.Utilities;

/// <summary>
/// Reports progress to console with formatted output
/// </summary>
public class ConsoleProgressReporter
{
    private readonly string _operation;
    private int _lastLength = 0;

    public ConsoleProgressReporter(string operation)
    {
        _operation = operation;
    }

    /// <summary>
    /// Reports download progress with percentage and size
    /// </summary>
    public void ReportDownload(long bytesDownloaded, long? totalBytes)
    {
        string message;
        if (totalBytes.HasValue && totalBytes.Value > 0)
        {
            var percentage = (int)((bytesDownloaded * 100) / totalBytes.Value);
            var downloadedMB = bytesDownloaded / (1024.0 * 1024.0);
            var totalMB = totalBytes.Value / (1024.0 * 1024.0);
            message = $"{_operation}: {percentage}% ({downloadedMB:F1} MB / {totalMB:F1} MB)";
        }
        else
        {
            var downloadedMB = bytesDownloaded / (1024.0 * 1024.0);
            message = $"{_operation}: {downloadedMB:F1} MB";
        }

        WriteProgress(message);
    }

    /// <summary>
    /// Reports count-based progress
    /// </summary>
    public void ReportCount(long current, long? total = null)
    {
        string message;
        if (total.HasValue && total.Value > 0)
        {
            var percentage = (int)((current * 100) / total.Value);
            message = $"{_operation}: {percentage}% ({current:N0} / {total:N0})";
        }
        else
        {
            message = $"{_operation}: {current:N0}";
        }

        WriteProgress(message);
    }

    /// <summary>
    /// Reports simple message
    /// </summary>
    public void ReportMessage(string message)
    {
        WriteProgress($"{_operation}: {message}");
    }

    /// <summary>
    /// Completes progress line
    /// </summary>
    public void Complete(string? finalMessage = null)
    {
        if (finalMessage != null)
        {
            WriteProgress($"{_operation}: {finalMessage}");
        }
        Console.WriteLine(); // Move to new line
    }

    private void WriteProgress(string message)
    {
        // Clear previous line
        if (_lastLength > 0)
        {
            Console.Write('\r' + new string(' ', _lastLength) + '\r');
        }

        // Write new message
        Console.Write(message);
        _lastLength = message.Length;
    }

    /// <summary>
    /// Creates a simple progress reporter for operations without detailed tracking
    /// </summary>
    public static void Report(string message)
    {
        Console.WriteLine(message);
    }
}
