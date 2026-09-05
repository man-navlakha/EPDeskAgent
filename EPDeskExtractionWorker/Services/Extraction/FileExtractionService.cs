using System.Text.Json;

namespace EPDeskExtractionWorker.Services.Extraction;

public sealed class FileExtractionService
{
    private readonly IReadOnlyList<IFileExtractor> _extractors;

    public FileExtractionService(IEnumerable<IFileExtractor> extractors)
    {
        _extractors = extractors.ToArray();
        if (_extractors.Count == 0)
        {
            throw new ArgumentException("At least one file extractor is required.", nameof(extractors));
        }
    }

    public static FileExtractionService CreateDefault(
        ExtractionLimits? limits = null,
        ExternalToolOptions? externalTools = null,
        IExternalProcessRunner? processRunner = null)
    {
        limits ??= new ExtractionLimits();
        externalTools ??= new ExternalToolOptions();
        processRunner ??= new ExternalProcessRunner();

        return new FileExtractionService(new IFileExtractor[]
        {
            new CsvExtractor(limits),
            new PlainTextExtractor(limits),
            new DocxExtractor(limits),
            new PptxExtractor(limits),
            new XlsxExtractor(limits),
            new PdfExtractor(processRunner, limits, externalTools),
            new ImageOcrExtractor(processRunner, limits, externalTools)
        });
    }

    public Task<ExtractedDocument> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        var extractor = _extractors.FirstOrDefault(candidate => candidate.CanExtract(request));
        if (extractor is null)
        {
            throw new ExtractionException(
                $"No v1 extractor supports '{request.FileName}' with MIME type '{request.DeclaredContentType ?? "unknown"}'.");
        }

        return extractor.ExtractAsync(request, cancellationToken);
    }
}

public static class JsonDerivativeSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public static byte[] SerializeToUtf8(ExtractedDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, Options);

    public static string Serialize(ExtractedDocument document) =>
        JsonSerializer.Serialize(document, Options);
}
