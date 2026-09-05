using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EPDeskExtractionWorker.Services.Extraction;

internal static class ExtractionUtilities
{
    public static string Extension(ExtractionRequest request) =>
        Path.GetExtension(request.FileName).ToLowerInvariant();

    public static void ValidateInputFile(ExtractionRequest request, ExtractionLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        limits.Validate();

        var info = new FileInfo(request.FilePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The extraction input file does not exist.", request.FilePath);
        }

        if (info.Length > limits.MaxInputBytes)
        {
            throw new ExtractionException($"Input exceeds the {limits.MaxInputBytes} byte extraction limit.");
        }
    }

    public static async Task<string> ReadBoundedTextAsync(
        string path,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);
        return await ReadBoundedTextAsync(reader, maxCharacters, cancellationToken);
    }

    public static async Task<string> ReadBoundedTextAsync(
        TextReader reader,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maxCharacters, 64 * 1024));
        var buffer = new char[16 * 1024];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return result.ToString();
            }

            if (result.Length > maxCharacters - read)
            {
                throw new ExtractionException($"Text output exceeds the {maxCharacters} character limit.");
            }

            result.Append(buffer, 0, read);
        }
    }

    public static XDocument LoadXml(Stream stream, int maxCharacters)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true
        };

        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    public static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    public static string InferScalarType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "null";
        }

        if (bool.TryParse(value, out _))
        {
            return "boolean";
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return "integer";
        }

        if (decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out _))
        {
            return "number";
        }

        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))
        {
            return "date";
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))
        {
            return "datetime";
        }

        return "string";
    }

    public static string MergeScalarTypes(string current, string next)
    {
        if (next == "null")
        {
            return current;
        }

        if (current == "null" || current == next)
        {
            return next;
        }

        if ((current == "integer" && next == "number") || (current == "number" && next == "integer"))
        {
            return "number";
        }

        if ((current == "date" && next == "datetime") || (current == "datetime" && next == "date"))
        {
            return "datetime";
        }

        return "string";
    }

    public static string ColumnName(int zeroBasedIndex)
    {
        var result = string.Empty;
        var index = zeroBasedIndex + 1;
        while (index > 0)
        {
            index--;
            result = (char)('A' + index % 26) + result;
            index /= 26;
        }

        return result;
    }

    public static int ColumnIndexFromReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return -1;
        }

        var index = 0;
        var found = false;
        foreach (var character in reference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            found = true;
            index = checked(index * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }

        return found ? index - 1 : -1;
    }
}
