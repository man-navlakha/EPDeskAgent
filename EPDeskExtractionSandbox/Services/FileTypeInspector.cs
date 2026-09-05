using System.IO.Compression;
using System.Text;
using EPDeskExtractionSandbox.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionSandbox.Services;

public sealed class FileTypeInspector
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".csv"] = "text/csv",
            [".txt"] = "text/plain",
            [".md"] = "text/markdown",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".bmp"] = "image/bmp",
            [".webp"] = "image/webp"
        };

    private readonly SandboxOptions _options;
    private readonly HashSet<string> _allowedExtensions;

    public FileTypeInspector(IOptions<SandboxOptions> options)
    {
        _options = options.Value;
        _allowedExtensions = new HashSet<string>(
            (_options.AllowedExtensions ?? []).Select(value => value.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<FileInspection> InspectAsync(
        string filePath,
        string fileName,
        string declaredContentType,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!_allowedExtensions.Contains(extension) || !ExpectedTypes.TryGetValue(extension, out var expectedType))
        {
            throw new SandboxRequestException(415, "unsupported_format", "The file extension is not supported.");
        }

        var detectedType = await DetectAsync(filePath, cancellationToken);
        if (detectedType == "application/zip")
        {
            // The caller must run ClamAV before invoking this method. This is the first ZIP inspection.
            detectedType = DetectOpenXmlType(filePath);
        }

        var detectedMatches = string.Equals(detectedType, expectedType, StringComparison.OrdinalIgnoreCase) ||
            (extension == ".csv" && detectedType == "text/plain") ||
            (extension == ".md" && detectedType == "text/plain");
        if (!detectedMatches)
        {
            throw new SandboxRequestException(
                415,
                "mime_mismatch",
                $"File content was detected as '{detectedType}', not the expected type for '{extension}'.");
        }

        var declared = NormalizeContentType(declaredContentType);
        var declaredIsGeneric = declared.Length == 0 ||
            declared.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
            declared.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase);
        var declaredMatches = declaredIsGeneric ||
            declared.Equals(expectedType, StringComparison.OrdinalIgnoreCase) ||
            ((extension is ".csv" or ".md") && declared.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
        if (!declaredMatches)
        {
            throw new SandboxRequestException(
                415,
                "declared_mime_mismatch",
                "The declared Content-Type conflicts with the detected file type.");
        }

        return new FileInspection(extension, detectedType);
    }

    public static string NormalizeContentType(string? value)
    {
        var mediaType = (value ?? "").Split(';', 2)[0].Trim();
        return mediaType.Length <= 128 && IsSafeMediaType(mediaType)
            ? mediaType.ToLowerInvariant()
            : "";
    }

    private static bool IsSafeMediaType(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        var slash = value.IndexOf('/');
        return slash is > 0 && slash < value.Length - 1 && value.Count(character => character == '/') == 1 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '!' or '#' or '$' or
                '&' or '^' or '_' or '.' or '+' or '-');
    }

    private static async Task<string> DetectAsync(string filePath, CancellationToken cancellationToken)
    {
        var buffer = new byte[8_192];
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = await stream.ReadAsync(buffer, cancellationToken);
        return DetectFromBytes(buffer, length);
    }

    private static string DetectFromBytes(byte[] buffer, int length)
    {
        var bytes = buffer.AsSpan(0, length);

        if (bytes.StartsWith("%PDF-"u8)) return "application/pdf";
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (bytes.StartsWith(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) ||
            bytes.StartsWith(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })) return "image/tiff";
        if (bytes.StartsWith("BM"u8)) return "image/bmp";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) ||
            bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) ||
            bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x07, 0x08 })) return "application/zip";
        if (LooksLikeText(bytes)) return "text/plain";
        return "application/octet-stream";
    }

    private string DetectOpenXmlType(string filePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var count = 0;
            var hasWord = false;
            var hasPresentation = false;
            var hasWorkbook = false;
            foreach (var entry in archive.Entries)
            {
                if (++count > _options.MaxArchiveEntries)
                {
                    throw new SandboxRequestException(
                        422,
                        "archive_entry_limit_exceeded",
                        "The Office package has too many entries.");
                }

                var name = entry.FullName.Replace('\\', '/');
                hasWord |= name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase);
                hasPresentation |= name.Equals("ppt/presentation.xml", StringComparison.OrdinalIgnoreCase);
                hasWorkbook |= name.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase);
            }

            if (hasWord) return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            if (hasPresentation) return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
            if (hasWorkbook) return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            return "application/zip";
        }
        catch (InvalidDataException)
        {
            throw new SandboxRequestException(422, "invalid_archive", "The Office file is not a valid ZIP package.");
        }
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

public sealed record FileInspection(string Extension, string DetectedContentType);
