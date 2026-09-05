namespace EPDeskExtractionWorker.Services.Extraction;

public sealed class PdfExtractor : IFileExtractor
{
    private readonly ExtractionLimits _limits;
    private readonly ExternalToolOptions _tools;
    private readonly IExternalProcessRunner _processRunner;

    public PdfExtractor(
        IExternalProcessRunner processRunner,
        ExtractionLimits? limits = null,
        ExternalToolOptions? tools = null)
    {
        _processRunner = processRunner;
        _limits = limits ?? new ExtractionLimits();
        _tools = tools ?? new ExternalToolOptions();
    }

    public bool CanExtract(ExtractionRequest request) =>
        ExtractionUtilities.Extension(request) == ".pdf" ||
        string.Equals(request.DeclaredContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractedDocument> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ExtractionUtilities.ValidateInputFile(request, _limits);
        var processResult = await _processRunner.RunAsync(
            new ExternalProcessRequest(
                _tools.PdfToTextExecutable,
                new[] { "-layout", "-enc", "UTF-8", request.FilePath, "-" },
                _limits.ExternalProcessTimeout,
                _limits.MaxExternalOutputCharacters),
            cancellationToken);

        if (processResult.ExitCode != 0)
        {
            throw new ExtractionException(
                $"pdftotext failed with exit code {processResult.ExitCode}: {SafeError(processResult.StandardError)}");
        }

        var pages = processResult.StandardOutput.Split('\f').ToList();
        if (pages.Count > 1 && string.IsNullOrWhiteSpace(pages[^1]))
        {
            pages.RemoveAt(pages.Count - 1);
        }

        var sections = pages
            .Select((page, index) => new ExtractedSection(
                "page",
                index + 1,
                null,
                page.Trim(),
                new Dictionary<string, object?>
                {
                    ["page_number"] = index + 1,
                    ["extractor"] = "pdftotext",
                    ["layout_preserved"] = true
                }))
            .ToArray();

        if (sections.All(section => string.IsNullOrWhiteSpace(section.Content)))
        {
            throw new ExtractionNeedsOcrException(
                "PDF contains no extractable text and requires the OCR pipeline."
            );
        }

        return new ExtractedDocument(
            "pdf",
            sections,
            new Dictionary<string, object?>
            {
                ["page_count"] = sections.Length,
                ["extractor"] = "pdftotext",
                ["ocr_performed"] = false
            },
            Array.Empty<string>());
    }

    private static string SafeError(string error)
    {
        const int maximumLength = 2_000;
        var normalized = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
