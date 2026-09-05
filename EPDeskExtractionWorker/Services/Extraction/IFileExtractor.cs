namespace EPDeskExtractionWorker.Services.Extraction;

public interface IFileExtractor
{
    bool CanExtract(ExtractionRequest request);

    Task<ExtractedDocument> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default);
}

