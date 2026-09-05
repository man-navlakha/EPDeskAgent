namespace EPDeskExtractionWorker.Services.Extraction;

public sealed record ExtractionLimits
{
    public long MaxInputBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxTextCharacters { get; init; } = 16 * 1024 * 1024;
    public int MaxXmlCharacters { get; init; } = 64 * 1024 * 1024;
    public int MaxArchiveEntries { get; init; } = 5_000;
    public long MaxArchiveEntryBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxArchiveExpandedBytes { get; init; } = 256L * 1024 * 1024;
    public double MaxCompressionRatio { get; init; } = 100;
    public int MaxTableRows { get; init; } = 100_000;
    public int MaxTableColumns { get; init; } = 2_048;
    public int MaxCellCharacters { get; init; } = 1_000_000;
    public int SampleRowCount { get; init; } = 10;
    public int MaxExternalOutputCharacters { get; init; } = 64 * 1024 * 1024;
    public TimeSpan ExternalProcessTimeout { get; init; } = TimeSpan.FromMinutes(3);

    internal void Validate()
    {
        if (MaxInputBytes <= 0 || MaxTextCharacters <= 0 || MaxXmlCharacters <= 0 ||
            MaxArchiveEntries <= 0 || MaxArchiveEntryBytes <= 0 || MaxArchiveExpandedBytes <= 0 ||
            MaxCompressionRatio <= 0 || MaxTableRows <= 0 || MaxTableColumns <= 0 ||
            MaxCellCharacters <= 0 || SampleRowCount < 0 || MaxExternalOutputCharacters <= 0 ||
            ExternalProcessTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ExtractionLimits), "All extraction limits must be positive.");
        }
    }
}

public sealed record ExternalToolOptions
{
    public string PdfToTextExecutable { get; init; } = "pdftotext";
    public string TesseractExecutable { get; init; } = "tesseract";
}

