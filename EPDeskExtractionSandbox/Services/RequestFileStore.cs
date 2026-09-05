using System.Buffers;
using EPDeskExtractionSandbox.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionSandbox.Services;

public sealed class RequestFileStore
{
    private readonly SandboxOptions _options;

    public RequestFileStore(IOptions<SandboxOptions> options)
    {
        _options = options.Value;
    }

    public async Task<TemporaryRequestFile> WriteAsync(
        Stream requestBody,
        long? contentLength,
        CancellationToken cancellationToken)
    {
        if (contentLength is <= 0)
        {
            throw new SandboxRequestException(400, "empty_body", "The request body must contain a file.");
        }

        if (contentLength > _options.MaxInputBytes)
        {
            throw new SandboxRequestException(413, "file_too_large", "The file exceeds the configured input limit.");
        }

        var root = Path.GetFullPath(_options.TempRoot);
        Directory.CreateDirectory(root);
        var requestDirectory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(requestDirectory);
        var filePath = Path.Combine(requestDirectory, "input.bin");

        try
        {
            await using var output = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long total = 0;
            try
            {
                while (true)
                {
                    var read = await requestBody.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    if (total > _options.MaxInputBytes - read)
                    {
                        throw new SandboxRequestException(
                            413,
                            "file_too_large",
                            "The file exceeds the configured input limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    total += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (total == 0)
            {
                throw new SandboxRequestException(400, "empty_body", "The request body must contain a file.");
            }

            await output.FlushAsync(cancellationToken);
            return new TemporaryRequestFile(requestDirectory, filePath, total);
        }
        catch
        {
            TryDelete(requestDirectory);
            throw;
        }
    }

    internal static void TryDelete(string requestDirectory)
    {
        try
        {
            if (Directory.Exists(requestDirectory))
            {
                Directory.Delete(requestDirectory, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort; the directory contains only a randomized request workspace.
        }
    }
}

public sealed class TemporaryRequestFile : IAsyncDisposable
{
    private readonly string _requestDirectory;
    private int _disposed;

    public TemporaryRequestFile(string requestDirectory, string filePath, long length)
    {
        _requestDirectory = requestDirectory;
        FilePath = filePath;
        Length = length;
    }

    public string FilePath { get; }
    public long Length { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            RequestFileStore.TryDelete(_requestDirectory);
        }

        return ValueTask.CompletedTask;
    }
}
