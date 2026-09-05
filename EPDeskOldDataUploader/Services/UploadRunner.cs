using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using EPDeskOldDataUploader.Models;

namespace EPDeskOldDataUploader.Services;

/// <summary>
/// Pushes scanned files through the API one multipart upload at a time, several
/// files at once. Counters are read by the window on a timer rather than raised
/// per file, so a hundred thousand small files cannot flood the UI thread.
/// </summary>
public sealed class UploadRunner
{
    private readonly UploadApiClient _client;
    private readonly int _parallelUploads;
    private readonly int _retryCount;
    private readonly bool _computeSha256;
    private readonly ManualResetEventSlim _resumeGate = new(true);

    private long _completedFiles;
    private long _alreadyPresentFiles;
    private long _skippedFiles;
    private long _failedFiles;
    private long _completedBytes;
    private long _transferredBytes;

    public UploadRunner(
        UploadApiClient client,
        int parallelUploads,
        int retryCount,
        bool computeSha256)
    {
        _client = client;
        _parallelUploads = Math.Max(1, parallelUploads);
        _retryCount = Math.Max(1, retryCount);
        _computeSha256 = computeSha256;
    }

    /// <summary>Files that reached a terminal state, waiting to be drawn.</summary>
    public ConcurrentQueue<ScannedFile> FinishedFiles { get; } = new();

    public ConcurrentQueue<string> Log { get; } = new();

    public long CompletedFiles => Interlocked.Read(ref _completedFiles);
    public long AlreadyPresentFiles => Interlocked.Read(ref _alreadyPresentFiles);
    public long SkippedFiles => Interlocked.Read(ref _skippedFiles);
    public long FailedFiles => Interlocked.Read(ref _failedFiles);

    /// <summary>Bytes of files that finished, whether sent now or already held.</summary>
    public long CompletedBytes => Interlocked.Read(ref _completedBytes);

    /// <summary>Bytes actually pushed to Backblaze, which drives the live rate.</summary>
    public long TransferredBytes => Interlocked.Read(ref _transferredBytes);

    public bool IsPaused => !_resumeGate.IsSet;

    public void Pause() => _resumeGate.Reset();

    public void Resume() => _resumeGate.Set();

    public async Task RunAsync(
        IReadOnlyList<ScannedFile> files,
        CancellationToken cancellationToken)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _parallelUploads,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(files, options, async (file, token) =>
        {
            // Pausing takes effect between files so no multipart upload is torn
            // in half; the server keeps unfinished parts either way.
            _resumeGate.Wait(token);

            await UploadOneAsync(file, token);

            FinishedFiles.Enqueue(file);
        });
    }

    private async Task UploadOneAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _retryCount; attempt++)
        {
            file.AttemptCount = attempt;
            Guid? uploadId = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new FileInfo(file.FullPath);

                if (!info.Exists)
                {
                    Finish(file, FileState.Skipped, "File is no longer on disk.");
                    return;
                }

                if (info.Length <= 0)
                {
                    Finish(file, FileState.Skipped, "Empty file.");
                    return;
                }

                var sha256 = _computeSha256
                    ? await ComputeSha256Async(file.FullPath, cancellationToken)
                    : "";

                var response = await _client.InitiateAsync(
                    new InitiateUploadRequest
                    {
                        DeviceCode = file.DeviceCode,
                        FullPath = file.FullPath,
                        FileName = file.FileName,
                        Extension = file.Extension,
                        SizeBytes = info.Length,
                        // Re-read rather than trusting the scan: the pair of size
                        // and timestamp is what the server versions the file by.
                        LastModifiedAtUtc = info.LastWriteTimeUtc,
                        Sha256 = sha256,
                        // Ignored by the agent endpoints; in readable-folder mode
                        // these two become the object key's path.
                        UserFolder = file.UserFolder,
                        RelativePath = file.RelativePath,
                        CreatedAtUtc = file.CreatedAtUtc
                    },
                    cancellationToken
                );

                uploadId = response.UploadId;

                if (!response.ShouldUpload)
                {
                    Finish(file, FileState.AlreadyOnServer, "Already stored.");
                    return;
                }

                var alreadyUploaded = response.UploadedPartNumbers.ToHashSet();

                for (var partNumber = 1; partNumber <= response.ExpectedPartCount; partNumber++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (alreadyUploaded.Contains(partNumber))
                    {
                        continue;
                    }

                    var part = await _client.GetPartUrlAsync(
                        response.UploadId,
                        partNumber,
                        cancellationToken
                    );

                    await _client.UploadPartAsync(file.FullPath, part, cancellationToken);
                    Interlocked.Add(ref _transferredBytes, part.LengthBytes);
                }

                await _client.CompleteAsync(response.UploadId, cancellationToken);

                Finish(file, FileState.Completed, "");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var message = Describe(exception);

                if (uploadId.HasValue)
                {
                    await _client.ReportFailureAsync(
                        uploadId.Value,
                        message,
                        CancellationToken.None
                    );
                }

                // A rejection is a decision about the file itself, so retrying it
                // would fail identically. Only transient problems are retried.
                if (IsPermanentRejection(exception))
                {
                    Finish(file, FileState.Skipped, message);
                    return;
                }

                if (attempt >= _retryCount)
                {
                    Finish(file, FileState.Failed, message);
                    Log.Enqueue($"FAILED  {file.FullPath} - {message}");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }
    }

    private void Finish(ScannedFile file, FileState state, string message)
    {
        file.State = state;
        file.Message = message;

        switch (state)
        {
            case FileState.Completed:
                Interlocked.Increment(ref _completedFiles);
                Interlocked.Add(ref _completedBytes, file.SizeBytes);
                break;
            case FileState.AlreadyOnServer:
                Interlocked.Increment(ref _alreadyPresentFiles);
                Interlocked.Add(ref _completedBytes, file.SizeBytes);
                break;
            case FileState.Skipped:
                Interlocked.Increment(ref _skippedFiles);
                break;
            case FileState.Failed:
                Interlocked.Increment(ref _failedFiles);
                break;
        }
    }

    /// <summary>
    /// The API answers 400 when a file breaks the upload policy - wrong extension,
    /// over the size limit, too many parts. Those never become uploadable.
    /// </summary>
    private static bool IsPermanentRejection(Exception exception)
    {
        return exception is HttpRequestException { StatusCode: HttpStatusCode.BadRequest };
    }

    private static string Describe(Exception exception)
    {
        return exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                "Unauthorized - the API key was rejected.",
            TaskCanceledException => "Timed out.",
            _ => exception.Message
        };
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 1024 * 1024,
            useAsync: true
        );

        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
