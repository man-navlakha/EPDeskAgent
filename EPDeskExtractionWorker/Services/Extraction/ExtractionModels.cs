namespace EPDeskExtractionWorker.Services.Extraction;

public sealed record ExtractionRequest(
    string FilePath,
    string FileName,
    string? DeclaredContentType = null,
    bool IncludeSpeakerNotes = false,
    bool IncludeWordComments = false,
    bool EnableImageOcr = false,
    string OcrLanguage = "eng");

public sealed record ExtractedSection(
    string SectionType,
    int? SectionNumber,
    string? Heading,
    string Content,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record ExtractedDocument(
    string Format,
    IReadOnlyList<ExtractedSection> Sections,
    IReadOnlyDictionary<string, object?> Metadata,
    IReadOnlyList<string> Warnings);

public class ExtractionException : Exception
{
    public ExtractionException(string message) : base(message)
    {
    }

    public ExtractionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class ExternalToolUnavailableException : ExtractionException
{
    public ExternalToolUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ExternalToolTimeoutException : ExtractionException
{
    public ExternalToolTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ExtractionNeedsOcrException : ExtractionException
{
    public ExtractionNeedsOcrException(string message)
        : base(message)
    {
    }
}
