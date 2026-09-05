using EPDeskExtractionWorker.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionWorker.Services.Processing;

public sealed class TemporaryWorkspaceFactory
{
    private readonly string _rootPath;
    private readonly ILogger<TemporaryWorkspaceFactory> _logger;

    public TemporaryWorkspaceFactory(
        IOptions<ExtractionWorkerOptions> options,
        ILogger<TemporaryWorkspaceFactory> logger)
    {
        _rootPath = ValidateRoot(options.Value.TempRoot);
        _logger = logger;
    }

    public TemporaryWorkspace Create(Guid jobId, string leaseToken)
    {
        var tokenFragment = new string(leaseToken
            .Where(char.IsAsciiLetterOrDigit)
            .Take(16)
            .ToArray());

        if (tokenFragment.Length == 0)
        {
            throw new InvalidOperationException("A safe lease token is required for temporary storage.");
        }

        Directory.CreateDirectory(_rootPath);
        var workspacePath = Path.GetFullPath(
            Path.Combine(_rootPath, $"{jobId:N}-{tokenFragment}")
        );
        EnsureChildPath(_rootPath, workspacePath);
        Directory.CreateDirectory(workspacePath);

        return new TemporaryWorkspace(_rootPath, workspacePath, _logger);
    }

    private static string ValidateRoot(string configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException("ExtractionWorker:TempRoot is required.");
        }

        var fullRoot = Path.GetFullPath(configuredRoot);
        var volumeRoot = Path.GetPathRoot(fullRoot);
        if (string.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar),
                volumeRoot?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ExtractionWorker:TempRoot cannot be a filesystem root.");
        }

        return fullRoot.TrimEnd(Path.DirectorySeparatorChar);
    }

    internal static void EnsureChildPath(string rootPath, string candidatePath)
    {
        var prefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Temporary workspace escaped its configured root.");
        }
    }
}

public sealed class TemporaryWorkspace : IAsyncDisposable
{
    private readonly string _rootPath;
    private readonly ILogger _logger;
    private bool _disposed;

    internal TemporaryWorkspace(string rootPath, string workspacePath, ILogger logger)
    {
        _rootPath = rootPath;
        WorkspacePath = workspacePath;
        _logger = logger;
    }

    public string WorkspacePath { get; }

    public string SourceFilePath(string originalFileName)
    {
        var extension = Path.GetExtension(Path.GetFileName(originalFileName));
        if (extension.Length > 20 || extension.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '.'))
        {
            extension = ".bin";
        }

        var path = Path.GetFullPath(Path.Combine(WorkspacePath, $"source{extension.ToLowerInvariant()}"));
        TemporaryWorkspaceFactory.EnsureChildPath(WorkspacePath, path);
        return path;
    }

    public string DerivativeFilePath()
    {
        var path = Path.GetFullPath(Path.Combine(WorkspacePath, "extraction.json"));
        TemporaryWorkspaceFactory.EnsureChildPath(WorkspacePath, path);
        return path;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            var resolved = Path.GetFullPath(WorkspacePath);
            TemporaryWorkspaceFactory.EnsureChildPath(_rootPath, resolved);
            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not fully delete extraction workspace {WorkspacePath}.",
                WorkspacePath
            );
        }

        return ValueTask.CompletedTask;
    }
}
