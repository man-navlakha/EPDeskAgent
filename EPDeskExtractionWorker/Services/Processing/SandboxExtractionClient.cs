using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EPDeskExtractionWorker.Configuration;
using EPDeskExtractionWorker.Services.Extraction;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionWorker.Services.Processing;

public sealed class SandboxExtractionClient
{
    private const int MaximumErrorBodyBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;
    private readonly ExtractionWorkerOptions _options;
    private readonly Uri? _baseUri;

    public SandboxExtractionClient(
        HttpClient httpClient,
        IOptions<ExtractionWorkerOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        SandboxEndpointPolicy.TryCreateSafeBaseUri(
            _options.SandboxBaseUrl,
            out _baseUri
        );
    }

    public async Task<SandboxExtractionResult> ExtractAsync(
        string filePath,
        string fileName,
        string declaredContentType,
        CancellationToken cancellationToken)
    {
        if (_baseUri is null || !SandboxEndpointPolicy.IsValidApiKey(_options.SandboxApiKey))
        {
            throw new RetryableExtractionException(
                "sandbox_not_configured",
                "The extraction sandbox endpoint or authentication key is not configured."
            );
        }

        await using var source = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        using var content = new StreamContent(source, 64 * 1024);
        content.Headers.ContentLength = source.Length;
        content.Headers.ContentType = ParseContentType(declaredContentType);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_baseUri, "extract")
        )
        {
            Content = content
        };
        request.Headers.Add("X-Extraction-Key", _options.SandboxApiKey);
        request.Headers.Add("X-File-Name", SanitizeFileName(fileName));
        request.Headers.Add(
            "X-Include-Speaker-Notes",
            _options.IncludeSpeakerNotes ? "true" : "false"
        );
        request.Headers.Add("X-Include-Word-Comments", "false");
        request.Headers.Add(
            "X-Enable-Image-Ocr",
            _options.OcrImagesEnabled ? "true" : "false"
        );
        request.Headers.Add("X-Ocr-Language", "eng");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidOperationException)
        {
            throw new RetryableExtractionException(
                "sandbox_unavailable",
                "The extraction sandbox could not be reached.",
                exception
            );
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await ThrowForErrorResponseAsync(response, cancellationToken);
            }

            byte[] body;
            try
            {
                body = await ReadBoundedBodyAsync(
                    response.Content,
                    _options.MaxSandboxResponseBytes,
                    cancellationToken
                );
            }
            catch (SandboxResponseLimitException exception)
            {
                throw new RetryableExtractionException(
                    "sandbox_response_too_large",
                    "The extraction sandbox returned a response larger than the configured safety limit.",
                    exception
                );
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException)
            {
                throw new RetryableExtractionException(
                    "sandbox_response_interrupted",
                    "The extraction sandbox response was interrupted.",
                    exception
                );
            }

            SandboxSuccessEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<SandboxSuccessEnvelope>(body, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new RetryableExtractionException(
                    "sandbox_invalid_response",
                    "The extraction sandbox returned invalid JSON.",
                    exception
                );
            }

            if (envelope is null ||
                !TryNormalizeContentType(
                    envelope.DetectedContentType,
                    out var detectedContentType) ||
                envelope.ExtractedDocument is null ||
                string.IsNullOrWhiteSpace(envelope.ExtractedDocument.Format) ||
                envelope.ExtractedDocument.Format.Length > 64 ||
                envelope.ExtractedDocument.Sections is null ||
                envelope.ExtractedDocument.Metadata is null ||
                envelope.ExtractedDocument.Warnings is null ||
                envelope.ExtractedDocument.Sections.Any(section =>
                    section is null || section.Metadata is null || section.Content is null))
            {
                throw new RetryableExtractionException(
                    "sandbox_invalid_response",
                    "The extraction sandbox returned an incomplete result."
                );
            }

            return new SandboxExtractionResult(
                detectedContentType,
                envelope.ExtractedDocument
            );
        }
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (_baseUri is null)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(_baseUri, "health/ready")
        );

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token
            );
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidOperationException or
                OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task ThrowForErrorResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        SandboxError? error = null;
        try
        {
            var maximumBytes = Math.Min(MaximumErrorBodyBytes, int.MaxValue);
            var body = await ReadBoundedBodyAsync(
                response.Content,
                maximumBytes,
                cancellationToken
            );
            error = TryParseError(body);
        }
        catch (SandboxResponseLimitException)
        {
            // Error bodies are deliberately ignored after the small diagnostic limit.
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException)
        {
            // The HTTP status still determines whether this job is permanent or retryable.
        }

        var statusCode = response.StatusCode;
        var code = NormalizeErrorCode(
            error?.Code,
            $"sandbox_http_{(int)statusCode}"
        );
        var message = NormalizeErrorMessage(
            error?.Message,
            "The extraction sandbox rejected the document."
        );

        if (statusCode is HttpStatusCode.BadRequest or
            HttpStatusCode.Forbidden or
            HttpStatusCode.RequestEntityTooLarge or
            HttpStatusCode.UnsupportedMediaType or
            HttpStatusCode.UnprocessableEntity)
        {
            throw new RejectedExtractionException(code, message);
        }

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            code = "sandbox_authentication_failed";
            message = "The extraction sandbox authentication configuration was rejected.";
        }
        else
        {
            message = NormalizeErrorMessage(
                error?.Message,
                "The extraction sandbox is temporarily unavailable."
            );
        }

        throw new RetryableExtractionException(code, message);
    }

    private static SandboxError? TryParseError(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SandboxErrorEnvelope>(body, JsonOptions)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (content.Headers.ContentLength is long contentLength &&
            contentLength > maximumBytes)
        {
            throw new SandboxResponseLimitException();
        }

        var initialCapacity = content.Headers.ContentLength is > 0 and <= int.MaxValue
            ? (int)content.Headers.ContentLength.Value
            : 0;
        using var destination = initialCapacity > 0
            ? new MemoryStream(initialCapacity)
            : new MemoryStream();
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maximumBytes)
                {
                    throw new SandboxResponseLimitException();
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static MediaTypeHeaderValue ParseContentType(string value)
    {
        if (MediaTypeHeaderValue.TryParse(value, out var parsed) && parsed.MediaType is not null)
        {
            return parsed;
        }

        return new MediaTypeHeaderValue("application/octet-stream");
    }

    private static bool TryNormalizeContentType(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 ||
            !MediaTypeHeaderValue.TryParse(value, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.MediaType))
        {
            return false;
        }

        normalized = parsed.MediaType.ToLowerInvariant();
        return normalized.Length <= 255;
    }

    private static string SanitizeFileName(string value)
    {
        var fileName = Path.GetFileName((value ?? "").Replace('\\', '/'));
        var safe = new StringBuilder(Math.Min(fileName.Length, 512));
        foreach (var character in fileName)
        {
            if (safe.Length >= 512)
            {
                break;
            }

            safe.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_');
        }

        if (safe.Length == 0)
        {
            return "document.bin";
        }

        if (safe.Length <= 180)
        {
            return safe.ToString();
        }

        var extensionOffset = safe.ToString().LastIndexOf('.');
        if (extensionOffset > 0 && safe.Length - extensionOffset <= 16)
        {
            var extension = safe.ToString(extensionOffset, safe.Length - extensionOffset);
            return safe.ToString(0, 180 - extension.Length) + extension;
        }

        return safe.ToString(0, 180);
    }

    private static string NormalizeErrorCode(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = new string(value.Trim()
            .Where(character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .Take(128)
            .ToArray());
        return normalized.Length == 0 ? fallback : normalized;
    }

    private static string NormalizeErrorMessage(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = new string(value.Trim()
            .Where(character => !char.IsControl(character))
            .Take(1000)
            .ToArray());
        return normalized.Length == 0 ? fallback : normalized;
    }

    private sealed class SandboxSuccessEnvelope
    {
        public string DetectedContentType { get; init; } = "";

        public ExtractedDocument? ExtractedDocument { get; init; }
    }

    private sealed class SandboxErrorEnvelope
    {
        public SandboxError? Error { get; init; }
    }

    private sealed class SandboxError
    {
        public string? Code { get; init; }

        public string? Message { get; init; }
    }

    private sealed class SandboxResponseLimitException : Exception
    {
    }
}

public sealed record SandboxExtractionResult(
    string DetectedContentType,
    ExtractedDocument ExtractedDocument
);

public static class SandboxEndpointPolicy
{
    public static bool TryCreateSafeBaseUri(string value, out Uri? baseUri)
    {
        baseUri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        var isHttps = candidate.Scheme.Equals(
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase
        );
        var isSafeInternalHttp = candidate.Scheme.Equals(
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase
            ) &&
            (candidate.IsLoopback || candidate.Host.EndsWith(
                ".railway.internal",
                StringComparison.OrdinalIgnoreCase
            ));
        if (!isHttps && !isSafeInternalHttp)
        {
            return false;
        }

        baseUri = candidate.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? candidate
            : new Uri(candidate.AbsoluteUri + "/", UriKind.Absolute);
        return true;
    }

    public static bool IsValidApiKey(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length is >= 32 and <= 1024 &&
        value.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character));
}
