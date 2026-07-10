namespace EPDeskAgent.Services;

public class LocalLogService
{
    private readonly object _lock = new();

    private readonly string _logRoot;

    public LocalLogService()
    {
        _logRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EPDeskAgent",
            "Logs"
        );

        Directory.CreateDirectory(_logRoot);
    }

    public void Info(
        string category,
        string message,
        Guid? requestId = null,
        string step = "",
        string details = "")
    {
        Write("INFO", category, message, requestId, step, details);
    }

    public void Warning(
        string category,
        string message,
        Guid? requestId = null,
        string step = "",
        string details = "")
    {
        Write("WARNING", category, message, requestId, step, details);
    }

    public void Error(
        string category,
        string message,
        Exception? exception = null,
        Guid? requestId = null,
        string step = "",
        string details = "")
    {
        var finalDetails = details;

        if (exception != null)
        {
            finalDetails =
                $"Exception: {exception.Message} | Inner: {exception.InnerException?.Message} | Stack: {exception.StackTrace}";
        }

        Write("ERROR", category, message, requestId, step, finalDetails);
        Write("ERROR", "error", message, requestId, step, finalDetails);
    }

    public List<string> ReadRecentLines(string logType = "all", int takeLines = 500)
    {
        if (takeLines <= 0)
        {
            takeLines = 500;
        }

        if (takeLines > 5000)
        {
            takeLines = 5000;
        }

        var files = GetLogFiles(logType);

        var allLines = new List<string>();

        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            var lines = File.ReadLines(file)
                .Reverse()
                .Take(takeLines)
                .Reverse()
                .ToList();

            allLines.Add($"===== {Path.GetFileName(file)} =====");
            allLines.AddRange(lines);
        }

        return allLines
            .TakeLast(takeLines)
            .ToList();
    }

    private void Write(
        string level,
        string category,
        string message,
        Guid? requestId,
        string step,
        string details)
    {
        try
        {
            Directory.CreateDirectory(_logRoot);

            var filePath = GetFilePath(category);

            var deviceCode = Environment.MachineName.ToUpperInvariant();

            var line =
                $"{DateTime.UtcNow:O} | {level} | Device={deviceCode} | RequestId={requestId?.ToString() ?? "-"} | Step={step} | {message}";

            if (!string.IsNullOrWhiteSpace(details))
            {
                line += $" | Details={details}";
            }

            lock (_lock)
            {
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Never crash Agent because logging failed.
        }
    }

    private string GetFilePath(string category)
    {
        var fileName = category.ToLowerInvariant() switch
        {
            "file-request" => "file-request.log",
            "upload" => "upload.log",
            "error" => "error.log",
            "diagnostic" => "diagnostic.log",
            _ => "agent.log"
        };

        return Path.Combine(_logRoot, fileName);
    }

    private List<string> GetLogFiles(string logType)
    {
        logType = string.IsNullOrWhiteSpace(logType)
            ? "all"
            : logType.Trim().ToLowerInvariant();

        if (logType == "error")
        {
            return new List<string> { Path.Combine(_logRoot, "error.log") };
        }

        if (logType == "file-request")
        {
            return new List<string> { Path.Combine(_logRoot, "file-request.log") };
        }

        if (logType == "upload")
        {
            return new List<string> { Path.Combine(_logRoot, "upload.log") };
        }

        if (logType == "diagnostic")
        {
            return new List<string> { Path.Combine(_logRoot, "diagnostic.log") };
        }

        return new List<string>
        {
            Path.Combine(_logRoot, "agent.log"),
            Path.Combine(_logRoot, "file-request.log"),
            Path.Combine(_logRoot, "upload.log"),
            Path.Combine(_logRoot, "error.log"),
            Path.Combine(_logRoot, "diagnostic.log")
        };
    }
}