using System.Text;
using System.Xml.Linq;

namespace EPDeskExtractionWorker.Services.Extraction;

public sealed class DocxExtractor : IFileExtractor
{
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private readonly ExtractionLimits _limits;

    public DocxExtractor(ExtractionLimits? limits = null)
    {
        _limits = limits ?? new ExtractionLimits();
    }

    public bool CanExtract(ExtractionRequest request) =>
        ExtractionUtilities.Extension(request) == ".docx" ||
        string.Equals(
            request.DeclaredContentType,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            StringComparison.OrdinalIgnoreCase);

    public Task<ExtractedDocument> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ExtractionUtilities.ValidateInputFile(request, _limits);
        cancellationToken.ThrowIfCancellationRequested();

        using var package = new SafeZipPackage(request.FilePath, _limits);
        var document = package.LoadRequiredXml("word/document.xml");
        var body = document.Root?.Element(Word + "body")
                   ?? throw new ExtractionException("DOCX document body was not found.");

        var sections = new List<ExtractedSection>();
        var headingHierarchy = new string?[9];
        var paragraphNumber = 0;
        var tableNumber = 0;

        foreach (var block in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Name == Word + "p")
            {
                var text = ReadWordText(block);
                if (text.Length == 0)
                {
                    continue;
                }

                paragraphNumber++;
                var style = (string?)block
                    .Element(Word + "pPr")?
                    .Element(Word + "pStyle")?
                    .Attribute(Word + "val");
                var headingLevel = HeadingLevel(style);
                if (headingLevel is > 0 and < 10)
                {
                    headingHierarchy[headingLevel.Value - 1] = text;
                    Array.Clear(headingHierarchy, headingLevel.Value, headingHierarchy.Length - headingLevel.Value);
                }

                var hierarchy = headingHierarchy.Where(value => value is not null).Cast<string>().ToArray();
                sections.Add(new ExtractedSection(
                    headingLevel.HasValue ? "heading" : "paragraph",
                    paragraphNumber,
                    headingLevel.HasValue ? text : hierarchy.LastOrDefault(),
                    text,
                    new Dictionary<string, object?>
                    {
                        ["paragraph_number"] = paragraphNumber,
                        ["style"] = style,
                        ["heading_level"] = headingLevel,
                        ["heading_path"] = hierarchy
                    }));
            }
            else if (block.Name == Word + "tbl")
            {
                tableNumber++;
                sections.Add(ReadTable(block, tableNumber, headingHierarchy));
            }
        }

        AppendHeaderFooterSections(package, "word/header", "header", sections, cancellationToken);
        AppendHeaderFooterSections(package, "word/footer", "footer", sections, cancellationToken);

        if (request.IncludeWordComments)
        {
            AppendComments(package, sections, cancellationToken);
        }

        return Task.FromResult(new ExtractedDocument(
            "docx",
            sections,
            new Dictionary<string, object?>
            {
                ["paragraph_count"] = paragraphNumber,
                ["table_count"] = tableNumber,
                ["comments_included"] = request.IncludeWordComments,
                ["header_parts"] = package.EntryNames.Count(name => name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase)),
                ["footer_parts"] = package.EntryNames.Count(name => name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase))
            },
            Array.Empty<string>()));
    }

    private ExtractedSection ReadTable(XElement table, int tableNumber, IReadOnlyList<string?> hierarchy)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var row in table.Elements(Word + "tr"))
        {
            if (rows.Count >= _limits.MaxTableRows)
            {
                throw new ExtractionException($"DOCX table exceeds {_limits.MaxTableRows} rows.");
            }

            var cells = row.Elements(Word + "tc").Select(ReadWordText).ToArray();
            if (cells.Length > _limits.MaxTableColumns)
            {
                throw new ExtractionException($"DOCX table exceeds {_limits.MaxTableColumns} columns.");
            }

            if (cells.Any(cell => cell.Length > _limits.MaxCellCharacters))
            {
                throw new ExtractionException($"DOCX table cell exceeds {_limits.MaxCellCharacters} characters.");
            }

            rows.Add(cells);
        }

        var heading = hierarchy.LastOrDefault(value => value is not null);
        var searchableText = string.Join(
            Environment.NewLine,
            rows.Select(row => string.Join(" | ", row)));

        return new ExtractedSection(
            "table",
            tableNumber,
            heading,
            searchableText,
            new Dictionary<string, object?>
            {
                ["table_number"] = tableNumber,
                ["row_count"] = rows.Count,
                ["column_count"] = rows.Count == 0 ? 0 : rows.Max(row => row.Count),
                ["rows"] = rows,
                ["heading_path"] = hierarchy.Where(value => value is not null).ToArray()
            });
    }

    private void AppendHeaderFooterSections(
        SafeZipPackage package,
        string prefix,
        string sectionType,
        ICollection<ExtractedSection> sections,
        CancellationToken cancellationToken)
    {
        var partNumber = 0;
        foreach (var part in package.EntryNames
                     .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xml = package.LoadRequiredXml(part);
            var text = ReadWordText(xml.Root);
            if (text.Length == 0)
            {
                continue;
            }

            partNumber++;
            sections.Add(new ExtractedSection(
                sectionType,
                partNumber,
                null,
                text,
                new Dictionary<string, object?> { ["part_name"] = part }));
        }
    }

    private void AppendComments(
        SafeZipPackage package,
        ICollection<ExtractedSection> sections,
        CancellationToken cancellationToken)
    {
        var comments = package.LoadOptionalXml("word/comments.xml");
        if (comments?.Root is null)
        {
            return;
        }

        var commentNumber = 0;
        foreach (var comment in comments.Root.Elements(Word + "comment"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = ReadWordText(comment);
            if (text.Length == 0)
            {
                continue;
            }

            commentNumber++;
            sections.Add(new ExtractedSection(
                "comment",
                commentNumber,
                null,
                text,
                new Dictionary<string, object?>
                {
                    ["comment_id"] = (string?)comment.Attribute(Word + "id"),
                    ["author"] = (string?)comment.Attribute(Word + "author"),
                    ["date"] = (string?)comment.Attribute(Word + "date")
                }));
        }
    }

    private static int? HeadingLevel(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        var digits = new string(style.Where(char.IsDigit).ToArray());
        return style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(digits, out var level) && level is >= 1 and <= 9
            ? level
            : null;
    }

    private static string ReadWordText(XContainer? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var element in container.Descendants())
        {
            if (element.Name == Word + "t")
            {
                builder.Append(element.Value);
            }
            else if (element.Name == Word + "tab")
            {
                builder.Append('\t');
            }
            else if (element.Name == Word + "br" || element.Name == Word + "cr")
            {
                builder.AppendLine();
            }
            else if (element.Name == Word + "p" && builder.Length > 0 && builder[^1] != '\n')
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().Trim();
    }
}
