using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using EPDeskExtractionSandbox.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionSandbox.Services;

public interface IClamAvDaemonClient
{
    Task<string> PingAsync(CancellationToken cancellationToken);

    Task<string> VersionAsync(CancellationToken cancellationToken);

    Task<string> ScanFileAsync(
        string filePath,
        long maximumBytes,
        CancellationToken cancellationToken);
}

public sealed class ClamAvDaemonClient : IClamAvDaemonClient
{
    private readonly SandboxOptions _options;

    public ClamAvDaemonClient(IOptions<SandboxOptions> options)
    {
        _options = options.Value;
    }

    public Task<string> PingAsync(CancellationToken cancellationToken) =>
        SendCommandAsync("PING", cancellationToken);

    public Task<string> VersionAsync(CancellationToken cancellationToken) =>
        SendCommandAsync("VERSION", cancellationToken);

    public async Task<string> ScanFileAsync(
        string filePath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new IOException("The temporary scan file no longer exists.");
        }

        if (file.Length > maximumBytes)
        {
            throw new ClamAvInputLimitException();
        }

        using var socket = CreateSocket();
        await ConnectAsync(socket, cancellationToken);
        await using var daemon = new NetworkStream(socket, ownsSocket: false);
        await using var source = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );

        await ClamAvProtocol.WriteInstreamAsync(
            daemon,
            source,
            maximumBytes,
            cancellationToken
        );
        return await ClamAvProtocol.ReadRecordAsync(daemon, cancellationToken);
    }

    private async Task<string> SendCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        using var socket = CreateSocket();
        await ConnectAsync(socket, cancellationToken);
        await using var daemon = new NetworkStream(socket, ownsSocket: false);
        await ClamAvProtocol.WriteCommandAsync(
            daemon,
            command,
            cancellationToken
        );
        return await ClamAvProtocol.ReadRecordAsync(daemon, cancellationToken);
    }

    private async Task ConnectAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(
            _options.ClamDaemonConnectTimeoutSeconds
        ));
        try
        {
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(_options.ClamSocketPath),
                connectTimeout.Token
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClamAvProtocolException(
                "The ClamAV daemon connection timed out."
            );
        }
    }

    private static Socket CreateSocket() => new(
        AddressFamily.Unix,
        SocketType.Stream,
        ProtocolType.Unspecified
    );
}

internal static class ClamAvProtocol
{
    private const int ChunkBytes = 64 * 1024;
    private const int MaximumResponseBytes = 8 * 1024;

    public static async Task WriteCommandAsync(
        Stream daemon,
        string command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) ||
            command.Any(character => !char.IsAsciiLetter(character)))
        {
            throw new ArgumentException("A safe ClamAV command is required.", nameof(command));
        }

        var bytes = Encoding.ASCII.GetBytes($"z{command}\0");
        await daemon.WriteAsync(bytes, cancellationToken);
        await daemon.FlushAsync(cancellationToken);
    }

    public static async Task WriteInstreamAsync(
        Stream daemon,
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        await WriteCommandAsync(daemon, "INSTREAM", cancellationToken);
        var prefix = new byte[sizeof(uint)];
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, ChunkBytes),
                    cancellationToken
                );
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maximumBytes)
                {
                    throw new ClamAvInputLimitException();
                }

                BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)read);
                await daemon.WriteAsync(prefix, cancellationToken);
                await daemon.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            prefix.AsSpan().Clear();
            await daemon.WriteAsync(prefix, cancellationToken);
            await daemon.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<string> ReadRecordAsync(
        Stream daemon,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            while (true)
            {
                var read = await daemon.ReadAsync(
                    buffer.AsMemory(0, 1024),
                    cancellationToken
                );
                if (read == 0)
                {
                    throw new ClamAvProtocolException(
                        "ClamAV closed the socket before returning a complete response."
                    );
                }

                var terminator = Array.IndexOf(buffer, (byte)0, 0, read);
                var responseBytes = terminator >= 0 ? terminator : read;
                if (response.Length + responseBytes > MaximumResponseBytes)
                {
                    throw new ClamAvProtocolException(
                        "ClamAV returned a response larger than the protocol limit."
                    );
                }

                await response.WriteAsync(
                    buffer.AsMemory(0, responseBytes),
                    cancellationToken
                );
                if (terminator >= 0)
                {
                    return Encoding.UTF8.GetString(response.GetBuffer(), 0, (int)response.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed class ClamAvInputLimitException : Exception
{
}

internal sealed class ClamAvProtocolException : IOException
{
    public ClamAvProtocolException(string message)
        : base(message)
    {
    }
}
