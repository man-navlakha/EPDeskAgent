using System.IO.Compression;
using System.Text;

namespace EPDeskExtractionWorker.Services.Processing;

public sealed class FileTypeInspector
{
    private readonly int _maximumArchiveEntries;

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
            [".gif"] = "image/gif",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".bmp"] = "image/bmp",
            [".webp"] = "image/webp"
        };

    public FileTypeInspector(int maximumArchiveEntries = 10_000)
    {
        _maximumArchiveEntries = maximumArchiveEntries > 0
            ? maximumArchiveEntries
            : throw new ArgumentOutOfRangeException(nameof(maximumArchiveEntries));
    }

    public async Task<FileInspection> InspectAsync(
        string filePath,
        string fileName,
        string declaredContentType,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!ExpectedTypes.TryGetValue(extension, out var expectedType))
        {
            throw new RejectedExtractionException(
                "unsupported_format",
                $"File extension '{extension}' is not supported by extraction pipeline v1."
            );
        }

        var detectedType = await DetectAsync(filePath, cancellationToken);
        if (detectedType == "application/zip")
        {
            detectedType = DetectOpenXmlType(filePath);
        }

        var detectedMatches = string.Equals(detectedType, expectedType, StringComparison.OrdinalIgnoreCase) ||
            (extension == ".csv" && detectedType == "text/plain") ||
            (extension == ".md" && detectedType == "text/plain");

        if (!detectedMatches)
        {
            throw new RejectedExtractionException(
                "mime_mismatch",
                $"File content was detected as '{detectedType}', not the expected '{expectedType}'."
            );
        }

        var declared = declaredContentType?.Trim() ?? "";
        var declaredIsGeneric = declared.Length == 0 ||
            declared.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
            declared.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase);
        var declaredMatches = declaredIsGeneric ||
            declared.Equals(expectedType, StringComparison.OrdinalIgnoreCase) ||
            ((extension is ".csv" or ".md") && declared.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

        if (!declaredMatches)
        {
            throw new RejectedExtractionException(
                "declared_mime_mismatch",
                $"Declared MIME type '{declared}' conflicts with detected type '{detectedType}'."
            );
        }

        return new FileInspection(extension, detectedType, declared);
    }

    private static async Task<string> DetectAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        var length = await stream.ReadAsync(buffer, cancellationToken);
        return DetectFromBytes(buffer, length);
    }

    private static string DetectFromBytes(byte[] buffer, int length)
    {
        var bytes = buffer.AsSpan(0, length);

        if (bytes.StartsWith("%PDF-"u8)) return "application/pdf";
        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return "image/gif";
        if (bytes.StartsWith(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) ||
            bytes.StartsWith(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })) return "image/tiff";
        if (bytes.StartsWith("BM"u8)) return "image/bmp";
        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
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
            var entryCount = 0;
            var hasWord = false;
            var hasPresentation = false;
            var hasWorkbook = false;
            foreach (var entry in archive.Entries)
            {
                if (++entryCount > _maximumArchiveEntries)
                {
                    throw new RejectedExtractionException(
                        "archive_entry_limit_exceeded",
                        $"Office package exceeds the {_maximumArchiveEntries} entry safety limit."
                    );
                }

                var name = entry.FullName.Replace('\\', '/');
                hasWord |= name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase);
                hasPresentation |= name.Equals("ppt/presentation.xml", StringComparison.OrdinalIgnoreCase);
                hasWorkbook |= name.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase);
            }

            if (hasWord) return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            if (hasPresentation) return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
            if (hasWorkbook) return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        }
        catch (InvalidDataException exception)
        {
            throw new RejectedExtractionException(
                "invalid_archive",
                "The Office document is not a valid ZIP package.",
                exception
            );
        }

        return "application/zip";
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

public sealed record FileInspection(
    string Extension,
    string DetectedContentType,
    string DeclaredContentType
);
