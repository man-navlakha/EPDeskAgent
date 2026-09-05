using System.Diagnostics;
using System.Text;

namespace EPDeskExtractionWorker.Services.Extraction;

public sealed record ExternalProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    int MaxOutputCharacters);

public sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public async Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Executable);
        if (request.Timeout <= TimeSpan.Zero || request.MaxOutputCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        RemoveParentSecrets(startInfo);

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new ExtractionException($"Could not start external tool '{request.Executable}'.");
            }
        }
        catch (Exception exception) when (exception is not ExtractionException)
        {
            throw new ExternalToolUnavailableException(
                $"Could not start external tool '{request.Executable}'.",
                exception
            );
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        var stdout = ReadBoundedAsync(process.StandardOutput, request.MaxOutputCharacters, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, Math.Min(request.MaxOutputCharacters, 1024 * 1024), timeout.Token);

        try
        {
            var processExit = process.WaitForExitAsync(timeout.Token);
            var outputComplete = Task.WhenAll(stdout, stderr);
            var firstCompleted = await Task.WhenAny(processExit, outputComplete);
            if (firstCompleted == outputComplete)
            {
                // Observe an output-limit failure immediately so a blocked child cannot wait out the timeout.
                await outputComplete;
            }

            await processExit;
            var output = await stdout;
            var error = await stderr;
            return new ExternalProcessResult(process.ExitCode, output, error);
        }
        catch (OperationCanceledException exception)
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch
            {
                // The original timeout or cancellation is the useful failure.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new ExternalToolTimeoutException(
                $"External tool '{request.Executable}' exceeded its {request.Timeout} timeout.",
                exception);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        TextReader reader,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(64 * 1024, maxCharacters));
        var buffer = new char[16 * 1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return builder.ToString();
            }

            if (builder.Length > maxCharacters - read)
            {
                throw new ExtractionException($"External tool output exceeds {maxCharacters} characters.");
            }

            builder.Append(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup; the caller still receives the original extraction failure.
        }
    }

    private static void RemoveParentSecrets(ProcessStartInfo startInfo)
    {
        var allowedNames = new[]
        {
            "PATH",
            "LANG",
            "LC_ALL",
            "HOME",
            "TMPDIR",
            "TESSDATA_PREFIX"
        };
        var allowed = allowedNames
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToArray();

        startInfo.Environment.Clear();
        foreach (var item in allowed)
        {
            startInfo.Environment[item.Name] = item.Value!;
        }
    }
}
