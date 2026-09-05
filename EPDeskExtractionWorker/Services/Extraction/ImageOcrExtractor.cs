namespace EPDeskExtractionWorker.Services.Extraction;

public sealed class ImageOcrExtractor : IFileExtractor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp"
    };

    private readonly ExtractionLimits _limits;
    private readonly ExternalToolOptions _tools;
    private readonly IExternalProcessRunner _processRunner;

    public ImageOcrExtractor(
        IExternalProcessRunner processRunner,
        ExtractionLimits? limits = null,
        ExternalToolOptions? tools = null)
    {
        _processRunner = processRunner;
        _limits = limits ?? new ExtractionLimits();
        _tools = tools ?? new ExternalToolOptions();
    }

    public bool CanExtract(ExtractionRequest request) =>
        SupportedExtensions.Contains(ExtractionUtilities.Extension(request)) ||
        request.DeclaredContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<ExtractedDocument> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ExtractionUtilities.ValidateInputFile(request, _limits);
        if (!request.EnableImageOcr)
        {
            return new ExtractedDocument(
                "image",
                Array.Empty<ExtractedSection>(),
                new Dictionary<string, object?> { ["ocr_performed"] = false },
                new[] { "OCR was disabled by extraction policy." });
        }

        if (string.IsNullOrWhiteSpace(request.OcrLanguage) ||
            request.OcrLanguage.Length > 64 ||
            request.OcrLanguage.Any(character => !char.IsLetterOrDigit(character) && character is not '_' and not '+'))
        {
            throw new ExtractionException("OCR language contains unsupported characters.");
        }

        var processResult = await _processRunner.RunAsync(
            new ExternalProcessRequest(
                _tools.TesseractExecutable,
                new[] { request.FilePath, "stdout", "-l", request.OcrLanguage },
                _limits.ExternalProcessTimeout,
                _limits.MaxExternalOutputCharacters),
            cancellationToken);

        if (processResult.ExitCode != 0)
        {
            throw new ExtractionException(
                $"tesseract failed with exit code {processResult.ExitCode}: {SafeError(processResult.StandardError)}");
        }

        var text = processResult.StandardOutput.Trim();
        var sections = text.Length == 0
            ? Array.Empty<ExtractedSection>()
            : new[]
            {
                new ExtractedSection(
                    "image_ocr",
                    1,
                    null,
                    text,
                    new Dictionary<string, object?>
                    {
                        ["ocr_engine"] = "tesseract",
                        ["ocr_language"] = request.OcrLanguage
                    })
            };

        return new ExtractedDocument(
            "image",
            sections,
            new Dictionary<string, object?>
            {
                ["ocr_performed"] = true,
                ["ocr_engine"] = "tesseract",
                ["ocr_language"] = request.OcrLanguage
            },
            text.Length == 0 ? new[] { "OCR completed but returned no text." } : Array.Empty<string>());
    }

    private static string SafeError(string error)
    {
        const int maximumLength = 2_000;
        var normalized = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}

