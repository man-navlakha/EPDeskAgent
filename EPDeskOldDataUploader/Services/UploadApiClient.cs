using System.Net.Http.Json;
using System.Text.Json;
using EPDeskOldDataUploader.Models;

namespace EPDeskOldDataUploader.Services;

/// <summary>
/// Talks to the deployed EPDesk API using the shared agent file-upload key. The
/// API hands back short-lived Backblaze URLs, so this tool never needs storage
/// credentials or a database connection of its own.
/// </summary>
public sealed class UploadApiClient : IDisposable
{
    private const string ApiKeyHeaderName = "X-Agent-File-Upload-Key";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string AgentUploadPath = "/api/agent/file-uploads";
    private const string OldUserDataPath = "/api/agent/old-user-data";

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    /// <summary>
    /// Set once a readable-folder job exists. Everything after that is addressed
    /// to the old-user-data endpoints, whose responses have the same shape.
    /// </summary>
    private Guid _oldUserDataJobId;

    private string BasePath =>
        _oldUserDataJobId == Guid.Empty ? AgentUploadPath : OldUserDataPath;

    public UploadApiClient(string baseUrl, string apiKey)
    {
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var baseAddress) ||
            (baseAddress.Scheme != Uri.UriSchemeHttp && baseAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "The API address must be an absolute http or https URL.",
                nameof(baseUrl)
            );
        }

        _apiKey = apiKey.Trim();
        _httpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            // Individual calls carry their own timeout token; a single ceiling here
            // would abort a legitimately slow 100 MB part upload.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<UploadPolicy> GetPolicyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(TimeSpan.FromSeconds(60), cancellationToken);
        using var response = await _httpClient.GetAsync(
            "/api/agent/file-uploads/policy",
            timeout.Token
        );

        await EnsureSuccessAsync(response, timeout.Token);

        return await response.Content.ReadFromJsonAsync<UploadPolicy>(
            JsonOptions,
            timeout.Token
        ) ?? throw new InvalidOperationException("The upload policy response was empty.");
    }

    /// <summary>
    /// Claims the import job that owns this archive root and switches the client
    /// over to the readable-folder endpoints. Called once before a run.
    /// </summary>
    public async Task<OldUserDataJobResponse> UseReadableFoldersAsync(
        string rootPath,
        string sourceLabel,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(TimeSpan.FromSeconds(60), cancellationToken);
        using var message = CreateRequest(
            HttpMethod.Post,
            OldUserDataPath + "/jobs",
            JsonContent.Create(new { rootPath, sourceLabel })
        );
        using var response = await _httpClient.SendAsync(message, timeout.Token);

        await EnsureSuccessAsync(response, timeout.Token);

        var job = await response.Content.ReadFromJsonAsync<OldUserDataJobResponse>(
            JsonOptions,
            timeout.Token
        ) ?? throw new InvalidOperationException("The import job response was empty.");

        _oldUserDataJobId = job.JobId;

        return job;
    }

    public async Task<InitiateUploadResponse> InitiateAsync(
        InitiateUploadRequest request,
        CancellationToken cancellationToken)
    {
        request.JobId = _oldUserDataJobId;

        using var timeout = CreateTimeout(TimeSpan.FromSeconds(120), cancellationToken);
        using var message = CreateRequest(
            HttpMethod.Post,
            BasePath + "/initiate",
            JsonContent.Create(request)
        );
        using var response = await _httpClient.SendAsync(message, timeout.Token);

        await EnsureSuccessAsync(response, timeout.Token);

        return await response.Content.ReadFromJsonAsync<InitiateUploadResponse>(
            JsonOptions,
            timeout.Token
        ) ?? throw new InvalidOperationException("The initiate response was empty.");
    }

    public async Task<UploadPartUrlResponse> GetPartUrlAsync(
        Guid uploadId,
        int partNumber,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(TimeSpan.FromSeconds(120), cancellationToken);
        using var message = CreateRequest(
            HttpMethod.Post,
            $"{BasePath}/{uploadId}/parts/{partNumber}/url"
        );
        using var response = await _httpClient.SendAsync(message, timeout.Token);

        await EnsureSuccessAsync(response, timeout.Token);

        return await response.Content.ReadFromJsonAsync<UploadPartUrlResponse>(
            JsonOptions,
            timeout.Token
        ) ?? throw new InvalidOperationException("The part URL response was empty.");
    }

    /// <summary>
    /// Streams one slice of the file straight to Backblaze. The presigned URL
    /// already carries its own authorisation, so the agent key is not sent here.
    /// </summary>
    public async Task UploadPartAsync(
        string filePath,
        UploadPartUrlResponse part,
        CancellationToken cancellationToken)
    {
        var fileStream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 81920,
            useAsync: true
        );

        fileStream.Seek(part.OffsetBytes, SeekOrigin.Begin);

        using var limitedStream = new LimitedReadStream(fileStream, part.LengthBytes);
        using var content = new StreamContent(limitedStream);
        content.Headers.ContentLength = part.LengthBytes;

        using var timeout = CreateTimeout(TimeSpan.FromMinutes(30), cancellationToken);
        using var response = await _httpClient.PutAsync(part.UploadUrl, content, timeout.Token);

        await EnsureSuccessAsync(response, timeout.Token);
    }

    public async Task CompleteAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(TimeSpan.FromMinutes(5), cancellationToken);
        using var message = CreateRequest(
            HttpMethod.Post,
            $"{BasePath}/{uploadId}/complete"
        );
        using var response = await _httpClient.SendAsync(message, timeout.Token);

        await EnsureSuccessAsync(response, timeout.Token);
    }

    /// <summary>
    /// Tells the server an attempt failed. The multipart upload is deliberately
    /// left open so the next attempt resumes from the parts already stored.
    /// </summary>
    public async Task ReportFailureAsync(
        Guid uploadId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateTimeout(TimeSpan.FromSeconds(60), cancellationToken);
            using var message = CreateRequest(
                HttpMethod.Post,
                $"{BasePath}/{uploadId}/failure",
                JsonContent.Create(new { errorMessage })
            );
            using var response = await _httpClient.SendAsync(message, timeout.Token);

            await EnsureSuccessAsync(response, timeout.Token);
        }
        catch (Exception)
        {
            // Reporting is best effort; the real error is already being surfaced.
        }
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string requestUri,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, requestUri) { Content = content };
        request.Headers.Add(ApiKeyHeaderName, _apiKey);

        return request;
    }

    private static CancellationTokenSource CreateTimeout(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);

        return source;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (body.Length > 400)
        {
            body = body[..400] + "...";
        }

        throw new HttpRequestException(
            $"{(int)response.StatusCode} {response.ReasonPhrase}. {body}".Trim(),
            null,
            response.StatusCode
        );
    }

    public void Dispose() => _httpClient.Dispose();
}
