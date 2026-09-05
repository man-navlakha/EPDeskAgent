using EPDeskExtractionSandbox.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionSandbox.Services;

public sealed class ExtractionRequestParser
{
    private readonly SandboxOptions _options;

    public ExtractionRequestParser(IOptions<SandboxOptions> options)
    {
        _options = options.Value;
    }

    public ParsedExtractionRequest Parse(HttpRequest request)
    {
        var rawFileName = request.Headers[_options.FileNameHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(rawFileName) || rawFileName.Length > 180 ||
            !string.Equals(rawFileName, Path.GetFileName(rawFileName), StringComparison.Ordinal) ||
            rawFileName.Any(character => char.IsControl(character) || character is '/' or '\\'))
        {
            throw new SandboxRequestException(
                400,
                "invalid_file_name",
                $"A safe file name is required in the {_options.FileNameHeader} header.");
        }

        var includeSpeakerNotes = ReadBooleanHeader(request, "X-Include-Speaker-Notes");
        var includeWordComments = ReadBooleanHeader(request, "X-Include-Word-Comments");
        var enableImageOcr = ReadBooleanHeader(request, "X-Enable-Image-Ocr");
        if (includeSpeakerNotes && !_options.AllowSpeakerNotes)
        {
            throw new SandboxRequestException(403, "speaker_notes_not_permitted", "Speaker notes are disabled by policy.");
        }

        if (includeWordComments && !_options.AllowWordComments)
        {
            throw new SandboxRequestException(403, "word_comments_not_permitted", "Word comments are disabled by policy.");
        }

        if (enableImageOcr && !_options.AllowImageOcr)
        {
            throw new SandboxRequestException(403, "image_ocr_not_permitted", "Image OCR is disabled by policy.");
        }

        var ocrLanguage = request.Headers["X-Ocr-Language"].ToString().Trim();
        if (ocrLanguage.Length == 0)
        {
            ocrLanguage = _options.DefaultOcrLanguage;
        }

        if (!(_options.AllowedOcrLanguages ?? []).Contains(ocrLanguage, StringComparer.OrdinalIgnoreCase))
        {
            throw new SandboxRequestException(400, "invalid_ocr_language", "The requested OCR language is not allowed.");
        }

        var declaredContentType = FileTypeInspector.NormalizeContentType(request.ContentType);
        if (!string.IsNullOrWhiteSpace(request.ContentType) && declaredContentType.Length == 0)
        {
            throw new SandboxRequestException(400, "invalid_content_type", "Content-Type is not a valid media type.");
        }

        return new ParsedExtractionRequest(
            rawFileName,
            declaredContentType,
            includeSpeakerNotes,
            includeWordComments,
            enableImageOcr,
            ocrLanguage);
    }

    private static bool ReadBooleanHeader(HttpRequest request, string name)
    {
        var value = request.Headers[name].ToString().Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (!bool.TryParse(value, out var result))
        {
            throw new SandboxRequestException(400, "invalid_option_header", $"{name} must be true or false.");
        }

        return result;
    }
}

public sealed record ParsedExtractionRequest(
    string FileName,
    string DeclaredContentType,
    bool IncludeSpeakerNotes,
    bool IncludeWordComments,
    bool EnableImageOcr,
    string OcrLanguage);
